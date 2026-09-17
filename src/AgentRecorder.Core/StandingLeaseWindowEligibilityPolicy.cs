using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Pure, internal policy for deciding whether an already-authorized standing
/// occurrence may be handed to the existing one-shot start gate. It does not
/// reserve a lease, create a run, issue proof, inspect the environment, or
/// invoke a capture backend.
/// </summary>
internal static class StandingLeaseWindowEligibilityPolicy
{
    internal static StandingLeaseWindowEligibilityDecision Evaluate(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        AuthorizedFixedRegionScope scope,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(scope);

        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("eligibility_time_not_utc");
        }

        if (!string.Equals(plan.Id, scope.PlanId, StringComparison.Ordinal) ||
            !string.Equals(plan.Id, occurrence.PlanId, StringComparison.Ordinal) ||
            !string.Equals(occurrence.Id, scope.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(lease.Id, scope.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("eligibility_relation_mismatch");
        }

        if (!plan.IsOneTime)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("plan_not_one_time");
        }

        if (Phase3TransitionGuards.IsTerminal(occurrence.Status))
        {
            return StandingLeaseWindowEligibilityDecision.AlreadyTerminal;
        }

        if (plan.Status != PlanDefinitionStatus.Enabled)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("plan_not_enabled");
        }

        if (occurrence.Status != PlanOccurrenceStatus.Authorized)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("occurrence_not_authorized");
        }

        if (lease.Status != ConsentLeaseStatus.Active)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("lease_not_active");
        }

        if (scope.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            scope.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            scope.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            scope.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            string.IsNullOrWhiteSpace(scope.StableDisplayFingerprint) ||
            scope.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            scope.AudioMode != AuthorizedAudioMode.None ||
            scope.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            scope.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            scope.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("scope_policy_not_supported");
        }

        if (scope.ReservedDuration <= TimeSpan.Zero ||
            scope.ReservedDuration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            scope.ReservedDuration > lease.MaxDuration)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("duration_out_of_scope");
        }

        // The occurrence window is half-open. The explicit pre-window result
        // takes precedence over all later time comparisons.
        if (nowUtc < occurrence.WindowStartUtc)
        {
            return StandingLeaseWindowEligibilityDecision.BeforeWindow;
        }

        if (nowUtc >= occurrence.WindowEndUtc)
        {
            return StandingLeaseWindowEligibilityDecision.Expired("occurrence_window_expired");
        }

        if (nowUtc < lease.ValidFromUtc)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("lease_not_yet_valid");
        }

        long endTicks;
        try
        {
            var nowTicks = nowUtc.UtcDateTime.Ticks;
            var durationTicks = scope.ReservedDuration.Ticks;
            if (durationTicks > DateTime.MaxValue.Ticks - nowTicks)
            {
                return StandingLeaseWindowEligibilityDecision.Rejected("eligibility_time_overflow");
            }

            endTicks = checked(nowTicks + durationTicks);
        }
        catch (OverflowException)
        {
            return StandingLeaseWindowEligibilityDecision.Rejected("eligibility_time_overflow");
        }

        if (endTicks > occurrence.WindowEndUtc.UtcDateTime.Ticks)
        {
            return StandingLeaseWindowEligibilityDecision.Expired("capture_duration_no_longer_fits");
        }

        if (endTicks > lease.ValidUntilUtc.UtcDateTime.Ticks)
        {
            return StandingLeaseWindowEligibilityDecision.Expired("lease_window_expired");
        }

        return StandingLeaseWindowEligibilityDecision.Eligible;
    }
}

internal readonly record struct StandingLeaseWindowEligibilityDecision(
    StandingLeaseWindowEligibilityStatus Status,
    string Reason)
{
    internal static StandingLeaseWindowEligibilityDecision BeforeWindow =>
        new(StandingLeaseWindowEligibilityStatus.BeforeWindow, "before_occurrence_window");

    internal static StandingLeaseWindowEligibilityDecision Eligible =>
        new(StandingLeaseWindowEligibilityStatus.Eligible, "execution_window_eligible");

    internal static StandingLeaseWindowEligibilityDecision AlreadyTerminal =>
        new(StandingLeaseWindowEligibilityStatus.AlreadyTerminal, "occurrence_already_terminal");

    internal static StandingLeaseWindowEligibilityDecision Expired(string reason) =>
        new(StandingLeaseWindowEligibilityStatus.Expired, reason);

    internal static StandingLeaseWindowEligibilityDecision Rejected(string reason) =>
        new(StandingLeaseWindowEligibilityStatus.Rejected, reason);
}

internal enum StandingLeaseWindowEligibilityStatus
{
    BeforeWindow,
    Eligible,
    Expired,
    AlreadyClaimed,
    AlreadyTerminal,
    Rejected,
}
