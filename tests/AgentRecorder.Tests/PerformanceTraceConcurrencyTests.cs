using System;
using System.Collections.Concurrent;
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
/// Concurrency-focused evidence for the performance trace spine: exactly-once
/// validation/terminal, lifecycle tombstones, two-phase cleanup, writer drain,
/// and backend callback + throw boundaries.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class PerformanceTraceConcurrencyTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public PerformanceTraceConcurrencyTests()
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

    private static RecordingPerformanceTracer CreateTracer(
        RollingJsonlWriter? writer = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? terminalTtl = null,
        int? maxContexts = null)
    {
        writer ??= new RollingJsonlWriter(Path.Combine(Path.GetTempPath(), $"perf-concurrency-{Guid.NewGuid():N}.jsonl"));
        return new RecordingPerformanceTracer(writer, utcNow ?? (() => DateTime.UtcNow),
            () => System.Diagnostics.Stopwatch.GetTimestamp(), terminalTtl, maxContexts);
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

    private static List<(string Event, string? RecordingId)> ReadAuditEvents()
    {
        var path = Paths.AuditLogPath;
        if (!File.Exists(path)) return new List<(string, string?)>();
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var node = JsonNode.Parse(line);
                return (node?["event"]?.GetValue<string>() ?? "", node?["recording_id"]?.GetValue<string>());
            })
            .ToList();
    }

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        private int _showErrorCount;
        public int ShowErrorCount => _showErrorCount;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) => Interlocked.Increment(ref _showErrorCount);
    }

    private sealed class NaturalExitThenThrowBackend : ICaptureBackend
    {
        public int StartCallCount { get; private set; }
        private Action<int, OutputMeta>? _naturalExit;

        public void Start(CaptureConfig cfg)
        {
            StartCallCount++;
            cfg.CommandArgs = "fake";
            _naturalExit?.Invoke(0, new OutputMeta { SizeBytes = 1024, DurationSeconds = 60 });
            throw new InvalidOperationException("Simulated failure after natural exit");
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public int ExitCode => 0;
        public void Dispose() { }
    }

    [Fact]
    public async Task Confirmation_ConcurrentApproveAndExpire_OnlyOneClaimWins()
    {
        var conf = new Confirmation { RecordingId = "rec_1" };
        var barrier = new Barrier(2);
        var results = new ConcurrentBag<(string Thread, bool Won)>();

        var t1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            results.Add(("approve", conf.TryDecide("approved")));
        });
        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            results.Add(("expire", conf.TryDecide("expired")));
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.Won);
        Assert.Contains(conf.Status, new[] { "approved", "expired" });
        Assert.True(conf.IsDecided);
    }

    [Fact]
    public async Task Confirmation_ConcurrentRejectAndExpire_OnlyOneClaimWins()
    {
        var conf = new Confirmation { RecordingId = "rec_1" };
        var barrier = new Barrier(2);
        var results = new ConcurrentBag<(string Thread, bool Won)>();

        var t1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            results.Add(("reject", conf.TryDecide("rejected")));
        });
        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            results.Add(("expire", conf.TryDecide("expired")));
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.Won);
        Assert.Contains(conf.Status, new[] { "rejected", "expired" });
        Assert.True(conf.IsDecided);
    }

    [Fact]
    public void Confirmation_DuplicateCallback_OnlyFirstDecisionHasEffects()
    {
        var conf = new Confirmation { RecordingId = "rec_1" };

        Assert.True(conf.TryDecide("approved"));
        Assert.False(conf.TryDecide("approved"));
        Assert.False(conf.TryDecide("rejected"));
        Assert.False(conf.TryDecide("expired"));

        Assert.Equal("approved", conf.Status);
        Assert.True(conf.IsDecided);
    }

    [Fact]
    public async Task IntentValidation_ConcurrentConflictingCalls_WritesExactlyOneResult()
    {
        var path = Path.Combine(_tmp.Path, "perf", "intent-conflict.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = CreateTracer(writer);
        var traceId = "trace_conflict_1";
        const int participants = 50;
        var barrier = new Barrier(participants);
        var tasks = new List<Task>();

        tracer.IntentAccepted(traceId, "recordings");

        for (int i = 0; i < participants; i++)
        {
            var success = i % 2 == 0;
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                tracer.IntentValidated(traceId, "recordings", success, errorCode: "TEST");
            }));
        }

        await Task.WhenAll(tasks);
        writer.Flush();

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);
        Assert.Equal(1, CountEvent(byTrace, "intent.validated") + CountEvent(byTrace, "intent.failed"));
        Assert.True(tracer.HasValidationResult(traceId));
    }

    [Fact]
    public async Task RecordingTerminal_ConcurrentCalls_WritesExactlyOneTerminal()
    {
        var path = Path.Combine(_tmp.Path, "perf", "terminal-conflict.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = CreateTracer(writer);
        var traceId = "trace_terminal_conflict";
        const int participants = 50;
        var barrier = new Barrier(participants);
        var tasks = new List<Task>();

        tracer.IntentAccepted(traceId, "recordings");
        tracer.CorrelationSet(traceId, "rec_terminal", sourceType: "display");

        for (int i = 0; i < participants; i++)
        {
            var status = i % 2 == 0 ? "completed" : "failed";
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                tracer.RecordingTerminal(traceId, "rec_terminal", status, stopReason: "test", errorCode: "TEST");
            }));
        }

        await Task.WhenAll(tasks);
        writer.Flush();

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);
        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
    }

    [Fact]
    public void IntentFailure_CleanupThenLateCall_DoesNotWriteDuplicate()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_tmp.Path, "perf", "intent-cleanup-late.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = CreateTracer(writer, () => now, terminalTtl: TimeSpan.FromMinutes(1), maxContexts: 1);
        tracer.AutoCleanupEnabled = false;
        var traceId = "trace_intent_cleanup";

        tracer.IntentAccepted(traceId, "recordings");
        tracer.IntentValidated(traceId, "recordings", success: false, errorCode: "TEST");

        now += TimeSpan.FromMinutes(2);
        tracer.RunCleanup();

        Assert.Equal(0, tracer.TraceContextCount);
        Assert.True(tracer.HasValidationResult(traceId));

        tracer.IntentValidated(traceId, "recordings", success: false, errorCode: "TEST");
        writer.Flush();

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);
        Assert.Equal(1, CountEvent(byTrace, "intent.failed"));
        Assert.Equal(0, CountEvent(byTrace, "intent.validated"));
    }

    [Fact]
    public void RecordingTerminal_CleanupThenLateCall_DoesNotWriteDuplicate()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_tmp.Path, "perf", "terminal-cleanup-late.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = CreateTracer(writer, () => now, terminalTtl: TimeSpan.FromMinutes(1), maxContexts: 1);
        tracer.AutoCleanupEnabled = false;
        var traceId = "trace_terminal_cleanup";

        tracer.IntentAccepted(traceId, "recordings");
        tracer.CorrelationSet(traceId, "rec_terminal", sourceType: "display");
        tracer.RecordingTerminal(traceId, "rec_terminal", status: "failed", errorCode: "TEST");

        now += TimeSpan.FromMinutes(2);
        tracer.RunCleanup();

        Assert.Equal(0, tracer.TraceContextCount);

        tracer.RecordingTerminal(traceId, "rec_terminal", status: "failed", errorCode: "TEST");
        writer.Flush();

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);
        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
    }

    [Fact]
    public void Writer_DisposeDrainsAllAcceptedLines()
    {
        var path = Path.Combine(_tmp.Path, "perf", "dispose-drain.jsonl");
        const int n = 100;
        var writer = new RollingJsonlWriter(path, boundedCapacity: 1000);

        for (int i = 0; i < n; i++)
            writer.Enqueue($"{{\"seq\":{i}}}");

        writer.Dispose();

        var lines = File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        Assert.Equal(n, lines.Count);
        foreach (var line in lines)
            Assert.NotNull(JsonNode.Parse(line));
    }

    [Fact]
    public async Task Writer_ConcurrentEnqueueAndDispose_DoesNotThrowOrCorruptJson()
    {
        var path = Path.Combine(_tmp.Path, "perf", "enqueue-dispose-race.jsonl");
        const int threads = 5;
        const int linesPerThread = 50;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var writer = new RollingJsonlWriter(path, boundedCapacity: 1000);
        var barrier = new Barrier(threads + 1);
        var exceptions = new ConcurrentBag<Exception>();
        var enqueueTasks = new List<Task>();

        for (int t = 0; t < threads; t++)
        {
            var threadIndex = t;
            enqueueTasks.Add(Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < linesPerThread; i++)
                        writer.Enqueue($"{{\"t\":{threadIndex},\"i\":{i}}}");
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        var disposeTask = Task.Run(() =>
        {
            try
            {
                barrier.SignalAndWait();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(enqueueTasks.Concat(new[] { disposeTask }));

        Assert.Empty(exceptions);

        var events = ReadTraceEvents(path);
        Assert.All(events, e => Assert.NotNull(e));
    }

    [Fact]
    public void Cleanup_CapacityEvictsSpecificOldestTerminalOnly()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_tmp.Path, "perf", "cleanup-capacity.jsonl");
        var writer = new RollingJsonlWriter(path);
        // Active + 4 terminals = 5 contexts; capacity should evict exactly the oldest terminal.
        using var tracer = CreateTracer(writer, () => now, terminalTtl: TimeSpan.FromHours(1), maxContexts: 4);
        tracer.AutoCleanupEnabled = false;

        tracer.IntentAccepted("trace_active", "recordings");
        tracer.CorrelationSet("trace_active", "rec_active", "conf_active");

        // Four terminal traces with distinct terminal timestamps.
        for (int i = 0; i < 4; i++)
        {
            var tid = $"trace_term_{i}";
            tracer.IntentAccepted(tid, "recordings");
            tracer.CorrelationSet(tid, $"rec_{i}", $"conf_{i}");
            tracer.RecordingTerminal(tid, $"rec_{i}", status: "failed");
            now += TimeSpan.FromSeconds(1);
        }

        tracer.RunCleanup();

        Assert.Equal(4, tracer.TraceContextCount);
        Assert.Equal(1, tracer.ActiveTraceCount);

        // Oldest terminal trace (0) must be evicted; newer ones must survive.
        Assert.Null(tracer.ResolveTraceId("rec_0"));
        Assert.Equal("trace_term_1", tracer.ResolveTraceId("rec_1"));
        Assert.Equal("trace_term_2", tracer.ResolveTraceId("rec_2"));
        Assert.Equal("trace_term_3", tracer.ResolveTraceId("rec_3"));
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));
    }

    [Fact]
    public void Cleanup_TtlThenCapacity_UsesTwoDistinctStages()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_tmp.Path, "perf", "cleanup-ttl-capacity.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = CreateTracer(writer, () => now, terminalTtl: TimeSpan.FromMinutes(2), maxContexts: 2);
        tracer.AutoCleanupEnabled = false;

        tracer.IntentAccepted("trace_active", "recordings");
        tracer.CorrelationSet("trace_active", "rec_active", "conf_active");

        // trace_expired is the only terminal older than the TTL.
        tracer.IntentAccepted("trace_expired", "recordings");
        tracer.CorrelationSet("trace_expired", "rec_expired", "conf_expired");
        tracer.RecordingTerminal("trace_expired", "rec_expired", status: "failed");

        now += TimeSpan.FromMinutes(2);
        tracer.IntentAccepted("trace_old", "recordings");
        tracer.CorrelationSet("trace_old", "rec_old", "conf_old");
        tracer.RecordingTerminal("trace_old", "rec_old", status: "failed");

        now += TimeSpan.FromSeconds(1);
        tracer.IntentAccepted("trace_new", "recordings");
        tracer.CorrelationSet("trace_new", "rec_new", "conf_new");
        tracer.RecordingTerminal("trace_new", "rec_new", status: "failed");

        now += TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1);
        tracer.RunCleanup();

        Assert.Equal(2, tracer.TraceContextCount);
        Assert.Equal(1, tracer.ActiveTraceCount);

        // TTL stage removes trace_expired; capacity stage removes the older
        // of the two remaining terminals (trace_old), keeping trace_new.
        Assert.Null(tracer.ResolveTraceId("rec_expired"));
        Assert.Null(tracer.ResolveTraceId("rec_old"));
        Assert.Equal("trace_new", tracer.ResolveTraceId("rec_new"));
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));
    }

    [Fact]
    public void Backend_NaturalExitThenStartThrows_WritesExactlyOneTerminal()
    {
        var path = Path.Combine(_tmp.Path, "perf", "backend-natural-then-throw.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = new RecordingPerformanceTracer(writer);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, tracer);
        var tray = new NoOpTray();
        engine.SetTray(tray);
        var backend = new NaturalExitThenThrowBackend();
        engine.BackendFactory = _ => (backend, "fake");

        var rec = new Recording
        {
            SourceType = "display",
            OutputPath = Path.Combine(_tmp.Path, "out.mp4"),
            Config = new CaptureConfig { CommandArgs = "fake" }
        };

        engine.StartCaptureForTests(rec, tray);
        writer.Flush();

        var traceId = tracer.ResolveTraceId(rec.Id);
        Assert.NotNull(traceId);

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(1, CountEvent(byTrace, "capture.start_requested"));
        Assert.Equal(1, CountEvent(byTrace, "capture.backend_start_failed"));
        Assert.Equal(0, CountEvent(byTrace, "capture.backend_start_returned"));

        var terminal = byTrace.Last(e => e["event"]?.GetValue<string>() == "recording.terminal");
        Assert.Equal("completed", terminal["data"]!["status"]!.GetValue<string>());

        Assert.Equal(RecState.completed, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.Null(rec.Error);
        Assert.Equal(0, tray.ShowErrorCount);

        var auditLines = ReadAuditEvents().Where(e => e.RecordingId == rec.Id).ToList();
        var terminalAuditIndices = auditLines
            .Select((e, i) => (Event: e.Event, Index: i))
            .Where(x => x.Event == "recording.completed" || x.Event == "recording.failed")
            .ToList();
        Assert.Single(terminalAuditIndices);
        Assert.Equal("recording.completed", terminalAuditIndices[0].Event);

        Assert.DoesNotContain(auditLines, e => e.Event == "recording.failed");
        Assert.Contains(auditLines, e => e.Event == "recording.backend_start_exception_after_terminal" && e.RecordingId == rec.Id);
    }

    private sealed class DirectThrowBackend : ICaptureBackend
    {
        public int StartCallCount { get; private set; }
        public void Start(CaptureConfig cfg)
        {
            StartCallCount++;
            throw new InvalidOperationException("direct throw");
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => -1;
        public void Dispose() { }
    }

    [Fact]
    public void Backend_DirectStartThrow_MarksFailedAndShowsErrorOnce()
    {
        var path = Path.Combine(_tmp.Path, "perf", "backend-direct-throw.jsonl");
        var writer = new RollingJsonlWriter(path);
        using var tracer = new RecordingPerformanceTracer(writer);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, tracer);
        var tray = new NoOpTray();
        engine.SetTray(tray);
        var backend = new DirectThrowBackend();
        engine.BackendFactory = _ => (backend, "fake");

        var rec = new Recording
        {
            SourceType = "display",
            OutputPath = Path.Combine(_tmp.Path, "direct-throw-out.mp4"),
            Config = new CaptureConfig { CommandArgs = "fake" }
        };

        engine.StartCaptureForTests(rec, tray);
        writer.Flush();

        var traceId = tracer.ResolveTraceId(rec.Id);
        Assert.NotNull(traceId);

        var events = ReadTraceEvents(path);
        var byTrace = EventsForTrace(events, traceId);

        Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
        Assert.Equal(1, CountEvent(byTrace, "capture.start_requested"));
        Assert.Equal(1, CountEvent(byTrace, "capture.backend_start_failed"));
        Assert.Equal(0, CountEvent(byTrace, "capture.backend_start_returned"));

        var terminal = byTrace.Last(e => e["event"]?.GetValue<string>() == "recording.terminal");
        Assert.Equal("failed", terminal["data"]!["status"]!.GetValue<string>());
        Assert.Equal("unexpected_exit", terminal["data"]!["stop_reason"]!.GetValue<string>());

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("direct throw", rec.Error);
        Assert.Equal(1, tray.ShowErrorCount);

        Assert.Contains(ReadAuditEvents().Where(e => e.RecordingId == rec.Id), e => e.Event == "recording.failed");
    }
}
