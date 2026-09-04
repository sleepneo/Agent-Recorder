namespace AgentRecorder.Core.Automation;

public sealed class RecordingRun
{
    private RecordingRunStatus _status;

    public RecordingRun(
        string id,
        string occurrenceId,
        DateTimeOffset createdAtUtc)
        : this(id, occurrenceId, createdAtUtc, RecordingRunStatus.Created, false, null, null, null, createdAtUtc, 0)
    {
    }

    private RecordingRun(
        string id,
        string occurrenceId,
        DateTimeOffset createdAtUtc,
        RecordingRunStatus status,
        bool hasCrossedStartCommit,
        string? mediaArtifactId,
        string? bundleId,
        string? terminalReasonCode,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        Id = Phase3Validation.RequiredId(id, nameof(id));
        OccurrenceId = Phase3Validation.RequiredId(occurrenceId, nameof(occurrenceId));
        CreatedAtUtc = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = Phase3Validation.Utc(updatedAtUtc, nameof(updatedAtUtc));
        Phase3Validation.Relation(UpdatedAtUtc >= CreatedAtUtc, "non_monotonic_time", "updatedAtUtc must not precede createdAtUtc.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known RecordingRunStatus.");
        HasCrossedStartCommit = hasCrossedStartCommit;
        MediaArtifactId = mediaArtifactId is null ? null : Phase3Validation.RequiredId(mediaArtifactId, nameof(mediaArtifactId));
        BundleId = bundleId is null ? null : Phase3Validation.RequiredId(bundleId, nameof(bundleId));
        TerminalReasonCode = terminalReasonCode is null ? null : Phase3Validation.RequiredId(terminalReasonCode, nameof(terminalReasonCode));
        ValidateSnapshot(status, hasCrossedStartCommit, TerminalReasonCode, version);
        _status = status;
        Version = version;
    }

    internal static RecordingRun Rehydrate(
        string id,
        string occurrenceId,
        DateTimeOffset createdAtUtc,
        RecordingRunStatus status,
        bool hasCrossedStartCommit,
        string? mediaArtifactId,
        string? bundleId,
        string? terminalReasonCode,
        DateTimeOffset updatedAtUtc,
        long version)
        => new(id, occurrenceId, createdAtUtc, status, hasCrossedStartCommit, mediaArtifactId, bundleId, terminalReasonCode, updatedAtUtc, version);

    public static RecordingRun CreateFor(
        PlanOccurrence occurrence,
        string runId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var candidateRunId = Phase3Validation.RequiredId(runId, nameof(runId));
        Phase3Validation.Relation(
            occurrence.RunId is not null && string.Equals(occurrence.RunId, candidateRunId, StringComparison.Ordinal),
            "inconsistent_relation",
            "The run ID must match the occurrence's attached run ID.");
        return new RecordingRun(candidateRunId, occurrence.Id, createdAtUtc);
    }

    public string Id { get; }

    public string OccurrenceId { get; }

    public RecordingRunStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public bool HasCrossedStartCommit { get; private set; }

    public string? MediaArtifactId { get; }

    public string? BundleId { get; }

    public string? TerminalReasonCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool IsNonRetryable => Phase3TransitionGuards.IsRunNonRetryable(_status, HasCrossedStartCommit);

    public Phase3TransitionResult TryTransition(
        RecordingRunStatus next,
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
        if (next == RecordingRunStatus.StartCommitted)
        {
            HasCrossedStartCommit = true;
        }

        _status = next;
        TerminalReasonCode = candidateReason;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public bool TryTransition(RecordingRunStatus next, out string reasonCode)
    {
        var result = TryTransition(next, null, null);
        reasonCode = result.ReasonCode;
        return result.Succeeded;
    }

    private static bool RequiresAbnormalTerminalReason(RecordingRunStatus status) => status is
        RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed;

    private static void ValidateSnapshot(RecordingRunStatus status, bool hasCrossedStartCommit, string? terminalReasonCode, long version)
    {
        var requiresCommit = status is RecordingRunStatus.StartCommitted or RecordingRunStatus.Recording or
            RecordingRunStatus.StartedUnknown or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady or
            RecordingRunStatus.Settled or RecordingRunStatus.SessionInterrupted;
        Phase3Validation.Relation(!requiresCommit || hasCrossedStartCommit, "start_commit_required", "This run state requires a committed start.");
        Phase3Validation.Relation(status is not (RecordingRunStatus.Created or RecordingRunStatus.Preparing) || !hasCrossedStartCommit,
            "invalid_start_commit_flag", "A pre-commit run state must not claim to have crossed start commit.");
        Phase3Validation.Relation(Phase3TransitionGuards.IsTerminal(status) || terminalReasonCode is null,
            "terminal_reason_on_nonterminal", "non-terminal runs must not have a terminal reason.");
        Phase3Validation.Relation(!RequiresAbnormalTerminalReason(status) || terminalReasonCode is not null,
            "terminal_reason_required", "abnormal terminal runs require a terminal reason.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
    }
}
