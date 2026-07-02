namespace AgentRecorder.Capture;

/// <summary>
/// Result type for a single event in the helper IPC v2 event stream.
/// </summary>
public enum ContinuousEventResult
{
    Unknown = 0,
    Started,
    Progress,
    Ok,
    Stopped,
    Fail
}

/// <summary>
/// Represents a single event emitted by wgc-native-helper.exe on stdout
/// during a continuous recording session. Events are delimited by blank lines.
/// </summary>
public sealed class WgcContinuousEvent
{
    /// <summary>
    /// The RESULT field value: STARTED, PROGRESS, OK, STOPPED, or FAIL.
    /// </summary>
    public ContinuousEventResult Result { get; set; } = ContinuousEventResult.Unknown;

    /// <summary>
    /// The Stage field value (e.g., "SessionStarted", "Capturing", "Complete").
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// The RecordingId field value.
    /// </summary>
    public string? RecordingId { get; set; }

    /// <summary>
    /// The Output path field value.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// The Container field value (e.g., "mp4").
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// The Codec field value (e.g., "h264").
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// The Fps field value.
    /// </summary>
    public int? Fps { get; set; }

    /// <summary>
    /// The Width field value.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// The Height field value.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// The FramesCaptured field value.
    /// </summary>
    public long? FramesCaptured { get; set; }

    /// <summary>
    /// The FramesDropped field value.
    /// </summary>
    public long? FramesDropped { get; set; }

    /// <summary>
    /// The ElapsedMs field value.
    /// </summary>
    public long? ElapsedMs { get; set; }

    /// <summary>
    /// The DurationMs field value.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// The BytesWritten field value.
    /// </summary>
    public long? BytesWritten { get; set; }

    /// <summary>
    /// The FileSize field value (parsed from formats like "12345" or "12345 bytes").
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// The StopReason field value.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// The CaptureMethod field value.
    /// </summary>
    public string? CaptureMethod { get; set; }

    /// <summary>
    /// The HRESULT field value.
    /// </summary>
    public string? Hresult { get; set; }

    /// <summary>
    /// The Reason field value (for FAIL events).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The ErrorCode field value.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The PartialOutputPath field value.
    /// </summary>
    public string? PartialOutputPath { get; set; }

    /// <summary>
    /// Whether Fps was present but failed to parse as a number.
    /// </summary>
    public bool FpsParseFailed { get; set; }

    /// <summary>
    /// Whether Width was present but failed to parse as a number.
    /// </summary>
    public bool WidthParseFailed { get; set; }

    /// <summary>
    /// Whether Height was present but failed to parse as a number.
    /// </summary>
    public bool HeightParseFailed { get; set; }

    /// <summary>
    /// Whether FramesCaptured was successfully parsed as a number.
    /// </summary>
    public bool FramesCapturedParseFailed { get; set; }

    /// <summary>
    /// Whether FramesDropped was present but failed to parse as a number.
    /// </summary>
    public bool FramesDroppedParseFailed { get; set; }

    /// <summary>
    /// Whether ElapsedMs was successfully parsed as a number.
    /// </summary>
    public bool ElapsedMsParseFailed { get; set; }

    /// <summary>
    /// Whether DurationMs was present but failed to parse as a number.
    /// </summary>
    public bool DurationMsParseFailed { get; set; }

    /// <summary>
    /// Whether BytesWritten was present but failed to parse as a number.
    /// </summary>
    public bool BytesWrittenParseFailed { get; set; }

    /// <summary>
    /// Whether FileSize was successfully parsed as a number.
    /// </summary>
    public bool FileSizeParseFailed { get; set; }

    /// <summary>
    /// Whether any numeric field on this event had a parse error.
    /// </summary>
    public bool HasNumericParseError =>
        FpsParseFailed ||
        WidthParseFailed ||
        HeightParseFailed ||
        FramesCapturedParseFailed ||
        FramesDroppedParseFailed ||
        ElapsedMsParseFailed ||
        DurationMsParseFailed ||
        BytesWrittenParseFailed ||
        FileSizeParseFailed;
}
