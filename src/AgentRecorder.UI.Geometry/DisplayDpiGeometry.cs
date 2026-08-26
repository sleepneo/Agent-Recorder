using System.Drawing;

namespace AgentRecorder.UI.Geometry;

/// <summary>
/// Pure display selection and DPI scaling policy. Display enumeration remains an
/// App/platform responsibility and is supplied as immutable candidates.
/// </summary>
public static class DisplayDpiGeometry
{
    public static DisplayDpiResolution Resolve(
        Rectangle targetBounds,
        IEnumerable<DisplayDpiCandidate> displays)
    {
        var candidates = displays.ToList();
        if (candidates.Count == 0)
            return Fallback("no_displays_found", Rectangle.Empty);

        var containing = candidates
            .Where(candidate => Contains(candidate.Bounds, targetBounds))
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(containing.Id))
            return ToResolution(containing, false, null);

        var intersecting = candidates
            .Select(candidate => new { Candidate = candidate, Area = IntersectionArea(candidate.Bounds, targetBounds) })
            .Where(item => item.Area > 0)
            .OrderByDescending(item => item.Area)
            .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
            .Select(item => item.Candidate)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(intersecting.Id))
            return ToResolution(intersecting, false, null);

        var nearest = candidates
            .Select(candidate => new { Candidate = candidate, Distance = CenterDistanceSquared(candidate.Bounds, targetBounds) })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
            .Select(item => item.Candidate)
            .First();
        return ToResolution(nearest, false, null);
    }

    private static DisplayDpiResolution ToResolution(
        DisplayDpiCandidate candidate,
        bool fallback,
        string? reason)
    {
        int dpiX = candidate.DpiX > 0 ? candidate.DpiX : 96;
        int dpiY = candidate.DpiY > 0 ? candidate.DpiY : 96;
        int effectiveDpi = Math.Max(dpiX, dpiY);
        return new DisplayDpiResolution(candidate.Id, candidate.Bounds, dpiX, dpiY,
            effectiveDpi / 96f, fallback, reason);
    }

    private static DisplayDpiResolution Fallback(string reason, Rectangle bounds)
        => new("fallback", bounds, 96, 96, 1.0f, true, reason);

    private static bool Contains(Rectangle container, Rectangle target)
        => target.X >= container.X && target.Y >= container.Y &&
           (long)target.X + target.Width <= (long)container.X + container.Width &&
           (long)target.Y + target.Height <= (long)container.Y + container.Height;

    private static decimal IntersectionArea(Rectangle a, Rectangle b)
    {
        long left = Math.Max((long)a.X, b.X);
        long top = Math.Max((long)a.Y, b.Y);
        long right = Math.Min((long)a.X + a.Width, (long)b.X + b.Width);
        long bottom = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height);
        return (decimal)Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    private static decimal CenterDistanceSquared(Rectangle a, Rectangle b)
    {
        long ax = (long)a.X + a.Width / 2;
        long ay = (long)a.Y + a.Height / 2;
        long bx = (long)b.X + b.Width / 2;
        long by = (long)b.Y + b.Height / 2;
        long dx = ax - bx;
        long dy = ay - by;
        return (decimal)dx * dx + (decimal)dy * dy;
    }
}
