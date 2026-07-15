using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Engine-level race tests for confirmation approve/reject/expiry. Uses a fake
/// tray that captures the confirmation callback and the internal expiry trigger
/// to release both decision paths simultaneously.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingConfirmationRaceTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public RecordingConfirmationRaceTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
    }

    public void Dispose()
    {
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        DataDirResolver.ClearOverride();
        _tmp.Dispose();
    }

    private static List<JsonNode> ReadTraceEvents(string path)
    {
        if (!File.Exists(path)) return new List<JsonNode>();
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private static int CountEvent(IEnumerable<JsonNode> events, string name)
        => events.Count(e => e["event"]?.GetValue<string>() == name);

    private static IReadOnlyList<JsonNode> EventsForTrace(IEnumerable<JsonNode> events, string traceId)
        => events.Where(e => e["trace_id"]?.GetValue<string>() == traceId)
            .OrderBy(e => e["elapsed_from_intent_ms"]?.GetValue<double>() ?? -1.0)
            .ToList();

    private sealed class CapturingTray : ITrayContext
    {
        public Action<ConfirmationDecision>? CapturedCallback { get; private set; }
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            CapturedCallback = callback;
        }

        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class AutoCompleteBackend : ICaptureBackend
    {
        public int StartCallCount { get; private set; }
        private Action<int, OutputMeta>? _naturalExit;

        public void Start(CaptureConfig cfg)
        {
            StartCallCount++;
            cfg.CommandArgs = "fake";
            _naturalExit?.Invoke(0, new OutputMeta { SizeBytes = 1024, DurationSeconds = 60 });
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private (RecordingEngine Engine, RecordingPerformanceTracer Tracer, CapturingTray Tray, AutoCompleteBackend Backend, string RecordingId, string ConfirmationId, string TraceId) Setup()
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var tracer = new RecordingPerformanceTracer(writer);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, tracer);
        var tray = new CapturingTray();
        engine.SetTray(tray);
        var backend = new AutoCompleteBackend();
        engine.BackendFactory = _ => (backend, "fake");
        engine.ConfirmationTimeout = TimeSpan.FromHours(1);

        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        var traceId = "trace_race_" + Guid.NewGuid().ToString("N")[..8];
        var cfg = JsonNode.Parse("{" +
            "\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"}," +
            "\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}")!;
        var result = engine.CreateRecording(cfg, "test", tray, traceId: traceId, endpoint: "recordings");

        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var recordingId = doc.RootElement.GetProperty("recording_id").GetString()!;
        var confirmationId = doc.RootElement.GetProperty("confirmation_id").GetString()!;

        return (engine, tracer, tray, backend, recordingId, confirmationId, traceId);
    }

    [Fact]
    public async Task ApprovalVersusExpiry_Race_OnlyOneWins()
    {
        var (engine, tracer, tray, backend, _, confirmationId, traceId) = Setup();
        Assert.NotNull(tray.CapturedCallback);

        var barrier = new Barrier(2);
        var approveTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            tray.CapturedCallback!(ConfirmationDecision.Approve());
        });
        var expireTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            engine.TriggerConfirmationExpiryForTests(confirmationId);
        });

        await Task.WhenAll(approveTask, expireTask);
        tracer.Flush();

        // Determine the actual winner from authoritative confirmation state,
        // not from which task finished first.
        var winner = engine._confs[confirmationId].Status == "expired" ? "expired" : "approved";

        var events = ReadTraceEvents(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));

        if (winner == "expired")
        {
            Assert.Equal(0, backend.StartCallCount);
            Assert.Equal(0, CountEvent(byTrace, "capture.start_requested"));
            var terminal = byTrace.Last(e => e["event"]?.GetValue<string>() == "recording.terminal");
            Assert.Equal("expired", terminal["data"]!["status"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(1, backend.StartCallCount);
            Assert.Equal(1, CountEvent(byTrace, "capture.start_requested"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.expired"));
        }
    }

    [Fact]
    public async Task RejectionVersusExpiry_Race_OnlyOneWins()
    {
        var (engine, tracer, tray, backend, _, confirmationId, traceId) = Setup();
        Assert.NotNull(tray.CapturedCallback);

        var barrier = new Barrier(2);
        var rejectTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            tray.CapturedCallback!(ConfirmationDecision.Reject());
        });
        var expireTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            engine.TriggerConfirmationExpiryForTests(confirmationId);
        });

        await Task.WhenAll(rejectTask, expireTask);
        tracer.Flush();

        var events = ReadTraceEvents(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(0, backend.StartCallCount);
        Assert.Equal(0, CountEvent(byTrace, "capture.start_requested"));
    }

    [Fact]
    public void DuplicateApprovalCallback_DoesNotStartBackendTwice()
    {
        var (engine, tracer, tray, backend, _, confirmationId, traceId) = Setup();
        Assert.NotNull(tray.CapturedCallback);

        tray.CapturedCallback(ConfirmationDecision.Approve());
        tray.CapturedCallback(ConfirmationDecision.Approve());
        tracer.Flush();

        var events = ReadTraceEvents(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, backend.StartCallCount);
        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(1, CountEvent(byTrace, "confirmation.approved"));
        Assert.Equal(0, CountEvent(byTrace, "confirmation.rejected"));
        Assert.Equal(0, CountEvent(byTrace, "confirmation.expired"));
    }

    [Fact]
    public void ExpiredConfirmation_BackendStartCountIsZero()
    {
        var (engine, tracer, tray, backend, _, confirmationId, traceId) = Setup();
        Assert.NotNull(tray.CapturedCallback);

        engine.TriggerConfirmationExpiryForTests(confirmationId);
        tray.CapturedCallback!(ConfirmationDecision.Approve());
        tracer.Flush();

        var events = ReadTraceEvents(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(0, backend.StartCallCount);
        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(1, CountEvent(byTrace, "confirmation.expired"));
        Assert.Equal(0, CountEvent(byTrace, "confirmation.approved"));
    }

    [Fact]
    public void ApprovedConfirmation_ExpiryDoesNotWriteEvents()
    {
        var (engine, tracer, tray, backend, _, confirmationId, traceId) = Setup();
        Assert.NotNull(tray.CapturedCallback);

        tray.CapturedCallback(ConfirmationDecision.Approve());
        engine.TriggerConfirmationExpiryForTests(confirmationId);
        tracer.Flush();

        var events = ReadTraceEvents(Path.Combine(_tmp.Path, "perf", "recording-traces.jsonl"));
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, backend.StartCallCount);
        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(1, CountEvent(byTrace, "confirmation.approved"));
        Assert.Equal(0, CountEvent(byTrace, "confirmation.expired"));
    }
}
