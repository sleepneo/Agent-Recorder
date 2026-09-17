using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal intent-bound approval entry point. The caller supplies only the
/// durable setup-intent identity and a trusted local approval receipt; the
/// transaction reads the four aggregate identities from SQLite itself.
/// </summary>
internal sealed class StandingLeasePreparedIntentApprovalActivationService
{
    private readonly SqliteStandingLeaseAuthorizationActivationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal StandingLeasePreparedIntentApprovalActivationService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, beforeWritesForTest: null, beforeIntentWriteForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingLeasePreparedIntentApprovalActivationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
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
        string? intentId,
        StandingLeaseLocalApprovalReceipt? approvalReceipt)
    {
        if (!IsCanonicalId(intentId))
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_intent_request_invalid");
        }

        if (approvalReceipt is null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_receipt_missing");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var failureReason))
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(null, failureReason);
        }

        try
        {
            return _transaction.ActivatePreparedIntent(intentId!, approvalReceipt, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapFailure(exception.Code);
        }
        catch (Exception)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_sqlite_failure");
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

    private static StandingLeaseAuthorizationActivationResult MapFailure(string code) => code switch
    {
        "activation_concurrency_conflict" or "activation_immutable_mismatch" or "activation_intent_conflict" =>
            StandingLeaseAuthorizationActivationResult.Conflict(null, code),
            "activation_sqlite_failure" or "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
                StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_sqlite_failure"),
            "setup_intent_snapshot_invalid" or "persisted_snapshot_invalid" or "activation_snapshot_invalid" =>
                StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_snapshot_invalid"),
            _ => StandingLeaseAuthorizationActivationResult.Rejected(null, code),
        };

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('/') &&
        !value.Contains('\\');
}
