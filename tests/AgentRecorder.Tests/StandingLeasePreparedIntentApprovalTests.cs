using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingLeasePreparedIntentApprovalTests
{
    [Fact]
    public void PreparedIntentApprovalAtomicallyActivatesThreeAggregatesAndIntent()
    {
        using var database = CreatePreparedDatabase();
        var before = ReadState(database.Store);
        var result = database.CreateApprovalService(At(3)).Activate("intent-1", database.CreateReceipt());

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, result.Status);
        Assert.Equal("activated", result.Reason);
        Assert.True(result.Changed);
        Assert.Equal(PlanDefinitionStatus.Enabled, new SqlitePlanDefinitionRepository(database.Store).Get("plan-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Authorized, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(ConsentLeaseStatus.Active, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal("activated", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal(At(3).UtcDateTime.Ticks, Scalar(database.Store, "SELECT updated_at_utc FROM setup_intents;"));
        Assert.Equal("plan-1", ReadText(database.Store, "SELECT plan_id FROM setup_intents;"));
        Assert.Equal("occurrence-1", ReadText(database.Store, "SELECT occurrence_id FROM setup_intents;"));
        Assert.Equal("lease-1", ReadText(database.Store, "SELECT lease_id FROM setup_intents;"));
        Assert.Equal("scope-1", ReadText(database.Store, "SELECT scope_id FROM setup_intents;"));
        Assert.Null(ReadNullable(database.Store, "SELECT terminal_reason_code FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.NotEqual(before, ReadState(database.Store));
    }

    [Fact]
    public void ReceiptAndIntentBindingMismatchesRejectWithoutWrites()
    {
        foreach (var receiptFactory in new Func<PreparedDatabase, StandingLeaseLocalApprovalReceipt>[]
        {
            database => database.CreateReceipt(planId: "other-plan"),
            database => database.CreateReceipt(scopeDigest: new string('b', 64)),
            database => database.CreateReceipt(currentUserSid: "S-1-5-21-other"),
            database => database.CreateReceipt(sessionBinding: "session-other"),
        })
        {
            using var database = CreatePreparedDatabase();
            var before = ReadState(database.Store);
            var result = database.CreateApprovalService(At(3)).Activate("intent-1", receiptFactory(database));

            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_receipt_identity_mismatch", result.Reason);
            Assert.Equal(before, ReadState(database.Store));
        }

        using var wrongIntentDatabase = CreatePreparedDatabase();
        var wrongIntentBefore = ReadState(wrongIntentDatabase.Store);
        var wrongIntent = wrongIntentDatabase.CreateApprovalService(At(3)).Activate("other-intent", wrongIntentDatabase.CreateReceipt());
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, wrongIntent.Status);
        Assert.Equal("activation_intent_not_found", wrongIntent.Reason);
        Assert.Equal(wrongIntentBefore, ReadState(wrongIntentDatabase.Store));

        using var tamperedIntentDatabase = CreatePreparedDatabase();
        Execute(tamperedIntentDatabase.Store, "UPDATE setup_intents SET request_digest = $digest WHERE intent_id = 'intent-1';", ("$digest", new string('b', 64)));
        var tamperedIntentBefore = ReadState(tamperedIntentDatabase.Store);
        var tamperedIntent = tamperedIntentDatabase.CreateApprovalService(At(3)).Activate("intent-1", tamperedIntentDatabase.CreateReceipt());
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, tamperedIntent.Status);
        Assert.Equal("activation_snapshot_invalid", tamperedIntent.Reason);
        Assert.Equal(tamperedIntentBefore, ReadState(tamperedIntentDatabase.Store));
    }

    [Fact]
    public void RepeatedApprovalIsAlreadyActiveAndLeavesAllVersionsAndTimesUntouched()
    {
        using var database = CreatePreparedDatabase();
        var receipt = database.CreateReceipt();
        var service = database.CreateApprovalService(At(3));
        var first = service.Activate("intent-1", receipt);
        var before = ReadState(database.Store);
        var second = new StandingLeasePreparedIntentApprovalActivationService(database.Store, () => At(4000))
            .Activate("intent-1", receipt);

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, first.Status);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.AlreadyActive, second.Status);
        Assert.Equal("already_active", second.Reason);
        Assert.False(second.Changed);
        Assert.Equal(before, ReadState(database.Store));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;") + Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void InvalidIntentStatePartialChainMissingScopeAndExpiredLeaseFailClosed()
    {
        using (var pending = CreatePreparedDatabase())
        {
            Execute(pending.Store, "UPDATE setup_intents SET plan_id = NULL WHERE intent_id = 'intent-1';");
            var before = ReadState(pending.Store);
            var result = pending.CreateApprovalService(At(3)).Activate("intent-1", pending.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Conflict, result.Status);
            Assert.Equal(before, ReadState(pending.Store));
        }

        using (var missingScope = CreatePreparedDatabase())
        {
            Execute(missingScope.Store, "PRAGMA foreign_keys = OFF; DELETE FROM authorized_capture_scopes WHERE scope_id = 'scope-1';");
            var before = ReadState(missingScope.Store);
            var result = missingScope.CreateApprovalService(At(3)).Activate("intent-1", missingScope.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Conflict, result.Status);
            Assert.Equal(before, ReadState(missingScope.Store));
        }

        foreach (var chainMutation in new[]
        {
            "PRAGMA foreign_keys = OFF; DELETE FROM plans WHERE id = 'plan-1';",
            "PRAGMA foreign_keys = OFF; DELETE FROM plan_occurrences WHERE id = 'occurrence-1';",
            "PRAGMA foreign_keys = OFF; DELETE FROM consent_leases WHERE id = 'lease-1';",
            "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET plan_id = 'other-plan' WHERE id = 'occurrence-1';",
            "PRAGMA foreign_keys = OFF; UPDATE consent_leases SET occurrence_id = 'other-occurrence' WHERE id = 'lease-1';",
        })
        {
            using var mismatchedChain = CreatePreparedDatabase();
            Execute(mismatchedChain.Store, chainMutation);
            var before = ReadState(mismatchedChain.Store);
            var result = mismatchedChain.CreateApprovalService(At(3)).Activate("intent-1", mismatchedChain.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Conflict, result.Status);
            Assert.Equal("activation_intent_conflict", result.Reason);
            Assert.Equal(before, ReadState(mismatchedChain.Store));
        }

        using (var wrongState = CreatePreparedDatabase())
        {
            Execute(wrongState.Store, "UPDATE setup_intents SET status_code = 'activated' WHERE intent_id = 'intent-1';");
            var before = ReadState(wrongState.Store);
            var result = wrongState.CreateApprovalService(At(3)).Activate("intent-1", wrongState.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Conflict, result.Status);
            Assert.Equal(before, ReadState(wrongState.Store));
        }

        foreach (var terminalStatus in new[] { "rejected", "expired" })
        {
            using var terminal = CreatePreparedDatabase();
            Execute(terminal.Store, "UPDATE setup_intents SET status_code = $status, terminal_reason_code = 'test_terminal' WHERE intent_id = 'intent-1';", ("$status", terminalStatus));
            var before = ReadState(terminal.Store);
            var result = terminal.CreateApprovalService(At(3)).Activate("intent-1", terminal.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_intent_state_invalid", result.Reason);
            Assert.Equal(before, ReadState(terminal.Store));
        }

        using (var intentExpired = CreatePreparedDatabase())
        {
            var before = ReadState(intentExpired.Store);
            var result = intentExpired.CreateApprovalService(At(300)).Activate("intent-1", intentExpired.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_intent_expired", result.Reason);
            Assert.Equal(before, ReadState(intentExpired.Store));
        }

        using (var expired = CreatePreparedDatabase(intentExpiresAtUtc: At(5000)))
        {
            var before = ReadState(expired.Store);
            var result = expired.CreateApprovalService(At(4000)).Activate("intent-1", expired.CreateReceipt(approvedAtUtc: At(3000)));
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_lease_expired", result.Reason);
            Assert.Equal(before, ReadState(expired.Store));
        }
    }

    [Fact]
    public void AggregateAndCommitFailureHooksRollBackTheWholeFiveObjectTransition()
    {
        using (var aggregateFailure = CreatePreparedDatabase(
            beforeIntentWriteForTest: (_, _) => throw new InvalidOperationException("intent-write injection")))
        {
            var before = ReadState(aggregateFailure.Store);
            var result = aggregateFailure.CreateApprovalService(At(3)).Activate("intent-1", aggregateFailure.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_sqlite_failure", result.Reason);
            Assert.Equal(before, ReadState(aggregateFailure.Store));
        }

        using (var commitFailure = CreatePreparedDatabase(
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("commit injection")))
        {
            var before = ReadState(commitFailure.Store);
            var result = commitFailure.CreateApprovalService(At(3)).Activate("intent-1", commitFailure.CreateReceipt());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_sqlite_failure", result.Reason);
            Assert.Equal(before, ReadState(commitFailure.Store));
        }
    }

    [Fact]
    public async Task ConcurrentIntentBoundApprovalCommitsAtMostOneActivation()
    {
        using var database = CreatePreparedDatabase();
        var first = database.CreateApprovalService(At(3));
        var second = database.CreateApprovalService(At(3));
        var receipt = database.CreateReceipt();
        var results = await Task.WhenAll(
            Task.Run(() => first.Activate("intent-1", receipt)),
            Task.Run(() => second.Activate("intent-1", receipt)));

        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseAuthorizationActivationStatus.Activated));
        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseAuthorizationActivationStatus.AlreadyActive));
        Assert.Equal("activated", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans WHERE status_code = 'enabled';"));
    }

    [Fact]
    public void NewBoundServiceAndResultRemainInternalAndDoNotExposeExecutionSecrets()
    {
        Assert.Empty(typeof(StandingLeasePreparedIntentApprovalActivationService).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(StandingLeasePreparedIntentApprovalActivationService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        var names = typeof(StandingLeaseAuthorizationActivationResult)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("RunId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("UseId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("NativeHandle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Capture", StringComparison.OrdinalIgnoreCase));
    }

    private static PreparedDatabase CreatePreparedDatabase(
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        DateTimeOffset? intentExpiresAtUtc = null)
    {
        var database = new PreparedDatabase(beforeWritesForTest, beforeIntentWriteForTest, beforeCommitForTest);
        var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "intent-1", "request-1", "S-1-5-21-test", "session-1", At(0), intentExpiresAtUtc ?? At(300),
            At(10), At(20), At(80), TimeSpan.FromSeconds(30), At(3600),
            Path.Combine(Path.GetTempPath(), "AgentRecorderApproval", "captures"), "capture.mp4");
        Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(intent).Result);
        var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            "intent-1", "S-1-5-21-test", "session-1", At(2), "display-a",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64));
        var prepared = new StandingLeasePreparationService(database.Store, () => At(2), new FixedIds()).Prepare(selection);
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);
        return database;
    }

    private static object?[] ReadState(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT status_code FROM setup_intents), (SELECT version FROM setup_intents), (SELECT updated_at_utc FROM setup_intents),
                   (SELECT status_code FROM plans), (SELECT version FROM plans), (SELECT updated_at_utc FROM plans),
                   (SELECT status_code FROM plan_occurrences), (SELECT version FROM plan_occurrences), (SELECT updated_at_utc FROM plan_occurrences),
                   (SELECT status_code FROM consent_leases), (SELECT version FROM consent_leases), (SELECT updated_at_utc FROM consent_leases);
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
    }

    private static long Scalar(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static object? ReadNullable(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void Execute(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private sealed class FixedIds : IStandingLeasePreparationIdProvider
    {
        public string CreatePlanId() => "plan-1";
        public string CreateOccurrenceId() => "occurrence-1";
        public string CreateLeaseId() => "lease-1";
        public string CreateScopeId() => "scope-1";
    }

    private sealed class PreparedDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderPreparedApproval_" + Guid.NewGuid().ToString("N"));
        private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
        private readonly Action<SqliteConnection, SqliteTransaction>? _beforeIntentWriteForTest;
        private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

        internal PreparedDatabase(
            Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest,
            Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest,
            Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest)
        {
            _beforeWritesForTest = beforeWritesForTest;
            _beforeIntentWriteForTest = beforeIntentWriteForTest;
            _beforeCommitForTest = beforeCommitForTest;
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            Execute(Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
        }

        internal SqliteOperationalStore Store { get; }

        internal StandingLeasePreparedIntentApprovalActivationService CreateApprovalService(DateTimeOffset nowUtc) =>
            new(Store, () => nowUtc, _beforeWritesForTest, _beforeIntentWriteForTest, _beforeCommitForTest);

        internal StandingLeaseLocalApprovalReceipt CreateReceipt(
            string planId = "plan-1",
            string occurrenceId = "occurrence-1",
            string leaseId = "lease-1",
            string scopeId = "scope-1",
            string? scopeDigest = null,
            string currentUserSid = "S-1-5-21-test",
            string sessionBinding = "session-1",
            DateTimeOffset? approvedAtUtc = null)
        {
            var digest = scopeDigest ?? ReadText(Store, "SELECT scope_digest FROM authorized_capture_scopes;");
            return StandingLeaseLocalApprovalReceipt.CreateForTest(
                "approval-1", planId, occurrenceId, leaseId, scopeId, digest,
                currentUserSid, sessionBinding, approvedAtUtc ?? At(3));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
