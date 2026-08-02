using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class VideoCaptureWorkerAnchorTests
{
    [Fact]
    public void SuccessfulProcessStart_RecordsNonzeroLaunchAnchor()
    {
        var process = new FakeVideoCaptureProcess();
        using var worker = CreateWorker(process, () => 123_456);

        worker.Start(new CaptureConfig(), CreateOutputPath());

        Assert.Equal(123_456, worker.LaunchAnchorTicks);
        Assert.Equal(0, worker.FirstFrameAnchorTicks);
    }

    [Fact]
    public void FailedProcessStart_LeavesLaunchAnchorAtZero()
    {
        var process = new FakeVideoCaptureProcess { ThrowOnStart = true };
        using var worker = CreateWorker(process, () => 123_456);

        Assert.Throws<ApiException>(() => worker.Start(new CaptureConfig(), CreateOutputPath()));
        Assert.Equal(0, worker.LaunchAnchorTicks);
    }

    [Fact]
    public void RepeatedStart_ResetsPreviousAnchorAndProgressState()
    {
        var firstProcess = new FakeVideoCaptureProcess();
        var secondProcess = new FakeVideoCaptureProcess();
        var processes = new Queue<FakeVideoCaptureProcess>(new[] { firstProcess, secondProcess });
        var timestamps = new Queue<long>(new[] { 100L, 110L, 200L });
        using var worker = CreateWorker(
            process: null,
            timestampProvider: () => timestamps.Dequeue(),
            processFactory: _ => processes.Dequeue());

        worker.Start(new CaptureConfig(), CreateOutputPath());
        worker.HandleProgressGroup(CreateProgressGroup(2, 10, 0));
        Assert.Equal(2, worker.FirstProgressFrame);

        worker.Start(new CaptureConfig(), CreateOutputPath());

        Assert.Equal(200, worker.LaunchAnchorTicks);
        Assert.Equal(0, worker.FirstFrameAnchorTicks);
        Assert.Null(worker.FirstProgressFrame);
        Assert.Null(worker.FirstProgressOutTimeUs);
        Assert.Null(worker.ProgressAnchorDeltaMs);
    }

    [Fact]
    public void FirstProgressWithZeroOutTime_RaisesEvidenceButKeepsProgressAnchorUnavailable()
    {
        var firstFrameCount = 0;
        var process = new FakeVideoCaptureProcess();
        using var worker = CreateWorker(process, () => 2_000);
        worker.FirstFrameObserved += _ => firstFrameCount++;

        worker.Start(new CaptureConfig(), CreateOutputPath());
        worker.HandleProgressGroup(CreateProgressGroup(20, 48, 0));

        Assert.Equal(1, firstFrameCount);
        Assert.Equal(2_000, worker.LaunchAnchorTicks);
        Assert.Equal(20, worker.FirstProgressFrame);
        Assert.Equal(0, worker.FirstProgressOutTimeUs);
        Assert.Equal(0, worker.FirstFrameAnchorTicks);
        Assert.Null(worker.ProgressAnchorDeltaMs);
    }

    [Fact]
    public void DelayedPositiveProgress_DoesNotMoveLaunchAnchor()
    {
        var launch = Stopwatch.Frequency * 10L;
        var timestamps = new Queue<long>(new[]
        {
            launch,
            launch + Stopwatch.Frequency / 4,
            launch + Stopwatch.Frequency / 2,
            launch + Stopwatch.Frequency * 3 / 2
        });
        var process = new FakeVideoCaptureProcess();
        using var worker = CreateWorker(process, () => timestamps.Dequeue());

        worker.Start(new CaptureConfig(), CreateOutputPath());
        worker.HandleProgressGroup(CreateProgressGroup(20, 48, 0));
        worker.HandleProgressGroup(CreateProgressGroup(21, 2048, 200_000));
        var progressAnchor = worker.FirstFrameAnchorTicks;
        worker.HandleProgressGroup(CreateProgressGroup(22, 4096, 800_000));

        Assert.Equal(launch, worker.LaunchAnchorTicks);
        Assert.Equal(progressAnchor, worker.FirstFrameAnchorTicks);
        Assert.InRange(worker.ProgressAnchorDeltaMs!.Value, 299.9, 300.1);
    }

    [Fact]
    public void ImmediateProgress_RecordsAccurateBoundedAnchorDelta()
    {
        var launch = Stopwatch.Frequency * 20L;
        var timestamps = new Queue<long>(new[]
        {
            launch,
            launch + Stopwatch.Frequency * 4 / 10
        });
        var process = new FakeVideoCaptureProcess();
        using var worker = CreateWorker(process, () => timestamps.Dequeue());

        worker.Start(new CaptureConfig(), CreateOutputPath());
        worker.HandleProgressGroup(CreateProgressGroup(1, 1024, 400_000));

        Assert.Equal(launch, worker.FirstFrameAnchorTicks);
        Assert.Equal(0, worker.ProgressAnchorDeltaMs);
        Assert.InRange(worker.ProgressAnchorDeltaMs!.Value, -86_400_000, 86_400_000);
    }

    [Fact]
    public void ProcessStartWithoutFrameEvidence_DoesNotRaiseFirstFrame()
    {
        var firstFrameCount = 0;
        var process = new FakeVideoCaptureProcess();
        using var worker = CreateWorker(process, () => 456_789);
        worker.FirstFrameObserved += _ => firstFrameCount++;

        worker.Start(new CaptureConfig(), CreateOutputPath());

        Assert.Equal(0, firstFrameCount);
        Assert.Null(worker.FirstProgressFrame);
        Assert.Equal(0, worker.FirstFrameAnchorTicks);
    }

    [Fact]
    public void AcceptanceDurations_KeepLaunchCoverageSeparateFromProgressLatency()
    {
        const double videoSeconds = 15.034;
        const double audioSeconds = 18.360;
        const double progressDerivedPreRollSeconds = 3.879;
        const double launchAnchorPreRollSeconds = 3.100;

        var oldProgressDelta = audioSeconds - (progressDerivedPreRollSeconds + videoSeconds);
        var launchDelta = audioSeconds - (launchAnchorPreRollSeconds + videoSeconds);
        var genuinelyShortAudioDelta = 17.700 - (launchAnchorPreRollSeconds + videoSeconds);

        Assert.Equal(-0.553, oldProgressDelta, 3);
        Assert.True(launchDelta >= -AvFinalizer.AudioCoverageToleranceSeconds);
        Assert.True(genuinelyShortAudioDelta < -AvFinalizer.AudioCoverageToleranceSeconds);
    }

    private static VideoCaptureWorker CreateWorker(
        FakeVideoCaptureProcess? process,
        Func<long> timestampProvider,
        Func<System.Diagnostics.ProcessStartInfo, IVideoCaptureProcess>? processFactory = null)
    {
        var worker = new VideoCaptureWorker
        {
            TestArgumentsOverride = new List<string> { "-nostdin" },
            TimestampProvider = timestampProvider,
            TestProcessFactory = processFactory ?? (_ => process!)
        };
        return worker;
    }

    private static string CreateOutputPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-recorder-video-anchor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "video.mp4");
    }

    private static FFmpegProgressGroup CreateProgressGroup(long frame, long totalSize, long? outTimeUs)
    {
        var values = new Dictionary<string, string>
        {
            ["frame"] = frame.ToString(),
            ["total_size"] = totalSize.ToString(),
            ["progress"] = "continue"
        };
        if (outTimeUs.HasValue)
            values["out_time_us"] = outTimeUs.Value.ToString();
        return new FFmpegProgressGroup(values);
    }

    private sealed class FakeVideoCaptureProcess : IVideoCaptureProcess
    {
        private readonly ProcessStartInfo _startInfo = new();
        private readonly StreamReader _standardOutput = new(new MemoryStream(Array.Empty<byte>()), Encoding.UTF8);
        private readonly StreamWriter _standardInput = new(Stream.Null, Encoding.UTF8);

        public event DataReceivedEventHandler? ErrorDataReceived
        {
            add { }
            remove { }
        }

        public bool ThrowOnStart { get; set; }
        public ProcessStartInfo StartInfo => _startInfo;
        public StreamReader StandardOutput => _standardOutput;
        public StreamWriter StandardInput => _standardInput;
        public bool HasExited => true;
        public int ExitCode => 0;
        public bool ErrorStreamClosed => true;

        public bool Start()
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("synthetic process start failure");
            return true;
        }

        public void BeginErrorReadLine() { }
        public bool WaitForExit(int milliseconds) => true;
        public bool WaitForExit(TimeSpan timeout) => true;
        public void Kill(bool entireProcessTree) { }
        public void Dispose()
        {
            _standardOutput.Dispose();
            _standardInput.Dispose();
        }
    }
}
