using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Verifies that no first-frame progress evidence is produced while the local
/// user has not approved the recording. Covers pending, rejected, and expired
/// confirmations via the public API.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class FirstFrameConsentInvariantTests : IDisposable
{
    private sealed class ControllableTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;

        public enum DecisionMode { Approve, Reject, Timeout }

        public DecisionMode Mode { get; set; } = DecisionMode.Timeout;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            if (Mode == DecisionMode.Timeout)
                return;

            var decision = Mode == DecisionMode.Approve
                ? ConfirmationDecision.Approve()
                : ConfirmationDecision.Reject();
            callback(decision);
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public bool StartCalled { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            StartCalled = true;
            cfg.CommandArgs = "fake args";
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private ApiServer? _server;
    private string? _dataDir;
    private RollingJsonlWriter? _writer;
    private RecordingPerformanceTracer? _tracer;
    private RecordingEngine? _engine;
    private readonly FakeCaptureBackend _backend = new();

    private ApiServer CreateServer(ControllableTray tray)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"first-frame-consent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var audit = new AuditLogger();
        _writer = new RollingJsonlWriter(Path.Combine(_dataDir, "perf", "recording-traces.jsonl"));
        _tracer = new RecordingPerformanceTracer(_writer);
        _engine = new RecordingEngine(audit, _tracer);
        _engine.SetTray(tray);
        _engine.BackendFactory = _ => (_backend, "fake");
        _server = new ApiServer(_engine, audit, tray);
        return _server;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<string> GetConfirmationStatusAsync(HttpClient client, string confirmationId)
    {
        var response = await client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/{confirmationId}");
        Assert.Equal(200, (int)response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        return body["status"]!.GetValue<string>();
    }

    private static async Task<string> GetRecordingStatusAsync(HttpClient client, string recordingId)
    {
        var response = await client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}");
        Assert.Equal(200, (int)response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        return body["status"]!.GetValue<string>();
    }

    private static bool HasFirstFrameEvent(RollingJsonlWriter writer)
    {
        writer.Flush();
        if (!File.Exists(writer.BasePath)) return false;
        return File.ReadAllLines(writer.BasePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .Any(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        try { _tracer?.Dispose(); } catch { }
        if (_dataDir != null)
        {
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    [Fact]
    public async Task PendingConfirmation_NoBackendStart_NoFirstFrameEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new System.Collections.Generic.List<SystemQuery.DisplayInfo>
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

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
            var recordingId = body["recording_id"]!.GetValue<string>();
            var confirmationId = body["confirmation_id"]!.GetValue<string>();

            Assert.Equal("pending", await GetConfirmationStatusAsync(client, confirmationId));
            Assert.Equal("pending_confirmation", await GetRecordingStatusAsync(client, recordingId));
            Assert.False(_backend.StartCalled);
            Assert.False(HasFirstFrameEvent(_writer!));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RejectedConfirmation_NoBackendStart_NoFirstFrameEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Reject };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new System.Collections.Generic.List<SystemQuery.DisplayInfo>
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

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
            var recordingId = body["recording_id"]!.GetValue<string>();
            var confirmationId = body["confirmation_id"]!.GetValue<string>();

            var confirmationStatus = await GetConfirmationStatusAsync(client, confirmationId);
            Assert.Equal("rejected", confirmationStatus);
            Assert.NotEqual("pending", confirmationStatus);
            Assert.NotEqual("expired", confirmationStatus);
            Assert.Equal("rejected", await GetRecordingStatusAsync(client, recordingId));
            Assert.False(_backend.StartCalled);
            Assert.False(HasFirstFrameEvent(_writer!));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ExpiredConfirmation_NoBackendStart_NoFirstFrameEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            // Shorten the confirmation timeout so the test does not wait.
            _engine!.ConfirmationTimeout = TimeSpan.FromMilliseconds(50);

            SystemQuery.SetDisplayProvider(() => new System.Collections.Generic.List<SystemQuery.DisplayInfo>
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

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
            var recordingId = body["recording_id"]!.GetValue<string>();
            var confirmationId = body["confirmation_id"]!.GetValue<string>();

            // Poll until the confirmation actually expires instead of relying on a fixed sleep.
            var status = await PollConfirmationStatusAsync(client, confirmationId, "expired", TimeSpan.FromSeconds(5));
            Assert.Equal("expired", status);
            Assert.Equal("expired", await GetRecordingStatusAsync(client, recordingId));
            Assert.False(_backend.StartCalled);
            Assert.False(HasFirstFrameEvent(_writer!));
        }
        finally
        {
            await Task.Delay(100);
            server.Stop();
        }
    }

    private static async Task<string> PollConfirmationStatusAsync(HttpClient client, string confirmationId, string expected, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = await GetConfirmationStatusAsync(client, confirmationId);
            if (string.Equals(status, expected, StringComparison.OrdinalIgnoreCase))
                return status;
            await Task.Delay(50);
        }
        return await GetConfirmationStatusAsync(client, confirmationId);
    }
}
