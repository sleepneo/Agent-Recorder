using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Headless API host tests: verifies that the headless host never auto-approves
/// recording confirmations and that audit logging does not misreport a failed
/// startup as a successful service start.
/// </summary>
public class HeadlessHostTests
{
    private class CapturingAuditLogger : AuditLogger
    {
        public CapturingAuditLogger() : base() { }

        public System.Collections.Generic.List<string> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            Events.Add(evt);
            base.Log(evt, payload);
        }
    }

    [Fact]
    public void HeadlessTrayContext_RequestConfirmation_DoesNotAutoApprove()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", tmp.Path, EnvironmentVariableTarget.Process);
        try
        {
            var audit = new CapturingAuditLogger();
            var tray = new HeadlessTrayContext(audit);

            bool? approved = null;
            tray.RequestConfirmation(new { source = "display" }, result => approved = result);

            Assert.False(approved);
            Assert.Contains("confirmation.headless_unavailable", audit.Events);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void HeadlessTrayContext_SetRecording_SetIdle_ShowError_DoNotThrow()
    {
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", tmp.Path, EnvironmentVariableTarget.Process);
        try
        {
            var audit = new CapturingAuditLogger();
            var tray = new HeadlessTrayContext(audit);

            tray.SetRecording(new object());
            tray.SetIdle(new object());
            tray.SetAllIdle();
            tray.ShowError("test error");

            Assert.Contains("recording.headless_set_recording", audit.Events);
            Assert.Contains("recording.headless_set_idle", audit.Events);
            Assert.Contains("recording.headless_error", audit.Events);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void ServiceStarted_IsNotLogged_WhenServerStartFails()
    {
        using var tmp = new TempDirectory();
        var dataDir = tmp.Path;
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        try
        {
            var audit = new AuditLogger();
            var engine = new RecordingEngine(audit);
            var tray = new HeadlessTrayContext(audit);
            engine.SetTray(tray);

            // Bind a TCP listener on the port so the ApiServer.Start() will fail.
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
            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString())
                .ToList();

            Assert.Contains("service.starting", lines);
            Assert.Contains("service.start_failed", lines);
            Assert.DoesNotContain("service.started", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        }
    }
}

/// <summary>
/// Integration tests that start the real headless host executable.
/// Serialized into a dedicated collection because they bind the fixed port 37891.
/// </summary>
[CollectionDefinition("HeadlessHostIntegration", DisableParallelization = true)]
public class HeadlessHostIntegrationCollection { }

[Collection("HeadlessHostIntegration")]
public class HeadlessHostIntegrationTests
{
    private static string HeadlessExePath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var config = baseDir.Contains("Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            return Path.Combine(TestHelper.ProjectRoot,
                "src", "AgentRecorder.Headless", "bin", config,
                "net8.0-windows10.0.19041.0", "AgentRecorder.Headless.exe");
        }
    }

    private static Process StartHeadless(string dataDir, string? windowBackend = null)
    {
        Assert.True(File.Exists(HeadlessExePath), $"Headless executable not found at {HeadlessExePath}");

        var args = $"--data-dir \"{dataDir}\"";
        if (!string.IsNullOrWhiteSpace(windowBackend))
        {
            args += $" --window-backend {windowBackend}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = HeadlessExePath,
            Arguments = args,
            WorkingDirectory = TestHelper.ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(psi)!;
    }

    private static bool WaitForHealthy(int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities").Result;
                if (response.StatusCode == System.Net.HttpStatusCode.OK) return true;
            }
            catch { }
            Thread.Sleep(500);
        }
        return false;
    }

    [Fact]
    public void HeadlessHost_StartsAndLogsServiceStartedOnlyAfterSuccess()
    {
        using var tmp = new TempDirectory();
        // Ensure no lingering headless process holds the port.
        foreach (var p in Process.GetProcessesByName("AgentRecorder.Headless"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        var proc = StartHeadless(tmp.Path);
        try
        {
            Assert.True(WaitForHealthy(30), "Headless host should become healthy");

            var logPath = Path.Combine(tmp.Path, "logs", "audit.jsonl");
            Assert.True(File.Exists(logPath), "Audit log should exist after startup");

            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var events = lines
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString())
                .ToList();

            Assert.Contains("service.starting", events);
            Assert.Contains("service.started", events);
            Assert.DoesNotContain("service.start_failed", events);

            var startedIndex = events.IndexOf("service.started");
            var startingIndex = events.IndexOf("service.starting");
            Assert.True(startedIndex > startingIndex, "service.started must come after service.starting");
        }
        finally
        {
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
    }

    [Fact]
    public void HeadlessHost_DataDirArgument_UsesProvidedDirectory()
    {
        using var tmp = new TempDirectory();
        foreach (var p in Process.GetProcessesByName("AgentRecorder.Headless"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        var proc = StartHeadless(tmp.Path);
        try
        {
            Assert.True(WaitForHealthy(30), "Headless host should become healthy");

            var logPath = Path.Combine(tmp.Path, "logs", "audit.jsonl");
            Assert.True(File.Exists(logPath), "Audit log should be created in --data-dir");

            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var starting = lines
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .First(e => e.GetProperty("event").GetString() == "service.starting");

            var dataDir = starting.GetProperty("data_dir").GetString();
            Assert.Equal(tmp.Path, dataDir);
        }
        finally
        {
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
    }

    [Fact]
    public void HeadlessHost_WindowBackendArgument_DefaultIsNotWgc()
    {
        using var tmp = new TempDirectory();
        foreach (var p in Process.GetProcessesByName("AgentRecorder.Headless"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        var proc = StartHeadless(tmp.Path);
        try
        {
            Assert.True(WaitForHealthy(30), "Headless host should become healthy");

            var logPath = Path.Combine(tmp.Path, "logs", "audit.jsonl");
            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var starting = lines
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .First(e => e.GetProperty("event").GetString() == "service.starting");

            var backend = starting.GetProperty("window_backend").GetString();
            Assert.Equal("ffmpeg_gdigrab", backend);
        }
        finally
        {
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
    }

    [Fact]
    public void HeadlessHost_WindowBackendArgument_Wgc_WhenExplicit()
    {
        using var tmp = new TempDirectory();
        foreach (var p in Process.GetProcessesByName("AgentRecorder.Headless"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        var proc = StartHeadless(tmp.Path, "wgc");
        try
        {
            Assert.True(WaitForHealthy(30), "Headless host should become healthy");

            var logPath = Path.Combine(tmp.Path, "logs", "audit.jsonl");
            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var starting = lines
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .First(e => e.GetProperty("event").GetString() == "service.starting");

            var backend = starting.GetProperty("window_backend").GetString();
            Assert.Equal("wgc", backend);
        }
        finally
        {
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }
    }
}
