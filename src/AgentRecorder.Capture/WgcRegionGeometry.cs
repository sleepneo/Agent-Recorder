using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Checked geometry contract shared by region selection, WGC probing and the
/// continuous helper argument builder. Coordinates are physical virtual-screen
/// pixels; widths and heights are positive dimensions.
/// </summary>
public readonly record struct WgcRegionRect(int X, int Y, int Width, int Height);

public static class WgcRegionGeometry
{
    public const int MinimumDimension = 32;

    /// <summary>
    /// Returns the crop origin relative to the complete display item. All
    /// arithmetic is widened before comparing or narrowing so malformed input
    /// cannot wrap across the display boundary.
    /// </summary>
    public static bool TryGetCrop(
        WgcRegionRect display,
        WgcRegionRect region,
        out int offsetX,
        out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;

        if (display.Width <= 0 || display.Height <= 0 ||
            region.Width < MinimumDimension || region.Height < MinimumDimension ||
            region.Width % 2 != 0 || region.Height % 2 != 0)
            return false;

        long displayRight = (long)display.X + display.Width;
        long displayBottom = (long)display.Y + display.Height;
        long regionRight = (long)region.X + region.Width;
        long regionBottom = (long)region.Y + region.Height;
        long cropX = (long)region.X - display.X;
        long cropY = (long)region.Y - display.Y;

        if (region.X < display.X || region.Y < display.Y ||
            regionRight > displayRight || regionBottom > displayBottom ||
            cropX < 0 || cropY < 0 ||
            cropX > int.MaxValue || cropY > int.MaxValue)
            return false;

        offsetX = (int)cropX;
        offsetY = (int)cropY;
        return true;
    }
}
