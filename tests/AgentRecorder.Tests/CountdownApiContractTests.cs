using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class CountdownApiContractTests
{
    [Theory]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"}}", 3)]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":0}", 0)]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":1}", 1)]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":3}", 3)]
    [InlineData("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":10}", 10)]
    public async Task RawRecording_ActualHttpRequest_PropagatesCountdown(string requestBody, int expectedSeconds)
    {
        var tray = new RawTray();
        var server = CreateServer(tray, out var engine, out var dataDir);
        try
        {
            ConfigureDisplay();
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent(requestBody));

            Assert.Equal(200, (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.Equal(expectedSeconds, data.GetProperty("summary").GetProperty("countdown_seconds").GetInt32());
            var recordingId = data.GetProperty("recording_id").GetString();
            Assert.NotNull(recordingId);

            var statusResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}");
            Assert.Equal(200, (int)statusResponse.StatusCode);
            using var statusDocument = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal(expectedSeconds,
                statusDocument.RootElement.GetProperty("data").GetProperty("config").GetProperty("countdown_seconds").GetInt32());

            var waitResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}?wait_ms=10&since_status=pending_confirmation");
            Assert.Equal(200, (int)waitResponse.StatusCode);
            using var waitDocument = JsonDocument.Parse(await waitResponse.Content.ReadAsStringAsync());
            Assert.Equal(expectedSeconds,
                waitDocument.RootElement.GetProperty("data").GetProperty("config").GetProperty("countdown_seconds").GetInt32());
            Assert.Single(engine._recs);
            Assert.Equal(1, tray.ConfirmationCount);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task RawRecording_InvalidCountdown_Returns400BeforeSideEffects(string value)
    {
        var tray = new RawTray();
        var server = CreateServer(tray, out var engine, out var dataDir);
        try
        {
            ConfigureDisplay();
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var body = $"{{\"source\":{{\"type\":\"display\",\"display_id\":\"display_1\"}},\"countdown_seconds\":{value}}}";
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent(body));

            Assert.Equal(400, (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(engine._recs);
            Assert.Equal(0, tray.ConfirmationCount);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    private static ApiServer CreateServer(RawTray tray, out RecordingEngine engine, out string dataDir)
    {
        dataDir = Path.Combine(Path.GetTempPath(), $"countdown-api-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);
        var audit = new AuditLogger();
        engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        return new ApiServer(engine, audit, tray);
    }

    private static void ConfigureDisplay()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    private static void Cleanup(string dataDir)
    {
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetDisplayTopologyProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed class RawTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public int ConfirmationCount { get; private set; }
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) => ConfirmationCount++;
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
