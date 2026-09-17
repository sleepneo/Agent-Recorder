using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local capability produced by a trusted local approval adapter.
/// This is an approval-action receipt only. It is deliberately not a capture
/// proof, execution authorization, public token, or persisted DTO.
/// </summary>
internal sealed class StandingLeaseLocalApprovalReceipt
{
    internal const string CurrentApprovalKind = "standing_lease_local_approval";
    internal const int CurrentApprovalVersion = 1;

    private StandingLeaseLocalApprovalReceipt(
        string approvalId,
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind,
        int approvalVersion)
    {
        ApprovalId = approvalId;
        PlanId = planId;
        OccurrenceId = occurrenceId;
        LeaseId = leaseId;
        ScopeId = scopeId;
        ScopeDigest = scopeDigest;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        ApprovedAtUtc = approvedAtUtc;
        ApprovalKind = approvalKind;
        ApprovalVersion = approvalVersion;
    }

    /// <summary>
    /// The only production creation seam. A future local confirmation adapter
    /// is expected to call this after it has accepted a real local approval.
    /// It accepts no capture configuration, target, path, duration, proof, or
    /// environment fields.
    /// </summary>
    internal static StandingLeaseLocalApprovalReceipt CreateForTrustedLocalApprovalAdapter(
        string approvalId,
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc) =>
        new(
            RequiredCanonicalText(approvalId, nameof(approvalId), allowSeparators: false),
            RequiredCanonicalText(planId, nameof(planId), allowSeparators: false),
            RequiredCanonicalText(occurrenceId, nameof(occurrenceId), allowSeparators: false),
            RequiredCanonicalText(leaseId, nameof(leaseId), allowSeparators: false),
            RequiredCanonicalText(scopeId, nameof(scopeId), allowSeparators: false),
            ValidateDigest(scopeDigest, nameof(scopeDigest)),
            RequiredCanonicalText(currentUserSid, nameof(currentUserSid), allowSeparators: false),
            RequiredCanonicalText(sessionBinding, nameof(sessionBinding), allowSeparators: false),
            Utc(approvedAtUtc, nameof(approvedAtUtc)),
            CurrentApprovalKind,
            CurrentApprovalVersion);

    /// <summary>
    /// Explicit test-only seam. It can manufacture malformed receipt shapes so
    /// the persistence boundary can prove that invalid kind/version/time and
    /// identity values are rejected. It is internal and is not a production
    /// deserialization or request-body path.
    /// </summary>
    internal static StandingLeaseLocalApprovalReceipt CreateForTest(
        string approvalId,
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset approvedAtUtc,
        string approvalKind = CurrentApprovalKind,
        int approvalVersion = CurrentApprovalVersion) =>
        new(
            approvalId,
            planId,
            occurrenceId,
            leaseId,
            scopeId,
            scopeDigest,
            currentUserSid,
            sessionBinding,
            approvedAtUtc,
            approvalKind,
            approvalVersion);

    internal string ApprovalId { get; }

    internal string PlanId { get; }

    internal string OccurrenceId { get; }

    internal string LeaseId { get; }

    internal string ScopeId { get; }

    internal string ScopeDigest { get; }

    internal string CurrentUserSid { get; }

    internal string SessionBinding { get; }

    internal DateTimeOffset ApprovedAtUtc { get; }

    internal string ApprovalKind { get; }

    internal int ApprovalVersion { get; }

    private static string RequiredCanonicalText(string? value, string name, bool allowSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new Phase3DomainException("approval_receipt_invalid", $"{name} must be a non-blank canonical value.");
        }

        if (!allowSeparators && (value.Contains('\\') || value.Contains('/')))
        {
            throw new Phase3DomainException("approval_receipt_invalid", $"{name} must not contain path separators.");
        }

        return value;
    }

    private static string ValidateDigest(string? value, string name)
    {
        var canonical = RequiredCanonicalText(value, name, allowSeparators: false);
        if (canonical.Length != 64 || canonical.Any(character =>
                character < '0' || (character > '9' && character < 'a') || character > 'f'))
        {
            throw new Phase3DomainException("approval_receipt_invalid", $"{name} must be lowercase hexadecimal SHA-256.");
        }

        return canonical;
    }

    private static DateTimeOffset Utc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException("approval_receipt_invalid", $"{name} must be UTC.");
        }

        return value;
    }
}
