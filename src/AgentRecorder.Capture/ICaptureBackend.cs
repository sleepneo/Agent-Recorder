namespace AgentRecorder.Capture;

public interface ICaptureBackend : IDisposable
{
    void Start(CaptureConfig cfg);
    OutputMeta Stop();
    void OnNaturalExit(Action<int, OutputMeta> callback) { }

    /// <summary>Process exit code (or -1 when not started / unknown).</summary>
    int ExitCode => -1;
}
