using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for the performance tracing infrastructure: event capture,
/// monotonic elapsed time, client hint filtering, rolling JSONL writer and
/// failure isolation.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class PerformanceTracerTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public PerformanceTracerTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        _tmp.Dispose();
    }

    private static RecordingPerformanceTracer CreateTracer(
        RollingJsonlWriter? writer = null,
        Func<DateTime>? utcNow = null,
        Func<long>? timestampTicks = null)
    {
        writer ??= new RollingJsonlWriter(Path.Combine(Path.GetTempPath(), $"perf-test-{Guid.NewGuid():N}.jsonl"));
        return new RecordingPerformanceTracer(writer, utcNow ?? (() => DateTime.UtcNow), timestampTicks ?? new Func<long>(() => Stopwatch.GetTimestamp()));
    }

    private sealed class FakeTracer : IPerformanceTracer
    {
        public readonly ConcurrentBag<(string TraceId, string EventName)> Events = new();

        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) =>
            Events.Add((traceId, "intent.accepted"));

        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) =>
            Events.Add((traceId, "ensure_context.associated"));

        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) =>
            Events.Add((traceId, success ? "intent.validated" : "intent.failed"));

        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) =>
            Events.Add((traceId, "correlation.set"));

        public bool HasValidationResult(string traceId) => false;

        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) =>
            Events.Add((traceId, "confirmation.created"));

        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) =>
            Events.Add((traceId, "confirmation.shown"));

        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) =>
            Events.Add((traceId, "confirmation.approved"));

        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) =>
            Events.Add((traceId, "confirmation.rejected"));

        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) =>
            Events.Add((traceId, "confirmation.expired"));

        public void CaptureStartRequested(string traceId, string recordingId, string backendType) =>
            Events.Add((traceId, "capture.start_requested"));

        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) =>
            Events.Add((traceId, "capture.backend_start_returned"));

        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) =>
            Events.Add((traceId, "capture.backend_start_failed"));

        public void MicrophonePrepareStarted(string traceId, string recordingId) =>
            Events.Add((traceId, "microphone_prepare_started"));

        public void MicrophoneReady(string traceId, string recordingId) =>
            Events.Add((traceId, "microphone_ready"));

        public void CountdownStarted(string traceId, string recordingId) =>
            Events.Add((traceId, "countdown_started"));

        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) =>
            Events.Add((traceId, "capture.first_frame_observed"));

        public void CaptureEnded(string traceId, string recordingId) =>
            Events.Add((traceId, "capture_ended"));

        public void FinalizationCompleted(string traceId, string recordingId, bool success) =>
            Events.Add((traceId, "finalization_completed"));

        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null) =>
            Events.Add((traceId, "recording.terminal"));

        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) =>
            Events.Add((traceId, "long_poll.completed"));

        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
    }

    [Fact]
    public void IntentAccepted_RecordsStartAndElapsedTime()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        long ticks = 1000;
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "t1.jsonl"));
        using var tracer = CreateTracer(writer, () => now, () => Interlocked.Increment(ref ticks));

        tracer.IntentAccepted("trace_1", "recordings");
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        Assert.Single(lines);
        var evt = JsonNode.Parse(lines[0])!;
        Assert.Equal("trace_1", evt["trace_id"]!.GetValue<string>());
        Assert.Equal("intent.accepted", evt["event"]!.GetValue<string>());
        Assert.Equal("recordings", evt["endpoint"]!.GetValue<string>());
        Assert.True(evt["elapsed_from_intent_ms"]!.GetValue<double>() >= 0);
        Assert.Equal("2026-07-15T00:00:00Z", evt["timestamp_utc"]!.GetValue<string>());
    }

    [Fact]
    public void IntentAccepted_ValidClientHint_RecordsUntrustedHint()
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "hint.jsonl"));
        using var tracer = CreateTracer(writer);

        var clientSentAt = DateTime.UtcNow.AddMilliseconds(-123).ToString("O");
        tracer.IntentAccepted("trace_hint", "recordings", clientSentAt);
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        var evt = JsonNode.Parse(lines[0])!;
        var hints = evt["client_hints"]!;
        Assert.Equal("untrusted_client_hint", hints["trust"]!.GetValue<string>());
        Assert.True(hints["agent_to_server_hint_ms"]!.GetValue<double>() > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IntentAccepted_MissingClientHint_NoHints(string? clientSentAt)
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "nohint.jsonl"));
        using var tracer = CreateTracer(writer);

        tracer.IntentAccepted("trace_nohint", "recordings", clientSentAt);
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        var evt = JsonNode.Parse(lines[0])!;
        Assert.Null(evt["client_hints"]);
    }

    [Theory]
    [InlineData("not-a-date", "rejected_unparseable")]
    [InlineData("3026-01-01T00:00:00Z", "rejected_out_of_range")]
    [InlineData("2000-01-01T00:00:00Z", "rejected_out_of_range")]
    public void IntentAccepted_BadClientHint_MarksTrust(string clientSentAt, string expectedTrust)
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "badhint.jsonl"));
        using var tracer = CreateTracer(writer);

        tracer.IntentAccepted("trace_badhint", "recordings", clientSentAt);
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        var evt = JsonNode.Parse(lines[0])!;
        Assert.Equal(expectedTrust, evt["client_hints"]!["trust"]!.GetValue<string>());
    }

    [Fact]
    public void CorrelationSet_ResolveTraceIdWorks()
    {
        using var tracer = CreateTracer();

        tracer.CorrelationSet("trace_a", "rec_a", "conf_a");

        Assert.Equal("trace_a", tracer.ResolveTraceId("rec_a"));
        Assert.Equal("trace_a", tracer.ResolveTraceId(null, "conf_a"));
        Assert.Null(tracer.ResolveTraceId("rec_b"));
    }

    [Fact]
    public void CaptureBackendStartFailed_IncludesErrorCodeAndType()
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "fail.jsonl"));
        using var tracer = CreateTracer(writer);

        tracer.CorrelationSet("trace_fail", "rec_fail");
        tracer.CaptureBackendStartFailed("trace_fail", "rec_fail", "ffmpeg", "backend_start_exception", "InvalidOperationException");
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        var evt = JsonNode.Parse(lines[0])!;
        Assert.Equal("capture.backend_start_failed", evt["event"]!.GetValue<string>());
        Assert.Equal("backend_start_exception", evt["data"]!["error_code"]!.GetValue<string>());
        Assert.Equal("InvalidOperationException", evt["data"]!["error_type"]!.GetValue<string>());
    }

    [Fact]
    public void RollingJsonlWriter_WritesValidJsonLines()
    {
        var path = Path.Combine(_tmp.Path, "perf", "rolling.jsonl");
        using var writer = new RollingJsonlWriter(path);

        writer.Enqueue("{\"a\":1}");
        writer.Enqueue("{\"b\":2}");
        writer.Flush();

        var lines = ReadAllLines(path);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["a"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["b"]!.GetValue<int>());
    }

    [Fact]
    public void RollingJsonlWriter_RollsWhenThresholdReached()
    {
        var path = Path.Combine(_tmp.Path, "perf", "roll.jsonl");
        var line = "{\"x\":\"y\"}";
        // Threshold 100 bytes, keep 2 history files.
        using var writer = new RollingJsonlWriter(path, maxFileSize: 100, maxHistoryFiles: 2);

        for (int i = 0; i < 20; i++)
            writer.Enqueue(line);
        writer.Flush();

        var history1 = Path.Combine(Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(history1), "history file .1 should exist after rolling");
        // With maxHistoryFiles=2, .2 may or may not exist depending on total size.
    }

    [Fact]
    public void RollingJsonlWriter_FlushThenEnqueueStillWrites()
    {
        var path = Path.Combine(_tmp.Path, "perf", "flush-cont.jsonl");
        using var writer = new RollingJsonlWriter(path);

        writer.Enqueue("{\"a\":1}");
        writer.Flush();
        writer.Enqueue("{\"b\":2}");
        writer.Flush();

        var lines = ReadAllLines(path);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["a"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["b"]!.GetValue<int>());
    }

    [Fact]
    public async Task RollingJsonlWriter_ConcurrentWrites_NoCorruption()
    {
        var path = Path.Combine(_tmp.Path, "perf", "concurrent.jsonl");
        using var writer = new RollingJsonlWriter(path, boundedCapacity: 2000);

        const int threads = 5;
        const int linesPerThread = 200;
        var expected = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int t = 0; t < threads; t++)
        {
            var threadIndex = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < linesPerThread; i++)
                {
                    var line = $"{{\"thread\":{threadIndex},\"seq\":{i}}}";
                    writer.Enqueue(line);
                    expected.Add(line);
                }
            }));
        }

        await Task.WhenAll(tasks);
        writer.Flush(TimeSpan.FromSeconds(10));

        var written = ReadAllLines(path).ToHashSet();
        Assert.Equal(0, writer.DroppedCount);
        Assert.Equal(expected.Count, written.Count);
        foreach (var line in expected)
        {
            Assert.Contains(line, written);
        }
    }

    [Fact]
    public void RollingJsonlWriter_BoundedQueue_DropsExcess()
    {
        var path = Path.Combine(_tmp.Path, "perf", "drop.jsonl");
        var writer = new RollingJsonlWriter(path, boundedCapacity: 5);
        const int total = 50;
        try
        {
            writer.PauseProcessing();

            for (int i = 0; i < total; i++)
                writer.Enqueue($"{{\"i\":{i}}}");
        }
        finally
        {
            writer.ResumeProcessing();
        }

        writer.Flush();

        var written = ReadAllLines(path).Count;
        Assert.True(writer.DroppedCount > 0, "some lines should have been dropped");
        Assert.Equal(total, written + writer.DroppedCount);

        foreach (var line in ReadAllLines(path))
        {
            var node = JsonNode.Parse(line);
            Assert.NotNull(node);
            Assert.NotNull(node["i"]);
        }

        writer.Dispose();
    }

    [Fact]
    public void RollingJsonlWriter_MaxHistoryFilesZero_DiscardsOldContent()
    {
        var path = Path.Combine(_tmp.Path, "perf", "no-history.jsonl");
        using var writer = new RollingJsonlWriter(path, maxFileSize: 50, maxHistoryFiles: 0);

        for (int i = 0; i < 20; i++)
            writer.Enqueue($"{{\"n\":{i}}}");

        writer.Flush();

        var history1 = Path.ChangeExtension(path, null) + ".1" + Path.GetExtension(path);
        Assert.False(File.Exists(history1), "no history file should be kept when maxHistoryFiles=0");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RollingJsonlWriter_RetainsExactHistoryCount()
    {
        var path = Path.Combine(_tmp.Path, "perf", "history-count.jsonl");
        using var writer = new RollingJsonlWriter(path, maxFileSize: 50, maxHistoryFiles: 2);

        for (int i = 0; i < 40; i++)
            writer.Enqueue($"{{\"n\":{i}}}");

        writer.Flush();

        var history1 = Path.ChangeExtension(path, null) + ".1" + Path.GetExtension(path);
        var history2 = Path.ChangeExtension(path, null) + ".2" + Path.GetExtension(path);
        var history3 = Path.ChangeExtension(path, null) + ".3" + Path.GetExtension(path);

        Assert.True(File.Exists(path), "base file must exist");
        Assert.True(File.Exists(history1), "history file .1 must exist");
        Assert.True(File.Exists(history2), "history file .2 must exist");
        Assert.False(File.Exists(history3), "history beyond maxHistoryFiles should not exist");

        var files = writer.GetExistingFiles();
        Assert.Equal(3, files.Count);
        Assert.Contains(path, files);
        Assert.Contains(history1, files);
        Assert.Contains(history2, files);
    }

    [Fact]
    public void RollingJsonlWriter_InjectedWriteFault_IsolatedAndContinues()
    {
        var path = Path.Combine(_tmp.Path, "perf", "fault.jsonl");
        using var writer = new RollingJsonlWriter(path);
        writer.FailNextNAppends = 3;

        for (int i = 0; i < 6; i++)
            writer.Enqueue($"{{\"i\":{i}}}");

        writer.Flush();

        var lines = ReadAllLines(path);
        // The first three lines were dropped by the simulated fault; the rest succeeded.
        foreach (var line in lines)
        {
            var node = JsonNode.Parse(line)!;
            var value = node["i"]!.GetValue<int>();
            Assert.True(value >= 3, $"line {value} should not have been written before fault cleared");
        }

        // Writer remains usable after the fault clears.
        writer.Enqueue("{\"after\":true}");
        writer.Flush();
        Assert.Contains(ReadAllLines(path), l => l.Contains("\"after\":true"));
    }

    [Fact]
    public void TracerFailure_IsolatedAndDoesNotThrow()
    {
        // Use a writer backed by a path in a non-existent drive-like location is hard
        // cross-platform; instead pass a writer and force an exception by disposing it.
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "iso.jsonl"));
        using var tracer = CreateTracer(writer);

        writer.Dispose();

        // These must not throw even though the writer is disposed.
        tracer.IntentAccepted("trace_iso", "recordings");
        tracer.ConfirmationCreated("trace_iso", "rec_iso", "conf_iso");
        tracer.Flush();
    }

    [Fact]
    public void NoOpTracer_AllMethodsAreNoOps()
    {
        var noop = NoOpPerformanceTracer.Instance;
        noop.IntentAccepted("t", "e");
        noop.IntentValidated("t", "e", true);
        noop.CorrelationSet("t", "r");
        noop.ConfirmationCreated("t", "r", "c");
        noop.ConfirmationShown("t", "r", "c");
        noop.ConfirmationApproved("t", "r", "c");
        noop.ConfirmationRejected("t", "r", "c");
        noop.ConfirmationExpired("t", "r", "c");
        noop.CaptureStartRequested("t", "r", "b");
        noop.CaptureBackendStartReturned("t", "r", "b");
        noop.CaptureBackendStartFailed("t", "r", "b", "e", "t");
        noop.RecordingTerminal("t", "r", "s");
        noop.LongPollCompleted("t", "k", 1, 2, false);
        noop.Flush();
        Assert.Null(noop.ResolveTraceId("r"));
    }

    [Fact]
    public void Retention_ActiveTracePreserved_TerminalTraceEvictedAfterTtl()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "retention-ttl.jsonl"));
        using var tracer = new RecordingPerformanceTracer(writer, () => now, () => 0,
            terminalTtl: TimeSpan.FromMinutes(1), maxContexts: 100);

        tracer.IntentAccepted("trace_active", "recordings");
        tracer.CorrelationSet("trace_active", "rec_active", "conf_active");

        tracer.IntentAccepted("trace_terminal", "recordings");
        tracer.CorrelationSet("trace_terminal", "rec_terminal", "conf_terminal");
        tracer.IntentValidated("trace_terminal", "recordings", success: false, errorCode: "TEST");

        Assert.Equal(2, tracer.TraceContextCount);
        Assert.True(tracer.HasValidationResult("trace_terminal"));
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));
        Assert.Equal("trace_terminal", tracer.ResolveTraceId(null, "conf_terminal"));

        now += TimeSpan.FromMinutes(2);
        tracer.RunCleanup();

        Assert.Equal(1, tracer.TraceContextCount);
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));
        Assert.Null(tracer.ResolveTraceId(null, "conf_terminal"));
    }

    [Fact]
    public void Retention_CapacityEvictsOldestTerminalContexts()
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "retention-cap.jsonl"));
        using var tracer = new RecordingPerformanceTracer(writer, () => now, () => 0,
            terminalTtl: TimeSpan.FromHours(1), maxContexts: 3);

        tracer.IntentAccepted("trace_active", "recordings");
        tracer.CorrelationSet("trace_active", "rec_active", "conf_active");

        for (int i = 0; i < 4; i++)
        {
            tracer.IntentAccepted($"trace_term_{i}", "recordings");
            tracer.CorrelationSet($"trace_term_{i}", $"rec_{i}", $"conf_{i}");
            tracer.RecordingTerminal($"trace_term_{i}", $"rec_{i}", status: "failed");
        }

        // Cleanup is triggered automatically as contexts exceed the capacity limit.
        Assert.True(tracer.TraceContextCount <= tracer.MaxContexts);
        // Active trace must survive capacity pressure.
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));

        tracer.RunCleanup();

        Assert.True(tracer.TraceContextCount <= tracer.MaxContexts);
        Assert.Equal("trace_active", tracer.ResolveTraceId("rec_active"));
    }

    [Fact]
    public void GenerateReportSample_WritesSanitizedJsonl()
    {
        var samplePath = Path.Combine(_tmp.Path, "perf", "performance-trace-sample.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(samplePath)!);
        if (File.Exists(samplePath)) File.Delete(samplePath);

        var writer = new RollingJsonlWriter(samplePath);
        var now = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
        long ticks = 1000;
        using var tracer = new RecordingPerformanceTracer(writer, () => now, () => Interlocked.Increment(ref ticks));

        tracer.IntentAccepted("trace_report_sample", "recordings", "2026-07-15T09:59:59.900Z");
        tracer.CorrelationSet("trace_report_sample", "rec_report_sample", "conf_report_sample", "display");
        tracer.IntentValidated("trace_report_sample", "recordings", success: true);
        tracer.ConfirmationCreated("trace_report_sample", "rec_report_sample", "conf_report_sample");
        tracer.ConfirmationApproved("trace_report_sample", "rec_report_sample", "conf_report_sample");
        tracer.CaptureStartRequested("trace_report_sample", "rec_report_sample", "ffmpeg");
        tracer.CaptureBackendStartReturned("trace_report_sample", "rec_report_sample", "ffmpeg");
        tracer.RecordingTerminal("trace_report_sample", "rec_report_sample", "completed", stopReason: "duration_reached");

        writer.Flush();

        Assert.True(File.Exists(samplePath));
        var lines = ReadAllLines(samplePath);
        Assert.True(lines.Count >= 6);
        Assert.All(lines, l => JsonNode.Parse(l));
    }

    [Fact]
    public void SetEnsureContextAssociation_PersistsAcrossSubsequentEvents()
    {
        var writer = new RollingJsonlWriter(Path.Combine(_tmp.Path, "perf", "ensure-assoc.jsonl"));
        using var tracer = CreateTracer(writer);

        tracer.SetEnsureContextAssociation("trace_assoc", new EnsureContextAssociation
        {
            StartupKind = "cold",
            EnsureElapsedMs = 842,
            ServiceStartupElapsedMs = 164,
            Status = EnsureContextStatus.Consumed
        });
        tracer.IntentAccepted("trace_assoc", "recordings");
        tracer.IntentValidated("trace_assoc", "recordings", success: true);
        tracer.CorrelationSet("trace_assoc", "rec_assoc");
        tracer.RecordingTerminal("trace_assoc", "rec_assoc", "completed");
        writer.Flush();

        var lines = ReadAllLines(writer.BasePath);
        Assert.True(lines.Count >= 3);
        var all = lines.Select(line => JsonNode.Parse(line)).ToList();

        Assert.All(all, e =>
        {
            Assert.Equal("cold", e!["startup_kind"]?.GetValue<string>());
            Assert.Equal(842L, e["ensure_elapsed_ms"]?.GetValue<long>());
            Assert.Equal(164L, e["service_startup_elapsed_ms"]?.GetValue<long>());
            Assert.Equal("consumed", e["ensure_context_status"]?.GetValue<string>());
        });

        var text = string.Join("\n", lines);
        Assert.DoesNotContain("ensure_context_id", text);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("ensure_[0-9a-f]{32}"), text);
        Assert.DoesNotContain("X-Agent-Recorder-Ensure-Context", text);
    }

    private static List<string> ReadAllLines(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }
}
