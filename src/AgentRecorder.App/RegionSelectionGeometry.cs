using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static AgentRecorder.Windows.SystemQuery;

namespace AgentRecorder.App;

/// <summary>
/// Edge mask used by snapping logic to indicate which edges of a rectangle
/// are allowed to move toward a snap target.
/// </summary>
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

    /// <summary>
    /// Computes the client-area bounds of a window for picking and snapping.
    /// Returns null if the window should be ignored (minimized, empty title,
    /// non-positive dimensions, no intersection with the client area, smaller
    /// than minSize in both dimensions, or exactly matching the full form).
    /// </summary>
    public static Rectangle? ComputeWindowClientBounds(Rectangle formBounds, WindowInfo window, int minSize = 32)
    {
        if (window.is_minimized)
            return null;
        if (string.IsNullOrWhiteSpace(window.title))
            return null;

        var b = window.bounds;
        if (b.width <= 0 || b.height <= 0)
            return null;

        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        var clientBounds = new Rectangle(
            b.x - formBounds.X,
            b.y - formBounds.Y,
            b.width,
            b.height);

        if (clientBounds == clientRect)
            return null;
        if (!clientBounds.IntersectsWith(clientRect))
            return null;
        if (clientBounds.Width < minSize && clientBounds.Height < minSize)
            return null;

        return clientBounds;
    }

    /// <summary>
    /// Computes the legal client-area pick bounds for a window, clamping the
    /// visible portion into the form's client area while preserving size whenever
    /// possible. Returns null when the window cannot be made into a legal selection.
    /// </summary>
    public static Rectangle? ComputeWindowPickBounds(Rectangle formBounds, WindowInfo window, int minSize = 32)
    {
        var clientBounds = ComputeWindowClientBounds(formBounds, window, minSize);
        if (!clientBounds.HasValue)
            return null;

        return ClampWindowBoundsToClientRectangle(formBounds, clientBounds.Value, minSize);
    }

    private static Rectangle? ClampWindowBoundsToClientRectangle(Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
    {
        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        if (!clientBounds.IntersectsWith(clientRect))
            return null;

        int left = Math.Max(0, clientBounds.Left);
        int top = Math.Max(0, clientBounds.Top);
        int right = Math.Min(clientRect.Width, clientBounds.Right);
        int bottom = Math.Min(clientRect.Height, clientBounds.Bottom);

        int width = right - left;
        int height = bottom - top;

        if (width < minSize)
        {
            width = minSize;
            right = left + width;
            if (right > clientRect.Width)
            {
                right = clientRect.Width;
                left = right - width;
                if (left < 0)
                    return null;
            }
        }

        if (height < minSize)
        {
            height = minSize;
            bottom = top + height;
            if (bottom > clientRect.Height)
            {
                bottom = clientRect.Height;
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
    /// Generates a list of rectangles in form client coordinates that can be used
    /// as snap targets. Includes display boundaries and visible window boundaries,
    /// filtering out empty/minimized/system windows, targets that do not intersect
    /// the client area, and the full-screen overlay window that matches the form.
    /// Window targets are clamped to the client area so they match the pick candidates.
    /// </summary>
    public static List<Rectangle> GenerateSnapTargets(
        Rectangle formBounds,
        IEnumerable<DisplayInfo> displays,
        IEnumerable<WindowInfo> windows,
        int minSize = 32)
    {
        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        var targets = new List<Rectangle>();

        foreach (var display in displays)
        {
            var b = display.bounds;
            var clientBounds = new Rectangle(
                b.x - formBounds.X,
                b.y - formBounds.Y,
                b.width,
                b.height);
            if (IsValidSnapTarget(clientBounds, clientRect, minSize))
                targets.Add(clientBounds);
        }

        foreach (var window in windows)
        {
            var bounds = ComputeWindowPickBounds(formBounds, window, minSize);
            if (bounds.HasValue)
                targets.Add(bounds.Value);
        }

        return targets;
    }

    private static bool IsValidSnapTarget(Rectangle target, Rectangle clientRect, int minSize)
    {
        if (target.Width <= 0 || target.Height <= 0)
            return false;
        if (target.Width < minSize && target.Height < minSize)
            return false;
        if (!target.IntersectsWith(clientRect))
            return false;
        return true;
    }

    /// <summary>
    /// Clamps a selection rectangle to the client area and enforces minimum size.
    /// Respects <paramref name="movableEdges"/> when expanding small rectangles and
    /// preserves size when <paramref name="preserveSize"/> is true. The result is
    /// normalized to even dimensions.
    /// </summary>
    public static Rectangle ClampSelectionAfterDrag(
        Rectangle current,
        Rectangle clientBounds,
        SnapEdgeMask movableEdges,
        bool preserveSize = false,
        int minSize = 32)
    {
        int left = current.Left;
        int right = current.Right;
        int top = current.Top;
        int bottom = current.Bottom;

        // Ensure correct ordering (handles pathological input but should not normally occur).
        if (left > right)
            (left, right) = (right, left);
        if (top > bottom)
            (top, bottom) = (bottom, top);

        // Enforce minimum size while preserving fixed edges where possible.
        if (right - left < minSize)
        {
            if ((movableEdges & SnapEdgeMask.Right) != 0 && (movableEdges & SnapEdgeMask.Left) == 0)
                right = left + minSize;
            else if ((movableEdges & SnapEdgeMask.Left) != 0 && (movableEdges & SnapEdgeMask.Right) == 0)
                left = right - minSize;
            else
            {
                int center = (left + right) / 2;
                left = center - minSize / 2;
                right = left + minSize;
            }
        }

        if (bottom - top < minSize)
        {
            if ((movableEdges & SnapEdgeMask.Bottom) != 0 && (movableEdges & SnapEdgeMask.Top) == 0)
                bottom = top + minSize;
            else if ((movableEdges & SnapEdgeMask.Top) != 0 && (movableEdges & SnapEdgeMask.Bottom) == 0)
                top = bottom - minSize;
            else
            {
                int center = (top + bottom) / 2;
                top = center - minSize / 2;
                bottom = top + minSize;
            }
        }

        var rect = new Rectangle(left, top, right - left, bottom - top);

        Rectangle? clamped;
        if (preserveSize)
        {
            clamped = ClampSizedSelectionToClientRectangle(clientBounds, rect, minSize);
        }
        else
        {
            clamped = ClampSelectionToClientRectangle(clientBounds, rect, minSize);
            if (clamped.HasValue)
            {
                var (w, h) = NormalizeEvenBounds(
                    clamped.Value.Width,
                    clamped.Value.Height,
                    minSize,
                    clientBounds.Width,
                    clientBounds.Height);
                var normalized = new Rectangle(clamped.Value.X, clamped.Value.Y, w, h);
                clamped = ClampSelectionToClientRectangle(clientBounds, normalized, minSize);
            }
        }

        return clamped ?? rect;
    }

    /// <summary>
    /// Snaps the edges of <paramref name="current"/> to nearby target edges.
    /// <paramref name="movableEdges"/> controls which edges may move;
    /// <paramref name="preserveSize"/> treats the rectangle as a rigid body
    /// (used for moving) rather than resizing individual edges.
    /// The result is always clamped to <paramref name="clientBounds"/>, kept
    /// at or above <paramref name="minSize"/>, and normalized to even dimensions,
    /// even when snapping is disabled or no targets are available.
    /// </summary>
    public static Rectangle ApplySnapping(
        Rectangle current,
        Rectangle clientBounds,
        IEnumerable<Rectangle> targets,
        int threshold,
        SnapEdgeMask movableEdges,
        bool preserveSize = false,
        bool enabled = true,
        int minSize = 32)
    {
        int left = current.Left;
        int right = current.Right;
        int top = current.Top;
        int bottom = current.Bottom;

        var targetList = (enabled && threshold > 0)
            ? targets as IReadOnlyList<Rectangle> ?? targets.ToList()
            : null;

        if (targetList != null && targetList.Count > 0 && movableEdges != SnapEdgeMask.None)
        {
            var hTargets = targetList.SelectMany(t => new[] { t.Left, t.Right }).ToList();
            var vTargets = targetList.SelectMany(t => new[] { t.Top, t.Bottom }).ToList();

            if (preserveSize)
            {
                var dx = FindBestSnapOffset(new[] { left, right }, hTargets, threshold);
                var dy = FindBestSnapOffset(new[] { top, bottom }, vTargets, threshold);

                if (dx.HasValue)
                {
                    left += dx.Value;
                    right += dx.Value;
                }
                if (dy.HasValue)
                {
                    top += dy.Value;
                    bottom += dy.Value;
                }
            }
            else
            {
                if ((movableEdges & SnapEdgeMask.Left) != 0)
                    left = SnapValue(left, hTargets, threshold);
                if ((movableEdges & SnapEdgeMask.Right) != 0)
                    right = SnapValue(right, hTargets, threshold);
                if ((movableEdges & SnapEdgeMask.Top) != 0)
                    top = SnapValue(top, vTargets, threshold);
                if ((movableEdges & SnapEdgeMask.Bottom) != 0)
                    bottom = SnapValue(bottom, vTargets, threshold);
            }
        }

        var snapped = new Rectangle(left, top, right - left, bottom - top);
        return ClampSelectionAfterDrag(snapped, clientBounds, movableEdges, preserveSize, minSize);
    }

    private static int? FindBestSnapOffset(IEnumerable<int> currentEdges, List<int> targetEdges, int threshold)
    {
        int? bestOffset = null;
        int bestDist = int.MaxValue;

        foreach (var edge in currentEdges)
        {
            foreach (var target in targetEdges)
            {
                int offset = target - edge;
                int dist = Math.Abs(offset);
                if (dist <= threshold && dist < bestDist)
                {
                    bestDist = dist;
                    bestOffset = offset;
                }
            }
        }

        return bestOffset;
    }

    private static int SnapValue(int value, List<int> targetEdges, int threshold)
    {
        int best = value;
        int bestDist = int.MaxValue;

        foreach (var target in targetEdges)
        {
            int dist = Math.Abs(target - value);
            if (dist <= threshold && dist < bestDist)
            {
                bestDist = dist;
                best = target;
            }
        }

        return best;
    }
}
