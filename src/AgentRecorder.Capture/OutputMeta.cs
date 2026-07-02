namespace AgentRecorder.Capture;

/// <summary>
/// Metadata about capture output produced by ICaptureBackend.
/// Supports both FFmpeg (mp4/h264) and WGC still-frame (png/still-frame) backends.
/// </summary>
public sealed class OutputMeta
{
    public long SizeBytes; public double DurationSeconds;
    public int Width; public int Height; public int Fps;
    public string? StderrLog;
    public string[] Warnings = Array.Empty<string>();

    /// <summary>Actual output file path (for WGC backends this is the PNG path, not the .mp4 rec.OutputPath).</summary>
    public string? OutputPath;

    /// <summary>Container format: "mp4" for FFmpeg, "png" for WGC still-frame. Defaults to "mp4" when unset.</summary>
    public string? Container;

    /// <summary>Codec: "h264" for FFmpeg, "still-frame" for WGC PNG. Defaults to "h264" when unset.</summary>
    public string? Codec;

    /// <summary>Capture method indicator, e.g. "WGC_D3D11_FRAME_SURFACE" from the native helper.</summary>
    public string? CaptureMethod;

    /// <summary>WGC helper stage string (e.g. "Complete" or "FrameArrived(timeout)").</summary>
    public string? Stage;

    /// <summary>WGC helper HRESULT on failure (e.g. "0x800705B4").</summary>
    public string? Hresult;

    /// <summary>True when the output file exists on disk (WGC backend post-check).</summary>
    public bool OutputFileExists;

    /// <summary>True when the first 8 bytes match the PNG signature
    /// (89 50 4E 47 0D 0A 1A 0A). Used by RecordingEngine to gate WGC still-frame success.</summary>
    public bool IsValidPngSignature;
}
