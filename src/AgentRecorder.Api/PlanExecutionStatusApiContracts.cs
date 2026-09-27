namespace AgentRecorder.Api;

/// <summary>
/// Authenticated, read-only gateway for durable plan execution state.
/// Identity is resolved by the interactive host, never supplied by HTTP.
/// </summary>
public interface IPlanExecutionStatusGateway
{
    PlanExecutionStatusState? Get(string planId);
}

public sealed record PlanExecutionStatusState(
    string PlanId,
    string Kind,
    string PlanStatus,
    bool? ScheduleExhausted,
    long OccurrenceCount,
    PlanExecutionOccurrenceState? NextOccurrence,
    PlanExecutionOccurrenceState? LatestOccurrence);

public sealed record PlanExecutionRunState(
    string RunId,
    string Status,
    string? TerminalReasonCode);

public sealed record PlanExecutionOccurrenceState(
    string OccurrenceId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Status,
    string? TerminalReasonCode,
    string? RunId,
    PlanExecutionRunState? Run,
    string? OutputPath,
    bool OutputPathRecorded,
    bool? OutputFileExists,
    string? ExecutionStatusCode = null);
