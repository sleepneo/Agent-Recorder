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
/// Deterministic concurrency tests for the strict ordering guarantee between
/// <c>capture.first_frame_observed</c> and <c>recording.terminal</c>.
/// </summary>
public class FirstFrameTerminalOrderingTests : IDisposable
{
    private readonly string _tmp;
    private readonly RollingJsonlWriter _writer;
    private readonly RecordingPerformanceTracer _tracer;

    public FirstFrameTerminalOrderingTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"first-frame-order-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task FirstFrameClaimedFirst_EnqueuedBeforeTerminal()
    {
        const string traceId = "trace_ff_first";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        var firstFrameEntered = new ManualResetEventSlim(false);
        var continueFirstFrame = new ManualResetEventSlim(false);
        var terminalWaiting = new ManualResetEventSlim(false);

        _tracer.BeforeFirstFrameEnqueueGateForTests = () =>
        {
            firstFrameEntered.Set();
            terminalWaiting.Wait();
            continueFirstFrame.Wait();
        };

        var evidence = new FirstFrameEvidence { FrameNumber = 1, TotalSizeBytes = 100, OutTimeUs = 0 };
        var firstFrameTask = Task.Run(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", evidence));

        Assert.True(firstFrameEntered.Wait(TimeSpan.FromSeconds(2)), "First-frame should enter the lifecycle lock");

        var terminalTask = Task.Run(() =>
        {
            terminalWaiting.Set();
            _tracer.RecordingTerminal(traceId, "rec_1", "completed");
        });

        // Deterministic handshake: wait until the terminal thread has reached
        // the lifecycle lock call before letting the first-frame thread leave
        // the critical section. No fixed scheduler delay is used.
        Assert.True(terminalWaiting.Wait(TimeSpan.FromSeconds(2)), "Terminal thread should have started");

        continueFirstFrame.Set();
        await Task.WhenAll(firstFrameTask, terminalTask);

        var events = ReadEvents();
        var ffIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        var termIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "recording.terminal");

        Assert.True(ffIdx >= 0, "First-frame event should be present");
        Assert.True(termIdx >= 0, "Terminal event should be present");
        Assert.True(ffIdx < termIdx, "First-frame must be enqueued before terminal");
    }

    [Fact]
    public async Task TerminalClaimedFirst_FirstFrameDropped()
    {
        const string traceId = "trace_term_first";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        var terminalEntered = new ManualResetEventSlim(false);
        var continueTerminal = new ManualResetEventSlim(false);
        var firstFrameWaiting = new ManualResetEventSlim(false);

        _tracer.BeforeTerminalEnqueueGateForTests = () =>
        {
            terminalEntered.Set();
            firstFrameWaiting.Wait();
            continueTerminal.Wait();
        };

        var terminalTask = Task.Run(() => _tracer.RecordingTerminal(traceId, "rec_1", "completed"));

        Assert.True(terminalEntered.Wait(TimeSpan.FromSeconds(2)), "Terminal should enter the lifecycle lock");

        var evidence = new FirstFrameEvidence { FrameNumber = 1, TotalSizeBytes = 100, OutTimeUs = 0 };
        var firstFrameTask = Task.Run(() =>
        {
            firstFrameWaiting.Set();
            _tracer.CaptureFirstFrameObserved(traceId, "rec_1", evidence);
        });

        // Deterministic handshake: wait until the first-frame thread has started
        // its lifecycle lock call before letting the terminal thread leave the
        // critical section. No fixed scheduler delay is used.
        Assert.True(firstFrameWaiting.Wait(TimeSpan.FromSeconds(2)), "First-frame thread should have started");

        continueTerminal.Set();
        await Task.WhenAll(firstFrameTask, terminalTask);

        var events = ReadEvents();
        var ffIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
        var termIdx = events.FindIndex(e => e["event"]?.GetValue<string>() == "recording.terminal");

        Assert.True(termIdx >= 0, "Terminal event should be present");
        Assert.Equal(-1, ffIdx);
    }

    [Fact]
    public async Task ConcurrentFirstFrameObservations_ExactlyOnce()
    {
        const string traceId = "trace_concurrent_ff";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        var evidence = new FirstFrameEvidence { FrameNumber = 1, TotalSizeBytes = 100, OutTimeUs = 0 };
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", evidence)))
            .ToArray();

        await Task.WhenAll(tasks);

        var events = ReadEvents();
        Assert.Single(events, e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");
    }
}
