using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Startup-only reconciliation. It enumerates the current SID/session and
/// reuses the existing restart transaction; it never creates a replacement
/// Run and never invokes a backend.
/// </summary>
internal sealed class StandingLeaseNaturalWakeStartupRecovery
{
    private readonly StandingLeaseNaturalWakeCandidateQuery _query;
    private readonly StandingLeaseRestartRecoveryService _recovery;
    private readonly Action<string, object> _audit;

    internal StandingLeaseNaturalWakeStartupRecovery(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNow = null,
        Action<string, object>? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _query = new StandingLeaseNaturalWakeCandidateQuery(store);
        _recovery = new StandingLeaseRestartRecoveryService(store, utcNow);
        _audit = audit ?? new AgentRecorder.Logging.AuditLogger().Log;
    }

    internal bool RecoverAll(string? currentUserSid, string? sessionBinding)
    {
        IReadOnlyList<StandingLeaseRecoveryCandidate> candidates;
        try
        {
            candidates = _query.ListRecoveryCandidates(currentUserSid, sessionBinding);
        }
        catch
        {
            SafeAudit("standing_lease.recovery_failed", new { reason_code = "recovery_candidate_query_failed" });
            return false;
        }

        var success = true;
        foreach (var candidate in candidates)
        {
            StandingLeaseRecoveryResult result;
            try
            {
                result = _recovery.Recover(new StandingLeaseRecoveryRequest(
                    candidate.PlanId,
                    candidate.OccurrenceId,
                    candidate.LeaseId,
                    candidate.RunId,
                    candidate.LeaseUseId,
                    candidate.ScopeId,
                    candidate.ScopeDigest));
            }
            catch
            {
                result = StandingLeaseRecoveryResult.Rejected("recovery_exception");
            }

            SafeAudit(
                result.Status == StandingLeaseRecoveryStatus.Rejected
                    ? "standing_lease.recovery_failed"
                    : "standing_lease.recovered",
                new
                {
                    intent_id = candidate.IntentId,
                    run_id = candidate.RunId,
                    reason_code = result.Reason,
                    changed = result.Changed,
                });

            if (result.Status == StandingLeaseRecoveryStatus.Rejected)
                success = false;
        }

        return success;
    }

    private void SafeAudit(string eventName, object payload)
    {
        try { _audit(eventName, payload); } catch { }
    }
}
