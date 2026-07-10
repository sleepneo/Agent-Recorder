using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Xunit;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies FFmpeg region backend populates CommandArgs correctly
/// and that the recording.started audit contains the actual ffmpeg_args.
/// </summary>
public class FfmpegRegionCommandArgsTests
{
    private static readonly string FfmpegExe = Path.Combine(
        TestHelper.ProjectRoot, "tools", "ffmpeg", "bin", "ffmpeg.exe");

    // -------------------------------------------------------------------------
    // Test 1: BuildArgs produces correct flags for region source
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildArgs_Region_HasRequiredFlags()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (1138, 341, 1592, 892),
            Fps = 15,
            Quality = "medium",
            DurationSeconds = 30,
            OutputPath = Path.Combine(Path.GetTempPath(), "test-output.mp4")
        };

        // Use reflection to call private static BuildArgs
        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.NotEmpty(args);
        Assert.Contains("-f gdigrab", args);
        Assert.Contains("-offset_x 1138", args);
        Assert.Contains("-offset_y 341", args);
        Assert.Contains("-video_size 1592x892", args);
        Assert.Contains("-i desktop", args);
    }

    [Fact]
    public void BuildArgs_Region_DoesNotHaveTitleFlag()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (100, 200, 800, 600),
            Fps = 15,
            Quality = "medium",
            OutputPath = Path.Combine(Path.GetTempPath(), "test-output.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.DoesNotContain("-i title=", args);
    }

    [Fact]
    public void BuildArgs_Window_HasDesktopRegionFlags()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "window",
            WindowTitle = "Test Window",
            WindowHandle = new nint(0x1234),
            Bounds = (100, 200, 800, 600),
            Fps = 30,
            Quality = "medium",
            DurationSeconds = 10,
            OutputPath = Path.Combine(Path.GetTempPath(), "test-window-output.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.NotEmpty(args);
        Assert.Contains("-f gdigrab", args);
        Assert.Contains("-offset_x 100", args);
        Assert.Contains("-offset_y 200", args);
        Assert.Contains("-video_size 800x600", args);
        Assert.Contains("-i desktop", args);
    }

    [Fact]
    public void BuildArgs_Window_DoesNotHaveTitleFlag()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "window",
            WindowTitle = "Test Window",
            WindowHandle = new nint(0x1234),
            Bounds = (100, 200, 800, 600),
            Fps = 15,
            Quality = "medium",
            OutputPath = Path.Combine(Path.GetTempPath(), "test-window-no-title.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.DoesNotContain("-i title=", args);
        Assert.DoesNotContain("title=\"", args);
    }

    [Fact]
    public void BuildArgs_Window_NegativeOffset_GeneratesCorrectOffsetFlags()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "window",
            WindowTitle = "Negative Coord Window",
            WindowHandle = new nint(0x5678),
            Bounds = (-1920, 100, 1280, 720),
            Fps = 24,
            Quality = "high",
            DurationSeconds = 60,
            OutputPath = Path.Combine(Path.GetTempPath(), "test-window-neg.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.Contains("-offset_x -1920", args);
        Assert.Contains("-offset_y 100", args);
        Assert.Contains("-video_size 1280x720", args);
        Assert.Contains("-i desktop", args);
    }

    [Fact]
    public void BuildArgs_Window_HasDurationFlag()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "window",
            WindowTitle = "Duration Test",
            WindowHandle = new nint(0xABCD),
            Bounds = (0, 0, 640, 480),
            Fps = 30,
            DurationSeconds = 25,
            OutputPath = Path.Combine(Path.GetTempPath(), "test-window-dur.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.Contains("-t 25", args);
    }

    [Fact]
    public void BuildArgs_Window_ClampedBounds_NoNegativeOffsetBeyondDesktop()
    {
        // Simulates a maximized window after clamping: original (-13,-13,3866,2090)
        // clamped to (0,0,3854,2088) on a 6400x3220 desktop
        var cfg = new CaptureConfig
        {
            SourceKind = "window",
            WindowTitle = "Maximized Window",
            WindowHandle = new nint(0x1234),
            Bounds = (0, 0, 3854, 2088),
            Fps = 30,
            DurationSeconds = 10,
            OutputPath = Path.Combine(Path.GetTempPath(), "test-window-clamped.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.Contains("-offset_x 0", args);
        Assert.Contains("-offset_y 0", args);
        Assert.Contains("-video_size 3854x2088", args);
        Assert.Contains("-i desktop", args);
        Assert.DoesNotContain("-i title=", args);
    }

    [Fact]
    public void BuildArgs_Region_NegativeOffset_GeneratesCorrectOffsetFlags()
    {
        // Simulates a region on a negative-coordinate display
        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (-2560, 0, 1920, 1080),
            Fps = 15,
            Quality = "medium",
            OutputPath = Path.Combine(Path.GetTempPath(), "test-output.mp4")
        };

        var method = typeof(FfmpegCaptureBackend).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = (string)method.Invoke(null, new object[] { cfg })!;

        Assert.Contains("-offset_x -2560", args);
        Assert.Contains("-offset_y 0", args);
        Assert.Contains("-video_size 1920x1080", args);
        Assert.Contains("-i desktop", args);
    }

    // -------------------------------------------------------------------------
    // Test 2: Start() populates cfg.CommandArgs for region source
    // -------------------------------------------------------------------------

    [Fact]
    public void Start_PopulatesCommandArgs_ForRegionSource()
    {
        if (!File.Exists(FfmpegExe)) return; // skip if FFmpeg not present

        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (0, 0, 640, 480),
            Fps = 15,
            Quality = "medium",
            OutputPath = Path.Combine(Path.GetTempPath(), $"test-region-{Guid.NewGuid()}.mp4")
        };

        var backend = new FfmpegCaptureBackend();
        try
        {
            backend.Start(cfg);

            // CommandArgs should be populated immediately after Start()
            Assert.NotEmpty(cfg.CommandArgs);
            Assert.Contains("-f gdigrab", cfg.CommandArgs);
            Assert.Contains("-offset_x", cfg.CommandArgs);
            Assert.Contains("-offset_y", cfg.CommandArgs);
            Assert.Contains("-video_size", cfg.CommandArgs);
            Assert.Contains("-i desktop", cfg.CommandArgs);
            Assert.DoesNotContain("-i title=", cfg.CommandArgs);
        }
        finally
        {
            backend.Stop();
            try
            {
                if (File.Exists(cfg.OutputPath)) File.Delete(cfg.OutputPath);
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Test 3: Audit log entry for recording.started contains non-empty ffmpeg_args
    // -------------------------------------------------------------------------

    [Fact]
    public void StartCapture_RecordsFfmpegArgs_InAudit()
    {
        if (!File.Exists(FfmpegExe)) return; // skip if FFmpeg not present

        // Create an in-memory audit logger
        var capturedEntries = new List<(string evt, object payload)>();
        var testAudit = new TestAuditLogger(capturedEntries);
        var engine = new RecordingEngine(testAudit);

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg-region",
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (0, 0, 640, 480),
                Fps = 15,
                Quality = "medium",
                OutputPath = Path.Combine(Path.GetTempPath(), $"test-audit-{Guid.NewGuid()}.mp4")
            }
        };

        // Use StartCaptureForTests which registers the recording and calls StartCapture
        var tray = new NoOpTrayContext();
        try
        {
            engine.StartCaptureForTests(rec, tray);
        }
        catch
        {
            // FFmpeg may fail to start in test environment; we only care about audit
        }

        // Find the recording.started audit entry
        var startedEntry = capturedEntries.Find(e => e.evt == "recording.started");
        Assert.True(startedEntry != default, "recording.started audit entry not found");

        // Verify ffmpeg_args is non-empty
        var payloadType = startedEntry.payload.GetType();
        var argsProp = payloadType.GetProperty("ffmpeg_args");
        Assert.NotNull(argsProp);
        var ffmpegArgs = argsProp.GetValue(startedEntry.payload) as string;
        Assert.NotNull(ffmpegArgs);
        Assert.NotEmpty(ffmpegArgs);
        Assert.Contains("-f gdigrab", ffmpegArgs);
        Assert.Contains("-offset_x", ffmpegArgs);
        Assert.Contains("-offset_y", ffmpegArgs);
        Assert.Contains("-video_size", ffmpegArgs);
        Assert.Contains("-i desktop", ffmpegArgs);

        // Clean up
        try { rec.Backend?.Stop(); } catch { }
        try
        {
            if (File.Exists(rec.OutputPath)) File.Delete(rec.OutputPath);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Test 4: GetOutput returns command_args, backend, source_type
    // -------------------------------------------------------------------------

    [Fact]
    public void GetOutput_Full_ReturnsCommandArgs_Backend_SourceType()
    {
        if (!File.Exists(FfmpegExe)) return; // skip if FFmpeg not present

        var testAudit = new TestAuditLogger(new List<(string, object)>());
        var engine = new RecordingEngine(testAudit);

        var outputPath = Path.Combine(Path.GetTempPath(), $"test-output-{Guid.NewGuid()}.mp4");
        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg-region",
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (100, 200, 800, 600),
                Fps = 15,
                Quality = "medium",
                OutputPath = outputPath
            }
        };

        var tray = new NoOpTrayContext();
        try
        {
            engine.StartCaptureForTests(rec, tray);
        }
        catch
        {
            // FFmpeg may fail; we still want to verify GetOutput structure
        }

        // GetOutput returns an object with recording_id, output, warnings, stderr_excerpt.
        // The 'output' sub-object contains command_args, backend, source_type.
        var result = engine.GetOutput(rec.Id);
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var output = document.RootElement.GetProperty("output");

        Assert.True(output.TryGetProperty("command_args", out var commandArgs),
            "command_args field missing from output");
        Assert.False(string.IsNullOrWhiteSpace(commandArgs.GetString()));

        Assert.Equal("ffmpeg-region", output.GetProperty("backend").GetString());
        Assert.Equal("region", output.GetProperty("source_type").GetString());

        // Clean up
        try { rec.Backend?.Stop(); } catch { }
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private sealed class NoOpTrayContext : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class TestAuditLogger : AuditLogger
    {
        private readonly List<(string evt, object payload)> _entries;
        public TestAuditLogger(List<(string evt, object payload)> entries) => _entries = entries;
        public override void Log(string evt, object payload) => _entries.Add((evt, payload));
    }
}
