using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies the consent invariant: no screen frame or audio sample capture may
/// start before local user approval. Covers the public <c>/recordings</c> and
/// <c>/recordings/quick</c> entry points via the HeadlessHostIntegration
/// collection because each test binds ApiServer.Port.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class ConsentInvariantTests : IDisposable
{
    private sealed class CaptureAuditLogger : AuditLogger
    {
        public List<(DateTime Time, string Event, JsonElement Payload)> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            Events.Add((DateTime.UtcNow, evt, JsonDocument.Parse(json).RootElement));
            base.Log(evt, payload);
        }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public bool StartCalled { get; private set; }
        public CaptureConfig? LastConfig { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            StartCalled = true;
            LastConfig = cfg;
            cfg.CommandArgs = "fake args";
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class FakeMicrophoneProvider : IMicrophoneDeviceProvider
    {
        private readonly IReadOnlyList<MicrophoneDeviceInfo> _devices;
        private readonly Exception? _exception;
        public int CallCount { get; private set; }

        public FakeMicrophoneProvider(params MicrophoneDeviceInfo[] devices)
        {
            _devices = devices.ToList();
        }

        public FakeMicrophoneProvider(Exception exception)
        {
            _exception = exception;
            _devices = Array.Empty<MicrophoneDeviceInfo>();
        }

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_exception != null)
                throw _exception;
            return Task.FromResult(_devices);
        }
    }

    private sealed class ControllableTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;

        public enum DecisionMode { Approve, Reject, Timeout }

        public DecisionMode Mode { get; set; } = DecisionMode.Reject;
        public int RequestRegionSelectionCallCount { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            if (Mode == DecisionMode.Timeout)
                return; // let the confirmation time out

            var decision = Mode == DecisionMode.Approve
                ? ConfirmationDecision.Approve()
                : ConfirmationDecision.Reject();
            callback(decision);
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            RequestRegionSelectionCallCount++;
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private ApiServer? _server;
    private string? _dataDir;
    private CaptureAuditLogger? _audit;
    private RecordingEngine? _engine;
    private readonly FakeCaptureBackend _backend = new();

    private ApiServer CreateServer(ControllableTray tray, IMicrophoneDeviceProvider? microphoneProvider = null)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"consent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        _audit = new CaptureAuditLogger();
        _engine = new RecordingEngine(_audit, microphoneProvider: microphoneProvider);
        _engine.SetTray(tray);
        _engine.BackendFactory = _ => (_backend, "fake");
        _server = new ApiServer(_engine, _audit, tray);
        return _server;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        if (_dataDir != null)
        {
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    [Fact]
    public async Task CreateRecording_PendingConfirmation_BackendStartNotCalled()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());

            Assert.False(_backend.StartCalled, "Backend.Start must not be called while pending confirmation.");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_PendingConfirmation_BackendStartNotCalled()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());

            Assert.False(_backend.StartCalled, "Backend.Start must not be called while quick recording is pending confirmation.");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_Approved_BackendStartCalledAndApprovalPrecedesStarted()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            Assert.True(_backend.StartCalled, "Backend.Start must be called after local approval.");

            var events = _audit!.Events;
            var approvalIdx = events.FindIndex(e => e.Event == "confirmation.approved");
            var startedIdx = events.FindIndex(e => e.Event == "recording.started");

            Assert.True(approvalIdx >= 0, "confirmation.approved audit event missing.");
            Assert.True(startedIdx >= 0, "recording.started audit event missing.");
            Assert.True(approvalIdx < startedIdx,
                "confirmation.approved must precede recording.started in the audit log.");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_Rejected_BackendStartNotCalled()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Reject };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            Assert.False(_backend.StartCalled, "Backend.Start must not be called when confirmation is rejected.");

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());

            var recordingId = doc.RootElement.GetProperty("data").GetProperty("summary").GetProperty("recording_id").GetString();
            var statusResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}");
            var statusBody = await statusResponse.Content.ReadAsStringAsync();
            using var statusDoc = JsonDocument.Parse(statusBody);
            Assert.Equal("rejected", statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_MicrophoneEnabled_ValidDevice_GoesPending_BackendNotStarted()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());

            // Consent invariant: backend must not start while pending confirmation.
            Assert.False(_backend.StartCalled, "Backend.Start must not be called while pending confirmation for microphone request.");
            Assert.Contains(_audit!.Events, e => e.Event == "confirmation.created");

            // The confirmation summary should expose the resolved device display name
            // (without leaking it into the audit log).
            var summary = doc.RootElement.GetProperty("data").GetProperty("summary");
            Assert.Contains("Test Microphone", summary.GetProperty("audio").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_MicrophoneEnabled_NoDevices_FailsFast_NoBackend()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider();
        var server = CreateServer(tray, provider);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(503, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.False(_backend.StartCalled, "Backend.Start must not be called when no microphone device is available.");
            Assert.DoesNotContain(_audit!.Events, e => e.Event == "confirmation.created");

            var recordings = _engine!.List().ToList();
            Assert.Empty(recordings);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_MicrophoneEnabled_Approved_BackendStartsWithResolvedDevice()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            Assert.True(_backend.StartCalled, "Backend.Start must be called after local approval for microphone request.");
            Assert.NotNull(_backend.LastConfig);
            Assert.True(_backend.LastConfig!.Microphone);
            Assert.Equal("mic_1", _backend.LastConfig!.MicDevice);
        }
        finally
        {
            server.Stop();
        }
    }

    [Theory]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"audio\":{\"microphone\":{\"enabled\":false}},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}")]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}")]
    public async Task CreateRecording_AudioDisabledOrAbsent_CreatesPendingConfirmation(string bodyJson)
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent(bodyJson));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.False(_backend.StartCalled);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_PrimaryDisplay_MicrophoneEnabled_ValidDevice_GoesPending()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        int displayCallCount = 0;
        try
        {
            SystemQuery.SetDisplayProvider(() =>
            {
                displayCallCount++;
                return new List<SystemQuery.DisplayInfo>
                {
                    new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
                };
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.True(displayCallCount >= 1, "Primary display resolution should call the display provider at least once.");
            Assert.False(_backend.StartCalled);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_ActiveWindow_MicrophoneEnabled_ValidDevice_GoesPending()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        int activeWindowCallCount = 0;
        try
        {
            SystemQuery.SetActiveWindowProvider(() =>
            {
                activeWindowCallCount++;
                return new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 1234, true, false,
                    new SystemQuery.Bounds(0, 0, 1280, 720));
            });
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new("window_1", "Notepad", "notepad.exe", 1234, true, false,
                    new SystemQuery.Bounds(0, 0, 1280, 720))
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"active_window\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.Equal(1, activeWindowCallCount);
            Assert.False(_backend.StartCalled);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_SelectedRegion_MicrophoneEnabled_RegionSelectionCalled()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("display_unavailable", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.True(tray.RequestRegionSelectionCallCount > 0);
            Assert.False(_backend.StartCalled);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_LastRegion_MicrophoneEnabled_NoLastRegion_ReturnsSourceNotFound()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var server = CreateServer(tray, provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"last_region\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(404, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("SOURCE_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(_backend.StartCalled);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_SystemAudioEnabled_FailsFast_WithCorrectCapability()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"audio\":{\"system_audio\":{\"enabled\":true}},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(400, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("CAPABILITY_NOT_IMPLEMENTED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("system_audio", doc.RootElement.GetProperty("error").GetProperty("details").GetProperty("capability").GetString());
            Assert.False(_backend.StartCalled);
            Assert.DoesNotContain(_audit!.Events, e => e.Event == "confirmation.created");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_SystemAudioEnabled_FailsFast_WithCorrectCapability()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"audio\":{\"system_audio\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(400, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("CAPABILITY_NOT_IMPLEMENTED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("system_audio", doc.RootElement.GetProperty("error").GetProperty("details").GetProperty("capability").GetString());
            Assert.False(_backend.StartCalled);
            Assert.DoesNotContain(_audit!.Events, e => e.Event == "confirmation.created");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_ConfirmationExpires_BackendNeverStarts()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            _engine!.ConfirmationTimeout = TimeSpan.FromMilliseconds(50);
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var recordingId = doc.RootElement.GetProperty("data").GetProperty("summary").GetProperty("recording_id").GetString();
            var confirmationId = doc.RootElement.GetProperty("data").GetProperty("summary").GetProperty("confirmation_id").GetString();

            // Wait for the engine's expiry continuation to finish.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (_audit!.Events.Any(e => e.Event == "confirmation.expired"))
                    break;
                await Task.Delay(20);
            }

            Assert.Contains(_audit!.Events, e => e.Event == "confirmation.expired");
            Assert.False(_backend.StartCalled, "Backend.Start must not be called when confirmation expires.");

            var statusResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}");
            var statusBody = await statusResponse.Content.ReadAsStringAsync();
            using var statusDoc = JsonDocument.Parse(statusBody);
            Assert.Equal("expired", statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString());

            var confResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/{confirmationId}");
            var confBody = await confResponse.Content.ReadAsStringAsync();
            using var confDoc = JsonDocument.Parse(confBody);
            Assert.Equal("expired", confDoc.RootElement.GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            // Give the expiry continuation a moment to finish before cleanup.
            await Task.Delay(100);
            server.Stop();
        }
    }
}
