using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class FfmpegCaptureBackendTests : IDisposable
{
    private readonly string _tmpDir;

    public FfmpegCaptureBackendTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"ffmpeg-backend-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpDir))
                Directory.Delete(_tmpDir, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_IncludesProgressParamsOnce(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        Assert.Contains("-nostats", args);
        Assert.Contains("-progress", args);
        Assert.Contains("pipe:1", args);
        Assert.DoesNotContain("-stats_period", args);
        Assert.True(args.IndexOf("-progress") == args.LastIndexOf("-progress"), "-progress should appear exactly once");
        Assert.True(args.IndexOf("-nostats") == args.LastIndexOf("-nostats"), "-nostats should appear exactly once");
    }

    [Fact]
    public void BuildArgs_ProgressAppearsBeforeInput()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4"
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        var progressIndex = args.IndexOf("-progress");
        var inputIndex = args.IndexOf("-i");

        Assert.True(progressIndex >= 0);
        Assert.True(inputIndex >= 0);
        Assert.True(progressIndex < inputIndex, "-progress must be an output/global option, before -i");
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "mp4")]
    [InlineData("mp4", "mp4")]
    [InlineData("matroska,webm", "matroska")]
    public void NormalizeContainer_MapsFormatNameToContainer(string formatName, string expected)
    {
        var actual = InvokeNormalizeContainer(formatName);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("h264", "h264")]
    [InlineData("H264", "h264")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NormalizeCodec_LowersCodecName(string? codecName, string? expected)
    {
        var actual = InvokeNormalizeCodec(codecName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Probe_RealFfmpegMp4_PopulatesContainerAndCodec()
    {
        var ffmpegPath = FfmpegLocator.FfmpegPath;
        Assert.True(File.Exists(ffmpegPath), "Bundled FFmpeg not available.");

        var outputPath = Path.Combine(_tmpDir, "test-probe.mp4");
        GenerateTinyMp4(ffmpegPath, outputPath);

        var meta = FfmpegCaptureBackend.Probe(outputPath);

        Assert.Equal("mp4", meta.Container);
        Assert.Equal("h264", meta.Codec);
        Assert.True(meta.Width > 0);
        Assert.True(meta.Height > 0);
        Assert.True(meta.DurationSeconds > 0);
    }

    private static void GenerateTinyMp4(string ffmpegPath, string outputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-y -nostats -loglevel error -f lavfi -i testsrc=duration=1:size=320x240:rate=30 -pix_fmt yuv420p -c:v libx264 " + outputPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
        proc.BeginOutputReadLine();
        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(true); } catch { }
            throw new InvalidOperationException("ffmpeg test video generation timed out");
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg test video generation failed: " + proc.StandardError.ReadToEnd());
    }

    private static string? InvokeNormalizeContainer(string? formatName)
    {
        var method = typeof(FfmpegCaptureBackend).GetMethod("NormalizeContainer", BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object?[] { formatName });
    }

    private static string? InvokeNormalizeCodec(string? codecName)
    {
        var method = typeof(FfmpegCaptureBackend).GetMethod("NormalizeCodec", BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object?[] { codecName });
    }
}
