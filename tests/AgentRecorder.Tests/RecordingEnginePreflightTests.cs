using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that <see cref="RecordingEngine"/> runs preflight checks at the
/// right lifecycle points and surfaces stable error codes / suggested actions.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class RecordingEnginePreflightTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly RecordingPreflightChecker.TryGetFreeSpace _originalFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _originalEncoder;
    private readonly RecordingPreflightChecker.TryResolveAudioHelper _originalAudioHelperPathResolver;
    private readonly RecordingPreflightChecker.RunAudioHelperProbe _originalAudioHelperProbeRunner;
    private readonly Func<bool> _originalShouldUseWasapiBackend;
    private readonly Func<bool, bool, List<SystemQuery.WindowInfo>>? _originalWindowProvider;
    private readonly Func<List<SystemQuery.DisplayInfo>>? _originalDisplayProvider;

    private class ControllableTray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => false;

        public ConfirmationDecision? Decision { get; set; }
        public bool DeferCallback { get; set; }
        public string? LastError { get; private set; }
        public List<object> RecordingObjects { get; } = new();
        public List<object> IdleObjects { get; } = new();
        public List<object> AllIdleCalls { get; } = new();

        private Action<ConfirmationDecision>? _pendingCallback;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        {
            if (DeferCallback)
            {
                _pendingCallback = callback;
                return;
            }

            var decision = Decision ?? ConfirmationDecision.Reject();
            callback(decision);
        }

        public void InvokeApproved()
        {
            Decision = ConfirmationDecision.Approve();
            _pendingCallback?.Invoke(ConfirmationDecision.Approve());
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }

        public void SetRecording(RecordingUiPresentation rec) => RecordingObjects.Add(rec);
        public void SetIdle(RecordingUiPresentation rec) => IdleObjects.Add(rec);
        public void SetAllIdle() => AllIdleCalls.Add(new object());
        public void ShowError(string text) => LastError = text;
    }

    private class FakeCaptureBackend : ICaptureBackend
    {
        public bool Started { get; private set; }
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Started = true;
            cfg.CommandArgs = "fake args";
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    public RecordingEnginePreflightTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);

        _originalFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _originalEncoder = RecordingPreflightChecker.EncoderProvider;
        _originalAudioHelperPathResolver = RecordingPreflightChecker.AudioHelperPathResolver;
        _originalAudioHelperProbeRunner = RecordingPreflightChecker.AudioHelperProbeRunner;
        _originalShouldUseWasapiBackend = RecordingPreflightChecker.ShouldUseWasapiBackend;
        _originalWindowProvider = GetWindowProviderField();
        _originalDisplayProvider = GetDisplayProviderField();

        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024;
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = Path.Combine(_tmp.Path, "ffmpeg.exe");
            p = Path.Combine(_tmp.Path, "ffprobe.exe");
            File.WriteAllText(f, "fake ffmpeg");
            File.WriteAllText(p, "fake ffprobe");
            return true;
        };
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => false;

        SystemQuery.SetDisplayProvider(() => new()
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        RecordingPreflightChecker.FreeSpaceProvider = _originalFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _originalEncoder;
        RecordingPreflightChecker.AudioHelperPathResolver = _originalAudioHelperPathResolver;
        RecordingPreflightChecker.AudioHelperProbeRunner = _originalAudioHelperProbeRunner;
        RecordingPreflightChecker.ShouldUseWasapiBackend = _originalShouldUseWasapiBackend;
        SystemQuery.SetWindowProvider(_originalWindowProvider);
        SystemQuery.SetDisplayProvider(_originalDisplayProvider);
        _tmp.Dispose();
    }

    private static Func<bool, bool, List<SystemQuery.WindowInfo>>? GetWindowProviderField()
    {
        var field = typeof(SystemQuery).GetField("_windowProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var scoped = field?.GetValue(null);
        return scoped?.GetType().GetProperty("Value")?.GetValue(scoped)
            as Func<bool, bool, List<SystemQuery.WindowInfo>>;
    }

    private static Func<List<SystemQuery.DisplayInfo>>? GetDisplayProviderField()
    {
        var field = typeof(SystemQuery).GetField("_displayProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var scoped = field?.GetValue(null);
        return scoped?.GetType().GetProperty("Value")?.GetValue(scoped)
            as Func<List<SystemQuery.DisplayInfo>>;
    }

    private static RecordingEngine MakeEngine(AuditLogger audit, ControllableTray tray, IMicrophoneDeviceProvider? microphoneProvider = null, ISystemAudioEndpointProvider? endpoints = null)
    {
        var engine = new RecordingEngine(audit, microphoneProvider: microphoneProvider, systemAudioEndpointProvider: endpoints);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new FakeCaptureBackend(), "fake");
        return engine;
    }

    private sealed class FakeMicrophoneProvider : IMicrophoneDeviceProvider
    {
        private readonly IReadOnlyList<MicrophoneDeviceInfo> _devices;
        public FakeMicrophoneProvider(params MicrophoneDeviceInfo[] devices)
        {
            _devices = devices.ToList();
        }
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_devices);
    }

    private static JsonNode WindowConfig(string windowId, string filename, int? durationSeconds = null)
    {
        var output = new JsonObject { ["filename"] = filename };
        var source = new JsonObject
        {
            ["type"] = "window",
            ["window_id"] = windowId
        };
        var root = new JsonObject
        {
            ["source"] = source,
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["output"] = output
        };
        root["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = durationSeconds ?? 30 };
        return root;
    }

    private static JsonNode WindowConfigWithMicrophone(string windowId, string filename, string deviceId = "fake-mic")
    {
        var output = new JsonObject { ["filename"] = filename };
        var source = new JsonObject
        {
            ["type"] = "window",
            ["window_id"] = windowId
        };
        return new JsonObject
        {
            ["source"] = source,
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["audio"] = new JsonObject
            {
                ["microphone"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["device_id"] = deviceId
                }
            },
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 30 },
            ["output"] = output
        };
    }

    private sealed class WindowAvailability : IWgcContinuousAvailabilityProbe
    {
        public WgcContinuousAvailabilityResult Check(CaptureConfig config) => new(true, "synthetic-window-provider", "test");
    }
    private static RecordingEngine MakeWindowEngine(CapturingAuditLogger audit, ControllableTray tray, ISystemAudioEndpointProvider? endpoints = null)
    {
        var engine = MakeEngine(audit, tray, endpoints: endpoints);
        engine.CapturePlanFactoryForTests = config => CaptureBackendSelector.BuildPlan(config, new WindowAvailability());
        return engine;
    }

    private sealed class WindowCapacity : IDisposable, IStorageCapacityProvider
    {
        private readonly IStorageCapacityProvider previous = RecordingPreflightChecker.StorageCapacityProvider;
        internal WindowCapacity() => RecordingPreflightChecker.StorageCapacityProvider = this;
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "synthetic-volume", 1L << 40)).ToArray());
        public void Dispose() => RecordingPreflightChecker.StorageCapacityProvider = previous;
    }
    private sealed class WindowEndpoints : ISystemAudioEndpointProvider
    {
        internal static readonly SystemAudioEndpointInfo Endpoint = new("window-render", "Synthetic render", "render", "active", true);
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { Endpoint });
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken token = default) => Task.FromResult<SystemAudioEndpointInfo?>(Endpoint);
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id, CancellationToken token = default) => Task.FromResult<SystemAudioEndpointInfo?>(id == Endpoint.Id ? Endpoint : null);
    }
    private static JsonNode WindowConfigWithSystemAudio(string id, string file)
    {
        var cfg = WindowConfig(id, file);
        cfg["audio"] = new JsonObject { ["system_audio"] = new JsonObject { ["enabled"] = true, ["device_id"] = WindowEndpoints.Endpoint.Id } };
        return cfg;
    }
    private static void AssertWindowPending(Recording rec, CapturingAuditLogger audit)
    {
        Assert.Equal(RecState.pending_confirmation, rec.State);
        Assert.Equal("window", rec.SourceType); Assert.True(rec.Config.RequireWindowSurface);
        Assert.Equal(30, rec.Config.DurationSeconds); Assert.Equal(42, rec.Config.WindowProcessId);
        Assert.Equal((nint)12345, rec.Config.WindowHandle);
        Assert.NotNull(rec.ApprovedCapturePlan); Assert.True(rec.ApprovedCapturePlan.IsWindowSurface);
        Assert.Contains(audit.Events, x => x.Event == "confirmation.created");
        Assert.DoesNotContain(audit.Events, x => x.Event == "recording.preflight_failed");
        Assert.Null(rec.Backend); Assert.False(rec.BackendStartAttempted);
    }
    private static void ChangeWindowAfterPlanRevalidation(CapturingAuditLogger audit, Action change)
    {
        // Log is emitted after plan revalidation and before CheckBeforeStart.
        // This drives a lifecycle boundary, not an enum-call count or real window.
        audit.AfterLog = name => { if (name == "recording.capture_plan_revalidated") change(); };
    }
    private static void AssertWindowPreflightFailure(Recording rec, CapturingAuditLogger audit, string code, string action)
    {
        Assert.Equal(RecState.failed, rec.State); Assert.True(rec.IsFinalized);
        Assert.Null(rec.Backend); Assert.False(rec.BackendStartAttempted);
        var plan = Assert.Single(audit.Payloads, x => x.Event == "recording.capture_plan_revalidated").Data;
        Assert.False(plan.GetProperty("semantics_changed").GetBoolean());
        Assert.Equal("matched", plan.GetProperty("window_target_status").GetString());
        var failure = Assert.Single(audit.Payloads, x => x.Event == "recording.preflight_failed").Data;
        Assert.Equal("before_start", failure.GetProperty("stage").GetString());
        Assert.Equal(code, failure.GetProperty("error_code").GetString());
        Assert.Equal(action, failure.GetProperty("suggested_action").GetString());
        Assert.Equal(rec.Error, failure.GetProperty("message").GetString());
    }

    private static JsonNode DisplayConfig(string filename) =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "display",
                ["display_id"] = "display_1"
            },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["output"] = new JsonObject { ["filename"] = filename }
        };

    private static JsonNode DisplayConfigWithMicrophone(string filename, string deviceId = "fake-mic") =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "display",
                ["display_id"] = "display_1"
            },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["audio"] = new JsonObject
            {
                ["microphone"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["device_id"] = deviceId
                }
            },
            ["output"] = new JsonObject { ["filename"] = filename }
        };

    private static AudioHelperProbeResult SuccessfulProbe()
        => new()
        {
            Success = true,
            ProtocolVersion = "audio-helper-v1",
            TimestampFrequency = Stopwatch.Frequency
        };

    [Fact]
    public void CreateRecording_BeforeConfirmationPreflightFails_DoesNotCreateConfirmation()
    {
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = null;
            p = null;
            return false;
        };

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { Decision = ConfirmationDecision.Approve() };
        var engine = MakeEngine(audit, tray);

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(DisplayConfig("out.mp4"), "test-agent", tray));

        Assert.Equal("ENCODER_UNAVAILABLE", ex.Code);
        var detailsJson = JsonSerializer.Serialize(ex.Details);
        using var detailsDoc = JsonDocument.Parse(detailsJson);
        Assert.Equal("before_confirmation", detailsDoc.RootElement.GetProperty("stage").GetString());

        Assert.Empty(engine._confs);
        Assert.Empty(engine._recs);
        Assert.Contains(audit.Events, e => e.Event == "recording.preflight_failed");
        Assert.DoesNotContain(audit.Events, e => e.Event == "confirmation.created");
    }

    [Fact]
    public void CreateRecording_BeforeStart_WindowDisappeared_DoesNotStartBackend()
    {
        using var capacity = new WindowCapacity();
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        using var engine = MakeWindowEngine(audit, tray);

        // Create a pending confirmation without invoking the callback yet.
        engine.CreateRecording(WindowConfig(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        AssertWindowPending(rec, audit);
        Assert.Equal(RecState.pending_confirmation, rec.State);
        var conf = engine._confs.Values.Single();
        Assert.Equal("pending", conf.Status);

        // Now simulate the window disappearing before the user confirms.
        ChangeWindowAfterPlanRevalidation(audit, () => SystemQuery.SetWindowProvider((_, _) => new()));
        tray.InvokeApproved();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.StartsWith("preflight_failed: SOURCE_NOT_FOUND"));

        var backend = (FakeCaptureBackend?)rec.Backend;
        Assert.True(backend == null || !backend.Started);

        Assert.Contains(audit.Events, e =>
            e.Event == "recording.preflight_failed" &&
            e.ErrorCode == "SOURCE_NOT_FOUND" &&
            e.Stage == "before_start");

        Assert.NotNull(tray.LastError);
        AssertWindowPreflightFailure(rec, audit, "SOURCE_NOT_FOUND", "choose_source_again");
    }

    [Fact]
    public async Task CreationWait_BeforeStartPreflightFailureWhileBlocked_ReturnsCoherentTerminalSnapshot()
    {
        using var capacity = new WindowCapacity();
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        using var engine = MakeWindowEngine(audit, tray);
        engine.CreateRecording(WindowConfig(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        AssertWindowPending(rec, audit);

        using var aboutToBlock = new ManualResetEventSlim();
        engine.BeforeCreationWaitBlocksForTests = _ => aboutToBlock.Set();
        var waitTask = Task.Run(() => engine.GetCreationRecordingWait(rec.Id, 5000));

        Assert.True(aboutToBlock.Wait(TimeSpan.FromSeconds(2)));
        ChangeWindowAfterPlanRevalidation(audit, () => SystemQuery.SetWindowProvider((_, _) => new()));
        tray.InvokeApproved();

        var result = await waitTask;
        var status = (string)result.GetType().GetProperty("Status")!.GetValue(result)!;
        var recording = result.GetType().GetProperty("Recording")!.GetValue(result)!;
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        Assert.Equal("failed", status);
        var snapshotWarnings = (string[])recording.GetType().GetProperty("warnings")!.GetValue(recording)!;
        Assert.Contains(snapshotWarnings, warning => warning.StartsWith("preflight_failed: SOURCE_NOT_FOUND"));
        Assert.Equal("failed", recording.GetType().GetProperty("status")!.GetValue(recording));
        Assert.False((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.True((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, warning => warning.StartsWith("preflight_failed: SOURCE_NOT_FOUND"));
        AssertWindowPreflightFailure(rec, audit, "SOURCE_NOT_FOUND", "choose_source_again");
    }

    [Fact]
    public void CreateRecording_BeforeStart_WindowMinimized_DoesNotStartBackend()
    {
        using var capacity = new WindowCapacity();
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        using var engine = MakeWindowEngine(audit, tray);

        // Create a pending confirmation without invoking the callback yet.
        engine.CreateRecording(WindowConfig(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        AssertWindowPending(rec, audit);
        Assert.Equal(RecState.pending_confirmation, rec.State);

        // Approve a valid plan, then minimize before the start preflight reads it.
        ChangeWindowAfterPlanRevalidation(audit, () =>
        {
            SystemQuery.SetWindowProvider((_, _) => new()
            {
                new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, true,
                    new SystemQuery.Bounds(0, 0, 1280, 720))
            });
        });
        tray.InvokeApproved();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.StartsWith("preflight_failed: SOURCE_UNAVAILABLE"));

        var backend = (FakeCaptureBackend?)rec.Backend;
        Assert.True(backend == null || !backend.Started);

        Assert.Contains(audit.Events, e =>
            e.Event == "recording.preflight_failed" &&
            e.ErrorCode == "SOURCE_UNAVAILABLE");

        Assert.NotNull(tray.LastError);
        AssertWindowPreflightFailure(rec, audit, "SOURCE_UNAVAILABLE", "restore_or_move_window_then_retry");
    }

    [Fact]
    public void CreateRecording_PreflightPass_PathsUnchanged()
    {
        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { Decision = ConfirmationDecision.Approve() };
        var engine = MakeEngine(audit, tray);

        var result = engine.CreateRecording(DisplayConfig("out.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.recording, rec.State);
        var backend = (FakeCaptureBackend)rec.Backend!;
        Assert.True(backend.Started);
        Assert.Contains(audit.Events, e => e.Event == "recording.started");
    }

    [Fact]
    public void CreateRecording_WasapiHelperProbeFails_DoesNotCreatePendingConfirmation()
    {
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
        {
            Success = false,
            ErrorCode = "audio_helper_probe_failed",
            ErrorMessage = "simulated probe failure"
        };

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { Decision = ConfirmationDecision.Approve() };
        var mic = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("fake-mic", "Test Mic", true, "active"));
        var engine = MakeEngine(audit, tray, mic);

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(DisplayConfigWithMicrophone("out.mp4"), "test-agent", tray));

        Assert.Equal("audio_helper_probe_failed", ex.Code);
        Assert.Empty(engine._confs);
        Assert.Empty(engine._recs);
        Assert.DoesNotContain(audit.Events, e => e.Event == "confirmation.created");
    }

    [Fact]
    public void CreateRecording_WasapiHelperProbeSuccess_CreatesPendingConfirmation()
    {
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => SuccessfulProbe();

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        var mic = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("fake-mic", "Test Mic", true, "active"));
        var engine = MakeEngine(audit, tray, mic);

        engine.CreateRecording(DisplayConfigWithMicrophone("out.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.pending_confirmation, rec.State);
        Assert.Single(engine._confs);
        Assert.Contains(audit.Events, e => e.Event == "confirmation.created");
    }

    [Fact]
    public void CreateRecording_WasapiProbeSuccess_ApprovalThenWindowDisappears_FailsBeforeStartWithoutBackend()
    {
        using var capacity = new WindowCapacity();
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        int probeCalls = 0;
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => { probeCalls++; return SuccessfulProbe(); };

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        using var engine = MakeWindowEngine(audit, tray, endpoints: new WindowEndpoints());

        engine.CreateRecording(WindowConfigWithSystemAudio(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        AssertWindowPending(rec, audit);
        Assert.Equal(RecState.pending_confirmation, rec.State);
        Assert.Equal(1, probeCalls); Assert.Equal(AudioCaptureSourceKind.SystemLoopback, rec.Config.AudioSourceKind);
        Assert.Equal(WindowEndpoints.Endpoint.Id, rec.Config.SystemLoopbackEndpoint);

        // Simulate the window disappearing between confirmation approval and capture start.
        ChangeWindowAfterPlanRevalidation(audit, () => SystemQuery.SetWindowProvider((_, _) => new()));
        tray.InvokeApproved();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.StartsWith("preflight_failed: SOURCE_NOT_FOUND"));

        var backend = (FakeCaptureBackend?)rec.Backend;
        Assert.True(backend == null || !backend.Started);

        Assert.Contains(audit.Events, e =>
            e.Event == "recording.preflight_failed" &&
            e.ErrorCode == "SOURCE_NOT_FOUND" &&
            e.Stage == "before_start");
        AssertWindowPreflightFailure(rec, audit, "SOURCE_NOT_FOUND", "choose_source_again");
    }

    [Fact]
    public void WindowSurfaceMicrophoneSelectionRemainsInvalidBeforePreflightOrConfirmation()
    {
        int probes = 0;
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => { probes++; return SuccessfulProbe(); };
        var audit = new CapturingAuditLogger(); var tray = new ControllableTray { DeferCallback = true };
        using var engine = MakeWindowEngine(audit, tray);
        var request = WindowConfigWithMicrophone("window_12345", "invalid-mic.mp4");
        var error = Assert.Throws<ApiException>(() => engine.CreateRecording(request, "invalid-window-mic", tray));
        Assert.Equal("INVALID_ARGUMENT", error.Code); Assert.Equal(0, probes);
        Assert.Empty(engine._recs); Assert.Empty(engine._confs);
        Assert.DoesNotContain(audit.Events, x => x.Event == "confirmation.created" || x.Event == "recording.preflight_failed");
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        public List<(string Event, string? ErrorCode, string? Stage)> Events { get; } = new();
        internal List<(string Event, JsonElement Data)> Payloads { get; } = new();
        internal Action<string>? AfterLog;

        public override void Log(string evt, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            string? errorCode = null;
            string? stage = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error_code", out var ec))
                    errorCode = ec.GetString();
                if (doc.RootElement.TryGetProperty("stage", out var st))
                    stage = st.GetString();
            }
            catch { }
            Events.Add((evt, errorCode, stage));
            Payloads.Add((evt, JsonSerializer.SerializeToElement(payload)));
            base.Log(evt, payload);
            AfterLog?.Invoke(evt);
        }
    }
}
