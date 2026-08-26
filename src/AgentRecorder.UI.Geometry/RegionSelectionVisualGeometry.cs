using System.Drawing;

namespace AgentRecorder.UI.Geometry;

public enum RegionSelectionLabelPlacement
{
    Hidden,
    AboveSelection,
    BelowSelection,
    InsideTop,
    InsideBottom
}

public readonly record struct RegionSelectionLineSegment(Point Start, Point End)
{
    public bool IsHorizontal => Start.Y == End.Y;
    public bool IsVertical => Start.X == End.X;
}

/// <summary>
/// An axis-aligned line plus the conservative paint envelope produced by a
/// round-capped stroke.  The envelope is represented in the same half-open
/// pixel coordinate space as <see cref="Rectangle"/>.
/// </summary>
public readonly record struct RegionSelectionStrokeSegment(
    RegionSelectionLineSegment Centerline,
    int StrokeWidth,
    Rectangle PaintEnvelope)
{
    public bool IsNonZero => Centerline.Start != Centerline.End;

    public bool IsWithin(Rectangle bounds)
        => bounds.Contains(PaintEnvelope);
}

public readonly record struct RegionSelectionVisualMetrics(
    int Dpi,
    int BoundaryStrokeWidth,
    int AccentStrokeWidth,
    int CornerLength,
    int LabelPadding,
    int LabelGap,
    int EdgeHandleSize)
{
    public float Scale => Dpi / 96f;

    /// <summary>
    /// Font sizes are physical pixels, not points.  9pt at 96 DPI is 12 px,
    /// and the value is scaled exactly once for the target display DPI.
    /// </summary>
    public int SelectionLabelFontPixelSize =>
        RegionSelectionVisualGeometry.PointsToPhysicalPixels(9f, Dpi);

    /// <summary>10pt display labels expressed as physical pixels.</summary>
    public int DisplayBoundaryLabelFontPixelSize =>
        RegionSelectionVisualGeometry.PointsToPhysicalPixels(10f, Dpi);
}

public readonly record struct RegionSelectionLabelLayout(
    bool IsVisible,
    RegionSelectionLabelPlacement Placement,
    Rectangle Bounds,
    Rectangle TextBounds,
    bool IsClipped)
{
    public static RegionSelectionLabelLayout Hidden => new(
        false,
        RegionSelectionLabelPlacement.Hidden,
        Rectangle.Empty,
        Rectangle.Empty,
        false);

    public Point TextOrigin => TextBounds.Location;
}

/// <summary>
/// Pure geometry for the region-selection visual layer. All rectangles and
/// points use the same client physical-pixel coordinate space as the existing
/// selection and hit-test code.
/// </summary>
public static class RegionSelectionVisualGeometry
{
    public static RegionSelectionVisualMetrics ComputeMetrics(int dpi)
    {
        int effectiveDpi = dpi <= 0 ? 96 : dpi;
        return new RegionSelectionVisualMetrics(
            effectiveDpi,
            ScaleLogical(1, effectiveDpi),
            ScaleLogical(2, effectiveDpi),
            ScaleLogical(24, effectiveDpi),
            ScaleLogical(6, effectiveDpi),
            ScaleLogical(8, effectiveDpi),
            ScaleLogical(6, effectiveDpi));
    }

    public static int ScaleLogical(int logicalPixels, int dpi)
    {
        int effectiveDpi = dpi <= 0 ? 96 : dpi;
        return Math.Max(1, (int)Math.Round(
            logicalPixels * effectiveDpi / 96d,
            MidpointRounding.AwayFromZero));
    }

    public static int PointsToPhysicalPixels(float points, int dpi)
    {
        int effectiveDpi = dpi <= 0 ? 96 : dpi;
        return Math.Max(1, (int)Math.Round(
            points * effectiveDpi / 72d,
            MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Returns the path rectangle passed to GDI+'s DrawRectangle.  Each path
    /// edge is inset on all four sides by the boundary stroke radius, so the
    /// actual stroke envelope stays inside the selection.
    /// </summary>
    public static Rectangle ComputeBoundaryBounds(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
            return Rectangle.Empty;

        int inset = StrokeInset(metrics.BoundaryStrokeWidth);
        int left = selection.Left + inset;
        int top = selection.Top + inset;
        int right = selection.Right - inset;
        int bottom = selection.Bottom - inset;
        return right <= left || bottom <= top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static Rectangle ComputeBoundaryBounds(Rectangle selection)
        => ComputeBoundaryBounds(selection, ComputeMetrics(96));

    /// <summary>
    /// Returns the four closed boundary edges as safe stroke segments.  The
    /// form may still use DrawRectangle for the visual, but these segments are
    /// the proof model for its path and paint envelope.
    /// </summary>
    public static IReadOnlyList<RegionSelectionStrokeSegment> ComputeBoundaryStrokeSegments(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
    {
        var path = ComputeBoundaryBounds(selection, metrics);
        if (path.IsEmpty)
            return Array.Empty<RegionSelectionStrokeSegment>();

        var topLeft = new Point(path.Left, path.Top);
        var topRight = new Point(path.Right, path.Top);
        var bottomRight = new Point(path.Right, path.Bottom);
        var bottomLeft = new Point(path.Left, path.Bottom);
        return CreateStrokeSegments(
            new[]
            {
                new RegionSelectionLineSegment(topLeft, topRight),
                new RegionSelectionLineSegment(topRight, bottomRight),
                new RegionSelectionLineSegment(bottomRight, bottomLeft),
                new RegionSelectionLineSegment(bottomLeft, topLeft)
            },
            metrics.BoundaryStrokeWidth);
    }

    public static Rectangle ComputeBoundaryPaintEnvelope(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
        => UnionPaintEnvelopes(ComputeBoundaryStrokeSegments(selection, metrics));

    public static IReadOnlyList<RegionSelectionLineSegment> ComputeCornerLines(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
        => ComputeCornerStrokeSegments(selection, metrics)
            .Select(segment => segment.Centerline)
            .ToArray();

    /// <summary>
    /// Computes the eight L-shaped accent strokes.  Centerlines are inset by
    /// the round-cap radius, and opposing corners leave a deterministic gap so
    /// the complete stroke envelopes cannot cross even for narrow selections.
    /// </summary>
    public static IReadOnlyList<RegionSelectionStrokeSegment> ComputeCornerStrokeSegments(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
            return Array.Empty<RegionSelectionStrokeSegment>();

        int inset = StrokeInset(metrics.AccentStrokeWidth);
        int left = selection.Left + inset;
        int top = selection.Top + inset;
        int right = selection.Right - inset;
        int bottom = selection.Bottom - inset;
        int horizontalSpan = right - left;
        int verticalSpan = bottom - top;
        if (horizontalSpan < 3 || verticalSpan < 3)
            return Array.Empty<RegionSelectionStrokeSegment>();

        // Leave enough room for both round-cap envelopes.  A centerline gap
        // alone is insufficient because the caps extend toward each other.
        int maxLength = Math.Min(
            (horizontalSpan - 2 * inset) / 2,
            (verticalSpan - 2 * inset) / 2);
        if (maxLength < 1)
            return Array.Empty<RegionSelectionStrokeSegment>();
        int length = Math.Clamp(metrics.CornerLength, 1, maxLength);

        return CreateStrokeSegments(
            new[]
        {
            new RegionSelectionLineSegment(new Point(left, top), new Point(left + length, top)),
            new RegionSelectionLineSegment(new Point(left, top), new Point(left, top + length)),
            new RegionSelectionLineSegment(new Point(right - length, top), new Point(right, top)),
            new RegionSelectionLineSegment(new Point(right, top), new Point(right, top + length)),
            new RegionSelectionLineSegment(new Point(left, bottom - length), new Point(left, bottom)),
            new RegionSelectionLineSegment(new Point(left, bottom), new Point(left + length, bottom)),
            new RegionSelectionLineSegment(new Point(right - length, bottom), new Point(right, bottom)),
            new RegionSelectionLineSegment(new Point(right, bottom - length), new Point(right, bottom))
        },
            metrics.AccentStrokeWidth);
    }

    /// <summary>
    /// Returns only the four edge-middle handles. Corner resize remains
    /// expressed by the L-shaped corners while the existing eight-way hit-test
    /// continues to operate independently.
    /// </summary>
    public static IReadOnlyList<Rectangle> ComputeEdgeHandleBounds(
        Rectangle selection,
        RegionSelectionVisualMetrics metrics)
    {
        if (selection.Width <= 0 || selection.Height <= 0)
            return Array.Empty<Rectangle>();

        int size = Math.Max(1, Math.Min(metrics.EdgeHandleSize,
            Math.Min(Math.Max(1, selection.Width), Math.Max(1, selection.Height))));
        int half = size / 2;
        int centerX = selection.Left + selection.Width / 2;
        int centerY = selection.Top + selection.Height / 2;

        return new[]
        {
            new Rectangle(centerX - half, selection.Top - half, size, size),
            new Rectangle(selection.Right - half, centerY - half, size, size),
            new Rectangle(centerX - half, selection.Bottom - half, size, size),
            new Rectangle(selection.Left - half, centerY - half, size, size)
        };
    }

    public static RegionSelectionLabelLayout ComputeLabelLayout(
        Rectangle selection,
        Size textSize,
        Rectangle clientBounds,
        RegionSelectionVisualMetrics metrics,
        IEnumerable<Rectangle>? avoidRects = null)
    {
        if (selection.Width <= 0 || selection.Height <= 0 ||
            textSize.Width <= 0 || textSize.Height <= 0 ||
            clientBounds.Width <= 0 || clientBounds.Height <= 0)
            return RegionSelectionLabelLayout.Hidden;

        int outerWidth = Math.Max(1, textSize.Width + metrics.LabelPadding * 2);
        int outerHeight = Math.Max(1, textSize.Height + metrics.LabelPadding * 2);
        int centerX = selection.Left + selection.Width / 2;
        var candidates = new[]
        {
            (RegionSelectionLabelPlacement.AboveSelection,
                new Rectangle(centerX - outerWidth / 2,
                    selection.Top - metrics.LabelGap - outerHeight,
                    outerWidth, outerHeight)),
            (RegionSelectionLabelPlacement.BelowSelection,
                new Rectangle(centerX - outerWidth / 2,
                    selection.Bottom + metrics.LabelGap,
                    outerWidth, outerHeight)),
            (RegionSelectionLabelPlacement.InsideTop,
                new Rectangle(selection.Left + metrics.LabelPadding,
                    selection.Top + metrics.LabelPadding,
                    outerWidth, outerHeight)),
            (RegionSelectionLabelPlacement.InsideBottom,
                new Rectangle(selection.Left + metrics.LabelPadding,
                    selection.Bottom - metrics.LabelPadding - outerHeight,
                    outerWidth, outerHeight))
        };

        var avoids = (avoidRects ?? Array.Empty<Rectangle>())
            .Where(rect => !rect.IsEmpty)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var clamped = ClampToClient(candidate.Item2, clientBounds);
            if (!IntersectsAny(clamped, avoids))
                return CreateLayout(candidate.Item1, clamped, metrics, clamped != candidate.Item2);
        }

        // A very small or edge-pinned selection can leave no fully clear slot.
        // Choose the least-overlapping deterministic candidate and clip it to
        // the visible client area instead of allowing it to escape the screen.
        var fallback = candidates
            .Select(candidate =>
            {
                var clamped = ClampToClient(candidate.Item2, clientBounds);
                int overlap = TotalOverlapArea(clamped, avoids);
                return (candidate.Item1, Bounds: clamped, Overlap: overlap,
                    Clipped: clamped != candidate.Item2);
            })
            .OrderBy(item => item.Overlap)
            .ThenBy(item => item.Item1)
            .First();

        return CreateLayout(fallback.Item1, fallback.Bounds, metrics, fallback.Clipped || fallback.Overlap > 0);
    }

    public static string FormatSelectionLabelText(
        int width,
        int height,
        string? displayId,
        int maxDisplayIdCharacters = 24)
    {
        string dimensions = $"{Math.Max(0, width)}×{Math.Max(0, height)}";
        if (string.IsNullOrWhiteSpace(displayId) ||
            displayId.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return dimensions;

        string id = displayId.Trim();
        if (maxDisplayIdCharacters < 3)
            return dimensions;
        if (id.Length > maxDisplayIdCharacters)
            id = MiddleEllipsis(id, maxDisplayIdCharacters);
        return $"{dimensions} @ {id}";
    }

    private static string MiddleEllipsis(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        int left = Math.Max(1, (maxLength - 1) / 2);
        int right = Math.Max(1, maxLength - left - 1);
        return value[..left] + "…" + value[^right..];
    }

    private static RegionSelectionLabelLayout CreateLayout(
        RegionSelectionLabelPlacement placement,
        Rectangle bounds,
        RegionSelectionVisualMetrics metrics,
        bool clipped)
    {
        var textBounds = new Rectangle(
            bounds.Left + Math.Min(metrics.LabelPadding, bounds.Width),
            bounds.Top + Math.Min(metrics.LabelPadding, bounds.Height),
            Math.Max(0, bounds.Width - metrics.LabelPadding * 2),
            Math.Max(0, bounds.Height - metrics.LabelPadding * 2));
        return new RegionSelectionLabelLayout(
            true,
            placement,
            bounds,
            textBounds,
            clipped);
    }

    private static Rectangle ClampToClient(Rectangle candidate, Rectangle clientBounds)
    {
        int width = Math.Min(candidate.Width, clientBounds.Width);
        int height = Math.Min(candidate.Height, clientBounds.Height);
        if (width <= 0 || height <= 0)
            return Rectangle.Empty;

        int x = Math.Clamp(candidate.X, clientBounds.Left, clientBounds.Right - width);
        int y = Math.Clamp(candidate.Y, clientBounds.Top, clientBounds.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private static bool IntersectsAny(Rectangle candidate, IReadOnlyList<Rectangle> avoids)
        => avoids.Any(candidate.IntersectsWith);

    private static int TotalOverlapArea(Rectangle candidate, IReadOnlyList<Rectangle> avoids)
    {
        int total = 0;
        foreach (var avoid in avoids)
        {
            var overlap = Rectangle.Intersect(candidate, avoid);
            total = checked(total + Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height));
        }
        return total;
    }

    /// <summary>
    /// A round cap extends by half the stroke width along and perpendicular to
    /// an axis-aligned centerline.  Integer pixel geometry uses a ceiling so
    /// anti-aliased edge coverage cannot escape the selected rectangle.
    /// </summary>
    public static Rectangle ComputeRoundStrokePaintEnvelope(
        RegionSelectionLineSegment centerline,
        int strokeWidth)
    {
        int radius = StrokeInset(strokeWidth);
        int left = Math.Min(centerline.Start.X, centerline.End.X) - radius;
        int top = Math.Min(centerline.Start.Y, centerline.End.Y) - radius;
        int right = Math.Max(centerline.Start.X, centerline.End.X) + radius;
        int bottom = Math.Max(centerline.Start.Y, centerline.End.Y) + radius;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static IReadOnlyList<RegionSelectionStrokeSegment> CreateStrokeSegments(
        IReadOnlyList<RegionSelectionLineSegment> centerlines,
        int strokeWidth)
        => centerlines
            .Select(centerline => new RegionSelectionStrokeSegment(
                centerline,
                strokeWidth,
                ComputeRoundStrokePaintEnvelope(centerline, strokeWidth)))
            .ToArray();

    private static Rectangle UnionPaintEnvelopes(
        IReadOnlyList<RegionSelectionStrokeSegment> segments)
    {
        if (segments.Count == 0)
            return Rectangle.Empty;

        Rectangle union = segments[0].PaintEnvelope;
        for (int i = 1; i < segments.Count; i++)
            union = Rectangle.Union(union, segments[i].PaintEnvelope);
        return union;
    }

    private static int StrokeInset(int strokeWidth)
        => Math.Max(1, (int)Math.Ceiling(Math.Max(1, strokeWidth) / 2d));
}
