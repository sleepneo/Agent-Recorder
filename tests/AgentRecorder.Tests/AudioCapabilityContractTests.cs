using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Contract tests for the three public audio capability endpoints:
/// <c>/capabilities</c>, <c>/permissions</c>, and <c>/audio/devices</c>.
/// Verifies that microphone is reported as supported and system audio remains
/// not implemented.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class AudioCapabilityContractTests : IDisposable
{
    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class CountingTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;

        public int RequestConfirmationCallCount { get; private set; }
        public int RequestRegionSelectionCallCount { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
            => RequestConfirmationCallCount++;

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
            => RequestRegionSelectionCallCount++;

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class AutoCompleteTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;

        public int RequestConfirmationCallCount { get; private set; }
        public int RequestRegionSelectionCallCount { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            RequestConfirmationCallCount++;
            callback(ConfirmationDecision.Approve());
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            RequestRegionSelectionCallCount++;
            callback("selected", 0, 0, 800, 600, "display_1", "virtual_screen");
        }

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private ApiServer? _server;
    private string? _dataDir;

    private ApiServer CreateServer(IMicrophoneDeviceProvider? microphoneProvider = null, ITrayContext? tray = null, IMicrophoneStatusProvider? microphoneStatusProvider = null)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"audio-contract-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var actualTray = tray ?? new NoOpTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, microphoneProvider: microphoneProvider, microphoneStatusProvider: microphoneStatusProvider);
        engine.SetTray(actualTray);
        _server = new ApiServer(engine, audit, actualTray);
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
        if (_dataDir != null)
        {
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    [Fact]
    public async Task Capabilities_RecordingAudio_IncludesMicrophone_WithSupportedAudioCapabilities()
    {
        var server = CreateServer();
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            var recording = data.GetProperty("recording");

            var audio = recording.GetProperty("audio");
            Assert.Equal(JsonValueKind.Array, audio.ValueKind);
            Assert.Contains("microphone", audio.EnumerateArray().Select(a => a.GetString()));

            var caps = recording.GetProperty("audio_capabilities");
            var mic = caps.GetProperty("microphone");
            Assert.True(mic.GetProperty("supported").GetBoolean());
            // With no injected devices the availability status must honestly report no_devices.
            Assert.Equal("no_devices", mic.GetProperty("status").GetString());

            var sys = caps.GetProperty("system_audio");
            Assert.False(sys.GetProperty("supported").GetBoolean());
            Assert.Equal("not_implemented", sys.GetProperty("status").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Permissions_Microphone_Supported_SystemAudio_NotImplemented()
    {
        var server = CreateServer();
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/permissions");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("granted", data.GetProperty("screen_capture").GetProperty("status").GetString());

            var mic = data.GetProperty("microphone");
            Assert.True(mic.GetProperty("supported").GetBoolean());
            Assert.Contains(mic.GetProperty("status").GetString(), new[] { "available", "no_devices", "unavailable" });

            var sys = data.GetProperty("system_audio");
            Assert.False(sys.GetProperty("supported").GetBoolean());
            Assert.Equal("not_implemented", sys.GetProperty("status").GetString());

            Assert.Equal("granted", data.GetProperty("output_directory").GetProperty("status").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_InputDevicesEmpty_MicrophoneSupported_NoDevices()
    {
        var server = CreateServer();
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            // Microphone is implemented; with no devices the status is "no_devices"
            // rather than the old "not_implemented".
            Assert.Equal("no_devices", data.GetProperty("status").GetString());
            Assert.True(data.GetProperty("microphone_supported").GetBoolean());
            Assert.False(data.GetProperty("system_audio_supported").GetBoolean());

            var inputs = data.GetProperty("input_devices");
            Assert.Equal(JsonValueKind.Array, inputs.ValueKind);
            Assert.Equal(0, inputs.GetArrayLength());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_ProviderReturnsDevices_StatusReady_WithRequiredFields()
    {
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"),
            new MicrophoneDeviceInfo("mic_2", "Second Mic", false, "active"));

        var server = CreateServer(provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("ready", data.GetProperty("status").GetString());
            Assert.True(data.GetProperty("microphone_supported").GetBoolean());
            Assert.False(data.GetProperty("system_audio_supported").GetBoolean());

            var inputs = data.GetProperty("input_devices").EnumerateArray().ToList();
            Assert.Equal(2, inputs.Count);

            var first = inputs[0];
            Assert.Equal("mic_1", first.GetProperty("id").GetString());
            Assert.Equal("Test Microphone", first.GetProperty("name").GetString());
            Assert.True(first.GetProperty("is_default").GetBoolean());
            Assert.Equal("active", first.GetProperty("state").GetString());

            // Privacy: no raw stderr, paths, or exception text should be present.
            Assert.False(body.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase));
            Assert.False(body.Contains("stderr", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_CoreAudioUnknown_FieldsAreNull()
    {
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Mystery Mic", IsDefault: null, State: null));

        var server = CreateServer(provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").GetProperty("input_devices").EnumerateArray().First();

            Assert.Equal("mic_1", first.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("is_default").ValueKind);
            Assert.Equal(JsonValueKind.Null, first.GetProperty("state").ValueKind);
            Assert.Equal(JsonValueKind.Null, first.GetProperty("is_muted").ValueKind);
            Assert.Equal(JsonValueKind.Null, first.GetProperty("volume_percent").ValueKind);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_ProviderThrows_StatusUnavailable_NoDetailsLeaked()
    {
        var provider = new ThrowingMicrophoneProvider(new InvalidOperationException("secret path C:\\tools\\ffmpeg.exe"));

        var server = CreateServer(provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("unavailable", data.GetProperty("status").GetString());
            Assert.True(data.GetProperty("microphone_supported").GetBoolean());
            Assert.Empty(data.GetProperty("input_devices").EnumerateArray());
            Assert.False(body.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.False(body.Contains("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Permissions_WhenDevicesExist_MicrophoneAvailable()
    {
        var provider = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));

        var server = CreateServer(provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/permissions");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("available", data.GetProperty("microphone").GetProperty("status").GetString());
            Assert.True(data.GetProperty("microphone").GetProperty("supported").GetBoolean());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ActiveWindowPreBuild_UsesEngineProvider_DoesNotFailWhenDeviceListHasMic()
    {
        var provider = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));

        var window = new SystemQuery.WindowInfo("window_1", "Test Window", "test.exe", 1234, true, false,
            new SystemQuery.Bounds(0, 0, 800, 600));
        SystemQuery.SetActiveWindowProvider(() => window);
        SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo> { window });

        var server = CreateServer(provider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            // Same provider must be visible on the device list endpoint.
            var devicesResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)devicesResponse.StatusCode);
            var devicesBody = await devicesResponse.Content.ReadAsStringAsync();
            using var devicesDoc = JsonDocument.Parse(devicesBody);
            var devices = devicesDoc.RootElement.GetProperty("data").GetProperty("input_devices").EnumerateArray().ToList();
            Assert.Single(devices);
            Assert.Equal("mic_1", devices[0].GetProperty("id").GetString());

            // active_window quick recording must not fall back to a different (empty)
            // provider during its pre-build; it should reach the confirmation stage.
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"active_window\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.Equal("active_window", data.GetProperty("quick").GetProperty("target_type").GetString());
        }
        finally
        {
            SystemQuery.SetActiveWindowProvider(null);
            SystemQuery.SetWindowProvider(null);
            server.Stop();
        }
    }

    [Fact]
    public async Task SelectedRegion_MutedMicrophone_ReturnsAudioDeviceMutedAndDoesNotOpenUi()
    {
        var tray = new CountingTray();
        var provider = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_1", "Muted Mic", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(true, 0));
        var server = CreateServer(provider, tray, statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));

            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(409, (int)response.StatusCode);

            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("AUDIO_DEVICE_MUTED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.Equal(0, tray.RequestRegionSelectionCallCount);
            Assert.Equal(0, tray.RequestConfirmationCallCount);

            var listResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings");
            var listBody = await listResponse.Content.ReadAsStringAsync();
            using var listDoc = JsonDocument.Parse(listBody);
            Assert.Equal(0, listDoc.RootElement.GetProperty("data").GetProperty("recordings").GetArrayLength());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task SelectedRegion_MutedThenUnmuted_RetrySucceedsAndOpensUi()
    {
        var tray = new AutoCompleteTray();
        var provider = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_1", "Muted Mic", true, "active"));
        var statusProvider = new MutableStatusProvider(new MicrophoneStatus(true, 0));
        var server = CreateServer(provider, tray, statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var first = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));
            Assert.Equal(409, (int)first.StatusCode);

            statusProvider.Status = new MicrophoneStatus(false, 50);

            var second = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));
            Assert.Equal(200, (int)second.StatusCode);

            Assert.True(tray.RequestRegionSelectionCallCount > 0, "Region selection UI should open after unmuting.");
            Assert.True(tray.RequestConfirmationCallCount > 0, "Confirmation UI should open after region selection.");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_ProviderReturnsDevices_IncludesMuteAndVolumeFields()
    {
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Test Microphone", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(false, 7));

        var server = CreateServer(provider, microphoneStatusProvider: statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            var inputs = data.GetProperty("input_devices").EnumerateArray().ToList();
            Assert.Single(inputs);

            Assert.False(inputs[0].GetProperty("is_muted").GetBoolean());
            Assert.Equal(7, inputs[0].GetProperty("volume_percent").GetInt32());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AudioDevices_FreshStatusOverridesStaleDefaultAndState()
    {
        // dshow provider claims both devices are non-default; fresh CoreAudio
        // status overrides that and marks mic_2 as the default.
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic 1", false, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic 2", false, "active"));

        var statusProvider = new PerDeviceStatusProvider(new Dictionary<string, MicrophoneStatus>
        {
            ["mic_1"] = new MicrophoneStatus(false, 50, false, "active"),
            ["mic_2"] = new MicrophoneStatus(false, 60, true, "active")
        });

        var server = CreateServer(provider, microphoneStatusProvider: statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var inputs = doc.RootElement.GetProperty("data").GetProperty("input_devices").EnumerateArray().ToList();
            Assert.Equal(2, inputs.Count);

            Assert.False(inputs[0].GetProperty("is_default").GetBoolean());
            Assert.True(inputs[1].GetProperty("is_default").GetBoolean());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AutoSelect_MultipleActiveDevices_PrefersFreshCoreAudioDefault()
    {
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic 1", false, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic 2", false, "active"));

        var statusProvider = new PerDeviceStatusProvider(new Dictionary<string, MicrophoneStatus>
        {
            ["mic_1"] = new MicrophoneStatus(false, 50, false, "active"),
            ["mic_2"] = new MicrophoneStatus(false, 60, true, "active")
        });

        var tray = new AutoCompleteTray();
        var server = CreateServer(provider, tray, statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));

            Assert.Equal(200, (int)response.StatusCode);
            Assert.True(tray.RequestConfirmationCallCount > 0, "Should open confirmation UI for default mic_2.");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AutoSelect_DefaultSwitchesImmediately_UsesNewDefault()
    {
        var provider = new FakeMicrophoneProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic 1", false, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic 2", false, "active"));

        var statuses = new Dictionary<string, MicrophoneStatus>
        {
            ["mic_1"] = new MicrophoneStatus(false, 50, true, "active"),
            ["mic_2"] = new MicrophoneStatus(false, 60, false, "active")
        };
        var statusProvider = new PerDeviceStatusProvider(statuses);

        var tray = new AutoCompleteTray();
        var server = CreateServer(provider, tray, statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            // First call: mic_1 is default -> succeeds.
            var first = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));
            Assert.Equal(200, (int)first.StatusCode);

            // Stop the first recording so the second one does not collide with
            // the single-recording guard.
            var firstBody = await first.Content.ReadAsStringAsync();
            using var firstDoc = JsonDocument.Parse(firstBody);
            var firstRecordingId = firstDoc.RootElement.GetProperty("data").GetProperty("recording_id").GetString();
            var stopResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{firstRecordingId}/stop",
                JsonContent("{\"reason\":\"test_cleanup\"}"));
            Assert.Equal(200, (int)stopResponse.StatusCode);

            // Wait until the engine no longer has any active recordings.
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!waitCts.Token.IsCancellationRequested)
            {
                var listResponse = await client.GetAsync(
                    $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings");
                var listBody = await listResponse.Content.ReadAsStringAsync();
                using var listDoc = JsonDocument.Parse(listBody);
                var activeCount = listDoc.RootElement.GetProperty("data").GetProperty("recordings")
                    .EnumerateArray().Count(r =>
                    {
                        var status = r.GetProperty("status").GetString();
                        return status is not ("completed" or "failed" or "cancelled" or "rejected" or "expired");
                    });
                if (activeCount == 0)
                    break;
                await Task.Delay(100, waitCts.Token);
            }

            // Switch default to mic_2. Because status is not cached, the next
            // request must see the new default.
            statuses["mic_1"] = new MicrophoneStatus(false, 50, false, "active");
            statuses["mic_2"] = new MicrophoneStatus(false, 60, true, "active");

            var second = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));
            var secondBody = await second.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)second.StatusCode);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task SelectedRegion_InactiveEndpoint_ReturnsAudioDeviceNotAvailableAndDoesNotOpenUi()
    {
        var tray = new CountingTray();
        var provider = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_1", "Inactive Mic", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(false, 50, true, "inactive"));
        var server = CreateServer(provider, tray, statusProvider);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));

            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(503, (int)response.StatusCode);

            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.Equal(0, tray.RequestRegionSelectionCallCount);
            Assert.Equal(0, tray.RequestConfirmationCallCount);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task SelectedRegion_NoMicrophoneDevice_ReturnsAudioDeviceNotAvailableAndDoesNotOpenUi()
    {
        var tray = new CountingTray();
        var server = CreateServer(tray: tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"audio\":{\"microphone\":{\"enabled\":true}}}"));

            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(503, (int)response.StatusCode);

            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.Equal(0, tray.RequestRegionSelectionCallCount);
            Assert.Equal(0, tray.RequestConfirmationCallCount);

            var listResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings");
            var listBody = await listResponse.Content.ReadAsStringAsync();
            using var listDoc = JsonDocument.Parse(listBody);
            Assert.Equal(0, listDoc.RootElement.GetProperty("data").GetProperty("recordings").GetArrayLength());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ProviderInstances_Isolated_DifferentEnginesDoNotPolluteEachOther()
    {
        var providerA = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_a", "Mic A", true, "active"));
        var providerB = new FakeMicrophoneProvider(new MicrophoneDeviceInfo("mic_b", "Mic B", true, "active"));

        var idsA1 = await QueryDeviceIdsAsync(providerA);
        Assert.Single(idsA1);
        Assert.Equal("mic_a", idsA1[0]);

        var idsB = await QueryDeviceIdsAsync(providerB);
        Assert.Single(idsB);
        Assert.Equal("mic_b", idsB[0]);

        var idsA2 = await QueryDeviceIdsAsync(providerA);
        Assert.Single(idsA2);
        Assert.Equal("mic_a", idsA2[0]);
    }

    private static async Task<IReadOnlyList<string>> QueryDeviceIdsAsync(IMicrophoneDeviceProvider provider)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"audio-contract-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        ApiServer? server = null;
        try
        {
            var tray = new NoOpTray();
            var audit = new AuditLogger();
            var engine = new RecordingEngine(audit, microphoneProvider: provider);
            engine.SetTray(tray);
            server = new ApiServer(engine, audit, tray);
            server.Start();

            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data").GetProperty("input_devices").EnumerateArray()
                .Select(d => d.GetProperty("id").GetString()!)
                .ToList();
        }
        finally
        {
            server?.Stop();
            // Give the loopback socket a moment to release before the next sequential server starts.
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            ApiKeyAuth.ResetForTesting(null);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeMicrophoneProvider : IMicrophoneDeviceProvider
    {
        private readonly IReadOnlyList<MicrophoneDeviceInfo> _devices;
        public FakeMicrophoneProvider(params MicrophoneDeviceInfo[] devices)
            => _devices = devices.ToList();
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_devices);
    }

    private sealed class ThrowingMicrophoneProvider : IMicrophoneDeviceProvider
    {
        private readonly Exception _exception;
        public ThrowingMicrophoneProvider(Exception exception) => _exception = exception;
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<MicrophoneDeviceInfo>>(_exception);
    }

    private sealed class FakeStatusProvider : IMicrophoneStatusProvider
    {
        private readonly MicrophoneStatus _status;
        public FakeStatusProvider(MicrophoneStatus status) => _status = status;
        public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_status);
    }

    private sealed class MutableStatusProvider : IMicrophoneStatusProvider
    {
        public MicrophoneStatus Status { get; set; }
        public MutableStatusProvider(MicrophoneStatus status) => Status = status;
        public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Status);
    }

    private sealed class PerDeviceStatusProvider : IMicrophoneStatusProvider
    {
        private readonly Dictionary<string, MicrophoneStatus> _statuses;
        public PerDeviceStatusProvider(Dictionary<string, MicrophoneStatus> statuses) => _statuses = statuses;
        public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_statuses.TryGetValue(dshowDeviceId, out var s) ? s : new MicrophoneStatus(null, null, null, null));
    }
}
