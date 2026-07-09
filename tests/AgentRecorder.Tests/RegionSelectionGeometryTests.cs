using System;
using System.Collections.Generic;
using System.Drawing;
using Xunit;
using AgentRecorder.App;
using static AgentRecorder.Windows.SystemQuery;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for RegionSelectionGeometry pure functions.
/// Tests virtual screen coordinate conversion, display lookup, preset sizing,
/// aspect-ratio fitting, and even-bound normalization without requiring WinForms
/// or a real desktop.
/// </summary>
public class RegionSelectionGeometryTests
{
    // -------------------------------------------------------------------------
    // ToVirtualBounds tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ToVirtualBounds_StandardScreen_ReturnsCorrectVirtualCoords()
    {
        // Virtual screen (0,0) to (6400,3219) with primary display at (0,0)
        var formBounds = new Rectangle(0, 0, 6400, 3219);
        var clientSelection = new Rectangle(1138, 341, 1592, 892);

        var result = RegionSelectionGeometry.ToVirtualBounds(formBounds, clientSelection);

        Assert.Equal(1138, result.X);
        Assert.Equal(341, result.Y);
        Assert.Equal(1592, result.Width);
        Assert.Equal(892, result.Height);
    }

    [Fact]
    public void ToVirtualBounds_NegativeCoordinateDisplay_ReturnsNegativeVirtualX()
    {
        // Left display starts at -2560, form spans full virtual screen
        var formBounds = new Rectangle(-2560, 0, 6400, 2160);
        var clientSelection = new Rectangle(100, 200, 801, 603);

        var result = RegionSelectionGeometry.ToVirtualBounds(formBounds, clientSelection);

        Assert.Equal(-2460, result.X);   // -2560 + 100
        Assert.Equal(200, result.Y);       // 0 + 200
        Assert.Equal(801, result.Width);
        Assert.Equal(603, result.Height);
    }

    [Fact]
    public void ToVirtualBounds_ZeroOffset_EqualsOriginal()
    {
        // When form bounds start at (0,0), virtual = client
        var formBounds = new Rectangle(0, 0, 3840, 2160);
        var clientSelection = new Rectangle(100, 200, 800, 600);

        var result = RegionSelectionGeometry.ToVirtualBounds(formBounds, clientSelection);

        Assert.Equal(100, result.X);
        Assert.Equal(200, result.Y);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    // -------------------------------------------------------------------------
    // NormalizeEvenBounds tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(1592, 892, 1592, 892)] // already even
    [InlineData(1593, 893, 1592, 892)] // odd -> even (minus 1)
    [InlineData(1594, 894, 1594, 894)] // already even
    [InlineData(1, 1, 32, 32)]         // below min -> clamped to 32
    [InlineData(63, 63, 62, 62)]       // odd -> even (minus 1)
    [InlineData(65, 65, 64, 64)]        // odd above min -> 64
    [InlineData(64, 64, 64, 64)]        // exactly even min -> 64
    [InlineData(32, 32, 32, 32)]        // exactly default min -> 32
    [InlineData(0, 0, 32, 32)]           // zero -> minimum 32
    public void NormalizeEvenBounds_VariousInputs_ReturnsExpectedOutput(
        int inputW, int inputH, int expectedW, int expectedH)
    {
        var (w, h) = RegionSelectionGeometry.NormalizeEvenBounds(inputW, inputH);

        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Fact]
    public void NormalizeEvenBounds_ClampsToMaxDimensions()
    {
        var (w, h) = RegionSelectionGeometry.NormalizeEvenBounds(2000, 1500, maxWidth: 1920, maxHeight: 1080);

        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void NormalizeEvenBounds_OddMaxDimensions_MakesEven()
    {
        // When max is odd, result should be even and at or below max
        var (w, h) = RegionSelectionGeometry.NormalizeEvenBounds(1921, 1081, maxWidth: 1921, maxHeight: 1081);

        Assert.Equal(1920, w);  // 1921 - 1
        Assert.Equal(1080, h);  // 1081 - 1
    }

    // -------------------------------------------------------------------------
    // FindDisplayId tests
    // -------------------------------------------------------------------------

    [Fact]
    public void FindDisplayId_CenterOnPrimaryDisplay_ReturnsPrimary()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
            new DisplayInfo("display_2", "Display 2", false,
                new Bounds(3840, 0, 2560, 1600), 1.0),
        };

        // Center of a region at x=1000, y=500 with size 800x600
        // center = (1000+400, 500+300) = (1400, 800) -> display_1
        var bounds = new Rectangle(1000, 500, 800, 600);

        var result = RegionSelectionGeometry.FindDisplayId(bounds, displays);

        Assert.Equal("display_1", result);
    }

    [Fact]
    public void FindDisplayId_CenterOnSecondaryDisplay_ReturnsSecondary()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
            new DisplayInfo("display_2", "Display 2", false,
                new Bounds(3840, 0, 2560, 1600), 1.0),
        };

        // Center at (5000, 800) -> display_2
        var bounds = new Rectangle(4500, 500, 800, 600);

        var result = RegionSelectionGeometry.FindDisplayId(bounds, displays);

        Assert.Equal("display_2", result);
    }

    [Fact]
    public void FindDisplayId_CenterOnNegativeCoordinateDisplay_ReturnsNegativeDisplay()
    {
        // Left monitor at -2560 to 0
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_neg", "Left Display", false,
                new Bounds(-2560, 0, 2560, 1600), 1.0),
            new DisplayInfo("display_main", "Main Display", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
        };

        // Selection at x=-2000, y=200 with size 800x600
        // center = (-1600, 500) -> display_neg
        var bounds = new Rectangle(-2000, 200, 800, 600);

        var result = RegionSelectionGeometry.FindDisplayId(bounds, displays);

        Assert.Equal("display_neg", result);
    }

    [Fact]
    public void FindDisplayId_CenterOutsideAllDisplays_ReturnsNull()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
        };

        // Center at x=5000 (outside display_1)
        var bounds = new Rectangle(4500, 1000, 100, 100);

        var result = RegionSelectionGeometry.FindDisplayId(bounds, displays);

        Assert.Null(result);
    }

    [Fact]
    public void FindDisplayIdByOverlap_MostOverlapWins()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
            new DisplayInfo("display_2", "Display 2", false,
                new Bounds(3840, 0, 2560, 1600), 1.0),
        };

        // Selection spanning across both displays
        // 2000x1000 region from x=3700, y=1000
        // display_1 gets 140x1000 = 140000 area
        // display_2 gets 200x1000 = 200000 area
        var bounds = new Rectangle(3700, 1000, 2000, 1000);

        var result = RegionSelectionGeometry.FindDisplayIdByOverlap(bounds, displays);

        Assert.Equal("display_2", result);
    }

    // -------------------------------------------------------------------------
    // ClampInitialSelection tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ClampInitialSelection_InsideFormBounds_ReturnsClampedRectangle()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var virtualBounds = new Rectangle(100, 150, 800, 600);

        var result = RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds);

        Assert.NotNull(result);
        Assert.Equal(new Rectangle(100, 150, 800, 600), result.Value);
    }

    [Fact]
    public void ClampInitialSelection_PartiallyOutsideFormBounds_ReturnsClampedRectangle()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var virtualBounds = new Rectangle(1800, 900, 300, 300);

        var result = RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds);

        Assert.NotNull(result);
        Assert.True(result.Value.Width >= 32);
        Assert.True(result.Value.Height >= 32);
        Assert.True(result.Value.Right <= formBounds.Width);
        Assert.True(result.Value.Bottom <= formBounds.Height);
    }

    [Fact]
    public void ClampInitialSelection_OutsideFormBounds_ReturnsNull()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var virtualBounds = new Rectangle(3000, 2000, 800, 600);

        var result = RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds);

        Assert.Null(result);
    }

    [Fact]
    public void ClampInitialSelection_TooSmall_ReturnsNull()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var virtualBounds = new Rectangle(100, 100, 10, 10);

        var result = RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds);

        Assert.Null(result);
    }

    [Fact]
    public void ClampInitialSelection_NegativeCoordinateDisplay_ReturnsClampedRectangle()
    {
        var formBounds = new Rectangle(-2560, 0, 6400, 2160);
        var virtualBounds = new Rectangle(-2460, 200, 800, 600);

        var result = RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds);

        Assert.NotNull(result);
        Assert.Equal(new Rectangle(100, 200, 800, 600), result.Value);
    }

    // -------------------------------------------------------------------------
    // ClampSelectionToClientRectangle tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ClampSelectionToClientRectangle_Inside_ReturnsSame()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(100, 100, 800, 600);

        var result = RegionSelectionGeometry.ClampSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.Equal(clientBounds, result.Value);
    }

    [Fact]
    public void ClampSelectionToClientRectangle_PartiallyOutside_ReturnsClamped()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(1800, 900, 300, 300);

        var result = RegionSelectionGeometry.ClampSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.True(result.Value.Right <= formBounds.Width);
        Assert.True(result.Value.Bottom <= formBounds.Height);
        Assert.True(result.Value.Width >= 32);
        Assert.True(result.Value.Height >= 32);
    }

    [Fact]
    public void ClampSelectionToClientRectangle_SmallerThanMin_Expands()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(100, 100, 10, 10);

        var result = RegionSelectionGeometry.ClampSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.Equal(32, result.Value.Width);
        Assert.Equal(32, result.Value.Height);
    }

    [Fact]
    public void ClampSelectionToClientRectangle_CompletelyOutside_ReturnsNull()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(3000, 2000, 800, 600);

        var result = RegionSelectionGeometry.ClampSelectionToClientRectangle(formBounds, clientBounds);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Preset size tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ApplyPresetSizeAroundCenter_CenteredOnScreen_ReturnsPresetSize()
    {
        // 1920x1080 virtual screen, center (960, 540)
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var centerVirtual = new Point(960, 540);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1280, 720));

        Assert.NotNull(result);
        Assert.Equal(1280, result.Value.Width);
        Assert.Equal(720, result.Value.Height);
        // Centered: left = 960 - 1280/2 = 320, top = 540 - 720/2 = 180
        Assert.Equal(320, result.Value.Left);
        Assert.Equal(180, result.Value.Top);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_ExceedsScreen_Clamped()
    {
        // Small 800x600 screen, preset 1920x1080 should be clamped.
        var formBounds = new Rectangle(0, 0, 800, 600);
        var centerVirtual = new Point(400, 300);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1920, 1080));

        Assert.NotNull(result);
        Assert.True(result.Value.Width <= 800);
        Assert.True(result.Value.Height <= 600);
        Assert.True(result.Value.Width >= 32);
        Assert.True(result.Value.Height >= 32);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_OddDimensions_NormalizedToEven()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var centerVirtual = new Point(960, 540);

        // 1281x721 are odd -> should normalize to 1280x720
        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1281, 721));

        Assert.NotNull(result);
        Assert.Equal(1280, result.Value.Width);
        Assert.Equal(720, result.Value.Height);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_32x32Minimum_IsValid()
    {
        var formBounds = new Rectangle(0, 0, 100, 100);
        var centerVirtual = new Point(50, 50);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(32, 32));

        Assert.NotNull(result);
        Assert.Equal(32, result.Value.Width);
        Assert.Equal(32, result.Value.Height);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_NegativeCoordinateDisplay_DoesNotOverflow()
    {
        // Virtual screen starts at -2560, left display is -2560..0.
        var formBounds = new Rectangle(-2560, 0, 6400, 2160);
        var centerVirtual = new Point(-1280, 800); // center of left display

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1280, 720));

        Assert.NotNull(result);
        Assert.True(result.Value.Width >= 32);
        Assert.True(result.Value.Height >= 32);
        Assert.True(result.Value.Right <= formBounds.Width);
        Assert.True(result.Value.Bottom <= formBounds.Height);
        Assert.True(result.Value.Left >= 0);
        Assert.True(result.Value.Top >= 0);
    }

    // -------------------------------------------------------------------------
    // Aspect ratio fit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void FitAspectRatio_On1920x1080Screen_Returns16x9()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var centerVirtual = new Point(960, 540);

        var result = RegionSelectionGeometry.FitAspectRatio(formBounds, centerVirtual, 16.0 / 9.0);

        Assert.NotNull(result);
        Assert.True(result.Value.Width >= 32);
        Assert.True(result.Value.Height >= 32);
        Assert.True(result.Value.Width <= 1920);
        Assert.True(result.Value.Height <= 1080);
        Assert.True(result.Value.Width % 2 == 0);
        Assert.True(result.Value.Height % 2 == 0);
        // Centered
        Assert.Equal(960, result.Value.Left + result.Value.Width / 2);
        Assert.Equal(540, result.Value.Top + result.Value.Height / 2);
    }

    [Fact]
    public void FitAspectRatio_TallScreen_LimitedByHeight()
    {
        var formBounds = new Rectangle(0, 0, 3840, 1080);
        var centerVirtual = new Point(1920, 540);

        var result = RegionSelectionGeometry.FitAspectRatio(formBounds, centerVirtual, 16.0 / 9.0);

        Assert.NotNull(result);
        // Height limited to 1080, so width should be close to 1920 (16:9)
        Assert.True(result.Value.Height <= 1080);
        Assert.True(result.Value.Width <= 3840);
        Assert.True(result.Value.Height >= 32);
        Assert.True(result.Value.Width >= 32);
    }

    // -------------------------------------------------------------------------
    // Integration: full coordinate conversion flow
    // -------------------------------------------------------------------------

    [Fact]
    public void FullFlow_NegativeDisplay_ConvertsAndNormalizes()
    {
        // Simulate: form on full virtual screen starting at (-2560,0)
        // user selects region at client coords (100, 200) size (801, 603)
        var formBounds = new Rectangle(-2560, 0, 6400, 2160);
        var clientSelection = new Rectangle(100, 200, 801, 603);

        // Step 1: Convert to virtual coords
        var virtualBounds = RegionSelectionGeometry.ToVirtualBounds(formBounds, clientSelection);
        Assert.Equal(-2460, virtualBounds.X);
        Assert.Equal(200, virtualBounds.Y);
        Assert.Equal(801, virtualBounds.Width);
        Assert.Equal(603, virtualBounds.Height);

        // Step 2: Normalize to even bounds
        var (normW, normH) = RegionSelectionGeometry.NormalizeEvenBounds(
            virtualBounds.Width, virtualBounds.Height);
        Assert.Equal(800, normW);  // 801 -> 800 (even)
        Assert.Equal(602, normH);  // 603 -> 602 (even)

        // Step 3: Find display
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_neg", "Left", false,
                new Bounds(-2560, 0, 2560, 1600), 1.0),
            new DisplayInfo("display_main", "Main", true,
                new Bounds(0, 0, 3840, 2160), 1.0),
        };

        // Center of (-2460, 200) + (800/2, 602/2) = (-2060, 501) -> display_neg
        var finalBounds = new Rectangle(virtualBounds.X, virtualBounds.Y, normW, normH);
        var displayId = RegionSelectionGeometry.FindDisplayId(finalBounds, displays);
        Assert.Equal("display_neg", displayId);
    }

    // -------------------------------------------------------------------------
    // ClampSizedSelectionToClientRectangle tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ClampSizedSelectionToClientRectangle_NearRightEdge_TranslatesPreservesSize()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(1700, 200, 1280, 720);

        var result = RegionSelectionGeometry.ClampSizedSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.Equal(1280, result.Value.Width);
        Assert.Equal(720, result.Value.Height);
        Assert.True(result.Value.Right <= 1920);
        Assert.True(result.Value.Bottom <= 1080);
    }

    [Fact]
    public void ClampSizedSelectionToClientRectangle_ClippedResultRemainsEven()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var clientBounds = new Rectangle(1800, 900, 200, 200);

        var result = RegionSelectionGeometry.ClampSizedSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.Equal(200, result.Value.Width);
        Assert.Equal(200, result.Value.Height);
        Assert.True(result.Value.Width % 2 == 0);
        Assert.True(result.Value.Height % 2 == 0);
        Assert.True(result.Value.Right <= 1920);
        Assert.True(result.Value.Bottom <= 1080);
    }

    [Fact]
    public void ClampSizedSelectionToClientRectangle_LargerThanScreen_ShrinksToEvenScreenBounds()
    {
        var formBounds = new Rectangle(0, 0, 1001, 701);
        var clientBounds = new Rectangle(0, 0, 1920, 1080);

        var result = RegionSelectionGeometry.ClampSizedSelectionToClientRectangle(formBounds, clientBounds);

        Assert.NotNull(result);
        Assert.True(result.Value.Width <= 1001);
        Assert.True(result.Value.Height <= 701);
        Assert.True(result.Value.Width % 2 == 0);
        Assert.True(result.Value.Height % 2 == 0);
    }

    // -------------------------------------------------------------------------
    // Preset size near-edge tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ApplyPresetSizeAroundCenter_NearRightEdge_PreservesPresetWidthWhenScreenCanFit()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var centerVirtual = new Point(1800, 540);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1280, 720));

        Assert.NotNull(result);
        Assert.Equal(1280, result.Value.Width);
        Assert.Equal(720, result.Value.Height);
        Assert.True(result.Value.Right <= 1920);
        Assert.Equal(640, result.Value.Left);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_NearBottomEdge_PreservesPresetHeightWhenScreenCanFit()
    {
        var formBounds = new Rectangle(0, 0, 1920, 1080);
        var centerVirtual = new Point(960, 900);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1280, 720));

        Assert.NotNull(result);
        Assert.Equal(1280, result.Value.Width);
        Assert.Equal(720, result.Value.Height);
        Assert.True(result.Value.Bottom <= 1080);
        Assert.Equal(360, result.Value.Top);
    }

    [Fact]
    public void ApplyPresetSizeAroundCenter_WhenPresetLargerThanScreen_ShrinksToEvenScreenBounds()
    {
        var formBounds = new Rectangle(0, 0, 1001, 701);
        var centerVirtual = new Point(500, 350);

        var result = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
            formBounds, centerVirtual, new Size(1920, 1080));

        Assert.NotNull(result);
        Assert.True(result.Value.Width <= 1001);
        Assert.True(result.Value.Height <= 701);
        Assert.True(result.Value.Width % 2 == 0);
        Assert.True(result.Value.Height % 2 == 0);
    }

    // -------------------------------------------------------------------------
    // FindDisplayIdByOverlap no-overlap test
    // -------------------------------------------------------------------------

    [Fact]
    public void FindDisplayIdByOverlap_NoOverlap_ReturnsNull()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 100, 100), 1.0),
        };

        var bounds = new Rectangle(150, 150, 20, 20);

        var result = RegionSelectionGeometry.FindDisplayIdByOverlap(bounds, displays);

        Assert.Null(result);
    }

    [Fact]
    public void FindDisplayIdByOverlap_TieKeepsFirstPositiveOverlap()
    {
        var displays = new List<DisplayInfo>
        {
            new DisplayInfo("display_1", "Display 1", true,
                new Bounds(0, 0, 100, 200), 1.0),
            new DisplayInfo("display_2", "Display 2", false,
                new Bounds(100, 0, 100, 200), 1.0),
        };

        // 100x200 region from (50,0) overlaps each display by 50x200 = 10000
        var bounds = new Rectangle(50, 0, 100, 200);

        var result = RegionSelectionGeometry.FindDisplayIdByOverlap(bounds, displays);

        Assert.Equal("display_1", result);
    }
}
