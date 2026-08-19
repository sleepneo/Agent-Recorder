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
    private readonly IStagingToFinalPublisher _publisher;
    private readonly IOutputProber _prober;

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
        : this(runner, muxTimeout, StagingToFinalPublisher.Instance)
    {
    }

    internal AvFinalizer(IExternalProcessRunner runner, TimeSpan muxTimeout, IStagingToFinalPublisher publisher)
        : this(runner, muxTimeout, publisher, new FfmpegOutputProber())
    {
    }

    internal AvFinalizer(IExternalProcessRunner runner, TimeSpan muxTimeout, IStagingToFinalPublisher publisher, IOutputProber prober)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _muxTimeout = muxTimeout;
        _publisher = publisher ?? StagingToFinalPublisher.Instance;
        _prober = prober ?? new FfmpegOutputProber();
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
        AudioCaptureSourceKind audioSourceKind,
        bool applyContinuityCheck,
        string? audioStderr = null)
    {
        return new AvFinalizer(new ExternalProcessRunner())
            .FinalizeAsync(videoPath, audioPath, outputPath, audioPreRoll, audioSourceKind, applyContinuityCheck, audioStderr)
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
        AudioCaptureSourceKind audioSourceKind,
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
        if (audioSourceKind == AudioCaptureSourceKind.None)
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
            if (!TrySetPublishedOutputMetadata(noAudioMeta, outputPath))
            {
                return Failed("output_missing_after_copy",
                    "The copied output was not present or had zero length after finalization.",
                    noAudioMeta);
            }
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
                .Append($"{SourceKey(audioSourceKind, "missing_track")}: audio file is missing").ToArray();
            return Failed("missing_audio_track", $"Audio file is missing; cannot finalize with {audioSourceKind}.", videoMeta);
        }

        if (!audioMeta.HasAudioStream)
        {
            videoMeta.AudioStatus = "missing_audio_track";
            videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                .Append($"{SourceKey(audioSourceKind, "missing_track")}: audio input does not contain an audio stream").ToArray();
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
                .Append($"{SourceKey(audioSourceKind, "start_failed")}: ffmpeg could not open the selected audio device").ToArray();
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

        // Mux to a temporary path so partial/ failed output never lands at the
        // final path. Atomic rename after validation prevents false success.
        var tempMuxPath = outputPath + ".muxing.partial.mp4";
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(tempMuxPath);

        // The mux execution, probe, post-mux validation and publish all touch
        // <output>.muxing.partial.mp4. Wrap the entire sequence in a try/finally
        // so every outcome — runner return, runner throwing (including caller
        // cancellation, which the production runner surfaces as
        // OperationCanceledException), probe failure, validation failure, publish
        // failure and any unexpected exception — leaves no mux partial behind and
        // never touches a pre-existing valid final file.
        var meta = new OutputMeta();
        var muxExitCode = -1;
        var muxTimedOut = false;
        var muxStderr = "";

        try
        {
            var muxResult = await RunFfmpegAsync(args, _muxTimeout, cancellationToken).ConfigureAwait(false);
            muxExitCode = muxResult.ExitCode;
            muxTimedOut = muxResult.TimedOut;
            muxStderr = muxResult.Stderr;

            meta = await ProbeAsync(tempMuxPath, cancellationToken).ConfigureAwait(false);
            CopyTimelineDiagnostics(videoMeta, meta);
            meta.StderrLog = muxStderr;

            // Failure and timeout paths: the partial temp file is removed in the
            // finally below; the final path is never created on any failure path.
            if (muxTimedOut)
                return Failed("mux_timeout", "FFmpeg mux timed out.", meta, muxExitCode, muxStderr, timedOut: true);

            if (muxExitCode != 0)
                return Failed("mux_failed", $"FFmpeg mux failed with exit code {muxExitCode}.", meta, muxExitCode, muxStderr);

            // Post-mux media validation: verify the actual stream order and final
            // A/V timeline before marking success. This catches cases where FFmpeg
            // exits 0 but produces a file missing the H.264 video stream or AAC
            // audio stream, has zero or non-finite duration, violates the 0.250s
            // coverage contract, or carries an ambiguous/duplicate stream set.
            var validationError = ValidateOutputMedia(meta);
            if (validationError != null)
                return Failed(validationError, $"Post-mux output validation failed: {validationError}", meta, muxExitCode, muxStderr);

            // Atomic publish: use the repository's staging-to-final publisher so
            // the final path is updated by a same-directory atomic move after the
            // staging file has been copied, flushed and size-verified. A
            // pre-existing valid final file is preserved when any step fails and
            // is never replaced with an unvalidated file.
            var publishResult = await _publisher.PublishAsync(tempMuxPath, outputPath, cancellationToken).ConfigureAwait(false);
            if (!publishResult.Success)
                return Failed("atomic_publish_failed", $"Publish failed: {publishResult.FailureCategory}", meta, -1, publishResult.FailureCategory ?? "");

            if (!TrySetPublishedOutputMetadata(meta, outputPath))
            {
                return Failed("output_missing_after_publish",
                    "The publisher reported success but the final output was not present or had zero length.",
                    meta);
            }
        }
        finally
        {
            // Every path — mux failure/timeout, validation failure, publish
            // failure, cancellation and unexpected exceptions — must leave no
            // `.muxing.partial.mp4` behind. On success the staging copy is no
            // longer needed either (the publisher already moved a verified copy
            // to the final path).
            TryDeleteTempFile(tempMuxPath);
        }

        if (audioSourceKind == AudioCaptureSourceKind.Microphone)
        {
            ClassifyAudioOutcome(meta, muxStderr, audioSourceKind, audioStderr);
            if (applyContinuityCheck)
            {
                var classification = await CheckAudioContinuityAsync(outputPath, meta.DurationSeconds, cancellationToken).ConfigureAwait(false);
                meta.AudioContinuityStatus = classification.HasInternalSilence ? "degraded" : "continuous";
                if (classification.HasInternalSilence)
                {
                    var longest = classification.LongestInternalSeconds;
                    meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                        .Append($"{SourceKey(audioSourceKind, "signal_interruption_suspected")}: internal silence {longest:F1}s >= 3.0s")
                        .ToArray();
                }
            }
            else
            {
                meta.AudioContinuityStatus = "not_checked";
            }
        }
        else if (audioSourceKind == AudioCaptureSourceKind.SystemLoopback)
        {
            meta.AudioStatus = "system_loopback_recorded";
            meta.AudioContinuityStatus = "continuous";
        }
        else
        {
            meta.AudioStatus = "not_requested";
            meta.AudioContinuityStatus = "not_checked";
        }

        return new Result
        {
            Meta = meta,
            Stderr = muxStderr,
            ExitCode = muxExitCode
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
    private static void ClassifyAudioOutcome(OutputMeta meta, string? muxStderr, AudioCaptureSourceKind audioSourceKind, string? audioStderr = null)
    {
        if (audioSourceKind != AudioCaptureSourceKind.Microphone &&
            audioSourceKind != AudioCaptureSourceKind.SystemLoopback)
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
                .Append($"{SourceKey(audioSourceKind, "start_failed")}: ffmpeg could not open the selected audio device")
                .ToArray();
            return;
        }

        bool hasAacTrack = meta.HasAudioStream &&
                           string.Equals(meta.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase);

        if (!hasAacTrack)
        {
            meta.AudioStatus = "missing_audio_track";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append($"{SourceKey(audioSourceKind, "missing_track")}: the output does not contain an AAC audio stream")
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
                .Append($"{SourceKey(audioSourceKind, "lost")}: audio input was lost during recording")
                .ToArray();
            return;
        }

        if (bufferUnderrun)
        {
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append($"{SourceKey(audioSourceKind, "buffer_underrun")}: transient audio queue pressure detected")
                .ToArray();
        }

        meta.AudioStatus = audioSourceKind == AudioCaptureSourceKind.SystemLoopback
            ? "system_loopback_recorded"
            : "recorded";
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
        to.VideoLaunchAnchorTicks = from.VideoLaunchAnchorTicks;
        to.VideoProgressAnchorTicks = from.VideoProgressAnchorTicks;
        to.VideoProgressAnchorDeltaMs = from.VideoProgressAnchorDeltaMs;
        to.VideoFirstProgressFrame = from.VideoFirstProgressFrame;
        to.VideoFirstProgressOutTimeUs = from.VideoFirstProgressOutTimeUs;
        to.AudioAnchorStatus = from.AudioAnchorStatus;
        to.AudioPreRollMs = from.AudioPreRollMs;
        to.TempVideoDurationSeconds = from.TempVideoDurationSeconds;
        to.TempAudioDurationSeconds = from.TempAudioDurationSeconds;
        to.RequiredAudioCoverageSeconds = from.RequiredAudioCoverageSeconds;
        to.AudioCoverageDeltaSeconds = from.AudioCoverageDeltaSeconds;
        to.AudioTimestampCompensationGapSeconds = from.AudioTimestampCompensationGapSeconds;
    }

    private static string SourceKey(AudioCaptureSourceKind kind, string key)
    {
        var prefix = kind switch
        {
            AudioCaptureSourceKind.Microphone => "microphone",
            AudioCaptureSourceKind.SystemLoopback => "system_audio",
            _ => "audio"
        };
        return $"{prefix}_{key}";
    }

    /// <summary>
    /// Validates the muxed output media from structured probe evidence: the
    /// actual stream order (stream 0 = H.264 video, stream 1 = AAC audio), both
    /// stream durations positive, and the final audio timeline covering the final
    /// video timeline within the 0.250s contract. Any probe failure, missing
    /// field, ambiguity, or contract violation fails closed. Returns null on
    /// success or a stable error code on failure.
    /// </summary>
    private static string? ValidateOutputMedia(OutputMeta meta)
    {
        if (meta.ProbeStreams.Length == 0)
            return "output_probe_failed";

        var videoStreams = meta.ProbeStreams
            .Where(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)).ToList();
        var audioStreams = meta.ProbeStreams
            .Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)).ToList();

        // The product mux contract produces exactly one video and one audio
        // stream. Missing or more-than-one of either kind is ambiguous and must
        // fail closed.
        if (videoStreams.Count == 0)
            return "output_missing_video_stream";
        if (videoStreams.Count > 1)
            return $"output_video_stream_ambiguous:{videoStreams.Count}";
        if (audioStreams.Count == 0)
            return "output_missing_audio_stream";
        if (audioStreams.Count > 1)
            return $"output_audio_stream_ambiguous:{audioStreams.Count}";

        // No other stream types (data, subtitle, ...) are allowed by the contract.
        var otherStreamCount = meta.ProbeStreams.Length - videoStreams.Count - audioStreams.Count;
        if (otherStreamCount != 0)
            return $"output_unexpected_extra_streams:{otherStreamCount}";

        // The two stream indexes must be unique and exactly video=0, audio=1.
        if (meta.ProbeStreams.Select(s => s.Index).Distinct().Count() != meta.ProbeStreams.Length)
            return "output_duplicate_stream_index";

        var videoStream = videoStreams[0];
        var audioStream = audioStreams[0];

        if (videoStream.Index != 0)
            return $"output_video_stream_index:{videoStream.Index}";
        if (audioStream.Index != 1)
            return $"output_audio_stream_index:{audioStream.Index}";

        if (string.IsNullOrEmpty(videoStream.CodecName) ||
            !string.Equals(videoStream.CodecName, "h264", StringComparison.OrdinalIgnoreCase))
            return videoStream.CodecName == null ? "output_video_codec_missing" : $"output_unexpected_video_codec:{videoStream.CodecName}";
        if (string.IsNullOrEmpty(audioStream.CodecName) ||
            !string.Equals(audioStream.CodecName, "aac", StringComparison.OrdinalIgnoreCase))
            return audioStream.CodecName == null ? "output_audio_codec_missing" : $"output_unexpected_audio_codec:{audioStream.CodecName}";

        // Format duration must be present, finite and positive.
        if (!double.IsFinite(meta.DurationSeconds))
            return "output_duration_non_finite";
        if (meta.DurationSeconds <= 0)
            return "output_duration_zero";

        // Both stream durations must be present, finite and positive.
        if (!TryGetPositiveDuration(videoStream.DurationSeconds, out double vDuration))
            return "output_video_duration_invalid";
        if (!TryGetPositiveDuration(audioStream.DurationSeconds, out double aDuration))
            return "output_audio_duration_invalid";

        // Both stream start times must be present and finite (MP4 muxed output
        // carries them). NaN/Infinity and absent values all fail closed.
        if (videoStream.StartTimeSeconds is not double vStart || !double.IsFinite(vStart))
            return "output_video_start_time_invalid";
        if (audioStream.StartTimeSeconds is not double aStart || !double.IsFinite(aStart))
            return "output_audio_start_time_invalid";

        // Final A/V alignment within the 0.250s contract. Reject audio that
        // starts too late OR too early, and audio that ends too short OR too
        // long — i.e. any final A/V boundary drift beyond the tolerance.
        double videoEnd = vStart + vDuration;
        double audioEnd = aStart + aDuration;

        if (aStart - vStart > AudioCoverageToleranceSeconds)
            return $"output_audio_start_late:video_start={vStart.ToString("F3", CultureInfo.InvariantCulture)}s,audio_start={aStart.ToString("F3", CultureInfo.InvariantCulture)}s";
        if (vStart - aStart > AudioCoverageToleranceSeconds)
            return $"output_audio_start_early:video_start={vStart.ToString("F3", CultureInfo.InvariantCulture)}s,audio_start={aStart.ToString("F3", CultureInfo.InvariantCulture)}s";
        if (videoEnd - audioEnd > AudioCoverageToleranceSeconds)
            return $"output_audio_end_short:video_end={videoEnd.ToString("F3", CultureInfo.InvariantCulture)}s,audio_end={audioEnd.ToString("F3", CultureInfo.InvariantCulture)}s";
        if (audioEnd - videoEnd > AudioCoverageToleranceSeconds)
            return $"output_audio_end_long:video_end={videoEnd.ToString("F3", CultureInfo.InvariantCulture)}s,audio_end={audioEnd.ToString("F3", CultureInfo.InvariantCulture)}s";

        return null;
    }

    private static bool TryGetPositiveDuration(double? value, out double result)
    {
        if (value.HasValue && double.IsFinite(value.Value) && value.Value > 0)
        {
            result = value.Value;
            return true;
        }
        result = 0;
        return false;
    }

    private Result Failed(string code, string message, OutputMeta meta, int exitCode = -1, string stderr = "", bool timedOut = false)
    {
        // A failed finalization must never inherit the presence or size of a
        // temporary mux file or a pre-existing final file. The final output
        // contract is only true after the current run publishes successfully.
        meta.OutputPath = null;
        meta.OutputFileExists = false;
        meta.SizeBytes = 0;
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

    private static bool TrySetPublishedOutputMetadata(OutputMeta meta, string outputPath)
    {
        try
        {
            var file = new FileInfo(outputPath);
            if (!file.Exists || file.Length <= 0)
            {
                meta.OutputPath = outputPath;
                meta.OutputFileExists = false;
                meta.SizeBytes = file.Exists ? file.Length : 0;
                return false;
            }

            meta.OutputPath = outputPath;
            meta.OutputFileExists = true;
            meta.SizeBytes = file.Length;
            return true;
        }
        catch
        {
            meta.OutputPath = outputPath;
            meta.OutputFileExists = false;
            meta.SizeBytes = 0;
            return false;
        }
    }

    private static void TryDeleteTempFile(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private Task<OutputMeta> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        // The external-process runner only captures stderr, while ffprobe writes
        // metadata to stdout. Use the injected probe seam (production: structured
        // ffprobe JSON) directly.
        return Task.FromResult(_prober.Probe(path));
    }
}

/// <summary>
/// Test seam for reading structured output metadata from a media file.
/// Production uses real structured ffprobe; tests inject a deterministic
/// <see cref="OutputMeta"/> to exercise fail-closed probe validation without
/// depending on the shape FFmpeg happens to produce.
/// </summary>
internal interface IOutputProber
{
    OutputMeta Probe(string path);
}

/// <summary>
/// Production <see cref="IOutputProber"/> backed by structured ffprobe JSON.
/// </summary>
internal sealed class FfmpegOutputProber : IOutputProber
{
    public OutputMeta Probe(string path) => FfmpegCaptureBackend.Probe(path);
}
