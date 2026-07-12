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
/// API-level tests for <c>capabilities.interaction.stop_controls</c> in tray and headless modes.
/// Serialized via the HeadlessHostIntegration collection because each test binds ApiServer.Port.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class CapabilitiesStopControlsTests
{
    private sealed class TrayCapableFake : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        public bool SupportsFloatingStopButton => true;
        public bool SupportsTrayStop => true;
        public bool SupportsGlobalStopHotkey => true;
        public bool IsGlobalStopHotkeyRegistered { get; set; }
        public string? GlobalStopHotkeyGesture => "Ctrl+Shift+F10";

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) =>
            callback(ConfirmationDecision.Approve());

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) =>
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class HeadlessCapableFake : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) =>
            callback(ConfirmationDecision.Reject());

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) =>
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private static (ApiServer server, string dataDir) CreateServer(ITrayContext tray)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"cap-stop-controls-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        var server = new ApiServer(engine, audit, tray);
        return (server, dataDir);
    }

    private static void Cleanup(string dataDir)
    {
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    [Fact]
    public async Task Capabilities_TrayMode_StopControls_ReflectRegisteredState()
    {
        var tray = new TrayCapableFake { IsGlobalStopHotkeyRegistered = true };
        var (server, dataDir) = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var stopControls = doc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("stop_controls");

            Assert.True(stopControls.GetProperty("floating_button").GetBoolean());
            Assert.True(stopControls.GetProperty("tray_stop").GetBoolean());

            var hotkey = stopControls.GetProperty("global_hotkey");
            Assert.True(hotkey.GetProperty("supported").GetBoolean());
            Assert.True(hotkey.GetProperty("registered").GetBoolean());
            Assert.Equal("Ctrl+Shift+F10", hotkey.GetProperty("gesture").GetString());
            Assert.Equal("stop_all_active_recordings", hotkey.GetProperty("behavior").GetString());
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_TrayMode_StopControls_RegisteredFalse_WhenNotRegistered()
    {
        var tray = new TrayCapableFake { IsGlobalStopHotkeyRegistered = false };
        var (server, dataDir) = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var stopControls = doc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("stop_controls");
            var hotkey = stopControls.GetProperty("global_hotkey");

            Assert.True(hotkey.GetProperty("supported").GetBoolean());
            Assert.False(hotkey.GetProperty("registered").GetBoolean());
            Assert.Equal("Ctrl+Shift+F10", hotkey.GetProperty("gesture").GetString());
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_HeadlessMode_StopControls_AreDisabled()
    {
        var tray = new HeadlessCapableFake();
        var (server, dataDir) = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var stopControls = doc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("stop_controls");

            Assert.False(stopControls.GetProperty("floating_button").GetBoolean());
            Assert.False(stopControls.GetProperty("tray_stop").GetBoolean());

            var hotkey = stopControls.GetProperty("global_hotkey");
            Assert.False(hotkey.GetProperty("supported").GetBoolean());
            Assert.False(hotkey.GetProperty("registered").GetBoolean());
            Assert.Null(hotkey.GetProperty("gesture").GetString());
            Assert.Equal("stop_all_active_recordings", hotkey.GetProperty("behavior").GetString());
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir);
        }
    }
}
