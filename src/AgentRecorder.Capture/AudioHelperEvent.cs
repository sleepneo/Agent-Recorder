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
    public string? AudioSourceKind { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? BitsPerSample { get; set; }
    public long? FirstSampleAnchorTicks { get; set; }
    public long? TimestampFrequency { get; set; }
    public long? BytesWritten { get; set; }
    public string? CaptureMethod { get; set; }
    public string? CaptureEngine { get; set; }
    public string? CaptureStrategy { get; set; }
    public string? PairEvidence { get; set; }
    public string? AutoHfpPairStatus { get; set; }
    public string? AutoHfpPairResultCode { get; set; }
    public string? AutoHfpPairTransportClassification { get; set; }
    public long? RenderPrimeReadyMs { get; set; }
    public long? ElapsedMs { get; set; }
    public long? WallElapsedMs { get; set; }
    public long? EstimatedGapMs { get; set; }
    public long? DurationMs { get; set; }
    public string? StopReason { get; set; }
    public string? ErrorCode { get; set; }
    public string? Reason { get; set; }
    public string? Hresult { get; set; }
    public string? FailureStage { get; set; }
    public string? EndpointId { get; set; }
    public string? PartialOutputPath { get; set; }
    public string? SecondaryFailure { get; set; }

    // Runtime stream-health and recovery metrics (optional, audio-helper-v1.1+).
    public long? LastCallbackAgeMs { get; set; }
    public long? DiscontinuityCount { get; set; }
    public long? RecoveryCount { get; set; }
    public long? RecoveryAttempts { get; set; }
    public long? GapFilledBytes { get; set; }
    public long? GapFilledMs { get; set; }
    public long? MaxEstimatedGapMs { get; set; }
    public long? QpcOutlierCount { get; set; }
    public string? ContinuityStatus { get; set; }

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
    public bool LastCallbackAgeMsParseFailed { get; set; }
    public bool DiscontinuityCountParseFailed { get; set; }
    public bool RecoveryCountParseFailed { get; set; }
    public bool RecoveryAttemptsParseFailed { get; set; }
    public bool GapFilledBytesParseFailed { get; set; }
    public bool GapFilledMsParseFailed { get; set; }
    public bool MaxEstimatedGapMsParseFailed { get; set; }
    public bool QpcOutlierCountParseFailed { get; set; }
    public bool RenderPrimeReadyMsParseFailed { get; set; }
    public bool DuplicateField { get; set; }
    public bool AudioSourceKindDuplicate { get; set; }
    public bool AudioSourceKindInvalid { get; set; }

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
        DurationMsParseFailed ||
        LastCallbackAgeMsParseFailed ||
        DiscontinuityCountParseFailed ||
        RecoveryCountParseFailed ||
        RecoveryAttemptsParseFailed ||
        GapFilledBytesParseFailed ||
        GapFilledMsParseFailed ||
        MaxEstimatedGapMsParseFailed ||
        QpcOutlierCountParseFailed ||
        RenderPrimeReadyMsParseFailed;
}
