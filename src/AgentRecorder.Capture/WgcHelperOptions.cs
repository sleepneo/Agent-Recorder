namespace AgentRecorder.Capture;

/// <summary>
/// Input parameters for a single WGC helper invocation.
/// Caller must provide HWND explicitly; runner will clamp
/// TimeoutMs into the [100, 30000] range documented in the
/// helper and in doc/wgc-helper-ipc-contract.md.
/// </summary>
public sealed class WgcHelperOptions
{
    /// <summary>HWND of the window to capture. Caller must pass this explicitly.</summary>
    public nint Hwnd { get; set; }

    /// <summary>Output PNG path; must be under .local-data/wgc-tests/ or %TEMP%/ (enforced by helper).</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Timeout in milliseconds. Will be clamped into [100, 30000].</summary>
    public int TimeoutMs { get; set; } = 5000;
}
