using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingLeaseAuthorizationActivationTests
{
    [Fact]
    public void ValidLocalApprovalAtomicallyActivatesAllThreeAggregatesWithoutExecutionEvidence()
    {
        using var database = new ActivationDatabase();

        var result = database.Service.Activate(database.CreateRequest());

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, result.Status);
        Assert.Equal("activated", result.Reason);
        Assert.True(result.Changed);
        AssertIdentity(database, result);
        Assert.Equal(PlanDefinitionStatus.Enabled, database.Plans.Get("plan-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Authorized, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(ConsentLeaseStatus.Active, database.Leases.Get("lease-1").Status);
        Assert.Equal(1L, database.Plans.Get("plan-1").Version);
        Assert.Equal(1L, database.Occurrences.Get("occ-1").Version);
        Assert.Equal(1L, database.Leases.Get("lease-1").Version);
        Assert.Equal(At(1), database.Plans.Get("plan-1").UpdatedAtUtc);
        Assert.Equal(At(1), database.Occurrences.Get("occ-1").UpdatedAtUtc);
        Assert.Equal(At(1), database.Leases.Get("lease-1").UpdatedAtUtc);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void IdenticalActivationIsAlreadyActiveWithoutConsumingReceiptOrChangingRows()
    {
        using var database = new ActivationDatabase();
        var request = database.CreateRequest();

        var first = database.Service.Activate(request);
        var before = ReadAggregateState(database);
        var second = database.Service.Activate(request);
        var after = ReadAggregateState(database);

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, first.Status);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.AlreadyActive, second.Status);
        Assert.Equal("already_active", second.Reason);
        Assert.False(second.Changed);
        Assert.Equal(before, after);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));

        var lateRetry = new StandingLeaseAuthorizationActivationService(database.Store, () => At(120));
        var afterExpiry = lateRetry.Activate(request);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.AlreadyActive, afterExpiry.Status);
        Assert.False(afterExpiry.Changed);
        Assert.Equal(before, ReadAggregateState(database));
    }

    [Fact]
    public void ActivationRequiresAValidReceiptBoundToThePersistedScopeAndUserSession()
    {
        var cases = new (string Name, Func<ActivationDatabase, StandingLeaseLocalApprovalReceipt?> ReceiptFactory, string Reason)[]
        {
            ("missing", _ => null, "activation_receipt_missing"),
            ("kind", database => database.CreateReceipt(approvalKind: "other_kind"), "activation_receipt_kind_unsupported"),
            ("version", database => database.CreateReceipt(approvalVersion: 2), "activation_receipt_kind_unsupported"),
            ("non-utc", database => database.CreateReceipt(approvedAtUtc: At(1).ToOffset(TimeSpan.FromHours(1))), "activation_receipt_time_not_utc"),
            ("future", database => database.CreateReceipt(approvedAtUtc: At(2)), "activation_receipt_time_in_future"),
            ("before-scope", database => database.CreateReceipt(approvedAtUtc: At(-1)), "activation_receipt_time_invalid"),
            ("wrong-identity", database => database.CreateReceipt(planId: "other-plan"), "activation_receipt_identity_mismatch"),
            ("wrong-digest", database => database.CreateReceipt(scopeDigest: new string('b', 64)), "activation_receipt_identity_mismatch"),
            ("wrong-sid", database => database.CreateReceipt(currentUserSid: "S-1-5-21-2"), "activation_receipt_identity_mismatch"),
            ("wrong-session", database => database.CreateReceipt(sessionBinding: "session-2"), "activation_receipt_identity_mismatch"),
        };

        foreach (var testCase in cases)
        {
            using var database = new ActivationDatabase();
            var request = new StandingLeaseAuthorizationActivationRequest(
                "plan-1",
                "occ-1",
                "lease-1",
                "scope-1",
                database.Scope.ScopeDigest,
                testCase.ReceiptFactory(database));

            var result = database.Service.Activate(request);

            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal(testCase.Reason, result.Reason);
            Assert.False(result.Changed);
            AssertPending(database);
            Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));
        }
    }

    [Fact]
    public void RequestIdentityAndPersistedScopeDigestMustMatchExactly()
    {
        using var database = new ActivationDatabase();

        var request = new StandingLeaseAuthorizationActivationRequest(
            "plan-1",
            "occ-1",
            "lease-1",
            "scope-1",
            new string('b', 64),
            database.CreateReceipt(scopeDigest: new string('b', 64)));

        var result = database.Service.Activate(request);

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
        Assert.Equal("activation_scope_identity_mismatch", result.Reason);
        AssertPending(database);
    }

    [Fact]
    public void InvalidPlanRelationOrScopePolicyFailsClosedWithoutWriting()
    {
        using (var nonOneTime = new ActivationDatabase())
        {
            Execute(nonOneTime, "UPDATE plans SET is_one_time = 0 WHERE id = 'plan-1';");
            var result = nonOneTime.Service.Activate(nonOneTime.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_snapshot_invalid", result.Reason);
            Assert.Equal(PlanDefinitionStatus.Draft, nonOneTime.Plans.Get("plan-1").Status);
            Assert.Equal(0L, nonOneTime.Plans.Get("plan-1").Version);
        }

        using (var relation = new ActivationDatabase())
        {
            Execute(relation, "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET plan_id = 'other-plan' WHERE id = 'occ-1';");
            var result = relation.Service.Activate(relation.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_snapshot_invalid", result.Reason);
            Assert.Equal(0L, Scalar(relation, "SELECT version FROM plans WHERE id = 'plan-1';"));
            Assert.Equal(0L, Scalar(relation, "SELECT version FROM plan_occurrences WHERE id = 'occ-1';"));
            Assert.Equal(0L, Scalar(relation, "SELECT version FROM consent_leases WHERE id = 'lease-1';"));
        }

        using (var policy = new ActivationDatabase())
        {
            Execute(policy, "PRAGMA ignore_check_constraints = ON; UPDATE authorized_capture_scopes SET audio_mode_code = 'system_audio' WHERE scope_id = 'scope-1';");
            var result = policy.Service.Activate(policy.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_snapshot_invalid", result.Reason);
            AssertPending(policy);
        }

        using (var lifecycle = new ActivationDatabase())
        {
            Execute(lifecycle, "UPDATE consent_leases SET max_duration_ms = 1000 WHERE id = 'lease-1';");
            var result = lifecycle.Service.Activate(lifecycle.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_snapshot_invalid", result.Reason);
            AssertPending(lifecycle);
        }
    }

    [Fact]
    public void PartialStatesExecutionEvidenceAndVersionExhaustionAreNeverAutoCompleted()
    {
        foreach (var mutation in new Action<ActivationDatabase>[]
        {
            database => Execute(database, "UPDATE plans SET status_code = 'enabled' WHERE id = 'plan-1';"),
            database => Execute(database, "UPDATE plan_occurrences SET status_code = 'authorized' WHERE id = 'occ-1';"),
            database => Execute(database, "UPDATE consent_leases SET status_code = 'active' WHERE id = 'lease-1';"),
            database => database.Runs.Insert(new RecordingRun("run-1", "occ-1", At(1))),
            database => Execute(database, "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET run_id = 'run-1' WHERE id = 'occ-1';"),
            database => Execute(database, "UPDATE plans SET version = $version WHERE id = 'plan-1';", ("$version", long.MaxValue)),
        })
        {
            using var database = new ActivationDatabase();
            mutation(database);
            var before = ReadAggregateState(database);

            var result = database.Service.Activate(database.CreateRequest());

            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal(before, ReadAggregateState(database));
            Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));
        }
    }

    [Fact]
    public void UpdateConstraintOrCommitFailureRollsBackAllThreeAggregateTransitions()
    {
        using (var constraint = new ActivationDatabase())
        {
            Execute(constraint, "CREATE TRIGGER abort_activation_lease BEFORE UPDATE ON consent_leases WHEN NEW.status_code = 'active' BEGIN SELECT RAISE(ABORT, 'test-only abort'); END;");

            var result = constraint.Service.Activate(constraint.CreateRequest());

            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_sqlite_constraint_violation", result.Reason);
            AssertPending(constraint);
        }

        using (var injected = new ActivationDatabase(beforeCommitForTest: (_, _) => throw new InvalidOperationException("commit-test")))
        {
            var result = injected.Service.Activate(injected.CreateRequest());

            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_sqlite_failure", result.Reason);
            AssertPending(injected);
        }
    }

    [Fact]
    public void VersionConflictBeforeTheFirstUpdateRollsBackInjectedMutationAndDoesNotRetry()
    {
        var callbackCount = 0;
        using var database = new ActivationDatabase(
            beforeWritesForTest: (connection, transaction) =>
            {
                callbackCount++;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE plans SET version = 1 WHERE id = 'plan-1';";
                command.ExecuteNonQuery();
            });

        var result = database.Service.Activate(database.CreateRequest());

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Conflict, result.Status);
        Assert.Equal("activation_concurrency_conflict", result.Reason);
        Assert.Equal(1, callbackCount);
        AssertPending(database);
    }

    [Fact]
    public async Task ConcurrentIdenticalActivationsProduceOneVersionAdvanceAndNoExecutionObjects()
    {
        using var database = new ActivationDatabase();
        var first = new StandingLeaseAuthorizationActivationService(database.Store, () => At(1));
        var second = new StandingLeaseAuthorizationActivationService(database.Store, () => At(1));
        var request = database.CreateRequest();

        var results = await Task.WhenAll(
            Task.Run(() => first.Activate(request)),
            Task.Run(() => second.Activate(request)));

        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseAuthorizationActivationStatus.Activated));
        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseAuthorizationActivationStatus.AlreadyActive));
        Assert.Equal(1L, database.Plans.Get("plan-1").Version);
        Assert.Equal(1L, database.Occurrences.Get("occ-1").Version);
        Assert.Equal(1L, database.Leases.Get("lease-1").Version);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void ActivatedChainCanReachTask237EligibilityButActivationDoesNotCallIt()
    {
        using var database = new ActivationDatabase();

        var activation = database.Service.Activate(database.CreateRequest());
        var eligibility = new StandingLeaseWindowEligibilityService(database.Store, () => At(1));
        var result = eligibility.Evaluate(new StandingLeaseWindowEligibilityRequest(
            "plan-1",
            "occ-1",
            "lease-1",
            "scope-1",
            database.Scope.ScopeDigest));

        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, activation.Status);
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Eligible, result.Status);
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.DoesNotContain(
            typeof(StandingLeaseAuthorizationActivationService).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic),
            field => field.FieldType == typeof(StandingLeaseWindowEligibilityService) ||
                field.FieldType == typeof(StandingLeaseNaturalWakeDispatcher));
    }

    [Fact]
    public void ActivationClockIsTrustedAndReceiptCannotBePubliclyConstructed()
    {
        using (var unavailable = new ActivationDatabase(utcNowForTest: () => null))
        {
            var result = unavailable.Service.Activate(unavailable.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_clock_unavailable", result.Reason);
            AssertPending(unavailable);
        }

        using (var nonUtc = new ActivationDatabase(utcNowForTest: () => At(1).ToOffset(TimeSpan.FromHours(1))))
        {
            var result = nonUtc.Service.Activate(nonUtc.CreateRequest());
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_time_not_utc", result.Reason);
            AssertPending(nonUtc);
        }

        Assert.Empty(typeof(StandingLeaseLocalApprovalReceipt).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ActivationRequestAndResultExposeOnlyDurableIdentityAndControlledStatus()
    {
        var forbidden = new[]
        {
            "RunId", "LeaseUseId", "Duration", "CaptureConfig", "Target", "Bounds", "Display",
            "Output", "Backend", "Audio", "Proof", "Nonce", "Environment", "NativeHandle",
        };

        var requestNames = typeof(StandingLeaseAuthorizationActivationRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        var resultNames = typeof(StandingLeaseAuthorizationActivationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[] { "PlanId", "OccurrenceId", "LeaseId", "ScopeId", "ScopeDigest", "ApprovalReceipt" },
            requestNames);
        Assert.DoesNotContain("ApprovalReceipt", resultNames);
        Assert.DoesNotContain(resultNames, name => forbidden.Any(item => name.Contains(item, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(requestNames, name => forbidden.Any(item => name.Contains(item, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(StandingLeaseAuthorizationActivationService).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter => forbidden.Any(item => parameter.ParameterType.Name.Contains(item, StringComparison.OrdinalIgnoreCase))));
    }

    private static void AssertIdentity(
        ActivationDatabase database,
        StandingLeaseAuthorizationActivationResult result)
    {
        Assert.Equal("plan-1", result.PlanId);
        Assert.Equal("occ-1", result.OccurrenceId);
        Assert.Equal("lease-1", result.LeaseId);
        Assert.Equal("scope-1", result.ScopeId);
        Assert.Equal(database.Scope.ScopeDigest, result.ScopeDigest);
    }

    private static void AssertPending(ActivationDatabase database)
    {
        Assert.Equal(PlanDefinitionStatus.Draft, database.Plans.Get("plan-1").Status);
        Assert.Equal(PlanOccurrenceStatus.PendingLeaseApproval, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(ConsentLeaseStatus.Pending, database.Leases.Get("lease-1").Status);
        Assert.Equal(0L, database.Plans.Get("plan-1").Version);
        Assert.Equal(0L, database.Occurrences.Get("occ-1").Version);
        Assert.Equal(0L, database.Leases.Get("lease-1").Version);
    }

    private static (string PlanStatus, long PlanVersion, long PlanUpdated, string OccurrenceStatus, long OccurrenceVersion, long OccurrenceUpdated, string LeaseStatus, long LeaseVersion, long LeaseUpdated) ReadAggregateState(ActivationDatabase database) =>
        (
            ScalarText(database, "SELECT status_code FROM plans WHERE id = 'plan-1';"),
            Scalar(database, "SELECT version FROM plans WHERE id = 'plan-1';"),
            Scalar(database, "SELECT updated_at_utc FROM plans WHERE id = 'plan-1';"),
            ScalarText(database, "SELECT status_code FROM plan_occurrences WHERE id = 'occ-1';"),
            Scalar(database, "SELECT version FROM plan_occurrences WHERE id = 'occ-1';"),
            Scalar(database, "SELECT updated_at_utc FROM plan_occurrences WHERE id = 'occ-1';"),
            ScalarText(database, "SELECT status_code FROM consent_leases WHERE id = 'lease-1';"),
            Scalar(database, "SELECT version FROM consent_leases WHERE id = 'lease-1';"),
            Scalar(database, "SELECT updated_at_utc FROM consent_leases WHERE id = 'lease-1';"));

    private static long Scalar(ActivationDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)command.ExecuteScalar()!;
    }

    private static string ScalarText(ActivationDatabase database, string sql)
    {
        using var connection = database.Store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(ActivationDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Store.OpenConnection();
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

    private sealed class ActivationDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentRecorderActivation_" + Guid.NewGuid().ToString("N"));

        internal ActivationDatabase(
            Func<DateTimeOffset?>? utcNowForTest = null,
            Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
            Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(_root, "state", "agent-recorder.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            using (var safetyConnection = Store.OpenConnection())
            using (var safetyCommand = safetyConnection.CreateCommand())
            {
                safetyCommand.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';";
                safetyCommand.ExecuteNonQuery();
            }
            Plans = new SqlitePlanDefinitionRepository(Store);
            Occurrences = new SqlitePlanOccurrenceRepository(Store);
            Leases = new SqliteConsentLeaseRepository(Store);
            Runs = new SqliteRecordingRunRepository(Store);

            var plan = PlanDefinition.Rehydrate("plan-1", true, PlanDefinitionStatus.Draft, At(0), At(0), 0);
            Plans.Insert(plan);
            var occurrence = PlanOccurrence.Rehydrate(
                "occ-1",
                plan.Id,
                At(0),
                At(60),
                At(0),
                PlanOccurrenceStatus.PendingLeaseApproval,
                null,
                null,
                At(0),
                0);
            Occurrences.Insert(occurrence);
            var lease = ConsentLease.Rehydrate(
                "lease-1",
                plan.Id,
                occurrence.Id,
                At(0),
                At(120),
                1,
                TimeSpan.FromSeconds(30),
                ConsentLeaseStatus.Pending,
                At(0),
                0);
            Leases.Insert(lease);

            Scope = AuthorizedFixedRegionScope.CreateFor(
                plan,
                occurrence,
                lease,
                "scope-1",
                At(0),
                AuthorizedScopeTargetType.FixedRegion,
                AuthorizedCaptureSemantics.DesktopRegion,
                AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved,
                "display-fingerprint-1",
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                new AuthorizedPhysicalRectangle(10, 10, 640, 480),
                96,
                96,
                1920,
                1080,
                AuthorizedDisplayOrientation.Landscape,
                AuthorizedCaptureBackend.FfmpegRegion,
                AuthorizedAudioMode.None,
                TimeSpan.FromSeconds(5),
                0,
                Path.Combine(_root, "output"),
                "capture.mp4",
                AuthorizedOutputConflictPolicy.FailIfExists,
                AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired,
                "S-1-5-21-1",
                "session-1",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            new SqliteAuthorizedCaptureScopeRepository(Store).Insert(Scope);

            Service = new StandingLeaseAuthorizationActivationService(
                Store,
                utcNowForTest ?? (() => At(1)),
                beforeWritesForTest,
                beforeCommitForTest);
        }

        internal SqliteOperationalStore Store { get; }

        internal SqlitePlanDefinitionRepository Plans { get; }

        internal SqlitePlanOccurrenceRepository Occurrences { get; }

        internal SqliteConsentLeaseRepository Leases { get; }

        internal SqliteRecordingRunRepository Runs { get; }

        internal AuthorizedFixedRegionScope Scope { get; }

        internal StandingLeaseAuthorizationActivationService Service { get; }

        internal StandingLeaseAuthorizationActivationRequest CreateRequest(
            StandingLeaseLocalApprovalReceipt? receipt = null) =>
            new(
                "plan-1",
                "occ-1",
                "lease-1",
                "scope-1",
                Scope.ScopeDigest,
                receipt ?? CreateReceipt());

        internal StandingLeaseLocalApprovalReceipt CreateReceipt(
            string approvalId = "approval-1",
            string planId = "plan-1",
            string occurrenceId = "occ-1",
            string leaseId = "lease-1",
            string scopeId = "scope-1",
            string? scopeDigest = null,
            string currentUserSid = "S-1-5-21-1",
            string sessionBinding = "session-1",
            DateTimeOffset? approvedAtUtc = null,
            string approvalKind = StandingLeaseLocalApprovalReceipt.CurrentApprovalKind,
            int approvalVersion = StandingLeaseLocalApprovalReceipt.CurrentApprovalVersion) =>
            StandingLeaseLocalApprovalReceipt.CreateForTest(
                approvalId,
                planId,
                occurrenceId,
                leaseId,
                scopeId,
                scopeDigest ?? Scope.ScopeDigest,
                currentUserSid,
                sessionBinding,
                approvedAtUtc ?? At(1),
                approvalKind,
                approvalVersion);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
