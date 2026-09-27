using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Owns the one committed required-once backend handoff. Durable state is
/// advanced only by the required-once repository; this session never creates
/// a standing/recurring lease or replays a failed start.
/// </summary>
internal sealed class SqliteRequiredOnceCaptureExecutionSession :
    IRequiredOnceCaptureLifecycleSession,
    IRequiredOnceCaptureLifecycleDriver
{
    private readonly object _sync = new();
    private readonly SqliteRequiredOnceExecutionRepository _repository;
    private readonly RequiredOnceCaptureExecutionTicket _ticket;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly long _initialUtcTicks;
    private long _lastUtcTicks;
    private ICaptureBackend? _backend;
    private bool _attached;
    private bool _disposed;
    private bool _terminal;
    private bool _failClosed;
    private bool _stopIssued;

    internal SqliteRequiredOnceCaptureExecutionSession(
        SqliteOperationalStore store,
        RequiredOnceCaptureExecutionTicket ticket,
        ICaptureBackend backend,
        Func<DateTimeOffset?> utcNow)
    {
        _repository = new SqliteRequiredOnceExecutionRepository(store ?? throw new ArgumentNullException(nameof(store)));
        _ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _initialUtcTicks = ticket.CommittedAtUtc.UtcDateTime.Ticks;
        _lastUtcTicks = _initialUtcTicks;
    }

    bool IRequiredOnceCaptureLifecycleSession.TryAttach(ICaptureBackend backend, out string failureReason)
    {
        lock (_sync)
        {
            failureReason = "required_once_lifecycle_attach_failed";
            if (_disposed || _terminal || _failClosed || _attached || !ReferenceEquals(_backend, backend))
                return false;
            _attached = true;
            failureReason = string.Empty;
            return true;
        }
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleSession.StartFailed(string reason)
    {
        lock (_sync)
            return TerminalizeNoThrow(CanonicalReason(reason) ?? "backend_start_failed",
                sessionInterrupted: false, startOutcomeKnownFailed: true);
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleSession.Stop() =>
        ((IRequiredOnceCaptureLifecycleDriver)this).StopForEngine().Lifecycle;

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleDriver.ObserveFirstFrame(FirstFrameObservation observation)
    {
        lock (_sync)
        {
            if (!CanObserve(out var unavailable)) return unavailable;
            if (!TryReadUtc(out var now)) return FailClosedNoThrow("required_once_lifecycle_time_unavailable");
            try
            {
                return Map(_repository.ObserveFirstFrame(_ticket.ExecutionId, now), terminal: false);
            }
            catch (Exception exception)
            {
                return FailClosedNoThrow(PersistenceReason(exception));
            }
        }
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleDriver.ObserveCaptureEnded(CaptureEndedObservation observation)
    {
        lock (_sync)
        {
            if (_stopIssued)
                return RequiredOnceLifecycleActionResult.Idempotent("capture_ended_during_stop");
            if (!CanObserve(out var unavailable)) return unavailable;
            if (!TryReadUtc(out var now)) return FailClosedNoThrow("required_once_lifecycle_time_unavailable");
            try
            {
                return Map(_repository.ObserveCaptureEnded(_ticket.ExecutionId, now), terminal: false);
            }
            catch (Exception exception)
            {
                return FailClosedNoThrow(PersistenceReason(exception));
            }
        }
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleDriver.ObserveNaturalExit(int exitCode, OutputMeta meta)
    {
        lock (_sync)
        {
            if (_terminal) return RequiredOnceLifecycleActionResult.Idempotent("termination_already_settled", terminal: true);
            if (_stopIssued) return RequiredOnceLifecycleActionResult.Idempotent("natural_exit_during_stop");
            if (!CanObserve(out var unavailable)) return unavailable;
            return RequiredOnceLifecycleActionResult.Applied("natural_exit_observed");
        }
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleDriver.SettleFinalization(
        bool succeeded, OutputMeta meta, int exitCode, string? reason)
    {
        lock (_sync)
        {
            if (_terminal) return RequiredOnceLifecycleActionResult.Idempotent("termination_already_settled", terminal: true);
            if (!_attached || _disposed || _failClosed)
                return RequiredOnceLifecycleActionResult.Rejected("required_once_lifecycle_unavailable");
            if (!TryReadUtc(out var now)) return FailClosedNoThrow("required_once_lifecycle_time_unavailable");
            try
            {
                var result = _repository.SettleFinalization(
                    _ticket.ExecutionId, succeeded, meta, exitCode,
                    CanonicalReason(reason), now);
                if (!result.Succeeded) return FailClosedNoThrow(result.ReasonCode);
                _terminal = true;
                DisposeBackendNoThrow();
                return RequiredOnceLifecycleActionResult.Applied(result.ReasonCode, terminal: true);
            }
            catch (Exception exception)
            {
                return FailClosedNoThrow(PersistenceReason(exception));
            }
        }
    }

    RequiredOnceLifecycleActionResult IRequiredOnceCaptureLifecycleDriver.FailBeforeStart(string reason)
    {
        lock (_sync)
            return TerminalizeNoThrow(CanonicalReason(reason) ?? "required_once_start_revalidation_failed", sessionInterrupted: false);
    }

    RequiredOnceCaptureStopResult IRequiredOnceCaptureLifecycleDriver.StopForEngine(string? reason)
    {
        lock (_sync)
        {
            if (_terminal || _disposed)
                return new(RequiredOnceLifecycleActionResult.Idempotent("termination_already_settled", terminal: _terminal), null, SafeExitCode());
            if (_failClosed || !_attached || _backend is null)
                return new(RequiredOnceLifecycleActionResult.Rejected("required_once_lifecycle_unavailable"), null, SafeExitCode());
            if (_stopIssued)
                return new(RequiredOnceLifecycleActionResult.Idempotent("stop_already_issued"), null, SafeExitCode());
            _stopIssued = true;
            try
            {
                var meta = _backend.Stop();
                return new(RequiredOnceLifecycleActionResult.Applied("stop_issued"), meta, SafeExitCode());
            }
            catch
            {
                return new(RequiredOnceLifecycleActionResult.Rejected("required_once_backend_stop_failed"), null, -1);
            }
        }
    }

    private RequiredOnceLifecycleActionResult TerminalizeNoThrow(
        string reason, bool sessionInterrupted, bool startOutcomeKnownFailed = false)
    {
        if (_terminal) return RequiredOnceLifecycleActionResult.Idempotent("termination_already_settled", terminal: true);
        if (!_attached || _disposed || _failClosed)
            return RequiredOnceLifecycleActionResult.Rejected("required_once_lifecycle_unavailable");
        if (!TryReadUtc(out var now)) return FailClosedNoThrow("required_once_lifecycle_time_unavailable");
        try
        {
            var result = _repository.MarkPostCommitFailure(
                _ticket.ExecutionId, reason, now, sessionInterrupted, startOutcomeKnownFailed);
            if (!result.Succeeded) return FailClosedNoThrow(result.ReasonCode);
            _terminal = true;
            DisposeBackendNoThrow();
            return RequiredOnceLifecycleActionResult.Applied(result.ReasonCode, terminal: true);
        }
        catch (Exception exception)
        {
            return FailClosedNoThrow(PersistenceReason(exception));
        }
    }

    private bool CanObserve(out RequiredOnceLifecycleActionResult result)
    {
        if (_terminal || _disposed)
        {
            result = RequiredOnceLifecycleActionResult.Idempotent(
                _terminal ? "termination_already_settled" : "required_once_lifecycle_disposed", terminal: _terminal);
            return false;
        }
        if (_failClosed || !_attached)
        {
            result = RequiredOnceLifecycleActionResult.Rejected("required_once_lifecycle_unavailable");
            return false;
        }
        result = default;
        return true;
    }

    private bool TryReadUtc(out DateTimeOffset now)
    {
        now = default;
        DateTimeOffset? sampled;
        try { sampled = _utcNow(); } catch { return false; }
        if (sampled is null || sampled.Value.Offset != TimeSpan.Zero) return false;
        var ticks = sampled.Value.UtcDateTime.Ticks;
        if (ticks < _initialUtcTicks || ticks < _lastUtcTicks) return false;
        _lastUtcTicks = ticks;
        now = sampled.Value;
        return true;
    }

    private RequiredOnceLifecycleActionResult FailClosedNoThrow(string reason)
    {
        _failClosed = true;
        if (!_terminal && TryReadUtc(out var now))
        {
            try
            {
                var result = _repository.MarkPostCommitFailure(_ticket.ExecutionId,
                    CanonicalReason(reason) ?? "required_once_lifecycle_persistence_failed", now);
                if (result.Succeeded) _terminal = true;
            }
            catch { }
        }
        if (!_stopIssued && _backend is not null)
        {
            _stopIssued = true;
            try { _ = _backend.Stop(); } catch { }
        }
        DisposeBackendNoThrow();
        return RequiredOnceLifecycleActionResult.Rejected(reason);
    }

    private int SafeExitCode()
    {
        try { return _backend?.ExitCode ?? -1; } catch { return -1; }
    }

    private void DisposeBackendNoThrow()
    {
        var backend = _backend;
        _backend = null;
        try { backend?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_attached && !_terminal && !_failClosed)
                _ = TerminalizeNoThrow("session_interrupted", sessionInterrupted: true);
            _disposed = true;
            DisposeBackendNoThrow();
        }
    }

    private static RequiredOnceLifecycleActionResult Map(RequiredOnceExecutionWriteResult result, bool terminal) =>
        result.Succeeded
            ? RequiredOnceLifecycleActionResult.Applied(result.ReasonCode, terminal)
            : RequiredOnceLifecycleActionResult.Rejected(result.ReasonCode);

    private static string PersistenceReason(Exception exception) => exception is Phase3PersistenceException phase3
        ? phase3.Code
        : "sqlite_failure";

    private static string? CanonicalReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason == reason.Trim() && !reason.Any(char.IsControl)
            ? reason
            : null;
}
