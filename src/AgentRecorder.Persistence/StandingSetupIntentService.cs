using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Trusted-process entry point for setup-intent create-or-get. It samples UTC
/// itself and exposes no caller time, API DTO, proof, or capture configuration.
/// </summary>
internal sealed class StandingSetupIntentService
{
    private readonly SqliteStandingSetupIntentCreateOrGetTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal StandingSetupIntentService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, beforeWritesForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingSetupIntentService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteStandingSetupIntentCreateOrGetTransaction(
            store,
            beforeWritesForTest,
            beforeCommitForTest);
    }

    internal StandingSetupIntentResult CreateOrGet(StandingSetupIntentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return StandingSetupIntentResult.Rejected(null, "setup_intent_request_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return StandingSetupIntentResult.Rejected(snapshot, clockFailure);
        }

        try
        {
            return _transaction.CreateOrGet(snapshot, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapFailure(snapshot, exception.Code);
        }
        catch (Exception)
        {
            return StandingSetupIntentResult.Rejected(snapshot, "setup_intent_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "setup_intent_clock_unavailable";
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
            failureReason = "setup_intent_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "setup_intent_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static StandingSetupIntentResult MapFailure(
        StandingSetupIntentSnapshot snapshot,
        string code) => code switch
        {
            "setup_intent_conflict" or "setup_intent_identity_conflict" or "setup_intent_request_conflict" or "concurrency_conflict" =>
                StandingSetupIntentResult.Conflict(snapshot, "setup_intent_request_conflict"),
            "setup_intent_snapshot_invalid" or "invalid_argument" or "setup_intent_expired_at_create" =>
                StandingSetupIntentResult.Rejected(snapshot, code),
            "setup_intent_constraint_violation" =>
                StandingSetupIntentResult.Rejected(snapshot, "setup_intent_sqlite_constraint_violation"),
            "sqlite_failure" or "setup_intent_sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
                StandingSetupIntentResult.Rejected(snapshot, "setup_intent_sqlite_failure"),
            _ => StandingSetupIntentResult.Rejected(snapshot, code),
        };
}
