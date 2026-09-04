namespace AgentRecorder.Core.Automation;

public sealed class ConsentLease
{
    private ConsentLeaseStatus _status;

    public ConsentLease(
        string id,
        string planId,
        string occurrenceId,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        int maxUses,
        TimeSpan maxDuration,
        DateTimeOffset? updatedAtUtc = null)
        : this(id, planId, occurrenceId, validFromUtc, validUntilUtc, maxUses, maxDuration,
            ConsentLeaseStatus.Pending, updatedAtUtc ?? validFromUtc, 0)
    {
    }

    private ConsentLease(
        string id,
        string planId,
        string occurrenceId,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        int maxUses,
        TimeSpan maxDuration,
        ConsentLeaseStatus status,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        Id = Phase3Validation.RequiredId(id, nameof(id));
        PlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        OccurrenceId = Phase3Validation.RequiredId(occurrenceId, nameof(occurrenceId));
        ValidFromUtc = Phase3Validation.Utc(validFromUtc, nameof(validFromUtc));
        ValidUntilUtc = Phase3Validation.Utc(validUntilUtc, nameof(validUntilUtc));
        Phase3Validation.Window(ValidFromUtc, ValidUntilUtc, nameof(validFromUtc));
        MaxUses = Phase3Validation.Positive(maxUses, nameof(maxUses));
        MaxDuration = Phase3Validation.Positive(maxDuration, nameof(maxDuration));
        UpdatedAtUtc = Phase3Validation.Utc(updatedAtUtc, nameof(updatedAtUtc));
        Phase3Validation.Relation(UpdatedAtUtc >= ValidFromUtc, "non_monotonic_time", "updatedAtUtc must not precede validFromUtc.");
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known ConsentLeaseStatus.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
        _status = status;
        Version = version;
    }

    internal static ConsentLease Rehydrate(
        string id,
        string planId,
        string occurrenceId,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        int maxUses,
        TimeSpan maxDuration,
        ConsentLeaseStatus status,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known ConsentLeaseStatus.");
        return new ConsentLease(id, planId, occurrenceId, validFromUtc, validUntilUtc, maxUses, maxDuration, status, updatedAtUtc, version);
    }

    public static ConsentLease CreateFor(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        int maxUses,
        TimeSpan maxDuration,
        string leaseId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(occurrence);
        Phase3Validation.Relation(
            string.Equals(plan.Id, occurrence.PlanId, StringComparison.Ordinal),
            "inconsistent_relation",
            "The occurrence must belong to the supplied plan.");
        return new ConsentLease(leaseId, plan.Id, occurrence.Id, validFromUtc, validUntilUtc, maxUses, maxDuration);
    }

    public string Id { get; }

    public string PlanId { get; }

    public string OccurrenceId { get; }

    public ConsentLeaseStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ValidUntilUtc { get; }

    public int MaxUses { get; }

    public TimeSpan MaxDuration { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool IsExpiredAt(DateTimeOffset atUtc)
    {
        var time = Phase3Validation.Utc(atUtc, nameof(atUtc));
        return time >= ValidUntilUtc;
    }

    public Phase3TransitionResult TryTransition(ConsentLeaseStatus next, DateTimeOffset? transitionedAtUtc = null)
    {
        if (!Phase3TransitionGuards.CanTransition(_status, next, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        if (_status == next)
        {
            return Phase3TransitionResult.Idempotent;
        }

        var transitionTime = Phase3Validation.Utc(transitionedAtUtc ?? DateTimeOffset.UtcNow, nameof(transitionedAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        _status = next;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public bool TryTransition(ConsentLeaseStatus next, out string reasonCode)
    {
        var result = TryTransition(next, null);
        reasonCode = result.ReasonCode;
        return result.Succeeded;
    }
}
