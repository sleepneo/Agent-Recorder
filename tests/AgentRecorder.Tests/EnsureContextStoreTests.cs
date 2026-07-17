using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the ensure-running context store: creation, one-time consumption,
/// identity binding, TTL, count limits, and malformed input rejection.
/// Each test gets its own temporary data directory to avoid cross-test pollution.
/// </summary>
public class EnsureContextStoreTests
{
    private const string Hex32 = "0123456789abcdef0123456789abcdef";

    private static TestStore CreateTestStore(Func<DateTime>? utcNow = null, TimeSpan? ttl = null, int? maxFiles = null)
    {
        var tmp = new TempDirectory();
        var store = new EnsureContextStore(tmp.Path, utcNow, ttl, maxFiles);
        return new TestStore(tmp, store);
    }

    private static EnsureContext MakeContext(
        string contextId,
        string startupKind = "cold",
        long ensureElapsedMs = 100,
        long serviceStartupElapsedMs = 50,
        int pid = 12345,
        string startedAt = "2024-01-01T00:00:00Z",
        string readyAt = "2024-01-01T00:00:01Z",
        DateTime? createdAtUtc = null)
    {
        return new EnsureContext
        {
            SchemaVersion = 1,
            EnsureContextId = contextId,
            ServicePid = pid,
            ServiceStartedAt = startedAt,
            ServiceReadyAt = readyAt,
            StartupKind = startupKind,
            EnsureElapsedMs = ensureElapsedMs,
            ServiceStartupElapsedMs = serviceStartupElapsedMs,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    private static void WriteReadyFile(TestStore ts, int pid = 12345, string startedAt = "2024-01-01T00:00:00Z",
        string readyAt = "2024-01-01T00:00:01Z", long startupElapsedMs = 50)
    {
        var readyPath = Path.Combine(ts.DataDir, "runtime", "ready.json");
        Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
        var snap = new ReadySnapshot
        {
            Ready = true,
            Pid = pid,
            Port = 37891,
            ApiVersion = "v1",
            Mode = "tray",
            StartedAt = startedAt,
            ReadyAt = readyAt,
            StartupElapsedMs = startupElapsedMs,
            DataDir = ts.DataDir,
            ApiKeyFile = Path.Combine(ts.DataDir, "config", "api-key.txt"),
            AuditLogPath = Path.Combine(ts.DataDir, "logs", "audit.jsonl"),
            ReadyFile = readyPath,
            NamedEvent = @"Local\AgentRecorderReady"
        };
        File.WriteAllText(readyPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }));
    }

    private static string ContextPath(TestStore ts, string contextId)
        => Path.Combine(ts.DataDir, "runtime", "ensure-contexts", $"{contextId}.json");

    [Fact]
    public void GenerateContextId_HasStrictAllowlistedFormat()
    {
        var id = EnsureContextStore.GenerateContextId();

        Assert.StartsWith("ensure_", id);
        Assert.Equal(39, id.Length);
        Assert.True(EnsureContextStore.IsValidContextId(id));
        Assert.Matches("^ensure_[0-9a-f]{32}$", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ensure_123")]
    [InlineData("ensure_" + Hex32 + "_extra")]
    [InlineData("ensure_gggggggggggggggggggggggggggggggg")]
    [InlineData("ensure_0000000000000000000000000000000")]
    [InlineData("ensure_000000000000000000000000000000000")]
    [InlineData("cold_00000000000000000000000000000000")]
    [InlineData("ensure_../ready.json")]
    [InlineData("ensure_C:\\windows\\system32")]
    [InlineData("ensure_0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void IsValidContextId_RejectsInvalidValues(string? contextId)
    {
        Assert.False(EnsureContextStore.IsValidContextId(contextId));
    }

    [Fact]
    public void TryCreate_ColdContext_AndTryConsume_Succeeds()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var context = MakeContext(contextId, "cold", 842, 164);

        var created = ts.Store.TryCreate(context);
        Assert.Equal(contextId, created);
        Assert.True(File.Exists(ContextPath(ts, contextId)));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Consumed, result.Status);
        Assert.Equal(contextId, result.EnsureContextId);
        Assert.Equal("cold", result.StartupKind);
        Assert.Equal(842L, result.EnsureElapsedMs);
        Assert.Equal(164L, result.ServiceStartupElapsedMs);
        Assert.False(File.Exists(ContextPath(ts, contextId)));
    }

    [Fact]
    public void TryCreate_WarmContext_AndTryConsume_Succeeds()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var context = MakeContext(contextId, "warm", 120, 50);

        ts.Store.TryCreate(context);
        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Consumed, result.Status);
        Assert.Equal("warm", result.StartupKind);
    }

    [Fact]
    public void TryConsume_SameIdTwice_SecondReturnsReused()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId));

        var first = ts.Store.TryConsume(contextId);
        var second = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Consumed, first.Status);
        Assert.Equal(EnsureContextStatus.Reused, second.Status);
    }

    [Fact]
    public void TryConsume_ConcurrentSameId_OnlyOneSucceeds()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId));

        var results = new System.Collections.Concurrent.ConcurrentBag<EnsureContextResult>();
        Parallel.For(0, 20, _ => results.Add(ts.Store.TryConsume(contextId)));

        Assert.Single(results, r => r.Status == EnsureContextStatus.Consumed);
        Assert.Equal(19, results.Count(r => r.Status == EnsureContextStatus.Reused));
    }

    [Theory]
    [InlineData("ensure_../ready.json")]
    [InlineData("ensure_00000000000000000000000000000000/x")]
    [InlineData("ensure_C:\\windows")]
    public void TryConsume_InvalidIdFormat_DoesNotTouchFilesystem(string contextId)
    {
        using var ts = CreateTestStore();
        var dir = Path.Combine(ts.DataDir, "runtime", "ensure-contexts");
        Directory.CreateDirectory(dir);

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(Directory.EnumerateFiles(dir).Any());
    }

    [Fact]
    public void TryConsume_MalformedJson_ReturnsInvalidAndDeletesFile()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var path = ContextPath(ts, contextId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not json {{");

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryConsume_UnknownSchema_ReturnsInvalidAndDeletesFile()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var path = ContextPath(ts, contextId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema_version = 99, ensure_context_id = contextId }));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryConsume_OversizedFile_ReturnsInvalidAndDeletesFile()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var path = ContextPath(ts, contextId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var huge = new string('x', EnsureContextStore.MaxFileBytes + 1);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema_version = 1, ensure_context_id = contextId, huge }));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("hot")]
    [InlineData("")]
    public void TryConsume_InvalidStartupKind_ReturnsInvalidAndDeletesFile(string kind)
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var path = ContextPath(ts, contextId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            ensure_context_id = contextId,
            service_pid = 12345,
            service_started_at = "2024-01-01T00:00:00Z",
            service_ready_at = "2024-01-01T00:00:01Z",
            startup_kind = kind,
            ensure_elapsed_ms = 100,
            service_startup_elapsed_ms = 50,
            created_at_utc = DateTime.UtcNow
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        File.WriteAllText(path, json);

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryConsume_MissingStartupKind_ReturnsInvalidAndDeletesFile()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var path = ContextPath(ts, contextId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            ensure_context_id = contextId,
            service_pid = 12345,
            service_started_at = "2024-01-01T00:00:00Z",
            service_ready_at = "2024-01-01T00:00:01Z",
            ensure_elapsed_ms = 100,
            service_startup_elapsed_ms = 50,
            created_at_utc = DateTime.UtcNow
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        File.WriteAllText(path, json);

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(EnsureContextStore.MaxElapsedMs + 1, 100)]
    [InlineData(100, EnsureContextStore.MaxElapsedMs + 1)]
    public void TryConsume_ExtremeElapsedValues_ReturnsInvalidAndDeletesFile(long ensureMs, long serviceMs)
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, ensureElapsedMs: ensureMs, serviceStartupElapsedMs: serviceMs));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Invalid, result.Status);
    }

    [Fact]
    public void TryConsume_MismatchedReadyAt_ReturnsInstanceMismatch()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts, readyAt: "2024-01-01T00:00:02Z");
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, readyAt: "2024-01-01T00:00:01Z"));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.InstanceMismatch, result.Status);
    }

    [Fact]
    public void TryConsume_MismatchedPid_ReturnsInstanceMismatch()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts, pid: 99999);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, pid: 12345));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.InstanceMismatch, result.Status);
    }

    [Fact]
    public void TryConsume_MissingReadyFile_ReturnsInstanceMismatch()
    {
        using var ts = CreateTestStore();
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.InstanceMismatch, result.Status);
    }

    [Fact]
    public void TryConsume_ExpiredContext_ReturnsExpired()
    {
        var now = new DateTime(2024, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5));
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, createdAtUtc: now - TimeSpan.FromMinutes(6)));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Expired, result.Status);
    }

    [Fact]
    public void TryConsume_BoundaryJustWithinTtl_ReturnsConsumed()
    {
        var now = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5));
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, createdAtUtc: now - TimeSpan.FromMinutes(5)));

        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Consumed, result.Status);
    }

    [Fact]
    public void TryCreate_CleanupExpiredFiles_RemovesOldFiles()
    {
        var now = new DateTime(2024, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5));
        WriteReadyFile(ts);
        var expiredId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(expiredId, createdAtUtc: now - TimeSpan.FromMinutes(10)));
        var path = ContextPath(ts, expiredId);
        Assert.True(File.Exists(path));

        var freshId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(freshId, createdAtUtc: now));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(ContextPath(ts, freshId)));
    }

    [Fact]
    public void TryCreate_EnforcesCountLimit()
    {
        var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5), 3);
        WriteReadyFile(ts);

        var id1 = EnsureContextStore.GenerateContextId();
        var id2 = EnsureContextStore.GenerateContextId();
        var id3 = EnsureContextStore.GenerateContextId();
        var id4 = EnsureContextStore.GenerateContextId();

        ts.Store.TryCreate(MakeContext(id1, createdAtUtc: now));
        ts.Store.TryCreate(MakeContext(id2, createdAtUtc: now.AddTicks(1)));
        ts.Store.TryCreate(MakeContext(id3, createdAtUtc: now.AddTicks(2)));
        ts.Store.TryCreate(MakeContext(id4, createdAtUtc: now.AddTicks(3)));

        var files = Directory.GetFiles(Path.Combine(ts.DataDir, "runtime", "ensure-contexts"), "*.json");
        Assert.Equal(3, files.Length);
        Assert.DoesNotContain(id1, files.Select(Path.GetFileNameWithoutExtension));
        Assert.Contains(id2, files.Select(Path.GetFileNameWithoutExtension));
        Assert.Contains(id3, files.Select(Path.GetFileNameWithoutExtension));
        Assert.Contains(id4, files.Select(Path.GetFileNameWithoutExtension));
    }

    [Fact]
    public void TryCreate_InvalidContextId_ReturnsNullWithoutThrowing()
    {
        using var ts = CreateTestStore();
        var context = MakeContext("invalid-id");

        var result = ts.Store.TryCreate(context);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreate_AtomicWrite_DoesNotLeavePartialFile()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var context = MakeContext(contextId);

        ts.Store.TryCreate(context);

        var dir = Path.Combine(ts.DataDir, "runtime", "ensure-contexts");
        var tempFiles = Directory.GetFiles(dir, ".tmp-*");
        Assert.Empty(tempFiles);
        Assert.True(File.Exists(Path.Combine(dir, $"{contextId}.json")));
    }

    [Fact]
    public void TryCreate_MoveFails_CleansTempAndReturnsNull()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        var context = MakeContext(contextId);
        var dir = Path.Combine(ts.DataDir, "runtime", "ensure-contexts");

        var moveInvoked = false;
        ts.Store.TestMoveFile = (temp, dest) =>
        {
            moveInvoked = true;
            Assert.True(File.Exists(temp), "Temp file must exist before move");
            Assert.EndsWith(EnsureContextStore.TempFileSuffix, temp, StringComparison.Ordinal);
            Assert.False(File.Exists(dest), "Destination must not exist before move");
            throw new IOException("Simulated move failure");
        };

        var result = ts.Store.TryCreate(context);

        Assert.Null(result);
        Assert.True(moveInvoked, "TestMoveFile must be invoked");
        Assert.False(File.Exists(Path.Combine(dir, $"{contextId}.json")), "Context file must not exist after failed move");
        Assert.Empty(Directory.GetFiles(dir, $"*{EnsureContextStore.TempFileSuffix}"));
    }

    [Fact]
    public void TryCreate_CleansExpiredTempFiles()
    {
        var now = new DateTime(2024, 1, 1, 0, 10, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5));
        WriteReadyFile(ts);
        var dir = Path.Combine(ts.DataDir, "runtime", "ensure-contexts");
        Directory.CreateDirectory(dir);

        var staleTemp = Path.Combine(dir, $".tmp-{EnsureContextStore.GenerateContextId()}-{Guid.NewGuid():N}.tmp");
        var freshTemp = Path.Combine(dir, $".tmp-{EnsureContextStore.GenerateContextId()}-{Guid.NewGuid():N}.tmp");
        var otherFile = Path.Combine(dir, "unrelated.txt");
        File.WriteAllText(staleTemp, "stale");
        File.WriteAllText(freshTemp, "fresh");
        File.WriteAllText(otherFile, "keep");
        File.SetCreationTimeUtc(staleTemp, now - TimeSpan.FromMinutes(10));
        File.SetCreationTimeUtc(freshTemp, now);

        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, createdAtUtc: now));

        Assert.False(File.Exists(staleTemp), "stale temp should be cleaned");
        Assert.True(File.Exists(freshTemp), "fresh temp should be kept");
        Assert.True(File.Exists(otherFile), "unrelated file should not be touched");
        Assert.True(File.Exists(ContextPath(ts, contextId)));
    }

    [Fact]
    public void TryConsume_VerifiedDeleteFailure_ReturnsUnavailable_NotConsumed()
    {
        using var ts = CreateTestStore();
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId));
        var path = ContextPath(ts, contextId);
        Assert.True(File.Exists(path));

        // Hold the file open without FileShare.Delete to force deletion to fail on Windows.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = ts.Store.TryConsume(contextId);

        Assert.Equal(EnsureContextStatus.Unavailable, result.Status);
        Assert.True(File.Exists(path), "file must still exist after failed deletion");

        // After releasing the handle, a retry can successfully consume.
        fs.Dispose();
        var retry = ts.Store.TryConsume(contextId);
        Assert.Equal(EnsureContextStatus.Consumed, retry.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryConsume_TombstoneExpires_AndReturnsMissing()
    {
        var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5));
        WriteReadyFile(ts);
        var contextId = EnsureContextStore.GenerateContextId();
        ts.Store.TryCreate(MakeContext(contextId, createdAtUtc: now));

        ts.Store.TryConsume(contextId);
        Assert.Equal(EnsureContextStatus.Reused, ts.Store.TryConsume(contextId).Status);

        now += TimeSpan.FromMinutes(6);
        Assert.Equal(EnsureContextStatus.Missing, ts.Store.TryConsume(contextId).Status);
    }

    [Fact]
    public void TryConsume_TombstoneCapacity_EnforcedDeterministically()
    {
        var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var ts = CreateTestStore(() => now, TimeSpan.FromMinutes(5), 3);
        WriteReadyFile(ts);

        // Use deterministic IDs so that tombstone eviction order (by expiration,
        // then by key) is predictable under a fixed clock. Create and consume
        // each ID immediately so the file count limit never removes files before
        // we can test tombstone eviction.
        var ids = new[]
        {
            "ensure_00000000000000000000000000000000",
            "ensure_00000000000000000000000000000001",
            "ensure_00000000000000000000000000000002",
            "ensure_00000000000000000000000000000003",
            "ensure_00000000000000000000000000000004"
        };
        foreach (var id in ids)
        {
            ts.Store.TryCreate(MakeContext(id, createdAtUtc: now));
            Assert.Equal(EnsureContextStatus.Consumed, ts.Store.TryConsume(id).Status);
        }

        // All 5 are within tombstone TTL, but capacity is 3. The oldest two tombstones
        // should have been evicted, so those IDs return Missing instead of Reused.
        Assert.Equal(EnsureContextStatus.Missing, ts.Store.TryConsume(ids[0]).Status);
        Assert.Equal(EnsureContextStatus.Missing, ts.Store.TryConsume(ids[1]).Status);
        Assert.Equal(EnsureContextStatus.Reused, ts.Store.TryConsume(ids[2]).Status);
        Assert.Equal(EnsureContextStatus.Reused, ts.Store.TryConsume(ids[3]).Status);
        Assert.Equal(EnsureContextStatus.Reused, ts.Store.TryConsume(ids[4]).Status);
    }

    private sealed class TestStore : IDisposable
    {
        public string DataDir { get; }
        public EnsureContextStore Store { get; }
        private readonly TempDirectory _tmp;

        public TestStore(TempDirectory tmp, EnsureContextStore store)
        {
            _tmp = tmp;
            Store = store;
            DataDir = tmp.Path;
        }

        public void Dispose() => _tmp.Dispose();
    }
}
