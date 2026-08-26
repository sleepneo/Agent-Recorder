using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.UI.Geometry;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RegionSelectionVisualGeometryTests
{
    [Theory]
    [InlineData(100, 80, 1920, 1080)]
    [InlineData(0, 0, 32, 32)]
    [InlineData(40, 50, 32, 240)]
    [InlineData(0, 0, 300, 32)]
    [InlineData(-640, 120, 800, 600)]
    public void CornerLines_AreEightOrthogonalSegmentsInsideSelection(
        int x,
        int y,
        int width,
        int height)
    {
        var selection = new Rectangle(x, y, width, height);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(96);
        var lines = RegionSelectionVisualGeometry.ComputeCornerLines(selection, metrics);

        Assert.Equal(8, lines.Count);
        Assert.Equal(4, lines.Count(line => line.IsHorizontal));
        Assert.Equal(4, lines.Count(line => line.IsVertical));
        foreach (var line in lines)
        {
            Assert.True(selection.Left <= line.Start.X && line.Start.X < selection.Right);
            Assert.True(selection.Top <= line.Start.Y && line.Start.Y < selection.Bottom);
            Assert.True(selection.Left <= line.End.X && line.End.X < selection.Right);
            Assert.True(selection.Top <= line.End.Y && line.End.Y < selection.Bottom);
            Assert.True(line.Start != line.End);
        }

        int maxLength = Math.Min((selection.Width - 1) / 2, (selection.Height - 1) / 2);
        Assert.All(lines, line =>
        {
            int length = Math.Abs(line.End.X - line.Start.X) + Math.Abs(line.End.Y - line.Start.Y);
            Assert.InRange(length, 1, Math.Max(1, maxLength));
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void CornerStrokePaintEnvelopes_AreInsideSelectionAtEverySupportedDpi(int dpi)
    {
        var selection = new Rectangle(0, 0, 320, 240);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(dpi);
        var segments = RegionSelectionVisualGeometry.ComputeCornerStrokeSegments(selection, metrics);

        Assert.Equal(8, segments.Count);
        Assert.All(segments, segment =>
        {
            Assert.True(segment.IsNonZero);
            Assert.Equal(metrics.AccentStrokeWidth, segment.StrokeWidth);
            Assert.True(selection.Contains(segment.PaintEnvelope),
                $"{segment.Centerline} has paint envelope {segment.PaintEnvelope}");
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void SelectionStrokes_AreInsideClientWhenSelectionTouchesEveryClientEdge(int dpi)
    {
        var client = new Rectangle(0, 0, 800, 600);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(dpi);
        var selections = new[]
        {
            new Rectangle(client.Left, 120, 320, 240),
            new Rectangle(180, client.Top, 320, 240),
            new Rectangle(client.Right - 320, 120, 320, 240),
            new Rectangle(180, client.Bottom - 240, 320, 240),
            new Rectangle(client.Left, client.Top, 320, 240),
            new Rectangle(client.Right - 320, client.Bottom - 240, 320, 240)
        };

        foreach (var selection in selections)
        {
            var corners = RegionSelectionVisualGeometry.ComputeCornerStrokeSegments(selection, metrics);
            var boundary = RegionSelectionVisualGeometry.ComputeBoundaryStrokeSegments(selection, metrics);

            Assert.Equal(8, corners.Count);
            Assert.Equal(4, boundary.Count);
            Assert.All(corners, segment => Assert.True(client.Contains(segment.PaintEnvelope)));
            Assert.All(boundary, segment => Assert.True(client.Contains(segment.PaintEnvelope)));
            Assert.True(client.Contains(RegionSelectionVisualGeometry.ComputeBoundaryPaintEnvelope(selection, metrics)));
        }
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(32, 240)]
    [InlineData(240, 32)]
    public void CornerStrokePaintEnvelopes_RemainNonZeroAndNonIntersectingForSmallSelections(
        int width,
        int height)
    {
        var selection = new Rectangle(40, 50, width, height);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(192);
        var segments = RegionSelectionVisualGeometry.ComputeCornerStrokeSegments(selection, metrics);

        Assert.Equal(8, segments.Count);
        Assert.All(segments, segment =>
        {
            Assert.True(segment.IsNonZero);
            Assert.True(selection.Contains(segment.PaintEnvelope));
        });

        // The two segments belonging to one corner intentionally meet.  All
        // other L arms must have a gap after their round cap envelopes.
        for (int i = 0; i < segments.Count; i++)
        for (int j = i + 1; j < segments.Count; j++)
        {
            if (i / 2 == j / 2)
                continue;
            Assert.False(segments[i].PaintEnvelope.IntersectsWith(segments[j].PaintEnvelope),
                $"segments {i} and {j} overlap");
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void BoundaryStroke_InsetIsAppliedOnAllFourSides(int dpi)
    {
        var selection = new Rectangle(10, 20, 320, 240);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(dpi);
        var path = RegionSelectionVisualGeometry.ComputeBoundaryBounds(selection, metrics);
        var boundary = RegionSelectionVisualGeometry.ComputeBoundaryStrokeSegments(selection, metrics);

        int inset = (int)Math.Ceiling(metrics.BoundaryStrokeWidth / 2d);
        Assert.Equal(selection.Left + inset, path.Left);
        Assert.Equal(selection.Top + inset, path.Top);
        Assert.Equal(selection.Right - inset, path.Right);
        Assert.Equal(selection.Bottom - inset, path.Bottom);
        Assert.Equal(4, boundary.Count);
        Assert.All(boundary, segment => Assert.True(selection.Contains(segment.PaintEnvelope)));
    }

    [Theory]
    [InlineData(96, 1, 2, 24, 6, 8, 6)]
    [InlineData(120, 1, 3, 30, 8, 10, 8)]
    [InlineData(144, 2, 3, 36, 9, 12, 9)]
    [InlineData(192, 2, 4, 48, 12, 16, 12)]
    public void Metrics_ScaleLogicalGeometryAtSupportedDpi(
        int dpi,
        int boundary,
        int accent,
        int corner,
        int padding,
        int gap,
        int handle)
    {
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(dpi);

        Assert.Equal(boundary, metrics.BoundaryStrokeWidth);
        Assert.Equal(accent, metrics.AccentStrokeWidth);
        Assert.Equal(corner, metrics.CornerLength);
        Assert.Equal(padding, metrics.LabelPadding);
        Assert.Equal(gap, metrics.LabelGap);
        Assert.Equal(handle, metrics.EdgeHandleSize);
    }

    [Theory]
    [InlineData(96, 12, 13)]
    [InlineData(120, 15, 17)]
    [InlineData(144, 18, 20)]
    [InlineData(192, 24, 27)]
    public void Metrics_ExposeExactlyOnceScaledPhysicalPixelFontSizes(
        int dpi,
        int selectionLabelPixels,
        int displayLabelPixels)
    {
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(dpi);

        Assert.Equal(selectionLabelPixels, metrics.SelectionLabelFontPixelSize);
        Assert.Equal(displayLabelPixels, metrics.DisplayBoundaryLabelFontPixelSize);
        Assert.Equal(selectionLabelPixels,
            RegionSelectionVisualGeometry.PointsToPhysicalPixels(9f, dpi));
        Assert.Equal(displayLabelPixels,
            RegionSelectionVisualGeometry.PointsToPhysicalPixels(10f, dpi));
    }

    [Fact]
    public void EdgeHandles_OnlyExposeFourSmallDpiScaledMidpoints()
    {
        var handles = RegionSelectionVisualGeometry.ComputeEdgeHandleBounds(
            new Rectangle(100, 100, 320, 240),
            RegionSelectionVisualGeometry.ComputeMetrics(192));

        Assert.Equal(4, handles.Count);
        Assert.All(handles, handle => Assert.Equal(12, handle.Width));
        Assert.All(handles, handle => Assert.Equal(12, handle.Height));
    }

    [Fact]
    public void LabelPlacement_PrefersAboveThenBelowThenInsideAndStaysVisible()
    {
        var client = new Rectangle(0, 0, 1200, 800);
        var selection = new Rectangle(300, 200, 400, 240);
        var metrics = RegionSelectionVisualGeometry.ComputeMetrics(96);

        var above = RegionSelectionVisualGeometry.ComputeLabelLayout(
            selection, new Size(160, 20), client, metrics);
        Assert.Equal(RegionSelectionLabelPlacement.AboveSelection, above.Placement);
        Assert.True(client.Contains(above.Bounds));

        var below = RegionSelectionVisualGeometry.ComputeLabelLayout(
            new Rectangle(300, 4, 400, 240),
            new Size(160, 20),
            client,
            metrics,
            new[] { new Rectangle(0, 0, client.Width, 80) });
        Assert.Equal(RegionSelectionLabelPlacement.BelowSelection, below.Placement);
        Assert.True(client.Contains(below.Bounds));

        var insideTop = RegionSelectionVisualGeometry.ComputeLabelLayout(
            selection,
            new Size(160, 20),
            client,
            metrics,
            new[]
            {
                new Rectangle(0, 140, client.Width, 60),
                new Rectangle(0, 440, client.Width, 360)
            });
        Assert.Equal(RegionSelectionLabelPlacement.InsideTop, insideTop.Placement);
        Assert.True(client.Contains(insideTop.Bounds));

        var insideBottom = RegionSelectionVisualGeometry.ComputeLabelLayout(
            selection,
            new Size(160, 20),
            client,
            metrics,
            new[]
            {
                new Rectangle(0, 140, client.Width, 60),
                new Rectangle(0, 440, client.Width, 360),
                new Rectangle(0, 200, client.Width, 45)
            });
        Assert.Equal(RegionSelectionLabelPlacement.InsideBottom, insideBottom.Placement);
        Assert.True(client.Contains(insideBottom.Bounds));
    }

    [Fact]
    public void LabelPlacement_ClipsEdgePinnedLongTextToClientBounds()
    {
        var layout = RegionSelectionVisualGeometry.ComputeLabelLayout(
            new Rectangle(0, 0, 32, 32),
            new Size(240, 40),
            new Rectangle(0, 0, 100, 80),
            RegionSelectionVisualGeometry.ComputeMetrics(192));

        Assert.True(layout.IsVisible);
        Assert.True(layout.IsClipped);
        Assert.True(new Rectangle(0, 0, 100, 80).Contains(layout.Bounds));
        Assert.True(layout.TextBounds.Width >= 0);
    }

    [Fact]
    public void LabelText_UsesDimensionsAndDeterministicMiddleEllipsis()
    {
        Assert.Equal(
            "1920×1080 @ display_1",
            RegionSelectionVisualGeometry.FormatSelectionLabelText(1920, 1080, "display_1"));
        Assert.Equal(
            "1920×1080",
            RegionSelectionVisualGeometry.FormatSelectionLabelText(1920, 1080, "unknown"));

        var abbreviated = RegionSelectionVisualGeometry.FormatSelectionLabelText(
            1920, 1080, "display-with-a-very-long-stable-name", 12);
        Assert.Equal("1920×1080 @ displ…e-name", abbreviated);
        Assert.Contains("1920×1080", abbreviated);
    }

    [Fact]
    public void HighContrastPalette_UsesSystemColorsAndStillHasBoundaryAndAccent()
    {
        var palette = RegionSelectionVisualPalette.Create(highContrast: true);

        Assert.True(palette.IsHighContrast);
        Assert.Equal(SystemColors.Highlight, palette.SelectionAccent);
        Assert.Equal(SystemColors.WindowText, palette.SelectionBoundary);
        Assert.Equal(SystemColors.WindowText, palette.SelectionLabelText);
        Assert.NotEqual(Color.Empty, palette.SelectionMask);
    }
}
