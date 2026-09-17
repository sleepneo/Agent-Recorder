using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum StandingLeasePreparationResultStatus
{
    Prepared,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

internal interface IStandingLeasePreparationIdProvider
{
    string CreatePlanId();
    string CreateOccurrenceId();
    string CreateLeaseId();
    string CreateScopeId();
}

internal sealed class GuidStandingLeasePreparationIdProvider : IStandingLeasePreparationIdProvider
{
    public string CreatePlanId() => "standing-plan-" + Guid.NewGuid().ToString("N");
    public string CreateOccurrenceId() => "standing-occurrence-" + Guid.NewGuid().ToString("N");
    public string CreateLeaseId() => "standing-lease-" + Guid.NewGuid().ToString("N");
    public string CreateScopeId() => "standing-scope-" + Guid.NewGuid().ToString("N");
}

/// <summary>
/// Internal trusted-process boundary for preparing a selected fixed region.
/// Preparation only creates pending authorization material; it never approves
/// a lease and never touches execution or capture services.
/// </summary>
internal sealed class StandingLeasePreparationService
{
    private readonly SqliteStandingLeasePreparationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal StandingLeasePreparationService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, idProviderForTest: null, beforeWritesForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingLeasePreparationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        IStandingLeasePreparationIdProvider? idProviderForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteStandingLeasePreparationTransaction(
            store,
            idProviderForTest ?? new GuidStandingLeasePreparationIdProvider(),
            beforeWritesForTest,
            beforeCommitForTest);
    }

    internal StandingLeasePreparationResult Prepare(StandingFixedRegionSelectionSnapshot? selection)
    {
        if (selection is null)
        {
            return StandingLeasePreparationResult.Rejected(null, "preparation_selection_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return StandingLeasePreparationResult.Rejected(selection.IntentId, clockFailure);
        }

        try
        {
            return _transaction.Prepare(selection, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapFailure(selection, exception.Code);
        }
        catch (Exception)
        {
            return StandingLeasePreparationResult.Rejected(selection.IntentId, "preparation_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "preparation_clock_unavailable";
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
            failureReason = "preparation_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "preparation_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static StandingLeasePreparationResult MapFailure(
        StandingFixedRegionSelectionSnapshot selection,
        string code) => code switch
        {
            "preparation_selection_conflict" or
            "preparation_identity_conflict" or
            "preparation_partial_binding" or
            "preparation_chain_conflict" or
            "concurrency_conflict" =>
                StandingLeasePreparationResult.Conflict(selection.IntentId, code),
            "preparation_sqlite_failure" or
            "preparation_constraint_violation" or
            "sqlite_failure" or
            "sqlite_corrupt" or
            "sqlite_not_initialized" or
            "sqlite_migration_checksum_mismatch" =>
                StandingLeasePreparationResult.Rejected(selection.IntentId, "preparation_sqlite_failure"),
            _ => StandingLeasePreparationResult.Rejected(selection.IntentId, code),
        };
}

internal sealed class StandingLeasePreparationResult
{
    private StandingLeasePreparationResult(
        StandingLeasePreparationResultStatus status,
        string reason,
        string? intentId,
        string? planId,
        string? occurrenceId,
        string? leaseId,
        string? scopeId,
        string? scopeDigest,
        bool changed)
    {
        Status = status;
        Reason = reason;
        IntentId = intentId;
        PlanId = planId;
        OccurrenceId = occurrenceId;
        LeaseId = leaseId;
        ScopeId = scopeId;
        ScopeDigest = scopeDigest;
        Changed = changed;
    }

    internal StandingLeasePreparationResultStatus Status { get; }
    internal string Reason { get; }
    internal string? IntentId { get; }
    internal string? PlanId { get; }
    internal string? OccurrenceId { get; }
    internal string? LeaseId { get; }
    internal string? ScopeId { get; }
    internal string? ScopeDigest { get; }
    internal bool Changed { get; }

    internal static StandingLeasePreparationResult Prepared(
        string intentId,
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest) =>
        new(StandingLeasePreparationResultStatus.Prepared, "prepared", intentId, planId, occurrenceId, leaseId, scopeId, scopeDigest, true);

    internal static StandingLeasePreparationResult Existing(
        string intentId,
        string planId,
        string occurrenceId,
        string leaseId,
        string scopeId,
        string scopeDigest) =>
        new(StandingLeasePreparationResultStatus.Existing, "existing", intentId, planId, occurrenceId, leaseId, scopeId, scopeDigest, false);

    internal static StandingLeasePreparationResult Conflict(string intentId, string reason) =>
        new(StandingLeasePreparationResultStatus.Conflict, reason, intentId, null, null, null, null, null, false);

    internal static StandingLeasePreparationResult Rejected(string? intentId, string reason) =>
        new(StandingLeasePreparationResultStatus.Rejected, reason, intentId, null, null, null, null, null, false);

    internal static StandingLeasePreparationResult Expired(string intentId) =>
        new(StandingLeasePreparationResultStatus.Expired, "expired", intentId, null, null, null, null, null, false);
}
