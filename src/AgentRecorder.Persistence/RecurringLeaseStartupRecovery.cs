using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Bounded recurring startup recovery entry point. It is intentionally not
/// wired to App, a scheduler, a timer, or any power/session event in this
/// task. Each candidate is reconciled in its own short SQLite transaction.
/// </summary>
internal sealed class RecurringLeaseStartupRecovery
{
    private readonly RecurringLeaseRestartRecoveryCandidateQuery _query;
    private readonly SqliteRecurringLeaseRestartRecoveryTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly Action<string, object> _audit;

    internal RecurringLeaseStartupRecovery(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNow = null,
        Action<string, object>? audit = null,
        Action<RecurringLeaseRestartRecoveryFailurePoint>? failureHookForTest = null,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _query = new RecurringLeaseRestartRecoveryCandidateQuery(store);
        _transaction = new SqliteRecurringLeaseRestartRecoveryTransaction(store, failureHookForTest, beforeCommitForTest);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _audit = audit ?? new AgentRecorder.Logging.AuditLogger().Log;
    }

    internal RecurringLeaseStartupRecoveryBatchResult RecoverBatch(
        string? currentUserSid,
        string? sessionBinding,
        int limit)
    {
        DateTimeOffset nowUtc;
        try
        {
            var sampled = _utcNow();
            if (sampled is null || sampled.Value.Offset != TimeSpan.Zero)
            {
                return RecurringLeaseStartupRecoveryBatchResult.QueryFailed("recovery_clock_unavailable");
            }

            nowUtc = sampled.Value;
        }
        catch
        {
            return RecurringLeaseStartupRecoveryBatchResult.QueryFailed("recovery_clock_unavailable");
        }

        RecurringLeaseRestartRecoveryCandidatePage page;
        try
        {
            page = _query.ListCandidates(currentUserSid, sessionBinding, limit);
        }
        catch (Phase3PersistenceException exception)
        {
            return RecurringLeaseStartupRecoveryBatchResult.QueryFailed(NormalizeQueryFailure(exception.Code));
        }
        catch (Exception)
        {
            return RecurringLeaseStartupRecoveryBatchResult.QueryFailed("recovery_candidate_query_failed");
        }

        var outcomes = new List<RecurringLeaseRestartRecoveryResult>(page.Candidates.Count);
        var recovered = 0;
        var alreadyReconciled = 0;
        var failed = 0;
        foreach (var candidate in page.Candidates)
        {
            RecurringLeaseRestartRecoveryResult outcome;
            try
            {
                outcome = _transaction.Reconcile(candidate, currentUserSid, sessionBinding, nowUtc);
            }
            catch (Exception)
            {
                outcome = RecurringLeaseRestartRecoveryResult.Rejected("recovery_sqlite_failure");
            }

            outcomes.Add(outcome);
            switch (outcome.Status)
            {
                case RecurringLeaseRestartRecoveryStatus.Recovered:
                    recovered++;
                    SafeAudit("recurring_lease.recovery_reconciled", candidate, outcome);
                    break;
                case RecurringLeaseRestartRecoveryStatus.AlreadyReconciled:
                    alreadyReconciled++;
                    SafeAudit("recurring_lease.recovery_already_reconciled", candidate, outcome);
                    break;
                default:
                    failed++;
                    SafeAudit("recurring_lease.recovery_failed", candidate, outcome);
                    break;
            }
        }

        return new RecurringLeaseStartupRecoveryBatchResult(
            page.Candidates.Count,
            recovered,
            alreadyReconciled,
            failed,
            page.HasMore,
            failureReason: null,
            outcomes);
    }

    private void SafeAudit(
        string eventName,
        RecurringLeaseRestartRecoveryCandidate candidate,
        RecurringLeaseRestartRecoveryResult outcome)
    {
        try
        {
            _audit(
                eventName,
                new
                {
                    plan_id = candidate.PlanId,
                    lease_id = candidate.LeaseId,
                    occurrence_identity = candidate.OccurrenceIdentity,
                    occurrence_id = candidate.OccurrenceId,
                    run_id = candidate.RunId,
                    use_id = candidate.UseId,
                    reason_code = outcome.Reason,
                    changed = outcome.Changed,
                    result_status = outcome.Status.ToString(),
                });
        }
        catch
        {
            // Audit availability must not change a committed recovery result.
        }
    }

    private static string NormalizeQueryFailure(string code) => code switch
    {
        "recovery_invalid_limit" => "recovery_invalid_limit",
        "recovery_identity_invalid" => "recovery_identity_invalid",
        "recovery_sqlite_failure" or "sqlite_failure" => "recovery_sqlite_failure",
        _ => "recovery_candidate_query_failed",
    };
}

internal sealed class RecurringLeaseStartupRecoveryBatchResult
{
    internal RecurringLeaseStartupRecoveryBatchResult(
        int scanned,
        int recovered,
        int alreadyReconciled,
        int failed,
        bool hasMore,
        string? failureReason,
        IReadOnlyList<RecurringLeaseRestartRecoveryResult> outcomes)
    {
        Scanned = scanned;
        Recovered = recovered;
        AlreadyReconciled = alreadyReconciled;
        Failed = failed;
        HasMore = hasMore;
        FailureReason = failureReason;
        Outcomes = outcomes;
    }

    internal int Scanned { get; }
    internal int Recovered { get; }
    internal int AlreadyReconciled { get; }
    internal int Failed { get; }
    internal bool HasMore { get; }
    internal string? FailureReason { get; }
    internal IReadOnlyList<RecurringLeaseRestartRecoveryResult> Outcomes { get; }

    internal bool Succeeded => Failed == 0 && FailureReason is null;

    internal static RecurringLeaseStartupRecoveryBatchResult QueryFailed(string reason) =>
        new(0, 0, 0, 1, hasMore: false, reason, Array.Empty<RecurringLeaseRestartRecoveryResult>());
}
