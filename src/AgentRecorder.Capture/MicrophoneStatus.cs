namespace AgentRecorder.Capture;

/// <summary>
/// Read-only CoreAudio capture endpoint status for a single microphone device.
/// All failures are represented as null values so that callers never treat
/// "unknown" as "muted", "default", or "inactive".
/// </summary>
public sealed record MicrophoneStatus(
    bool? IsMuted,
    int? VolumePercent,
    bool? IsDefault = null,
    string? State = null);
