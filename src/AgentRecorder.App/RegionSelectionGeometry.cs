using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AgentRecorder.Windows.SystemQuery;

namespace AgentRecorder.App;

/// <summary>
/// Pure functions for region selection geometry calculations.
/// Independent of WinForms, suitable for unit testing without a desktop.
/// </summary>
public static class RegionSelectionGeometry
{
    /// <summary>
    /// Converts client-area selection bounds to virtual screen coordinates.
    /// The selection bounds are relative to the form's client area (0,0 at form top-left),
    /// while virtual screen coordinates are absolute (primary display starts at 0,0;
    /// secondary displays can have negative X/Y).
    /// </summary>
    /// <param name="formBounds">Form's virtual screen Bounds (from SystemInformation.VirtualScreen)</param>
    /// <param name="clientSelectionBounds">Selection rectangle in client-area coordinates</param>
    /// <returns>Selection rectangle in virtual screen coordinates</returns>
    public static Rectangle ToVirtualBounds(Rectangle formBounds, Rectangle clientSelectionBounds)
    {
        int virtualX = formBounds.X + clientSelectionBounds.X;
        int virtualY = formBounds.Y + clientSelectionBounds.Y;
        return new Rectangle(virtualX, virtualY, clientSelectionBounds.Width, clientSelectionBounds.Height);
    }

    /// <summary>
    /// Normalizes width and height to even values for x264/yuv420p compatibility.
    /// Also enforces a minimum size and clamps to form bounds.
    /// </summary>
    /// <param name="width">Original width</param>
    /// <param name="height">Original height</param>
    /// <param name="minSize">Minimum dimension (default 64)</param>
    /// <param name="maxWidth">Maximum width (clamp boundary, default int.MaxValue)</param>
    /// <param name="maxHeight">Maximum height (clamp boundary, default int.MaxValue)</param>
    /// <returns>Normalized (width, height) tuple</returns>
    public static (int width, int height) NormalizeEvenBounds(
        int width, int height, int minSize = 64, int maxWidth = int.MaxValue, int maxHeight = int.MaxValue)
    {
        int w = width;
        int h = height;

        // Clamp to max
        w = Math.Min(w, maxWidth);
        h = Math.Min(h, maxHeight);

        // Normalize to even (x264/yuv420p requirement)
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;

        // Enforce minimum
        if (w < minSize) w = minSize;
        if (h < minSize) h = minSize;

        // Ensure not zero after normalization
        if (w < 0) w = 0;
        if (h < 0) h = 0;

        return (w, h);
    }

    /// <summary>
    /// Finds the display that contains the center of the given bounds.
    /// </summary>
    /// <param name="bounds">Selection bounds in virtual screen coordinates</param>
    /// <param name="displays">List of displays from SystemQuery.EnumDisplays()</param>
    /// <returns>Display ID string (e.g. "display_1") or null if no display found</returns>
    public static string? FindDisplayId(Rectangle bounds, IEnumerable<DisplayInfo> displays)
    {
        int centerX = bounds.X + bounds.Width / 2;
        int centerY = bounds.Y + bounds.Height / 2;

        foreach (var d in displays)
        {
            var b = d.bounds;
            if (centerX >= b.x && centerX < b.x + b.width &&
                centerY >= b.y && centerY < b.y + b.height)
            {
                return d.id;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the display that contains the most area of the given bounds.
    /// Fallback when center is not inside any display.
    /// </summary>
    public static string? FindDisplayIdByOverlap(Rectangle bounds, IEnumerable<DisplayInfo> displays)
    {
        string? bestId = null;
        int bestArea = -1;

        foreach (var d in displays)
        {
            var b = d.bounds;
            // Calculate intersection
            int x1 = Math.Max(bounds.X, b.x);
            int y1 = Math.Max(bounds.Y, b.y);
            int x2 = Math.Min(bounds.X + bounds.Width, b.x + b.width);
            int y2 = Math.Min(bounds.Y + bounds.Height, b.y + b.height);

            int overlapW = Math.Max(0, x2 - x1);
            int overlapH = Math.Max(0, y2 - y1);
            int area = overlapW * overlapH;

            if (area > bestArea)
            {
                bestArea = area;
                bestId = d.id;
            }
        }

        return bestId;
    }
}
