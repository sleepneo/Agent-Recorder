using System;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Immutable virtual-screen bounds used by the local confirmation preview.
/// This model intentionally has no dependency on the capture, UI, or Windows
/// projects so the confirmation contract can cross host boundaries safely.
/// </summary>
public sealed record ConfirmationCaptureBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Typed screenshot-series metadata shown during confirmation.
/// </summary>
public sealed record RecordingSeriesPresentation
{
    public int IntervalMs { get; init; }
    public int? MaxCount { get; init; }
    public int? MaxDurationSeconds { get; init; }
    public int PlannedFrameCount { get; init; }
    public string OutputKind { get; init; } = "png_sequence_directory";
}

/// <summary>
/// Typed request summary produced by the configuration layer without using an
/// anonymous object or reflection-based field lookup.
/// </summary>
public sealed record RecordingRequestSummary
{
    public string Mode { get; init; } = "video";
    public string Source { get; init; } = "";
    public string Audio { get; init; } = "No audio";
    public string AudioSourceKind { get; init; } = "none";
    public bool AudioSystemEnabled { get; init; }
    public string? AudioSystemDefaultOutput { get; init; }
    public string? AudioSystemOutputName { get; init; }
    public bool? AudioSystemOutputIsDefault { get; init; }
    public string AudioSystemOutputSelection { get; init; } = "selected";
    public string? AudioDevice { get; init; }
    public int? AudioVolumePercent { get; init; }
    public string Duration { get; init; } = "Manual stop";
    public int CountdownSeconds { get; init; }
    public string Output { get; init; } = "";
    public RecordingSeriesPresentation? Series { get; init; }
    public string NestedRole { get; init; } = "none";
}

/// <summary>
/// Complete immutable presentation payload for local confirmation. It is the
/// only value passed through <see cref="ITrayContext.RequestConfirmation"/>.
/// Created/expires timestamps are authoritative and are not recomputed by UI
/// queue code.
/// </summary>
public sealed record RecordingConfirmationPresentation
{
    public RecordingRequestSummary Summary { get; init; } = new();
    public string RecordingId { get; init; } = "";
    public string ConfirmationId { get; init; } = "";
    public int TimeoutSeconds { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public string SourceType { get; init; } = "";
    public string? SourceTitle { get; init; }
    public string? SourceApplication { get; init; }
    public string? WindowId { get; init; }
    public string? TraceId { get; init; }
    public string CoordinateSpace { get; init; } = "virtual_screen";
    public string CaptureSemantics { get; init; } = "";
    public string PlannedBackend { get; init; } = "";
    public string PreviewSemantics { get; init; } = "";
    public string SelectionReasonCode { get; init; } = "";
    public string SelectionAvailabilitySource { get; init; } = "";
    public bool SelectionFallback { get; init; }
    public string TargetDisplayId { get; init; } = "";
    public ConfirmationCaptureBounds? TargetDisplayBounds { get; init; }
    public ConfirmationCaptureBounds? CaptureBounds { get; init; }
    public string OutputKind { get; init; } = "mp4_file";
}
