using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

/// <summary>
/// Deterministic tests for runtime starvation detection, bounded same-endpoint
/// recovery, gap padding, and stop/dispose priority in <see cref="CaptureSession"/>.
/// </summary>
public class AudioHelperRuntimeRecoveryTests
{
    private const int BytesPerMs = 32; // 16000 Hz x 16-bit mono = 32000 B/s

    private sealed class ScriptedAudioInput : IAudioInput
    {
        public WaveFormat? Format { get; set; } = new WaveFormat(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        private int _disposeCount;
        private int _startCallCount;

        public StartRecordingResult StartResult { get; set; } = StartRecordingResult.Started;
        public Action<ScriptedAudioInput>? OnStartRecording { get; set; }
        public Action<ScriptedAudioInput>? OnStopRecording { get; set; }
        public Action<ScriptedAudioInput>? OnDispose { get; set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int StartCallCount => Volatile.Read(ref _startCallCount);
        public long DiscontinuityCount { get; set; }

        public StartRecordingResult StartRecording()
        {
            Interlocked.Increment(ref _startCallCount);
            Started = true;
            OnStartRecording?.Invoke(this);
            return StartResult;
        }

        public void StopRecording()
        {
            if (Stopped) return;
            Stopped = true;
            OnStopRecording?.Invoke(this);
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void InjectData(byte[] buffer, int bytesRecorded)
        {
            DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesRecorded));
        }

        public void InjectError(Exception ex)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }

        public void InjectStopped()
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public EventHandler<WaveInEventArgs>? CaptureDataDelegate()
            => DataAvailable;

        public EventHandler<StoppedEventArgs>? CaptureStoppedDelegate()
            => RecordingStopped;

        public void InvokeDataDelegate(EventHandler<WaveInEventArgs>? handler, byte[] buffer)
        {
            handler?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));
        }

        public void InvokeStoppedDelegate(EventHandler<StoppedEventArgs>? handler, Exception? exception = null)
        {
            handler?.Invoke(this, new StoppedEventArgs(exception));
        }

        public void Dispose()
        {
            OnDispose?.Invoke(this);
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class ScriptedInputFactory
    {
        private readonly Queue<Func<(IAudioInput? Input, string? ErrorCode, string? Reason)>> _script = new();
        private readonly object _lock = new();
        private int _callCount;

        public int CallCount
        {
            get { lock (_lock) return _callCount; }
        }

        public List<ScriptedAudioInput> CreatedInputs { get; } = new();

        public void EnqueueInput(ScriptedAudioInput input)
        {
            lock (_lock) _script.Enqueue(() =>
            {
                CreatedInputs.Add(input);
                return ((IAudioInput?)input, null, null);
            });
        }

        public void EnqueueFailure(string code, string reason)
        {
            lock (_lock) _script.Enqueue(() => ((IAudioInput?)null, code, reason));
        }

        public void EnqueueGate(ManualResetEventSlim entered, ManualResetEventSlim release, ScriptedAudioInput input)
        {
            lock (_lock) _script.Enqueue(() =>
            {
                entered.Set();
                release.Wait();
                CreatedInputs.Add(input);
                return ((IAudioInput?)input, null, null);
            });
        }

        public (IAudioInput? Input, string? ErrorCode, string? Reason) Open(TimeSpan budget)
        {
            Func<(IAudioInput?, string?, string?)>? next = null;
            lock (_lock)
            {
                _callCount++;
                if (_script.Count > 0)
                    next = _script.Dequeue();
            }

            if (next != null)
                return next();

            // Default: a fresh silent input (used by the budget-exhaustion test).
            var fallback = new ScriptedAudioInput();
            lock (_lock) CreatedInputs.Add(fallback);
            return (fallback, null, null);
        }
    }

    private static AudioHelperOptions Options(string recordingId, string output, string stopSignal)
    {
        return new AudioHelperOptions
        {
            Mode = AudioHelperMode.Capture,
            EndpointId = "{0.0.1.00000000}.{guid}",
            OutputPath = output,
            AllowedRoot = Path.GetDirectoryName(output)!,
            StopSignalPath = stopSignal,
            RecordingId = recordingId
        };
    }

    private static PathCheckResult PathResult(string output, string partial)
    {
        return new PathCheckResult
        {
            Ok = true,
            CanonicalPath = output,
            PartialPath = partial,
            OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        };
    }

    private sealed class SessionHarness : IDisposable
    {
        public string Dir { get; }
        public string Output { get; }
        public string Partial { get; }
        public string StopSignal { get; }
        public StringWriter Sw { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public StopWatcher Watcher { get; }
        public CaptureSession Session { get; }

        public SessionHarness(string recordingId, ScriptedInputFactory factory,
            TimeSpan? stallThreshold = null, TimeSpan? gapThreshold = null, TimeSpan? maxSingleGapPad = null)
        {
            Dir = Path.Combine(Path.GetTempPath(), $"ah_recovery_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
            Output = Path.Combine(Dir, "rec.wav");
            Partial = Path.Combine(Dir, $"rec.{Environment.ProcessId}.partial.wav");
            StopSignal = Path.Combine(Dir, "stop.signal");

            var opts = Options(recordingId, Output, StopSignal);
            var paths = PathResult(Output, Partial);
            Watcher = new StopWatcher(StopSignal, () => Cts.Cancel());
            Session = new CaptureSession(opts, paths, new EventWriter(Sw, null), Watcher, Cts, factory.Open,
                stallDetectionThreshold: stallThreshold ?? TimeSpan.FromMilliseconds(150),
                runtimeGapThreshold: gapThreshold,
                maxSingleGapPad: maxSingleGapPad);
        }

        public List<AudioHelperEvent> Events => AudioHelperEventStreamParser.ParseEvents(Sw.ToString());

        public AudioHelperEvent Terminal =>
            Events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);

        public void SignalStop() => File.WriteAllText(StopSignal, "stop");

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { Watcher.Dispose(); } catch { }
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    private static byte[] Zeros(int count) => new byte[count];

    private static byte[] Pattern(int count, byte value)
        => Enumerable.Repeat(value, count).Select(v => (byte)v).ToArray();

    private static byte[] ReadWavData(string path)
    {
        using var reader = new WaveFileReader(path);
        var data = new byte[(int)reader.Length];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = reader.Read(data, offset, data.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        return data;
    }

    // -----------------------------------------------------------------
    // 1. Complete callback stop -> single recovery on same endpoint -> continue
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_CallbackStopsCompletely_RecoversOnceOnSameEndpoint_AndContinuesWithPaddedTimeline()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_recover_once", factory);
        var runTask = Task.Run(() => h.Session.Run());

        // Deliver some real media, then the stream goes completely silent.
        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        var injectedTotal = 10 * 320;
        foreach (var _ in Enumerable.Range(0, 10))
            input1.InjectData(Zeros(320), 320);

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)),
            "Recovery must reopen and start a replacement input on the same endpoint factory");

        // The starved input was stopped and released exactly once.
        Assert.True(input1.Stopped);
        Assert.Equal(1, input1.DisposeCount);

        // The recovered stream keeps delivering real packets.
        injectedTotal += 5 * 320;
        foreach (var _ in Enumerable.Range(0, 5))
            input2.InjectData(Zeros(320), 320);

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var events = h.Events;
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Started);
        Assert.DoesNotContain(events, e => e.Result == AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);

        // Recovery metrics: exactly one recovery; continuity degraded; the
        // measured hole was padded with block-aligned zeros and the timeline
        // (bytes) equals real media + padded media exactly.
        Assert.Equal(1, terminal.RecoveryCount);
        Assert.Equal(1, terminal.RecoveryAttempts);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        Assert.NotNull(terminal.GapFilledBytes);
        Assert.True(terminal.GapFilledBytes > 0, "Measured gap must be gap-filled so the timeline is not shortened");
        Assert.True(terminal.GapFilledBytes!.Value % 2 == 0, "Gap fill must be block-aligned (2 bytes/frame)");
        Assert.Equal(injectedTotal + terminal.GapFilledBytes.Value, terminal.BytesWritten);
        Assert.True(terminal.MaxEstimatedGapMs >= terminal.EstimatedGapMs);

        // The published WAV carries the padded timeline: data length equals
        // terminal BytesWritten exactly.
        Assert.True(File.Exists(h.Output));
        long wavDataBytes;
        using (var reader = new WaveFileReader(h.Output))
            wavDataBytes = reader.Length;
        Assert.Equal(terminal.BytesWritten, wavDataBytes);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 2. Sporadic callbacks with growing gap -> recovery not bypassed by byte growth
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_SporadicCallbacksWithGrowingGap_RecoveryNotBypassedByByteGrowth()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        // Starvation threshold high so only the gap-divergence trigger can fire;
        // bytes keep growing slowly so the no-growth check is always satisfied-false.
        using var h = new SessionHarness("rec_gap_diverge", factory,
            stallThreshold: TimeSpan.FromSeconds(30),
            gapThreshold: TimeSpan.FromMilliseconds(300));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));

        // Trickle: 1ms of media every 60ms of wall time. Bytes grow on every
        // stall check, but the media timeline falls behind ~59ms per 60ms.
        using var trickleCts = new CancellationTokenSource();
        var trickle = Task.Run(async () =>
        {
            while (!trickleCts.IsCancellationRequested && !input2.Started)
            {
                input1.InjectData(Zeros(BytesPerMs), BytesPerMs);
                await Task.Delay(60);
            }
        });

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(15)),
            "Gap divergence must trigger recovery even though bytes keep growing");
        trickleCts.Cancel();
        try { await trickle; } catch { }

        foreach (var _ in Enumerable.Range(0, 5))
            input2.InjectData(Zeros(320), 320);

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(1, terminal.RecoveryCount);
        Assert.Equal("degraded", terminal.ContinuityStatus);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 3. Normal jitter -> no recovery, no padding
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_NormalJitter_DoesNotRecoverOrPad()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);

        using var h = new SessionHarness("rec_jitter", factory,
            stallThreshold: TimeSpan.FromMilliseconds(400));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));

        // Real-time-rate delivery with ordinary scheduling jitter (15-60ms
        // between bursts, media matched to the actual delay): the wall/media
        // gap stays near zero and far below the 2s default gap threshold.
        var delays = new[] { 25, 40, 15, 60, 30, 25, 50, 20, 35, 25, 45, 20 };
        long injectedTotal = 0;
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            int bytes = BytesPerMs * delay;
            input1.InjectData(Zeros(bytes), bytes);
            injectedTotal += bytes;
        }

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(0, terminal.RecoveryCount);
        Assert.Equal(0, terminal.GapFilledBytes);
        Assert.Equal("continuous", terminal.ContinuityStatus);
        Assert.Equal(injectedTotal, terminal.BytesWritten);
        Assert.Equal(1, factory.CallCount);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 5. Gap padding: block-aligned, matches measured gap, strict cap
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_RecoveryGapPadding_RespectsInjectedCap()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        // Cap a single pad at 50ms of media (1600 bytes @ 32KB/s).
        using var h = new SessionHarness("rec_pad_cap", factory,
            stallThreshold: TimeSpan.FromMilliseconds(150),
            maxSingleGapPad: TimeSpan.FromMilliseconds(50));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        long injectedTotal = 10 * 320;
        foreach (var _ in Enumerable.Range(0, 10))
            input1.InjectData(Zeros(320), 320);

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)));
        injectedTotal += 5 * 320;
        foreach (var _ in Enumerable.Range(0, 5))
            input2.InjectData(Zeros(320), 320);

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(1, terminal.RecoveryCount);
        // The measured gap (~hundreds of ms) exceeds the 50ms cap: padding must
        // stop exactly at the block-aligned cap and the continuity must be marked
        // degraded with the remaining gap visible in the terminal metrics.
        Assert.Equal(1600, terminal.GapFilledBytes);
        Assert.Equal(injectedTotal + 1600, terminal.BytesWritten);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        Assert.True(terminal.EstimatedGapMs > 0, "The unpadded remainder of the gap stays visible");

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 6a. Recovery vs user stop: stop wins, no revive, single terminal
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_RecoveryRacesUserStop_StopWins_NoRevive_SingleTerminal()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        var factoryEntered = new ManualResetEventSlim(false);
        var factoryRelease = new ManualResetEventSlim(false);
        factory.EnqueueInput(input1);
        factory.EnqueueGate(factoryEntered, factoryRelease, input2);

        using var h = new SessionHarness("rec_recovery_stop", factory,
            stallThreshold: TimeSpan.FromMilliseconds(150));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        foreach (var _ in Enumerable.Range(0, 5))
            input1.InjectData(Zeros(320), 320);

        // Wait until the recovery is blocked inside the reopen, then request stop.
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(10)), "Recovery must reach the reopen attempt");
        h.SignalStop();
        Assert.True(SpinWait.SpinUntil(() => h.Cts.IsCancellationRequested, TimeSpan.FromSeconds(5)),
            "Stop watcher must observe the stop signal while recovery is in flight");
        factoryRelease.Set();

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Stop wins: the candidate was never started (no revive), both inputs
        // were disposed exactly once, and there is exactly one terminal event
        // with user-stop semantics.
        Assert.Equal(0, exitCode);
        Assert.Equal(0, input2.StartCallCount);
        Assert.True(SpinWait.SpinUntil(() => input1.DisposeCount == 1 && input2.DisposeCount == 1, TimeSpan.FromSeconds(5)),
            "Both the starved input and the unstarted recovery candidate must be released exactly once");
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 6b. Recovery vs Dispose: no revive, exactly-once disposal
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_RecoveryRacesDispose_NoRevive_ExactlyOnceDisposal()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        var factoryEntered = new ManualResetEventSlim(false);
        var factoryRelease = new ManualResetEventSlim(false);
        factory.EnqueueInput(input1);
        factory.EnqueueGate(factoryEntered, factoryRelease, input2);

        var dir = Path.Combine(Path.GetTempPath(), $"ah_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var opts = Options("rec_recovery_dispose", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = new StopWatcher(stopSignal, () => cts.Cancel());
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, factory.Open,
            stallDetectionThreshold: TimeSpan.FromMilliseconds(150));

        try
        {
            var runTask = Task.Run(() => session.Run());

            SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
            foreach (var _ in Enumerable.Range(0, 5))
                input1.InjectData(Zeros(320), 320);

            Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(10)), "Recovery must reach the reopen attempt");

            // Request the stop synchronously while the recovery is blocked in
            // the reopen: the stop-requested flag is set before the gate opens,
            // so the recovery must abort without ever starting the candidate.
            session.RequestStop();
            factoryRelease.Set();

            session.Dispose();
            await runTask.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, input2.StartCallCount);
            Assert.Equal(1, input1.DisposeCount);
            Assert.Equal(1, input2.DisposeCount);

            var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
            Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        }
        finally
        {
            watcher.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // 7a. First reopen fails, second succeeds
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_FirstRecoveryOpenFails_SecondSucceeds()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input3 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueFailure("audio_endpoint_unavailable", "transient");
        factory.EnqueueInput(input3);

        using var h = new SessionHarness("rec_recovery_retry", factory,
            stallThreshold: TimeSpan.FromMilliseconds(150));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        foreach (var _ in Enumerable.Range(0, 5))
            input1.InjectData(Zeros(320), 320);

        Assert.True(SpinWait.SpinUntil(() => input3.Started, TimeSpan.FromSeconds(10)),
            "Recovery must retry the reopen on the same endpoint after a transient failure");

        foreach (var _ in Enumerable.Range(0, 5))
            input3.InjectData(Zeros(320), 320);

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(1, terminal.RecoveryCount);
        Assert.Equal(2, terminal.RecoveryAttempts);
        Assert.Equal("degraded", terminal.ContinuityStatus);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input3.DisposeCount);
    }

    // -----------------------------------------------------------------
    // 7b. Recovery budget exhausted -> stable audio_capture_discontinuous
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_RecoveryBudgetExhausted_FailsDiscontinuous()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        // All subsequent opens return fresh inputs that also never deliver.
        // MaxRuntimeRecoveries = 2, so the third starvation exhausts the budget.

        using var h = new SessionHarness("rec_recovery_cap", factory,
            stallThreshold: TimeSpan.FromMilliseconds(150));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        foreach (var _ in Enumerable.Range(0, 5))
            input1.InjectData(Zeros(320), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, exitCode);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Equal(2, terminal.RecoveryCount);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        // Structured root-cause metrics must be present in the terminal reason.
        Assert.Contains("callback_starvation", terminal.Reason ?? "");
        Assert.Contains("wall_elapsed_ms=", terminal.Reason ?? "");
        Assert.Contains("media_elapsed_ms=", terminal.Reason ?? "");
        Assert.Contains("estimated_gap_ms=", terminal.Reason ?? "");
        Assert.Contains("bytes_written=", terminal.Reason ?? "");
        Assert.Contains("last_callback_age_ms=", terminal.Reason ?? "");
        Assert.Contains("discontinuity_count=", terminal.Reason ?? "");
        Assert.False(File.Exists(h.Output));

        h.Session.Dispose();
        Assert.All(factory.CreatedInputs, i => Assert.Equal(1, i.DisposeCount));
    }

    // -----------------------------------------------------------------
    // 7c. All reopen attempts fail -> stable audio_capture_discontinuous
    // -----------------------------------------------------------------

    [Fact]
    public async Task Run_RecoveryOpenAlwaysFails_FailsDiscontinuous()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueFailure("audio_endpoint_unavailable", "gone 1");
        factory.EnqueueFailure("audio_endpoint_unavailable", "gone 2");

        using var h = new SessionHarness("rec_recovery_gone", factory,
            stallThreshold: TimeSpan.FromMilliseconds(150));
        var runTask = Task.Run(() => h.Session.Run());

        SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2));
        foreach (var _ in Enumerable.Range(0, 5))
            input1.InjectData(Zeros(320), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEqual(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Equal(0, terminal.RecoveryCount);
        Assert.Equal(2, terminal.RecoveryAttempts);
        Assert.Contains("reopen attempt", terminal.Reason ?? "");
        Assert.False(File.Exists(h.Output));

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryCandidateStartDeliversSynchronousFirstPacket_WritesOldPaddingThenFirstPacket()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var firstPacket = Pattern(128, 0x7E);
        var input2 = new ScriptedAudioInput
        {
            OnStartRecording = input => input.InjectData(firstPacket, firstPacket.Length)
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_sync_first_packet", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        var oldMedia = Pattern(320, 0x11);
        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(oldMedia, oldMedia.Length);

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)));
        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(1, terminal.RecoveryCount);
        Assert.True(terminal.GapFilledBytes > 0);
        Assert.Equal(oldMedia.Length + terminal.GapFilledBytes + firstPacket.Length, terminal.BytesWritten);

        var data = ReadWavData(h.Output);
        Assert.Equal(terminal.BytesWritten, data.Length);
        Assert.Equal(oldMedia, data.Take(oldMedia.Length).ToArray());
        Assert.All(data.Skip(oldMedia.Length).Take((int)terminal.GapFilledBytes!.Value), b => Assert.Equal(0, b));
        Assert.Equal(firstPacket, data.Skip(oldMedia.Length + (int)terminal.GapFilledBytes.Value).Take(firstPacket.Length).ToArray());

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RecoveryCandidateStopsSynchronouslyInsideStart_ConvergesOnceAsDiscontinuous(bool withRuntimeException)
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        input2.OnStartRecording = input =>
        {
            if (withRuntimeException)
            {
                input.InjectError(new AudioCaptureRuntimeException(
                    "ReadPacket",
                    "ReadPacket failed (COMException, HRESULT=0x88890004): device invalidated",
                    new COMException("device invalidated", unchecked((int)0x88890004)),
                    unchecked((int)0x88890004)));
            }
            else
            {
                input.InjectStopped();
            }
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness($"rec_sync_stop_{withRuntimeException}", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x21), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, exitCode);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        Assert.False(File.Exists(h.Output));
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);

        if (withRuntimeException)
        {
            Assert.Equal("0x88890004", terminal.Hresult);
            Assert.Contains("ReadPacket", terminal.Reason ?? "");
            Assert.Contains("HRESULT=0x88890004", terminal.Reason ?? "");
        }

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RetiredInputLateCallbacks_AreIgnoredAfterRecovery()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_late_callbacks", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x31), 320);
        var lateData = input1.CaptureDataDelegate();
        var lateStopped = input1.CaptureStoppedDelegate();

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)));
        input2.InjectData(Pattern(128, 0x42), 128);

        input1.InvokeDataDelegate(lateData, Pattern(96, 0x66));
        input1.InvokeStoppedDelegate(lateStopped, new InvalidOperationException("late stale stop"));
        input2.InjectData(Pattern(128, 0x43), 128);

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(1, terminal.RecoveryCount);
        var data = ReadWavData(h.Output);
        Assert.DoesNotContain((byte)0x66, data);
        Assert.Contains((byte)0x42, data);
        Assert.Contains((byte)0x43, data);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryCandidateDifferentFormat_IsRejectedBeforeSynchronousDataCanWrite()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var wrongFormat = new ScriptedAudioInput
        {
            Format = new WaveFormat(48000, 16, 2),
            OnStartRecording = input => input.InjectData(Pattern(256, 0x55), 256)
        };
        var input3 = new ScriptedAudioInput
        {
            OnStartRecording = input => input.InjectData(Pattern(128, 0x33), 128)
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(wrongFormat);
        factory.EnqueueInput(input3);

        using var h = new SessionHarness("rec_wrong_format", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x51), 320);

        Assert.True(SpinWait.SpinUntil(() => input3.Started, TimeSpan.FromSeconds(10)));
        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Equal(0, wrongFormat.StartCallCount);
        Assert.Equal(1, wrongFormat.DisposeCount);
        var terminal = h.Terminal;
        Assert.Equal(2, terminal.RecoveryAttempts);
        Assert.Equal(1, terminal.RecoveryCount);
        var data = ReadWavData(h.Output);
        Assert.DoesNotContain((byte)0x55, data);
        Assert.Contains((byte)0x33, data);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input3.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryPaddingAndSynchronousFirstPacket_OrderStableForFiftyRuns()
    {
        const int rounds = 50;
        var completedRounds = 0;

        for (int i = 0; i < rounds; i++)
        {
            var factory = new ScriptedInputFactory();
            var input1 = new ScriptedAudioInput();
            var firstPacket = Pattern(64, (byte)(0x70 + (i % 8)));
            var input2 = new ScriptedAudioInput
            {
                OnStartRecording = input => input.InjectData(firstPacket, firstPacket.Length)
            };
            factory.EnqueueInput(input1);
            factory.EnqueueInput(input2);

            using var h = new SessionHarness($"rec_order_50_{i}", factory,
                stallThreshold: TimeSpan.FromMilliseconds(40));
            var runTask = Task.Run(() => h.Session.Run());

            var oldMedia = Pattern(96, 0x61);
            Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
            input1.InjectData(oldMedia, oldMedia.Length);

            Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)), $"round {i}");
            h.Session.RequestStop();
            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, exitCode);
            var terminal = h.Terminal;
            Assert.Equal(1, terminal.RecoveryCount);
            Assert.True(terminal.GapFilledBytes > 0);
            Assert.Equal(oldMedia.Length + terminal.GapFilledBytes + firstPacket.Length, terminal.BytesWritten);

            var data = ReadWavData(h.Output);
            Assert.Equal(oldMedia, data.Take(oldMedia.Length).ToArray());
            Assert.All(data.Skip(oldMedia.Length).Take((int)terminal.GapFilledBytes!.Value), b => Assert.Equal(0, b));
            Assert.Equal(firstPacket, data.Skip(oldMedia.Length + (int)terminal.GapFilledBytes.Value).Take(firstPacket.Length).ToArray());
            Assert.Equal(1, input1.DisposeCount);

            h.Session.Dispose();
            Assert.Equal(1, input2.DisposeCount);
            completedRounds++;
        }

        Assert.Equal(rounds, completedRounds);
    }

    [Fact]
    public async Task Run_ProgressAfterRecovery_AllowsCurrentEstimatedGapToDecrease()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput
        {
            OnStartRecording = input => input.InjectData(Pattern(320, 0x24), 320)
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_progress_gap_drop", factory,
            stallThreshold: TimeSpan.FromSeconds(30),
            gapThreshold: TimeSpan.FromMilliseconds(120));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x22), 320);
        await Task.Delay(750);
        input1.InjectData(Pattern(BytesPerMs, 0x23), BytesPerMs);

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)));
        for (int i = 0; i < 12; i++)
        {
            input2.InjectData(Pattern(BytesPerMs * 50, 0x24), BytesPerMs * 50);
            await Task.Delay(50);
        }

        h.SignalStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var progress = h.Events.Where(e => e.Result == AudioHelperEventResult.Progress).ToList();
        var highGap = progress.Last(e => (e.RecoveryCount ?? 0) == 0 && (e.EstimatedGapMs ?? 0) > 0);
        var recoveredProgress = progress.First(e => (e.RecoveryCount ?? 0) == 1);

        Assert.True(recoveredProgress.EstimatedGapMs < highGap.EstimatedGapMs,
            $"Recovered current gap should be allowed to decrease: before={highGap.EstimatedGapMs}, after={recoveredProgress.EstimatedGapMs}");
        Assert.True(h.Terminal.MaxEstimatedGapMs >= highGap.EstimatedGapMs);
        Assert.True(recoveredProgress.BytesWritten >= highGap.BytesWritten);
        Assert.True(recoveredProgress.ElapsedMs >= highGap.ElapsedMs);
        Assert.True(recoveredProgress.WallElapsedMs >= highGap.WallElapsedMs);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_StopDuringRecoveryCandidateStart_StopWinsWithoutReviveOrDuplicateTerminal()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var startEntered = new ManualResetEventSlim(false);
        var releaseStart = new ManualResetEventSlim(false);
        var input2 = new ScriptedAudioInput
        {
            OnStartRecording = _ =>
            {
                startEntered.Set();
                releaseStart.Wait(TimeSpan.FromSeconds(5));
            }
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_stop_during_candidate_start", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x44), 320);

        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(10)));
        h.Session.RequestStop();
        releaseStart.Set();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);
        Assert.DoesNotContain((byte)0x44, ReadWavData(h.Output).Skip(320));

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryCandidateSynchronouslyStopsThenReturnsCancelled_DoesNotDisposeTwiceOrReopen()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput
        {
            StartResult = StartRecordingResult.Cancelled,
            OnStartRecording = input => input.InjectStopped()
        };
        var unexpected = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);
        factory.EnqueueInput(unexpected);

        using var h = new SessionHarness("rec_sync_stop_cancelled", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x81), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, exitCode);
        Assert.Equal(2, factory.CallCount);
        Assert.Equal(0, unexpected.StartCallCount);
        Assert.Equal(1, input2.DisposeCount);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        Assert.False(File.Exists(h.Output));

        h.Session.Dispose();
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryCandidateSynchronouslyErrorsThenReturnsCancelled_PreservesRuntimeDiagnosticsAndSingleDispose()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput
        {
            StartResult = StartRecordingResult.Cancelled,
            OnStartRecording = input => input.InjectError(new AudioCaptureRuntimeException(
                "ReadPacket",
                "ReadPacket failed (COMException, HRESULT=0x88890004): device invalidated",
                new COMException("device invalidated", unchecked((int)0x88890004)),
                unchecked((int)0x88890004)))
        };
        var unexpected = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);
        factory.EnqueueInput(unexpected);

        using var h = new SessionHarness("rec_sync_error_cancelled", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x82), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, exitCode);
        Assert.Equal(2, factory.CallCount);
        Assert.Equal(0, unexpected.StartCallCount);
        Assert.Equal(1, input2.DisposeCount);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Equal("0x88890004", terminal.Hresult);
        Assert.Contains("ReadPacket", terminal.Reason ?? "");
        Assert.Contains("HRESULT=0x88890004", terminal.Reason ?? "");

        h.Session.Dispose();
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_UserStopInsideRecoveryStartThenCancelled_StopWinsAndDoesNotCountRecovery()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var startEntered = new ManualResetEventSlim(false);
        var stopRequested = new ManualResetEventSlim(false);
        var input2 = new ScriptedAudioInput
        {
            StartResult = StartRecordingResult.Cancelled,
            OnStartRecording = input =>
            {
                startEntered.Set();
                Assert.True(stopRequested.Wait(TimeSpan.FromSeconds(5)));
                input.InjectStopped();
            }
        };
        var unexpected = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);
        factory.EnqueueInput(unexpected);

        using var h = new SessionHarness("rec_user_stop_inside_start_cancelled", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x83), 320);

        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(10)));
        var stopTask = Task.Run(() => h.Session.RequestStop());
        Assert.True(SpinWait.SpinUntil(() => input2.Stopped, TimeSpan.FromSeconds(5)));
        stopRequested.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Equal(2, factory.CallCount);
        Assert.Equal(0, unexpected.StartCallCount);
        Assert.Equal(1, input2.DisposeCount);
        var events = h.Events;
        Assert.Single(events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);
        Assert.Equal("degraded", terminal.ContinuityStatus);
        Assert.Equal(0, terminal.RecoveryCount);

        h.Session.Dispose();
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryCandidateStartReturnsDisposedWithoutCallback_ConvergesDiscontinuousWithoutReopen()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var input2 = new ScriptedAudioInput { StartResult = StartRecordingResult.Disposed };
        var unexpected = new ScriptedAudioInput();
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);
        factory.EnqueueInput(unexpected);

        using var h = new SessionHarness("rec_start_disposed_no_callback", factory,
            stallThreshold: TimeSpan.FromMilliseconds(80));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x84), 320);

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, exitCode);
        Assert.Equal(2, factory.CallCount);
        Assert.Equal(0, unexpected.StartCallCount);
        Assert.Equal(1, input2.DisposeCount);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
        Assert.Contains("Disposed", terminal.Reason ?? "");

        h.Session.Dispose();
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoverySynchronousFirstPacketProgress_MetadataIsCommittedAfterSuccessfulStart()
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var firstPacket = Pattern(512, 0x85);
        var input2 = new ScriptedAudioInput
        {
            OnStartRecording = input => input.InjectData(firstPacket, firstPacket.Length)
        };
        factory.EnqueueInput(input1);
        factory.EnqueueInput(input2);

        using var h = new SessionHarness("rec_sync_first_progress_metadata", factory,
            stallThreshold: TimeSpan.FromSeconds(30),
            gapThreshold: TimeSpan.FromMilliseconds(120));
        var runTask = Task.Run(() => h.Session.Run());

        var oldMedia = Pattern(320, 0x86);
        var triggerMedia = Pattern(BytesPerMs, 0x87);
        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(oldMedia, oldMedia.Length);
        await Task.Delay(750);
        input1.InjectData(triggerMedia, triggerMedia.Length);

        Assert.True(SpinWait.SpinUntil(() => input2.Started, TimeSpan.FromSeconds(10)));
        h.Session.RequestStop();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        var terminal = h.Terminal;
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(1, terminal.RecoveryCount);
        Assert.True(terminal.GapFilledBytes > 0);

        var data = ReadWavData(h.Output);
        var oldMediaAll = oldMedia.Concat(triggerMedia).ToArray();
        Assert.Equal(oldMediaAll, data.Take(oldMediaAll.Length).ToArray());
        Assert.All(data.Skip(oldMediaAll.Length).Take((int)terminal.GapFilledBytes!.Value), b => Assert.Equal(0, b));
        Assert.Equal(firstPacket, data.Skip(oldMediaAll.Length + (int)terminal.GapFilledBytes.Value).Take(firstPacket.Length).ToArray());

        var progress = h.Events.Where(e => e.Result == AudioHelperEventResult.Progress).ToList();
        Assert.DoesNotContain(progress, e =>
            (e.GapFilledBytes ?? 0) > 0 &&
            (e.RecoveryCount ?? 0) == 0 &&
            string.Equals(e.ContinuityStatus, "continuous", StringComparison.OrdinalIgnoreCase));
        var recoveredProgress = Assert.Single(progress, e => (e.RecoveryCount ?? 0) == 1);
        Assert.Equal("degraded", recoveredProgress.ContinuityStatus);
        Assert.True(recoveredProgress.GapFilledBytes > 0);
        Assert.True(recoveredProgress.MaxEstimatedGapMs >= recoveredProgress.EstimatedGapMs);

        h.Session.Dispose();
        Assert.Equal(1, input1.DisposeCount);
        Assert.Equal(1, input2.DisposeCount);
    }

    [Fact]
    public async Task Run_RecoveryExternalCleanupCallbacks_DoNotRunUnderStateLock_ForFiftyRuns()
    {
        const int rounds = 50;
        var completedScenarios = 0;

        for (int i = 0; i < rounds; i++)
        {
            await RunExternalCleanupScenario(
                $"rec_lock_success_stop_{i}",
                StartRecordingResult.Started,
                requestStopDuringStart: true);
            completedScenarios++;

            await RunExternalCleanupScenario(
                $"rec_lock_cancelled_{i}",
                StartRecordingResult.Cancelled,
                requestStopDuringStart: false);
            completedScenarios++;

            await RunExternalCleanupScenario(
                $"rec_lock_writer_callback_{i}",
                StartRecordingResult.Started,
                requestStopDuringStart: true,
                injectDataFromStop: true);
            completedScenarios++;
        }

        Assert.Equal(rounds * 3, completedScenarios);
    }

    private static async Task RunExternalCleanupScenario(
        string recordingId,
        StartRecordingResult startResult,
        bool requestStopDuringStart,
        bool injectDataFromStop = false)
    {
        var factory = new ScriptedInputFactory();
        var input1 = new ScriptedAudioInput();
        var startEntered = new ManualResetEventSlim(false);
        var releaseStart = new ManualResetEventSlim(false);
        var callbackTimeouts = 0;
        var candidate = new ScriptedAudioInput
        {
            StartResult = startResult,
            OnStartRecording = input =>
            {
                startEntered.Set();
                releaseStart.Wait(TimeSpan.FromSeconds(5));
                if (injectDataFromStop)
                    input.InjectData(Pattern(64, 0x88), 64);
            },
            OnStopRecording = input =>
            {
                var task = Task.Run(() =>
                {
                    if (injectDataFromStop)
                        input.InjectData(Pattern(64, 0x89), 64);
                    input.InjectStopped();
                });
                if (!task.Wait(TimeSpan.FromSeconds(2)))
                    Interlocked.Increment(ref callbackTimeouts);
            },
            OnDispose = input =>
            {
                var task = Task.Run(() => input.InjectStopped());
                if (!task.Wait(TimeSpan.FromSeconds(2)))
                    Interlocked.Increment(ref callbackTimeouts);
            }
        };

        factory.EnqueueInput(input1);
        factory.EnqueueInput(candidate);

        using var h = new SessionHarness(recordingId, factory,
            stallThreshold: TimeSpan.FromMilliseconds(40));
        var runTask = Task.Run(() => h.Session.Run());

        Assert.True(SpinWait.SpinUntil(() => input1.Started, TimeSpan.FromSeconds(2)));
        input1.InjectData(Pattern(320, 0x90), 320);
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(10)));

        if (requestStopDuringStart)
            h.Session.RequestStop();

        releaseStart.Set();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, callbackTimeouts);
        Assert.Single(h.Events, e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(1, candidate.DisposeCount);

        if (requestStopDuringStart)
        {
            Assert.Equal(0, exitCode);
            Assert.Equal(AudioHelperEventResult.Stopped, h.Terminal.Result);
        }
        else
        {
            Assert.NotEqual(0, exitCode);
            Assert.Equal(AudioHelperEventResult.Fail, h.Terminal.Result);
            Assert.Equal("audio_capture_discontinuous", h.Terminal.ErrorCode);
        }
    }
}
