using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class SystemAudioProductFlowTests : IDisposable
{
    private readonly string? _oldTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _oldRegionBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);
    private readonly string? _oldDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "agent-recorder-system-data-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingPreflightChecker.TryGetFreeSpace _oldFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _oldEncoder = RecordingPreflightChecker.EncoderProvider;
    private readonly RecordingPreflightChecker.TryResolveAudioHelper _oldAudioHelperPathResolver = RecordingPreflightChecker.AudioHelperPathResolver;
    private readonly RecordingPreflightChecker.RunAudioHelperProbe _oldAudioHelperProbeRunner = RecordingPreflightChecker.AudioHelperProbeRunner;
    private readonly Func<bool> _oldWasapi = RecordingPreflightChecker.ShouldUseWasapiBackend;

    public SystemAudioProductFlowTests()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir);
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTestMode);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, _oldRegionBackend);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _oldDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        RecordingPreflightChecker.FreeSpaceProvider = _oldFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _oldEncoder;
        RecordingPreflightChecker.AudioHelperPathResolver = _oldAudioHelperPathResolver;
        RecordingPreflightChecker.AudioHelperProbeRunner = _oldAudioHelperProbeRunner;
        RecordingPreflightChecker.ShouldUseWasapiBackend = _oldWasapi;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    public void ExperimentFlag_OnlyNormalizedTrueEnables(string? raw, bool expected)
    {
        var flag = SystemAudioExperimentFlag.FromEnvironment(() => raw);
        Assert.Equal(expected, flag.IsEnabled);
    }

    [Fact]
    public void ExperimentFlag_ExplicitNullReader_DoesNotFallbackToProcessEnvironment()
    {
        var old = Environment.GetEnvironmentVariable(SystemAudioExperimentFlag.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(SystemAudioExperimentFlag.EnvironmentVariableName, "true");

            Assert.False(SystemAudioExperimentFlag.FromEnvironment(() => null).IsEnabled);
            Assert.True(SystemAudioExperimentFlag.FromEnvironment().IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemAudioExperimentFlag.EnvironmentVariableName, old);
        }
    }

    [Fact]
    public void FlagOff_RejectsBeforeTargetLookupOrEndpointEnumeration()
    {
        var endpointProvider = new CountingEndpointProvider(DefaultEndpoint);
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(
            Request(sourceType: "display", displayId: "missing-display"),
            "agent",
            out _,
            systemAudioEndpointProvider: endpointProvider,
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(false)));

        Assert.Equal("CAPABILITY_NOT_IMPLEMENTED", ex.Code);
        Assert.Equal(0, endpointProvider.CallCount);
    }

    [Fact]
    public void EnabledIntent_ResolvesRenderEndpointAndBuildsSourceAccuratePlan()
    {
        var endpointProvider = new CountingEndpointProvider(DefaultEndpoint);
        var rec = ConfigParser.Build(
            Request(),
            "agent",
            out var summary,
            systemAudioEndpointProvider: endpointProvider,
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(true));

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, rec.AudioSourceKind);
        Assert.False(rec.Microphone);
        Assert.Equal(DefaultEndpoint.Id, rec.SystemAudioEndpointId);
        Assert.Equal(DefaultEndpoint.Name, rec.SystemAudioEndpointName);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, rec.Config.AudioSourceKind);
        Assert.Equal(DefaultEndpoint.Id, rec.Config.SystemLoopbackEndpoint);
        Assert.Equal("system-loopback", SummaryString(summary, "audio_source_kind"));
        Assert.Contains("System audio: On (Default output: Speakers Long Name)", SummaryString(summary, "audio"));

        var plan = CaptureBackendSelector.BuildPlan(rec.Config, new FakeAvailabilityProbe());
        Assert.Equal("ffmpeg-av-split", plan.PlannedBackend);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, plan.AudioSourceKind);
        Assert.Equal(DefaultEndpoint.Id, plan.AudioEndpointId);
        Assert.Equal(DefaultEndpoint.Name, plan.AudioEndpointName);
    }

    [Fact]
    public void ExplicitNonDefaultEndpoint_UsesSelectedOutputSemantics()
    {
        var selected = new SystemAudioEndpointInfo(
            "{0.0.0.00000000}.{SELECTED}",
            "Headphones",
            "render",
            "active",
            false);
        var rec = ConfigParser.Build(
            Request(systemDeviceId: selected.Id),
            "agent",
            out var summary,
            systemAudioEndpointProvider: new CountingEndpointProvider(selected),
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(true));

        Assert.False(rec.SystemAudioEndpointIsDefault);
        Assert.Equal("selected", SummaryString(summary, "audio_system_output_selection"));
        Assert.Equal("Headphones", SummaryString(summary, "audio_system_output_name"));
        Assert.Equal("System audio: On (Selected output: Headphones)", SummaryString(summary, "audio"));
        Assert.Equal("", SummaryString(summary, "audio_system_default_output"));
    }

    [Fact]
    public void QuickBoundDefaultSnapshot_RemainsDefaultWhenProviderLaterReportsNonDefault()
    {
        var laterEndpoint = DefaultEndpoint with { IsDefaultMultimedia = false };
        var rec = ConfigParser.Build(
            Request(systemDeviceId: DefaultEndpoint.Id),
            "agent",
            out var summary,
            systemAudioEndpointProvider: new CountingEndpointProvider(laterEndpoint),
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(true),
            preResolvedSystemAudioEndpoint: DefaultEndpoint);

        Assert.True(rec.SystemAudioEndpointIsDefault);
        Assert.Equal("default", SummaryString(summary, "audio_system_output_selection"));
        Assert.Contains("Default output: Speakers Long Name", SummaryString(summary, "audio"));
    }

    [Fact]
    public void MicrophoneAndSystemAudio_AreRejectedBeforeEndpointResolution()
    {
        var endpointProvider = new CountingEndpointProvider(DefaultEndpoint);
        var ex = Assert.Throws<ApiException>(() => ConfigParser.ResolveAudioIntentDetails(
            Request(microphoneEnabled: true),
            systemAudioEndpointProvider: endpointProvider,
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(true)));

        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.Equal(0, endpointProvider.CallCount);
    }

    [Theory]
    [InlineData("inactive", "SYSTEM_AUDIO_ENDPOINT_INACTIVE")]
    [InlineData("capture", "SYSTEM_AUDIO_ENDPOINT_WRONG_DIRECTION")]
    public void EnabledIntent_RejectsInactiveOrWrongDirectionBeforeSourceLookup(string stateOrDirection, string expectedCode)
    {
        var endpoint = stateOrDirection == "capture"
            ? new SystemAudioEndpointInfo("capture-id", "Microphone", "capture", "active", true)
            : new SystemAudioEndpointInfo("render-id", "Speakers", "render", stateOrDirection, true);
        var endpointProvider = new CountingEndpointProvider(endpoint);

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(
            Request(sourceType: "display", displayId: "missing-display"),
            "agent",
            out _,
            systemAudioEndpointProvider: endpointProvider,
            systemAudioExperimentFlag: new SystemAudioExperimentFlag(true)));

        Assert.Equal(expectedCode, ex.Code);
        Assert.True(endpointProvider.CallCount > 0);
    }

    [Fact]
    public void WgcExperiment_WithSystemAudio_FallsBackToAvSplitWithoutDroppingAudio()
    {
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, "wgc-continuous");
        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (0, 0, 640, 480),
            DisplayId = "display_1",
            DisplayBounds = (0, 0, 1920, 1080),
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = DefaultEndpoint.Id,
            DurationSeconds = 5
        };

        var plan = CaptureBackendSelector.BuildPlan(cfg, new FakeAvailabilityProbe(true));

        Assert.Equal("ffmpeg-region-av-split", plan.PlannedBackend);
        Assert.True(plan.FallbackOccurred);
        Assert.Equal("audio_not_eligible", plan.Evidence.SelectionReasonCode);
    }

    [Fact]
    public void DshowPreference_SystemLoopbackHelperMissing_FailsBeforeBackendStart()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "agent-recorder-system-dshow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var oldBackend = Environment.GetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, AvWorkerFactory.DshowBackend);
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) => { free = 10L * 1024 * 1024 * 1024; return true; };
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            {
                ffmpeg = typeof(SystemAudioProductFlowTests).Assembly.Location;
                ffprobe = ffmpeg;
                return true;
            };
            RecordingPreflightChecker.ShouldUseWasapiBackend = () => false;
            RecordingPreflightChecker.AudioHelperPathResolver = () => null;

            int backendStarts = 0;
            var engine = new RecordingEngine(
                new AuditLogger(),
                systemAudioEndpointProvider: new CountingEndpointProvider(DefaultEndpoint),
                systemAudioExperimentFlag: new SystemAudioExperimentFlag(true));
            engine.BackendFactory = _ =>
            {
                backendStarts++;
                return (new OrderedAudioReadyBackend(), "ffmpeg-av-split");
            };

            var ex = Assert.Throws<ApiException>(() => engine.CreateRecording(
                Request(outputDirectory: tmp), "agent", new PendingTray()));

            Assert.Equal("audio_helper_unavailable", ex.Code);
            Assert.Equal(0, backendStarts);
            Assert.Empty(Directory.GetFiles(tmp, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, oldBackend);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ApprovedSystemAudio_UsesAudioReadyThenCountdownThenVideoAndRecording()
    {
        var endpointProvider = new CountingEndpointProvider(DefaultEndpoint);
        var tmp = Path.Combine(Path.GetTempPath(), "agent-recorder-system-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) => { free = 10L * 1024 * 1024 * 1024; return true; };
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            {
                ffmpeg = typeof(SystemAudioProductFlowTests).Assembly.Location;
                ffprobe = ffmpeg;
                return true;
            };
            RecordingPreflightChecker.ShouldUseWasapiBackend = () => false;

            var backend = new OrderedAudioReadyBackend();
            var tray = new PendingTray();
            var engine = new RecordingEngine(
                new AuditLogger(),
                systemAudioEndpointProvider: endpointProvider,
                systemAudioExperimentFlag: new SystemAudioExperimentFlag(true))
            {
                CountdownInterval = TimeSpan.FromMilliseconds(5),
                CountdownSteps = 1,
                FirstFrameTimeout = TimeSpan.FromSeconds(1)
            };
            engine.BackendFactory = _ => (backend, "ffmpeg-region-av-split");

            var cfg = Request(outputDirectory: tmp);
            var result = engine.CreateRecording(cfg, "agent", tray);
            var rec = Assert.Single(engine._recs.Values);
            Assert.Equal(RecState.pending_confirmation, rec.State);
            Assert.Null(rec.Backend);
            Assert.NotNull(tray.PendingCallback);
            Assert.Equal(0, backend.StartCalls);

            tray.PendingCallback!(ConfirmationDecision.Approve());
            Assert.True(SpinWait.SpinUntil(() => backend.StartCalls == 1, TimeSpan.FromSeconds(2)),
                $"state={rec.State}, error={rec.Error}, warnings={string.Join(",", rec.Warnings)}");
            Assert.Equal(RecState.preparing, rec.State);
            Assert.Equal(0, backend.StartVideoCalls);

            backend.SignalAudioReady();
            Assert.True(SpinWait.SpinUntil(() => backend.StartVideoCalls == 1, TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2)));
            Assert.Equal(new[] { "start", "audio-ready", "start-video", "first-frame" }, backend.Events);
            Assert.False(rec.Microphone);
            Assert.Equal(AudioCaptureSourceKind.SystemLoopback, rec.AudioSourceKind);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrExpiredSystemAudio_NeverStartsBackendOrMedia(bool expire)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "agent-recorder-system-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) => { free = 10L * 1024 * 1024 * 1024; return true; };
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            {
                ffmpeg = typeof(SystemAudioProductFlowTests).Assembly.Location;
                ffprobe = ffmpeg;
                return true;
            };
            RecordingPreflightChecker.ShouldUseWasapiBackend = () => false;

            var backend = new OrderedAudioReadyBackend();
            var tray = new PendingTray();
            var engine = new RecordingEngine(
                new AuditLogger(),
                systemAudioEndpointProvider: new CountingEndpointProvider(DefaultEndpoint),
                systemAudioExperimentFlag: new SystemAudioExperimentFlag(true));
            engine.BackendFactory = _ => (backend, "ffmpeg-region-av-split");
            engine.CreateRecording(Request(outputDirectory: tmp), "agent", tray);
            var rec = Assert.Single(engine._recs.Values);
            Assert.Equal(RecState.pending_confirmation, rec.State);

            if (expire)
                engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!);
            else
                tray.PendingCallback!(ConfirmationDecision.Reject());

            Assert.Equal(expire ? RecState.expired : RecState.rejected, rec.State);
            Assert.Equal(0, backend.StartCalls);
            Assert.Empty(Directory.GetFiles(tmp, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    private static readonly SystemAudioEndpointInfo DefaultEndpoint =
        new("{0.0.0.00000000}.{SYSTEM}", "Speakers Long Name", "render", "active", true);

    private static JsonNode Request(
        string sourceType = "display",
        string displayId = "display_1",
        bool microphoneEnabled = false,
        string? outputDirectory = null,
        string? systemDeviceId = null)
    {
        var audio = new JsonObject
        {
            ["microphone"] = new JsonObject { ["enabled"] = microphoneEnabled },
            ["system_audio"] = new JsonObject { ["enabled"] = true }
        };
        if (systemDeviceId != null)
            audio["system_audio"]!["device_id"] = systemDeviceId;
        var source = new JsonObject
        {
            ["type"] = sourceType,
            ["display_id"] = displayId
        };
        if (sourceType == "region")
        {
            source["coordinate_space"] = "virtual_screen";
            source["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 640, ["height"] = 480 };
        }

        var root = new JsonObject
        {
            ["source"] = source,
            ["audio"] = audio,
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 15 }
        };
        if (outputDirectory != null)
            root["output"] = new JsonObject { ["directory"] = outputDirectory, ["filename"] = "controlled-system-audio.mp4" };
        return root;
    }

    private static string SummaryString(object summary, string property)
        => (summary.GetType().GetProperty(property)?.GetValue(summary)?.ToString()) ?? "";

    private sealed class CountingEndpointProvider : ISystemAudioEndpointProvider
    {
        private readonly SystemAudioEndpointInfo? _endpoint;
        public CountingEndpointProvider(SystemAudioEndpointInfo? endpoint) => _endpoint = endpoint;
        public int CallCount { get; private set; }
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_endpoint);
        }
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_endpoint?.Id == endpointId ? _endpoint : null);
        }
    }

    private sealed class FakeAvailabilityProbe : IWgcContinuousAvailabilityProbe
    {
        private readonly bool _available;
        public FakeAvailabilityProbe(bool available = false) => _available = available;
        public WgcContinuousAvailabilityResult Check(CaptureConfig cfg)
            => new(_available, _available ? "probe_success" : "probe_unavailable", "fresh_probe", 1);
    }

    private sealed class PendingTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;
        public Action<ConfirmationDecision>? PendingCallback { get; private set; }
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) => PendingCallback = callback;
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class OrderedAudioReadyBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<int, OutputMeta>? _naturalExit;
        private bool _audioReady;
        public event Action? AudioReady;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public List<string> Events { get; } = new();
        public int StartCalls { get; private set; }
        public int StartVideoCalls { get; private set; }
        public bool IsAudioReady => _audioReady;
        public int ExitCode => 0;
        public void Start(CaptureConfig cfg) { StartCalls++; Events.Add("start"); cfg.CommandArgs = "controlled-test"; }
        public void SignalAudioReady() { _audioReady = true; Events.Add("audio-ready"); AudioReady?.Invoke(); }
        public void StartVideo()
        {
            StartVideoCalls++;
            Events.Add("start-video");
            Events.Add("first-frame");
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1024, OutTimeUs = 1 });
        }
        public OutputMeta Stop() => new()
        {
            SizeBytes = 1024,
            DurationSeconds = 1,
            OutputPath = "controlled-test.mp4",
            Container = "mp4",
            Codec = "h264",
            AudioSourceKind = "system-loopback",
            AudioStatus = "system_loopback_recorded",
            AudioCodec = "aac",
            HasAudioStream = true
        };
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public void Dispose() { }
    }

}
