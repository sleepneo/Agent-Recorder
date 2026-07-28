namespace AgentRecorder.Capture;

/// <summary>
/// Possible RESULT values emitted by the audio helper IPC protocol.
/// </summary>
public enum AudioHelperEventResult
{
    Unknown,
    Started,
    Progress,
    Ok,
    Stopped,
    Fail
}

/// <summary>
/// A single event parsed from the audio helper's blank-line-delimited stdout stream.
/// </summary>
public sealed class AudioHelperEvent
{
    public AudioHelperEventResult Result { get; set; }
    public string? Stage { get; set; }
    public string? RecordingId { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? BitsPerSample { get; set; }
    public long? FirstSampleAnchorTicks { get; set; }
    public long? TimestampFrequency { get; set; }
    public long? BytesWritten { get; set; }
    public string? CaptureMethod { get; set; }
    public long? ElapsedMs { get; set; }
    public long? WallElapsedMs { get; set; }
    public long? EstimatedGapMs { get; set; }
    public long? DurationMs { get; set; }
    public string? StopReason { get; set; }
    public string? ErrorCode { get; set; }
    public string? Reason { get; set; }
    public string? Hresult { get; set; }
    public string? PartialOutputPath { get; set; }

    public bool SampleRateParseFailed { get; set; }
    public bool ChannelsParseFailed { get; set; }
    public bool BitsPerSampleParseFailed { get; set; }
    public bool FirstSampleAnchorTicksParseFailed { get; set; }
    public bool TimestampFrequencyParseFailed { get; set; }
    public bool BytesWrittenParseFailed { get; set; }
    public bool ElapsedMsParseFailed { get; set; }
    public bool WallElapsedMsParseFailed { get; set; }
    public bool EstimatedGapMsParseFailed { get; set; }
    public bool DurationMsParseFailed { get; set; }

    public bool HasNumericParseError =>
        SampleRateParseFailed ||
        ChannelsParseFailed ||
        BitsPerSampleParseFailed ||
        FirstSampleAnchorTicksParseFailed ||
        TimestampFrequencyParseFailed ||
        BytesWrittenParseFailed ||
        ElapsedMsParseFailed ||
        WallElapsedMsParseFailed ||
        EstimatedGapMsParseFailed ||
        DurationMsParseFailed;
}
