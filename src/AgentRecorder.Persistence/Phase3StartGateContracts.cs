using AgentRecorder.Core.Automation;
using AgentRecorder.Capture;

namespace AgentRecorder.Persistence;

public interface IPhase3StartGateTransaction
{
    Phase3StartGateCommitResult Commit(Phase3StartGateRequest request);
}

/// <summary>
/// Immutable input for the single-use, fixed-scope Phase 3 start gate.
/// Validation is performed by the transaction before it opens a database write transaction.
/// </summary>
public sealed record Phase3StartGateRequest(
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string ScopeId,
    string ScopeDigest,
    string RunId,
    string LeaseUseId,
    long ExpectedPlanVersion,
    long ExpectedOccurrenceVersion,
    long ExpectedLeaseVersion,
    TimeSpan ReservedDuration,
    DateTimeOffset CommitAtUtc);

public enum Phase3StartGateCommitStatus
{
    Committed,
    AlreadyCommitted,
}

/// <summary>
/// The only successful outcomes of the start-gate transaction.
/// It contains stable business identifiers only; no proof, media, or provider data.
/// </summary>
public sealed record Phase3StartGateCommitResult(
    Phase3StartGateCommitStatus Status,
    string RunId,
    string LeaseUseId)
{
    /// <summary>
    /// Trusted, process-local first-commit hand-off. It is internal so the
    /// public result remains a business DTO and cannot be a proof API surface.
    /// </summary>
    internal StandingLeaseUseProof? FirstCommitProof { get; init; }

    public string Code => Status switch
    {
        Phase3StartGateCommitStatus.Committed => "committed",
        Phase3StartGateCommitStatus.AlreadyCommitted => "already_committed",
        _ => throw new ArgumentOutOfRangeException(nameof(Status)),
    };

    public bool IsAlreadyCommitted => Status == Phase3StartGateCommitStatus.AlreadyCommitted;
}
