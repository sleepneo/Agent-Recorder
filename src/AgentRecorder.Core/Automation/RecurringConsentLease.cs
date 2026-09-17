using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

public static class RecurringConsentLeaseReasonCodes
{
    public const string InvalidArgument = "recurring_lease_invalid_argument";
    public const string LeaseIdInvalid = "recurring_lease_id_invalid";
    public const string NonUtcTime = "recurring_lease_non_utc_time";
    public const string WindowInvalid = "recurring_lease_window_invalid";
    public const string LatestEndInvalid = "recurring_lease_latest_end_invalid";
    public const string PerRunDurationInvalid = "recurring_lease_per_run_duration_invalid";
    public const string MaxUsesInvalid = "recurring_lease_max_uses_invalid";
    public const string CumulativeDurationInvalid = "recurring_lease_cumulative_duration_invalid";
    public const string AuthorizationOverflow = "recurring_lease_authorization_overflow";
    public const string AuthorizationDigestInvalid = "recurring_lease_authorization_digest_invalid";
    public const string AuthorizationDigestMismatch = "recurring_lease_authorization_digest_mismatch";
    public const string UnknownState = "recurring_lease_unknown_state";
    public const string NegativeVersion = "recurring_lease_negative_version";
    public const string NonMonotonicTime = "recurring_lease_non_monotonic_time";
    public const string CreatedAtInvalid = "recurring_lease_created_at_invalid";
    public const string UpdatedBeforeCreated = "recurring_lease_updated_before_created";
    public const string TransitionBeforeCreation = "recurring_lease_transition_before_creation";
    public const string ActiveAfterValidity = "recurring_lease_active_after_validity";
    public const string ExpirationBeforeValidity = "recurring_lease_expiration_before_validity";
    public const string ExhaustionRequiresQuotaDecision = "recurring_lease_exhaustion_requires_terminal_quota";
    public const string ExhaustionSnapshotInvalid = "recurring_lease_exhaustion_snapshot_invalid";
    public const string ExhaustionSnapshotLeaseMismatch = "recurring_lease_exhaustion_snapshot_lease_mismatch";
    public const string ExhaustionSnapshotDigestMismatch = "recurring_lease_exhaustion_snapshot_digest_mismatch";
    public const string ExhaustionSnapshotVersionMismatch = "recurring_lease_exhaustion_snapshot_version_mismatch";
    public const string ExhaustionSnapshotNotTerminal = "recurring_lease_exhaustion_snapshot_not_terminal";
    public const string ExhaustionSnapshotTemporarilyUnavailable = "recurring_lease_exhaustion_snapshot_temporarily_unavailable";
}

/// <summary>
/// Immutable recurring consent authorization identity. Status, version and
/// updated time are intentionally outside this value and outside its digest.
/// </summary>
public sealed class RecurringConsentLeaseAuthorizationRef
{
    public const int CanonicalVersion = 1;
    public const string DigestPrefix = "recurring-consent-lease-authorization/v1:";

    public RecurringConsentLeaseAuthorizationRef(
        string leaseId,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset authorizedPlanLatestEndUtc,
        TimeSpan perRunDuration,
        long maxUses,
        TimeSpan maxCumulativeDuration)
    {
        LeaseId = ValidateLeaseId(leaseId);
        ConfigurationRef = configurationRef ?? throw new Phase3DomainException(
            RecurringConsentLeaseReasonCodes.InvalidArgument,
            "The recurring lease configuration reference is required.");
        ValidFromUtc = ValidateUtc(validFromUtc, nameof(validFromUtc));
        ValidUntilUtc = ValidateUtc(validUntilUtc, nameof(validUntilUtc));
        AuthorizedPlanLatestEndUtc = ValidateUtc(authorizedPlanLatestEndUtc, nameof(authorizedPlanLatestEndUtc));
        if (ValidFromUtc >= ValidUntilUtc)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.WindowInvalid,
                "The recurring lease validity window must be ordered and non-empty.");
        }

        if (ValidUntilUtc <= AuthorizedPlanLatestEndUtc || AuthorizedPlanLatestEndUtc <= ValidFromUtc)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.LatestEndInvalid,
                "The authorized latest plan end must be after valid-from and strictly before valid-until.");
        }

        PerRunDuration = ValidatePositiveDuration(perRunDuration);
        MaxUses = maxUses > 0
            ? maxUses
            : throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.MaxUsesInvalid,
                "The recurring lease max uses must be positive.");
        MaxCumulativeDuration = ValidateCumulativeDuration(maxCumulativeDuration, MaxUses, PerRunDuration);
        AuthorizationDigest = RecurringConsentLeaseAuthorizationDigest.Compute(this);
    }

    private RecurringConsentLeaseAuthorizationRef(
        string leaseId,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset authorizedPlanLatestEndUtc,
        TimeSpan perRunDuration,
        long maxUses,
        TimeSpan maxCumulativeDuration,
        string authorizationDigest)
        : this(
            leaseId,
            configurationRef,
            validFromUtc,
            validUntilUtc,
            authorizedPlanLatestEndUtc,
            perRunDuration,
            maxUses,
            maxCumulativeDuration)
    {
        var canonicalDigest = ValidateDigest(authorizationDigest);
        if (!string.Equals(canonicalDigest, AuthorizationDigest, StringComparison.Ordinal))
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.AuthorizationDigestMismatch,
                "The recurring lease authorization digest does not match its canonical immutable fields.");
        }

        AuthorizationDigest = canonicalDigest;
    }

    public string LeaseId { get; }

    public string Id => LeaseId;

    public RecurringPlanConfigurationRef ConfigurationRef { get; }

    public RecurringPlanConfigurationRef Configuration => ConfigurationRef;

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ValidUntilUtc { get; }

    public DateTimeOffset AuthorizedPlanLatestEndUtc { get; }

    public TimeSpan PerRunDuration { get; }

    public long MaxUses { get; }

    public TimeSpan MaxCumulativeDuration { get; }

    public string AuthorizationDigest { get; }

    internal static RecurringConsentLeaseAuthorizationRef Rehydrate(
        string leaseId,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset authorizedPlanLatestEndUtc,
        TimeSpan perRunDuration,
        long maxUses,
        TimeSpan maxCumulativeDuration,
        string authorizationDigest) =>
        new(
            leaseId,
            configurationRef,
            validFromUtc,
            validUntilUtc,
            authorizedPlanLatestEndUtc,
            perRunDuration,
            maxUses,
            maxCumulativeDuration,
            authorizationDigest);

    private static string ValidateLeaseId(string value)
    {
        try
        {
            return Phase3Validation.RequiredId(value, nameof(LeaseId));
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.LeaseIdInvalid, exception.Message);
        }
    }

    private static DateTimeOffset ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.NonUtcTime,
                $"{name} must use UTC.");
        }

        return value;
    }

    private static TimeSpan ValidatePositiveDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.PerRunDurationInvalid,
                "The recurring lease per-run duration must be positive.");
        }

        return value;
    }

    private static TimeSpan ValidateCumulativeDuration(TimeSpan value, long maxUses, TimeSpan perRunDuration)
    {
        if (value < perRunDuration)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.CumulativeDurationInvalid,
                "The recurring lease cumulative duration must cover one complete run.");
        }

        long maximumTicks;
        try
        {
            maximumTicks = checked(maxUses * perRunDuration.Ticks);
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.AuthorizationOverflow,
                "The recurring lease max uses and per-run duration overflow the checked capacity.");
        }

        if (value.Ticks > maximumTicks)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.CumulativeDurationInvalid,
                "The recurring lease cumulative duration exceeds max uses times per-run duration.");
        }

        return value;
    }

    private static string ValidateDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(DigestPrefix, StringComparison.Ordinal) ||
            value.Length != DigestPrefix.Length + 64 ||
            value[DigestPrefix.Length..].Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.AuthorizationDigestInvalid,
                "The recurring lease authorization digest is not canonical lowercase SHA-256.");
        }

        return value;
    }
}

public static class RecurringConsentLeaseAuthorizationDigest
{
    public static string Compute(RecurringConsentLeaseAuthorizationRef authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var bytes = new List<byte>(768);
        AppendString(bytes, "recurring-consent-lease-authorization/v1");
        AppendInt64(bytes, RecurringConsentLeaseAuthorizationRef.CanonicalVersion);
        AppendString(bytes, "lease_id");
        AppendString(bytes, authorization.LeaseId);
        AppendString(bytes, "configuration_digest");
        AppendString(bytes, authorization.ConfigurationRef.ConfigurationDigest);
        AppendString(bytes, "configuration_plan_id");
        AppendString(bytes, authorization.ConfigurationRef.PlanId);
        AppendString(bytes, "configuration_schedule_revision");
        AppendInt64(bytes, authorization.ConfigurationRef.ScheduleRevision);
        AppendString(bytes, "configuration_schedule_digest");
        AppendString(bytes, authorization.ConfigurationRef.ScheduleDigest);
        AppendString(bytes, "configuration_time_zone_rules_digest");
        AppendString(bytes, authorization.ConfigurationRef.TimeZoneRulesDigest);
        AppendString(bytes, "configuration_profile_id");
        AppendString(bytes, authorization.ConfigurationRef.ProfileRef.ProfileId);
        AppendString(bytes, "configuration_profile_version");
        AppendInt64(bytes, authorization.ConfigurationRef.ProfileRef.ProfileVersion);
        AppendString(bytes, "configuration_profile_digest");
        AppendString(bytes, authorization.ConfigurationRef.ProfileRef.ProfileDigest);
        AppendString(bytes, "valid_from_utc_ticks");
        AppendInt64(bytes, authorization.ValidFromUtc.UtcDateTime.Ticks);
        AppendString(bytes, "valid_until_utc_ticks");
        AppendInt64(bytes, authorization.ValidUntilUtc.UtcDateTime.Ticks);
        AppendString(bytes, "authorized_plan_latest_end_utc_ticks");
        AppendInt64(bytes, authorization.AuthorizedPlanLatestEndUtc.UtcDateTime.Ticks);
        AppendString(bytes, "per_run_duration_ticks");
        AppendInt64(bytes, authorization.PerRunDuration.Ticks);
        AppendString(bytes, "max_uses");
        AppendInt64(bytes, authorization.MaxUses);
        AppendString(bytes, "max_cumulative_duration_ticks");
        AppendInt64(bytes, authorization.MaxCumulativeDuration.Ticks);
        return RecurringConsentLeaseAuthorizationRef.DigestPrefix +
            Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendInt64(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void AppendInt64(List<byte> bytes, long value)
    {
        unchecked
        {
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                bytes.Add((byte)(value >> shift));
            }
        }
    }
}

/// <summary>
/// A recurring multi-use authorization aggregate. Public creation is pending
/// only; activation remains a future local approval transaction concern.
/// </summary>
public sealed class RecurringConsentLease
{
    private ConsentLeaseStatus _status;

    private RecurringConsentLease(
        RecurringConsentLeaseAuthorizationRef authorization,
        ConsentLeaseStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        Authorization = authorization ?? throw new Phase3DomainException(
            RecurringConsentLeaseReasonCodes.InvalidArgument,
            "The recurring lease authorization is required.");
        if (!Enum.IsDefined(status))
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.UnknownState, "The recurring lease status is unknown.");
        }

        if (version < 0)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.NegativeVersion, "The recurring lease version must not be negative.");
        }

        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.NonUtcTime, "updatedAtUtc must use UTC.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.NonUtcTime, "createdAtUtc must use UTC.");
        }

        if (createdAtUtc >= authorization.ValidUntilUtc)
        {
            throw new Phase3DomainException(
                RecurringConsentLeaseReasonCodes.CreatedAtInvalid,
                "createdAtUtc must be strictly before validUntilUtc.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.NonMonotonicTime, "updatedAtUtc must not precede createdAtUtc.");
        }

        _status = status;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
    }

    public static RecurringConsentLease CreatePending(
        string leaseId,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset authorizedPlanLatestEndUtc,
        TimeSpan perRunDuration,
        long maxUses,
        TimeSpan maxCumulativeDuration,
        DateTimeOffset createdAtUtc) =>
        CreatePending(new RecurringConsentLeaseAuthorizationRef(
            leaseId,
            configurationRef,
            validFromUtc,
            validUntilUtc,
            authorizedPlanLatestEndUtc,
            perRunDuration,
            maxUses,
            maxCumulativeDuration),
            createdAtUtc);

    public static RecurringConsentLease CreatePending(
        RecurringConsentLeaseAuthorizationRef authorization,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return new(authorization, ConsentLeaseStatus.Pending, createdAtUtc, createdAtUtc, version: 0);
    }

    internal static RecurringConsentLease Rehydrate(
        string leaseId,
        RecurringPlanConfigurationRef configurationRef,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset authorizedPlanLatestEndUtc,
        TimeSpan perRunDuration,
        long maxUses,
        TimeSpan maxCumulativeDuration,
        string authorizationDigest,
        ConsentLeaseStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        var authorization = RecurringConsentLeaseAuthorizationRef.Rehydrate(
            leaseId,
            configurationRef,
            validFromUtc,
            validUntilUtc,
            authorizedPlanLatestEndUtc,
            perRunDuration,
            maxUses,
            maxCumulativeDuration,
            authorizationDigest);
        return new RecurringConsentLease(authorization, status, createdAtUtc, updatedAtUtc, version);
    }

    public RecurringConsentLeaseAuthorizationRef Authorization { get; }

    public RecurringConsentLeaseAuthorizationRef AuthorizationRef => Authorization;

    public string LeaseId => Authorization.LeaseId;

    public string Id => LeaseId;

    public RecurringPlanConfigurationRef ConfigurationRef => Authorization.ConfigurationRef;

    public RecurringPlanConfigurationRef Configuration => ConfigurationRef;

    public string PlanId => ConfigurationRef.PlanId;

    public DateTimeOffset ValidFromUtc => Authorization.ValidFromUtc;

    public DateTimeOffset ValidUntilUtc => Authorization.ValidUntilUtc;

    public DateTimeOffset AuthorizedPlanLatestEndUtc => Authorization.AuthorizedPlanLatestEndUtc;

    public TimeSpan PerRunDuration => Authorization.PerRunDuration;

    public long MaxUses => Authorization.MaxUses;

    public TimeSpan MaxCumulativeDuration => Authorization.MaxCumulativeDuration;

    public string AuthorizationDigest => Authorization.AuthorizationDigest;

    public ConsentLeaseStatus Status => _status;

    public string StatusCode => Phase3StateCodes.ToCode(_status);

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public bool IsExpiredAt(DateTimeOffset atUtc)
    {
        if (atUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringConsentLeaseReasonCodes.NonUtcTime, "The recurring lease time must use UTC.");
        }

        return atUtc >= ValidUntilUtc;
    }

    public Phase3TransitionResult TryTransition(ConsentLeaseStatus next, DateTimeOffset? transitionedAtUtc = null)
    {
        if (!Enum.IsDefined(next))
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.UnknownState);
        }

        if (next == ConsentLeaseStatus.Exhausted && _status != ConsentLeaseStatus.Exhausted)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionRequiresQuotaDecision);
        }

        if (!Phase3TransitionGuards.CanTransition(_status, next, out var reasonCode))
        {
            return Phase3TransitionResult.Failure(reasonCode);
        }

        var transitionTime = transitionedAtUtc ?? DateTimeOffset.UtcNow;
        if (transitionTime.Offset != TimeSpan.Zero)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.NonUtcTime);
        }

        if (transitionTime < CreatedAtUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.TransitionBeforeCreation);
        }

        if (transitionTime < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.NonMonotonicTime);
        }

        if (_status == next)
        {
            return Phase3TransitionResult.Idempotent;
        }

        if (_status == ConsentLeaseStatus.Pending && next == ConsentLeaseStatus.Active && transitionTime >= ValidUntilUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ActiveAfterValidity);
        }

        if ((_status is ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active) &&
            next == ConsentLeaseStatus.Expired &&
            transitionTime < ValidUntilUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExpirationBeforeValidity);
        }

        _status = next;
        UpdatedAtUtc = transitionTime;
        Version++;
        return Phase3TransitionResult.Success();
    }

    /// <summary>
    /// The only domain entry point that can move an active recurring lease to
    /// exhausted. The supplied snapshot is evidence for this exact aggregate
    /// and is deliberately checked again by the future persistence boundary.
    /// </summary>
    public Phase3TransitionResult TryMarkExhausted(
        RecurringLeaseQuotaSnapshot snapshot,
        DateTimeOffset transitionedAtUtc)
    {
        if (snapshot is null)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotInvalid);
        }

        if (!string.Equals(snapshot.LeaseId, LeaseId, StringComparison.Ordinal))
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotLeaseMismatch);
        }

        if (!string.Equals(snapshot.LeaseAuthorizationDigest, AuthorizationDigest, StringComparison.Ordinal))
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotDigestMismatch);
        }

        if (snapshot.LeaseVersion != Version)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotVersionMismatch);
        }

        if (!snapshot.IsTerminallyExhausted)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotNotTerminal);
        }

        if (snapshot.IsTemporarilyUnavailable)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotTemporarilyUnavailable);
        }

        if (!Enum.IsDefined(_status) || (_status != ConsentLeaseStatus.Active && _status != ConsentLeaseStatus.Exhausted))
        {
            return Phase3TransitionResult.Failure("invalid_transition");
        }

        if (transitionedAtUtc.Offset != TimeSpan.Zero)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.NonUtcTime);
        }

        if (transitionedAtUtc < CreatedAtUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.TransitionBeforeCreation);
        }

        if (transitionedAtUtc < UpdatedAtUtc)
        {
            return Phase3TransitionResult.Failure(RecurringConsentLeaseReasonCodes.NonMonotonicTime);
        }

        if (_status == ConsentLeaseStatus.Exhausted)
        {
            return Phase3TransitionResult.Idempotent;
        }

        _status = ConsentLeaseStatus.Exhausted;
        UpdatedAtUtc = transitionedAtUtc;
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
