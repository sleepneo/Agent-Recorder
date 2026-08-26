using System.Drawing;

namespace AgentRecorder.UI.Geometry;

/// <summary>
/// Pure placement and collision policy for the floating stop control. The App
/// adapts recording models and virtual-screen acquisition to these inputs.
/// </summary>
public static class StopControlGeometry
{
    public const int DefaultButtonWidth = 76;
    public const int DefaultButtonHeight = 28;
    public const int OutsideMargin = 4;
    public const int InsideMargin = 4;
    public const int NestedOffset = 32;

    private static readonly (int dx, int dy)[] CollisionSearchDirections =
    {
        (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)
    };

    public static RecordingStopControlBounds ComputeBounds(
        Rectangle recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen)
        => ComputeBounds(recordingBounds, controlSize, nestedRole, virtualScreen, null,
            StopControlVisibilityMode.ExcludeFromCapture);

    public static RecordingStopControlBounds ComputeBounds(
        Rectangle recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen,
        Rectangle? parentBounds,
        StopControlVisibilityMode mode)
    {
        if (mode == StopControlVisibilityMode.ParentVisible && parentBounds.HasValue &&
            string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase))
        {
            var preferred = ComputeParentVisiblePreferredBounds(recordingBounds, parentBounds.Value, controlSize);
            if (preferred is not null) return preferred;
        }

        var outer = ComputeBaseBounds(recordingBounds, controlSize, virtualScreen);
        return string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase)
            ? ComputeInnerBounds(outer, controlSize, virtualScreen)
            : outer;
    }

    public static RecordingStopControlBounds ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds)
    {
        var occupied = occupiedBounds.ToList();
        if (IsValid(preferred, virtualScreen, occupied, null, null)) return preferred;
        if (TryFindValidCandidate(preferred, controlSize, virtualScreen, occupied, null, null, out var candidate) && candidate is not null)
            return candidate;

        int clampedX = Math.Max(virtualScreen.X, Math.Min(preferred.X, virtualScreen.Right - controlSize.Width));
        int clampedY = Math.Max(virtualScreen.Y, Math.Min(preferred.Y, virtualScreen.Bottom - controlSize.Height));
        return new RecordingStopControlBounds(clampedX, clampedY, controlSize.Width, controlSize.Height);
    }

    public static RecordingStopControlBounds? ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone)
    {
        TryResolveCollision(preferred, controlSize, virtualScreen, occupiedBounds,
            forbiddenZone, allowedZone, out var bounds);
        return bounds;
    }

    public static bool TryResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone,
        out RecordingStopControlBounds? bounds)
    {
        var occupied = occupiedBounds.ToList();
        if (IsValid(preferred, virtualScreen, occupied, forbiddenZone, allowedZone))
        {
            bounds = preferred;
            return true;
        }
        if (TryFindValidCandidate(preferred, controlSize, virtualScreen, occupied,
            forbiddenZone, allowedZone, out bounds) && bounds is not null)
            return true;
        bounds = null;
        return false;
    }

    public static bool Intersects(RecordingStopControlBounds a, RecordingStopControlBounds b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X &&
           a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    public static bool IsInside(RecordingStopControlBounds bounds, Rectangle virtualScreen)
        => bounds.X >= virtualScreen.X && bounds.Y >= virtualScreen.Y &&
           bounds.X + bounds.Width <= virtualScreen.Right &&
           bounds.Y + bounds.Height <= virtualScreen.Bottom;

    private static RecordingStopControlBounds? ComputeParentVisiblePreferredBounds(
        Rectangle inner,
        Rectangle parent,
        Size controlSize)
    {
        var candidates = new[]
        {
            (inner.X + inner.Width + OutsideMargin, inner.Y,
                parent.X + parent.Width - (inner.X + inner.Width) - OutsideMargin, inner.Height),
            (inner.X, inner.Y + inner.Height + OutsideMargin,
                inner.Width, parent.Y + parent.Height - (inner.Y + inner.Height) - OutsideMargin),
            (parent.X, inner.Y, inner.X - parent.X - OutsideMargin, inner.Height),
            (inner.X, parent.Y, inner.Width, inner.Y - parent.Y - OutsideMargin)
        };
        foreach (var (x, y, availableWidth, availableHeight) in candidates)
            if (availableWidth >= controlSize.Width && availableHeight >= controlSize.Height)
                return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
        return null;
    }

    private static bool TryFindValidCandidate(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        List<RecordingStopControlBounds> occupied,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone,
        out RecordingStopControlBounds? candidate)
    {
        int stepX = controlSize.Width + OutsideMargin;
        int stepY = controlSize.Height + OutsideMargin;
        int maxRingsX = Math.Max(1, virtualScreen.Width / stepX + 2);
        int maxRingsY = Math.Max(1, virtualScreen.Height / stepY + 2);
        int maxRings = Math.Max(maxRingsX, maxRingsY);
        for (int ring = 1; ring <= maxRings; ring++)
            foreach (var (dx, dy) in CollisionSearchDirections)
            {
                var ringCandidate = new RecordingStopControlBounds(
                    preferred.X + dx * ring * stepX,
                    preferred.Y + dy * ring * stepY,
                    controlSize.Width, controlSize.Height);
                if (IsValid(ringCandidate, virtualScreen, occupied, forbiddenZone, allowedZone))
                {
                    candidate = ringCandidate;
                    return true;
                }
            }

        var scanArea = allowedZone ?? virtualScreen;
        for (int y = scanArea.Y; y + controlSize.Height <= scanArea.Bottom; y += stepY)
            for (int x = scanArea.X; x + controlSize.Width <= scanArea.Right; x += stepX)
            {
                var fallback = new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
                if (IsValid(fallback, virtualScreen, occupied, forbiddenZone, allowedZone))
                {
                    candidate = fallback;
                    return true;
                }
            }
        candidate = null;
        return false;
    }

    private static bool IsValid(
        RecordingStopControlBounds candidate,
        Rectangle virtualScreen,
        List<RecordingStopControlBounds> occupied,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone)
    {
        if (!IsInside(candidate, virtualScreen)) return false;
        if (forbiddenZone.HasValue && candidate.ToRectangle().IntersectsWith(forbiddenZone.Value)) return false;
        if (allowedZone.HasValue && !allowedZone.Value.Contains(candidate.ToRectangle())) return false;
        return !occupied.Any(o => Intersects(candidate, o));
    }

    private static RecordingStopControlBounds ComputeBaseBounds(
        Rectangle recordingBounds,
        Size controlSize,
        Rectangle virtualScreen)
    {
        int outsideX = recordingBounds.X + recordingBounds.Width + OutsideMargin;
        int outsideY = recordingBounds.Y;
        int insideX = recordingBounds.X + recordingBounds.Width - controlSize.Width - InsideMargin;
        int insideY = recordingBounds.Y + InsideMargin;
        int x;
        int y;
        if (outsideX + controlSize.Width <= virtualScreen.Right &&
            outsideY + controlSize.Height <= virtualScreen.Bottom &&
            outsideX >= virtualScreen.X && outsideY >= virtualScreen.Y)
        {
            x = outsideX; y = outsideY;
        }
        else
        {
            x = insideX; y = insideY;
        }
        x = Math.Max(virtualScreen.X, Math.Min(x, virtualScreen.Right - controlSize.Width));
        y = Math.Max(virtualScreen.Y, Math.Min(y, virtualScreen.Bottom - controlSize.Height));
        return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
    }

    private static RecordingStopControlBounds ComputeInnerBounds(
        RecordingStopControlBounds outer,
        Size controlSize,
        Rectangle virtualScreen)
    {
        var offsets = new[]
        {
            (0, NestedOffset), (0, -NestedOffset),
            (-(controlSize.Width + OutsideMargin), 0),
            (controlSize.Width + OutsideMargin, 0)
        };
        foreach (var (dx, dy) in offsets)
        {
            var candidate = TryPlaceRelative(outer, dx, dy, controlSize, virtualScreen);
            if (candidate is not null && !Intersects(outer, candidate)) return candidate;
        }

        int fallbackX = virtualScreen.X;
        int fallbackY = virtualScreen.Y;
        if (Intersects(outer, new RecordingStopControlBounds(fallbackX, fallbackY, controlSize.Width, controlSize.Height)))
        {
            fallbackX = outer.X + outer.Width;
            fallbackY = outer.Y;
            if (fallbackX + controlSize.Width > virtualScreen.Right)
            {
                fallbackX = outer.X;
                fallbackY = outer.Y + outer.Height;
            }
        }
        fallbackX = Math.Max(virtualScreen.X, Math.Min(fallbackX, virtualScreen.Right - controlSize.Width));
        fallbackY = Math.Max(virtualScreen.Y, Math.Min(fallbackY, virtualScreen.Bottom - controlSize.Height));
        return new RecordingStopControlBounds(fallbackX, fallbackY, controlSize.Width, controlSize.Height);
    }

    private static RecordingStopControlBounds? TryPlaceRelative(
        RecordingStopControlBounds outer,
        int dx,
        int dy,
        Size controlSize,
        Rectangle virtualScreen)
    {
        int x = outer.X + dx;
        int y = outer.Y + dy;
        if (x < virtualScreen.X || y < virtualScreen.Y ||
            x + controlSize.Width > virtualScreen.Right || y + controlSize.Height > virtualScreen.Bottom)
            return null;
        return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
    }
}
