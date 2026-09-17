using System.Security.Principal;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Win32;

namespace AgentRecorder.App;

/// <summary>
/// Production composition root for the bounded standing one-shot flow.
/// Recovery completes before the scheduler is started. A missing component or
/// failed startup leaves ordinary recording available but keeps the unattended
/// capability false and the dispatcher stopped.
/// </summary>
internal sealed class StandingLeaseNaturalWakeRuntime : IDisposable
{
    private readonly SqliteOperationalStore _store;
    private readonly RecordingEngine _engine;
    private readonly TrayContext _tray;
    private readonly AuditLogger _audit;
    private readonly StandingLeaseNaturalWakeStartupRecovery _recovery;
    private readonly StandingLeaseNaturalWakeScheduler _scheduler;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _started;
    private int _disposed;
    private bool _systemEventsRegistered;

    internal StandingLeaseNaturalWakeRuntime(
        SqliteOperationalStore store,
        RecordingEngine engine,
        TrayContext tray,
        AuditLogger audit)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));

        _recovery = new StandingLeaseNaturalWakeStartupRecovery(
            _store,
            utcNow: () => DateTimeOffset.UtcNow,
            audit: _audit.Log);

        Func<StandingLeaseCaptureExecutionTicket, CancellationToken, Task<StandingLeaseCaptureExecutionResult>> engineStarter =
            (ticket, cancellationToken) => Task.FromResult(
                _engine.StartStandingCapture(
                    ticket,
                    _tray,
                    backend => new StandingLeaseOneShotExecutionSession(
                        _store,
                        ticket.Specification,
                        backend,
                        () => DateTimeOffset.UtcNow,
                        attachBackendCallbacks: false),
                    cancellationToken));

        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            _store,
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null,
            executionStarterForProduction: engineStarter);
        var preparedDispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(
            _store,
            utcNowForTest: null,
            coordinatorForTest: coordinator.ExecuteAsync,
            auditForTest: _audit.Log);

        _scheduler = new StandingLeaseNaturalWakeScheduler(
            _store,
            preparedDispatcher,
            CurrentUserSid,
            () => CaptureAuthorizationSessionBinding.Current,
            utcNow: () => DateTimeOffset.UtcNow,
            audit: _audit.Log);
    }

    internal bool ExecutionSupported =>
        Volatile.Read(ref _started) != 0 &&
        Volatile.Read(ref _disposed) == 0 &&
        _scheduler.IsStarted &&
        _systemEventsRegistered;

    internal bool Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return ExecutionSupported;

        if (!_recovery.RecoverAll(CurrentUserSid(), CaptureAuthorizationSessionBinding.Current))
        {
            SafeAudit("standing_lease.runtime_blocked", new { reason_code = "startup_recovery_failed" });
            Volatile.Write(ref _started, 0);
            return false;
        }

        if (!TryRegisterSystemLifecycleSignals())
        {
            SafeAudit("standing_lease.runtime_blocked", new { reason_code = "session_lifecycle_signals_unavailable" });
            Volatile.Write(ref _started, 0);
            return false;
        }

        if (!_scheduler.Start())
        {
            UnregisterSystemLifecycleSignals();
            SafeAudit("standing_lease.runtime_blocked", new { reason_code = "natural_wake_scheduler_start_failed" });
            Volatile.Write(ref _started, 0);
            return false;
        }

        SafeAudit("standing_lease.runtime_started", new { execution_supported = true });
        return true;
    }

    private bool TryRegisterSystemLifecycleSignals()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _systemEventsRegistered = true;
            return true;
        }
        catch
        {
            _systemEventsRegistered = false;
            return false;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or
            SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteDisconnect)
        {
            try { _engine.StopAllSync("session_interrupted"); } catch { }
            _scheduler.Signal();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            try { _engine.StopAllSync("sleep_interrupted"); } catch { }
            _scheduler.Signal();
        }
    }

    private void UnregisterSystemLifecycleSignals()
    {
        if (!_systemEventsRegistered)
            return;
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        _systemEventsRegistered = false;
    }

    private static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private void SafeAudit(string eventName, object payload)
    {
        try { _audit.Log(eventName, payload); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _shutdownCts.Cancel(); } catch { }
        UnregisterSystemLifecycleSignals();
        _scheduler.Dispose();
        _shutdownCts.Dispose();
        SafeAudit("standing_lease.runtime_stopped", new { reason_code = "application_shutdown" });
    }
}
