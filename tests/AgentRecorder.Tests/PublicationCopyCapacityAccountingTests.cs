using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PublicationCopyCapacityAccountingTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task328r", "copy-" + Guid.NewGuid().ToString("N")));
        internal string Source => Path.Combine(Root, "source.bin");
        internal string Final => Path.Combine(Root, "final.mp4");
        internal Files(long bytes = 100 * MiB) { Directory.CreateDirectory(Root); using var s = new FileStream(Source, FileMode.CreateNew, FileAccess.Write); s.SetLength(bytes); File.WriteAllBytes(Final, new byte[] { 8, 3, 2, 8 }); }
        public void Dispose() { SqliteConnection.ClearAllPools(); Assert.Equal(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task328r")), Path.GetDirectoryName(Root)); Directory.Delete(Root, true); }
    }
    private sealed class Native : IPublicationFileOperations
    {
        internal FileStream? Copy; internal SafeFileHandle? Handle; internal int Renames, Deletes; internal string? CopyPath;
        public FileStream CreateNew(string p) { CopyPath = p; Copy = WindowsPublicationFileOperations.Instance.CreateNew(p); Handle = Copy.SafeFileHandle; return Copy; }
        public void Rename(SafeFileHandle h, string p) { Renames++; WindowsPublicationFileOperations.Instance.Rename(h, p); }
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle h) { Deletes++; return WindowsPublicationFileOperations.Instance.DeleteOriginal(h); }
    }
    private sealed class Capacity : IStorageCapacityProvider
    {
        internal long Initial = WindowStorageSafety.LowWaterBytes + 200 * MiB, SourceBytes = 100 * MiB, External;
        internal Native? Native; internal bool SameVolume, Changed; internal Func<CancellationToken, Task>? Pause; internal int Queries;
        public async Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            Interlocked.Increment(ref Queries);
            var free = Initial - SourceBytes - (Native?.Copy is { } c && c.CanWrite ? c.Length : 0) - External;
            if (Pause is { } pause) await pause(token);
            return paths.Select(p => new StorageVolumeSample(p, Changed ? "changed" : SameVolume ? "same" : p, free)).ToArray();
        }
    }
    private static WindowStorageSafety Guard(Files f, Capacity c, bool samePath = false)
    {
        var g = new WindowStorageSafety(new(f.Root, null, (samePath ? f.Root : Path.Combine(f.Root, "synthetic-wgc"))), 1, c);
        g.ReserveFfmpegVideoOnlyPublication(); return g;
    }
    private static ObservedFileIdentity Identity(string p) { using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); Assert.True(WindowsFileMetadataApi.Instance.Identity(s.SafeFileHandle, out var i, out _)); return new(i.VolumeSerialNumber, i.IdLow, i.IdHigh); }
    [Fact]
    public async Task RealPublisherAtCompletedCopyWatermarkReproducesReviewBoundary()
    {
        using var f = new Files(); var native = new Native(); var c = new Capacity { Native = native, SourceBytes = 0 }; using var g = Guard(f, c);
        Assert.Null(await g.CheckAsync(true)); c.SourceBytes = 100 * MiB; g.ReservePublicationCopy(c.SourceBytes); Assert.Null(await g.CheckAsync(false));
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native);
        publisher.BeforeOriginalFlushForTests = _ => { var failure = g.CheckAsync(false).GetAwaiter().GetResult(); output.WriteLine($"Actual copy bytes={native.Copy!.Length}, simulated free={c.Initial-c.SourceBytes-native.Copy.Length}, check={failure ?? "ok"}"); if (failure is not null) g.TryAbort(failure); };
        var result = await publisher.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true));
        Assert.True(result.Success, $"failure={g.FailureCode}, publish={result.FailureCategory}"); Assert.NotNull(result.Receipt); Assert.True(g.Committed); Assert.Equal(result.Receipt!.Anchor.Identity, Identity(f.Final)); Assert.Equal(100 * MiB, result.FinalSizeBytes); Assert.Equal(1, native.Renames); Assert.True(native.Handle!.IsClosed);
    }

    private static void Preserved(Files f, ObservedFileIdentity old) { Assert.Equal(old, Identity(f.Final)); Assert.Equal(new byte[] { 8, 3, 2, 8 }, File.ReadAllBytes(f.Final)); }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ActualCopyProgressAtWatermarkSupportsRepeatedChecksAndPathAggregation(bool sameVolume, bool samePath)
    {
        using var f = new Files(); var native = new Native(); var c = new Capacity { Native = native, SameVolume = sameVolume, SourceBytes = 0, Initial = WindowStorageSafety.LowWaterBytes + (sameVolume ? 300 : 200) * MiB }; using var g = Guard(f, c, samePath);
        Assert.Null(await g.CheckAsync(true)); c.SourceBytes = 100 * MiB; c.External = sameVolume ? 100 * MiB : 0; g.ReservePublicationCopy(c.SourceBytes);
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(); var paused = 0;
        pub.OriginalCopyProgressForTests = (_, bytes) => { if (Interlocked.Exchange(ref paused, 1) == 0) { Assert.Equal(c.SourceBytes - bytes, g.RemainingPublicationCopyBytes); entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var run = Task.Run(() => pub.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true)));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); for (var i = 0; i < 3; i++) Assert.Null(await g.CheckAsync(false)); Assert.Equal(0, native.Renames); }
        finally { release.Set(); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(result.Success, result.FailureCategory); Assert.NotNull(result.Receipt); Assert.Equal(Identity(f.Final), result.Receipt!.Anchor.Identity); Assert.Equal(0, g.RemainingPublicationCopyBytes); Assert.Equal(1, native.Renames); Assert.Equal(0, native.Deletes); Assert.True(native.Handle!.IsClosed); Assert.False(File.Exists(native.CopyPath));
        output.WriteLine($"Real native 100MiB copy, sameVolume={sameVolume}, samePath={samePath}, inclusive 256MiB simulated watermark: repeated checks and commit succeeded.");
    }
    [Theory]
    [InlineData("minus_one")] [InlineData("external_loss")] [InlineData("large_source")] [InlineData("remount")] [InlineData("query_timeout")]
    [InlineData("copy")] [InlineData("flush")] [InlineData("cancel")] [InlineData("gate")]
    public async Task ActualPublisherFaultsPreserveOldObjectAndFinishOnlyOwnReservation(string fault)
    {
        using var f = new Files(fault == "large_source" ? 120 * MiB : 100 * MiB); var old = Identity(f.Final); var native = new Native(); var c = new Capacity { Native = native, SourceBytes = 0 }; using var g = Guard(f, c);
        Assert.Null(await g.CheckAsync(true)); c.SourceBytes = new FileInfo(f.Source).Length; g.ReservePublicationCopy(c.SourceBytes);
        using var local = CancellationTokenSource.CreateLinkedTokenSource(g.AbortToken); var gate = new FileCommitGate(); FileStream? denied = null;
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var fired = 0;
        pub.OriginalCopyProgressForTests = (_, _) =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            if (fault == "minus_one") c.External = 1;
            if (fault == "external_loss") c.External = MiB;
            if (fault == "remount") c.Changed = true;
            if (fault == "query_timeout") c.Pause = t => Task.Delay(Timeout.InfiniteTimeSpan, t);
            if (fault == "cancel") local.Cancel();
            if (fault == "gate") gate.Close();
            if (fault is "minus_one" or "external_loss" or "remount" or "query_timeout")
            {
                var code = g.CheckAsync(false).GetAwaiter().GetResult(); Assert.Equal(fault is "remount" or "query_timeout" ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode, code); Assert.True(g.TryAbort(code!));
            }
        };
        if (fault == "copy") pub.OriginalCopyCreatedForTests = _ => denied = new FileStream(f.Source, FileMode.Open, FileAccess.Read, FileShare.None);
        if (fault == "flush") pub.BeforeOriginalFlushForTests = _ => throw new IOException("injected flush failure");
        PublishResult result;
        try { result = await pub.PublishAsync(f.Source, f.Final, local.Token, g.CreateCommitGate(gate, true)).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { denied?.Dispose(); }
        Assert.False(result.Success); Assert.Null(result.Receipt); Assert.False(g.Committed); Preserved(f, old); Assert.Equal(0, g.RemainingPublicationCopyBytes); Assert.Equal(0, native.Renames);
        if (native.Handle is not null) { Assert.True(native.Handle.IsClosed); Assert.Equal(1, native.Deletes); Assert.False(File.Exists(native.CopyPath)); }
        Assert.Equal(fault == "large_source" ? 120 * MiB : 100 * MiB, new FileInfo(f.Source).Length);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SampleDuringActualCopyPreventsStaleProgressPairAndRechecksExternalLoss(bool externalLoss)
    {
        using var f = new Files(); var old = Identity(f.Final); var native = new Native(); var c = new Capacity { Native = native }; using var g = Guard(f, c); g.ReservePublicationCopy(c.SourceBytes);
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var atChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var nextChunk = new ManualResetEventSlim(); var first = 0;
        pub.OriginalCopyProgressForTests = (_, _) => { if (Interlocked.Exchange(ref first, 1) == 0) { atChunk.TrySetResult(); Assert.True(nextChunk.Wait(TimeSpan.FromSeconds(5))); } };
        var run = Task.Run(() => pub.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true))); await atChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var returnSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); c.Pause = async t => { queryEntered.TrySetResult(); await returnSample.Task.WaitAsync(t); };
        var check = g.CheckAsync(false); await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var length = native.Copy!.Length; var remaining = g.RemainingPublicationCopyBytes;
        try { nextChunk.Set(); await Task.Delay(40); Assert.Equal(length, native.Copy.Length); Assert.Equal(remaining, g.RemainingPublicationCopyBytes); if (externalLoss) c.External = 1; }
        finally { c.Pause = null; returnSample.TrySetResult(); nextChunk.Set(); }
        Assert.Null(await check.WaitAsync(TimeSpan.FromSeconds(5))); var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        if (externalLoss) { Assert.False(result.Success); Assert.Equal(WindowStorageSafety.LowSpaceCode, g.FailureCode); Preserved(f, old); Assert.Null(result.Receipt); }
        else { Assert.True(result.Success); Assert.NotNull(result.Receipt); }
        Assert.Equal(0, g.RemainingPublicationCopyBytes);
    }
    [Fact]
    public async Task NativeWriteBeforeProgressBlocksCapacityUntilMatchingRemainingIsVisible()
    {
        using var f = new Files(); var native = new Native(); var c = new Capacity { Native = native }; using var g = Guard(f, c); g.ReservePublicationCopy(c.SourceBytes);
        using var recordProgress = new ManualResetEventSlim(); var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var once = 0;
        g.AfterPhysicalCopyWriteForTests = () => { if (Interlocked.Exchange(ref once, 1) == 0) { written.TrySetResult(); Assert.True(recordProgress.Wait(TimeSpan.FromSeconds(5))); } };
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var run = Task.Run(() => pub.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true))); await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queries = c.Queries; var check = g.CheckAsync(false);
        try { await Task.Delay(40); Assert.Equal(queries, c.Queries); Assert.Equal(100 * MiB, g.RemainingPublicationCopyBytes); Assert.True(native.Copy!.Length > 0); }
        finally { recordProgress.Set(); }
        Assert.Null(await check.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))).Success); Assert.Equal(0, g.RemainingPublicationCopyBytes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapacityQueryAcrossCopyStartPreservesSufficiencyOrRejectsActualDeficit(bool loss)
    {
        using var f = new Files(); var old = Identity(f.Final); var native = new Native(); var c = new Capacity { Native = native }; using var g = Guard(f, c); g.ReservePublicationCopy(c.SourceBytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); c.Pause = async t => { entered.TrySetResult(); await release.Task.WaitAsync(t); };
        var check = g.CheckAsync(false); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var run = pub.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true));
        try { await Task.Delay(40); Assert.Null(native.Copy); if (loss) c.External = 1; }
        finally { c.Pause = null; release.TrySetResult(); }
        Assert.Null(await check); var result = await run.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(!loss, result.Success); if (loss) { Preserved(f, old); Assert.Null(result.Receipt); } Assert.Equal(0, g.RemainingPublicationCopyBytes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeCommitAndCapacityAbortHaveOneGateWinner(bool commitWins)
    {
        using var f = new Files(); var old = Identity(f.Final); var native = new Native(); var c = new Capacity { Native = native }; using var g = Guard(f, c); g.ReservePublicationCopy(c.SourceBytes);
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (commitWins) pub.BeforeOriginalCommitForTests = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        else pub.BeforeOriginalFlushForTests = _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var run = Task.Run(() => pub.PublishAsync(f.Source, f.Final, g.AbortToken, g.CreateCommitGate(null, true))); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var abort = Task.Run(() => g.TryAbort(WindowStorageSafety.LowSpaceCode));
        try { if (!commitWins) Assert.True(await abort.WaitAsync(TimeSpan.FromSeconds(5))); else await Task.Delay(40); }
        finally { release.Set(); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(commitWins, result.Success); Assert.Equal(!commitWins, await abort); Assert.Equal(commitWins ? 1 : 0, native.Renames); if (!commitWins) { Preserved(f, old); Assert.Null(result.Receipt); } Assert.Equal(0, g.RemainingPublicationCopyBytes);
    }
    [Fact]
    public async Task OtherRunsAndNonFfmpegPublisherKeepTheirOriginalBudgetAndCopyContract()
    {
        using var f = new Files(); var native = new Native(); var c = new Capacity { Native = native }; using var opted = Guard(f, c); using var other = Guard(f, new Capacity());
        using var ordinary = new WindowStorageSafety(new(f.Root, null, Path.Combine(f.Root, "wgc")), 1, c); var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); var progress = 0; pub.OriginalCopyProgressForTests = (_, _) => progress++;
        Assert.True((await pub.PublishAsync(f.Source, f.Final, ordinary.AbortToken, ordinary.CreateCommitGate(null, true))).Success); Assert.Equal(0, progress); Assert.Equal(100 * MiB, opted.RemainingPublicationCopyBytes); Assert.Equal(100 * MiB, other.RemainingPublicationCopyBytes);
        opted.EndFfmpegVideoOnlyPublication(); Assert.Equal(0, opted.RemainingPublicationCopyBytes); Assert.Equal(100 * MiB, other.RemainingPublicationCopyBytes);
    }

    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm; public string HostMode => "tray"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => Confirm = c;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { } public void SetAllIdle() { } public void ShowError(string t) { }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void ActualBackendCoreAtWatermarkPublishesOrdinaryAnchorOrFailsClosed(bool sameVolume, bool shortage)
    {
        using var f = new Files(); var old = Identity(f.Final); var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE"); var previousData = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", f.Root); DataDirResolver.SetOverride(f.Root);
        SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, "synthetic-test-display:display_1", DisplayIdentityResolutionStatus.Resolved) });
        var native = new Native(); var c = new Capacity { Native = native, SourceBytes = 0, SameVolume = sameVolume, Initial = WindowStorageSafety.LowWaterBytes + (sameVolume ? 300 : 200) * MiB }; using var g = Guard(f, c);
        var pub = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: native); pub.BeforeOriginalFlushForTests = _ => { Assert.Equal(0, g.RemainingPublicationCopyBytes); if (shortage) c.External++; var code = g.CheckAsync(false).GetAwaiter().GetResult(); Assert.Equal(shortage ? WindowStorageSafety.LowSpaceCode : null, code); if (code is not null) g.TryAbort(code); };
        using var backend = new FfmpegCaptureBackend { VideoInputArgumentsForTests = _ => new[] { "-f", "lavfi", "-t", "1", "-i", "color=c=black:s=64x64:r=15" }, VideoOnlyPublisherFactory = _ => pub };
        backend.BeforeVideoOnlyFinalizeForTests = path => { using var stream = new FileStream(path, FileMode.Open, FileAccess.Write); stream.SetLength(100 * MiB); c.SourceBytes = 100 * MiB; c.External = sameVolume ? 100 * MiB : 0; };
        var store = new SqliteOperationalStore(Path.Combine(f.Root, "history.db")); store.Initialize(); using var writer = new SqliteOrdinaryRecordingHistoryStore(store); var tray = new Tray(); using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit()); engine.SetTray(tray); engine.ConfigureOrdinaryRecordingHistory(writer); engine.BackendFactory = cfg => { cfg.StorageSafety = g; return (backend, "ffmpeg"); };
        try
        {
            var coreFinal = Path.Combine(f.Root, "core.mp4");
            var request = new JsonObject { ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 }, ["duration_seconds"] = 1, ["countdown_seconds"] = 0, ["output"] = new JsonObject { ["directory"] = f.Root, ["filename"] = "core.mp4", ["conflict_policy"] = "overwrite" } };
            engine.CreateRecording(request, "task328r-synthetic", tray); Assert.NotNull(tray.Confirm); tray.Confirm!(ConfirmationDecision.Approve()); var rec = Assert.Single(engine._recs.Values);
            Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(15)), $"state={rec.State}, error={rec.Error}");
            Assert.True(SpinWait.SpinUntil(() => writer.Read(rec.Id)?.Status == (shortage ? "failed" : "completed"), TimeSpan.FromSeconds(3)));
            var row = writer.Read(rec.Id)!; using var connection = store.OpenReadOnlyConnection(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM ordinary_publication_anchors;";
            Assert.Equal(shortage ? 0L : 1L, command.ExecuteScalar()); Assert.Equal(shortage ? RecState.failed : RecState.completed, rec.State); Assert.Equal(shortage ? "failed" : "completed", row.Status);
            Assert.Equal(0, g.RemainingPublicationCopyBytes); Assert.False(File.Exists(backend.VideoOnlyStagingPath));
            if (shortage) { Assert.Null(rec.LastMeta!.PublicationReceipt); Assert.False(rec.LastMeta.OutputFileExists); Assert.False(File.Exists(coreFinal)); Preserved(f, old); }
            else { var receipt = Assert.IsType<PublicationReceipt>(rec.LastMeta!.PublicationReceipt); Assert.Equal(100 * MiB, receipt.Anchor.Length); Assert.Equal(Identity(coreFinal), receipt.Anchor.Identity); Assert.True(g.Committed); Assert.Equal(1, native.Renames); }
            output.WriteLine($"Actual lavfi FFmpeg Start -> padded owned MP4 source 100MiB -> native publisher at synthetic watermark -> Core {row.Status} -> SQLite anchors={(shortage ? 0 : 1)}, sameVolume={sameVolume}; source cleared/retained by existing policy.");
        }
        finally { engine.Dispose(); writer.Dispose(); SqliteConnection.ClearAllPools(); DataDirResolver.ClearOverride(); SystemQuery.SetDisplayTopologyProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", previous); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", previousData); }
    }
}
