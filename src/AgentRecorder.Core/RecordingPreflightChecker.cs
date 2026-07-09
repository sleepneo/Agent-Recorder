using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentRecorder.Capture;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Dry-run / preflight checks for a recording. Runs before creating a pending
/// confirmation and again before starting capture, so failures are surfaced
/// early instead of producing empty or broken output files.
/// </summary>
internal static class RecordingPreflightChecker
{
    public delegate bool TryGetFreeSpace(string path, out long freeBytes);
    public delegate bool TryGetEncoderPaths(out string? ffmpegPath, out string? ffprobePath);

    /// <summary>
    /// Injectable disk-space provider for tests. Returns true if free space could
    /// be determined, with the value in <paramref name="freeBytes"/>.
    /// </summary>
    public static TryGetFreeSpace FreeSpaceProvider { get; set; } = DefaultFreeSpaceProvider;

    /// <summary>
    /// Injectable encoder-path provider for tests. Avoids touching the static
    /// FfmpegLocator cache directly in negative-path tests.
    /// </summary>
    public static TryGetEncoderPaths EncoderProvider { get; set; } = DefaultEncoderProvider;

    /// <summary>
    /// Checks that can run immediately after ConfigParser.Build, before creating
    /// a confirmation. These do not depend on user interaction or elapsed time.
    /// </summary>
    public static RecordingPreflightResult CheckBeforeConfirmation(Recording rec)
    {
        var warnings = new List<string>();

        var result = CheckOutputDirectoryWritable(rec, warnings);
        if (!result.Passed) return result;

        result = CheckDiskSpace(rec, warnings);
        if (!result.Passed) return result;

        result = CheckEncoderAvailable();
        if (!result.Passed) return result;

        result = CheckBounds(rec, warnings);
        if (!result.Passed) return result;

        return Pass(warnings);
    }

    /// <summary>
    /// Checks that run after the user approves but before FFmpeg starts. Repeats
    /// the before-confirmation checks and adds source-availability checks,
    /// because the desktop state may have changed while the confirmation was
    /// pending.
    /// </summary>
    public static RecordingPreflightResult CheckBeforeStart(Recording rec)
    {
        var warnings = new List<string>();

        var result = CheckOutputDirectoryWritable(rec, warnings);
        if (!result.Passed) return result;

        result = CheckDiskSpace(rec, warnings);
        if (!result.Passed) return result;

        result = CheckEncoderAvailable();
        if (!result.Passed) return result;

        result = CheckSourceAvailable(rec, warnings);
        if (!result.Passed) return result;

        result = CheckBounds(rec, warnings);
        if (!result.Passed) return result;

        return Pass(warnings);
    }

    private static RecordingPreflightResult Pass(List<string> warnings)
    {
        return new RecordingPreflightResult(
            true,
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    private static RecordingPreflightResult Fail(string errorCode, string message, string suggestedAction)
    {
        return new RecordingPreflightResult(false, errorCode, message, suggestedAction);
    }

    private static RecordingPreflightResult CheckOutputDirectoryWritable(Recording rec, List<string> warnings)
    {
        var dir = Path.GetDirectoryName(rec.OutputPath);
        if (string.IsNullOrWhiteSpace(dir))
            return Fail("OUTPUT_DIRECTORY_UNWRITABLE", "Output path has no directory.", "choose_another_output_directory");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return Fail("OUTPUT_DIRECTORY_UNWRITABLE",
                $"Cannot create output directory '{dir}': {ex.Message}",
                "choose_another_output_directory");
        }

        var tmpName = ".agent-recorder-preflight-" + Guid.NewGuid().ToString("N") + ".tmp";
        var tmpPath = Path.Combine(dir, tmpName);
        try
        {
            File.WriteAllBytes(tmpPath, new byte[] { 0 });
            File.Delete(tmpPath);
        }
        catch (Exception ex)
        {
            return Fail("OUTPUT_DIRECTORY_UNWRITABLE",
                $"Output directory '{dir}' is not writable: {ex.Message}",
                "choose_another_output_directory");
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }

        return Pass(warnings);
    }

    private static RecordingPreflightResult CheckDiskSpace(Recording rec, List<string> warnings)
    {
        var dir = Path.GetDirectoryName(rec.OutputPath);
        if (string.IsNullOrWhiteSpace(dir))
            return Pass(warnings);

        if (!FreeSpaceProvider(dir, out var freeBytes))
        {
            warnings.Add("Could not determine free disk space; continuing.");
            return Pass(warnings);
        }

        long thresholdBytes = 100L * 1024 * 1024; // default 100 MB
        if (rec.DurationSeconds is int secs && secs > 0)
        {
            long estimated = (long)secs * 2 * 1024 * 1024; // 2 MB/s
            thresholdBytes = Math.Max(thresholdBytes, estimated);
        }

        if (freeBytes < thresholdBytes)
        {
            return Fail("INSUFFICIENT_DISK_SPACE",
                $"Insufficient disk space on output drive (available {freeBytes / (1024 * 1024)} MB, required at least {thresholdBytes / (1024 * 1024)} MB).",
                "free_disk_space_or_choose_another_directory");
        }

        return Pass(warnings);
    }

    private static RecordingPreflightResult CheckEncoderAvailable()
    {
        if (!EncoderProvider(out var ffmpeg, out var ffprobe) ||
            string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe) ||
            !File.Exists(ffmpeg) || !File.Exists(ffprobe))
        {
            return Fail("ENCODER_UNAVAILABLE",
                "FFmpeg or FFprobe is not available.",
                "check_ffmpeg_files_or_reinstall_package");
        }

        return Pass(new List<string>());
    }

    private static RecordingPreflightResult CheckSourceAvailable(Recording rec, List<string> warnings)
    {
        if (rec.SourceType != "window" || rec.Config.WindowHandle == nint.Zero)
            return Pass(warnings);

        var windows = SystemQuery.EnumWindows(includeMinimized: true, includeSystem: false);
        var window = windows.FirstOrDefault(w => w.id == $"window_{rec.Config.WindowHandle.ToInt64()}");

        if (window == null)
        {
            return Fail("SOURCE_NOT_FOUND",
                $"Target window '{rec.SourceTitle}' no longer exists.",
                "choose_source_again");
        }

        if (window.is_minimized)
        {
            return Fail("SOURCE_UNAVAILABLE",
                $"Target window '{rec.SourceTitle}' is minimized and cannot be captured.",
                "restore_or_move_window_then_retry");
        }

        const int MinSize = 32;
        if (window.bounds.width < MinSize || window.bounds.height < MinSize)
        {
            return Fail("SOURCE_UNAVAILABLE",
                $"Target window '{rec.SourceTitle}' is too small ({window.bounds.width}x{window.bounds.height}).",
                "restore_or_move_window_then_retry");
        }

        var virtualScreen = SystemQuery.VirtualScreenBounds();
        if (!HasPositiveOverlap(window.bounds, virtualScreen))
        {
            return Fail("SOURCE_UNAVAILABLE",
                $"Target window '{rec.SourceTitle}' is outside the capturable desktop area.",
                "restore_or_move_window_then_retry");
        }

        return Pass(warnings);
    }

    private static RecordingPreflightResult CheckBounds(Recording rec, List<string> warnings)
    {
        var bounds = rec.Config.Bounds;
        if (bounds.w <= 0 || bounds.h <= 0)
        {
            return Fail("SOURCE_UNAVAILABLE",
                "Capture bounds have zero or negative dimensions.",
                "choose_source_again");
        }

        if (bounds.w % 2 != 0 || bounds.h % 2 != 0)
        {
            return Fail("SOURCE_UNAVAILABLE",
                "Capture bounds dimensions must be even.",
                "choose_source_again");
        }

        const int MinSize = 32;
        if (bounds.w < MinSize || bounds.h < MinSize)
        {
            return Fail("SOURCE_UNAVAILABLE",
                $"Capture bounds are too small ({bounds.w}x{bounds.h}). Minimum is {MinSize}x{MinSize}.",
                "choose_source_again");
        }

        if (rec.SourceType == "region" || rec.SourceType == "window")
        {
            var virtualScreen = SystemQuery.VirtualScreenBounds();
            if (!HasPositiveOverlap(
                new SystemQuery.Bounds(bounds.x, bounds.y, bounds.w, bounds.h),
                virtualScreen))
            {
                return Fail("SOURCE_UNAVAILABLE",
                    "Capture bounds are outside the virtual screen area.",
                    "restore_or_move_window_then_retry");
            }
        }

        return Pass(warnings);
    }

    private static bool HasPositiveOverlap(SystemQuery.Bounds a, SystemQuery.Bounds b)
    {
        int left = Math.Max(a.x, b.x);
        int top = Math.Max(a.y, b.y);
        int right = Math.Min(a.x + a.width, b.x + b.width);
        int bottom = Math.Min(a.y + a.height, b.y + b.height);
        return right > left && bottom > top;
    }

    private static bool DefaultFreeSpaceProvider(string path, out long freeBytes)
    {
        freeBytes = 0;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var drive = new DriveInfo(root);
            freeBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DefaultEncoderProvider(out string? ffmpegPath, out string? ffprobePath)
    {
        ffmpegPath = null;
        ffprobePath = null;
        try
        {
            ffmpegPath = FfmpegLocator.FfmpegPath;
            ffprobePath = FfmpegLocator.FfprobePath;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
