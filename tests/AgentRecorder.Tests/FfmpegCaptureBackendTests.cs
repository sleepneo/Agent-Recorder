using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class FfmpegCaptureBackendTests
{
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
        Assert.Contains("-progress pipe:1", args);
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

        var progressIndex = args.IndexOf("-progress pipe:1");
        var inputIndex = args.IndexOf("-i desktop");

        Assert.True(progressIndex >= 0);
        Assert.True(inputIndex >= 0);
        Assert.True(progressIndex < inputIndex, "-progress pipe:1 must be an output/global option, before -i");
    }
}
