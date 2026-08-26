using System.Drawing;
using System.Reflection;
using AgentRecorder.UI.Geometry;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class SharedUiGeometryTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void DisplayDpi_ContainedRegion_PreservesDpiMatrix(int dpi)
    {
        var result = DisplayDpiGeometry.Resolve(
            new Rectangle(100, 100, 800, 600),
            new[] { new DisplayDpiCandidate("display_1", new Rectangle(0, 0, 1920, 1080), dpi, dpi) });

        Assert.Equal("display_1", result.MonitorId);
        Assert.Equal(dpi, result.DpiX);
        Assert.Equal(dpi, result.DpiY);
        Assert.Equal(dpi / 96f, result.Scale);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void DisplayDpi_NegativeCoordinatesAndGap_UsesDeterministicNearestDisplay()
    {
        var displays = new[]
        {
            new DisplayDpiCandidate("left", new Rectangle(-1920, -200, 1920, 1080), 120, 120),
            new DisplayDpiCandidate("right", new Rectangle(0, 0, 1920, 1080), 144, 144)
        };

        var result = DisplayDpiGeometry.Resolve(new Rectangle(-1500, -100, 800, 600), displays);
        Assert.Equal("left", result.MonitorId);

        var gapResult = DisplayDpiGeometry.Resolve(new Rectangle(1920, 400, 100, 100),
            new[]
            {
                new DisplayDpiCandidate("z-right", new Rectangle(2200, 0, 1000, 1000), 192, 192),
                new DisplayDpiCandidate("a-left", new Rectangle(0, 0, 1000, 1000), 96, 96)
            });
        Assert.Equal("z-right", gapResult.MonitorId);
    }

    [Fact]
    public void DisplayDpi_TiesUseOrdinalIdAndLargeCoordinatesDoNotOverflow()
    {
        var result = DisplayDpiGeometry.Resolve(
            new Rectangle(int.MaxValue - 800, int.MaxValue - 800, 400, 400),
            new[]
            {
                new DisplayDpiCandidate("z", new Rectangle(int.MaxValue - 1000, int.MaxValue - 1000, 600, 600), 120, 120),
                new DisplayDpiCandidate("a", new Rectangle(int.MaxValue - 1000, int.MaxValue - 1000, 600, 600), 144, 144)
            });

        Assert.Equal("a", result.MonitorId);
        Assert.Equal(144, result.DpiX);
    }

    [Fact]
    public void DisplayDpi_NoDisplaysAndInvalidDpiUseExplicitFallback()
    {
        var empty = DisplayDpiGeometry.Resolve(new Rectangle(1, 2, 3, 4), Array.Empty<DisplayDpiCandidate>());
        Assert.Equal("fallback", empty.MonitorId);
        Assert.Equal(96, empty.DpiX);
        Assert.Equal(96, empty.DpiY);
        Assert.Equal("no_displays_found", empty.FallbackReason);

        var invalid = DisplayDpiGeometry.Resolve(new Rectangle(10, 10, 20, 20),
            new[] { new DisplayDpiCandidate("invalid", new Rectangle(0, 0, 100, 100), 0, -1) });
        Assert.Equal(96, invalid.DpiX);
        Assert.Equal(96, invalid.DpiY);
        Assert.Equal(1f, invalid.Scale);
    }

    [Fact]
    public void RegionSelection_UsesSharedDtosForNegativeDesktopAndWindowFiltering()
    {
        var form = new Rectangle(-1920, -200, 3840, 2160);
        var displays = new[]
        {
            new GeometryDisplay("left", new Rectangle(-1920, -200, 1920, 1080)),
            new GeometryDisplay("right", new Rectangle(0, 0, 1920, 1960))
        };
        var windows = new[]
        {
            new GeometryWindow("min", new Rectangle(-1700, -100, 500, 400), true, true),
            new GeometryWindow("untitled", new Rectangle(-1200, 0, 500, 400), false, false),
            new GeometryWindow("valid", new Rectangle(-1000, 0, 500, 400), false, true)
        };

        Assert.Equal("left", RegionSelectionGeometry.FindDisplayId(new Rectangle(-1700, -100, 400, 300), displays));
        Assert.Single(RegionSelectionGeometry.GenerateSnapTargets(form, Array.Empty<GeometryDisplay>(), windows));
        Assert.NotNull(RegionSelectionGeometry.ComputeWindowPickBounds(form, windows[2]));
    }

    [Fact]
    public void RegionSelection_ClampPresetAspectAndSnappingKeepExistingSemantics()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var preset = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(screen, new Point(960, 540), new Size(1281, 721));
        Assert.Equal(new Size(1280, 720), preset!.Value.Size);

        var snapped = RegionSelectionGeometry.ApplySnapping(
            new Rectangle(103, 103, 801, 601), screen,
            new[] { new Rectangle(100, 100, 800, 600) }, 10,
            SnapEdgeMask.All, preserveSize: true);
        Assert.Equal(new Rectangle(100, 100, 800, 600), snapped);

        var dragged = RegionSelectionGeometry.ClampSelectionAfterDrag(
            new Rectangle(-10, -10, 11, 11), screen, SnapEdgeMask.All);
        Assert.True(dragged.Width >= 32 && dragged.Height >= 32);
        Assert.True(dragged.Width % 2 == 0 && dragged.Height % 2 == 0);
    }

    [Fact]
    public void StopControl_OutsideNestedAndParentVisibleConstraintsArePure()
    {
        var virtualScreen = new Rectangle(-1920, -200, 3840, 2160);
        var recording = new Rectangle(-1500, 100, 800, 600);
        var outside = StopControlGeometry.ComputeBounds(recording, new Size(76, 28), null, virtualScreen);
        Assert.Equal(recording.Right + StopControlGeometry.OutsideMargin, outside.X);
        Assert.True(StopControlGeometry.IsInside(outside, virtualScreen));

        var outer = StopControlGeometry.ComputeBounds(recording, new Size(76, 28), "outer", virtualScreen);
        var inner = StopControlGeometry.ComputeBounds(new Rectangle(-1300, 250, 300, 200),
            new Size(76, 28), "inner", virtualScreen);
        Assert.False(StopControlGeometry.Intersects(outer, inner));

        var parent = new Rectangle(-1600, 0, 1200, 1000);
        var parentVisible = StopControlGeometry.ComputeBounds(
            new Rectangle(-1400, 250, 300, 200), new Size(76, 28), "inner", virtualScreen,
            parent, StopControlVisibilityMode.ParentVisible);
        Assert.True(parent.Contains(parentVisible.ToRectangle()));
        Assert.False(parentVisible.ToRectangle().IntersectsWith(new Rectangle(-1400, 250, 300, 200)));
    }

    [Fact]
    public void StopControl_CollisionSearchHonorsForbiddenAllowedAndDeterministicFallback()
    {
        var screen = new Rectangle(0, 0, 500, 400);
        var preferred = new RecordingStopControlBounds(210, 210, 76, 28);
        var result = StopControlGeometry.ResolveCollision(
            preferred, new Size(76, 28), screen,
            new[] { preferred }, new Rectangle(200, 200, 100, 100), new Rectangle(50, 50, 400, 300));

        Assert.NotNull(result);
        Assert.True(screen.Contains(result!.ToRectangle()));
        Assert.True(new Rectangle(50, 50, 400, 300).Contains(result.ToRectangle()));
        Assert.False(result.ToRectangle().IntersectsWith(new Rectangle(200, 200, 100, 100)));
    }

    [Fact]
    public void SharedGeometryAssemblyHasNoPlatformOrProjectReferences()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AgentRecorder.App", "AgentRecorder.Windows", "AgentRecorder.Capture",
            "AgentRecorder.Api", "AgentRecorder.Core", "System.Windows.Forms", "PresentationCore"
        };
        var references = typeof(RegionSelectionGeometry).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(references, name => forbidden.Contains(name!));
        Assert.DoesNotContain(typeof(RegionSelectionGeometry).Assembly.GetTypes(), type =>
            type.FullName is not null &&
            (type.FullName.Contains("System.Windows.Forms", StringComparison.Ordinal) ||
             type.FullName.Contains("AgentRecorder.Windows", StringComparison.Ordinal)));
    }
}
