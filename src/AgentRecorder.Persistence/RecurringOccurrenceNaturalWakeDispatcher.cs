using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Persistence;

/// <summary>
/// One-shot handoff from a durable natural-wake candidate to the existing
/// recurring execution coordinator.  This class is not a scheduler and owns
/// no retry or recovery policy.
/// </summary>
internal sealed class RecurringOccurrenceNaturalWakeDispatcher
{
    private readonly Func<
        RecurringOccurrenceExecutionRequest,
        CancellationToken,
        Task<RecurringOccurrenceExecutionResult>> _coordinator;
    private readonly Action<string, object> _audit;

    internal RecurringOccurrenceNaturalWakeDispatcher(SqliteOperationalStore store)
        : this(
            new RecurringOccurrenceExecutionCoordinator(store).ExecuteAsync,
            auditForTest: null)
    {
    }

    internal RecurringOccurrenceNaturalWakeDispatcher(
        Func<
            RecurringOccurrenceExecutionRequest,
            CancellationToken,
            Task<RecurringOccurrenceExecutionResult>> coordinator,
        Action<string, object>? auditForTest = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _audit = auditForTest ?? new AuditLogger().Log;
    }

    internal async Task<RecurringOccurrenceNaturalWakeDispatchResult> DispatchAsync(
        RecurringOccurrenceNaturalWakeCandidate? candidate,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                candidate,
                RecurringOccurrenceNaturalWakeDispatchResult.Cancelled(
                    candidate,
                    "natural_wake_cancelled_before_coordinator"));
        }

        if (candidate is null ||
            !IsCanonicalId(candidate.LeaseId) ||
            candidate.Slot is null ||
            !IsCanonicalId(candidate.Slot.PlanId) ||
            !IsCanonicalId(candidate.Slot.OccurrenceIdentity))
        {
            return Complete(
                candidate,
                RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "natural_wake_candidate_invalid"));
        }

        RecurringOccurrenceExecutionResult coordinatorResult;
        try
        {
            coordinatorResult = await _coordinator(
                    new RecurringOccurrenceExecutionRequest(candidate.LeaseId, candidate.Slot),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                candidate,
                RecurringOccurrenceNaturalWakeDispatchResult.Cancelled(
                    candidate,
                    "natural_wake_coordinator_cancelled"));
        }
        catch
        {
            return Complete(
                candidate,
                RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "natural_wake_coordinator_failed"));
        }

        return Complete(
            candidate,
            RecurringOccurrenceNaturalWakeDispatchResult.FromCoordinator(candidate, coordinatorResult));
    }

    private RecurringOccurrenceNaturalWakeDispatchResult Complete(
        RecurringOccurrenceNaturalWakeCandidate? candidate,
        RecurringOccurrenceNaturalWakeDispatchResult result)
    {
        SafeAudit("recurring_occurrence.natural_wake_dispatch", new
        {
            plan_id = result.PlanId ?? candidate?.Slot.PlanId,
            lease_id = result.LeaseId ?? candidate?.LeaseId,
            occurrence_identity = result.OccurrenceIdentity ?? candidate?.Slot.OccurrenceIdentity,
            occurrence_id = result.OccurrenceId,
            run_id = result.RunId,
            use_id = result.UseId,
            result_status = ToStableStatus(result.Status),
            reason = result.Reason,
        });
        return result;
    }

    private void SafeAudit(string eventName, object payload)
    {
        try
        {
            _audit(eventName, payload);
        }
        catch
        {
            // Audit is observational only and must never change the durable
            // coordinator result or the lifecycle ownership transfer.
        }
    }

    private static string ToStableStatus(RecurringOccurrenceExecutionStatus status) => status switch
    {
        RecurringOccurrenceExecutionStatus.Started => "started",
        RecurringOccurrenceExecutionStatus.CompletedDuringStart => "completed_during_start",
        RecurringOccurrenceExecutionStatus.NotDue => "not_due",
        RecurringOccurrenceExecutionStatus.BeforeWindow => "before_window",
        RecurringOccurrenceExecutionStatus.Paused => "paused",
        RecurringOccurrenceExecutionStatus.Missed => "missed",
        RecurringOccurrenceExecutionStatus.Cancelled => "cancelled",
        RecurringOccurrenceExecutionStatus.Superseded => "superseded",
        RecurringOccurrenceExecutionStatus.AlreadyTerminal => "already_terminal",
        RecurringOccurrenceExecutionStatus.AlreadyClaimed => "already_claimed",
        RecurringOccurrenceExecutionStatus.AlreadyAdvanced => "already_advanced",
        RecurringOccurrenceExecutionStatus.Blocked => "blocked",
        RecurringOccurrenceExecutionStatus.CommittedNotStarted => "committed_not_started",
        RecurringOccurrenceExecutionStatus.Rejected => "rejected",
        RecurringOccurrenceExecutionStatus.Conflict => "conflict",
        _ => "unknown",
    };

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}

/// <summary>
/// Thin result projection that preserves the coordinator status and reason.
/// Only a Started result may carry the exact lifecycle owner.
/// </summary>
internal sealed class RecurringOccurrenceNaturalWakeDispatchResult
{
    private RecurringOccurrenceNaturalWakeDispatchResult(
        RecurringOccurrenceExecutionStatus status,
        string reason,
        string? planId,
        string? leaseId,
        string? occurrenceIdentity,
        string? occurrenceId,
        string? runId,
        string? useId,
        IRecurringLeaseCaptureLifecycleSession? lifecycleSession)
    {
        Status = status;
        Reason = reason;
        PlanId = planId;
        LeaseId = leaseId;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        RunId = runId;
        UseId = useId;
        LifecycleSession = lifecycleSession;
    }

    internal RecurringOccurrenceExecutionStatus Status { get; }
    internal string Reason { get; }
    internal string? PlanId { get; }
    internal string? LeaseId { get; }
    internal string? OccurrenceIdentity { get; }
    internal string? OccurrenceId { get; }
    internal string? RunId { get; }
    internal string? UseId { get; }
    internal IRecurringLeaseCaptureLifecycleSession? LifecycleSession { get; }

    internal static RecurringOccurrenceNaturalWakeDispatchResult FromCoordinator(
        RecurringOccurrenceNaturalWakeCandidate candidate,
        RecurringOccurrenceExecutionResult? coordinatorResult)
    {
        if (coordinatorResult is null)
        {
            return Rejected(candidate, "natural_wake_coordinator_result_missing");
        }

        if (coordinatorResult.Status == RecurringOccurrenceExecutionStatus.Started)
        {
            if (coordinatorResult.LifecycleSession is null)
                return Rejected(candidate, "natural_wake_started_owner_missing");

            return new(
                RecurringOccurrenceExecutionStatus.Started,
                coordinatorResult.Reason,
                coordinatorResult.PlanId ?? candidate.Slot.PlanId,
                coordinatorResult.LeaseId ?? candidate.LeaseId,
                coordinatorResult.OccurrenceIdentity ?? candidate.Slot.OccurrenceIdentity,
                coordinatorResult.OccurrenceId,
                coordinatorResult.RunId,
                coordinatorResult.UseId,
                coordinatorResult.LifecycleSession);
        }

        if (coordinatorResult.LifecycleSession is not null)
        {
            try
            {
                coordinatorResult.LifecycleSession.Dispose();
            }
            catch
            {
                // The result is already being rejected. Disposal is best
                // effort because an invalid non-Started owner must never be
                // allowed to cross the dispatcher boundary.
            }

            return Rejected(candidate, "natural_wake_non_started_owner_present");
        }

        return new(
            coordinatorResult.Status,
            coordinatorResult.Reason,
            coordinatorResult.PlanId ?? candidate.Slot.PlanId,
            coordinatorResult.LeaseId ?? candidate.LeaseId,
            coordinatorResult.OccurrenceIdentity ?? candidate.Slot.OccurrenceIdentity,
            coordinatorResult.OccurrenceId,
            coordinatorResult.RunId,
            coordinatorResult.UseId,
            lifecycleSession: null);
    }

    internal static RecurringOccurrenceNaturalWakeDispatchResult Cancelled(
        RecurringOccurrenceNaturalWakeCandidate? candidate,
        string reason) =>
        new(
            RecurringOccurrenceExecutionStatus.Cancelled,
            reason,
            candidate?.Slot.PlanId,
            candidate?.LeaseId,
            candidate?.Slot.OccurrenceIdentity,
            occurrenceId: null,
            runId: null,
            useId: null,
            lifecycleSession: null);

    internal static RecurringOccurrenceNaturalWakeDispatchResult Rejected(
        RecurringOccurrenceNaturalWakeCandidate? candidate,
        string reason) =>
        new(
            RecurringOccurrenceExecutionStatus.Rejected,
            reason,
            candidate?.Slot.PlanId,
            candidate?.LeaseId,
            candidate?.Slot.OccurrenceIdentity,
            occurrenceId: null,
            runId: null,
            useId: null,
            lifecycleSession: null);
}
