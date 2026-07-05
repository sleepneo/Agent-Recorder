using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using AgentRecorder.Cli;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for AgentRecorder.Cli ensure-running command.
/// Tests argument parsing, JSON output format, validation logic, and error handling.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class CliEnsureRunningTests : IDisposable
{
    private readonly string _testDir;

    public CliEnsureRunningTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void EnsureRunningResult_Json_Success_ContainsOkTrueAndStatusReady()
    {
        var result = new EnsureRunningResult
        {
            Ok = true,
            Status = "ready",
            Started = false,
            Source = "existing",
            Mode = "tray",
            Pid = 12345,
            Port = 37891,
            ApiVersion = "v1",
            StartedAt = "2024-01-01T00:00:00Z",
            ReadyAt = "2024-01-01T00:00:01Z",
            StartupElapsedMs = 1000,
            ReadyFile = @"C:\data\runtime\ready.json",
            ApiKeyFile = @"C:\data\config\api-key.txt",
            DataDir = @"C:\data",
            AuditLogPath = @"C:\data\logs\audit.jsonl",
            NamedEvent = @"Local\AgentRecorderReady"
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });

        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"status\":\"ready\"", json);
        Assert.Contains("\"started\":false", json);
        Assert.Contains("\"source\":\"existing\"", json);
        Assert.Contains("\"mode\":\"tray\"", json);
        Assert.Contains("\"pid\":12345", json);
        Assert.Contains("\"port\":37891", json);
        Assert.Contains("\"api_version\":\"v1\"", json);
        Assert.Contains("\"ready_file\":\"", json);
        Assert.Contains("\"api_key_file\":\"", json);
        Assert.Contains("\"data_dir\":\"", json);
    }

    [Fact]
    public void EnsureRunningResult_Json_Error_ContainsOkFalseCodeMessageSuggestedAction()
    {
        var result = new EnsureRunningResult
        {
            Ok = false,
            Status = "error",
            Code = "READY_TIMEOUT",
            Message = "Agent Recorder did not become ready within 30 seconds.",
            SuggestedAction = "Check whether AgentRecorder.App.exe can start in the current desktop session."
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"code\":\"READY_TIMEOUT\"", json);
        Assert.Contains("\"message\":\"Agent Recorder did not become ready within 30 seconds.\"", json);
        Assert.Contains("\"suggested_action\":\"Check whether AgentRecorder.App.exe can start in the current desktop session.\"", json);
    }

    [Fact]
    public void EnsureRunningResult_ErrorStatus_ApiKeyFileIsEmpty()
    {
        var result = new EnsureRunningResult
        {
            Ok = false,
            Status = "error",
            Code = "READY_TIMEOUT",
            Message = "Timed out",
            SuggestedAction = "Try again"
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });

        // api_key_file should be present but empty for error results
        Assert.Contains("\"api_key_file\":\"\"", json);
        // Should NOT contain any API key content
        Assert.DoesNotContain("sk-", json);
    }

    [Fact]
    public void EnsureRunningResult_ReadyStatus_ContainsAllExpectedFields()
    {
        var result = new EnsureRunningResult
        {
            Ok = true,
            Status = "ready",
            Started = true,
            Source = "started",
            Mode = "tray",
            Pid = 56789,
            Port = 37891,
            ApiVersion = "v1",
            ReadyFile = Path.Combine(_testDir, "runtime", "ready.json"),
            ApiKeyFile = Path.Combine(_testDir, "config", "api-key.txt"),
            DataDir = _testDir,
            NamedEvent = @"Local\AgentRecorderReady"
        };

        Assert.True(result.Ok);
        Assert.Equal("ready", result.Status);
        Assert.True(result.Started);
        Assert.Equal("started", result.Source);
        Assert.Equal("tray", result.Mode);
        Assert.Equal(56789, result.Pid);
        Assert.Equal(37891, result.Port);
        Assert.Equal("v1", result.ApiVersion);
        Assert.EndsWith("ready.json", result.ReadyFile);
        Assert.EndsWith("api-key.txt", result.ApiKeyFile);
        Assert.Equal(_testDir, result.DataDir);
        // api_key_file should be a FILE PATH, not the actual key content
        Assert.DoesNotContain("sk-", result.ApiKeyFile);
        Assert.DoesNotContain("secret", result.ApiKeyFile.ToLower());
    }

    [Fact]
    public void FakeReadyFile_WithNonAgentRecorderPid_ReturnsError()
    {
        // Create a fake ready.json with the current test process PID (not AgentRecorder)
        var runtimeDir = Path.Combine(_testDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var readyPath = Path.Combine(runtimeDir, "ready.json");

        var fakeReady = new
        {
            ready = true,
            pid = Environment.ProcessId, // Current test process - NOT AgentRecorder
            port = 37891,
            mode = "tray",
            api_version = "v1",
            started_at = DateTime.UtcNow.ToString("O"),
            ready_at = DateTime.UtcNow.ToString("O"),
            startup_elapsed_ms = 500,
            ready_file = readyPath,
            api_key_file = Path.Combine(_testDir, "config", "api-key.txt"),
            data_dir = _testDir,
            named_event = "Local\\AgentRecorderReady"
        };

        File.WriteAllText(readyPath, JsonSerializer.Serialize(fakeReady));

        var opts = new CliOptions
        {
            DataDir = _testDir,
            TimeoutMs = 1000,
            PackageRoot = _testDir,
            AppPath = Path.Combine(_testDir, "nonexistent.exe")
        };

        var result = Program.EnsureRunningCore(opts);

        // Should NOT return ok:true just because PID is alive
        Assert.False(result.Ok);
        // Should eventually fail with SERVICE_NOT_FOUND (can't find EXE to start)
        // or some error code - but definitely not ready
        Assert.NotEqual("ready", result.Status);
    }

    [Fact]
    public void FakeReadyFile_WithDeadPid_ReturnsError()
    {
        var runtimeDir = Path.Combine(_testDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var readyPath = Path.Combine(runtimeDir, "ready.json");

        var fakeReady = new
        {
            ready = true,
            pid = 99999, // Likely dead PID
            port = 37891,
            mode = "tray",
            api_version = "v1",
            started_at = DateTime.UtcNow.ToString("O"),
            ready_at = DateTime.UtcNow.ToString("O"),
            startup_elapsed_ms = 500,
            ready_file = readyPath,
            api_key_file = Path.Combine(_testDir, "config", "api-key.txt"),
            data_dir = _testDir,
            named_event = "Local\\AgentRecorderReady"
        };

        File.WriteAllText(readyPath, JsonSerializer.Serialize(fakeReady));

        var opts = new CliOptions
        {
            DataDir = _testDir,
            TimeoutMs = 1000,
            PackageRoot = _testDir,
            AppPath = Path.Combine(_testDir, "nonexistent.exe")
        };

        var result = Program.EnsureRunningCore(opts);

        Assert.False(result.Ok);
        Assert.NotEqual("ready", result.Status);
    }

    [Fact]
    public void ErrorCodes_AreStableStrings()
    {
        // Verify all expected error codes can be represented
        var codes = new[]
        {
            "READY_TIMEOUT",
            "SERVICE_NOT_FOUND",
            "SERVICE_EXITED",
            "STALE_READY_FILE",
            "CAPABILITIES_UNAVAILABLE",
            "INVALID_ARGUMENT",
            "INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY"
        };

        foreach (var code in codes)
        {
            var result = new EnsureRunningResult
            {
                Ok = false,
                Status = "error",
                Code = code,
                Message = "Test error",
                SuggestedAction = "Try again"
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });

            Assert.Contains($"\"code\":\"{code}\"", json);
        }
    }

    [Fact]
    public void DefaultResolve_ShouldPreferAppOverHeadless()
    {
        // We test the default preference by checking that --tray path is default
        // and --headless is only used when explicitly requested.
        // Since we can't easily test the EXE resolution without actual files,
        // we verify the options model has the right semantics.

        var defaultOpts = new CliOptions();
        Assert.False(defaultOpts.PreferHeadless, "Headless should not be preferred by default");
        Assert.False(defaultOpts.PreferTray, "Tray should not be a forced flag by default (it's the implicit default)");

        var headlessOpts = new CliOptions { PreferHeadless = true };
        Assert.True(headlessOpts.PreferHeadless);
    }

    [Fact]
    public void ParseOpts_InvalidTimeoutSeconds_SetsParseError()
    {
        var opts = Program.ParseOptsForTest(new[] { "--json", "--timeout-seconds", "abc" }, 0);
        Assert.NotNull(opts.ParseError);
        Assert.True(opts.Json); // --json should still be parsed
    }

    [Fact]
    public void ParseOpts_InvalidTimeoutMs_SetsParseError()
    {
        var opts = Program.ParseOptsForTest(new[] { "--json", "--timeout-ms", "xyz" }, 0);
        Assert.NotNull(opts.ParseError);
        Assert.True(opts.Json);
    }

    [Fact]
    public void ParseOpts_MissingDataDirValue_SetsParseError()
    {
        var opts = Program.ParseOptsForTest(new[] { "--json", "--data-dir" }, 0);
        Assert.NotNull(opts.ParseError);
        Assert.True(opts.Json);
    }

    [Fact]
    public void ParseOpts_UnknownOption_SetsParseError()
    {
        var opts = Program.ParseOptsForTest(new[] { "--json", "--unknown" }, 0);
        Assert.NotNull(opts.ParseError);
        Assert.True(opts.Json);
    }

    [Fact]
    public void ParseOpts_ValidArgs_NoParseError()
    {
        var opts = Program.ParseOptsForTest(new[] { "--json", "--timeout-seconds", "30", "--data-dir", @"C:\tmp" }, 0);
        Assert.Null(opts.ParseError);
        Assert.True(opts.Json);
        Assert.Equal(30, opts.TimeoutSeconds);
        Assert.Equal(@"C:\tmp", opts.DataDir);
    }
}
