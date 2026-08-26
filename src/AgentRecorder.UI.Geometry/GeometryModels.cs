using System.Drawing;

namespace AgentRecorder.UI.Geometry;

/// <summary>
/// Minimal display input for pure geometry. Bounds are physical pixels in the
/// virtual desktop coordinate space; negative X/Y are valid.
/// </summary>
public readonly record struct GeometryDisplay(string Id, Rectangle Bounds);

/// <summary>
/// Minimal window input for selection picking. Window text and platform identity
/// are intentionally reduced to the one semantic needed by the picker.
/// </summary>
public readonly record struct GeometryWindow(
    string Id,
    Rectangle Bounds,
    bool IsMinimized,
    bool HasUsableTitle);

/// <summary>
/// Display input for DPI selection. Bounds are physical virtual-desktop pixels.
/// </summary>
public readonly record struct DisplayDpiCandidate(
    string Id,
    Rectangle Bounds,
    int DpiX,
    int DpiY);

/// <summary>
/// DPI selection result returned by the pure resolver.
/// </summary>
public readonly record struct DisplayDpiResolution(
    string MonitorId,
    Rectangle MonitorBounds,
    int DpiX,
    int DpiY,
    float Scale,
    bool IsFallback,
    string? FallbackReason);

/// <summary>
/// Immutable virtual-desktop bounds for a floating stop control.
/// </summary>
public sealed record RecordingStopControlBounds(
    int X,
    int Y,
    int Width,
    int Height)
{
    public Rectangle ToRectangle() => new(X, Y, Width, Height);
}

[Flags]
public enum SnapEdgeMask
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom
}

/// <summary>
/// Geometry-only visibility semantics used by stop-control placement.
/// </summary>
public enum StopControlVisibilityMode
{
    ExcludeFromCapture,
    ParentVisible
}
