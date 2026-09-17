using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Process-local owner for the successful Task 234 backend handoff. It
/// serializes backend callbacks and user Stop, while the SQLite transaction is
/// the durable idempotency/concurrency authority.
/// </summary>
internal sealed class StandingLeaseOneShotExecutionSession :
    IStandingLeaseCaptureLifecycleSession,
    IStandingLeaseCaptureLifecycleDriver
{
    private readonly object _sync = new();
    private readonly SqliteStandingLeaseLifecycleTransaction _lifecycle;
    private readonly StandingLeaseRestartRecoveryService _recovery;
    private readonly StandingLeaseRecoveryRequest _recoveryRequest;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly bool _attachBackendCallbacks;
    private readonly IFirstFrameObservableCaptureBackend? _firstFrameBackend;
    private readonly ICaptureEndedObservableBackend? _captureEndedBackend;
    private ICaptureBackend? _backend;
    private bool _attached;
    private bool _disposed;
    private bool _terminal;
    private bool _stopIssued;
    private bool _stopInProgress;
    private bool _firstFrameObserved;
    private bool _preserveCommittedNotStarted;

    internal StandingLeaseOneShotExecutionSession(
        SqliteOperationalStore store,
        StandingLeaseCaptureSpecification specification,
        ICaptureBackend backend,
        Func<DateTimeOffset?> utcNow,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? beforeCommitForTest = null,
        bool attachBackendCallbacks = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(utcNow);
        _backend = backend;
        _utcNow = utcNow;
        _attachBackendCallbacks = attachBackendCallbacks;
        _lifecycle = new SqliteStandingLeaseLifecycleTransaction(store, specification, beforeCommitForTest);
        _recovery = new StandingLeaseRestartRecoveryService(store, utcNow);
        _recoveryRequest = new StandingLeaseRecoveryRequest(
            specification.PlanId,
            specification.OccurrenceId,
            specification.LeaseId,
            specification.RunId,
            specification.LeaseUseId,
            specification.ScopeId,
            specification.ScopeDigest);
        _firstFrameBackend = backend as IFirstFrameObservableCaptureBackend;
        _captureEndedBackend = backend as ICaptureEndedObservableBackend;
    }

    bool IStandingLeaseCaptureLifecycleSession.TryAttach(ICaptureBackend backend, out string failureReason)
    {
        failureReason = "standing_lifecycle_attach_failed";
        lock (_sync)
        {
            if (_disposed || _attached || !ReferenceEquals(_backend, backend))
                return false;

            try
            {
                if (_attachBackendCallbacks)
                {
                    if (_firstFrameBackend is not null)
                        _firstFrameBackend.FirstFrameObserved += OnFirstFrameObservedCallback;
                    if (_captureEndedBackend is not null)
                        _captureEndedBackend.CaptureEnded += OnCaptureEndedCallback;
                    backend.OnNaturalExit(OnNaturalExitCallback);
                }
                _attached = true;
                failureReason = "";
                return true;
            }
            catch
            {
                // No backend.Start has been attempted when callback
                // registration fails. Preserve Task 234's committed-but-not
                // started result; restart recovery is only for a session that
                // could have outlived the process after the handoff.
                _preserveCommittedNotStarted = true;
                DetachCallbacksNoThrow();
                return false;
            }
        }
    }

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleSession.StartFailed()
    {
        lock (_sync)
        {
            if (_disposed || _terminal)
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    _terminal);

            // Preserve the Task 234 start-failure contract when Start failed
            // before any frame evidence. A frame observed before an exception,
            // however, must not leave the durable run in Recording.
            if (!_firstFrameObserved)
            {
                _preserveCommittedNotStarted = true;
                return StandingLeaseLifecycleActionResult.Idempotent("start_failed_before_first_frame");
            }

            var result = ApplyTerminationNoThrow(
                StandingLeaseLifecycleTerminationKind.BackendStartFailure,
                -1,
                null);
            if (result.Terminal)
                CompleteAndDisposeNoThrow();
            return result;
        }
    }

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleDriver.FailBeforeStart(string reason)
    {
        lock (_sync)
        {
            if (_disposed || _terminal)
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    _terminal);

            var result = ApplyTerminationNoThrow(
                StandingLeaseLifecycleTerminationKind.BackendStartFailure,
                -1,
                null);
            if (result.Terminal)
                CompleteAndDisposeNoThrow();
            return result;
        }
    }

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleSession.Stop()
    {
        return StopCoreNoThrow().Lifecycle;
    }

    StandingLeaseCaptureStopResult IStandingLeaseCaptureLifecycleDriver.StopForEngine(string? reason) =>
        StopCoreNoThrow(reason);

    private StandingLeaseCaptureStopResult StopCoreNoThrow(string? reason = null)
    {
        lock (_sync)
        {
            if (_disposed || _terminal || _stopIssued)
            {
                return new StandingLeaseCaptureStopResult(
                    StandingLeaseLifecycleActionResult.Idempotent(
                        _terminal ? "termination_already_settled" : "stop_already_issued",
                        _terminal),
                    null,
                    SafeExitCodeNoThrow());
            }

            _stopIssued = true;
            _stopInProgress = true;
            OutputMeta? meta = null;
            var stopFailed = false;
            try
            {
                meta = _backend!.Stop();
            }
            catch
            {
                stopFailed = true;
            }
            finally
            {
                _stopInProgress = false;
            }

            if (_terminal)
            {
                return new StandingLeaseCaptureStopResult(
                    StandingLeaseLifecycleActionResult.Idempotent(
                        "termination_already_settled",
                        terminal: true),
                    meta,
                    SafeExitCodeNoThrow());
            }

            var terminationKind = reason switch
            {
                "standing_lease_safety_control" => StandingLeaseLifecycleTerminationKind.SafetyStop,
                "session_interrupted" or "sleep_interrupted" or "application_exit" or "process_exit" =>
                    StandingLeaseLifecycleTerminationKind.SessionInterrupted,
                "standing_lifecycle_persistence_failure" =>
                    StandingLeaseLifecycleTerminationKind.LifecyclePersistenceFailure,
                "standing_lifecycle_start_failure" =>
                    StandingLeaseLifecycleTerminationKind.BackendStartFailure,
                _ => StandingLeaseLifecycleTerminationKind.UserStop,
            };
            var result = ApplyTerminationNoThrow(
                terminationKind,
                stopFailed ? -1 : SafeExitCodeNoThrow(),
                stopFailed ? null : meta);
            var exitCode = stopFailed ? -1 : SafeExitCodeNoThrow();
            if (result.Terminal)
                CompleteAndDisposeNoThrow();
            return new StandingLeaseCaptureStopResult(
                result,
                stopFailed ? null : meta,
                exitCode);
        }
    }

    void IDisposable.Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (!_terminal && !_preserveCommittedNotStarted)
            {
                var recovery = TryAbandonNoThrow();
                if (recovery.Status != StandingLeaseRecoveryStatus.Rejected)
                    _terminal = true;
            }

            _disposed = true;
            DetachCallbacksNoThrow();
            var backend = _backend;
            _backend = null;
            try
            {
                backend?.Dispose();
            }
            catch
            {
                // Disposal is best effort; the lifecycle transaction remains
                // the authority for durable state.
            }
        }
    }

    internal StandingLeaseRecoveryResult Abandon()
    {
        lock (_sync)
        {
            if (_terminal)
                return StandingLeaseRecoveryResult.AlreadyReconciled(_recoveryRequest, "recovery_already_terminal");
            if (_disposed)
                return StandingLeaseRecoveryResult.Rejected("recovery_session_disposed");
            if (_preserveCommittedNotStarted)
                return StandingLeaseRecoveryResult.Rejected("recovery_start_failed_before_first_frame");

            var result = TryAbandonNoThrow();
            if (result.Status != StandingLeaseRecoveryStatus.Rejected)
                _terminal = true;
            return result;
        }
    }

    private StandingLeaseLifecycleActionResult OnFirstFrameObserved(FirstFrameObservation observation)
    {
        lock (_sync)
        {
            if (_disposed || _terminal)
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    _terminal);

            if (!TryReadUtcNow(out var observedAtUtc))
                return FailClosedAfterPersistenceFailure("standing_lifecycle_time_unavailable");

            var result = ApplyNoThrow(() => _lifecycle.ObserveFirstFrame(observation, observedAtUtc));
            if (result.Succeeded &&
                (result.Changed || result.Reason == "first_frame_already_observed"))
            {
                _firstFrameObserved = true;
                // This flag is only a local optimization for callback ordering;
                // the transaction/database remains authoritative.
            }
            return result;
        }
    }

    private StandingLeaseLifecycleActionResult OnCaptureEnded(CaptureEndedObservation observation)
    {
        lock (_sync)
        {
            if (_disposed || _terminal || _stopInProgress)
                return StandingLeaseLifecycleActionResult.Idempotent(
                    _stopInProgress ? "capture_ended_during_stop" : "termination_already_settled",
                    _terminal);
            if (!TryReadUtcNow(out var observedAtUtc))
                return FailClosedAfterPersistenceFailure("standing_lifecycle_time_unavailable");
            return ApplyNoThrow(() => _lifecycle.ObserveCaptureEnded(observedAtUtc));
        }
    }

    private StandingLeaseLifecycleActionResult OnNaturalExit(int exitCode, OutputMeta meta)
    {
        lock (_sync)
        {
            if (_disposed || _terminal)
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    _terminal);

            // A Stop implementation may synchronously raise the natural-exit
            // callback. The returned Stop metadata is authoritative for the
            // user-stop path, so defer that callback until Stop returns.
            if (_stopInProgress)
            {
                return StandingLeaseLifecycleActionResult.Idempotent("natural_exit_during_stop");
            }

            var result = ApplyTerminationNoThrow(
                StandingLeaseLifecycleTerminationKind.NaturalExit,
                exitCode,
                meta);
            if (result.Terminal)
                CompleteAndDisposeNoThrow();
            return result;
        }
    }

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleDriver.ObserveFirstFrame(FirstFrameObservation observation) =>
        OnFirstFrameObserved(observation);

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleDriver.ObserveCaptureEnded(CaptureEndedObservation observation) =>
        OnCaptureEnded(observation);

    StandingLeaseLifecycleActionResult IStandingLeaseCaptureLifecycleDriver.ObserveNaturalExit(int exitCode, OutputMeta meta) =>
        OnNaturalExit(exitCode, meta);

    private void OnFirstFrameObservedCallback(FirstFrameObservation observation) =>
        _ = OnFirstFrameObserved(observation);

    private void OnCaptureEndedCallback(CaptureEndedObservation observation) =>
        _ = OnCaptureEnded(observation);

    private void OnNaturalExitCallback(int exitCode, OutputMeta meta) =>
        _ = OnNaturalExit(exitCode, meta);

    private StandingLeaseLifecycleActionResult ApplyTerminationNoThrow(
        StandingLeaseLifecycleTerminationKind kind,
        int exitCode,
        OutputMeta? meta)
    {
        if (!TryReadUtcNow(out var terminatedAtUtc))
            return FailClosedAfterPersistenceFailure("standing_lifecycle_time_unavailable");

        return ApplyNoThrow(() => _lifecycle.CompleteTermination(kind, exitCode, meta, terminatedAtUtc));
    }

    private StandingLeaseLifecycleActionResult ApplyNoThrow(
        Func<StandingLeaseLifecycleActionResult> operation)
    {
        try
        {
            return operation();
        }
        catch (Phase3PersistenceException exception)
        {
            ReconcileAfterPersistenceFailureNoThrow();
            return StandingLeaseLifecycleActionResult.Rejected(exception.Code);
        }
        catch
        {
            ReconcileAfterPersistenceFailureNoThrow();
            return StandingLeaseLifecycleActionResult.Rejected("sqlite_failure");
        }
    }

    private StandingLeaseLifecycleActionResult FailClosedAfterPersistenceFailure(string reason)
    {
        // Recovery is the durable fail-closed boundary.  It converts an
        // in-flight start/recording/finalizing aggregate into the existing
        // started_unknown/session_interrupted + blocked terminal, while the
        // Engine still owns the one physical Stop call.
        ReconcileAfterPersistenceFailureNoThrow();
        return StandingLeaseLifecycleActionResult.Rejected(reason);
    }

    private void ReconcileAfterPersistenceFailureNoThrow()
    {
        try { _ = TryAbandonNoThrow(); } catch { }
    }

    private bool TryReadUtcNow(out DateTimeOffset nowUtc)
    {
        nowUtc = default;
        DateTimeOffset? sampled;
        try
        {
            sampled = _utcNow();
        }
        catch
        {
            return false;
        }

        if (sampled is null || sampled.Value.Offset != TimeSpan.Zero)
            return false;
        nowUtc = sampled.Value;
        return true;
    }

    private int SafeExitCodeNoThrow()
    {
        try
        {
            return _backend?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private void CompleteAndDisposeNoThrow()
    {
        // Caller already holds _sync. Dispose is re-entrant and therefore
        // keeps callback/Stop/dispose ownership serialized.
        _terminal = true;
        ((IDisposable)this).Dispose();
    }

    private StandingLeaseRecoveryResult TryAbandonNoThrow()
    {
        try
        {
            return _recovery.Recover(_recoveryRequest);
        }
        catch
        {
            return StandingLeaseRecoveryResult.Rejected("recovery_failure");
        }
    }

    private void DetachCallbacksNoThrow()
    {
        try
        {
            if (_firstFrameBackend is not null)
                _firstFrameBackend.FirstFrameObserved -= OnFirstFrameObservedCallback;
        }
        catch { }
        try
        {
            if (_captureEndedBackend is not null)
                _captureEndedBackend.CaptureEnded -= OnCaptureEndedCallback;
        }
        catch { }
    }
}
