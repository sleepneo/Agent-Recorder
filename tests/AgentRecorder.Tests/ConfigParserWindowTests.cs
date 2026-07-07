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

public class ConfigParserWindowTests : IDisposable
{
    private static JsonNode ParseJson(string json) => JsonNode.Parse(json)!;

    public ConfigParserWindowTests()
    {
        // Default: single 1920x1080 display for most tests
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_1234", "Test Window", "test.exe", 123, true, false,
                new SystemQuery.Bounds(-10, 20, 801, 603))
        });
    }

    public void Dispose()
    {
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetWindowProvider(null);
    }

    [Fact]
    public void Parse_ValidWindow_ParsesSuccessfully_WithBounds()
    {
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_1234""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" },
            ""stop_condition"": { ""type"": ""duration"", ""seconds"": 10 }
        }");

        var rec = ConfigParser.Build(cfg, "test-agent", out _);

        Assert.NotNull(rec);
        Assert.Equal("window", rec.SourceType);
        Assert.Equal("Test Window", rec.SourceTitle);

        var cap = Assert.IsType<CaptureConfig>(rec.Config);
        Assert.Equal("window", cap.SourceKind);
        Assert.Equal("Test Window", cap.WindowTitle);
        Assert.NotEqual(nint.Zero, cap.WindowHandle);
    }

    [Fact]
    public void Parse_WindowOddDimensions_NormalizesToEven()
    {
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_1234""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Raw: x=-10, y=20, w=801, h=603
        // Clamped to virtual screen (0,0,1920,1080):
        //   left=max(-10,0)=0, top=max(20,0)=20
        //   right=min(-10+801=-791,1920)=791, bottom=min(20+603=623,1080)=623
        //   => w=791-0=791, h=623-20=603
        // Normalized to even => w=790, h=602
        Assert.Equal(0, cap.Bounds.x);
        Assert.Equal(20, cap.Bounds.y);
        Assert.Equal(790, cap.Bounds.w);
        Assert.Equal(602, cap.Bounds.h);
    }

    [Fact]
    public void Parse_NegativeDisplayWindow_NotClampedToZero()
    {
        // Multi-monitor: left display at x=-1920
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_left", "Left", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.0),
            new("display_main", "Main", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_5678", "Negative Window", "neg.exe", 456, true, false,
                new SystemQuery.Bounds(-1900, 50, 800, 600))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_5678""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Window at x=-1900 is within left display (-1920..0), should NOT be clamped to 0
        Assert.Equal(-1900, cap.Bounds.x);
        Assert.Equal(50, cap.Bounds.y);
        Assert.Equal(800, cap.Bounds.w);
        Assert.Equal(600, cap.Bounds.h);
    }

    [Fact]
    public void Parse_MaximizedWindowBorder_ClampedToVirtualScreen()
    {
        // Large display (e.g. ultrawide or high-DPI scaled)
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 6400, 3220), 1.0)
        });
        // Window with invisible resize borders (like maximized window via GetWindowRect)
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_1234", "Maximized", "app.exe", 1, true, false,
                new SystemQuery.Bounds(-13, -13, 3866, 2090))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_1234""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var rec = ConfigParser.Build(cfg, "test-agent", out _);
        var cap = Assert.IsType<CaptureConfig>(rec.Config);

        // Should be clamped to virtual screen (0,0,6400,3220)
        Assert.True(cap.Bounds.x >= 0, $"x={cap.Bounds.x} should be >= 0");
        Assert.True(cap.Bounds.y >= 0, $"y={cap.Bounds.y} should be >= 0");
        Assert.True(cap.Bounds.x + cap.Bounds.w <= 6400, $"right={cap.Bounds.x + cap.Bounds.w} should be <= 6400");
        Assert.True(cap.Bounds.y + cap.Bounds.h <= 3220, $"bottom={cap.Bounds.y + cap.Bounds.h} should be <= 3220");
        Assert.True(cap.Bounds.w % 2 == 0, "width should be even");
        Assert.True(cap.Bounds.h % 2 == 0, "height should be even");
    }

    [Fact]
    public void Parse_WindowOutsideVirtualScreen_ThrowsSourceUnavailable()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_9999", "Far Away", "far.exe", 789, true, false,
                new SystemQuery.Bounds(-5000, 0, 800, 600))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_9999""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Equal("SOURCE_UNAVAILABLE", ex.Code);
        Assert.Contains("restore_or_move_window_then_retry", ex.Details?.ToString() ?? "");
    }

    [Fact]
    public void Parse_WindowZeroWidth_AfterClamping_ThrowsSourceUnavailable()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            // Window completely to the left of the virtual screen
            new("window_8888", "Zero After Clip", "clip.exe", 101, true, false,
                new SystemQuery.Bounds(-100, 0, 50, 600))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_8888""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Equal("SOURCE_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void Parse_WindowTooSmall_ThrowsInvalidArgument()
    {
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_7777", "Tiny Window", "tiny.exe", 101, true, false,
                new SystemQuery.Bounds(0, 0, 20, 20))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_7777""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Parse_MinimizedWindow_ThrowsSourceUnavailable()
    {
        SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
        {
            new("window_6666", "Minimized Window", "min.exe", 202, false, true,
                new SystemQuery.Bounds(0, 0, 800, 600))
        });

        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_6666""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(403, ex.Status);
        Assert.Equal("SOURCE_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void Parse_UnknownWindow_ThrowsSourceNotFound()
    {
        var cfg = ParseJson(@"{
            ""source"": {
                ""type"": ""window"",
                ""window_id"": ""window_nonexistent""
            },
            ""video"": { ""fps"": 30, ""quality"": ""medium"" }
        }");

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test-agent", out _));
        Assert.Equal(404, ex.Status);
        Assert.Equal("SOURCE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void VirtualScreenBounds_SingleDisplay_MatchesDisplayBounds()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        var vs = SystemQuery.VirtualScreenBounds();
        Assert.Equal(0, vs.x);
        Assert.Equal(0, vs.y);
        Assert.Equal(1920, vs.width);
        Assert.Equal(1080, vs.height);
    }

    [Fact]
    public void VirtualScreenBounds_MultiDisplay_UnionCorrectly()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_left", "Left", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.0),
            new("display_main", "Main", true, new SystemQuery.Bounds(0, 0, 2560, 1440), 1.0)
        });

        var vs = SystemQuery.VirtualScreenBounds();
        Assert.Equal(-1920, vs.x);
        Assert.Equal(0, vs.y);
        Assert.Equal(4480, vs.width);   // -1920 to 2560 = 4480
        Assert.Equal(1440, vs.height);
    }

    [Fact]
    public void VirtualScreenBounds_NoDisplay_ReturnsZero()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>());
        var vs = SystemQuery.VirtualScreenBounds();
        Assert.Equal(0, vs.x);
        Assert.Equal(0, vs.y);
        Assert.Equal(0, vs.width);
        Assert.Equal(0, vs.height);
    }

    [Fact]
    public void EnumWindows_WithInjectedProvider_ReturnsInjectedData()
    {
        var result = SystemQuery.EnumWindows(true, false);
        Assert.Single(result);
        Assert.Equal("window_1234", result[0].id);
    }

    [Fact]
    public void EnumWindows_AfterReset_DoesNotReturnInjectedData()
    {
        SystemQuery.SetWindowProvider(null);
        var result = SystemQuery.EnumWindows(false, false);
        Assert.DoesNotContain(result, w => w.id == "window_1234");
    }
}
