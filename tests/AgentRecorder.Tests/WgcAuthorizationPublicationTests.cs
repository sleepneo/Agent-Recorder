using System.IO.Pipes;
using System.Text;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-RealProcess")]
public sealed class WgcAuthorizationPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", "authorization-publication-" + Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WgcContinuousSessionOptions Options()
    {
        Directory.CreateDirectory(_root);
        var helper = Path.Combine(_root, "not-executed.exe");
        File.WriteAllText(helper, "Owned fixture, never executed or captured.");
        return new WgcContinuousSessionOptions
        {
            HelperExePath = helper, RecordingId = "rec_publication", DisplayWidth = 1920, DisplayHeight = 1080,
            OutputPath = Path.Combine(_root, "owned.mp4"), DurationMs = 5000, Fps = 30,
            BeginSignalPath = Path.Combine(_root, "begin.signal"), BeginToken = "owned-publication-token",
            BeginTimeoutMs = 30000, StopSignalPath = Path.Combine(_root, "stop.signal"),
            ProcessTimeoutMs = 30000, StopWaitTimeoutMs = 1000
        };
    }

    private static string Started(WgcContinuousSessionOptions o) => string.Join('\n', new[]
    {
        "RESULT: STARTED", "RecordingId: " + o.RecordingId, "Output: " + o.OutputPath,
        "Container: mp4", "Codec: h264", "Fps: 30", "Width: 1920", "Height: 1080",
        "CaptureMethod: WGC_D3D11_FRAME_STREAM", "EncoderMode: software",
        "EncoderSelectionReason: software_default", "", ""
    });

    private static string FirstFrame(string stage = "Capturing") => string.Join('\n', new[]
    {
        "RESULT: FIRST_FRAME", "RecordingId: rec_publication", "Stage: " + stage,
        "FrameNumber: 1", "ElapsedMs: 10", "SourceTimeHns: 100000", "", ""
    });

    private static string Progress => "RESULT: PROGRESS\nFramesCaptured: 1\nFramesDropped: 0\nElapsedMs: 10\nBytesWritten: 0\n\n";

    private static async Task Released(WgcContinuousManagedSession session)
    {
        // The existing owner task completes before its finally decrements the CTS
        // counter. Poll only this resource-release fact within the original 5s bound.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.ActiveAuthorizationLinkedCtsCountForTests != 0)
            await Task.Delay(1, deadline.Token);
        Assert.Equal(0, session.ActiveAuthorizationLinkedCtsCountForTests);
    }

    private static void NoSignals(WgcContinuousSessionOptions o)
    {
        Assert.False(File.Exists(o.BeginSignalPath));
        Assert.False(File.Exists(o.BeginSignalPath + ".tmp"));
        Assert.False(File.Exists(o.StopSignalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPublicationAcceptsImmediateStartedAndFirstFrameWhileWriterReturnIsPaused(bool fallback)
    {
        var o = Options();
        using var transport = new Transport();
        var writer = new BarrierWriter();
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observed = Signal();
        var firstFrames = 0;
        session.FirstFrameObserved += _ => { Interlocked.Increment(ref firstFrames); observed.TrySetResult(); };
        await session.StartAsync();
        var owner = session.AuthorizeCapture();
        await writer.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(o.BeginToken, File.ReadAllText(o.BeginSignalPath));
        Assert.False(owner.IsCompleted);
        Assert.False(await session.AuthorizeCapture());
        await transport.Emit(Started(o) + (fallback ? Progress : FirstFrame()));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Started, session.State);
        Assert.False(session.CompletionTask.IsCompleted);
        Assert.False(owner.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref firstFrames));
        writer.AfterRelease.TrySetResult();
        Assert.True(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(WgcContinuousManagedSessionState.Started, session.State);
        Assert.Equal(1, Volatile.Read(ref firstFrames));
        session.Dispose();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.NotEqual("event_before_authorization", result.FailureCategory);
        await Released(session);
        NoSignals(o);
    }

    [Theory]
    [InlineData("first_before_started", "first_frame_before_started", 0)]
    [InlineData("malformed", "first_frame_invalid", 0)]
    [InlineData("duplicate", "duplicate_first_frame", 1)]
    [InlineData("after_terminal", "first_frame_after_terminal", 0)]
    public async Task PublishedButPausedStillEnforcesExactProtocolOrder(string scenario, string category, int expectedObservations)
    {
        var o = Options();
        using var transport = new Transport();
        var writer = new BarrierWriter();
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var count = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref count);
        await session.StartAsync();
        var owner = session.AuthorizeCapture();
        await writer.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(owner.IsCompleted);
        var events = scenario switch
        {
            "first_before_started" => FirstFrame(),
            "malformed" => Started(o) + FirstFrame("Finalizing") + Progress,
            "duplicate" => Started(o) + FirstFrame() + FirstFrame(),
            _ => Started(o) + "RESULT: OK\nEncoderMode: software\nEncoderSelectionReason: software_default\nFramesCaptured: 1\nFramesDropped: 0\nDurationMs: 10\nFileSize: 1024 bytes\nWidth: 1920\nHeight: 1080\n\n" + FirstFrame()
        };
        await transport.Emit(events);
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal(category, result.FailureCategory);
        Assert.Equal(expectedObservations, Volatile.Read(ref count));
        writer.AfterRelease.TrySetResult();
        Assert.False(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        await Released(session);
        NoSignals(o);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventsBeforePublicationRemainUnauthorized(bool callAuthorization)
    {
        var o = Options();
        using var transport = new Transport();
        var writer = new BarrierWriter { PauseBefore = true };
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observations = 0;
        var publicEvents = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref observations);
        session.EventReceived += _ => Interlocked.Increment(ref publicEvents);
        await session.StartAsync();
        var owner = callAuthorization ? session.AuthorizeCapture() : Task.FromResult(false);
        if (callAuthorization) await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(o.BeginSignalPath));
        await transport.Emit(Started(o) + FirstFrame());
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        writer.BeforeRelease.TrySetResult();
        Assert.False(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("event_before_authorization", result.FailureCategory);
        Assert.Equal(0, Volatile.Read(ref observations));
        Assert.Equal(0, Volatile.Read(ref publicEvents));
        await Released(session);
        NoSignals(o);
    }

    [Theory]
    [InlineData("throw_before")]
    [InlineData("missing_commit")]
    [InlineData("move_failure")]
    [InlineData("throw_after")]
    [InlineData("duplicate_commit")]
    public async Task WriterOrCommitFailureTerminatesWithoutObservationOrRetry(string failure)
    {
        var o = Options();
        if (failure == "move_failure") Directory.CreateDirectory(o.BeginSignalPath);
        using var transport = new Transport();
        var writer = new BarrierWriter { Failure = failure };
        writer.AfterRelease.TrySetResult();
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observations = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref observations);
        await session.StartAsync();
        Assert.False(await session.AuthorizeCapture().WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("authorize_write_failed", result.FailureCategory);
        Assert.Equal(0, Volatile.Read(ref observations));
        Assert.False(await session.AuthorizeCapture());
        Assert.True(transport.Killed);
        await Released(session);
        Assert.False(File.Exists(o.BeginSignalPath));
        Assert.False(File.Exists(o.BeginSignalPath + ".tmp"));
    }

    [Fact]
    public async Task CancellationBeforeCommitCleansTmpAndOwnerCanRetryThroughRealWriter()
    {
        var o = Options();
        using var transport = new Transport();
        using var cancellation = new CancellationTokenSource();
        var writer = new BarrierWriter { CancelAtCommit = cancellation };
        writer.AfterRelease.TrySetResult();
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        await session.StartAsync();
        Assert.False(await session.AuthorizeCapture(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        await Released(session);
        Assert.True(writer.SawPreparedTmp);
        NoSignals(o);
        Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);
        writer.CancelAtCommit = null;
        Assert.True(await session.AuthorizeCapture().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(o.BeginToken, File.ReadAllText(o.BeginSignalPath));
        session.Dispose();
        await Released(session);
        NoSignals(o);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileWriterPendingHasCorrectPreAndPostCommitOutcome(bool published)
    {
        var o = Options();
        using var transport = new Transport();
        using var cancellation = new CancellationTokenSource();
        var writer = new BarrierWriter { PauseBefore = !published };
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observations = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref observations);
        await session.StartAsync();
        var owner = session.AuthorizeCapture(cancellation.Token);
        await (published ? writer.Published.Task : writer.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        if (published)
        {
            var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
            Assert.True(transport.Killed);
        }
        else
        {
            Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);
            Assert.False(session.CompletionTask.IsCompleted);
            session.Dispose();
        }
        Assert.Equal(0, Volatile.Read(ref observations));
        await Released(session);
        NoSignals(o);
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    public async Task StopAndDisposeDoNotWaitForWriterOrAllowLatePublication(string action, bool published)
    {
        var o = Options();
        using var transport = new Transport();
        var writer = new BarrierWriter { PauseBefore = !published };
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observations = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref observations);
        await session.StartAsync();
        var owner = session.AuthorizeCapture();
        await (published ? writer.Published.Task : writer.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(owner.IsCompleted);
        if (action == "dispose")
            await Task.Run(session.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            var stopping = session.RequestStop();
            if (published)
                await transport.Emit(Started(o));
            transport.Exit();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(WgcContinuousManagedSessionState.Success, result.State);
        writer.BeforeRelease.TrySetResult();
        writer.AfterRelease.TrySetResult();
        Assert.False(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await session.AuthorizeCapture());
        Assert.Equal(0, Volatile.Read(ref observations));
        await Released(session);
        NoSignals(o);
    }

    [Fact]
    public async Task StopAfterPublishedImmediateFramesKeepsSingleCompletionAndCleanup()
    {
        var o = Options();
        using var transport = new Transport();
        var writer = new BarrierWriter();
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observed = Signal();
        session.FirstFrameObserved += _ => observed.TrySetResult();
        await session.StartAsync();
        var owner = session.AuthorizeCapture();
        await writer.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.Emit(Started(o) + FirstFrame());
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.RequestStop();
        transport.Exit();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        var first = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        writer.AfterRelease.TrySetResult();
        Assert.False(await owner.WaitAsync(TimeSpan.FromSeconds(5)));
        session.Dispose();
        Assert.Same(first, await session.CompletionTask);
        Assert.NotEqual("event_before_authorization", first.FailureCategory);
        await Released(session);
        NoSignals(o);
    }

    [Fact]
    public async Task CancellationAfterCommitWithSuccessfulWriterReturnStillTerminates()
    {
        var o = Options();
        using var transport = new Transport();
        using var cancellation = new CancellationTokenSource();
        var writer = new BarrierWriter { CancelAfterCommit = cancellation };
        using var session = new WgcContinuousManagedSession(o, transport, writer);
        var observations = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref observations);
        await session.StartAsync();
        Assert.False(await session.AuthorizeCapture(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writer.ReturnedNormallyAfterCancellation);
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.Equal(0, Volatile.Read(ref observations));
        Assert.False(await session.AuthorizeCapture());
        Assert.True(transport.Killed);
        await Released(session);
        NoSignals(o);
    }

    private sealed class BarrierWriter : IAuthorizationSignalWriter
    {
        internal readonly TaskCompletionSource Entered = Signal(), Published = Signal(), BeforeRelease = Signal(), AfterRelease = Signal();
        internal bool PauseBefore;
        internal string? Failure;
        internal CancellationTokenSource? CancelAtCommit;
        internal CancellationTokenSource? CancelAfterCommit;
        internal bool SawPreparedTmp;
        internal bool ReturnedNormallyAfterCancellation;

        public async Task WriteBeginTokenAsync(string tmp, string final, string token, Action commit, CancellationToken cancellation)
        {
            Entered.TrySetResult();
            if (PauseBefore) await BeforeRelease.Task.WaitAsync(cancellation);
            if (Failure == "throw_before") throw new IOException("Controlled pre-publication failure.");
            if (Failure == "missing_commit") return;
            await new FileAuthorizationSignalWriter().WriteBeginTokenAsync(tmp, final, token, () =>
            {
                SawPreparedTmp = File.ReadAllText(tmp) == token;
                CancelAtCommit?.Cancel();
                commit();
            }, cancellation);
            Published.TrySetResult();
            if (CancelAfterCommit is not null)
            {
                CancelAfterCommit.Cancel();
                ReturnedNormallyAfterCancellation = true;
                return;
            }
            await AfterRelease.Task.WaitAsync(cancellation);
            if (Failure == "throw_after") throw new IOException("Controlled post-publication failure.");
            if (Failure == "duplicate_commit") commit();
        }
    }

    private sealed class Transport : IWgcContinuousProcess
    {
        private readonly AnonymousPipeServerStream _writer = new(PipeDirection.Out, HandleInheritability.None);
        private readonly AnonymousPipeClientStream _reader;
        private readonly TaskCompletionSource _exit = Signal();
        internal bool Killed;
        internal Transport() => _reader = new(PipeDirection.In, _writer.ClientSafePipeHandle);
        public int Id => 4242;
        public bool HasExited => _exit.Task.IsCompleted;
        public int ExitCode => 0;
        public Stream StandardOutputStream => _reader;
        public Stream StandardErrorStream => Stream.Null;
        public void Start(string fileName, IReadOnlyList<string> arguments) { }
        internal async Task Emit(string text) { await _writer.WriteAsync(Encoding.UTF8.GetBytes(text)); await _writer.FlushAsync(); }
        internal void Exit() { _writer.Dispose(); _exit.TrySetResult(); }
        public void KillEntireTree() { Killed = true; Exit(); }
        public Task WaitForExitAsync(CancellationToken cancellation) => _exit.Task.WaitAsync(cancellation);
        public void Dispose() { _writer.Dispose(); _reader.Dispose(); }
    }

    public void Dispose()
    {
        // Deliberately retain owned fixture evidence; no cleanup of previous runs.
    }
}
