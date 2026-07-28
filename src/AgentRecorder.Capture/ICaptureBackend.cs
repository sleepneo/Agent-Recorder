namespace AgentRecorder.Capture;

public interface ICaptureBackend : IDisposable
{
    void Start(CaptureConfig cfg);
    OutputMeta Stop();
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
