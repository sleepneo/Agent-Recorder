using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringLeaseLocalApprovalActivationStatus
{
    Activated,
    AlreadyActive,
    Rejected,
    Conflict,
}

internal enum RecurringLeaseLocalApprovalActivationFailurePoint
{
    AfterApprovalEvidenceInsert,
    AfterPlanUpdate,
    AfterLeaseUpdate,
    AfterIntentUpdate,
    BeforeCommitAfterFinalRead,
}

internal sealed class RecurringLeaseLocalApprovalActivationRequest
{
    internal RecurringLeaseLocalApprovalActivationRequest(string leaseId, RecurringLeaseLocalApprovalReceipt? approvalReceipt)
    {
        LeaseId = leaseId;
        ApprovalReceipt = approvalReceipt;
    }

    internal string LeaseId { get; }
    internal RecurringLeaseLocalApprovalReceipt? ApprovalReceipt { get; }
}

internal sealed class RecurringLeaseLocalApprovalActivationResult
{
    private RecurringLeaseLocalApprovalActivationResult(
        RecurringLeaseLocalApprovalActivationStatus status,
        string reason,
        RecurringLeaseLocalApprovalActivationRequest? request,
        bool changed)
    {
        Status = status;
        Reason = reason;
        LeaseId = request?.LeaseId;
        Changed = changed;
    }

    internal RecurringLeaseLocalApprovalActivationStatus Status { get; }
    internal string Reason { get; }
    internal string? LeaseId { get; }
    internal string? PlanId { get; init; }
    internal bool Changed { get; }

    internal static RecurringLeaseLocalApprovalActivationResult Activated(RecurringLeaseLocalApprovalActivationRequest request, string planId) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Activated, "activated", request, true) { PlanId = planId };

    internal static RecurringLeaseLocalApprovalActivationResult AlreadyActive(RecurringLeaseLocalApprovalActivationRequest request, string planId) =>
        new(RecurringLeaseLocalApprovalActivationStatus.AlreadyActive, "already_active", request, false) { PlanId = planId };

    internal static RecurringLeaseLocalApprovalActivationResult Rejected(RecurringLeaseLocalApprovalActivationRequest? request, string reason) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Rejected, reason, request, false);

    internal static RecurringLeaseLocalApprovalActivationResult Conflict(RecurringLeaseLocalApprovalActivationRequest? request, string reason) =>
        new(RecurringLeaseLocalApprovalActivationStatus.Conflict, reason, request, false);
}

/// <summary>
/// Samples trusted UTC once per request and serializes each activation through
/// the durable immediate transaction.  No execution or scheduler side effect
/// is reachable from this service.
/// </summary>
internal sealed class RecurringLeaseLocalApprovalActivationService
{
    private readonly SqliteRecurringLeaseLocalApprovalActivationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal RecurringLeaseLocalApprovalActivationService(SqliteOperationalStore store)
        : this(store, null, null, null)
    {
    }

    internal RecurringLeaseLocalApprovalActivationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringLeaseLocalApprovalActivationFailurePoint>? failureHookForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteRecurringLeaseLocalApprovalActivationTransaction(store, beforeWritesForTest, beforeCommitForTest, failureHookForTest);
    }

    internal RecurringLeaseLocalApprovalActivationResult Activate(
        string leaseId,
        RecurringLeaseLocalApprovalReceipt? approvalReceipt) =>
        Activate(new RecurringLeaseLocalApprovalActivationRequest(leaseId, approvalReceipt));

    internal RecurringLeaseLocalApprovalActivationResult Activate(
        RecurringLeaseLocalApprovalActivationRequest? request)
    {
        if (request is null || !IsCanonicalId(request.LeaseId))
        {
            return RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_request_invalid");
        }

        if (request.ApprovalReceipt is null)
        {
            return RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_receipt_missing");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return RecurringLeaseLocalApprovalActivationResult.Rejected(request, clockFailure);
        }

        try
        {
            return _transaction.Activate(request, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return exception.Code switch
            {
                "activation_concurrency_conflict" or "concurrency_conflict" => RecurringLeaseLocalApprovalActivationResult.Conflict(request, "activation_concurrency_conflict"),
                "activation_receipt_conflict" => RecurringLeaseLocalApprovalActivationResult.Conflict(request, exception.Code),
                "activation_snapshot_invalid" or "not_found" or "persisted_snapshot_invalid" or "invalid_argument" => RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_snapshot_invalid"),
                "activation_sqlite_constraint_violation" or "constraint_violation" => RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_sqlite_constraint_violation"),
                "activation_sqlite_failure" or "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" => RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_sqlite_failure"),
                _ => RecurringLeaseLocalApprovalActivationResult.Rejected(request, exception.Code),
            };
        }
        catch
        {
            return RecurringLeaseLocalApprovalActivationResult.Rejected(request, "activation_sqlite_failure");
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
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('/') && !value.Contains('\\');
}
