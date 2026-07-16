using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that failures during first-frame or terminal lifecycle event
/// construction/serialization/enqueue are isolated inside the tracer and never
/// propagate to the recording flow or reorder events.
/// </summary>
public class FirstFrameTerminalFailureIsolationTests : IDisposable
{
    private readonly string _tmp;
    private readonly RollingJsonlWriter _writer;
    private readonly RecordingPerformanceTracer _tracer;

    public FirstFrameTerminalFailureIsolationTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"first-frame-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _writer = new RollingJsonlWriter(Path.Combine(_tmp, "perf", "traces.jsonl"));
        _tracer = new RecordingPerformanceTracer(_writer);
    }

    public void Dispose()
    {
        _tracer.Dispose();
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private List<JsonNode> ReadEvents()
    {
        _tracer.Flush();
        _writer.Flush();
        if (!File.Exists(_writer.BasePath)) return new List<JsonNode>();
        return File.ReadAllLines(_writer.BasePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private static FirstFrameEvidence Evidence => new()
    {
        FrameNumber = 1,
        TotalSizeBytes = 100,
        OutTimeUs = 0
    };

    [Fact]
    public void FirstFrameEnqueueThrows_CaptureFirstFrameObservedDoesNotThrow()
    {
        const string traceId = "trace_ff_fault";
        _tracer.BeforeFirstFrameEnqueueGateForTests = () => throw new InvalidOperationException("injected first-frame enqueue fault");

        var ex = Record.Exception(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", Evidence));

        Assert.Null(ex);
        var events = ReadEvents();
        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
    }

    [Fact]
    public async Task FirstFrameEnqueueThrows_TerminalStillCompletes_NoFirstFrameAfterTerminal()
    {
        const string traceId = "trace_ff_fault_then_terminal";
        _tracer.BeforeFirstFrameEnqueueGateForTests = () => throw new InvalidOperationException("injected first-frame enqueue fault");

        var firstFrameTask = Task.Run(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", Evidence));
        var terminalTask = Task.Run(() => _tracer.RecordingTerminal(traceId, "rec_1", "completed"));

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(firstFrameTask, terminalTask).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(ex);

        var events = ReadEvents();
        var ffIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        var termIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "recording.terminal");

        Assert.Equal(-1, ffIdx);
        Assert.True(termIdx >= 0, "Terminal event should still be written after first-frame failure");
    }

    [Fact]
    public void TerminalEnqueueThrows_RecordingTerminalDoesNotThrow()
    {
        const string traceId = "trace_term_fault";
        _tracer.BeforeTerminalEnqueueGateForTests = () => throw new InvalidOperationException("injected terminal enqueue fault");

        var ex = Record.Exception(() => _tracer.RecordingTerminal(traceId, "rec_1", "completed"));

        Assert.Null(ex);
        var events = ReadEvents();
        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "recording.terminal");
    }

    [Fact]
    public void TerminalEnqueueThrows_LateFirstFrameDropped()
    {
        const string traceId = "trace_term_fault_late_ff";
        _tracer.BeforeTerminalEnqueueGateForTests = () => throw new InvalidOperationException("injected terminal enqueue fault");

        var terminalEx = Record.Exception(() => _tracer.RecordingTerminal(traceId, "rec_1", "completed"));
        Assert.Null(terminalEx);

        var ffEx = Record.Exception(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", Evidence));
        Assert.Null(ffEx);

        var events = ReadEvents();
        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "recording.terminal");
    }

    [Fact]
    public void FaultInOneTrace_DoesNotBlockAnotherTrace()
    {
        const string traceA = "trace_faulty";
        const string traceB = "trace_healthy";

        var injected = false;
        _tracer.BeforeFirstFrameEnqueueGateForTests = () =>
        {
            // Only inject the fault for trace A; let trace B through.
            if (!injected)
            {
                injected = true;
                throw new InvalidOperationException("injected first-frame enqueue fault");
            }
        };

        var exA = Record.Exception(() => _tracer.CaptureFirstFrameObserved(traceA, "rec_a", Evidence));
        Assert.Null(exA);

        _tracer.CaptureStartRequested(traceB, "rec_b", "ffmpeg");
        _tracer.CaptureFirstFrameObserved(traceB, "rec_b", Evidence);
        _tracer.RecordingTerminal(traceB, "rec_b", "completed");

        var events = ReadEvents();
        Assert.DoesNotContain(events, e => e["trace_id"]?.GetValue<string>() == traceA && e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        Assert.Contains(events, e => e["trace_id"]?.GetValue<string>() == traceB && e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        Assert.Contains(events, e => e["trace_id"]?.GetValue<string>() == traceB && e["event"]?.GetValue<string>() == "recording.terminal");
    }

    [Fact]
    public void FirstFrameEnqueueThrows_RetryDoesNotProduceDuplicate()
    {
        const string traceId = "trace_ff_fault_retry";
        _tracer.BeforeFirstFrameEnqueueGateForTests = () => throw new InvalidOperationException("injected first-frame enqueue fault");

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", Evidence);
        _tracer.BeforeFirstFrameEnqueueGateForTests = null;
        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", Evidence);

        var events = ReadEvents();
        Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
    }
}
