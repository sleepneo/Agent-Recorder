using System.Text.Json.Nodes;
using System.Globalization;
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
public sealed class FfmpegVideoOnlyPublicationTests(ITestOutputHelper output)
{
    private sealed class Files : IDisposable
    {
        private static readonly string TaskRoot = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task328"));
        internal readonly string Root = Path.Combine(TaskRoot, "video-" + Guid.NewGuid().ToString("N"));
        internal string Videos => Path.Combine(Root, "Videos");
        internal string Fixture => Path.Combine(Root, "fixture.mp4");
        internal string Final => Path.Combine(Videos, "final.mp4");
        private readonly string? _previousData = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        internal byte[] Bytes = Array.Empty<byte>();
        internal Files() { Directory.CreateDirectory(Videos); DataDirResolver.SetOverride(Root); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root); }
        internal async Task Generate()
        {
            await GenerateMedia(new[] { "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=15", "-t", "1", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", Fixture });
            File.Copy(Fixture, Final); Bytes = File.ReadAllBytes(Final);
            File.WriteAllText(Path.Combine(Videos, "user-sentinel.txt"), "unchanged");
        }
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
    private sealed class Operations(bool replace) : IPublicationFileOperations
    {
        internal SafeFileHandle? Original;
        internal string? OriginalPath;
        internal ObservedFileIdentity? OriginalId;
        internal int Renames, Deletes;
        public FileStream CreateNew(string path) { OriginalPath = path; var stream = WindowsPublicationFileOperations.Instance.CreateNew(path); Original = stream.SafeFileHandle; OriginalId = Identity(Original); return stream; }
        public void Rename(SafeFileHandle handle, string final) { Assert.Same(Original, handle); Renames++; (replace ? WindowsPublicationFileOperations.Instance : WindowsPublicationFileOperations.NoReplace).Rename(handle, final); }
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle handle) { Assert.Same(Original, handle); Deletes++; return WindowsPublicationFileOperations.Instance.DeleteOriginal(handle); }
    }

    private sealed class Publisher(IStagingToFinalPublisher inner) : IStagingToFinalPublisher
    {
        internal int Calls; internal CancellationToken Token; internal Action<PublishResult>? After; internal IFileCommitGate? ExtraGate;
        public async Task<PublishResult> PublishAsync(string source, string final, CancellationToken cancellationToken = default, IFileCommitGate? commitGate = null)
        {
            Interlocked.Increment(ref Calls); Token = cancellationToken;
            var result = await inner.PublishAsync(source, final, cancellationToken, ExtraGate ?? commitGate); After?.Invoke(result); return result;
        }
    }
    private sealed class Prober(Func<string, int, OutputMeta> read) : IOutputProber
    {
        private int _calls; public OutputMeta Probe(string path) => read(path, ++_calls);
    }
    private sealed class Capacity : IStorageCapacityProvider
    {
        internal long Available = 4L * 1024 * 1024 * 1024; internal Func<string, string> Volume = _ => "same-volume";
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token) => Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, Volume(p), Available)).ToArray());
    }
    private static ObservedFileIdentity Identity(string path) { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Identity(s.SafeFileHandle); }
    private static ObservedFileIdentity Identity(SafeFileHandle h) { Assert.True(WindowsFileMetadataApi.Instance.Identity(h, out var id, out _)); return new(id.VolumeSerialNumber, id.IdLow, id.IdHigh); }
    private static CaptureConfig Config(Files f, int seconds = 1, string policy = "overwrite") => new() { SourceKind = "region", Bounds = (0, 0, 64, 64), Fps = 15, DurationSeconds = seconds, OutputPath = f.Final, OutputConflictPolicy = policy };
    private static IReadOnlyList<string> Lavfi(CaptureConfig cfg, bool realtime = false, string? rate = null) => (realtime ? new[] { "-re" } : Array.Empty<string>()).Concat(new[] { "-f", "lavfi", "-t", (cfg.DurationSeconds ?? 20).ToString(CultureInfo.InvariantCulture), "-i", $"color=c=black:s=64x64:r={rate ?? cfg.Fps.ToString(CultureInfo.InvariantCulture)}" }).ToArray();
    private static FfmpegCaptureBackend Backend(Publisher publisher) => new() { VideoInputArgumentsForTests = cfg => Lavfi(cfg), VideoOnlyPublisherFactory = _ => publisher };
    private static void Start(FfmpegCaptureBackend backend, CaptureConfig cfg) => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend, cfg);
    private static string Retained(Files f, string stage) => Path.Combine(f.Root, "failed", Path.GetFileName(Path.GetDirectoryName(stage))![".agent-recorder-".Length..], "video.mp4");
    private static void OldFinal(Files f, ObservedFileIdentity old) { Assert.Equal(old, Identity(f.Final)); Assert.Equal(f.Bytes.Length, new FileInfo(f.Final).Length); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Final)); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(f.Videos, "user-sentinel.txt"))); }
    private static void Failed(FfmpegCaptureBackend backend, OutputMeta m, string final)
    {
        Assert.Same(m, backend.LastMeta); Assert.Equal(final, m.OutputPath); Assert.False(m.OutputFileExists); Assert.Equal(0, m.SizeBytes); Assert.Null(m.PublicationReceipt); Assert.Null(m.QuantizedTailEvidence);
        var error = (m.StderrLog ?? "") + string.Join(";", m.Warnings); Assert.DoesNotContain("PRIVATE", error); Assert.DoesNotContain("capture_video.mp4", error); Assert.DoesNotContain(".publish-tmp-", error); Assert.DoesNotContain(".agent-recorder-", error);
    }
    private static void Success(Files f, FfmpegCaptureBackend backend, OutputMeta m, Publisher publisher, Operations ops)
    {
        Assert.Same(m, backend.LastMeta); Assert.True(m.OutputFileExists, m.StderrLog); Assert.Equal(f.Final, m.OutputPath); Assert.Equal(0, backend.ExitCode);
        Assert.Equal("not_requested", m.AudioStatus); Assert.Equal("not_checked", m.AudioContinuityStatus); Assert.Equal("none", m.AudioSourceKind); Assert.False(m.HasAudioStream); Assert.Null(m.AudioCodec);
        Assert.Equal(64, m.Width); Assert.Equal(64, m.Height); Assert.Equal(15, m.Fps); Assert.Equal("h264", m.Codec); Assert.Equal("mp4", m.Container); Assert.True(m.DurationSeconds > 0); Assert.Single(m.ProbeStreams);
        var receipt = Assert.IsType<PublicationReceipt>(m.PublicationReceipt); Assert.Equal(ops.OriginalId, receipt.Anchor.Identity); Assert.Equal(Identity(f.Final), receipt.Anchor.Identity); Assert.Equal(m.SizeBytes, receipt.Anchor.Length); Assert.Equal(new FileInfo(f.Final).Length, receipt.Anchor.Length); Assert.Equal(f.Final, receipt.Anchor.Path);
        Assert.Equal(1, publisher.Calls); Assert.True(ops.Original!.IsClosed); Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes);
        Assert.False(File.Exists(backend.VideoOnlyStagingPath)); Assert.False(Directory.Exists(Path.GetDirectoryName(backend.VideoOnlyStagingPath))); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
        using var exclusive = new FileStream(f.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealStartNaturalOrUserStopWritesOwnedStageThenPublishesFinalOnce(bool manual)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var cfg = Config(f, manual ? 20 : 2); var ops = new Operations(true);
        var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); using var backend = Backend(publisher);
        backend.VideoInputArgumentsForTests = c => Lavfi(c, realtime: true); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var callback = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously); var frames = 0; var callbacks = 0;
        backend.FirstFrameObserved += _ => { frames++; first.TrySetResult(); }; backend.OnNaturalExit((_, m) => { Assert.True(first.Task.IsCompleted); callbacks++; callback.TrySetResult(m); });
        Start(backend, cfg); await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); OldFinal(f, old); Assert.Equal(f.Final, cfg.OutputPath);
        Assert.Equal(backend.VideoOnlyStagingPath, backend.ExecutedArguments![^1]); Assert.NotEqual(f.Final, backend.ExecutedArguments[^1]); Assert.Equal(f.Videos, Path.GetDirectoryName(Path.GetDirectoryName(backend.VideoOnlyStagingPath))); Assert.Contains("lavfi", backend.ExecutedArguments); Assert.DoesNotContain("gdigrab", backend.ExecutedArguments);
        cfg.CommandArgs = "PRIVATE diagnostic is never an execution source";
        var result = manual ? await Task.Run(backend.Stop).WaitAsync(TimeSpan.FromSeconds(10)) : await callback.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Success(f, backend, result, publisher, ops); Assert.Equal(1, frames); Assert.Equal(manual ? 0 : 1, callbacks); Assert.NotEqual(old, Identity(f.Final)); if (manual) Assert.True(result.DurationSeconds < cfg.DurationSeconds);
        output.WriteLine($"Real backend Start/{(manual ? "Stop" : "natural")}: child output={backend.VideoOnlyStagingPath}; approved={cfg.OutputPath}; original={ops.OriginalId}; receipt={result.PublicationReceipt!.Anchor.Identity}; bytes={result.SizeBytes}; duration={result.DurationSeconds}; H264/MP4 64x64/15fps silent; source cleaned, frame before terminal.");
    }
    [Theory]
    [InlineData("waiters")] [InlineData("abort")] [InlineData("dispose")] [InlineData("storage")]
    public async Task NaturalOwnerManualWaitersAndLifecycleCancellationAtFlushShareCanonicalResult(string signal)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var cfg = Config(f); var ops = new Operations(true); var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); using var backend = Backend(publisher);
        using var safety = new WindowStorageSafety(new(f.Videos, null, Path.Combine(f.Root, "wgc")), 1, new Capacity()); cfg.StorageSafety = safety;
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var callback = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously); var callbacks = 0;
        native.BeforeOriginalFlushForTests = _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }; backend.OnNaturalExit((_, m) => { callbacks++; callback.TrySetResult(m); });
        Start(backend, cfg); Task? cancellation = null; Task<OutputMeta>[] waiters = Array.Empty<Task<OutputMeta>>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); waiters = Enumerable.Range(0, 3).Select(_ => Task.Run(backend.Stop)).ToArray();
            if (signal != "waiters") { cancellation = Task.Run(() => { if (signal == "abort") backend.Abort(CaptureAbortReason.DisplayUnavailable); else if (signal == "dispose") backend.Dispose(); else safety.TryAbort(WindowStorageSafety.LowSpaceCode); }); Assert.True(SpinWait.SpinUntil(() => publisher.Token.IsCancellationRequested, TimeSpan.FromSeconds(3))); }
        }
        finally { release.Set(); }
        var result = await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)); foreach (var w in await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5))) Assert.Same(result, w); if (cancellation is not null) await cancellation.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, callbacks);
        if (signal == "waiters") { Success(f, backend, result, publisher, ops); Assert.True(safety.Committed); }
        else { Failed(backend, result, f.Final); OldFinal(f, old); Assert.False(safety.Committed); Assert.Equal(0, ops.Renames); Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed); Assert.True(File.Exists(Retained(f, backend.VideoOnlyStagingPath!))); }
        Assert.Equal(1, publisher.Calls);
    }
    [Theory]
    [InlineData("manual")] [InlineData("abort")] [InlineData("dispose")]
    public async Task ManualOwnerSuppressesNaturalCallbackAndSharesTerminalWithOtherStops(string signal)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var ops = new Operations(true); var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); using var backend = Backend(publisher);
        backend.VideoInputArgumentsForTests = cfg => Lavfi(cfg, true); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var callbacks = 0; backend.FirstFrameObserved += _ => first.TrySetResult(); backend.OnNaturalExit((_, _) => callbacks++);
        Start(backend, Config(f, 20)); await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.BeforeOriginalFlushForTests = _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }; var owner = Task.Run(backend.Stop); Task? cancel = null; Task<OutputMeta>[] waiters = Array.Empty<Task<OutputMeta>>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); waiters = Enumerable.Range(0, 3).Select(_ => Task.Run(backend.Stop)).ToArray();
            if (signal != "manual") { cancel = Task.Run(() => { if (signal == "abort") backend.Abort(CaptureAbortReason.DisplayUnavailable); else backend.Dispose(); }); Assert.True(SpinWait.SpinUntil(() => publisher.Token.IsCancellationRequested, TimeSpan.FromSeconds(3))); }
        }
        finally { release.Set(); }
        var result = await owner.WaitAsync(TimeSpan.FromSeconds(5)); foreach (var m in await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5))) Assert.Same(result, m); if (cancel is not null) await cancel.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, callbacks); Assert.Equal(1, publisher.Calls);
        if (signal == "manual") Success(f, backend, result, publisher, ops); else { Failed(backend, result, f.Final); OldFinal(f, old); Assert.Equal(0, ops.Renames); Assert.True(ops.Original!.IsClosed); Assert.True(File.Exists(Retained(f, backend.VideoOnlyStagingPath!))); }
    }
    [Theory]
    [InlineData("invalid")] [InlineData("missing")] [InlineData("zero")] [InlineData("encoder")]
    [InlineData("copy")] [InlineData("flush")] [InlineData("native")] [InlineData("gate")] [InlineData("capacity")]
    public async Task ProductFailureNeverDeletesOldValidFinalAndRetainsOnlyOwnedDiagnostics(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var cfg = Config(f); var ops = new Operations(true); var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); using var backend = Backend(publisher);
        var capacity = new Capacity(); using var safety = new WindowStorageSafety(new(f.Videos, null, Path.Combine(f.Root, "wgc")), 1, capacity); cfg.StorageSafety = safety;
        FileStream? denied = null; var gate = new FileCommitGate(); byte[]? staged = null; ObservedFileIdentity? stagedId = null;
        if (fault == "encoder") backend.VideoInputArgumentsForTests = _ => new[] { "-f", "lavfi", "-i", "invalid_private_filter" };
        backend.BeforeVideoOnlyFinalizeForTests = p => { if (fault == "invalid") File.WriteAllText(p, "invalid video"); if (fault == "missing") File.Delete(p); if (fault == "zero") File.WriteAllBytes(p, Array.Empty<byte>()); if (File.Exists(p)) { staged = File.ReadAllBytes(p); stagedId = Identity(p); } if (fault == "capacity") capacity.Available = WindowStorageSafety.LowWaterBytes; };
        if (fault == "copy") native.OriginalCopyCreatedForTests = _ => denied = new FileStream(backend.VideoOnlyStagingPath!, FileMode.Open, FileAccess.Read, FileShare.None);
        if (fault == "flush") native.BeforeOriginalFlushForTests = _ => throw new IOException("PRIVATE native flush error");
        if (fault == "native") native.BeforeOriginalCommitForTests = () => denied = new FileStream(f.Final, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fault == "gate") { publisher.ExtraGate = gate; native.BeforeOriginalFlushForTests = _ => gate.Close(); }
        publisher.After = _ => { denied?.Dispose(); denied = null; };
        try { Start(backend, cfg); var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(10)); Failed(backend, result, f.Final); }
        finally { denied?.Dispose(); }
        OldFinal(f, old); Assert.Equal(fault is "copy" or "flush" or "native" or "gate" ? 1 : 0, publisher.Calls);
        var retained = Retained(f, backend.VideoOnlyStagingPath!); if (staged is not null) { Assert.Equal(staged, File.ReadAllBytes(retained)); Assert.Equal(stagedId, Identity(retained)); } else Assert.False(File.Exists(retained));
        Assert.False(File.Exists(backend.VideoOnlyStagingPath)); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4")); if (ops.Original is not null) { Assert.True(ops.Original.IsClosed); Assert.Equal(1, ops.Deletes); }
    }
    [Theory]
    [InlineData("launch")] [InlineData("directory_collision")] [InlineData("admission")]
    public async Task StartupFailureAndAbortPreserveOldFinalAndNeverOwnOtherStaging(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var cfg = Config(f); var publisher = new Publisher(StagingToFinalPublisher.Instance); using var backend = Backend(publisher); string? foreign = null;
        using var safety = new WindowStorageSafety(new(f.Videos, null, Path.Combine(f.Root, "wgc")), 1, new Capacity { Available = WindowStorageSafety.LowWaterBytes });
        if (fault == "launch") backend.ConfigureProcessForTests = p => p.FileName = Path.Combine(f.Root, "absent-encoder.exe");
        if (fault == "directory_collision") backend.BeforeStagingCreateForTests = p => { foreign = p; Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "unrelated.txt"), "keep"); };
        if (fault == "admission") cfg.StorageSafety = safety;
        var error = Record.Exception(() => Start(backend, cfg)); Assert.NotNull(error); Assert.DoesNotContain("absent-encoder", error.Message);
        var meta = backend.Abort(CaptureAbortReason.DisplayUnavailable); Failed(backend, meta, f.Final); OldFinal(f, old); Assert.Equal(0, publisher.Calls);
        if (foreign is not null) Assert.Equal("keep", File.ReadAllText(Path.Combine(foreign, "unrelated.txt")));
    }
    [Theory]
    [InlineData("fail", "existing")] [InlineData("fail_if_exists", "created")] [InlineData("rename", "replaced")]
    public async Task NativeNoReplaceProtectsTargetAtCommitBoundaryWithoutFallback(string policy, string situation)
    {
        using var f = new Files(); await f.Generate(); var cfg = Config(f, policy: policy); ObservedFileIdentity? protectedId = Identity(f.Final); var ops = new Operations(false); var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var publisher = new Publisher(native); using var backend = Backend(publisher);
        if (situation != "existing") File.Move(f.Final, Path.Combine(f.Videos, "held-old.mp4"));
        native.BeforeOriginalCommitForTests = () => { if (situation == "created") File.Copy(f.Fixture, f.Final); if (situation == "replaced") { File.Copy(f.Fixture, f.Final); File.Move(f.Final, Path.Combine(f.Videos, "held-interloper.mp4")); File.Copy(f.Fixture, f.Final); } protectedId = Identity(f.Final); };
        backend.VideoOnlyPublisherFactory = replace => { Assert.False(replace); return publisher; }; Start(backend, cfg);
        var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5)); Failed(backend, result, f.Final); OldFinal(f, protectedId!);
        Assert.Equal(1, publisher.Calls); Assert.Equal(1, ops.Renames); Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed); Assert.True(File.Exists(Retained(f, backend.VideoOnlyStagingPath!))); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4")); Assert.Equal(f.Final, cfg.OutputPath);
    }
    [Fact]
    public async Task NoReplaceCleanupDeletesRenamedOriginalCopyAndKeepsForeignOldName()
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var ops = new Operations(false);
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        var publisher = new Publisher(native); using var backend = Backend(publisher); string? originalName = null; string? moved = null;
        native.BeforeOriginalFlushForTests = stream =>
        {
            originalName = ops.OriginalPath!; moved = Path.Combine(f.Videos, "moved-original-copy.mp4");
            File.Move(originalName, moved); File.WriteAllText(originalName, "FOREIGN-KEEP");
        };
        Start(backend, Config(f, policy: "fail")); var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Failed(backend, result, f.Final); OldFinal(f, old); Assert.False(File.Exists(moved)); Assert.Equal("FOREIGN-KEEP", File.ReadAllText(originalName!));
        Assert.Equal(1, ops.Renames); Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed);
    }

    [Theory]
    [InlineData("rename")] [InlineData("fail")] [InlineData("fail_if_exists")] [InlineData("overwrite")]
    public async Task DefaultPublisherHonorsNoReplaceAndOverwriteWithoutTestFactory(string policy)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); using var backend = new FfmpegCaptureBackend { VideoInputArgumentsForTests = cfg => Lavfi(cfg) }; Start(backend, Config(f, policy: policy));
        var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        if (policy == "overwrite") { Assert.True(result.OutputFileExists); Assert.NotNull(result.PublicationReceipt); Assert.NotEqual(old, Identity(f.Final)); }
        else { Failed(backend, result, f.Final); OldFinal(f, old); }
    }
    [Theory]
    [InlineData("rename")] [InlineData("fail")] [InlineData("fail_if_exists")]
    public async Task DefaultNoReplacePublishesAtExactlyApprovedAbsentTarget(string policy)
    {
        using var f = new Files(); await f.Generate(); File.Move(f.Final, Path.Combine(f.Videos, "held-old.mp4"));
        using var backend = new FfmpegCaptureBackend { VideoInputArgumentsForTests = cfg => Lavfi(cfg) };
        var cfg = Config(f, policy: policy); Start(backend, cfg);
        var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OutputFileExists, result.StderrLog); var receipt = Assert.IsType<PublicationReceipt>(result.PublicationReceipt);
        Assert.Equal(f.Final, cfg.OutputPath); Assert.Equal(f.Final, receipt.Anchor.Path); Assert.Equal(Identity(f.Final), receipt.Anchor.Identity);
        Assert.False(File.Exists(backend.VideoOnlyStagingPath)); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
    }
    [Fact]
    public async Task KilledRealEncoderCannotPublishValidPartialOrDeleteOldFinal()
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var publisher = new Publisher(StagingToFinalPublisher.Instance);
        using var backend = Backend(publisher); backend.VideoInputArgumentsForTests = c => Lavfi(c, true);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); backend.FirstFrameObserved += _ => first.TrySetResult();
        Start(backend, Config(f, 20)); await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); backend.KillVideoEncoderForTests();
        var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5)); Failed(backend, result, f.Final);
        OldFinal(f, old); Assert.Equal("encoder_failed", result.StopReason); Assert.Equal(0, publisher.Calls);
        Assert.True(File.Exists(Retained(f, backend.VideoOnlyStagingPath!))); Assert.False(File.Exists(backend.VideoOnlyStagingPath));
    }
    [Theory]
    [InlineData("unsupported")] [InlineData("identity")] [InlineData("after_basic")] [InlineData("throw")]
    public async Task OptionalIdentityFailureGenuineCommitRemainsUsableWithoutReceipt(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var ops = new Operations(true); var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: new PublicationIdentityAnchorTests.Native { Fault = fault }, publicationFiles: ops)); using var backend = Backend(publisher); Start(backend, Config(f));
        var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(result.OutputFileExists); Assert.Null(result.PublicationReceipt); Assert.NotEqual(old, Identity(f.Final)); Assert.Equal(new FileInfo(f.Final).Length, result.SizeBytes); Assert.False(result.HasAudioStream); Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed); Assert.False(File.Exists(backend.VideoOnlyStagingPath));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PostCommitProbeFailureDoesNotRollbackFinalOrIssueHistoryReceipt(bool throwProbe)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var ops = new Operations(true); var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); using var backend = Backend(publisher);
        backend.VideoOnlyProberForTests = new Prober((p, call) => { if (call == 2 && throwProbe) throw new IOException("PRIVATE final probe exception"); var m = FfmpegCaptureBackend.Probe(p); if (call == 2) m.DurationSeconds = 0; return m; });
        Start(backend, Config(f)); var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(5)); Failed(backend, result, f.Final); Assert.NotEqual(old, Identity(f.Final)); Assert.Equal(ops.OriginalId, Identity(f.Final)); Assert.True(FfmpegCaptureBackend.Probe(f.Final).DurationSeconds > 0); Assert.True(File.Exists(Retained(f, backend.VideoOnlyStagingPath!))); Assert.True(ops.Original!.IsClosed); Assert.Equal(0, ops.Deletes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinalPacketTailEvidenceIsRevalidatedAtApprovedPathAndRealOverrunRemainsUnproven(bool overrun)
    {
        using var f = new Files(); await f.Generate(); var cfg = Config(f, 10); cfg.Fps = 30; var ops = new Operations(true); var publisher = new Publisher(new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops)); using var backend = Backend(publisher);
        backend.VideoInputArgumentsForTests = c => Lavfi(c, rate: "88/3");
        backend.BeforeVideoOnlyFinalizeForTests = p => { File.Delete(p); QuantizedTailTestMedia.Generate(p, overrun ? 295 : 294, "88/3"); Assert.Null(FfmpegCaptureBackend.ProbeAuthorizedFixedRateCapture(p, cfg).QuantizedTailEvidence); };
        Start(backend, cfg); var result = await backend.VideoOnlyCompletion!.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(result.OutputFileExists, result.StderrLog); Assert.NotNull(result.PublicationReceipt); Assert.True(result.DurationSeconds > 10);
        if (overrun) Assert.Null(result.QuantizedTailEvidence);
        else { var proof = Assert.IsType<FfmpegQuantizedTailEvidence>(result.QuantizedTailEvidence); Assert.True(proof.Matches(result, f.Final, TimeSpan.FromSeconds(10))); Assert.False(proof.Matches(result, backend.VideoOnlyStagingPath!, TimeSpan.FromSeconds(10))); }
        output.WriteLine($"Final packet validation: approved={cfg.OutputPath}, bytes={result.SizeBytes}, duration={result.DurationSeconds:R}, frameCount={result.ProbeStreams.Single().FrameCount}, proof={!overrun}; staged-path proof rejected; post-exit packet fixture injection=true, overrun={overrun}.");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AdmissionAndRuntimeReservePeakOnOutputVolumeEvenWhenWgcIsElsewhere(bool sameVolume)
    {
        var paths = new CaptureWritePaths(@"D:\approved", null, @"C:\wgc"); var provider = new Capacity { Volume = p => sameVolume ? "one-volume" : p }; using var safety = new WindowStorageSafety(paths, 1, provider); var samples = await provider.QueryAsync(paths.Directories, default);
        var before = safety.AdmissionBudgets(samples); safety.ReserveFfmpegVideoOnlyPublication(); var after = safety.AdmissionBudgets(samples); var key = provider.Volume(paths.OutputDirectory); var estimate = WindowStorageSafety.VideoEstimate(1);
        Assert.Equal(before[key] + estimate, after[key]); Assert.Equal(WindowStorageSafety.LowWaterBytes + estimate * (sameVolume ? 3 : 2), after[key]);
        provider.Available = WindowStorageSafety.LowWaterBytes + estimate - 1; Assert.Equal(WindowStorageSafety.LowSpaceCode, await safety.CheckAsync(false)); provider.Available = WindowStorageSafety.LowWaterBytes + estimate; Assert.Null(await safety.CheckAsync(false)); safety.ReservePublicationCopy(estimate * 2); Assert.Equal(WindowStorageSafety.LowSpaceCode, await safety.CheckAsync(false));
    }
    [Fact]
    public void LegacyMicrophoneTypedAudioIntermediateAndScreenshotNeverEnterFinalNonePath()
    {
        var cfg = new CaptureConfig { Microphone = true, Bounds = (0, 0, 64, 64) }; Assert.False(cfg.AudioRequested); Assert.False(FfmpegCaptureBackend.UsesVideoOnlyPublication(cfg)); Assert.Contains("dshow", FfmpegCaptureBackend.BuildArgs(cfg));
        cfg.Microphone = false; cfg.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback; Assert.False(FfmpegCaptureBackend.UsesVideoOnlyPublication(cfg)); cfg.AudioSourceKind = AudioCaptureSourceKind.None; cfg.StorageIntermediatePublication = true; Assert.False(FfmpegCaptureBackend.UsesVideoOnlyPublication(cfg)); cfg.StorageIntermediatePublication = false; cfg.Mode = ScreenshotSeriesConfig.ModeName; Assert.False(FfmpegCaptureBackend.UsesVideoOnlyPublication(cfg));
    }
    [Fact]
    public void MissingAuthorizationCreatesNoOwnedStagingSideEffect()
    {
        using var f = new Files(); using var backend = new FfmpegCaptureBackend(); var cfg = Config(f); cfg.OutputPath = Path.Combine(f.Root, "not-created", "final.mp4"); Assert.Throws<ArgumentNullException>(() => backend.Start(cfg, null!)); Assert.False(Directory.Exists(Path.GetDirectoryName(cfg.OutputPath)));
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm; public string HostMode => "tray"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => Confirm = c;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { } public void SetAllIdle() { } public void ShowError(string text) { }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualBackendCoreOrdinaryTransactionAndDefaultNativeRetentionPreserveOriginalAnchor(bool failed)
    {
        using var f = new Files(); await f.Generate(); var prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE"); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, "synthetic-test-display:display_1", DisplayIdentityResolutionStatus.Resolved) });
        var store = new SqliteOperationalStore(Path.Combine(f.Root, "core.db")); store.Initialize(); using var writer = new SqliteOrdinaryRecordingHistoryStore(store); var tray = new Tray(); using var backend = new FfmpegCaptureBackend { VideoInputArgumentsForTests = c => Lavfi(c, true) };
        if (failed) backend.VideoOnlyProberForTests = new Prober((p, call) => { var m = FfmpegCaptureBackend.Probe(p); if (call == 2) m.DurationSeconds = 0; return m; });
        using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit()); engine.SetTray(tray); engine.ConfigureOrdinaryRecordingHistory(writer); engine.BackendFactory = _ => (backend, "ffmpeg"); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); backend.FirstFrameObserved += _ => first.TrySetResult();
        try
        {
            var request = new JsonObject { ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 }, ["duration_seconds"] = 20, ["countdown_seconds"] = 0, ["output"] = new JsonObject { ["directory"] = f.Videos, ["filename"] = "core.mp4", ["conflict_policy"] = "overwrite" } };
            engine.CreateRecording(request, "task328-synthetic", tray); Assert.NotNull(tray.Confirm); tray.Confirm!(ConfirmationDecision.Approve()); var rec = Assert.Single(engine._recs.Values);
            SpinWait.SpinUntil(() => first.Task.IsCompleted || rec.IsFinalized, TimeSpan.FromSeconds(5));
            Assert.True(first.Task.IsCompleted, $"state={rec.State}, error={rec.Error}, reason={rec.StopReason}, stage={backend.VideoOnlyStagingPath}, stderr={backend.GetStderrLog()}");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(5)), $"state={rec.State}, error={rec.Error}, reason={rec.StopReason}"); engine.Stop(rec.Id, "user"); var row = writer.Read(rec.Id)!;
            using var c = store.OpenReadOnlyConnection(); using var command = c.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM ordinary_publication_anchors;"; Assert.Equal(failed ? 0L : 1L, command.ExecuteScalar()); Assert.Null(row.PublicationReceipt);
            if (failed) { Assert.Equal(RecState.failed, rec.State); Assert.Equal("failed", row.Status); Assert.Null(rec.LastMeta!.PublicationReceipt); Assert.False(rec.LastMeta.OutputFileExists); return; }
            Assert.Equal(RecState.completed, rec.State); Assert.Equal("completed", row.Status); var receipt = Assert.IsType<PublicationReceipt>(rec.LastMeta!.PublicationReceipt); Assert.Equal(Identity(row.OutputPath), receipt.Anchor.Identity); Assert.Equal(row.OutputSizeBytes, receipt.Anchor.Length); Assert.False(File.Exists(backend.VideoOnlyStagingPath));
            var service = new RetentionPreviewQueryService(new UnifiedHistoryQueryService(store, writer), f.Root, TestRuntimeOutputUse.None); var before = DateTimeOffset.UtcNow; Assert.Equal("age_rule_match", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            var time = File.GetLastWriteTimeUtc(row.OutputPath); File.Move(row.OutputPath, Path.Combine(f.Videos, "held-original.mp4")); File.Copy(Path.Combine(f.Videos, "held-original.mp4"), row.OutputPath); File.SetLastWriteTimeUtc(row.OutputPath, time); Assert.Equal(receipt.Anchor.Length, new FileInfo(row.OutputPath).Length); Assert.Equal(time, File.GetLastWriteTimeUtc(row.OutputPath)); Assert.NotEqual(receipt.Anchor.Identity, Identity(row.OutputPath));
            Assert.Equal("publication_identity_mismatch", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            output.WriteLine($"Actual FFmpeg Start -> Core completed -> SQLite private anchor -> default retention: bytes={receipt.Anchor.Length}, original={receipt.Anchor.Identity}, replacement={Identity(row.OutputPath)}, copiedLastWrite={time:O}; age_rule_match -> publication_identity_mismatch.");
        }
        finally { engine.Dispose(); writer.Dispose(); SqliteConnection.ClearAllPools(); SystemQuery.SetDisplayTopologyProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", prior); }
    }
}
