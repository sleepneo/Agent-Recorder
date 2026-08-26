using System.Drawing;
using AgentRecorder.UI.Geometry;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// App-facing result retained for existing WinForms callers. The selection
/// policy lives in <see cref="DisplayDpiGeometry"/>.
/// </summary>
internal sealed record DisplayDpiInfo(
    string MonitorId,
    Rectangle MonitorBounds,
    int DpiX,
    int DpiY,
    float Scale,
    bool IsFallback,
    string? FallbackReason);

internal interface IDisplayDpiResolver
{
    DisplayDpiInfo Resolve(Rectangle bounds);
}

/// <summary>
/// Platform adapter: reads Windows display details, maps them to the shared
/// immutable DTO, and maps the pure result back to the App contract.
/// </summary>
internal sealed class DisplayDpiResolver : IDisplayDpiResolver
{
    public DisplayDpiInfo Resolve(Rectangle bounds)
    {
        var candidates = SystemQuery.EnumDisplayDetails()
            .Select(display => new DisplayDpiCandidate(
                display.id,
                new Rectangle(display.bounds.x, display.bounds.y, display.bounds.width, display.bounds.height),
                display.dpiX,
                display.dpiY))
            .ToList();

        var result = DisplayDpiGeometry.Resolve(bounds, candidates);
        return new DisplayDpiInfo(
            result.MonitorId,
            result.MonitorBounds,
            result.DpiX,
            result.DpiY,
            result.Scale,
            result.IsFallback,
            result.FallbackReason);
    }
}
