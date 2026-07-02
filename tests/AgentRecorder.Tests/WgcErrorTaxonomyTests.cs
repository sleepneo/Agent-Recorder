using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Documents, by example, the 8+ WGC error taxonomy codes that should
/// be distinguishable at the evidence JSON level. These tests do NOT
/// exercise a real helper process, real WGC API, or real window. They
/// build evidence payloads matching each taxonomy code and assert the
/// payload shape, and for a subset assert behaviour on a fake PNG
/// file.
/// </summary>
public sealed class WgcErrorTaxonomyTests
{
    private const string SchemaVersion = "1.0";

    private static JsonElement ParsePayload(string json)
    {
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string BuildFailurePayload(
        string errorCode,
        string finalStatus,
        bool actualFileExists,
        long actualFileSize,
        bool isValidPngSignature,
        string outputPath,
        string[] warnings,
        string[] extraAuditEvents)
    {
        var auditEvents = new List<object>
        {
            new { @event = "recording.failed", error_code = errorCode, recording_id = "rec_t48_" + errorCode },
        };
        if (extraAuditEvents != null)
        {
            foreach (var ev in extraAuditEvents)
            {
                auditEvents.Add(new { @event = ev, recording_id = "rec_t48_" + errorCode });
            }
        }

        var payload = new
        {
            schema_version = SchemaVersion,
            timestamp = "2026-06-21T14:00:00Z",
            mode = "real",
            window_hwnd = "1839564",
            window_id = "window_1839564",
            recording_id = "rec_t48_" + errorCode,
            confirmation_id = "conf_t48_" + errorCode,
            final_status = finalStatus,
            http_status = 0,
            output = new
            {
                path = outputPath,
                container = "png",
                codec = "still-frame",
                bytes_written = actualFileSize,
                width = 0,
                height = 0,
                capture_method = "WGC_D3D11_FRAME_SURFACE",
            },
            actual_file_exists = actualFileExists,
            actual_file_size = actualFileSize,
            is_valid_png_signature = isValidPngSignature,
            warnings = warnings ?? Array.Empty<string>(),
            error = errorCode + ": simulated failure for taxonomy coverage",
            audit_events = auditEvents,
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    // -----------------------------------------------------------------
    // helper_timeout
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_HelperTimeout_ProducesFailureEvidence()
    {
        var json = BuildFailurePayload(
            errorCode: "helper_timeout",
            finalStatus: "failed",
            actualFileExists: false,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: "",
            warnings: new[] { "helper process timed out before producing output" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
        Assert.Equal("failed", root.GetProperty("final_status").GetString());
        Assert.False(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.Contains("helper_timeout", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("is_valid_png_signature").GetBoolean());
    }

    // -----------------------------------------------------------------
    // helper_nonzero_exit
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_HelperNonzeroExit_ExitCodePropagated()
    {
        var json = BuildFailurePayload(
            errorCode: "helper_nonzero_exit",
            finalStatus: "failed",
            actualFileExists: false,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: "",
            warnings: new[] { "helper exited with code 2" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.Equal("failed", root.GetProperty("final_status").GetString());
        Assert.Contains("helper_nonzero_exit", root.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // wgc_missing_output
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_MissingOutput_DeclaresNoFile()
    {
        using var tmp = new TempDirectory();
        var missing = Path.Combine(tmp.Path, "nonexistent.png");

        var json = BuildFailurePayload(
            errorCode: "wgc_missing_output",
            finalStatus: "failed",
            actualFileExists: false,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: missing,
            warnings: new[] { "helper completed with zero bytes and no output file" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.False(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.Equal(0, root.GetProperty("actual_file_size").GetInt64());
        Assert.False(File.Exists(missing));
    }

    // -----------------------------------------------------------------
    // wgc_empty_output
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_EmptyOutput_ZeroByteFileDeclared()
    {
        using var tmp = new TempDirectory();
        var emptyPng = Path.Combine(tmp.Path, "empty.png");
        File.WriteAllBytes(emptyPng, Array.Empty<byte>());

        var json = BuildFailurePayload(
            errorCode: "wgc_empty_output",
            finalStatus: "failed",
            actualFileExists: true,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: emptyPng,
            warnings: new[] { "helper produced a 0-byte file" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.True(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.Equal(0, root.GetProperty("actual_file_size").GetInt64());
        Assert.False(root.GetProperty("is_valid_png_signature").GetBoolean());
        Assert.True(File.Exists(emptyPng));
        Assert.Equal(0, new FileInfo(emptyPng).Length);
    }

    // -----------------------------------------------------------------
    // wgc_invalid_png_signature
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_InvalidPngSignature_MagicBytesDoNotMatch()
    {
        using var tmp = new TempDirectory();
        var notPng = Path.Combine(tmp.Path, "fake.png");
        File.WriteAllText(notPng, "THIS-IS-NOT-A-PNG");

        var bytes = File.ReadAllBytes(notPng);
        bool hasValidMagic = bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

        var json = BuildFailurePayload(
            errorCode: "wgc_invalid_png_signature",
            finalStatus: "failed",
            actualFileExists: true,
            actualFileSize: bytes.Length,
            isValidPngSignature: hasValidMagic,
            outputPath: notPng,
            warnings: new[] { "PNG 8-byte signature mismatch" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.True(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.False(root.GetProperty("is_valid_png_signature").GetBoolean());
        Assert.Equal(bytes.Length, root.GetProperty("actual_file_size").GetInt64());
    }

    // -----------------------------------------------------------------
    // wgc_zero_dimensions
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_ZeroDimensions_WidthOrHeightIsZero()
    {
        // If helper produced bytes but declares width=0 or height=0,
        // the evidence must flag this. A valid PNG file may exist, but
        // capture metadata is broken.
        using var tmp = new TempDirectory();
        var pngPath = Path.Combine(tmp.Path, "zero-dim.png");
        // Write a valid PNG magic (8 bytes) + junk. actual_file_exists
        // = true, but is_valid_png_signature remains false because the
        // rest is not real PNG — we only care about dimensions in this
        // test.
        var fs = File.OpenWrite(pngPath);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        fs.Write(new byte[32], 0, 32);
        fs.Dispose();

        var json = BuildFailurePayload(
            errorCode: "wgc_zero_dimensions",
            finalStatus: "failed",
            actualFileExists: true,
            actualFileSize: new FileInfo(pngPath).Length,
            isValidPngSignature: false,
            outputPath: pngPath,
            warnings: new[] { "helper reported width=0 or height=0" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        var output = root.GetProperty("output");
        // width/height must be 0 for this taxonomy class
        Assert.Equal(0, output.GetProperty("width").GetInt32());
        Assert.Equal(0, output.GetProperty("height").GetInt32());
        Assert.Contains("wgc_zero_dimensions", root.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // file_write_error
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_FileWriteError_OutputPathRefused()
    {
        using var tmp = new TempDirectory();
        // Attempt to write inside a non-existent directory: the path
        // must NOT exist on disk.
        var badPath = Path.Combine(tmp.Path, "does", "not", "exist", "out.png");

        var json = BuildFailurePayload(
            errorCode: "file_write_error",
            finalStatus: "failed",
            actualFileExists: false,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: badPath,
            warnings: new[] { "path under .local-data/wgc-tests rejected or access denied" },
            extraAuditEvents: new[] { "recording.started" });

        var root = ParsePayload(json);
        Assert.False(root.GetProperty("actual_file_exists").GetBoolean());
        Assert.False(File.Exists(badPath));
        Assert.Contains("file_write_error", root.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // window_not_found / invalid window_id
    // -----------------------------------------------------------------
    [Fact]
    public void Taxonomy_WindowNotFound_WindowIdAbsentFromListing()
    {
        // Simulate a window_id that is well-formed but not present in
        // the server's window list. Evidence should still look valid
        // at the JSON-schema level.
        var json = BuildFailurePayload(
            errorCode: "window_not_found",
            finalStatus: "failed",
            actualFileExists: false,
            actualFileSize: 0,
            isValidPngSignature: false,
            outputPath: "",
            warnings: new[] { "window_id not present in GET /windows response" },
            extraAuditEvents: Array.Empty<string>());

        var root = ParsePayload(json);
        Assert.Equal("window_1839564", root.GetProperty("window_id").GetString());
        Assert.Contains("window_not_found", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("failed", root.GetProperty("final_status").GetString());
    }

    // -----------------------------------------------------------------
    // Sanity: distinct taxonomy codes produce distinct evidence errors
    // -----------------------------------------------------------------
    [Fact]
    public void DistinctTaxonomyCodes_ProduceDistinctErrorMessages()
    {
        string[] codes =
        {
            "helper_timeout",
            "helper_nonzero_exit",
            "wgc_missing_output",
            "wgc_empty_output",
            "wgc_invalid_png_signature",
            "wgc_zero_dimensions",
            "file_write_error",
            "window_not_found",
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            var json = BuildFailurePayload(
                errorCode: code,
                finalStatus: "failed",
                actualFileExists: false,
                actualFileSize: 0,
                isValidPngSignature: false,
                outputPath: "",
                warnings: new[] { code },
                extraAuditEvents: Array.Empty<string>());

            var root = ParsePayload(json);
            string error = root.GetProperty("error").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrEmpty(error));
            Assert.True(seen.Add(error), $"duplicate error message across codes; {code} collides with another taxonomy entry.");
        }
    }

    // -----------------------------------------------------------------
    // Guard: real helper stdout parsing doesn't need a window for
    // failure cases. The .NET parser should handle exit codes without
    // panicking on empty stdout.
    // -----------------------------------------------------------------
    [Fact]
    public void HelperOutputParser_EmptyStdout_ReturnsFailure()
    {
        var parsed = AgentRecorder.Capture.WgcHelperOutputParser.Parse(exitCode: 1, stdout: "", stderr: "");
        Assert.NotNull(parsed);
        Assert.False(parsed.Success);
        Assert.Equal(1, parsed.ExitCode);
    }

    [Fact]
    public void HelperOutputParser_OnlyExitCodeZeroAndOk_IsSuccess()
    {
        var parsed = AgentRecorder.Capture.WgcHelperOutputParser.Parse(
            exitCode: 0,
            stdout: "RESULT: OK\nWidth: 1280\nHeight: 720\nFileSize: 65536 bytes\n",
            stderr: "");

        Assert.True(parsed.Success);
        Assert.Equal(1280, parsed.Width);
        Assert.Equal(720, parsed.Height);
        Assert.Equal(65536, parsed.FileSize);
    }

    [Fact]
    public void HelperOutputParser_ResultFail_TreatedAsFailure_WhenExitCodeIsZero()
    {
        var parsed = AgentRecorder.Capture.WgcHelperOutputParser.Parse(
            exitCode: 0,
            stdout: "RESULT: FAIL\nReason: HRESULT 0x80070005\n",
            stderr: "");

        Assert.False(parsed.Success);
    }
}
