using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// A process-local, one-time handoff from a committed standing authorization
/// to the execution bridge. The consumed proof is held privately and is never
/// exposed as a public or serializable ticket field.
/// </summary>
internal sealed class StandingLeaseCaptureExecutionTicket
{
    private readonly StandingLeaseCaptureSpecification _specification;
    private readonly AuthorizedFixedRegionScope _scope;
    private readonly StandingLeaseUseProof _consumedProof;
    private int _claimState;

    private StandingLeaseCaptureExecutionTicket(
        StandingLeaseCaptureSpecification specification,
        StandingLeaseCaptureAuthorization authorization)
    {
        _specification = specification;
        _scope = authorization.Scope;
        _consumedProof = authorization.GetConsumedProofForExecution();
    }

    internal StandingLeaseCaptureSpecification Specification => _specification;

    internal AuthorizedFixedRegionScope Scope => _scope;

    internal bool IsProofConsumed => _consumedProof.IsConsumed;

    internal CaptureAuthorizationProof GetConsumedProofForBackendStart() => _consumedProof;

    internal static bool TryCreate(
        StandingLeaseCaptureAuthorization? authorization,
        StandingLeaseCaptureSpecification? specification,
        out StandingLeaseCaptureExecutionTicket? ticket,
        out string failureReason)
    {
        ticket = null;
        failureReason = "standing_execution_authorization_missing";
        if (authorization is null || specification is null)
            return false;

        if (!authorization.IsProofConsumed)
        {
            failureReason = "standing_execution_proof_not_consumed";
            return false;
        }

        if (!authorization.HasValidConsumedProofBinding)
        {
            failureReason = "standing_execution_authorization_invalid";
            return false;
        }

        bool matches;
        try
        {
            matches = specification.MatchesAuthorization(authorization);
        }
        catch
        {
            matches = false;
        }

        if (!matches)
        {
            failureReason = "standing_execution_ticket_mismatch";
            return false;
        }

        var proof = authorization.GetConsumedProofForExecution();
        if (!proof.IsConsumed)
        {
            failureReason = "standing_execution_proof_not_consumed";
            return false;
        }

        ticket = new StandingLeaseCaptureExecutionTicket(specification, authorization);
        failureReason = "";
        return true;
    }

    internal bool TryClaim(out string failureReason)
    {
        if (Interlocked.CompareExchange(ref _claimState, 1, 0) != 0)
        {
            failureReason = "standing_execution_already_claimed";
            return false;
        }

        failureReason = "";
        return true;
    }
}

internal enum StandingLeaseCaptureExecutionStatus
{
    Started,
    Rejected,
    Failed,
    Cancelled,
}

/// <summary>
/// Internal result of one claimed standing execution attempt. A successful
/// result retains the started backend for the future lifecycle bridge; failed
/// results never expose a retry path.
/// </summary>
internal sealed class StandingLeaseCaptureExecutionResult
{
    private StandingLeaseCaptureExecutionResult(
        StandingLeaseCaptureExecutionStatus status,
        string reason,
        ICaptureBackend? backend)
    {
        Status = status;
        Reason = reason;
        Backend = backend;
    }

    internal StandingLeaseCaptureExecutionStatus Status { get; }

    internal string Reason { get; }

    internal ICaptureBackend? Backend { get; }

    internal static StandingLeaseCaptureExecutionResult Started(ICaptureBackend backend) =>
        new(StandingLeaseCaptureExecutionStatus.Started, "", backend);

    internal static StandingLeaseCaptureExecutionResult Rejected(string reason) =>
        new(StandingLeaseCaptureExecutionStatus.Rejected, reason, null);

    internal static StandingLeaseCaptureExecutionResult Failed(string reason) =>
        new(StandingLeaseCaptureExecutionStatus.Failed, reason, null);

    internal static StandingLeaseCaptureExecutionResult Cancelled() =>
        new(StandingLeaseCaptureExecutionStatus.Cancelled, "standing_execution_cancelled", null);
}
