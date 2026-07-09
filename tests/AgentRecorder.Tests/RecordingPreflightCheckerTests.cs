using System;
using System.IO;
using System.Linq;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Direct tests for <see cref="RecordingPreflightChecker"/> using its provider
/// seams so failures can be injected without depending on real disk space,
/// missing FFmpeg binaries, or live windows.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class RecordingPreflightCheckerTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly RecordingPreflightChecker.TryGetFreeSpace _originalFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _originalEncoder;
    private readonly Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>? _originalWindowProvider;
    private readonly Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>? _originalDisplayProvider;

    public RecordingPreflightCheckerTests()
    {
        _originalFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _originalEncoder = RecordingPreflightChecker.EncoderProvider;
        _originalWindowProvider = GetWindowProviderField();
        _originalDisplayProvider = GetDisplayProviderField();

        // Default safe providers for most tests.
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024; // 10 GB
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = Path.Combine(_tmp.Path, "ffmpeg.exe");
            p = Path.Combine(_tmp.Path, "ffprobe.exe");
            File.WriteAllText(f, "fake ffmpeg");
            File.WriteAllText(p, "fake ffprobe");
            return true;
        };

        SystemQuery.SetDisplayProvider(() => new()
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        RecordingPreflightChecker.FreeSpaceProvider = _originalFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _originalEncoder;
        SystemQuery.SetWindowProvider(_originalWindowProvider);
        SystemQuery.SetDisplayProvider(_originalDisplayProvider);
        _tmp.Dispose();
    }

    private static Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>? GetWindowProviderField()
    {
        // Access private static field via reflection to allow restoration.
        var field = typeof(SystemQuery).GetField("_windowProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>?)field?.GetValue(null);
    }

    private static Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>? GetDisplayProviderField()
    {
        var field = typeof(SystemQuery).GetField("_displayProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>?)field?.GetValue(null);
    }

    private static Recording DisplayRecording(string outputPath, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "display",
            SourceTitle = "Display 1",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            Config = new AgentRecorder.Capture.CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 1920, 1080),
                OutputPath = outputPath,
                DurationSeconds = durationSeconds
            }
        };
    }

    private static Recording WindowRecording(string outputPath, nint hwnd, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "window",
            SourceTitle = $"Test Window (window_{hwnd.ToInt64()})",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            Config = new AgentRecorder.Capture.CaptureConfig
            {
                SourceKind = "window",
                Bounds = (0, 0, 1280, 720),
                OutputPath = outputPath,
                DurationSeconds = durationSeconds,
                WindowHandle = hwnd
            }
        };
    }

    [Fact]
    public void CheckBeforeConfirmation_OutputDirectoryWritable_PassesAndCleansTempFile()
    {
        var outputPath = Path.Combine(_tmp.Path, "videos", "out.mp4");
        var rec = DisplayRecording(outputPath);

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.True(result.Passed);
        var dir = Path.Combine(_tmp.Path, "videos");
        Assert.True(Directory.Exists(dir));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir), f => Path.GetFileName(f).StartsWith(".agent-recorder-preflight-"));
    }

    [Fact]
    public void CheckBeforeConfirmation_InsufficientDiskSpace_Fails()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecording(outputPath, durationSeconds: 60);
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024; // 10 MB
            return true;
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("INSUFFICIENT_DISK_SPACE", result.ErrorCode);
        Assert.Equal("free_disk_space_or_choose_another_directory", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeConfirmation_EncoderUnavailable_Fails()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecording(outputPath);
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = null;
            p = null;
            return false;
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("ENCODER_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeStart_WindowDisappeared_FailsSourceNotFound()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = WindowRecording(outputPath, new nint(12345));
        SystemQuery.SetWindowProvider((_, _) => new());

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_NOT_FOUND", result.ErrorCode);
        Assert.Equal("choose_source_again", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeStart_WindowMinimized_FailsSourceUnavailable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Notepad",
                "notepad.exe",
                42,
                false,
                true,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_UNAVAILABLE", result.ErrorCode);
        Assert.Equal("restore_or_move_window_then_retry", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeStart_WindowAvailable_Passes()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Notepad",
                "notepad.exe",
                42,
                false,
                false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.True(result.Passed);
    }

    [Fact]
    public void CheckBeforeStart_WindowTooSmall_FailsSourceUnavailable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Tiny",
                "tiny.exe",
                42,
                false,
                false,
                new SystemQuery.Bounds(0, 0, 10, 10))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_UNAVAILABLE", result.ErrorCode);
    }
}
