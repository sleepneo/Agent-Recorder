using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local evidence that a trusted local adapter accepted one recurring
/// lease approval.  It contains no capture target, path, duration, quota,
/// status, or execution proof and has no public constructor or deserializer.
/// </summary>
internal sealed class RecurringLeaseLocalApprovalReceipt
{
    internal const string CurrentApprovalKind = "recurring_lease_local_approval";
    internal const int CurrentApprovalVersion = 1;
    internal const int MaximumBindingLength = 256;

    private RecurringLeaseLocalApprovalReceipt(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind,
        int approvalVersion)
    {
        ApprovalId = approvalId;
        LeaseId = leaseId;
        PlanId = planId;
        ConfigurationDigest = configurationDigest;
        AuthorizationDigest = authorizationDigest;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        ApprovedAtUtc = approvedAtUtc;
        ApprovalKind = approvalKind;
        ApprovalVersion = approvalVersion;
    }

    internal static RecurringLeaseLocalApprovalReceipt CreateForTrustedLocalApprovalAdapter(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc) =>
        new(
            RequiredCanonicalText(approvalId, nameof(approvalId), pathSeparators: false),
            RequiredCanonicalText(leaseId, nameof(leaseId), pathSeparators: false),
            RequiredCanonicalText(planId, nameof(planId), pathSeparators: false),
            ValidateVersionedDigest(configurationDigest, RecurringPlanConfigurationRef.DigestPrefix, nameof(configurationDigest)),
            ValidateVersionedDigest(authorizationDigest, RecurringConsentLeaseAuthorizationRef.DigestPrefix, nameof(authorizationDigest)),
            RequiredBinding(currentUserSid, nameof(currentUserSid)),
            RequiredBinding(sessionBinding, nameof(sessionBinding)),
            Utc(approvedAtUtc, nameof(approvedAtUtc)),
            CurrentApprovalKind,
            CurrentApprovalVersion);

    /// <summary>Test-only malformed-shape seam; it is not a production input path.</summary>
    internal static RecurringLeaseLocalApprovalReceipt CreateForTest(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind = CurrentApprovalKind,
        int approvalVersion = CurrentApprovalVersion) =>
        new(approvalId, leaseId, planId, configurationDigest, authorizationDigest,
            currentUserSid, sessionBinding, approvedAtUtc, approvalKind, approvalVersion);

    internal string ApprovalId { get; }
    internal string LeaseId { get; }
    internal string PlanId { get; }
    internal string ConfigurationDigest { get; }
    internal string AuthorizationDigest { get; }
    internal string CurrentUserSid { get; }
    internal string SessionBinding { get; }
    internal DateTimeOffset ApprovedAtUtc { get; }
    internal string ApprovalKind { get; }
    internal int ApprovalVersion { get; }

    internal string ApprovalDigest => RecurringLeaseLocalApprovalDigest.Compute(this);

    private static string RequiredCanonicalText(string? value, string name, bool pathSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new Phase3DomainException("recurring_lease_approval_invalid", $"{name} must be canonical text.");
        }

        if (!pathSeparators && (value.Contains('/') || value.Contains('\\')))
        {
            throw new Phase3DomainException("recurring_lease_approval_invalid", $"{name} must not contain path separators.");
        }

        return value;
    }

    private static string RequiredBinding(string? value, string name)
    {
        var canonical = RequiredCanonicalText(value, name, pathSeparators: true);
        if (canonical.Length > MaximumBindingLength)
        {
            throw new Phase3DomainException("recurring_lease_approval_invalid", $"{name} exceeds the binding length limit.");
        }

        return canonical;
    }

    private static string ValidateVersionedDigest(string? value, string prefix, string name)
    {
        // Versioned digest prefixes intentionally contain a slash (for
        // example recurring-plan-configuration/v1:); only the hexadecimal
        // suffix is restricted below.
        var canonical = RequiredCanonicalText(value, name, pathSeparators: true);
        if (canonical.Length != prefix.Length + 64 || !canonical.StartsWith(prefix, StringComparison.Ordinal) ||
            canonical[prefix.Length..].Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException("recurring_lease_approval_invalid", $"{name} must be a lowercase versioned SHA-256 digest.");
        }

        return canonical;
    }

    private static DateTimeOffset Utc(DateTimeOffset value, string name) => value.Offset == TimeSpan.Zero
        ? value
        : throw new Phase3DomainException("recurring_lease_approval_invalid", $"{name} must be UTC.");
}

internal static class RecurringLeaseLocalApprovalDigest
{
    internal const string Prefix = "recurring-lease-local-approval/v1:";

    internal static string Compute(RecurringLeaseLocalApprovalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var bytes = new List<byte>(768);
        Append(bytes, "recurring-lease-local-approval/v1");
        Append(bytes, receipt.ApprovalKind);
        Append(bytes, receipt.ApprovalVersion);
        Append(bytes, receipt.ApprovalId);
        Append(bytes, receipt.LeaseId);
        Append(bytes, receipt.PlanId);
        Append(bytes, receipt.ConfigurationDigest);
        Append(bytes, receipt.AuthorizationDigest);
        Append(bytes, receipt.CurrentUserSid);
        Append(bytes, receipt.SessionBinding);
        Append(bytes, receipt.ApprovedAtUtc.UtcDateTime.Ticks);
        return Prefix + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    internal static string Compute(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind,
        int approvalVersion)
    {
        var bytes = new List<byte>(768);
        Append(bytes, "recurring-lease-local-approval/v1");
        Append(bytes, approvalKind);
        Append(bytes, approvalVersion);
        Append(bytes, approvalId);
        Append(bytes, leaseId);
        Append(bytes, planId);
        Append(bytes, configurationDigest);
        Append(bytes, authorizationDigest);
        Append(bytes, currentUserSid);
        Append(bytes, sessionBinding);
        Append(bytes, approvedAtUtc.UtcDateTime.Ticks);
        return Prefix + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void Append(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        Append(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void Append(List<byte> bytes, long value)
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

/// <summary>Read-only persisted approval evidence projection.</summary>
public sealed class RecurringLeaseLocalApprovalEvidence
{
    private RecurringLeaseLocalApprovalEvidence(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind,
        int approvalVersion,
        string approvalDigest)
    {
        ApprovalId = approvalId;
        LeaseId = leaseId;
        PlanId = planId;
        ConfigurationDigest = configurationDigest;
        AuthorizationDigest = authorizationDigest;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        ApprovedAtUtc = approvedAtUtc;
        ApprovalKind = approvalKind;
        ApprovalVersion = approvalVersion;
        ApprovalDigest = approvalDigest;
    }

    public string ApprovalId { get; }
    public string LeaseId { get; }
    public string PlanId { get; }
    public string ConfigurationDigest { get; }
    public string AuthorizationDigest { get; }
    public string CurrentUserSid { get; }
    public string SessionBinding { get; }
    public DateTimeOffset ApprovedAtUtc { get; }
    public string ApprovalKind { get; }
    public int ApprovalVersion { get; }
    public string ApprovalDigest { get; }

    internal static RecurringLeaseLocalApprovalEvidence Rehydrate(
        string approvalId,
        string leaseId,
        string planId,
        string configurationDigest,
        string authorizationDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind,
        int approvalVersion,
        string approvalDigest)
    {
        ValidateText(approvalId, nameof(approvalId), pathSeparators: false);
        ValidateText(leaseId, nameof(leaseId), pathSeparators: false);
        ValidateText(planId, nameof(planId), pathSeparators: false);
        ValidateDigest(configurationDigest, RecurringPlanConfigurationRef.DigestPrefix, nameof(configurationDigest));
        ValidateDigest(authorizationDigest, RecurringConsentLeaseAuthorizationRef.DigestPrefix, nameof(authorizationDigest));
        ValidateBinding(currentUserSid, nameof(currentUserSid));
        ValidateBinding(sessionBinding, nameof(sessionBinding));
        if (approvedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException("recurring_lease_approval_persisted_data_invalid", "The persisted approval time is not UTC.");
        }
        if (approvalKind != RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind ||
            approvalVersion != RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion ||
            approvalDigest != RecurringLeaseLocalApprovalDigest.Compute(
                approvalId, leaseId, planId, configurationDigest, authorizationDigest,
                currentUserSid, sessionBinding, approvedAtUtc, approvalKind, approvalVersion))
        {
            throw new Phase3DomainException("recurring_lease_approval_persisted_data_invalid", "The persisted recurring approval digest or version is invalid.");
        }

        return new(approvalId, leaseId, planId, configurationDigest, authorizationDigest,
            currentUserSid, sessionBinding, approvedAtUtc, approvalKind, approvalVersion, approvalDigest);
    }

    private static void ValidateText(string value, string name, bool pathSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            (!pathSeparators && (value.Contains('/') || value.Contains('\\'))))
        {
            throw new Phase3DomainException("recurring_lease_approval_persisted_data_invalid", $"The persisted {name} is not canonical.");
        }
    }

    private static void ValidateBinding(string value, string name)
    {
        ValidateText(value, name, pathSeparators: true);
        if (value.Length > RecurringLeaseLocalApprovalReceipt.MaximumBindingLength)
        {
            throw new Phase3DomainException("recurring_lease_approval_persisted_data_invalid", $"The persisted {name} exceeds the binding length limit.");
        }
    }

    private static void ValidateDigest(string value, string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != prefix.Length + 64 ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value[prefix.Length..].Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException("recurring_lease_approval_persisted_data_invalid", $"The persisted {name} is not canonical.");
        }
    }

    internal bool Matches(RecurringLeaseLocalApprovalReceipt receipt) =>
        string.Equals(ApprovalId, receipt.ApprovalId, StringComparison.Ordinal) &&
        string.Equals(LeaseId, receipt.LeaseId, StringComparison.Ordinal) &&
        string.Equals(PlanId, receipt.PlanId, StringComparison.Ordinal) &&
        string.Equals(ConfigurationDigest, receipt.ConfigurationDigest, StringComparison.Ordinal) &&
        string.Equals(AuthorizationDigest, receipt.AuthorizationDigest, StringComparison.Ordinal) &&
        string.Equals(CurrentUserSid, receipt.CurrentUserSid, StringComparison.Ordinal) &&
        string.Equals(SessionBinding, receipt.SessionBinding, StringComparison.Ordinal) &&
        ApprovedAtUtc == receipt.ApprovedAtUtc &&
        string.Equals(ApprovalKind, receipt.ApprovalKind, StringComparison.Ordinal) &&
        ApprovalVersion == receipt.ApprovalVersion &&
        string.Equals(ApprovalDigest, receipt.ApprovalDigest, StringComparison.Ordinal);
}
