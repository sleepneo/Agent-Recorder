namespace AgentRecorder.Capture;

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

    /// <summary>Target display left coordinate in physical pixels.</summary>
    public int DisplayX { get; set; }

    /// <summary>Target display top coordinate in physical pixels.</summary>
    public int DisplayY { get; set; }

    /// <summary>Target display width in physical pixels.</summary>
    public int DisplayWidth { get; set; }

    /// <summary>Target display height in physical pixels.</summary>
    public int DisplayHeight { get; set; }

    /// <summary>Absolute output MP4 path.</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Recording duration in milliseconds, 1000-10000.</summary>
    public int DurationMs { get; set; } = 5000;

    /// <summary>Target frame rate, 1-60.</summary>
    public int Fps { get; set; } = 30;

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
