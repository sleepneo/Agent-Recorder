using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the TcpListener-based HTTP adapter that replaced HttpListener.
/// Uses the HeadlessHostIntegrationCollection because both sets of tests bind
/// the fixed port 37891 and must not run in parallel.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class TcpHttpAdapterTests
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

    private static ApiServer CreateServer(out string dataDir)
    {
        dataDir = Path.Combine(Path.GetTempPath(), $"tcp-adapter-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new FakeTray();
        engine.SetTray(tray);
        return new ApiServer(engine, audit, tray);
    }

    private static void Cleanup(string dataDir)
    {
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static async Task<(int Status, string Body)> RawRequestAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ApiServer.Port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        stream.WriteTimeout = 5000;
        var reqBytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(reqBytes);
        client.Client.Shutdown(SocketShutdown.Send);

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            ms.Write(buffer, 0, read);

        var response = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(response))
            return (-1, "");

        var lines = response.Split("\r\n");
        if (lines.Length < 1 || lines[0].Split(' ').Length < 2)
            return (-1, response);

        var statusParts = lines[0].Split(' ');
        var status = int.Parse(statusParts[1]);
        var bodyStart = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyStart >= 0 ? response[(bodyStart + 4)..] : "";
        return (status, body);
    }

    [Fact]
    public async Task TcpHttpAdapter_GetCapabilities_Returns200()
    {
        var server = CreateServer(out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", body);
            Assert.Contains("Agent Recorder", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TcpHttpAdapter_MalformedRequest_Returns400()
    {
        var server = CreateServer(out var dataDir);
        try
        {
            server.Start();
            await Task.Delay(200);

            var (status, body) = await RawRequestAsync("NOT_A_VALID_REQUEST\r\n\r\n");
            Assert.Equal(400, status);
            Assert.Contains("BAD_REQUEST", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TcpHttpAdapter_PostRecordings_MissingKey_Returns401()
    {
        var server = CreateServer(out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(401, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("UNAUTHORIZED", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TcpHttpAdapter_PostRecordings_WrongKey_Returns403()
    {
        var server = CreateServer(out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", "wrong-key");
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(403, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("FORBIDDEN", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task TcpHttpAdapter_ConfirmationApprove_Returns405()
    {
        var server = CreateServer(out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/test-id/approve",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(405, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("METHOD_NOT_ALLOWED", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public void TcpHttpAdapter_PortOccupied_StartThrowsAndDoesNotLogServiceStarted()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"tcp-occupied-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        try
        {
            ApiKeyAuth.InitializeForTesting(dataDir);
            var audit = new AuditLogger();
            var engine = new RecordingEngine(audit);
            var tray = new FakeTray();
            engine.SetTray(tray);

            var blocker = new TcpListener(IPAddress.Loopback, ApiServer.Port);
            blocker.Start();
            try
            {
                var server = new ApiServer(engine, audit, tray);
                audit.Log("service.starting", new { mode = "test", port = ApiServer.Port });
                try
                {
                    server.Start();
                    Assert.Fail("Expected server start to fail because port is occupied");
                }
                catch (Exception ex)
                {
                    audit.Log("service.start_failed", new { mode = "test", error = ex.Message, type = ex.GetType().FullName });
                }
            }
            finally
            {
                try { blocker.Stop(); } catch { }
            }

            var logPath = Path.Combine(dataDir, "logs", "audit.jsonl");
            Assert.True(File.Exists(logPath), "Audit log should have been created");
            var events = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString())
                .ToList();

            Assert.Contains("service.starting", events);
            Assert.Contains("service.start_failed", events);
            Assert.DoesNotContain("service.started", events);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
            ApiKeyAuth.ResetForTesting(null);
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }
}
