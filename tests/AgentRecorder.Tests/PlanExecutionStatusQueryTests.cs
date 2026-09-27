using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PlanExecutionStatusQueryTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private const string Sid = "S-1-5-21-plan-status";
    private const string Session = "plan-status-session";

    [Fact]
    public void PreV16SettledRunIsDurableAcrossQueryRecreationWithoutInventingAnOutputPath()
    {
        using var database = new TestDatabase();
        var target = Path.Combine(database.Root, "approved-target.mp4");
        File.WriteAllText(target, "a file at the approved target is not artifact-path evidence");
        SeedOwnedPlan(database.Store, "once-1", isOneTime: true, outputPath: target);
        var setupOnly = new PlanExecutionStatusQueryService(database.Store).Get("once-1", Sid, Session)!;
        Assert.Equal("enabled", setupOnly.PlanStatus);
        Assert.Equal(0, setupOnly.OccurrenceCount);
        Assert.Null(setupOnly.NextOccurrence);
        Assert.Null(setupOnly.LatestOccurrence);

        SeedOccurrenceAndRun(database.Store, "once-1", "occ-1", "run-1",
            "completed", "settled", Created, mediaArtifactId: "artifact-id-only");
        DowngradeToV15AndReinitialize(database.Store);

        var first = new PlanExecutionStatusQueryService(database.Store).Get("once-1", Sid, Session)!;
        Assert.Equal(("once", "enabled", (bool?)null, 1L), (first.Kind, first.PlanStatus, first.ScheduleExhausted, first.OccurrenceCount));
        Assert.Null(first.NextOccurrence);
        Assert.Equal("occ-1", first.LatestOccurrence!.OccurrenceId);
        Assert.Equal("completed", first.LatestOccurrence.Status);
        Assert.Equal("settled", first.LatestOccurrence.Run!.Status);
        Assert.Equal("run-1", first.LatestOccurrence.RunId);
        Assert.Null(first.LatestOccurrence.OutputPath);
        Assert.False(first.LatestOccurrence.OutputPathRecorded);
        Assert.Null(first.LatestOccurrence.OutputFileExists);

        File.Delete(target);
        var reopenedStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var afterRestart = new PlanExecutionStatusQueryService(reopenedStore).Get("once-1", Sid, Session)!;
        Assert.Equal(first, afterRestart);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("missed", "window_elapsed")]
    [InlineData("blocked", "display_changed")]
    public void NonRunTerminalOccurrencesDoNotFabricateRuns(string status, string reason)
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "once-terminal", isOneTime: true);
        SeedOccurrence(database.Store, "once-terminal", "occ-terminal", status, reason, Created);

        var result = new PlanExecutionStatusQueryService(database.Store).Get("once-terminal", Sid, Session)!;
        Assert.Null(result.NextOccurrence);
        Assert.Equal("occ-terminal", result.LatestOccurrence!.OccurrenceId);
        Assert.Equal(status, result.LatestOccurrence.Status);
        Assert.Equal(reason, result.LatestOccurrence.TerminalReasonCode);
        Assert.Null(result.LatestOccurrence.RunId);
        Assert.Null(result.LatestOccurrence.Run);
    }

    [Fact]
    public void ScheduledOccurrenceWithoutRunRemainsTheNextOccurrence()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "scheduled-plan", isOneTime: true);
        SeedOccurrence(database.Store, "scheduled-plan", "scheduled-occ", "scheduled", null, Created);

        var result = new PlanExecutionStatusQueryService(database.Store).Get("scheduled-plan", Sid, Session)!;
        Assert.Equal(1, result.OccurrenceCount);
        Assert.Equal("scheduled-occ", result.NextOccurrence!.OccurrenceId);
        Assert.Equal("scheduled", result.NextOccurrence.Status);
        Assert.Null(result.NextOccurrence.RunId);
        Assert.Null(result.NextOccurrence.Run);
        Assert.Equal(result.NextOccurrence, result.LatestOccurrence);
    }

    [Fact]
    public void CompletedOccurrenceWithRecordingRunFailsClosed()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "completed-recording-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "completed-recording-plan", "completed-recording-occ",
            "completed-recording-run", "completed", "recording", Created);

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("completed-recording-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public void CompletedOccurrenceWithoutRunFailsClosed()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "completed-no-run-plan", isOneTime: true);
        SeedOccurrence(database.Store, "completed-no-run-plan", "completed-no-run-occ", "completed", null, Created);

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("completed-no-run-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public void RunCreatedOccurrenceWithSettledRunFailsClosed()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "run-created-settled-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "run-created-settled-plan", "run-created-settled-occ",
            "run-created-settled-run", "run_created", "settled", Created);

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("run-created-settled-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public void RunCreatedOccurrenceWithoutRunFailsClosed()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "run-created-no-run-plan", isOneTime: true);
        SeedOccurrence(database.Store, "run-created-no-run-plan", "run-created-no-run-occ", "run_created", null, Created);

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("run-created-no-run-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public void CompletedOccurrenceWithSettledRunIsReturned()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "completed-settled-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "completed-settled-plan", "completed-settled-occ",
            "completed-settled-run", "completed", "settled", Created, mediaArtifactId: "artifact-id");
        DowngradeToV15AndReinitialize(database.Store);

        var result = new PlanExecutionStatusQueryService(database.Store).Get("completed-settled-plan", Sid, Session)!;
        Assert.Null(result.NextOccurrence);
        Assert.Equal("completed", result.LatestOccurrence!.Status);
        Assert.Equal("settled", result.LatestOccurrence.Run!.Status);
        Assert.Equal("completed-settled-run", result.LatestOccurrence.Run.RunId);
        Assert.Null(result.LatestOccurrence.OutputPath);
        Assert.False(result.LatestOccurrence.OutputPathRecorded);
        Assert.Null(result.LatestOccurrence.OutputFileExists);
    }

    [Fact]
    public void CurrentSettledRunWithoutEvidenceFailsClosedInsteadOfBeingTreatedAsLegacy()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "missing-output-evidence", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "missing-output-evidence", "missing-evidence-occ",
            "missing-evidence-run", "completed", "settled", Created);

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("missing-output-evidence", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Theory]
    [InlineData("blocked", "failed")]
    [InlineData("blocked", "session_interrupted")]
    public void AbnormalRunTerminalStateAndReasonsAreProjected(string occurrenceStatus, string runStatus)
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "abnormal-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "abnormal-plan", "abnormal-occ", "abnormal-run",
            occurrenceStatus, runStatus, Created, occurrenceReason: "capture_failed");

        var result = new PlanExecutionStatusQueryService(database.Store).Get("abnormal-plan", Sid, Session)!;
        Assert.Null(result.NextOccurrence);
        Assert.Equal(occurrenceStatus, result.LatestOccurrence!.Status);
        Assert.Equal("capture_failed", result.LatestOccurrence.TerminalReasonCode);
        Assert.Equal("abnormal-run", result.LatestOccurrence.RunId);
        Assert.Equal(runStatus, result.LatestOccurrence.Run!.Status);
        Assert.Equal("capture_failed", result.LatestOccurrence.Run.TerminalReasonCode);
    }

    [Fact]
    public void StatusQueryHidesUnknownForeignAndCrossSessionPlansAndRejectsMalformedIds()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "owned-plan", isOneTime: true);
        SeedOwnedPlan(database.Store, "foreign-plan", isOneTime: true, sid: "S-1-5-21-other", session: "other-session");
        SeedOwnedPlan(database.Store, "other-session-plan", isOneTime: true, sid: Sid, session: "other-session");
        var query = new PlanExecutionStatusQueryService(database.Store);

        Assert.Null(query.Get("missing-plan", Sid, Session));
        Assert.Null(query.Get("foreign-plan", Sid, Session));
        Assert.Null(query.Get("other-session-plan", Sid, Session));
        Assert.Null(query.Get("owned-plan", "S-1-5-21-other", Session));
        Assert.Null(query.Get("owned-plan", Sid, "other-session"));
        Assert.Equal("invalid_argument", Assert.Throws<Phase3PersistenceException>(() =>
            query.Get("bad%2Fid", Sid, Session)).Code);
    }

    [Fact]
    public void BrokenRunRelationFailsClosedInsteadOfReturningACompletedOccurrence()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "broken-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "broken-plan", "broken-occ", "broken-run",
            "run_created", "recording", Created);
        Execute(database.Store, "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET run_id = 'missing-run' WHERE id = 'broken-occ';");

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("broken-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public async Task ReadSnapshotDoesNotMixOccurrenceAndRunAcrossConcurrentTerminalCommit()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "snapshot-plan", isOneTime: true);
        SeedOccurrenceAndRun(database.Store, "snapshot-plan", "snapshot-occ", "snapshot-run",
            "run_created", "recording", Created);

        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() =>
        {
            using var connection = database.Store.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE recording_runs
                    SET status_code = 'failed', terminal_reason_code = 'capture_failed', updated_at_utc = $updated, version = version + 1
                    WHERE id = 'snapshot-run';
                    UPDATE plan_occurrences
                    SET status_code = 'blocked', terminal_reason_code = 'capture_failed', updated_at_utc = $updated, version = version + 1
                    WHERE id = 'snapshot-occ';
                    """;
                command.Parameters.AddWithValue("$updated", Created.AddSeconds(2).UtcDateTime.Ticks);
                Assert.Equal(2, command.ExecuteNonQuery());
            }
            staged.SetResult();
            allowCommit.Task.GetAwaiter().GetResult();
            transaction.Commit();
        });

        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var query = new PlanExecutionStatusQueryService(database.Store);
            var whileWriterIsUncommitted = query.Get("snapshot-plan", Sid, Session)!;
            Assert.Equal("run_created", whileWriterIsUncommitted.LatestOccurrence!.Status);
            Assert.Equal("recording", whileWriterIsUncommitted.LatestOccurrence.Run!.Status);

            allowCommit.SetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
            var afterCommit = query.Get("snapshot-plan", Sid, Session)!;
            Assert.Equal("blocked", afterCommit.LatestOccurrence!.Status);
            Assert.Equal("failed", afterCommit.LatestOccurrence.Run!.Status);
            Assert.Equal("capture_failed", afterCommit.LatestOccurrence.TerminalReasonCode);
            Assert.Null(afterCommit.LatestOccurrence.OutputPath);
        }
        finally
        {
            allowCommit.TrySetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void DuplicateSetupOwnershipEvidenceFailsClosed()
    {
        using var database = new TestDatabase();
        SeedOwnedPlan(database.Store, "ambiguous-plan", isOneTime: true);
        InsertSetupIntent(database.Store, "second-setup", "ambiguous-plan", Sid, Session, "second-key");

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            new PlanExecutionStatusQueryService(database.Store).Get("ambiguous-plan", Sid, Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    [Fact]
    public void RecurringStatusSeparatesLatestNextAndExhaustedCursorAfterRestart()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "status-recurring-intent";
        fixture.Create(intentId, prepared: true);
        fixture.Activate(intentId);
        var setup = fixture.Query.Get(intentId, RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session)!;
        var planId = setup.PlanId!;
        var advancement = new SqliteRecurringAdvancementTransaction(fixture.Store);

        var first = advancement.AdvanceOne(planId, 1, "status-advance-1", 0,
            RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(10));
        var second = advancement.AdvanceOne(planId, 1, "status-advance-2", 1,
            RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(20));
        Assert.Equal("scheduled", first.ResultCode);
        Assert.Equal("scheduled", second.ResultCode);

        var occurrenceRepository = new SqlitePlanOccurrenceRepository(fixture.Store);
        var generated = new[]
        {
            occurrenceRepository.Get(first.OccurrenceIdentity!),
            occurrenceRepository.Get(second.OccurrenceIdentity!),
        };
        var chronological = generated
            .OrderBy(item => item.WindowStartUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        MarkMissed(fixture.Store, chronological[0].Id, RecurringPlanSetupTestFixture.At(30));
        var query = new PlanExecutionStatusQueryService(fixture.Store);
        var partial = query.Get(planId, RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session)!;
        Assert.Equal("daily", partial.Kind);
        Assert.True(partial.ScheduleExhausted);
        Assert.Equal(2, partial.OccurrenceCount);
        Assert.Equal(chronological[1].Id, partial.LatestOccurrence!.OccurrenceId);
        Assert.Equal(chronological[1].Id, partial.NextOccurrence!.OccurrenceId);
        Assert.Equal("scheduled", partial.LatestOccurrence.Status);
        Assert.Equal(PlanOccurrenceStatus.Missed, occurrenceRepository.Get(chronological[0].Id).Status);
        Assert.Null(partial.NextOccurrence.Run);

        MarkMissed(fixture.Store, chronological[1].Id, RecurringPlanSetupTestFixture.At(40));
        var reopenedStore = new SqliteOperationalStore(fixture.Store.DatabasePath);
        var afterRestart = new PlanExecutionStatusQueryService(reopenedStore).Get(
            planId, RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session)!;
        Assert.True(afterRestart.ScheduleExhausted);
        Assert.Null(afterRestart.NextOccurrence);
        Assert.Equal(chronological[1].Id, afterRestart.LatestOccurrence!.OccurrenceId);
        Assert.Equal("missed", afterRestart.LatestOccurrence.Status);
        Assert.Equal(2, afterRestart.OccurrenceCount);
    }

    [Fact]
    public void RecurringStatusFailsClosedWhenAdvancementHistoryHasNoCursor()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "status-missing-cursor-intent";
        fixture.Create(intentId, prepared: true);
        fixture.Activate(intentId);
        var planId = fixture.Query.Get(intentId, RecurringPlanSetupTestFixture.Sid,
            RecurringPlanSetupTestFixture.Session)!.PlanId!;
        const long scheduleRevision = 1;
        var result = new SqliteRecurringAdvancementTransaction(fixture.Store).AdvanceOne(
            planId, scheduleRevision, "status-missing-cursor-advance", 0,
            RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(10));
        Assert.Equal("scheduled", result.ResultCode);

        fixture.Corrupt($"DELETE FROM recurring_schedule_cursors WHERE plan_id = '{planId}' AND schedule_revision = {scheduleRevision};");
        var exception = Assert.Throws<Phase3PersistenceException>(() => new PlanExecutionStatusQueryService(fixture.Store)
            .Get(planId, RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session));
        Assert.Equal("plan_status_data_invalid", exception.Code);
    }

    private static void MarkMissed(SqliteOperationalStore store, string occurrenceId, DateTimeOffset at)
    {
        var repository = new SqlitePlanOccurrenceRepository(store);
        var occurrence = repository.Get(occurrenceId);
        if (occurrence.Status == PlanOccurrenceStatus.Scheduled)
        {
            var version = occurrence.Version;
            Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, at).Succeeded);
            repository.Update(occurrence, version);
        }
        occurrence = repository.Get(occurrenceId);
        var expectedVersion = occurrence.Version;
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Missed, at.AddSeconds(1), "window_elapsed").Succeeded);
        repository.Update(occurrence, expectedVersion);
    }

    private static void SeedOwnedPlan(SqliteOperationalStore store, string planId, bool isOneTime,
        string? outputPath = null, string sid = Sid, string session = Session)
    {
        var plan = new PlanDefinition(planId, isOneTime, Created);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, Created.AddSeconds(1)).Succeeded);
        new SqlitePlanDefinitionRepository(store).Insert(plan);
        InsertSetupIntent(store, "setup-" + planId, planId, sid, session, "key-" + planId, outputPath);
    }

    private static void InsertSetupIntent(SqliteOperationalStore store, string intentId, string planId,
        string sid, string session, string key, string? outputPath = null)
    {
        var at = Created.UtcDateTime.Ticks;
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup_intents
                (intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid,
                 session_binding, status_code, requested_at_utc, expires_at_utc, plan_id,
                 created_at_utc, updated_at_utc, version, output_directory, frozen_file_name)
            VALUES
                ($intent, 'standing_once_fixed_region', $key, $digest, $sid, $session,
                 'activated', $requested, $expires, $plan, $requested, $requested, 0,
                 $directory, $file_name);
            """;
        command.Parameters.AddWithValue("$intent", intentId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$digest", new string('a', 64));
        command.Parameters.AddWithValue("$sid", sid);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$requested", at);
        command.Parameters.AddWithValue("$expires", at + TimeSpan.TicksPerDay);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$directory", outputPath is null ? DBNull.Value : Path.GetDirectoryName(outputPath)!);
        command.Parameters.AddWithValue("$file_name", outputPath is null ? DBNull.Value : Path.GetFileName(outputPath));
        command.ExecuteNonQuery();
    }

    private static void SeedOccurrence(SqliteOperationalStore store, string planId, string occurrenceId,
        string status, string? reason, DateTimeOffset at)
    {
        var start = at.UtcDateTime.Ticks;
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plan_occurrences
                (id, plan_id, status_code, window_start_utc, window_end_utc, run_id,
                 terminal_reason_code, created_at_utc, updated_at_utc, version)
            VALUES ($id, $plan, $status, $start, $end, NULL, $reason, $created, $updated, 1);
            """;
        command.Parameters.AddWithValue("$id", occurrenceId);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", start + TimeSpan.TicksPerMinute);
        command.Parameters.AddWithValue("$reason", reason is null ? DBNull.Value : reason);
        command.Parameters.AddWithValue("$created", start);
        command.Parameters.AddWithValue("$updated", start + 1);
        command.ExecuteNonQuery();
    }

    private static void SeedOccurrenceAndRun(SqliteOperationalStore store, string planId,
        string occurrenceId, string runId, string occurrenceStatus, string runStatus,
        DateTimeOffset at, string? mediaArtifactId = null, string? occurrenceReason = null)
    {
        var start = at.UtcDateTime.Ticks;
        using var connection = store.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plan_occurrences
                (id, plan_id, status_code, window_start_utc, window_end_utc, run_id,
                 terminal_reason_code, created_at_utc, updated_at_utc, version)
            VALUES ($occ, $plan, $occ_status, $start, $end, $run, $occ_reason, $created, $updated, 1);
            INSERT INTO recording_runs
                (id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id,
                 bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version)
            VALUES ($run, $occ, $run_status, $crossed, $artifact, NULL, $run_reason,
                    $created, $updated, 1);
            """;
        command.Parameters.AddWithValue("$occ", occurrenceId);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$occ_status", occurrenceStatus);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", start + TimeSpan.TicksPerMinute);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$occ_reason", occurrenceReason is null ? DBNull.Value : occurrenceReason);
        command.Parameters.AddWithValue("$created", start);
        command.Parameters.AddWithValue("$updated", start + 1);
        command.Parameters.AddWithValue("$run_status", runStatus);
        command.Parameters.AddWithValue("$crossed", runStatus is "recording" or "finalizing" or "media_ready" or "settled" or "session_interrupted" ? 1 : 0);
        command.Parameters.AddWithValue("$artifact", mediaArtifactId is null ? DBNull.Value : mediaArtifactId);
        command.Parameters.AddWithValue("$run_reason", runStatus is "failed" or "session_interrupted" or "started_unknown" ? "capture_failed" : DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void Execute(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void DowngradeToV15AndReinitialize(SqliteOperationalStore store)
    {
        using (var connection = store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE recording_run_output_evidence; DELETE FROM schema_migrations WHERE version = 16;";
            command.ExecuteNonQuery();
        }

        store.Initialize();
        using var verify = store.OpenConnection();
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM recording_run_output_evidence WHERE evidence_kind_code = 'legacy_path_unavailable' AND output_path IS NULL;";
        Assert.True(Convert.ToInt64(verifyCommand.ExecuteScalar()) > 0);
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = Path.Combine(Path.GetTempPath(), "AgentRecorderPlanStatus_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new SqliteOperationalStore(Path.Combine(Root, SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();
        }

        internal string Root { get; }
        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
