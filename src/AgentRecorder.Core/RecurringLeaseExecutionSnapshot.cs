using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Complete, process-local recurring execution snapshot. Persistence creates
/// this value while holding its immediate transaction; Core never reloads any
/// part of it from a second connection.
/// </summary>
internal sealed class RecurringLeaseExecutionSnapshot
{
    internal RecurringLeaseExecutionSnapshot(
        PlanDefinition plan,
        RecurringConsentLease lease,
        RecurringFixedRegionProfileVersion profile,
        RecurringLeaseLocalApprovalEvidence approval,
        RecurringLeaseExecutionSafetyEvidence safety,
        PlanOccurrence occurrence,
        RecurringOccurrenceExecutionSpecification specification,
        RecordingRun run,
        LeaseUse use,
        IReadOnlyList<RecurringLeaseUseAccountingEntry> leaseEntries,
        RecurringLeaseQuotaSnapshot quota,
        DateTimeOffset? profileBindingBoundAtUtc = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
        Safety = safety ?? throw new ArgumentNullException(nameof(safety));
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Use = use ?? throw new ArgumentNullException(nameof(use));
        ArgumentNullException.ThrowIfNull(leaseEntries);
        LeaseEntries = Array.AsReadOnly(leaseEntries.ToArray());
        Quota = quota ?? throw new ArgumentNullException(nameof(quota));
        ProfileBindingBoundAtUtc = profileBindingBoundAtUtc;
    }

    internal PlanDefinition Plan { get; }

    internal RecurringFixedRegionProfileVersion Profile { get; }

    internal RecurringLeaseLocalApprovalEvidence Approval { get; }

    internal RecurringLeaseExecutionSafetyEvidence Safety { get; }

    internal PlanOccurrence Occurrence { get; }

    internal RecurringOccurrenceExecutionSpecification Specification { get; }

    internal RecordingRun Run { get; }

    internal LeaseUse Use { get; }

    internal IReadOnlyList<RecurringLeaseUseAccountingEntry> LeaseEntries { get; }

    internal RecurringLeaseQuotaSnapshot Quota { get; }

    /// <summary>
    /// Exact immutable profile-binding timestamp read with the snapshot. A
    /// nullable value keeps process-local synthetic snapshots source
    /// compatible; persistence-created snapshots always carry it.
    /// </summary>
    internal DateTimeOffset? ProfileBindingBoundAtUtc { get; }

    internal RecurringConsentLease Lease { get; }
}
