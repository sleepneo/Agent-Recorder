using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// A complete, transaction-consistent persisted execution view. Persistence
/// creates this internal value only after rehydrating and validating every
/// aggregate in one SQLite snapshot.
/// </summary>
internal sealed class StandingLeaseExecutionSnapshot
{
    internal StandingLeaseExecutionSnapshot(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        AuthorizedFixedRegionScope scope,
        RecordingRun run,
        LeaseUse use)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Use = use ?? throw new ArgumentNullException(nameof(use));
    }

    internal PlanDefinition Plan { get; }

    internal PlanOccurrence Occurrence { get; }

    internal ConsentLease Lease { get; }

    internal AuthorizedFixedRegionScope Scope { get; }

    internal RecordingRun Run { get; }

    internal LeaseUse Use { get; }
}
