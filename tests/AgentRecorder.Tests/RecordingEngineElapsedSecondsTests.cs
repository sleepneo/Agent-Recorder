using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that <c>elapsed_seconds</c> is a stable wall-clock duration:
/// - 0 before capture has started.
/// - computed from UtcNow while recording/stopping.
/// - computed from CompletedAtUtc for terminal recordings.
///
/// All tests use an isolated temp directory for audit logs so they do not
/// read, write, or delete content under the real user data directory.
/// </summary>
public class RecordingEngineElapsedSecondsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _auditPath;

    public RecordingEngineElapsedSecondsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"elapsed-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _auditPath = Path.Combine(_tempDir, "logs", "audit.jsonl");
    }

    public void Dispose()
    {
        // AuditLogger has no disposable background resources; we just need to
        // ensure the temp directory (and any audit/perf files inside it) is
        // removed after the test finishes, including on assertion failures.
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private RecordingEngine CreateEngine()
    {
        var audit = new AuditLogger(_auditPath);
        var engine = new RecordingEngine(audit);
        engine.SetTray(new NoOpTray());
        return engine;
    }

    private static int GetElapsedSeconds(object status)
    {
        var json = JsonSerializer.Serialize(status);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("elapsed_seconds", out var p) && p.TryGetInt32(out var v)
            ? v : -1;
    }

    private static int GetWaitElapsedSeconds(object status)
    {
        var json = JsonSerializer.Serialize(status);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ElapsedSeconds", out var p) && p.TryGetInt32(out var v)
            ? v : -1;
    }

    private void AssertAuditInTempDir()
    {
        Assert.True(File.Exists(_auditPath), "Audit log should exist in test temp directory");
        Assert.DoesNotContain(_tempDir, Paths.AuditLogPath);
    }

    [Fact]
    public void Completed_GivenStartAndEndTimes_ReturnsFloorElapsed()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-15.7),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.True(elapsed >= 15, $"expected at least 15s, got {elapsed}");
        Assert.True(elapsed <= 16, $"expected at most 16s, got {elapsed}");
    }

    [Fact]
    public void Completed_GetStatusWait_ReturnsSameElapsedAsGetStatus()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-42.3),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var direct = GetElapsedSeconds(engine.GetStatus(rec.Id));
        var waited = GetWaitElapsedSeconds(engine.GetStatusWait(rec.Id, "recording", 1));

        Assert.Equal(direct, waited);
        Assert.True(waited >= 42);
        Assert.True(waited <= 43);
    }

    [Fact]
    public void FailedAfterStart_WithCompletedAtUtc_ReturnsStableNonZeroElapsed()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.failed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-8.5),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var first = GetElapsedSeconds(engine.GetStatus(rec.Id));
        Thread.Sleep(50);
        var second = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(first, second);
        Assert.True(first >= 8);
        Assert.True(first <= 9);
    }

    [Fact]
    public void Recording_UsesCurrentTime()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.recording,
            StartedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var first = GetElapsedSeconds(engine.GetStatus(rec.Id));
        Thread.Sleep(1200);
        var second = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.True(first >= 0);
        Assert.True(second > first, $"elapsed should grow while recording: {first} -> {second}");
    }

    [Fact]
    public void Stopping_ReturnsActiveElapsedNotZero()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.stopping,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-3.2),
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.True(elapsed >= 3);
        Assert.True(elapsed <= 4);
    }

    [Theory]
    [InlineData(RecState.created)]
    [InlineData(RecState.pending_confirmation)]
    [InlineData(RecState.rejected)]
    [InlineData(RecState.expired)]
    public void BeforeStart_NoStartedAt_ReturnsZero(RecState state)
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = state,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(0, elapsed);
    }

    [Fact]
    public void Completed_EndEarlierThanStart_ReturnsZero()
    {
        var engine = CreateEngine();
        var now = DateTime.UtcNow;
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = now,
            CompletedAtUtc = now.AddSeconds(-5),
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(0, elapsed);
    }

    [Fact]
    public void Completed_RepeatedQuery_DoesNotGrow()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-25),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var first = GetElapsedSeconds(engine.GetStatus(rec.Id));
        Thread.Sleep(100);
        var second = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(first, second);
        Assert.True(first >= 25);
    }

    [Fact]
    public void ApiJsonContract_GetStatus_SerializesSnakeCaseElapsedSeconds()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-12.5),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var json = ApiResponse.Ok(engine.GetStatus(rec.Id), "req_test");

        Assert.Contains("\"elapsed_seconds\":", json);
        Assert.DoesNotContain("\"ElapsedSeconds\":", json);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("elapsed_seconds", out var p));
        Assert.True(p.TryGetInt32(out var elapsed));
        Assert.True(elapsed >= 12);
    }

    [Fact]
    public void ApiJsonContract_GetStatusWait_SerializesSnakeCaseElapsedSeconds()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-12.5),
            CompletedAtUtc = DateTime.UtcNow,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var json = ApiResponse.Ok(engine.GetStatusWait(rec.Id, "recording", 1), "req_test");

        Assert.Contains("\"elapsed_seconds\":", json);
        Assert.DoesNotContain("\"ElapsedSeconds\":", json);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("elapsed_seconds", out var p));
        Assert.True(p.TryGetInt32(out var elapsed));
        Assert.True(elapsed >= 12);
    }

    [Fact]
    public void ApiJsonContract_GetStatusAndWait_HaveIdenticalElapsedSeconds()
    {
        var engine = CreateEngine();
        var start = DateTime.UtcNow.AddSeconds(-18.4);
        var end = DateTime.UtcNow;
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = start,
            CompletedAtUtc = end,
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var directJson = ApiResponse.Ok(engine.GetStatus(rec.Id), "req_direct");
        var waitJson = ApiResponse.Ok(engine.GetStatusWait(rec.Id, "recording", 1), "req_wait");

        using var directDoc = JsonDocument.Parse(directJson);
        using var waitDoc = JsonDocument.Parse(waitJson);
        var directElapsed = directDoc.RootElement.GetProperty("data").GetProperty("elapsed_seconds").GetInt32();
        var waitElapsed = waitDoc.RootElement.GetProperty("data").GetProperty("elapsed_seconds").GetInt32();

        Assert.Equal(directElapsed, waitElapsed);
        Assert.True(directElapsed >= 18);
    }

    [Theory]
    [InlineData(RecState.completed)]
    [InlineData(RecState.failed)]
    [InlineData(RecState.cancelled)]
    public void TerminalWithoutCompletedAtUtc_DoesNotGrow_ReturnsZero(RecState state)
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = state,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-120),
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var first = GetElapsedSeconds(engine.GetStatus(rec.Id));
        Thread.Sleep(100);
        var second = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(0, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void NonActiveState_WithStartedAtUtc_ReturnsZero()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.pending_confirmation,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-60),
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(0, elapsed);
    }

    [Fact]
    public void ExtremeTimestamp_DoesNotOverflow_ReturnsZero()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            State = RecState.completed,
            StartedAtUtc = DateTime.MinValue.ToUniversalTime(),
            CompletedAtUtc = DateTime.MaxValue.ToUniversalTime(),
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4")
        };
        engine._recs[rec.Id] = rec;

        var elapsed = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(0, elapsed);
    }

    [Fact]
    public void BackendStartThrow_AfterStartedAt_SetsCompletedAtUtcAndStableElapsed()
    {
        var engine = CreateEngine();
        var rec = new Recording
        {
            SourceType = "region",
            OutputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.mp4"),
            Config = new CaptureConfig()
        };
        engine.BackendFactory = _ => (new ThrowingBackend("boom"), "fake");
        engine.StartCaptureForTests(rec, new NoOpTray());

        Assert.True(rec.StartedAtUtc != default);
        Assert.True(rec.CompletedAtUtc.HasValue);

        var first = GetElapsedSeconds(engine.GetStatus(rec.Id));
        Thread.Sleep(100);
        var second = GetElapsedSeconds(engine.GetStatus(rec.Id));

        Assert.Equal(first, second);
        Assert.True(first >= 0);

        // This is the path that previously wrote to the real user audit log.
        // Verify it is now isolated in the test temp directory.
        AssertAuditInTempDir();
    }

    private sealed class ThrowingBackend : ICaptureBackend
    {
        private readonly string _message;
        public ThrowingBackend(string message) => _message = message;
        public void Start(CaptureConfig cfg) => throw new Exception(_message);
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => -1;
        public void Dispose() { }
    }
}
