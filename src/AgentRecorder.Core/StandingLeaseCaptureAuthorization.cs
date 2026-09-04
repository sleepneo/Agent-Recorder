using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Immutable internal authorization context produced only after a standing
/// proof has been validated and consumed. The consumed proof is retained
/// privately so this context cannot expose its nonce or other proof secret.
/// </summary>
internal sealed class StandingLeaseCaptureAuthorization
{
    private readonly StandingLeaseUseProof _consumedProof;
    private readonly Guid _specificationBindingId = Guid.NewGuid();

    internal StandingLeaseCaptureAuthorization(
        StandingLeaseUseProof consumedProof,
        StandingLeaseExecutionSnapshot snapshot,
        DateTimeOffset consumedAtUtc)
    {
        _consumedProof = consumedProof ?? throw new ArgumentNullException(nameof(consumedProof));
        ArgumentNullException.ThrowIfNull(snapshot);

        Scope = snapshot.Scope;
        RunId = snapshot.Run.Id;
        LeaseId = snapshot.Lease.Id;
        LeaseUseId = snapshot.Use.Id;
        ConsumedAtUtc = consumedAtUtc;
    }

    internal AuthorizedFixedRegionScope Scope { get; }

    internal string RunId { get; }

    internal string LeaseId { get; }

    internal string LeaseUseId { get; }

    internal string OutputFilePath => Scope.OutputFilePath;

    internal AuthorizedPhysicalRectangle VirtualScreenRegion => Scope.VirtualScreenRegion;

    internal AuthorizedCaptureBackend Backend => Scope.Backend;

    internal AuthorizedAudioMode AudioMode => Scope.AudioMode;

    internal AuthorizedWakePolicy WakePolicy => Scope.WakePolicy;

    internal AuthorizedDesktopRequirement DesktopRequirement => Scope.DesktopRequirement;

    internal TimeSpan ReservedDuration => Scope.ReservedDuration;

    internal DateTimeOffset ConsumedAtUtc { get; }

    internal bool IsProofConsumed => _consumedProof.IsConsumed;

    /// <summary>
    /// Keeps the specification projection bound to the exact proof and
    /// snapshot identity that produced this context without exposing proof
    /// secrets or the proof object itself.
    /// </summary>
    internal bool HasValidConsumedProofBinding =>
        _consumedProof.IsConsumed &&
        string.Equals(_consumedProof.RunId, RunId, StringComparison.Ordinal) &&
        string.Equals(_consumedProof.LeaseId, LeaseId, StringComparison.Ordinal) &&
        string.Equals(_consumedProof.LeaseUseId, LeaseUseId, StringComparison.Ordinal) &&
        string.Equals(_consumedProof.ScopeDigest, Scope.ScopeDigest, StringComparison.Ordinal) &&
        string.Equals(_consumedProof.CurrentUserSid, Scope.CurrentUserSid, StringComparison.Ordinal) &&
        string.Equals(_consumedProof.SessionBinding, Scope.SessionBinding, StringComparison.Ordinal) &&
        _consumedProof.MaxDuration == Scope.ReservedDuration;

    /// <summary>
    /// Internal bridge handoff only. The proof object and its nonce never leave
    /// the process-local authorization path through a public property.
    /// </summary>
    internal StandingLeaseUseProof GetConsumedProofForExecution() => _consumedProof;

    internal Guid SpecificationBindingId => _specificationBindingId;
}
