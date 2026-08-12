using System.Diagnostics;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class LoopbackTerminalRaceTests
{
    [Fact]
    public async Task InFlightTickThenUserStop_FinalPadBeforeTerminalAndNoPostTerminalProgress()
    {
        using var fixture = new Fixture();
        using var session = fixture.CreateSession();
        var runTask = Task.Run(() => session.Run());

        Assert.True(fixture.Input.Started.Wait(TimeSpan.FromSeconds(2)));
        fixture.Stdout.ArmNextProgressWrite();
        Assert.True(fixture.Stdout.ProgressWriteEntered.Wait(TimeSpan.FromSeconds(3)));

        var stopTask = Task.Run(session.RequestStop);
        await Task.WhenAny(stopTask, Task.Delay(250));
        Assert.False(stopTask.IsCompleted,
            "user stop should be waiting for the in-flight tick gate");

        fixture.Stdout.ReleaseProgressWrite.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        AssertTerminalOrdering(fixture.Stdout.ToString(), AudioHelperEventResult.Stopped);
        Assert.True(File.Exists(fixture.Output));
        Assert.False(File.Exists(fixture.Partial));
    }

    [Fact]
    public async Task InFlightTickThenRuntimeFail_FailsClosedWithoutPublishedOutput()
    {
        using var fixture = new Fixture();
        using var session = fixture.CreateSession();
        var runTask = Task.Run(() => session.Run());

        Assert.True(fixture.Input.Started.Wait(TimeSpan.FromSeconds(2)));
        fixture.Stdout.ArmNextProgressWrite();
        Assert.True(fixture.Stdout.ProgressWriteEntered.Wait(TimeSpan.FromSeconds(3)));

        var failureTask = Task.Run(() => fixture.Input.InjectError(new InvalidOperationException("runtime failure")));
        await Task.WhenAny(failureTask, Task.Delay(250));
        Assert.False(failureTask.IsCompleted,
            "runtime failure should be waiting for the in-flight tick gate");

        fixture.Stdout.ReleaseProgressWrite.Set();
        await failureTask.WaitAsync(TimeSpan.FromSeconds(3));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        AssertTerminalOrdering(fixture.Stdout.ToString(), AudioHelperEventResult.Fail);
        Assert.False(File.Exists(fixture.Output));
        Assert.False(File.Exists(fixture.Partial));
    }

    [Fact]
    public async Task SilentLoopbackCrossesTwoProgressIntervals_ThenStopsWithOneTerminal()
    {
        using var fixture = new Fixture();
        using var session = fixture.CreateSession();
        var runTask = Task.Run(() => session.Run());

        Assert.True(fixture.Input.Started.Wait(TimeSpan.FromSeconds(2)));
        var initialWavLength = fixture.Stream.Length;
        Assert.True(fixture.Stdout.ProgressEvents.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(fixture.Stdout.ProgressEvents.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(initialWavLength, fixture.Stream.Length);

        session.RequestStop();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(fixture.Stdout.ToString());
        Assert.True(events.Count(e => e.Result == AudioHelperEventResult.Progress) >= 2);
        AssertTerminalOrdering(fixture.Stdout.ToString(), AudioHelperEventResult.Stopped);
    }

    [Fact]
    public async Task StartBlockedThenSucceeds_AnchorBeginsAtSuccessfulStartBoundary()
    {
        using var fixture = new Fixture();
        fixture.Input.BlockStart = true;
        using var session = fixture.CreateSession();
        var runTask = Task.Run(() => session.Run());

        Assert.True(fixture.Input.StartEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(fixture.Input.StartCompleted.IsSet);
        fixture.Input.ReleaseStart.Set();
        Assert.True(fixture.Input.Started.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Stdout.StartedEvent.Wait(TimeSpan.FromSeconds(2)));

        session.RequestStop();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var started = Assert.Single(AudioHelperEventStreamParser.ParseEvents(fixture.Stdout.ToString()),
            evt => evt.Result == AudioHelperEventResult.Started);
        Assert.True(started.FirstSampleAnchorTicks >= fixture.Input.StartCompletedTimestamp);
    }

    [Fact]
    public async Task FirstStartFailsSecondSucceeds_TimelineStateIsNotInherited()
    {
        using var fixture = new Fixture();
        var first = new RaceInput
        {
            StartRecordingException = new AudioCaptureStartException(
                "first start failed", new InvalidOperationException("first"), unchecked((int)0x80070057))
        };
        var second = fixture.Input;
        int openCount = 0;
        fixture.InputFactory = _ => Interlocked.Increment(ref openCount) == 1 ? first : second;
        using var session = fixture.CreateSession();
        var runTask = Task.Run(() => session.Run());

        Assert.True(second.Started.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(fixture.Stdout.StartedEvent.Wait(TimeSpan.FromSeconds(2)));
        session.RequestStop();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var events = AudioHelperEventStreamParser.ParseEvents(fixture.Stdout.ToString());
        Assert.Single(events, evt => evt.Result == AudioHelperEventResult.Started);
        Assert.Single(events, evt => evt.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail);
        Assert.Equal(2, openCount);
        Assert.Equal(1, first.StartCount);
    }

    private static void AssertTerminalOrdering(string stdout, AudioHelperEventResult expectedTerminal)
    {
        var events = AudioHelperEventStreamParser.ParseEvents(stdout);
        Assert.NotEmpty(events);
        Assert.Equal(AudioHelperEventResult.Started, events[0].Result);
        var terminals = events.Where(e => e.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail).ToList();
        Assert.Single(terminals);
        Assert.Equal(expectedTerminal, terminals[0].Result);
        Assert.Same(terminals[0], events[^1]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ah_201b_race_{Guid.NewGuid():N}");
        public RaceInput Input { get; } = new();
        public SignalingWriter Stdout { get; } = new();
        public BlockingFileStream Stream { get; private set; } = null!;
        public Func<TimeSpan, IAudioInput>? InputFactory { get; set; }
        public string Output => Path.Combine(_directory, "capture.wav");
        public string Partial => Path.Combine(_directory, "capture.partial.wav");

        public Fixture() => Directory.CreateDirectory(_directory);

        public CaptureSession CreateSession()
        {
            var paths = new PathCheckResult
            {
                Ok = true,
                CanonicalPath = Output,
                PartialPath = Partial,
                OpenPartialStream = () =>
                {
                    Stream = new BlockingFileStream(Partial);
                    return Stream;
                }
            };
            var cts = new CancellationTokenSource();
            var watcher = new StopWatcher(Path.Combine(_directory, "stop.signal"), () => cts.Cancel());
            return new CaptureSession(
                new AudioHelperOptions
                {
                    Mode = AudioHelperMode.Capture,
                    SourceKind = AudioSourceKind.SystemLoopback,
                    EndpointId = "{endpoint}",
                    OutputPath = Output,
                    AllowedRoot = _directory,
                    StopSignalPath = Path.Combine(_directory, "stop.signal"),
                    RecordingId = "rec_201b_race"
                },
                paths,
                new EventWriter(Stdout, null),
                watcher,
                cts,
                budget => (InputFactory?.Invoke(budget) ?? Input, null, null));
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { }
            Input.Started.Dispose();
            Stream?.Dispose();
            Stdout.ProgressEvents.Dispose();
            Stdout.ProgressWriteEntered.Dispose();
            Stdout.ReleaseProgressWrite.Dispose();
            Stdout.StartedEvent.Dispose();
        }
    }

    private sealed class RaceInput : IAudioInput, IAudioPacketPositionSource
    {
        public WaveFormat? Format { get; } = new(1000, 16, 1);
        public AudioSourceKind SourceKind => AudioSourceKind.SystemLoopback;
        public long DiscontinuityCount => 0;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<AudioPacketEventArgs>? PacketPositionAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim StartEntered { get; } = new(false);
        public ManualResetEventSlim StartCompleted { get; } = new(false);
        public ManualResetEventSlim ReleaseStart { get; } = new(false);
        public bool BlockStart { get; set; }
        public Exception? StartRecordingException { get; set; }
        public long StartCompletedTimestamp { get; private set; }
        public int StartCount { get; private set; }
        private int _stopped;

        public StartRecordingResult StartRecording()
        {
            StartCount++;
            StartEntered.Set();
            if (StartRecordingException != null)
                throw StartRecordingException;
            if (BlockStart)
                ReleaseStart.Wait();
            StartCompletedTimestamp = Stopwatch.GetTimestamp();
            StartCompleted.Set();
            Started.Set();
            return StartRecordingResult.Started;
        }

        public void StopRecording()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void InjectError(Exception error)
            => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));

        public void Dispose() { }
    }

    private sealed class SignalingWriter : StringWriter
    {
        public ManualResetEventSlim ProgressEvents { get; } = new(false);
        public ManualResetEventSlim ProgressWriteEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseProgressWrite { get; } = new(false);
        public ManualResetEventSlim StartedEvent { get; } = new(false);
        private int _progressCount;
        private int _blockNextProgressWrite;

        public void ArmNextProgressWrite() => Interlocked.Exchange(ref _blockNextProgressWrite, 1);

        public override void WriteLine(string? value)
        {
            if (string.Equals(value, "RESULT: PROGRESS", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _blockNextProgressWrite, 0) == 1)
            {
                ProgressWriteEntered.Set();
                ReleaseProgressWrite.Wait();
            }

            base.WriteLine(value);
            if (string.Equals(value, "RESULT: PROGRESS", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _progressCount) >= 2)
                    ProgressEvents.Set();
            }
            else if (string.Equals(value, "RESULT: STARTED", StringComparison.Ordinal))
            {
                StartedEvent.Set();
            }
        }
    }

    private sealed class BlockingFileStream : FileStream
    {
        private int _blockNextWrite;
        public ManualResetEventSlim WriteEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseWrite { get; } = new(false);

        public BlockingFileStream(string path)
            : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        {
        }

        public void ArmNextDataWrite() => Interlocked.Exchange(ref _blockNextWrite, 1);

        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockIfArmed(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockIfArmed(buffer.Length);
            base.Write(buffer);
        }

        private void BlockIfArmed(int count)
        {
            if (count <= 0 || Interlocked.Exchange(ref _blockNextWrite, 0) != 1)
                return;
            WriteEntered.Set();
            ReleaseWrite.Wait();
        }
    }
}
