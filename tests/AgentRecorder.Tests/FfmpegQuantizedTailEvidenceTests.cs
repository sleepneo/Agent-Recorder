using System.Diagnostics;
using System.Globalization;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FfmpegQuantizedTailEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-recorder-task286r-evidence-" + Guid.NewGuid().ToString("N"));

    public FfmpegQuantizedTailEvidenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ProbeCreatesBoundedEvidenceOnlyForTrustedSingleVideoTail()
    {
        var config = CreateConfig(Path.Combine(_root, "quantized-299-clock.mp4"));
        QuantizedTailTestMedia.Generate(config.OutputPath, 299, "179/6");

        var meta = FfmpegCaptureBackend.ProbeAuthorizedFixedRateCapture(config.OutputPath, config);
        var video = Assert.Single(meta.ProbeStreams);
        Assert.True(meta.OutputFileExists);
        Assert.Equal(299, video.FrameCount);
        Assert.True(video.StartTimeSeconds < 10);
        Assert.True(video.DurationSeconds > 10);
        Assert.True(meta.DurationSeconds > 10 && meta.DurationSeconds < 10.04, $"format.duration={meta.DurationSeconds:R}");
        Assert.NotNull(meta.QuantizedTailEvidence);

        var evidence = meta.QuantizedTailEvidence!;
        Assert.True(evidence.Matches(meta, config.OutputPath, TimeSpan.FromSeconds(10)));
        Assert.Null(FfmpegCaptureBackend.Probe(config.OutputPath).QuantizedTailEvidence);
        Assert.False(evidence.Matches(meta, config.OutputPath + ".different", TimeSpan.FromSeconds(10)));
        Assert.False(evidence.Matches(Clone(meta, outputPath: config.OutputPath + ".different"), config.OutputPath, TimeSpan.FromSeconds(10)));
        Assert.False(evidence.Matches(Clone(meta, durationSeconds: 10.1), config.OutputPath, TimeSpan.FromSeconds(10)));
        Assert.False(evidence.Matches(Clone(meta, fileStamp: (meta.ProbeFileLastWriteUtcTicks ?? 0) + 1), config.OutputPath, TimeSpan.FromSeconds(10)));
        Assert.False(evidence.Matches(Clone(meta, hasAudio: true), config.OutputPath, TimeSpan.FromSeconds(10)));

        var timeline = CreateTimeline();
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            Clone(meta, durationSeconds: 10), config, timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks!.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, timeline.Packets.Count - 1, new FfmpegPacketTiming(10, 6d / 179)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, timeline.Packets.Count - 1, new FfmpegPacketTiming(9.988827, 0.15)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, 20, new FfmpegPacketTiming(double.NaN, 6d / 179)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, 20, new FfmpegPacketTiming(20 * 6d / 179, double.NaN)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, 20, new FfmpegPacketTiming(-1, 6d / 179)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, 20, new FfmpegPacketTiming(20 * 6d / 179, -0.01)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, WithPacket(timeline, 20, new FfmpegPacketTiming(timeline.Packets[19].PtsSeconds, 6d / 179)), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, new FfmpegPacketTimeline(timeline.Packets.Take(timeline.Packets.Count - 1).ToArray()), true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, timeline, false,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, CreateConfig(config.OutputPath, sourceKind: "display"), timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, CreateConfig(config.OutputPath, fps: 60), timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, CreateConfig(config.OutputPath, audio: true), timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            meta, config, timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes + 1, meta.ProbeFileLastWriteUtcTicks.Value));

        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            Clone(meta, durationSeconds: double.NaN), config, timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            Clone(meta, durationSeconds: -1), config, timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));

        var missingTimeBase = Clone(meta);
        missingTimeBase.ProbeStreams.Single().TimeBaseSeconds = null;
        Assert.Null(FfmpegQuantizedTailEvidence.TryCreate(
            missingTimeBase, config, timeline, true,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value,
            meta.SizeBytes, meta.ProbeFileLastWriteUtcTicks.Value));
    }

    private static CaptureConfig CreateConfig(string outputPath, string sourceKind = "region", int fps = 30, bool audio = false) => new()
    {
        SourceKind = sourceKind,
        Mode = "video",
        Bounds = (0, 0, 640, 480),
        Fps = fps,
        OutputPath = outputPath,
        OutputConflictPolicy = "fail_if_exists",
        DurationSeconds = 10,
        AudioSourceKind = audio ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.None,
        Microphone = audio,
    };

    private static OutputMeta Clone(
        OutputMeta source,
        string? outputPath = null,
        double? durationSeconds = null,
        long? fileStamp = null,
        bool? hasAudio = null)
    {
        var clone = new OutputMeta
        {
            SizeBytes = source.SizeBytes,
            DurationSeconds = durationSeconds ?? source.DurationSeconds,
            Fps = source.Fps,
            OutputPath = outputPath ?? source.OutputPath,
            Container = source.Container,
            Codec = source.Codec,
            OutputFileExists = source.OutputFileExists,
            HasAudioStream = hasAudio ?? source.HasAudioStream,
            ProbeStreams = source.ProbeStreams.Select(stream => new ProbeStreamInfo
            {
                Index = stream.Index,
                CodecType = stream.CodecType,
                CodecName = stream.CodecName,
                StartTimeSeconds = stream.StartTimeSeconds,
                DurationSeconds = stream.DurationSeconds,
                TimeBaseSeconds = stream.TimeBaseSeconds,
                AverageFrameRate = stream.AverageFrameRate,
                NominalFrameRate = stream.NominalFrameRate,
                FrameCount = stream.FrameCount,
            }).ToArray(),
        };
        clone.ProbeFileLastWriteUtcTicks = fileStamp ?? source.ProbeFileLastWriteUtcTicks;
        return clone;
    }

    private static FfmpegPacketTimeline CreateTimeline()
    {
        const double framePeriod = 6d / 179;
        return new FfmpegPacketTimeline(Enumerable.Range(0, 299)
            .Select(index => new FfmpegPacketTiming(index * framePeriod, framePeriod))
            .ToArray());
    }

    private static FfmpegPacketTimeline WithPacket(FfmpegPacketTimeline timeline, int index, FfmpegPacketTiming packet)
    {
        var packets = timeline.Packets.ToArray();
        packets[index] = packet;
        return new FfmpegPacketTimeline(packets);
    }
}

internal static class QuantizedTailTestMedia
{
    internal static void Generate(string outputPath, int frameCount, string inputFrameRate)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
            throw new InvalidOperationException("The isolated FFmpeg sample output path was not fresh.");

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-n", "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", $"color=c=black:s=640x480:r={inputFrameRate}",
                     "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
                     "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p",
                     "-movflags", "+faststart", fullPath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the isolated FFmpeg sample generator.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("The isolated FFmpeg sample generator timed out.");
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg sample generation failed ({process.ExitCode}): {stderr}");
    }
}
