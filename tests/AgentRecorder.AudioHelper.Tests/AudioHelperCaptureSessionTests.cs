using System.Diagnostics;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class AudioHelperCaptureSessionTests
{
    private class FakeAudioInput : IAudioInput
    {
        public WaveFormat? Format { get; set; } = new WaveFormat(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public void StartRecording()
        {
            Started = true;
        }

        public void StopRecording()
        {
            if (Stopped) return;
            Stopped = true;
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

        public void Dispose()
        {
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

    [Fact]
    public void Run_WithData_EmitsStartedAndOkAndPublishesWav()
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

        var session = new CaptureSession(opts, paths, events, watcher, cts, () => (input, null, null));
        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        var buffer = new byte[320];
        input.InjectData(buffer, buffer.Length);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(stopSignal, "stop");

        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(partial));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
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
            () => (null, "audio_endpoint_not_found", "Endpoint gone"));

        var exitCode = session.Run();

        Assert.NotEqual(0, exitCode);
        Assert.False(File.Exists(output));

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_RecordingStoppedWithException_EmitsFail()
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

        var session = new CaptureSession(opts, paths, events, watcher, cts, () => (input, null, null));
        var runTask = Task.Run(() => session.Run());

        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        var buffer = new byte[320];
        input.InjectData(buffer, buffer.Length);
        input.InjectError(new InvalidOperationException("device lost"));

        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

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
    public void Run_NoDataCaptured_EmitsExactlyOneFail_WithAudioNoPacketsCaptured()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.StopRecording();
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

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
    public void Run_DataAvailableWriteFailure_EmitsFail_AudioWriteFailure()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => input.Stopped, TimeSpan.FromSeconds(2));
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

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
    public void Run_WriterDisposeFailure_EmitsFail_AudioWriterFinalizeFailed()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        File.WriteAllText(stopSignal, "stop");
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_writer_finalize_failed");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_OutputConflict_EmitsFail_AudioOutputConflict()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(output, "conflict");
        File.WriteAllText(stopSignal, "stop");
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

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
    public void Run_PublishMoveFailure_EmitsFail_AudioPublishFailed()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        File.WriteAllText(stopSignal, "stop");
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_publish_failed");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_RecordingStoppedWithException_EmitsFail_AudioCaptureError()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        input.InjectError(new InvalidOperationException("device lost"));
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        Assert.NotEqual(0, exitCode);
        AssertExactlyOneTerminal(events);
        AssertTerminalErrorCode(events, "audio_capture_error");

        session.Dispose();
        watcher.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Run_UserStop_EmitsExactlyOneStopped_ExitZero()
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
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        SpinWait.SpinUntil(() => File.Exists(partial), TimeSpan.FromSeconds(2));
        File.WriteAllText(stopSignal, "stop");
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

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
    public void Run_ConcurrentStopAndError_EmitsExactlyOneTerminal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "rec.wav");
        var partial = Path.Combine(dir, $"rec.{Environment.ProcessId}.partial.wav");
        var stopSignal = Path.Combine(dir, "stop.signal");

        var input = new FakeAudioInput();
        var opts = Options("rec_race", output, stopSignal);
        var paths = PathResult(output, partial);
        var cts = new CancellationTokenSource();
        var watcher = Watcher(stopSignal, cts);
        var sw = new StringWriter();
        var session = new CaptureSession(opts, paths, new EventWriter(sw, null), watcher, cts, () => (input, null, null));

        var runTask = Task.Run(() => session.Run());
        SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(2));
        input.InjectData(new byte[320], 320);
        Parallel.Invoke(
            () => input.StopRecording(),
            () => input.InjectError(new InvalidOperationException("race")));
        var exitCode = runTask.Wait(TimeSpan.FromSeconds(5)) ? runTask.Result : throw new TimeoutException();

        var events = AudioHelperEventStreamParser.ParseEvents(sw.ToString());
        AssertExactlyOneTerminal(events);

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
