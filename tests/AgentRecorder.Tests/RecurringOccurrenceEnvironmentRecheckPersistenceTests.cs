using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceEnvironmentRecheckPersistenceTests
{
    [Fact]
    public void EligibleRecheckPersistsImmutableSpecificationAndAdvancesOccurrenceAtomically()
    {
        using var context = RecheckContext.Create();
        var provider = new MatchingEnvironmentProvider();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var service = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => now,
            provider);

        var result = service.Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.True(result.Succeeded, $"{result.Status}: {result.ReasonCode}");
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Authorized, result.FinalOccurrenceStatus);
        Assert.Equal(3, result.FinalOccurrenceVersion);
        Assert.NotNull(result.SpecificationSummary);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(now, provider.LastRequest!.TrustedNowUtc);
        Assert.Equal(result.SpecificationDigest, result.SpecificationSummary!.SpecificationDigest);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));

        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Authorized, occurrence.Status);
        Assert.Equal(3, occurrence.Version);
        Assert.Equal(result.SpecificationDigest, context.Fixture.Scalar(
            "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrenceId;",
            ("$occurrenceId", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));

        Assert.Throws<SqliteException>(() => context.Fixture.Execute(
            "UPDATE recurring_occurrence_execution_specs SET evaluated_at_utc = evaluated_at_utc + 1 WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
        Assert.Throws<SqliteException>(() => context.Fixture.Execute(
            "DELETE FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
    }

    [Fact]
    public void ReplayReturnsAlreadyAuthorizedWithoutCallingEnvironmentProvider()
    {
        using var context = RecheckContext.Create();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var firstProvider = new MatchingEnvironmentProvider();
        var first = new RecurringOccurrenceEnvironmentRecheckService(context.Fixture.Store, () => now, firstProvider)
            .Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        var replayProvider = new MatchingEnvironmentProvider();
        var replay = new RecurringOccurrenceEnvironmentRecheckService(context.Fixture.Store, () => now, replayProvider)
            .Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAuthorized, replay.Status);
        Assert.False(replay.Changed);
        Assert.Equal(first.SpecificationDigest, replay.SpecificationDigest);
        Assert.Equal(0, replayProvider.CaptureCount);
        Assert.Equal(3L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
    }

    [Fact]
    public void AuthorizedReplayRejectsSpecificationWhenOccurrenceUpdatedTimeDrifts()
    {
        using var context = RecheckContext.Create();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var first = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => now,
            new MatchingEnvironmentProvider()).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);

        context.Fixture.Execute(
            "UPDATE plan_occurrences SET updated_at_utc = updated_at_utc + 1 WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!));
        var provider = new MatchingEnvironmentProvider();
        var replay = new RecurringOccurrenceEnvironmentRecheckService(context.Fixture.Store, () => now, provider)
            .Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, replay.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, replay.ReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Authorized, replay.FinalOccurrenceStatus);
        Assert.Equal(3, replay.FinalOccurrenceVersion);
        Assert.False(replay.Changed);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void ApprovalBindingForeignKeyRejectsDigestSidAndSessionDrift()
    {
        var mutations = new (string Column, string Value)[]
        {
            ("local_approval_digest", "recurring-lease-local-approval/v1:" + new string('0', 64)),
            ("approved_current_user_sid", "S-1-5-21-task263-other"),
            ("approved_session_binding", "session-task263-other"),
        };

        foreach (var mutation in mutations)
        {
            using var context = RecheckContext.Create();
            var now = context.Slot.ScheduledStartUtc!.Value;
            var first = new RecurringOccurrenceEnvironmentRecheckService(
                context.Fixture.Store,
                () => now,
                new MatchingEnvironmentProvider()).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);

            context.Fixture.ReplaceTriggerDefinition(
                "trg_recurring_occurrence_execution_specs_immutable_update",
                "CREATE TRIGGER trg_recurring_occurrence_execution_specs_immutable_update BEFORE UPDATE ON recurring_occurrence_execution_specs BEGIN SELECT 1; END;");
            Assert.Throws<SqliteException>(() => context.Fixture.Execute(
                $"UPDATE recurring_occurrence_execution_specs SET {mutation.Column} = $value WHERE occurrence_identity = $identity;",
                ("$value", mutation.Value),
                ("$identity", context.Slot.OccurrenceIdentity)));
        }
    }

    [Fact]
    public void BeforeWindowIsReadOnlyAndDoesNotProbeEnvironment()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var provider = new MatchingEnvironmentProvider();
        var beforeWindow = context.Slot.ScheduledStartUtc!.Value.AddTicks(-1);

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => beforeWindow,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal("due", context.Fixture.Scalar(
            "SELECT status_code FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!)));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void MissedWindowBecomesTerminalWithoutCallingEnvironmentProvider()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var provider = new MatchingEnvironmentProvider();
        var afterWindow = context.Slot.PlannedEndUtc!.Value.AddTicks(1);

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => afterWindow,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Missed, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Missed, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(3L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void DisabledSafetyBecomesBlockedBeforeEnvironmentProbe()
    {
        using var context = RecheckContext.Create(nextDay: true);
        new SqliteStandingLeaseSafetyControlTransaction(context.Fixture.Store)
            .SetUnattendedMode("task263-disable", false, "task263-test", context.Fixture.CreatedAt.AddMinutes(1));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(3L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void StopAllBoundaryInvalidatesOlderApprovalBeforeEnvironmentProbe()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var stopAllAt = context.Fixture.CreatedAt.AddMinutes(1);
        context.Fixture.Execute(
            "UPDATE unattended_safety_state SET stop_all_applied = 1, stop_all_operation_id = $operationId, stop_all_reason_code = $reason, stop_all_requested_at_utc = $at, stop_all_applied_at_utc = $at, version = version + 1 WHERE state_id = 'global';",
            ("$operationId", "task263-stop-all"),
            ("$reason", "task263-test"),
            ("$at", stopAllAt.UtcDateTime.Ticks));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.StopAllActive, result.ReasonCode);
        Assert.True(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void ProviderFailureBecomesStableBlockedWithoutLeakingTheException()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var provider = new ThrowingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.EnvironmentUnavailable, result.ReasonCode);
        Assert.True(result.Changed);
        Assert.Equal(1, provider.CaptureCount);
        Assert.DoesNotContain("provider failure", result.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void ProviderSnapshotWithNonTrustedTimeBecomesStableBlocked()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var provider = new NonTrustedTimeEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.EnvironmentUnavailable, result.ReasonCode);
        Assert.True(result.Changed);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void RecheckingRecoveryUsesOnlyTheFinalCas()
    {
        using var context = RecheckContext.Create();
        var occurrenceRepository = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = occurrenceRepository.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, context.Slot.ScheduledStartUtc!.Value).Succeeded);
        occurrenceRepository.Update(occurrence, occurrence.Version - 1);
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, result.Status);
        Assert.Equal(3, result.FinalOccurrenceVersion);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(3L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
    }

    [Fact]
    public void FinalOccurrenceReadbackMismatchRollsBackTheWholeTransaction()
    {
        using var context = RecheckContext.Create();
        var provider = new MatchingEnvironmentProvider();
        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider,
            beforeFinalReadbackHookForTest: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE plan_occurrences SET version = version + 1 WHERE id = $id;";
                command.Parameters.AddWithValue("$id", context.Slot.OccurrenceId!);
                Assert.Equal(1, command.ExecuteNonQuery());
            }).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal("due", context.Fixture.Scalar(
            "SELECT status_code FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!)));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void CasConflictReturnsConflictAndOriginalOccurrenceSnapshot()
    {
        using var context = RecheckContext.Create();
        var provider = new MatchingEnvironmentProvider();
        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider,
            afterFirstRecheckingCasHookForTest: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE plan_occurrences SET version = version + 1 WHERE id = $id;";
                command.Parameters.AddWithValue("$id", context.Slot.OccurrenceId!);
                Assert.Equal(1, command.ExecuteNonQuery());
            }).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Conflict, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal("due", context.Fixture.Scalar(
            "SELECT status_code FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!)));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
    }

    [Fact]
    public void AuthorizedWithoutSpecificationIsPersistedSnapshotInvalidAndReadOnly()
    {
        using var context = RecheckContext.Create();
        context.PromoteDueToAuthorized();
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Authorized, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(4L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
    }

    [Fact]
    public void AuthorizedWithoutSpecificationBeforeWindowRemainsPersistedSnapshotInvalid()
    {
        using var context = RecheckContext.Create();
        context.PromoteDueToAuthorized();
        var provider = new MatchingEnvironmentProvider();
        var beforeWindow = context.Slot.ScheduledStartUtc!.Value.AddTicks(-1);

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => beforeWindow,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Authorized, result.FinalOccurrenceStatus);
        Assert.Equal(4, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void PausedPlanBeforeWindowIsBlockedByLifecyclePrecedence()
    {
        using var context = RecheckContext.Create(nextDay: true);
        context.Fixture.Execute(
            "UPDATE plans SET status_code = 'paused' WHERE id = $id;",
            ("$id", context.Fixture.PlanId));
        context.Fixture.Execute(
            "UPDATE plan_occurrences SET updated_at_utc = $updated WHERE id = $id;",
            ("$updated", context.Fixture.CreatedAt.UtcDateTime.Ticks),
            ("$id", context.Slot.OccurrenceId!));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotEnabled, result.ReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked, result.FinalOccurrenceStatus);
        Assert.Equal(3, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void RevokedLeaseBeforeWindowIsBlockedByLifecyclePrecedence()
    {
        using var context = RecheckContext.Create(nextDay: true);
        context.Fixture.Execute(
            "UPDATE recurring_consent_leases SET status_code = 'revoked', updated_at_utc = $updated, version = version + 1 WHERE lease_id = $id;",
            ("$updated", context.Fixture.CreatedAt.AddMinutes(1).UtcDateTime.Ticks),
            ("$id", context.Lease.LeaseId));
        context.Fixture.Execute(
            "UPDATE plan_occurrences SET updated_at_utc = $updated WHERE id = $id;",
            ("$updated", context.Fixture.CreatedAt.UtcDateTime.Ticks),
            ("$id", context.Slot.OccurrenceId!));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseNotActive, result.ReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked, result.FinalOccurrenceStatus);
        Assert.Equal(3, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void PersistedOccurrenceWindowCorruptionBeforeWindowIsRejectedReadOnly()
    {
        using var context = RecheckContext.Create(nextDay: true);
        context.Fixture.Execute(
            "UPDATE plan_occurrences SET window_end_utc = window_end_utc + 1 WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!))));
    }

    [Fact]
    public void RecheckingBeforeWindowKeepsOriginalStatusAndVersion()
    {
        using var context = RecheckContext.Create(nextDay: true);
        var repository = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = repository.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, context.Slot.ScheduledStartUtc!.Value).Succeeded);
        repository.Update(occurrence, occurrence.Version - 1);
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid, result.ReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Rechecking, result.FinalOccurrenceStatus);
        Assert.Equal(2, result.FinalOccurrenceVersion);
        Assert.False(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
        var persisted = repository.Get(context.Slot.OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Rechecking, persisted.Status);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public void PartialExecutionClaimsArePersistedSnapshotInvalid()
    {
        var mutations = new Action<RecheckContext>[]
        {
            context => new SqliteRecordingRunRepository(context.Fixture.Store)
                .Insert(new RecordingRun("task263-run-only", context.Slot.OccurrenceId!, context.Fixture.CreatedAt)),
            context => context.InsertRecurringUse("task263-use-only", "task263-use-only-run"),
            context => context.Fixture.Execute(
                "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET run_id = 'task263-missing-run' WHERE id = $id;",
                ("$id", context.Slot.OccurrenceId!)),
            context =>
            {
                new SqliteRecordingRunRepository(context.Fixture.Store)
                    .Insert(new RecordingRun("task263-claim-run", context.Slot.OccurrenceId!, context.Fixture.CreatedAt));
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET status_code = 'run_created', run_id = $runId, updated_at_utc = $updated, version = 2 WHERE id = $id;",
                    ("$runId", "task263-claim-run"),
                    ("$updated", context.Fixture.CreatedAt.UtcDateTime.Ticks),
                    ("$id", context.Slot.OccurrenceId!));
                context.InsertRecurringUse("task263-mismatched-use", "task263-other-run");
            },
        };

        foreach (var mutation in mutations)
        {
            using var context = RecheckContext.Create();
            mutation(context);
            var provider = new MatchingEnvironmentProvider();
            var result = new RecurringOccurrenceEnvironmentRecheckService(
                context.Fixture.Store,
                () => context.Slot.ScheduledStartUtc!.Value,
                provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
            Assert.False(result.Changed);
            Assert.Equal(0, provider.CaptureCount);
        }
    }

    [Fact]
    public void MultipleRunsWithUseArePersistedSnapshotInvalid()
    {
        using var context = RecheckContext.Create();
        context.AllowMultipleRunsPerOccurrenceForCorruption();
        var runs = new SqliteRecordingRunRepository(context.Fixture.Store);
        runs.Insert(new RecordingRun("task263rr-run-a", context.Slot.OccurrenceId!, context.Fixture.CreatedAt));
        runs.Insert(new RecordingRun("task263rr-run-b", context.Slot.OccurrenceId!, context.Fixture.CreatedAt));
        context.InsertRecurringUse("task263rr-use", "task263rr-run-a");
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(2L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrenceId;",
            ("$occurrenceId", context.Slot.OccurrenceId!))));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void MultipleRunsWithoutUseArePersistedSnapshotInvalid()
    {
        using var context = RecheckContext.Create();
        context.AllowMultipleRunsPerOccurrenceForCorruption();
        var runs = new SqliteRecordingRunRepository(context.Fixture.Store);
        runs.Insert(new RecordingRun("task263rr-run-a", context.Slot.OccurrenceId!, context.Fixture.CreatedAt));
        runs.Insert(new RecordingRun("task263rr-run-b", context.Slot.OccurrenceId!, context.Fixture.CreatedAt));
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.Due, result.FinalOccurrenceStatus);
        Assert.Equal(1, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(2L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrenceId;",
            ("$occurrenceId", context.Slot.OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void MultipleUsesWithRunArePersistedSnapshotInvalid()
    {
        using var context = RecheckContext.Create();
        context.PromoteDueToAuthorizedWithSpecification();
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            () => "task263rr-run",
            () => "task263rr-use-a").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        context.AllowMultipleUsesPerRunForCorruption();
        context.InsertRecurringUse("task263rr-use-b", "task263rr-run");
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, result.FinalOccurrenceStatus);
        Assert.Equal(4, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrenceId;",
            ("$occurrenceId", context.Slot.OccurrenceId!))));
        Assert.Equal(2L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void TerminalOccurrenceWithCompleteRunAndUseClaimIsAlreadyTerminal()
    {
        using var context = RecheckContext.Create();
        context.PromoteDueToAuthorizedWithSpecification();
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            () => "task263-terminal-run",
            () => "task263-terminal-use").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);

        var repository = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = repository.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Blocked, context.Slot.ScheduledStartUtc!.Value, "task263-terminal").Succeeded);
        repository.Update(occurrence, occurrence.Version - 1);
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyTerminal, result.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyTerminal, result.ReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked, result.FinalOccurrenceStatus);
        Assert.Equal(5, result.FinalOccurrenceVersion);
        Assert.False(result.Changed);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void ExistingRunAndUseClaimReturnsAlreadyAdvancedWithoutRecheck()
    {
        using var context = RecheckContext.Create();
        context.PromoteDueToAuthorizedWithSpecification();
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            () => "task263-run",
            () => "task263-use").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        var provider = new MatchingEnvironmentProvider();

        var result = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAdvanced, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, result.FinalOccurrenceStatus);
        Assert.Equal(4, result.FinalOccurrenceVersion);
        Assert.Equal(0, provider.CaptureCount);
        Assert.NotNull(result.SpecificationSummary);
        Assert.Equal(reservation.SpecificationDigest, result.SpecificationDigest);
    }

    [Fact]
    public void TerminalOccurrenceWithValidHistoricalSpecificationReturnsAlreadyTerminal()
    {
        using var context = RecheckContext.Create();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var first = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => now,
            new MatchingEnvironmentProvider()).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);

        var occurrenceRepository = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = occurrenceRepository.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Blocked, now, "task263-test-terminal").Succeeded);
        occurrenceRepository.Update(occurrence, occurrence.Version - 1);
        var provider = new MatchingEnvironmentProvider();
        var replay = new RecurringOccurrenceEnvironmentRecheckService(context.Fixture.Store, () => now, provider)
            .Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyTerminal, replay.Status);
        Assert.False(replay.Changed);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(first.SpecificationDigest, context.Fixture.Scalar(
            "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
    }

    [Fact]
    public void EachInjectedFailurePointRollsBackRecheckingAndSpecification()
    {
        foreach (var failurePoint in Enum.GetValues<RecurringOccurrenceEnvironmentRecheckFailurePoint>())
        {
            using var context = RecheckContext.Create();
            var provider = new MatchingEnvironmentProvider();
            var result = new RecurringOccurrenceEnvironmentRecheckService(
                context.Fixture.Store,
                () => context.Slot.ScheduledStartUtc!.Value,
                provider,
                point =>
                {
                    if (point == failurePoint)
                        throw new InvalidOperationException("injected task263 failure");
                }).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.TransactionFailed, result.ReasonCode);
            Assert.Equal(failurePoint == RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterFirstRecheckingCas ? 0 : 1, provider.CaptureCount);
            Assert.Equal("due", context.Fixture.Scalar(
                "SELECT status_code FROM plan_occurrences WHERE id = $id;",
                ("$id", context.Slot.OccurrenceId!)));
            Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM plan_occurrences WHERE id = $id;",
                ("$id", context.Slot.OccurrenceId!))));
            Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
                "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
        }
    }

    [Fact]
    public async Task ConcurrentSameOccurrenceCallsAuthorizeAtMostOnce()
    {
        using var context = RecheckContext.Create();
        var provider = new MatchingEnvironmentProvider();
        var service = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            provider);

        var results = await Task.WhenAll(
            Task.Run(() => service.Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)),
            Task.Run(() => service.Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)));

        Assert.Equal(1, results.Count(result => result.Status == RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized));
        Assert.Equal(1, results.Count(result => result.Status == RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAuthorized));
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void DifferentLeaseCannotReuseAnAuthorizedOccurrenceSpecification()
    {
        using var context = RecheckContext.Create();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var first = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => now,
            new MatchingEnvironmentProvider()).Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);

        var otherLease = context.AddPendingLeaseWithEvidence("task263-other-lease", "task263-other-approval");
        var other = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () => now,
            new MatchingEnvironmentProvider()).Recheck(otherLease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict, other.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Conflict, other.ReasonCode);
        Assert.False(other.Changed);
        Assert.Equal(first.SpecificationDigest, context.Fixture.Scalar(
            "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
        Assert.Equal(PlanOccurrenceStatus.Authorized, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public void ClockRollbackAndInvalidBusinessInputsAreRejectedWithoutOpeningAWriteTransaction()
    {
        using var context = RecheckContext.Create();
        var calls = 0;
        var service = new RecurringOccurrenceEnvironmentRecheckService(
            context.Fixture.Store,
            () =>
            {
                calls++;
                return context.Slot.ScheduledStartUtc!.Value.AddTicks(calls == 1 ? 0 : -1);
            },
            new MatchingEnvironmentProvider());

        var first = service.Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        var rollback = service.Recheck(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        var invalid = service.Recheck(" task263-lease", context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, first.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, rollback.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.ClockMovedBackwards, rollback.ReasonCode);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected, invalid.Status);
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.RequestInvalid, invalid.ReasonCode);
    }

    private sealed class RecheckContext : IDisposable
    {
        private RecheckContext(RecurringLeaseFixture fixture, RecurringConsentLease lease, RecurringOccurrenceSlotSnapshot slot)
        {
            Fixture = fixture;
            Lease = lease;
            Slot = slot;
        }

        internal RecurringLeaseFixture Fixture { get; }
        internal RecurringConsentLease Lease { get; }
        internal RecurringOccurrenceSlotSnapshot Slot { get; }

        internal static RecheckContext Create(bool nextDay = false)
        {
            var fixture = RecurringLeaseFixture.Create(maxOccurrences: nextDay ? 2 : 1);
            var calculator = new RecurringOccurrenceCalculator();
            var firstCalculation = calculator.CalculateNext(fixture.PlanId, 1, fixture.Schedule);
            var firstCandidate = firstCalculation.Candidate ?? throw new InvalidOperationException("The fixture did not produce a recurring candidate.");
            var calculation = nextDay
                ? calculator.CalculateNext(fixture.PlanId, 1, fixture.Schedule, firstCandidate.PlannedEndUtc)
                : firstCalculation;
            var candidate = calculation.Candidate ?? throw new InvalidOperationException("The fixture did not produce a recurring candidate.");
            var slot = new SqliteRecurringOccurrenceMaterializationTransaction(fixture.Store)
                .Materialize(candidate, fixture.CreatedAt);

            var lease = CreateLease(
                fixture,
                "task263-lease",
                fixture.CreatedAt.AddHours(-1),
                fixture.CreatedAt.AddMinutes(-30),
                nextDay ? fixture.CreatedAt.AddDays(3) : fixture.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
            var safety = new SqliteStandingLeaseSafetyControlTransaction(fixture.Store);
            safety.SetUnattendedMode("task263-enable", true, "task263-test", fixture.CreatedAt.AddMinutes(-1));
            ActivateLease(fixture, lease, "task263-approval");

            var due = new SqlitePeriodicOccurrenceDueProjectionTransaction(fixture.Store)
                .Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value);
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
            return new RecheckContext(fixture, lease, slot);
        }

        internal RecurringConsentLease AddPendingLeaseWithEvidence(string leaseId, string approvalId)
        {
            var lease = CreateLease(
                Fixture,
                leaseId,
                Fixture.CreatedAt.AddHours(-1),
                Fixture.CreatedAt.AddMinutes(-30),
                Fixture.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(Fixture.Store).InsertPending(lease);
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                approvalId,
                lease.LeaseId,
                lease.PlanId,
                lease.ConfigurationRef.ConfigurationDigest,
                lease.AuthorizationDigest,
                "S-1-5-21-task263",
                "session-task263",
                Fixture.CreatedAt);
            Fixture.Execute(
                "INSERT INTO recurring_lease_local_approvals (approval_id, lease_id, plan_id, configuration_digest, authorization_digest, current_user_sid, session_binding, approved_at_utc, approval_kind_code, approval_version, approval_digest) VALUES ($approvalId, $leaseId, $planId, $configurationDigest, $authorizationDigest, $sid, $session, $approvedAt, $kind, $version, $digest);",
                ("$approvalId", receipt.ApprovalId),
                ("$leaseId", receipt.LeaseId),
                ("$planId", receipt.PlanId),
                ("$configurationDigest", receipt.ConfigurationDigest),
                ("$authorizationDigest", receipt.AuthorizationDigest),
                ("$sid", receipt.CurrentUserSid),
                ("$session", receipt.SessionBinding),
                ("$approvedAt", receipt.ApprovedAtUtc.UtcDateTime.Ticks),
                ("$kind", receipt.ApprovalKind),
                ("$version", receipt.ApprovalVersion),
                ("$digest", receipt.ApprovalDigest));
            return lease;
        }

        internal void InsertRecurringUse(string useId, string runId)
        {
            Fixture.Execute(
                "PRAGMA foreign_keys = OFF; INSERT INTO recurring_lease_uses (use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version) VALUES ($useId, $leaseId, $planId, $identity, $occurrenceId, $runId, 'reserved', 1, $duration, NULL, $created, $created, 0);",
                ("$useId", useId),
                ("$leaseId", Lease.LeaseId),
                ("$planId", Lease.PlanId),
                ("$identity", Slot.OccurrenceIdentity),
                ("$occurrenceId", Slot.OccurrenceId!),
                ("$runId", runId),
                ("$duration", Lease.PerRunDuration.Ticks),
                ("$created", Fixture.CreatedAt.UtcDateTime.Ticks));
        }

        internal void AllowMultipleRunsPerOccurrenceForCorruption()
        {
            Fixture.Execute("""
                PRAGMA foreign_keys = OFF;
                BEGIN;
                CREATE TABLE recording_runs_corrupt (
                    id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    occurrence_id TEXT NOT NULL CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    has_crossed_start_commit INTEGER NOT NULL CHECK (has_crossed_start_commit IN (0, 1)),
                    media_artifact_id TEXT NULL CHECK (media_artifact_id IS NULL OR length(trim(media_artifact_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    bundle_id TEXT NULL CHECK (bundle_id IS NULL OR length(trim(bundle_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    terminal_reason_code TEXT NULL CHECK (terminal_reason_code IS NULL OR length(trim(terminal_reason_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
                    created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
                    updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
                    version INTEGER NOT NULL CHECK (version >= 0),
                    UNIQUE (id, occurrence_id),
                    FOREIGN KEY (occurrence_id) REFERENCES plan_occurrences(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
                );
                INSERT INTO recording_runs_corrupt
                    SELECT id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id,
                           terminal_reason_code, created_at_utc, updated_at_utc, version
                    FROM recording_runs;
                DROP INDEX idx_recording_runs_status_updated;
                DROP TABLE recording_runs;
                ALTER TABLE recording_runs_corrupt RENAME TO recording_runs;
                CREATE INDEX idx_recording_runs_status_updated ON recording_runs(status_code, updated_at_utc);
                COMMIT;
                """);
        }

        internal void AllowMultipleUsesPerRunForCorruption()
        {
            Fixture.Execute("""
                PRAGMA foreign_keys = OFF;
                BEGIN;
                CREATE TABLE recurring_lease_uses_corrupt (
                    use_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(use_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND use_id = trim(use_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND lease_id = trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND plan_id = trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    occurrence_identity TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_identity, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND occurrence_identity = trim(occurrence_identity, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    occurrence_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND occurrence_id = trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    run_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND run_id = trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
                    status_code TEXT NOT NULL COLLATE BINARY CHECK (status_code IN ('available', 'reserved', 'start_committed', 'consumed', 'settled', 'started_unknown')),
                    reserved_use_count INTEGER NOT NULL CHECK (reserved_use_count BETWEEN 0 AND 1),
                    reserved_duration_ticks INTEGER NOT NULL CHECK (reserved_duration_ticks >= 0),
                    actual_settled_duration_ticks INTEGER NULL CHECK (actual_settled_duration_ticks IS NULL OR actual_settled_duration_ticks >= 0),
                    created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
                    updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
                    version INTEGER NOT NULL CHECK (version >= 0),
                    PRIMARY KEY (use_id),
                    FOREIGN KEY (lease_id, plan_id) REFERENCES recurring_consent_leases(lease_id, plan_id) ON DELETE RESTRICT,
                    FOREIGN KEY (occurrence_identity, plan_id, occurrence_id)
                        REFERENCES recurring_occurrence_slots(occurrence_identity, plan_id, occurrence_id) ON DELETE RESTRICT,
                    FOREIGN KEY (run_id, occurrence_id) REFERENCES recording_runs(id, occurrence_id) ON DELETE RESTRICT,
                    CHECK ((status_code = 'available' AND reserved_use_count = 0 AND reserved_duration_ticks = 0 AND actual_settled_duration_ticks IS NULL)
                        OR (status_code IN ('reserved', 'start_committed', 'consumed', 'started_unknown')
                            AND reserved_use_count = 1 AND reserved_duration_ticks > 0 AND actual_settled_duration_ticks IS NULL)
                        OR (status_code = 'settled' AND reserved_use_count = 1 AND reserved_duration_ticks > 0
                            AND actual_settled_duration_ticks IS NOT NULL AND actual_settled_duration_ticks <= reserved_duration_ticks))
                );
                INSERT INTO recurring_lease_uses_corrupt
                    SELECT use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code,
                           reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks,
                           created_at_utc, updated_at_utc, version
                    FROM recurring_lease_uses;
                DROP INDEX idx_recurring_lease_uses_lease_status_created;
                DROP TABLE recurring_lease_uses;
                ALTER TABLE recurring_lease_uses_corrupt RENAME TO recurring_lease_uses;
                CREATE INDEX idx_recurring_lease_uses_lease_status_created
                    ON recurring_lease_uses(lease_id, status_code, created_at_utc, use_id);
                COMMIT;
                """);
        }

        internal void PromoteDueToAuthorized()
        {
            var repository = new SqlitePlanOccurrenceRepository(Fixture.Store);
            var occurrence = repository.Get(Slot.OccurrenceId!);
            foreach (var target in new[] { PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Authorized })
            {
                Assert.True(occurrence.TryTransition(target, Slot.ScheduledStartUtc!.Value).Succeeded);
                repository.Update(occurrence, occurrence.Version - 1);
                occurrence = repository.Get(Slot.OccurrenceId!);
            }
        }

        internal void PromoteDueToAuthorizedWithSpecification()
        {
            var result = new RecurringOccurrenceEnvironmentRecheckService(
                Fixture.Store,
                () => Slot.ScheduledStartUtc!.Value,
                new MatchingEnvironmentProvider())
                .Recheck(Lease.LeaseId, Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, result.Status);
        }

        public void Dispose() => Fixture.Dispose();

        private static RecurringConsentLease CreateLease(
            RecurringLeaseFixture fixture,
            string leaseId,
            DateTimeOffset createdAt,
            DateTimeOffset validFrom,
            DateTimeOffset validUntil)
        {
            var latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
                fixture.PlanId,
                fixture.Setup.ScheduleVersion.ScheduleRevision,
                fixture.Schedule);
            return RecurringConsentLease.CreatePending(
                leaseId,
                fixture.Setup.ConfigurationRef,
                validFrom,
                validUntil,
                latestEnd,
                fixture.Setup.ExactProfile.Duration,
                maxUses: 10,
                maxCumulativeDuration: TimeSpan.FromMinutes(20),
                createdAt);
        }

        private static void ActivateLease(RecurringLeaseFixture fixture, RecurringConsentLease lease, string approvalId)
        {
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                approvalId,
                lease.LeaseId,
                lease.PlanId,
                lease.ConfigurationRef.ConfigurationDigest,
                lease.AuthorizationDigest,
                "S-1-5-21-task263",
                "session-task263",
                fixture.CreatedAt);
            var activation = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt)
                .Activate(lease.LeaseId, receipt);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, activation.Status);
        }
    }

    private sealed class MatchingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        internal int CaptureCount { get; private set; }
        internal RecurringOccurrenceEnvironmentCaptureRequest? LastRequest { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            CaptureCount++;
            LastRequest = request;
            var requirements = request.Requirements;
            var requiredFreeBytes = RecordingPreflightChecker.RequiredFreeSpaceBytes(requirements.ReservedDuration);
            return new StandingLeaseExecutionEnvironment(
                request.TrustedNowUtc,
                requirements.CurrentUserSid,
                requirements.SessionBinding,
                isInteractiveDesktop: true,
                displays: new[]
                {
                    new StandingLeaseDisplayMetadata(
                        "task263-display",
                        requirements.StableDisplayFingerprint,
                        DisplayIdentityResolutionStatus.Resolved,
                        requirements.DisplayBounds,
                        requirements.DpiX,
                        requirements.DpiY,
                        requirements.PhysicalWidth,
                        requirements.PhysicalHeight,
                        requirements.Orientation),
                },
                requirements.TopologyDigest,
                new StandingLeaseOutputFileSystemSnapshot(
                    requirements.NormalizedOutputDirectory,
                    requirements.FrozenOutputFilePath,
                    directoryExists: true,
                    frozenFileExists: false,
                    directoryWritable: true,
                    freeSpaceAvailable: true,
                    availableFreeBytes: requiredFreeBytes,
                    requiredFreeBytes));
        }
    }

    private sealed class ThrowingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        internal int CaptureCount { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            CaptureCount++;
            throw new InvalidOperationException("provider failure must not escape the boundary");
        }
    }

    private sealed class NonTrustedTimeEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        internal int CaptureCount { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            CaptureCount++;
            return new StandingLeaseExecutionEnvironment(
                request.TrustedNowUtc.AddHours(1),
                "S-1-5-21-task263",
                "session-task263",
                isInteractiveDesktop: true);
        }
    }
}
