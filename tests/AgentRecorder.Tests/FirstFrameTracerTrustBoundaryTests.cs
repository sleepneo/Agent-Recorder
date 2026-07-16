using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

public class FirstFrameTracerTrustBoundaryTests : IDisposable
{
    private readonly string _tmp;
    private readonly RollingJsonlWriter _writer;
    private readonly RecordingPerformanceTracer _tracer;

    public FirstFrameTracerTrustBoundaryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"first-frame-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _writer = new RollingJsonlWriter(Path.Combine(_tmp, "perf", "traces.jsonl"));
        _tracer = new RecordingPerformanceTracer(_writer);
    }

    public void Dispose()
    {
        _tracer.Dispose();
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static string ReadAllText(RollingJsonlWriter writer)
    {
        writer.Flush();
        return File.ReadAllText(writer.BasePath);
    }

    private static IEnumerable<JsonNode> ReadEvents(RecordingPerformanceTracer tracer, RollingJsonlWriter writer)
    {
        tracer.Flush();
        writer.Flush();
        if (!File.Exists(writer.BasePath)) return Array.Empty<JsonNode>();
        return File.ReadAllLines(writer.BasePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    [Fact]
    public void HardcodesEvidenceKind_AndDropsMaliciousBackendKind()
    {
        const string traceId = "trace_malicious_kind";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        var maliciousEvidence = new FirstFrameEvidence
        {
            EvidenceKind = "C:\\secret\\api-key.txt progress=continue frame=999",
            FrameNumber = 1,
            TotalSizeBytes = 100,
            OutTimeUs = 0
        };
        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", maliciousEvidence);

        var text = ReadAllText(_writer);
        Assert.Contains("\"evidence_kind\":\"ffmpeg_progress_frame_and_output_bytes\"", text);
        Assert.DoesNotContain("C:\\secret", text);
        Assert.DoesNotContain("api-key", text);
        Assert.DoesNotContain("progress=continue", text);
    }

    [Fact]
    public void InvalidFrameNumberZero_DoesNotConsumeSlot_LaterValidWritesOnce()
    {
        const string traceId = "trace_invalid_frame";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 0,
            TotalSizeBytes = 100,
            OutTimeUs = 0
        });

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 1,
            TotalSizeBytes = 200,
            OutTimeUs = 0
        });

        var events = ReadEvents(_tracer, _writer)
            .Where(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed")
            .ToList();

        Assert.Single(events);
        Assert.Equal(1, events[0]["data"]!["frame_number"]!.GetValue<long>());
        Assert.Equal(200, events[0]["data"]!["total_size_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void InvalidTotalSizeZero_DoesNotConsumeSlot_LaterValidWritesOnce()
    {
        const string traceId = "trace_invalid_size";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 1,
            TotalSizeBytes = 0,
            OutTimeUs = 0
        });

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 5,
            TotalSizeBytes = 300,
            OutTimeUs = 0
        });

        var events = ReadEvents(_tracer, _writer)
            .Where(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed")
            .ToList();

        Assert.Single(events);
        Assert.Equal(5, events[0]["data"]!["frame_number"]!.GetValue<long>());
        Assert.Equal(300, events[0]["data"]!["total_size_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void NegativeOutTimeUs_NormalizedToNull()
    {
        const string traceId = "trace_negative_out_time";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 1,
            TotalSizeBytes = 100,
            OutTimeUs = -12345
        });

        var evt = ReadEvents(_tracer, _writer)
            .FirstOrDefault(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed");

        Assert.NotNull(evt);
        Assert.Null(evt!["data"]!["out_time_us"]);
    }

    [Fact]
    public void NullEvidence_DoesNotThrow_DoesNotConsumeSlot()
    {
        const string traceId = "trace_null_evidence";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        var ex = Record.Exception(() => _tracer.CaptureFirstFrameObserved(traceId, "rec_1", null!));
        Assert.Null(ex);

        _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 1,
            TotalSizeBytes = 100,
            OutTimeUs = 0
        });

        var events = ReadEvents(_tracer, _writer)
            .Where(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed")
            .ToList();

        Assert.Single(events);
    }

    [Fact]
    public void MultipleValidObservations_AreExactlyOnce()
    {
        const string traceId = "trace_multiple_valid";
        _tracer.IntentAccepted(traceId, "/recordings");
        _tracer.CaptureStartRequested(traceId, "rec_1", "ffmpeg");

        for (int i = 0; i < 3; i++)
        {
            _tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
            {
                FrameNumber = i + 1,
                TotalSizeBytes = 100 * (i + 1),
                OutTimeUs = i
            });
        }

        var events = ReadEvents(_tracer, _writer)
            .Where(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed")
            .ToList();

        Assert.Single(events);
        Assert.Equal(1, events[0]["data"]!["frame_number"]!.GetValue<long>());
    }

    [Fact]
    public void TerminalContext_AfterCleanup_LateFirstFrameIgnored()
    {
        const string traceId = "trace_cleanup_tombstone";
        var writer = new RollingJsonlWriter(Path.Combine(_tmp, "perf2", "traces.jsonl"));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracer = new RecordingPerformanceTracer(writer, () => now, () => System.Diagnostics.Stopwatch.GetTimestamp(),
            terminalTtl: TimeSpan.FromSeconds(1), maxContexts: 100);

        tracer.IntentAccepted(traceId, "/recordings");
        tracer.CorrelationSet(traceId, "rec_1", "conf_1", "display");
        tracer.RecordingTerminal(traceId, "rec_1", status: "completed", stopReason: "duration_reached");

        // Advance time past the context TTL so RunCleanup evicts the context,
        // but leave the tombstone inside its TTL so it still blocks late events.
        now += TimeSpan.FromSeconds(2);
        tracer.RunCleanup();

        tracer.CaptureFirstFrameObserved(traceId, "rec_1", new FirstFrameEvidence
        {
            FrameNumber = 1,
            TotalSizeBytes = 100,
            OutTimeUs = 0
        });

        tracer.Flush();
        writer.Flush();
        var events = File.ReadAllLines(writer.BasePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .Where(e => e["event"]?.GetValue<string>() == "capture.first_frame_observed")
            .ToList();

        Assert.Empty(events);
        tracer.Dispose();
    }
}
