using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

public class RecordingEngineFirstFrameTracerTests
{
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

    private sealed class FakeObservableBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public Action? OnStart { get; set; }
        public Action? OnStop { get; set; }
        public bool Started { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            Started = true;
            cfg.CommandArgs = "fake-observable";
            OnStart?.Invoke();
        }

        public OutputMeta Stop()
        {
            OnStop?.Invoke();
            return new();
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => -1;
        public void Dispose() { }

        public void Emit(FirstFrameObservation obs)
        {
            try
            {
                FirstFrameObserved?.Invoke(obs);
            }
            catch
            {
                // Simulate production backend isolation: observer exceptions must
                // not propagate out of the backend and fail the recording.
            }
        }
    }

    private sealed class FakeNonObservableBackend : ICaptureBackend
    {
        public bool Started { get; private set; }
        public void Start(CaptureConfig cfg)
        {
            Started = true;
            cfg.CommandArgs = "fake-non-observable";
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => -1;
        public void Dispose() { }
    }

    /// <summary>
    /// Owns the temp directory, audit logger, tracer, writer, engine and recording
    /// for a single test. Disposes the tracer (which shuts down the writer) before
    /// deleting the temp directory so background tasks do not race with cleanup.
    /// </summary>
    private sealed class TestContext : IDisposable
    {
        public string TempDir { get; }
        public string AuditPath { get; }
        public RollingJsonlWriter Writer { get; }
        public RecordingPerformanceTracer Tracer { get; }
        public AuditLogger Audit { get; }
        public RecordingEngine Engine { get; }
        public Recording Recording { get; }
        public string TraceId { get; }
        public FakeObservableBackend? ObservableBackend { get; private set; }
        public FakeNonObservableBackend? NonObservableBackend { get; private set; }

        private TestContext(string traceId)
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"first-frame-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            AuditPath = Path.Combine(TempDir, "logs", "audit.jsonl");
            Audit = new AuditLogger(AuditPath);

            Writer = new RollingJsonlWriter(Path.Combine(TempDir, "perf", "traces.jsonl"));
            Tracer = new RecordingPerformanceTracer(Writer);

            Engine = new RecordingEngine(Audit, Tracer);
            Engine.SetTray(new NoOpTray());

            TraceId = traceId;

            Recording = new Recording
            {
                SourceType = "display",
                OutputPath = Path.Combine(TempDir, $"out-{Guid.NewGuid():N}.mp4"),
                Config = new CaptureConfig
                {
                    SourceKind = "display",
                    Bounds = (0, 0, 1920, 1080),
                    Fps = 30,
                    OutputPath = Path.Combine(TempDir, $"out-{Guid.NewGuid():N}.mp4")
                }
            };
        }

        public static TestContext Observable(string traceId)
        {
            var ctx = new TestContext(traceId);
            ctx.ObservableBackend = new FakeObservableBackend();
            ctx.Engine.BackendFactory = _ => (ctx.ObservableBackend, "fake-observable");
            return ctx;
        }

        public static TestContext NonObservable(string traceId)
        {
            var ctx = new TestContext(traceId);
            ctx.NonObservableBackend = new FakeNonObservableBackend();
            ctx.Engine.BackendFactory = _ => (ctx.NonObservableBackend, "fake-non-observable");
            return ctx;
        }

        public static TestContext TracerOnly(string traceId) => new(traceId);

        public void Dispose()
        {
            // Dispose the tracer first so its owned writer shuts down before we
            // delete the temp directory. The writer is not disposed separately to
            // keep ownership clear (tracer owns writer).
            try { Tracer.Dispose(); } catch { }
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Start_SubscribesBeforeStart_SynchronousObservationIsTraced()
    {
        using var ctx = TestContext.Observable("trace_test_123");
        ctx.ObservableBackend!.OnStart = () =>
        {
            ctx.ObservableBackend.Emit(new FirstFrameObservation
            {
                FrameNumber = 1,
                TotalSizeBytes = 1234,
                OutTimeUs = 0
            });
        };

        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray(), ctx.TraceId);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var evt = lines.Select(Parse).FirstOrDefault(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed");

        Assert.NotNull(evt);
        Assert.Equal(ctx.TraceId, evt!["trace_id"]!.GetValue<string>());
        Assert.Equal(ctx.Recording.Id, evt["recording_id"]!.GetValue<string>());
        Assert.Equal("fake-observable", evt["backend"]!.GetValue<string>());
        Assert.Equal("display", evt["source_type"]!.GetValue<string>());
        Assert.Equal("capture.first_frame_observed", evt["event"]!.GetValue<string>());

        AssertAuditInTempDir(ctx);
    }

    [Fact]
    public void DuplicateBackendObservations_OnlyOneTraceEvent()
    {
        using var ctx = TestContext.Observable("trace_dup_backend");
        ctx.ObservableBackend!.OnStart = () =>
        {
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 100 });
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 2, TotalSizeBytes = 200 });
        };

        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray(), ctx.TraceId);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).Where(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed").ToList();

        Assert.Single(events);
        AssertAuditInTempDir(ctx);
    }

    [Fact]
    public void DirectTracerDuplicateCalls_OnlyOneTraceEvent()
    {
        using var ctx = TestContext.TracerOnly("trace_dup");
        ctx.Tracer.CorrelationSet("trace_dup", "rec_dup", sourceType: "display");

        var evidence = new FirstFrameEvidence { FrameNumber = 1, TotalSizeBytes = 100 };
        ctx.Tracer.CaptureFirstFrameObserved("trace_dup", "rec_dup", evidence);
        ctx.Tracer.CaptureFirstFrameObserved("trace_dup", "rec_dup", evidence);
        ctx.Tracer.CaptureFirstFrameObserved("trace_dup", "rec_dup", evidence);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).Where(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed").ToList();

        Assert.Single(events);
    }

    [Fact]
    public void TerminalAlreadyRecorded_LateObservationIgnored()
    {
        using var ctx = TestContext.TracerOnly("trace_term");
        ctx.Tracer.CorrelationSet("trace_term", "rec_term", sourceType: "display");
        ctx.Tracer.RecordingTerminal("trace_term", "rec_term", "completed");

        var evidence = new FirstFrameEvidence { FrameNumber = 1, TotalSizeBytes = 100 };
        ctx.Tracer.CaptureFirstFrameObserved("trace_term", "rec_term", evidence);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).Where(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed").ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void NonObservableBackend_DoesNotProduceFirstFrameEvent()
    {
        using var ctx = TestContext.NonObservable("trace_none");

        ctx.Tracer.CorrelationSet("trace_none", ctx.Recording.Id);
        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray());

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).Where(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed").ToList();

        Assert.Empty(events);
        Assert.True(ctx.NonObservableBackend!.Started);
        AssertAuditInTempDir(ctx);
    }

    [Fact]
    public void FirstFrameObservationException_DoesNotFailRecording()
    {
        using var ctx = TestContext.Observable("trace_exception");
        ctx.ObservableBackend!.OnStart = () =>
        {
            ctx.ObservableBackend.FirstFrameObserved += _ => throw new InvalidOperationException("boom");
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 100 });
        };

        var ex = Record.Exception(() => ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray()));

        Assert.Null(ex);
        Assert.Equal(RecState.recording, ctx.Recording.State);
    }

    [Fact]
    public void Jsonl_DoesNotContainSensitiveFields()
    {
        using var ctx = TestContext.Observable("trace_sensitive");
        ctx.ObservableBackend!.OnStart = () =>
        {
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1234, OutTimeUs = 0 });
        };

        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray(), ctx.TraceId);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var json = string.Join("\n", lines);

        Assert.DoesNotContain(ctx.Recording.OutputPath, json);
        Assert.DoesNotContain("window_title", json);
        Assert.DoesNotContain("api_key", json);
        Assert.DoesNotContain("progress=continue", json);
    }

    [Fact]
    public void FirstFrameEventData_ContainsAllowedFieldsOnly()
    {
        using var ctx = TestContext.Observable("trace_fields");
        ctx.ObservableBackend!.OnStart = () =>
        {
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 7, TotalSizeBytes = 890, OutTimeUs = 12345 });
        };

        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray(), ctx.TraceId);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var evt = lines.Select(Parse).FirstOrDefault(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed");

        Assert.NotNull(evt);
        var data = evt!["data"];
        Assert.Equal("ffmpeg_progress_frame_and_output_bytes", data!["evidence_kind"]!.GetValue<string>());
        Assert.Equal(7, data["frame_number"]!.GetValue<long>());
        Assert.Equal(890, data["total_size_bytes"]!.GetValue<long>());
        Assert.Equal(12345, data["out_time_us"]!.GetValue<long>());
    }

    [Fact]
    public void ConfirmationApproved_PrecedesFirstFrame_NoEventForRejectedOrExpired()
    {
        using var ctx = TestContext.Observable("trace_conf_123");
        var rec = ctx.Recording;
        rec.ConfirmationId = "conf_123";

        ctx.Tracer.CorrelationSet(ctx.TraceId, rec.Id, confirmationId: "conf_123", sourceType: "display");
        ctx.Tracer.ConfirmationCreated(ctx.TraceId, rec.Id, "conf_123");
        ctx.Tracer.ConfirmationApproved(ctx.TraceId, rec.Id, "conf_123");

        ctx.ObservableBackend!.OnStart = () => ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 100 });
        ctx.Engine.StartCaptureForTests(rec, new NoOpTray(), ctx.TraceId);

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).ToList();

        var approved = events.Find(e => e?["event"]?.GetValue<string>() == "confirmation.approved");
        var firstFrame = events.Find(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed");
        Assert.NotNull(approved);
        Assert.NotNull(firstFrame);
        Assert.True(approved!["elapsed_from_intent_ms"]!.GetValue<double>() <= firstFrame!["elapsed_from_intent_ms"]!.GetValue<double>());
        Assert.Equal("conf_123", firstFrame["confirmation_id"]!.GetValue<string>());
        AssertAuditInTempDir(ctx);
    }

    [Fact]
    public void Stop_BackendEmitsFirstFrameDuringStop_FirstFramePrecedesTerminal()
    {
        using var ctx = TestContext.Observable("trace_stop_order");

        // Simulate the real FfmpegCaptureBackend behavior: after the process exits,
        // the stdout drain emits the first-frame observation before Stop() returns.
        ctx.ObservableBackend!.OnStop = () =>
        {
            ctx.ObservableBackend.Emit(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 100 });
        };

        ctx.Engine.StartCaptureForTests(ctx.Recording, new NoOpTray(), ctx.TraceId);
        ctx.Engine.Stop(ctx.Recording.Id, "test_stop");

        ctx.Writer.Flush();
        var lines = ReadAllLines(ctx.Writer.BasePath);
        var events = lines.Select(Parse).ToList();

        var firstFrameIdx = events.FindIndex(e => e?["event"]?.GetValue<string>() == "capture.first_frame_observed");
        var terminalIdx = events.FindIndex(e => e?["event"]?.GetValue<string>() == "recording.terminal");

        Assert.True(firstFrameIdx >= 0, "First-frame event should be written");
        Assert.True(terminalIdx >= 0, "Terminal event should be written");
        Assert.True(firstFrameIdx < terminalIdx, "First-frame must precede recording.terminal");
    }

    /// <summary>
    /// Verifies that the audit file used by the test is located inside the test's
    /// own temp directory and that the default user audit path is not referenced.
    /// </summary>
    private static void AssertAuditInTempDir(TestContext ctx)
    {
        Assert.True(File.Exists(ctx.AuditPath), "Audit log should exist in test temp directory");
        var auditLines = ReadAllLines(ctx.AuditPath);
        Assert.NotEmpty(auditLines);
        Assert.DoesNotContain(ctx.AuditPath, Paths.AuditLogPath);
    }

    private static List<string> ReadAllLines(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }

    private static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch { return null; }
    }
}
