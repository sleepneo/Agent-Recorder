namespace AgentRecorder.Core.Automation;

public static class RecurringLeaseQuotaReasonCodes
{
    public const string Available = "recurring_lease_quota_available";
    public const string Pending = "recurring_lease_pending";
    public const string NotActive = "recurring_lease_not_active";
    public const string Rejected = "recurring_lease_rejected";
    public const string Revoked = "recurring_lease_revoked";
    public const string Expired = "recurring_lease_expired";
    public const string Exhausted = "recurring_lease_exhausted";
    public const string BeforeValidity = "recurring_lease_before_validity";
    public const string ConfigurationMismatch = "recurring_lease_configuration_mismatch";
    public const string OccurrenceWindowInvalid = "recurring_lease_occurrence_window_invalid";
    public const string OccurrenceBeforeValidity = "recurring_lease_occurrence_before_validity";
    public const string OccurrenceAfterValidity = "recurring_lease_occurrence_after_validity";
    public const string OccurrenceAfterAuthorizedLatestEnd = "recurring_lease_occurrence_after_authorized_latest_end";
    public const string DurationMismatch = "recurring_lease_duration_mismatch";
    public const string OccurrenceAlreadyClaimed = "recurring_lease_occurrence_already_claimed";
    public const string RunInFlight = "recurring_lease_run_in_flight";
    public const string UseQuotaExceeded = "recurring_lease_use_quota_exceeded";
    public const string CumulativeDurationExceeded = "recurring_lease_cumulative_duration_exceeded";
    public const string QuotaExhausted = "recurring_lease_quota_exhausted";
    public const string AccountingInvalid = "recurring_lease_accounting_invalid";
    public const string AccountingOverflow = "recurring_lease_accounting_overflow";
    public const string DuplicateUseId = "recurring_lease_duplicate_use_id";
    public const string DuplicateRunId = "recurring_lease_duplicate_run_id";
    public const string DuplicateOccurrence = "recurring_lease_duplicate_occurrence";
    public const string UnknownState = "recurring_lease_accounting_unknown_state";
    public const string ReservedCountInvalid = "recurring_lease_reserved_count_invalid";
    public const string ReservedDurationInvalid = "recurring_lease_reserved_duration_invalid";
    public const string SettledDurationInvalid = "recurring_lease_settled_duration_invalid";
    public const string LeaseRelationInvalid = "recurring_lease_accounting_lease_relation_invalid";
    public const string OccurrenceIdentityInvalid = "recurring_lease_occurrence_identity_invalid";
    public const string RequestDurationInvalid = "recurring_lease_request_duration_invalid";
    public const string RequestTimeInvalid = "recurring_lease_request_time_invalid";
    public const string CurrentTimeInvalid = "recurring_lease_current_time_invalid";
    public const string OccurrencePlannedEndMismatch = "recurring_lease_occurrence_planned_end_mismatch";
    public const string OccurrencePlannedEndOverflow = "recurring_lease_occurrence_planned_end_overflow";
}

/// <summary>
/// Immutable facts supplied to a future recurring occurrence reservation
/// transaction. It has no occurrence/run persistence side effects.
/// </summary>
public sealed class RecurringLeaseOccurrenceRequest
{
    public RecurringLeaseOccurrenceRequest(
        string occurrenceIdentity,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan requestedDuration)
    {
        OccurrenceIdentity = RequiredIdentity(occurrenceIdentity);
        ConfigurationRef = configurationRef ?? throw new Phase3DomainException(
            RecurringLeaseQuotaReasonCodes.ConfigurationMismatch,
            "The recurring occurrence configuration reference is required.");
        ScheduledStartUtc = ValidateUtc(scheduledStartUtc, nameof(scheduledStartUtc));
        LatestStartUtc = ValidateUtc(latestStartUtc, nameof(latestStartUtc));
        PlannedEndUtc = ValidateUtc(plannedEndUtc, nameof(plannedEndUtc));
        if (requestedDuration <= TimeSpan.Zero)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.RequestDurationInvalid,
                "The recurring occurrence requested duration must be positive.");
        }

        RequestedDuration = requestedDuration;

        DateTimeOffset expectedPlannedEndUtc;
        try
        {
            expectedPlannedEndUtc = LatestStartUtc.Add(RequestedDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.OccurrencePlannedEndOverflow,
                "The recurring occurrence planned end calculation overflowed.");
        }

        if (PlannedEndUtc != expectedPlannedEndUtc)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.OccurrencePlannedEndMismatch,
                "The recurring occurrence planned end must equal latest start plus requested duration exactly.");
        }

        if (ScheduledStartUtc > LatestStartUtc || LatestStartUtc >= PlannedEndUtc)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.OccurrenceWindowInvalid,
                "The recurring occurrence window must satisfy scheduled start <= latest start < planned end.");
        }
    }

    public static RecurringLeaseOccurrenceRequest CreateFor(
        RecurringConsentLease lease,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan requestedDuration)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            lease.ConfigurationRef,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            requestedDuration);
    }

    public string OccurrenceIdentity { get; }

    public string OccurrenceId => OccurrenceIdentity;

    public string PlanId => ConfigurationRef.PlanId;

    public long ScheduleRevision => ConfigurationRef.ScheduleRevision;

    public string ScheduleDigest => ConfigurationRef.ScheduleDigest;

    public string TimeZoneRulesDigest => ConfigurationRef.TimeZoneRulesDigest;

    public ProfileRef ProfileRef => ConfigurationRef.ProfileRef;

    public RecurringPlanConfigurationRef ConfigurationRef { get; }

    public RecurringPlanConfigurationRef Configuration => ConfigurationRef;

    public DateTimeOffset ScheduledStartUtc { get; }

    public DateTimeOffset LatestStartUtc { get; }

    public DateTimeOffset PlannedEndUtc { get; }

    public TimeSpan RequestedDuration { get; }

    private static string RequiredIdentity(string value)
    {
        try
        {
            return Phase3Validation.RequiredId(value, nameof(OccurrenceIdentity));
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.OccurrenceIdentityInvalid, exception.Message);
        }
    }

    private static DateTimeOffset ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.RequestTimeInvalid, $"{name} must use UTC.");
        }

        return value;
    }
}

/// <summary>
/// Read-only projection of future LeaseUse persistence evidence. It validates
/// state-specific fields without changing the existing one-shot LeaseUse type.
/// </summary>
public sealed class RecurringLeaseUseAccountingEntry
{
    private RecurringLeaseUseAccountingEntry(
        string leaseId,
        string useId,
        string occurrenceIdentity,
        string runId,
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration)
    {
        LeaseId = RequiredId(leaseId, nameof(LeaseId));
        UseId = RequiredId(useId, nameof(UseId));
        OccurrenceIdentity = RequiredId(occurrenceIdentity, nameof(OccurrenceIdentity));
        RunId = RequiredId(runId, nameof(RunId));
        if (!Enum.IsDefined(status))
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.UnknownState, "The recurring accounting LeaseUse status is unknown.");
        }

        if (reservedUseCount is < 0 or > 1)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.ReservedCountInvalid, "A recurring accounting entry reserved use count must be 0 or 1.");
        }

        if (reservedDuration < TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.ReservedDurationInvalid, "A recurring accounting reserved duration must not be negative.");
        }

        if (actualSettledDuration is { } actual && actual < TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.SettledDurationInvalid, "A recurring accounting settled duration must not be negative.");
        }

        ValidateStateEvidence(status, reservedUseCount, reservedDuration, actualSettledDuration);
        Status = status;
        ReservedUseCount = reservedUseCount;
        ReservedDuration = reservedDuration;
        ActualSettledDuration = actualSettledDuration;
    }

    public static RecurringLeaseUseAccountingEntry CreateFor(
        RecurringConsentLease lease,
        string useId,
        string occurrenceIdentity,
        string runId,
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var entry = new RecurringLeaseUseAccountingEntry(
            lease.Id,
            useId,
            occurrenceIdentity,
            runId,
            status,
            reservedUseCount,
            reservedDuration,
            actualSettledDuration);
        entry.ValidateAgainst(lease);
        return entry;
    }

    internal static RecurringLeaseUseAccountingEntry Rehydrate(
        string leaseId,
        string useId,
        string occurrenceIdentity,
        string runId,
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration)
    {
        var entry = new RecurringLeaseUseAccountingEntry(
            leaseId,
            useId,
            occurrenceIdentity,
            runId,
            status,
            reservedUseCount,
            reservedDuration,
            actualSettledDuration);
        return entry;
    }

    public string LeaseId { get; }

    public string UseId { get; }

    public string Id => UseId;

    public string OccurrenceIdentity { get; }

    public string OccurrenceId => OccurrenceIdentity;

    public string RunId { get; }

    public LeaseUseStatus Status { get; }

    public string StatusCode => Phase3StateCodes.ToCode(Status);

    public int ReservedUseCount { get; }

    public TimeSpan ReservedDuration { get; }

    public TimeSpan? ActualSettledDuration { get; }

    public bool IsQuotaConsumed => Phase3TransitionGuards.IsUseQuotaConsumed(Status);

    internal void ValidateAgainst(RecurringConsentLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(LeaseId, lease.LeaseId, StringComparison.Ordinal))
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.LeaseRelationInvalid,
                "A recurring accounting entry points to another lease.");
        }

        if (ReservedUseCount == 1 && ReservedDuration != lease.PerRunDuration)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.ReservedDurationInvalid,
                "A recurring accounting reservation must equal the lease per-run duration exactly.");
        }
    }

    private static string RequiredId(string value, string name)
    {
        try
        {
            return Phase3Validation.RequiredId(value, name);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.AccountingInvalid, exception.Message);
        }
    }

    private static void ValidateStateEvidence(
        LeaseUseStatus status,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualSettledDuration)
    {
        switch (status)
        {
            case LeaseUseStatus.Available:
                Require(reservedUseCount == 0 && reservedDuration == TimeSpan.Zero && actualSettledDuration is null,
                    "available accounting entries must have no reservation or settlement evidence.");
                break;
            case LeaseUseStatus.Reserved:
            case LeaseUseStatus.StartCommitted:
            case LeaseUseStatus.Consumed:
            case LeaseUseStatus.StartedUnknown:
                Require(reservedUseCount == 1 && reservedDuration > TimeSpan.Zero && actualSettledDuration is null,
                    "non-settled recurring accounting entries must retain one full reservation and no actual settlement.");
                break;
            case LeaseUseStatus.Settled:
                Require(reservedUseCount == 1 && reservedDuration > TimeSpan.Zero && actualSettledDuration is not null,
                    "settled recurring accounting entries must retain one reservation and actual settlement evidence.");
                if (actualSettledDuration!.Value > reservedDuration)
                {
                    throw new Phase3DomainException(
                        RecurringLeaseQuotaReasonCodes.SettledDurationInvalid,
                        "Settled recurring accounting actual duration must not exceed reserved duration.");
                }
                break;
            default:
                throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.UnknownState, "The recurring accounting LeaseUse status is unknown.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.AccountingInvalid, message);
            }
        }
    }
}

public sealed class RecurringLeaseQuotaSnapshot
{
    internal RecurringLeaseQuotaSnapshot(
        string leaseId,
        string leaseAuthorizationDigest,
        long leaseVersion,
        long admissionUsedUses,
        TimeSpan admissionUsedDuration,
        long permanentlyConsumedUses,
        TimeSpan conservativelyChargedDuration,
        long remainingReservableUses,
        TimeSpan remainingReservableDuration,
        long inFlightUseCount,
        bool isTemporarilyUnavailable,
        bool isTerminallyExhausted,
        string reasonCode)
    {
        LeaseId = leaseId;
        LeaseAuthorizationDigest = leaseAuthorizationDigest;
        LeaseVersion = leaseVersion;
        AdmissionUsedUses = admissionUsedUses;
        AdmissionUsedDuration = admissionUsedDuration;
        PermanentlyConsumedUses = permanentlyConsumedUses;
        ConservativelyChargedDuration = conservativelyChargedDuration;
        RemainingReservableUses = remainingReservableUses;
        RemainingReservableDuration = remainingReservableDuration;
        InFlightUseCount = inFlightUseCount;
        IsTemporarilyUnavailable = isTemporarilyUnavailable;
        IsTerminallyExhausted = isTerminallyExhausted;
        ReasonCode = reasonCode;
    }

    public string LeaseId { get; }

    public string LeaseAuthorizationDigest { get; }

    public string AuthorizationDigest => LeaseAuthorizationDigest;

    public long LeaseVersion { get; }

    public long AdmissionUsedUses { get; }

    public long ProvisionalUsedUses => AdmissionUsedUses;

    public TimeSpan AdmissionUsedDuration { get; }

    public TimeSpan ProvisionalUsedDuration => AdmissionUsedDuration;

    public long PermanentlyConsumedUses { get; }

    public TimeSpan ConservativelyChargedDuration { get; }

    public TimeSpan ChargedDuration => ConservativelyChargedDuration;

    public long RemainingReservableUses { get; }

    public long RemainingUses => RemainingReservableUses;

    public TimeSpan RemainingReservableDuration { get; }

    public TimeSpan RemainingDuration => RemainingReservableDuration;

    public long InFlightUseCount { get; }

    public bool IsTemporarilyUnavailable { get; }

    public bool IsTerminallyExhausted { get; }

    public string ReasonCode { get; }

    public bool IsAvailable => !IsTemporarilyUnavailable && !IsTerminallyExhausted;
}

public sealed class RecurringLeaseQuotaDecision
{
    internal RecurringLeaseQuotaDecision(
        bool isAllowed,
        string reasonCode,
        RecurringLeaseQuotaSnapshot? quota,
        int reservedUseCount,
        TimeSpan reservedDuration)
    {
        IsAllowed = isAllowed;
        ReasonCode = reasonCode;
        Quota = quota;
        ReservedUseCount = reservedUseCount;
        ReservedDuration = reservedDuration;
    }

    public bool IsAllowed { get; }

    public bool Allowed => IsAllowed;

    public bool Succeeded => IsAllowed;

    public string ReasonCode { get; }

    public RecurringLeaseQuotaSnapshot? Quota { get; }

    public int ReservedUseCount { get; }

    public TimeSpan ReservedDuration { get; }
}

public static class RecurringLeaseQuotaCalculator
{
    public static RecurringLeaseQuotaSnapshot Calculate(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> existingEntries)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(existingEntries);
        var entries = existingEntries.ToArray();
        var useIds = new HashSet<string>(StringComparer.Ordinal);
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var occurrenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw InvalidAccounting("A recurring accounting entry is null.");
            }

            entry.ValidateAgainst(lease);

            if (!useIds.Add(entry.UseId))
            {
                throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.DuplicateUseId, "Recurring accounting use IDs must be unique.");
            }

            if (!runIds.Add(entry.RunId))
            {
                throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.DuplicateRunId, "Recurring accounting run IDs must be unique.");
            }

            if (!occurrenceIds.Add(entry.OccurrenceIdentity))
            {
                throw new Phase3DomainException(RecurringLeaseQuotaReasonCodes.DuplicateOccurrence, "A recurring occurrence may have at most one accounting entry.");
            }
        }

        long admissionUses = 0;
        long permanentUses = 0;
        long inFlightUses = 0;
        long admissionTicks = 0;
        long chargedTicks = 0;
        foreach (var entry in entries)
        {
            if (entry.ReservedUseCount == 1)
            {
                admissionUses = AddChecked(admissionUses, 1, "admission use count");
            }

            if (entry.IsQuotaConsumed)
            {
                permanentUses = AddChecked(permanentUses, 1, "permanent use count");
            }

            if (entry.Status is LeaseUseStatus.Reserved or LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed)
            {
                inFlightUses = AddChecked(inFlightUses, 1, "in-flight use count");
            }

            var admissionDuration = entry.Status == LeaseUseStatus.Settled
                ? entry.ActualSettledDuration!.Value
                : entry.Status == LeaseUseStatus.Available
                    ? TimeSpan.Zero
                    : entry.ReservedDuration;
            admissionTicks = AddChecked(admissionTicks, admissionDuration.Ticks, "admission duration");

            var chargedDuration = entry.Status == LeaseUseStatus.Settled
                ? entry.ActualSettledDuration!.Value
                : entry.Status is LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed or LeaseUseStatus.StartedUnknown
                    ? entry.ReservedDuration
                    : TimeSpan.Zero;
            chargedTicks = AddChecked(chargedTicks, chargedDuration.Ticks, "charged duration");
        }

        if (admissionUses > lease.MaxUses || admissionTicks > lease.MaxCumulativeDuration.Ticks)
        {
            throw InvalidAccounting("Recurring accounting evidence exceeds the lease authorization capacity.");
        }

        var remainingUses = lease.MaxUses - admissionUses;
        var remainingTicks = lease.MaxCumulativeDuration.Ticks - admissionTicks;
        var terminal = permanentUses >= lease.MaxUses ||
                       inFlightUses == 0 && (remainingUses < 1 || remainingTicks < lease.PerRunDuration.Ticks);
        var temporary = !terminal && inFlightUses > 0;
        var reason = terminal
            ? RecurringLeaseQuotaReasonCodes.QuotaExhausted
            : temporary
                ? RecurringLeaseQuotaReasonCodes.RunInFlight
                : RecurringLeaseQuotaReasonCodes.Available;

        return new RecurringLeaseQuotaSnapshot(
            lease.LeaseId,
            lease.AuthorizationDigest,
            lease.Version,
            admissionUses,
            TimeSpan.FromTicks(admissionTicks),
            permanentUses,
            TimeSpan.FromTicks(chargedTicks),
            remainingUses,
            TimeSpan.FromTicks(remainingTicks),
            inFlightUses,
            temporary,
            terminal,
            reason);
    }

    public static RecurringLeaseQuotaDecision EvaluateReservation(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> existingEntries,
        RecurringLeaseOccurrenceRequest occurrenceRequest,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(existingEntries);
        ArgumentNullException.ThrowIfNull(occurrenceRequest);

        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.CurrentTimeInvalid);
        }

        RecurringLeaseQuotaSnapshot quota;
        RecurringLeaseUseAccountingEntry[] entries;
        try
        {
            entries = existingEntries.ToArray();
            quota = Calculate(lease, entries);
        }
        catch (Phase3DomainException exception)
        {
            return Denied(MapAccountingReason(exception.ReasonCode));
        }

        var statusDecision = EvaluateStatus(lease.Status);
        if (statusDecision is not null)
        {
            return Denied(statusDecision, quota);
        }

        if (nowUtc < lease.ValidFromUtc)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.BeforeValidity, quota);
        }

        if (nowUtc >= lease.ValidUntilUtc)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.Expired, quota);
        }

        if (!SameConfiguration(lease.ConfigurationRef, occurrenceRequest.ConfigurationRef))
        {
            return Denied(RecurringLeaseQuotaReasonCodes.ConfigurationMismatch, quota);
        }

        if (occurrenceRequest.RequestedDuration != lease.PerRunDuration)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.DurationMismatch, quota);
        }

        if (occurrenceRequest.ScheduledStartUtc < lease.ValidFromUtc)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.OccurrenceBeforeValidity, quota);
        }

        if (occurrenceRequest.PlannedEndUtc >= lease.ValidUntilUtc)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.OccurrenceAfterValidity, quota);
        }

        if (occurrenceRequest.PlannedEndUtc > lease.AuthorizedPlanLatestEndUtc)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.OccurrenceAfterAuthorizedLatestEnd, quota);
        }

        var existingOccurrence = entries.FirstOrDefault(entry =>
            string.Equals(entry.OccurrenceIdentity, occurrenceRequest.OccurrenceIdentity, StringComparison.Ordinal));
        if (existingOccurrence is not null)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.OccurrenceAlreadyClaimed, quota);
        }

        if (quota.IsTerminallyExhausted)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.QuotaExhausted, quota);
        }

        if (quota.IsTemporarilyUnavailable)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.RunInFlight, quota);
        }

        if (quota.RemainingReservableUses < 1)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.UseQuotaExceeded, quota);
        }

        if (quota.RemainingReservableDuration < lease.PerRunDuration)
        {
            return Denied(RecurringLeaseQuotaReasonCodes.CumulativeDurationExceeded, quota);
        }

        return new RecurringLeaseQuotaDecision(
            isAllowed: true,
            RecurringLeaseQuotaReasonCodes.Available,
            quota,
            reservedUseCount: 1,
            lease.PerRunDuration);
    }

    private static string? EvaluateStatus(ConsentLeaseStatus status) => status switch
    {
        ConsentLeaseStatus.Active => null,
        ConsentLeaseStatus.Pending => RecurringLeaseQuotaReasonCodes.Pending,
        ConsentLeaseStatus.Rejected => RecurringLeaseQuotaReasonCodes.Rejected,
        ConsentLeaseStatus.Revoked => RecurringLeaseQuotaReasonCodes.Revoked,
        ConsentLeaseStatus.Expired => RecurringLeaseQuotaReasonCodes.Expired,
        ConsentLeaseStatus.Exhausted => RecurringLeaseQuotaReasonCodes.Exhausted,
        _ => RecurringLeaseQuotaReasonCodes.NotActive,
    };

    private static RecurringLeaseQuotaDecision Denied(string reasonCode, RecurringLeaseQuotaSnapshot? quota = null) =>
        new(false, reasonCode, quota, reservedUseCount: 0, TimeSpan.Zero);

    private static string MapAccountingReason(string reasonCode) =>
        reasonCode.StartsWith("recurring_lease_", StringComparison.Ordinal)
            ? reasonCode
            : RecurringLeaseQuotaReasonCodes.AccountingInvalid;

    private static bool SameConfiguration(
        RecurringPlanConfigurationRef left,
        RecurringPlanConfigurationRef right) =>
        string.Equals(left.PlanId, right.PlanId, StringComparison.Ordinal) &&
        left.ScheduleRevision == right.ScheduleRevision &&
        string.Equals(left.ScheduleDigest, right.ScheduleDigest, StringComparison.Ordinal) &&
        string.Equals(left.TimeZoneRulesDigest, right.TimeZoneRulesDigest, StringComparison.Ordinal) &&
        string.Equals(left.ProfileRef.ProfileId, right.ProfileRef.ProfileId, StringComparison.Ordinal) &&
        left.ProfileRef.ProfileVersion == right.ProfileRef.ProfileVersion &&
        string.Equals(left.ProfileRef.ProfileDigest, right.ProfileRef.ProfileDigest, StringComparison.Ordinal) &&
        string.Equals(left.ConfigurationDigest, right.ConfigurationDigest, StringComparison.Ordinal);

    private static long AddChecked(long current, long value, string field)
    {
        try
        {
            return checked(current + value);
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(
                RecurringLeaseQuotaReasonCodes.AccountingOverflow,
                $"Recurring accounting {field} overflowed.");
        }
    }

    private static Phase3DomainException InvalidAccounting(string message) =>
        new(RecurringLeaseQuotaReasonCodes.AccountingInvalid, message);
}

/// <summary>Named policy facade for callers that prefer a policy vocabulary.</summary>
public static class RecurringLeaseQuotaPolicy
{
    public static RecurringLeaseQuotaSnapshot Calculate(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> existingEntries) =>
        RecurringLeaseQuotaCalculator.Calculate(lease, existingEntries);

    public static RecurringLeaseQuotaDecision EvaluateReservation(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> existingEntries,
        RecurringLeaseOccurrenceRequest occurrenceRequest,
        DateTimeOffset nowUtc) =>
        RecurringLeaseQuotaCalculator.EvaluateReservation(lease, existingEntries, occurrenceRequest, nowUtc);
}
