using System;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// UI-visible lifecycle phase. This is deliberately separate from the Core
/// recording state enum so the tray boundary does not expose domain objects.
/// </summary>
public enum RecordingUiState
{
    PendingConfirmation,
    Preparing,
    Countdown,
    Recording,
    Stopping,
    Finalizing,
    Idle
}

/// <summary>
/// Immutable capture bounds in the virtual-desktop coordinate space.
/// </summary>
public sealed record RecordingUiBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Immutable, call-time snapshot of the values consumed by local recording UI.
/// It intentionally contains no Core, capture-config, WinForms, or JSON types.
/// </summary>
public sealed record RecordingUiPresentation
{
    public required string RecordingId { get; init; }
    public required RecordingUiState State { get; init; }
    public required string SourceType { get; init; }
    public required RecordingUiBounds CaptureBounds { get; init; }
    public int? DurationSeconds { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public bool IsScreenshotSeries { get; init; }
    public int? SeriesCapturedFrameCount { get; init; }
    public int? SeriesPlannedFrameCount { get; init; }
    public DateTime? SeriesNextCaptureDueAtUtc { get; init; }
    public int? CountdownRemainingSeconds { get; init; }
    public string? NestedRole { get; init; }
    public string? ParentRecordingId { get; init; }
    public string? NestedSessionId { get; init; }
}
