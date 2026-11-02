using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class WorkerStartBoundaryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DisposeDuringStartDefersCleanupAndCannotClaimRetirement(bool ambiguousLaunch)
    {
        var root = NeverStartedWorkerRetirementTests.Root();
        using var worker = NeverStartedWorkerRetirementTests.MissingWorker(root);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        if (ambiguousLaunch)
            worker.StartProcessForTests = _ => { entered.Set(); release.Wait(); throw new Win32Exception(2); };
        else
            worker.ConfigureProcessForTests = _ => { entered.Set(); release.Wait(); };
        var start = Task.Run(() => Record.Exception(() => worker.Start(NeverStartedWorkerRetirementTests.Config(), Path.Combine(root, "no.wav"))));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            worker.Dispose();
            Assert.False(worker.HostResourcesRetired);
            Assert.False(start.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => worker.Start(NeverStartedWorkerRetirementTests.Config(), Path.Combine(root, "other.wav")));
        }
        finally { release.Set(); }
        Assert.NotNull(await start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(!ambiguousLaunch, worker.HostResourcesRetired);
        Assert.False(worker.HasExited);
        Assert.Null(worker.HelperProcessIdForTests);
    }

    [Theory]
    [InlineData("audio")] [InlineData("video")]
    public void OtherNativeWorkerMissingExecutableNeedsClosedAdmissionAndRealCleanup(string kind)
    {
        var root = NeverStartedWorkerRetirementTests.Root();
        var missing = Path.Combine(root, "never-existing-encoder.exe");
        var cfg = new CaptureConfig { Bounds = (0, 0, 64, 64), DurationSeconds = 1 };
        if (kind == "audio")
        {
            using var worker = new AudioCaptureWorker { TestArgumentsOverride = new() { "-version" }, ConfigureProcessForTests = p => p.FileName = missing };
            Assert.Throws<ApiException>(() => worker.Start(cfg, Path.Combine(root, "audio.wav")));
            Assert.False(worker.HostResourcesRetired);
            worker.Dispose();
            Assert.True(worker.HostResourcesRetired);
            Assert.False(worker.HasExited);
            Assert.Throws<ObjectDisposedException>(() => worker.Start(cfg, Path.Combine(root, "later.wav")));
        }
        else
        {
            using var worker = new VideoCaptureWorker { TestArgumentsOverride = new() { "-version" },
                TestProcessFactory = info => { info.FileName = missing; return new SystemVideoCaptureProcess(info); } };
            Assert.Throws<ApiException>(() => worker.Start(cfg, Path.Combine(root, "video.mp4")));
            Assert.False(worker.HostResourcesRetired);
            worker.Dispose();
            Assert.True(worker.HostResourcesRetired);
            Assert.False(worker.HasExited);
            Assert.Throws<ObjectDisposedException>(() => worker.Start(cfg, Path.Combine(root, "later.mp4")));
        }
    }

    [Fact]
    public void MissingFfmpegStartKeepsFailedPublicationAndRetiresAfterOwnerCloses()
    {
        var root = NeverStartedWorkerRetirementTests.Root();
        using var backend = new FfmpegCaptureBackend { ConfigureProcessForTests = p => p.FileName = Path.Combine(root, "missing-encoder.exe") };
        var cfg = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 64, 64), DurationSeconds = 1, OutputPath = Path.Combine(root, "not-published.mp4") };
        Assert.Throws<ApiException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend, cfg));
        Assert.False(backend.HostResourcesRetired);
        Assert.True(backend.IsVideoOnlyOutputSettled);
        backend.RetireUnstartedHostResources(); // Same owner operation invoked by the failed engine winner.
        Assert.True(backend.HostResourcesRetired);
        Assert.False(File.Exists(cfg.OutputPath));
        Assert.False(backend.LastMeta?.OutputFileExists ?? false);
        Assert.Throws<ObjectDisposedException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend, cfg));
    }

    [Fact]
    public void WgcRejectedBeforeFactoryRequiresCleanupAndCannotStartAfterDispose()
    {
        int calls = 0;
        using var worker = new WgcContinuousVideoCaptureWorker(CaptureAuthorizationTestHelper.CreateSyntheticConsumedProof(),
            () => { calls++; throw new InvalidOperationException(); }, () => 1);
        Assert.Throws<InvalidOperationException>(() => worker.Start(new CaptureConfig(), "never.mp4"));
        Assert.False(worker.HostResourcesRetired);
        worker.Dispose();
        Assert.True(worker.HostResourcesRetired);
        Assert.Equal(0, calls);
        Assert.Throws<ObjectDisposedException>(() => worker.Start(new CaptureConfig(), "never.mp4"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WgcKeepsRealBackendRetirementEvidenceAndDoesNotInferFromNull(bool unknownFactory)
    {
        var root = NeverStartedWorkerRetirementTests.Root();
        var provider = (AsyncLocal<Func<bool, bool, List<SystemQuery.WindowInfo>>?>)typeof(SystemQuery)
            .GetField("_windowProvider", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var prior = provider.Value;
        SystemQuery.SetWindowProvider((_, _) => new() { new("window_12345", "Synthetic", "fixture.exe", 99, false, false, new(0, 0, 64, 64)) });
        using var backend = new WgcContinuousCaptureBackend(_ => throw new IOException("No session was created."),
            StagingToFinalPublisher.Instance, _ => throw new InvalidOperationException("No probe."), () => "unused-helper.exe", root);
        using var worker = new WgcContinuousVideoCaptureWorker(CaptureAuthorizationTestHelper.CreateSyntheticConsumedProof(),
            () => unknownFactory ? throw new IOException("Factory result unknown.") : backend, () => 1);
        try
        {
            var cfg = new CaptureConfig { RequireWindowSurface = true, SourceKind = "window", WindowHandle = (nint)12345,
                WindowProcessId = 99, WindowSurfaceBounds = (0, 0, 64, 64), Bounds = (0, 0, 64, 64), DurationSeconds = 1,
                OutputPath = Path.Combine(root, "no.mp4") };
            Assert.ThrowsAny<Exception>(() => worker.Start(cfg, cfg.OutputPath));
            worker.Dispose();
            Assert.False(worker.HostResourcesRetired); // Even the returned backend still owns an unproven throwing session factory.
            if (!unknownFactory) Assert.False(backend.OutputRetirement.IsCompleted);
            Assert.False(worker.HasBackendForTests);
            Assert.False(File.Exists(cfg.OutputPath));
        }
        finally { SystemQuery.SetWindowProvider(prior); }
    }

    [Fact]
    public void WgcBackendNeverStartedRetiresOnlyAtItsExistingAtomicCreatedDisposal()
    {
        using var backend = new WgcContinuousCaptureBackend(_ => throw new InvalidOperationException("No real session."),
            StagingToFinalPublisher.Instance, _ => throw new InvalidOperationException("No probe."),
            () => throw new InvalidOperationException("No helper."), NeverStartedWorkerRetirementTests.Root());
        Assert.False(backend.OutputRetirement.IsCompleted);
        backend.RetireUnstartedHostResources();
        Assert.True(backend.OutputRetirement.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,
            new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 64, 64), DisplayId = "synthetic", DisplayBounds = (0, 0, 128, 128), DurationSeconds = 1, OutputPath = Path.Combine(NeverStartedWorkerRetirementTests.Root(), "never.mp4") }));
    }

    [Fact]
    public async Task LiveWorkerStaysBusyWithoutKillUntilItsExitIsActuallyAcknowledged()
    {
        var process = new VideoProcess(new MemoryStream()) { ErrorStreamClosed = true };
        process.Exit.Reset();
        using var worker = Video(process);
        worker.Start(new CaptureConfig(), Path.Combine(NeverStartedWorkerRetirementTests.Root(), "not-created.mp4"));
        try
        {
            Assert.False(worker.HostResourcesRetired);
            worker.Dispose();
            Assert.False(worker.HostResourcesRetired);
            Assert.Equal(0, process.Kills);
        }
        finally { process.Exit.Set(); }
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HostResourcesRetired, TimeSpan.FromSeconds(3))));
        Assert.Equal(0, process.Kills);
    }

    [Fact]
    public async Task ProcessExitAndDisposeDoNotStandInForAHangingStdoutReader()
    {
        using var stream = new PendingReadStream();
        var process = new VideoProcess(stream) { ErrorStreamClosed = true };
        using var worker = Video(process);
        worker.Start(new CaptureConfig(), Path.Combine(NeverStartedWorkerRetirementTests.Root(), "video.mp4"));
        try
        {
            Assert.True(await Task.Run(() => stream.Entered.Wait(TimeSpan.FromSeconds(3))));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(4))));
            worker.Dispose();
            Assert.False(worker.HostResourcesRetired);
            Assert.Equal(0, process.Kills);
        }
        finally { stream.Release.Set(); }
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HostResourcesRetired, TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public async Task ConfirmedNoChildFailureCannotRetireTheNextStartedGeneration()
    {
        var root = NeverStartedWorkerRetirementTests.Root();
        var second = new VideoProcess(new MemoryStream());
        int calls = 0;
        using var worker = new VideoCaptureWorker { TestArgumentsOverride = new() { "-version" }, TestProcessFactory = info =>
        {
            if (++calls == 1) { info.FileName = Path.Combine(root, "missing-video.exe"); return new SystemVideoCaptureProcess(info); }
            return second;
        } };
        Assert.Throws<ApiException>(() => worker.Start(new CaptureConfig(), Path.Combine(root, "first.mp4")));
        worker.Start(new CaptureConfig(), Path.Combine(root, "second.mp4"));
        Assert.False(worker.HostResourcesRetired);
        second.Eof();
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HostResourcesRetired, TimeSpan.FromSeconds(3))));
        Assert.Equal(0, second.Kills);
    }

    [Fact]
    public async Task PreviousEofCannotRetireNewGenerationWithPendingStderr()
    {
        var first = new VideoProcess(new MemoryStream()) { ErrorStreamClosed = true };
        var second = new VideoProcess(new MemoryStream());
        var processes = new Queue<VideoProcess>(new[] { first, second });
        using var worker = new VideoCaptureWorker { TestArgumentsOverride = new() { "-version" }, TestProcessFactory = _ => processes.Dequeue() };
        var root = NeverStartedWorkerRetirementTests.Root();
        worker.Start(new CaptureConfig(), Path.Combine(root, "first.mp4"));
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HostResourcesRetired, TimeSpan.FromSeconds(3))));
        worker.Start(new CaptureConfig(), Path.Combine(root, "second.mp4"));
        first.Eof();
        Assert.False(worker.HostResourcesRetired);
        second.Eof();
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => worker.HostResourcesRetired, TimeSpan.FromSeconds(3))));
        Assert.Equal(0, first.Kills + second.Kills);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownInjectedStartResultIsNotTheNativeNoChildContract(bool falseResult)
    {
        var process = new VideoProcess(new MemoryStream()) { ThrowStart = !falseResult, FalseStart = falseResult };
        using var worker = Video(process);
        Assert.Throws<ApiException>(() => worker.Start(new CaptureConfig(), Path.Combine(NeverStartedWorkerRetirementTests.Root(), "no.mp4")));
        worker.Dispose();
        Assert.False(worker.HostResourcesRetired);
        Assert.Equal(0, process.Kills);
    }

    [Fact]
    public void MissingErrorCodeWithAssociatedOrUnknownProcessIsStillBusy()
    {
        var boundary = new WorkerStartBoundary();
        var failure = new Win32Exception(2);
        Assert.Same(failure, Assert.Throws<Win32Exception>(() => boundary.Start(attempt =>
            attempt.Launch(() => throw failure, nativeProcessContract: true, () => false), () => false)));
        boundary.Close(() => true);
        Assert.False(boundary.ObserveRetired(() => false));
    }

    [Fact]
    public void UnstartedCleanupFailureRemainsUnknownUntilThatCleanupActuallySucceeds()
    {
        var boundary = new WorkerStartBoundary();
        bool completed = false;
        boundary.Start(_ => { }, () => false);
        boundary.Close(() => completed);
        Assert.False(boundary.ObserveRetired(() => true));
        completed = true;
        boundary.Close(() => completed);
        Assert.True(boundary.ObserveRetired(() => false));
    }

    private static VideoCaptureWorker Video(VideoProcess process) => new()
    { TestArgumentsOverride = new() { "-version" }, TestProcessFactory = _ => process };

    private sealed class VideoProcess(Stream stdout) : IVideoCaptureProcess
    {
        public event DataReceivedEventHandler? ErrorDataReceived;
        public ProcessStartInfo StartInfo { get; } = new();
        public StreamReader StandardOutput { get; } = new(stdout, Encoding.UTF8);
        public StreamWriter StandardInput { get; } = new(Stream.Null);
        internal readonly ManualResetEventSlim Exit = new(true);
        public bool HasExited => Exit.IsSet;
        public int ExitCode => 0;
        public bool ErrorStreamClosed { get; set; }
        internal bool ThrowStart, FalseStart;
        internal int Kills;
        public bool Start() => ThrowStart ? throw new Win32Exception(2) : !FalseStart;
        public void BeginErrorReadLine() { }
        public bool WaitForExit(int milliseconds) => Exit.Wait(milliseconds);
        public bool WaitForExit(TimeSpan timeout) => Exit.Wait(timeout);
        public void Kill(bool entireProcessTree) => Kills++;
        public void Dispose() { } // No inferred EOF or reader completion from disposal.
        internal void Eof()
        {
            ErrorStreamClosed = true;
            var args = (DataReceivedEventArgs)Activator.CreateInstance(typeof(DataReceivedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { null }, null)!;
            ErrorDataReceived?.Invoke(this, args);
        }
    }
    private sealed class PendingReadStream : Stream
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        public override int Read(byte[] buffer, int offset, int count) { Entered.Set(); Release.Wait(); return 0; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
