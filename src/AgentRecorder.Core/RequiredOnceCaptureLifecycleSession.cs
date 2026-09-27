using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>
/// Durable lifecycle owner for the single committed required-once Run. This
/// contract is intentionally separate from every Lease lifecycle session.
/// </summary>
internal interface IRequiredOnceCaptureLifecycleSession : IDisposable
{
    bool TryAttach(ICaptureBackend backend, out string failureReason);
    RequiredOnceLifecycleActionResult StartFailed(string reason);
    RequiredOnceLifecycleActionResult Stop();
}

internal interface IRequiredOnceCaptureLifecycleDriver
{
    RequiredOnceLifecycleActionResult ObserveFirstFrame(FirstFrameObservation observation);
    RequiredOnceLifecycleActionResult ObserveCaptureEnded(CaptureEndedObservation observation);
    RequiredOnceLifecycleActionResult ObserveNaturalExit(int exitCode, OutputMeta meta);
    RequiredOnceLifecycleActionResult SettleFinalization(bool succeeded, OutputMeta meta, int exitCode, string? reason);
    RequiredOnceLifecycleActionResult FailBeforeStart(string reason);
    RequiredOnceCaptureStopResult StopForEngine(string? reason = null);
}

internal readonly record struct RequiredOnceCaptureStopResult(
    RequiredOnceLifecycleActionResult Lifecycle,
    OutputMeta? Meta,
    int ExitCode);

internal readonly record struct RequiredOnceLifecycleActionResult(
    bool Succeeded,
    bool Changed,
    bool Terminal,
    string Reason)
{
    internal static RequiredOnceLifecycleActionResult Applied(string reason, bool terminal = false) =>
        new(true, true, terminal, reason);

    internal static RequiredOnceLifecycleActionResult Idempotent(string reason = "idempotent_noop", bool terminal = false) =>
        new(true, false, terminal, reason);

    internal static RequiredOnceLifecycleActionResult Rejected(string reason) =>
        new(false, false, false, reason);
}

internal enum RequiredOnceCaptureExecutionStatus
{
    Started,
    Rejected,
    Failed,
}

internal sealed record RequiredOnceCaptureExecutionResult(
    RequiredOnceCaptureExecutionStatus Status,
    string Reason)
{
    internal static RequiredOnceCaptureExecutionResult Started() =>
        new(RequiredOnceCaptureExecutionStatus.Started, string.Empty);

    internal static RequiredOnceCaptureExecutionResult Rejected(string reason) =>
        new(RequiredOnceCaptureExecutionStatus.Rejected, reason);

    internal static RequiredOnceCaptureExecutionResult Failed(string reason) =>
        new(RequiredOnceCaptureExecutionStatus.Failed, reason);
}
