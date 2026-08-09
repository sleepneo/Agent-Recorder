namespace AgentRecorder.Capture;

/// <summary>
/// Raw process-execution result: exit code + stdout/stderr strings.
/// Deliberately unstructured — used by the continuous WGC probe/session
/// process boundary.
/// </summary>
public sealed class WgcHelperProcessResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public bool StandardOutputTruncated { get; set; }
    public bool StandardErrorTruncated { get; set; }
}
