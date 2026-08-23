using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class AudioHelperCaptureSessionTests
{
    private class FakeAudioInput : IAudioInput, IAudioPacketPositionSource
    {
        public WaveFormat? Format { get; set; } = new WaveFormat(16000, 16, 1);
        public AudioSourceKind SourceKind { get; set; } = AudioSourceKind.Microphone;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<AudioPacketEventArgs>? PacketPositionAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public int PacketCallbackThreadId { get; private set; }
        public int DisposeThreadId { get; private set; }
        public Exception? StartRecordingException { get; set; }
        public bool RaiseStartExceptionAfterCallback { get; set; }
        public Action<FakeAudioInput>? OnStartRecording { get; set; }
        public long DiscontinuityCount { get; set; }
        public Action<FakeAudioInput>? OnStopRecording { get; set; }
        public Action<FakeAudioInput>? OnDispose { get; set; }

        /// <summary>
        /// When set, RecordingStopped carrying this exception is raised
        /// synchronously inside StartRecording, so the session reaches its
        /// terminal state before Start returns control to the caller.
        /// </summary>
        public Exception? ErrorToRaiseInsideStartRecording { get; set; }

        public StartRecordingResult StartRecording()
        {
            if (StartRecordingException != null && !RaiseStartExceptionAfterCallback)
                throw StartRecordingException;
            Started = true;
            OnStartRecording?.Invoke(this);
            if (StartRecordingException != null)
                throw StartRecordingException;
            if (ErrorToRaiseInsideStartRecording != null)
                RecordingStopped?.Invoke(this, new StoppedEventArgs(ErrorToRaiseInsideStartRecording));
            return StartRecordingResult.Started;
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

        public void InjectPositionedPacket(
            byte[] buffer,
            long devicePosition,
            long packetStartTimestampTicks,
            bool positionValid = true,
            bool dataDiscontinuity = false)
        {
            PacketCallbackThreadId = Environment.CurrentManagedThreadId;
            int blockAlign = Math.Max(1, Format?.BlockAlign ?? 1);
            PacketPositionAvailable?.Invoke(this, new AudioPacketEventArgs(
                buffer,
                buffer.Length,
                buffer.Length / blockAlign,
                devicePosition,
                qpcPosition: 1,
                packetStartTimestampTicks: packetStartTimestampTicks,
                positionValid: positionValid,
                dataDiscontinuity: dataDiscontinuity));
        }

        public void InjectError(Exception ex)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
            OnDispose?.Invoke(this);
            Disposed = true;
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

    private static EventWriter Writer() => new();

    private static StopWatcher Watcher(string path, CancellationTokenSource cts)
        => new(path, () => cts.Cancel());

    private static void ForceProgress(CaptureSession session, long bytesWritten)
    {
        var firstCallback = (long)(typeof(CaptureSession)
            .GetField("_firstCallbackTimestamp", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(session) ?? 0L);
        var method = typeof(CaptureSession).GetMethod(
            "TryEmitProgress", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(session, new object?[] { bytesWritten, firstCallback, true, false });
    }

    [Fact]
    public async Task Run_WithData_EmitsStartedAndOkAndPublishesWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_1", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var events = Writer();

        var session = new CaptureSession(opts, paths, events, watcher, cts, _ => (input, null, null));
        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        var buffer = new byte[320];
        input.InjectData(buffer, buffer.Length);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(stopSignal, "stop");

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(partial));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_SystemLoopbackWithoutPackets_StartsProgressAndPublishesNonEmptyWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, $"loopback.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var input = new FakeAudioInput { SourceKind = AudioSourceKind.SystemLoopback };
        var opts = Options("rec_loopback", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var events = new EventWriter(stdout, null);
        var session = new CaptureSession(opts, paths, events, watcher, cts, _ => (input, null, null));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(partial));
            Assert.True(new FileInfo(output).Length > 44, "A silent loopback session must publish non-empty WAV data");

            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());
            Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
            Assert.Equal("system-loopback", summary.AudioSourceKind);
            Assert.Equal("WASAPI_SHARED_LOOPBACK", summary.CaptureMethod);
            Assert.True(summary.BytesWritten > 0);
            Assert.Contains("RESULT: STARTED", stdout.ToString());
            Assert.Contains("RESULT: STOPPED", stdout.ToString());
            Assert.Contains("AudioSourceKind: system-loopback", stdout.ToString());
            Assert.DoesNotContain("PairEvidence:", stdout.ToString());
            Assert.DoesNotContain("AutoHfpPairStatus:", stdout.ToString());
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopbackAdjacentQpcOverlap_StopsWithoutPositionFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_qpc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var input = new FakeAudioInput { SourceKind = AudioSourceKind.SystemLoopback };
        var opts = Options("rec_loopback_qpc", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts, _ => (input, null, null));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
            var anchor = (long)(typeof(CaptureSession)
                .GetField("_firstSampleAnchorTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) ?? 0L);
            input.InjectPositionedPacket(Enumerable.Repeat((byte)0x12, 200).ToArray(), 0, anchor);
            input.InjectPositionedPacket(Enumerable.Repeat((byte)0x34, 200).ToArray(), 100, anchor + 62_000);
            session.RequestStop();
            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            var events = AudioHelperEventStreamParser.ParseEvents(stdout.ToString());
            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(events, evt => evt.ErrorCode == "audio_loopback_packet_position_invalid");
            Assert.Single(events, evt => evt.Result == AudioHelperEventResult.Stopped);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(partial));
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_IsolatedQpcOutlierRecoversToStoppedDegradedAndCompleteWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_qpc_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var input = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var opts = Options("rec_loopback_qpc_recovery", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts, _ => (input, null, null));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
            var anchor = (long)(typeof(CaptureSession)
                .GetField("_firstSampleAnchorTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) ?? 0L);
            var expectedTwoPackets = AudioPacketPositionMath.FramesToTimestampTicks(960, 48_000, Stopwatch.Frequency);
            var expectedThreePackets = AudioPacketPositionMath.FramesToTimestampTicks(1_440, 48_000, Stopwatch.Frequency);
            var first = Enumerable.Repeat((byte)0x11, 960).ToArray();
            var outlier = Enumerable.Repeat((byte)0x22, 960).ToArray();
            var recovered = Enumerable.Repeat((byte)0x33, 960).ToArray();

            input.InjectPositionedPacket(first, 0, anchor);
            input.InjectPositionedPacket(outlier, 960, anchor + expectedTwoPackets + 1_164_385);
            input.InjectPositionedPacket(recovered, 1_440, anchor + expectedThreePackets);
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());

            Assert.Equal(0, exitCode);
            Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
            Assert.Equal("degraded", summary.ContinuityStatus);
            Assert.Equal(1, summary.QpcOutlierCount);
            Assert.Equal(3_840, summary.BytesWritten);
            Assert.Equal(960, summary.GapFilledBytes);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(partial));
            Assert.True(new FileInfo(output).Length >= 44 + 3_840);
            using var reader = new WaveFileReader(output);
            var pcm = new byte[reader.Length];
            var read = reader.Read(pcm, 0, pcm.Length);
            Assert.Equal(pcm.Length, read);
            Assert.True(pcm.Length >= 3_840);
            pcm = pcm.Take(3_840).ToArray();
            Assert.Equal(first.Concat(new byte[960]).Concat(outlier).Concat(recovered).ToArray(), pcm);
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_ContinuousQpcConflictFailsClosedWithoutFinalWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_qpc_conflict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var input = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var opts = Options("rec_loopback_qpc_conflict", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts, _ => (input, null, null));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
            var anchor = (long)(typeof(CaptureSession)
                .GetField("_firstSampleAnchorTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) ?? 0L);
            var expectedTwoPackets = AudioPacketPositionMath.FramesToTimestampTicks(960, 48_000, Stopwatch.Frequency);
            var expectedThreePackets = AudioPacketPositionMath.FramesToTimestampTicks(1_440, 48_000, Stopwatch.Frequency);

            input.InjectPositionedPacket(new byte[960], 0, anchor);
            input.InjectPositionedPacket(new byte[960], 960, anchor + expectedTwoPackets + 1_164_385);
            input.InjectPositionedPacket(new byte[960], 1_440, anchor + expectedThreePackets + 1_164_385);

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            var events = AudioHelperEventStreamParser.ParseEvents(stdout.ToString());
            var terminal = events.Last(evt => evt.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);

            Assert.NotEqual(0, exitCode);
            Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
            Assert.Equal("audio_capture_discontinuous", terminal.ErrorCode);
            Assert.Contains("qpc_outlier_count=1", terminal.Reason);
            Assert.Contains("last_trusted_qpc_ticks=", terminal.Reason);
            Assert.Contains("last_trusted_device_start=0", terminal.Reason);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(partial));
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_LargeQpcEpochReset_ReopensSameApprovedInputAndRebasesContinuously()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_epoch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var firstInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var recoveredInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var inputs = new Queue<FakeAudioInput>(new[] { firstInput, recoveredInput });
        int openCount = 0;
        var opts = Options("rec_loopback_epoch", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts,
            _ =>
            {
                Interlocked.Increment(ref openCount);
                return (inputs.Dequeue(), null, null);
            });

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => firstInput.Started, TimeSpan.FromSeconds(2)));
            var anchor = (long)(typeof(CaptureSession)
                .GetField("_firstSampleAnchorTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) ?? 0L);
            var packetTicks = AudioPacketPositionMath.FramesToTimestampTicks(480, 48_000, Stopwatch.Frequency);
            const long epochJumpTicks = 15_000_000;

            firstInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x11, 960).ToArray(), 0, anchor);
            var epochPacketThread = new Thread(() => firstInput.InjectPositionedPacket(
                Enumerable.Repeat((byte)0x22, 960).ToArray(), 480, anchor + packetTicks + epochJumpTicks));
            epochPacketThread.Start();
            Assert.True(epochPacketThread.Join(TimeSpan.FromSeconds(1)),
                "The packet callback must return quickly without synchronously disposing its own capture input");

            Assert.True(SpinWait.SpinUntil(() => recoveredInput.Started, TimeSpan.FromSeconds(5)),
                "The large same-endpoint QPC epoch reset must enter the existing bounded recovery path");
            Assert.NotEqual(firstInput.PacketCallbackThreadId, firstInput.DisposeThreadId);
            recoveredInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x44, 960).ToArray(), 0, 100_000_000);
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());
            Assert.Equal(0, exitCode);
            Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
            Assert.Equal(1, summary.RecoveryCount);
            Assert.Equal(1, summary.RecoveryAttempts);
            Assert.Equal(1, summary.QpcOutlierCount);
            Assert.Equal("degraded", summary.ContinuityStatus);
            Assert.Equal(2, openCount);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(partial));

            using var reader = new WaveFileReader(output);
            var pcm = new byte[reader.Length];
            Assert.Equal(pcm.Length, reader.Read(pcm, 0, pcm.Length));
            Assert.Equal(Enumerable.Repeat((byte)0x11, 960).ToArray(), pcm.Take(960).ToArray());
            int newEpochOffset = FindRun(pcm, 0x44, 960, 960);
            Assert.True(newEpochOffset >= 960);
            Assert.All(pcm.Skip(960).Take(newEpochOffset - 960), value => Assert.Equal(0, value));
            Assert.DoesNotContain((byte)0x22, pcm.Take(newEpochOffset).ToArray());
            Assert.DoesNotContain(AudioHelperEventStreamParser.ParseEvents(stdout.ToString()),
                evt => evt.ErrorCode == "audio_helper_failure");
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_CallbackStarvation_RebasesNewGenerationBeforeItsFirstPacket()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_starvation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var firstInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var recoveredInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var inputs = new Queue<FakeAudioInput>(new[] { firstInput, recoveredInput });
        int openCount = 0;
        var opts = Options("rec_loopback_starvation", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts,
            _ =>
            {
                Interlocked.Increment(ref openCount);
                return (inputs.Dequeue(), null, null);
            },
            stallDetectionThreshold: TimeSpan.FromSeconds(30));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => firstInput.Started, TimeSpan.FromSeconds(2)));
            var anchor = GetPrivateLong(session, "_firstSampleAnchorTicks");
            firstInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x11, 960).ToArray(), 0, anchor);

            // Drive CheckStall through its existing private clock state rather
            // than sleeping. This is the callback_starvation trigger, not a
            // synthetic QPC conflict.
            ForceCallbackStarvation(session, 960);

            Assert.True(SpinWait.SpinUntil(() => recoveredInput.Started, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => GetPrivateLong(session, "_successfulRecoveries") >= 1,
                TimeSpan.FromSeconds(5)));
            recoveredInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x44, 960).ToArray(), 0, 100_000_000);
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());
            Assert.Equal(0, exitCode);
            Assert.Equal(1, summary.RecoveryCount);
            Assert.Equal(1, summary.RecoveryAttempts);
            Assert.Equal("degraded", summary.ContinuityStatus);
            Assert.Equal(2, openCount);
            Assert.DoesNotContain(AudioHelperEventStreamParser.ParseEvents(stdout.ToString()),
                evt => evt.ErrorCode == "audio_capture_discontinuous");

            var pcm = ReadPcm(output);
            Assert.Equal(Enumerable.Repeat((byte)0x11, 960).ToArray(), pcm.Take(960).ToArray());
            int newEpochOffset = FindRun(pcm, 0x44, 960, 960);
            Assert.True(newEpochOffset >= 960);
            Assert.All(pcm.Skip(960).Take(newEpochOffset - 960), value => Assert.Equal(0, value));
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_MediaWallGapDivergence_RebasesNewGeneration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_gap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var firstInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var recoveredInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var inputs = new Queue<FakeAudioInput>(new[] { firstInput, recoveredInput });
        int openCount = 0;
        var opts = Options("rec_loopback_gap", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts,
            _ =>
            {
                Interlocked.Increment(ref openCount);
                return (inputs.Dequeue(), null, null);
            },
            stallDetectionThreshold: TimeSpan.FromSeconds(30),
            runtimeGapThreshold: TimeSpan.FromMilliseconds(1));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => firstInput.Started, TimeSpan.FromSeconds(2)));
            var anchor = GetPrivateLong(session, "_firstSampleAnchorTicks");
            firstInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x11, 960).ToArray(), 0, anchor);

            // Two deterministic CheckStall passes satisfy the existing
            // hysteresis for media_wall_gap_divergence without a long sleep.
            ForceMediaWallGapDivergence(session, 960);

            Assert.True(SpinWait.SpinUntil(() => recoveredInput.Started, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => GetPrivateLong(session, "_successfulRecoveries") >= 1,
                TimeSpan.FromSeconds(5)));
            recoveredInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x55, 960).ToArray(), 0, 120_000_000);
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());
            Assert.Equal(0, exitCode);
            Assert.Equal(1, summary.RecoveryCount);
            Assert.Equal(1, summary.RecoveryAttempts);
            Assert.Equal("degraded", summary.ContinuityStatus);
            Assert.True(summary.GapFilledBytes > 0);
            Assert.Equal(2, openCount);

            var pcm = ReadPcm(output);
            Assert.Equal(Enumerable.Repeat((byte)0x11, 960).ToArray(), pcm.Take(960).ToArray());
            int newEpochOffset = FindRun(pcm, 0x55, 960, 960);
            Assert.True(newEpochOffset >= 960);
            Assert.All(pcm.Skip(960).Take(newEpochOffset - 960), value => Assert.Equal(0, value));
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_SystemLoopback_FirstRecoveryCandidateFailsAfterBufferedPacket_SecondCandidateRebasesIndependently()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_loopback_attempts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "loopback.wav");
        var partial = Path.Combine(dir, "loopback.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var firstInput = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var failedCandidate = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1),
            StartRecordingException = new InvalidOperationException("candidate start failed"),
            RaiseStartExceptionAfterCallback = true
        };
        failedCandidate.OnStartRecording = input => input.InjectPositionedPacket(
            Enumerable.Repeat((byte)0x33, 960).ToArray(), 0, 200_000_000);
        var successfulCandidate = new FakeAudioInput
        {
            SourceKind = AudioSourceKind.SystemLoopback,
            Format = new WaveFormat(48_000, 16, 1)
        };
        var inputs = new Queue<FakeAudioInput>(new[] { firstInput, failedCandidate, successfulCandidate });
        int openCount = 0;
        var opts = Options("rec_loopback_attempts", output, stopSignal);
        opts.SourceKind = AudioSourceKind.SystemLoopback;
        var paths = PathResult(output, partial);
        using var cts = new CancellationTokenSource();
        using var watcher = Watcher(stopSignal, cts);
        var stdout = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(stdout, null), watcher, cts,
            _ =>
            {
                Interlocked.Increment(ref openCount);
                return (inputs.Dequeue(), null, null);
            },
            stallDetectionThreshold: TimeSpan.FromSeconds(30));

        try
        {
            var runTask = Task.Run(() => session.Run());
            Assert.True(SpinWait.SpinUntil(() => firstInput.Started, TimeSpan.FromSeconds(2)));
            var anchor = GetPrivateLong(session, "_firstSampleAnchorTicks");
            firstInput.InjectPositionedPacket(Enumerable.Repeat((byte)0x11, 960).ToArray(), 0, anchor);
            ForceCallbackStarvation(session, 960);

            Assert.True(SpinWait.SpinUntil(() => successfulCandidate.Started, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => GetPrivateLong(session, "_successfulRecoveries") >= 1,
                TimeSpan.FromSeconds(5)));
            successfulCandidate.InjectPositionedPacket(Enumerable.Repeat((byte)0x44, 960).ToArray(), 0, 220_000_000);
            session.RequestStop();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
            var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout.ToString());
            Assert.Equal(0, exitCode);
            Assert.Equal(1, summary.RecoveryCount);
            Assert.Equal(2, summary.RecoveryAttempts);
            Assert.Equal("degraded", summary.ContinuityStatus);
            Assert.Equal(3, openCount);
            Assert.True(failedCandidate.Disposed);

            var pcm = ReadPcm(output);
            Assert.Equal(Enumerable.Repeat((byte)0x11, 960).ToArray(), pcm.Take(960).ToArray());
            int newEpochOffset = FindRun(pcm, 0x44, 960, 960);
            Assert.True(newEpochOffset >= 960);
            Assert.All(pcm.Skip(960).Take(newEpochOffset - 960), value => Assert.Equal(0, value));
            Assert.DoesNotContain((byte)0x33, pcm);
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private static int FindRun(byte[] bytes, byte value, int length, int start)
    {
        for (int offset = start; offset + length <= bytes.Length; offset++)
        {
            if (bytes.AsSpan(offset, length).IndexOfAnyExcept(value) < 0)
                return offset;
        }

        return -1;
    }

    private static byte[] ReadPcm(string path)
    {
        using var reader = new WaveFileReader(path);
        var pcm = new byte[reader.Length];
        Assert.Equal(pcm.Length, reader.Read(pcm, 0, pcm.Length));
        return pcm;
    }

    private static void SetPrivateLong(CaptureSession session, string fieldName, long value)
    {
        var field = typeof(CaptureSession).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        object converted = field!.FieldType == typeof(int)
            ? (object)checked((int)value)
            : value;
        field.SetValue(session, converted);
    }

    private static long GetPrivateLong(CaptureSession session, string fieldName)
    {
        var field = typeof(CaptureSession).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Convert.ToInt64(field!.GetValue(session));
    }

    private static void InvokeCheckStall(CaptureSession session)
    {
        var method = typeof(CaptureSession).GetMethod("CheckStall", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(session, null);
    }

    private static void ForceCallbackStarvation(CaptureSession session, long bytesWritten)
    {
        var old = Stopwatch.GetTimestamp() - (60 * Stopwatch.Frequency);
        SetPrivateLong(session, "_lastCallbackTimestamp", old);
        SetPrivateLong(session, "_lastStreamResumeTimestamp", old);
        SetPrivateLong(session, "_stallCheckLastBytes", bytesWritten);
        InvokeCheckStall(session);
    }

    private static void ForceMediaWallGapDivergence(CaptureSession session, long bytesWritten)
    {
        var old = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var now = Stopwatch.GetTimestamp();
        SetPrivateLong(session, "_firstCallbackTimestamp", old);
        SetPrivateLong(session, "_lastCallbackTimestamp", now);
        SetPrivateLong(session, "_lastStreamResumeTimestamp", now);
        SetPrivateLong(session, "_stallCheckLastBytes", -1);
        SetPrivateLong(session, "_gapOverThresholdChecks", 0);
        SetPrivateLong(session, "_bytesWritten", bytesWritten);
        InvokeCheckStall(session);
        InvokeCheckStall(session);
    }

    [Fact]
    public void Run_InputOpenFailure_EmitsFail()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var opts = Options("rec_2", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var events = Writer();

        var session = new CaptureSession(opts, paths, events, watcher, cts,
            _ => (null, "audio_endpoint_not_found", "Endpoint gone"));

        var exitCode = session.Run();

        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(output));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_RecordingStoppedWithException_EmitsFail()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_3", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var events = Writer();

        var session = new CaptureSession(opts, paths, events, watcher, cts, _ => (input, null, null));
        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        var buffer = new byte[320];
        input.InjectData(buffer, buffer.Length);
        input.InjectError(new InvalidOperationException("device lost"));

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, exitCode);

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    private static void AssertExactlyOneTerminal(List<AudioHelperEvent> events)
    {
        var terminals = events.Where(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail).ToList();
        Assert.Single(terminals);
    }

    private static void AssertTerminalErrorCode(List<AudioHelperEvent> events, string expectedCode)
    {
        var terminal = events.LastOrDefault(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.NotNull(terminal);
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal(expectedCode, terminal.ErrorCode);
    }

    [Fact]
    public async Task Run_NoDataCaptured_EmitsExactlyOneFail_WithAudioNoPacketsCaptured()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_no_data", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.StopRecording();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(partial));
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_no_packets_captured");

        session.Dispose();
        watcher.Dispose();
        Assert.True(input.Disposed, "Input should be disposed after session disposal");
        Assert.False(File.Exists(partial), "Partial file should be cleaned up after session disposal");
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_DataAvailableWriteFailure_EmitsFail_AudioWriteFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_write_fail", output, stopSignal);
        var paths = new PathCheckResult
        {
            Ok = true,
            CanonicalPath = output,
            PartialPath = partial,
            OpenPartialStream = () => new ThrowOnWriteStream()
        };
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => input.Stopped, TimeSpan.FromSeconds(2));
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_write_failure");

        session.Dispose();
        watcher.Dispose();
        Assert.True(input.Disposed, "Input should be disposed after session disposal");
        Assert.False(File.Exists(partial), "Partial file should be cleaned up on write failure");
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_WriterDisposeFailure_EmitsFail_AudioWriterFinalizeFailed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_dispose_fail", output, stopSignal);
        var paths = new PathCheckResult
        {
            Ok = true,
            CanonicalPath = output,
            PartialPath = partial,
            OpenPartialStream = () => new ThrowOnDisposeStream()
        };
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_writer_finalize_failed");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_OutputConflict_EmitsFail_AudioOutputConflict()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_conflict", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(output, "conflict");
        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_output_conflict");
        Assert.False(File.Exists(partial), "Partial file should be cleaned up on conflict");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_PublishMoveFailure_EmitsFail_AudioPublishFailed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "missing", "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_publish_fail", output, stopSignal);
        var paths = new PathCheckResult
        {
            Ok = true,
            CanonicalPath = output,
            PartialPath = partial,
            OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        };
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_publish_failed");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_RecordingStoppedWithException_EmitsFail_AudioCaptureError()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_capture_error", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        input.InjectError(new InvalidOperationException("device lost"));
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_capture_error");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_UserStop_EmitsExactlyOneStopped_ExitZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_user_stop", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.Equal(0, exitCode);
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(partial));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_ProgressDiscontinuityCount_IsPreservedInStoppedTerminal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput { DiscontinuityCount = 46 };
        var opts = Options("rec_discontinuity_terminal", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
        input.InjectData(new byte[320], 320);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2)));
        ForceProgress(session, 320);

        File.WriteAllText(stopSignal, "stop");
        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        var progress = events.Where(e => e.Result == AudioHelperEventResult.Progress).ToList();
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.NotEmpty(progress);
        Assert.True(progress.Zip(progress.Skip(1), (a, b) => b.DiscontinuityCount >= a.DiscontinuityCount).All(v => v));
        Assert.Equal(46, progress.Max(e => e.DiscontinuityCount));
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal(46, terminal.DiscontinuityCount);
        Assert.True(terminal.DiscontinuityCount >= progress.Max(e => e.DiscontinuityCount));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_NaturalOkAndUserStopped_PreserveCurrentInputDiscontinuityCount()
    {
        async Task<AudioHelperEvent> RunCaseAsync(string recordingId, long count, bool userStop)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "rec.wav");
            var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
            var stopSignal = Path.Combine(dir, "stop.signal");
            var input = new FakeAudioInput { DiscontinuityCount = count };
            var opts = Options(recordingId, output, stopSignal);
            var paths = PathResult(output, partial);
            var cts = new CancellationTokenSource();
            var watcher = Watcher(stopSignal, cts);
            var sw = new StringWriter();
            var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

            try
            {
                var runTask = Task.Run(() => session.Run());
                Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
                input.InjectData(new byte[320], 320);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2)));
                if (userStop)
                    File.WriteAllText(stopSignal, "stop");
                else
                    input.StopRecording();
                Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
                return AudioHelperEventStreamParser.ParseEvents(sw.ToString())
                    .Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
            }
            finally
            {
                session.Dispose();
                watcher.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        var natural = await RunCaseAsync("rec_natural_ok_count", 17, userStop: false);
        var stopped = await RunCaseAsync("rec_user_stopped_count", 23, userStop: true);

        Assert.Equal(AudioHelperEventResult.Ok, natural.Result);
        Assert.Equal(17, natural.DiscontinuityCount);
        Assert.Equal(AudioHelperEventResult.Stopped, stopped.Result);
        Assert.Equal(23, stopped.DiscontinuityCount);
    }

    [Fact]
    public async Task Run_FailTerminal_PreservesCurrentInputDiscontinuityCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");
        var input = new FakeAudioInput { DiscontinuityCount = 13 };
        var opts = Options("rec_fail_count", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2)));
        input.InjectData(new byte[320], 320);
        input.InjectError(new InvalidOperationException("device lost"));
        Assert.NotEqual(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal(13, terminal.DiscontinuityCount);

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_ConcurrentStopAndError_EmitsExactlyOneTerminal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput { DiscontinuityCount = 19 };
        var opts = Options("rec_race", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        Parallel.Invoke(
            () => input.StopRecording(),
            () => input.InjectError(new InvalidOperationException("race")));
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(19, terminal.DiscontinuityCount);

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_StartRecordingThrowsAudioCaptureStartException_EmitsFail_AudioCaptureStartFailed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput
        {
            DiscontinuityCount = 23,
            StartRecordingException = new AudioCaptureStartException(
                "StartRecording failed (COMException, HRESULT=0x80070057): Value does not fall within the expected range.",
                new InvalidOperationException("Value does not fall within the expected range."),
                unchecked((int)0x80070057))
        };
        var opts = Options("rec_start_fail", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var exitCode = await Task.Run(() => session.Run()).WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(partial), "Partial file should be cleaned up when StartRecording fails");
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_capture_start_failed");
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Contains("0x80070057", terminal.Hresult ?? "");
        Assert.Equal(23, terminal.DiscontinuityCount);

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_StartRecordingFailsThenSucceeds_RetriesAndPublishesWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        int factoryCall = 0;
        FakeAudioInput? firstInput = null;
        FakeAudioInput? successInput = null;

        var opts = Options("rec_start_retry", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            factoryCall++;
            if (factoryCall == 1)
            {
                firstInput = new FakeAudioInput
                {
                    StartRecordingException = new AudioCaptureStartException(
                        "StartRecording failed",
                        new InvalidOperationException("E_INVALIDARG"),
                        unchecked((int)0x80070057))
                };
                return (firstInput, null, null);
            }
            successInput = new FakeAudioInput();
            return (successInput, null, null);
        });

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => factoryCall >= 2 && successInput?.Started == true, TimeSpan.FromSeconds(2));
        successInput?.InjectData(new byte[320], 320);
        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output), "Output WAV must be published after retry succeeds");
        Assert.False(File.Exists(partial), "Partial file must be cleaned up");
        Assert.True(firstInput?.Disposed ?? false, "Failed first input must be disposed");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_StallAfterStarted_RecoveryExhausted_EmitsFail_AudioCaptureDiscontinuous()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        // Every factory call returns a fresh input that never delivers data, so
        // the bounded recovery budget (2) is exhausted and the session must fail
        // with the stable discontinuous code instead of waiting for final mux.
        var createdInputs = new List<FakeAudioInput>();
        var opts = Options("rec_stall", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            var input = new FakeAudioInput();
            createdInputs.Add(input);
            return (input, null, null);
        }, TimeSpan.FromMilliseconds(100));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => createdInputs.Count >= 1 && createdInputs[0].Started, TimeSpan.FromSeconds(2));
        createdInputs[0].InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));

        // After the first sample, no more data arrives. The starvation trigger
        // starts bounded recovery; with every replacement also starving, the
        // budget is exhausted and the terminal failure is discontinuous.
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(partial), "Partial file should be cleaned up on unrecoverable starvation");
        Assert.True(createdInputs.Count >= 2, "Recovery must have reopened the endpoint at least once");
        Assert.True(createdInputs[0].Stopped, "Starved input should be stopped when recovery starts");
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_capture_discontinuous");
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Contains("callback_starvation", terminal.Reason ?? "");

        session.Dispose();
        watcher.Dispose();
        Assert.All(createdInputs, i => Assert.True(i.Disposed));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_ContinuousSilence_DoesNotStall()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_silence", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null), TimeSpan.FromMilliseconds(100));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));

        // Continuously feed zero/silence samples. WASAPI still delivers packets
        // during silence, so bytes must keep growing and the stall monitor must
        // not fire.
        var silence = new byte[320];
        var stopAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(350);
        while (DateTime.UtcNow < stopAt && !input.Stopped)
        {
            input.InjectData(silence, silence.Length);
            await Task.Delay(20);
        }

        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.Equal(0, exitCode);
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_BriefPauseThenResume_DoesNotStall_LongPauseFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var createdInputs = new List<FakeAudioInput>();
        var opts = Options("rec_pause", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            var input = new FakeAudioInput();
            createdInputs.Add(input);
            return (input, null, null);
        }, TimeSpan.FromMilliseconds(100));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => createdInputs.Count >= 1 && createdInputs[0].Started, TimeSpan.FromSeconds(2));
        var firstInput = createdInputs[0];
        var silence = new byte[320];

        // Brief pause (below threshold) then resume: must not be classified as stall.
        firstInput.InjectData(silence, silence.Length);
        await Task.Delay(60);
        firstInput.InjectData(silence, silence.Length);
        await Task.Delay(60);
        firstInput.InjectData(silence, silence.Length);

        // Continue feeding data until the monitor has had time to run several
        // checks after the brief pause.
        var stopAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < stopAt && !firstInput.Stopped)
        {
            firstInput.InjectData(silence, silence.Length);
            await Task.Delay(20);
        }

        Assert.False(firstInput.Stopped, "Brief pause below threshold must not trigger recovery");

        // Now stop feeding data long enough to exceed the threshold. Recovery
        // starts; the replacement inputs also receive no data, so the bounded
        // budget is exhausted and the session fails as discontinuous.
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(15));
        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.True(firstInput.Stopped, "Long pause exceeding threshold must stop the starved input for recovery");
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_capture_discontinuous");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void BuildEventInfo_ComputesDurationFromBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, "rec.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var opts = Options("rec_5", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var session = new CaptureSession(opts, paths, Writer(), Watcher(stopSignal, cts), cts, null);

        var method = typeof(CaptureSession).GetMethod("BuildEventInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var format = new WaveFormat(16000, 16, 1);
        typeof(CaptureSession).GetField("_waveFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(session, format);
        var buffer = new byte[16000 * 2];
        var now = Stopwatch.GetTimestamp();
        var info = (AudioHelperEventInfo?)method?.Invoke(session, new object?[] { buffer.Length, now, now });

        Assert.NotNull(info);
        Assert.Equal(1000, info.ElapsedMs);
        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.Channels);

        session.Dispose();
        Directory.Delete(dir, true);
    }

    // -----------------------------------------------------------------
    // Production-path tests using real AudioClientAudioInput
    // -----------------------------------------------------------------

    private sealed class CountedDevice : IDevice
    {
        public DeviceState State { get; set; } = DeviceState.Active;
        public DataFlow DataFlow { get; set; } = DataFlow.Capture;
        public Func<IAudioClient>? CreateAudioClientCallback { get; set; }
        private int _disposeCount;
        public int DisposeCount => _disposeCount;

        public IAudioClient CreateAudioClient()
            => CreateAudioClientCallback?.Invoke() ?? throw new InvalidOperationException("No audio client");

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class CountedAudioClient : IAudioClient
    {
        public WaveFormat MixFormat { get; set; } = new WaveFormat(16000, 16, 1);
        public int BufferSize { get; set; } = 1600;
        public Exception? StartException { get; set; }
        public Func<IAudioCaptureClient>? CaptureClientFactory { get; set; }
        public Action? StartAction { get; set; }
        private int _disposeCount;
        public int DisposeCount => _disposeCount;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration, long periodicity, WaveFormat format, Guid audioSessionGuid) { }

        public void Start()
        {
            StartAction?.Invoke();
            if (StartException != null)
                throw StartException;
            Started = true;
        }

        public void Stop() => Stopped = true;

        public IAudioCaptureClient GetAudioCaptureClient()
            => CaptureClientFactory?.Invoke() ?? throw new InvalidOperationException("No capture client");

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class CountedAudioCaptureClient : IAudioCaptureClient
    {
        public Queue<(IntPtr Buffer, int Frames, AudioClientBufferFlags Flags)> Packets { get; } = new();
        public Exception? ReleaseBufferException { get; set; }
        private int _releaseBufferCallCount;
        public int ReleaseBufferCallCount => Volatile.Read(ref _releaseBufferCallCount);
        private int _disposeCount;
        public int DisposeCount => _disposeCount;

        public int GetNextPacketSize() => Packets.Count;

        public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags)
        {
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

        public void ReleaseBuffer(int framesRead)
        {
            Interlocked.Increment(ref _releaseBufferCallCount);
            if (ReleaseBufferException != null)
                throw ReleaseBufferException;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private static (CountedDevice Device, CountedAudioClient Client, CountedAudioCaptureClient Capture, AudioClientAudioInput Input)
        CreateRealInput(Exception? startException = null)
    {
        var device = new CountedDevice();
        var capture = new CountedAudioCaptureClient();
        var client = new CountedAudioClient
        {
            StartException = startException,
            CaptureClientFactory = () => capture
        };
        device.CreateAudioClientCallback = () => client;
        var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100);
        return (device, client, capture, input);
    }

    [Fact]
    public async Task Run_FormalStartFailsThenSucceeds_UsingRealAudioClientAudioInput_RetriesAndPublishesWav()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var first = CreateRealInput(new COMException("E_INVALIDARG", unchecked((int)0x80070057)));
        CountedAudioCaptureClient? secondCapture = null;
        GCHandle pinned = default;

        int factoryCall = 0;
        var opts = Options("prod_start_retry", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            factoryCall++;
            if (factoryCall == 1)
                return (first.Input, null, null);

            var second = CreateRealInput();
            secondCapture = second.Capture;
            var buffer = new byte[320];
            pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            second.Capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 160, AudioClientBufferFlags.None));
            second.Capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));
            return (second.Input, null, null);
        });

        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() =>
        {
            var evts = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
            return evts.Any(e => e.Result == AudioHelperEventResult.Started);
        }, TimeSpan.FromSeconds(3));
        File.WriteAllText(stopSignal, "stop");

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        session.Dispose();
        watcher.Dispose();

        try
        {
            var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(output), "Output WAV must be published after formal Start retry succeeds");
            Assert.False(File.Exists(partial), "Partial file must be cleaned up");
            Assert.Equal(2, factoryCall);

            Assert.Equal(1, first.Device.DisposeCount);
            Assert.Equal(1, first.Client.DisposeCount);
            Assert.Equal(1, first.Capture.DisposeCount);

            Assert.NotNull(secondCapture);
            Assert.Equal(1, secondCapture.DisposeCount);

            Assert.Single(events, e => e.Result == AudioHelperEventResult.Started);
            Assert.Single(events, e => e.Result == AudioHelperEventResult.Stopped);
            Assert.DoesNotContain(events, e => e.Result == AudioHelperEventResult.Fail);
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Run_FormalStartFailsTwice_UsingRealAudioClientAudioInput_EmitsSingleAudioCaptureStartFailed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var first = CreateRealInput(new COMException("E_INVALIDARG", unchecked((int)0x80070057)));
        var second = CreateRealInput(new COMException("E_INVALIDARG", unchecked((int)0x80070057)));

        int factoryCall = 0;
        var opts = Options("prod_start_fail2", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            factoryCall++;
            return factoryCall == 1 ? (first.Input, null, null) : (second.Input, null, null);
        });

        var exitCode = await Task.Run(() => session.Run()).WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(partial));
        Assert.Equal(2, factoryCall);

        Assert.Equal(1, first.Device.DisposeCount);
        Assert.Equal(1, first.Client.DisposeCount);
        Assert.Equal(1, first.Capture.DisposeCount);
        Assert.Equal(1, second.Device.DisposeCount);
        Assert.Equal(1, second.Client.DisposeCount);
        Assert.Equal(1, second.Capture.DisposeCount);

        Assert.DoesNotContain(events, e => e.Result == AudioHelperEventResult.Started);
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Fail);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal("audio_capture_start_failed", terminal.ErrorCode);
        Assert.Contains("0x80070057", terminal.Hresult ?? "");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_SecondOpenReturnsMoreSpecificError_PrefersEndpointErrorOverStartFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var first = CreateRealInput(new COMException("E_INVALIDARG", unchecked((int)0x80070057)));

        int factoryCall = 0;
        var opts = Options("prod_specific_error", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            factoryCall++;
            if (factoryCall == 1)
                return (first.Input, null, null);
            return (null, "audio_endpoint_not_found", "Endpoint removed during retry");
        });

        var exitCode = session.Run();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.Equal(2, factoryCall);

        Assert.Single(events, e => e.Result == AudioHelperEventResult.Fail);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal("audio_endpoint_not_found", terminal.ErrorCode);
        Assert.Contains("Endpoint removed", terminal.Reason ?? "");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_RuntimeErrorWithHresult_StructuredHresultPropagated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_runtime_hresult", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        input.InjectError(new AudioCaptureRuntimeException(
            "GetBuffer",
            "GetBuffer failed (COMException, HRESULT=0x80070490): device lost",
            new COMException("device lost", unchecked((int)0x80070490)),
            unchecked((int)0x80070490)));

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal("audio_capture_error", terminal.ErrorCode);
        Assert.Contains("0x80070490", terminal.Hresult ?? "");
        Assert.Contains("GetBuffer", terminal.Reason ?? "");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_FirstPacketArrivesSynchronously_NoTimerLeak()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var real = CreateRealInput();
        var buffer = new byte[320];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        real.Capture.Packets.Enqueue((pinned.AddrOfPinnedObject(), 160, AudioClientBufferFlags.None));
        real.Capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        var opts = Options("prod_first_packet", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (real.Input, null, null),
            firstPacketTimeout: TimeSpan.FromSeconds(30));

        var runTask = Task.Run(() => session.Run());
        var events = new List<AudioHelperEvent>();
        SpinWait.SpinUntil(() =>
        {
            events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
            return events.Any(e => e.Result == AudioHelperEventResult.Started);
        }, TimeSpan.FromSeconds(3));

        // Give the timer a moment to be armed/disarmed, then assert no orphan timer.
        await Task.Delay(50);
        var timerField = typeof(CaptureSession).GetField("_firstPacketTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(timerField?.GetValue(session));

        File.WriteAllText(stopSignal, "stop");
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        session.Dispose();
        watcher.Dispose();

        events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.Equal(0, exitCode);
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Started);
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Stopped);

        pinned.Free();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_RuntimeErrorBeforeStartReturns_DoesNotStartTimersOrWatcher()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        // The error is raised synchronously inside StartRecording, so the
        // terminal FAIL converges before Start returns control to RunCore.
        // (With a real AudioClientAudioInput the capture thread reports the
        // error asynchronously, so "terminal before Start returns" cannot be
        // forced deterministically through the real input.)
        var input = new FakeAudioInput
        {
            ErrorToRaiseInsideStartRecording = new AudioCaptureRuntimeException(
                "GetNextPacketSize",
                "GetNextPacketSize failed (COMException, HRESULT=0x80070490): device lost",
                new COMException("device lost", unchecked((int)0x80070490)),
                unchecked((int)0x80070490))
        };

        var opts = Options("prod_early_terminal", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal("audio_capture_error", terminal.ErrorCode);
        Assert.Contains("GetNextPacketSize", terminal.Reason ?? "");
        Assert.Contains("0x80070490", terminal.Hresult ?? "");

        var firstPacketTimer = typeof(CaptureSession).GetField("_firstPacketTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(session);
        var stallTimer = typeof(CaptureSession).GetField("_stallTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(session);
        Assert.Null(firstPacketTimer);
        Assert.Null(stallTimer);
        // Triggered only reports whether a stop file was observed; it cannot
        // prove the watcher never started. StartCount is the stable contract:
        // 0 means the polling thread was never launched.
        Assert.Equal(0, watcher.StartCount);
        Assert.False(watcher.Started, "StopWatcher must not be started when terminal was reached before Start returned");
        Assert.False(watcher.Triggered);

        session.Dispose();
        watcher.Dispose();
        Assert.True(input.Disposed, "Input should be disposed after session disposal");
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_WithData_StartsStopWatcherExactlyOnce_AndLeavesNoPollingThread()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_watcher_started", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var events = Writer();

        var session = new CaptureSession(opts, paths, events, watcher, cts, _ => (input, null, null));
        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        var buffer = new byte[320];
        input.InjectData(buffer, buffer.Length);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(stopSignal, "stop");

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);

        // Normal path: the watcher is started exactly once, and it actually
        // observed the stop file (proves the seam is not permanently false).
        Assert.Equal(1, watcher.StartCount);
        Assert.True(watcher.Started);
        Assert.True(watcher.Triggered, "Watcher must observe the stop file on the normal stop path");

        // After Dispose the polling loop must be gone: no background thread
        // left behind.
        watcher.Dispose();
        Assert.True(watcher.PollingExited, "Polling thread must have exited after Dispose");

        session.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Run_ReadPacketAndReleaseBufferBothFail_EmitsSingleFailWithPrimaryAndSecondaryDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var real = CreateRealInput();
        // A non-silent packet with a null buffer pointer makes ReadPacket fail
        // with E_POINTER; the subsequent ReleaseBuffer call also fails. The
        // ReadPacket error must remain the primary root cause.
        real.Capture.ReleaseBufferException = new COMException("release boom", unchecked((int)0x80004005));
        real.Capture.Packets.Enqueue((IntPtr.Zero, 160, AudioClientBufferFlags.None));
        real.Capture.Packets.Enqueue((IntPtr.Zero, 0, AudioClientBufferFlags.None));

        var opts = Options("prod_dual_failure", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (real.Input, null, null));

        var exitCode = await Task.Run(() => session.Run()).WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(AudioHelperEventResult.Fail, terminal.Result);
        Assert.Equal("audio_capture_error", terminal.ErrorCode);
        // Terminal HRESULT stays the primary (ReadPacket) HRESULT.
        Assert.Contains("0x80004003", terminal.Hresult ?? "");
        // Terminal reason surfaces both the primary stage and the secondary
        // ReleaseBuffer diagnostics, including the secondary HRESULT.
        Assert.Contains("ReadPacket", terminal.Reason ?? "");
        Assert.Contains("ReleaseBuffer", terminal.Reason ?? "");
        Assert.Contains("0x80004005", terminal.Reason ?? "");

        // GetBuffer succeeded once, so ReleaseBuffer was attempted exactly once.
        Assert.Equal(1, real.Capture.ReleaseBufferCallCount);

        session.Dispose();
        watcher.Dispose();

        Assert.Equal(1, real.Device.DisposeCount);
        Assert.Equal(1, real.Client.DisposeCount);
        Assert.Equal(1, real.Capture.DisposeCount);

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// Delivers a fixed number of silent packets (Silent flag + null data
    /// pointer) at a paced interval so the capture thread spans several
    /// stall-check intervals instead of draining everything in one burst.
    /// </summary>
    private sealed class PacedSilentCaptureClient : IAudioCaptureClient
    {
        private readonly int _totalPackets;
        private readonly int _framesPerPacket;
        private readonly TimeSpan _pacingDelay;
        private int _delivered;
        private int _releaseBufferCallCount;
        private int _disposeCount;

        public PacedSilentCaptureClient(int totalPackets, int framesPerPacket, TimeSpan pacingDelay)
        {
            _totalPackets = totalPackets;
            _framesPerPacket = framesPerPacket;
            _pacingDelay = pacingDelay;
        }

        public int DeliveredCount => Volatile.Read(ref _delivered);
        public int ReleaseBufferCallCount => Volatile.Read(ref _releaseBufferCallCount);
        public int DisposeCount => _disposeCount;
        public Action? LastPacketReleased { get; set; }

        public int GetNextPacketSize()
        {
            if (DeliveredCount >= _totalPackets)
                return 0;
            // Pace delivery: each packet becomes available one pacing delay
            // after the previous one, like a real device filling its buffer.
            Thread.Sleep(_pacingDelay);
            return 1;
        }

        public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags)
        {
            framesAvailable = _framesPerPacket;
            flags = AudioClientBufferFlags.Silent;
            return IntPtr.Zero; // Silent packets carry no data pointer.
        }

        public void ReleaseBuffer(int framesRead)
        {
            Interlocked.Increment(ref _releaseBufferCallCount);
            var delivered = Interlocked.Increment(ref _delivered);
            if (delivered == _totalPackets)
                LastPacketReleased?.Invoke();
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    [Fact]
    public async Task Run_ConsecutiveSilentPackets_ThroughSession_DoesNotStallAndWritesExactBytes()
    {
        const int packetCount = 30;
        const int framesPerPacket = 160;
        const int bytesPerFrame = 2; // 16-bit mono
        const int expectedBytes = packetCount * framesPerPacket * bytesPerFrame;

        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var device = new CountedDevice();
        var client = new CountedAudioClient();
        // 30 packets paced 20ms apart: the flow spans ~600ms, covering several
        // stall-check intervals (threshold 300ms -> check every 150ms) while
        // bytes keep growing.
        var capture = new PacedSilentCaptureClient(packetCount, framesPerPacket, TimeSpan.FromMilliseconds(20));
        device.CreateAudioClientCallback = () => client;
        client.CaptureClientFactory = () => capture;
        var input = new AudioClientAudioInput(device, client, capture, new WaveFormat(16000, 16, 1), 100);

        var opts = Options("prod_silent_stall", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ => (input, null, null),
            stallDetectionThreshold: TimeSpan.FromMilliseconds(300));

        // Stop deterministically the moment the last silent packet has been
        // released: the last callback is fresh, far inside the 300ms stall
        // threshold, so no stall can fire on the shutdown path.
        capture.LastPacketReleased = () => File.WriteAllText(stopSignal, "stop");

        var exitCode = await Task.Run(() => session.Run()).WaitAsync(TimeSpan.FromSeconds(15));

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.Equal(0, exitCode);
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Started);
        Assert.DoesNotContain(events, e => e.Result == AudioHelperEventResult.Fail);
        Assert.DoesNotContain(events, e => e.ErrorCode == "audio_capture_stalled");
        Assert.DoesNotContain(events, e => e.ErrorCode == "audio_capture_discontinuous");
        AssertExactlyOneTerminal(events);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(AudioHelperEventResult.Stopped, terminal.Result);
        Assert.Equal("user_requested", terminal.StopReason);
        Assert.Equal(expectedBytes, terminal.BytesWritten);
        // Normal Silent packets must never trigger recovery or gap padding:
        // the timeline is preserved by the real zero samples themselves.
        Assert.Equal(0, terminal.RecoveryCount);
        Assert.Equal(0, terminal.GapFilledBytes);
        Assert.Equal("continuous", terminal.ContinuityStatus);

        // The published WAV data chunk must equal silent frames x block align.
        Assert.True(File.Exists(output));
        long wavDataBytes;
        using (var reader = new WaveFileReader(output))
        {
            wavDataBytes = reader.Length;
        }
        Assert.Equal(expectedBytes, wavDataBytes);

        Assert.Equal(packetCount, capture.ReleaseBufferCallCount);

        session.Dispose();
        watcher.Dispose();

        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, capture.DisposeCount);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_StartupBudgetExhausted_StopsAttemptsAndEmitsBudgetExceeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var clock = new FakeClock();
        var first = CreateRealInput(new COMException("E_INVALIDARG", unchecked((int)0x80070057)));

        int factoryCall = 0;
        var opts = Options("prod_budget", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, _ =>
        {
            factoryCall++;
            clock.Advance(CaptureSession.TotalStartupBudget + TimeSpan.FromSeconds(1));
            return (first.Input, null, null);
        }, clock: clock);

        var exitCode = session.Run();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        Assert.Equal(1, factoryCall);
        Assert.Single(events, e => e.Result == AudioHelperEventResult.Fail);
        var terminal = events.Last(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal("audio_capture_start_failed", terminal.ErrorCode);
        Assert.Contains("0x80070057", terminal.Hresult ?? "");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    private sealed class FakeClock : ISystemClock
    {
        private readonly FakeStopwatch _stopwatch = new();

        public IStopwatch StartStopwatch() => _stopwatch;

        public void Sleep(TimeSpan delay) => _stopwatch.Elapsed += delay;

        public void Advance(TimeSpan amount) => _stopwatch.Elapsed += amount;

        private sealed class FakeStopwatch : IStopwatch
        {
            public TimeSpan Elapsed { get; set; }
        }
    }

    /// <summary>
    /// A writable stream that allows the WAV header plus a small margin to be
    /// written and then throws on every subsequent write. This forces the
    /// failure to surface inside OnDataAvailable rather than during
    /// finalization. The margin covers BinaryWriter buffering during header
    /// construction; the first real data packet is still larger than the
    /// allowed budget and will fail.
    /// </summary>
    private sealed class ThrowOnWriteStream : MemoryStream
    {
        private long _bytesWritten;
        // Standard PCM WAV header is 44 bytes; allow a margin for any
        // intermediate BinaryWriter flushing, but keep well below a single
        // 320-byte data packet so the data write still fails.
        private const int AllowedBytes = 128;

        public override void Write(byte[] buffer, int offset, int count)
        {
            _bytesWritten += count;
            if (_bytesWritten > AllowedBytes)
                throw new IOException("Simulated write failure");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _bytesWritten += buffer.Length;
            if (_bytesWritten > AllowedBytes)
                throw new IOException("Simulated write failure");
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            _bytesWritten++;
            if (_bytesWritten > AllowedBytes)
                throw new IOException("Simulated write failure");
            base.WriteByte(value);
        }
    }

    private sealed class ThrowOnDisposeStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("Simulated dispose failure");
        }
    }
}
