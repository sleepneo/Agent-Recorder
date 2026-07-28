using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    public delegate string? TryResolveAudioHelper();
    public delegate AudioHelperProbeResult RunAudioHelperProbe(string helperPath, CancellationToken token);

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
    /// Injectable audio helper path resolver for tests.
    /// </summary>
    public static TryResolveAudioHelper AudioHelperPathResolver { get; set; } = DefaultAudioHelperPathResolver;

    /// <summary>
    /// Injectable audio helper version/probe runner for tests.
    /// </summary>
    public static RunAudioHelperProbe AudioHelperProbeRunner { get; set; } = DefaultAudioHelperProbeRunner;

    /// <summary>
    /// Injectable backend selector for tests. Returns true if the current
    /// configuration would use the WASAPI helper backend.
    /// </summary>
    public static Func<bool> ShouldUseWasapiBackend { get; set; } = DefaultShouldUseWasapiBackend;

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

        result = CheckWasapiHelperPreflight(rec);
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

        result = CheckWasapiHelperPreflight(rec);
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

    internal static bool DefaultEncoderProvider(out string? ffmpegPath, out string? ffprobePath)
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

    private static RecordingPreflightResult CheckWasapiHelperPreflight(Recording rec)
    {
        if (!rec.Config.Microphone || !ShouldUseWasapiBackend())
            return Pass(new List<string>());

        var helperPath = AudioHelperPathResolver();
        if (string.IsNullOrEmpty(helperPath))
        {
            return Fail("audio_helper_unavailable",
                "WASAPI audio helper executable not found.",
                "ensure_audio_helper_exists");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        AudioHelperProbeResult probe;
        try
        {
            probe = AudioHelperProbeRunner(helperPath, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return Fail("audio_helper_probe_timeout",
                "Timed out waiting for audio helper --version response.",
                "check_audio_helper_responsiveness");
        }
        catch (Exception ex)
        {
            return Fail("audio_helper_unavailable",
                $"Failed to start audio helper probe: {ex.Message}",
                "ensure_audio_helper_exists");
        }

        if (!probe.Success)
        {
            return Fail(probe.ErrorCode ?? "audio_helper_probe_failed",
                probe.ErrorMessage ?? "Audio helper probe failed.",
                "check_audio_helper_version");
        }

        if (!string.Equals(probe.ProtocolVersion, "audio-helper-v1", StringComparison.Ordinal))
        {
            return Fail("audio_helper_protocol_error",
                $"Unsupported audio helper protocol version: {probe.ProtocolVersion}",
                "update_audio_helper");
        }

        if (probe.TimestampFrequency != Stopwatch.Frequency)
        {
            return Fail("audio_helper_protocol_error",
                $"Audio helper timestamp frequency mismatch: helper={probe.TimestampFrequency}, host={Stopwatch.Frequency}",
                "update_audio_helper");
        }

        if (!string.IsNullOrEmpty(rec.Config.MicDevice) &&
            rec.Config.MicDevice.IndexOf(@"\wave_", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string? endpointId;
            try
            {
                endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(rec.Config.MicDevice);
            }
            catch (Exception ex)
            {
                return Fail("audio_endpoint_id_unmappable",
                    $"Could not map microphone device id to CoreAudio endpoint: {ex.Message}",
                    "select_valid_microphone");
            }

            if (string.IsNullOrWhiteSpace(endpointId))
            {
                return Fail("audio_endpoint_id_unmappable",
                    "Could not map microphone device id to CoreAudio endpoint.",
                    "select_valid_microphone");
            }
        }

        return Pass(new List<string>());
    }

    private static string? DefaultAudioHelperPathResolver()
    {
        try
        {
            return AudioHelperExePathResolver.TryResolve();
        }
        catch
        {
            return null;
        }
    }

    private static AudioHelperProbeResult DefaultAudioHelperProbeRunner(string helperPath, CancellationToken token)
    {
        return AudioHelperProbeLauncher.Run(helperPath, TimeSpan.FromSeconds(10), token);
    }

    private static bool DefaultShouldUseWasapiBackend()
    {
        try
        {
            return AvWorkerFactory.GetBackend() == AvWorkerFactory.WasapiBackend;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Launches the audio helper with <c>--version</c>, drains stdout/stderr with
/// bounded buffers, and guarantees the process is terminated on cancellation or
/// timeout. Extracted as an internal class so real process timeout behaviour can
/// be exercised in tests without waiting for the production 10-second default.
/// </summary>
internal static class AudioHelperProbeLauncher
{
    public static AudioHelperProbeResult Run(string helperPath, TimeSpan timeout, CancellationToken externalToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        cts.CancelAfter(timeout);
        return RunAsync(helperPath, cts.Token).GetAwaiter().GetResult();
    }

    private static async Task<AudioHelperProbeResult> RunAsync(string helperPath, CancellationToken token)
    {
        const int MaxProbeLogChars = 4096;
        const int DrainTimeoutMs = 2000;
        const int KillWaitTimeoutMs = 2000;

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = "--version",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutBuilder = new BoundedStringBuilder(MaxProbeLogChars);
        var stderrBuilder = new BoundedStringBuilder(MaxProbeLogChars);

        using var killRegistration = token.Register(() =>
        {
            try { proc?.Kill(true); } catch { }
        });

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new AudioHelperProbeResult
            {
                Success = false,
                ErrorCode = "audio_helper_unavailable",
                ErrorMessage = "Failed to start audio helper process: " + ex.Message
            };
        }

        int processId = proc.Id;

        var stdoutDrain = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)) != null)
                    stdoutBuilder.AppendLine(line);
            }
            catch { }
        }, token);

        var stderrDrain = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync(token).ConfigureAwait(false)) != null)
                    stderrBuilder.AppendLine(line);
            }
            catch { }
        }, token);

        bool canceled = false;
        try
        {
            await proc.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            try { proc.Kill(true); } catch { }
        }

        // Bounded wait for the output streams to drain after process exit/kill.
        try
        {
            await Task.WhenAll(stdoutDrain, stderrDrain)
                .WaitAsync(TimeSpan.FromMilliseconds(DrainTimeoutMs), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { }

        // Final guarantee: no probe process is left behind.
        try
        {
            if (!proc.HasExited)
                proc.Kill(true);
        }
        catch { }
        try
        {
            proc.WaitForExit(TimeSpan.FromMilliseconds(KillWaitTimeoutMs));
        }
        catch { }

        if (canceled || token.IsCancellationRequested)
        {
            return new AudioHelperProbeResult
            {
                Success = false,
                ErrorCode = "audio_helper_probe_timeout",
                ErrorMessage = "Timed out waiting for audio helper --version response.",
                ProcessId = processId
            };
        }

        if (proc.ExitCode != 0)
        {
            return new AudioHelperProbeResult
            {
                Success = false,
                ErrorCode = "audio_helper_probe_failed",
                ErrorMessage = $"Audio helper --version returned exit code {proc.ExitCode}. stderr: {stderrBuilder}",
                ProcessId = processId
            };
        }

        var stdout = stdoutBuilder.ToString();
        string protocolVersion = "";
        long timestampFrequency = 0;

        foreach (var rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Protocol:", StringComparison.OrdinalIgnoreCase))
            {
                protocolVersion = line.Substring("Protocol:".Length).Trim();
            }
            else if (line.StartsWith("TimestampFrequency:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Substring("TimestampFrequency:".Length).Trim();
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out timestampFrequency);
            }
        }

        if (string.IsNullOrEmpty(protocolVersion))
        {
            return new AudioHelperProbeResult
            {
                Success = false,
                ErrorCode = "audio_helper_protocol_error",
                ErrorMessage = "Audio helper --version did not report a protocol version.",
                ProcessId = processId
            };
        }

        return new AudioHelperProbeResult
        {
            Success = true,
            ProtocolVersion = protocolVersion,
            TimestampFrequency = timestampFrequency,
            ProcessId = processId
        };
    }
}

/// <summary>
/// Result of an audio helper --version probe.
/// </summary>
public sealed class AudioHelperProbeResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string ProtocolVersion { get; set; } = "";
    public long TimestampFrequency { get; set; }

    /// <summary>
    /// Process id of the probe helper process. Exposed for test verification
    /// that no helper process is left behind after a timeout.
    /// </summary>
    internal int ProcessId { get; set; }
}
