using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class WindowContentDefaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task363-default-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldTest = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _oldBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar);

    public WindowContentDefaultTests()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, "ffmpeg");
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new("window_12345", "Private fake window", "private.exe", 42, true, false,
                new SystemQuery.Bounds(20, 30, 1280, 720))
        });
    }

    [Fact]
    public void OmittedWindowSemantics_EqualsExplicitStrictPlan_DespiteLegacyFfmpegEnvironment()
    {
        var implicitRecording = ConfigParser.Build(Request(false), "test", out _);
        var explicitRecording = ConfigParser.Build(Request(true), "test", out _);
        Assert.True(implicitRecording.Config.RequireWindowSurface);
        var implicitPlan = CaptureBackendSelector.BuildPlan(implicitRecording.Config, new Probe(true));
        var explicitPlan = CaptureBackendSelector.BuildPlan(explicitRecording.Config, new Probe(true));
        Assert.Equal("window_surface", implicitPlan.CaptureSemantics);
        Assert.Equal("window_surface", implicitPlan.PreviewSemantics);
        Assert.Equal("wgc-continuous", implicitPlan.PlannedBackend);
        Assert.Equal(CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(explicitPlan),
            CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(implicitPlan));
        Assert.False(implicitPlan.FallbackOccurred);
    }

    [Fact]
    public void OmittedWindowSemantics_UnavailableRejectsWithoutDesktopFallback()
    {
        var recording = ConfigParser.Build(Request(false), "test", out _);
        var error = Assert.Throws<ApiException>(() => CaptureBackendSelector.BuildPlan(recording.Config, new Probe(false)));
        Assert.Equal(503, error.Status);
        Assert.Equal("WINDOW_SURFACE_UNAVAILABLE", error.Code);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    public async Task HttpWindow_ConfirmationAndFakeStartUseSameStrictPlan(bool quick, bool explicitSemantics, bool audio)
    {
        using var f = new HttpFixture(_root);
        var request = Request(explicitSemantics);
        request["countdown_seconds"] = 0;
        if (audio) request["audio"] = JsonNode.Parse("""{"system_audio":{"enabled":true}}""");
        if (quick) ToQuick(request);
        var response = await f.Post(request, quick);
        Assert.Equal(200, response.Status);
        Assert.Equal("requires_user_confirmation", response.Body["data"]!["status"]!.GetValue<string>());
        var summary = response.Body["data"]!["summary"]!;
        Assert.Equal("window_surface", summary["capture_semantics"]!.GetValue<string>());
        Assert.Equal("window_surface", summary["preview_semantics"]!.GetValue<string>());
        Assert.Equal(audio ? "wgc-window-av-split" : "wgc-continuous", summary["planned_backend"]!.GetValue<string>());
        Assert.Equal(0, f.Backend.Starts);
        Assert.Equal(1, f.Tray.Confirmations);
        if (quick)
        {
            Assert.Equal(1, f.ActiveCalls);
            // A fresh enumeration would now report a different window; creation
            // must have retained the one active-window snapshot, including PID/size.
            Assert.Equal(0, f.WindowCalls);
            Assert.Equal(1280, response.Body["data"]!["quick"]!["resolved_source"]!["capture_bounds"]!["width"]!.GetValue<int>());
        }
        var rec = Assert.Single(f.Engine._recs.Values);
        var approved = rec.ApprovedCapturePlan;
        f.Tray.Decide!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => f.Backend.Starts == 1 || rec.State == RecState.failed, 5000));
        Assert.Equal(1, f.Backend.Starts);
        Assert.Equal(RecState.recording, rec.State);
        Assert.Same(approved, rec.ApprovedCapturePlan);
        Assert.True(f.Backend.Config!.RequireWindowSurface);
        Assert.Equal((nint)12345, f.Backend.Config.WindowHandle);
        Assert.Equal(42, f.Backend.Config.WindowProcessId);
        Assert.NotNull(f.Backend.Proof);
        Assert.Equal(audio ? "render-fixed" : null, f.Backend.Config.SystemLoopbackEndpoint);
        Assert.Equal(15, f.Backend.Config.DurationSeconds);
        using var status = await f.Client.GetAsync($"/api/v1/recordings/{rec.Id}");
        var statusBody = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
        Assert.Equal(approved!.PlannedBackend, statusBody["data"]!["backend"]!.GetValue<string>());
        f.SaveExample(quick ? "quick-start" : explicitSemantics ? "explicit-start" : "raw-start", response.Body);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task HttpWindow_UnavailableOrThrowingProbeRejectsBeforeConfirmationAndBackend(bool quick, bool throws)
    {
        using var f = new HttpFixture(_root);
        f.ProbeAvailable = false;
        f.ProbeThrows = throws;
        var request = Request(false);
        if (quick) ToQuick(request);
        var response = await f.Post(request, quick);
        Assert.Equal(503, response.Status);
        Assert.Equal("WINDOW_SURFACE_UNAVAILABLE", response.Body["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, f.Tray.Confirmations);
        Assert.Equal(0, f.BackendFactories);
        Assert.Equal(0, f.Backend.Starts);
        Assert.Empty(f.Engine._recs);
        Assert.False(File.Exists(Path.Combine(_root, "private.mp4")));
        f.SaveExample(quick ? "quick-unavailable" : "raw-unavailable", response.Body);
    }

    [Theory]
    [InlineData("null")] [InlineData("false")] [InlineData("17")]
    [InlineData("[]")] [InlineData("{}")] [InlineData("\"screen_rectangle\"")]
    [InlineData("\"window_surface \"")]
    public void InvalidExplicitSemanticsCannotDisableNewDefault(string json)
    {
        int calls = 0;
        SystemQuery.SetWindowProvider((_, _) => { calls++; throw new InvalidOperationException(); });
        var request = Request(false);
        request["required_capture_semantics"] = JsonNode.Parse(json);
        var error = Assert.Throws<ApiException>(() => ConfigParser.Build(request, "test", out _));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(false, "microphone")] [InlineData(true, "microphone")]
    [InlineData(false, "nested")] [InlineData(true, "nested")]
    [InlineData(false, "manual")] [InlineData(true, "manual")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "too_long")] [InlineData(true, "too_long")]
    [InlineData(false, "fractional")] [InlineData(true, "fractional")]
    public async Task UnsupportedWindowCombinationsFailBeforeProvidersUiOrWorkers(bool quick, string fault)
    {
        using var f = new HttpFixture(_root);
        var request = Request(false);
        switch (fault)
        {
            case "microphone": request["audio"] = JsonNode.Parse("""{"microphone":{"enabled":true}}"""); break;
            case "nested": request["nested"] = JsonNode.Parse("""{"role":"outer"}"""); break;
            case "manual": request["stop_condition"] = JsonNode.Parse("""{"type":"manual"}"""); break;
            case "missing": request.Remove("stop_condition"); break;
            case "too_long": request["stop_condition"]!["seconds"] = 1801; break;
            case "fractional": request["stop_condition"]!["seconds"] = 15.5; break;
        }
        if (quick) ToQuick(request);
        var response = await f.Post(request, quick);
        Assert.Equal(400, response.Status);
        Assert.Equal("INVALID_ARGUMENT", response.Body["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, f.ActiveCalls + f.WindowCalls + f.Devices.Calls + f.ProbeCalls);
        Assert.Equal(0, f.Tray.Confirmations + f.BackendFactories + f.Backend.Starts);
        Assert.Empty(f.Engine._recs);
        Assert.False(File.Exists(Path.Combine(_root, "private.mp4")));
    }

    [Theory]
    [InlineData(false, "pid")] [InlineData(true, "pid")]
    [InlineData(false, "size")] [InlineData(true, "size")]
    [InlineData(false, "minimized")] [InlineData(true, "minimized")]
    [InlineData(false, "closed")] [InlineData(true, "closed")]
    public async Task ChangedTargetAfterConfirmationFailsClosedWithoutBackend(bool quick, string fault)
    {
        using var f = new HttpFixture(_root);
        var request = Request(false);
        if (quick) ToQuick(request);
        Assert.Equal(200, (await f.Post(request, quick)).Status);
        f.CurrentWindow = fault switch
        {
            "closed" => null,
            "pid" => f.CurrentWindow! with { process_id = 99 },
            "size" => f.CurrentWindow! with { bounds = new(20, 30, 640, 480) },
            _ => f.CurrentWindow! with { is_minimized = true }
        };
        f.Tray.Decide!(ConfirmationDecision.Approve());
        var rec = Assert.Single(f.Engine._recs.Values);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Equal(0, f.BackendFactories + f.Backend.Starts);
    }

    [Theory]
    [InlineData(true, true)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task CapabilitiesAndRecipeShareOneNonCapturingProbeAndDistinguishScreenshotWindow(bool available, bool active)
    {
        using var f = new HttpFixture(_root);
        f.ProbeAvailable = available;
        if (!active) f.CurrentWindow = null;
        using var response = await f.Client.GetAsync("/api/v1/capabilities");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(response.IsSuccessStatusCode);
        var capability = body["data"]!["recording"]!["window_surface"]!;
        Assert.True(capability["default_for_ordinary_window_video"]!.GetValue<bool>());
        Assert.True(capability["contract_supported"]!.GetValue<bool>());
        Assert.False(capability["automatic_fallback"]!.GetValue<bool>());
        Assert.Equal(available, capability["supported"]!.GetValue<bool>());
        var recipes = body["data"]!["interaction"]!["quick_recipes"]!.AsArray();
        var window = recipes.Single(x => x!["name"]!.GetValue<string>() == "record_active_window")!;
        Assert.Equal(active && available, window["available"]!.GetValue<bool>());
        Assert.Equal("window_surface", window["capture_semantics"]!.GetValue<string>());
        Assert.Equal(1800, window["max_duration_seconds"]!.GetValue<int>());
        if (!available && active) Assert.Equal("probe_wgc_unsupported", window["unavailable_reason"]!.GetValue<string>());
        Assert.True(recipes.Single(x => x!["name"]!.GetValue<string>() == "record_primary_display")!["available"]!.GetValue<bool>());
        var screenshot = body["data"]!["recording"]!["screenshot_series"]!;
        Assert.Equal("screen_rectangle", screenshot["window_capture_semantics"]!.GetValue<string>());
        Assert.False(screenshot["window_surface_supported"]!.GetValue<bool>());
        Assert.Equal(1, f.CapabilityProbeCalls);
        Assert.Equal(0, f.BackendFactories + f.Backend.Starts);
        f.SaveExample("capabilities-" + available + "-" + active, body);
    }

    [Fact]
    public void InternalLegacyAndScreenshotWindowContractsAreNotGloballyReinterpreted()
    {
        var legacy = new CaptureConfig { SourceKind = "window", WindowHandle = (nint)12345,
            Bounds = (0, 0, 1280, 720), DurationSeconds = 15 };
        Assert.Equal("screen_rectangle", CaptureBackendSelector.BuildPlan(legacy, new Probe(false)).CaptureSemantics);
        var request = Request(false);
        request["mode"] = "screenshot_series";
        request.Remove("stop_condition");
        request["interval_ms"] = 1000;
        request["max_count"] = 2;
        var screenshot = ConfigParser.Build(request, "test", out _);
        Assert.False(screenshot.Config.RequireWindowSurface);
        Assert.Equal("screen_rectangle", CaptureBackendSelector.BuildScreenshotSeriesPlan(screenshot.Config).CaptureSemantics);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WaitForAndHttpApprovalCannotStartPendingWindow(bool quick)
    {
        using var f = new HttpFixture(_root);
        var request = Request(false);
        if (quick) ToQuick(request);
        var response = await f.Post(request, quick, "?wait_for=recording&wait_ms=1");
        Assert.Equal(200, response.Status);
        Assert.Equal("pending_confirmation", response.Body["data"]!["status"]!.GetValue<string>());
        var confirmation = response.Body["data"]!["confirmation_id"]!.GetValue<string>();
        using var approval = await f.Client.PostAsync($"/api/v1/confirmations/{confirmation}/approve", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(405, (int)approval.StatusCode);
        Assert.Equal(RecState.pending_confirmation, Assert.Single(f.Engine._recs.Values).State);
        Assert.Equal(0, f.BackendFactories + f.Backend.Starts);
    }

    private static void ToQuick(JsonObject request)
    {
        request.Remove("source");
        request["target"] = new JsonObject { ["type"] = "active_window" };
        if (request["stop_condition"] is JsonObject stop && stop["type"]?.ToString() == "duration")
        {
            request["duration_seconds"] = stop["seconds"]?.DeepClone();
            request.Remove("stop_condition");
        }
    }

    private JsonObject Request(bool explicitSemantics)
    {
        var request = (JsonObject)JsonNode.Parse("""
        {"source":{"type":"window","window_id":"window_12345"},
         "stop_condition":{"type":"duration","seconds":15},
         "audio":{"microphone":{"enabled":false},"system_audio":{"enabled":false}}}
        """)!;
        request["output"] = new JsonObject { ["directory"] = _root, ["filename"] = "private.mp4" };
        if (explicitSemantics) request["required_capture_semantics"] = "window_surface";
        return request;
    }

    public void Dispose()
    {
        SystemQuery.SetWindowProvider(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTest);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, _oldBackend);
    }

    private sealed class Probe(bool available) : IWgcContinuousAvailabilityProbe
    {
        public WgcContinuousAvailabilityResult Check(CaptureConfig config) => new(available, available ? "ready" : "probe_wgc_unsupported", "fresh_probe", 0);
    }

    private sealed class Tray : ITrayContext
    {
        public string HostMode => "private-test";
        public bool SupportsRegionSelectionUi => false;
        public int Confirmations;
        public Action<ConfirmationDecision>? Decide;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        { Confirmations++; Decide = callback; }
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback) => throw new InvalidOperationException("Unexpected region selection");
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class Backend : ICaptureBackend
    {
        public int Starts;
        public CaptureConfig? Config;
        public CaptureAuthorizationProof? Proof;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof proof) { Config = cfg; Proof = proof; Starts++; }
        public OutputMeta Stop() => new() { OutputPath = Config?.OutputPath ?? "", StopReason = "private_fake_end" };
        public void Dispose() { }
    }

    private sealed class Devices : IMicrophoneDeviceProvider, ISystemAudioEndpointProvider
    {
        public int Calls;
        private readonly SystemAudioEndpointInfo _endpoint = new("render-fixed", "Private fake speakers", "render", "active", true);
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken token = default)
        { Calls++; return Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(Array.Empty<MicrophoneDeviceInfo>()); }
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken token = default)
        { Calls++; return Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { _endpoint }); }
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken token = default)
        { Calls++; return Task.FromResult<SystemAudioEndpointInfo?>(_endpoint); }
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id, CancellationToken token = default)
        { Calls++; return Task.FromResult<SystemAudioEndpointInfo?>(id == _endpoint.Id ? _endpoint : null); }
    }

    private sealed class HttpFixture : IDisposable
    {
        public readonly Tray Tray = new();
        public readonly Backend Backend = new();
        public readonly Devices Devices = new();
        public readonly RecordingEngine Engine;
        public readonly ApiServer Api;
        public readonly HttpClient Client;
        public SystemQuery.WindowInfo? CurrentWindow = new("window_12345", "Private fake window", "private.exe", 42, true, false, new(20, 30, 1280, 720));
        public bool ProbeAvailable = true, ProbeThrows;
        public int ActiveCalls, WindowCalls, ProbeCalls, CapabilityProbeCalls, BackendFactories;
        private readonly string _root;
        private readonly string? _oldDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly RecordingPreflightChecker.TryGetFreeSpace _space = RecordingPreflightChecker.FreeSpaceProvider;
        private readonly RecordingPreflightChecker.TryGetEncoderPaths _encoder = RecordingPreflightChecker.EncoderProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _helper = RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly RecordingPreflightChecker.RunAudioHelperProbe _audioProbe = RecordingPreflightChecker.AudioHelperProbeRunner;
        private readonly IStorageCapacityProvider _capacity = RecordingPreflightChecker.StorageCapacityProvider;

        public HttpFixture(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            DataDirResolver.SetOverride(root);
            ApiKeyAuth.InitializeForTesting(root);
            SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Private display", true, new(0, 0, 1920, 1080), 1) });
            SystemQuery.SetActiveWindowProvider(() => { ActiveCalls++; return CurrentWindow; });
            SystemQuery.SetWindowProvider((_, _) => { WindowCalls++; return CurrentWindow is null ? new() : new() { CurrentWindow }; });
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long bytes) => { bytes = 100L << 30; return true; };
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            { ffmpeg = ffprobe = typeof(WindowContentDefaultTests).Assembly.Location; return true; };
            RecordingPreflightChecker.AudioHelperPathResolver = () => "private-nonexecuted-audio-helper";
            RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new() { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
            RecordingPreflightChecker.StorageCapacityProvider = new FakeStorageProvider();
            var audit = new AuditLogger();
            Engine = new(audit, microphoneProvider: Devices, systemAudioEndpointProvider: Devices)
            { CountdownSteps = 0, DisableDeadlineWatchdogForTests = true };
            Engine.CapturePlanFactoryForTests = cfg => { ProbeCalls++; return CaptureBackendSelector.BuildPlan(cfg, new FixtureProbe(this)); };
            Engine.BackendFactory = cfg => { BackendFactories++; return (Backend, cfg.IsSystemLoopback ? "wgc-window-av-split" : "wgc-continuous"); };
            Engine.SetTray(Tray);
            Api = new(Engine, audit, Tray, null, null, null, null, null, null, null, null, null, 0);
            Api.WindowSurfaceAvailabilityForTests = () => { CapabilityProbeCalls++; return new(ProbeAvailable, ProbeAvailable ? "ready" : "probe_wgc_unsupported", "fresh_probe", 0); };
            Api.Start();
            Client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{Api.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        }

        public async Task<(int Status, JsonNode Body)> Post(JsonObject request, bool quick, string query = "")
        {
            using var response = await Client.PostAsync("/api/v1/recordings" + (quick ? "/quick" : "") + query, new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
            return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
        }

        public void SaveExample(string name, JsonNode response) => File.WriteAllText(Path.Combine(_root, name + ".json"), response.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));

        public void Dispose()
        {
            Api.Stop(); Engine.Dispose(); Client.Dispose();
            ApiKeyAuth.ResetForTesting(null);
            SystemQuery.SetDisplayProvider(null); SystemQuery.SetActiveWindowProvider(null);
            RecordingPreflightChecker.FreeSpaceProvider = _space;
            RecordingPreflightChecker.EncoderProvider = _encoder;
            RecordingPreflightChecker.AudioHelperPathResolver = _helper;
            RecordingPreflightChecker.AudioHelperProbeRunner = _audioProbe;
            RecordingPreflightChecker.StorageCapacityProvider = _capacity;
            if (_oldDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_oldDir);
        }

        private sealed class FixtureProbe(HttpFixture fixture) : IWgcContinuousAvailabilityProbe
        {
            public WgcContinuousAvailabilityResult Check(CaptureConfig cfg) => fixture.ProbeThrows
                ? throw new InvalidOperationException("Private probe fault")
                : new(fixture.ProbeAvailable, fixture.ProbeAvailable ? "ready" : "probe_wgc_unsupported", "fresh_probe", 0);
        }
    }
}
