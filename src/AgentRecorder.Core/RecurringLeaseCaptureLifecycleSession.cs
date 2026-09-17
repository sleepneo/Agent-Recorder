using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local ownership boundary for one recurring capture after the
/// recurring start-commit. The implementation lives in Persistence; Core
/// exposes only the lifecycle shape and never references SQLite.
/// </summary>
internal interface IRecurringLeaseCaptureLifecycleSession : IDisposable
{
    bool TryAttach(ICaptureBackend backend, out string failureReason);

    RecurringLeaseCaptureLifecycleHandoffResult CompleteStartHandoff();

    RecurringLeaseLifecycleActionResult StartFailed();

    RecurringLeaseLifecycleActionResult Stop();
}

internal enum RecurringLeaseCaptureLifecycleHandoffStatus
{
    Active,
    AlreadyTerminal,
    FailClosed,
    NotAttached,
    Disposed,
}

internal readonly record struct RecurringLeaseCaptureLifecycleHandoffResult(
    RecurringLeaseCaptureLifecycleHandoffStatus Status,
    RecurringLeaseLifecycleActionResult? FirstTerminalResult = null)
{
    internal bool IsActive => Status == RecurringLeaseCaptureLifecycleHandoffStatus.Active;
}

/// <summary>
/// Optional adapter for a host that owns backend callback registration. In
/// driver mode the recurring session must not install competing callbacks.
/// </summary>
internal interface IRecurringLeaseCaptureLifecycleDriver
{
    RecurringLeaseLifecycleActionResult ObserveFirstFrame(FirstFrameObservation observation);

    RecurringLeaseLifecycleActionResult ObserveCaptureEnded(CaptureEndedObservation observation);

    RecurringLeaseLifecycleActionResult ObserveNaturalExit(int exitCode, OutputMeta meta);

    RecurringLeaseLifecycleActionResult FailBeforeStart(string reason);

    RecurringLeaseCaptureStopResult StopForEngine(string? reason = null);
}

internal readonly record struct RecurringLeaseCaptureStopResult(
    RecurringLeaseLifecycleActionResult Lifecycle,
    OutputMeta? Meta,
    int ExitCode);

/// <summary>
/// The process-local result mirrors the durable transaction result. It is
/// deliberately internal and is not a transport/API or JSON contract.
/// </summary>
internal readonly record struct RecurringLeaseLifecycleActionResult(
    bool Succeeded,
    bool Changed,
    bool Terminal,
    string Reason)
{
    internal static RecurringLeaseLifecycleActionResult Applied(string reason, bool terminal = false) =>
        new(true, true, terminal, reason);

    internal static RecurringLeaseLifecycleActionResult Idempotent(
        string reason = "idempotent_noop",
        bool terminal = false) =>
        new(true, false, terminal, reason);

    internal static RecurringLeaseLifecycleActionResult Rejected(string reason) =>
        new(false, false, false, reason);
}
