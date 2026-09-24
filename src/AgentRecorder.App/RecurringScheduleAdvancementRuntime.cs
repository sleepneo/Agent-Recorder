using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

/// <summary>
/// Owns bounded recurring cursor advancement. It creates only the next slot;
/// durable setup, authorization, occurrence and capture transitions remain in
/// their existing persistence owners.
/// </summary>
internal sealed class RecurringScheduleAdvancementRuntime
{
    internal const int CandidatePageSize = 32;
    internal const int MaximumPagesPerLoopTick = 2;
    internal const int MaximumStartupPages = 8;
    internal const int MaximumPlansPerPass = 8;
    internal const int MaximumTransitionsPerPlanPerPass = 8;

    private readonly RecurringAdvancementCandidateQuery _query;
    private readonly SqliteRecurringAdvancementTransaction _advancement;
    private readonly Action<string, object> _audit;
    private readonly object _sync = new();
    private readonly LinkedList<RecurringAdvancementCandidate> _pending = new();
    private readonly Dictionary<string, LinkedListNode<RecurringAdvancementCandidate>> _pendingByIntent = new(StringComparer.Ordinal);
    private DateTimeOffset? _afterRequestedAtUtc;
    private string? _afterSetupIntentId;

    internal RecurringScheduleAdvancementRuntime(
        SqliteOperationalStore store,
        Action<string, object>? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _query = new RecurringAdvancementCandidateQuery(store);
        _advancement = new SqliteRecurringAdvancementTransaction(store);
        _audit = audit ?? new AuditLogger().Log;
    }

    internal bool RunStartupPass(
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        CancellationToken cancellationToken = default) =>
        RunPass(nowUtc, currentUserSid, sessionBinding, MaximumStartupPages, cancellationToken);

    internal bool RunLoopPass(
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        CancellationToken cancellationToken) =>
        RunPass(nowUtc, currentUserSid, sessionBinding, MaximumPagesPerLoopTick, cancellationToken);

    private bool RunPass(
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        int maximumPages,
        CancellationToken cancellationToken)
    {
        if (nowUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(currentUserSid) ||
            currentUserSid != currentUserSid.Trim() || string.IsNullOrWhiteSpace(sessionBinding) ||
            sessionBinding != sessionBinding.Trim())
        {
            SafeAudit("recurring_lease.advancement_query_failed", new
            {
                reason_code = "recurring_advancement_runtime_input_invalid",
            });
            return false;
        }

        lock (_sync)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var priority = _query.ListPage(
                    nowUtc, currentUserSid, sessionBinding,
                    afterRequestedAtUtc: null, afterSetupIntentId: null, CandidatePageSize);
                if (_scanAfterIsUnset() && priority.HasMore)
                {
                    _afterRequestedAtUtc = priority.LastRequestedAtUtc;
                    _afterSetupIntentId = priority.LastSetupIntentId;
                }
                foreach (var candidate in priority.Candidates)
                    Enqueue(candidate);
                if (_pending.Count == 0 && _afterRequestedAtUtc is not null)
                    FillPending(Math.Max(0, maximumPages - 1), nowUtc, currentUserSid, sessionBinding, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                SafeAudit("recurring_lease.advancement_query_failed", new
                {
                    reason_code = "recurring_advancement_candidate_query_failed",
                });
                return false;
            }

            var processed = 0;
            while (processed < MaximumPlansPerPass && _pending.First is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = _pending.First!;
                var candidate = node.Value;
                _pending.RemoveFirst();
                _pendingByIntent.Remove(candidate.SetupIntentId);
                try
                {
                    var continuation = AdvanceCandidate(
                        candidate, nowUtc, currentUserSid, sessionBinding, cancellationToken);
                    if (continuation is not null)
                        Enqueue(continuation, first: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Enqueue(candidate, first: true);
                    throw;
                }
                catch
                {
                    SafeAudit("recurring_lease.advancement_failed", new
                    {
                        reason_code = "recurring_advancement_transaction_failed",
                    });
                    return false;
                }
                processed++;
            }
            return true;
        }
    }

    private void FillPending(
        int maximumPages,
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        CancellationToken cancellationToken)
    {
        for (var pageNumber = 0; _pending.Count == 0 && _afterRequestedAtUtc is not null && pageNumber < maximumPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _query.ListPage(
                nowUtc,
                currentUserSid,
                sessionBinding,
                _afterRequestedAtUtc,
                _afterSetupIntentId,
                CandidatePageSize);
            if (page.HasMore)
            {
                _afterRequestedAtUtc = page.LastRequestedAtUtc;
                _afterSetupIntentId = page.LastSetupIntentId;
            }
            else
            {
                _afterRequestedAtUtc = null;
                _afterSetupIntentId = null;
            }

            foreach (var candidate in page.Candidates)
                _pending.AddLast(candidate);
            if (_pending.Count != 0)
                return;
            if (!page.HasMore)
                return;
        }
    }

    private bool _scanAfterIsUnset() => _afterRequestedAtUtc is null;

    private void Enqueue(RecurringAdvancementCandidate candidate, bool first = false)
    {
        if (_pendingByIntent.TryGetValue(candidate.SetupIntentId, out var existing))
        {
            if (candidate.ExpectedCursorVersion >= existing.Value.ExpectedCursorVersion)
                existing.Value = candidate;
            return;
        }

        var node = first ? _pending.AddFirst(candidate) : _pending.AddLast(candidate);
        _pendingByIntent.Add(candidate.SetupIntentId, node);
    }

    private RecurringAdvancementCandidate? AdvanceCandidate(
        RecurringAdvancementCandidate candidate,
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        CancellationToken cancellationToken)
    {
        var transitions = 0;
        var staleRereads = 0;
        while (transitions < MaximumTransitionsPerPlanPerPass)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationId = DeriveOperationId(candidate.PlanId, candidate.ScheduleRevision, candidate.ExpectedCursorVersion);
            RecurringAdvancementOperationSnapshot? operation;
            bool wasReplay;
            try
            {
                operation = _advancement.AdvanceOneForRuntime(candidate, operationId, nowUtc, out wasReplay);
            }
            catch (Phase3PersistenceException exception) when (
                exception.Code == RecurringPersistenceReasonCodes.CursorStale && staleRereads == 0)
            {
                staleRereads++;
                var refreshed = _query.ReadCandidate(
                    candidate.SetupIntentId, nowUtc, currentUserSid, sessionBinding);
                if (refreshed is null)
                    return null;
                candidate = refreshed;
                continue;
            }

            if (operation is null)
                return null;

            if (wasReplay)
            {
                SafeAudit("recurring_lease.advancement_replayed", new
                {
                    plan_id = candidate.PlanId,
                    schedule_revision = candidate.ScheduleRevision,
                    expected_cursor_version = candidate.ExpectedCursorVersion,
                    operation_id = operation.OperationId,
                });
            }

            transitions++;
            switch (operation.ResultCode)
            {
                case "scheduled":
                    SafeAudit("recurring_lease.advancement_materialized", new
                    {
                        plan_id = candidate.PlanId,
                        schedule_revision = candidate.ScheduleRevision,
                        cursor_version = operation.ResultCursorVersion,
                        occurrence_identity = operation.OccurrenceIdentity,
                    });
                    return null;
                case "skipped":
                    SafeAudit("recurring_lease.advancement_skipped", new
                    {
                        plan_id = candidate.PlanId,
                        schedule_revision = candidate.ScheduleRevision,
                        cursor_version = operation.ResultCursorVersion,
                        occurrence_identity = operation.OccurrenceIdentity,
                    });
                    candidate = candidate with
                    {
                        ExpectedCursorVersion = operation.ResultCursorVersion,
                        PreviousResultCode = operation.ResultCode,
                        PreviousOccurrenceIdentity = operation.OccurrenceIdentity,
                        PreviousOccurrenceStatusCode = null,
                    };
                    break;
                case "exhausted":
                    SafeAudit("recurring_lease.advancement_exhausted", new
                    {
                        plan_id = candidate.PlanId,
                        schedule_revision = candidate.ScheduleRevision,
                        cursor_version = operation.ResultCursorVersion,
                    });
                    return null;
                default:
                    throw new Phase3PersistenceException(
                        RecurringPersistenceReasonCodes.OperationRelationInvalid,
                        "The recurring advancement returned an unknown result code.");
            }
        }

        // A run of DST-skipped wall-clock slots is allowed to make progress,
        // but continuation is deferred to the next poll instead of spinning.
        return candidate;
    }

    private static string DeriveOperationId(string planId, long scheduleRevision, long expectedCursorVersion)
    {
        var canonical = string.Concat(
            planId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "#", planId, "#",
            scheduleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), "#",
            expectedCursorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return "recurring-advancement-op:" + digest;
    }

    private void SafeAudit(string eventName, object payload)
    {
        try { _audit(eventName, payload); } catch { }
    }
}
