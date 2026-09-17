using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local ownership boundary for a capture that has crossed the
/// standing start gate.  The bridge creates the implementation only after it
/// has built the ticket and is about to call the proof-bearing backend start.
/// </summary>
internal interface IStandingLeaseCaptureLifecycleSession : IDisposable
{
    bool TryAttach(ICaptureBackend backend, out string failureReason);

    StandingLeaseLifecycleActionResult StartFailed();

    StandingLeaseLifecycleActionResult Stop();
}

/// <summary>
/// Optional adapter used when RecordingEngine owns the backend callbacks.
/// The persistence session remains the durable lifecycle owner, but it no
/// longer installs a competing OnNaturalExit delegate in that composition.
/// </summary>
internal interface IStandingLeaseCaptureLifecycleDriver
{
    StandingLeaseLifecycleActionResult ObserveFirstFrame(FirstFrameObservation observation);

    StandingLeaseLifecycleActionResult ObserveCaptureEnded(CaptureEndedObservation observation);

    StandingLeaseLifecycleActionResult ObserveNaturalExit(int exitCode, OutputMeta meta);

    StandingLeaseLifecycleActionResult FailBeforeStart(string reason);

    StandingLeaseCaptureStopResult StopForEngine(string? reason = null);
}

internal readonly record struct StandingLeaseCaptureStopResult(
    StandingLeaseLifecycleActionResult Lifecycle,
    OutputMeta? Meta,
    int ExitCode);

internal readonly record struct StandingLeaseLifecycleActionResult(
    bool Succeeded,
    bool Changed,
    bool Terminal,
    string Reason)
{
    internal static StandingLeaseLifecycleActionResult Applied(string reason, bool terminal = false) =>
        new(true, true, terminal, reason);

    internal static StandingLeaseLifecycleActionResult Idempotent(string reason = "idempotent_noop", bool terminal = false) =>
        new(true, false, terminal, reason);

    internal static StandingLeaseLifecycleActionResult Rejected(string reason) =>
        new(false, false, false, reason);
}
