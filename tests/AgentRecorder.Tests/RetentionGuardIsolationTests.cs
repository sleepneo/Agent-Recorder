using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.RetentionGuardFixture;

namespace AgentRecorder.Tests;

public sealed class RetentionGuardIsolationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NewFrozenPlanReferenceCannotBeInsertedUntilGuardActuallyRetires()
    {
        using var f = new RetentionSharedOutputPathTests.Fixture(root: Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task356", Guid.NewGuid().ToString("N")));
        await RetentionPlannedOutputPathTests.Purpose(f, "standing", path: f.PathFor("different.mp4"));
        var row = f.Seed(); var at = UnifiedHistoryTestDatabase.At;
        var plan = new PlanDefinition("new-plan", true, at);
        var occurrence = PlanOccurrence.CreateFor(plan, "new-occurrence", at.AddMinutes(1), at.AddMinutes(10), at);
        var lease = ConsentLease.CreateFor(plan, occurrence, at, at.AddMinutes(11), 1, TimeSpan.FromMinutes(5), "new-lease");
        new SqlitePlanDefinitionRepository(f.Db.Store).Insert(plan); new SqlitePlanOccurrenceRepository(f.Db.Store).Insert(occurrence); new SqliteConsentLeaseRepository(f.Db.Store).Insert(lease);
        using var writer = f.Db.Store.CreateRetentionGuardConnection(); writer.Open(); SqliteOperationalStore.ConfigureRetentionGuardConnection(writer);
        using var columns = writer.CreateCommand(); columns.CommandText = "PRAGMA table_info(authorized_capture_scopes);";
        var names = new List<string>(); using (var r = columns.ExecuteReader()) while (r.Read()) names.Add(r.GetString(1));
        var sql = "INSERT INTO authorized_capture_scopes SELECT " + string.Join(",", names.Select(n => n switch
        { "scope_id" => "'new-scope'", "plan_id" => "'new-plan'", "occurrence_id" => "'new-occurrence'", "lease_id" => "'new-lease'", "frozen_file_name" => "$name", _ => n })) + " FROM authorized_capture_scopes WHERE scope_id='SECRET_STANDING_SCOPE';";
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var g = new RetentionReferenceGuard(f.Db.Store, f.Db.Ordinary, UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, new());
        var session = g.Execute(row.RecordingId, row.PublicationReceipt!.Anchor, f.Before, context =>
        { context.RequireActive(); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(12))); return RetentionGuardContinuationResult.Completed; });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); var args = new[] { ("$name", (object)Path.GetFileName(row.OutputPath)) };
            Assert.Equal(5, Assert.Throws<SqliteException>(() => Write(writer, sql, args)).SqliteErrorCode);
            release.Set(); Assert.Equal(RetentionGuardReason.Checked, await Finish(session)); Write(writer, sql, args);
            var calls = 0;
            Assert.Equal(RetentionGuardReason.PlannedPathProtected, await Finish(g.Execute(row.RecordingId, row.PublicationReceipt.Anchor, f.Before, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
            Assert.Equal(0, calls); f.NoProbes();
            output.WriteLine("Frozen authorized scope insert: attempted SQLITE_BUSY; same INSERT succeeded after retirement; next guard saw PlannedPathProtected, zero callback.");
        }
        finally { release.Set(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
    [Theory]
    [InlineData("ordinary")][InlineData("plan")][InlineData("future")][InlineData("pending")]
    public async Task ImmediateScopeActuallyRejectsIndependentWalWriterThenSameWriterSucceeds(string source)
    {
        using var f = new RetentionGuardFixture(); var targetName = Path.GetFileName(f.Row.OutputPath);
        string sql; (string, object)[] args;
        if (source == "ordinary")
        {
            f.Db.Ordinary.Register(f.Db.Recording("other") with { OutputPath = f.Row.OutputPath + ".other" });
            sql = "UPDATE ordinary_recording_history SET output_path=$path WHERE recording_id='other';"; args = new[] { ("$path", (object)f.Row.OutputPath) };
        }
        else if (source == "future")
        {
            f.Db.SeedFuture("foreign", sid: "foreign-sid", session: "foreign-session", outputPath: Path.Combine(Path.GetDirectoryName(f.Row.OutputPath)!, "other.mp4"));
            using var prepare = f.Writer(); using var columns = prepare.CreateCommand(); columns.CommandText = "PRAGMA table_info(future_window_authorizations);";
            var names = new List<string>(); using (var reader = columns.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(1));
            sql = "INSERT INTO future_window_authorizations SELECT " + string.Join(",", names.Select(n => n is "authorization_id" or "idempotency_key" ? "'new-foreign'" : n == "output_file_name" ? "$name" : n)) + " FROM future_window_authorizations WHERE authorization_id='foreign';";
            args = new[] { ("$name", (object)targetName) };
        }
        else
        {
            f.Fixed("foreign", "other.mp4");
            // The new occurrence is a genuine independent plan write; the
            // separate four frozen-source tests verify their reference rules.
            if (source == "plan")
            {
                f.Db.SeedPlan("foreign-plan"); f.Db.SeedOccurrence("foreign-plan", "old-occurrence");
                using var prepare = f.Writer(); using var columns = prepare.CreateCommand(); columns.CommandText = "PRAGMA table_info(plan_occurrences);";
                var names = new List<string>(); using (var reader = columns.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(1));
                sql = "INSERT INTO plan_occurrences SELECT " + string.Join(",", names.Select(n => n == "id" ? "'new-occurrence'" : n)) + " FROM plan_occurrences WHERE id='old-occurrence';";
                args = Array.Empty<(string, object)>();
            }
            else { sql = "UPDATE setup_intents SET frozen_file_name=$name WHERE intent_id='foreign';"; args = new[] { ("$name", (object)targetName) }; }
        }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var adapter = new TraceDatabase(); using var g = f.Guard(adapter); var calls = 0;
        var session = f.Execute(g, context => { context.RequireActive(); calls++; entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(12))); return RetentionGuardContinuationResult.Completed; });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); using var writer = f.Writer(); Assert.NotSame(writer, adapter.Connection);
            using (var read = writer.CreateCommand()) { read.CommandText = "PRAGMA journal_mode;"; Assert.Equal("wal", read.ExecuteScalar()); }
            var error = Assert.Throws<SqliteException>(() => Write(writer, sql, args)); Assert.Equal(5, error.SqliteErrorCode);
            Assert.False(session.Retirement.IsCompleted); output.WriteLine("Independent WAL writer executed SQL and returned SQLITE_BUSY (5) while guard continuation held BEGIN IMMEDIATE.");
            release.Set(); Assert.Equal(RetentionGuardReason.Checked, await Finish(session)); Write(writer, sql, args);
            Assert.Equal(1, calls); output.WriteLine("After actual retirement the same independent connection and exact write succeeded.");
        }
        finally { release.Set(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [Theory]
    [InlineData("ordinary")][InlineData("pending")][InlineData("future")]
    public async Task WriterWinsAndCommitsReferenceGuardSeesItAndNeverContinues(string source)
    {
        using var f = new RetentionGuardFixture();
        if (source == "ordinary") f.Db.Ordinary.Register(f.Db.Recording("other") with { OutputPath = f.Row.OutputPath });
        if (source == "pending") f.Fixed("foreign", Path.GetFileName(f.Row.OutputPath));
        if (source == "future") f.Db.SeedFuture("foreign", sid: "private-sid", session: "private-session", outputPath: f.Row.OutputPath);
        using var g = f.Guard(); var calls = 0;
        var reason = await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; }));
        Assert.Equal(source == "ordinary" ? RetentionGuardReason.SharedPathProtected : source == "pending" ? RetentionGuardReason.PendingExactProtected : RetentionGuardReason.PlannedPathProtected, reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CompetingWriterAlreadyHoldsImmediateBeginFailsAtOneSecondThenRecovers()
    {
        using var f = new RetentionGuardFixture(); using var writer = f.Writer(); using var transaction = writer.BeginTransaction(System.Data.IsolationLevel.Serializable, false);
        using var g = f.Guard(); var calls = 0; var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(RetentionGuardReason.DatabaseBusy, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls); Assert.InRange(started.Elapsed.TotalSeconds, 0.8, 2.8); transaction.Rollback();
        Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(g)));
    }

    [Fact]
    public void WalDeferredReadPositiveControlDoesAllowOtherWriter()
    {
        using var f = new RetentionGuardFixture(); using var first = f.Writer(); using var t = first.BeginTransaction(System.Data.IsolationLevel.Serializable, true);
        using var cmd = first.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT count(*) FROM ordinary_recording_history;"; Assert.Equal(1L, cmd.ExecuteScalar());
        using var second = f.Writer(); Write(second, "UPDATE ordinary_recording_history SET source_title='other';");
        // Historical read snapshot survives that successfully committed write.
        cmd.CommandText = "SELECT source_title FROM ordinary_recording_history;"; Assert.Equal("Synthetic source", cmd.ExecuteScalar()); t.Rollback();
    }
}
