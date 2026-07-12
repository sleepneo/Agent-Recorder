using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Result of resolving the target monitor and DPI for a screen region.
/// </summary>
internal sealed record DisplayDpiInfo(
    string MonitorId,
    Rectangle MonitorBounds,
    int DpiX,
    int DpiY,
    float Scale,
    bool IsFallback,
    string? FallbackReason);

/// <summary>
/// Resolves the monitor/DPI that should drive the layout of a floating stop-control
/// button for the given recording bounds.
/// </summary>
internal interface IDisplayDpiResolver
{
    DisplayDpiInfo Resolve(Rectangle bounds);
}

/// <summary>
/// Production implementation that uses <see cref="SystemQuery.EnumDisplays"/>.
/// </summary>
internal sealed class DisplayDpiResolver : IDisplayDpiResolver
{
    public DisplayDpiInfo Resolve(Rectangle bounds)
    {
        var displays = SystemQuery.EnumDisplayDetails();
        if (displays.Count == 0)
        {
            return Fallback("no_displays_found", new Rectangle(0, 0, 0, 0));
        }

        var candidates = displays.Select(d => new
        {
            d.id,
            Bounds = new Rectangle(d.bounds.x, d.bounds.y, d.bounds.width, d.bounds.height),
            d.dpiX,
            d.dpiY
        }).ToList();

        // Prefer the display that fully contains the region.
        var containing = candidates.FirstOrDefault(c => c.Bounds.Contains(bounds));
        if (containing != null)
        {
            return ToInfo(containing.id, containing.Bounds, containing.dpiX, containing.dpiY, false, null);
        }

        // Otherwise pick the display with the largest intersection area.
        var intersecting = candidates
            .Select(c => new { c.id, c.Bounds, c.dpiX, c.dpiY, Area = AreaOrZero(Rectangle.Intersect(c.Bounds, bounds)) })
            .Where(x => x.Area > 0)
            .OrderByDescending(x => x.Area)
            .FirstOrDefault();

        if (intersecting != null)
        {
            return ToInfo(intersecting.id, intersecting.Bounds, intersecting.dpiX, intersecting.dpiY, false, null);
        }

        // No intersection: use the nearest display by center-to-center distance.
        var nearest = candidates
            .Select(c => new { c.id, c.Bounds, c.dpiX, c.dpiY, Dist = CenterDistanceSquared(c.Bounds, bounds) })
            .OrderBy(x => x.Dist)
            .First();

        return ToInfo(nearest.id, nearest.Bounds, nearest.dpiX, nearest.dpiY, false, null);
    }

    private static DisplayDpiInfo ToInfo(string id, Rectangle bounds, int dpiX, int dpiY, bool fallback, string? reason)
    {
        int effectiveDpi = Math.Max(dpiX, dpiY);
        if (effectiveDpi <= 0)
            effectiveDpi = 96;
        return new DisplayDpiInfo(id, bounds, dpiX, dpiY, effectiveDpi / 96f, fallback, reason);
    }

    private static DisplayDpiInfo Fallback(string reason, Rectangle bounds) =>
        ToInfo("fallback", bounds, 96, 96, true, reason);

    private static int AreaOrZero(Rectangle r) =>
        r.Width > 0 && r.Height > 0 ? r.Width * r.Height : 0;

    private static double CenterDistanceSquared(Rectangle a, Rectangle b)
    {
        var ca = new Point(a.X + a.Width / 2, a.Y + a.Height / 2);
        var cb = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
        long dx = ca.X - cb.X;
        long dy = ca.Y - cb.Y;
        return dx * dx + dy * dy;
    }
}
