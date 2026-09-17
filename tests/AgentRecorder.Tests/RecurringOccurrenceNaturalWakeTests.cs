using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceNaturalWakeTests
{
    private const string Sid = "S-1-5-21-natural-wake";
    private static readonly string Session = CaptureAuthorizationSessionBinding.Current;

    [Fact]
    public void CandidateQueryDiscoversScheduledDueAuthorizedAndRunCreatedFromExactDurableParents()
    {
        using var scheduled = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var scheduledPage = Query(scheduled);
        Assert.Single(scheduledPage.Candidates);
        AssertCandidate(scheduledPage.Candidates[0], scheduled, "scheduled");
        Assert.Equal(scheduled.Lease.LeaseId, scheduledPage.Candidates[0].LeaseId);
        Assert.Equal(0L, Count(scheduled.Fixture, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));
        Assert.Equal(0L, Count(scheduled.Fixture, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Count(scheduled.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));

        using var due = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "due",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var duePage = Query(due);
        Assert.Single(duePage.Candidates);
        AssertCandidate(duePage.Candidates[0], due, "due");

        using var authorized = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var authorizedPage = Query(authorized);
        Assert.Single(authorizedPage.Candidates);
        AssertCandidate(authorizedPage.Candidates[0], authorized, "authorized");
        Assert.Equal(1L, Count(authorized.Fixture, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));

        var reservation = new RecurringOccurrenceReservationService(
            authorized.Fixture.Store,
            () => authorized.Slot.ScheduledStartUtc!.Value,
            () => "natural-wake-run-created-run",
            () => "natural-wake-run-created-use")
            .Reserve(authorized.Lease.LeaseId, authorized.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);

        var runCreatedPage = Query(authorized);
        Assert.Single(runCreatedPage.Candidates);
        AssertCandidate(runCreatedPage.Candidates[0], authorized, "run_created");
        Assert.Equal(reservation.RunId, ReadText(authorized.Fixture, "SELECT id FROM recording_runs LIMIT 1;"));
        Assert.Equal("reserved", ReadText(authorized.Fixture, "SELECT status_code FROM recurring_lease_uses LIMIT 1;"));
    }

    [Fact]
    public void CandidateQueryExcludesFutureAndPausedButReturnsCancelledScheduledOccurrence()
    {
        using var future = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        Assert.Empty(Query(future, future.Slot.ScheduledStartUtc!.Value.AddTicks(-1)).Candidates);

        using var paused = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        TransitionPlan(paused, PlanDefinitionStatus.Paused);
        Assert.Empty(Query(paused).Candidates);

        using var cancelled = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        TransitionPlan(cancelled, PlanDefinitionStatus.Cancelled);
        var page = Query(cancelled);
        Assert.Single(page.Candidates);
        Assert.Equal("scheduled", page.Candidates[0].ObservedOccurrenceStatusCode);
    }

    [Fact]
    public void CandidateQueryExcludesPostStartAndAllTerminalOccurrenceShapes()
    {
        using var startCommitted = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var reservation = Reserve(startCommitted);
        var commit = new RecurringOccurrenceStartCommitService(
            startCommitted.Fixture.Store,
            () => startCommitted.Slot.ScheduledStartUtc!.Value,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(startCommitted.Lease.LeaseId, startCommitted.Slot.OccurrenceIdentity);
        Assert.True(commit.Succeeded, $"status={commit.Status}; reason={commit.ReasonCode}");
        Assert.Empty(Query(startCommitted).Candidates);
        Assert.Equal("start_committed", ReadText(startCommitted.Fixture, "SELECT status_code FROM recurring_lease_uses LIMIT 1;"));
        Assert.Equal(reservation.RunId, commit.RunId);

        using var terminal = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var terminalOccurrence = new SqlitePlanOccurrenceRepository(terminal.Fixture.Store)
            .Get(terminal.Slot.OccurrenceId!);
        Assert.True(terminalOccurrence.TryTransition(
            PlanOccurrenceStatus.Blocked,
            terminal.Slot.ScheduledStartUtc!.Value,
            "natural_wake_test_terminal").Succeeded);
        new SqlitePlanOccurrenceRepository(terminal.Fixture.Store)
            .Update(terminalOccurrence, terminalOccurrence.Version - 1);
        Assert.Empty(Query(terminal).Candidates);
    }

    [Fact]
    public void CandidateQueryAppliesLimitAfterTwoStartCommittedChainsAndReturnsLaterScheduled()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 3,
            targetStatus: "scheduled",
            maxUses: 10,
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);

        AdvanceToPostStart(context, 0, PostStartShape.StartCommitted);
        AdvanceToPostStart(context, 1, PostStartShape.StartCommitted);

        var page = Query(context, context.Slots[2].ScheduledStartUtc!.Value, limit: 1);

        Assert.Single(page.Candidates);
        Assert.Equal(context.Slots[2].OccurrenceIdentity, page.Candidates[0].Slot.OccurrenceIdentity);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void CandidateQueryKeepsStablePreStartOrderAndHasMoreBehindMixedPostStartChains()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 8,
            targetStatus: "scheduled",
            maxUses: 20,
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);

        AdvanceToPostStart(context, 0, PostStartShape.StartCommitted);
        AdvanceToPostStart(context, 1, PostStartShape.Recording);
        AdvanceToPostStart(context, 2, PostStartShape.Finalizing);
        AdvanceToPostStart(context, 3, PostStartShape.MediaReady);
        AdvanceToPostStart(context, 4, PostStartShape.ReconciledCompleted);

        var firstPage = Query(context, context.Slots[6].ScheduledStartUtc!.Value, limit: 1);
        Assert.Single(firstPage.Candidates);
        Assert.True(firstPage.HasMore);
        Assert.Equal(context.Slots[5].OccurrenceIdentity, firstPage.Candidates[0].Slot.OccurrenceIdentity);

        var secondPage = Query(context, context.Slots[6].ScheduledStartUtc!.Value, limit: 2);
        Assert.False(
            secondPage.HasMore,
            $"candidates={string.Join('|', secondPage.Candidates.Select(candidate => candidate.Slot.OccurrenceIdentity))}");
        Assert.Equal(
            new[] { context.Slots[5].OccurrenceIdentity, context.Slots[6].OccurrenceIdentity },
            secondPage.Candidates.Select(candidate => candidate.Slot.OccurrenceIdentity));
    }

    [Fact]
    public void CandidateQueryFindsPreStartBehindMoreThanOneHundredMixedPostStartAndReconciledChains()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 102,
            targetStatus: "authorized",
            maxUses: 200,
            maxCumulativeDuration: TimeSpan.FromMinutes(400),
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);

        for (var index = 0; index < 101; index++)
        {
            AdvanceToPostStart(
                context,
                index,
                (index % 5) switch
                {
                    0 => PostStartShape.StartCommitted,
                    1 => PostStartShape.Recording,
                    2 => PostStartShape.Finalizing,
                    3 => PostStartShape.MediaReady,
                    _ => PostStartShape.ReconciledCompleted,
                });
        }

        var before = CaptureExecutionChainState(context);
        var page = Query(context, context.Slots[101].ScheduledStartUtc!.Value, limit: 1);
        var after = CaptureExecutionChainState(context);

        Assert.Single(page.Candidates);
        Assert.Equal(context.Slots[101].OccurrenceIdentity, page.Candidates[0].Slot.OccurrenceIdentity);
        Assert.False(page.HasMore);
        Assert.Equal(before, after);
        Assert.Equal(101L, before.RunCount);
        Assert.Equal(101L, before.UseCount);
    }

    [Fact]
    public void CandidateQueryFailsClosedForIllegalRunUseStatusCombination()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, Reserve(context).Status);
        context.Fixture.Execute(
            "UPDATE recording_runs SET status_code = 'recording', has_crossed_start_commit = 1, version = 3 WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slot.OccurrenceId!));

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(context));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
        Assert.Contains("malformed run/use", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenRunCreatedAtDiffersFromUseCreatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE recording_runs SET created_at_utc = $at + 1, updated_at_utc = $at + 1 WHERE occurrence_id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenOccurrenceCreatedAtIsAfterOccurrenceUpdatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE plan_occurrences SET created_at_utc = $at + 2, updated_at_utc = $at + 1 WHERE id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenOccurrenceCreatedAtIsAfterRunCreatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE plan_occurrences SET created_at_utc = $at + 1, updated_at_utc = $at + 1 WHERE id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenRunCreatedAtIsAfterRunUpdatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE recording_runs SET created_at_utc = $at + 1 WHERE occurrence_id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenUseCreatedAtIsAfterUseUpdatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET created_at_utc = $at + 1 WHERE occurrence_id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenOccurrenceUpdatedAtIsAfterRunUpdatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE plan_occurrences SET updated_at_utc = $at + 1 WHERE id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenUseUpdatedAtIsAfterRunUpdatedAt()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET updated_at_utc = $at + 1 WHERE occurrence_id = $occurrence_id;",
            ("$at", context.Slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenReservedAndPerRunDurationAreZero()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_consent_leases SET per_run_duration_ticks = 0 WHERE lease_id = $lease_id; UPDATE recurring_lease_uses SET reserved_duration_ticks = 0 WHERE occurrence_id = $occurrence_id;",
            ("$lease_id", context.Lease.LeaseId),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenReservedAndPerRunDurationAreNegative()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_consent_leases SET per_run_duration_ticks = -1 WHERE lease_id = $lease_id; UPDATE recurring_lease_uses SET reserved_duration_ticks = -1 WHERE occurrence_id = $occurrence_id;",
            ("$lease_id", context.Lease.LeaseId),
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenReservedDurationDiffersFromPerRunDuration()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET reserved_duration_ticks = reserved_duration_ticks + 1 WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenPostStartSpecificationEvaluatedAfterOccurrenceUpdatedAt()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 2,
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        AdvanceToPostStart(context, 0, PostStartShape.Recording);

        var evaluatedAtUtc = Convert.ToInt64(context.Fixture.Scalar(
            "SELECT evaluated_at_utc FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slots[0].OccurrenceIdentity)));
        var runUpdatedAtUtc = Convert.ToInt64(context.Fixture.Scalar(
            "SELECT updated_at_utc FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[0].OccurrenceId!)));
        var occurrenceCreatedAtUtc = evaluatedAtUtc - 2;
        var occurrenceUpdatedAtUtc = evaluatedAtUtc - 1;
        Assert.True(occurrenceCreatedAtUtc <= occurrenceUpdatedAtUtc);
        Assert.True(occurrenceUpdatedAtUtc <= runUpdatedAtUtc);
        Assert.True(evaluatedAtUtc > occurrenceUpdatedAtUtc);

        context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE plan_occurrences SET created_at_utc = $created_at_utc, updated_at_utc = $updated_at_utc WHERE id = $occurrence_id;",
            ("$created_at_utc", occurrenceCreatedAtUtc),
            ("$updated_at_utc", occurrenceUpdatedAtUtc),
            ("$occurrence_id", context.Slots[0].OccurrenceId!));

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(
            context,
            context.Slots[1].ScheduledStartUtc!.Value,
            limit: 1));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
        var laterOccurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slots[1].OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, laterOccurrence.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenPostStartSpecificationSelfDigestMismatches()
    {
        AssertMalformedPostStartMutation(TamperSpecificationDigest);
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenPostStartSpecificationStableFingerprintHasSelfDigestMismatch()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET stable_display_fingerprint = 'tampered-fingerprint' WHERE occurrence_identity = $identity;",
            ("$identity", context.Slots[0].OccurrenceIdentity)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenPostStartSpecificationCanonicalCountdownRangeIsInvalid()
    {
        AssertMalformedPostStartMutation(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET countdown_seconds = 11 WHERE occurrence_identity = $identity;",
            ("$identity", context.Slots[0].OccurrenceIdentity)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButScheduleSlotParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; UPDATE recurring_occurrence_slots SET scheduled_start_utc = scheduled_start_utc + 1 WHERE occurrence_identity = $identity;",
            ("$identity", context.Slots[0].OccurrenceIdentity)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButProfileGeometryTopologyParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET region_x = region_x + 1, topology_digest = $topology WHERE profile_id = $profile_id;",
            ("$topology", new string('d', 64)),
            ("$profile_id", context.Fixture.Setup.ExactProfile.Reference.ProfileId)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButProfileDurationAndCountdownParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET duration_ms = duration_ms + 1, countdown_seconds = countdown_seconds + 1 WHERE profile_id = $profile_id;",
            ("$profile_id", context.Fixture.Setup.ExactProfile.Reference.ProfileId)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButOutputParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET output_directory = output_directory || 'tampered' WHERE profile_id = $profile_id;",
            ("$profile_id", context.Fixture.Setup.ExactProfile.Reference.ProfileId)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButApprovalSessionParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET session_binding = 'tampered-session' WHERE lease_id = $lease_id;",
            ("$lease_id", context.Lease.LeaseId)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenValidDigestButLeaseAuthorizationParentMismatchExists()
    {
        AssertValidDigestButParentMismatch(context => context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; PRAGMA ignore_check_constraints = ON; DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET authorization_digest = $authorization_digest WHERE lease_id = $lease_id;",
            ("$authorization_digest", "recurring-consent-lease-authorization/v1:" + new string('e', 64)),
            ("$lease_id", context.Lease.LeaseId)));
    }

    [Fact]
    public void CandidateQueryFailsClosedWhenAuthorizedSpecificationIsMissing()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_delete; DELETE FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity));

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(context));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateQueryRejectsIdentityMismatchAndLeavesDurableStateUntouched()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var before = CaptureDurableState(context);

        Assert.Empty(Query(context, sid: "S-1-5-21-other").Candidates);
        Assert.Empty(Query(context, session: "session-other").Candidates);

        var after = CaptureDurableState(context);
        Assert.Equal(before, after);
    }

    [Fact]
    public void CandidateQueryEnforcesLimitStableOrderingHasMoreAndDoesNotStarveBehindTerminalHistory()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 3,
            targetStatus: "scheduled",
            maxUses: 10,
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var firstPage = Query(context, context.Slots[2].ScheduledStartUtc!.Value, limit: 1);
        Assert.Single(firstPage.Candidates);
        Assert.True(firstPage.HasMore);
        Assert.Equal(context.Slots[0].OccurrenceIdentity, firstPage.Candidates[0].Slot.OccurrenceIdentity);

        var all = Query(context, context.Slots[2].ScheduledStartUtc!.Value, limit: 3);
        Assert.False(all.HasMore);
        Assert.Equal(
            context.Slots.Select(slot => slot.OccurrenceIdentity),
            all.Candidates.Select(candidate => candidate.Slot.OccurrenceIdentity));

        Assert.Throws<Phase3PersistenceException>(() => Query(context, limit: 0));
        Assert.Throws<Phase3PersistenceException>(() => Query(context, limit: 101));

        using var history = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 102,
            targetStatus: "scheduled",
            maxUses: 200,
            maxCumulativeDuration: TimeSpan.FromMinutes(400),
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var occurrenceRepository = new SqlitePlanOccurrenceRepository(history.Fixture.Store);
        for (var index = 0; index < 101; index++)
        {
            var occurrence = occurrenceRepository.Get(history.Slots[index].OccurrenceId!);
            Assert.True(occurrence.TryTransition(
                PlanOccurrenceStatus.Blocked,
                history.Slots[index].ScheduledStartUtc!.Value,
                "natural_wake_history_terminal").Succeeded);
            occurrenceRepository.Update(occurrence, occurrence.Version - 1);
        }

        var targetPage = Query(history, history.Slots[101].ScheduledStartUtc!.Value, limit: 1);
        Assert.Single(targetPage.Candidates);
        Assert.Equal(history.Slots[101].OccurrenceIdentity, targetPage.Candidates[0].Slot.OccurrenceIdentity);
        Assert.False(targetPage.HasMore);
    }

    [Fact]
    public void CandidateQueryFailsClosedForSpecificationBindingMismatchAndChildCardinality()
    {
        using var mismatch = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        TamperSpecificationDigest(mismatch);
        var mismatchException = Assert.Throws<Phase3PersistenceException>(() => Query(mismatch));
        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, mismatchException.Code);

        using var childMismatch = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        new SqliteRecordingRunRepository(childMismatch.Fixture.Store)
            .Insert(new RecordingRun(
                "natural-wake-extra-run",
                childMismatch.Slot.OccurrenceId!,
                childMismatch.Fixture.CreatedAt));
        childMismatch.Fixture.Execute(
            "UPDATE plan_occurrences SET run_id = $run_id WHERE id = $id;",
            ("$run_id", "natural-wake-extra-run"),
            ("$id", childMismatch.Slot.OccurrenceId!));
        var childException = Assert.Throws<Phase3PersistenceException>(() => Query(childMismatch));
        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, childException.Code);
    }

    [Theory]
    [InlineData("profile_digest", "recurring-fixed-region-profile/v1:")]
    [InlineData("configuration_digest", "recurring-plan-configuration/v1:")]
    [InlineData("schedule_digest", "recurring-schedule/v1:")]
    [InlineData("schedule_revision", "")]
    [InlineData("local_approval_digest", "recurring-lease-local-approval/v1:")]
    [InlineData("lease_authorization_digest", "recurring-consent-lease-authorization/v1:")]
    [InlineData("occurrence_identity", "recurring-occurrence/v1:")]
    [InlineData("occurrence_id", "occurrence-mismatch-")]
    public void CandidateQueryFailsClosedForEachSpecificationParentOrIdentityBindingField(
        string field,
        string valuePrefix)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        TamperSpecificationField(context, field, valuePrefix + (field == "schedule_revision" ? "2" : new string('c', 64)));

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(context));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("missed")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public void CandidateQueryExcludesEachNamedTerminalOccurrenceState(string statusCode)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        context.Fixture.Execute(
            "UPDATE plan_occurrences SET status_code = $status_code, terminal_reason_code = CASE WHEN $status_code = 'completed' THEN NULL ELSE 'natural_wake_terminal_test' END, version = version + 1 WHERE id = $occurrence_id;",
            ("$status_code", statusCode),
            ("$occurrence_id", context.Slot.OccurrenceId!));

        Assert.Empty(Query(context).Candidates);
    }

    [Fact]
    public void CandidateQueryExcludesSessionInterruptedReconciledChain()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, Reserve(context).Status);
        context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recording_runs SET status_code = 'session_interrupted', has_crossed_start_commit = 1, terminal_reason_code = 'session_interrupted', version = 4 WHERE occurrence_id = $occurrence_id; UPDATE recurring_lease_uses SET status_code = 'started_unknown', version = 3 WHERE occurrence_id = $occurrence_id; UPDATE plan_occurrences SET status_code = 'blocked', terminal_reason_code = 'session_interrupted', version = 5 WHERE id = $occurrence_id;",
            ("$occurrence_id", context.Slot.OccurrenceId!));

        Assert.Empty(Query(context).Candidates);
    }

    [Fact]
    public async Task BoundAuthorizedChainSurvivesLeaseRevocationAndCoordinatorBlocksWithoutStart()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);
        RevokeLease(context, "natural-wake-revoke-authorized");

        var rediscovered = Assert.Single(Query(context).Candidates);
        Assert.Equal(candidate.LeaseId, rediscovered.LeaseId);
        var backend = new NaturalWakeBackend();
        var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                CreateCoordinator(context, backend).ExecuteAsync)
            .DispatchAsync(rediscovered);

        Assert.NotEqual(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Contains("revoked", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundRunCreatedChainSurvivesLeaseRevocationWithoutSecondClaimOrStart()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var reservation = Reserve(context);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        var candidate = Assert.Single(Query(context).Candidates);
        RevokeLease(context, "natural-wake-revoke-run-created");

        var rediscovered = Assert.Single(Query(context).Candidates);
        var backend = new NaturalWakeBackend();
        var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                CreateCoordinator(context, backend).ExecuteAsync)
            .DispatchAsync(rediscovered);

        Assert.NotEqual(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));
        Assert.Equal("failed", ReadText(context.Fixture, "SELECT status_code FROM recording_runs;"));
        Assert.Equal("available", ReadText(context.Fixture, "SELECT status_code FROM recurring_lease_uses;"));
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Contains("revoked", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundAuthorizedChainAtValidityBoundaryIsDiscoveredAndCoordinatorMissesWithoutStart()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);

        var boundaryCandidate = Assert.Single(Query(context, context.Lease.ValidUntilUtc).Candidates);
        Assert.Equal(candidate.Slot.OccurrenceIdentity, boundaryCandidate.Slot.OccurrenceIdentity);
        var backend = new NaturalWakeBackend();
        var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                CreateCoordinator(context, backend, nowUtc: context.Lease.ValidUntilUtc).ExecuteAsync)
            .DispatchAsync(boundaryCandidate);

        Assert.NotEqual(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(0, backend.StartCalls);
        Assert.Contains(
            result.Status,
            new[]
            {
                RecurringOccurrenceExecutionStatus.Blocked,
                RecurringOccurrenceExecutionStatus.Missed,
                RecurringOccurrenceExecutionStatus.AlreadyTerminal,
            });
    }

    [Fact]
    public async Task DispatcherUsesRealQueryAndCoordinatorOnceAndReturnsExactLifecycleOwner()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);
        var backend = new NaturalWakeBackend();
        var stages = new List<RecurringOccurrenceExecutionStage>();
        var environment = new CountingEnvironmentProvider();
        var recoveryCalls = 0;
        var auditEvents = new List<(string Name, string Json)>();
        var coordinator = CreateCoordinator(
            context,
            backend,
            environmentProvider: environment,
            stages: stages,
            recoveryCalls: () => recoveryCalls++);
        var dispatcher = new RecurringOccurrenceNaturalWakeDispatcher(
            coordinator.ExecuteAsync,
            (name, payload) => auditEvents.Add((name, JsonSerializer.Serialize(payload))));

        var result = await dispatcher.DispatchAsync(candidate);

        Assert.True(
            result.Status == RecurringOccurrenceExecutionStatus.Started,
            $"status={result.Status}; reason={result.Reason}");
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(4, environment.CaptureCalls);
        Assert.Equal(0, recoveryCalls);
        Assert.Equal(
            new[]
            {
                RecurringOccurrenceExecutionStage.AfterDueProjection,
                RecurringOccurrenceExecutionStage.AfterReservation,
                RecurringOccurrenceExecutionStage.AfterStartCommit,
                RecurringOccurrenceExecutionStage.AfterAuthorization,
                RecurringOccurrenceExecutionStage.AfterTicket,
                RecurringOccurrenceExecutionStage.BeforeBridge,
            },
            stages);
        Assert.Single(auditEvents);
        Assert.DoesNotContain("proof", auditEvents[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce", auditEvents[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("specification", auditEvents[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output", auditEvents[0].Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", auditEvents[0].Json, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(Query(context).Candidates);
        result.LifecycleSession!.Dispose();
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task DispatcherReentersDueAuthorizedAndRunCreatedWithoutDuplicateClaims()
    {
        foreach (var targetStatus in new[] { "due", "authorized", "run_created" })
        {
            using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
                targetStatus: targetStatus == "run_created" ? "authorized" : targetStatus,
                countdownSeconds: 0,
                approvalUserSid: Sid,
                approvalSessionBinding: Session);
            if (targetStatus == "run_created")
                Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, Reserve(context).Status);

            var candidate = Assert.Single(Query(context).Candidates);
            var backend = new NaturalWakeBackend();
            var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                    CreateCoordinator(context, backend).ExecuteAsync)
                .DispatchAsync(candidate);

            Assert.True(
                result.Status == RecurringOccurrenceExecutionStatus.Started,
                $"target={targetStatus}; status={result.Status}; reason={result.Reason}");
            Assert.Equal(1, backend.StartCalls);
            Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));
            Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));
            result.LifecycleSession!.Dispose();
        }
    }

    [Fact]
    public async Task CancelledPlanCandidateConvergesToCancelledWithoutBackendStart()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);
        TransitionPlan(context, PlanDefinitionStatus.Cancelled);
        var backend = new NaturalWakeBackend();

        var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                CreateCoordinator(context, backend).ExecuteAsync)
            .DispatchAsync(candidate);

        Assert.Equal(RecurringOccurrenceExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(PlanOccurrenceStatus.Cancelled,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task DispatcherPreCancellationExceptionAndNonStartedResultsNeverRetryOrLeakOwner()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);

        var calls = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var preCancelled = await new RecurringOccurrenceNaturalWakeDispatcher(
                (request, token) =>
                {
                    calls++;
                    return Task.FromResult(RecurringOccurrenceExecutionResult.Rejected("should_not_call"));
                })
            .DispatchAsync(candidate, cancelled.Token);
        Assert.Equal(RecurringOccurrenceExecutionStatus.Cancelled, preCancelled.Status);
        Assert.Equal(0, calls);

        var exceptionResult = await new RecurringOccurrenceNaturalWakeDispatcher(
                (request, token) =>
                {
                    calls++;
                    throw new InvalidOperationException("natural-wake-test");
                })
            .DispatchAsync(candidate);
        Assert.Equal(RecurringOccurrenceExecutionStatus.Rejected, exceptionResult.Status);
        Assert.Equal(1, calls);
        Assert.Null(exceptionResult.LifecycleSession);

        foreach (var status in new[]
                 {
                     RecurringOccurrenceExecutionStatus.Conflict,
                     RecurringOccurrenceExecutionStatus.Rejected,
                     RecurringOccurrenceExecutionStatus.AlreadyClaimed,
                     RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                 })
        {
            var localCalls = 0;
            var invalidOwner = status == RecurringOccurrenceExecutionStatus.Conflict
                ? new FakeLifecycleSession()
                : null;
            var requestResult = await new RecurringOccurrenceNaturalWakeDispatcher(
                    (request, token) =>
                    {
                        localCalls++;
                        return Task.FromResult(
                            RecurringOccurrenceExecutionResult.ForRequest(
                                    request.LeaseId,
                                    request.Candidate!.OccurrenceIdentity)
                                .WithVerifiedPlanId(request.Candidate.PlanId)
                                .WithStatus(status, "natural_wake_test_result", lifecycleSession: invalidOwner));
                    })
                .DispatchAsync(candidate);
            Assert.Equal(
                invalidOwner is null ? status : RecurringOccurrenceExecutionStatus.Rejected,
                requestResult.Status);
            Assert.Equal(1, localCalls);
            Assert.Null(requestResult.LifecycleSession);
            if (invalidOwner is not null)
            {
                Assert.Equal("natural_wake_non_started_owner_present", requestResult.Reason);
                Assert.Equal(1, invalidOwner.DisposeCalls);
            }
        }
    }

    [Fact]
    public async Task TwoDispatchersUseOneSqliteStartWinnerAndLoserDoesNotRecoverWinner()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);
        var path = context.Fixture.Store.DatabasePath;
        var firstBackend = new NaturalWakeBackend();
        var secondBackend = new NaturalWakeBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var firstRecoveryCalls = 0;
        var secondRecoveryCalls = 0;
        var first = new RecurringOccurrenceNaturalWakeDispatcher(
            CreateCoordinator(
                context,
                firstBackend,
                new SqliteOperationalStore(path),
                interlock,
                recoveryCalls: () => firstRecoveryCalls++).ExecuteAsync);
        var second = new RecurringOccurrenceNaturalWakeDispatcher(
            CreateCoordinator(
                context,
                secondBackend,
                new SqliteOperationalStore(path),
                interlock,
                recoveryCalls: () => secondRecoveryCalls++).ExecuteAsync);

        var results = await Task.WhenAll(first.DispatchAsync(candidate), second.DispatchAsync(candidate));

        Assert.Single(results, result => result.Status == RecurringOccurrenceExecutionStatus.Started);
        Assert.Single(results, result => result.Status is RecurringOccurrenceExecutionStatus.AlreadyClaimed or RecurringOccurrenceExecutionStatus.AlreadyAdvanced);
        Assert.Equal(1, firstBackend.StartCalls + secondBackend.StartCalls);
        Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));
        var winner = results.Single(result => result.Status == RecurringOccurrenceExecutionStatus.Started);
        var loser = results.Single(result => result.Status != RecurringOccurrenceExecutionStatus.Started);
        Assert.Null(loser.LifecycleSession);
        Assert.True(winner.LifecycleSession is not null);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(winner.RunId!).Status);
        Assert.Equal("start_committed",
            ReadText(context.Fixture, "SELECT status_code FROM recurring_lease_uses WHERE occurrence_identity = '" + context.Slot.OccurrenceIdentity + "';"));
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(1, firstBackend.StartCalls + secondBackend.StartCalls);
        Assert.Equal(0, firstRecoveryCalls + secondRecoveryCalls);
        winner.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task DispatcherAuditFailureDoesNotChangeDurableResultOrOwnership()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        var candidate = Assert.Single(Query(context).Candidates);
        var backend = new NaturalWakeBackend();
        var result = await new RecurringOccurrenceNaturalWakeDispatcher(
                CreateCoordinator(context, backend).ExecuteAsync,
                (_, _) => throw new InvalidOperationException("audit unavailable"))
            .DispatchAsync(candidate);

        Assert.True(
            result.Status == RecurringOccurrenceExecutionStatus.Started,
            $"status={result.Status}; reason={result.Reason}");
        Assert.Equal(1, backend.StartCalls);
        Assert.NotNull(result.LifecycleSession);
        result.LifecycleSession!.Dispose();
    }

    private static RecurringOccurrenceNaturalWakeCandidatePage Query(
        RecurringOccurrenceReservationTests.ReservationContext context,
        DateTimeOffset? observedAtUtc = null,
        string? sid = null,
        string? session = null,
        int limit = 100) =>
        new RecurringOccurrenceNaturalWakeCandidateQuery(context.Fixture.Store)
            .ListReady(
                observedAtUtc ?? context.Slot.ScheduledStartUtc!.Value,
                sid ?? Sid,
                session ?? Session,
                limit);

    private static void AssertMalformedPostStartMutation(
        Action<RecurringOccurrenceReservationTests.ReservationContext> mutate)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 2,
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        AdvanceToPostStart(context, 0, PostStartShape.Recording);
        mutate(context);

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(
            context,
            context.Slots[1].ScheduledStartUtc!.Value,
            limit: 1));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
        var laterOccurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slots[1].OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, laterOccurrence.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
    }

    private static void AssertValidDigestButParentMismatch(
        Action<RecurringOccurrenceReservationTests.ReservationContext> mutate)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 2,
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: Sid,
            approvalSessionBinding: Session);
        AdvanceToPostStart(context, 0, PostStartShape.Recording);

        var specificationDigest = Convert.ToString(context.Fixture.Scalar(
            "SELECT specification_digest FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slots[0].OccurrenceIdentity)))!;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = context.Fixture.Store.DatabasePath,
        }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection,
                transaction,
                context.Slots[0].OccurrenceIdentity,
                context.Slots[0].OccurrenceId!);
            Assert.NotNull(specification);
            Assert.Equal(specificationDigest, specification!.SpecificationDigest);
            transaction.Commit();
        }
        mutate(context);

        var exception = Assert.Throws<Phase3PersistenceException>(() => Query(
            context,
            context.Slots[1].ScheduledStartUtc!.Value,
            limit: 1));

        Assert.Equal(RecurringPersistenceReasonCodes.PersistedDataInvalid, exception.Code);
        var laterOccurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slots[1].OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, laterOccurrence.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", context.Slots[1].OccurrenceId!))));
    }

    private static void AssertCandidate(
        RecurringOccurrenceNaturalWakeCandidate candidate,
        RecurringOccurrenceReservationTests.ReservationContext context,
        string status)
    {
        Assert.Equal(context.Lease.LeaseId, candidate.LeaseId);
        Assert.Equal(status, candidate.ObservedOccurrenceStatusCode);
        Assert.Equal(context.Fixture.PlanId, candidate.Slot.PlanId);
        Assert.Equal(context.Slot.ScheduleRevision, candidate.Slot.ScheduleRevision);
        Assert.Equal(context.Slot.ScheduleDigest, candidate.Slot.ScheduleDigest);
        Assert.Equal(context.Slot.OccurrenceIdentity, candidate.Slot.OccurrenceIdentity);
        Assert.Equal(context.Slot.ScheduledStartUtc, candidate.Slot.ScheduledStartUtc);
        Assert.Equal(context.Slot.LatestStartUtc, candidate.Slot.LatestStartUtc);
        Assert.Equal(context.Slot.PlannedEndUtc, candidate.Slot.PlannedEndUtc);
    }

    private static RecurringOccurrenceReservationResult Reserve(
        RecurringOccurrenceReservationTests.ReservationContext context) =>
        new RecurringOccurrenceReservationService(
                context.Fixture.Store,
                () => context.Slot.ScheduledStartUtc!.Value,
                () => "natural-wake-reserved-run",
                () => "natural-wake-reserved-use")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

    private static void AdvanceToPostStart(
        RecurringOccurrenceReservationTests.ReservationContext context,
        int slotIndex,
        PostStartShape shape)
    {
        var slot = context.Slots[slotIndex];
        var observedAt = slot.ScheduledStartUtc!.Value;
        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(slot.OccurrenceId!);
        if (occurrence.Status != PlanOccurrenceStatus.Authorized)
        {
            var due = new SqlitePeriodicOccurrenceDueProjectionTransaction(context.Fixture.Store)
                .Project(slot.OccurrenceIdentity, 0, observedAt);
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);

            var recheck = new RecurringOccurrenceEnvironmentRecheckService(
                    context.Fixture.Store,
                    () => observedAt,
                    new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
                .Recheck(context.Lease.LeaseId, slot.OccurrenceIdentity);
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized, recheck.Status);
        }

        InsertExactPostStartShape(context, slot, slotIndex, shape);
    }

    private static void InsertExactPostStartShape(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        int slotIndex,
        PostStartShape shape)
    {
        var (runStatus, runVersion, useStatus, useVersion, occurrenceStatus, occurrenceVersion, actualSettledDuration) = shape switch
        {
            PostStartShape.StartCommitted => ("start_committed", 2, "start_committed", 1, "run_created", 4, (long?)null),
            PostStartShape.Recording => ("recording", 3, "consumed", 2, "run_created", 4, (long?)null),
            PostStartShape.Finalizing => ("finalizing", 4, "consumed", 2, "run_created", 4, (long?)null),
            PostStartShape.MediaReady => ("media_ready", 5, "consumed", 2, "run_created", 4, (long?)null),
            PostStartShape.ReconciledCompleted => ("settled", 6, "settled", 3, "completed", 6, 1L),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        context.Fixture.Execute(
            "INSERT INTO recording_runs (id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ($run_id, $occurrence_id, $run_status, 1, NULL, NULL, NULL, $at, $at, $run_version); INSERT INTO recurring_lease_uses (use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version) VALUES ($use_id, $lease_id, $plan_id, $identity, $occurrence_id, $run_id, $use_status, 1, $duration, $actual_duration, $at, $at, $use_version); UPDATE plan_occurrences SET status_code = $occurrence_status, run_id = $run_id, terminal_reason_code = NULL, version = $occurrence_version WHERE id = $occurrence_id;",
            ("$run_id", "natural-wake-mixed-run-" + slotIndex),
            ("$use_id", "natural-wake-mixed-use-" + slotIndex),
            ("$lease_id", context.Lease.LeaseId),
            ("$plan_id", context.Fixture.PlanId),
            ("$identity", slot.OccurrenceIdentity),
            ("$at", slot.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
            ("$run_status", runStatus),
            ("$run_version", runVersion),
            ("$use_status", useStatus),
            ("$use_version", useVersion),
            ("$duration", context.Lease.PerRunDuration.Ticks),
            ("$actual_duration", actualSettledDuration),
            ("$occurrence_status", occurrenceStatus),
            ("$occurrence_version", occurrenceVersion),
            ("$occurrence_id", slot.OccurrenceId!));
    }

    private static void RevokeLease(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string operationId)
    {
        var result = new StandingLeaseSafetyControlService(
                context.Fixture.Store,
                () => context.Slot.ScheduledStartUtc!.Value.AddMinutes(1))
            .RevokeRecurringLease(context.Lease.LeaseId, operationId, "natural_wake_test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked,
            new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId).Status);
    }

    private static void TamperSpecificationField(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string field,
        string value)
    {
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "profile_digest",
            "configuration_digest",
            "schedule_digest",
            "schedule_revision",
            "local_approval_digest",
            "lease_authorization_digest",
            "occurrence_identity",
            "occurrence_id",
        };
        Assert.Contains(field, allowedFields);
        context.Fixture.Execute(
            $"PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET {field} = $value WHERE occurrence_identity = $identity;",
            ("$value", value),
            ("$identity", context.Slot.OccurrenceIdentity));
    }

    private static (long RunCount, long UseCount, string RunStatuses, string UseStatuses, string Quota) CaptureExecutionChainState(
        RecurringOccurrenceReservationTests.ReservationContext context) =>
        (
            Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"),
            Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"),
            ReadText(context.Fixture, "SELECT GROUP_CONCAT(status_code || ':' || version, ',') FROM recording_runs ORDER BY id;"),
            ReadText(context.Fixture, "SELECT GROUP_CONCAT(status_code || ':' || version, ',') FROM recurring_lease_uses ORDER BY use_id;"),
            ReadText(context.Fixture, "SELECT status_code || ':' || version || ':' || max_uses || ':' || max_cumulative_duration_ticks FROM recurring_consent_leases;"));

    private static RecurringOccurrenceExecutionCoordinator CreateCoordinator(
        RecurringOccurrenceReservationTests.ReservationContext context,
        NaturalWakeBackend backend,
        SqliteOperationalStore? store = null,
        StandingLeaseStartSafetyInterlock? interlock = null,
        IRecurringOccurrenceEnvironmentProvider? environmentProvider = null,
        IList<RecurringOccurrenceExecutionStage>? stages = null,
        Action? recoveryCalls = null,
        DateTimeOffset? nowUtc = null) =>
        new(
            store ?? context.Fixture.Store,
            environmentProvider ?? new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider(),
            _ => backend,
            utcNowForTest: () => nowUtc ?? context.Slot.ScheduledStartUtc!.Value,
            startSafetyInterlockForTest: interlock,
            currentUserSidForTest: () => Sid,
            sessionBindingForTest: () => Session,
            recoveryAfterExactReadForTest: (_, _) => recoveryCalls?.Invoke(),
            stageHookForTest: stage => stages?.Add(stage));

    private static void TransitionPlan(
        RecurringOccurrenceReservationTests.ReservationContext context,
        PlanDefinitionStatus status)
    {
        var repository = new SqlitePlanDefinitionRepository(context.Fixture.Store);
        var plan = repository.Get(context.Fixture.PlanId);
        Assert.True(plan.TryTransition(status, context.Slot.ScheduledStartUtc!.Value).Succeeded);
        repository.Update(plan, plan.Version - 1);
    }

    private static void TamperSpecificationDigest(
        RecurringOccurrenceReservationTests.ReservationContext context)
    {
        context.Fixture.Execute(
            "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $digest WHERE occurrence_identity = $identity;",
            ("$digest", "recurring-occurrence-execution-spec/v1:" + new string('b', 64)),
            ("$identity", context.Slot.OccurrenceIdentity));
    }

    private static (long OccurrenceVersion, long PlanVersion, long LeaseVersion, long SpecCount, long RunCount, long UseCount) CaptureDurableState(
        RecurringOccurrenceReservationTests.ReservationContext context) =>
        (
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Version,
            new SqlitePlanDefinitionRepository(context.Fixture.Store).Get(context.Fixture.PlanId).Version,
            new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId).Version,
            Count(context.Fixture, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"),
            Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"),
            Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));

    private static long Count(RecurringLeaseFixture fixture, string sql) =>
        Convert.ToInt64(fixture.Scalar(sql));

    private static string ReadText(RecurringLeaseFixture fixture, string sql) =>
        Convert.ToString(fixture.Scalar(sql))!;

    private enum PostStartShape
    {
        StartCommitted,
        Recording,
        Finalizing,
        MediaReady,
        ReconciledCompleted,
    }

    private sealed class NaturalWakeBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<FirstFrameObservation>? _firstFrameObserved;
        private Action<int, OutputMeta>? _naturalExit;

        internal int StartCalls { get; private set; }

        public void Start(CaptureConfig config, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            authorizationProof.RequireConsumed();
        }

        public OutputMeta Stop() => new()
        {
            SizeBytes = 1024,
            OutputFileExists = true,
            DurationSeconds = 1,
            OutputPath = "natural-wake-test-output.mp4",
            StopReason = "stop",
        };

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add => _firstFrameObserved += value;
            remove => _firstFrameObserved -= value;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        private readonly RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider _inner = new();

        internal int CaptureCalls { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            CaptureCalls++;
            return _inner.Capture(request);
        }
    }

    private sealed class FakeLifecycleSession : IRecurringLeaseCaptureLifecycleSession
    {
        internal int DisposeCalls { get; private set; }

        public bool TryAttach(ICaptureBackend backend, out string failureReason)
        {
            failureReason = "";
            return true;
        }

        public RecurringLeaseCaptureLifecycleHandoffResult CompleteStartHandoff() =>
            new(RecurringLeaseCaptureLifecycleHandoffStatus.Active);

        public RecurringLeaseLifecycleActionResult StartFailed() =>
            RecurringLeaseLifecycleActionResult.Applied("test_start_failed", terminal: true);

        public RecurringLeaseLifecycleActionResult Stop() =>
            RecurringLeaseLifecycleActionResult.Applied("test_stop", terminal: true);

        public void Dispose() => DisposeCalls++;
    }
}
