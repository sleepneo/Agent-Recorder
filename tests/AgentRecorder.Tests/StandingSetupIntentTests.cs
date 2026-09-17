using System.Reflection;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingSetupIntentTests
{
    [Fact]
    public void FirstCreatePersistsPendingIdentityAndLeavesCaptureAggregatesUntouched()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new StandingSetupIntentService(database.Store, () => At(1));

        var result = service.CreateOrGet(snapshot);

        Assert.Equal(StandingSetupIntentResultStatus.Created, result.Result);
        Assert.Equal(StandingSetupIntentStatus.RegionSelectionPending, result.IntentStatus);
        Assert.Equal("intent-1", result.IntentId);
        Assert.True(result.Changed);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));

        Assert.Equal(
            new object?[]
            {
                "intent-1", StandingSetupIntentCodes.IntentKind, "request-1", snapshot.RequestDigest,
                "S-1-5-21-test", "session-1", StandingSetupIntentCodes.InitialStatus,
                At(1).UtcDateTime.Ticks, At(300).UtcDateTime.Ticks, null, null, null, null,
                At(1).UtcDateTime.Ticks, At(1).UtcDateTime.Ticks, 0L, null,
            },
            ReadRow(database.Store));
    }

    [Fact]
    public void SameNormalizedRequestReturnsExistingWithoutRefreshingAnyDurableField()
    {
        using var database = new TemporaryDatabase();
        var firstSnapshot = CreateSnapshot();
        var service = new StandingSetupIntentService(database.Store, () => At(1));
        Assert.Equal(StandingSetupIntentResultStatus.Created, service.CreateOrGet(firstSnapshot).Result);
        var before = ReadRow(database.Store);

        var retrySnapshot = CreateSnapshot(intentId: "new-adapter-generated-id");
        var result = service.CreateOrGet(retrySnapshot);

        Assert.Equal(StandingSetupIntentResultStatus.Existing, result.Result);
        Assert.Equal(StandingSetupIntentStatus.RegionSelectionPending, result.IntentStatus);
        Assert.Equal("intent-1", result.IntentId);
        Assert.False(result.Changed);
        Assert.Equal(before, ReadRow(database.Store));
    }

    [Fact]
    public void SameIdentityWithChangedDigestOrExpiryConflictsWithoutMutation()
    {
        using var database = new TemporaryDatabase();
        var service = new StandingSetupIntentService(database.Store, () => At(1));
        Assert.Equal(StandingSetupIntentResultStatus.Created, service.CreateOrGet(CreateSnapshot()).Result);
        var before = ReadRow(database.Store);

        var changedDuration = CreateSnapshot(maximumDuration: TimeSpan.FromSeconds(31));
        var digestConflict = service.CreateOrGet(changedDuration);
        Assert.Equal(StandingSetupIntentResultStatus.Conflict, digestConflict.Result);
        Assert.Equal("setup_intent_request_conflict", digestConflict.Reason);
        Assert.Equal(before, ReadRow(database.Store));

        var changedExpiry = CreateSnapshot(expiresAtUtc: At(301));
        var expiryConflict = service.CreateOrGet(changedExpiry);
        Assert.Equal(StandingSetupIntentResultStatus.Conflict, expiryConflict.Result);
        Assert.Equal(before, ReadRow(database.Store));
    }

    [Fact]
    public void DifferentUsersAndSessionsHaveIndependentIdempotencyNamespaces()
    {
        using var database = new TemporaryDatabase();
        var service = new StandingSetupIntentService(database.Store, () => At(1));
        Assert.Equal(StandingSetupIntentResultStatus.Created, service.CreateOrGet(CreateSnapshot()).Result);
        Assert.Equal(
            StandingSetupIntentResultStatus.Created,
            service.CreateOrGet(CreateSnapshot(intentId: "intent-user-2", currentUserSid: "S-1-5-21-other")).Result);
        Assert.Equal(
            StandingSetupIntentResultStatus.Created,
            service.CreateOrGet(CreateSnapshot(intentId: "intent-session-2", sessionBinding: "session-2")).Result);
        Assert.Equal(3L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public void ExistingStatusesAreReadOnlyAndPendingExpiryIsProjectedWithoutTransition()
    {
        using var database = new TemporaryDatabase();
        var service = new StandingSetupIntentService(database.Store, () => At(1));
        Assert.Equal(StandingSetupIntentResultStatus.Created, service.CreateOrGet(CreateSnapshot()).Result);

        SetStatus(database.Store, StandingSetupIntentCodes.LeaseApprovalPendingStatus, version: 1, terminalReason: null);
        var approvalPending = service.CreateOrGet(CreateSnapshot());
        Assert.Equal(StandingSetupIntentResultStatus.Existing, approvalPending.Result);
        Assert.Equal(StandingSetupIntentStatus.LeaseApprovalPending, approvalPending.IntentStatus);

        SetStatus(database.Store, StandingSetupIntentCodes.ActivatedStatus, version: 2, terminalReason: null);
        var activated = service.CreateOrGet(CreateSnapshot());
        Assert.Equal(StandingSetupIntentResultStatus.Existing, activated.Result);
        Assert.Equal(StandingSetupIntentStatus.Activated, activated.IntentStatus);

        SetStatus(database.Store, StandingSetupIntentCodes.RejectedStatus, version: 3, terminalReason: "user_rejected");
        var rejected = service.CreateOrGet(CreateSnapshot());
        Assert.Equal(StandingSetupIntentResultStatus.Existing, rejected.Result);
        Assert.Equal(StandingSetupIntentStatus.Rejected, rejected.IntentStatus);

        SetStatus(database.Store, StandingSetupIntentCodes.ExpiredStatus, version: 4, terminalReason: "expired");
        var expiredState = service.CreateOrGet(CreateSnapshot());
        Assert.Equal(StandingSetupIntentResultStatus.Existing, expiredState.Result);
        Assert.Equal(StandingSetupIntentStatus.Expired, expiredState.IntentStatus);

        using var pendingDatabase = new TemporaryDatabase();
        var pendingSnapshot = CreateSnapshot(expiresAtUtc: At(2));
        var createService = new StandingSetupIntentService(pendingDatabase.Store, () => At(1));
        Assert.Equal(StandingSetupIntentResultStatus.Created, createService.CreateOrGet(pendingSnapshot).Result);
        var expiredReadService = new StandingSetupIntentService(pendingDatabase.Store, () => At(2));
        var expiredProjection = expiredReadService.CreateOrGet(CreateSnapshot(expiresAtUtc: At(2)));
        Assert.Equal(StandingSetupIntentResultStatus.Expired, expiredProjection.Result);
        Assert.Equal(StandingSetupIntentStatus.Expired, expiredProjection.IntentStatus);
        Assert.Equal(StandingSetupIntentCodes.InitialStatus, ReadStatus(pendingDatabase.Store));
        Assert.Equal(0L, ReadVersion(pendingDatabase.Store));
    }

    [Fact]
    public void InvalidSnapshotPoliciesDigestAndClockFailClosedWithoutWrites()
    {
        using var database = new TemporaryDatabase();
        var valid = CreateSnapshot();

        var invalidPolicy = StandingSetupIntentSnapshot.CreateForTest(
            valid.IntentId,
            valid.IdempotencyKey,
            valid.CurrentUserSid,
            valid.SessionBinding,
            valid.RequestedAtUtc,
            valid.ExpiresAtUtc,
            valid.ScheduledStartUtc,
            valid.LatestStartUtc,
            valid.PlannedEndUtc,
            valid.MaximumDuration,
            valid.LeaseValidUntilUtc,
            valid.OutputDirectory,
            valid.FrozenFileName,
            backendCode: "wgc",
            requestDigest: valid.RequestDigest);
        var invalidResult = new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(invalidPolicy);
        Assert.Equal(StandingSetupIntentResultStatus.Rejected, invalidResult.Result);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));

        var invalidDigest = StandingSetupIntentSnapshot.CreateForTest(
            valid.IntentId,
            valid.IdempotencyKey,
            valid.CurrentUserSid,
            valid.SessionBinding,
            valid.RequestedAtUtc,
            valid.ExpiresAtUtc,
            valid.ScheduledStartUtc,
            valid.LatestStartUtc,
            valid.PlannedEndUtc,
            valid.MaximumDuration,
            valid.LeaseValidUntilUtc,
            valid.OutputDirectory,
            valid.FrozenFileName,
            requestDigest: new string('f', 64));
        Assert.Equal(
            StandingSetupIntentResultStatus.Rejected,
            new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(invalidDigest).Result);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));

        var unavailableClock = new StandingSetupIntentService(database.Store, () => null);
        Assert.Equal(StandingSetupIntentResultStatus.Rejected, unavailableClock.CreateOrGet(valid).Result);
        var nonUtcClock = new StandingSetupIntentService(database.Store, () => At(1).ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(StandingSetupIntentResultStatus.Rejected, nonUtcClock.CreateOrGet(valid).Result);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public void WriteAndCommitFailuresRollBackTheIntent()
    {
        using var writeFailureDatabase = new TemporaryDatabase();
        var writeFailure = new StandingSetupIntentService(
            writeFailureDatabase.Store,
            () => At(1),
            beforeWritesForTest: (_, _) => throw new InvalidOperationException("injected write failure"));
        Assert.Equal(StandingSetupIntentResultStatus.Rejected, writeFailure.CreateOrGet(CreateSnapshot()).Result);
        Assert.Equal(0L, Scalar(writeFailureDatabase.Store, "SELECT COUNT(*) FROM setup_intents;"));

        using var commitFailureDatabase = new TemporaryDatabase();
        var commitFailure = new StandingSetupIntentService(
            commitFailureDatabase.Store,
            () => At(1),
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("injected commit failure"));
        Assert.Equal(StandingSetupIntentResultStatus.Rejected, commitFailure.CreateOrGet(CreateSnapshot()).Result);
        Assert.Equal(0L, Scalar(commitFailureDatabase.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public async Task ConcurrentCreateOrGetCreatesAtMostOneIntent()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var first = new StandingSetupIntentService(database.Store, () => At(1));
        var second = new StandingSetupIntentService(database.Store, () => At(1));

        var results = await Task.WhenAll(
            Task.Run(() => first.CreateOrGet(snapshot)),
            Task.Run(() => second.CreateOrGet(snapshot)));

        Assert.Equal(1, results.Count(result => result.Result == StandingSetupIntentResultStatus.Created));
        Assert.Equal(1, results.Count(result => result.Result == StandingSetupIntentResultStatus.Existing));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public void InternalBoundaryDoesNotExposePublicConstructorsOrExecutionSecrets()
    {
        foreach (var type in new[]
        {
            typeof(StandingSetupIntentSnapshot),
            typeof(StandingSetupIntentService),
            typeof(StandingSetupIntentResult),
        })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        }

        var names = typeof(StandingSetupIntentSnapshot).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Concat(typeof(StandingSetupIntentResult).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("RunId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("UseId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("NativeHandle", StringComparison.OrdinalIgnoreCase));
    }

    private static StandingSetupIntentSnapshot CreateSnapshot(
        string intentId = "intent-1",
        string idempotencyKey = "request-1",
        string currentUserSid = "S-1-5-21-test",
        string sessionBinding = "session-1",
        DateTimeOffset? expiresAtUtc = null,
        TimeSpan? maximumDuration = null) =>
        StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            intentId,
            idempotencyKey,
            currentUserSid,
            sessionBinding,
            At(0),
            expiresAtUtc ?? At(300),
            At(10),
            At(20),
            At(80),
            maximumDuration ?? TimeSpan.FromSeconds(30),
            At(3600),
            Path.Combine(Path.GetTempPath(), "AgentRecorderSetupIntent", "captures"),
            "capture.mp4");

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static object?[] ReadRow(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc, version, terminal_reason_code FROM setup_intents;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var values = Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))
            .ToArray();
        return values;
    }

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadStatus(SqliteOperationalStore store) =>
        ReadScalar(store, "SELECT status_code FROM setup_intents;");

    private static long ReadVersion(SqliteOperationalStore store) =>
        Convert.ToInt64(ReadScalar(store, "SELECT version FROM setup_intents;"));

    private static string ReadScalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void SetStatus(SqliteOperationalStore store, string status, long version, string? terminalReason)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE setup_intents SET status_code = $status, version = $version, updated_at_utc = $updated, terminal_reason_code = $reason;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$updated", At(1).UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$reason", terminalReason is null ? DBNull.Value : terminalReason);
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderStandingSetupIntent_" + Guid.NewGuid().ToString("N"));

        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
        }

        internal SqliteOperationalStore Store { get; }

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
