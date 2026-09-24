using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed class RecurringLeasePreparedIntentApprovalActivationResult
{
    private RecurringLeasePreparedIntentApprovalActivationResult(
        RecurringLeaseLocalApprovalActivationStatus status,
        string reason,
        string? intentId,
        string? planId,
        string? leaseId,
        bool changed)
    {
        Status = status;
        Reason = reason;
        IntentId = intentId;
        PlanId = planId;
        LeaseId = leaseId;
        Changed = changed;
    }

    internal RecurringLeaseLocalApprovalActivationStatus Status { get; }
    internal string Reason { get; }
    internal string? IntentId { get; }
    internal string? PlanId { get; }
    internal string? LeaseId { get; }
    internal bool Changed { get; }

    internal static RecurringLeasePreparedIntentApprovalActivationResult Activated(
        string intentId,
        string planId,
        string leaseId) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Activated, "activated", intentId, planId, leaseId, true);

    internal static RecurringLeasePreparedIntentApprovalActivationResult AlreadyActive(
        string intentId,
        string planId,
        string leaseId) =>
        new(RecurringLeaseLocalApprovalActivationStatus.AlreadyActive, "already_active", intentId, planId, leaseId, false);

    internal static RecurringLeasePreparedIntentApprovalActivationResult Rejected(
        string? intentId,
        string reason) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Rejected, reason, intentId, null, null, false);

    internal static RecurringLeasePreparedIntentApprovalActivationResult Conflict(
        string? intentId,
        string reason,
        string? planId = null,
        string? leaseId = null) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Conflict, reason, intentId, planId, leaseId, false);
}

/// <summary>
/// Internal setup-intent-bound local approval entry point. The caller supplies
/// only the durable intent identity and a trusted local receipt. Plan, Profile,
/// configuration, and Lease identities are derived from the preparation row
/// inside the same SQLite immediate transaction.
/// </summary>
internal sealed class RecurringLeasePreparedIntentApprovalActivationService
{
    private readonly SqliteRecurringLeasePreparedIntentApprovalActivationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal RecurringLeasePreparedIntentApprovalActivationService(SqliteOperationalStore store)
        : this(store, null, null, null, null)
    {
    }

    internal RecurringLeasePreparedIntentApprovalActivationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringLeaseLocalApprovalActivationFailurePoint>? failureHookForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteRecurringLeasePreparedIntentApprovalActivationTransaction(
            store,
            beforeWritesForTest,
            beforeIntentWriteForTest,
            beforeCommitForTest,
            failureHookForTest);
    }

    internal RecurringLeasePreparedIntentApprovalActivationResult Activate(
        string? intentId,
        RecurringLeaseLocalApprovalReceipt? approvalReceipt)
    {
        if (!IsCanonicalId(intentId))
            return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(null, "activation_intent_request_invalid");

        if (approvalReceipt is null)
            return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, "activation_receipt_missing");

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
            return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, clockFailure);

        try
        {
            return _transaction.ActivatePreparedIntent(intentId!, approvalReceipt, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return exception.Code switch
            {
                "activation_concurrency_conflict" or "activation_immutable_mismatch" or "activation_intent_conflict" or
                "activation_receipt_conflict" => RecurringLeasePreparedIntentApprovalActivationResult.Conflict(intentId, exception.Code),
                "activation_intent_not_found" or "activation_intent_state_invalid" or "activation_snapshot_invalid" or
                "activation_receipt_invalid" or "activation_receipt_identity_mismatch" or "activation_receipt_time_not_utc" or
                "activation_receipt_time_invalid" or "activation_receipt_time_in_future" or "activation_intent_expired" or
                "activation_lease_expired" or "activation_time_non_monotonic" or "unattended_disabled" or
                "stop_all_boundary" or "activation_version_exhausted" =>
                    RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, exception.Code),
                "activation_sqlite_constraint_violation" or "activation_sqlite_failure" or "sqlite_failure" or
                "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
                    RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, "activation_sqlite_failure"),
                _ => RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, exception.Code),
            };
        }
        catch
        {
            return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(intentId, "activation_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "activation_clock_unavailable";
        DateTimeOffset? sampled;
        try { sampled = _utcNow(); } catch { return false; }
        if (sampled is null) return false;
        nowUtc = sampled.Value;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "activation_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "activation_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('/') &&
        !value.Contains('\\');
}
