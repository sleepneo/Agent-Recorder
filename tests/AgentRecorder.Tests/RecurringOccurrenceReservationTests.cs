using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceReservationTests
{
    [Fact]
    public void ReserveWritesExactRunUseAndOccurrenceChainAndKeepsParentsUnchanged()
    {
        using var context = ReservationContext.Create();
        var planBefore = new SqlitePlanDefinitionRepository(context.Fixture.Store).Get(context.Fixture.PlanId);
        var leaseBefore = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);

        var result = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task261-happy",
            () => "use-task261-happy").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.Reserved, result.ReasonCode);
        Assert.True(result.Changed);
        Assert.Equal(context.Slot.OccurrenceId, result.OccurrenceId);
        Assert.Equal("run-task261-happy", result.RunId);
        Assert.Equal("use-task261-happy", result.UseId);
        Assert.NotNull(result.SpecificationSummary);
        Assert.Equal(result.SpecificationSummary!.SpecificationDigest, result.SpecificationDigest);
        Assert.Equal(context.Slot.OccurrenceIdentity, result.SpecificationSummary.OccurrenceIdentity);
        Assert.Equal(context.Slot.OccurrenceId, result.SpecificationSummary.OccurrenceId);
        Assert.Equal(context.Lease.PerRunDuration, result.SpecificationSummary.Duration);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;", ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(result.SpecificationDigest, Convert.ToString(context.Fixture.Scalar("SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;", ("$identity", context.Slot.OccurrenceIdentity))));

        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        var planAfter = new SqlitePlanDefinitionRepository(context.Fixture.Store).Get(context.Fixture.PlanId);
        var leaseAfter = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);

        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
        Assert.Equal(result.RunId, occurrence.RunId);
        Assert.Equal(RecordingRunStatus.Created, run.Status);
        Assert.False(run.HasCrossedStartCommit);
        Assert.Equal(context.Slot.OccurrenceId, run.OccurrenceId);
        Assert.NotNull(use);
        Assert.Equal(LeaseUseStatus.Reserved, use!.Status);
        Assert.Equal(1, use.ReservedUseCount);
        Assert.Equal(context.Lease.PerRunDuration, use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);
        Assert.Equal(planBefore.Version, planAfter.Version);
        Assert.Equal(planBefore.UpdatedAtUtc, planAfter.UpdatedAtUtc);
        Assert.Equal(leaseBefore.Version, leaseAfter.Version);
        Assert.Equal(ConsentLeaseStatus.Active, leaseAfter.Status);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $id;", ("$id", context.Slot.OccurrenceId))));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity;", ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void AuthorizedOccurrenceWithoutExecutionSpecificationIsRejectedWithoutExecutionRows()
    {
        using var context = ReservationContext.Create();
        context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_delete; DELETE FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity));

        var result = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task264-missing-spec",
            () => "use-task264-missing-spec")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Null(result.SpecificationSummary);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Theory]
    [InlineData("specification_digest")]
    [InlineData("plan_id")]
    public void CorruptExecutionSpecificationIsRejectedBeforeExecutionWrites(string tamper)
    {
        using var context = ReservationContext.Create();
        var sql = tamper == "specification_digest"
            ? "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $value WHERE occurrence_identity = $identity;"
            : "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET plan_id = 'task264-corrupt-plan' WHERE occurrence_identity = $identity;";
        context.Fixture.Execute(
            sql,
            ("$value", TamperHex("recurring-occurrence-execution-spec/v1:" + new string('a', 64))),
            ("$identity", context.Slot.OccurrenceIdentity));

        var result = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task264-corrupt-spec",
            () => "use-task264-corrupt-spec")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.Null(result.SpecificationSummary);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void ExistingClaimWithoutExecutionSpecificationIsNotAcceptedAsReplay()
    {
        using var context = ReservationContext.Create();
        var first = ReserveOnce(context, "run-task264-missing-replay-spec", "use-task264-missing-replay-spec");
        context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_delete; DELETE FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity));

        var replay = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddMinutes(10),
            () => "run-task264-new-replay",
            () => "use-task264-new-replay")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, replay.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, replay.ReasonCode);
        Assert.False(replay.Changed);
        Assert.Null(replay.SpecificationSummary);
        Assert.Null(replay.RunId);
        Assert.Null(replay.UseId);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void NonAuthorizedOccurrenceWithExecutionSpecificationIsPersistedSnapshotInvalid()
    {
        using var context = ReservationContext.Create();
        context.Fixture.Execute(
            "UPDATE plan_occurrences SET status_code = 'due', updated_at_utc = updated_at_utc + 1, version = version + 1 WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!));

        var result = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task264-illegal-state",
            () => "use-task264-illegal-state")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.Null(result.SpecificationSummary);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void FinalReadbackMissingSpecificationRollsBackRunUseAndOccurrenceCas()
    {
        using var context = ReservationContext.Create();
        var result = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task264-final-spec",
            () => "use-task264-final-spec",
            beforeFinalReadbackHookForTest: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_delete; DELETE FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;";
                command.Parameters.AddWithValue("$identity", context.Slot.OccurrenceIdentity);
                command.ExecuteNonQuery();
            })
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Changed);
        Assert.Null(result.SpecificationSummary);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        Assert.Equal(PlanOccurrenceStatus.Authorized, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;", ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void ProductionRequestAndReserveMethodExposeOnlyTwoBusinessIds()
    {
        var request = typeof(RecurringOccurrenceReservationRequest);
        Assert.Equal(new[] { "LeaseId", "OccurrenceIdentity" }, request.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(property => property.Name));
        Assert.Equal(new[] { typeof(string), typeof(string) }, request.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single().GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(new[] { typeof(string), typeof(string) }, typeof(RecurringOccurrenceReservationService).GetMethod(
            nameof(RecurringOccurrenceReservationService.Reserve),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("due")]
    [InlineData("rechecking")]
    [InlineData("pending_lease_approval")]
    [InlineData("pending_confirmation")]
    public void NonAuthorizedOccurrenceStatesNeverCreateExecutionRows(string targetStatus)
    {
        using var context = ReservationContext.Create(targetStatus: targetStatus);
        var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt)
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.OccurrenceNotAuthorized, result.ReasonCode);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void PausedPlanAndDisabledSafetyAreRejectedBeforeAnyWrite()
    {
        using (var paused = ReservationContext.Create())
        {
            var repository = new SqlitePlanDefinitionRepository(paused.Fixture.Store);
            var plan = repository.Get(paused.Fixture.PlanId);
            Assert.True(plan.TryTransition(PlanDefinitionStatus.Paused, paused.Fixture.CreatedAt.AddTicks(1)).Succeeded);
            repository.Update(plan, plan.Version - 1);

            var result = new RecurringOccurrenceReservationService(paused.Fixture.Store, () => paused.Fixture.CreatedAt.AddTicks(1))
                .Reserve(paused.Lease.LeaseId, paused.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PlanNotEnabled, result.ReasonCode);
            Assert.Equal(0L, Convert.ToInt64(paused.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        }

        using var disabled = ReservationContext.Create(disableSafetyBeforeReservation: true);
        var disabledResult = new RecurringOccurrenceReservationService(disabled.Fixture.Store, () => disabled.Fixture.CreatedAt)
            .Reserve(disabled.Lease.LeaseId, disabled.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, disabledResult.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.UnattendedDisabled, disabledResult.ReasonCode);
    }

    [Fact]
    public void CancelledPlanAndEveryNonActiveLeaseStateAreRejectedWithoutWrites()
    {
        using (var cancelled = ReservationContext.Create())
        {
            var repository = new SqlitePlanDefinitionRepository(cancelled.Fixture.Store);
            var plan = repository.Get(cancelled.Fixture.PlanId);
            Assert.True(plan.TryTransition(PlanDefinitionStatus.Cancelled, cancelled.Fixture.CreatedAt.AddTicks(1)).Succeeded);
            repository.Update(plan, plan.Version - 1);
            var result = new RecurringOccurrenceReservationService(cancelled.Fixture.Store, () => cancelled.Fixture.CreatedAt.AddTicks(1))
                .Reserve(cancelled.Lease.LeaseId, cancelled.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PlanNotEnabled, result.ReasonCode);
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pending"] = RecurringLeaseQuotaReasonCodes.Pending,
            ["revoked"] = RecurringLeaseQuotaReasonCodes.Revoked,
            ["expired"] = RecurringLeaseQuotaReasonCodes.Expired,
            ["exhausted"] = RecurringLeaseQuotaReasonCodes.Exhausted,
        };
        foreach (var pair in expected)
        {
            using var context = ReservationContext.Create();
            context.Fixture.Execute("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = $status, version = version + 1, updated_at_utc = $updated WHERE lease_id = $leaseId;", ("$status", pair.Key), ("$updated", context.Fixture.CreatedAt.AddTicks(1).UtcDateTime.Ticks), ("$leaseId", context.Lease.LeaseId));
            var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddTicks(1))
                .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(pair.Value, result.ReasonCode);
            Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        }
    }

    [Fact]
    public void StopAllApprovalBoundaryAndClockWindowEdgesAreStable()
    {
        using (var stopped = ReservationContext.Create(stopAllBeforeReservation: true))
        {
            var result = new RecurringOccurrenceReservationService(stopped.Fixture.Store, () => stopped.Fixture.CreatedAt)
                .Reserve(stopped.Lease.LeaseId, stopped.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.StopAllBoundary, result.ReasonCode);
        }

        using (var late = ReservationContext.Create())
        {
            var afterLatest = new RecurringOccurrenceReservationService(late.Fixture.Store, () => late.Fixture.CreatedAt.AddTicks(1))
                .Reserve(late.Lease.LeaseId, late.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.OccurrenceAfterLatestStart, afterLatest.ReasonCode);
        }

        using (var before = ReservationContext.Create(validFrom: ReservationContext.DefaultCreatedAt.AddMinutes(1)))
        {
            var beforeValidity = new RecurringOccurrenceReservationService(before.Fixture.Store, () => before.Fixture.CreatedAt)
                .Reserve(before.Lease.LeaseId, before.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, beforeValidity.ReasonCode);
        }

        using var expiry = ReservationContext.Create(validUntil: ReservationContext.DefaultCreatedAt.AddDays(4));
        var atExpiry = new RecurringOccurrenceReservationService(expiry.Fixture.Store, () => expiry.Lease.ValidUntilUtc)
            .Reserve(expiry.Lease.LeaseId, expiry.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Expired, atExpiry.ReasonCode);
    }

    [Fact]
    public void ExactReplayAfterLatestAndLeaseExpiryIsReadOnlyAlreadyReserved()
    {
        using var context = ReservationContext.Create();
        var service = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "run-task261-replay-first",
            () => "use-task261-replay-first");
        var first = service.Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, first.Status);

        var occurrenceBefore = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var runBefore = new SqliteRecordingRunRepository(context.Fixture.Store).Get(first.RunId!);
        var useBefore = context.Fixture.ReadAccountingRow(first.UseId!);
        var replay = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Lease.ValidUntilUtc.AddHours(1),
            () => "run-task261-replay-second",
            () => "use-task261-replay-second").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.AlreadyReserved, replay.Status);
        Assert.False(replay.Changed);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.Equal(first.UseId, replay.UseId);
        Assert.Equal(first.SpecificationDigest, replay.SpecificationDigest);
        Assert.Equal(first.SpecificationSummary, replay.SpecificationSummary);
        Assert.Equal(occurrenceBefore.Version, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Version);
        Assert.Equal(runBefore.Version, new SqliteRecordingRunRepository(context.Fixture.Store).Get(first.RunId!).Version);
        Assert.Equal(useBefore, context.Fixture.ReadAccountingRow(first.UseId!));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void InFlightProgressedTuplesReturnAlreadyAdvancedWithoutNewIds()
    {
        foreach (var target in new[] { "start_committed", "recording", "finalizing", "media_ready" })
        {
            using var context = ReservationContext.Create();
            var first = ReserveOnce(context, "run-progressed-" + target, "use-progressed-" + target);
            ApplyValidProgressedTuple(context, first, target);
            var before = CaptureChainProjection(context, first.RunId!, first.UseId!);

            var replay = new RecurringOccurrenceReservationService(
                context.Fixture.Store,
                () => context.Lease.ValidUntilUtc.AddHours(1),
                () => "run-new-" + target,
                () => "use-new-" + target).Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

            Assert.True(replay.Status == RecurringOccurrenceReservationStatus.AlreadyAdvanced, $"{target}: {replay.Status}/{replay.ReasonCode}");
            Assert.False(replay.Changed);
            Assert.Equal(first.RunId, replay.RunId);
            Assert.Equal(first.UseId, replay.UseId);
            Assert.Equal(first.SpecificationDigest, replay.SpecificationDigest);
            Assert.Equal(first.SpecificationSummary, replay.SpecificationSummary);
            Assert.Equal(before, CaptureChainProjection(context, first.RunId!, first.UseId!));
        }
    }

    [Fact]
    public void SettledAndBlockedTerminalTuplesReturnAlreadyAdvancedAfterExpiry()
    {
        using (var settled = ReservationContext.Create())
        {
            var first = ReserveOnce(settled, "run-settled", "use-settled");
            ApplySettledTuple(settled, first);
            var before = CaptureChainProjection(settled, first.RunId!, first.UseId!);
            var replay = new RecurringOccurrenceReservationService(settled.Fixture.Store, () => settled.Lease.ValidUntilUtc.AddHours(1), () => "run-new", () => "use-new")
                .Reserve(settled.Lease.LeaseId, settled.Slot.OccurrenceIdentity);

            Assert.True(replay.Status == RecurringOccurrenceReservationStatus.AlreadyAdvanced, $"settled: {replay.Status}/{replay.ReasonCode}");
            Assert.Equal(first.RunId, replay.RunId);
            Assert.Equal(first.UseId, replay.UseId);
            Assert.Equal(before, CaptureChainProjection(settled, first.RunId!, first.UseId!));
        }

        foreach (var terminal in new[] { RecordingRunStatus.StartedUnknown, RecordingRunStatus.SessionInterrupted, RecordingRunStatus.Failed })
        {
            using var blocked = ReservationContext.Create();
            var first = ReserveOnce(blocked, "run-blocked-" + terminal, "use-blocked-" + terminal);
            ApplyBlockedTuple(blocked, first, terminal);
            var before = CaptureChainProjection(blocked, first.RunId!, first.UseId!);
            var replay = new RecurringOccurrenceReservationService(blocked.Fixture.Store, () => blocked.Lease.ValidUntilUtc.AddHours(1), () => "run-new", () => "use-new")
                .Reserve(blocked.Lease.LeaseId, blocked.Slot.OccurrenceIdentity);

            Assert.True(replay.Status == RecurringOccurrenceReservationStatus.AlreadyAdvanced, $"{terminal}: {replay.Status}/{replay.ReasonCode}");
            Assert.Equal(first.RunId, replay.RunId);
            Assert.Equal(first.UseId, replay.UseId);
            Assert.Equal(before, CaptureChainProjection(blocked, first.RunId!, first.UseId!));
        }
    }

    [Fact]
    public void ReleasedClaimIsReadOnlyRejectedAndCannotCreateAnotherExecutionIdentity()
    {
        using var context = ReservationContext.Create();
        var first = ReserveOnce(context, "run-released", "use-released");
        context.Fixture.UpdateAccountingRow(first.UseId!, "available", 0, 0, null, context.Fixture.CreatedAt.AddTicks(1), 1);
        var before = CaptureChainProjection(context, first.RunId!, first.UseId!);

        var replay = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddTicks(1), () => "run-new", () => "use-new")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, replay.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, replay.ReasonCode);
        Assert.False(replay.Changed);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.Equal(first.UseId, replay.UseId);
        Assert.Equal(before, CaptureChainProjection(context, first.RunId!, first.UseId!));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void IllegalHalfProgressedAndTerminalTuplesFailClosed()
    {
        var tuples = new[]
        {
            new InvalidChainTuple("run_created", "preparing", false, null, "reserved", null, 1, 1, 0),
            new InvalidChainTuple("run_created", "start_committed", true, null, "reserved", null, 1, 2, 0),
            new InvalidChainTuple("run_created", "recording", true, null, "start_committed", null, 1, 3, 1),
            new InvalidChainTuple("run_created", "settled", true, null, "consumed", null, 1, 6, 2),
            new InvalidChainTuple("completed", "recording", true, null, "consumed", null, 2, 3, 2),
            new InvalidChainTuple("blocked", "session_interrupted", true, "session_interrupted", "started_unknown", "different_reason", 2, 4, 3),
            new InvalidChainTuple("run_created", "failed", false, "capture_output_invalid", "consumed", null, 1, 1, 2),
        };

        foreach (var tuple in tuples)
        {
            using var context = ReservationContext.Create();
            var first = ReserveOnce(context, "run-invalid-" + tuple.RunStatus, "use-invalid-" + tuple.UseStatus);
            ApplyRawTuple(context, first, tuple);

            var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddMinutes(10), () => "run-new", () => "use-new")
                .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
            Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
            Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        }
    }

    [Fact]
    public void InvalidSettledDurationAndMissingOccurrenceAttachmentFailClosed()
    {
        using (var invalidDuration = ReservationContext.Create())
        {
            var first = ReserveOnce(invalidDuration, "run-invalid-duration", "use-invalid-duration");
            ApplySettledTuple(invalidDuration, first);
            invalidDuration.Fixture.Execute(
                "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; PRAGMA ignore_check_constraints = ON; UPDATE recurring_lease_uses SET actual_settled_duration_ticks = -1, version = version + 1, updated_at_utc = updated_at_utc + 1 WHERE use_id = $useId;",
                ("$useId", first.UseId!));
            var result = new RecurringOccurrenceReservationService(invalidDuration.Fixture.Store, () => invalidDuration.Fixture.CreatedAt.AddMinutes(10))
                .Reserve(invalidDuration.Lease.LeaseId, invalidDuration.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        }

        using var missingAttachment = ReservationContext.Create();
        var attachment = ReserveOnce(missingAttachment, "run-missing-attachment", "use-missing-attachment");
        missingAttachment.Fixture.Execute("UPDATE plan_occurrences SET run_id = NULL WHERE id = $occurrenceId;", ("$occurrenceId", missingAttachment.Slot.OccurrenceId!));
        var missingAttachmentResult = new RecurringOccurrenceReservationService(missingAttachment.Fixture.Store, () => missingAttachment.Fixture.CreatedAt.AddMinutes(10))
            .Reserve(missingAttachment.Lease.LeaseId, missingAttachment.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, missingAttachmentResult.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, missingAttachmentResult.ReasonCode);
    }

    [Fact]
    public void ExistingRunInFlightAccountingBlocksAnotherOccurrence()
    {
        using var context = ReservationContext.Create(slotCount: 2);
        var existingRun = new RecordingRun("run-existing-accounting", context.Slots[1].OccurrenceId!, context.Fixture.CreatedAt);
        new SqliteRecordingRunRepository(context.Fixture.Store).Insert(existingRun);
        context.Fixture.InsertAccountingRow(
            "use-existing-accounting",
            context.Lease,
            context.Slots[1],
            existingRun.Id,
            "reserved",
            context.Fixture.CreatedAt,
            1,
            context.Lease.PerRunDuration,
            null);

        var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt, () => "run-blocked", () => "use-blocked")
            .Reserve(context.Lease.LeaseId, context.Slots[0].OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.RunInFlight, result.ReasonCode);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void MissingApprovalAndWrongIdentityFailClosedWithoutExecutionWrites()
    {
        using (var missing = ReservationContext.Create())
        {
            missing.Fixture.Execute("PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_delete; DELETE FROM recurring_lease_local_approvals WHERE lease_id = $leaseId;", ("$leaseId", missing.Lease.LeaseId));
            var result = new RecurringOccurrenceReservationService(missing.Fixture.Store, () => missing.Fixture.CreatedAt)
                .Reserve(missing.Lease.LeaseId, missing.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
            Assert.Equal(0L, Convert.ToInt64(missing.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        }

        using var wrongIdentity = ReservationContext.Create();
        var wrong = new RecurringOccurrenceReservationService(wrongIdentity.Fixture.Store, () => wrongIdentity.Fixture.CreatedAt)
            .Reserve(wrongIdentity.Lease.LeaseId, wrongIdentity.Slot.OccurrenceIdentity + "-tampered");
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, wrong.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, wrong.ReasonCode);
    }

    [Fact]
    public void SlotOccurrenceAndLeaseImmutableFactTamperingFailsClosed()
    {
        using (var slot = ReservationContext.Create())
        {
            slot.Fixture.Execute("UPDATE recurring_occurrence_slots SET scheduled_start_utc = scheduled_start_utc + 1, latest_start_utc = latest_start_utc + 1, planned_end_utc = planned_end_utc + 1 WHERE occurrence_identity = $identity;", ("$identity", slot.Slot.OccurrenceIdentity));
            var result = new RecurringOccurrenceReservationService(slot.Fixture.Store, () => slot.Fixture.CreatedAt)
                .Reserve(slot.Lease.LeaseId, slot.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        }

        using (var occurrence = ReservationContext.Create())
        {
            occurrence.Fixture.Execute("UPDATE plan_occurrences SET window_end_utc = window_end_utc + 1 WHERE id = $id;", ("$id", occurrence.Slot.OccurrenceId));
            var result = new RecurringOccurrenceReservationService(occurrence.Fixture.Store, () => occurrence.Fixture.CreatedAt)
                .Reserve(occurrence.Lease.LeaseId, occurrence.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        }

    }

    [Theory]
    [InlineData("schedule_revision")]
    [InlineData("schedule_digest")]
    [InlineData("time_zone_rules_digest")]
    [InlineData("profile_binding_digest")]
    [InlineData("profile_duration")]
    [InlineData("lease_duration")]
    [InlineData("approval_kind")]
    [InlineData("approval_version")]
    [InlineData("approval_sid")]
    [InlineData("approval_session")]
    [InlineData("approval_digest")]
    [InlineData("approval_fk")]
    public void ExactParentAndApprovalTamperingFailClosed(string tamper)
    {
        using var context = ReservationContext.Create();
        var first = ReserveOnce(context, "run-tamper-" + tamper, "use-tamper-" + tamper);
        var expectedReason = RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid;
        var before = CaptureChainProjection(context, first.RunId!, first.UseId!);

        switch (tamper)
        {
            case "schedule_revision":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET schedule_revision = schedule_revision + 1 WHERE plan_id = $plan AND schedule_revision = $revision;",
                    ("$plan", context.Fixture.PlanId),
                    ("$revision", context.Fixture.Setup.ScheduleVersion.ScheduleRevision));
                break;
            case "schedule_digest":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET schedule_digest = $digest WHERE plan_id = $plan AND schedule_revision = $revision;",
                    ("$digest", TamperHex(context.Fixture.Setup.ScheduleVersion.ScheduleDigest)),
                    ("$plan", context.Fixture.PlanId),
                    ("$revision", context.Fixture.Setup.ScheduleVersion.ScheduleRevision));
                break;
            case "time_zone_rules_digest":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = $plan AND schedule_revision = $revision;",
                    ("$digest", TamperHex(context.Fixture.Setup.ScheduleVersion.TimeZoneRulesDigest)),
                    ("$plan", context.Fixture.PlanId),
                    ("$revision", context.Fixture.Setup.ScheduleVersion.ScheduleRevision));
                break;
            case "profile_binding_digest":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_plan_profile_bindings_immutable_update; PRAGMA foreign_keys = OFF; UPDATE recurring_plan_profile_bindings SET profile_digest = $digest WHERE plan_id = $plan;",
                    ("$digest", TamperHex(context.Fixture.Setup.ExactProfile.Reference.ProfileDigest)),
                    ("$plan", context.Fixture.PlanId));
                break;
            case "profile_duration":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET duration_ms = duration_ms + 1 WHERE profile_id = $profileId AND profile_version = $profileVersion;",
                    ("$profileId", context.Fixture.Setup.ExactProfile.Reference.ProfileId),
                    ("$profileVersion", context.Fixture.Setup.ExactProfile.Reference.ProfileVersion));
                break;
            case "lease_duration":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET per_run_duration_ticks = per_run_duration_ticks + 1, version = version + 1, updated_at_utc = updated_at_utc + 1 WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_kind":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; PRAGMA ignore_check_constraints = ON; UPDATE recurring_lease_local_approvals SET approval_kind_code = 'tampered_kind' WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_version":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; PRAGMA ignore_check_constraints = ON; UPDATE recurring_lease_local_approvals SET approval_version = approval_version + 1 WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_sid":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET current_user_sid = 'S-1-5-21-tampered' WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_session":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET session_binding = 'session-tampered' WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_digest":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET approval_digest = $digest WHERE lease_id = $leaseId;",
                    ("$digest", TamperHex("recurring-lease-local-approval/v1:" + new string('a', 64))),
                    ("$leaseId", context.Lease.LeaseId));
                break;
            case "approval_fk":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_delete; DELETE FROM recurring_lease_local_approvals WHERE lease_id = $leaseId;",
                    ("$leaseId", context.Lease.LeaseId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper));
        }

        var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddMinutes(10), () => "run-new", () => "use-new")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        Assert.Equal(before, CaptureChainProjection(context, first.RunId!, first.UseId!));
    }

    [Fact]
    public void PartialClaimAndAnotherLeaseClaimHaveStableReadOnlyResults()
    {
        using (var missingUse = ReservationContext.Create())
        {
            var first = ReserveOnce(missingUse, "run-missing-use", "use-missing-use");
            missingUse.Fixture.Execute("DROP TRIGGER trg_recurring_lease_uses_immutable_delete; DELETE FROM recurring_lease_uses WHERE use_id = $useId;", ("$useId", first.UseId!));
            var result = new RecurringOccurrenceReservationService(missingUse.Fixture.Store, () => missingUse.Fixture.CreatedAt.AddMinutes(10))
                .Reserve(missingUse.Lease.LeaseId, missingUse.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        }

        using (var missingRun = ReservationContext.Create())
        {
            var first = ReserveOnce(missingRun, "run-missing-run", "use-missing-run");
            missingRun.Fixture.Execute("PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET run_id = 'missing-run' WHERE id = $occurrenceId;", ("$occurrenceId", missingRun.Slot.OccurrenceId!));
            var result = new RecurringOccurrenceReservationService(missingRun.Fixture.Store, () => missingRun.Fixture.CreatedAt.AddMinutes(10))
                .Reserve(missingRun.Lease.LeaseId, missingRun.Slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        }

        using var otherLease = ReservationContext.Create();
        var firstOther = ReserveOnce(otherLease, "run-other-lease", "use-other-lease");
        var secondLease = otherLease.Fixture.CreateLease(
            "task261-other-lease",
            otherLease.Fixture.CreatedAt,
            otherLease.Fixture.CreatedAt.AddHours(-1),
            otherLease.Lease.ValidUntilUtc);
        new SqliteRecurringConsentLeaseRepository(otherLease.Fixture.Store).InsertPending(secondLease);
        otherLease.Fixture.Execute(
            "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET lease_id = $otherLease WHERE use_id = $useId;",
            ("$otherLease", secondLease.LeaseId),
            ("$useId", firstOther.UseId!));
        var conflict = new RecurringOccurrenceReservationService(otherLease.Fixture.Store, () => otherLease.Fixture.CreatedAt.AddMinutes(10))
            .Reserve(otherLease.Lease.LeaseId, otherLease.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Conflict, conflict.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.Conflict, conflict.ReasonCode);
        Assert.Equal(firstOther.UseId, conflict.UseId);
    }

    [Fact]
    public void TenCommittedAccountingRowsProduceTerminalQuotaRejection()
    {
        using var context = ReservationContext.Create(slotCount: 11);
        for (var index = 1; index <= 10; index++)
        {
            var run = new RecordingRun("run-terminal-" + index, context.Slots[index].OccurrenceId!, context.Slots[index].ScheduledStartUtc!.Value);
            new SqliteRecordingRunRepository(context.Fixture.Store).Insert(run);
            context.Fixture.InsertAccountingRow(
                "use-terminal-" + index,
                context.Lease,
                context.Slots[index],
                run.Id,
                "start_committed",
                run.CreatedAtUtc,
                1,
                context.Lease.PerRunDuration,
                null);
        }

        var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt, () => "run-quota", () => "use-quota")
            .Reserve(context.Lease.LeaseId, context.Slots[0].OccurrenceIdentity);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.QuotaExhausted, result.ReasonCode);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses WHERE use_id = 'use-quota';")));
    }

    [Fact]
    public void FourFailurePointsRollbackRunUseAndOccurrenceCas()
    {
        foreach (var point in Enum.GetValues<RecurringOccurrenceReservationFailurePoint>())
        {
            using var context = ReservationContext.Create();
            var specificationDigest = Convert.ToString(context.Fixture.Scalar(
                "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
                ("$identity", context.Slot.OccurrenceIdentity)));
            var result = new RecurringOccurrenceReservationService(
                context.Fixture.Store,
                () => context.Fixture.CreatedAt,
                () => "run-failure-" + point,
                () => "use-failure-" + point,
                failureHookForTest: reached =>
                {
                    if (reached == point)
                    {
                        throw new InvalidOperationException("injected reservation failure");
                    }
                }).Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, result.Status);
            Assert.Equal(RecurringOccurrenceReservationReasonCodes.TransactionFailed, result.ReasonCode);
            Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
            Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
            Assert.Equal(specificationDigest, Convert.ToString(context.Fixture.Scalar(
                "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
                ("$identity", context.Slot.OccurrenceIdentity))));
            Assert.Equal(PlanOccurrenceStatus.Authorized, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
            Assert.Null(new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).RunId);
        }
    }

    [Fact]
    public void SameOccurrenceConcurrentStoresConvergeToOneReservation()
    {
        using var context = ReservationContext.Create();
        var path = context.Fixture.Store.DatabasePath;
        var calls = new[]
        {
            Task.Run(() => new RecurringOccurrenceReservationService(new SqliteOperationalStore(path), () => context.Fixture.CreatedAt, () => "run-concurrent-a", () => "use-concurrent-a").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)),
            Task.Run(() => new RecurringOccurrenceReservationService(new SqliteOperationalStore(path), () => context.Fixture.CreatedAt, () => "run-concurrent-b", () => "use-concurrent-b").Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)),
        };
        var results = Task.WhenAll(calls).GetAwaiter().GetResult();

        Assert.Equal(1, results.Count(result => result.Status == RecurringOccurrenceReservationStatus.Reserved));
        Assert.Equal(1, results.Count(result => result.Status == RecurringOccurrenceReservationStatus.AlreadyReserved));
        var reserved = results.Single(result => result.Status == RecurringOccurrenceReservationStatus.Reserved);
        var replay = results.Single(result => result.Status == RecurringOccurrenceReservationStatus.AlreadyReserved);
        Assert.NotNull(reserved.SpecificationSummary);
        Assert.NotNull(replay.SpecificationSummary);
        Assert.Equal(reserved.SpecificationDigest, replay.SpecificationDigest);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void SameLeaseConcurrentDifferentOccurrencesAllowsAtMostOneRunInFlight()
    {
        using var context = ReservationContext.Create(slotCount: 2);
        var path = context.Fixture.Store.DatabasePath;
        var calls = new[]
        {
            Task.Run(() => new RecurringOccurrenceReservationService(new SqliteOperationalStore(path), () => context.Fixture.CreatedAt, () => "run-different-a", () => "use-different-a").Reserve(context.Lease.LeaseId, context.Slots[0].OccurrenceIdentity)),
            Task.Run(() => new RecurringOccurrenceReservationService(new SqliteOperationalStore(path), () => context.Slots[1].ScheduledStartUtc!.Value, () => "run-different-b", () => "use-different-b").Reserve(context.Lease.LeaseId, context.Slots[1].OccurrenceIdentity)),
        };
        var results = Task.WhenAll(calls).GetAwaiter().GetResult();

        Assert.Equal(1, results.Count(result => result.Status == RecurringOccurrenceReservationStatus.Reserved));
        Assert.Contains(results, result => result.Status == RecurringOccurrenceReservationStatus.Rejected && result.ReasonCode == RecurringLeaseQuotaReasonCodes.RunInFlight);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public void ClockRollbackAndInvalidFactoryIdsAreRejectedBeforeTransaction()
    {
        using var context = ReservationContext.Create();
        var samples = new Queue<DateTimeOffset>(new[] { context.Fixture.CreatedAt.AddSeconds(1), context.Fixture.CreatedAt });
        var service = new RecurringOccurrenceReservationService(context.Fixture.Store, () => samples.Dequeue(), () => "run-clock", () => "use-clock");
        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, service.Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity).Status);
        var second = service.Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.ClockMovedBackwards, second.ReasonCode);

        var badIds = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt, () => " ", () => "use-bad");
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.RequestInvalid, badIds.Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity).ReasonCode);
    }

    [Fact]
    public void ReservationHasNoProofMediaCursorOrOneShotStartGateSideEffects()
    {
        using var context = ReservationContext.Create();
        var before = new[]
        {
            Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM authorized_capture_scopes;")),
            Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_schedule_cursors;")),
            Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM consent_leases;")),
        };

        var result = new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt)
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, result.Status);

        Assert.Equal(before[0], Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM authorized_capture_scopes;")));
        Assert.Equal(before[1], Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_schedule_cursors;")));
        Assert.Equal(before[2], Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM consent_leases;")));
    }

    private static RecurringOccurrenceReservationResult ReserveOnce(
        ReservationContext context,
        string runId,
        string useId) =>
        new RecurringOccurrenceReservationService(context.Fixture.Store, () => context.Fixture.CreatedAt, () => runId, () => useId)
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

    private static void ApplyValidProgressedTuple(
        ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string target)
    {
        var runs = new SqliteRecordingRunRepository(context.Fixture.Store);
        var run = runs.Get(reservation.RunId!);
        var firstTransitionAt = context.Fixture.CreatedAt.AddMinutes(1);
        Assert.True(run.TryTransition(RecordingRunStatus.Preparing, firstTransitionAt).Succeeded);
        runs.Update(run, run.Version - 1);

        run = runs.Get(reservation.RunId!);
        var startCommittedAt = context.Fixture.CreatedAt.AddMinutes(2);
        Assert.True(run.TryTransition(RecordingRunStatus.StartCommitted, startCommittedAt).Succeeded);
        runs.Update(run, run.Version - 1);
        context.Fixture.UpdateAccountingRow(reservation.UseId!, "start_committed", 1, context.Lease.PerRunDuration.Ticks, null, startCommittedAt, 1);
        if (target == "start_committed")
        {
            return;
        }

        run = runs.Get(reservation.RunId!);
        var recordingAt = context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(run.TryTransition(RecordingRunStatus.Recording, recordingAt).Succeeded);
        runs.Update(run, run.Version - 1);
        context.Fixture.UpdateAccountingRow(reservation.UseId!, "consumed", 1, context.Lease.PerRunDuration.Ticks, null, recordingAt, 2);
        if (target == "recording")
        {
            return;
        }

        run = runs.Get(reservation.RunId!);
        var finalizingAt = context.Fixture.CreatedAt.AddMinutes(4);
        Assert.True(run.TryTransition(RecordingRunStatus.Finalizing, finalizingAt).Succeeded);
        runs.Update(run, run.Version - 1);
        if (target == "finalizing")
        {
            return;
        }

        run = runs.Get(reservation.RunId!);
        var mediaReadyAt = context.Fixture.CreatedAt.AddMinutes(5);
        Assert.True(run.TryTransition(RecordingRunStatus.MediaReady, mediaReadyAt).Succeeded);
        runs.Update(run, run.Version - 1);
        Assert.Equal("media_ready", target);
    }

    private static void ApplySettledTuple(
        ReservationContext context,
        RecurringOccurrenceReservationResult reservation)
    {
        ApplyValidProgressedTuple(context, reservation, "media_ready");
        var runs = new SqliteRecordingRunRepository(context.Fixture.Store);
        var settledAt = context.Fixture.CreatedAt.AddMinutes(6);
        var run = runs.Get(reservation.RunId!);
        Assert.True(run.TryTransition(RecordingRunStatus.Settled, settledAt).Succeeded);
        runs.Update(run, run.Version - 1);
        context.Fixture.UpdateAccountingRow(
            reservation.UseId!,
            "settled",
            1,
            context.Lease.PerRunDuration.Ticks,
            TimeSpan.FromMinutes(1).Ticks,
            settledAt,
            3);

        var occurrences = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = occurrences.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Completed, settledAt).Succeeded);
        occurrences.Update(occurrence, occurrence.Version - 1);
    }

    private static void ApplyBlockedTuple(
        ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        RecordingRunStatus terminalStatus)
    {
        var progressedTarget = terminalStatus == RecordingRunStatus.StartedUnknown ? "start_committed" : "recording";
        ApplyValidProgressedTuple(context, reservation, progressedTarget);
        var runs = new SqliteRecordingRunRepository(context.Fixture.Store);
        var terminalAt = context.Fixture.CreatedAt.AddMinutes(7);
        var reason = terminalStatus switch
        {
            RecordingRunStatus.StartedUnknown => "recovery_after_start_commit",
            RecordingRunStatus.SessionInterrupted => "session_interrupted",
            RecordingRunStatus.Failed => "capture_output_invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(terminalStatus)),
        };
        var run = runs.Get(reservation.RunId!);
        Assert.True(run.TryTransition(terminalStatus, terminalAt, reason).Succeeded);
        runs.Update(run, run.Version - 1);
        var useVersion = terminalStatus == RecordingRunStatus.StartedUnknown ? 2 : 3;
        context.Fixture.UpdateAccountingRow(
            reservation.UseId!,
            "started_unknown",
            1,
            context.Lease.PerRunDuration.Ticks,
            null,
            terminalAt,
            useVersion);

        var occurrences = new SqlitePlanOccurrenceRepository(context.Fixture.Store);
        var occurrence = occurrences.Get(context.Slot.OccurrenceId!);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Blocked, terminalAt, reason).Succeeded);
        occurrences.Update(occurrence, occurrence.Version - 1);
    }

    private static void ApplyRawTuple(
        ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        InvalidChainTuple tuple)
    {
        context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; " +
            "UPDATE recording_runs SET status_code = $runStatus, has_crossed_start_commit = $crossed, terminal_reason_code = $runReason, updated_at_utc = $updated, version = $runVersion WHERE id = $runId; " +
            "UPDATE recurring_lease_uses SET status_code = $useStatus, reserved_use_count = 1, reserved_duration_ticks = $duration, actual_settled_duration_ticks = $actual, updated_at_utc = $updated, version = $useVersion WHERE use_id = $useId; " +
            "UPDATE plan_occurrences SET status_code = $occurrenceStatus, run_id = $runId, terminal_reason_code = $occurrenceReason, updated_at_utc = $updated, version = $occurrenceVersion WHERE id = $occurrenceId;",
            ("$runStatus", tuple.RunStatus),
            ("$crossed", tuple.RunCrossed ? 1 : 0),
            ("$runReason", tuple.RunReason),
            ("$updated", context.Fixture.CreatedAt.AddMinutes(10).UtcDateTime.Ticks),
            ("$runVersion", tuple.RunVersion),
            ("$runId", reservation.RunId!),
            ("$useStatus", tuple.UseStatus),
            ("$duration", context.Lease.PerRunDuration.Ticks),
            ("$actual", tuple.UseStatus == "settled" ? TimeSpan.FromMinutes(1).Ticks : null),
            ("$useVersion", tuple.UseVersion),
            ("$useId", reservation.UseId!),
            ("$occurrenceStatus", tuple.OccurrenceStatus),
            ("$occurrenceReason", tuple.OccurrenceReason),
            ("$occurrenceVersion", tuple.OccurrenceVersion),
            ("$occurrenceId", context.Slot.OccurrenceId!));
    }

    private static string CaptureChainProjection(
        ReservationContext context,
        string runId,
        string useId)
    {
        var occurrence = context.Fixture.Scalar(
            "SELECT status_code || '|' || IFNULL(run_id, '') || '|' || IFNULL(terminal_reason_code, '') || '|' || updated_at_utc || '|' || version FROM plan_occurrences WHERE id = $id;",
            ("$id", context.Slot.OccurrenceId!));
        var run = context.Fixture.Scalar(
            "SELECT status_code || '|' || has_crossed_start_commit || '|' || IFNULL(terminal_reason_code, '') || '|' || created_at_utc || '|' || updated_at_utc || '|' || version FROM recording_runs WHERE id = $id;",
            ("$id", runId));
        var use = context.Fixture.Scalar(
            "SELECT status_code || '|' || reserved_use_count || '|' || reserved_duration_ticks || '|' || IFNULL(actual_settled_duration_ticks, '') || '|' || created_at_utc || '|' || updated_at_utc || '|' || version FROM recurring_lease_uses WHERE use_id = $id;",
            ("$id", useId));
        return $"{occurrence}|{run}|{use}";
    }

    private static string TamperHex(string value) =>
        value[..^1] + (value[^1] == '0' ? '1' : '0');

    private sealed record InvalidChainTuple(
        string OccurrenceStatus,
        string RunStatus,
        bool RunCrossed,
        string? RunReason,
        string UseStatus,
        string? OccurrenceReason,
        long OccurrenceVersion,
        long RunVersion,
        long UseVersion);

    internal sealed class ReservationContext : IDisposable
    {
        internal static readonly DateTimeOffset DefaultCreatedAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

        private ReservationContext(RecurringLeaseFixture fixture, RecurringConsentLease lease, IReadOnlyList<RecurringOccurrenceSlotSnapshot> slots)
        {
            Fixture = fixture;
            Lease = lease;
            Slots = slots;
            Slot = slots[0];
        }

        internal RecurringLeaseFixture Fixture { get; }
        internal RecurringConsentLease Lease { get; }
        internal IReadOnlyList<RecurringOccurrenceSlotSnapshot> Slots { get; }
        internal RecurringOccurrenceSlotSnapshot Slot { get; }

        internal static ReservationContext Create(
            int slotCount = 1,
            string targetStatus = "authorized",
            DateTimeOffset? validFrom = null,
            DateTimeOffset? validUntil = null,
            long maxUses = 10,
            TimeSpan? maxCumulativeDuration = null,
            bool disableSafetyBeforeReservation = false,
            bool stopAllBeforeReservation = false,
            string? approvalUserSid = null,
            string? approvalSessionBinding = null,
            TimeSpan? latestStartGrace = null,
            int countdownSeconds = 3)
        {
            var fixture = RecurringLeaseFixture.Create(
                maxOccurrences: Math.Max(3, slotCount + 1),
                latestStartGrace: latestStartGrace ?? TimeSpan.Zero,
                countdownSeconds: countdownSeconds);
            var slots = MaterializeSlots(fixture, slotCount);
            var shouldCreateSpecification = targetStatus == "authorized" &&
                (validFrom is null || validFrom.Value <= slots[0].ScheduledStartUtc!.Value) &&
                (validUntil is null || validUntil.Value > slots[0].ScheduledStartUtc!.Value);
            var authorizedLatestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
                fixture.PlanId,
                fixture.Setup.ScheduleVersion.ScheduleRevision,
                fixture.Schedule);
            var leaseValidUntil = validUntil ?? authorizedLatestEnd.AddHours(2);
            var lease = RecurringConsentLease.CreatePending(
                "task261-lease",
                fixture.Setup.ConfigurationRef,
                validFrom ?? fixture.CreatedAt.AddHours(-1),
                leaseValidUntil,
                authorizedLatestEnd,
                fixture.Setup.ExactProfile.Duration,
                maxUses: maxUses,
                maxCumulativeDuration: maxCumulativeDuration ?? TimeSpan.FromTicks(checked(fixture.Setup.ExactProfile.Duration.Ticks * maxUses)),
                fixture.CreatedAt.AddHours(-1));
            new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
            var safety = new SqliteStandingLeaseSafetyControlTransaction(fixture.Store);
            safety.SetUnattendedMode("task261-enable", true, "task261-test", fixture.CreatedAt.AddMinutes(-1));
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                "task261-approval",
                lease.LeaseId,
                lease.PlanId,
                lease.ConfigurationRef.ConfigurationDigest,
                lease.AuthorizationDigest,
                approvalUserSid ?? "S-1-5-21-task261",
                approvalSessionBinding ?? "session-task261",
                fixture.CreatedAt);
            var activation = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt)
                .Activate(lease.LeaseId, receipt);
            Assert.True(activation.Status == RecurringLeaseLocalApprovalActivationStatus.Activated, activation.Reason);
            foreach (var slot in slots)
            {
                AdvanceOccurrenceTo(
                    fixture,
                    slot,
                    targetStatus,
                    directAuthorize: targetStatus == "authorized" && !shouldCreateSpecification);
            }

            if (shouldCreateSpecification)
            {
                foreach (var slot in slots)
                {
                    var recheck = new RecurringOccurrenceEnvironmentRecheckService(
                        fixture.Store,
                        () => slot.ScheduledStartUtc!.Value,
                        new MatchingReservationEnvironmentProvider())
                        .Recheck(lease.LeaseId, slot.OccurrenceIdentity);
                    Assert.True(
                        recheck.Status == RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized,
                        $"recheck={recheck.Status}; reason={recheck.ReasonCode}");
                    Assert.NotNull(recheck.SpecificationSummary);
                }
            }

            if (stopAllBeforeReservation)
            {
                fixture.Execute("UPDATE unattended_safety_state SET stop_all_applied = 1, stop_all_operation_id = 'task261-stop-all', stop_all_reason_code = 'task261-test', stop_all_requested_at_utc = $at, stop_all_applied_at_utc = $at, version = version + 1 WHERE state_id = 'global';", ("$at", fixture.CreatedAt.UtcDateTime.Ticks));
            }
            if (disableSafetyBeforeReservation)
            {
                safety.SetUnattendedMode("task261-disable", false, "task261-test", fixture.CreatedAt.AddTicks(1));
            }

            return new ReservationContext(fixture, lease, slots);
        }

        public void Dispose() => Fixture.Dispose();

        private static IReadOnlyList<RecurringOccurrenceSlotSnapshot> MaterializeSlots(RecurringLeaseFixture fixture, int count)
        {
            var calculator = new RecurringOccurrenceCalculator();
            var materializer = new SqliteRecurringOccurrenceMaterializationTransaction(fixture.Store);
            var slots = new List<RecurringOccurrenceSlotSnapshot>();
            DateTimeOffset? after = null;
            for (var index = 0; index < count; index++)
            {
                var candidate = calculator.CalculateNext(fixture.PlanId, 1, fixture.Schedule, after).Candidate
                    ?? throw new InvalidOperationException("The test schedule did not produce a candidate.");
                slots.Add(materializer.Materialize(candidate, fixture.CreatedAt));
                after = candidate.PlannedEndUtc;
            }

            return slots;
        }

        private static void AdvanceOccurrenceTo(
            RecurringLeaseFixture fixture,
            RecurringOccurrenceSlotSnapshot slot,
            string targetStatus,
            bool directAuthorize = false)
        {
            if (targetStatus == "scheduled")
            {
                return;
            }

            var observedAt = slot.ScheduledStartUtc!.Value;
            var due = new SqlitePeriodicOccurrenceDueProjectionTransaction(fixture.Store)
                .Project(slot.OccurrenceIdentity, 0, observedAt);
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
            if (targetStatus == "due" || targetStatus == "authorized" && !directAuthorize)
            {
                return;
            }

            var repository = new SqlitePlanOccurrenceRepository(fixture.Store);
            var occurrence = repository.Get(slot.OccurrenceId!);
            Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, observedAt).Succeeded);
            repository.Update(occurrence, occurrence.Version - 1);
            if (targetStatus == "rechecking")
            {
                return;
            }

            occurrence = repository.Get(slot.OccurrenceId!);
            if (targetStatus == "pending_confirmation")
            {
                Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.PendingConfirmation, observedAt).Succeeded);
                repository.Update(occurrence, occurrence.Version - 1);
                return;
            }

            Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.PendingLeaseApproval, observedAt).Succeeded);
            repository.Update(occurrence, occurrence.Version - 1);
            if (targetStatus == "pending_lease_approval")
            {
                return;
            }

            Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Authorized, observedAt).Succeeded);
            repository.Update(occurrence, occurrence.Version - 1);
        }
    }

    internal sealed class MatchingReservationEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
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
                        "task264-display",
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
}
