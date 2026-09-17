using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal orchestration for one recurring occurrence attempt. This class
/// owns ordering and ownership transfer only; every durable state transition,
/// authorization check and capture-start invariant remains in its existing
/// service or transaction.
/// </summary>
internal sealed class RecurringOccurrenceExecutionCoordinator
{
    private readonly SqliteOperationalStore _store;
    private readonly SqlitePeriodicOccurrenceDueProjectionTransaction _dueProjection;
    private readonly RecurringOccurrenceEnvironmentRecheckService _environmentRecheck;
    private readonly RecurringOccurrenceReservationService _reservation;
    private readonly RecurringOccurrenceStartCommitService _startCommit;
    private readonly SqliteRecurringLeaseExecutionSnapshotLoader _snapshotLoader;
    private readonly RecurringLeaseCaptureExecutionBridge _executionBridge;
    private readonly RecurringLeaseImmediateRecoveryService _immediateRecovery;
    private readonly Action<RecurringOccurrenceExecutionStage>? _stageHookForTest;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _clockGate = new();
    private DateTimeOffset? _lastTrustedUtcNow;

    internal RecurringOccurrenceExecutionCoordinator(SqliteOperationalStore store)
        : this(
            store,
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null,
            startSafetyInterlockForTest: null,
            currentSafetyValidatorForTest: null,
            lifecycleSessionFactoryForTest: null,
            currentUserSidForTest: null,
            sessionBindingForTest: null)
    {
    }

    // The optional dependencies are internal composition/test seams. No
    // caller request can supply time, identity, environment, safety, backend,
    // lifecycle, proof, ticket, or capture configuration.
    internal RecurringOccurrenceExecutionCoordinator(
        SqliteOperationalStore store,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest,
        Func<RecurringOccurrenceExecutionSpecification, ICaptureBackend?>? backendFactoryForTest,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null,
        StandingLeaseStartSafetyInterlock? startSafetyInterlockForTest = null,
        Func<RecurringLeaseCaptureExecutionTicket, DateTimeOffset, RecurringLeaseCurrentSafetyDecision>? currentSafetyValidatorForTest = null,
        Func<RecurringLeaseCaptureExecutionTicket, ICaptureBackend, IRecurringLeaseCaptureLifecycleSession?>? lifecycleSessionFactoryForTest = null,
        Func<string>? currentUserSidForTest = null,
        Func<string>? sessionBindingForTest = null,
        Func<string>? runIdFactoryForTest = null,
        Func<string>? useIdFactoryForTest = null,
        Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? recheckFailureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? recheckBeforeFinalReadbackForTest = null,
        Action<SqliteConnection, SqliteTransaction>? recheckAfterFirstRecheckingCasForTest = null,
        Action<RecurringOccurrenceReservationFailurePoint>? reservationFailureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? reservationBeforeFinalReadbackForTest = null,
        Action<RecurringOccurrenceStartCommitFailurePoint>? startCommitFailureHookForTest = null,
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? recurringProofIssuerForTest = null,
        Action<RecurringLeaseRestartRecoveryFailurePoint>? recoveryFailureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? recoveryBeforeCommitForTest = null,
        Action<SqliteConnection, SqliteTransaction>? recoveryAfterExactReadForTest = null,
        Action<RecurringOccurrenceExecutionStage>? stageHookForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _utcNow = utcNowForTest is null
            ? static () => DateTimeOffset.UtcNow
            : () => utcNowForTest() ?? throw new InvalidOperationException("The trusted recurring clock returned null.");

        _dueProjection = new SqlitePeriodicOccurrenceDueProjectionTransaction(store);
        _environmentRecheck = new RecurringOccurrenceEnvironmentRecheckService(
            store,
            _utcNow,
            environmentProviderForTest,
            recheckFailureHookForTest,
            recheckBeforeFinalReadbackForTest,
            recheckAfterFirstRecheckingCasForTest);
        _reservation = new RecurringOccurrenceReservationService(
            store,
            _utcNow,
            runIdFactoryForTest,
            useIdFactoryForTest,
            reservationFailureHookForTest,
            reservationBeforeFinalReadbackForTest);
        _startCommit = new RecurringOccurrenceStartCommitService(
            store,
            _utcNow,
            environmentProviderForTest,
            startCommitFailureHookForTest,
            recurringProofIssuerForTest: recurringProofIssuerForTest);
        _snapshotLoader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest,
            _utcNow);

        var safetyInterlock = startSafetyInterlockForTest ?? new StandingLeaseStartSafetyInterlock();
        var currentSafetyValidator = currentSafetyValidatorForTest ??
            new SqliteRecurringLeaseCurrentSafetyValidator(store).Validate;
        _executionBridge = new RecurringLeaseCaptureExecutionBridge(
            environmentProviderForTest,
            backendFactoryForTest,
            delayForTest,
            () => _utcNow(),
            safetyInterlock,
            currentSafetyValidator,
            lifecycleSessionFactoryForTest ?? ((ticket, backend) =>
                new RecurringLeaseCaptureExecutionSession(
                    _store,
                    ticket,
                    backend,
                    () => _utcNow())));

        _immediateRecovery = new RecurringLeaseImmediateRecoveryService(
            store,
            currentUserSidForTest,
            sessionBindingForTest,
            recoveryFailureHookForTest,
            recoveryBeforeCommitForTest,
            recoveryAfterExactReadForTest);
        _stageHookForTest = stageHookForTest;
    }

    internal async Task<RecurringOccurrenceExecutionResult> ExecuteAsync(
        RecurringOccurrenceExecutionRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRequest(request, out var requestFailure))
            return RecurringOccurrenceExecutionResult.Rejected(requestFailure);

        var validRequest = request!;
        var candidate = validRequest.Candidate!;
        var identity = ResultIdentity(validRequest);

        if (cancellationToken.IsCancellationRequested)
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Cancelled, "recurring_execution_cancelled");

        if (!TryReadTrustedUtc(out var dueAtUtc, out var clockFailure))
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, clockFailure);

        PeriodicOccurrenceDueProjectionResult due;
        try
        {
            due = _dueProjection.Project(candidate, dueAtUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapPersistenceFailure(identity, exception.Code);
        }
        catch
        {
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_due_projection_failed");
        }

        // The candidate overload has compared the plan identity and every
        // immutable slot fact inside the same due transaction. Only now may
        // the result carry a plan identity derived from that durable match.
        identity = identity.WithVerifiedPlanId(candidate.PlanId);
        InvokeStage(RecurringOccurrenceExecutionStage.AfterDueProjection);

        var dueResult = MapDueBoundary(identity, due);
        if (dueResult is not null)
            return dueResult;

        if (cancellationToken.IsCancellationRequested)
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Cancelled, "recurring_execution_cancelled");

        RecurringOccurrenceEnvironmentRecheckResult recheck;
        try
        {
            recheck = _environmentRecheck.Recheck(validRequest.LeaseId, candidate.OccurrenceIdentity);
        }
        catch
        {
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_environment_recheck_failed");
        }

        switch (recheck.Status)
        {
            case RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized:
            case RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAuthorized:
            case RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAdvanced:
                break;
            case RecurringOccurrenceEnvironmentRecheckResultStatus.BeforeWindow:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.BeforeWindow, recheck.ReasonCode);
            case RecurringOccurrenceEnvironmentRecheckResultStatus.Missed:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Missed, recheck.ReasonCode);
            case RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Blocked, recheck.ReasonCode);
            case RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyTerminal:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.AlreadyTerminal, recheck.ReasonCode);
            case RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected:
                return MapPersistenceFailure(identity, recheck.ReasonCode, "recurring_environment_recheck_rejected");
            case RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Conflict, recheck.ReasonCode);
            default:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_environment_recheck_invalid");
        }

        if (cancellationToken.IsCancellationRequested)
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Cancelled, "recurring_execution_cancelled");

        RecurringOccurrenceReservationResult reservation;
        try
        {
            reservation = _reservation.Reserve(validRequest.LeaseId, candidate.OccurrenceIdentity);
        }
        catch
        {
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_occurrence_reservation_failed");
        }

        switch (reservation.Status)
        {
            case RecurringOccurrenceReservationStatus.Reserved:
            case RecurringOccurrenceReservationStatus.AlreadyReserved:
            case RecurringOccurrenceReservationStatus.AlreadyAdvanced:
                break;
            case RecurringOccurrenceReservationStatus.Conflict:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Conflict, reservation.ReasonCode);
            case RecurringOccurrenceReservationStatus.Rejected:
                if (reservation.ReasonCode == RecurringOccurrenceReservationReasonCodes.PlanNotEnabled)
                {
                    return identity.WithStatus(RecurringOccurrenceExecutionStatus.Paused, reservation.ReasonCode);
                }
                return MapPersistenceFailure(identity, reservation.ReasonCode, "recurring_occurrence_reservation_rejected");
            default:
                return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_occurrence_reservation_invalid");
        }

        InvokeStage(RecurringOccurrenceExecutionStage.AfterReservation);

        if (cancellationToken.IsCancellationRequested)
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Cancelled, "recurring_execution_cancelled");

        RecurringOccurrenceStartCommitResult startCommit;
        try
        {
            startCommit = _startCommit.Commit(validRequest.LeaseId, candidate.OccurrenceIdentity);
        }
        catch
        {
            return identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_start_commit_failed");
        }

        var startResult = MapStartCommitBoundary(identity, startCommit);
        if (startResult is not null)
            return startResult;

        InvokeStage(RecurringOccurrenceExecutionStage.AfterStartCommit);

        var runId = startCommit.RunId;
        var useId = startCommit.UseId;
        if (startCommit.Status == RecurringOccurrenceStartCommitStatus.CommittedWithoutProof)
        {
            var recoveryReason = Recover(
                startCommit.PostCommitRecoveryReceipt,
                startCommit.ReasonCode);
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        if (startCommit.Status != RecurringOccurrenceStartCommitStatus.Committed ||
            startCommit.FirstCommitProof is null)
        {
            var recoveryReason = Recover(
                startCommit.FirstCommitReceipt,
                "recurring_start_commit_proof_missing");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var recoveryReason = Recover(startCommit.FirstCommitProof, "recurring_execution_cancelled");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        RecurringLeaseCaptureAuthorization? authorization;
        string authorizationFailure;
        try
        {
            if (!_snapshotLoader.TryAuthorizeAndConsumeRecurringLeaseUse(
                    startCommit.FirstCommitProof,
                    out authorization,
                    out authorizationFailure) ||
                authorization is null)
            {
                var recoveryReason = Recover(startCommit.FirstCommitProof, authorizationFailure);
                return identity.WithStatus(
                    RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                    recoveryReason,
                    startCommit.OccurrenceId,
                    runId,
                    useId);
            }
        }
        catch
        {
            var recoveryReason = Recover(startCommit.FirstCommitProof, "recurring_execution_authorization_failed");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        InvokeStage(RecurringOccurrenceExecutionStage.AfterAuthorization);

        if (cancellationToken.IsCancellationRequested)
        {
            var recoveryReason = Recover(authorization, "recurring_execution_cancelled");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        RecurringLeaseCaptureExecutionTicket? ticket;
        string ticketFailure;
        try
        {
            if (!RecurringLeaseCaptureExecutionTicket.TryCreate(
                    authorization,
                    out ticket,
                    out ticketFailure) ||
                ticket is null)
            {
                var recoveryReason = Recover(authorization, ticketFailure);
                return identity.WithStatus(
                    RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                    recoveryReason,
                    startCommit.OccurrenceId,
                    runId,
                    useId);
            }
        }
        catch
        {
            var recoveryReason = Recover(authorization, "recurring_execution_ticket_failed");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        InvokeStage(RecurringOccurrenceExecutionStage.AfterTicket);

        if (cancellationToken.IsCancellationRequested)
        {
            var recoveryReason = Recover(ticket, "recurring_execution_cancelled");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        InvokeStage(RecurringOccurrenceExecutionStage.BeforeBridge);

        RecurringLeaseCaptureExecutionResult bridgeResult;
        try
        {
            bridgeResult = await _executionBridge
                .ExecuteAsync(ticket, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            var recoveryReason = Recover(ticket, "recurring_execution_bridge_failed");
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CommittedNotStarted,
                recoveryReason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        if (bridgeResult.Status == RecurringLeaseCaptureExecutionStatus.Started &&
            bridgeResult.LifecycleSession is not null)
        {
            // Ownership has transferred. The coordinator must not dispose,
            // stop, or recover this exact session after returning it.
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Started,
                "recurring_execution_started",
                startCommit.OccurrenceId,
                runId,
                useId,
                bridgeResult.LifecycleSession);
        }

        if (bridgeResult.Status == RecurringLeaseCaptureExecutionStatus.CompletedDuringStart)
        {
            return identity.WithStatus(
                RecurringOccurrenceExecutionStatus.CompletedDuringStart,
                bridgeResult.Reason,
                startCommit.OccurrenceId,
                runId,
                useId);
        }

        var finalRecoveryReason = Recover(
            ticket,
            string.IsNullOrWhiteSpace(bridgeResult.Reason)
                ? "recurring_execution_bridge_failed"
                : bridgeResult.Reason);
        return identity.WithStatus(
            RecurringOccurrenceExecutionStatus.CommittedNotStarted,
            finalRecoveryReason,
            startCommit.OccurrenceId,
            runId,
            useId);
    }

    private RecurringOccurrenceExecutionResult? MapDueBoundary(
        RecurringOccurrenceExecutionResult identity,
        PeriodicOccurrenceDueProjectionResult due) =>
        due.ResultCode switch
        {
            PeriodicOccurrenceDueResultCodes.Due or
            PeriodicOccurrenceDueResultCodes.AlreadyDue or
            PeriodicOccurrenceDueResultCodes.AlreadyClaimed => null,
            PeriodicOccurrenceDueResultCodes.BeforeWindow => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.BeforeWindow, due.ResultCode),
            PeriodicOccurrenceDueResultCodes.PlanPaused => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Paused, due.ResultCode),
            PeriodicOccurrenceDueResultCodes.Missed => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Missed, due.ResultCode),
            PeriodicOccurrenceDueResultCodes.Cancelled => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Cancelled, due.ResultCode),
            PeriodicOccurrenceDueResultCodes.Superseded => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Superseded, due.ResultCode),
            PeriodicOccurrenceDueResultCodes.AlreadyTerminal => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.AlreadyTerminal, due.ResultCode),
            _ => identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_due_projection_invalid"),
        };

    private RecurringOccurrenceExecutionResult? MapStartCommitBoundary(
        RecurringOccurrenceExecutionResult identity,
        RecurringOccurrenceStartCommitResult startCommit)
    {
        return startCommit.Status switch
        {
            RecurringOccurrenceStartCommitStatus.Committed => null,
            RecurringOccurrenceStartCommitStatus.CommittedWithoutProof => null,
            RecurringOccurrenceStartCommitStatus.AlreadyCommitted => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.AlreadyClaimed,
                "already_committed",
                startCommit.OccurrenceId,
                startCommit.RunId,
                startCommit.UseId),
            RecurringOccurrenceStartCommitStatus.AlreadyAdvanced => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.AlreadyAdvanced,
                "already_advanced",
                startCommit.OccurrenceId,
                startCommit.RunId,
                startCommit.UseId),
            RecurringOccurrenceStartCommitStatus.Blocked or
            RecurringOccurrenceStartCommitStatus.AlreadyBlocked => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Blocked,
                startCommit.ReasonCode,
                startCommit.OccurrenceId,
                startCommit.RunId,
                startCommit.UseId),
            RecurringOccurrenceStartCommitStatus.Conflict => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Conflict,
                startCommit.ReasonCode,
                startCommit.OccurrenceId,
                startCommit.RunId,
                startCommit.UseId),
            RecurringOccurrenceStartCommitStatus.Rejected => identity.WithStatus(
                RecurringOccurrenceExecutionStatus.Rejected,
                startCommit.ReasonCode,
                startCommit.OccurrenceId,
                startCommit.RunId,
                startCommit.UseId),
            _ => identity.WithStatus(RecurringOccurrenceExecutionStatus.Rejected, "recurring_start_commit_invalid"),
        };
    }

    private RecurringOccurrenceExecutionResult MapPersistenceFailure(
        RecurringOccurrenceExecutionResult identity,
        string code,
        string fallback = "recurring_persistence_rejected")
    {
        var status = code.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            ? RecurringOccurrenceExecutionStatus.Conflict
            : RecurringOccurrenceExecutionStatus.Rejected;
        return identity.WithStatus(status, IsStableReason(code) ? code : fallback);
    }

    private string Recover(
        RecurringStartCommitReceipt? receipt,
        string originalReason)
    {
        if (!TryReadTrustedUtc(out var nowUtc, out var clockFailure))
            return RecoveryReason(originalReason, "recovery_failed:" + clockFailure);

        RecurringLeaseRestartRecoveryResult result;
        try
        {
            result = _immediateRecovery.Recover(receipt, nowUtc);
        }
        catch
        {
            return RecoveryReason(originalReason, "recovery_failed:recovery_sqlite_failure");
        }

        return RecoveryReason(originalReason, result.Status switch
        {
            RecurringLeaseRestartRecoveryStatus.Recovered => "recovered",
            RecurringLeaseRestartRecoveryStatus.AlreadyReconciled => "already_reconciled",
            _ => "recovery_failed:" + StableOrFallback(result.Reason, "recovery_snapshot_invalid"),
        });
    }

    private string Recover(
        RecurringLeaseUseProof? proof,
        string originalReason)
    {
        if (!TryReadTrustedUtc(out var nowUtc, out var clockFailure))
            return RecoveryReason(originalReason, "recovery_failed:" + clockFailure);

        RecurringLeaseRestartRecoveryResult result;
        try
        {
            result = _immediateRecovery.Recover(proof, nowUtc);
        }
        catch
        {
            return RecoveryReason(originalReason, "recovery_failed:recovery_sqlite_failure");
        }

        return RecoveryReason(originalReason, result.Status switch
        {
            RecurringLeaseRestartRecoveryStatus.Recovered => "recovered",
            RecurringLeaseRestartRecoveryStatus.AlreadyReconciled => "already_reconciled",
            _ => "recovery_failed:" + StableOrFallback(result.Reason, "recovery_snapshot_invalid"),
        });
    }

    private string Recover(
        RecurringLeaseCaptureAuthorization? authorization,
        string originalReason)
    {
        if (!TryReadTrustedUtc(out var nowUtc, out var clockFailure))
            return RecoveryReason(originalReason, "recovery_failed:" + clockFailure);

        RecurringLeaseRestartRecoveryResult result;
        try
        {
            result = _immediateRecovery.Recover(authorization, nowUtc);
        }
        catch
        {
            return RecoveryReason(originalReason, "recovery_failed:recovery_sqlite_failure");
        }

        return RecoveryReason(originalReason, result.Status switch
        {
            RecurringLeaseRestartRecoveryStatus.Recovered => "recovered",
            RecurringLeaseRestartRecoveryStatus.AlreadyReconciled => "already_reconciled",
            _ => "recovery_failed:" + StableOrFallback(result.Reason, "recovery_snapshot_invalid"),
        });
    }

    private string Recover(
        RecurringLeaseCaptureExecutionTicket? ticket,
        string originalReason)
    {
        if (!TryReadTrustedUtc(out var nowUtc, out var clockFailure))
            return RecoveryReason(originalReason, "recovery_failed:" + clockFailure);

        RecurringLeaseRestartRecoveryResult result;
        try
        {
            result = _immediateRecovery.Recover(ticket, nowUtc);
        }
        catch
        {
            return RecoveryReason(originalReason, "recovery_failed:recovery_sqlite_failure");
        }

        return RecoveryReason(originalReason, result.Status switch
        {
            RecurringLeaseRestartRecoveryStatus.Recovered => "recovered",
            RecurringLeaseRestartRecoveryStatus.AlreadyReconciled => "already_reconciled",
            _ => "recovery_failed:" + StableOrFallback(result.Reason, "recovery_snapshot_invalid"),
        });
    }

    private static string RecoveryReason(string originalReason, string outcome) =>
        "committed_not_started:" + outcome + ":" + StableOrFallback(originalReason, "post_commit_failure");

    private bool TryReadTrustedUtc(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "recurring_execution_clock_unavailable";
        DateTimeOffset sampled;
        try
        {
            sampled = _utcNow();
        }
        catch
        {
            return false;
        }

        if (sampled.Offset != TimeSpan.Zero)
        {
            failureReason = "recurring_execution_time_not_utc";
            return false;
        }

        lock (_clockGate)
        {
            if (_lastTrustedUtcNow is { } previous && sampled < previous)
            {
                failureReason = "recurring_execution_clock_moved_backwards";
                return false;
            }

            _lastTrustedUtcNow = sampled;
        }

        nowUtc = sampled;
        failureReason = "";
        return true;
    }

    private void InvokeStage(RecurringOccurrenceExecutionStage stage) =>
        _stageHookForTest?.Invoke(stage);

    private static bool TryValidateRequest(
        RecurringOccurrenceExecutionRequest? request,
        out string failureReason)
    {
        failureReason = "recurring_execution_request_invalid";
        if (request is null ||
            !IsCanonicalId(request.LeaseId) ||
            request.Candidate is null)
        {
            return false;
        }

        var candidate = request.Candidate;
        if (!IsCanonicalId(candidate.PlanId) ||
            !IsCanonicalId(candidate.ScheduleDigest) ||
            !IsCanonicalId(candidate.OccurrenceIdentity) ||
            candidate.ScheduleRevision <= 0 ||
            candidate.ScheduleRevision == long.MaxValue ||
            !IsFiniteVersion(candidate.OccurrenceVersion) ||
            candidate.ScheduledStartUtc.Offset != TimeSpan.Zero ||
            candidate.LatestStartUtc.Offset != TimeSpan.Zero ||
            candidate.PlannedEndUtc.Offset != TimeSpan.Zero ||
            candidate.ScheduledStartUtc > candidate.LatestStartUtc ||
            candidate.LatestStartUtc > candidate.PlannedEndUtc)
        {
            return false;
        }

        return true;
    }

    private static RecurringOccurrenceExecutionResult ResultIdentity(
        RecurringOccurrenceExecutionRequest request) =>
        RecurringOccurrenceExecutionResult.ForRequest(
            request.LeaseId,
            request.Candidate!.OccurrenceIdentity);

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsFiniteVersion(long value) => value >= 0 && value < long.MaxValue;

    private static bool IsStableReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string StableOrFallback(string? value, string fallback) =>
        IsStableReason(value) ? value! : fallback;
}

/// <summary>
/// Internal request built from a durable due candidate. It contains no
/// caller-controlled clock, capture or authorization payload.
/// </summary>
internal enum RecurringOccurrenceExecutionStage
{
    AfterDueProjection,
    AfterReservation,
    AfterStartCommit,
    AfterAuthorization,
    AfterTicket,
    BeforeBridge,
}

internal sealed class RecurringOccurrenceExecutionRequest
{
    internal RecurringOccurrenceExecutionRequest(
        string leaseId,
        PeriodicOccurrenceDueCandidate? candidate)
    {
        LeaseId = leaseId;
        Candidate = candidate;
    }

    internal string LeaseId { get; }
    internal PeriodicOccurrenceDueCandidate? Candidate { get; }
}

internal enum RecurringOccurrenceExecutionStatus
{
    Started,
    CompletedDuringStart,
    NotDue,
    BeforeWindow,
    Paused,
    Missed,
    Cancelled,
    Superseded,
    AlreadyTerminal,
    AlreadyClaimed,
    AlreadyAdvanced,
    Blocked,
    CommittedNotStarted,
    Rejected,
    Conflict,
}

/// <summary>
/// Internal result surface. Only durable identities, a stable reason and the
/// exact lifecycle owner on Started are exposed; proof, ticket, backend,
/// specification, configuration, path and native handles never cross it.
/// </summary>
internal sealed class RecurringOccurrenceExecutionResult
{
    private RecurringOccurrenceExecutionResult(
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

    internal static RecurringOccurrenceExecutionResult Rejected(string reason) =>
        new(RecurringOccurrenceExecutionStatus.Rejected, StableOrFallback(reason, "recurring_execution_rejected"), null, null, null, null, null, null, null);

    internal static RecurringOccurrenceExecutionResult ForRequest(
        string leaseId,
        string occurrenceIdentity) =>
        new(RecurringOccurrenceExecutionStatus.NotDue, "", null, leaseId, occurrenceIdentity, null, null, null, null);

    internal RecurringOccurrenceExecutionResult WithVerifiedPlanId(string planId) =>
        new(
            Status,
            Reason,
            planId,
            LeaseId,
            OccurrenceIdentity,
            OccurrenceId,
            RunId,
            UseId,
            LifecycleSession);

    internal RecurringOccurrenceExecutionResult WithStatus(
        RecurringOccurrenceExecutionStatus status,
        string reason,
        string? occurrenceId = null,
        string? runId = null,
        string? useId = null,
        IRecurringLeaseCaptureLifecycleSession? lifecycleSession = null) =>
        new(
            status,
            StableOrFallback(reason, status == RecurringOccurrenceExecutionStatus.Started
                ? "recurring_execution_started"
                : "recurring_execution_rejected"),
            PlanId,
            LeaseId,
            OccurrenceIdentity,
            occurrenceId ?? OccurrenceId,
            runId ?? RunId,
            useId ?? UseId,
            lifecycleSession);

    private static bool IsStableReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or ':');

    private static string StableOrFallback(string? value, string fallback) =>
        IsStableReason(value) ? value! : fallback;
}
