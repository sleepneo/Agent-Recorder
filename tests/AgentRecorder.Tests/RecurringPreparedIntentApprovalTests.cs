using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringPreparedIntentApprovalTests
{
    [Fact]
    public void PreparedIntentApprovalAtomicallyActivatesOnlyTheSetupChain()
    {
        using var database = CreatePreparedDatabase();
        var receipt = database.CreateReceipt();

        var result = database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", receipt);

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, result.Status);
        Assert.Equal("activated", result.Reason);
        Assert.True(result.Changed);
        Assert.Equal("plan-1", result.PlanId);
        Assert.Equal("lease-1", result.LeaseId);
        Assert.Equal("activated", Text(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal(At(5).UtcDateTime.Ticks, Scalar(database.Store, "SELECT updated_at_utc FROM setup_intents;"));
        Assert.Equal("enabled", Text(database.Store, "SELECT status_code FROM plans;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM plans;"));
        Assert.Equal("active", Text(database.Store, "SELECT status_code FROM recurring_consent_leases;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM recurring_consent_leases;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_uses;"));

        var readback = new RecurringSetupIntentService(database.Store, () => At(6))
            .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation");
        Assert.NotNull(readback);
        Assert.Equal(RecurringSetupIntentStatus.Activated, readback!.Status);
        Assert.Equal(2L, readback.Version);
    }

    [Fact]
    public void IdenticalReplayIsAlreadyActiveAndDifferentReceiptIsConflictWithoutWrites()
    {
        using var database = CreatePreparedDatabase();
        var receipt = database.CreateReceipt();
        var service = database.CreateApprovalService(At(5));
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            service.Activate("recurring-preparation-intent", receipt).Status);
        var before = ReadState(database.Store);

        var replay = new RecurringLeasePreparedIntentApprovalActivationService(database.Store, () => At(500))
            .Activate("recurring-preparation-intent", receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.AlreadyActive, replay.Status);
        Assert.Equal("already_active", replay.Reason);
        Assert.False(replay.Changed);
        Assert.Equal(before, ReadState(database.Store));

        var conflictReceipt = database.CreateReceipt(approvalId: "approval-2", approvedAtUtc: At(4));
        var conflict = new RecurringLeasePreparedIntentApprovalActivationService(database.Store, () => At(500))
            .Activate("recurring-preparation-intent", conflictReceipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Conflict, conflict.Status);
        Assert.Equal("activation_receipt_conflict", conflict.Reason);
        Assert.Equal(before, ReadState(database.Store));
    }

    [Theory]
    [InlineData("AfterApprovalEvidenceInsert")]
    [InlineData("AfterPlanUpdate")]
    [InlineData("AfterLeaseUpdate")]
    [InlineData("AfterIntentUpdate")]
    [InlineData("BeforeCommitAfterFinalRead")]
    public void EveryPreparedActivationFailurePointRollsBackAllWritesAndRetryWorks(string pointName)
    {
        var point = Enum.Parse<RecurringLeaseLocalApprovalActivationFailurePoint>(pointName);
        using var database = CreatePreparedDatabase();
        var before = ReadState(database.Store);
        var failing = new RecurringLeasePreparedIntentApprovalActivationService(
            database.Store,
            () => At(5),
            failureHookForTest: actual =>
            {
                if (actual == point) throw new InvalidOperationException("injected");
            });

        var rejected = failing.Activate("recurring-preparation-intent", database.CreateReceipt());

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, rejected.Status);
        Assert.Equal("activation_sqlite_failure", rejected.Reason);
        Assert.Equal(before, ReadState(database.Store));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_local_approvals;"));

        var retry = new RecurringLeasePreparedIntentApprovalActivationService(database.Store, () => At(5))
            .Activate("recurring-preparation-intent", database.CreateReceipt());
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, retry.Status);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("lease")]
    [InlineData("configuration")]
    [InlineData("authorization")]
    [InlineData("sid")]
    [InlineData("session")]
    public void ReceiptIdentityMismatchRejectsWithoutChangingPreparedState(string mismatch)
    {
        using var database = CreatePreparedDatabase();
        var before = ReadState(database.Store);
        var receipt = database.CreateReceipt(
            planId: mismatch == "plan" ? "other-plan" : "plan-1",
            leaseId: mismatch == "lease" ? "other-lease" : "lease-1",
            configurationDigest: mismatch == "configuration" ? VersionedDigest(RecurringPlanConfigurationRef.DigestPrefix, 'b') : null,
            authorizationDigest: mismatch == "authorization" ? VersionedDigest(RecurringConsentLeaseAuthorizationRef.DigestPrefix, 'b') : null,
            currentUserSid: mismatch == "sid" ? "S-1-5-21-other" : "S-1-5-21-preparation",
            sessionBinding: mismatch == "session" ? "session-other" : "session-preparation");

        var result = database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", receipt);

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
        Assert.Equal("activation_receipt_identity_mismatch", result.Reason);
        Assert.Equal(before, ReadState(database.Store));
    }

    [Fact]
    public void ReceiptTimeSafetyAndIntentBindingBoundariesFailClosed()
    {
        using (var nonUtc = CreatePreparedDatabase())
        {
            var receipt = nonUtc.CreateReceipt(approvedAtUtc: At(4).ToOffset(TimeSpan.FromHours(8)));
            var result = nonUtc.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", receipt);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_receipt_time_not_utc", result.Reason);
        }

        using (var beforePreparation = CreatePreparedDatabase())
        {
            var result = beforePreparation.CreateApprovalService(At(5))
                .Activate("recurring-preparation-intent", beforePreparation.CreateReceipt(approvedAtUtc: At(2)));
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_receipt_time_invalid", result.Reason);
        }

        using (var future = CreatePreparedDatabase())
        {
            var result = future.CreateApprovalService(At(5))
                .Activate("recurring-preparation-intent", future.CreateReceipt(approvedAtUtc: At(6)));
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal("activation_receipt_time_in_future", result.Reason);
        }

        using (var disabled = CreatePreparedDatabase())
        {
            Execute(disabled.Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'disabled' WHERE state_id = 'global';");
            var result = disabled.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", disabled.CreateReceipt());
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal("unattended_disabled", result.Reason);
        }

        using (var stopAll = CreatePreparedDatabase())
        {
            Execute(stopAll.Store, """
                UPDATE unattended_safety_state
                SET stop_all_applied = 1, stop_all_operation_id = 'task-282-stop',
                    stop_all_reason_code = 'task-282-test', stop_all_requested_at_utc = $at,
                    stop_all_applied_at_utc = $at, version = version + 1
                WHERE state_id = 'global';
                """, ("$at", At(4).UtcDateTime.Ticks));
            var result = stopAll.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", stopAll.CreateReceipt());
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal("stop_all_boundary", result.Reason);
        }

        using var wrongIntent = CreatePreparedDatabase();
        var missing = wrongIntent.CreateApprovalService(At(5)).Activate("other-intent", wrongIntent.CreateReceipt());
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, missing.Status);
        Assert.Equal("activation_intent_not_found", missing.Reason);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsProduceOneActivationAndOneAlreadyActive()
    {
        using var database = CreatePreparedDatabase();
        var first = database.CreateApprovalService(At(5));
        var second = new RecurringLeasePreparedIntentApprovalActivationService(
            new SqliteOperationalStore(database.Store.DatabasePath), () => At(5));
        var receipt = database.CreateReceipt();
        using var startGate = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(() => ActivateAtBarrier(startGate, first, receipt)),
            Task.Run(() => ActivateAtBarrier(startGate, second, receipt)));

        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.Activated));
        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.AlreadyActive));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
    }

    [Fact]
    public async Task ConcurrentConflictingReceiptsProduceOneWinnerAndOneConflict()
    {
        using var database = CreatePreparedDatabase();
        var first = database.CreateApprovalService(At(5));
        var second = new RecurringLeasePreparedIntentApprovalActivationService(
            new SqliteOperationalStore(database.Store.DatabasePath), () => At(5));
        var firstReceipt = database.CreateReceipt("approval-1");
        var secondReceipt = database.CreateReceipt("approval-2", approvedAtUtc: At(4));
        using var startGate = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(() => ActivateAtBarrier(startGate, first, firstReceipt)),
            Task.Run(() => ActivateAtBarrier(startGate, second, secondReceipt)));

        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.Activated));
        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.Conflict));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
    }

    [Fact]
    public void ActivatedReadbackAcceptsPlanPauseResumeAndCancelThroughProductionRepository()
    {
        using var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);

        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var plan = plans.Get("plan-1");
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Paused, At(6)).Succeeded);
        plans.Update(plan, expectedVersion: 1);
        AssertActivatedReadback(database);

        plan = plans.Get("plan-1");
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, At(7)).Succeeded);
        plans.Update(plan, expectedVersion: 2);
        AssertActivatedReadback(database);

        plan = plans.Get("plan-1");
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Cancelled, At(8)).Succeeded);
        plans.Update(plan, expectedVersion: 3);
        AssertActivatedReadback(database);
    }

    [Fact]
    public void ActivatedReadbackAcceptsDirectRevocationThroughProductionSafetyControl()
    {
        using var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);

        var revoke = new StandingLeaseSafetyControlService(database.Store, () => At(6))
            .RevokeRecurringLease("lease-1", "task-282r-revoke", "task-282r-test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked,
            new SqliteRecurringConsentLeaseRepository(database.Store).Get("lease-1").Status);
        AssertActivatedReadback(database);
    }

    [Fact]
    public void ActivatedReadbackAcceptsExpiredLeaseThroughDomainAndRepositoryCas()
    {
        using var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);

        var leases = new SqliteRecurringConsentLeaseRepository(database.Store);
        var lease = leases.Get("lease-1");
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Expired, At(172800)).Succeeded);
        leases.Update(lease, expectedVersion: 1);
        AssertActivatedReadback(database);
    }

    [Fact]
    public void ActivatedReadbackAcceptsExhaustedLeaseAfterProductionReservationAndStartCommit()
    {
        using var database = CreateProductionExhaustedDatabase(completeLastLifecycle: true);
        Assert.Equal(ConsentLeaseStatus.Exhausted,
            new SqliteRecurringConsentLeaseRepository(database.Store).Get("lease-1").Status);
        AssertActivatedReadback(database);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    public void ActivatedReadbackRejectsTerminalQuotaOnDirectV2LeaseState(string status)
    {
        using var database = CreateProductionExhaustedDatabase(completeLastLifecycle: false);
        Execute(
            database.Store,
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = $status, updated_at_utc = $updated WHERE lease_id = 'lease-1';",
            ("$status", status),
            // Keep the expired corruption case on its legal time boundary so
            // the rejection is specifically the terminal quota mismatch.
            ("$updated", status == "expired" ? At(172800).UtcDateTime.Ticks : At(172700).UtcDateTime.Ticks));

        Assert.Null(new RecurringSetupIntentService(database.Store, () => At(172801))
            .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation"));
    }

    [Fact]
    public void ActivatedReadbackAcceptsExhaustedThenRevokedV3ThroughProductionSafetyControl()
    {
        using var database = CreateProductionExhaustedDatabase(completeLastLifecycle: false);
        var revoke = new StandingLeaseSafetyControlService(database.Store, () => At(172700))
            .RevokeRecurringLease("lease-1", "task-282r2-revoke", "task-282r2-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
        var lease = new SqliteRecurringConsentLeaseRepository(database.Store).Get("lease-1");
        Assert.Equal(ConsentLeaseStatus.Revoked, lease.Status);
        Assert.Equal(3, lease.Version);
        AssertActivatedReadback(database);
    }

    private static void AssertActivatedReadback(PreparedDatabase database)
    {

        var readback = new RecurringSetupIntentService(database.Store, () => At(200001))
            .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation");
        Assert.NotNull(readback);
        Assert.Equal(RecurringSetupIntentStatus.Activated, readback!.Status);
    }

    private static PreparedDatabase CreateProductionExhaustedDatabase(bool completeLastLifecycle)
    {
        var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);

        DateTimeOffset? afterUtc = null;
        for (var index = 0; index < 2; index++)
        {
            var candidate = new RecurringOccurrenceCalculator()
                .CalculateNext("plan-1", 1, database.Schedule, afterUtc)
                .Candidate ?? throw new InvalidOperationException("The prepared schedule did not produce a candidate.");
            var slot = new SqliteRecurringOccurrenceMaterializationTransaction(database.Store)
                .Materialize(candidate, At(6));
            afterUtc = candidate.PlannedEndUtc;
            var due = new SqlitePeriodicOccurrenceDueProjectionTransaction(database.Store)
                .Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value);
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);

            var occurrences = new SqlitePlanOccurrenceRepository(database.Store);
            var occurrence = occurrences.Get(slot.OccurrenceId!);
            Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, slot.ScheduledStartUtc.Value).Succeeded);
            occurrences.Update(occurrence, expectedVersion: occurrence.Version - 1);
            var recheck = new RecurringOccurrenceEnvironmentRecheckService(
                database.Store,
                () => slot.ScheduledStartUtc.Value,
                new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
                .Recheck("lease-1", slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, recheck.Status);

            var reservation = new RecurringOccurrenceReservationService(
                database.Store,
                () => slot.ScheduledStartUtc.Value,
                () => "task-282r2-run-" + index,
                () => "task-282r2-use-" + index)
                .Reserve("lease-1", slot.OccurrenceIdentity);
            Assert.True(reservation.Status == RecurringOccurrenceReservationStatus.Reserved, reservation.ReasonCode);

            var start = new RecurringOccurrenceStartCommitService(
                database.Store,
                () => slot.ScheduledStartUtc.Value,
                new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
                .Commit("lease-1", slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, start.Status);
            Assert.NotNull(start.FirstCommitReceipt);
            if (index == 0 || completeLastLifecycle)
            {
                var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(database.Store, start.FirstCommitReceipt!);
                var settled = lifecycle.CompleteTermination(
                    RecurringLeaseLifecycleTerminationKind.NaturalExit,
                    0,
                    meta: null,
                    slot.ScheduledStartUtc.Value.AddTicks(1));
                Assert.True(settled.Succeeded, settled.Reason);
            }
        }

        return database;
    }

    [Fact]
    public void ActivatedReadbackRejectsDraftPendingRejectedAndMissingEvidence()
    {
        foreach (var mutation in new[]
        {
            "UPDATE plans SET status_code = 'draft', version = 2 WHERE id = 'plan-1';",
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'pending', version = 2 WHERE lease_id = 'lease-1';",
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'rejected', version = 2 WHERE lease_id = 'lease-1';",
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_delete; DELETE FROM recurring_lease_local_approvals WHERE lease_id = 'lease-1';",
        })
        {
            using var database = CreatePreparedDatabase();
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
                database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);
            Execute(database.Store, mutation);

            Assert.Null(new RecurringSetupIntentService(database.Store, () => At(6))
                .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation"));
        }
    }

    [Theory]
    [InlineData("UPDATE plans SET status_code = 'paused', version = 1, updated_at_utc = $at WHERE id = 'plan-1';")]
    [InlineData("UPDATE plans SET status_code = 'cancelled', version = 1, updated_at_utc = $at WHERE id = 'plan-1';")]
    [InlineData("UPDATE plans SET status_code = 'paused', version = 99, updated_at_utc = $at WHERE id = 'plan-1';")]
    [InlineData("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'active', version = 2, updated_at_utc = $at WHERE lease_id = 'lease-1';")]
    [InlineData("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', version = 1, updated_at_utc = $at WHERE lease_id = 'lease-1';")]
    [InlineData("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'expired', version = 1, updated_at_utc = $at WHERE lease_id = 'lease-1';")]
    [InlineData("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'exhausted', version = 1, updated_at_utc = $at WHERE lease_id = 'lease-1';")]
    [InlineData("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', version = 99, updated_at_utc = $at WHERE lease_id = 'lease-1';")]
    public void ActivatedReadbackRejectsUnreachableLifecycleVersionShapes(string mutation)
    {
        using var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);
        Execute(database.Store, mutation, ("$at", At(6).UtcDateTime.Ticks));

        Assert.Null(new RecurringSetupIntentService(database.Store, () => At(7))
            .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation"));
    }

    [Theory]
    [InlineData("exhausted", 2)]
    [InlineData("active", 2)]
    public void ActivatedReadbackRejectsQuotaInconsistentTerminalStateWithNoUses(string status, long version)
    {
        using var database = CreatePreparedDatabase();
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            database.CreateApprovalService(At(5)).Activate("recurring-preparation-intent", database.CreateReceipt()).Status);
        Execute(database.Store,
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = $status, version = $version, updated_at_utc = $at WHERE lease_id = 'lease-1';",
            ("$status", status),
            ("$version", version),
            ("$at", At(172800).UtcDateTime.Ticks));

        Assert.Null(new RecurringSetupIntentService(database.Store, () => At(172801))
            .Get("recurring-preparation-intent", "S-1-5-21-preparation", "session-preparation"));
    }

    private static RecurringLeasePreparedIntentApprovalActivationResult ActivateAtBarrier(
        Barrier startGate,
        RecurringLeasePreparedIntentApprovalActivationService service,
        RecurringLeaseLocalApprovalReceipt receipt)
    {
        if (!startGate.SignalAndWait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The activation start gate did not open.");
        return service.Activate("recurring-preparation-intent", receipt);
    }

    private static PreparedDatabase CreatePreparedDatabase(int maxUses = 2)
    {
        var database = new PreparedDatabase();
        var request = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "recurring-preparation-intent",
            "recurring-preparation-key",
            "S-1-5-21-preparation",
            "session-preparation",
            At(0),
            At(172800),
            RecurringPlanSchedule.CreateDaily(
                "China Standard Time",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 2),
                new TimeOnly(9, 30),
                2,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5)),
            maxUses,
            TimeSpan.FromSeconds(60),
            At(172800),
            "D:\\Recordings\\Agent",
            "daily-review");
        Assert.Equal(RecurringSetupIntentResultStatus.Created,
            new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);

        var selection = RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            request.IntentId,
            request.CurrentUserSid,
            request.SessionBinding,
            At(2),
            "display-fingerprint-1",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            96,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            new string('a', 64));
        var prepared = new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds()).Prepare(selection);
        Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, prepared.Status);
        database.Schedule = request.Schedule;
        return database;
    }

    private static object?[] ReadState(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT status_code FROM setup_intents), (SELECT version FROM setup_intents), (SELECT updated_at_utc FROM setup_intents),
                   (SELECT status_code FROM plans), (SELECT version FROM plans), (SELECT updated_at_utc FROM plans),
                   (SELECT status_code FROM recurring_consent_leases), (SELECT version FROM recurring_consent_leases), (SELECT updated_at_utc FROM recurring_consent_leases),
                   (SELECT COUNT(*) FROM recurring_lease_local_approvals);
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))
            .ToArray();
    }

    private static RecurringLeaseLocalApprovalReceipt CreateReceipt(
        SqliteOperationalStore store,
        string approvalId,
        string leaseId,
        string planId,
        string? configurationDigest,
        string? authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc) =>
        RecurringLeaseLocalApprovalReceipt.CreateForTest(
            approvalId,
            leaseId,
            planId,
            configurationDigest ?? Text(store, "SELECT configuration_digest FROM recurring_consent_leases;"),
            authorizationDigest ?? Text(store, "SELECT authorization_digest FROM recurring_consent_leases;"),
            currentUserSid,
            sessionBinding,
            approvedAtUtc);

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void Execute(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string VersionedDigest(string prefix, char fill) => prefix + new string(fill, 64);

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private sealed class FixedIds : IRecurringSetupPreparationIdProvider
    {
        public string CreateProfileId() => "profile-1";
        public string CreatePlanId() => "plan-1";
        public string CreateLeaseId() => "lease-1";
    }

    private sealed class PreparedDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringPreparedApproval_" + Guid.NewGuid().ToString("N"));

        internal PreparedDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            Execute(Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
        }

        internal SqliteOperationalStore Store { get; }

        internal RecurringPlanSchedule Schedule { get; set; } = null!;

        internal RecurringLeasePreparedIntentApprovalActivationService CreateApprovalService(DateTimeOffset nowUtc) =>
            new(Store, () => nowUtc);

        internal RecurringLeaseLocalApprovalReceipt CreateReceipt(
            string approvalId = "approval-1",
            string leaseId = "lease-1",
            string planId = "plan-1",
            string? configurationDigest = null,
            string? authorizationDigest = null,
            string currentUserSid = "S-1-5-21-preparation",
            string sessionBinding = "session-preparation",
            DateTimeOffset? approvedAtUtc = null) =>
            RecurringPreparedIntentApprovalTests.CreateReceipt(
                Store,
                approvalId,
                leaseId,
                planId,
                configurationDigest,
                authorizationDigest,
                currentUserSid,
                sessionBinding,
                approvedAtUtc ?? At(4));

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}
