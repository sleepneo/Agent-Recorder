using AgentRecorder.Core.Automation;

namespace AgentRecorder.Api;

/// <summary>
/// Strict recurring request DTO.  It is a parser boundary only; it does not
/// select a region, create a profile, issue a proof, or activate a lease.
/// </summary>
public sealed record RecurringPlanApiRequest(
    string IdempotencyKey,
    RecurringPlanSchedule Schedule,
    DateTimeOffset LeaseValidUntilUtc,
    int MaxRuns,
    TimeSpan MaxTotalDuration,
    string OutputDirectory,
    string FilenamePrefix);
