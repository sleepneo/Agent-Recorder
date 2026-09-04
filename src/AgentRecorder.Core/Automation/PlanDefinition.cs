namespace AgentRecorder.Core.Automation;

public sealed class PlanDefinition
{
    private PlanDefinitionStatus _status;

    public PlanDefinition(
        string id,
        bool isOneTime,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null)
    {
        Id = Phase3Validation.RequiredId(id, nameof(id));
        CreatedAtUtc = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = Phase3Validation.Utc(updatedAtUtc ?? createdAtUtc, nameof(updatedAtUtc));
        Phase3Validation.Relation(UpdatedAtUtc >= CreatedAtUtc, "non_monotonic_time", "updatedAtUtc must not precede createdAtUtc.");
        IsOneTime = isOneTime;
        _status = PlanDefinitionStatus.Draft;
    }

    internal static PlanDefinition Rehydrate(
        string id,
        bool isOneTime,
        PlanDefinitionStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        ValidateRehydration(status, version);
        var plan = new PlanDefinition(id, isOneTime, createdAtUtc, updatedAtUtc)
        {
            _status = status,
            Version = version,
        };
        return plan;
    }

    public string Id { get; }

    public bool IsOneTime { get; }

    public bool IsPeriodic => !IsOneTime;

    public PlanDefinitionStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public Phase3TransitionResult TryMarkUpdated(DateTimeOffset updatedAtUtc)
    {
        var updateTime = Phase3Validation.Utc(updatedAtUtc, nameof(updatedAtUtc));
        if (updateTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        if (updateTime == UpdatedAtUtc)
        {
            return Phase3TransitionResult.Idempotent;
        }

        UpdatedAtUtc = updateTime;
        Version++;
        return Phase3TransitionResult.Success("updated");
    }

    public Phase3TransitionResult TryTransition(PlanDefinitionStatus next, DateTimeOffset? transitionedAtUtc = null)
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

    public bool TryTransition(PlanDefinitionStatus next, out string reasonCode)
    {
        var result = TryTransition(next, null);
        reasonCode = result.ReasonCode;
        return result.Succeeded;
    }

    private static void ValidateRehydration(PlanDefinitionStatus status, long version)
    {
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known PlanDefinitionStatus.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
    }
}
