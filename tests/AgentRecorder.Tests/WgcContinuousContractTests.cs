using System.Text.Json;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Contract tests for WGC continuous recording evidence (schema v2.0 draft).
/// These tests validate the contract shape without requiring real WGC, helper, or GUI.
/// Status: Design draft only - no real continuous capture is implemented.
/// </summary>
public class WgcContinuousContractTests
{
    private const string SchemaV2 = "2.0";
    private const string CaptureKindContinuous = "continuous";
    private const string CaptureKindStillFrame = "still-frame";

    private static JsonElement ParsePayload(string json)
    {
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Serialize(object obj)
    {
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
    }

    // -----------------------------------------------------------------
    // Schema version detection
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousEvidence_RequiresSchemaVersionTwoPointZero()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:00:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            final_status = "completed",
            frames_captured = 900,
            duration_ms = 30000L
        };
        var json = Serialize(payload);
        var root = ParsePayload(json);

        Assert.Equal("2.0", root.GetProperty("schema_version").GetString());
    }

    [Fact]
    public void ContinuousEvidence_RequiresCaptureKindField()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:00:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            final_status = "completed"
        };
        var json = Serialize(payload);
        var root = ParsePayload(json);

        Assert.True(root.TryGetProperty("capture_kind", out var kind));
        Assert.Equal(CaptureKindContinuous, kind.GetString());
    }

    // -----------------------------------------------------------------
    // Continuous success payload shape
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousSuccess_HasRequiredFields()
    {
        var payload = BuildContinuousSuccessPayload();
        var root = ParsePayload(payload);

        Assert.Equal(SchemaV2, root.GetProperty("schema_version").GetString());
        Assert.Equal(CaptureKindContinuous, root.GetProperty("capture_kind").GetString());
        Assert.Equal("real", root.GetProperty("mode").GetString());
        Assert.Equal("completed", root.GetProperty("final_status").GetString());
        Assert.True(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.True(root.GetProperty("is_playable_media").GetBoolean());
        Assert.False(root.GetProperty("partial_output_exists").GetBoolean());
        Assert.True(root.TryGetProperty("frames_captured", out _));
        Assert.True(root.TryGetProperty("duration_ms", out _));
        Assert.True(root.TryGetProperty("stop_reason", out _));
    }

    [Fact]
    public void ContinuousSuccess_OutputHasContainerMp4()
    {
        var payload = BuildContinuousSuccessPayload();
        var root = ParsePayload(payload);
        var output = root.GetProperty("output");

        Assert.Equal("mp4", output.GetProperty("container").GetString());
        Assert.Equal("h264", output.GetProperty("codec").GetString());
        Assert.True(output.GetProperty("width").GetInt32() > 0);
        Assert.True(output.GetProperty("height").GetInt32() > 0);
    }

    [Fact]
    public void ContinuousSuccess_FramesCapturedGreaterThanZero()
    {
        var payload = BuildContinuousSuccessPayload();
        var root = ParsePayload(payload);

        var frames = root.GetProperty("frames_captured").GetInt32();
        Assert.True(frames > 0);
    }

    [Fact]
    public void ContinuousSuccess_StopReasonIsValid()
    {
        var payload = BuildContinuousSuccessPayload();
        var root = ParsePayload(payload);

        var stopReason = root.GetProperty("stop_reason").GetString();
        Assert.True(stopReason == "duration_reached" || stopReason == "user_requested");
    }

    // -----------------------------------------------------------------
    // Partial output failure shape
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousPartialFailure_HasPartialOutputExistsTrue()
    {
        var payload = BuildContinuousPartialFailurePayload();
        var root = ParsePayload(payload);

        Assert.Equal("failed", root.GetProperty("final_status").GetString());
        Assert.True(root.GetProperty("partial_output_exists").GetBoolean());
        Assert.False(root.GetProperty("is_playable_media").GetBoolean());
    }

    [Fact]
    public void ContinuousPartialFailure_HasStopReason()
    {
        var payload = BuildContinuousPartialFailurePayload();
        var root = ParsePayload(payload);

        var stopReason = root.GetProperty("stop_reason").GetString();
        Assert.True(stopReason == "cancelled" || stopReason == "error" || stopReason == "timeout");
    }

    [Fact]
    public void ContinuousPartialFailure_HasNonEmptyError()
    {
        var payload = BuildContinuousPartialFailurePayload();
        var root = ParsePayload(payload);

        var error = root.GetProperty("error").GetString();
        Assert.False(string.IsNullOrEmpty(error));
    }

    // -----------------------------------------------------------------
    // Timeout failure shape
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousTimeoutFailure_HasStopReasonTimeout()
    {
        var payload = BuildContinuousTimeoutPayload();
        var root = ParsePayload(payload);

        Assert.Equal("timeout", root.GetProperty("stop_reason").GetString());
        Assert.True(root.GetProperty("partial_output_exists").GetBoolean());
    }

    // -----------------------------------------------------------------
    // Zero frames failure shape
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousZeroFramesFailure_HasZeroFramesCaptured()
    {
        var payload = BuildContinuousZeroFramesPayload();
        var root = ParsePayload(payload);

        Assert.Equal(0, root.GetProperty("frames_captured").GetInt32());
        Assert.Equal("failed", root.GetProperty("final_status").GetString());
        Assert.Contains("zero_frames", root.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------
    // Codec/container mismatch failure
    // -----------------------------------------------------------------

    [Fact]
    public void ContinuousCodecMismatch_HasError()
    {
        var payload = BuildContinuousCodecMismatchPayload();
        var root = ParsePayload(payload);

        Assert.Equal("failed", root.GetProperty("final_status").GetString());
        Assert.Contains("codec", root.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------
    // Still-frame vs continuous distinction
    // -----------------------------------------------------------------

    [Fact]
    public void StillFrameEvidence_DoesNotHaveContinuousFields()
    {
        var payload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T15:00:00Z",
            mode = "real",
            window_hwnd = "1839564",
            window_id = "window_1839564",
            final_status = "completed",
            output = new
            {
                path = "C:\\output.png",
                container = "png",
                codec = "still-frame"
            },
            actual_file_exists = true,
            is_valid_png_signature = true
        };
        var json = Serialize(payload);
        var root = ParsePayload(json);

        Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
        Assert.False(root.TryGetProperty("capture_kind", out _));
        Assert.False(root.TryGetProperty("frames_captured", out _));
        Assert.False(root.TryGetProperty("duration_ms", out _));
    }

    [Fact]
    public void ContinuousEvidence_DistinctFromStillFrame()
    {
        var continuousPayload = BuildContinuousSuccessPayload();
        var continuousRoot = ParsePayload(continuousPayload);

        var stillFramePayload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T15:00:00Z",
            mode = "real",
            window_hwnd = "1839564",
            window_id = "window_1839564",
            final_status = "completed",
            output = new
            {
                path = "C:\\output.png",
                container = "png",
                codec = "still-frame"
            },
            actual_file_exists = true,
            is_valid_png_signature = true
        };
        var stillFrameJson = Serialize(stillFramePayload);
        var stillFrameRoot = ParsePayload(stillFrameJson);

        // Different schema versions
        Assert.NotEqual(continuousRoot.GetProperty("schema_version").GetString(),
                       stillFrameRoot.GetProperty("schema_version").GetString());

        // Continuous has capture_kind
        Assert.True(continuousRoot.TryGetProperty("capture_kind", out _));
        Assert.False(stillFrameRoot.TryGetProperty("capture_kind", out _));

        // Different containers
        Assert.Equal("mp4", continuousRoot.GetProperty("output").GetProperty("container").GetString());
        Assert.Equal("png", stillFrameRoot.GetProperty("output").GetProperty("container").GetString());
    }

    // -----------------------------------------------------------------
    // Helper methods to build payloads
    // -----------------------------------------------------------------

    private static string BuildContinuousSuccessPayload()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:00:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t50_continuous_001",
            confirmation_id = "conf_t50_continuous_001",
            final_status = "completed",
            output = new
            {
                path = "C:\\output\\rec_t50_continuous_001.mp4",
                container = "mp4",
                codec = "h264",
                bytes_written = 15728640L,
                width = 1920,
                height = 1080,
                duration_ms = 30000L,
                fps = 30,
                frames_captured = 900,
                frames_dropped = 0,
                capture_method = "WGC_D3D11_FRAME_STREAM"
            },
            actual_file_exists = true,
            actual_file_size = 15728640L,
            is_playable_media = true,
            partial_output_exists = false,
            duration_ms = 30000L,
            frames_captured = 900,
            frames_dropped = 0,
            stop_reason = "duration_reached",
            warnings = new string[] { },
            error = "",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t50_continuous_001" },
                new { @event = "confirmation.approved", confirmation_id = "conf_t50_continuous_001", recording_id = "rec_t50_continuous_001" },
                new { @event = "recording.backend_selected", backend = "wgc", recording_id = "rec_t50_continuous_001" },
                new { @event = "recording.session_started", recording_id = "rec_t50_continuous_001" },
                new { @event = "recording.completed", recording_id = "rec_t50_continuous_001" }
            }
        };
        return Serialize(payload);
    }

    private static string BuildContinuousPartialFailurePayload()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:05:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t50_partial_001",
            confirmation_id = "conf_t50_partial_001",
            final_status = "failed",
            output = new
            {
                path = "C:\\output\\rec_t50_partial_001.mp4",
                container = "mp4",
                codec = "h264",
                bytes_written = 5242880L,
                width = 1920,
                height = 1080,
                duration_ms = 10000L,
                fps = 30,
                frames_captured = 300,
                frames_dropped = 0,
                capture_method = "WGC_D3D11_FRAME_STREAM"
            },
            actual_file_exists = true,
            actual_file_size = 5242880L,
            is_playable_media = false,
            partial_output_exists = true,
            duration_ms = 10000L,
            frames_captured = 300,
            frames_dropped = 0,
            stop_reason = "cancelled",
            warnings = new[] { "session cancelled by user" },
            error = "session_cancelled: user requested stop during capture",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t50_partial_001" },
                new { @event = "confirmation.approved", confirmation_id = "conf_t50_partial_001", recording_id = "rec_t50_partial_001" },
                new { @event = "recording.backend_selected", backend = "wgc", recording_id = "rec_t50_partial_001" },
                new { @event = "recording.session_started", recording_id = "rec_t50_partial_001" },
                new { @event = "recording.cancelled", recording_id = "rec_t50_partial_001", reason = "user_requested" }
            }
        };
        return Serialize(payload);
    }

    private static string BuildContinuousTimeoutPayload()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:10:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t50_timeout_001",
            confirmation_id = "conf_t50_timeout_001",
            final_status = "failed",
            output = new
            {
                path = "C:\\output\\rec_t50_timeout_001.mp4",
                container = "mp4",
                codec = "h264",
                bytes_written = 2097152L,
                width = 1920,
                height = 1080,
                duration_ms = 5000L,
                fps = 30,
                frames_captured = 150,
                frames_dropped = 0,
                capture_method = "WGC_D3D11_FRAME_STREAM"
            },
            actual_file_exists = true,
            actual_file_size = 2097152L,
            is_playable_media = false,
            partial_output_exists = true,
            duration_ms = 5000L,
            frames_captured = 150,
            frames_dropped = 0,
            stop_reason = "timeout",
            warnings = new[] { "session timed out" },
            error = "session_timeout: helper did not complete within timeout",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t50_timeout_001" },
                new { @event = "recording.session_started", recording_id = "rec_t50_timeout_001" },
                new { @event = "recording.failed", recording_id = "rec_t50_timeout_001", error_code = "timeout" }
            }
        };
        return Serialize(payload);
    }

    private static string BuildContinuousZeroFramesPayload()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:15:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t50_zero_001",
            confirmation_id = "conf_t50_zero_001",
            final_status = "failed",
            output = new
            {
                path = "",
                container = "mp4",
                codec = "h264",
                bytes_written = 0L,
                width = 0,
                height = 0,
                duration_ms = 0L,
                fps = 30,
                frames_captured = 0,
                frames_dropped = 0,
                capture_method = "WGC_D3D11_FRAME_STREAM"
            },
            actual_file_exists = false,
            actual_file_size = 0L,
            is_playable_media = false,
            partial_output_exists = false,
            duration_ms = 0L,
            frames_captured = 0,
            frames_dropped = 0,
            stop_reason = "error",
            warnings = new string[] { },
            error = "zero_frames_captured: no frames were captured during session",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t50_zero_001" },
                new { @event = "recording.session_started", recording_id = "rec_t50_zero_001" },
                new { @event = "recording.failed", recording_id = "rec_t50_zero_001", error_code = "zero_frames" }
            }
        };
        return Serialize(payload);
    }

    private static string BuildContinuousCodecMismatchPayload()
    {
        var payload = new
        {
            schema_version = SchemaV2,
            timestamp = "2026-06-21T15:20:00Z",
            mode = "real",
            capture_kind = CaptureKindContinuous,
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t50_codec_001",
            confirmation_id = "conf_t50_codec_001",
            final_status = "failed",
            output = new
            {
                path = "",
                container = "mp4",
                codec = "h264",
                bytes_written = 0L,
                width = 0,
                height = 0,
                duration_ms = 0L,
                fps = 30,
                frames_captured = 0,
                frames_dropped = 0,
                capture_method = "WGC_D3D11_FRAME_STREAM"
            },
            actual_file_exists = false,
            actual_file_size = 0L,
            is_playable_media = false,
            partial_output_exists = false,
            duration_ms = 0L,
            frames_captured = 0,
            frames_dropped = 0,
            stop_reason = "error",
            warnings = new string[] { },
            error = "codec_mismatch: requested codec h264 is not supported for this capture method",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t50_codec_001" },
                new { @event = "recording.failed", recording_id = "rec_t50_codec_001", error_code = "codec_mismatch" }
            }
        };
        return Serialize(payload);
    }
}
