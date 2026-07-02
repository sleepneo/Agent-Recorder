namespace AgentRecorder.Capture;

/// <summary>
/// Raw process-execution result: exit code + stdout/stderr strings.
/// Deliberately unstructured — used as input to WgcHelperOutputParser.
/// </summary>
public sealed class WgcHelperProcessResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
}
