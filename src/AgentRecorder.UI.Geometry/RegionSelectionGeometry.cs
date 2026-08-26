using System.Drawing;

namespace AgentRecorder.UI.Geometry;

/// <summary>
/// Pure selection, display-target and snapping calculations. All coordinates are
/// physical pixels in virtual-desktop space unless a parameter says client space.
/// </summary>
public static class RegionSelectionGeometry
{
    public static Rectangle ToVirtualBounds(Rectangle formBounds, Rectangle clientSelectionBounds)
        => new(formBounds.X + clientSelectionBounds.X, formBounds.Y + clientSelectionBounds.Y,
            clientSelectionBounds.Width, clientSelectionBounds.Height);

    public static (int width, int height) NormalizeEvenBounds(
        int width,
        int height,
        int minSize = 32,
        int maxWidth = int.MaxValue,
        int maxHeight = int.MaxValue)
    {
        int w = Math.Min(width, maxWidth);
        int h = Math.Min(height, maxHeight);
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        if (w < minSize) w = minSize;
        if (h < minSize) h = minSize;
        if (w < 0) w = 0;
        if (h < 0) h = 0;
        return (w, h);
    }

    public static string? FindDisplayId(Rectangle bounds, IEnumerable<GeometryDisplay> displays)
    {
        long centerX = (long)bounds.X + bounds.Width / 2;
        long centerY = (long)bounds.Y + bounds.Height / 2;
        foreach (var display in displays)
        {
            if (Contains(display.Bounds, centerX, centerY))
                return display.Id;
        }
        return null;
    }

    public static Rectangle? ClampInitialSelection(Rectangle formBounds, Rectangle virtualBounds, int minSize = 32)
    {
        var clientBounds = new Rectangle(
            virtualBounds.X - formBounds.X,
            virtualBounds.Y - formBounds.Y,
            virtualBounds.Width,
            virtualBounds.Height);
        var clientRectangle = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        if (!clientBounds.IntersectsWith(clientRectangle) ||
            clientBounds.Width < minSize || clientBounds.Height < minSize)
            return null;

        int left = Math.Max(0, clientBounds.Left);
        int top = Math.Max(0, clientBounds.Top);
        int right = Math.Min(clientRectangle.Width, clientBounds.Right);
        int bottom = Math.Min(clientRectangle.Height, clientBounds.Bottom);
        int width = right - left;
        int height = bottom - top;
        return width < minSize || height < minSize ? null : new Rectangle(left, top, width, height);
    }

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

        if (width < minSize)
        {
            width = minSize;
            right = left + width;
            if (right > clientRectangle.Width)
            {
                right = clientRectangle.Width;
                left = right - width;
                if (left < 0) return null;
            }
        }
        if (height < minSize)
        {
            height = minSize;
            bottom = top + height;
            if (bottom > clientRectangle.Height)
            {
                bottom = clientRectangle.Height;
                top = bottom - height;
                if (top < 0) return null;
            }
        }
        return width < minSize || height < minSize ? null : new Rectangle(left, top, width, height);
    }

    public static Rectangle? ClampSizedSelectionToClientRectangle(
        Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
    {
        if (formBounds.Width < minSize || formBounds.Height < minSize)
            return null;

        var (width, height) = NormalizeEvenBounds(
            clientBounds.Width, clientBounds.Height, minSize, formBounds.Width, formBounds.Height);
        if (width < minSize || height < minSize || width > formBounds.Width || height > formBounds.Height)
            return null;

        int left = clientBounds.Left;
        if (left < 0) left = 0;
        else if (left + width > formBounds.Width) left = formBounds.Width - width;
        int top = clientBounds.Top;
        if (top < 0) top = 0;
        else if (top + height > formBounds.Height) top = formBounds.Height - height;
        return new Rectangle(left, top, width, height);
    }

    public static Rectangle? ApplyPresetSizeAroundCenter(
        Rectangle formBounds, Point centerVirtual, Size targetSize, int minSize = 32)
    {
        int clientCx = centerVirtual.X - formBounds.X;
        int clientCy = centerVirtual.Y - formBounds.Y;
        return ClampSizedSelectionToClientRectangle(formBounds,
            new Rectangle(clientCx - targetSize.Width / 2, clientCy - targetSize.Height / 2,
                targetSize.Width, targetSize.Height), minSize);
    }

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
        int hFromW = (int)(maxW / aspectRatio);
        if (hFromW % 2 != 0) hFromW--;
        int finalW;
        int finalH;
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
        return ClampSelectionToClientRectangle(formBounds,
            new Rectangle(clientCx - finalW / 2, clientCy - finalH / 2, finalW, finalH), minSize);
    }

    public static Point GetVirtualScreenCenter(Rectangle formBounds)
        => new(formBounds.X + formBounds.Width / 2, formBounds.Y + formBounds.Height / 2);

    public static string? FindDisplayIdByOverlap(Rectangle bounds, IEnumerable<GeometryDisplay> displays)
    {
        string? bestId = null;
        decimal bestArea = 0;
        foreach (var display in displays)
        {
            long x1 = Math.Max((long)bounds.X, display.Bounds.X);
            long y1 = Math.Max((long)bounds.Y, display.Bounds.Y);
            long x2 = Math.Min((long)bounds.X + bounds.Width, (long)display.Bounds.X + display.Bounds.Width);
            long y2 = Math.Min((long)bounds.Y + bounds.Height, (long)display.Bounds.Y + display.Bounds.Height);
            decimal area = (decimal)Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (area > bestArea)
            {
                bestArea = area;
                bestId = display.Id;
            }
        }
        return bestId;
    }

    public static Rectangle? ComputeWindowClientBounds(Rectangle formBounds, GeometryWindow window, int minSize = 32)
    {
        if (window.IsMinimized || !window.HasUsableTitle || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
            return null;
        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        var clientBounds = new Rectangle(window.Bounds.X - formBounds.X, window.Bounds.Y - formBounds.Y,
            window.Bounds.Width, window.Bounds.Height);
        if (clientBounds == clientRect || !clientBounds.IntersectsWith(clientRect) ||
            (clientBounds.Width < minSize && clientBounds.Height < minSize))
            return null;
        return clientBounds;
    }

    public static Rectangle? ComputeWindowPickBounds(Rectangle formBounds, GeometryWindow window, int minSize = 32)
    {
        var clientBounds = ComputeWindowClientBounds(formBounds, window, minSize);
        return clientBounds.HasValue ? ClampWindowBoundsToClientRectangle(formBounds, clientBounds.Value, minSize) : null;
    }

    public static List<Rectangle> GenerateSnapTargets(
        Rectangle formBounds,
        IEnumerable<GeometryDisplay> displays,
        IEnumerable<GeometryWindow> windows,
        int minSize = 32)
    {
        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        var targets = new List<Rectangle>();
        foreach (var display in displays)
        {
            var b = display.Bounds;
            var clientBounds = new Rectangle(b.X - formBounds.X, b.Y - formBounds.Y, b.Width, b.Height);
            if (IsValidSnapTarget(clientBounds, clientRect, minSize)) targets.Add(clientBounds);
        }
        foreach (var window in windows)
        {
            var bounds = ComputeWindowPickBounds(formBounds, window, minSize);
            if (bounds.HasValue) targets.Add(bounds.Value);
        }
        return targets;
    }

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
        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        if (right - left < minSize)
        {
            if ((movableEdges & SnapEdgeMask.Right) != 0 && (movableEdges & SnapEdgeMask.Left) == 0) right = left + minSize;
            else if ((movableEdges & SnapEdgeMask.Left) != 0 && (movableEdges & SnapEdgeMask.Right) == 0) left = right - minSize;
            else { int center = (left + right) / 2; left = center - minSize / 2; right = left + minSize; }
        }
        if (bottom - top < minSize)
        {
            if ((movableEdges & SnapEdgeMask.Bottom) != 0 && (movableEdges & SnapEdgeMask.Top) == 0) bottom = top + minSize;
            else if ((movableEdges & SnapEdgeMask.Top) != 0 && (movableEdges & SnapEdgeMask.Bottom) == 0) top = bottom - minSize;
            else { int center = (top + bottom) / 2; top = center - minSize / 2; bottom = top + minSize; }
        }

        var rect = new Rectangle(left, top, right - left, bottom - top);
        Rectangle? clamped;
        if (preserveSize)
            clamped = ClampSizedSelectionToClientRectangle(clientBounds, rect, minSize);
        else
        {
            clamped = ClampSelectionToClientRectangle(clientBounds, rect, minSize);
            if (clamped.HasValue)
            {
                var (w, h) = NormalizeEvenBounds(clamped.Value.Width, clamped.Value.Height,
                    minSize, clientBounds.Width, clientBounds.Height);
                clamped = ClampSelectionToClientRectangle(clientBounds,
                    new Rectangle(clamped.Value.X, clamped.Value.Y, w, h), minSize);
            }
        }
        return clamped ?? rect;
    }

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
        var targetList = enabled && threshold > 0
            ? targets as IReadOnlyList<Rectangle> ?? targets.ToList()
            : null;
        if (targetList is { Count: > 0 } && movableEdges != SnapEdgeMask.None)
        {
            var horizontal = targetList.SelectMany(t => new[] { t.Left, t.Right }).ToList();
            var vertical = targetList.SelectMany(t => new[] { t.Top, t.Bottom }).ToList();
            if (preserveSize)
            {
                var dx = FindBestSnapOffset(new[] { left, right }, horizontal, threshold);
                var dy = FindBestSnapOffset(new[] { top, bottom }, vertical, threshold);
                if (dx.HasValue) { left += dx.Value; right += dx.Value; }
                if (dy.HasValue) { top += dy.Value; bottom += dy.Value; }
            }
            else
            {
                if ((movableEdges & SnapEdgeMask.Left) != 0) left = SnapValue(left, horizontal, threshold);
                if ((movableEdges & SnapEdgeMask.Right) != 0) right = SnapValue(right, horizontal, threshold);
                if ((movableEdges & SnapEdgeMask.Top) != 0) top = SnapValue(top, vertical, threshold);
                if ((movableEdges & SnapEdgeMask.Bottom) != 0) bottom = SnapValue(bottom, vertical, threshold);
            }
        }
        return ClampSelectionAfterDrag(new Rectangle(left, top, right - left, bottom - top),
            clientBounds, movableEdges, preserveSize, minSize);
    }

    private static Rectangle? ClampWindowBoundsToClientRectangle(Rectangle formBounds, Rectangle clientBounds, int minSize)
    {
        var clientRect = new Rectangle(0, 0, formBounds.Width, formBounds.Height);
        if (!clientBounds.IntersectsWith(clientRect)) return null;
        int left = Math.Max(0, clientBounds.Left);
        int top = Math.Max(0, clientBounds.Top);
        int right = Math.Min(clientRect.Width, clientBounds.Right);
        int bottom = Math.Min(clientRect.Height, clientBounds.Bottom);
        int width = right - left;
        int height = bottom - top;
        if (width < minSize)
        {
            width = minSize; right = left + width;
            if (right > clientRect.Width) { right = clientRect.Width; left = right - width; if (left < 0) return null; }
        }
        if (height < minSize)
        {
            height = minSize; bottom = top + height;
            if (bottom > clientRect.Height) { bottom = clientRect.Height; top = bottom - height; if (top < 0) return null; }
        }
        return width < minSize || height < minSize ? null : new Rectangle(left, top, width, height);
    }

    private static bool IsValidSnapTarget(Rectangle target, Rectangle clientRect, int minSize)
        => target.Width > 0 && target.Height > 0 &&
           !(target.Width < minSize && target.Height < minSize) && target.IntersectsWith(clientRect);

    private static bool Contains(Rectangle bounds, long x, long y)
        => x >= bounds.X && x < (long)bounds.X + bounds.Width &&
           y >= bounds.Y && y < (long)bounds.Y + bounds.Height;

    private static int? FindBestSnapOffset(IEnumerable<int> currentEdges, List<int> targetEdges, int threshold)
    {
        int? bestOffset = null;
        int bestDist = int.MaxValue;
        foreach (var edge in currentEdges)
            foreach (var target in targetEdges)
            {
                int offset = target - edge;
                int dist = Math.Abs(offset);
                if (dist <= threshold && dist < bestDist) { bestDist = dist; bestOffset = offset; }
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
            if (dist <= threshold && dist < bestDist) { bestDist = dist; best = target; }
        }
        return best;
    }
}
