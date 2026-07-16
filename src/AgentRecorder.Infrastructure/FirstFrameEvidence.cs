namespace AgentRecorder.Infrastructure;

/// <summary>
/// Privacy-safe first-frame progress evidence reported by a capture backend.
/// Only numeric, non-sensitive fields are included.
/// </summary>
public sealed class FirstFrameEvidence
{
    /// <summary>Kind of evidence that produced this observation.</summary>
    public string EvidenceKind { get; init; } = "ffmpeg_progress_frame_and_output_bytes";

    /// <summary>Reported frame number when the observation was made.</summary>
    public long FrameNumber { get; init; }

    /// <summary>Reported total output size in bytes.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Reported output time in microseconds, if available.</summary>
    public long? OutTimeUs { get; init; }
}
