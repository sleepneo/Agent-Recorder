using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class UnifiedHistoryQueryTests
{
    [Fact]
    public void MixedCreationTimelineHasStableSourceAndLocalIdOrderAndWhitelistedFacts()
    {
        using var d = new UnifiedHistoryTestDatabase();
        d.SeedPlan("plan"); d.SeedOccurrence("plan", "o-b"); d.SeedOccurrence("plan", "o-a");
        d.SeedFuture("f-b"); d.SeedFuture("f-a"); d.SeedOrdinary("r-b"); d.SeedOrdinary("r-a");
        var before = d.Snapshot();
        var page = d.Page();
        Assert.Equal(new[] { "future_window:f-a", "future_window:f-b", "ordinary:r-a", "ordinary:r-b", "plan:o-a", "plan:o-b" }, page.Items.Select(i => i.EntryId));
        Assert.All(page.Items, i => { Assert.Equal(At, i.CreatedAtUtc); Assert.False(i.OutputPathRecorded); Assert.Null(i.OutputPath); Assert.Null(i.RunId); });
        Assert.All(page.Items.Where(i => i.Source == "future_window"), i => { Assert.Equal("future_window_authorization", i.EntryKind); Assert.Equal("pending", i.AuthorizationStatus); Assert.Null(i.RecordingId); });
        Assert.Equal(before, d.Snapshot());
    }

    [Fact]
    public void MoreThan512MixedEntriesPageWithoutDuplicatesAndExcludeLateOldAndNewInserts()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("scale");
        for (var n = 0; n < 210; n++)
        { d.SeedOccurrence("scale", $"o-{n:D4}"); d.SeedFuture($"f-{n:D4}"); d.SeedOrdinary($"r-{n:D4}"); }
        var candidateCounts = new List<int>(); var validationCounts = new List<int>();
        d.Query.CandidatesSelectedForTests = (_, count) => candidateCounts.Add(count);
        d.Query.EntriesValidatedForTests = (_, count) => validationCounts.Add(count);
        var page = d.Page(31); var ids = page.Items.Select(i => i.EntryId).ToList();
        foreach (var time in new[] { At.AddDays(-1), At.AddDays(1) })
        { var suffix = time.Day.ToString(); d.SeedFuture("late-f-" + suffix, time); d.SeedOrdinary("late-r-" + suffix, time); d.SeedOccurrence("scale", "late-o-" + suffix, time); }
        while (page.NextCursor is not null)
        { page = d.Page(31, cursor: page.NextCursor); ids.AddRange(page.Items.Select(i => i.EntryId)); }
        Assert.Equal(630, ids.Count); Assert.Equal(630, ids.Distinct().Count());
        Assert.DoesNotContain(ids, id => id.Contains("late-"));
        Assert.All(candidateCounts, count => Assert.InRange(count, 0, 32));
        Assert.All(validationCounts, count => Assert.InRange(count, 1, 31));
    }

    [Fact]
    public void CursorBindsIdentityFiltersHostAndDatabaseButAllowsNewPageSizeAndReopen()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("r-a"); d.SeedOrdinary("r-b"); d.SeedOrdinary("r-c");
        var first = d.Page(1); Assert.NotNull(first.NextCursor);
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(20, cursor: first.NextCursor, session: "foreign"));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(20, new("ordinary"), first.NextCursor));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(20, new("all", At), first.NextCursor));
        var reopened = new SqliteOperationalStore(d.Store.DatabasePath); reopened.Initialize();
        var query = new UnifiedHistoryQueryService(reopened, d.Ordinary);
        Assert.Equal(new[] { "ordinary:r-b", "ordinary:r-c" }, query.GetPage(Sid, Session, 2, new(), first.NextCursor).Items.Select(i => i.EntryId));
        Assert.Throws<UnifiedHistoryArgumentException>(() => new UnifiedHistoryQueryService(reopened, d.Ordinary, true).GetPage(Sid, Session, 20, new(), first.NextCursor));
        using var other = new UnifiedHistoryTestDatabase();
        Assert.Throws<UnifiedHistoryArgumentException>(() => other.Page(20, cursor: first.NextCursor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad.cursor")]
    [InlineData("e30.bad")]
    public void BadCursorHasDedicatedArgumentError(string cursor)
    { using var d = new UnifiedHistoryTestDatabase(); Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(cursor: cursor)); }

    [Fact]
    public void OversizedCursorAndInvalidArgumentsRejectWithoutReadingStore()
    {
        using var d = new UnifiedHistoryTestDatabase();
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(cursor: new string('a', 2049)));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(0));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(101));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(filters: new("ALL")));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(filters: new("all", At, At)));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(filters: new("all", At.ToOffset(TimeSpan.FromHours(1)))));
    }

    [Fact]
    public void SourceAndSubMillisecondUtcRangeFiltersAreInclusiveExclusive()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedFuture("f-0", At); d.SeedFuture("f-1", At.AddMilliseconds(1));
        d.SeedOrdinary("r-0", At); d.SeedOrdinary("r-half", At.AddTicks(5000));
        d.SeedPlan("p"); d.SeedOccurrence("p", "o-half", At.AddTicks(5000));
        var result = d.Page(filters: new("all", At.AddTicks(1), At.AddMilliseconds(1)));
        Assert.Equal(new[] { "ordinary:r-half", "plan:o-half" }, result.Items.Select(i => i.EntryId));
        Assert.Single(d.Page(filters: new("ordinary", At, At.AddTicks(1))).Items);
        Assert.Equal(2, d.Page(filters: new("future_window", At, At.AddMilliseconds(2))).Items.Count);
        Assert.Empty(d.Page(filters: new("plan", At.AddDays(1))).Items);
    }

    [Fact]
    public void FutureAuthorizationRemainsSameMemberWhenStartedBetweenPagesAndUsesCurrentPersistentState()
    {
        using var d = new UnifiedHistoryTestDatabase(); var target = d.SeedFuture("f-b"); d.SeedFuture("f-a");
        var first = d.Page(1); var receipt = d.StartFuture(target);
        var second = d.Page(1, cursor: first.NextCursor);
        var item = Assert.Single(second.Items); Assert.Equal("future_window:f-b", item.EntryId); Assert.Equal(At, item.CreatedAtUtc);
        Assert.Equal(receipt.RunId, item.RunId); Assert.Equal("used", item.AuthorizationStatus); Assert.Equal("start_committed", item.RunStatus);
        Assert.False(item.OutputPathRecorded); Assert.Null(item.StartedAtUtc); Assert.Null(second.NextCursor);
    }

    [Fact]
    public void FuturePendingExpiredActiveUnknownFailedAndRevokedAreNotCompletedAndReadNeverExpiresActive()
    {
        using var d = new UnifiedHistoryTestDatabase();
        var active = d.SeedFuture("active", At.AddDays(-20)); d.Future.Approve("active", Sid, Session, "approve-active", active.CreatedAtUtc.AddSeconds(1));
        var expired = d.SeedFuture("expired"); d.Future.SetTerminalWithoutRun("expired", Sid, Session, "expired", "authorization_expired", At.AddSeconds(1));
        var unknown = d.SeedFuture("unknown"); d.StartFuture(unknown); d.Future.RecoverUncertainStarts(At.AddSeconds(3));
        var failed = d.SeedFuture("failed"); var receipt = d.StartFuture(failed); d.Future.CompleteRun("failed", receipt.RunId, false, "stopped_before_duration", null, null, null, At.AddSeconds(3));
        d.SeedFuture("revoked"); d.Future.Revoke("revoked", Sid, Session, At.AddSeconds(1)); d.SeedFuture("pending");
        var before = d.Snapshot(); var items = d.Page().Items.ToDictionary(i => i.AuthorizationId!);
        Assert.Equal("active", items["active"].AuthorizationStatus); Assert.Equal("started_unknown", items["unknown"].RunStatus);
        Assert.Equal("failed", items["failed"].RunStatus); Assert.Null(items["expired"].RunId);
        Assert.All(items.Values, i => { Assert.False(i.OutputPathRecorded); Assert.Null(i.OutputPath); }); Assert.Equal(before, d.Snapshot());
    }

    [Fact]
    public void LegalMediaEvidenceAndMissingMediaRemainSeparateFromPersistentStatus()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("stopped", completed: true);
        d.Execute("UPDATE ordinary_recording_history SET reason_code='api_stop' WHERE recording_id='stopped';");
        var future = d.SeedFuture("complete"); var receipt = d.StartFuture(future);
        var path = Path.Combine(d.Root, future.OutputFileName);
        d.Future.CompleteRun(future.AuthorizationId, receipt.RunId, true, "", path, 12, 60000, At.AddSeconds(63));
        var items = d.Page().Items; Assert.All(items, i => { Assert.True(i.OutputPathRecorded); Assert.False(i.OutputFileExists); Assert.NotNull(i.OutputPath); });
        File.WriteAllBytes(path, new byte[12]);
        Assert.True(d.Page(filters: new("future_window")).Items[0].OutputFileExists);
        Assert.Equal("completed", d.Page(filters: new("ordinary")).Items[0].Status);
    }

    [Fact]
    public void OwnedCorruptionFailsButForeignCorruptRowsDoNotAffectResultsOrMediaProbes()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("owned"); d.SeedOccurrence("owned", "o-owned");
        d.SeedPlan("foreign", "foreign-sid", "foreign-session"); d.SeedOccurrence("foreign", "o-foreign");
        d.SeedFuture("f-foreign", sid: "foreign-sid", session: "foreign-session");
        d.Corrupt("UPDATE plan_occurrences SET status_code='garbage' WHERE id='o-foreign'; UPDATE future_window_authorizations SET status_code='garbage' WHERE authorization_id='f-foreign';");
        Assert.Equal("plan:o-owned", Assert.Single(d.Page().Items).EntryId);
        d.Execute("UPDATE plan_occurrences SET status_code='garbage' WHERE id='o-owned';");
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page());
    }

    [Fact]
    public void OwnedPersistentBadPathIsUnavailableRatherThanArgumentError()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("r", completed: true);
        d.Execute("UPDATE ordinary_recording_history SET output_path=$path WHERE recording_id='r';", ("$path", "D:\\bad\0file.mp4"));
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page());
    }

    [Fact]
    public void OneSnapshotContainsAllSourcesWhileLaterPageSeesStateChanges()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p"); d.SeedOccurrence("p", "o"); d.SeedFuture("f"); d.SeedOrdinary("r");
        d.Query.SnapshotEstablishedForTests = () => d.Execute("UPDATE plan_occurrences SET status_code='missed', terminal_reason_code='window_elapsed', updated_at_utc=updated_at_utc+1,version=version+1 WHERE id='o'; UPDATE future_window_authorizations SET status_code='expired',reason_code='authorization_expired',updated_at_utc=updated_at_utc+1,version=version+1 WHERE authorization_id='f';");
        var page = d.Page(); Assert.Equal("scheduled", page.Items.Single(i => i.Source == "plan").Status); Assert.Equal("pending", page.Items.Single(i => i.Source == "future_window").Status);
        d.Query.SnapshotEstablishedForTests = null; page = d.Page(); Assert.Equal("missed", page.Items.Single(i => i.Source == "plan").Status); Assert.Equal("expired", page.Items.Single(i => i.Source == "future_window").Status);
    }

    [Fact]
    public async Task OrdinaryWriteBarrierTimesOutBoundedlyWithoutDeadlockAndWriteFailureFailsClosed()
    {
        using var d = new UnifiedHistoryTestDatabase(); var rec = d.Recording("r"); d.Ordinary.Register(rec);
        using var gate = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        d.Ordinary.BeforePersistForTests = _ => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(5)); };
        d.Ordinary.TryPublish(rec with { Status = "recording", StartedAtUtc = At.AddSeconds(1) }); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { await Assert.ThrowsAsync<UnifiedHistoryUnavailableException>(() => Task.Run(() => d.Page()).WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { gate.Set(); }
        Assert.Equal("recording", d.Page().Items[0].Status); d.Ordinary.BeforePersistForTests = null;
        d.Execute("CREATE TRIGGER fail_ordinary BEFORE UPDATE ON ordinary_recording_history BEGIN SELECT RAISE(ABORT,'test history failure'); END;");
        d.Ordinary.TryPublish(rec with { Status = "failed", StartedAtUtc = At.AddSeconds(1), CompletedAtUtc = At.AddSeconds(2), ReasonCode = "injected" });
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page());
    }

    [Fact]
    public void HeadlessAllIsOrdinaryOnlyAndExplicitUnsupportedSourcesReject()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("r"); d.SeedFuture("f"); d.SeedPlan("p"); d.SeedOccurrence("p", "o");
        var query = new UnifiedHistoryQueryService(d.Store, d.Ordinary, true);
        Assert.Equal(new[] { "ordinary" }, query.SupportedSources);
        Assert.Equal("ordinary", Assert.Single(query.GetPage(Sid, Session, 20, new()).Items).Source);
        Assert.Throws<UnifiedHistoryArgumentException>(() => query.GetPage(Sid, Session, 20, new("plan")));
        Assert.Throws<UnifiedHistoryArgumentException>(() => query.GetPage(Sid, Session, 20, new("future_window")));
    }

    [Fact]
    public void EmptyDatabaseIsSuccessButSchemaFailureIsUnavailable()
    {
        using var d = new UnifiedHistoryTestDatabase(); var empty = d.Page(); Assert.Empty(empty.Items); Assert.Null(empty.NextCursor);
        d.Execute("DROP INDEX idx_future_window_history_identity_created;");
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page());
    }

    [Fact]
    public void ReadOnlyOpenDoesNotCreateAMissingDatabaseOrDirectoryAndWriterCannotBeMiswired()
    {
        using var d = new UnifiedHistoryTestDatabase();
        var path = Path.Combine(d.Root, "missing-parent", "missing.db");
        var missing = new SqliteOperationalStore(path);
        using var writer = new SqliteOrdinaryRecordingHistoryStore(missing);
        var query = new UnifiedHistoryQueryService(missing, writer);
        Assert.Throws<UnifiedHistoryUnavailableException>(() => query.GetPage(Sid, Session, 20, new()));
        Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Throws<ArgumentException>(() => new UnifiedHistoryQueryService(d.Store, writer));
    }

    [Fact]
    public void UnavailableServerIdentityAndDisposedFailedWriterRemainUnavailable()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("r"); d.SeedOrdinary("r2");
        var cursor = d.Page(1).NextCursor;
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page(1, cursor: cursor, sid: ""));
        d.Execute("CREATE TRIGGER fail_ordinary BEFORE UPDATE ON ordinary_recording_history BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        d.Ordinary.TryPublish(d.Recording("r") with { Status="failed",CompletedAtUtc=At.AddSeconds(1),ReasonCode="fixture" });
        d.Ordinary.Dispose();
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page());
    }
}
