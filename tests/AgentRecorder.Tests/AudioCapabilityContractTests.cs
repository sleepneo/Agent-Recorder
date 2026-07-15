using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Contract tests for the three public audio capability endpoints:
/// <c>/capabilities</c>, <c>/permissions</c>, and <c>/audio/devices</c>.
/// Verifies that the API honestly reports audio as unimplemented while
/// preserving backward-compatible field types.
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

    private ApiServer? _server;
    private string? _dataDir;

    private ApiServer CreateServer()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"audio-contract-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var tray = new NoOpTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray);
        return _server;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

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
    public async Task Capabilities_RecordingAudio_IsEmptyArray_WithAudioCapabilitiesNotImplemented()
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
            Assert.Equal(0, audio.GetArrayLength());

            var caps = recording.GetProperty("audio_capabilities");
            var mic = caps.GetProperty("microphone");
            Assert.False(mic.GetProperty("supported").GetBoolean());
            Assert.Equal("not_implemented", mic.GetProperty("status").GetString());

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
    public async Task Permissions_MicrophoneAndSystemAudio_NotImplemented()
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
            Assert.False(mic.GetProperty("supported").GetBoolean());
            Assert.Equal("not_implemented", mic.GetProperty("status").GetString());

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
    public async Task AudioDevices_InputDevicesEmpty_StatusNotImplemented()
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

            Assert.Equal("not_implemented", data.GetProperty("status").GetString());
            Assert.False(data.GetProperty("microphone_supported").GetBoolean());
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
}
