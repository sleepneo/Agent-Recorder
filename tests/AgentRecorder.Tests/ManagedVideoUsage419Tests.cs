using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ManagedVideoUsage419Tests
{
    private static readonly ObservedFileIdentity Identity = new(17, 33, 65);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EmptyUnknownAndMissingStoreHaveDistinctResultsAndNoSideEffects()
    {
        using var db = NewDatabase();
        var service = new ManagedVideoUsage(db.Store, db.Root);
        var at = DateTimeOffset.UtcNow;
        var empty = await service.ReadAsync();
        Assert.Equal(0, empty.RecordedLogicalBytes);
        Assert.Equal(0, empty.ScopedRecordings);
        Assert.Empty(empty.ReasonCodes);
        Assert.Null(empty.UnavailableReason);
        Assert.Equal(TimeSpan.Zero, empty.SampledAtUtc.Offset);
        Assert.InRange(empty.SampledAtUtc, at, DateTimeOffset.UtcNow);

        Seed(db, "unanchored", identity: null);
        var before = db.Snapshot();
        var unknown = await service.ReadAsync();
        Assert.Null(unknown.UnavailableReason);
        Assert.Null(unknown.RecordedLogicalBytes);
        Assert.Equal(1, unknown.ScopedRecordings);
        Assert.Equal(1, unknown.UnknownRecordings);
        Assert.Equal(["publication_evidence_missing"], unknown.ReasonCodes);
        Assert.Equal(before, db.Snapshot());
        Assert.False(Directory.Exists(Videos(db)));

        var absent = Path.Combine(db.Root, "absent", "data.db");
        AssertUnavailable(await new ManagedVideoUsage(new(absent), db.Root).ReadAsync(), "failed");
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));
    }

    [Fact]
    public async Task AnchoredMainBundleAndOrdinaryFilesCountWithoutReadingMediaOrAttachments()
    {
        using var db = NewDatabase();
        var bundleMain = Path.Combine(Videos(db), "legacy.mp4");
        var operation = OrdinaryRecordingBundleOperation.Create("bundle", bundleMain);
        Seed(db, "bundle", Identity, path: bundleMain, bundle: new(operation));
        Assert.True(db.Ordinary.TryPublishBundle(new(operation, new("ready", operation.BundlePath, null))));
        Assert.True(db.Ordinary.DrainAndDispose(Bound));
        using var writer = new SqliteOrdinaryRecordingHistoryStore(db.Store);
        var managed = Path.Combine(Videos(db), "录制.mp4");
        Seed(db, "managed", Identity with { IdHigh = 66 }, bytes: 31, path: managed, writer: writer);
        Seed(db, "external", Identity with { IdHigh = 67 }, path: Path.Combine(db.Root, "Videos-other", "same.mp4"), writer: writer);
        Seed(db, "nested", Identity with { IdHigh = 68 }, path: Path.Combine(Videos(db), "child", "nested.mp4"), writer: writer);
        writer.Register(db.Recording("nonterminal") with { OutputPath = Path.Combine(Videos(db), "pending.mp4") });
        Directory.CreateDirectory(Videos(db));
        await File.WriteAllTextAsync(managed, "marker");
        var before = db.Snapshot();
        using (new FileStream(managed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var usage = await new ManagedVideoUsage(db.Store, db.Root).ReadAsync();
            Assert.Null(usage.UnavailableReason);
            Assert.Empty(usage.ReasonCodes);
            Assert.Equal(43, usage.RecordedLogicalBytes);
            Assert.Equal(2, usage.ScopedRecordings);
            Assert.Equal(2, usage.IncludedRecordings);
            Assert.Equal(2, usage.IncludedObjects);
        }
        Assert.Equal("marker", await File.ReadAllTextAsync(managed));
        Assert.Equal([managed], Directory.GetFileSystemEntries(Videos(db)));
        Assert.Equal(before, db.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameIdentityDeduplicatesAndConflictingPathOrLengthIsUnknown(bool pathConflict)
    {
        using var db = NewDatabase();
        var path = Path.Combine(Videos(db), "first.mp4");
        Seed(db, "first", Identity, path: path);
        Seed(db, "second", Identity, path: Path.Combine(Videos(db), "FIRST.MP4"));
        Seed(db, "unknown", null);
        var service = new ManagedVideoUsage(db.Store, db.Root);
        var usage = await service.ReadAsync();
        Assert.Equal(12, usage.RecordedLogicalBytes);
        Assert.Equal(2, usage.IncludedRecordings);
        Assert.Equal(1, usage.IncludedObjects);
        Assert.Equal(1, usage.UnknownRecordings);

        Seed(db, "conflict", Identity, bytes: pathConflict ? 12 : 15,
            path: pathConflict ? Path.Combine(Videos(db), "other.mp4") : path);
        usage = await service.ReadAsync();
        Assert.Null(usage.UnavailableReason);
        Assert.Null(usage.RecordedLogicalBytes);
        Assert.Equal(4, usage.UnknownRecordings);
        Assert.Equal(0, usage.IncludedObjects);
        Assert.Equal(["publication_evidence_missing", "publication_identity_conflict"], usage.ReasonCodes);
    }

    [Fact]
    public async Task OnlyExactSettledAcceptedReceiptsRemoveAnObject()
    {
        using var db = NewDatabase();
        var removed = Seed(db, "removed", Identity);
        Seed(db, "same-object", Identity, path: removed.OutputPath);
        var other = Seed(db, "kept", Identity with { IdHigh = 70 }, bytes: 20);
        var reused = Seed(db, "new-object-at-same-path", Identity with { IdHigh = 77 }, bytes: 25, path: removed.OutputPath);
        Receipt(db, removed, Identity);
        Receipt(db, other, Identity, bytes: 20);
        Receipt(db, other, Identity with { IdHigh = 70 }, path: Path.Combine(Videos(db), "mismatch.mp4"), bytes: 20);
        Receipt(db, other, Identity with { IdHigh = 70 }, bytes: 19);
        Receipt(db, other, Identity with { IdHigh = 70 }, bytes: 20, progress: null);
        Receipt(db, other, Identity with { IdHigh = 70 }, bytes: 20, progress: 4);
        Receipt(db, other, Identity with { IdHigh = 70 }, bytes: 20, retired: false);
        Receipt(db, reused with { RecordingId = "unregistered" }, Identity with { IdHigh = 77 }, bytes: 25);
        var before = db.Snapshot();
        var usage = await new ManagedVideoUsage(db.Store, db.Root).ReadAsync();
        Assert.Null(usage.UnavailableReason);
        Assert.Empty(usage.ReasonCodes);
        Assert.Equal(45, usage.RecordedLogicalBytes);
        Assert.Equal(4, usage.ScopedRecordings);
        Assert.Equal(2, usage.IncludedRecordings);
        Assert.Equal(2, usage.IncludedObjects);
        Assert.Equal(2, usage.ExcludedSettledRecordings);
        Assert.Equal(1, usage.ExcludedSettledDispositions);
        Assert.Equal(before, db.Snapshot());
    }

    [Fact]
    public async Task EarlierReceiptCannotSettleLaterRegistrationOfSameIdentityAndPath()
    {
        using var db = NewDatabase();
        var row = Seed(db, "original", Identity);
        Receipt(db, row, Identity);
        Seed(db, "later", Identity, path: row.OutputPath, at: UnifiedHistoryTestDatabase.At.AddDays(3));
        var usage = await new ManagedVideoUsage(db.Store, db.Root).ReadAsync();
        Assert.Equal(12, usage.RecordedLogicalBytes);
        Assert.Equal(2, usage.IncludedRecordings);
        Assert.Equal(0, usage.ExcludedSettledDispositions);
    }

    [Theory]
    [InlineData("UPDATE ordinary_publication_anchors SET output_size_bytes=99;")]
    [InlineData("UPDATE ordinary_recording_history SET output_path=output_path||char(0);")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_status_created;")]
    public async Task DamagedEvidenceIsUnavailableWithoutRepair(string damage)
    {
        using var db = NewDatabase();
        Seed(db, "anchored", Identity);
        db.Execute(damage);
        var before = db.Snapshot();
        AssertUnavailable(await new ManagedVideoUsage(db.Store, db.Root).ReadAsync(), "data_invalid");
        Assert.Equal(before, db.Snapshot());
    }

    [Fact]
    public async Task InconsistentMatchingSettlementAndOverflowAreUnavailable()
    {
        using var db = NewDatabase();
        var row = Seed(db, "anchored", Identity);
        Receipt(db, row, Identity, reason: 2);
        AssertUnavailable(await new ManagedVideoUsage(db.Store, db.Root).ReadAsync(), "data_invalid");

        using var overflow = NewDatabase();
        Seed(overflow, "big", Identity, long.MaxValue);
        Seed(overflow, "more", Identity with { IdHigh = 70 });
        AssertUnavailable(await new ManagedVideoUsage(overflow.Store, overflow.Root).ReadAsync(), "data_invalid");
    }

    [Theory]
    [InlineData("history_limit")]
    [InlineData("ledger_limit")]
    [InlineData("receipt_validation_limit")]
    public async Task CapsRejectAnExtraRowInsteadOfReturningPartialTotals(string reason)
    {
        using var db = NewDatabase();
        var row = Seed(db, "base", Identity);
        var service = new ManagedVideoUsage(db.Store, db.Root);
        if (reason == "history_limit")
        {
            db.Execute($"""
                WITH RECURSIVE rows(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM rows WHERE i<{ManagedVideoUsage.HistoryLimit})
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,
                    source_title,audio_mode,output_path,completed_at_utc,updated_at_utc)
                SELECT 'bulk-'||i,i+1,h.created_at_utc,'completed',h.source_type,h.capture_semantics,h.source_title,h.audio_mode,
                    h.output_path,h.completed_at_utc,h.updated_at_utc FROM rows CROSS JOIN ordinary_recording_history h WHERE h.recording_id='base';
                """);
        }
        else
        {
            var accepted = reason == "receipt_validation_limit";
            Receipt(db, row, Identity, progress: accepted ? 2 : null);
            var count = accepted ? ManagedVideoUsage.ReceiptValidationLimit : ManagedVideoUsage.LedgerLimit;
            var columns = SqliteSchemaV43.BindingColumns.Concat(new[]
                { "phase", "progress", "reason", "resources_retired", "settled_ticks" }).ToArray();
            var template = string.Join(',', columns.Select(c => c switch
            {
                "operation_id" => "printf('%032x',i)",
                "approval_id" => "printf('%032x',i+10000)",
                _ => "o." + c
            }));
            db.RawExecute($"""
                WITH RECURSIVE rows(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM rows WHERE i<{count}),
                one AS MATERIALIZED (SELECT * FROM ordinary_deletion_operations LIMIT 1)
                INSERT INTO ordinary_deletion_operations({string.Join(',', columns)})
                SELECT {template} FROM rows CROSS JOIN one o;
                """);
        }
        AssertUnavailable(await service.ReadAsync(), reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutAndCancellationKeepActualSharedSlotUntilWorkerRetires(bool cancel)
    {
        using var db = NewDatabase();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        var service = new ManagedVideoUsage(db.Store, db.Root)
        {
            BeforeReadForTests = () => { Interlocked.Increment(ref dispatches); entered.TrySetResult(); release.Wait(Bound); },
            DeadlineForTests = token => deadline.Task.WaitAsync(token),
            WorkerRetiredForTests = () => retired.TrySetResult()
        };
        var pending = service.ReadAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Bound);
            if (cancel) cancellation.Cancel();
            else deadline.TrySetResult();
            AssertUnavailable(await pending.WaitAsync(Bound), cancel ? "cancelled" : "timeout");
            Assert.Equal("artifact_metadata_worker_pending", HostReadOnlyWorkerState.BusyReason);
            AssertUnavailable(await service.ReadAsync(), "busy");
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            AssertUnavailable(await service.ReadAsync(alreadyCancelled.Token), "cancelled");
            Assert.Equal(1, dispatches);
        }
        finally
        {
            release.Set();
            await retired.Task.WaitAsync(Bound);
        }
        Assert.Null(HostReadOnlyWorkerState.BusyReason);
        Assert.Equal(0, (await new ManagedVideoUsage(db.Store, db.Root).ReadAsync()).RecordedLogicalBytes);
    }

    private static UnifiedHistoryTestDatabase NewDatabase() =>
        new(Path.Combine(Path.GetTempPath(), "task419-" + Guid.NewGuid().ToString("N")));

    private static string Videos(UnifiedHistoryTestDatabase db) => Path.Combine(db.Root, "Videos");

    private static OrdinaryRecordingHistorySnapshot Seed(UnifiedHistoryTestDatabase db, string id, ObservedFileIdentity? identity,
        long bytes = 12, string? path = null, OrdinaryRecordingBundleRegistration? bundle = null,
        DateTimeOffset? at = null, SqliteOrdinaryRecordingHistoryStore? writer = null)
    {
        path ??= Path.Combine(Videos(db), id + ".mp4");
        var row = db.Recording(id, at, completed: true) with
        {
            OutputPath = path, OutputSizeBytes = bytes, BundleRegistration = bundle,
            PublicationReceipt = identity is null ? null : PublicationReceipt.IssueForTests(path, bytes, identity)
        };
        (writer ?? db.Ordinary).Register(row);
        return row;
    }

    private static void Receipt(UnifiedHistoryTestDatabase db, OrdinaryRecordingHistorySnapshot row, ObservedFileIdentity identity,
        string? path = null, long bytes = 12, int? progress = 2, bool retired = true, int? reason = null)
    {
        var at = row.CompletedAtUtc!.Value;
        db.Execute("""
            INSERT INTO ordinary_deletion_operations(operation_id,approval_id,owner_sid,owner_session,host_id,recording_id,
                videos_root,output_path,volume_id,file_id,recorded_bytes,completed_ticks,cutoff_ticks,semantics,prepared_ticks,
                phase,progress,reason,resources_retired,settled_ticks)
            VALUES($operation,$approval,'fixture-sid','fixture-session',$host,$id,$root,$path,$volume,$file,$bytes,$at,$cutoff,
                $semantics,$prepared,$phase,$progress,$reason,$retired,$settled);
            """, ("$operation", Guid.NewGuid().ToString("N")), ("$approval", Guid.NewGuid().ToString("N")),
            ("$host", Guid.NewGuid().ToString("N")), ("$id", row.RecordingId), ("$root", Videos(db)),
            ("$path", path ?? row.OutputPath), ("$volume", BitConverter.GetBytes(identity.VolumeSerialNumber)),
            ("$file", BitConverter.GetBytes(identity.IdLow).Concat(BitConverter.GetBytes(identity.IdHigh)).ToArray()),
            ("$bytes", bytes), ("$at", at.UtcTicks), ("$cutoff", at.AddDays(1).UtcTicks), ("$semantics", LocalRetentionApproval.Semantics),
            ("$prepared", at.AddDays(2).UtcTicks), ("$phase", progress is null ? "prepared_unknown" : "settled"),
            ("$progress", (object?)progress ?? DBNull.Value), ("$reason", progress is null ? DBNull.Value : (object)(reason ?? (progress == 4 ? 4 : 0))),
            ("$retired", progress is null ? DBNull.Value : (object)(retired ? 1 : 0)),
            ("$settled", progress is null ? DBNull.Value : (object)at.AddDays(2).AddSeconds(1).UtcTicks));
    }

    private static void AssertUnavailable(ManagedVideoUsageSample sample, string reason)
    {
        Assert.Equal(reason, sample.UnavailableReason);
        Assert.Null(sample.RecordedLogicalBytes);
        Assert.Equal(default, sample.SampledAtUtc);
    }
}
