using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Process-local owner for one recurring backend handoff. This class owns
/// callback registration, event ordering and physical backend cleanup; the
/// recurring lifecycle transaction remains the only durable state machine.
/// </summary>
internal sealed class RecurringLeaseCaptureExecutionSession :
    IRecurringLeaseCaptureLifecycleSession,
    IRecurringLeaseCaptureLifecycleDriver
{
    private readonly object _sync = new();
    private readonly SqliteRecurringLeaseLifecycleTransaction _lifecycle;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly bool _attachBackendCallbacks;
    private readonly IFirstFrameObservableCaptureBackend? _firstFrameBackend;
    private readonly ICaptureEndedObservableBackend? _captureEndedBackend;
    private readonly long _initialPersistedUtcTicks;
    private ICaptureBackend? _backend;
    private long _lastClockTicks;
    private bool _attached;
    private bool _ownsBackend;
    private bool _disposed;
    private bool _terminal;
    private bool _failClosed;
    private bool _stopIssued;
    private bool _stopInProgress;
    private bool _backendDisposed;
    private bool _failureTerminalizationAttempted;
    private RecurringLeaseLifecycleActionResult? _firstTerminalResult;
    private RecurringLeaseLifecycleActionResult _lastCallbackResult =
        RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_started");

    internal RecurringLeaseCaptureExecutionSession(
        SqliteOperationalStore store,
        RecurringStartCommitReceipt receipt,
        ICaptureBackend backend,
        Func<DateTimeOffset?> utcNow,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        bool attachBackendCallbacks = true)
        : this(
            new SqliteRecurringLeaseLifecycleTransaction(
                store,
                receipt ?? throw new ArgumentNullException(nameof(receipt)),
                failureHookForTest,
                beforeCommitForTest),
            backend,
            utcNow,
            MaxUtcTicks(
                receipt.CommittedAtUtc,
                receipt.Lease.UpdatedAtUtc,
                receipt.Occurrence.UpdatedAtUtc,
                receipt.Run.UpdatedAtUtc,
                receipt.Use.UpdatedAtUtc),
            attachBackendCallbacks)
    {
    }

    internal RecurringLeaseCaptureExecutionSession(
        SqliteOperationalStore store,
        RecurringLeaseCaptureExecutionTicket ticket,
        ICaptureBackend backend,
        Func<DateTimeOffset?> utcNow,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        bool attachBackendCallbacks = true)
        : this(
            new SqliteRecurringLeaseLifecycleTransaction(
                store,
                ticket ?? throw new ArgumentNullException(nameof(ticket)),
                failureHookForTest,
                beforeCommitForTest),
            backend,
            utcNow,
            MaxUtcTicks(
                ticket.ValidatedAtUtc,
                ticket.LeaseUpdatedAtUtc,
                ticket.OccurrenceUpdatedAtUtc,
                ticket.RunUpdatedAtUtc,
                ticket.UseUpdatedAtUtc),
            attachBackendCallbacks)
    {
    }

    private RecurringLeaseCaptureExecutionSession(
        SqliteRecurringLeaseLifecycleTransaction lifecycle,
        ICaptureBackend backend,
        Func<DateTimeOffset?> utcNow,
        long initialPersistedUtcTicks,
        bool attachBackendCallbacks)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _initialPersistedUtcTicks = initialPersistedUtcTicks;
        _lastClockTicks = initialPersistedUtcTicks;
        _attachBackendCallbacks = attachBackendCallbacks;
        _firstFrameBackend = backend as IFirstFrameObservableCaptureBackend;
        _captureEndedBackend = backend as ICaptureEndedObservableBackend;
    }

    /// <summary>
    /// Exposes only the latest process-local callback result for deterministic
    /// tests and diagnostics. It is not a durable or public result surface.
    /// </summary>
    internal RecurringLeaseLifecycleActionResult LastCallbackResult
    {
        get
        {
            lock (_sync)
                return _lastCallbackResult;
        }
    }

    bool IRecurringLeaseCaptureLifecycleSession.TryAttach(
        ICaptureBackend backend,
        out string failureReason)
    {
        failureReason = "recurring_lifecycle_attach_failed";
        lock (_sync)
        {
            if (_disposed || _terminal || _failClosed || _attached || !ReferenceEquals(_backend, backend))
                return false;

            try
            {
                if (_attachBackendCallbacks)
                {
                    if (_firstFrameBackend is not null)
                        _firstFrameBackend.FirstFrameObserved += OnFirstFrameObservedCallback;
                    if (_captureEndedBackend is not null)
                        _captureEndedBackend.CaptureEnded += OnCaptureEndedCallback;

                    // Register the non-event callback last. If this throws,
                    // the event handlers above can be fully removed.
                    backend.OnNaturalExit(OnNaturalExitCallback);
                }

                _attached = true;
                _ownsBackend = true;
                failureReason = "";
                return true;
            }
            catch
            {
                DetachCallbacksNoThrow();
                _attached = false;
                _ownsBackend = false;
                return false;
            }
        }
    }

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleSession.StartFailed()
    {
        lock (_sync)
        {
            if (_terminal)
                return RecurringLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    terminal: true);
            if (_disposed || _failClosed)
                return RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed");
            if (!_attached)
                return RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached");

            var result = ApplyTerminalNoThrow(
                RecurringLeaseLifecycleTerminationKind.BackendStartFailure,
                exitCode: -1,
                meta: null);
            if (result.Terminal)
                CompleteTerminalCleanupNoThrow();
            return result;
        }
    }

    RecurringLeaseCaptureLifecycleHandoffResult
        IRecurringLeaseCaptureLifecycleSession.CompleteStartHandoff()
    {
        lock (_sync)
        {
            if (_disposed)
                return new RecurringLeaseCaptureLifecycleHandoffResult(
                    RecurringLeaseCaptureLifecycleHandoffStatus.Disposed);
            if (_failClosed)
                return new RecurringLeaseCaptureLifecycleHandoffResult(
                    RecurringLeaseCaptureLifecycleHandoffStatus.FailClosed);
            if (_terminal)
                return new RecurringLeaseCaptureLifecycleHandoffResult(
                    RecurringLeaseCaptureLifecycleHandoffStatus.AlreadyTerminal,
                    _firstTerminalResult);
            if (!_attached || !_ownsBackend || _backend is null || _backendDisposed)
                return new RecurringLeaseCaptureLifecycleHandoffResult(
                    RecurringLeaseCaptureLifecycleHandoffStatus.NotAttached);

            return new RecurringLeaseCaptureLifecycleHandoffResult(
                RecurringLeaseCaptureLifecycleHandoffStatus.Active);
        }
    }

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleDriver.FailBeforeStart(string reason)
    {
        lock (_sync)
        {
            if (_terminal)
                return RecurringLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    terminal: true);
            if (_disposed || _failClosed)
                return RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed");
            if (!_attached)
                return RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached");

            var result = ApplyTerminalNoThrow(
                MapBeforeStartReason(reason),
                exitCode: -1,
                meta: null);
            if (result.Terminal)
                CompleteTerminalCleanupNoThrow();
            return result;
        }
    }

    RecurringLeaseCaptureStopResult IRecurringLeaseCaptureLifecycleDriver.StopForEngine(string? reason) =>
        StopCoreNoThrow(reason);

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleSession.Stop() =>
        StopCoreNoThrow(null).Lifecycle;

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleDriver.ObserveFirstFrame(
        FirstFrameObservation observation) =>
        ObserveFirstFrameCore(observation);

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleDriver.ObserveCaptureEnded(
        CaptureEndedObservation observation) =>
        ObserveCaptureEndedCore(observation);

    RecurringLeaseLifecycleActionResult IRecurringLeaseCaptureLifecycleDriver.ObserveNaturalExit(
        int exitCode,
        OutputMeta meta) =>
        ObserveNaturalExitCore(exitCode, meta);

    private RecurringLeaseLifecycleActionResult ObserveFirstFrameCore(FirstFrameObservation? observation)
    {
        lock (_sync)
        {
            if (IsTerminalLocked())
                return TerminalIdempotentLocked();
            if (_stopInProgress)
                return Remember(RecurringLeaseLifecycleActionResult.Idempotent("first_frame_during_stop"));
            if (!_attached)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached"));
            if (_failClosed)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed"));
            if (!TryReadTrustedUtcNoThrow(out var observedAtUtc))
                return FailClosedAfterFailureNoThrow("recurring_lifecycle_time_unavailable");

            try
            {
                var result = _lifecycle.ObserveFirstFrame(observation, observedAtUtc);
                return Remember(result);
            }
            catch (Phase3PersistenceException exception)
            {
                return FailClosedAfterFailureNoThrow(exception.Code);
            }
            catch
            {
                return FailClosedAfterFailureNoThrow("sqlite_failure");
            }
        }
    }

    private RecurringLeaseLifecycleActionResult ObserveCaptureEndedCore(CaptureEndedObservation? observation)
    {
        lock (_sync)
        {
            if (IsTerminalLocked())
                return TerminalIdempotentLocked();
            if (_stopInProgress)
                return Remember(RecurringLeaseLifecycleActionResult.Idempotent("capture_ended_during_stop"));
            if (!_attached)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached"));
            if (_failClosed)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed"));
            if (!TryReadTrustedUtcNoThrow(out var observedAtUtc))
                return FailClosedAfterFailureNoThrow("recurring_lifecycle_time_unavailable");

            try
            {
                var trustedObservation = observation is null
                    ? null
                    : new CaptureEndedObservation
                    {
                        EndedAtUtc = observedAtUtc.UtcDateTime,
                        ExitCode = observation.ExitCode,
                        Reason = observation.Reason,
                    };
                var result = _lifecycle.ObserveCaptureEnded(trustedObservation);
                return Remember(result);
            }
            catch (Phase3PersistenceException exception)
            {
                return FailClosedAfterFailureNoThrow(exception.Code);
            }
            catch
            {
                return FailClosedAfterFailureNoThrow("sqlite_failure");
            }
        }
    }

    private RecurringLeaseLifecycleActionResult ObserveNaturalExitCore(int exitCode, OutputMeta? meta)
    {
        lock (_sync)
        {
            if (IsTerminalLocked())
                return TerminalIdempotentLocked();
            if (_stopInProgress)
                return Remember(RecurringLeaseLifecycleActionResult.Idempotent("natural_exit_during_stop"));
            if (!_attached)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached"));
            if (_failClosed)
                return Remember(RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed"));
            return CompleteTerminationCoreNoThrow(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                exitCode,
                meta);
        }
    }

    private RecurringLeaseCaptureStopResult StopCoreNoThrow(string? reason)
    {
        lock (_sync)
        {
            if (_terminal)
            {
                return new RecurringLeaseCaptureStopResult(
                    RecurringLeaseLifecycleActionResult.Idempotent(
                        "termination_already_settled",
                        terminal: true),
                    null,
                    SafeExitCodeNoThrow());
            }
            if (_disposed || _failClosed)
            {
                return new RecurringLeaseCaptureStopResult(
                    RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_persistence_failed"),
                    null,
                    SafeExitCodeNoThrow());
            }
            if (!_attached)
            {
                return new RecurringLeaseCaptureStopResult(
                    RecurringLeaseLifecycleActionResult.Rejected("recurring_lifecycle_not_attached"),
                    null,
                    -1);
            }
            if (_stopIssued)
            {
                return new RecurringLeaseCaptureStopResult(
                    RecurringLeaseLifecycleActionResult.Idempotent("stop_already_issued"),
                    null,
                    SafeExitCodeNoThrow());
            }

            var meta = IssuePhysicalStopNoThrow(out var stopFailed);
            var kind = MapStopReason(reason);
            var exitCode = stopFailed ? -1 : SafeExitCodeNoThrow();
            var result = CompleteTerminationCoreNoThrow(kind, exitCode, stopFailed ? null : meta);
            if (result.Terminal)
                CompleteTerminalCleanupNoThrow();
            return new RecurringLeaseCaptureStopResult(result, stopFailed ? null : meta, exitCode);
        }
    }

    private RecurringLeaseLifecycleActionResult CompleteTerminationCoreNoThrow(
        RecurringLeaseLifecycleTerminationKind kind,
        int exitCode,
        OutputMeta? meta)
    {
        if (!TryReadTrustedUtcNoThrow(out var terminatedAtUtc))
            return FailClosedAfterFailureNoThrow("recurring_lifecycle_time_unavailable");

        try
        {
            var result = _lifecycle.CompleteTermination(kind, exitCode, meta, terminatedAtUtc);
            if (result.Succeeded && result.Terminal)
            {
                FreezeFirstTerminalResult(result);
                CompleteTerminalCleanupNoThrow();
            }
            else if (!result.Succeeded)
                return FailClosedAfterFailureNoThrow(result.Reason);
            return Remember(result);
        }
        catch (Phase3PersistenceException exception)
        {
            return FailClosedAfterFailureNoThrow(exception.Code);
        }
        catch
        {
            return FailClosedAfterFailureNoThrow("sqlite_failure");
        }
    }

    private RecurringLeaseLifecycleActionResult ApplyTerminalNoThrow(
        RecurringLeaseLifecycleTerminationKind kind,
        int exitCode,
        OutputMeta? meta) =>
        CompleteTerminationCoreNoThrow(kind, exitCode, meta);

    private RecurringLeaseLifecycleActionResult FailClosedAfterFailureNoThrow(string reason)
    {
        if (_terminal)
            return TerminalIdempotentLocked();

        if (_failureTerminalizationAttempted)
            return Remember(RecurringLeaseLifecycleActionResult.Rejected(reason));

        _failureTerminalizationAttempted = true;
        _failClosed = true;
        _ = IssuePhysicalStopNoThrow(out _);

        if (TryReadTrustedUtcNoThrow(out var nowUtc))
        {
            try
            {
                var result = _lifecycle.CompleteTermination(
                    RecurringLeaseLifecycleTerminationKind.LifecyclePersistenceFailure,
                    exitCode: -1,
                    meta: null,
                    nowUtc);
                if (result.Succeeded && result.Terminal)
                {
                    FreezeFirstTerminalResult(result);
                    _terminal = true;
                    CompleteTerminalCleanupNoThrow();
                }
            }
            catch
            {
                // The original durable failure is preserved. There is no
                // retry loop and no fabricated terminal success.
            }
        }

        // A failed durable terminalization must not leave a live callback or
        // backend resource behind. The durable chain remains available for
        // restart recovery; this process-local owner is fail-closed.
        DetachCallbacksNoThrow();
        DisposeBackendNoThrow();

        return Remember(RecurringLeaseLifecycleActionResult.Rejected(reason));
    }

    private bool TryReadTrustedUtcNoThrow(out DateTimeOffset nowUtc)
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

        var ticks = sampled.Value.UtcDateTime.Ticks;
        if (ticks < _initialPersistedUtcTicks || ticks < _lastClockTicks)
            return false;

        _lastClockTicks = ticks;
        nowUtc = sampled.Value;
        return true;
    }

    private OutputMeta? IssuePhysicalStopNoThrow(out bool failed)
    {
        failed = false;
        if (_stopIssued || !_ownsBackend || _backend is null)
            return null;

        _stopIssued = true;
        _stopInProgress = true;
        try
        {
            return _backend.Stop();
        }
        catch
        {
            failed = true;
            return null;
        }
        finally
        {
            _stopInProgress = false;
        }
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

    private bool IsTerminalLocked() => _terminal || _disposed;

    private RecurringLeaseLifecycleActionResult TerminalIdempotentLocked() =>
        Remember(RecurringLeaseLifecycleActionResult.Idempotent(
            _terminal ? "termination_already_settled" : "recurring_lifecycle_disposed",
            terminal: _terminal));

    private RecurringLeaseLifecycleActionResult Remember(RecurringLeaseLifecycleActionResult result)
    {
        _lastCallbackResult = result;
        return result;
    }

    private void FreezeFirstTerminalResult(RecurringLeaseLifecycleActionResult result)
    {
        if (result.Succeeded && result.Changed && result.Terminal && _firstTerminalResult is null)
            _firstTerminalResult = result;
    }

    private void CompleteTerminalCleanupNoThrow()
    {
        _terminal = true;
        DetachCallbacksNoThrow();
        DisposeBackendNoThrow();
    }

    void IDisposable.Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_attached && !_terminal && !_failClosed)
                _ = StopCoreNoThrow("session_interrupted");

            DetachCallbacksNoThrow();
            DisposeBackendNoThrow();
            _disposed = true;
        }
    }

    private void DisposeBackendNoThrow()
    {
        if (!_ownsBackend || _backendDisposed || _backend is null)
            return;

        _backendDisposed = true;
        _ownsBackend = false;
        try
        {
            _backend.Dispose();
        }
        catch
        {
            // Backend disposal is best effort and cannot replace a durable
            // lifecycle result or manufacture one.
        }
    }

    private void DetachCallbacksNoThrow()
    {
        _attached = false;
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

    private void OnFirstFrameObservedCallback(FirstFrameObservation observation) =>
        _ = ObserveFirstFrameCore(observation);

    private void OnCaptureEndedCallback(CaptureEndedObservation observation) =>
        _ = ObserveCaptureEndedCore(observation);

    private void OnNaturalExitCallback(int exitCode, OutputMeta meta) =>
        _ = ObserveNaturalExitCore(exitCode, meta);

    private static RecurringLeaseLifecycleTerminationKind MapStopReason(string? reason) =>
        reason switch
        {
            "recurring_lease_safety_control" => RecurringLeaseLifecycleTerminationKind.SafetyStop,
            "session_interrupted" or "sleep_interrupted" or "rdp_disconnected" or "application_exit" or "process_exit" =>
                RecurringLeaseLifecycleTerminationKind.SessionInterrupted,
            "recurring_lifecycle_persistence_failure" => RecurringLeaseLifecycleTerminationKind.LifecyclePersistenceFailure,
            "recurring_lifecycle_start_failure" => RecurringLeaseLifecycleTerminationKind.BackendStartFailure,
            _ => RecurringLeaseLifecycleTerminationKind.UserStop,
        };

    private static RecurringLeaseLifecycleTerminationKind MapBeforeStartReason(string? reason) =>
        reason switch
        {
            "recurring_lease_safety_control" => RecurringLeaseLifecycleTerminationKind.SafetyStop,
            "recurring_lifecycle_start_failure" => RecurringLeaseLifecycleTerminationKind.BackendStartFailure,
            "session_interrupted" or "sleep_interrupted" or "rdp_disconnected" or
                "application_exit" or "process_exit" => RecurringLeaseLifecycleTerminationKind.SessionInterrupted,
            _ => RecurringLeaseLifecycleTerminationKind.SessionInterrupted,
        };

    private static long MaxUtcTicks(params DateTimeOffset[] values)
    {
        var max = values.Length == 0 ? DateTimeOffset.MinValue.UtcDateTime.Ticks : values[0].UtcDateTime.Ticks;
        foreach (var value in values)
            max = Math.Max(max, value.UtcDateTime.Ticks);
        return max;
    }
}
