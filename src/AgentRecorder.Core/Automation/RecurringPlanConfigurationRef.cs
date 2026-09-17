using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

public static class RecurringPlanConfigurationReasonCodes
{
    public const string PlanIdInvalid = "recurring_plan_configuration_plan_id_invalid";
    public const string RevisionInvalid = "recurring_plan_configuration_revision_invalid";
    public const string ScheduleDigestInvalid = "recurring_plan_configuration_schedule_digest_invalid";
    public const string TimeZoneRulesDigestInvalid = "recurring_plan_configuration_time_zone_rules_digest_invalid";
    public const string ConfigurationDigestInvalid = "recurring_plan_configuration_digest_invalid";
    public const string ConfigurationDigestMismatch = "recurring_plan_configuration_digest_mismatch";
}

/// <summary>
/// The immutable identity of a recurring draft setup. It is derived only from
/// the plan identity, revision-one schedule content/rules, and exact profile
/// reference. Plan status, PlanDefinition.Version, and mutable timestamps are
/// deliberately excluded.
/// </summary>
public sealed class RecurringPlanConfigurationRef
{
    public const int CanonicalVersion = 1;
    public const string DigestPrefix = "recurring-plan-configuration/v1:";

    public RecurringPlanConfigurationRef(
        string planId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        ProfileRef profileRef)
    {
        PlanId = ValidatePlanId(planId);
        ScheduleRevision = ValidateRevision(scheduleRevision);
        ScheduleDigest = ValidateDigest(
            scheduleDigest,
            "recurring-schedule/v1:",
            RecurringPlanConfigurationReasonCodes.ScheduleDigestInvalid);
        TimeZoneRulesDigest = ValidateDigest(
            timeZoneRulesDigest,
            RecurringTimeZoneRulesDigest.Prefix,
            RecurringPlanConfigurationReasonCodes.TimeZoneRulesDigestInvalid);
        profileRef.Validate();
        ProfileRef = profileRef;
        ConfigurationDigest = RecurringPlanConfigurationDigest.Compute(
            PlanId,
            ScheduleRevision,
            ScheduleDigest,
            TimeZoneRulesDigest,
            ProfileRef);
    }

    private RecurringPlanConfigurationRef(
        string planId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        ProfileRef profileRef,
        string configurationDigest)
        : this(planId, scheduleRevision, scheduleDigest, timeZoneRulesDigest, profileRef)
    {
        var canonicalDigest = ValidateDigest(
            configurationDigest,
            DigestPrefix,
            RecurringPlanConfigurationReasonCodes.ConfigurationDigestInvalid);
        if (!string.Equals(canonicalDigest, ConfigurationDigest, StringComparison.Ordinal))
        {
            throw new Phase3DomainException(
                RecurringPlanConfigurationReasonCodes.ConfigurationDigestMismatch,
                "The persisted recurring plan configuration digest does not match its canonical fields.");
        }

        ConfigurationDigest = canonicalDigest;
    }

    public string PlanId { get; }

    public long ScheduleRevision { get; }

    public string ScheduleDigest { get; }

    public string TimeZoneRulesDigest { get; }

    public ProfileRef ProfileRef { get; }

    public ProfileRef ProfileReference => ProfileRef;

    public string ConfigurationDigest { get; }

    internal static RecurringPlanConfigurationRef Rehydrate(
        string planId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        ProfileRef profileRef,
        string configurationDigest) =>
        new(planId, scheduleRevision, scheduleDigest, timeZoneRulesDigest, profileRef, configurationDigest);

    private static string ValidatePlanId(string value)
    {
        try
        {
            return Phase3Validation.RequiredId(value, nameof(PlanId));
        }
        catch (Phase3DomainException)
        {
            throw new Phase3DomainException(
                RecurringPlanConfigurationReasonCodes.PlanIdInvalid,
                "The recurring plan configuration plan identifier is invalid.");
        }
    }

    private static long ValidateRevision(long value)
    {
        if (value <= 0)
        {
            throw new Phase3DomainException(
                RecurringPlanConfigurationReasonCodes.RevisionInvalid,
                "The recurring plan configuration schedule revision must be positive.");
        }

        return value;
    }

    private static string ValidateDigest(string value, string prefix, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 64)
        {
            throw new Phase3DomainException(reasonCode, "The recurring plan configuration digest is not canonical.");
        }

        var hex = value[prefix.Length..];
        if (hex.Any(character => !IsLowerHex(character)))
        {
            throw new Phase3DomainException(reasonCode, "The recurring plan configuration digest is not lowercase SHA-256.");
        }

        return value;
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

/// <summary>
/// Computes a length-delimited, field-labelled canonical digest. The labels
/// and lengths prevent ambiguous concatenation and make future versions
/// explicit without including mutable plan state.
/// </summary>
public static class RecurringPlanConfigurationDigest
{
    public static string Compute(
        string planId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        ProfileRef profileRef)
    {
        var bytes = new List<byte>(512);
        AppendString(bytes, "recurring-plan-configuration/v1");
        AppendInt64(bytes, RecurringPlanConfigurationRef.CanonicalVersion);
        AppendString(bytes, "plan_id");
        AppendString(bytes, planId);
        AppendString(bytes, "schedule_revision");
        AppendInt64(bytes, scheduleRevision);
        AppendString(bytes, "schedule_digest");
        AppendString(bytes, scheduleDigest);
        AppendString(bytes, "time_zone_rules_digest");
        AppendString(bytes, timeZoneRulesDigest);
        AppendString(bytes, "profile_id");
        AppendString(bytes, profileRef.ProfileId);
        AppendString(bytes, "profile_version");
        AppendInt64(bytes, profileRef.ProfileVersion);
        AppendString(bytes, "profile_digest");
        AppendString(bytes, profileRef.ProfileDigest);

        return RecurringPlanConfigurationRef.DigestPrefix +
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
