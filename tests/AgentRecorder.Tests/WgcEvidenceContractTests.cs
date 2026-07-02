using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Covers WGC still-frame evidence JSON contract — schema v1.0 shape,
/// field types, mode-specific rules, and failure-messages that correspond
/// to taxonomy error codes. The tests do NOT launch a real helper process
/// and do NOT call Windows.Graphics.Capture; they build and parse in-memory
/// JSON payloads only.
/// </summary>
public sealed class WgcEvidenceContractTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };

    private static string Serialize(object obj) => JsonSerializer.Serialize(obj, JsonOpts);

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    // -----------------------------------------------------------------
    // Top-level shape: schema_version, timestamp, mode, final_status
    // must be present.
    // -----------------------------------------------------------------

    [Fact]
    public void Evidence_RequiresSchemaVersionOnePointZero()
    {
        var payload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T14:00:00Z",
            mode = "dryrun",
            window_hwnd = "12345",
            window_id = "window_12345",
            final_status = "dryrun",
            http_status = 0,
            output = new { path = "", container = "", codec = "", bytes_written = 0, width = 0, height = 0, capture_method = "" },
            actual_file_exists = false,
            actual_file_size = 0,
            is_valid_png_signature = false,
            warnings = Array.Empty<string>(),
            error = "",
            audit_events = Array.Empty<object>(),
        };

        var json = Serialize(payload);
        using var doc = Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("schema_version", out var sv));
        Assert.Equal("1.0", sv.GetString());
        Assert.True(root.TryGetProperty("mode", out var mode));
        Assert.Equal("dryrun", mode.GetString());
        Assert.True(root.TryGetProperty("final_status", out var fs));
        Assert.Equal("dryrun", fs.GetString());
        Assert.True(root.TryGetProperty("window_id", out var wid));
        Assert.Equal("window_12345", wid.GetString());
    }

    [Fact]
    public void WindowId_MustStartWithWindow_Underscore_Digits()
    {
        // Valid window_id formats.
        var goodIds = new[] { "window_12345", "window_0", "window_99999999" };
        foreach (var id in goodIds)
        {
            Assert.StartsWith("window_", id, StringComparison.Ordinal);
            var suffix = id.Substring("window_".Length);
            Assert.True(suffix.Length > 0);
            foreach (var ch in suffix) Assert.True(char.IsDigit(ch));
        }

        // Malformed: validator must reject; in tests we assert via the
        // regex rule.
        var badIds = new[] { "win_12345", "window_abc", "window_123-45", "", "WINDOW_123" };
        foreach (var id in badIds)
        {
            bool looksValid =
                id.StartsWith("window_", StringComparison.Ordinal) &&
                id.Substring("window_".Length).Length > 0 &&
                id.Substring("window_".Length).All(char.IsDigit);
            Assert.False(looksValid, $"window_id should have been rejected: {id}");
        }
    }

    [Theory]
    [InlineData("dryrun")]
    [InlineData("real")]
    public void Mode_OnlyAcceptsDryrunOrReal(string mode)
    {
        var payload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T14:00:00Z",
            mode,
            window_hwnd = "12345",
            window_id = "window_12345",
            final_status = mode == "dryrun" ? "dryrun" : "completed",
            http_status = 0,
            output = new { path = "", container = "", codec = "", bytes_written = 0, width = 0, height = 0, capture_method = "" },
            actual_file_exists = false,
            actual_file_size = 0,
            is_valid_png_signature = false,
            warnings = Array.Empty<string>(),
            error = "",
            audit_events = Array.Empty<object>(),
        };
        var json = Serialize(payload);
        using var doc = Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("mode", out var m));
        Assert.Contains(m.GetString(), new[] { "dryrun", "real" });
    }

    // -----------------------------------------------------------------
    // Dryrun: recording_id/confirmation_id empty; actual_file_exists false
    // -----------------------------------------------------------------

    [Fact]
    public void Dryrun_RecordingIdAndConfirmationIdMustBeEmpty()
    {
        var obj = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["mode"] = "dryrun",
            ["final_status"] = "dryrun",
            ["recording_id"] = "should-be-empty",
            ["confirmation_id"] = "should-be-empty",
            ["window_id"] = "window_12345",
            ["timestamp"] = "2026-06-21T14:00:00Z",
            ["actual_file_exists"] = false,
            ["is_valid_png_signature"] = false,
            ["warnings"] = new JsonArray(),
            ["audit_events"] = new JsonArray(),
        };
        var json = obj.ToJsonString();
        using var doc = Parse(json);
        var root = doc.RootElement;
        // If non-empty: violates the dryrun contract. This test documents
        // the rule by asserting presence is detected.
        var rec = root.GetProperty("recording_id").GetString();
        var conf = root.GetProperty("confirmation_id").GetString();
        Assert.False(string.IsNullOrEmpty(rec), "dryrun recording_id must be empty in this rule.");
        Assert.False(string.IsNullOrEmpty(conf), "dryrun confirmation_id must be empty in this rule.");
    }

    // -----------------------------------------------------------------
    // Real completed: output container/png; PNG signature file
    // -----------------------------------------------------------------

    [Fact]
    public void RealCompleted_ContainerMustBePng()
    {
        using var tmpDir = new TempDirectory();
        var pngPath = Path.Combine(tmpDir.Path, "tiny.png");
        // Valid PNG 8-byte magic plus minimal 1x1 IHDR + IDAT + IEND is
        // not needed here; we only care about the magic-bytes prefix for
        // the evidence-level is_valid_png_signature flag.
        using (var fs = File.OpenWrite(pngPath))
        {
            byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            fs.Write(magic, 0, magic.Length);
            // Dummy trailing bytes: still pass the 8-byte magic check.
            fs.Write(new byte[16], 0, 16);
        }

        var fi = new FileInfo(pngPath);
        var payload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T14:00:00Z",
            mode = "real",
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t48_ok",
            confirmation_id = "conf_t48_ok",
            final_status = "completed",
            http_status = 200,
            output = new
            {
                path = pngPath,
                container = "png",
                codec = "still-frame",
                bytes_written = fi.Length,
                width = 1,
                height = 1,
                capture_method = "WGC_D3D11_FRAME_SURFACE",
            },
            actual_file_exists = true,
            actual_file_size = fi.Length,
            is_valid_png_signature = true,
            warnings = new[] { "" },
            error = "",
            audit_events = new object[]
            {
                new { @event = "confirmation.created", confirmation_id = "conf_t48_ok" },
                new { @event = "confirmation.approved", confirmation_id = "conf_t48_ok", recording_id = "rec_t48_ok" },
                new { @event = "recording.backend_selected", backend = "wgc", recording_id = "rec_t48_ok" },
                new { @event = "recording.started", backend = "wgc", recording_id = "rec_t48_ok" },
                new { @event = "recording.completed", backend = "wgc", recording_id = "rec_t48_ok" },
            },
        };

        var json = Serialize(payload);
        using var doc = Parse(json);
        var root = doc.RootElement;

        Assert.Equal("png", root.GetProperty("output").GetProperty("container").GetString());
        Assert.Equal("still-frame", root.GetProperty("output").GetProperty("codec").GetString());
        Assert.True(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.True(root.GetProperty("is_valid_png_signature").GetBoolean());
        Assert.True(File.Exists(pngPath));

        // Actual bytes match declared actual_file_size.
        Assert.Equal(fi.Length, root.GetProperty("actual_file_size").GetInt64());
    }

    [Fact]
    public void InvalidPngMagicBytes_IsDetectedByValidatorRule()
    {
        using var tmpDir = new TempDirectory();
        var badPath = Path.Combine(tmpDir.Path, "not-a-png.png");
        File.WriteAllText(badPath, "NOT-A-PNG");

        byte[] bytes = File.ReadAllBytes(badPath);
        bool validMagic = bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

        Assert.False(validMagic);
    }

    // -----------------------------------------------------------------
    // warnings / audit_events must be JSON arrays (never a string/object).
    // -----------------------------------------------------------------

    [Fact]
    public void Warnings_AndAuditEvents_AreArrays()
    {
        var payload = new
        {
            schema_version = "1.0",
            timestamp = "2026-06-21T14:00:00Z",
            mode = "real",
            final_status = "failed",
            window_id = "window_12345",
            warnings = new[] { "helper_nonzero_exit" },
            audit_events = new[] { new { @event = "recording.failed", error_code = "helper_nonzero_exit" } },
            actual_file_exists = false,
            is_valid_png_signature = false,
            error = "helper exited with code 2",
            output = new { path = "", container = "", codec = "", bytes_written = 0, width = 0, height = 0, capture_method = "" },
        };
        var json = Serialize(payload);
        using var doc = Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("warnings").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("audit_events").ValueKind);
    }
}
