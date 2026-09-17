using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal boundary for accepting one trusted local standing-lease approval.
/// It samples trusted UTC and delegates the complete durable operation to one
/// SQLite immediate transaction. It does not start capture or call eligibility.
/// </summary>
internal sealed class StandingLeaseAuthorizationActivationService
{
    private readonly SqliteStandingLeaseAuthorizationActivationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal StandingLeaseAuthorizationActivationService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, beforeWritesForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingLeaseAuthorizationActivationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteStandingLeaseAuthorizationActivationTransaction(
            store,
            beforeWritesForTest,
            beforeCommitForTest,
            beforeIntentWriteForTest);
    }

    internal StandingLeaseAuthorizationActivationResult Activate(
        StandingLeaseAuthorizationActivationRequest? request)
    {
        if (!IsValidRequest(request))
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_request_invalid");
        }

        if (request!.ApprovalReceipt is null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_receipt_missing");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var failureReason))
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, failureReason);
        }

        try
        {
            return _transaction.Activate(request, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapFailure(request, exception.Code);
        }
        catch (Exception)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "activation_clock_unavailable";

        DateTimeOffset? sampled;
        try
        {
            sampled = _utcNow();
        }
        catch
        {
            return false;
        }

        if (sampled is null)
        {
            return false;
        }

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

    private static StandingLeaseAuthorizationActivationResult MapFailure(
        StandingLeaseAuthorizationActivationRequest request,
        string code) => code switch
        {
            "activation_concurrency_conflict" or "concurrency_conflict" =>
                StandingLeaseAuthorizationActivationResult.Conflict(request, "activation_concurrency_conflict"),
            "activation_snapshot_invalid" or "not_found" or "persisted_snapshot_invalid" or "invalid_argument" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_snapshot_invalid"),
            "activation_constraint_violation" or "constraint_violation" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_constraint_violation"),
            "activation_sqlite_failure" or "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_failure"),
            _ => StandingLeaseAuthorizationActivationResult.Rejected(request, code),
        };

    private static bool IsValidRequest(StandingLeaseAuthorizationActivationRequest? request) =>
        request is not null &&
        IsCanonicalId(request.PlanId) &&
        IsCanonicalId(request.OccurrenceId) &&
        IsCanonicalId(request.LeaseId) &&
        IsCanonicalId(request.ScopeId) &&
        IsCanonicalDigest(request.ScopeDigest);

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalDigest(string? value) =>
        IsCanonicalId(value) &&
        value!.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Durable identity plus the process-local approval receipt. No caller time,
/// expected version, execution ID, capture configuration, or environment is
/// accepted; versions are read from the same activation transaction.
/// </summary>
internal sealed class StandingLeaseAuthorizationActivationRequest
{
    internal StandingLeaseAuthorizationActivationRequest(
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest,
        StandingLeaseLocalApprovalReceipt? approvalReceipt)
    {
        PlanId = planId;
        OccurrenceId = occurrenceId;
        LeaseId = leaseId;
        ScopeId = scopeId;
        ScopeDigest = scopeDigest;
        ApprovalReceipt = approvalReceipt;
    }

    internal string PlanId { get; }

    internal string OccurrenceId { get; }

    internal string LeaseId { get; }

    internal string ScopeId { get; }

    internal string ScopeDigest { get; }

    internal StandingLeaseLocalApprovalReceipt? ApprovalReceipt { get; }
}

internal enum StandingLeaseAuthorizationActivationStatus
{
    Activated,
    AlreadyActive,
    Rejected,
    Conflict,
}

/// <summary>
/// Controlled activation result. It carries durable identity and a stable
/// reason only; the approval receipt and all execution/capture surfaces stay
/// inside the trusted process-local boundary.
/// </summary>
internal sealed class StandingLeaseAuthorizationActivationResult
{
    private StandingLeaseAuthorizationActivationResult(
        StandingLeaseAuthorizationActivationStatus status,
        string reason,
        StandingLeaseAuthorizationActivationRequest? request,
        bool changed)
    {
        Status = status;
        Reason = reason;
        PlanId = request?.PlanId;
        OccurrenceId = request?.OccurrenceId;
        LeaseId = request?.LeaseId;
        ScopeId = request?.ScopeId;
        ScopeDigest = request?.ScopeDigest;
        Changed = changed;
    }

    internal StandingLeaseAuthorizationActivationStatus Status { get; }

    internal string Reason { get; }

    internal string? PlanId { get; }

    internal string? OccurrenceId { get; }

    internal string? LeaseId { get; }

    internal string? ScopeId { get; }

    internal string? ScopeDigest { get; }

    internal bool Changed { get; }

    internal static StandingLeaseAuthorizationActivationResult Activated(
        StandingLeaseAuthorizationActivationRequest request) =>
        new(StandingLeaseAuthorizationActivationStatus.Activated, "activated", request, changed: true);

    internal static StandingLeaseAuthorizationActivationResult AlreadyActive(
        StandingLeaseAuthorizationActivationRequest request) =>
        new(StandingLeaseAuthorizationActivationStatus.AlreadyActive, "already_active", request, changed: false);

    internal static StandingLeaseAuthorizationActivationResult Rejected(
        StandingLeaseAuthorizationActivationRequest? request,
        string reason) =>
        new(StandingLeaseAuthorizationActivationStatus.Rejected, reason, request, changed: false);

    internal static StandingLeaseAuthorizationActivationResult Conflict(
        StandingLeaseAuthorizationActivationRequest? request,
        string reason) =>
        new(StandingLeaseAuthorizationActivationStatus.Conflict, reason, request, changed: false);
}
