namespace AgentRecorder.Core.Automation;

public sealed class LeaseUse
{
    private LeaseUseStatus _status;

    public LeaseUse(
        string id,
        string leaseId,
        string occurrenceId,
        string runId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? updatedAtUtc = null)
        : this(id, leaseId, occurrenceId, runId, createdAtUtc, LeaseUseStatus.Available, 0, TimeSpan.Zero, null,
            updatedAtUtc ?? createdAtUtc, 0)
    {
    }

    private LeaseUse(
        string id,
        string leaseId,
        string occurrenceId,
        string runId,
        DateTimeOffset createdAtUtc,
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        Id = Phase3Validation.RequiredId(id, nameof(id));
        LeaseId = Phase3Validation.RequiredId(leaseId, nameof(leaseId));
        OccurrenceId = Phase3Validation.RequiredId(occurrenceId, nameof(occurrenceId));
        RunId = Phase3Validation.RequiredId(runId, nameof(runId));
        CreatedAtUtc = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = Phase3Validation.Utc(updatedAtUtc, nameof(updatedAtUtc));
        Phase3Validation.Relation(UpdatedAtUtc >= CreatedAtUtc, "non_monotonic_time", "updatedAtUtc must not precede createdAtUtc.");
        Phase3Validation.Relation(Enum.IsDefined(status), "unknown_state", "status must be a known LeaseUseStatus.");
        Phase3Validation.Relation(version >= 0, "negative_version", "version must not be negative.");
        ReservedUseCount = Phase3Validation.NonNegative(reservedUseCount, nameof(reservedUseCount));
        ReservedDuration = Phase3Validation.NonNegative(reservedDuration, nameof(reservedDuration));
        ActualSettledDuration = actualSettledDuration is null ? null : Phase3Validation.NonNegative(actualSettledDuration.Value, nameof(actualSettledDuration));
        ValidateSnapshot(status, ReservedUseCount, ReservedDuration, ActualSettledDuration);
        _status = status;
        Version = version;
    }

    internal static LeaseUse Rehydrate(
        string id,
        string leaseId,
        string occurrenceId,
        string runId,
        DateTimeOffset createdAtUtc,
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration,
        DateTimeOffset updatedAtUtc,
        long version)
        => new(id, leaseId, occurrenceId, runId, createdAtUtc, status, reservedUseCount, reservedDuration, actualSettledDuration, updatedAtUtc, version);

    public static LeaseUse CreateFor(
        ConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        string useId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(run);
        Phase3Validation.Relation(
            string.Equals(lease.PlanId, occurrence.PlanId, StringComparison.Ordinal) &&
            string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal) &&
            string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal),
            "inconsistent_relation",
            "Lease, occurrence, and run IDs must describe the same execution chain.");
        return new LeaseUse(useId, lease.Id, occurrence.Id, run.Id, createdAtUtc);
    }

    public string Id { get; }

    public string LeaseId { get; }

    public string OccurrenceId { get; }

    public string RunId { get; }

    public LeaseUseStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public int ReservedUseCount { get; private set; }

    public TimeSpan ReservedDuration { get; private set; }

    public TimeSpan? ActualSettledDuration { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool IsQuotaConsumed => Phase3TransitionGuards.IsUseQuotaConsumed(_status);

    public Phase3TransitionResult TryReserve(
        int reservedUseCount,
        TimeSpan reservedDuration,
        DateTimeOffset? reservedAtUtc = null)
    {
        var count = Phase3Validation.Positive(reservedUseCount, nameof(reservedUseCount));
        var duration = Phase3Validation.Positive(reservedDuration, nameof(reservedDuration));

        if (_status == LeaseUseStatus.Reserved)
        {
            return ReservedUseCount == count && ReservedDuration == duration
                ? Phase3TransitionResult.Idempotent
                : Phase3TransitionResult.Failure("reservation_mismatch");
        }

        if (_status != LeaseUseStatus.Available)
        {
            return Phase3TransitionResult.Failure("reservation_not_available");
        }

        if (!Phase3TransitionGuards.CanTransition(_status, LeaseUseStatus.Reserved, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        var transitionTime = Phase3Validation.Utc(reservedAtUtc ?? DateTimeOffset.UtcNow, nameof(reservedAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        // All values are validated before this single domain commit.
        ReservedUseCount = count;
        ReservedDuration = duration;
        ActualSettledDuration = null;
        _status = LeaseUseStatus.Reserved;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public Phase3TransitionResult TryReleaseReservation(DateTimeOffset? releasedAtUtc = null)
    {
        if (_status == LeaseUseStatus.Available)
        {
            return Phase3TransitionResult.Idempotent;
        }

        if (_status != LeaseUseStatus.Reserved)
        {
            return Phase3TransitionResult.Failure("reservation_not_active");
        }

        if (!Phase3TransitionGuards.CanTransition(_status, LeaseUseStatus.Available, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        var transitionTime = Phase3Validation.Utc(releasedAtUtc ?? DateTimeOffset.UtcNow, nameof(releasedAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        // Release clears the reservation in the same commit as the state change.
        ReservedUseCount = 0;
        ReservedDuration = TimeSpan.Zero;
        ActualSettledDuration = null;
        _status = LeaseUseStatus.Available;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success("reservation_released");
    }

    public Phase3TransitionResult TrySettle(TimeSpan actualSettledDuration, DateTimeOffset? settledAtUtc = null)
    {
        var duration = Phase3Validation.NonNegative(actualSettledDuration, nameof(actualSettledDuration));
        if (_status == LeaseUseStatus.Settled)
        {
            return ActualSettledDuration == duration
                ? Phase3TransitionResult.Idempotent
                : Phase3TransitionResult.Failure("settlement_mismatch");
        }

        if (_status != LeaseUseStatus.Consumed)
        {
            return Phase3TransitionResult.Failure("settlement_requires_consumed");
        }

        if (!Phase3TransitionGuards.CanTransition(_status, LeaseUseStatus.Settled, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        var transitionTime = Phase3Validation.Utc(settledAtUtc ?? DateTimeOffset.UtcNow, nameof(settledAtUtc));
        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure("non_monotonic_time");
        }

        // The duration and terminal state are committed together.
        ActualSettledDuration = duration;
        _status = LeaseUseStatus.Settled;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    public Phase3TransitionResult TryTransition(LeaseUseStatus next, DateTimeOffset? transitionedAtUtc = null)
    {
        if (!Phase3TransitionGuards.CanTransition(_status, next, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        if (_status == next)
        {
            return Phase3TransitionResult.Idempotent;
        }

        if (_status == LeaseUseStatus.Available && next == LeaseUseStatus.Reserved)
        {
            return Phase3TransitionResult.Failure("reservation_required");
        }

        if (_status == LeaseUseStatus.Reserved && next == LeaseUseStatus.Available)
        {
            return Phase3TransitionResult.Failure("release_required");
        }

        if (_status == LeaseUseStatus.Consumed && next == LeaseUseStatus.Settled)
        {
            return Phase3TransitionResult.Failure("settlement_required");
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

    public bool TryTransition(LeaseUseStatus next, out string reasonCode)
    {
        var result = TryTransition(next, null);
        reasonCode = result.ReasonCode;
        return result.Succeeded;
    }

    private static void ValidateSnapshot(LeaseUseStatus status, int reservedUseCount, TimeSpan reservedDuration, TimeSpan? actualSettledDuration)
    {
        Phase3Validation.Relation(status != LeaseUseStatus.Available ||
            reservedUseCount == 0 && reservedDuration == TimeSpan.Zero && actualSettledDuration is null,
            "inconsistent_lease_use",
            "available uses must have no reservation or settlement data.");
        var requiresReservationEvidence = status is LeaseUseStatus.Reserved or LeaseUseStatus.StartCommitted or
            LeaseUseStatus.Consumed or LeaseUseStatus.Settled or LeaseUseStatus.StartedUnknown;
        Phase3Validation.Relation(!requiresReservationEvidence ||
            reservedUseCount > 0 && reservedDuration > TimeSpan.Zero,
            "reservation_evidence_required",
            "post-commit uses must retain a positive reservation.");
        Phase3Validation.Relation(status != LeaseUseStatus.Reserved || actualSettledDuration is null,
            "invalid_reservation",
            "reserved uses must not have settlement data.");
        Phase3Validation.Relation(status != LeaseUseStatus.Settled || actualSettledDuration is not null,
            "settlement_duration_required",
            "settled uses must have an actual settled duration.");
        Phase3Validation.Relation(status == LeaseUseStatus.Settled || actualSettledDuration is null,
            "settlement_on_nonterminal",
            "only settled uses may have an actual settled duration.");
    }
}
