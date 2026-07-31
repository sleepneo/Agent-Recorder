using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class WasapiAudioInputTests
{
    private static int GetRecordBufferLength(AudioClientAudioInput input)
    {
        var field = typeof(AudioClientAudioInput).GetField("_recordBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (field?.GetValue(input) as byte[])?.Length ?? 0;
    }

    private sealed class StubInput : IAudioInput
    {
        public bool Disposed { get; private set; }
        public NAudio.Wave.WaveFormat? Format => null;
        public long DiscontinuityCount => 0;
#pragma warning disable CS0067
        public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;
#pragma warning restore CS0067
        public StartRecordingResult StartRecording() => StartRecordingResult.Started;
        public void StopRecording() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeClock : ISystemClock
    {
        private readonly FakeStopwatch _stopwatch = new();
        public List<TimeSpan> SleptDelays { get; } = new();

        public IStopwatch StartStopwatch() => _stopwatch;

        public void Sleep(TimeSpan delay)
        {
            SleptDelays.Add(delay);
            _stopwatch.Elapsed += delay;
        }

        public void Advance(TimeSpan amount) => _stopwatch.Elapsed += amount;

        private sealed class FakeStopwatch : IStopwatch
        {
            public TimeSpan Elapsed { get; set; }
        }
    }

    private sealed class FakeEnumerator : IDeviceEnumerator
    {
        public Func<string, IDevice>? GetDeviceCallback { get; set; }
        public bool Disposed { get; private set; }

        public IDevice GetDevice(string endpointId)
            => GetDeviceCallback?.Invoke(endpointId) ?? throw new COMException("device not found", unchecked((int)0x80070490));

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDevice : IDevice
    {
        public DeviceState State { get; set; } = DeviceState.Active;
        public Func<IAudioClient>? CreateAudioClientCallback { get; set; }
        public bool Disposed => _disposeCount > 0;
        public int DisposeCount => _disposeCount;
        private int _disposeCount;

        public IAudioClient CreateAudioClient()
            => CreateAudioClientCallback?.Invoke() ?? throw new InvalidOperationException("No audio client");

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private class FakeAudioClient : IAudioClient
    {
        public WaveFormat MixFormat { get; set; } = new WaveFormat(16000, 16, 1);
        public int BufferSize { get; set; } = 1600;
        public bool Disposed => _disposeCount > 0;
        public int DisposeCount => _disposeCount;
        protected int _disposeCount;
        public bool Started { get; protected set; }
        public bool Stopped { get; protected set; }
        public Exception? InitializeException { get; set; }
        public Exception? StartException { get; set; }
        public Exception? StopException { get; set; }
        public Func<IAudioCaptureClient>? CaptureClientFactory { get; set; }
        public Action? StartAction { get; set; }

        public virtual void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration, long periodicity, WaveFormat format, Guid audioSessionGuid)
        {
            if (InitializeException != null)
                throw InitializeException;
        }

        public virtual void Start()
        {
            StartAction?.Invoke();
            if (StartException != null)
                throw StartException;
            Started = true;
        }

        public virtual void Stop()
        {
            if (StopException != null)
                throw StopException;
            Stopped = true;
        }

        public virtual IAudioCaptureClient GetAudioCaptureClient()
            => CaptureClientFactory?.Invoke() ?? new FakeAudioCaptureClient();

        public virtual void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private class FakeAudioCaptureClient : IAudioCaptureClient
    {
        public bool Disposed => _disposeCount > 0;
        public int DisposeCount => _disposeCount;
        protected int _disposeCount;
        public Queue<(IntPtr Buffer, int Frames, AudioClientBufferFlags Flags)> Packets { get; } = new();
        public Exception? GetNextPacketSizeException { get; set; }
        public Exception? GetBufferException { get; set; }
        public Exception? ReleaseBufferException { get; set; }
        public int ReleaseBufferCallCount { get; protected set; }

        public virtual int GetNextPacketSize()
        {
            if (GetNextPacketSizeException != null)
                throw GetNextPacketSizeException;
            return Packets.Count;
        }

        public virtual IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags)
        {
            if (GetBufferException != null)
                throw GetBufferException;
            if (!Packets.TryDequeue(out var packet))
            {
                framesAvailable = 0;
                flags = AudioClientBufferFlags.None;
                return IntPtr.Zero;
            }
            framesAvailable = packet.Frames;
            flags = packet.Flags;
            return packet.Buffer;
        }

        public virtual void ReleaseBuffer(int framesRead)
        {
            ReleaseBufferCallCount++;
            if (ReleaseBufferException != null)
                throw ReleaseBufferException;
        }

        public virtual void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    [Fact]
    public void Open_EmptyEndpointId_ReturnsEndpointNotFound()
    {
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();
        var (input, code, reason) = WasapiAudioInput.Open("   ", enumerator, clock, (_, _) => (new StubInput(), null, null));

        Assert.Null(input);
        Assert.Equal("audio_endpoint_not_found", code);
        Assert.Contains("empty", reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_TransientFailureThenSuccess_RetriesAndReturnsInput()
    {
        int attempts = 0;
        var capturedIds = new List<string>();
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            capturedIds.Add(endpointId);
            if (attempts < 3)
                return (null, "audio_endpoint_unavailable", $"transient-{attempts}");
            return (new StubInput(), null, null);
        }

        var (input, code, reason) = WasapiAudioInput.Open("{endpoint-id}", enumerator, clock, TryOpen);

        Assert.NotNull(input);
        Assert.Null(code);
        Assert.Null(reason);
        Assert.Equal(3, attempts);
        Assert.All(capturedIds, id => Assert.Equal("{endpoint-id}", id));
        Assert.Equal(new[] { 100, 200 }, clock.SleptDelays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public void Open_PersistentTransientFailure_ExhaustsAttemptsAndReturnsLastError()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            return (null, "audio_endpoint_unavailable", $"attempt-{attempts}");
        }

        var (input, code, reason) = WasapiAudioInput.Open("{endpoint-id}", enumerator, clock, TryOpen);

        Assert.Null(input);
        Assert.Equal("audio_endpoint_unavailable", code);
        Assert.Contains("attempt-3", reason ?? "");
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Open_NonTransientFailure_DoesNotRetry()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            return (null, "audio_endpoint_not_found", "device gone");
        }

        var (input, code, reason) = WasapiAudioInput.Open("{endpoint-id}", enumerator, clock, TryOpen);

        Assert.Null(input);
        Assert.Equal("audio_endpoint_not_found", code);
        Assert.Equal(1, attempts);
        Assert.Empty(clock.SleptDelays);
    }

    [Fact]
    public void Open_RetryBudgetExceeded_StopsRetryingEvenIfTransient()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            // Simulate that each attempt itself consumes 2.6s of wall-clock time.
            clock.Advance(TimeSpan.FromSeconds(2.6));
            return (null, "audio_format_negotiation_failure", "slow transient");
        }

        var (input, code, reason) = WasapiAudioInput.Open("{endpoint-id}", enumerator, clock, TryOpen);

        Assert.Null(input);
        Assert.Equal("audio_format_negotiation_failure", code);
        Assert.True(attempts <= 2, $"Should stop when total retry budget is exhausted, but attempted {attempts} times");
        Assert.True(clock.SleptDelays.Count <= 1, "No delay should be scheduled after budget is exhausted");
    }

    [Fact]
    public void Open_NeverSwitchesEndpointIdBetweenRetries()
    {
        var capturedIds = new List<string>();
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            capturedIds.Add(endpointId);
            return (null, "audio_endpoint_unavailable", "retry");
        }

        WasapiAudioInput.Open("approved-endpoint", enumerator, clock, TryOpen);

        Assert.Equal(3, capturedIds.Count);
        Assert.All(capturedIds, id => Assert.Equal("approved-endpoint", id));
    }

    [Fact]
    public void TryOpenOnce_DeviceNotActive_ReturnsInactive()
    {
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator
        {
            GetDeviceCallback = _ => new FakeDevice { State = DeviceState.Unplugged }
        };

        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, WasapiAudioInput.TryOpenOnce);

        Assert.Null(input);
        Assert.Equal("audio_endpoint_inactive", code);
        Assert.Contains("unplugged", reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryInitializeCapture_AllCandidatesFail_DisposesDeviceAndReturnsDiagnostics()
    {
        var clock = new FakeClock();
        var device = new FakeDevice();
        var createdClients = new List<FakeAudioClient>();
        device.CreateAudioClientCallback = () =>
        {
            var client = new FakeAudioClient
            {
                // Use an extensible mix format so mix-standard and mix-raw differ,
                // yielding the full set of five distinct candidates.
                MixFormat = new WaveFormatExtensible(48000, 24, 2),
                InitializeException = new COMException("Value does not fall within the expected range.", unchecked((int)0x80070057))
            };
            createdClients.Add(client);
            return client;
        };

        var (input, code, reason) = WasapiAudioInput.TryInitializeCapture(device, "{e}", DeviceState.Active);

        Assert.Null(input);
        Assert.Equal("audio_format_negotiation_failure", code);
        Assert.Contains("EndpointId={e}", reason);
        Assert.Contains("HRESULT=0x80070057", reason);
        Assert.Contains("CandidateIndex=4", reason);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(6, createdClients.Count); // probe + 5 candidates
        Assert.All(createdClients, c => Assert.Equal(1, c.DisposeCount));
    }

    [Fact]
    public void TryInitializeCapture_Success_TransfersOwnershipAndDoesNotDisposeObjects()
    {
        var clock = new FakeClock();
        var device = new FakeDevice();
        FakeAudioClient? createdClient = null;
        FakeAudioCaptureClient? createdCapture = null;
        device.CreateAudioClientCallback = () =>
        {
            createdClient = new FakeAudioClient();
            createdCapture = new FakeAudioCaptureClient();
            createdClient.CaptureClientFactory = () => createdCapture;
            return createdClient;
        };

        var (input, code, reason) = WasapiAudioInput.TryInitializeCapture(device, "{e}", DeviceState.Active);

        Assert.NotNull(input);
        Assert.Null(code);
        Assert.Null(reason);
        Assert.NotNull(createdClient);
        Assert.NotNull(createdCapture);
        Assert.Equal(0, device.DisposeCount);
        Assert.Equal(0, createdClient.DisposeCount);
        Assert.Equal(0, createdCapture.DisposeCount);

        input.Dispose();
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, createdClient.DisposeCount);
        Assert.Equal(1, createdCapture.DisposeCount);
    }

    [Fact]
    public void Open_StartRecordingFailureClassifiedAsTransient_RetriesUntilSuccess()
    {
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();
        int attempt = 0;

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpenWithStartFailure(string endpointId, IDeviceEnumerator e)
        {
            attempt++;
            if (attempt == 1)
                return (null, "audio_capture_start_failed", "StartRecording failed");
            return (new StubInput(), null, null);
        }

        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, TryOpenWithStartFailure);

        Assert.NotNull(input);
        Assert.Null(code);
        Assert.Null(reason);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public void Open_BudgetSmallerThanFirstBackoff_DoesNotStartExtraAttempt()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            return (null, "audio_endpoint_unavailable", "transient");
        }

        // Budget is smaller than the first backoff (100ms), so after the first
        // failure the sleep is truncated to the remaining budget and the loop-top
        // check must prevent a second attempt.
        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, TryOpen, TimeSpan.FromMilliseconds(50));

        Assert.Null(input);
        Assert.Equal("audio_endpoint_unavailable", code);
        Assert.Equal(1, attempts);
        Assert.Single(clock.SleptDelays);
        Assert.Equal(50, clock.SleptDelays[0].TotalMilliseconds);
    }

    [Fact]
    public void Open_ZeroBudget_DoesNotStartAnyAttempt()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            return (null, "audio_endpoint_unavailable", "transient");
        }

        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, TryOpen, TimeSpan.Zero);

        Assert.Null(input);
        Assert.Equal("audio_helper_runtime_failure", code);
        Assert.Equal(0, attempts);
        Assert.Empty(clock.SleptDelays);
    }

    [Fact]
    public void Open_AttemptConsumesBudgetToDeadline_DoesNotStartNextAttempt()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            // The attempt itself advances virtual time to the deadline.
            clock.Advance(TimeSpan.FromSeconds(5));
            return (null, "audio_format_negotiation_failure", "slow");
        }

        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, TryOpen, TimeSpan.FromSeconds(5));

        Assert.Null(input);
        Assert.Equal("audio_format_negotiation_failure", code);
        Assert.Equal(1, attempts);
        Assert.Empty(clock.SleptDelays);
    }

    [Fact]
    public void Open_DeadlineNotReached_AllowsNextAttempt()
    {
        int attempts = 0;
        var clock = new FakeClock();
        var enumerator = new FakeEnumerator();

        (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpen(string endpointId, IDeviceEnumerator e)
        {
            attempts++;
            if (attempts < 2)
                return (null, "audio_endpoint_unavailable", "transient");
            return (new StubInput(), null, null);
        }

        var (input, code, reason) = WasapiAudioInput.Open("{e}", enumerator, clock, TryOpen, TimeSpan.FromSeconds(5));

        Assert.NotNull(input);
        Assert.Null(code);
        Assert.Null(reason);
        Assert.Equal(2, attempts);
    }

    // -----------------------------------------------------------------
    // AudioClientAudioInput lifecycle and packet tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task StartRecording_StopRecordingDuringStarting_DoesNotLoseStop()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var startGate = new ManualResetEventSlim(false);
            var client = new FakeAudioClient
            {
                StartAction = () => startGate.Wait()
            };
            var capture = new FakeAudioCaptureClient();
            var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);
            var stoppedCount = 0;
            input.RecordingStopped += (_, _) => Interlocked.Increment(ref stoppedCount);

            // StartRecording blocks inside AudioClient.Start until the gate is set,
            // giving us a deterministic point in the Starting state to request stop.
            var startTask = Task.Run(() => input.StartRecording());
            await Task.Run(() => SpinWait.SpinUntil(() => startTask.Status == TaskStatus.Running, TimeSpan.FromSeconds(2)));
            input.StopRecording();
            startGate.Set();
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(stoppedCount > 0, "RecordingStopped must be raised exactly once");
            Assert.True(client.Stopped, "AudioClient must be stopped");
            input.Dispose();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void StartRecording_ThrowsAudioCaptureStartException_OnAudioClientStartFailure()
    {
        var client = new FakeAudioClient
        {
            StartException = new COMException("E_INVALIDARG", unchecked((int)0x80070057))
        };
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);

        var ex = Assert.Throws<AudioCaptureStartException>(() => input.StartRecording());
        Assert.Equal(unchecked((int)0x80070057), ex.Hresult);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_GetNextPacketSizeThrows_PropagatesRuntimeException()
    {
        var client = new FakeAudioClient();
        var capture = new FakeAudioCaptureClient
        {
            GetNextPacketSizeException = new COMException("device lost", unchecked((int)0x80070490))
        };
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);
        Exception? observed = null;
        input.RecordingStopped += (_, e) => observed = e.Exception;

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);
        Assert.Equal("GetNextPacketSize", runtimeEx.Stage);
        Assert.Equal(unchecked((int)0x80070490), runtimeEx.Hresult);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_LargerThanBuffer_SplitsIntoValidEvents()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var events = new List<WaveInEventArgs>();
        input.DataAvailable += (_, e) => events.Add(e);

        // One packet of 800 frames = 1600 bytes, larger than initial 10-frame buffer.
        var largeBuffer = new byte[1600];
        new Random(42).NextBytes(largeBuffer);
        var pinned = GCHandle.Alloc(largeBuffer, GCHandleType.Pinned);
        try
        {
            capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 800, AudioClientBufferFlags.None));

            input.StartRecording();
            Assert.True(SpinWait.SpinUntil(() => events.Count >= 1, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            pinned.Free();
        }

        Assert.All(events, e => Assert.True(e.BytesRecorded <= e.Buffer.Length && e.BytesRecorded > 0));
        Assert.Equal(800 * 2, events.Sum(e => e.BytesRecorded));
        Assert.Equal(1, capture.ReleaseBufferCallCount);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_CopyThrows_ReleasesBufferExactlyOnce()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        input.DataAvailable += (_, _) => throw new InvalidOperationException("callback failure");

        var buffer = new byte[320];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 160, AudioClientBufferFlags.None));

            input.StartRecording();
            Assert.True(SpinWait.SpinUntil(() => capture.ReleaseBufferCallCount > 0, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            pinned.Free();
        }

        Assert.Equal(1, capture.ReleaseBufferCallCount);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_SilentPacket_WritesZeros()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var events = new List<WaveInEventArgs>();
        input.DataAvailable += (_, e) => events.Add(e);

        // 10 frames = 20 bytes, exactly one record-buffer chunk for BufferSize=10.
        capture.Packets.Enqueue((IntPtr.Zero, 10, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => events.Count >= 1, TimeSpan.FromSeconds(2)));

        Assert.Equal(20, events[0].BytesRecorded);
        Assert.All(events[0].Buffer.Take(20), b => Assert.Equal(0, b));
        input.Dispose();
    }

    [Fact]
    public void StartRecording_FirstPacketArrivesSynchronously_DoesNotTimeout()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var buffer = new byte[320];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 160, AudioClientBufferFlags.None));
            capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

            var events = new List<WaveInEventArgs>();
            input.DataAvailable += (_, e) => events.Add(e);

            input.StartRecording();
            Assert.True(SpinWait.SpinUntil(() => events.Count >= 1, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            pinned.Free();
        }

        input.StopRecording();
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_SilentWithNullBuffer_EmitsZeroSamples()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var events = new List<WaveInEventArgs>();
        input.DataAvailable += (_, e) => events.Add(e);

        // Silent packet with IntPtr.Zero and 160 frames = 320 bytes.
        capture.Packets.Enqueue((IntPtr.Zero, 160, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => events.Sum(e => e.BytesRecorded) >= 320, TimeSpan.FromSeconds(2)), "All zero samples should be emitted");

        Assert.Equal(320, events.Sum(e => e.BytesRecorded));
        Assert.All(events.SelectMany(e => e.Buffer.Take(e.BytesRecorded)), b => Assert.Equal(0, b));
        Assert.All(events, e => Assert.True(e.BytesRecorded <= GetRecordBufferLength(input)));
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_NonSilentNullBuffer_ProducesRuntimeError()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        Exception? observed = null;
        input.RecordingStopped += (_, e) => observed = e.Exception;

        capture.Packets.Enqueue((IntPtr.Zero, 160, AudioClientBufferFlags.None));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);
        Assert.Equal("ReadPacket", runtimeEx.Stage);
        Assert.Equal(unchecked((int)0x80004003), runtimeEx.Hresult);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_SilentLargePacket_SplitsIntoChunks()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var events = new List<WaveInEventArgs>();
        var eventsLock = new object();
        input.DataAvailable += (_, e) => { lock (eventsLock) events.Add(e); };

        // 2000 frames = 4000 bytes, larger than the 20-byte record buffer.
        capture.Packets.Enqueue((IntPtr.Zero, 2000, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => { lock (eventsLock) return events.Sum(e => e.BytesRecorded) >= 4000; }, TimeSpan.FromSeconds(2)), "All silent chunks should be emitted");

        lock (eventsLock)
        {
            Assert.Equal(4000, events.Sum(e => e.BytesRecorded));
            Assert.All(events, e => Assert.True(e.BytesRecorded > 0 && e.BytesRecorded <= GetRecordBufferLength(input)));
            Assert.All(events.SelectMany(e => e.Buffer.Take(e.BytesRecorded)), b => Assert.Equal(0, b));
        }
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_ConsecutiveSilentPackets_AccumulateCorrectly()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var format = new WaveFormat(16000, 16, 1);
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, format, 100);

        var events = new List<WaveInEventArgs>();
        var eventsLock = new object();
        input.DataAvailable += (_, e) => { lock (eventsLock) events.Add(e); };

        capture.Packets.Enqueue((IntPtr.Zero, 80, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 120, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => { lock (eventsLock) return events.Sum(e => e.BytesRecorded) >= 400; }, TimeSpan.FromSeconds(2)));

        lock (eventsLock)
        {
            Assert.Equal(400, events.Sum(e => e.BytesRecorded));
            Assert.All(events.SelectMany(e => e.Buffer.Take(e.BytesRecorded)), b => Assert.Equal(0, b));
        }
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_GetBufferFailure_StageIsGetBuffer()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var capture = new FakeAudioCaptureClient
        {
            GetBufferException = new COMException("E_INVALIDARG", unchecked((int)0x80070057))
        };
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);

        Exception? observed = null;
        input.RecordingStopped += (_, e) => observed = e.Exception;

        capture.Packets.Enqueue((IntPtr.Zero, 1, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);
        Assert.Equal("GetBuffer", runtimeEx.Stage);
        Assert.Equal(unchecked((int)0x80070057), runtimeEx.Hresult);
        Assert.Equal(0, capture.ReleaseBufferCallCount);
        Assert.Null(runtimeEx.SecondaryFailure);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_ReleaseBufferFailure_StageIsReleaseBuffer()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var capture = new FakeAudioCaptureClient
        {
            ReleaseBufferException = new COMException("E_FAIL", unchecked((int)0x80004005))
        };
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);

        Exception? observed = null;
        input.RecordingStopped += (_, e) => observed = e.Exception;

        capture.Packets.Enqueue((IntPtr.Zero, 10, AudioClientBufferFlags.Silent));
        capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        input.StartRecording();
        Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);
        Assert.Equal("ReleaseBuffer", runtimeEx.Stage);
        Assert.Equal(unchecked((int)0x80004005), runtimeEx.Hresult);
        Assert.Equal(1, capture.ReleaseBufferCallCount);
        Assert.Null(runtimeEx.SecondaryFailure);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_CallbackFailure_StageIsReadPacket()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var capture = new FakeAudioCaptureClient();
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);

        Exception? observed = null;
        input.DataAvailable += (_, _) => throw new InvalidOperationException("callback failure");
        input.RecordingStopped += (_, e) => observed = e.Exception;

        var buffer = new byte[20];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 10, AudioClientBufferFlags.None));
            capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

            input.StartRecording();
            Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");
        }
        finally
        {
            pinned.Free();
        }

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);
        Assert.Equal("ReadPacket", runtimeEx.Stage);
        input.Dispose();
    }

    [Fact]
    public void ReadNextPacket_CallbackAndReleaseBufferFail_PrimaryReadPacketWithSecondaryReleaseBuffer()
    {
        var client = new FakeAudioClient { BufferSize = 10 };
        var capture = new FakeAudioCaptureClient
        {
            ReleaseBufferException = new COMException("release boom", unchecked((int)0x80004005))
        };
        var input = new AudioClientAudioInput(new FakeDevice(), client, capture, new WaveFormat(16000, 16, 1), 100);

        var callbackException = new InvalidOperationException("callback failure");
        Exception? observed = null;
        input.DataAvailable += (_, _) => throw callbackException;
        input.RecordingStopped += (_, e) => observed = e.Exception;

        var buffer = new byte[20];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 10, AudioClientBufferFlags.None));
            capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

            input.StartRecording();
            Assert.True(SpinWait.SpinUntil(() => observed != null, TimeSpan.FromSeconds(2)), "RecordingStopped must fire with error");
        }
        finally
        {
            pinned.Free();
        }

        var runtimeEx = Assert.IsType<AudioCaptureRuntimeException>(observed);

        // The packet-processing error keeps root-cause priority: stage and
        // HRESULT still describe the ReadPacket failure, not ReleaseBuffer.
        Assert.Equal("ReadPacket", runtimeEx.Stage);
        Assert.Equal(callbackException.HResult, runtimeEx.Hresult);

        // The ReleaseBuffer failure is retained as structured secondary
        // diagnostics: stage, HRESULT, exception type, and message.
        var secondary = runtimeEx.SecondaryFailure;
        Assert.NotNull(secondary);
        Assert.Equal("ReleaseBuffer", secondary.Stage);
        Assert.Equal(unchecked((int)0x80004005), secondary.Hresult);
        Assert.Equal("COMException", secondary.ExceptionType);
        Assert.Contains("release boom", secondary.FailureMessage);

        // Message composes both failures so reason-only consumers (helper
        // terminal event) surface the secondary diagnostics as well.
        Assert.Contains("ReadPacket", runtimeEx.Message);
        Assert.Contains("ReleaseBuffer", runtimeEx.Message);
        Assert.Contains("0x80004005", runtimeEx.Message);

        // GetBuffer succeeded, so ReleaseBuffer was called exactly once.
        Assert.Equal(1, capture.ReleaseBufferCallCount);
        input.Dispose();
    }

    // -----------------------------------------------------------------
    // Gated concurrency tests for Dispose/Start/Capture ownership
    // -----------------------------------------------------------------

    private sealed class GatedStartAudioClient : FakeAudioClient
    {
        public ManualResetEventSlim EnteredStartGate { get; } = new(false);
        public ManualResetEventSlim ReleaseStartGate { get; } = new(false);

        public override void Start()
        {
            EnteredStartGate.Set();
            ReleaseStartGate.Wait();
            base.Start();
        }
    }

    private sealed class GatedCaptureClient : FakeAudioCaptureClient
    {
        public ManualResetEventSlim EnteredGate { get; } = new(false);
        public ManualResetEventSlim ReleaseGate { get; } = new(false);

        public override int GetNextPacketSize()
        {
            EnteredGate.Set();
            ReleaseGate.Wait();
            return base.GetNextPacketSize();
        }
    }

    [Fact]
    public async Task Dispose_DuringStarting_TimeoutThenStartReleasesExactlyOnce()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var client = new GatedStartAudioClient();
            var capture = new FakeAudioCaptureClient();
            var device = new FakeDevice();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromMilliseconds(50));

            StartRecordingResult startResult = StartRecordingResult.Started;
            var startTask = Task.Run(() => { startResult = input.StartRecording(); });

            // Wait until StartRecording is blocked inside AudioClient.Start.
            Assert.True(client.EnteredStartGate.Wait(TimeSpan.FromSeconds(2)), "Start must enter AudioClient.Start");

            // While Start is stuck, COM objects must not be released and Dispose
            // must not report success.
            Assert.Equal(0, device.DisposeCount);
            Assert.Equal(0, client.DisposeCount);
            Assert.Equal(0, capture.DisposeCount);
            Assert.False(input.DisposeCompletedSuccessfully);

            // Dispose will time out waiting for Start and hand ownership to Start's finally.
            var disposeTask = Task.Run(() => input.Dispose());
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            // Dispose returned, but Start is still inside AudioClient.Start: objects
            // must still be alive and Dispose must report incomplete.
            Assert.Equal(0, device.DisposeCount);
            Assert.Equal(0, client.DisposeCount);
            Assert.Equal(0, capture.DisposeCount);
            Assert.False(input.DisposeCompletedSuccessfully);

            // Release Start. Start's finally must release exactly once.
            client.ReleaseStartGate.Set();
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StartRecordingResult.Disposed, startResult);
            Assert.Equal(1, device.DisposeCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Equal(1, capture.DisposeCount);
            Assert.True(input.DisposeCompletedSuccessfully);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Dispose_DuringCapturing_WaitForThreadThenReleasesExactlyOnce()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var client = new FakeAudioClient { BufferSize = 10 };
            var capture = new GatedCaptureClient();
            var device = new FakeDevice();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromSeconds(2));

            var startResultTask = Task.Run(() => input.StartRecording());

            // Wait until the capture thread is blocked inside GetNextPacketSize.
            Assert.True(capture.EnteredGate.Wait(TimeSpan.FromSeconds(2)), "Capture thread must enter GetNextPacketSize");

            // COM objects must not be released while the capture thread is using them.
            Assert.Equal(0, device.DisposeCount);
            Assert.Equal(0, client.DisposeCount);
            Assert.Equal(0, capture.DisposeCount);

            var disposeTask = Task.Run(() => input.Dispose());
            // Dispose must wait for the capture thread to exit; do not release the gate yet.
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(disposeTask.IsCompleted, "Dispose must block until capture thread exits");

            // Objects still alive.
            Assert.Equal(0, device.DisposeCount);
            Assert.Equal(0, client.DisposeCount);
            Assert.Equal(0, capture.DisposeCount);

            // Release the capture thread. It must exit and Dispose must complete.
            capture.ReleaseGate.Set();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            var startResult = await startResultTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StartRecordingResult.Started, startResult);

            Assert.Equal(1, device.DisposeCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Equal(1, capture.DisposeCount);
            Assert.True(input.DisposeCompletedSuccessfully);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Theory]
    [InlineData(true)]   // Dispose times out before capture thread exits; thread finally cleans up.
    [InlineData(false)]  // Capture thread exits before Dispose times out; Dispose cleans up.
    public async Task Dispose_JoinTimeoutOwnership_ExactlyOnceCleanup(bool threadExitsAfterTimeout)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var client = new FakeAudioClient { BufferSize = 10 };
            var capture = new GatedCaptureClient();
            var device = new FakeDevice();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromMilliseconds(20));

            var startResultTask = Task.Run(() => input.StartRecording());
            Assert.True(capture.EnteredGate.Wait(TimeSpan.FromSeconds(2)), "Capture thread must enter GetNextPacketSize");

            var disposeTask = Task.Run(() => input.Dispose());

            if (threadExitsAfterTimeout)
            {
                // Dispose has a 20ms join timeout, so it should return well
                // before this delay while the capture thread is still blocked.
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                Assert.True(disposeTask.IsCompleted, "Dispose must have timed out and returned");
                Assert.True(device.DisposeCount == 0, "COM objects must not be released until the thread exits");
                capture.ReleaseGate.Set();
            }
            else
            {
                // Let the thread exit before Dispose times out.
                capture.ReleaseGate.Set();
            }

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            // Wait for Start to return (it will see Disposed and exit).
            await startResultTask.WaitAsync(TimeSpan.FromSeconds(2));

            // If Dispose timed out, the capture thread/Start finally owns
            // cleanup and may still be releasing COM objects.
            Assert.True(SpinWait.SpinUntil(() => input.DisposeCompletedSuccessfully, TimeSpan.FromSeconds(2)), "Cleanup must complete");

            Assert.Equal(1, device.DisposeCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Equal(1, capture.DisposeCount);
            Assert.True(input.DisposeCompletedSuccessfully);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Dispose_ConcurrentDoubleDispose_ReleasesExactlyOnce()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var device = new FakeDevice();
            var client = new FakeAudioClient { BufferSize = 10 };
            var capture = new FakeAudioCaptureClient();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromSeconds(2));
            input.StartRecording();
            try
            {
                var dispose1 = Task.Run(() => input.Dispose());
                var dispose2 = Task.Run(() => input.Dispose());

                await Task.WhenAll(dispose1, dispose2).WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(1, device.DisposeCount);
                Assert.Equal(1, client.DisposeCount);
                Assert.Equal(1, capture.DisposeCount);
                Assert.True(input.DisposeCompletedSuccessfully);
            }
            finally
            {
                input.Dispose();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Dispose_StopThenDisposeWhileCapturing_ReleasesExactlyOnce()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var device = new FakeDevice();
            var client = new FakeAudioClient { BufferSize = 10 };
            var capture = new FakeAudioCaptureClient();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromSeconds(2));
            input.StartRecording();
            try
            {
                var stopTask = Task.Run(() => input.StopRecording());
                var disposeTask = Task.Run(() => input.Dispose());

                await Task.WhenAll(stopTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(1, device.DisposeCount);
                Assert.Equal(1, client.DisposeCount);
                Assert.Equal(1, capture.DisposeCount);
                Assert.True(input.DisposeCompletedSuccessfully);
            }
            finally
            {
                input.Dispose();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Dispose_CaptureErrorWhileDisposing_ReleasesExactlyOnce()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var client = new FakeAudioClient { BufferSize = 10 };
            var capture = new GatedCaptureClient
            {
                GetNextPacketSizeException = new COMException("device lost", unchecked((int)0x80070490))
            };
            var device = new FakeDevice();
            var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromSeconds(2));

            var startResultTask = Task.Run(() => input.StartRecording());
            Assert.True(capture.EnteredGate.Wait(TimeSpan.FromSeconds(2)), "Capture thread must enter GetNextPacketSize");

            var disposeTask = Task.Run(() => input.Dispose());
            capture.ReleaseGate.Set();

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            await startResultTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, device.DisposeCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Equal(1, capture.DisposeCount);
            Assert.True(input.DisposeCompletedSuccessfully);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task StartRecording_DisposeRacesDuringThreadCreation_AlwaysReleasesResources()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            for (int i = 0; i < 50; i++)
            {
                var device = new FakeDevice();
                var client = new FakeAudioClient { BufferSize = 10 };
                var capture = new FakeAudioCaptureClient();
                var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100, TimeSpan.FromMilliseconds(10));

                var startTask = Task.Run(() => input.StartRecording());

                // Race Dispose very early, trying to hit the window after the
                // thread object is created but before Capturing is published.
                var disposeTask = Task.Run(() => input.Dispose());

                await Task.WhenAll(startTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));

                Assert.True(device.DisposeCount == 1, $"Iteration {i}: device must be disposed exactly once");
                Assert.True(client.DisposeCount == 1, $"Iteration {i}: client must be disposed exactly once");
                Assert.True(capture.DisposeCount == 1, $"Iteration {i}: capture client must be disposed exactly once");
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
}
