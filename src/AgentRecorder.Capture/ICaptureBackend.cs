namespace AgentRecorder.Capture;

public interface ICaptureBackend : IDisposable
{
    /// <summary>
    /// Proof-bearing production start boundary. RecordingEngine consumes and
    /// validates the proof before invoking this method. A backend is never
    /// startable through a proof-less interface contract.
    /// </summary>
    void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof);
    OutputMeta Stop();

    /// <summary>
    /// Aborts an active capture for an application-owned lifecycle reason.
    /// The reason is strongly typed so arbitrary client input or backend text
    /// cannot manufacture a trusted lifecycle failure.
    /// </summary>
    OutputMeta Abort(CaptureAbortReason reason)
    {
        var meta = Stop();
        meta.StopReason = CaptureAbortReasonCodes.ToCode(reason);
        return meta;
    }

    void OnNaturalExit(Action<int, OutputMeta> callback) { }

    /// <summary>
    /// Cancels a recording that has not yet reached active capture.
    /// Default implementation simply calls <see cref="Stop"/>.
    /// Backends may override this to avoid starting video workers or
    /// finalizing when cancellation happens during warmup.
    /// </summary>
    void Cancel() { Stop(); }

    /// <summary>Process exit code (or -1 when not started / unknown).</summary>
    int ExitCode => -1;
}
