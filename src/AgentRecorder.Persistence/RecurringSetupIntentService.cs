using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed class RecurringSetupIntentService
{
    private readonly SqliteRecurringSetupIntentCreateOrGetTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal RecurringSetupIntentService(SqliteOperationalStore store)
        : this(store, null, null, null)
    {
    }

    internal RecurringSetupIntentService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteRecurringSetupIntentCreateOrGetTransaction(store, beforeWritesForTest, beforeCommitForTest);
    }

    internal RecurringSetupIntentResult CreateOrGet(RecurringSetupIntentSnapshot? snapshot)
    {
        if (snapshot is null) return RecurringSetupIntentResult.Rejected(null, "recurring_setup_intent_request_invalid");
        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure)) return RecurringSetupIntentResult.Rejected(snapshot, clockFailure);
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
            return RecurringSetupIntentResult.Rejected(snapshot, "recurring_setup_intent_sqlite_failure");
        }
    }

    internal RecurringSetupIntentReadback? Get(string intentId, string currentUserSid, string sessionBinding)
    {
        if (!TryReadTrustedUtcNow(out var nowUtc, out _)) return null;
        try { return _transaction.Get(intentId, currentUserSid, sessionBinding, nowUtc); }
        catch (Phase3PersistenceException) { return null; }
        catch (Exception) { return null; }
    }

    internal int ReconcileExpiredPending(int maximumRows = 128)
    {
        if (!TryReadTrustedUtcNow(out var nowUtc, out _)) return 0;
        try { return _transaction.ReconcileExpiredPending(nowUtc, maximumRows); }
        catch (Phase3PersistenceException) { return 0; }
        catch (Exception) { return 0; }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "recurring_setup_intent_clock_unavailable";
        DateTimeOffset? sampled;
        try { sampled = _utcNow(); } catch { return false; }
        if (sampled is null) return false;
        nowUtc = sampled.Value;
        if (nowUtc.Offset != TimeSpan.Zero) { failureReason = "recurring_setup_intent_time_not_utc"; return false; }
        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value) { failureReason = "recurring_setup_intent_time_non_monotonic"; return false; }
            _lastUtcNow = nowUtc;
        }
        return true;
    }

    private static RecurringSetupIntentResult MapFailure(RecurringSetupIntentSnapshot snapshot, string code) => code switch
    {
        "recurring_setup_intent_cross_kind_conflict" or "recurring_setup_intent_identity_conflict" or "recurring_setup_intent_request_conflict" => RecurringSetupIntentResult.Conflict(snapshot, code),
        "recurring_setup_intent_snapshot_invalid" or "recurring_setup_intent_expired_at_create" or "invalid_argument" => RecurringSetupIntentResult.Rejected(snapshot, code),
        "recurring_setup_intent_constraint_violation" => RecurringSetupIntentResult.Rejected(snapshot, "recurring_setup_intent_sqlite_constraint_violation"),
        _ => RecurringSetupIntentResult.Rejected(snapshot, code),
    };
}
