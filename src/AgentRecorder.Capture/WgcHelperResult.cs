namespace AgentRecorder.Capture;

/// <summary>
/// Structured result from WGC helper. Success is defined as
/// (ExitCode == 0) AND (RESULT == "OK").
/// Missing fields from helper stdout text; all fields are preserved in
/// RawStandardOutput / RawStandardError for auditing.
/// </summary>
public sealed class WgcHelperResult
{
    /// <summary>True when exit code is 0 AND RESULT: OK was parsed from stdout.</summary>
    public bool Success { get; set; }

    /// <summary>Raw "RESULT" token found on stdout ("OK", "FAIL", or null).</summary>
    public string? ResultToken { get; set; }

    /// <summary>Stage text preserved verbatim from the helper (e.g. "FrameArrived(timeout)").</summary>
    public string? Stage { get; set; }

    /// <summary>Parsed HRESULT string (e.g. "0x800705B4"), or null.</summary>
    public string? Hresult { get; set; }

    /// <summary>Reason text on failure.</summary>
    public string? Reason { get; set; }

    /// <summary>Output PNG path parsed from helper.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Parsed frame width; 0 when not present / not numeric.</summary>
    public int Width { get; set; }

    /// <summary>Parsed frame height; 0 when not present / not numeric.</summary>
    public int Height { get; set; }

    /// <summary>Parsed file size; 0 when not present / not numeric.</summary>
    public long FileSize { get; set; }

    /// <summary>Parsed SHA-256 hex string from helper (uppercase).</summary>
    public string? Sha256 { get; set; }

    /// <summary>Window display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Capture method; strict WGC should be WGC_D3D11_FRAME_SURFACE.</summary>
    public string? CaptureMethod { get; set; }

    /// <summary>Parsed HWND string (e.g. 0x0000000000012345).</summary>
    public string? Hwnd { get; set; }

    /// <summary>Raw stdout; always preserved for auditing.</summary>
    public string RawStandardOutput { get; set; } = "";

    /// <summary>Raw stderr; always preserved for auditing.</summary>
    public string RawStandardError { get; set; } = "";

    /// <summary>Process exit code.</summary>
    public int ExitCode { get; set; }
}
