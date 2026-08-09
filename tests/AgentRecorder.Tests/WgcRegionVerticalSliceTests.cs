using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-WindowBackend")]
public sealed class WgcRegionVerticalSliceTests
{
    [Fact]
    public void RegionExperimentDisabled_UsesFfmpegWithoutProbe()
    {
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: true);
        var result = WithRegionBackend(null, () =>
            CaptureBackendSelector.SelectWithEvidence(RegionConfig(), probe));

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.Equal("experiment_disabled", result.Evidence.SelectionReasonCode);
        Assert.False(result.Evidence.Fallback);
        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wgc")]
    [InlineData("WGC-CONTINUOUS")]
    [InlineData(" wgc-continuous ")]
    [InlineData("future-backend")]
    public void RegionExperimentNonCanonical_StaysOnFfmpegWithoutProbe(string value)
    {
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: true);
        var result = WithRegionBackend(value, () =>
            CaptureBackendSelector.SelectWithEvidence(RegionConfig(), probe));

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.NotEqual("wgc-continuous", result.BackendType);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void RegionExperimentHealthyProbe_SelectsWgcWithLockedGeometry()
    {
        var config = RegionConfig();
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: true);
        var plan = WithRegionBackend(CaptureBackendSelector.WgcContinuousBackend, () =>
            CaptureBackendSelector.BuildPlan(config, probe));

        Assert.Equal("wgc-continuous", plan.PlannedBackend);
        Assert.Equal("region_rectangle", plan.CaptureSemantics);
        Assert.Equal("display-left", plan.TargetDisplayId);
        Assert.Equal("display-stable-test", plan.TargetDisplayIdentity);
        Assert.Equal(new CapturePlanBounds(-1920, -200, 1920, 1080), plan.DisplayBounds);
        Assert.Equal(new CapturePlanBounds(-1800, -100, 640, 480), plan.Bounds);
        Assert.Equal(1, probe.CallCount);
    }

    [Theory]
    [InlineData(true, 5, 30, "microphone_not_eligible")]
    [InlineData(false, 0, 30, "duration_not_eligible")]
    [InlineData(false, 11, 30, "duration_not_eligible")]
    [InlineData(false, 5, 0, "fps_not_eligible")]
    [InlineData(false, 5, 61, "fps_not_eligible")]
    public void RegionExperimentIneligible_FallsBackBeforeProbe(
        bool microphone,
        int duration,
        int fps,
        string reason)
    {
        var config = RegionConfig();
        config.Microphone = microphone;
        config.DurationSeconds = duration;
        config.Fps = fps;
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: true);

        var result = WithRegionBackend(CaptureBackendSelector.WgcContinuousBackend, () =>
            CaptureBackendSelector.SelectWithEvidence(config, probe));

        Assert.Equal(microphone ? "ffmpeg-region-av-split" : "ffmpeg-region", result.BackendType);
        Assert.Equal(reason, result.Evidence.SelectionReasonCode);
        Assert.True(result.Evidence.Fallback);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void RegionCrossDisplayBounds_FallsBackBeforeProbe()
    {
        var config = RegionConfig();
        config.Bounds = (-2000, -100, 640, 480);
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: true);

        var result = WithRegionBackend(CaptureBackendSelector.WgcContinuousBackend, () =>
            CaptureBackendSelector.SelectWithEvidence(config, probe));

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.Equal("region_bounds_not_eligible", result.Evidence.SelectionReasonCode);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void RegionProbeAvailableButTargetDisplayMissing_FallsBackWithStableReason()
    {
        var probe = new CountingProbe(healthy: true, IncludeTargetDisplay: false);
        var result = WithRegionBackend(CaptureBackendSelector.WgcContinuousBackend, () =>
            CaptureBackendSelector.SelectWithEvidence(RegionConfig(), probe));

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.Equal("probe_bounds_mismatch", result.Evidence.SelectionReasonCode);
        Assert.Equal("fresh_probe", result.Evidence.AvailabilitySource);
        Assert.True(result.Evidence.Fallback);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void RegionProbeWithDuplicateTargetBounds_FailsClosedForTopologyAmbiguity()
    {
        var probe = new DuplicateTargetProbe();
        var result = WithRegionBackend(CaptureBackendSelector.WgcContinuousBackend, () =>
            CaptureBackendSelector.SelectWithEvidence(RegionConfig(), probe));

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.Equal("probe_bounds_mismatch", result.Evidence.SelectionReasonCode);
        Assert.True(result.Evidence.Fallback);
    }

    [Fact]
    public void RegionNegativeVirtualOrigin_ProducesRelativeCropWithoutChangingSemantics()
    {
        var config = RegionConfig();
        Assert.True(WgcRegionGeometry.TryGetCrop(
            new WgcRegionRect(config.DisplayBounds!.Value.x, config.DisplayBounds.Value.y,
                config.DisplayBounds.Value.w, config.DisplayBounds.Value.h),
            new WgcRegionRect(config.Bounds.x, config.Bounds.y, config.Bounds.w, config.Bounds.h),
            out int offsetX, out int offsetY));
        Assert.Equal(120, offsetX);
        Assert.Equal(100, offsetY);
    }

    [Fact]
    public void StaticGuards_KeepRegionExperimentalAndBeginGated()
    {
        string root = FindRepositoryRoot();
        string selector = File.ReadAllText(Path.Combine(root, "src", "AgentRecorder.Core", "CaptureBackendSelector.cs"));
        string captureSession = File.ReadAllText(Path.Combine(root, "tools", "wgc-native-helper", "src", "capture_session.cpp"));
        string apiRoot = Path.Combine(root, "src", "AgentRecorder.Api");

        Assert.Contains("RegionBackendEnvVar", selector, StringComparison.Ordinal);
        Assert.Contains("experiment_disabled", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("--capture-one-frame-window", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("--capture-one-frame-window", captureSession, StringComparison.Ordinal);
        Assert.DoesNotContain("capture-continuous-region", string.Join("\n", Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)), StringComparison.Ordinal);

        int beginGateIndex = captureSession.IndexOf("WaitForBeginAuthorization", StringComparison.Ordinal);
        int startCaptureIndex = captureSession.IndexOf("session.StartCapture()", StringComparison.Ordinal);
        Assert.True(beginGateIndex >= 0 && startCaptureIndex > beginGateIndex,
            "The region helper must wait for begin authorization before StartCapture.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AgentRecorder.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("AgentRecorder repository root was not found.");
    }

    private static CaptureConfig RegionConfig() => new()
    {
        SourceKind = "region",
        DisplayId = "display-left",
        DisplayStableIdentity = "display-stable-test",
        DisplayBounds = (-1920, -200, 1920, 1080),
        Bounds = (-1800, -100, 640, 480),
        DurationSeconds = 5,
        Fps = 30,
        OutputPath = "C:\\temp\\region.mp4",
    };

    private static T WithRegionBackend<T>(string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, previous);
        }
    }

    private sealed class CountingProbe : IWgcContinuousAvailabilityProbe
    {
        private readonly bool _healthy;
        private readonly bool _includeTargetDisplay;

        public CountingProbe(bool healthy, bool IncludeTargetDisplay)
        {
            _healthy = healthy;
            _includeTargetDisplay = IncludeTargetDisplay;
        }

        public int CallCount { get; private set; }

        public WgcContinuousAvailabilityResult Check(CaptureConfig config)
        {
            CallCount++;
            var monitors = _includeTargetDisplay && config.DisplayBounds.HasValue
                ? new[] { new WgcMonitorBounds(
                    config.DisplayBounds.Value.x,
                    config.DisplayBounds.Value.y,
                    config.DisplayBounds.Value.w,
                    config.DisplayBounds.Value.h) }
                : Array.Empty<WgcMonitorBounds>();
            var evidence = new WgcContinuousCapabilityEvidence(
                "0.3.0", "per_monitor_v2", _healthy, _healthy, _healthy, monitors);
            return new WgcContinuousAvailabilityResult(
                _healthy,
                _healthy ? "available" : "probe_timeout",
                "fresh_probe",
                12,
                evidence);
        }
    }

    private sealed class DuplicateTargetProbe : IWgcContinuousAvailabilityProbe
    {
        public WgcContinuousAvailabilityResult Check(CaptureConfig config)
        {
            var display = config.DisplayBounds!.Value;
            var evidence = new WgcContinuousCapabilityEvidence(
                "0.3.0",
                "per_monitor_v2",
                true,
                true,
                true,
                new[]
                {
                    new WgcMonitorBounds(display.x, display.y, display.w, display.h),
                    new WgcMonitorBounds(display.x, display.y, display.w, display.h)
                });
            return new WgcContinuousAvailabilityResult(true, "available", "fresh_probe", 1, evidence);
        }
    }
}
