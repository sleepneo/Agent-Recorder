using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

/// <summary>
/// Finalizes audio/video capture by cropping the temporary audio track to the
/// actual video duration and muxing both streams into the final MP4.
/// </summary>
public sealed class AvFinalizer
{
    private readonly IExternalProcessRunner _runner;
    private readonly TimeSpan _muxTimeout;

    internal static readonly TimeSpan DefaultMuxTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SilenceDetectTimeout = TimeSpan.FromMinutes(2);
    internal const double AudioCoverageToleranceSeconds = 0.25;

    /// <summary>
    /// Result of finalization, including the merged output metadata.
    /// </summary>
    public sealed class Result
    {
        public OutputMeta Meta { get; init; } = new();
        public string Stderr { get; init; } = "";
        public int ExitCode { get; init; }
        public string? Error { get; init; }
        public bool TimedOut { get; init; }
    }

    public AvFinalizer(IExternalProcessRunner runner)
        : this(runner, DefaultMuxTimeout)
    {
    }

    internal AvFinalizer(IExternalProcessRunner runner, TimeSpan muxTimeout)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _muxTimeout = muxTimeout;
    }

    /// <summary>
    /// Convenience overload that creates a finalizer using a new
    /// <see cref="ExternalProcessRunner"/>.
    /// </summary>
    public static Result Finalize(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan? audioPreRoll,
        bool microphoneRequested,
        bool applyContinuityCheck,
        string? audioStderr = null)
    {
        return new AvFinalizer(new ExternalProcessRunner())
            .FinalizeAsync(videoPath, audioPath, outputPath, audioPreRoll, microphoneRequested, applyContinuityCheck, audioStderr)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Muxes the temporary video and audio files into the final output path.
    /// The audio is cropped so its start aligns with the video start and its
    /// duration matches the actual video duration.
    /// </summary>
    public async Task<Result> FinalizeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan? audioPreRoll,
        bool microphoneRequested,
        bool applyContinuityCheck,
        string? audioStderr = null,
        bool? videoAnchorAvailable = null,
        bool? audioAnchorAvailable = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var videoMeta = await ProbeAsync(videoPath, cancellationToken).ConfigureAwait(false);

        // Fast path when no audio is requested: validate video and copy it to the final path.
        if (!microphoneRequested)
        {
            if (videoMeta.DurationSeconds <= 0)
                return Failed("video_duration_zero", "Video duration is zero; cannot finalize.", videoMeta);

            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Copy(videoPath, outputPath);
            }
            catch (Exception ex)
            {
                return Failed("copy_failed", $"Failed to copy video to output: {ex.Message}", videoMeta, -1, ex.Message);
            }

            var noAudioMeta = await ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
            noAudioMeta.StderrLog = "";
            noAudioMeta.AudioStatus = "not_requested";
            noAudioMeta.AudioContinuityStatus = "not_checked";
            noAudioMeta.OutputPath = outputPath;
            return new Result
            {
                Meta = noAudioMeta,
                Stderr = "",
                ExitCode = 0
            };
        }

        var audioMeta = await ProbeAsync(audioPath, cancellationToken).ConfigureAwait(false);

        double videoDuration = videoMeta.DurationSeconds;
        double audioDuration = audioMeta.DurationSeconds;
        ApplyTimelineDiagnostics(
            videoMeta,
            videoAnchorAvailable ?? audioPreRoll.HasValue,
            audioAnchorAvailable ?? audioPreRoll.HasValue,
            audioPreRoll,
            videoDuration,
            audioDuration);

        if (videoDuration <= 0)
        {
            return Failed("video_duration_zero", "Video duration is zero; cannot finalize.", videoMeta);
        }

        if (!File.Exists(audioPath))
        {
            videoMeta.AudioStatus = "missing_audio_track";
            videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                .Append("microphone_missing_audio_track: audio file is missing").ToArray();
            return Failed("missing_audio_track", "Audio file is missing; cannot finalize with microphone.", videoMeta);
        }

        if (!audioMeta.HasAudioStream)
        {
            videoMeta.AudioStatus = "missing_audio_track";
            videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                .Append("microphone_missing_audio_track: audio input does not contain an audio stream").ToArray();
            return Failed("missing_audio_track", "Audio input does not contain an audio stream.", videoMeta);
        }

        var lowerStderr = (audioStderr ?? "").ToLowerInvariant();
        bool openFailed = lowerStderr.Contains("could not open audio device") ||
                          lowerStderr.Contains("audio device not found") ||
                          lowerStderr.Contains("no such audio device") ||
                          lowerStderr.Contains("cannot open audio device") ||
                          (lowerStderr.Contains("i/o error") && lowerStderr.Contains("audio="));
        if (openFailed)
        {
            videoMeta.AudioStatus = "start_failed";
            videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                .Append("microphone_start_failed: ffmpeg could not open the selected audio device").ToArray();
            return Failed("start_failed", "Audio device open failed.", videoMeta, -1, audioStderr ?? "");
        }

        var preRoll = audioPreRoll ?? TimeSpan.Zero;
        if (videoAnchorAvailable == false)
        {
            return Failed("video_anchor_missing",
                "Video media-start anchor is missing; cannot align split audio/video.",
                videoMeta,
                -1,
                "video_anchor_status=missing");
        }

        if (audioAnchorAvailable == false)
        {
            return Failed("audio_anchor_missing",
                "Audio media-start anchor is missing; cannot align split audio/video.",
                videoMeta,
                -1,
                "audio_anchor_status=missing");
        }

        if (preRoll <= TimeSpan.Zero)
        {
            // Audio starting at or after video means the streams are misaligned.
            // Do not silently produce an A/V misaligned file.
            return Failed("audio_preroll_invalid",
                $"Audio pre-roll ({preRoll.TotalSeconds:F3}s) is not positive; audio must start before video for atrim alignment.",
                videoMeta,
                -1,
                "audioPreRoll <= 0");
        }

        var requiredAudioCoverage = preRoll.TotalSeconds + videoDuration;
        if (audioDuration + AudioCoverageToleranceSeconds < requiredAudioCoverage)
        {
            var delta = audioDuration - requiredAudioCoverage;
            return Failed("audio_timeline_too_short",
                $"Audio duration ({audioDuration:F3}s) does not cover pre-roll plus video ({requiredAudioCoverage:F3}s, tolerance {AudioCoverageToleranceSeconds:F3}s).",
                videoMeta,
                -1,
                $"audio_duration={audioDuration:F3};required_audio_coverage={requiredAudioCoverage:F3};audio_coverage_delta={delta:F3}");
        }

        var args = new List<string>
        {
            "-y",
            "-nostats",
            "-i", videoPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "[a]",
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "128k",
            "-filter_complex",
            $"[1:a]atrim=start={preRoll.TotalSeconds.ToString(CultureInfo.InvariantCulture)}:duration={videoDuration.ToString(CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a]"
        };

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        var muxResult = await RunFfmpegAsync(args, _muxTimeout, cancellationToken).ConfigureAwait(false);

        var meta = await ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        CopyTimelineDiagnostics(videoMeta, meta);
        meta.StderrLog = muxResult.Stderr;

        if (muxResult.TimedOut)
        {
            return Failed("mux_timeout", "FFmpeg mux timed out.", meta, muxResult.ExitCode, muxResult.Stderr, timedOut: true);
        }

        if (muxResult.ExitCode != 0)
        {
            return Failed("mux_failed", $"FFmpeg mux failed with exit code {muxResult.ExitCode}.", meta, muxResult.ExitCode, muxResult.Stderr);
        }

        if (microphoneRequested)
        {
            ClassifyAudioOutcome(meta, muxResult.Stderr, microphoneRequested, audioStderr);
            if (applyContinuityCheck)
            {
                var classification = await CheckAudioContinuityAsync(outputPath, meta.DurationSeconds, cancellationToken).ConfigureAwait(false);
                meta.AudioContinuityStatus = classification.HasInternalSilence ? "degraded" : "continuous";
                if (classification.HasInternalSilence)
                {
                    var longest = classification.LongestInternalSeconds;
                    meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                        .Append($"microphone_signal_interruption_suspected: internal silence {longest:F1}s >= 3.0s")
                        .ToArray();
                }
            }
            else
            {
                meta.AudioContinuityStatus = "not_checked";
            }
        }
        else
        {
            meta.AudioStatus = "not_requested";
            meta.AudioContinuityStatus = "not_checked";
        }

        return new Result
        {
            Meta = meta,
            Stderr = muxResult.Stderr,
            ExitCode = muxResult.ExitCode
        };
    }

    /// <summary>
    /// Runs silencedetect on the final output to classify audio continuity.
    /// Returns the parsed silence classification; callers should set
    /// <see cref="OutputMeta.AudioContinuityStatus"/> and append
    /// <c>microphone_signal_interruption_suspected</c> when internal silence
    /// is detected.
    /// </summary>
    public async Task<SilenceClassification> CheckAudioContinuityAsync(string path, double durationSeconds, CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "-y",
            "-nostats",
            "-i", path,
            "-af", "silencedetect=noise=-50dB:d=3.0",
            "-f", "null",
            "-"
        };

        var result = await RunFfmpegAsync(args, SilenceDetectTimeout, cancellationToken).ConfigureAwait(false);
        return SilenceIntervalParser.ParseAndClassify(
            result.Stderr, durationSeconds, 3.0);
    }

    /// <summary>
    /// Synchronous convenience for continuity checks.
    /// </summary>
    public static SilenceClassification CheckAudioContinuity(string path, double durationSeconds)
    {
        return new AvFinalizer(new ExternalProcessRunner())
            .CheckAudioContinuityAsync(path, durationSeconds)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<(int ExitCode, string Stderr, bool TimedOut)> RunFfmpegAsync(List<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            FfmpegLocator.FfmpegPath,
            args,
            timeout,
            captureStderr: true,
            stderrEncoding: Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        return (result.ExitCode, result.Stderr, result.TimedOut);
    }

    /// <summary>
    /// Classifies the microphone outcome based on the mux stderr and optional
    /// separate audio-capture stderr. Audio capture stderr carries runtime
    /// device-loss and buffer-underrun evidence from the audio worker.
    /// </summary>
    private static void ClassifyAudioOutcome(OutputMeta meta, string? muxStderr, bool microphoneRequested, string? audioStderr = null)
    {
        if (!microphoneRequested)
        {
            meta.AudioStatus = "not_requested";
            return;
        }

        var stderr = CombineStderr(muxStderr, audioStderr);
        var lower = (stderr ?? "").ToLowerInvariant();

        bool openFailed = lower.Contains("could not open audio device") ||
                          lower.Contains("audio device not found") ||
                          lower.Contains("no such audio device") ||
                          lower.Contains("cannot open audio device") ||
                          (lower.Contains("i/o error") && lower.Contains("audio="));

        if (openFailed)
        {
            meta.AudioStatus = "start_failed";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_start_failed: ffmpeg could not open the selected audio device")
                .ToArray();
            return;
        }

        bool hasAacTrack = meta.HasAudioStream &&
                           string.Equals(meta.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase);

        if (!hasAacTrack)
        {
            meta.AudioStatus = "missing_audio_track";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_missing_audio_track: the output does not contain an AAC audio stream")
                .ToArray();
            return;
        }

        bool lostInCapture = lower.Contains("error reading input") ||
                             (lower.Contains("i/o error") && lower.Contains("dshow"));

        bool bufferUnderrun = lower.Contains("buffer underrun");

        if (lostInCapture)
        {
            meta.AudioStatus = "lost";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_lost: audio input was lost during recording")
                .ToArray();
            return;
        }

        if (bufferUnderrun)
        {
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_buffer_underrun: transient audio queue pressure detected")
                .ToArray();
        }

        meta.AudioStatus = "recorded";
    }

    private static string CombineStderr(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return b ?? "";
        if (string.IsNullOrEmpty(b)) return a;
        return a + "\n" + b;
    }

    private static void ApplyTimelineDiagnostics(
        OutputMeta meta,
        bool videoAnchorAvailable,
        bool audioAnchorAvailable,
        TimeSpan? audioPreRoll,
        double videoDuration,
        double audioDuration)
    {
        meta.VideoAnchorStatus = videoAnchorAvailable ? "available" : "missing";
        meta.AudioAnchorStatus = audioAnchorAvailable ? "available" : "missing";
        meta.AudioPreRollMs = audioPreRoll?.TotalMilliseconds;
        meta.TempVideoDurationSeconds = videoDuration;
        meta.TempAudioDurationSeconds = audioDuration;
        if (audioPreRoll.HasValue && videoDuration > 0)
        {
            var required = audioPreRoll.Value.TotalSeconds + videoDuration;
            meta.RequiredAudioCoverageSeconds = required;
            meta.AudioCoverageDeltaSeconds = audioDuration - required;
        }
    }

    private static void CopyTimelineDiagnostics(OutputMeta from, OutputMeta to)
    {
        to.VideoAnchorStatus = from.VideoAnchorStatus;
        to.AudioAnchorStatus = from.AudioAnchorStatus;
        to.AudioPreRollMs = from.AudioPreRollMs;
        to.TempVideoDurationSeconds = from.TempVideoDurationSeconds;
        to.TempAudioDurationSeconds = from.TempAudioDurationSeconds;
        to.RequiredAudioCoverageSeconds = from.RequiredAudioCoverageSeconds;
        to.AudioCoverageDeltaSeconds = from.AudioCoverageDeltaSeconds;
        to.AudioTimestampCompensationGapSeconds = from.AudioTimestampCompensationGapSeconds;
    }

    private Result Failed(string code, string message, OutputMeta meta, int exitCode = -1, string stderr = "", bool timedOut = false)
    {
        meta.Warnings = (meta.Warnings ?? Array.Empty<string>()).Append($"{code}: {message}").ToArray();
        return new Result
        {
            Meta = meta,
            Error = message,
            ExitCode = exitCode,
            Stderr = stderr,
            TimedOut = timedOut
        };
    }

    private Task<OutputMeta> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        // The external-process runner only captures stderr, while ffprobe writes
        // metadata to stdout. Use the existing synchronous probe helper directly.
        return Task.FromResult(FfmpegCaptureBackend.Probe(path));
    }
}
