namespace AgentRecorder.Capture;

/// <summary>
/// Result of a completed <see cref="WgcContinuousManagedSession"/>.
/// </summary>
public sealed class WgcContinuousSessionResult
{
    /// <summary>Final session state.</summary>
    public WgcContinuousManagedSessionState State { get; set; }

    /// <summary>Helper process exit code, or -1 if unavailable.</summary>
    public int ExitCode { get; set; } = -1;

    /// <summary>Parsed and validated event-stream summary.</summary>
    public WgcContinuousSessionSummary? Summary { get; set; }

    /// <summary>True when the caller explicitly requested a stop.</summary>
    public bool StopRequestedByCaller { get; set; }

    /// <summary>Bounded tail of helper stderr for diagnostics.</summary>
    public string StderrTail { get; set; } = "";

    /// <summary>Output path reported by the helper or from options.</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Whether the final output file exists on disk.</summary>
    public bool OutputFileExists { get; set; }

    /// <summary>Size of the final output file in bytes.</summary>
    public long OutputFileSizeBytes { get; set; }

    /// <summary>Stable failure phase when <see cref="State"/> is Failed.</summary>
    public string FailurePhase { get; set; } = "";

    /// <summary>Failure category derived from the helper summary (e.g. timeout, encoding_error).</summary>
    public string FailureCategory { get; set; } = "";

    /// <summary>True when at least one PROGRESS event with FramesCaptured > 0 was observed.</summary>
    public bool FirstFrameObserved { get; set; }

    /// <summary>Frame number reported at the first-frame observation.</summary>
    public long? FirstFrameNumber { get; set; }

    /// <summary>Elapsed milliseconds reported at the first-frame observation.</summary>
    public long? FirstFrameElapsedMs { get; set; }
}
