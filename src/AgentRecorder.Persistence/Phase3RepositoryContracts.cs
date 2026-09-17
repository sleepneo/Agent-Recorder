using AgentRecorder.Core.Automation;
using AgentRecorder.Core;

namespace AgentRecorder.Persistence;

public interface IPlanDefinitionRepository
{
    void Insert(PlanDefinition snapshot);

    PlanDefinition Get(string id);

    void Update(PlanDefinition snapshot, long expectedVersion);
}

public interface IPlanOccurrenceRepository
{
    void Insert(PlanOccurrence snapshot);

    PlanOccurrence Get(string id);

    void Update(PlanOccurrence snapshot, long expectedVersion);
}

public interface IRecordingRunRepository
{
    void Insert(RecordingRun snapshot);

    RecordingRun Get(string id);

    void Update(RecordingRun snapshot, long expectedVersion);
}

public interface IConsentLeaseRepository
{
    void Insert(ConsentLease snapshot);

    ConsentLease Get(string id);

    void Update(ConsentLease snapshot, long expectedVersion);
}

public interface ILeaseUseRepository
{
    void Insert(LeaseUse snapshot);

    LeaseUse Get(string id);

    void Update(LeaseUse snapshot, long expectedVersion);
}

public interface IAuthorizedCaptureScopeRepository
{
    void Insert(AuthorizedFixedRegionScope snapshot);

    AuthorizedFixedRegionScope GetById(string id);

    AuthorizedFixedRegionScope Get(string id);

    AuthorizedFixedRegionScope GetByLeaseId(string leaseId);

    AuthorizedFixedRegionScope GetByOccurrence(string occurrenceId);
}

public interface IRecurringPlanProfileBindingRepository
{
    RecurringPlanProfileBinding Bind(string planId, ProfileRef profileRef, DateTimeOffset boundAtUtc);

    RecurringPlanProfileBinding Get(string planId);

    RecurringPlanProfileBinding? TryGet(string planId);

    IReadOnlyList<string> ListReferencingPlanIds(
        ProfileRef profileRef,
        string? beforePlanIdExclusive = null,
        int limit = 100);
}

public interface IRecurringConsentLeaseRepository
{
    void InsertPending(RecurringConsentLease lease);

    RecurringConsentLease Get(string leaseId);

    RecurringConsentLease? TryGet(string leaseId);

    IReadOnlyList<RecurringConsentLease> ListByPlan(
        string planId,
        string? afterLeaseIdExclusive = null,
        int limit = 100);
}

public interface IRecurringLeaseLocalApprovalEvidenceReader
{
    RecurringLeaseLocalApprovalEvidence? TryGetByLease(string leaseId);
}

public interface IRecurringLeaseUseAccountingReader
{
    IReadOnlyList<RecurringLeaseUseAccountingEntry> ListByLease(string leaseId);

    RecurringLeaseUseAccountingEntry? TryGetByOccurrence(string occurrenceIdentity);
}
