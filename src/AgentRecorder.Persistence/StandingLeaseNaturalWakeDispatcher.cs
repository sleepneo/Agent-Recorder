using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Persistence;

/// <summary>
/// One-shot natural-wake dispatch boundary. It evaluates the durable window,
/// creates process-local execution IDs only after an Eligible handoff, and
/// invokes the existing coordinator exactly once. It does not implement a
/// scheduler or any wake mechanism.
/// </summary>
internal sealed class StandingLeaseNaturalWakeDispatcher
{
    private readonly StandingLeaseWindowEligibilityService _eligibility;
    private readonly Func<
        StandingLeaseOneShotExecutionRequest,
        CancellationToken,
        Task<StandingLeaseOneShotExecutionResult>> _coordinator;
    private readonly Func<StandingLeaseGeneratedExecutionIds> _idGenerator;
    private readonly Func<
        StandingLeaseOneShotExecutionRequest,
        StandingLeaseNaturalWakeConcurrencyRevalidationResult> _revalidateAfterConcurrency;
    private readonly Action<string, object> _audit;

    internal StandingLeaseNaturalWakeDispatcher(SqliteOperationalStore store)
        : this(
            new StandingLeaseWindowEligibilityService(store),
            new StandingLeaseOneShotExecutionCoordinator(store).ExecuteAsync,
            GenerateExecutionIds,
            new AuditLogger().Log,
            revalidateAfterConcurrencyForTest: null)
    {
    }

    internal StandingLeaseNaturalWakeDispatcher(
        StandingLeaseWindowEligibilityService eligibility,
        Func<
            StandingLeaseOneShotExecutionRequest,
            CancellationToken,
            Task<StandingLeaseOneShotExecutionResult>> coordinator,
        Func<StandingLeaseGeneratedExecutionIds> idGenerator,
        Action<string, object>? auditForTest = null,
        Func<
            StandingLeaseOneShotExecutionRequest,
            StandingLeaseNaturalWakeConcurrencyRevalidationResult>? revalidateAfterConcurrencyForTest = null)
    {
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _revalidateAfterConcurrency = revalidateAfterConcurrencyForTest ?? eligibility.RevalidateAfterConcurrency;
        _audit = auditForTest ?? new AuditLogger().Log;
    }

    internal Task<StandingLeaseNaturalWakeDispatchResult> DispatchAsync(
        StandingLeaseWindowEligibilityRequest? request,
        CancellationToken cancellationToken = default) =>
        DispatchCoreAsync(request, cancellationToken, _eligibility.Evaluate);

    internal Task<StandingLeaseNaturalWakeDispatchResult> DispatchWithEligibilityAsync(
        StandingLeaseWindowEligibilityRequest? request,
        CancellationToken cancellationToken,
        Func<StandingLeaseWindowEligibilityRequest?, StandingLeaseWindowEligibilityResult> eligibilityEvaluator)
    {
        ArgumentNullException.ThrowIfNull(eligibilityEvaluator);
        return DispatchCoreAsync(request, cancellationToken, eligibilityEvaluator);
    }

    private async Task<StandingLeaseNaturalWakeDispatchResult> DispatchCoreAsync(
        StandingLeaseWindowEligibilityRequest? request,
        CancellationToken cancellationToken,
        Func<StandingLeaseWindowEligibilityRequest?, StandingLeaseWindowEligibilityResult> eligibilityEvaluator)
    {
        var eligibilityResult = eligibilityEvaluator(request);
        if (eligibilityResult.Status != StandingLeaseWindowEligibilityStatus.Eligible)
        {
            AuditSafetyBlock(eligibilityResult);
            return StandingLeaseNaturalWakeDispatchResult.FromEligibility(eligibilityResult);
        }

        var handoff = eligibilityResult.Handoff;
        if (handoff is null ||
            request is null ||
            !HasMatchingIdentity(request, handoff))
        {
            return StandingLeaseNaturalWakeDispatchResult.Rejected(
                eligibilityResult,
                "natural_wake_eligibility_handoff_missing");
        }

        StandingLeaseGeneratedExecutionIds generatedIds;
        try
        {
            generatedIds = _idGenerator();
        }
        catch
        {
            return StandingLeaseNaturalWakeDispatchResult.Rejected(
                eligibilityResult,
                "natural_wake_id_generation_failed");
        }

        if (!HasValidGeneratedIds(generatedIds))
        {
            return StandingLeaseNaturalWakeDispatchResult.Rejected(
                eligibilityResult,
                "natural_wake_generated_id_invalid");
        }

        var coordinatorRequest = new StandingLeaseOneShotExecutionRequest(
            handoff.PlanId,
            handoff.OccurrenceId,
            handoff.LeaseId,
            generatedIds.RunId,
            generatedIds.LeaseUseId,
            handoff.ExpectedPlanVersion,
            handoff.ExpectedOccurrenceVersion,
            handoff.ExpectedLeaseVersion,
            handoff.ScopeId,
            handoff.ScopeDigest);

        StandingLeaseOneShotExecutionResult coordinatorResult;
        try
        {
            coordinatorResult = await _coordinator(coordinatorRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StandingLeaseNaturalWakeDispatchResult.Rejected(
                eligibilityResult,
                "natural_wake_coordinator_cancelled");
        }
        catch
        {
            return StandingLeaseNaturalWakeDispatchResult.Rejected(
                eligibilityResult,
                "natural_wake_coordinator_failed");
        }

        if (coordinatorResult.Status == StandingLeaseOneShotExecutionStatus.Rejected &&
            string.Equals(coordinatorResult.Reason, "concurrency_conflict", StringComparison.Ordinal))
        {
            StandingLeaseNaturalWakeConcurrencyRevalidationResult revalidation;
            try
            {
                // This is a bounded semantic recheck only. It must not call the
                // coordinator or start gate again after the original conflict.
                revalidation = _revalidateAfterConcurrency(coordinatorRequest);
            }
            catch
            {
                revalidation = StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                    "natural_wake_concurrency_revalidation_failed");
            }

            var revalidatedResult = StandingLeaseNaturalWakeDispatchResult.FromConcurrencyRevalidation(
                eligibilityResult,
                revalidation);
            if (IsSafetyBlockReason(revalidatedResult.Reason))
            {
                SafeAudit("standing_lease.wake_blocked", new
                {
                    plan_id = revalidatedResult.PlanId,
                    occurrence_id = revalidatedResult.OccurrenceId,
                    lease_id = revalidatedResult.LeaseId,
                    reason_code = revalidatedResult.Reason,
                });
            }

            return revalidatedResult;
        }

        var dispatchResult = StandingLeaseNaturalWakeDispatchResult.FromCoordinator(
            eligibilityResult,
            coordinatorResult);
        if (IsSafetyBlockReason(dispatchResult.Reason))
        {
            SafeAudit("standing_lease.wake_blocked", new
            {
                plan_id = dispatchResult.PlanId,
                occurrence_id = dispatchResult.OccurrenceId,
                lease_id = dispatchResult.LeaseId,
                reason_code = dispatchResult.Reason,
            });
        }

        return dispatchResult;
    }

    private void AuditSafetyBlock(StandingLeaseWindowEligibilityResult result)
    {
        if (!IsSafetyBlockReason(result.Reason))
        {
            return;
        }

        SafeAudit("standing_lease.wake_blocked", new
        {
            plan_id = result.PlanId,
            occurrence_id = result.OccurrenceId,
            lease_id = result.LeaseId,
            reason_code = result.Reason,
        });
    }

    private void SafeAudit(string eventName, object payload)
    {
        try
        {
            _audit(eventName, payload);
        }
        catch
        {
            // Audit must not turn a fail-closed safety decision into execution.
        }
    }

    private static bool IsSafetyBlockReason(string? reason) => reason is
        StandingLeaseSafetyReasonCodes.UnattendedDisabled or
        StandingLeaseSafetyReasonCodes.StopAllActive or
        StandingLeaseSafetyReasonCodes.LeaseRevoked or
        StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization;

    private static bool HasMatchingIdentity(
        StandingLeaseWindowEligibilityRequest request,
        StandingLeaseWindowEligibilityHandoff handoff) =>
        string.Equals(request.PlanId, handoff.PlanId, StringComparison.Ordinal) &&
        string.Equals(request.OccurrenceId, handoff.OccurrenceId, StringComparison.Ordinal) &&
        string.Equals(request.LeaseId, handoff.LeaseId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeId, handoff.ScopeId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeDigest, handoff.ScopeDigest, StringComparison.Ordinal);

    private static bool HasValidGeneratedIds(StandingLeaseGeneratedExecutionIds generatedIds) =>
        IsCanonicalId(generatedIds.RunId) &&
        IsCanonicalId(generatedIds.LeaseUseId) &&
        !string.Equals(generatedIds.RunId, generatedIds.LeaseUseId, StringComparison.Ordinal);

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    internal static StandingLeaseGeneratedExecutionIds GenerateExecutionIds() =>
        new(
            "run_" + Guid.NewGuid().ToString("N"),
            "use_" + Guid.NewGuid().ToString("N"));
}

/// <summary>
/// Internal generated IDs. Callers cannot supply these through the dispatcher
/// request; the production generator creates them only after eligibility.
/// </summary>
internal sealed record StandingLeaseGeneratedExecutionIds(
    string RunId,
    string LeaseUseId);

internal enum StandingLeaseNaturalWakeConcurrencyRevalidationStatus
{
    Unresolved,
    LeaseRevoked,
    AlreadyClaimed,
    AlreadyCommitted,
    Rejected,
}

internal sealed class StandingLeaseNaturalWakeConcurrencyRevalidationResult
{
    private StandingLeaseNaturalWakeConcurrencyRevalidationResult(
        StandingLeaseNaturalWakeConcurrencyRevalidationStatus status,
        string reason)
    {
        Status = status;
        Reason = reason;
    }

    internal StandingLeaseNaturalWakeConcurrencyRevalidationStatus Status { get; }

    internal string Reason { get; }

    internal static StandingLeaseNaturalWakeConcurrencyRevalidationResult Unresolved() =>
        new(
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.Unresolved,
            "concurrency_conflict");

    internal static StandingLeaseNaturalWakeConcurrencyRevalidationResult LeaseRevoked() =>
        new(
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.LeaseRevoked,
            StandingLeaseSafetyReasonCodes.LeaseRevoked);

    internal static StandingLeaseNaturalWakeConcurrencyRevalidationResult AlreadyClaimed() =>
        new(
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.AlreadyClaimed,
            "occurrence_already_claimed");

    internal static StandingLeaseNaturalWakeConcurrencyRevalidationResult AlreadyCommitted() =>
        new(
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.AlreadyCommitted,
            "already_committed");

    internal static StandingLeaseNaturalWakeConcurrencyRevalidationResult Rejected(string reason) =>
        new(
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.Rejected,
            string.IsNullOrWhiteSpace(reason)
                ? "natural_wake_concurrency_revalidation_failed"
                : reason);
}

internal enum StandingLeaseNaturalWakeDispatchStatus
{
    BeforeWindow,
    Expired,
    AlreadyClaimed,
    AlreadyTerminal,
    Started,
    AlreadyCommitted,
    CommittedNotStarted,
    Rejected,
}

/// <summary>
/// Controlled natural-wake result. It carries durable authorization identity
/// and, for Started, the existing lifecycle ownership boundary only. It does
/// not expose Run/Use IDs, proof, ticket, config, scope geometry, environment,
/// backend, or native handles.
/// </summary>
internal sealed class StandingLeaseNaturalWakeDispatchResult
{
    private StandingLeaseNaturalWakeDispatchResult(
        StandingLeaseNaturalWakeDispatchStatus status,
        string reason,
        StandingLeaseWindowEligibilityResult eligibilityResult,
        IStandingLeaseCaptureLifecycleSession? lifecycleSession)
    {
        Status = status;
        Reason = reason;
        PlanId = eligibilityResult.PlanId;
        OccurrenceId = eligibilityResult.OccurrenceId;
        LeaseId = eligibilityResult.LeaseId;
        ScopeId = eligibilityResult.ScopeId;
        ScopeDigest = eligibilityResult.ScopeDigest;
        Changed = eligibilityResult.Changed;
        LifecycleSession = lifecycleSession;
    }

    internal StandingLeaseNaturalWakeDispatchStatus Status { get; }

    internal string Reason { get; }

    internal string? PlanId { get; }

    internal string? OccurrenceId { get; }

    internal string? LeaseId { get; }

    internal string? ScopeId { get; }

    internal string? ScopeDigest { get; }

    internal bool Changed { get; }

    internal IStandingLeaseCaptureLifecycleSession? LifecycleSession { get; }

    internal static StandingLeaseNaturalWakeDispatchResult FromEligibility(
        StandingLeaseWindowEligibilityResult eligibilityResult)
    {
        var status = eligibilityResult.Status switch
        {
            StandingLeaseWindowEligibilityStatus.BeforeWindow => StandingLeaseNaturalWakeDispatchStatus.BeforeWindow,
            StandingLeaseWindowEligibilityStatus.Expired => StandingLeaseNaturalWakeDispatchStatus.Expired,
            StandingLeaseWindowEligibilityStatus.AlreadyClaimed => StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed,
            StandingLeaseWindowEligibilityStatus.AlreadyTerminal => StandingLeaseNaturalWakeDispatchStatus.AlreadyTerminal,
            StandingLeaseWindowEligibilityStatus.Rejected => StandingLeaseNaturalWakeDispatchStatus.Rejected,
            _ => StandingLeaseNaturalWakeDispatchStatus.Rejected,
        };
        var reason = status == StandingLeaseNaturalWakeDispatchStatus.Rejected &&
                     eligibilityResult.Status == StandingLeaseWindowEligibilityStatus.Eligible
            ? "natural_wake_eligibility_handoff_missing"
            : eligibilityResult.Reason;
        return new(status, reason, eligibilityResult, lifecycleSession: null);
    }

    internal static StandingLeaseNaturalWakeDispatchResult Rejected(
        StandingLeaseWindowEligibilityResult eligibilityResult,
        string reason) =>
        new(
            StandingLeaseNaturalWakeDispatchStatus.Rejected,
            reason,
            eligibilityResult,
            lifecycleSession: null);

    internal static StandingLeaseNaturalWakeDispatchResult FromCoordinator(
        StandingLeaseWindowEligibilityResult eligibilityResult,
        StandingLeaseOneShotExecutionResult? coordinatorResult)
    {
        if (coordinatorResult is null)
        {
            return Rejected(eligibilityResult, "natural_wake_coordinator_result_missing");
        }

        return coordinatorResult.Status switch
        {
            StandingLeaseOneShotExecutionStatus.Started => new(
                StandingLeaseNaturalWakeDispatchStatus.Started,
                string.IsNullOrWhiteSpace(coordinatorResult.Reason) ? "started" : coordinatorResult.Reason,
                eligibilityResult,
                coordinatorResult.LifecycleSession),
            StandingLeaseOneShotExecutionStatus.AlreadyCommitted => new(
                StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted,
                string.IsNullOrWhiteSpace(coordinatorResult.Reason) ? "already_committed" : coordinatorResult.Reason,
                eligibilityResult,
                lifecycleSession: null),
            StandingLeaseOneShotExecutionStatus.CommittedNotStarted => new(
                StandingLeaseNaturalWakeDispatchStatus.CommittedNotStarted,
                string.IsNullOrWhiteSpace(coordinatorResult.Reason) ? "committed_not_started" : coordinatorResult.Reason,
                eligibilityResult,
                lifecycleSession: null),
            StandingLeaseOneShotExecutionStatus.Rejected when IsClaimRace(coordinatorResult.Reason) => new(
                StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed,
                "occurrence_already_claimed",
                eligibilityResult,
                lifecycleSession: null),
            StandingLeaseOneShotExecutionStatus.Rejected => new(
                StandingLeaseNaturalWakeDispatchStatus.Rejected,
                string.IsNullOrWhiteSpace(coordinatorResult.Reason) ? "natural_wake_coordinator_rejected" : coordinatorResult.Reason,
                eligibilityResult,
                lifecycleSession: null),
            _ => Rejected(eligibilityResult, "natural_wake_coordinator_status_unknown"),
        };
    }

    internal static StandingLeaseNaturalWakeDispatchResult FromConcurrencyRevalidation(
        StandingLeaseWindowEligibilityResult eligibilityResult,
        StandingLeaseNaturalWakeConcurrencyRevalidationResult revalidation)
    {
        return revalidation.Status switch
        {
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.LeaseRevoked =>
                Rejected(eligibilityResult, StandingLeaseSafetyReasonCodes.LeaseRevoked),
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.AlreadyClaimed => new(
                StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed,
                "occurrence_already_claimed",
                eligibilityResult,
                lifecycleSession: null),
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.AlreadyCommitted => new(
                StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted,
                "already_committed",
                eligibilityResult,
                lifecycleSession: null),
            StandingLeaseNaturalWakeConcurrencyRevalidationStatus.Rejected =>
                Rejected(eligibilityResult, revalidation.Reason),
            _ => Rejected(eligibilityResult, "concurrency_conflict"),
        };
    }

    private static bool IsClaimRace(string? reason) =>
        string.Equals(reason, "start_gate_conflict", StringComparison.Ordinal) ||
        string.Equals(reason, "occurrence_already_claimed", StringComparison.Ordinal);
}
