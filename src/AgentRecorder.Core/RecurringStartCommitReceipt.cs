using AgentRecorder.Core.Automation;
using System.Linq;

namespace AgentRecorder.Core;

/// <summary>
/// Internal hand-off from the recurring persistence start-commit transaction
/// to the future Core proof issuer. It is immutable, Core-only, and cannot be
/// constructed from an API or JSON request.
/// </summary>
internal sealed class RecurringStartCommitReceipt
{
    internal RecurringStartCommitReceipt(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        RecurringConsentLease lease,
        RecurringOccurrenceExecutionSpecification specification,
        RecordingRun run,
        LeaseUse use,
        RecurringLeaseLocalApprovalEvidence approval,
        DateTimeOffset committedAtUtc,
        RecurringLeaseQuotaSnapshot quota,
        IEnumerable<RecurringLeaseUseAccountingEntry> leaseEntries)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Use = use ?? throw new ArgumentNullException(nameof(use));
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
        Quota = quota ?? throw new ArgumentNullException(nameof(quota));
        ArgumentNullException.ThrowIfNull(leaseEntries);
        var copiedLeaseEntries = leaseEntries.ToArray();
        if (copiedLeaseEntries.Any(entry => entry is null))
            throw new ArgumentException("Recurring lease accounting evidence cannot contain null entries.", nameof(leaseEntries));
        LeaseEntries = Array.AsReadOnly(copiedLeaseEntries);
        CommittedAtUtc = committedAtUtc.Offset == TimeSpan.Zero
            ? committedAtUtc
            : throw new Phase3DomainException(
                "recurring_start_commit_receipt_non_utc",
                "The recurring start-commit receipt time must be UTC.");
    }

    internal PlanDefinition Plan { get; }

    internal PlanOccurrence Occurrence { get; }

    internal RecurringConsentLease Lease { get; }

    internal RecurringOccurrenceExecutionSpecification Specification { get; }

    internal RecordingRun Run { get; }

    internal LeaseUse Use { get; }

    internal RecurringLeaseLocalApprovalEvidence Approval { get; }

    /// <summary>
    /// The exact post-commit quota projection used by the transaction. It is
    /// carried only as Core-owned evidence so the issuer can distinguish an
    /// active lease from one exhausted by this committed use without reading
    /// SQLite or trusting a caller supplied status.
    /// </summary>
    internal RecurringLeaseQuotaSnapshot Quota { get; }

    /// <summary>
    /// The complete lease accounting projection read back before the durable
    /// start-commit was published. The constructor copies and freezes the
    /// sequence so an internal caller cannot replace entries after receipt
    /// construction.
    /// </summary>
    internal IReadOnlyList<RecurringLeaseUseAccountingEntry> LeaseEntries { get; }

    internal DateTimeOffset CommittedAtUtc { get; }
}
