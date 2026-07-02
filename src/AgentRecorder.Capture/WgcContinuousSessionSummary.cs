namespace AgentRecorder.Capture;

/// <summary>
/// Terminal state of a continuous recording session after parsing the event stream.
/// </summary>
public enum ContinuousSessionState
{
    Unknown = 0,
    Success,
    Stopped,
    Failed,
    MalformedSequence
}

/// <summary>
/// Summary of a continuous recording session, derived from the parsed event stream.
/// This is used to build the evidence JSON and for session state machine validation.
/// </summary>
public sealed class WgcContinuousSessionSummary
{
    /// <summary>
    /// The terminal state of the session.
    /// </summary>
    public ContinuousSessionState State { get; set; } = ContinuousSessionState.Unknown;

    /// <summary>
    /// Whether the session was successful (OK or STOPPED gracefully).
    /// </summary>
    public bool Success => State == ContinuousSessionState.Success || State == ContinuousSessionState.Stopped;

    /// <summary>
    /// The RecordingId from the STARTED event.
    /// </summary>
    public string? RecordingId { get; set; }

    /// <summary>
    /// The Output path from the STARTED event or partial output path on failure.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// The Container from the STARTED event (e.g., "mp4").
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// The Codec from the STARTED event (e.g., "h264").
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// The Fps from the STARTED event.
    /// </summary>
    public int? Fps { get; set; }

    /// <summary>
    /// The Width from the STARTED event or the final event.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// The Height from the STARTED event or the final event.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// The FramesCaptured from the final event.
    /// </summary>
    public long? FramesCaptured { get; set; }

    /// <summary>
    /// The FramesDropped from the final event.
    /// </summary>
    public long? FramesDropped { get; set; }

    /// <summary>
    /// The DurationMs from the final event.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// The BytesWritten from the final event.
    /// </summary>
    public long? BytesWritten { get; set; }

    /// <summary>
    /// The FileSize from the final event.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// The StopReason from STOPPED or FAIL events.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// The CaptureMethod from the STARTED event.
    /// </summary>
    public string? CaptureMethod { get; set; }

    /// <summary>
    /// The Hresult from FAIL events.
    /// </summary>
    public string? Hresult { get; set; }

    /// <summary>
    /// The Reason from FAIL events.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The ErrorCode from FAIL events.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The PartialOutputPath from FAIL events.
    /// </summary>
    public string? PartialOutputPath { get; set; }

    /// <summary>
    /// Whether a partial output file exists (PartialOutputPath is set).
    /// STOPPED/OK sessions do not produce partial output; only FAIL with
    /// failure surfaces this flag.
    /// </summary>
    public bool PartialOutputExists => !string.IsNullOrEmpty(PartialOutputPath);

    /// <summary>
    /// Whether the session has a structural malformed sequence flag raised by the parser.
    /// </summary>
    public bool HasMalformedSequence { get; set; }

    /// <summary>
    /// Whether any event in the stream had a numeric field parse failure.
    /// </summary>
    public bool HasNumericParseError { get; set; }

    /// <summary>
    /// Validation errors produced by parser state-machine checks.
    /// </summary>
    public List<string> ValidationErrors { get; } = new();

    /// <summary>
    /// Returns the evidence stop-reason value. STOPPED maps to user_requested,
    /// OK maps to duration_reached, and FAIL surfaces the specific failure
    /// category (timeout, cancelled, encoding_error, disk_full,
    /// window_not_found, zero_frames, error) when possible.
    /// </summary>
    public string GetStopReasonForEvidence()
    {
        if (State == ContinuousSessionState.Success)
            return "duration_reached";

        if (State == ContinuousSessionState.Stopped)
            return "user_requested";

        // Failed or malformed-with-failure signal: try to surface the specific
        // failure category instead of collapsing everything to "error".
        string[] allowedCategories = new[]
        {
            "timeout", "cancelled", "encoding_error", "disk_full",
            "window_not_found", "zero_frames", "error"
        };

        string[] preciseCategories = new[]
        {
            "timeout", "cancelled", "encoding_error", "disk_full",
            "window_not_found", "zero_frames"
        };

        // Try explicit StopReason from the event (may come from the event
        // StopReason field or ErrorCode fallback)
        if (!string.IsNullOrEmpty(StopReason))
        {
            string lowered = StopReason.ToLowerInvariant();
            if (preciseCategories.Contains(lowered))
                return lowered;
        }

        // Try ErrorCode
        if (!string.IsNullOrEmpty(ErrorCode))
        {
            string lowered = ErrorCode.ToLowerInvariant();
            if (preciseCategories.Contains(lowered))
                return lowered;
        }

        // Try Reason (free-text) for an allowed-category match
        if (!string.IsNullOrEmpty(Reason))
        {
            string lowered = Reason.ToLowerInvariant();
            foreach (var cat in preciseCategories)
            {
                if (lowered.Contains(cat))
                    return cat;
            }
        }

        // Exact "error" as a fallback for StopReason/ErrorCode/Reason
        // is still allowed.
        if (!string.IsNullOrEmpty(StopReason) && StopReason.ToLowerInvariant() == "error")
            return "error";
        if (!string.IsNullOrEmpty(ErrorCode) && ErrorCode.ToLowerInvariant() == "error")
            return "error";
        if (!string.IsNullOrEmpty(Reason) && Reason.ToLowerInvariant() == "error")
            return "error";

        // Malformed with no mapping
        return "error";
    }
}
