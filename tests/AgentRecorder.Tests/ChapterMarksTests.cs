using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class ChapterMarksTests : IDisposable
{
    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FirstFrameBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool EmitOnStart { get; set; }

        public void Start(CaptureConfig cfg)
        {
            cfg.CommandArgs = "chapter-marks-test";
            if (EmitOnStart)
                Emit();
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }

        public void Emit() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "test",
            FrameNumber = 1,
            TotalSizeBytes = 1,
            OutTimeUs = 0
        });
    }

    private readonly string _dataDir;
    private readonly CaptureAuditLogger _audit;
    private readonly RecordingEngine _engine;
    private readonly ApiServer _server;

    public ChapterMarksTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"chapter-marks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        _audit = new CaptureAuditLogger();
        _engine = new RecordingEngine(_audit);
        _engine.MonotonicFrequencyForTests = 1000;
        _engine.MonotonicTimestampProviderForTests = () => 0;
        _server = new ApiServer(_engine, _audit, new NoOpTray());
        _server.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        ApiKeyAuth.ResetForTesting(null);
    }

    private Recording Register(RecState state = RecState.recording, DateTime? startedAtUtc = null)
    {
        var rec = new Recording
        {
            State = state,
            StartedAtUtc = startedAtUtc ?? DateTime.UtcNow.AddSeconds(-1),
            BackendStartAtUtc = DateTime.UtcNow.AddMinutes(-1),
            OutputPath = Path.Combine(_dataDir, "recording.mp4"),
            MarkTimelineAnchorTicks = 0
        };
        _engine._recs[rec.Id] = rec;
        return rec;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private async Task<(HttpStatusCode Status, string Body)> PostMarkAsync(
        string recordingId, string body, bool authenticated = true)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        if (authenticated)
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/{recordingId}/marks",
            JsonContent(body));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthenticatedPost_AcceptsTrimmedUnicodeMark_AndReturnsTimestamp()
    {
        var firstFrame = new DateTime(2026, 8, 20, 1, 2, 3, DateTimeKind.Utc);
        var rec = Register(startedAtUtc: firstFrame);
        _engine.MonotonicTimestampProviderForTests = () => 1234;

        var result = await PostMarkAsync(rec.Id, "{\"label\":\"  重要决定 😀  \"}");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        using var doc = JsonDocument.Parse(result.Body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(rec.Id, data.GetProperty("recording_id").GetString());
        var mark = data.GetProperty("mark");
        Assert.Equal(1234, mark.GetProperty("t_ms").GetInt64());
        Assert.Equal("重要决定 😀", mark.GetProperty("label").GetString());
        Assert.Equal("agent", mark.GetProperty("source").GetString());

        var accepted = Assert.Single(rec.SnapshotMarks());
        Assert.Equal(1234, accepted.TMs);
        Assert.Equal("重要决定 😀", accepted.Label);
        Assert.Single(_audit.Events, e => e.evt == "recording.mark_added");
        Assert.DoesNotContain("重要决定", _audit.Events.Single(e => e.evt == "recording.mark_added").json);
    }

    [Fact]
    public async Task MutatingMarkEndpoint_RequiresAuthentication()
    {
        var rec = Register();
        var result = await PostMarkAsync(rec.Id, "{\"label\":\"secret\"}", authenticated: false);

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }

    [Fact]
    public async Task InvalidJsonLabelsAndSources_Return400WithoutMutationOrAudit()
    {
        var rec = Register();
        var invalidBodies = new[]
        {
            "{",
            "{}",
            "{\"label\":\"   \"}",
            "{\"label\":123}",
            "{\"label\":\"\\nchapter\"}",
            "{\"label\":\"chapter\\r\"}",
            "{\"label\":\"\\tchapter\"}",
            "{\"label\":\"chapter\\t\"}",
            "{\"label\":\"a\\u0001b\"}",
            "{\"label\":\"ok\",\"source\":\"hotkey\"}",
            "{\"label\":\"ok\",\"source\":\"unknown\"}"
        };

        foreach (var body in invalidBodies)
        {
            var result = await PostMarkAsync(rec.Id, body);
            Assert.Equal(HttpStatusCode.BadRequest, result.Status);
            Assert.Contains("INVALID_ARGUMENT", result.Body);
        }

        var oversized = JsonSerializer.Serialize(new { label = new string('x', 201) });
        var oversizedResult = await PostMarkAsync(rec.Id, oversized);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedResult.Status);

        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }

    [Fact]
    public async Task UnknownRecording_ReturnsExistingNotFoundError()
    {
        var result = await PostMarkAsync("rec_does_not_exist", "{\"label\":\"x\"}");

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Contains("RECORDING_NOT_FOUND", result.Body);
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }

    [Theory]
    [InlineData(RecState.created)]
    [InlineData(RecState.pending_confirmation)]
    [InlineData(RecState.preparing)]
    [InlineData(RecState.countdown)]
    [InlineData(RecState.paused)]
    [InlineData(RecState.stopping)]
    [InlineData(RecState.finalizing)]
    [InlineData(RecState.completed)]
    [InlineData(RecState.failed)]
    [InlineData(RecState.cancelled)]
    [InlineData(RecState.rejected)]
    [InlineData(RecState.expired)]
    public void DomainOperation_RejectsEveryNonRecordingStateWithoutMutation(RecState state)
    {
        var rec = Register(state);

        var exception = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "x"));

        Assert.Equal(409, exception.Status);
        Assert.Equal("RECORDING_NOT_ACTIVE", exception.Code);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }

    [Fact]
    public void DomainOperation_DoesNotFabricateTimestampBeforeFirstFrame()
    {
        var rec = Register(RecState.recording);
        rec.StartedAtUtc = default;

        var exception = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "x"));

        Assert.Equal(409, exception.Status);
        Assert.Equal("RECORDING_NOT_ACTIVE", exception.Code);
        Assert.Empty(rec.SnapshotMarks());
    }

    [Fact]
    public void DomainOperation_UsesMonotonicAnchor_RegardlessOfUtcClockChanges()
    {
        var firstFrame = new DateTime(2026, 8, 20, 2, 0, 0, DateTimeKind.Utc);
        var rec = Register(startedAtUtc: firstFrame);
        rec.BackendStartAtUtc = firstFrame.AddMinutes(-5);
        _engine.MonotonicTimestampProviderForTests = () => 2000;

        var mark = _engine.AddMark(rec.Id, "timeline");
        Assert.Equal(2000, mark.TMs);

        _engine.UtcNowForTests = () => firstFrame.AddHours(-12);
        _engine.MonotonicTimestampProviderForTests = () => 2500;
        var later = _engine.AddMark(rec.Id, "clock-step");
        Assert.Equal(2500, later.TMs);
        Assert.All(rec.SnapshotMarks(), m => Assert.True(m.TMs >= 0));
    }

    [Fact]
    public void RecordingMark_ValidatesSubmittedControlsBeforeTrimming_AndPreservesScalarLimit()
    {
        foreach (var invalid in new[] { "\nchapter", "chapter\r", "\tchapter", "chapter\t", "chap\0ter" })
            Assert.Throws<ArgumentException>(() => new RecordingMark(0, invalid, "agent"));

        Assert.Equal("chapter", new RecordingMark(0, "  chapter  ", "agent").Label);
        Assert.Equal(200, new RecordingMark(0, new string('x', 200), "agent").Label.Length);
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, new string('x', 201), "agent"));
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, "\uD800", "agent"));
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, "\uDC00", "agent"));
    }

    [Fact]
    public void FirstFrameTransition_EstablishesMonotonicAnchorExactlyOnce()
    {
        var rec = new Recording
        {
            SourceType = "display",
            OutputPath = Path.Combine(_dataDir, "transition.mp4"),
            Config = new CaptureConfig { SourceKind = "display", OutputPath = Path.Combine(_dataDir, "transition.mp4") }
        };
        var backend = new FirstFrameBackend { EmitOnStart = true };
        _engine.BackendFactory = _ => (backend, "fake-first-frame");
        var providerCalls = 0;
        _engine.MonotonicTimestampProviderForTests = () =>
        {
            providerCalls++;
            return 10_000;
        };
        _engine.UtcNowForTests = () => new DateTime(2026, 8, 20, 4, 0, 0, DateTimeKind.Utc);

        _engine.StartCaptureForTests(rec, new NoOpTray());

        Assert.Equal(RecState.recording, rec.State);
        Assert.Equal(10_000, rec.MarkTimelineAnchorTicks);
        Assert.Equal(1, providerCalls);

        backend.Emit();
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public void MarkTimeline_UsesTickDeltaAndRejectsMissingRegressionAndOverflow()
    {
        var firstFrame = new DateTime(2026, 8, 20, 5, 0, 0, DateTimeKind.Utc);
        var rec = Register(startedAtUtc: firstFrame);
        rec.MarkTimelineAnchorTicks = null;
        _engine.MonotonicTimestampProviderForTests = () => 1000;

        var missing = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "missing"));
        Assert.Equal("RECORDING_NOT_ACTIVE", missing.Code);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");

        rec.MarkTimelineAnchorTicks = 1000;
        _engine.MonotonicTimestampProviderForTests = () => 999;
        var regression = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "regression"));
        Assert.Equal("RECORDING_NOT_ACTIVE", regression.Code);
        Assert.Empty(rec.SnapshotMarks());

        rec.MarkTimelineAnchorTicks = 0;
        _engine.MonotonicFrequencyForTests = 1;
        _engine.MonotonicTimestampProviderForTests = () => long.MaxValue;
        var overflow = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "overflow"));
        Assert.Equal("RECORDING_NOT_ACTIVE", overflow.Code);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }

    [Fact]
    public async Task DomainOperation_PreservesSequentialOrderAndConcurrentMarks()
    {
        var firstFrame = new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);
        var rec = Register(startedAtUtc: firstFrame);
        _engine.UtcNowForTests = () => firstFrame.AddSeconds(1);

        _engine.AddMark(rec.Id, "first");
        _engine.AddMark(rec.Id, "second");
        Assert.Equal(new[] { "first", "second" }, rec.SnapshotMarks().Select(m => m.Label));

        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => _engine.AddMark(rec.Id, $"concurrent-{i}")))
            .ToArray();
        await Task.WhenAll(tasks);

        var snapshot = rec.SnapshotMarks();
        Assert.Equal(102, snapshot.Count);
        Assert.Equal(102, snapshot.Select(m => m.Label).Distinct(StringComparer.Ordinal).Count());
        var markEvents = _audit.Events.Where(e => e.evt == "recording.mark_added").ToArray();
        Assert.Equal(102, markEvents.Length);
        Assert.All(markEvents, e => Assert.DoesNotContain("label", e.json, StringComparison.OrdinalIgnoreCase));
    }
}
