namespace AgentRecorder.Capture;

/// <summary>
/// Process-local, validated proof for the narrow FFmpeg fixed-rate final-frame
/// quantization case. Instances can only be created after the backend has
/// checked the complete video packet timeline from ffprobe.
/// </summary>
internal sealed class FfmpegQuantizedTailEvidence
{
    private const double FormatDurationRoundingToleranceSeconds = 0.0011;
    private const double FrameRateDeviationRatio = 0.01;

    private FfmpegQuantizedTailEvidence(
        string outputPath,
        long outputSizeBytes,
        long outputLastWriteUtcTicks,
        double authorizedDurationSeconds,
        int configuredFrameRate,
        double formatDurationSeconds,
        double videoStreamDurationSeconds,
        double videoStartTimeSeconds,
        double timeBaseSeconds,
        double averageFrameRate,
        double nominalFrameRate,
        long frameCount,
        double firstFramePtsSeconds,
        double lastFramePtsSeconds,
        double lastPacketDurationSeconds,
        double minimumFrameIntervalSeconds,
        double maximumFrameIntervalSeconds,
        double minimumPacketDurationSeconds,
        double maximumPacketDurationSeconds)
    {
        OutputPath = outputPath;
        OutputSizeBytes = outputSizeBytes;
        OutputLastWriteUtcTicks = outputLastWriteUtcTicks;
        AuthorizedDurationSeconds = authorizedDurationSeconds;
        ConfiguredFrameRate = configuredFrameRate;
        FormatDurationSeconds = formatDurationSeconds;
        VideoStreamDurationSeconds = videoStreamDurationSeconds;
        VideoStartTimeSeconds = videoStartTimeSeconds;
        TimeBaseSeconds = timeBaseSeconds;
        AverageFrameRate = averageFrameRate;
        NominalFrameRate = nominalFrameRate;
        FrameCount = frameCount;
        FirstFramePtsSeconds = firstFramePtsSeconds;
        LastFramePtsSeconds = lastFramePtsSeconds;
        LastPacketDurationSeconds = lastPacketDurationSeconds;
        MinimumFrameIntervalSeconds = minimumFrameIntervalSeconds;
        MaximumFrameIntervalSeconds = maximumFrameIntervalSeconds;
        MinimumPacketDurationSeconds = minimumPacketDurationSeconds;
        MaximumPacketDurationSeconds = maximumPacketDurationSeconds;
    }

    private string OutputPath { get; }
    private long OutputSizeBytes { get; }
    private long OutputLastWriteUtcTicks { get; }
    private double AuthorizedDurationSeconds { get; }
    private int ConfiguredFrameRate { get; }
    private double FormatDurationSeconds { get; }
    private double VideoStreamDurationSeconds { get; }
    private double VideoStartTimeSeconds { get; }
    private double TimeBaseSeconds { get; }
    private double AverageFrameRate { get; }
    private double NominalFrameRate { get; }
    private long FrameCount { get; }
    private double FirstFramePtsSeconds { get; }
    private double LastFramePtsSeconds { get; }
    private double LastPacketDurationSeconds { get; }
    private double MinimumFrameIntervalSeconds { get; }
    private double MaximumFrameIntervalSeconds { get; }
    private double MinimumPacketDurationSeconds { get; }
    private double MaximumPacketDurationSeconds { get; }

    internal static FfmpegQuantizedTailEvidence? TryCreate(
        OutputMeta meta,
        CaptureConfig config,
        FfmpegPacketTimeline timeline,
        bool exactDurationCapsApplied,
        long beforeProbeLength,
        long beforeProbeLastWriteUtcTicks,
        long afterProbeLength,
        long afterProbeLastWriteUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeline);

        if (!exactDurationCapsApplied ||
            !string.Equals(config.SourceKind, "region", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(config.Mode, "video", StringComparison.OrdinalIgnoreCase) ||
            config.AudioRequested ||
            config.DurationSeconds is not int configuredDuration || configuredDuration <= 0 || configuredDuration > 600 ||
            config.Fps is not (15 or 24 or 30 or 60) ||
            !meta.OutputFileExists ||
            meta.SizeBytes <= 0 ||
            meta.SizeBytes != beforeProbeLength || beforeProbeLength != afterProbeLength ||
            beforeProbeLastWriteUtcTicks != afterProbeLastWriteUtcTicks ||
            meta.DurationSeconds <= configuredDuration ||
            !string.Equals(meta.Container, "mp4", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(meta.Codec, "h264", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(meta.OutputPath))
        {
            return null;
        }

        string outputPath;
        try
        {
            outputPath = Path.GetFullPath(meta.OutputPath);
        }
        catch
        {
            return null;
        }

        var videoStreams = meta.ProbeStreams
            .Where(stream => string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (videoStreams.Length != 1 ||
            meta.ProbeStreams.Any(stream => !string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var video = videoStreams[0];
        if (!string.Equals(video.CodecName, "h264", StringComparison.OrdinalIgnoreCase) ||
            video.StartTimeSeconds is not double videoStart ||
            video.DurationSeconds is not double videoDuration ||
            video.TimeBaseSeconds is not double timeBase ||
            video.AverageFrameRate is not double averageFrameRate ||
            video.NominalFrameRate is not double nominalFrameRate ||
            video.FrameCount is not long declaredFrameCount ||
            declaredFrameCount != timeline.Packets.Count ||
            timeline.Packets.Count < 2 ||
            timeline.Packets.Count > checked(configuredDuration * config.Fps + 2) ||
            !AllFinite(meta.DurationSeconds, videoStart, videoDuration, timeBase, averageFrameRate, nominalFrameRate) ||
            timeBase <= 0 || timeBase > 0.001 ||
            averageFrameRate <= 0 || nominalFrameRate <= 0)
        {
            return null;
        }

        var ordered = timeline.Packets.OrderBy(packet => packet.PtsSeconds).ToArray();
        if (ordered.Any(packet => !double.IsFinite(packet.PtsSeconds) ||
                                  !double.IsFinite(packet.DurationSeconds) ||
                                  packet.PtsSeconds < 0 || packet.DurationSeconds <= 0))
        {
            return null;
        }

        var intervals = ordered.Zip(ordered.Skip(1), (first, second) => second.PtsSeconds - first.PtsSeconds).ToArray();
        if (intervals.Any(interval => !double.IsFinite(interval) || interval <= 0))
            return null;

        var packetDurations = ordered.Select(packet => packet.DurationSeconds).ToArray();
        var firstPts = ordered[0].PtsSeconds;
        var last = ordered[^1];
        var minimumInterval = intervals.Min();
        var maximumInterval = intervals.Max();
        var minimumPacketDuration = packetDurations.Min();
        var maximumPacketDuration = packetDurations.Max();
        var observedFrameRate = intervals.Length / (last.PtsSeconds - firstPts);
        var nominalPeriod = 1d / config.Fps;
        var timestampTolerance = timeBase + 1e-9;
        var formatTolerance = Math.Max(FormatDurationRoundingToleranceSeconds, timeBase * 2);
        var lastFrameEnd = last.PtsSeconds + last.DurationSeconds;

        if (!AllFinite(
                firstPts,
                last.PtsSeconds,
                last.DurationSeconds,
                minimumInterval,
                maximumInterval,
                minimumPacketDuration,
                maximumPacketDuration,
                observedFrameRate,
                lastFrameEnd) ||
            Math.Abs(firstPts) > timestampTolerance ||
            Math.Abs(videoStart) > timestampTolerance ||
            maximumInterval - minimumInterval > timestampTolerance ||
            maximumPacketDuration - minimumPacketDuration > timestampTolerance ||
            Math.Abs(minimumInterval - minimumPacketDuration) > timestampTolerance ||
            Math.Abs(maximumInterval - maximumPacketDuration) > timestampTolerance ||
            maximumPacketDuration > nominalPeriod * (1 + FrameRateDeviationRatio) + timestampTolerance ||
            Math.Abs(observedFrameRate - config.Fps) > config.Fps * FrameRateDeviationRatio ||
            Math.Abs(averageFrameRate - observedFrameRate) > observedFrameRate * FrameRateDeviationRatio ||
            Math.Abs(nominalFrameRate - observedFrameRate) > observedFrameRate * FrameRateDeviationRatio ||
            Math.Abs(videoDuration - lastFrameEnd) > timestampTolerance ||
            Math.Abs(meta.DurationSeconds - videoDuration) > formatTolerance ||
            last.PtsSeconds >= configuredDuration ||
            lastFrameEnd <= configuredDuration ||
            videoDuration <= configuredDuration ||
            meta.DurationSeconds - configuredDuration > last.DurationSeconds + formatTolerance ||
            Math.Abs(meta.DurationSeconds - lastFrameEnd) > formatTolerance)
        {
            return null;
        }

        return new FfmpegQuantizedTailEvidence(
            outputPath,
            afterProbeLength,
            afterProbeLastWriteUtcTicks,
            configuredDuration,
            config.Fps,
            meta.DurationSeconds,
            videoDuration,
            videoStart,
            timeBase,
            averageFrameRate,
            nominalFrameRate,
            ordered.LongLength,
            firstPts,
            last.PtsSeconds,
            last.DurationSeconds,
            minimumInterval,
            maximumInterval,
            minimumPacketDuration,
            maximumPacketDuration);
    }

    internal bool Matches(OutputMeta meta, string authorizedOutputPath, TimeSpan authorizedDuration)
    {
        if (authorizedDuration.Ticks != checked((long)(AuthorizedDurationSeconds * TimeSpan.TicksPerSecond)) ||
            !meta.OutputFileExists || meta.SizeBytes != OutputSizeBytes ||
            !double.IsFinite(meta.DurationSeconds) || meta.DurationSeconds != FormatDurationSeconds ||
            !string.Equals(meta.Container, "mp4", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(meta.Codec, "h264", StringComparison.OrdinalIgnoreCase) ||
            meta.HasAudioStream || meta.Fps != ConfiguredFrameRate ||
            meta.ProbeFileLastWriteUtcTicks != OutputLastWriteUtcTicks ||
            meta.ProbeStreams.Length != 1 ||
            !string.Equals(meta.ProbeStreams[0].CodecType, "video", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(meta.ProbeStreams[0].CodecName, "h264", StringComparison.OrdinalIgnoreCase) ||
            meta.ProbeStreams[0].StartTimeSeconds != VideoStartTimeSeconds ||
            meta.ProbeStreams[0].DurationSeconds != VideoStreamDurationSeconds ||
            meta.ProbeStreams[0].TimeBaseSeconds != TimeBaseSeconds ||
            meta.ProbeStreams[0].AverageFrameRate != AverageFrameRate ||
            meta.ProbeStreams[0].NominalFrameRate != NominalFrameRate ||
            meta.ProbeStreams[0].FrameCount != FrameCount)
        {
            return false;
        }

        string metadataPath;
        string authorizedPath;
        try
        {
            metadataPath = Path.GetFullPath(meta.OutputPath ?? string.Empty);
            authorizedPath = Path.GetFullPath(authorizedOutputPath);
        }
        catch
        {
            return false;
        }

        return string.Equals(OutputPath, metadataPath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(OutputPath, authorizedPath, StringComparison.OrdinalIgnoreCase) &&
               meta.DurationSeconds > authorizedDuration.TotalSeconds &&
               LastFramePtsSeconds < authorizedDuration.TotalSeconds &&
               LastFramePtsSeconds + LastPacketDurationSeconds > authorizedDuration.TotalSeconds &&
               MaximumPacketDurationSeconds >= LastPacketDurationSeconds &&
               MinimumPacketDurationSeconds <= LastPacketDurationSeconds &&
               MinimumFrameIntervalSeconds > 0 &&
               MaximumFrameIntervalSeconds >= MinimumFrameIntervalSeconds;
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);
}

internal sealed record FfmpegPacketTiming(double PtsSeconds, double DurationSeconds);

internal sealed record FfmpegPacketTimeline(IReadOnlyList<FfmpegPacketTiming> Packets);
