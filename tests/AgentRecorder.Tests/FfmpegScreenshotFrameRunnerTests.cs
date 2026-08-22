using System.Diagnostics;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FfmpegScreenshotFrameRunnerTests
{
    [Fact]
    public void CommandPlan_IsOneFinitePngFrame_UsesLowLatencyRateAndPreservesPathAsOneArgument()
    {
        var config = ScreenshotConfig("region", ( -1800, -100, 2070, 1360));
        var output = Path.Combine(Path.GetTempPath(), "series capture \"截图\".tmp");

        var arguments = FfmpegScreenshotFrameCommand.BuildArguments(config, output);
        var startInfo = FfmpegScreenshotFrameCommand.BuildStartInfo(config, output);

        Assert.Equal("gdigrab", arguments[ArgumentIndex(arguments, "-f") + 1]);
        Assert.Equal(FfmpegScreenshotFrameCommand.InputFrameRate.ToString(), arguments[ArgumentIndex(arguments, "-framerate") + 1]);
        Assert.NotEqual("1", arguments[ArgumentIndex(arguments, "-framerate") + 1]);
        Assert.Equal("1", arguments[ArgumentIndex(arguments, "-frames:v") + 1]);
        Assert.Equal("-c:v", arguments[ArgumentIndex(arguments, "png") - 1]);
        Assert.Equal("2070x1360", arguments[ArgumentIndex(arguments, "-video_size") + 1]);
        Assert.Equal(output, arguments[^1]);
        Assert.Equal(output, startInfo.ArgumentList[^1]);
        Assert.DoesNotContain("\\\"", startInfo.Arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("display", "display_surface")]
    [InlineData("region", "region_rectangle")]
    [InlineData("window", "screen_rectangle")]
    public async Task Runner_PreservesSourceSemanticsAndCoordinateSpace(string sourceKind, string semantics)
    {
        var process = new FakeScreenshotProcess { ExitCode = 0 };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);
        var request = Request(ScreenshotConfig(sourceKind, (0, 0, 32, 32)), semantics, "virtual_screen");

        var result = await runner.CaptureAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(process.Started);
        Assert.Equal(0, process.KillCalls);
    }

    [Fact]
    public async Task Runner_RejectsCoordinateSpaceDriftBeforeProcessStart()
    {
        var process = new FakeScreenshotProcess();
        var runner = new FfmpegScreenshotFrameRunner(_ => process);
        var result = await runner.CaptureAsync(Request(
            ScreenshotConfig("region", (0, 0, 32, 32)), "region_rectangle", "screen_pixels"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unsupported_capture_plan", result.ErrorCode);
        Assert.False(process.Started);
        Assert.Equal(0, process.KillCalls);
    }

    [Fact]
    public async Task Runner_NonzeroExitIsStableAndDoesNotKillExitedProcess()
    {
        var process = new FakeScreenshotProcess { ExitCode = 7 };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);

        var result = await runner.CaptureAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("frame_capture_failed", result.ErrorCode);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(0, process.KillCalls);
        Assert.Equal(1, process.WaitCalls);
    }

    [Fact]
    public async Task Runner_EmptySuccessfulOutputIsReturnedForPngValidationLayer()
    {
        var process = new FakeScreenshotProcess { ExitCode = 0 };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);

        var result = await runner.CaptureAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.SizeBytes);
    }

    [Fact]
    public async Task Runner_StartExceptionReturnsStableFailureAndReapsProcess()
    {
        var process = new FakeScreenshotProcess { StartException = new InvalidOperationException("start") };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);

        var result = await runner.CaptureAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("frame_capture_failed", result.ErrorCode);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, process.WaitCalls);
    }

    [Fact]
    public async Task Runner_TimeoutKillsAndReapsProcessTree()
    {
        var process = new FakeScreenshotProcess { WaitForever = true };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);

        var result = await runner.CaptureAsync(Request(timeout: TimeSpan.FromMilliseconds(30)), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("frame_timeout", result.ErrorCode);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(2, process.WaitCalls);
        Assert.True(process.KillEntireTree);
    }

    [Fact]
    public async Task Runner_CancellationKillsAndReapsProcessTree()
    {
        var process = new FakeScreenshotProcess { WaitForever = true };
        var runner = new FfmpegScreenshotFrameRunner(_ => process);
        using var cancellation = new CancellationTokenSource();
        var capture = runner.CaptureAsync(Request(timeout: TimeSpan.FromSeconds(5)), cancellation.Token);
        Assert.True(process.WaitEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var result = await capture.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal("capture_cancelled", result.ErrorCode);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(2, process.WaitCalls);
        Assert.True(process.KillEntireTree);
    }

    private static ScreenshotFrameRequest Request(
        string sourceKind = "region",
        string semantics = "region_rectangle",
        string coordinateSpace = "virtual_screen",
        TimeSpan? timeout = null)
        => Request(ScreenshotConfig(sourceKind, (0, 0, 32, 32)), semantics, coordinateSpace, timeout);

    private static ScreenshotFrameRequest Request(
        CaptureConfig config,
        string semantics,
        string coordinateSpace,
        TimeSpan? timeout = null)
        => new(config, Path.Combine(Path.GetTempPath(), "screenshot-series-test.tmp"),
            timeout ?? TimeSpan.FromSeconds(1), 1, "ffmpeg-single-frame", semantics,
            config.SourceKind, "target", coordinateSpace);

    private static CaptureConfig ScreenshotConfig(string sourceKind, (int x, int y, int w, int h) bounds)
        => new()
        {
            Mode = ScreenshotSeriesConfig.ModeName,
            ScreenshotSeries = new ScreenshotSeriesConfig { IntervalMs = 1000, MaxCount = 1, PlannedFrameCount = 1 },
            SourceKind = sourceKind,
            Bounds = bounds
        };

    private static int ArgumentIndex(IReadOnlyList<string> arguments, string value)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.Ordinal))
                return index;
        }

        throw new Xunit.Sdk.XunitException($"Argument '{value}' was not found.");
    }

    private sealed class FakeScreenshotProcess : IScreenshotFrameProcess
    {
        public bool Started { get; private set; }
        public bool HasExited { get; private set; }
        public int ExitCode { get; init; } = 0;
        public int KillCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public bool KillEntireTree { get; private set; }
        public bool WaitForever { get; init; }
        public Exception? StartException { get; init; }
        public ManualResetEventSlim WaitEntered { get; } = new(false);

        public void Start()
        {
            if (StartException != null)
                throw StartException;
            Started = true;
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            if (HasExited)
                return;
            if (WaitForever)
            {
                WaitEntered.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            HasExited = true;
        }

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult("");

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult("");

        public void Kill(bool entireProcessTree)
        {
            KillCalls++;
            KillEntireTree = entireProcessTree;
            HasExited = true;
        }

        public void Dispose() => WaitEntered.Dispose();
    }
}
