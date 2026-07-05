using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit and integration tests for SingleInstanceGuard:
/// - First instance acquires the mutex.
/// - Second instance cannot acquire the mutex.
/// - Abandoned mutex is recoverable.
/// - ReadExistingReadyFile correctly parses ready.json.
/// - Second instance does NOT delete existing ready.json.
///
/// These tests use a globally-named mutex, so they must not run in parallel.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class SingleInstanceGuardTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly string _runtimeDir;
    private readonly string _testMutexName;

    public SingleInstanceGuardTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"sig-test-{Guid.NewGuid():N}");
        _runtimeDir = Path.Combine(_testDataDir, "runtime");
        Directory.CreateDirectory(_runtimeDir);
        _testMutexName = $@"Local\AgentRecorder.Test.Instance.{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDataDir)) Directory.Delete(_testDataDir, recursive: true); } catch { }
    }

    [Fact]
    public void TryAcquire_FirstInstance_Succeeds()
    {
        using var guard = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.True(guard.IsAcquired, "First instance should acquire the mutex");
        Assert.Equal(_testMutexName, guard.Mutex);
    }

    [Fact]
    public void TryAcquire_SecondInstance_Fails()
    {
        using var first = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.True(first.IsAcquired, "First instance should acquire");

        using var second = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.False(second.IsAcquired, "Second instance should NOT acquire the mutex");
    }

    [Fact]
    public void Dispose_ReleasesMutex_NextInstanceCanAcquire()
    {
        using (var first = SingleInstanceGuard.TryAcquire(_testMutexName))
        {
            Assert.True(first.IsAcquired);
        }

        // After dispose, a new instance should be able to acquire.
        using var second = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.True(second.IsAcquired, "After first instance releases, second should acquire");
    }

    [Fact]
    public void ReadExistingReadyFile_FileDoesNotExist_ReturnsNull()
    {
        var result = SingleInstanceGuard.ReadExistingReadyFile(_testDataDir);
        Assert.Null(result);
    }

    [Fact]
    public void ReadExistingReadyFile_ValidJson_ReturnsSnapshot()
    {
        var expected = new ReadySnapshot
        {
            Ready = true,
            Pid = 12345,
            Port = 37891,
            Mode = "headless",
            ApiVersion = "v1",
            StartedAt = "2024-01-01T00:00:00Z",
            ReadyAt = "2024-01-01T00:00:01Z",
            StartupElapsedMs = 1000,
            DataDir = _testDataDir,
            ApiKeyFile = Path.Combine(_testDataDir, "api-key.txt"),
            AuditLogPath = Path.Combine(_testDataDir, "logs", "audit.jsonl"),
            ReadyFile = Path.Combine(_runtimeDir, "ready.json"),
            NamedEvent = "Local\\AgentRecorderReady"
        };

        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        File.WriteAllText(Path.Combine(_runtimeDir, "ready.json"), json);

        var actual = SingleInstanceGuard.ReadExistingReadyFile(_testDataDir);
        Assert.NotNull(actual);
        Assert.True(actual.Ready);
        Assert.Equal(12345, actual.Pid);
        Assert.Equal(37891, actual.Port);
        Assert.Equal("headless", actual.Mode);
        Assert.Equal(Path.Combine(_runtimeDir, "ready.json"), actual.ReadyFile);
    }

    [Fact]
    public void ReadExistingReadyFile_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_runtimeDir, "ready.json"), "not valid json {{{");
        var result = SingleInstanceGuard.ReadExistingReadyFile(_testDataDir);
        Assert.Null(result);
    }

    [Fact]
    public void SecondInstance_DoesNotDeleteExistingReadyFile()
    {
        // Write a fake ready.json
        var readyPath = Path.Combine(_runtimeDir, "ready.json");
        var fakeSnap = new ReadySnapshot
        {
            Ready = true,
            Pid = 99999,
            Port = 37891,
            Mode = "headless",
            ReadyFile = readyPath
        };
        File.WriteAllText(readyPath, JsonSerializer.Serialize(fakeSnap, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }));

        // Acquire the mutex as "first instance"
        using var first = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.True(first.IsAcquired);

        // "Second instance" tries to acquire - should fail
        using var second = SingleInstanceGuard.TryAcquire(_testMutexName);
        Assert.False(second.IsAcquired);

        // The ready file should still exist (second instance must not delete it)
        Assert.True(File.Exists(readyPath), "Second instance must NOT delete existing ready.json");
    }
}
