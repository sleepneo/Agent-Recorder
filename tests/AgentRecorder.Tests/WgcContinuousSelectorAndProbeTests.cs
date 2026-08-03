using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-WindowBackend")]
public sealed class WgcContinuousSelectorTests
{
    [Fact]
    public void DisplayFlagUnset_UsesFfmpeg_WithoutProbe()
    {
        var probe = new FakeAvailabilityProbe(true);
        var result = WithDisplayFlag(null, () =>
            CaptureBackendSelector.Select(EligibleConfig(), probe));

        Assert.Equal("ffmpeg", result.BackendType);
        Assert.IsType<FfmpegCaptureBackend>(result.Backend);
        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ffmpeg")]
    [InlineData("unknown")]
    public void DisplayFlagNonExact_UsesFfmpeg_WithoutProbe(string flag)
    {
        var probe = new FakeAvailabilityProbe(true);
        var result = WithDisplayFlag(flag, () =>
            CaptureBackendSelector.Select(EligibleConfig(), probe));

        Assert.Equal("ffmpeg", result.BackendType);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void ExactDisplayFlagAndHealthyProbe_UsesWgcContinuous()
    {
        var probe = new FakeAvailabilityProbe(true);
        var result = WithDisplayFlag("  WGC-CONTINUOUS  ", () =>
            CaptureBackendSelector.Select(EligibleConfig(), probe));

        Assert.Equal("wgc-continuous", result.BackendType);
        Assert.IsType<WgcContinuousCaptureBackend>(result.Backend);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void DisplayWithMicrophone_UsesAvSplit_WithoutProbe()
    {
        var config = EligibleConfig();
        config.Microphone = true;
        var probe = new FakeAvailabilityProbe(true);

        var result = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.Select(config, probe));

        Assert.Equal("ffmpeg-av-split", result.BackendType);
        Assert.IsType<AvSplitCaptureBackend>(result.Backend);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void InvalidWgcCandidate_UsesOriginalBackend_WithoutProbe()
    {
        var configs = new[]
        {
            ConfigWith(duration: null),
            ConfigWith(duration: 0),
            ConfigWith(duration: 11),
            ConfigWith(fps: 0),
            ConfigWith(fps: 61),
            ConfigWith(bounds: (0, 0, 0, 1080)),
            ConfigWith(bounds: (0, 0, 1920, 0)),
        };

        foreach (var config in configs)
        {
            var probe = new FakeAvailabilityProbe(true);
            var result = WithDisplayFlag("wgc-continuous", () =>
                CaptureBackendSelector.Select(config, probe));

            Assert.Equal("ffmpeg", result.BackendType);
            Assert.IsType<FfmpegCaptureBackend>(result.Backend);
            Assert.Equal(0, probe.CallCount);
        }
    }

    [Fact]
    public void UnhealthyProbe_UsesOriginalFfmpegBackend()
    {
        var probe = new FakeAvailabilityProbe(false);
        var result = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.Select(EligibleConfig(), probe));

        Assert.Equal("ffmpeg", result.BackendType);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void ProbeException_UsesOriginalFfmpegBackend()
    {
        var result = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.Select(EligibleConfig(), new ThrowingAvailabilityProbe()));

        Assert.Equal("ffmpeg", result.BackendType);
        Assert.IsType<FfmpegCaptureBackend>(result.Backend);
    }

    [Fact]
    public void SelectAndSelectBackendType_ShareTheSameDecision()
    {
        var healthyProbe = new FakeAvailabilityProbe(true);
        var healthy = WithDisplayFlag("wgc-continuous", () =>
        {
            var selected = CaptureBackendSelector.Select(EligibleConfig(), healthyProbe);
            var type = CaptureBackendSelector.SelectBackendType(EligibleConfig(), healthyProbe);
            return (selected.BackendType, type);
        });
        Assert.Equal(healthy.BackendType, healthy.type);

        var unhealthyProbe = new FakeAvailabilityProbe(false);
        var unhealthy = WithDisplayFlag("wgc-continuous", () =>
        {
            var selected = CaptureBackendSelector.Select(EligibleConfig(), unhealthyProbe);
            var type = CaptureBackendSelector.SelectBackendType(EligibleConfig(), unhealthyProbe);
            return (selected.BackendType, type);
        });
        Assert.Equal(unhealthy.BackendType, unhealthy.type);
    }

    [Fact]
    public void SelectingWgcBackend_DoesNotStartSessionOrCreateFrames()
    {
        var probe = new FakeAvailabilityProbe(true);
        var result = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.Select(EligibleConfig(), probe));

        var backend = Assert.IsType<WgcContinuousCaptureBackend>(result.Backend);
        Assert.Equal("Created", backend.LifecycleStateNameForTests);
        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
    }

    private static CaptureConfig EligibleConfig() => ConfigWith();

    private static CaptureConfig ConfigWith(
        int? duration = 5,
        int fps = 30,
        (int x, int y, int w, int h)? bounds = null) => new()
    {
        SourceKind = "display",
        Bounds = bounds ?? (-1920, 0, 1920, 1080),
        DurationSeconds = duration,
        Fps = fps,
        OutputPath = "C:\\temp\\recording.mp4",
    };

    private static T WithDisplayFlag<T>(string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, previous);
        }
    }

    private sealed class FakeAvailabilityProbe : IWgcContinuousAvailabilityProbe
    {
        private readonly bool _available;

        public FakeAvailabilityProbe(bool available) => _available = available;

        public int CallCount { get; private set; }

        public WgcContinuousAvailabilityResult Check(CaptureConfig config)
        {
            CallCount++;
            return new WgcContinuousAvailabilityResult(_available, _available ? "available" : "probe_timeout");
        }
    }

    private sealed class ThrowingAvailabilityProbe : IWgcContinuousAvailabilityProbe
    {
        public WgcContinuousAvailabilityResult Check(CaptureConfig config) =>
            throw new InvalidOperationException("synthetic probe parser failure");
    }
}

[Collection("NonParallel-WindowBackend")]
public sealed class WgcContinuousAvailabilityProbeTests
{
    private static readonly (int x, int y, int w, int h) Bounds = (10, -20, 1920, 1080);

    [Fact]
    public void HealthyProbe_UsesVersionAndProbeArgumentsAndBoundedTimeouts()
    {
        var runner = new FakeProbeProcessRunner(
            new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput = "wgc-native-helper 0.1.0\n",
            },
            new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput = HealthyProbeOutput(),
            });
        var probe = CreateProbe(runner);

        var result = probe.Check(EligibleConfig());

        Assert.True(result.Available);
        Assert.Equal("available", result.ReasonCode);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("fake-helper.exe", runner.Calls[0].FileName);
        Assert.Equal(new[] { "--version" }, runner.Calls[0].Arguments);
        Assert.Equal(WgcContinuousAvailabilityProbe.VersionTimeoutMs, runner.Calls[0].TimeoutMs);
        Assert.Equal(new[] { "--probe" }, runner.Calls[1].Arguments);
        Assert.Equal(WgcContinuousAvailabilityProbe.ProbeTimeoutMs, runner.Calls[1].TimeoutMs);
    }

    [Fact]
    public void MissingHelper_ReturnsUnavailableWithoutStartingProcess()
    {
        var runner = new FakeProbeProcessRunner();
        var probe = new WgcContinuousAvailabilityProbe(
            () => throw new FileNotFoundException(), runner);

        var result = probe.Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal("helper_missing", result.ReasonCode);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("version_nonzero_exit")]
    [InlineData("version_timeout")]
    [InlineData("version_output_invalid")]
    public void VersionFailure_ReturnsUnavailable(string expectedReason)
    {
        WgcHelperProcessResult version = expectedReason switch
        {
            "version_nonzero_exit" => new() { ExitCode = 1, StandardOutput = "wgc-native-helper 0.1.0\n" },
            "version_timeout" => new() { ExitCode = -1, TimedOut = true },
            _ => new() { ExitCode = 0, StandardOutputTruncated = true },
        };
        var runner = new FakeProbeProcessRunner(version);
        var probe = CreateProbe(runner);

        var result = probe.Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void IncompatibleVersion_ReturnsUnavailable()
    {
        var runner = new FakeProbeProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "wgc-native-helper 9.9.9\n",
        });
        var result = CreateProbe(runner).Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal("version_incompatible", result.ReasonCode);
    }

    [Theory]
    [InlineData("probe_nonzero_exit")]
    [InlineData("probe_timeout")]
    [InlineData("probe_output_invalid")]
    [InlineData("probe_bounds_mismatch")]
    public void ProbeFailure_ReturnsUnavailable(string expectedReason)
    {
        var probeResult = expectedReason switch
        {
            "probe_nonzero_exit" => new WgcHelperProcessResult { ExitCode = 1, StandardOutput = HealthyProbeOutput() },
            "probe_timeout" => new WgcHelperProcessResult { ExitCode = -1, TimedOut = true },
            "probe_output_invalid" => new WgcHelperProcessResult { ExitCode = 0, StandardOutput = "RESULT: OK\n" },
            _ => new WgcHelperProcessResult { ExitCode = 0, StandardOutput = HealthyProbeOutput((0, 0, 800, 600)) },
        };
        var runner = new FakeProbeProcessRunner(
            new WgcHelperProcessResult { ExitCode = 0, StandardOutput = "wgc-native-helper 0.1.0\n" },
            probeResult);

        var result = CreateProbe(runner).Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("DpiAwareness", "system_aware", "probe_dpi_mismatch")]
    [InlineData("WgcSupported", "false", "probe_wgc_unsupported")]
    [InlineData("D3d11Initialized", "false", "probe_d3d11_uninitialized")]
    [InlineData("EncoderCreated", "false", "probe_encoder_unavailable")]
    public void RequiredCapabilityFailure_ReturnsUnavailable(
        string field,
        string value,
        string expectedReason)
    {
        string output = HealthyProbeOutput().Replace(
            field + ": " + (field == "DpiAwareness" ? "per_monitor_v2" : "true"),
            field + ": " + value,
            StringComparison.Ordinal);
        var runner = new FakeProbeProcessRunner(
            new WgcHelperProcessResult { ExitCode = 0, StandardOutput = "wgc-native-helper 0.1.0\n" },
            new WgcHelperProcessResult { ExitCode = 0, StandardOutput = output });

        var result = CreateProbe(runner).Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal(expectedReason, result.ReasonCode);
    }

    [Fact]
    public void RunnerException_IsolatedAsUnavailableWithoutLeakingException()
    {
        var runner = new FakeProbeProcessRunner(
            new WgcHelperProcessResult { ExitCode = 0, StandardOutput = "wgc-native-helper 0.1.0\n" })
        {
            ThrowOnCall = 2,
        };
        var result = CreateProbe(runner).Check(EligibleConfig());

        Assert.False(result.Available);
        Assert.Equal("probe_start_failed", result.ReasonCode);
    }

    private static CaptureConfig EligibleConfig() => new()
    {
        SourceKind = "display",
        Bounds = Bounds,
        DurationSeconds = 5,
        Fps = 30,
    };

    private static WgcContinuousAvailabilityProbe CreateProbe(FakeProbeProcessRunner runner) =>
        new(
            () => "fake-helper.exe",
            runner,
            _ => new WgcContinuousAvailabilityProbe.WgcHelperFileIdentity(
                "C:\\wgc-probe-tests\\fake-helper.exe",
                7,
                new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)),
            () => 1000,
            timestampFrequency: 1000,
            cacheTtl: TimeSpan.FromSeconds(30));

    private static string HealthyProbeOutput((int x, int y, int w, int h)? bounds = null)
    {
        var b = bounds ?? Bounds;
        return $"RESULT: OK\nDpiAwareness: per_monitor_v2\nMonitorCount: 1\n" +
               $"Monitor[0]: x={b.x} y={b.y} width={b.w} height={b.h} primary=true\n" +
               "WgcSupported: true\nD3d11Initialized: true\nEncoderCreated: true\n";
    }

    private sealed class FakeProbeProcessRunner : IWgcHelperProcessRunner
    {
        private readonly Queue<WgcHelperProcessResult> _results;

        public FakeProbeProcessRunner(params WgcHelperProcessResult[] results)
        {
            _results = new Queue<WgcHelperProcessResult>(results);
        }

        public List<Call> Calls { get; } = new();
        public int ThrowOnCall { get; init; }

        public WgcHelperProcessResult Run(
            string fileName,
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(fileName, argumentList.ToArray(), timeoutMs));
            if (ThrowOnCall == Calls.Count)
                throw new InvalidOperationException("synthetic runner failure");
            return _results.Count > 0 ? _results.Dequeue() : new WgcHelperProcessResult();
        }
    }

    private sealed record Call(string FileName, string[] Arguments, int TimeoutMs);
}
