using System;
using System.IO;
using System.Text.Json;
using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for CLI readiness identity validation logic.
/// Validates that /capabilities readiness fields are checked against ready.json.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class CliReadinessIdentityTests
{
    private static string MakeCapabilitiesJson(
        bool ok = true,
        bool ready = true,
        int pid = 12345,
        int port = 37891,
        string mode = "tray",
        string? readyFile = null,
        string? apiKeyFile = null,
        string apiVersion = "v1",
        bool includeReadiness = true,
        bool includeData = true,
        bool includePid = true,
        bool includePort = true,
        bool includeMode = true,
        bool includeReadyFile = true,
        bool includeApiKeyFile = true)
    {
        var readyFileStr = readyFile ?? @"C:\data\runtime\ready.json";
        var apiKeyFileStr = apiKeyFile ?? @"C:\data\config\api-key.txt";

        var readinessObj = new Dictionary<string, object?>
        {
            ["ready"] = ready,
            ["api_version"] = apiVersion,
            ["startup_elapsed_ms"] = 850,
            ["named_event"] = @"Local\AgentRecorderReady"
        };
        if (includePid) readinessObj["pid"] = pid;
        if (includePort) readinessObj["port"] = port;
        if (includeMode) readinessObj["mode"] = mode;
        if (includeReadyFile) readinessObj["ready_file"] = readyFileStr;
        if (includeApiKeyFile) readinessObj["api_key_file"] = apiKeyFileStr;

        var dataObj = new Dictionary<string, object?>
        {
            ["app"] = new { name = "Agent Recorder", version = "0.1.0", platform = "windows" }
        };
        if (includeReadiness)
            dataObj["readiness"] = readinessObj;

        var rootObj = new Dictionary<string, object?> { ["ok"] = ok };
        if (includeData)
            rootObj["data"] = dataObj;
        rootObj["request_id"] = "req_test";

        return JsonSerializer.Serialize(rootObj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });
    }

    private static ReadySnapshot MakeSnapshot(
        int pid = 12345,
        int port = 37891,
        string mode = "tray",
        string? readyFile = null,
        string? apiKeyFile = null)
    {
        return new ReadySnapshot
        {
            Ready = true,
            Pid = pid,
            Port = port,
            Mode = mode,
            ApiVersion = "v1",
            StartedAt = "2024-01-01T00:00:00Z",
            ReadyAt = "2024-01-01T00:00:01Z",
            StartupElapsedMs = 850,
            DataDir = @"C:\data",
            ApiKeyFile = apiKeyFile ?? @"C:\data\config\api-key.txt",
            AuditLogPath = @"C:\data\logs\audit.jsonl",
            ReadyFile = readyFile ?? @"C:\data\runtime\ready.json",
            NamedEvent = @"Local\AgentRecorderReady"
        };
    }

    [Fact]
    public void IdentityMatch_ReturnsValid()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(pid: snap.Pid, port: snap.Port, mode: snap.Mode,
            readyFile: snap.ReadyFile, apiKeyFile: snap.ApiKeyFile);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.True(result.Valid);
        Assert.Equal("v1", result.ApiVersion);
    }

    [Fact]
    public void PidMismatch_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(pid: 12345);
        var json = MakeCapabilitiesJson(pid: 99999); // Different PID

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        Assert.Contains("PID mismatch", result.Message);
    }

    [Fact]
    public void PortMismatch_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(port: 37891);
        var json = MakeCapabilitiesJson(port: 40000); // Different port

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        Assert.Contains("Port mismatch", result.Message);
    }

    [Fact]
    public void ModeMismatch_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(mode: "tray");
        var json = MakeCapabilitiesJson(mode: "headless"); // Different mode

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        Assert.Contains("Mode mismatch", result.Message);
    }

    [Fact]
    public void ReadyFileMismatch_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(readyFile: @"C:\real-data\runtime\ready.json");
        var json = MakeCapabilitiesJson(readyFile: @"C:\fake-data\runtime\ready.json"); // Different path

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        Assert.Contains("ready_file mismatch", result.Message);
    }

    [Fact]
    public void ApiKeyFileMismatch_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(apiKeyFile: @"C:\real-data\config\api-key.txt");
        var json = MakeCapabilitiesJson(apiKeyFile: @"C:\fake-data\config\api-key.txt"); // Different path

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        Assert.Contains("api_key_file mismatch", result.Message);
    }

    [Fact]
    public void MissingReadinessField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includeReadiness: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("readiness", result.Message ?? "");
    }

    [Fact]
    public void MissingDataField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includeData: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void OkFalse_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(ok: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void ReadyFalse_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(ready: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void ModeCaseInsensitive_Matches()
    {
        var snap = MakeSnapshot(mode: "tray");
        var json = MakeCapabilitiesJson(mode: "Tray"); // Different case

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.True(result.Valid);
    }

    [Fact]
    public void ReadyFilePathNormalized_Matches()
    {
        var snap = MakeSnapshot(readyFile: @"C:\data\runtime\ready.json");
        // Use different separator / trailing slash style
        var json = MakeCapabilitiesJson(readyFile: @"C:/data/runtime/ready.json");

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.True(result.Valid);
    }

    [Fact]
    public void EmptyApiKeyFile_ReturnsStaleReadyFile()
    {
        var snap = MakeSnapshot(apiKeyFile: ""); // Empty
        var json = MakeCapabilitiesJson(apiKeyFile: @"C:\data\config\api-key.txt");

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
    }

    [Fact]
    public void FakeReadyFile_WithRealPidButFakePaths_ReturnsStaleReadyFile()
    {
        // Simulate: real AgentRecorder.App is running on pid 12345
        // Attacker writes fake ready.json with pid=12345 but fake api_key_file
        var snap = MakeSnapshot(
            pid: 12345,
            port: 37891,
            mode: "tray",
            readyFile: @"C:\fake-data\runtime\ready.json",
            apiKeyFile: @"C:\fake-data\config\api-key.txt");

        // Real /capabilities returns the REAL paths (not the fake ones)
        var json = MakeCapabilitiesJson(
            pid: 12345,
            port: 37891,
            mode: "tray",
            readyFile: @"C:\real-data\runtime\ready.json",
            apiKeyFile: @"C:\real-data\config\api-key.txt");

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("STALE_READY_FILE", result.ErrorCode);
        // Should detect the path mismatch
        Assert.Contains("mismatch", result.Message ?? "");
    }

    [Fact]
    public void InvalidJson_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = "not valid json {{{";

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void MissingPidField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includePid: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("pid", result.Message ?? "");
    }

    [Fact]
    public void MissingPortField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includePort: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("port", result.Message ?? "");
    }

    [Fact]
    public void MissingModeField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includeMode: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("mode", result.Message ?? "");
    }

    [Fact]
    public void MissingReadyFileField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includeReadyFile: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("ready_file", result.Message ?? "");
    }

    [Fact]
    public void MissingApiKeyFileField_ReturnsCapabilitiesUnavailable()
    {
        var snap = MakeSnapshot();
        var json = MakeCapabilitiesJson(includeApiKeyFile: false);

        var result = Program.ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);

        Assert.False(result.Valid);
        Assert.Equal("CAPABILITIES_UNAVAILABLE", result.ErrorCode);
        Assert.Contains("api_key_file", result.Message ?? "");
    }
}
