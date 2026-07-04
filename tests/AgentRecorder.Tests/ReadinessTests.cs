using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for RuntimeReadiness: ready.json writing, named event, /capabilities
/// readiness field, and security boundary preservation.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class ReadinessTests
{
    private sealed class FakeTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<bool> callback) => callback(true);
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
            => callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private static (ApiServer server, RuntimeReadiness readiness, string dataDir) CreateServerWithReadiness()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"readiness-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        var readiness = new RuntimeReadiness("headless", ApiServer.Port);
        readiness.CleanupOldReadyFile();

        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new FakeTray();
        engine.SetTray(tray);
        var server = new ApiServer(engine, audit, tray, readiness);
        return (server, readiness, dataDir);
    }

    private static void Cleanup(string dataDir, RuntimeReadiness? readiness)
    {
        try { readiness?.Dispose(); } catch { }
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
    public void RuntimeReadiness_MarkReady_WritesValidReadyJson()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"readiness-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        try
        {
            var readiness = new RuntimeReadiness("headless", ApiServer.Port);
            var snapshot = readiness.MarkReady();

            // ready.json should exist at <data-dir>/runtime/ready.json
            var expectedPath = Path.Combine(dataDir, "runtime", "ready.json");
            Assert.True(File.Exists(expectedPath), "ready.json should exist");
            Assert.Equal(expectedPath, snapshot.ReadyFile);

            // Should be valid JSON
            var json = File.ReadAllText(expectedPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // All required fields present
            Assert.True(root.GetProperty("ready").GetBoolean());
            Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
            Assert.Equal(ApiServer.Port, root.GetProperty("port").GetInt32());
            Assert.Equal("v1", root.GetProperty("api_version").GetString());
            Assert.Equal("headless", root.GetProperty("mode").GetString());
            Assert.True(root.GetProperty("startup_elapsed_ms").GetInt64() >= 0);
            Assert.True(root.TryGetProperty("started_at", out _));
            Assert.True(root.TryGetProperty("ready_at", out _));
            Assert.True(root.TryGetProperty("data_dir", out _));
            Assert.True(root.TryGetProperty("api_key_file", out _));
            Assert.True(root.TryGetProperty("audit_log_path", out _));
            Assert.True(root.TryGetProperty("ready_file", out _));
            Assert.Equal(RuntimeReadiness.NamedEventName, root.GetProperty("named_event").GetString());

            readiness.Dispose();
        }
        finally
        {
            Cleanup(dataDir, null);
        }
    }

    [Fact]
    public void RuntimeReadiness_ReadyJson_DoesNotContainApiKeyContent()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"readiness-nokey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        try
        {
            var apiKey = ApiKeyAuth.GetApiKey();
            Assert.NotNull(apiKey);

            var readiness = new RuntimeReadiness("tray", ApiServer.Port);
            readiness.MarkReady();

            var json = File.ReadAllText(Path.Combine(dataDir, "runtime", "ready.json"));

            // The ready.json must NOT contain the actual API key value
            Assert.DoesNotContain(apiKey, json);

            // But it SHOULD contain the api_key_file path
            Assert.Contains("api_key_file", json);

            readiness.Dispose();
        }
        finally
        {
            Cleanup(dataDir, null);
        }
    }

    [Fact]
    public void RuntimeReadiness_ReadyFilePath_IsInRuntimeSubdir()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"readiness-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        try
        {
            var readiness = new RuntimeReadiness("tray", ApiServer.Port);
            var expectedPath = Path.Combine(dataDir, "runtime", "ready.json");
            Assert.Equal(expectedPath, readiness.ReadyFilePath);
            readiness.Dispose();
        }
        finally
        {
            Cleanup(dataDir, null);
        }
    }

    [Fact]
    public void RuntimeReadiness_Dispose_DeletesReadyFile()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"readiness-dispose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        try
        {
            var readiness = new RuntimeReadiness("tray", ApiServer.Port);
            readiness.MarkReady();

            var readyPath = Path.Combine(dataDir, "runtime", "ready.json");
            Assert.True(File.Exists(readyPath));

            readiness.Dispose();

            // After dispose, the ready file should be deleted
            Assert.False(File.Exists(readyPath), "ready.json should be deleted on Dispose");
        }
        finally
        {
            Cleanup(dataDir, null);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Capabilities_IncludesReadinessField()
    {
        var (server, readiness, dataDir) = CreateServerWithReadiness();
        try
        {
            server.Start();
            readiness.MarkReady();

            using var client = CreateClient();
            var resp = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("readiness", body);
            Assert.Contains("ready", body);
            Assert.Contains("startup_elapsed_ms", body);
            Assert.Contains("named_event", body);
            Assert.Contains("api_key_file", body);
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir, readiness);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Capabilities_Readiness_DoesNotContainApiKeyContent()
    {
        var (server, readiness, dataDir) = CreateServerWithReadiness();
        try
        {
            server.Start();
            readiness.MarkReady();

            var apiKey = ApiKeyAuth.GetApiKey();

            using var client = CreateClient();
            var resp = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            var body = await resp.Content.ReadAsStringAsync();

            // Must not leak the API key value
            Assert.DoesNotContain(apiKey, body);
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir, readiness);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Capabilities_Readiness_ModeIsHeadless()
    {
        var (server, readiness, dataDir) = CreateServerWithReadiness();
        try
        {
            server.Start();
            readiness.MarkReady();

            using var client = CreateClient();
            var resp = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            var body = await resp.Content.ReadAsStringAsync();

            // The readiness mode should be "headless" since FakeTray.HostMode = "headless"
            Assert.Contains("\"mode\":\"headless\"", body);
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir, readiness);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ConfirmationApprove_StillReturns405()
    {
        // Security regression: HTTP self-approval must still be blocked.
        var (server, readiness, dataDir) = CreateServerWithReadiness();
        try
        {
            server.Start();

            using var client = CreateClient();
            var apiKey = ApiKeyAuth.GetApiKey();
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/fake_id/approve?reason=test");
            req.Headers.Add("X-Agent-Recorder-Key", apiKey);
            var resp = await client.SendAsync(req);

            Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally
        {
            try { server.Stop(); } catch { }
            Cleanup(dataDir, readiness);
        }
    }
}
