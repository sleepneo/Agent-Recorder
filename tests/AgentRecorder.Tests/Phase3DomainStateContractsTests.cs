using System.Globalization;
using System.Reflection;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class Phase3DomainStateContractsTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatusCodesAreExactStableLowerSnakeCaseCollections()
    {
        Assert.Equal(
            new[] { "draft", "enabled", "paused", "cancelled" },
            Enum.GetValues<PlanDefinitionStatus>().Select(Phase3StateCodes.ToCode));
        Assert.Equal(
            new[] { "scheduled", "due", "rechecking", "pending_lease_approval", "authorized", "pending_confirmation", "run_created", "completed", "missed", "blocked", "cancelled", "expired" },
            Enum.GetValues<PlanOccurrenceStatus>().Select(Phase3StateCodes.ToCode));
        Assert.Equal(
            new[] { "created", "preparing", "start_committed", "recording", "started_unknown", "finalizing", "media_ready", "settled", "session_interrupted", "failed" },
            Enum.GetValues<RecordingRunStatus>().Select(Phase3StateCodes.ToCode));
        Assert.Equal(
            new[] { "pending", "active", "rejected", "revoked", "expired", "exhausted" },
            Enum.GetValues<ConsentLeaseStatus>().Select(Phase3StateCodes.ToCode));
        Assert.Equal(
            new[] { "available", "reserved", "start_committed", "consumed", "settled", "started_unknown" },
            Enum.GetValues<LeaseUseStatus>().Select(Phase3StateCodes.ToCode));
    }

    [Fact]
    public void StatusCodesRoundTripWithoutCultureDependence()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var cultureName in new[] { "en-US", "zh-CN", "ar-SA" })
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                Assert.Equal(RecordingRunStatus.StartCommitted, Phase3StateCodes.ParseRecordingRun("start_committed"));
                Assert.Equal("pending_lease_approval", Phase3StateCodes.ToCode(PlanOccurrenceStatus.PendingLeaseApproval));
                Assert.Equal(ConsentLeaseStatus.Expired, Phase3StateCodes.ParseConsentLease("expired"));
                Assert.Equal(LeaseUseStatus.StartedUnknown, Phase3StateCodes.ParseLeaseUse("started_unknown"));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void UnknownStateCodeFailsClosed()
    {
        var exception = Assert.Throws<Phase3DomainException>(() => Phase3StateCodes.ParsePlanDefinition("ENABLED"));
        Assert.Equal("unknown_state_code", exception.ReasonCode);
        Assert.False(Phase3StateCodes.TryParseLeaseUse("not-a-state", out _));
    }

    [Fact]
    public void EveryDeclaredLegalTransitionIsAcceptedByTheSingleGuard()
    {
        AssertLegal(Phase3TransitionGuards.IsPlanDefinitionEdge, new[]
        {
            (PlanDefinitionStatus.Draft, PlanDefinitionStatus.Enabled),
            (PlanDefinitionStatus.Enabled, PlanDefinitionStatus.Paused),
            (PlanDefinitionStatus.Enabled, PlanDefinitionStatus.Cancelled),
            (PlanDefinitionStatus.Paused, PlanDefinitionStatus.Enabled),
            (PlanDefinitionStatus.Paused, PlanDefinitionStatus.Cancelled),
        });
        AssertLegal(Phase3TransitionGuards.IsPlanOccurrenceEdge, new[]
        {
            (PlanOccurrenceStatus.Scheduled, PlanOccurrenceStatus.Due),
            (PlanOccurrenceStatus.Scheduled, PlanOccurrenceStatus.Missed),
            (PlanOccurrenceStatus.Scheduled, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.Scheduled, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Rechecking),
            (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Missed),
            (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.PendingLeaseApproval),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.Authorized),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.PendingConfirmation),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.Missed),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Authorized),
            (PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.Authorized, PlanOccurrenceStatus.RunCreated),
            (PlanOccurrenceStatus.Authorized, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.Authorized, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.Authorized, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.PendingConfirmation, PlanOccurrenceStatus.RunCreated),
            (PlanOccurrenceStatus.PendingConfirmation, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.PendingConfirmation, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.PendingConfirmation, PlanOccurrenceStatus.Expired),
            (PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Completed),
            (PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Blocked),
            (PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Cancelled),
            (PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Expired),
        });
        AssertLegal(Phase3TransitionGuards.IsRecordingRunEdge, new[]
        {
            (RecordingRunStatus.Created, RecordingRunStatus.Preparing),
            (RecordingRunStatus.Created, RecordingRunStatus.Failed),
            (RecordingRunStatus.Preparing, RecordingRunStatus.StartCommitted),
            (RecordingRunStatus.Preparing, RecordingRunStatus.Failed),
            (RecordingRunStatus.StartCommitted, RecordingRunStatus.Recording),
            (RecordingRunStatus.StartCommitted, RecordingRunStatus.StartedUnknown),
            (RecordingRunStatus.StartCommitted, RecordingRunStatus.Failed),
            (RecordingRunStatus.Recording, RecordingRunStatus.Finalizing),
            (RecordingRunStatus.Recording, RecordingRunStatus.StartedUnknown),
            (RecordingRunStatus.Recording, RecordingRunStatus.SessionInterrupted),
            (RecordingRunStatus.Recording, RecordingRunStatus.Failed),
            (RecordingRunStatus.Finalizing, RecordingRunStatus.MediaReady),
            (RecordingRunStatus.Finalizing, RecordingRunStatus.SessionInterrupted),
            (RecordingRunStatus.Finalizing, RecordingRunStatus.Failed),
            (RecordingRunStatus.MediaReady, RecordingRunStatus.Settled),
        });
        AssertLegal(Phase3TransitionGuards.IsConsentLeaseEdge, new[]
        {
            (ConsentLeaseStatus.Pending, ConsentLeaseStatus.Active),
            (ConsentLeaseStatus.Pending, ConsentLeaseStatus.Rejected),
            (ConsentLeaseStatus.Pending, ConsentLeaseStatus.Revoked),
            (ConsentLeaseStatus.Pending, ConsentLeaseStatus.Expired),
            (ConsentLeaseStatus.Active, ConsentLeaseStatus.Revoked),
            (ConsentLeaseStatus.Active, ConsentLeaseStatus.Expired),
            (ConsentLeaseStatus.Active, ConsentLeaseStatus.Exhausted),
        });
        AssertLegal(Phase3TransitionGuards.IsLeaseUseEdge, new[]
        {
            (LeaseUseStatus.Available, LeaseUseStatus.Reserved),
            (LeaseUseStatus.Reserved, LeaseUseStatus.Available),
            (LeaseUseStatus.Reserved, LeaseUseStatus.StartCommitted),
            (LeaseUseStatus.StartCommitted, LeaseUseStatus.Consumed),
            (LeaseUseStatus.StartCommitted, LeaseUseStatus.StartedUnknown),
            (LeaseUseStatus.Consumed, LeaseUseStatus.Settled),
        });
    }

    [Fact]
    public void SameStateIsAnIdempotentNoOpAndTerminalStatesCannotReactivate()
    {
        var plan = new PlanDefinition("plan-1", true, T0);
        var same = plan.TryTransition(PlanDefinitionStatus.Draft, T0.AddMinutes(1));
        Assert.True(same.Succeeded);
        Assert.False(same.Changed);
        Assert.Equal(0, plan.Version);
        Assert.Equal(T0, plan.UpdatedAtUtc);

        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, T0.AddMinutes(1)).Succeeded);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Cancelled, T0.AddMinutes(2)).Succeeded);
        Assert.False(plan.TryTransition(PlanDefinitionStatus.Enabled, T0.AddMinutes(3)).Succeeded);
        Assert.True(Phase3TransitionGuards.IsTerminal(plan.Status));

        var lease = NewLease();
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, T0.AddMinutes(1)).Succeeded);
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Revoked, T0.AddMinutes(2)).Succeeded);
        Assert.False(lease.TryTransition(ConsentLeaseStatus.Active, T0.AddMinutes(3)).Succeeded);
        Assert.True(Phase3TransitionGuards.IsTerminal(lease.Status));
    }

    [Fact]
    public void EveryTerminalStateRejectsReactivation()
    {
        AssertTerminalGuards(
            Enum.GetValues<PlanDefinitionStatus>().Where(Phase3TransitionGuards.IsTerminal),
            (current, next) => Phase3TransitionGuards.CanTransition(current, next, out _),
            PlanDefinitionStatus.Enabled);
        AssertTerminalGuards(
            Enum.GetValues<PlanOccurrenceStatus>().Where(Phase3TransitionGuards.IsTerminal),
            (current, next) => Phase3TransitionGuards.CanTransition(current, next, out _),
            PlanOccurrenceStatus.Due);
        AssertTerminalGuards(
            Enum.GetValues<RecordingRunStatus>().Where(Phase3TransitionGuards.IsTerminal),
            (current, next) => Phase3TransitionGuards.CanTransition(current, next, out _),
            RecordingRunStatus.Recording);
        AssertTerminalGuards(
            Enum.GetValues<ConsentLeaseStatus>().Where(Phase3TransitionGuards.IsTerminal),
            (current, next) => Phase3TransitionGuards.CanTransition(current, next, out _),
            ConsentLeaseStatus.Active);
        AssertTerminalGuards(
            Enum.GetValues<LeaseUseStatus>().Where(Phase3TransitionGuards.IsTerminal),
            (current, next) => Phase3TransitionGuards.CanTransition(current, next, out _),
            LeaseUseStatus.Available);
    }

    [Fact]
    public void OccurrenceMissedAndRunCreatedAreMutuallyExclusive()
    {
        var missed = NewOccurrence();
        Assert.True(missed.TryTransition(PlanOccurrenceStatus.Due, T0.AddMinutes(1)).Succeeded);
        Assert.True(missed.TryTransition(PlanOccurrenceStatus.Missed, T0.AddMinutes(2), "window_elapsed").Succeeded);
        Assert.False(missed.TryTransition(PlanOccurrenceStatus.Due, T0.AddMinutes(3)).Succeeded);
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate(
            "occ-bad", "plan-1", T0, T0.AddMinutes(5), T0, PlanOccurrenceStatus.Missed, "run-1", "window_elapsed", T0, 0));

        var created = NewOccurrence();
        Assert.True(created.TryTransition(PlanOccurrenceStatus.Due, T0.AddMinutes(1)).Succeeded);
        Assert.True(created.TryTransition(PlanOccurrenceStatus.Rechecking, T0.AddMinutes(2)).Succeeded);
        Assert.True(created.TryTransition(PlanOccurrenceStatus.Authorized, T0.AddMinutes(3)).Succeeded);
        Assert.True(created.TryCreateRun("run-1", T0.AddMinutes(4)).Succeeded);
        Assert.False(created.TryTransition(PlanOccurrenceStatus.Missed, T0.AddMinutes(5), "too-late").Succeeded);
        Assert.Equal("run-1", created.RunId);
    }

    [Fact]
    public void RecordingRunStartCommitAndTerminalSemanticsAreMonotonic()
    {
        var run = new RecordingRun("run-1", "occ-1", T0);
        Assert.True(run.TryTransition(RecordingRunStatus.Preparing, T0.AddMinutes(1)).Succeeded);
        Assert.True(run.TryTransition(RecordingRunStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(run.HasCrossedStartCommit);
        Assert.True(run.TryTransition(RecordingRunStatus.Recording, T0.AddMinutes(3)).Succeeded);
        Assert.True(run.TryTransition(RecordingRunStatus.Finalizing, T0.AddMinutes(4)).Succeeded);
        Assert.True(run.TryTransition(RecordingRunStatus.MediaReady, T0.AddMinutes(5)).Succeeded);
        Assert.True(run.TryTransition(RecordingRunStatus.Settled, T0.AddMinutes(6)).Succeeded);
        Assert.True(run.IsNonRetryable);
        Assert.False(run.TryTransition(RecordingRunStatus.Created, T0.AddMinutes(7)).Succeeded);

        var failedBeforeStart = new RecordingRun("run-2", "occ-1", T0);
        Assert.True(failedBeforeStart.TryTransition(RecordingRunStatus.Failed, T0.AddMinutes(1), "preflight_failed").Succeeded);
        Assert.False(failedBeforeStart.HasCrossedStartCommit);
        Assert.False(failedBeforeStart.IsNonRetryable);

        var interrupted = new RecordingRun("run-3", "occ-1", T0);
        Assert.True(interrupted.TryTransition(RecordingRunStatus.Preparing, T0.AddMinutes(1)).Succeeded);
        Assert.True(interrupted.TryTransition(RecordingRunStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(interrupted.TryTransition(RecordingRunStatus.Recording, T0.AddMinutes(3)).Succeeded);
        Assert.True(interrupted.TryTransition(RecordingRunStatus.SessionInterrupted, T0.AddMinutes(4), "session_lost").Succeeded);
        Assert.True(Phase3TransitionGuards.IsTerminal(interrupted.Status));
    }

    [Fact]
    public void LeaseUseCanReleaseOnlyBeforeStartCommitAndStartedUnknownConsumesQuota()
    {
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(1)).Succeeded);
        Assert.True(use.TryReleaseReservation(T0.AddMinutes(2)).Succeeded);
        Assert.False(use.IsQuotaConsumed);
        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(3)).Succeeded);
        Assert.True(use.TryTransition(LeaseUseStatus.StartCommitted, T0.AddMinutes(4)).Succeeded);
        Assert.True(use.IsQuotaConsumed);
        Assert.False(use.TryTransition(LeaseUseStatus.Available, T0.AddMinutes(5)).Succeeded);
        Assert.True(use.TryTransition(LeaseUseStatus.StartedUnknown, T0.AddMinutes(5)).Succeeded);
        Assert.True(use.IsQuotaConsumed);
        Assert.True(Phase3TransitionGuards.IsTerminal(use.Status));
    }

    [Fact]
    public void LeaseExpiryIsEvaluatedAtSuppliedUtcTimeAndQuotaIsValidated()
    {
        var lease = NewLease();
        Assert.False(lease.IsExpiredAt(T0.AddMinutes(9)));
        Assert.True(lease.IsExpiredAt(T0.AddMinutes(10)));
        Assert.Throws<Phase3DomainException>(() => lease.IsExpiredAt(T0.AddMinutes(10).ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<Phase3DomainException>(() => new ConsentLease("lease-bad", "plan-1", "occ-1", T0, T0.AddMinutes(1), -1, TimeSpan.Zero));
        Assert.Throws<Phase3DomainException>(() => new ConsentLease("lease-bad", "plan-1", "occ-1", T0, T0.AddMinutes(1), 1, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ConstructorsRejectInvalidIdsTimesWindowsRelationsAndDurations()
    {
        Assert.Throws<Phase3DomainException>(() => new PlanDefinition(" ", true, T0));
        Assert.Throws<Phase3DomainException>(() => new PlanDefinition("plan-1", true, T0.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<Phase3DomainException>(() => new PlanOccurrence("occ-1", "plan-1", T0, T0, T0));
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate(
            "occ-1", "plan-1", T0, T0.AddMinutes(1), T0, PlanOccurrenceStatus.RunCreated, null, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Available, -1, TimeSpan.Zero, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Settled, 1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1), T0, 0));

        var plan = new PlanDefinition("plan-1", true, T0);
        var otherPlan = new PlanDefinition("plan-2", false, T0);
        var occurrence = PlanOccurrence.CreateFor(plan, "occ-1", T0, T0.AddMinutes(5), T0);
        Assert.Throws<Phase3DomainException>(() => ConsentLease.CreateFor(otherPlan, occurrence, T0, T0.AddMinutes(1), 1, TimeSpan.FromMinutes(1), "lease-1"));
        Assert.Throws<Phase3DomainException>(() => new RecordingRun("run-1", " ", T0));
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate(
            "occ-1", "plan-1", T0, T0.AddMinutes(1), T0, PlanOccurrenceStatus.Scheduled, null, " ", T0, 0));
    }

    [Fact]
    public void DomainObjectsExposeNoPublicStatusSetter()
    {
        Assert.False(typeof(PlanDefinition).GetProperty(nameof(PlanDefinition.Status))!.CanWrite);
        Assert.False(typeof(PlanOccurrence).GetProperty(nameof(PlanOccurrence.Status))!.CanWrite);
        Assert.False(typeof(RecordingRun).GetProperty(nameof(RecordingRun.Status))!.CanWrite);
        Assert.False(typeof(ConsentLease).GetProperty(nameof(ConsentLease.Status))!.CanWrite);
        Assert.False(typeof(LeaseUse).GetProperty(nameof(LeaseUse.Status))!.CanWrite);
    }

    [Fact]
    public void FactoriesValidateCrossAggregateIdentityWithoutChangingOtherStates()
    {
        var plan = new PlanDefinition("plan-1", true, T0);
        var occurrence = PlanOccurrence.CreateFor(plan, "occ-1", T0, T0.AddMinutes(5), T0);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, T0.AddMinutes(1)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, T0.AddMinutes(2)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Authorized, T0.AddMinutes(3)).Succeeded);
        Assert.True(occurrence.TryCreateRun("run-1", T0.AddMinutes(4)).Succeeded);
        var run = RecordingRun.CreateFor(occurrence, "run-1", T0.AddMinutes(4));
        var lease = ConsentLease.CreateFor(plan, occurrence, T0, T0.AddMinutes(5), 1, TimeSpan.FromMinutes(1), "lease-1");
        var use = LeaseUse.CreateFor(lease, occurrence, run, "use-1", T0.AddMinutes(4));

        Assert.Equal(PlanDefinitionStatus.Draft, plan.Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
        Assert.Equal(RecordingRunStatus.Created, run.Status);
        Assert.Equal(ConsentLeaseStatus.Pending, lease.Status);
        Assert.Equal(LeaseUseStatus.Available, use.Status);
        Assert.Throws<Phase3DomainException>(() => RecordingRun.CreateFor(occurrence, "different-run", T0));
    }

    [Fact]
    public void RepeatedReserveWithSameDataIsNoOpAndDifferentDataIsAnAtomicConflict()
    {
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(1)).Succeeded);
        var version = use.Version;
        var updatedAt = use.UpdatedAtUtc;
        var count = use.ReservedUseCount;
        var duration = use.ReservedDuration;

        var same = use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(2));
        Assert.True(same.Succeeded);
        Assert.False(same.Changed);
        Assert.Equal("idempotent_noop", same.ReasonCode);
        Assert.Equal(version, use.Version);
        Assert.Equal(updatedAt, use.UpdatedAtUtc);
        Assert.Equal(count, use.ReservedUseCount);
        Assert.Equal(duration, use.ReservedDuration);

        var mismatch = use.TryReserve(2, TimeSpan.FromMinutes(5), T0.AddMinutes(3));
        Assert.False(mismatch.Succeeded);
        Assert.Equal("reservation_mismatch", mismatch.ReasonCode);
        Assert.Equal(version, use.Version);
        Assert.Equal(updatedAt, use.UpdatedAtUtc);
        Assert.Equal(count, use.ReservedUseCount);
        Assert.Equal(duration, use.ReservedDuration);
    }

    [Fact]
    public void ReleaseAtomicallyClearsReservationAndCannotBeBypassed()
    {
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.Equal("reservation_required", use.TryTransition(LeaseUseStatus.Reserved, T0.AddMinutes(1)).ReasonCode);
        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(2)).Succeeded);

        var released = use.TryReleaseReservation(T0.AddMinutes(3));
        Assert.True(released.Succeeded);
        Assert.Equal(LeaseUseStatus.Available, use.Status);
        Assert.Equal(0, use.ReservedUseCount);
        Assert.Equal(TimeSpan.Zero, use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);
        var version = use.Version;
        var updatedAt = use.UpdatedAtUtc;
        var repeated = use.TryReleaseReservation(T0.AddMinutes(4));
        Assert.True(repeated.Succeeded);
        Assert.False(repeated.Changed);
        Assert.Equal(version, use.Version);
        Assert.Equal(updatedAt, use.UpdatedAtUtc);

        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(5)).Succeeded);
        Assert.Equal("release_required", use.TryTransition(LeaseUseStatus.Available, T0.AddMinutes(6)).ReasonCode);
        Assert.Equal(LeaseUseStatus.Reserved, use.Status);
        Assert.Equal(1, use.ReservedUseCount);
    }

    [Fact]
    public void SettleIsAtomicIdempotentAndCannotBeBypassedByGenericTransition()
    {
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.True(use.TryReserve(1, TimeSpan.FromMinutes(5), T0.AddMinutes(1)).Succeeded);
        Assert.True(use.TryTransition(LeaseUseStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(use.TryTransition(LeaseUseStatus.Consumed, T0.AddMinutes(3)).Succeeded);
        Assert.Equal("settlement_required", use.TryTransition(LeaseUseStatus.Settled, T0.AddMinutes(4)).ReasonCode);

        Assert.True(use.TrySettle(TimeSpan.FromMinutes(2), T0.AddMinutes(5)).Succeeded);
        var version = use.Version;
        var updatedAt = use.UpdatedAtUtc;
        var actual = use.ActualSettledDuration;
        var same = use.TrySettle(TimeSpan.FromMinutes(2), T0.AddMinutes(6));
        Assert.True(same.Succeeded);
        Assert.False(same.Changed);
        Assert.Equal("idempotent_noop", same.ReasonCode);
        Assert.Equal(version, use.Version);
        Assert.Equal(updatedAt, use.UpdatedAtUtc);
        Assert.Equal(actual, use.ActualSettledDuration);

        var mismatch = use.TrySettle(TimeSpan.FromMinutes(3), T0.AddMinutes(7));
        Assert.False(mismatch.Succeeded);
        Assert.Equal("settlement_mismatch", mismatch.ReasonCode);
        Assert.Equal(version, use.Version);
        Assert.Equal(updatedAt, use.UpdatedAtUtc);
        Assert.Equal(actual, use.ActualSettledDuration);
    }

    [Fact]
    public void PostCommitRehydrateRejectsMissingOrPartialReservationEvidence()
    {
        foreach (var status in new[]
        {
            LeaseUseStatus.StartCommitted,
            LeaseUseStatus.Consumed,
            LeaseUseStatus.Settled,
            LeaseUseStatus.StartedUnknown,
        })
        {
            var actualDuration = status == LeaseUseStatus.Settled ? TimeSpan.Zero : (TimeSpan?)null;

            var missing = Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate(
                "use-" + status, "lease-1", "occ-1", "run-1", T0, status,
                0, TimeSpan.Zero, actualDuration, T0, 0));
            Assert.Equal("reservation_evidence_required", missing.ReasonCode);

            var missingDuration = Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate(
                "use-duration-" + status, "lease-1", "occ-1", "run-1", T0, status,
                1, TimeSpan.Zero, actualDuration, T0, 0));
            Assert.Equal("reservation_evidence_required", missingDuration.ReasonCode);

            var missingCount = Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate(
                "use-count-" + status, "lease-1", "occ-1", "run-1", T0, status,
                0, TimeSpan.FromMinutes(1), actualDuration, T0, 0));
            Assert.Equal("reservation_evidence_required", missingCount.ReasonCode);
        }
    }

    [Fact]
    public void PostCommitRehydratePreservesPositiveReservationEvidence()
    {
        foreach (var status in new[]
        {
            LeaseUseStatus.StartCommitted,
            LeaseUseStatus.Consumed,
            LeaseUseStatus.Settled,
            LeaseUseStatus.StartedUnknown,
        })
        {
            var actualDuration = status == LeaseUseStatus.Settled ? TimeSpan.FromSeconds(17) : (TimeSpan?)null;
            var use = LeaseUse.Rehydrate("use-" + status, "lease-1", "occ-1", "run-1", T0, status,
                3, TimeSpan.FromMinutes(7), actualDuration, T0.AddMinutes(1), 4);

            Assert.Equal(status, use.Status);
            Assert.Equal(3, use.ReservedUseCount);
            Assert.Equal(TimeSpan.FromMinutes(7), use.ReservedDuration);
            Assert.Equal(actualDuration, use.ActualSettledDuration);
            Assert.Equal(4, use.Version);
        }
    }

    [Fact]
    public void SettledAndStartedUnknownTransitionsRetainReservationEvidence()
    {
        var settled = new LeaseUse("use-settled", "lease-1", "occ-1", "run-1", T0);
        Assert.True(settled.TryReserve(2, TimeSpan.FromMinutes(7), T0.AddMinutes(1)).Succeeded);
        Assert.True(settled.TryTransition(LeaseUseStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(settled.TryTransition(LeaseUseStatus.Consumed, T0.AddMinutes(3)).Succeeded);
        Assert.True(settled.TrySettle(TimeSpan.FromSeconds(19), T0.AddMinutes(4)).Succeeded);
        Assert.Equal(LeaseUseStatus.Settled, settled.Status);
        Assert.Equal(2, settled.ReservedUseCount);
        Assert.Equal(TimeSpan.FromMinutes(7), settled.ReservedDuration);
        Assert.Equal(TimeSpan.FromSeconds(19), settled.ActualSettledDuration);

        var unknown = new LeaseUse("use-unknown", "lease-1", "occ-1", "run-2", T0);
        Assert.True(unknown.TryReserve(2, TimeSpan.FromMinutes(7), T0.AddMinutes(1)).Succeeded);
        Assert.True(unknown.TryTransition(LeaseUseStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(unknown.TryTransition(LeaseUseStatus.StartedUnknown, T0.AddMinutes(3)).Succeeded);
        Assert.Equal(LeaseUseStatus.StartedUnknown, unknown.Status);
        Assert.Equal(2, unknown.ReservedUseCount);
        Assert.Equal(TimeSpan.FromMinutes(7), unknown.ReservedDuration);
        Assert.Null(unknown.ActualSettledDuration);
    }

    [Fact]
    public void ReleaseIsTheOnlyPreCommitPathThatClearsReservationEvidence()
    {
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.True(use.TryReserve(2, TimeSpan.FromMinutes(7), T0.AddMinutes(1)).Succeeded);

        var sameState = use.TryTransition(LeaseUseStatus.Reserved, T0.AddMinutes(2));
        Assert.True(sameState.Succeeded);
        Assert.False(sameState.Changed);
        Assert.Equal(2, use.ReservedUseCount);
        Assert.Equal(TimeSpan.FromMinutes(7), use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);

        var released = use.TryReleaseReservation(T0.AddMinutes(3));
        Assert.True(released.Succeeded);
        Assert.Equal(LeaseUseStatus.Available, use.Status);
        Assert.Equal(0, use.ReservedUseCount);
        Assert.Equal(TimeSpan.Zero, use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);
    }

    [Fact]
    public void TheOnlyRunRetryabilityHelperConsidersStatusAndStartCommit()
    {
        var helperMethods = typeof(Phase3TransitionGuards).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(Phase3TransitionGuards.IsRunNonRetryable))
            .ToArray();
        var helper = Assert.Single(helperMethods);
        Assert.Equal(2, helper.GetParameters().Length);

        var preStartFailed = new RecordingRun("run-1", "occ-1", T0);
        Assert.True(preStartFailed.TryTransition(RecordingRunStatus.Failed, T0.AddMinutes(1), "preflight_failed").Succeeded);
        Assert.False(Phase3TransitionGuards.IsRunNonRetryable(preStartFailed.Status, preStartFailed.HasCrossedStartCommit));
        Assert.False(preStartFailed.IsNonRetryable);

        var postStartFailed = new RecordingRun("run-2", "occ-1", T0);
        Assert.True(postStartFailed.TryTransition(RecordingRunStatus.Preparing, T0.AddMinutes(1)).Succeeded);
        Assert.True(postStartFailed.TryTransition(RecordingRunStatus.StartCommitted, T0.AddMinutes(2)).Succeeded);
        Assert.True(postStartFailed.TryTransition(RecordingRunStatus.Failed, T0.AddMinutes(3), "backend_start_failed").Succeeded);
        Assert.True(Phase3TransitionGuards.IsRunNonRetryable(postStartFailed.Status, postStartFailed.HasCrossedStartCommit));
        Assert.True(postStartFailed.IsNonRetryable);

        Assert.Throws<Phase3DomainException>(() => Phase3TransitionGuards.IsRunNonRetryable((RecordingRunStatus)999, false));
        Assert.Throws<Phase3DomainException>(() => Phase3TransitionGuards.IsRunNonRetryable(RecordingRunStatus.Created, true));
    }

    [Fact]
    public void OrdinaryPublicCreationOnlyProducesInitialStatesAndHasNoRehydrateBypass()
    {
        Assert.Equal(PlanDefinitionStatus.Draft, new PlanDefinition("plan-1", true, T0).Status);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, new PlanOccurrence("occ-1", "plan-1", T0, T0.AddMinutes(1), T0).Status);
        Assert.Equal(RecordingRunStatus.Created, new RecordingRun("run-1", "occ-1", T0).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, NewLease().Status);
        var use = new LeaseUse("use-1", "lease-1", "occ-1", "run-1", T0);
        Assert.Equal(LeaseUseStatus.Available, use.Status);
        Assert.Equal(0, use.ReservedUseCount);
        Assert.Equal(TimeSpan.Zero, use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);

        foreach (var type in new[] { typeof(PlanDefinition), typeof(PlanOccurrence), typeof(RecordingRun), typeof(ConsentLease), typeof(LeaseUse) })
        {
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(constructor.GetParameters(), parameter =>
                    string.Equals(parameter.Name, "status", StringComparison.OrdinalIgnoreCase));
            }

            Assert.Null(type.GetMethod("Rehydrate", BindingFlags.Public | BindingFlags.Static));
        }

        Assert.Null(typeof(ConsentLease).GetMethod("CreateActiveLease", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void TrustedRehydrateAcceptsEveryValidStateAndPreservesSnapshotData()
    {
        foreach (var status in Enum.GetValues<PlanDefinitionStatus>())
        {
            Assert.Equal(status, PlanDefinition.Rehydrate("plan-" + status, true, status, T0,
                status == PlanDefinitionStatus.Draft ? T0 : T0.AddMinutes(1), status == PlanDefinitionStatus.Draft ? 0 : 1).Status);
        }

        foreach (var status in Enum.GetValues<PlanOccurrenceStatus>())
        {
            var runId = status is PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed ? "run-" + status : null;
            var reason = status is PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or
                PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired ? "reason-" + status : null;
            var occurrence = PlanOccurrence.Rehydrate("occ-" + status, "plan-1", T0, T0.AddMinutes(5), T0,
                status, runId, reason, T0.AddMinutes(1), 1);
            Assert.Equal(status, occurrence.Status);
            Assert.Equal(runId, occurrence.RunId);
        }

        foreach (var status in Enum.GetValues<RecordingRunStatus>())
        {
            var crossed = status is not (RecordingRunStatus.Created or RecordingRunStatus.Preparing);
            var reason = status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed
                ? "reason-" + status
                : null;
            var run = RecordingRun.Rehydrate("run-" + status, "occ-1", T0, status, crossed,
                status is RecordingRunStatus.MediaReady or RecordingRunStatus.Settled ? "media-1" : null,
                status is RecordingRunStatus.MediaReady or RecordingRunStatus.Settled ? "bundle-1" : null,
                reason, T0.AddMinutes(1), 1);
            Assert.Equal(status, run.Status);
        }

        foreach (var status in Enum.GetValues<ConsentLeaseStatus>())
        {
            Assert.Equal(status, ConsentLease.Rehydrate("lease-" + status, "plan-1", "occ-1", T0,
                T0.AddMinutes(5), 1, TimeSpan.FromMinutes(1), status, T0.AddMinutes(1), 1).Status);
        }

        Assert.Equal(LeaseUseStatus.Available,
            LeaseUse.Rehydrate("use-a", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.Available,
                0, TimeSpan.Zero, null, T0, 0).Status);
        Assert.Equal(LeaseUseStatus.Reserved,
            LeaseUse.Rehydrate("use-r", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.Reserved,
                1, TimeSpan.FromMinutes(1), null, T0.AddMinutes(1), 1).Status);
        Assert.Equal(LeaseUseStatus.StartCommitted,
            LeaseUse.Rehydrate("use-s", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.StartCommitted,
                1, TimeSpan.FromMinutes(1), null, T0.AddMinutes(1), 1).Status);
        Assert.Equal(LeaseUseStatus.Consumed,
            LeaseUse.Rehydrate("use-c", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.Consumed,
                1, TimeSpan.FromMinutes(1), null, T0.AddMinutes(1), 1).Status);
        Assert.Equal(TimeSpan.Zero,
            LeaseUse.Rehydrate("use-t", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.Settled,
                1, TimeSpan.FromMinutes(1), TimeSpan.Zero, T0.AddMinutes(1), 1).ActualSettledDuration);
        Assert.Equal(LeaseUseStatus.StartedUnknown,
            LeaseUse.Rehydrate("use-u", "lease-1", "occ-1", "run-1", T0, LeaseUseStatus.StartedUnknown,
                1, TimeSpan.FromMinutes(1), null, T0.AddMinutes(1), 1).Status);
    }

    [Fact]
    public void RehydrateRejectsInvalidStateSpecificSnapshotData()
    {
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate("occ-1", "plan-1", T0, T0.AddMinutes(1), T0,
            PlanOccurrenceStatus.Completed, null, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate("occ-1", "plan-1", T0, T0.AddMinutes(1), T0,
            PlanOccurrenceStatus.Missed, "run-1", "late", T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Settled, 1, TimeSpan.FromMinutes(1), null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Available, 1, TimeSpan.Zero, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Available, 0, TimeSpan.FromMinutes(1), null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => LeaseUse.Rehydrate("use-1", "lease-1", "occ-1", "run-1", T0,
            LeaseUseStatus.Reserved, 0, TimeSpan.Zero, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => RecordingRun.Rehydrate("run-1", "occ-1", T0,
            RecordingRunStatus.StartCommitted, false, null, null, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => RecordingRun.Rehydrate("run-1", "occ-1", T0,
            RecordingRunStatus.Created, true, null, null, null, T0, 0));
        Assert.Throws<Phase3DomainException>(() => RecordingRun.Rehydrate("run-1", "occ-1", T0,
            RecordingRunStatus.Recording, true, null, null, "reason_on_active", T0, 0));
        Assert.Throws<Phase3DomainException>(() => new ConsentLease("lease-0", "plan-1", "occ-1", T0,
            T0.AddMinutes(1), 0, TimeSpan.FromMinutes(1)));
        Assert.Throws<Phase3DomainException>(() => new ConsentLease("lease-0", "plan-1", "occ-1", T0,
            T0.AddMinutes(1), 1, TimeSpan.Zero));
        Assert.Throws<Phase3DomainException>(() => ConsentLease.Rehydrate("lease-0", "plan-1", "occ-1", T0,
            T0.AddMinutes(1), -1, TimeSpan.FromMinutes(1), ConsentLeaseStatus.Pending, T0, 0));
    }

    [Fact]
    public void TryCreateRunIsIdempotentConflictingAndCompletedKeepsRunId()
    {
        var occurrence = AuthorizedOccurrence();
        Assert.True(occurrence.TryCreateRun("run-1", T0.AddMinutes(4)).Succeeded);
        var version = occurrence.Version;
        var updatedAt = occurrence.UpdatedAtUtc;
        var same = occurrence.TryCreateRun("run-1", T0.AddMinutes(5));
        Assert.True(same.Succeeded);
        Assert.False(same.Changed);
        Assert.Equal("idempotent_noop", same.ReasonCode);
        Assert.Equal(version, occurrence.Version);
        Assert.Equal(updatedAt, occurrence.UpdatedAtUtc);
        var mismatch = occurrence.TryCreateRun("run-2", T0.AddMinutes(6));
        Assert.False(mismatch.Succeeded);
        Assert.Equal("run_id_mismatch", mismatch.ReasonCode);
        Assert.Equal(version, occurrence.Version);
        Assert.Equal(updatedAt, occurrence.UpdatedAtUtc);
        Assert.Equal("run-1", occurrence.RunId);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Completed, T0.AddMinutes(7)).Succeeded);
        Assert.Equal("run-1", occurrence.RunId);
    }

    [Fact]
    public void StateSpecificRulesDoNotExpandTheExistingTransitionMatrix()
    {
        Assert.False(Phase3TransitionGuards.IsRecordingRunEdge(RecordingRunStatus.StartCommitted, RecordingRunStatus.Created));
        Assert.False(Phase3TransitionGuards.IsRecordingRunEdge(RecordingRunStatus.Settled, RecordingRunStatus.Failed));
        Assert.False(Phase3TransitionGuards.IsPlanOccurrenceEdge(PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Missed));
        Assert.False(Phase3TransitionGuards.IsLeaseUseEdge(LeaseUseStatus.StartCommitted, LeaseUseStatus.Available));
        Assert.False(Phase3TransitionGuards.CanTransition((PlanDefinitionStatus)999, (PlanDefinitionStatus)999, out _));
    }

    private static PlanOccurrence AuthorizedOccurrence()
    {
        var occurrence = NewOccurrence();
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, T0.AddMinutes(1)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, T0.AddMinutes(2)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Authorized, T0.AddMinutes(3)).Succeeded);
        return occurrence;
    }

    private static PlanOccurrence NewOccurrence() => new("occ-1", "plan-1", T0, T0.AddMinutes(10), T0);

    private static ConsentLease NewLease() => new("lease-1", "plan-1", "occ-1", T0, T0.AddMinutes(10), 2, TimeSpan.FromMinutes(5));

    private static void AssertLegal<TStatus>(
        Func<TStatus, TStatus, bool> guard,
        IEnumerable<(TStatus Current, TStatus Next)> transitions)
        where TStatus : struct, Enum
    {
        foreach (var (current, next) in transitions)
        {
            Assert.True(guard(current, next), $"Expected {current} -> {next} to be legal.");
        }
    }

    private static void AssertTerminalGuards<TStatus>(
        IEnumerable<TStatus> terminals,
        Func<TStatus, TStatus, bool> guard,
        TStatus reactivation)
        where TStatus : struct, Enum
    {
        foreach (var terminal in terminals)
        {
            Assert.False(guard(terminal, reactivation), $"Expected terminal {terminal} not to reactivate.");
        }
    }
}
