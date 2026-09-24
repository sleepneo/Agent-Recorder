using System.Security.Principal;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Win32;

namespace AgentRecorder.App;

/// <summary>
/// App composition for persisted recurring advancement and natural wake.
/// Startup recovery and one bounded advancement pass must succeed before the
/// scheduler is visible as a supported capability. Engine and its durable
/// lifecycle session remain the single capture owners.
/// </summary>
internal sealed class RecurringLeaseNaturalWakeRuntime : IDisposable
{
    internal const int StartupRecoveryBatchSize = 8;
    internal const int MaxStartupRecoveryBatches = 8;
    internal const int MaxStartupRecoveryCandidates =
        StartupRecoveryBatchSize * MaxStartupRecoveryBatches;

    private readonly SqliteOperationalStore _store;
    private readonly RecordingEngine _engine;
    private readonly ITrayContext _tray;
    private readonly AuditLogger _audit;
    private readonly StandingLeaseStartSafetyInterlock _startSafetyInterlock;
    private readonly RecurringLeaseStartupRecovery _recovery;
    private readonly RecurringScheduleAdvancementRuntime _advancement;
    private readonly Func<
        string?,
        string?,
        int,
        RecurringLeaseStartupRecoveryBatchResult> _recoverBatch;
    private readonly RecurringLeaseNaturalWakeScheduler _scheduler;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly Func<string?> _currentUserSid;
    private readonly Func<string?> _sessionBinding;
    private readonly Func<bool>? _registerSystemLifecycleSignalsForTest;
    private readonly object _startSync = new();
    private const int StartNotAttempted = 0;
    private const int StartStarting = 1;
    private const int StartSucceeded = 2;
    private const int StartFailed = 3;
    private const int StartDisposed = 4;
    private int _startState;
    private int _disposed;
    private int _systemEventsRegistered;
    private int _advancementHealthy;

    internal RecurringLeaseNaturalWakeRuntime(
        SqliteOperationalStore store,
        RecordingEngine engine,
        ITrayContext tray,
        AuditLogger audit,
        StandingLeaseStartSafetyInterlock startSafetyInterlock,
        Func<DateTimeOffset?>? utcNowForTest = null,
        Func<string?>? currentUserSidForTest = null,
        Func<string?>? sessionBindingForTest = null,
        Func<string?, string?, int, RecurringLeaseStartupRecoveryBatchResult>? recoveryBatchForTest = null,
        RecurringLeaseNaturalWakeScheduler? schedulerForTest = null,
        Func<bool>? registerSystemLifecycleSignalsForTest = null,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        TimeSpan? pollIntervalForTest = null,
        RecurringScheduleAdvancementRuntime? advancementRuntimeForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _startSafetyInterlock = startSafetyInterlock ??
            throw new ArgumentNullException(nameof(startSafetyInterlock));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _currentUserSid = currentUserSidForTest ?? (() => CurrentUserSid());
        _sessionBinding = sessionBindingForTest ??
            new Func<string?>(() => CaptureAuthorizationSessionBinding.Current);
        _registerSystemLifecycleSignalsForTest = registerSystemLifecycleSignalsForTest;
        _advancement = advancementRuntimeForTest ??
            new RecurringScheduleAdvancementRuntime(_store, _audit.Log);

        _recovery = new RecurringLeaseStartupRecovery(
            _store,
            _utcNow,
            _audit.Log);
        _recoverBatch = recoveryBatchForTest ??
            ((currentUserSid, sessionBinding, limit) =>
                _recovery.RecoverBatch(currentUserSid, sessionBinding, limit));

        var currentSafetyValidator = new SqliteRecurringLeaseCurrentSafetyValidator(_store);
        var environmentProvider = environmentProviderForTest ??
            SystemQueryRecurringOccurrenceEnvironmentProvider.Instance;
        Func<
            RecurringLeaseCaptureExecutionTicket,
            StandingLeaseStartSafetyInterlock,
            CancellationToken,
            Task<RecurringLeaseCaptureExecutionResult>> engineStarter =
            (ticket, exactInterlock, cancellationToken) => Task.FromResult(
                _engine.StartRecurringCapture(
                    ticket,
                    _tray,
                    exactInterlock,
                    backend => new RecurringLeaseCaptureExecutionSession(
                        _store,
                        ticket,
                        backend,
                        _utcNow,
                        attachBackendCallbacks: false),
                    cancellationToken));

        var coordinator = new RecurringOccurrenceExecutionCoordinator(
            _store,
            environmentProviderForTest: environmentProvider,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: _utcNow,
            startSafetyInterlockForTest: _startSafetyInterlock,
            currentSafetyValidatorForTest: currentSafetyValidator.Validate,
            lifecycleSessionFactoryForTest: null,
            currentUserSidForTest: () => _currentUserSid() ?? "",
            sessionBindingForTest: () => _sessionBinding() ?? "",
            executionStarterForProduction: engineStarter);
        var dispatcher = new RecurringOccurrenceNaturalWakeDispatcher(
            coordinator.ExecuteAsync,
            auditForTest: _audit.Log);

        _scheduler = schedulerForTest ?? new RecurringLeaseNaturalWakeScheduler(
            _store,
            dispatcher,
            _engine.HasActiveRecording,
            _currentUserSid,
            _sessionBinding,
            _utcNow,
            pollInterval: pollIntervalForTest,
            audit: _audit.Log,
            advanceBeforeQuery: RunAdvancementLoopPassAsync);
    }

    internal bool ExecutionSupported =>
        Volatile.Read(ref _startState) == StartSucceeded &&
        Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _advancementHealthy) != 0 &&
        _scheduler.IsStarted &&
        _scheduler.IsHealthy &&
        Volatile.Read(ref _systemEventsRegistered) != 0;

    internal int StartStateForTests => Volatile.Read(ref _startState);

    internal bool SystemEventsRegisteredForTests =>
        Volatile.Read(ref _systemEventsRegistered) != 0;

    internal bool SchedulerStartedForTests => _scheduler.IsStarted;

    internal bool AdvancementHealthyForTests => Volatile.Read(ref _advancementHealthy) != 0;

    internal bool Start()
    {
        lock (_startSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            if (Volatile.Read(ref _startState) != StartNotAttempted)
                return ExecutionSupported;

            Volatile.Write(ref _startState, StartStarting);

            if (!TryRecoverAtStartup(out var recoveryFailure))
            {
                SafeAudit("recurring_lease.runtime_blocked", new
                {
                    reason_code = recoveryFailure,
                    execution_supported = false,
                });
                Volatile.Write(ref _startState, StartFailed);
                return false;
            }

            if (!TryAdvanceAtStartup(out var advancementFailure))
            {
                SafeAudit("recurring_lease.runtime_blocked", new
                {
                    reason_code = advancementFailure,
                    execution_supported = false,
                });
                Volatile.Write(ref _startState, StartFailed);
                return false;
            }

            if (Volatile.Read(ref _disposed) != 0 || !TryRegisterSystemLifecycleSignals())
            {
                UnregisterSystemLifecycleSignals();
                SafeAudit("recurring_lease.runtime_blocked", new
                {
                    reason_code = Volatile.Read(ref _disposed) != 0
                        ? "recurring_runtime_disposed_during_start"
                        : "recurring_session_lifecycle_signals_unavailable",
                    execution_supported = false,
                });
                Volatile.Write(ref _startState, StartFailed);
                return false;
            }

            if (Volatile.Read(ref _disposed) != 0 || !_scheduler.Start())
            {
                UnregisterSystemLifecycleSignals();
                SafeAudit("recurring_lease.runtime_blocked", new
                {
                    reason_code = Volatile.Read(ref _disposed) != 0
                        ? "recurring_runtime_disposed_during_start"
                        : "recurring_natural_wake_scheduler_start_failed",
                    execution_supported = false,
                });
                Volatile.Write(ref _startState, StartFailed);
                return false;
            }

            // The started state is published only after recovery, event
            // registration and scheduler startup have all completed while the
            // startup lock excludes concurrent Start/Dispose callers.
            Volatile.Write(ref _startState, StartSucceeded);
            SafeAudit("recurring_lease.runtime_started", new { execution_supported = true });
            return true;
        }
    }

    private bool TryAdvanceAtStartup(out string failureReason)
    {
        failureReason = "recurring_advancement_startup_failed";
        if (!TryReadTrustedStartupInputs(out var currentUserSid, out var sessionBinding, out failureReason))
            return false;

        DateTimeOffset? nowUtc;
        try { nowUtc = _utcNow(); }
        catch
        {
            failureReason = "recurring_advancement_startup_clock_unavailable";
            return false;
        }
        if (nowUtc is null || nowUtc.Value.Offset != TimeSpan.Zero)
            return false;
        var succeeded = _advancement.RunStartupPass(nowUtc.Value, currentUserSid, sessionBinding);
        Volatile.Write(ref _advancementHealthy, succeeded ? 1 : 0);
        if (!succeeded)
            failureReason = "recurring_advancement_startup_failed";
        return succeeded;
    }

    private Task<bool> RunAdvancementLoopPassAsync(
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var succeeded = _advancement.RunLoopPass(
            nowUtc, currentUserSid, sessionBinding, cancellationToken);
        Volatile.Write(ref _advancementHealthy, succeeded ? 1 : 0);
        return Task.FromResult(succeeded);
    }

    private bool TryRecoverAtStartup(out string failureReason)
    {
        failureReason = "recurring_startup_recovery_failed";
        var totalScanned = 0;

        for (var batchNumber = 0; batchNumber < MaxStartupRecoveryBatches; batchNumber++)
        {
            if (!TryReadTrustedStartupInputs(out var currentUserSid, out var sessionBinding, out failureReason))
                return false;

            RecurringLeaseStartupRecoveryBatchResult batch;
            try
            {
                batch = _recoverBatch(
                    currentUserSid,
                    sessionBinding,
                    StartupRecoveryBatchSize);
            }
            catch
            {
                failureReason = "recurring_startup_recovery_failed";
                return false;
            }

            if (!batch.Succeeded)
            {
                failureReason = string.IsNullOrWhiteSpace(batch.FailureReason)
                    ? "recurring_startup_recovery_failed"
                    : batch.FailureReason!;
                return false;
            }

            if (batch.Scanned < 0 ||
                batch.Scanned > MaxStartupRecoveryCandidates - totalScanned)
            {
                failureReason = "recurring_startup_recovery_limit_exceeded";
                return false;
            }

            totalScanned += batch.Scanned;

            if (!batch.HasMore)
                return true;

            if (batch.Scanned == 0 || batch.Outcomes.Count == 0)
            {
                failureReason = "recurring_startup_recovery_non_converging";
                return false;
            }
        }

        failureReason = "recurring_startup_recovery_limit_exceeded";
        return false;
    }

    private bool TryReadTrustedStartupInputs(
        out string currentUserSid,
        out string sessionBinding,
        out string failureReason)
    {
        currentUserSid = "";
        sessionBinding = "";
        failureReason = "recurring_startup_recovery_clock_unavailable";
        try
        {
            var nowUtc = _utcNow();
            if (nowUtc is null || nowUtc.Value.Offset != TimeSpan.Zero)
                return false;

            currentUserSid = ReadCanonicalIdentity(_currentUserSid(), "sid");
            sessionBinding = ReadCanonicalIdentity(_sessionBinding(), "session");
            return true;
        }
        catch (StartupIdentityException exception)
        {
            failureReason = exception.Reason;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadCanonicalIdentity(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new StartupIdentityException(
                "recurring_startup_recovery_" + kind + "_invalid");
        }

        return value;
    }

    private bool TryRegisterSystemLifecycleSignals()
    {
        if (_registerSystemLifecycleSignalsForTest is not null)
        {
            try
            {
                Volatile.Write(
                    ref _systemEventsRegistered,
                    _registerSystemLifecycleSignalsForTest() ? 1 : 0);
                return Volatile.Read(ref _systemEventsRegistered) != 0;
            }
            catch
            {
                Volatile.Write(ref _systemEventsRegistered, 0);
                return false;
            }
        }

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Volatile.Write(ref _systemEventsRegistered, 1);
            return true;
        }
        catch
        {
            Volatile.Write(ref _systemEventsRegistered, 0);
            return false;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) =>
        HandleSessionSwitchForTests(e.Reason);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) =>
        HandlePowerModeChangedForTests(e.Mode);

    internal void HandleSessionSwitchForTests(SessionSwitchReason reason)
    {
        if (reason is not (SessionSwitchReason.SessionLock or
            SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteDisconnect))
            return;

        try { _engine.StopAllSync("session_interrupted"); } catch { }
        _scheduler.Signal();
    }

    internal void HandlePowerModeChangedForTests(PowerModes mode)
    {
        if (mode != PowerModes.Suspend)
            return;

        try { _engine.StopAllSync("sleep_interrupted"); } catch { }
        _scheduler.Signal();
    }

    private void UnregisterSystemLifecycleSignals()
    {
        if (Volatile.Read(ref _systemEventsRegistered) == 0)
            return;
        if (_registerSystemLifecycleSignalsForTest is null)
        {
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        }
        Volatile.Write(ref _systemEventsRegistered, 0);
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
        lock (_startSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Volatile.Write(ref _startState, StartDisposed);
            UnregisterSystemLifecycleSignals();
            _scheduler.Dispose();
            SafeAudit("recurring_lease.runtime_stopped", new
            {
                reason_code = "application_shutdown",
            });
        }
    }

    private sealed class StartupIdentityException : Exception
    {
        internal StartupIdentityException(string reason) => Reason = reason;

        internal string Reason { get; }
    }
}
