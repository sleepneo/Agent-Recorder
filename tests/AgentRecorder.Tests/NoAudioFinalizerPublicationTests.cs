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
public sealed class NoAudioFinalizerPublicationTests(ITestOutputHelper output)
{
    private sealed class Files : IDisposable
    {
        private static readonly string TaskRoot = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task326"));
        internal readonly string Root = Path.Combine(TaskRoot, "silent-" + Guid.NewGuid().ToString("N"));
        internal string Videos => Path.Combine(Root, "Videos");
        internal string Source => Path.Combine(Root, "source.mp4");
        internal string Final => Path.Combine(Videos, "final.mp4");
        internal Files()
        {
            Assert.True(OperatingSystem.IsWindows());
            Directory.CreateDirectory(Videos);
            File.WriteAllBytes(Source, new byte[1024]);
            File.WriteAllBytes(Final, new byte[] { 5, 4, 3, 2, 1 });
        }
        internal async Task Generate()
        {
            var result = await new ExternalProcessRunner().RunAsync(
                Path.Combine(TestHelper.ProjectRoot, "tools/ffmpeg/bin/ffmpeg.exe"),
                new[] { "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=15", "-t", "1", "-an",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", Source }, TimeSpan.FromSeconds(30));
            Assert.Equal(0, result.ExitCode);
        }
        public void Dispose()
        {
            Assert.Equal(TaskRoot, Path.GetDirectoryName(Path.GetFullPath(Root)), ignoreCase: true);
            Assert.StartsWith(TaskRoot + Path.DirectorySeparatorChar, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, recursive: true);
        }
    }
    private sealed class Prober(Func<string, int, OutputMeta> read) : IOutputProber
    {
        internal int Calls;
        public OutputMeta Probe(string path) => read(path, ++Calls);
    }
    private sealed class Publisher(IStagingToFinalPublisher inner, IFileCommitGate? gate = null) : IStagingToFinalPublisher
    {
        internal int Calls;
        public Task<PublishResult> PublishAsync(string source, string final, CancellationToken token = default, IFileCommitGate? commitGate = null)
        { Calls++; return inner.PublishAsync(source, final, token, gate ?? commitGate); }
    }
    private sealed class Operations : IPublicationFileOperations
    {
        internal SafeFileHandle? Original;
        internal int Renames, Deletes;
        public FileStream CreateNew(string path)
        { var stream = WindowsPublicationFileOperations.Instance.CreateNew(path); Original = stream.SafeFileHandle; return stream; }
        public void Rename(SafeFileHandle handle, string final)
        { Assert.Same(Original, handle); Renames++; WindowsPublicationFileOperations.Instance.Rename(handle, final); }
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle handle)
        { Assert.Same(Original, handle); Deletes++; return WindowsPublicationFileOperations.Instance.DeleteOriginal(handle); }
    }
    private sealed class Capacity : IStorageCapacityProvider
    {
        internal long Available = 4L * 1024 * 1024 * 1024;
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
            => Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "synthetic-volume", Available)).ToArray());
    }
    private static OutputMeta Valid() => new()
    {
        OutputFileExists = true, SizeBytes = 1024, DurationSeconds = 1, Width = 64, Height = 64, Fps = 15,
        Container = "mp4", Codec = "h264", HasAudioStream = false,
        PublicationReceipt = PublicationReceipt.IssueForTests("PRIVATE-input.mp4", 12, new(1, 2, 3)),
        ProbeStreams = new[] { new ProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264", StartTimeSeconds = 0, DurationSeconds = 1 } }
    };
    private static ObservedFileIdentity Identity(string path) => WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Before!.Identity!;
    private static ObservedFileIdentity Identity(SafeFileHandle handle)
    {
        Assert.True(WindowsFileMetadataApi.Instance.Identity(handle, out var id, out _));
        return new(id.VolumeSerialNumber, id.IdLow, id.IdHigh);
    }
    private static Task<AvFinalizer.Result> Finalize(Files f, IStagingToFinalPublisher publisher, IOutputProber probe,
        CancellationToken token = default, WindowStorageSafety? safety = null)
        => new AvFinalizer(new ExternalProcessRunner(), TimeSpan.FromSeconds(5), publisher, probe).FinalizeAsync(
            f.Source, "PRIVATE-unused-audio.wav", f.Final, null, AudioCaptureSourceKind.None, true, null, null, null, token, safety);
    private static void Failure(AvFinalizer.Result result)
    {
        Assert.NotEqual(0, result.ExitCode); Assert.NotNull(result.Error); Assert.Null(result.Meta.PublicationReceipt);
        Assert.False(result.Meta.OutputFileExists); Assert.Null(result.Meta.OutputPath); Assert.Equal(0, result.Meta.SizeBytes);
        Assert.Equal("", result.Stderr); Assert.Equal("", result.Meta.StderrLog);
        var publicError = result.Error + string.Join(";", result.Meta.Warnings);
        Assert.DoesNotContain("PRIVATE", publicError); Assert.DoesNotContain(TestHelper.ProjectRoot, publicError);
    }
    private static void OldFinal(Files f, ObservedFileIdentity old)
    {
        Assert.Equal(old, Identity(f.Final)); Assert.Equal(5, new FileInfo(f.Final).Length);
        Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, File.ReadAllBytes(f.Final));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealSilentMp4CommitsOriginalHandleAndOnlyPublisherReceipt(bool storageGate)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        ObservedFileIdentity? original = null;
        publisher.OriginalCopyCreatedForTests = stream => original = Identity(stream.SafeFileHandle);
        var probe = new Prober((path, _) =>
        {
            var meta = new FfmpegOutputProber().Probe(path);
            meta.PublicationReceipt = Valid().PublicationReceipt; return meta;
        });
        using var safety = storageGate ? new WindowStorageSafety(new(f.Videos, null, f.Root), 1, new Capacity()) : null;
        if (safety is not null) Assert.Null(await safety.CheckAsync(true));
        var result = await Finalize(f, publisher, probe, safety: safety);
        Assert.True(result.ExitCode == 0, result.Error + " " + string.Join(";", result.Meta.Warnings));
        Assert.Equal(2, probe.Calls); Assert.Equal(f.Final, result.Meta.OutputPath); Assert.True(result.Meta.OutputFileExists);
        Assert.Equal("not_requested", result.Meta.AudioStatus); Assert.Equal("not_checked", result.Meta.AudioContinuityStatus);
        Assert.False(result.Meta.HasAudioStream); Assert.Null(result.Meta.AudioCodec); Assert.Single(result.Meta.ProbeStreams);
        Assert.Equal(64, result.Meta.Width); Assert.Equal(64, result.Meta.Height); Assert.Equal(15, result.Meta.Fps);
        Assert.Equal("mp4", result.Meta.Container); Assert.Equal("h264", result.Meta.Codec); Assert.InRange(result.Meta.DurationSeconds, .9, 1.1);
        var receipt = Assert.IsType<PublicationReceipt>(result.Meta.PublicationReceipt);
        Assert.Equal(original, receipt.Anchor.Identity); Assert.Equal(original, Identity(f.Final)); Assert.NotEqual(old, Identity(f.Final));
        Assert.Equal(f.Final, receipt.Anchor.Path); Assert.Equal(new FileInfo(f.Source).Length, receipt.Anchor.Length);
        Assert.Equal(receipt.Anchor.Length, result.Meta.SizeBytes); Assert.Equal(File.ReadAllBytes(f.Source), File.ReadAllBytes(f.Final));
        output.WriteLine($"Actual silent H264/MP4: gate={storageGate}, path={f.Final}, original={original}, receipt={receipt.Anchor.Identity}, bytes={receipt.Anchor.Length}, duration={result.Meta.DurationSeconds}, streams={result.Meta.ProbeStreams.Length}, audio={result.Meta.HasAudioStream}");
        Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed);
        Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
        if (safety is not null) Assert.False(safety.TryAbort(WindowStorageSafety.LowSpaceCode));
        using var exclusive = new FileStream(f.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData("zero", "output_duration_zero")] [InlineData("negative", "output_duration_zero")]
    [InlineData("nan", "output_duration_non_finite")] [InlineData("infinity", "output_duration_non_finite")]
    [InlineData("no_stream", "output_probe_failed")] [InlineData("no_video", "output_missing_video_stream")]
    [InlineData("audio", "output_unexpected_extra_streams")] [InlineData("audio_flag", "output_unexpected_extra_streams")]
    [InlineData("video_codec", "output_unexpected_video_codec")] [InlineData("missing_codec", "output_video_codec_missing")]
    [InlineData("video_duration", "output_video_duration_invalid")] [InlineData("video_start", "output_video_start_time_invalid")]
    [InlineData("video_index", "output_video_stream_index")] [InlineData("two_video", "output_video_stream_ambiguous")]
    [InlineData("container", "output_probe_failed")] [InlineData("size", "output_probe_failed")]
    [InlineData("fps", "output_probe_failed")] [InlineData("throw", "output_probe_failed")]
    public async Task InvalidInputNeverPublishesOrLosesOldFinal(string fault, string code)
    {
        using var f = new Files(); var old = Identity(f.Final); var publisher = new Publisher(StagingToFinalPublisher.Instance);
        var probe = new Prober((_, _) =>
        {
            var m = Valid();
            switch (fault)
            {
                case "zero": m.DurationSeconds = 0; break;
                case "negative": m.DurationSeconds = -1; break;
                case "nan": m.DurationSeconds = double.NaN; break;
                case "infinity": m.DurationSeconds = double.PositiveInfinity; break;
                case "no_stream": m.ProbeStreams = Array.Empty<ProbeStreamInfo>(); break;
                case "no_video": m.ProbeStreams[0].CodecType = "data"; break;
                case "audio": m.ProbeStreams = m.ProbeStreams.Append(new() { Index = 1, CodecType = "audio", CodecName = "aac" }).ToArray(); break;
                case "audio_flag": m.HasAudioStream = true; break;
                case "video_codec": m.ProbeStreams[0].CodecName = "PRIVATE-codec"; break;
                case "missing_codec": m.ProbeStreams[0].CodecName = null; break;
                case "video_duration": m.ProbeStreams[0].DurationSeconds = double.NaN; break;
                case "video_start": m.ProbeStreams[0].StartTimeSeconds = null; break;
                case "video_index": m.ProbeStreams[0].Index = 1; break;
                case "two_video": m.ProbeStreams = m.ProbeStreams.Append(m.ProbeStreams[0]).ToArray(); break;
                case "container": m.Container = "PRIVATE-format"; break;
                case "size": m.SizeBytes = 0; break;
                case "fps": m.Fps = 0; break;
                case "throw": throw new IOException("PRIVATE input path/native exception");
            }
            return m;
        });
        var result = await Finalize(f, publisher, probe); Failure(result);
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith(code + ":", StringComparison.Ordinal));
        Assert.Equal(0, publisher.Calls); OldFinal(f, old); Assert.True(File.Exists(f.Source));
    }
    [Theory]
    [InlineData("missing_input")] [InlineData("copy")] [InlineData("flush")] [InlineData("native_rename")]
    [InlineData("cancel_before")] [InlineData("cancel_copy")] [InlineData("closed_gate")]
    [InlineData("storage_before")] [InlineData("storage_copy")] [InlineData("storage_gate")] [InlineData("capacity")]
    public async Task PublicationFailuresPreserveOldIdentityLengthContentAndReleaseOriginal(string fault)
    {
        using var f = new Files(); var old = Identity(f.Final); var ops = new Operations();
        var native = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        using var cancellation = new CancellationTokenSource(); var gate = new FileCommitGate();
        var capacity = new Capacity(); using var safety = new WindowStorageSafety(new(f.Videos, null, f.Root), 1, capacity);
        Assert.Null(await safety.CheckAsync(true)); FileStream? denied = null, deniedSource = null;
        var probe = new Prober((_, _) => Valid());
        if (fault == "missing_input") File.Delete(f.Source);
        if (fault == "cancel_before") cancellation.Cancel();
        if (fault == "storage_before") Assert.True(safety.TryAbort(WindowStorageSafety.LowSpaceCode));
        if (fault == "capacity") capacity.Available = 0;
        if (fault == "copy") native.OriginalCopyCreatedForTests = _ =>
            deniedSource = new FileStream(f.Source, FileMode.Open, FileAccess.Read, FileShare.None);
        native.BeforeOriginalFlushForTests = _ =>
        {
            if (fault == "flush") throw new IOException("PRIVATE flush failure");
            if (fault == "cancel_copy") cancellation.Cancel();
            if (fault == "closed_gate") gate.Close();
            if (fault == "storage_copy") Assert.True(safety.TryAbort(WindowStorageSafety.LowSpaceCode));
        };
        native.BeforeOriginalCommitForTests = () =>
        {
            if (fault == "native_rename") denied = new FileStream(f.Final, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fault == "storage_gate") Assert.True(safety.TryAbort(WindowStorageSafety.LowSpaceCode));
        };
        var wrapper = new Publisher(native, fault == "closed_gate" ? gate : null);
        AvFinalizer.Result result;
        try { result = await Finalize(f, wrapper, probe, cancellation.Token, safety); }
        finally { denied?.Dispose(); deniedSource?.Dispose(); }
        Failure(result); OldFinal(f, old);
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith("atomic_publish_failed:", StringComparison.Ordinal));
        if (ops.Original is not null) { Assert.True(ops.Original.IsClosed); Assert.Equal(1, ops.Deletes); }
        if (fault == "native_rename") Assert.Equal(1, ops.Renames); else Assert.Equal(0, ops.Renames);
        if (fault is "capacity" or "storage_before" or "cancel_before") Assert.Equal(0, wrapper.Calls);
        Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
        Assert.Equal(fault != "missing_input", File.Exists(f.Source));
        using var exclusive = new FileStream(f.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact]
    public async Task ActualMissingInputFailsStructuredProbeWithoutTouchingOldFinal()
    {
        using var f = new Files(); var old = Identity(f.Final); File.Delete(f.Source);
        var publisher = new Publisher(StagingToFinalPublisher.Instance);
        var result = await Finalize(f, publisher, new FfmpegOutputProber());
        Failure(result); OldFinal(f, old); Assert.Equal(0, publisher.Calls);
    }
    [Theory]
    [InlineData("unsupported")] [InlineData("identity")] [InlineData("after_basic")] [InlineData("throw")]
    public async Task OptionalIdentityFailureLeavesRealSuccessfulSilentPublicationUnanchored(string fault)
    {
        using var f = new Files(); await f.Generate(); var old = Identity(f.Final); var ops = new Operations();
        var api = new PublicationIdentityAnchorTests.Native { Fault = fault };
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: api, publicationFiles: ops);
        var result = await Finalize(f, publisher, new FfmpegOutputProber());
        Assert.True(result.ExitCode == 0, result.Error); Assert.Null(result.Meta.PublicationReceipt);
        Assert.NotEqual(old, Identity(f.Final)); Assert.Equal(File.ReadAllBytes(f.Source), File.ReadAllBytes(f.Final));
        Assert.Equal(new FileInfo(f.Source).Length, result.Meta.SizeBytes); Assert.False(result.Meta.HasAudioStream);
        Assert.Equal("not_requested", result.Meta.AudioStatus); Assert.True(ops.Original!.IsClosed);
        Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes);
    }
    [Theory]
    [InlineData("throw")] [InlineData("duration")] [InlineData("audio")] [InlineData("cancel")]
    [InlineData("replace")] [InlineData("replace_length")]
    public async Task PostPublishProbeFailureDoesNotRollBackOrResignFinal(string fault)
    {
        using var f = new Files(); var old = Identity(f.Final); var ops = new Operations();
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        ObservedFileIdentity? committed = null; using var cancellation = new CancellationTokenSource();
        var probe = new Prober((_, call) =>
        {
            var meta = Valid(); if (call == 1) return meta;
            committed = Identity(f.Final); Assert.NotEqual(old, committed);
            if (fault == "throw") throw new IOException("PRIVATE final probe exception");
            if (fault == "duration") meta.DurationSeconds = 0;
            if (fault == "audio") meta.HasAudioStream = true;
            if (fault == "cancel") cancellation.Cancel();
            if (fault is "replace" or "replace_length")
            {
                File.Move(f.Final, Path.Combine(f.Videos, "held.mp4")); File.WriteAllBytes(f.Final, new byte[fault == "replace" ? 1024 : 2048]);
            }
            return meta;
        });
        var result = await Finalize(f, publisher, probe, cancellation.Token);
        if (fault is "replace" or "replace_length")
        {
            Assert.Equal(0, result.ExitCode); Assert.Equal(committed, result.Meta.PublicationReceipt!.Anchor.Identity);
            Assert.NotEqual(Identity(f.Final), result.Meta.PublicationReceipt.Anchor.Identity);
            Assert.Equal(1024, result.Meta.SizeBytes);
        }
        else { Failure(result); Assert.Equal(committed, Identity(f.Final)); Assert.Equal(File.ReadAllBytes(f.Source), File.ReadAllBytes(f.Final)); }
        Assert.True(File.Exists(f.Source)); Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed);
    }
    private sealed class Backend(Files files) : ICaptureBackend
    {
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CaptureConfig? _config;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof proof) { _config = cfg; Started.TrySetResult(true); }
        public OutputMeta Stop() => new AvFinalizer(new ExternalProcessRunner()).FinalizeAsync(files.Source, "unused", _config!.OutputPath,
            null, AudioCaptureSourceKind.None, false).GetAwaiter().GetResult().Meta;
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode => "tray"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => Confirm = callback;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException("No selection UI");
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { }
        public void SetAllIdle() { } public void ShowError(string text) { }
    }
    [Fact]
    public async Task RealNoneFinalizerCoreCompletedWriterAndDefaultRetentionRejectCopiedTimeReplacement()
    {
        using var f = new Files(); await f.Generate(); var prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(f.Root);
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0) });
        var store = new SqliteOperationalStore(Path.Combine(f.Root, "core.db")); store.Initialize();
        using var writer = new SqliteOrdinaryRecordingHistoryStore(store); var tray = new Tray(); var backend = new Backend(f);
        using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        engine.SetTray(tray); engine.ConfigureOrdinaryRecordingHistory(writer); engine.BackendFactory = _ => (backend, "fake");
        try
        {
            var request = new JsonObject { ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" },
                ["video"] = new JsonObject { ["fps"] = 15 }, ["duration_seconds"] = 10, ["countdown_seconds"] = 0,
                ["output"] = new JsonObject { ["directory"] = f.Videos, ["filename"] = "core.mp4", ["conflict_policy"] = "rename" } };
            engine.CreateRecording(request, "task326-synthetic", tray); Assert.NotNull(tray.Confirm);
            tray.Confirm!(ConfirmationDecision.Approve()); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rec = Assert.Single(engine._recs.Values);
            Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(5)));
            engine.Stop(rec.Id, "user"); Assert.True(rec.State == RecState.completed, rec.State + " " + rec.Error);
            var receipt = Assert.IsType<PublicationReceipt>(rec.LastMeta!.PublicationReceipt);
            var row = writer.Read(rec.Id)!; Assert.Equal("completed", row.Status); Assert.Null(row.PublicationReceipt);
            Assert.Equal(Identity(row.OutputPath), receipt.Anchor.Identity); Assert.Equal(row.OutputSizeBytes, receipt.Anchor.Length);
            using (var c = store.OpenReadOnlyConnection()) using (var command = c.CreateCommand())
            { command.CommandText = "SELECT COUNT(*) FROM ordinary_publication_anchors;"; Assert.Equal(1L, command.ExecuteScalar()); }
            var service = new RetentionPreviewQueryService(new UnifiedHistoryQueryService(store, writer), f.Root, TestRuntimeOutputUse.None);
            var before = DateTimeOffset.UtcNow;
            Assert.Equal("age_rule_match", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            var time = File.GetLastWriteTimeUtc(row.OutputPath); File.Move(row.OutputPath, Path.Combine(f.Videos, "held-original.mp4"));
            File.WriteAllBytes(row.OutputPath, File.ReadAllBytes(f.Source)); File.SetLastWriteTimeUtc(row.OutputPath, time);
            Assert.Equal(receipt.Anchor.Length, new FileInfo(row.OutputPath).Length); Assert.NotEqual(receipt.Anchor.Identity, Identity(row.OutputPath));
            Assert.Equal(time, File.GetLastWriteTimeUtc(row.OutputPath));
            Assert.Equal("publication_identity_mismatch", Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, before).Items).ReasonCode);
            output.WriteLine($"Core completed/writer/default retention: path={row.OutputPath}, bytes={receipt.Anchor.Length}, original={receipt.Anchor.Identity}, replacement={Identity(row.OutputPath)}, copiedLastWrite={time:O}; age_rule_match -> publication_identity_mismatch");
        }
        finally
        {
            engine.Dispose(); writer.Dispose(); SqliteConnection.ClearAllPools(); SystemQuery.SetDisplayProvider(null);
            DataDirResolver.ClearOverride(); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", prior);
        }
    }
}
