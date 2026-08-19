namespace AgentRecorder.Capture;

/// <summary>
/// Metadata about capture output produced by ICaptureBackend.
/// Supports the FFmpeg and WGC continuous media backends.
/// </summary>
public sealed class OutputMeta
{
    public long SizeBytes; public double DurationSeconds;
    public int Width; public int Height; public int Fps;
    public string? StderrLog;
    public string[] Warnings = Array.Empty<string>();

    /// <summary>Actual output file path. WGC continuous publishes an MP4 here.</summary>
    public string? OutputPath;

    /// <summary>Container format, normally "mp4". Defaults to "mp4" when unset.</summary>
    public string? Container;

    /// <summary>Codec, normally "h264". Defaults to "h264" when unset.</summary>
    public string? Codec;

    /// <summary>Capture method indicator, e.g. "WGC_D3D11_FRAME_SURFACE" from the native helper.</summary>
    public string? CaptureMethod;

    /// <summary>WGC helper stage string (e.g. "Complete" or "FrameArrived(timeout)").</summary>
    public string? Stage;

    /// <summary>
    /// Stable backend terminal reason. WGC lifecycle failures use values such
    /// as window_closed, window_minimized, or size_changed.
    /// </summary>
    public string? StopReason;

    /// <summary>WGC helper HRESULT on failure (e.g. "0x800705B4").</summary>
    public string? Hresult;

    /// <summary>True when the output file exists on disk (WGC backend post-check).</summary>
    public bool OutputFileExists;

    /// <summary>Legacy image-signature field retained for non-WGC backends.</summary>
    public bool IsValidPngSignature;

    /// <summary>Actual WGC video encoder mode: software or hardware.</summary>
    public string? VideoEncoderMode;

    /// <summary>Stable WGC encoder selection reason.</summary>
    public string? VideoEncoderSelectionReason;

    // Microphone audio outcome tracking.
    public string? AudioSourceKind; // "none" | "microphone" | "system-loopback"
    public string? AudioStatus; // "not_requested" | "recorded" | "start_failed" | "lost" | "missing_audio_track"
    public bool HasAudioStream;
    public string? AudioCodec;

    /// <summary>
    /// When <see cref="AudioStatus"/> is "lost", this is the best-effort wall-clock
    /// timestamp (UTC milliseconds since epoch) at which audio was last known to be
    /// present. Null when no reliable evidence is available.
    /// </summary>
    public long? AudioLostAtMs;

    /// <summary>
    /// Best-effort continuity classification for the final media:
    /// not_checked, continuous, degraded.
    /// </summary>
    public string? AudioContinuityStatus;

    /// <summary>video media-start anchor availability: available or missing.</summary>
    public string? VideoAnchorStatus;

    /// <summary>Monotonic FFmpeg process-start anchor used for A/V alignment.</summary>
    public long? VideoLaunchAnchorTicks;

    /// <summary>Diagnostic anchor estimated from FFmpeg progress delivery.</summary>
    public long? VideoProgressAnchorTicks;

    /// <summary>Progress-derived anchor minus launch anchor, in milliseconds.</summary>
    public double? VideoProgressAnchorDeltaMs;

    /// <summary>First credible FFmpeg progress frame number.</summary>
    public long? VideoFirstProgressFrame;

    /// <summary>First credible FFmpeg progress out_time_us value.</summary>
    public long? VideoFirstProgressOutTimeUs;

    /// <summary>audio media-start anchor availability: available or missing.</summary>
    public string? AudioAnchorStatus;

    /// <summary>Computed audio pre-roll before video start, in milliseconds.</summary>
    public double? AudioPreRollMs;

    /// <summary>Probe duration of the temporary video before mux, in seconds.</summary>
    public double? TempVideoDurationSeconds;

    /// <summary>Probe duration of the temporary audio before mux, in seconds.</summary>
    public double? TempAudioDurationSeconds;

    /// <summary>Required audio coverage before mux, in seconds.</summary>
    public double? RequiredAudioCoverageSeconds;

    /// <summary>Actual audio duration minus required coverage, in seconds.</summary>
    public double? AudioCoverageDeltaSeconds;

    /// <summary>True when the production audio worker requested timestamp compensation.</summary>
    public bool? AudioTimestampCompensationApplied;

    /// <summary>Bounded summary of detected audio timeline holes, in seconds.</summary>
    public double? AudioTimestampCompensationGapSeconds;

    /// <summary>Audio capture backend identifier: "wasapi-helper" or "dshow".</summary>
    public string? AudioCaptureBackend;

    /// <summary>Audio helper protocol/version summary.</summary>
    public string? AudioHelperProtocol;

    /// <summary>Observed audio sample rate.</summary>
    public int? AudioSampleRate;

    /// <summary>Observed audio channel count.</summary>
    public int? AudioChannels;

    /// <summary>Observed audio bits per sample.</summary>
    public int? AudioBitsPerSample;

    /// <summary>Audio capture method summary (e.g. "WASAPI_SHARED_CAPTURE").</summary>
    public string? AudioCaptureMethod;

    /// <summary>Optional audio-helper capture profile evidence.</summary>
    public string? AudioCaptureStrategy;
    public string? AudioPairEvidence;
    public string? AudioAutoHfpPairStatus;
    public string? AudioAutoHfpPairResultCode;
    public string? AudioAutoHfpPairTransportClassification;
    public string? AudioHelperFailureReason;
    public string? AudioHelperFailureStage;
    public string? AudioHelperFailureHresult;
    public long? AudioRenderPrimeReadyMs;

    /// <summary>Estimated cumulative audio gap reported by the helper, in milliseconds.</summary>
    public long? AudioEstimatedGapMs;

    /// <summary>Maximum wall-minus-media gap observed by the helper at any stall check, in milliseconds.</summary>
    public long? AudioMaxEstimatedGapMs;

    /// <summary>Number of successful runtime recoveries performed by the helper on the same approved endpoint.</summary>
    public long? AudioRecoveryCount;

    /// <summary>Total open/start attempts the helper spent on runtime recovery.</summary>
    public long? AudioRecoveryAttempts;

    /// <summary>Zero-sample bytes the helper wrote to fill objectively measured stream gaps.</summary>
    public long? AudioGapFilledBytes;

    /// <summary>Zero-sample milliseconds the helper wrote to fill objectively measured stream gaps.</summary>
    public long? AudioGapFilledMs;

    /// <summary>WASAPI DataDiscontinuity packets observed by the helper.</summary>
    public long? AudioDiscontinuityCount;

    /// <summary>Isolated loopback QPC outliers recovered using device position.</summary>
    public long? AudioQpcOutlierCount;

    /// <summary>
    /// Stable error code from the WASAPI helper, normalized to the allowlist
    /// of machine-readable codes. Null when no helper was used or no error
    /// was reported.
    /// </summary>
    public string? AudioHelperErrorCode;

    /// <summary>
    /// Structured per-stream probe evidence (index, codec type/name, start time
    /// and duration) parsed from the ffprobe JSON for the muxed output. Used by
    /// post-mux validation to verify actual stream order and final A/V timeline
    /// rather than relying on pre-mux WAV duration estimates.
    /// </summary>
    public ProbeStreamInfo[] ProbeStreams = Array.Empty<ProbeStreamInfo>();
}

/// <summary>
/// Per-stream metadata parsed from structured ffprobe JSON output.
/// </summary>
public sealed class ProbeStreamInfo
{
    /// <summary>Stream index within the container (0-based).</summary>
    public int Index = -1;

    /// <summary>Stream type: "video", "audio", "data", ...</summary>
    public string? CodecType;

    /// <summary>Codec name as reported by ffprobe (e.g. "h264", "aac").</summary>
    public string? CodecName;

    /// <summary>Stream start time in seconds, or null when absent/unparseable.</summary>
    public double? StartTimeSeconds;

    /// <summary>Stream duration in seconds, or null when absent/unparseable.</summary>
    public double? DurationSeconds;
}
