namespace AgentRecorder.Core.Automation;

/// <summary>
/// Stable result codes for the bounded, persistence-only periodic occurrence
/// due projection.  These codes describe the projection outcome; they do not
/// start a scheduler, lease, run, or capture operation.
/// </summary>
public static class PeriodicOccurrenceDueResultCodes
{
    public const string BeforeWindow = "before_window";
    public const string Due = "due";
    public const string Missed = "missed";
    public const string AlreadyDue = "already_due";
    public const string AlreadyTerminal = "already_terminal";
    public const string AlreadyClaimed = "already_claimed";
    public const string PlanPaused = "plan_paused";
    public const string Cancelled = "cancelled";
    public const string Superseded = "schedule_revision_superseded";
}

public static class PeriodicOccurrenceDueTerminalReasonCodes
{
    public const string MissedScheduleWindow = "missed_schedule_window";
    public const string PlanCancelled = "plan_cancelled";
    public const string ScheduleRevisionSuperseded = "schedule_revision_superseded";
}

/// <summary>
/// Detached immutable facts returned by the periodic due-candidate query.
/// All timestamps are UTC and the occurrence version is the version observed
/// at query time, not a caller-provided schedule snapshot.
/// </summary>
public sealed record PeriodicOccurrenceDueCandidate(
    string PlanId,
    long ScheduleRevision,
    string ScheduleDigest,
    string OccurrenceIdentity,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    long OccurrenceVersion);

/// <summary>
/// The committed or converged result of one periodic occurrence projection.
/// </summary>
public sealed record PeriodicOccurrenceDueProjectionResult(
    string ResultCode,
    bool Changed,
    string OccurrenceIdentity,
    PlanOccurrenceStatus PreviousStatus,
    PlanOccurrenceStatus CurrentStatus,
    string? TerminalReasonCode,
    DateTimeOffset ObservedAtUtc,
    long OccurrenceVersion)
{
    public string PreviousStatusCode => Phase3StateCodes.ToCode(PreviousStatus);

    public string CurrentStatusCode => Phase3StateCodes.ToCode(CurrentStatus);
}
