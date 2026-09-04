namespace AgentRecorder.Core.Automation;

public sealed class PlanOccurrence
{
    private PlanOccurrenceStatus _status;

    public PlanOccurrence(
        string id,
        string planId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null)
    {
        Id = Phase3Validation.RequiredId(id, nameof(id));
        PlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        WindowStartUtc = Phase3Validation.Utc(windowStartUtc, nameof(windowStartUtc));
        WindowEndUtc = Phase3Validation.Utc(windowEndUtc, nameof(windowEndUtc));
        Phase3Validation.Window(WindowStartUtc, WindowEndUtc, nameof(windowStartUtc));
        CreatedAtUtc = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = Phase3Validation.Utc(updatedAtUtc ?? createdAtUtc, nameof(updatedAtUtc));
        Phase3Validation.Relation(UpdatedAtUtc >= CreatedAtUtc, "non_monotonic_time", "updatedAtUtc must not precede createdAtUtc.");
        _status = PlanOccurrenceStatus.Scheduled;
    }

    internal static PlanOccurrence Rehydrate(
        string id,
        string planId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        DateTimeOffset createdAtUtc,
        PlanOccurrenceStatus status,
        string? runId,
        string? terminalReasonCode,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        var candidateRunId = runId is null ? null : Phase3Validation.RequiredId(runId, nameof(runId));
        var candidateReason = terminalReasonCode is null ? null : Phase3Validation.RequiredId(terminalReasonCode, nameof(terminalReasonCode));
        ValidateSnapshot(status, candidateRunId, candidateReason, version);
        var occurrence = new PlanOccurrence(id, planId, windowStartUtc, windowEndUtc, createdAtUtc, updatedAtUtc)
        {
            _status = status,
            RunId = candidateRunId,
            TerminalReasonCode = candidateReason,
            Version = version,
        };
        return occurrence;
    }

    public static PlanOccurrence CreateFor(
        PlanDefinition plan,
        string occurrenceId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanOccurrence(occurrenceId, plan.Id, windowStartUtc, windowEndUtc, createdAtUtc);
    }

    public string Id { get; }

    public string PlanId { get; }

    public PlanOccurrenceStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public DateTimeOffset WindowStartUtc { get; }

    public DateTimeOffset WindowEndUtc { get; }

    public string? RunId { get; private set; }

    public string? TerminalReasonCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool HasCreatedRun => RunId is not null;

    public bool CanCreateRun => RunId is null && Phase3TransitionGuards.IsOccurrenceAbleToCreateRun(_status);

    public Phase3TransitionResult TryCreateRun(string runId, DateTimeOffset? createdAtUtc = null)
    {
        var candidateRunId = Phase3Validation.RequiredId(runId, nameof(runId));
        if (_status == PlanOccurrenceStatus.RunCreated)
        {
            return string.Equals(RunId, candidateRunId, StringComparison.Ordinal)
                ? Phase3TransitionResult.Idempotent
                : Phase3TransitionResult.Failure("run_id_mismatch");
        }

        if (RunId is not null)
        {
            return Phase3TransitionResult.Failure("run_id_already_attached");
        }

        if (!Phase3TransitionGuards.IsOccurrenceAbleToCreateRun(_status))
        {
            return Phase3TransitionResult.Failure("occurrence_not_ready_to_create_run");
        }

        if (!Phase3TransitionGuards.CanTransition(_status, PlanOccurrenceStatus.RunCreated, out var transitionReason))
        {
            return Phase3TransitionResult.Failure(transitionReason);
        }

        var transitionTime = Phase3Validation.Utc(createdAtUtc ?? DateTimeOffset.UtcNow, nameof(createdAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        // Validate every input before the single in-memory commit.
        _status = PlanOccurrenceStatus.RunCreated;
        RunId = candidateRunId;
        TerminalReasonCode = null;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public Phase3TransitionResult TryTransition(
        PlanOccurrenceStatus next,
        DateTimeOffset? transitionedAtUtc = null,
        string? terminalReasonCode = null)
    {
        if (!Phase3TransitionGuards.CanTransition(_status, next, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        if (_status == next)
        {
            return Phase3TransitionResult.Idempotent;
        }

        if (next == PlanOccurrenceStatus.RunCreated)
        {
            return Phase3TransitionResult.Failure("run_creation_required");
        }

        if (next == PlanOccurrenceStatus.Missed && RunId is not null)
        {
            return Phase3TransitionResult.Failure("missed_after_run_created");
        }

        if (!Phase3TransitionGuards.IsTerminal(next) && terminalReasonCode is not null)
        {
            return Phase3TransitionResult.Failure("terminal_reason_on_nonterminal");
        }

        if (RequiresAbnormalTerminalReason(next) && string.IsNullOrWhiteSpace(terminalReasonCode))
        {
            return Phase3TransitionResult.Failure("terminal_reason_required");
        }

        var transitionTime = Phase3Validation.Utc(transitionedAtUtc ?? DateTimeOffset.UtcNow, nameof(transitionedAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        var candidateReason = terminalReasonCode is null ? null : Phase3Validation.RequiredId(terminalReasonCode, nameof(terminalReasonCode));
        _status = next;
        TerminalReasonCode = candidateReason;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public bool TryTransition(PlanOccurrenceStatus next, out string reasonCode)
    {
        var result = TryTransition(next, null, null);
        reasonCode = result.ReasonCode;
        return result.Succeeded;
    }

    private static bool RequiresAbnormalTerminalReason(PlanOccurrenceStatus status) => status is
        PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired;

    private static void ValidateSnapshot(PlanOccurrenceStatus status, string? runId, string? terminalReasonCode, long version)
    {
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known PlanOccurrenceStatus.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
        Phase3Validation.Relation(status is PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed ? runId is not null : true,
            "run_id_required", "run_created and completed occurrences must have a run ID.");
        Phase3Validation.Relation(status != PlanOccurrenceStatus.Missed || runId is null,
            "missed_after_run_created", "missed occurrences must not have a run ID.");
        Phase3Validation.Relation(Phase3TransitionGuards.IsTerminal(status) || terminalReasonCode is null,
            "terminal_reason_on_nonterminal", "non-terminal occurrences must not have a terminal reason.");
        Phase3Validation.Relation(!RequiresAbnormalTerminalReason(status) || terminalReasonCode is not null,
            "terminal_reason_required", "abnormal terminal occurrences require a terminal reason.");
    }
}
