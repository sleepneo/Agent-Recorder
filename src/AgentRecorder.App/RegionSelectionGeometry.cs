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
    /// Also enforces a minimum size and clamps to max dimensions.
    /// </summary>
    /// <param name="width">Original width</param>
    /// <param name="height">Original height</param>
    /// <param name="minSize">Minimum dimension (default 32 to match region selection UI)</param>
    /// <param name="maxWidth">Maximum width (clamp boundary, default int.MaxValue)</param>
    /// <param name="maxHeight">Maximum height (clamp boundary, default int.MaxValue)</param>
    /// <returns>Normalized (width, height) tuple</returns>
    public static (int width, int height) NormalizeEvenBounds(
        int width, int height, int minSize = 32, int maxWidth = int.MaxValue, int maxHeight = int.MaxValue)
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
    /// Converts virtual screen bounds to form client coordinates and clamps to the form's client area.
    /// Returns null if the initial bounds do not intersect the client area or are smaller than minSize.
    /// </summary>
    public static Rectangle? ClampInitialSelection(Rectangle formBounds, Rectangle virtualBounds, int minSize = 32)
    {
        int clientX = virtualBounds.X - formBounds.X;
        int clientY = virtualBounds.Y - formBounds.Y;

        var clientBounds = new Rectangle(clientX, clientY, virtualBounds.Width, virtualBounds.Height);
        var clientRectangle = new Rectangle(0, 0, formBounds.Width, formBounds.Height);

        if (!clientBounds.IntersectsWith(clientRectangle))
            return null;
        if (clientBounds.Width < minSize || clientBounds.Height < minSize)
            return null;

        int left = Math.Max(0, clientBounds.Left);
        int top = Math.Max(0, clientBounds.Top);
        int right = Math.Min(clientRectangle.Width, clientBounds.Right);
        int bottom = Math.Min(clientRectangle.Height, clientBounds.Bottom);

        int width = right - left;
        int height = bottom - top;

        if (width < minSize || height < minSize)
            return null;

        return new Rectangle(left, top, width, height);
    }

    /// <summary>
    /// Clamps client-area selection bounds to the form's client area while enforcing
    /// a minimum size. Unlike ClampInitialSelection, this operates on bounds that are
    /// already in client coordinates and will attempt to expand small rectangles to
    /// meet the minimum size if they intersect the client area.
    /// </summary>
    public static Rectangle? ClampSelectionToClientRectangle(Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
    {
        var clientRectangle = new Rectangle(0, 0, formBounds.Width, formBounds.Height);

        if (!clientBounds.IntersectsWith(clientRectangle))
            return null;

        int left = Math.Max(0, clientBounds.Left);
        int top = Math.Max(0, clientBounds.Top);
        int right = Math.Min(clientRectangle.Width, clientBounds.Right);
        int bottom = Math.Min(clientRectangle.Height, clientBounds.Bottom);

        int width = right - left;
        int height = bottom - top;

        // Try to expand width to meet minimum size, preferring rightward expansion
        // then leftward if we hit the right edge.
        if (width < minSize)
        {
            width = minSize;
            right = left + width;
            if (right > clientRectangle.Width)
            {
                right = clientRectangle.Width;
                left = right - width;
                if (left < 0)
                    return null;
            }
        }

        // Same for height.
        if (height < minSize)
        {
            height = minSize;
            bottom = top + height;
            if (bottom > clientRectangle.Height)
            {
                bottom = clientRectangle.Height;
                top = bottom - height;
                if (top < 0)
                    return null;
            }
        }

        if (width < minSize || height < minSize)
            return null;

        return new Rectangle(left, top, width, height);
    }

    /// <summary>
    /// Clamps a client-area selection to the form's client area while preferring to
    /// preserve the requested width and height. The rectangle is first translated into
    /// the client area; only if the requested size exceeds the available area is it
    /// shrunk. Width and height are normalized to even values and enforced to minSize.
    /// Returns null if the form itself is smaller than minSize.
    /// </summary>
    public static Rectangle? ClampSizedSelectionToClientRectangle(
        Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
    {
        if (formBounds.Width < minSize || formBounds.Height < minSize)
            return null;

        var (width, height) = NormalizeEvenBounds(
            clientBounds.Width,
            clientBounds.Height,
            minSize,
            maxWidth: formBounds.Width,
            maxHeight: formBounds.Height);

        if (width < minSize || height < minSize)
            return null;
        if (width > formBounds.Width || height > formBounds.Height)
            return null;

        int left = clientBounds.Left;
        if (left < 0)
            left = 0;
        else if (left + width > formBounds.Width)
            left = formBounds.Width - width;

        int top = clientBounds.Top;
        if (top < 0)
            top = 0;
        else if (top + height > formBounds.Height)
            top = formBounds.Height - height;

        return new Rectangle(left, top, width, height);
    }

    /// <summary>
    /// Applies a preset target size centered around a virtual screen point.
    /// The result is clamped to the form's client area and normalized to even dimensions.
    /// Preserves the preset size whenever it fits on screen by translating the rectangle.
    /// </summary>
    public static Rectangle? ApplyPresetSizeAroundCenter(
        Rectangle formBounds, Point centerVirtual, Size targetSize, int minSize = 32)
    {
        int clientCx = centerVirtual.X - formBounds.X;
        int clientCy = centerVirtual.Y - formBounds.Y;

        var clientBounds = new Rectangle(
            clientCx - targetSize.Width / 2,
            clientCy - targetSize.Height / 2,
            targetSize.Width,
            targetSize.Height);

        return ClampSizedSelectionToClientRectangle(formBounds, clientBounds, minSize);
    }

    /// <summary>
    /// Fits the largest possible rectangle with the given aspect ratio, centered at
    /// the virtual screen point, and clamps it to the form's client area.
    /// Width and height are normalized to even values and enforced to minSize.
    /// </summary>
    public static Rectangle? FitAspectRatio(
        Rectangle formBounds, Point centerVirtual, double aspectRatio, int minSize = 32)
    {
        int clientCx = centerVirtual.X - formBounds.X;
        int clientCy = centerVirtual.Y - formBounds.Y;

        int maxHalfW = Math.Min(clientCx, formBounds.Width - clientCx);
        int maxHalfH = Math.Min(clientCy, formBounds.Height - clientCy);

        if (maxHalfW < minSize / 2 || maxHalfH < minSize / 2)
            return null;

        int maxW = maxHalfW * 2;
        int maxH = maxHalfH * 2;

        // Determine whether width or height is the limiting dimension for the aspect ratio.
        int hFromW = (int)(maxW / aspectRatio);
        if (hFromW % 2 != 0) hFromW--;

        int finalW, finalH;
        if (hFromW <= maxH)
        {
            finalW = maxW;
            finalH = hFromW;
        }
        else
        {
            finalH = maxH;
            finalW = (int)(maxH * aspectRatio);
            if (finalW % 2 != 0) finalW--;
        }

        if (finalW < minSize) finalW = minSize;
        if (finalH < minSize) finalH = minSize;

        var clientBounds = new Rectangle(
            clientCx - finalW / 2,
            clientCy - finalH / 2,
            finalW,
            finalH);

        return ClampSelectionToClientRectangle(formBounds, clientBounds, minSize);
    }

    /// <summary>
    /// Returns the center point of the virtual screen represented by the form bounds.
    /// </summary>
    public static Point GetVirtualScreenCenter(Rectangle formBounds)
    {
        return new Point(formBounds.X + formBounds.Width / 2, formBounds.Y + formBounds.Height / 2);
    }

    /// <summary>
    /// Finds the display that contains the most area of the given bounds.
    /// Fallback when center is not inside any display.
    /// </summary>
    public static string? FindDisplayIdByOverlap(Rectangle bounds, IEnumerable<DisplayInfo> displays)
    {
        string? bestId = null;
        int bestArea = 0;

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
