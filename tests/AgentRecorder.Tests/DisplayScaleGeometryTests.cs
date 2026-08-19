using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class DisplayScaleGeometryTests
{
    [Theory]
    [InlineData(2000, 1268, 1702, 1080)]
    [InlineData(3840, 1080, 1920, 540)]
    [InlineData(1920, 2160, 960, 1080)]
    [InlineData(1921, 1081, 1918, 1080)]
    [InlineData(2001, 1001, 1920, 960)]
    public void DisplayScale_ProducesPositiveEvenDimensions(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var result = DisplayScaleGeometry.GetOutputSize(sourceWidth, sourceHeight);

        Assert.True(result.HasValue);
        Assert.Equal(expectedWidth, result.Value.Width);
        Assert.Equal(expectedHeight, result.Value.Height);
        Assert.True(result.Value.Width > 0 && (result.Value.Width & 1) == 0);
        Assert.True(result.Value.Height > 0 && (result.Value.Height & 1) == 0);
        Assert.True(result.Value.Width <= sourceWidth && result.Value.Height <= sourceHeight);
    }

    [Fact]
    public void DisplayScale_Exact1920x1080_DoesNotAddFilter()
    {
        Assert.Null(DisplayScaleGeometry.GetOutputSize(1920, 1080));

        var cfg = CreateConfig("display", 0, 0, 1920, 1080);
        var ffmpegArgs = FfmpegCaptureBackend.BuildArgs(cfg);
        var workerArgs = BuildVideoWorkerArgs(cfg);

        Assert.DoesNotContain("-vf", ffmpegArgs);
        Assert.DoesNotContain("-vf", workerArgs);
    }

    [Theory]
    [InlineData(2000, 1268, "scale=1702:1080")]
    [InlineData(3840, 1080, "scale=1920:540")]
    [InlineData(1920, 2160, "scale=960:1080")]
    [InlineData(1921, 1081, "scale=1918:1080")]
    public void BothFfmpegArgumentBuilders_UseTheSameConcreteEvenFilter(
        int sourceWidth,
        int sourceHeight,
        string expectedFilter)
    {
        var cfg = CreateConfig("display", 0, 0, sourceWidth, sourceHeight);
        var ffmpegArgs = FfmpegCaptureBackend.BuildArgs(cfg);
        var workerArgs = BuildVideoWorkerArgs(cfg);

        Assert.Equal(expectedFilter, GetArgumentAfter(ffmpegArgs, "-vf"));
        Assert.Equal(expectedFilter, GetArgumentAfter(workerArgs, "-vf"));
        Assert.DoesNotContain("force_original_aspect_ratio", expectedFilter);
        Assert.DoesNotContain("1703:1080", expectedFilter);
    }

    [Fact]
    public void RegionAndWindow_KeepPhysicalBoundsWithoutScaling()
    {
        foreach (var sourceKind in new[] { "region", "window" })
        {
            var cfg = CreateConfig(sourceKind, 897, 366, 2000, 1268);
            var ffmpegArgs = FfmpegCaptureBackend.BuildArgs(cfg);
            var workerArgs = BuildVideoWorkerArgs(cfg);

            Assert.DoesNotContain("-vf", ffmpegArgs);
            Assert.DoesNotContain("-vf", workerArgs);
            Assert.Equal("2000x1268", GetArgumentAfter(ffmpegArgs, "-video_size"));
            Assert.Equal("2000x1268", GetArgumentAfter(workerArgs, "-video_size"));
            Assert.Equal("897", GetArgumentAfter(ffmpegArgs, "-offset_x"));
            Assert.Equal("366", GetArgumentAfter(ffmpegArgs, "-offset_y"));
            Assert.Equal("897", GetArgumentAfter(workerArgs, "-offset_x"));
            Assert.Equal("366", GetArgumentAfter(workerArgs, "-offset_y"));
        }
    }

    [Theory]
    [InlineData("region", 0, 0, 0, 480)]
    [InlineData("region", 0, 0, 641, 480)]
    [InlineData("window", 0, 0, 640, 481)]
    [InlineData("display", 0, 0, 0, 1080)]
    public void InvalidOrOddPhysicalBounds_FailBeforeArgumentConstruction(
        string sourceKind,
        int x,
        int y,
        int width,
        int height)
    {
        var cfg = CreateConfig(sourceKind, x, y, width, height);

        Assert.Throws<ArgumentException>(() => FfmpegCaptureBackend.BuildArgs(cfg));
    }

    [Fact]
    public void BundledFfmpeg_SyntheticScaleFilter_Accepts2000x1268Replacement()
    {
        if (!File.Exists(FfmpegLocator.FfmpegPath))
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-y", "-nostats", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=0.2:size=2000x1268:rate=5",
            "-vf", "scale=1702:1080", "-frames:v", "1", "-f", "null", "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "synthetic FFmpeg filter verification timed out");
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("width not divisible by 2", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static CaptureConfig CreateConfig(string sourceKind, int x, int y, int width, int height)
        => new()
        {
            SourceKind = sourceKind,
            Bounds = (x, y, width, height),
            Fps = 30,
            Quality = "medium",
            OutputPath = Path.Combine(Path.GetTempPath(), "display-scale-test.mp4")
        };

    private static List<string> BuildVideoWorkerArgs(CaptureConfig cfg)
    {
        var method = typeof(VideoCaptureWorker).GetMethod(
            "BuildArgs",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (List<string>)method!.Invoke(null, new object[] { cfg, cfg.OutputPath })!;
    }

    private static string GetArgumentAfter(IReadOnlyList<string> args, string name)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name)
            {
                index = i;
                break;
            }
        }
        Assert.True(index >= 0, $"Expected argument '{name}'.");
        Assert.True(index + 1 < args.Count, $"Argument '{name}' has no value.");
        return args[index + 1];
    }
}
