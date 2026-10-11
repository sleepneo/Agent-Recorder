using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvSplitVideoOnlyPublicationTests(ITestOutputHelper output)
{
    private sealed class Files : IDisposable
    {
        private static readonly string TaskRoot = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task327"));
        internal readonly string Root = Path.Combine(TaskRoot, "split-" + Guid.NewGuid().ToString("N"));
        internal string Videos => Path.Combine(Root, "Videos");
        internal string Fixture => Path.Combine(Root, "fixture.mp4");
        internal string Audio => Path.Combine(Root, "fixture.wav");
        internal string Final => Path.Combine(Videos, "final.mp4");
        internal string Retained => Path.Combine(Root, "failed/final/video.mp4");
        private readonly string? _previousData = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        internal byte[] OldBytes = Array.Empty<byte>();
        internal Files() { Directory.CreateDirectory(Videos); DataDirResolver.SetOverride(Root); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root); }
        internal async Task Generate()
        {
            await GenerateMedia(new[] { "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=15", "-t", "1", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", Fixture });
            File.Copy(Fixture, Final); OldBytes = File.ReadAllBytes(Final);
            File.WriteAllText(Path.Combine(Videos, "user-sentinel.txt"), "unchanged");
        }
        internal Task GenerateAudio() => GenerateMedia(new[] { "-y", "-f", "lavfi", "-i", "sine=frequency=1000:duration=2", "-acodec", "pcm_s16le", "-ar", "44100", "-ac", "2", Audio });
        private static async Task GenerateMedia(string[] args)
        {
            var result = await new ExternalProcessRunner().RunAsync(Path.Combine(TestHelper.ProjectRoot, "tools/ffmpeg/bin/ffmpeg.exe"), args, TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.Stderr);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools(); DataDirResolver.ClearOverride(); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _previousData);
            Assert.Equal(TaskRoot, Path.GetDirectoryName(Path.GetFullPath(Root)), ignoreCase: true);
            Assert.StartsWith(TaskRoot + Path.DirectorySeparatorChar, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, recursive: true);
        }
    }
    private sealed class Video(string fixture) : IVideoCaptureWorker
    {
        internal readonly FakeVideoCaptureWorker Inner = new();
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<FirstFrameObservation>? FirstFrameObserved { add => Inner.FirstFrameObserved += value; remove => Inner.FirstFrameObserved -= value; }
        public event Action<int, string>? NaturalExit { add => Inner.NaturalExit += value; remove => Inner.NaturalExit -= value; }
        public string? OutputPath => Inner.OutputPath; public int ExitCode => Inner.ExitCode; public bool HasExited => Inner.HasExited;
        public long LaunchAnchorTicks => Inner.LaunchAnchorTicks; public long FirstFrameAnchorTicks => Inner.FirstFrameAnchorTicks;
        public long? FirstProgressFrame => Inner.FirstProgressFrame; public long? FirstProgressOutTimeUs => Inner.FirstProgressOutTimeUs;
        public double? ProgressAnchorDeltaMs => Inner.ProgressAnchorDeltaMs;
        public void Start(CaptureConfig cfg, string path) { File.Copy(fixture, path, overwrite: true); Inner.Start(cfg, path); Started.TrySetResult(true); }
        public OutputMeta Stop() { var m = Inner.Stop(); m.PublicationReceipt = PublicationReceipt.IssueForTests("PRIVATE-intermediate.mp4", 12, new(1, 2, 3)); return m; }
        public bool WaitForExit(TimeSpan timeout) => Inner.WaitForExit(timeout);
        public string GetStderrLog() => "";
        public void Dispose() => Inner.Dispose();
    }
    private sealed class Factory(Video video, IAudioCaptureWorker? audio = null) : IAvWorkerFactory
    {
        internal int AudioCalls, VideoCalls;
        public IAudioCaptureWorker CreateAudioWorker() { AudioCalls++; return audio ?? throw new InvalidOperationException("No audio requested"); }
        public IVideoCaptureWorker CreateVideoWorker() { VideoCalls++; return video; }
    }
    private sealed class Prober(Func<string, int, OutputMeta> read) : IOutputProber
    {
        internal int Calls;
        public OutputMeta Probe(string path) => read(path, ++Calls);
    }
    private sealed class Publisher(IStagingToFinalPublisher inner) : IStagingToFinalPublisher
    {
        internal int Calls;
        internal Action? Before;
        internal Action<PublishResult>? After;
        internal IFileCommitGate? Gate;
        internal CancellationToken ExtraToken;
        internal CancellationToken PublicationToken;
        public async Task<PublishResult> PublishAsync(string source, string final, CancellationToken token = default, IFileCommitGate? commitGate = null)
        {
            PublicationToken = token; Interlocked.Increment(ref Calls); Before?.Invoke();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ExtraToken);
            var result = await inner.PublishAsync(source, final, linked.Token, Gate ?? commitGate);
            After?.Invoke(result); return result;
        }
    }
    private sealed class Operations : IPublicationFileOperations
    {
        internal SafeFileHandle? Original;
        internal ObservedFileIdentity? OriginalId;
        internal int Renames, Deletes;
        public FileStream CreateNew(string path) { var stream = WindowsPublicationFileOperations.Instance.CreateNew(path); Original = stream.SafeFileHandle; OriginalId = Identity(Original); return stream; }
        public void Rename(SafeFileHandle handle, string final) { Assert.Same(Original, handle); Renames++; WindowsPublicationFileOperations.Instance.Rename(handle, final); }
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle handle) { Assert.Same(Original, handle); Deletes++; return WindowsPublicationFileOperations.Instance.DeleteOriginal(handle); }
    }
    private sealed class Capacity : IStorageCapacityProvider
    {
        internal long Available = 4L * 1024 * 1024 * 1024;
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
            => Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "synthetic-volume", Available)).ToArray());
    }
    private static CaptureConfig Config(Files f, AudioCaptureSourceKind kind = AudioCaptureSourceKind.None) => new()
    { SourceKind = "display", AudioSourceKind = kind, MicDevice = kind == AudioCaptureSourceKind.Microphone ? "fake-mic" : null, SystemLoopbackEndpoint = kind == AudioCaptureSourceKind.SystemLoopback ? "fake-render" : null, Fps = 15, Bounds = (0, 0, 64, 64), OutputPath = f.Final };
    private static ObservedFileIdentity Identity(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Identity(stream.SafeFileHandle); }
    private static ObservedFileIdentity Identity(SafeFileHandle h) { Assert.True(WindowsFileMetadataApi.Instance.Identity(h, out var id, out _)); return new(id.VolumeSerialNumber, id.IdLow, id.IdHigh); }
    private static AvSplitCaptureBackend Backend(Files f, Factory factory, Publisher publisher, FakeExternalProcessRunner runner) => new(factory, runner, new TempRetentionPolicy(f.Root)) { FinalOutputPublisher = publisher };
    private static void Start(AvSplitCaptureBackend backend, CaptureConfig cfg) { CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend, cfg); backend.StartVideo(); }
    private static void Failed(AvSplitCaptureBackend backend, OutputMeta m, string final)
    {
        Assert.Same(m, backend.LastMeta); Assert.Equal(final, m.OutputPath); Assert.False(m.OutputFileExists); Assert.Equal(0, m.SizeBytes); Assert.Null(m.PublicationReceipt);
        var error = (m.StderrLog ?? "") + string.Join(";", m.Warnings);
        Assert.DoesNotContain("PRIVATE", error); Assert.DoesNotContain(".publish-tmp-", error); Assert.DoesNotContain("_video.mp4", error);
    }
    private static void OldFinal(Files f, ObservedFileIdentity old)
    { Assert.Equal(old, Identity(f.Final)); Assert.Equal(f.OldBytes.Length, new FileInfo(f.Final).Length); Assert.Equal(f.OldBytes, File.ReadAllBytes(f.Final)); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(f.Videos, "user-sentinel.txt"))); }
    private static void Successful(Files f, AvSplitCaptureBackend backend, OutputMeta meta, Factory factory, Publisher publisher, Operations ops, FakeExternalProcessRunner runner)
    {
        Assert.Same(meta, backend.LastMeta); Assert.True(meta.OutputFileExists, meta.StderrLog + " " + string.Join(";", meta.Warnings));
        Assert.Equal(f.Final, meta.OutputPath); Assert.Equal("not_requested", meta.AudioStatus); Assert.Equal("not_checked", meta.AudioContinuityStatus); Assert.Equal("none", meta.AudioSourceKind);
        Assert.Equal(64, meta.Width); Assert.Equal(64, meta.Height); Assert.Equal(15, meta.Fps); Assert.Equal("h264", meta.Codec); Assert.Equal("mp4", meta.Container); Assert.Equal(1, meta.DurationSeconds);
        Assert.False(meta.HasAudioStream); Assert.Null(meta.AudioCodec); Assert.Single(meta.ProbeStreams);
        Assert.NotNull(meta.PublicationReceipt); Assert.Equal(ops.OriginalId, meta.PublicationReceipt!.Anchor.Identity); Assert.Equal(Identity(f.Final), ops.OriginalId);
        Assert.Equal(meta.SizeBytes, meta.PublicationReceipt.Anchor.Length); Assert.Equal(new FileInfo(f.Final).Length, meta.SizeBytes); Assert.Equal(f.Final, meta.PublicationReceipt.Anchor.Path);
        Assert.Equal(File.ReadAllBytes(f.Fixture), File.ReadAllBytes(f.Final)); Assert.Equal(0, factory.AudioCalls); Assert.Equal(1, factory.VideoCalls); Assert.Equal(1, publisher.Calls); Assert.Equal(0, runner.RunCallCount);
        Assert.False(File.Exists(backend.TempVideoPath)); Assert.False(File.Exists(backend.TempAudioPath)); Assert.False(Directory.Exists(Path.Combine(f.Root, "failed/final")));
        Assert.True(ops.Original!.IsClosed); Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
        using var exclusive = new FileStream(f.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealStartVideoManualOrNaturalExitPublishesValidatedSilentMp4AndCleansOwnedSource(bool natural)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var video = new Video(f.Fixture); var factory = new Factory(video);
        var ops = new Operations(); var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); var runner = new FakeExternalProcessRunner();
        using var backend = Backend(f, factory, publisher, runner);
        var ended = 0; var callbacks = 0; OutputMeta? notified = null;
        backend.CaptureEnded += _ => ended++; backend.OnNaturalExit((_, m) => { callbacks++; notified = m; });
        publisher.Before = () => Assert.Equal(1, ended);
        backend.VideoOnlyOutputProber = new Prober((p, _) => { var meta = new FfmpegOutputProber().Probe(p); meta.PublicationReceipt = PublicationReceipt.IssueForTests("PRIVATE-probe.mp4", 12, new(1, 2, 3)); return meta; });
        Start(backend, Config(f)); OutputMeta result;
        if (natural) { video.Inner.EmitNaturalExit(0, ""); result = notified!; } else result = backend.Stop();
        Successful(f, backend, result, factory, publisher, ops, runner); Assert.Equal(1, ended); Assert.Equal(natural ? 1 : 0, callbacks); Assert.NotEqual(old, Identity(f.Final));
        output.WriteLine($"AV split Start/StartVideo/{(natural ? "natural" : "Stop")}: original={ops.OriginalId}, receipt={result.PublicationReceipt!.Anchor.Identity}, path={result.OutputPath}, bytes={result.SizeBytes}, H264/MP4 64x64/15fps/1s, audio=False; source cleaned, CaptureEnded before publication.");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentNaturalManualAndStopWaitersShareSingleRealPublication(bool naturalOwner)
    {
        using var f = new Files(); await f.Generate(); var video = new Video(f.Fixture); var factory = new Factory(video); var ops = new Operations();
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); var runner = new FakeExternalProcessRunner();
        using var backend = Backend(f, factory, publisher, runner); Start(backend, Config(f));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.BeforeOriginalFlushForTests = _ => { entered.TrySetResult(true); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        OutputMeta? callback = null; var callbacks = 0; var ended = 0;
        backend.OnNaturalExit((_, m) => { callback = m; Interlocked.Increment(ref callbacks); }); backend.CaptureEnded += _ => Interlocked.Increment(ref ended);
        var owner = Task.Run(() => { if (naturalOwner) { video.Inner.EmitNaturalExit(0, ""); return backend.LastMeta!; } return backend.Stop(); });
        Task<OutputMeta>[] waiters = Array.Empty<Task<OutputMeta>>(); Task? competitor = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiters = Enumerable.Range(0, 3).Select(_ => Task.Run(backend.Stop)).ToArray();
            if (!naturalOwner) competitor = Task.Run(() => video.Inner.EmitNaturalExit(0, ""));
            Assert.Equal(1, publisher.Calls); Assert.False(owner.IsCompleted);
        }
        finally { release.Set(); }
        var result = await owner.WaitAsync(TimeSpan.FromSeconds(5)); var results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5)); if (competitor is not null) await competitor.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var m in results) Assert.Same(result, m); if (naturalOwner) Assert.Same(callback, result);
        Assert.Equal(naturalOwner ? 1 : 0, callbacks); Assert.Equal(1, ended); Successful(f, backend, result, factory, publisher, ops, runner);
    }
    [Theory]
    [InlineData("abort")] [InlineData("cancel")] [InlineData("dispose")]
    public async Task LifecycleCancellationDuringRealCopyProtectsOldFinalAndConvergesWaiters(string signal)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var video = new Video(f.Fixture); var factory = new Factory(video); var ops = new Operations();
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); var runner = new FakeExternalProcessRunner();
        using var backend = Backend(f, factory, publisher, runner); Start(backend, Config(f));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.BeforeOriginalFlushForTests = _ => { entered.TrySetResult(true); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var owner = Task.Run(backend.Stop); Task? cancellation = null; Task<OutputMeta>[] waiters = Array.Empty<Task<OutputMeta>>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiters = Enumerable.Range(0, 3).Select(_ => Task.Run(backend.Stop)).ToArray();
            cancellation = Task.Run(() => { if (signal == "abort") backend.Abort(CaptureAbortReason.DisplayUnavailable); else if (signal == "cancel") backend.Cancel(); else backend.Dispose(); });
            Assert.True(SpinWait.SpinUntil(() => publisher.PublicationToken.IsCancellationRequested, TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); }
        var result = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var m in await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5))) Assert.Same(result, m);
        if (cancellation is not null) await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        Failed(backend, result, f.Final); OldFinal(f, old); Assert.Equal(1, publisher.Calls); Assert.Equal(0, ops.Renames); Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed);
        Assert.False(File.Exists(video.OutputPath)); Assert.Equal(File.ReadAllBytes(f.Fixture), File.ReadAllBytes(f.Retained));
        Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4")); Assert.Equal(0, factory.AudioCalls); Assert.Equal(0, runner.RunCallCount);
    }
    [Theory]
    [InlineData("invalid_video")] [InlineData("missing_video")] [InlineData("copy")] [InlineData("flush")]
    [InlineData("native_rename")] [InlineData("cancel_publish")] [InlineData("closed_gate")]
    [InlineData("storage_before")] [InlineData("storage_copy")] [InlineData("capacity")]
    [InlineData("abort")] [InlineData("cancel")]
    public async Task ProductFailuresProtectOldValidMovieAndRetainOnlyOwnedDiagnostics(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var video = new Video(f.Fixture); var factory = new Factory(video); var ops = new Operations();
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); var runner = new FakeExternalProcessRunner();
        using var backend = Backend(f, factory, publisher, runner); var capacity = new Capacity(); var cfg = Config(f);
        using var safety = new WindowStorageSafety(new(f.Videos, Path.Combine(f.Root, "temp"), f.Root), 1, capacity); cfg.StorageSafety = safety;
        Start(backend, cfg); using var cancellation = new CancellationTokenSource(); var gate = new FileCommitGate(); FileStream? denied = null;
        if (fault == "invalid_video") File.WriteAllText(video.OutputPath!, "invalid silent media");
        if (fault == "missing_video") File.Delete(video.OutputPath!);
        var sourceBytes = File.Exists(video.OutputPath) ? File.ReadAllBytes(video.OutputPath!) : null;
        var sourceId = File.Exists(video.OutputPath) ? Identity(video.OutputPath!) : null;
        if (fault == "storage_before") safety.TryAbort(WindowStorageSafety.LowSpaceCode);
        if (fault == "capacity") capacity.Available = 0;
        if (fault == "copy") native.OriginalCopyCreatedForTests = _ => denied = new FileStream(video.OutputPath!, FileMode.Open, FileAccess.Read, FileShare.None);
        publisher.After = _ => { denied?.Dispose(); denied = null; };
        if (fault == "cancel_publish") publisher.ExtraToken = cancellation.Token;
        if (fault == "closed_gate") publisher.Gate = gate;
        native.BeforeOriginalFlushForTests = _ =>
        {
            if (fault == "flush") throw new IOException("PRIVATE flush exception");
            if (fault == "cancel_publish") cancellation.Cancel();
            if (fault == "closed_gate") gate.Close();
            if (fault == "storage_copy") safety.TryAbort(WindowStorageSafety.LowSpaceCode);
        };
        if (fault == "native_rename") native.BeforeOriginalCommitForTests = () => denied = new FileStream(f.Final, FileMode.Open, FileAccess.Read, FileShare.Read);
        OutputMeta meta;
        try
        {
            if (fault == "abort") meta = backend.Abort(CaptureAbortReason.DisplayUnavailable);
            else { if (fault == "cancel") backend.Cancel(); meta = backend.Stop(); }
        }
        finally { denied?.Dispose(); }
        Failed(backend, meta, f.Final); OldFinal(f, old); Assert.Equal(0, runner.RunCallCount); Assert.Equal(0, factory.AudioCalls);
        if (fault is "invalid_video" or "missing_video" or "storage_before" or "capacity" or "abort" or "cancel") Assert.Equal(0, publisher.Calls); else Assert.Equal(1, publisher.Calls);
        if (fault is not ("missing_video" or "cancel"))
        { Assert.True(File.Exists(f.Retained)); Assert.Equal(sourceBytes, File.ReadAllBytes(f.Retained)); Assert.Equal(sourceId, Identity(f.Retained)); Assert.False(File.Exists(video.OutputPath)); }
        else Assert.False(File.Exists(video.OutputPath));
        if (ops.Original is not null) { Assert.True(ops.Original.IsClosed); Assert.Equal(1, ops.Deletes); }
        Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4")); using var exclusive = new FileStream(f.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData("unsupported")] [InlineData("identity")] [InlineData("after_basic")] [InlineData("throw")]
    public async Task OptionalIdentityFailureStillCompletesVideoOnlyProductAndCleansTemp(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var video = new Video(f.Fixture); var factory = new Factory(video); var ops = new Operations();
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: new PublicationIdentityAnchorTests.Native { Fault = fault }, publicationFiles: ops);
        var publisher = new Publisher(native); var runner = new FakeExternalProcessRunner(); using var backend = Backend(f, factory, publisher, runner); Start(backend, Config(f));
        var result = backend.Stop(); Assert.True(result.OutputFileExists); Assert.Equal(f.Final, result.OutputPath); Assert.Equal("not_requested", result.AudioStatus); Assert.Equal("not_checked", result.AudioContinuityStatus);
        Assert.Null(result.PublicationReceipt); Assert.False(result.HasAudioStream); Assert.Equal(new FileInfo(f.Fixture).Length, result.SizeBytes); Assert.NotEqual(old, Identity(f.Final));
        Assert.False(File.Exists(video.OutputPath)); Assert.Equal(0, runner.RunCallCount); Assert.Equal(0, factory.AudioCalls); Assert.Equal(1, publisher.Calls); Assert.True(ops.Original!.IsClosed);
    }
    [Theory]
    [InlineData("throw")] [InlineData("invalid")]
    public async Task PostCommitProbeFailureKeepsCommittedMovieAndRetainsSourceWithoutSuccessAnchor(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var video = new Video(f.Fixture); var factory = new Factory(video); var ops = new Operations();
        var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); var runner = new FakeExternalProcessRunner(); using var backend = Backend(f, factory, publisher, runner);
        backend.VideoOnlyOutputProber = new Prober((p, call) => { if (call == 2 && fault == "throw") throw new IOException("PRIVATE final probe exception"); var m = new FfmpegOutputProber().Probe(p); if (call == 2) m.DurationSeconds = 0; return m; });
        Start(backend, Config(f)); var meta = backend.Stop(); Failed(backend, meta, f.Final);
        Assert.NotEqual(old, Identity(f.Final)); Assert.Equal(ops.OriginalId, Identity(f.Final)); Assert.Equal(File.ReadAllBytes(f.Fixture), File.ReadAllBytes(f.Final));
        Assert.True(File.Exists(f.Retained)); Assert.Equal(File.ReadAllBytes(f.Fixture), File.ReadAllBytes(f.Retained)); Assert.Equal(1, publisher.Calls); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed);
    }
    private sealed class Audio(string fixture, bool disappearLate) : IAudioCaptureWorker, IAudioHelperSummaryProvider
    {
        private readonly FakeAudioCaptureWorker _inner = new(raiseAudioReadyOnStart: true);
        internal bool RemovedAfterExitAndStability;
        public event Action? AudioReady { add => _inner.AudioReady += value; remove => _inner.AudioReady -= value; }
        public event Action<int, string>? NaturalExit { add => _inner.NaturalExit += value; remove => _inner.NaturalExit -= value; }
        public DateTime? ReadyAtUtc => _inner.ReadyAtUtc; public long MediaStartAnchorTicks => _inner.MediaStartAnchorTicks;
        public string? OutputPath => _inner.OutputPath; public int ExitCode => _inner.ExitCode; public bool HasExited => _inner.HasExited; public bool IsAudioReady => _inner.IsAudioReady; public long RuntimeAudioLostAtMs => 0;
        public void Start(CaptureConfig cfg, string path) { _inner.Start(cfg, path); if (disappearLate) File.Copy(fixture, path, overwrite: true); }
        public void Stop() => _inner.Stop(); public bool WaitForExit(TimeSpan timeout) => _inner.WaitForExit(timeout);
        public string GetStderrLog()
        { if (disappearLate && !RemovedAfterExitAndStability) { Assert.True(_inner.WaitForExitCalled); Assert.True(HasExited); using (var read = new FileStream(OutputPath!, FileMode.Open, FileAccess.Read, FileShare.None)) Assert.True(read.Length > 0); File.Delete(OutputPath!); RemovedAfterExitAndStability = true; } return ""; }
        public void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? p) => _inner.SetMicrophoneStatusProvider(p);
        public AudioHelperSessionSummary? GetTerminalSummary() => null;
        public void Dispose() => _inner.Dispose();
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task RequestedMicrophoneOrLoopbackMissingBeforeOrAfterWavStabilityNeverDowngrades(bool loopback, bool late)
    {
        using var f = new Files(); await f.Generate(); await f.GenerateAudio(); var old = Identity(f.Final); var video = new Video(f.Fixture); var audio = new Audio(f.Audio, late); var factory = new Factory(video, audio);
        var ops = new Operations(); var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); var runner = new FakeExternalProcessRunner(); using var backend = Backend(f, factory, publisher, runner);
        var silentProber = new Prober((_, _) => throw new InvalidOperationException("Requested audio must not enter None")); backend.VideoOnlyOutputProber = silentProber;
        Start(backend, Config(f, loopback ? AudioCaptureSourceKind.SystemLoopback : AudioCaptureSourceKind.Microphone));
        var result = backend.Stop(); Failed(backend, result, f.Final); OldFinal(f, old);
        Assert.Equal(late, audio.RemovedAfterExitAndStability); Assert.Equal(0, silentProber.Calls); Assert.Equal(0, publisher.Calls); Assert.Equal(0, runner.RunCallCount);
        Assert.NotEqual("not_requested", result.AudioStatus); Assert.Equal(loopback ? "system-loopback" : "microphone", result.AudioSourceKind); Assert.True(File.Exists(f.Retained));
        Assert.Contains(result.Warnings, w => w.Contains(late ? "missing_audio_track" : "wav_file_not_stable", StringComparison.Ordinal));
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode => "tray"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => Confirm = callback;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException("No selection UI");
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { } public void SetAllIdle() { } public void ShowError(string text) { }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FormalAvSplitCoreTerminalWriterAndDefaultRetentionUseOriginalPublication(bool failProbe)
    {
        using var f = new Files(); await f.Generate(); var prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE"); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0) });
        var store = new SqliteOperationalStore(Path.Combine(f.Root, "core.db")); store.Initialize(); using var writer = new SqliteOrdinaryRecordingHistoryStore(store); var tray = new Tray(); var video = new Video(f.Fixture); var factory = new Factory(video);
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations()); var publisher = new Publisher(native); var runner = new FakeExternalProcessRunner(); using var backend = Backend(f, factory, publisher, runner);
        if (failProbe) backend.VideoOnlyOutputProber = new Prober((p, call) => { var m = new FfmpegOutputProber().Probe(p); if (call == 2) m.DurationSeconds = 0; return m; });
        using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit()); engine.SetTray(tray); engine.ConfigureOrdinaryRecordingHistory(writer); engine.BackendFactory = _ => (backend, "fake");
        try
        {
            var request = new JsonObject { ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 }, ["duration_seconds"] = 10, ["countdown_seconds"] = 0,
                ["output"] = new JsonObject { ["directory"] = f.Videos, ["filename"] = "core.mp4", ["conflict_policy"] = "rename" } };
            engine.CreateRecording(request, "task327-synthetic", tray); Assert.NotNull(tray.Confirm); tray.Confirm!(ConfirmationDecision.Approve());
            // The factory seam selects an otherwise unselected display/None split
            // backend. Core performs its approved Start; the test supplies the
            // split start action without changing engine selection/countdown.
            Assert.True(SpinWait.SpinUntil(() => backend.TempVideoPath is not null, TimeSpan.FromSeconds(5)));
            backend.StartVideo(); await video.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rec = Assert.Single(engine._recs.Values); Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(5))); engine.Stop(rec.Id, "user");
            var row = writer.Read(rec.Id)!;
            using var c = store.OpenReadOnlyConnection(); using var command = c.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM ordinary_publication_anchors;";
            Assert.Equal(failProbe ? 0L : 1L, command.ExecuteScalar()); Assert.Null(row.PublicationReceipt); Assert.Equal(1, publisher.Calls); Assert.Equal(0, factory.AudioCalls); Assert.Equal(0, runner.RunCallCount);
            if (failProbe) { Assert.Equal(RecState.failed, rec.State); Assert.Equal("failed", row.Status); Assert.Null(rec.LastMeta!.PublicationReceipt); Assert.False(rec.LastMeta.OutputFileExists); return; }
            Assert.Equal(RecState.completed, rec.State); Assert.Equal("completed", row.Status); var receipt = Assert.IsType<PublicationReceipt>(rec.LastMeta!.PublicationReceipt);
            Assert.Equal(Identity(row.OutputPath), receipt.Anchor.Identity); Assert.Equal(row.OutputSizeBytes, receipt.Anchor.Length); Assert.False(File.Exists(backend.TempVideoPath));
            var service = new RetentionPreviewQueryService(new UnifiedHistoryQueryService(store, writer), f.Root); var before = DateTimeOffset.UtcNow;
            Assert.Equal("age_rule_match", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            var time = File.GetLastWriteTimeUtc(row.OutputPath); File.Move(row.OutputPath, Path.Combine(f.Videos, "held-original.mp4")); File.Copy(f.Fixture, row.OutputPath); File.SetLastWriteTimeUtc(row.OutputPath, time);
            Assert.Equal(receipt.Anchor.Length, new FileInfo(row.OutputPath).Length); Assert.Equal(time, File.GetLastWriteTimeUtc(row.OutputPath)); Assert.NotEqual(receipt.Anchor.Identity, Identity(row.OutputPath));
            Assert.Equal("publication_identity_mismatch", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            output.WriteLine($"Formal AV split -> Core completed -> SQLite private anchor -> default retention: bytes={receipt.Anchor.Length}, original={receipt.Anchor.Identity}, replacement={Identity(row.OutputPath)}, copiedLastWrite={time:O}; age_rule_match -> publication_identity_mismatch.");
        }
        finally { engine.Dispose(); writer.Dispose(); SqliteConnection.ClearAllPools(); SystemQuery.SetDisplayProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", prior); }
    }
}
