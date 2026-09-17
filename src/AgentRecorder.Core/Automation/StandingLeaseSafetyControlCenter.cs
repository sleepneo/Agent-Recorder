namespace AgentRecorder.Core.Automation;

internal enum StandingLeaseControlCenterQueryStatus
{
    Available,
    Rejected,
}

internal sealed record StandingLeaseControlCenterLeaseSummary(
    string IntentId,
    StandingSetupIntentStatus IntentStatus,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    TimeSpan MaximumDuration,
    DateTimeOffset LeaseValidUntilUtc,
    string? PlanId,
    string? OccurrenceId,
    string? LeaseId,
    ConsentLeaseStatus? LeaseStatus,
    PlanOccurrenceStatus? OccurrenceStatus,
    string? BlockedReason,
    string? TerminalReasonCode,
    bool ActiveRunPresent,
    bool RequiresActiveRunStop);

internal sealed record StandingLeaseControlCenterState(
    UnattendedModeStatus UnattendedMode,
    bool StopAllApplied,
    string? StopAllOperationId,
    string? StopAllReasonCode,
    DateTimeOffset? StopAllRequestedAtUtc,
    DateTimeOffset? StopAllAppliedAtUtc,
    IReadOnlyList<StandingLeaseControlCenterLeaseSummary> Items);

internal sealed class StandingLeaseControlCenterQueryResult
{
    private StandingLeaseControlCenterQueryResult(
        StandingLeaseControlCenterQueryStatus status,
        string reason,
        StandingLeaseControlCenterState? state)
    {
        Status = status;
        Reason = reason;
        State = state;
    }

    internal StandingLeaseControlCenterQueryStatus Status { get; }
    internal string Reason { get; }
    internal StandingLeaseControlCenterState? State { get; }

    internal static StandingLeaseControlCenterQueryResult Available(
        StandingLeaseControlCenterState state) =>
        new(StandingLeaseControlCenterQueryStatus.Available, "available", state);

    internal static StandingLeaseControlCenterQueryResult Rejected(string reason) =>
        new(StandingLeaseControlCenterQueryStatus.Rejected, reason, null);
}
