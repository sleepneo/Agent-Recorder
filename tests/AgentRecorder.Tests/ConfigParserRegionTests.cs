using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Windows;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for ConfigParser region source validation.
/// Uses injectable display provider for deterministic tests.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class ConfigParserRegionTests : IDisposable
{
    private static JsonNode ParseJson(string json) => JsonNode.Parse(json)!;

    public ConfigParserRegionTests()
    {
        // Set up a standard test display before each test
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Primary Display", true,
                new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        // Restore default Win32 provider after each test
        SystemQuery.SetDisplayProvider(null);
    }

    // ============ Positive tests ============

    [Fact]
    public void Parse_ValidRegionRequest_ParsesSuccessfully()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 100, ""y"": 50, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act
        var rec = ConfigParser.Build(cfg, "test-agent", out _);

        // Assert
        Assert.NotNull(rec);
        Assert.Equal("region", rec.SourceType);
        Assert.StartsWith("region:", rec.SourceTitle);
        Assert.Contains("Primary Display", rec.SourceTitle);

        var cap = Assert.IsType<CaptureConfig>(rec.Config);
        Assert.Equal("region", cap.SourceKind);
        Assert.Equal(100, cap.Bounds.x);
        Assert.Equal(50, cap.Bounds.y);
        Assert.Equal(800, cap.Bounds.w);
        Assert.Equal(600, cap.Bounds.h);
        Assert.Null(cap.RegionNormalizedBounds);
    }

    [Fact]
    public void Parse_ValidRegionRequest_DefaultCoordinateSpace()
    {
        // Arrange: no coordinate_space, should default to virtual_screen
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert: should not throw
        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        Assert.NotNull(rec);
    }

    [Fact]
    public void Parse_OddWidth_NormalizesToEven()
    {
        // Arrange: odd width (1279)
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 1279, ""height"": 720 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act
        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Assert
        Assert.Equal(1278, cap.Bounds.w);
        Assert.NotNull(cap.RegionNormalizedBounds);
        Assert.Equal(1278, cap.RegionNormalizedBounds.Value.w);
        Assert.Equal(720, cap.RegionNormalizedBounds.Value.h);
    }

    [Fact]
    public void Parse_OddHeight_NormalizesToEven()
    {
        // Arrange: odd height (721)
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 1280, ""height"": 721 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act
        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Assert
        Assert.Equal(720, cap.Bounds.h);
        Assert.NotNull(cap.RegionNormalizedBounds);
    }

    [Fact]
    public void Parse_EvenDimensions_NoNormalizedBounds()
    {
        // Arrange: both dimensions even
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 1280, ""height"": 720 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act
        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Assert
        Assert.Null(cap.RegionNormalizedBounds);
    }

    [Fact]
    public void Parse_RegionExceedsDisplayBounds_ThrowsInvalidArgument()
    {
        // Arrange: region exceeds 1920x1080 display
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 100, ""y"": 100, ""width"": 2000, ""height"": 1000 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display_bounds", ex.Details?.ToString() ?? "");
    }

    [Fact]
    public void Parse_NegativeCoordinateDisplay_RegionWithinBounds_Succeeds()
    {
        // Arrange: secondary display with negative x coordinate
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Primary", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0),
            new("display_2", "Secondary Left", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.0)
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_2"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": -1000, ""y"": 100, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act
        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Assert: -1000 + 800 = -200, which is within -1920 .. 0
        Assert.Equal(-1000, cap.Bounds.x);
        Assert.Equal(800, cap.Bounds.w);
    }

    // ============ Negative validation tests ============

    [Fact]
    public void Parse_UnsupportedSourceType_ThrowsUnsupportedFeature()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": { ""type"": ""foobar"", ""display_id"": ""display_1"" }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("UNSUPPORTED_FEATURE", ex.Code);
    }

    [Fact]
    public void Parse_MissingDisplayId_ThrowsInvalidArgument()
    {
        // Arrange: missing display_id
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""coordinate_space"": ""virtual_screen"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("display_id", ex.Message);
    }

    [Fact]
    public void Parse_UnknownDisplayId_ThrowsNotFound()
    {
        // Arrange: display not in our mock
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_999"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(404, ex.Status);
        Assert.Contains("SOURCE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void Parse_MissingBounds_ThrowsInvalidArgument()
    {
        // Arrange: missing bounds entirely
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1""
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("bounds", ex.Message);
    }

    [Fact]
    public void Parse_MissingBoundsX_ThrowsInvalidArgument()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""bounds"": { ""y"": 0, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("bounds.x", ex.Message);
    }

    [Fact]
    public void Parse_NegativeWidth_ThrowsInvalidArgument()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": -100, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("width", ex.Message);
    }

    [Fact]
    public void Parse_ZeroHeight_ThrowsInvalidArgument()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 800, ""height"": 0 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("height", ex.Message);
    }

    [Fact]
    public void Parse_TooSmallRegion_ThrowsInvalidArgument()
    {
        // Arrange: width < 32
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 10, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Parse_UnsupportedCoordinateSpace_ThrowsInvalidArgument()
    {
        // Arrange
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""region"",
                ""display_id"": ""display_1"",
                ""coordinate_space"": ""screen_space"",
                ""bounds"": { ""x"": 0, ""y"": 0, ""width"": 800, ""height"": 600 }
            },
            ""video"": { ""fps"": 15, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        // Act & Assert
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Contains("coordinate_space", ex.Message);
    }

    // ============ Display provider injection test ============

    [Fact]
    public void EnumDisplays_WithInjectedProvider_ReturnsInjectedData()
    {
        // Arrange
        var expected = new List<SystemQuery.DisplayInfo>
        {
            new("test_display", "Test", true, new SystemQuery.Bounds(0, 0, 100, 100), 1.0)
        };
        SystemQuery.SetDisplayProvider(() => expected);

        // Act
        var result = SystemQuery.EnumDisplays();

        // Assert
        Assert.Single(result);
        Assert.Equal("test_display", result[0].id);
    }

    [Fact]
    public void EnumDisplays_AfterReset_ReturnsWin32Data()
    {
        // Arrange: inject, then reset
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("fake", "Fake", true, new SystemQuery.Bounds(0, 0, 100, 100), 1.0)
        });
        Assert.Equal("fake", SystemQuery.EnumDisplays()[0].id);

        // Act: reset
        SystemQuery.SetDisplayProvider(null);

        // Assert: returns from Win32 (may be 0 in headless, or real displays)
        var result = SystemQuery.EnumDisplays();
        Assert.DoesNotContain(result, d => d.id == "fake");
    }
}
