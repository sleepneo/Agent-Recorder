using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Internal hand-off from the trusted Phase 3 persistence flow to the Core
/// standing-proof issuer. It is never a public request/result or a collection
/// of caller-supplied identifiers.
/// </summary>
internal sealed class StandingLeaseUseProofIssuanceReceipt
{
    internal StandingLeaseUseProofIssuanceReceipt(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        AuthorizedFixedRegionScope scope,
        RecordingRun run,
        LeaseUse use,
        DateTimeOffset committedAtUtc)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Use = use ?? throw new ArgumentNullException(nameof(use));
        CommittedAtUtc = committedAtUtc;
    }

    internal PlanDefinition Plan { get; }

    internal PlanOccurrence Occurrence { get; }

    internal ConsentLease Lease { get; }

    internal AuthorizedFixedRegionScope Scope { get; }

    internal RecordingRun Run { get; }

    internal LeaseUse Use { get; }

    internal DateTimeOffset CommittedAtUtc { get; }
}
