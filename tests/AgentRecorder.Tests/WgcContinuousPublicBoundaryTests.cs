using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Task 64: public API/CLI boundary freeze tests.
/// Verifies that explicit WGC continuous recording markers are rejected
/// with 400 UNSUPPORTED_FEATURE before any source/window/display lookup.
/// No real WGC continuous capture is implemented.
/// </summary>
public class WgcContinuousPublicBoundaryTests
{
    private static JsonNode Cfg(object value) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))!;

    private static void AssertUnsupported(ApiException ex, string field, string value)
    {
        Assert.Equal(400, ex.Status);
        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.Contains(field, ex.Message);
        Assert.Contains(value, ex.Message);
        Assert.Contains("WGC continuous recording is not implemented", ex.Message);
    }

    [Fact]
    public void ConfigParser_Build_CaptureKindContinuous_ThrowsUnsupportedFeature()
    {
        var cfg = Cfg(new
        {
            capture_kind = "continuous",
            source = new { type = "display", display_id = "DISPLAY_1" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        AssertUnsupported(ex, "capture_kind", "continuous");
    }

    [Fact]
    public void ConfigParser_Build_RecordingModeContinuous_ThrowsUnsupportedFeature()
    {
        var cfg = Cfg(new
        {
            recording_mode = "continuous",
            source = new { type = "display", display_id = "DISPLAY_1" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        AssertUnsupported(ex, "recording_mode", "continuous");
    }

    [Fact]
    public void ConfigParser_Build_CaptureMethodWgcStream_ThrowsUnsupportedFeature()
    {
        var cfg = Cfg(new
        {
            capture_method = "WGC_D3D11_FRAME_STREAM",
            source = new { type = "display", display_id = "DISPLAY_1" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        AssertUnsupported(ex, "capture_method", "WGC_D3D11_FRAME_STREAM");
    }

    [Theory]
    [InlineData("wgc_continuous")]
    [InlineData("wgc-continuous")]
    public void ConfigParser_Build_BackendWgcContinuous_ThrowsUnsupportedFeature(string backend)
    {
        var cfg = Cfg(new
        {
            backend,
            source = new { type = "display", display_id = "DISPLAY_1" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        AssertUnsupported(ex, "backend", backend);
    }

    [Fact]
    public void ConfigParser_Build_CaptureKindContinuous_BeforeSourceLookup()
    {
        // Even with a completely invalid/non-existent window, the continuous
        // marker must be rejected first -- not SOURCE_NOT_FOUND.
        var cfg = Cfg(new
        {
            capture_kind = "continuous",
            source = new { type = "window", window_id = "window_999999999" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        Assert.Equal(400, ex.Status);
        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.NotEqual("SOURCE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void ConfigParser_Build_RecordingModeContinuous_BeforeSourceLookup()
    {
        var cfg = Cfg(new
        {
            recording_mode = "continuous",
            source = new { type = "window", window_id = "window_999999999" }
        });

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(cfg, "test", out _));
        Assert.Equal(400, ex.Status);
        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.NotEqual("SOURCE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void ConfigParser_Build_StandardMp4H264_NotRejected()
    {
        // Ordinary FFmpeg recording uses mp4/h264 and must not be blocked.
        var cfg = Cfg(new
        {
            source = new { type = "display", display_id = "DISPLAY_1" },
            output = new { directory = "default" },
            video = new { fps = 30, codec = "h264" },
            audio = new { microphone = new { enabled = false } }
        });

        // Does not throw for unsupported continuous; may still throw for source not
        // found if the test machine has no matching display, but that is a
        // different error code.
        try
        {
            _ = ConfigParser.Build(cfg, "test", out _);
        }
        catch (ApiException ex)
        {
            Assert.NotEqual("UNSUPPORTED_FEATURE", ex.Code);
        }
    }
}
