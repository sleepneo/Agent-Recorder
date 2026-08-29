namespace AgentRecorder.Capture;

public enum WgcContinuousTargetKind
{
    Display,
    Window,
    Region
}

/// <summary>
/// Options for a single WGC continuous recording session managed by
/// <see cref="WgcContinuousManagedSession"/>.
/// </summary>
public sealed class WgcContinuousSessionOptions
{
    /// <summary>Absolute path to wgc-native-helper.exe.</summary>
    public string HelperExePath { get; set; } = "";

    /// <summary>1-64 character safe recording identifier.</summary>
    public string RecordingId { get; set; } = "";

    /// <summary>Capture target selected after local approval.</summary>
    public WgcContinuousTargetKind TargetKind { get; set; } = WgcContinuousTargetKind.Display;

    /// <summary>Target display left coordinate in physical pixels.</summary>
    public int DisplayX { get; set; }

    /// <summary>Target display top coordinate in physical pixels.</summary>
    public int DisplayY { get; set; }

    /// <summary>Target display width in physical pixels.</summary>
    public int DisplayWidth { get; set; }

    /// <summary>Target display height in physical pixels.</summary>
    public int DisplayHeight { get; set; }

    /// <summary>Effective region left coordinate in virtual physical pixels.</summary>
    public int RegionX { get; set; }

    /// <summary>Effective region top coordinate in virtual physical pixels.</summary>
    public int RegionY { get; set; }

    /// <summary>Effective region width in physical pixels.</summary>
    public int RegionWidth { get; set; }

    /// <summary>Effective region height in physical pixels.</summary>
    public int RegionHeight { get; set; }

    /// <summary>Target window HWND. Required only for the Window target.</summary>
    public nint WindowHandle { get; set; }

    /// <summary>Absolute output MP4 path.</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Recording duration in milliseconds, 1000-60000.</summary>
    public int DurationMs { get; set; } = 5000;

    /// <summary>Target frame rate, 1-60.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>Normalized hidden WGC encoder policy; actual selection is reported by IPC.</summary>
    public WgcEncoderMode EncoderMode { get; set; } = WgcEncoderMode.Software;

    /// <summary>Absolute path to the begin authorization signal file.</summary>
    public string BeginSignalPath { get; set; } = "";

    /// <summary>Secret token that must be written to the begin signal file.</summary>
    public string BeginToken { get; set; } = "";

    /// <summary>How long the helper waits for begin authorization, 100-300000 ms.</summary>
    public int BeginTimeoutMs { get; set; } = 30000;

    /// <summary>Absolute path to the stop signal file.</summary>
    public string StopSignalPath { get; set; } = "";

    /// <summary>Maximum total lifetime of the helper process in milliseconds.</summary>
    public int ProcessTimeoutMs { get; set; } = 30000;

    /// <summary>Maximum time to wait for graceful stop after creating the stop signal.</summary>
    public int StopWaitTimeoutMs { get; set; } = 10000;
}
