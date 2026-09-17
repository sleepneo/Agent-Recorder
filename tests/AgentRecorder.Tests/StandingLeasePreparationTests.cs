using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingLeasePreparationTests
{
    [Fact]
    public void ValidSelectionAtomicallyCreatesPendingChainAndBindsIntent()
    {
        using var database = new TemporaryDatabase();
        CreateIntent(database.Store);
        var result = CreateService(database.Store, At(2)).Prepare(CreateSelection());

        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, result.Status);
        Assert.True(result.Changed);
        Assert.Equal("intent-1", result.IntentId);
        Assert.Equal("plan-1", result.PlanId);
        Assert.Equal("occurrence-1", result.OccurrenceId);
        Assert.Equal("lease-1", result.LeaseId);
        Assert.Equal("scope-1", result.ScopeId);
        Assert.NotNull(result.ScopeDigest);

        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans WHERE status_code = 'draft' AND is_one_time = 1 AND version = 0;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences WHERE status_code = 'pending_lease_approval' AND run_id IS NULL AND terminal_reason_code IS NULL AND version = 0;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases WHERE status_code = 'pending' AND max_uses = 1 AND version = 0;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes WHERE scope_id = 'scope-1';"));
        Assert.Equal("lease_approval_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal(result.ScopeDigest, ReadText(database.Store, "SELECT scope_digest FROM authorized_capture_scopes;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));

        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        Assert.Equal(new AuthorizedPhysicalRectangle(0, 0, 1920, 1080), scope.DisplayBounds);
        Assert.Equal(new AuthorizedPhysicalRectangle(10, 20, 640, 480), scope.RegionWithinDisplay);
        Assert.Equal(96, scope.DpiX);
        Assert.Equal(96, scope.DpiY);
        Assert.Equal("display-a", scope.StableDisplayFingerprint);
        Assert.Equal(new string('a', 64), scope.TopologyDigest);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AgentRecorderPreparation", "captures")) + Path.DirectorySeparatorChar, scope.OutputDirectory);
        Assert.Equal("capture.mp4", scope.FrozenFileName);
    }

    [Fact]
    public void SameDurableChainReturnsExistingWithoutChangingVersionsOrTimes()
    {
        using var database = new TemporaryDatabase();
        CreateIntent(database.Store);
        var service = CreateService(database.Store, At(2));
        var first = service.Prepare(CreateSelection());
        var before = ReadIntentAndCounts(database.Store);

        var retry = new StandingLeasePreparationService(database.Store, () => At(3), new FixedIds());
        var second = retry.Prepare(CreateSelection());

        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, first.Status);
        Assert.Equal(StandingLeasePreparationResultStatus.Existing, second.Status);
        Assert.False(second.Changed);
        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.ScopeDigest, second.ScopeDigest);
        var after = ReadIntentAndCounts(database.Store);
        Assert.Equal(before.Plans, after.Plans);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.Leases, after.Leases);
        Assert.Equal(before.Scopes, after.Scopes);
        Assert.Equal(before.Intent, after.Intent);
    }

    [Fact]
    public void SelectionMismatchAndExpiredIntentDoNotWriteOrRepair()
    {
        using var mismatchDatabase = new TemporaryDatabase();
        CreateIntent(mismatchDatabase.Store);
        var prepared = CreateService(mismatchDatabase.Store, At(2)).Prepare(CreateSelection());
        var before = ReadIntentAndCounts(mismatchDatabase.Store);
        var mismatch = CreateService(mismatchDatabase.Store, At(3)).Prepare(CreateSelection(region: new AuthorizedPhysicalRectangle(11, 20, 640, 480)));
        Assert.Equal(StandingLeasePreparationResultStatus.Conflict, mismatch.Status);
        var mismatchAfter = ReadIntentAndCounts(mismatchDatabase.Store);
        Assert.Equal(before.Plans, mismatchAfter.Plans);
        Assert.Equal(before.Occurrences, mismatchAfter.Occurrences);
        Assert.Equal(before.Leases, mismatchAfter.Leases);
        Assert.Equal(before.Scopes, mismatchAfter.Scopes);
        Assert.Equal(before.Intent, mismatchAfter.Intent);
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);

        using var expiredDatabase = new TemporaryDatabase();
        CreateIntent(expiredDatabase.Store, expiresAtUtc: At(2));
        var expired = CreateService(expiredDatabase.Store, At(2)).Prepare(CreateSelection());
        Assert.Equal(StandingLeasePreparationResultStatus.Expired, expired.Status);
        Assert.Equal(0L, Scalar(expiredDatabase.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal("region_selection_pending", ReadText(expiredDatabase.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(0L, Scalar(expiredDatabase.Store, "SELECT version FROM setup_intents;"));
    }

    [Fact]
    public void InvalidGeometryAndCommitFailureRollBackAllObjects()
    {
        using var invalidDatabase = new TemporaryDatabase();
        CreateIntent(invalidDatabase.Store);
        var invalid = CreateService(invalidDatabase.Store, At(2)).Prepare(CreateSelection(region: new AuthorizedPhysicalRectangle(1800, 20, 640, 480)));
        Assert.Equal(StandingLeasePreparationResultStatus.Rejected, invalid.Status);
        Assert.Equal(0L, Scalar(invalidDatabase.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal("region_selection_pending", ReadText(invalidDatabase.Store, "SELECT status_code FROM setup_intents;"));

        using var failureDatabase = new TemporaryDatabase();
        CreateIntent(failureDatabase.Store);
        var failure = new StandingLeasePreparationService(
            failureDatabase.Store,
            () => At(2),
            new FixedIds(),
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("commit injection"));
        var result = failure.Prepare(CreateSelection());
        Assert.Equal(StandingLeasePreparationResultStatus.Rejected, result.Status);
        Assert.Equal(0L, Scalar(failureDatabase.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(failureDatabase.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal("region_selection_pending", ReadText(failureDatabase.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(0L, Scalar(failureDatabase.Store, "SELECT version FROM setup_intents;"));
    }

    [Fact]
    public void PreparedChainRemainsCompatibleWithSeparateLocalApprovalActivation()
    {
        using var database = new TemporaryDatabase();
        CreateIntent(database.Store);
        var prepared = CreateService(database.Store, At(2)).Prepare(CreateSelection());
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);

        var receipt = StandingLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-1", prepared.PlanId!, prepared.OccurrenceId!, prepared.LeaseId!, prepared.ScopeId!, prepared.ScopeDigest!,
            "S-1-5-21-test", "session-1", At(3));
        var request = new StandingLeaseAuthorizationActivationRequest(
            prepared.PlanId!, prepared.OccurrenceId!, prepared.LeaseId!, prepared.ScopeId!, prepared.ScopeDigest!, receipt);
        var activation = new StandingLeaseAuthorizationActivationService(database.Store, () => At(3)).Activate(request);

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, activation.Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void BoundaryDoesNotExposePublicConstructionOrExecutionSecrets()
    {
        foreach (var type in new[]
        {
            typeof(StandingFixedRegionSelectionSnapshot),
            typeof(StandingLeasePreparationService),
            typeof(StandingLeasePreparationResult),
        })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        }

        var names = typeof(StandingFixedRegionSelectionSnapshot).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Concat(typeof(StandingLeasePreparationResult).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("RunId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("UseId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("NativeHandle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("Approved", StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateIntent(SqliteOperationalStore store, DateTimeOffset? expiresAtUtc = null)
    {
        var snapshot = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "intent-1", "request-1", "S-1-5-21-test", "session-1", At(0), expiresAtUtc ?? At(300),
            At(10), At(20), At(80), TimeSpan.FromSeconds(30), At(3600),
            Path.Combine(Path.GetTempPath(), "AgentRecorderPreparation", "captures"), "capture.mp4");
        var result = new StandingSetupIntentService(store, () => At(1)).CreateOrGet(snapshot);
        Assert.Equal(StandingSetupIntentResultStatus.Created, result.Result);
    }

    private static StandingLeasePreparationService CreateService(SqliteOperationalStore store, DateTimeOffset now) =>
        new(store, () => now, new FixedIds());

    private static StandingFixedRegionSelectionSnapshot CreateSelection(
        AuthorizedPhysicalRectangle? region = null) =>
        StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            "intent-1", "S-1-5-21-test", "session-1", At(2), "display-a",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            region ?? new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64));

    private static (object?[] Intent, long Plans, long Occurrences, long Leases, long Scopes) ReadIntentAndCounts(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_code, plan_id, occurrence_id, lease_id, scope_id, updated_at_utc, version FROM setup_intents;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var values = Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
        return (values, Scalar(store, "SELECT COUNT(*) FROM plans;"), Scalar(store, "SELECT COUNT(*) FROM plan_occurrences;"), Scalar(store, "SELECT COUNT(*) FROM consent_leases;"), Scalar(store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
    }

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
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

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderStandingPreparation_" + Guid.NewGuid().ToString("N"));

        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            using var safetyConnection = Store.OpenConnection();
            using var safetyCommand = safetyConnection.CreateCommand();
            safetyCommand.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';";
            safetyCommand.ExecuteNonQuery();
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
