using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Integration tests for the performance trace spine. Verifies end-to-end event
/// ordering, correlation, and persistence through the API and UI layers.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class PerformanceTraceIntegrationTests : IDisposable
{
    private sealed class CaptureAuditLogger : AuditLogger
    {
        public List<(DateTime Time, string Event, JsonElement Payload)> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            Events.Add((DateTime.UtcNow, evt, JsonDocument.Parse(json).RootElement));
            base.Log(evt, payload);
        }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public bool StartCalled { get; private set; }
        public bool ThrowOnStart { get; set; }
        public bool CompleteOnStart { get; set; }
        public CaptureConfig? LastConfig { get; private set; }
        private Action<int, OutputMeta>? _naturalExit;

        public void Start(CaptureConfig cfg)
        {
            StartCalled = true;
            LastConfig = cfg;
            cfg.CommandArgs = "fake args";
            if (ThrowOnStart)
                throw new InvalidOperationException("Simulated backend failure");

            if (CompleteOnStart)
                _naturalExit?.Invoke(0, new OutputMeta { SizeBytes = 1024, DurationSeconds = 60 });
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class ControllableTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;
        public bool SupportsFloatingStopButton => false;
        public bool SupportsTrayStop => false;
        public bool SupportsGlobalStopHotkey => false;
        public bool IsGlobalStopHotkeyRegistered => false;
        public string? GlobalStopHotkeyGesture => null;

        public enum DecisionMode { Approve, Reject, Timeout }

        public DecisionMode Mode { get; set; } = DecisionMode.Approve;
        public string? ApproveOutputDirectory { get; set; }
        public int RequestConfirmationCallCount { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            RequestConfirmationCallCount++;
            if (Mode == DecisionMode.Timeout)
                return;

            var decision = Mode == DecisionMode.Approve
                ? ConfirmationDecision.Approve(ApproveOutputDirectory)
                : ConfirmationDecision.Reject();
            callback(decision);
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private ApiServer? _server;
    private string? _dataDir;
    private CaptureAuditLogger? _audit;
    private RecordingEngine? _engine;
    private IPerformanceTracer? _tracer;
    private readonly FakeCaptureBackend _backend = new();

    private ApiServer CreateServer(ControllableTray tray, string? subDir = null,
        IEnsureContextStore? ensureContextStore = null, IPerformanceTracer? tracer = null,
        string? existingDataDir = null)
    {
        _dataDir = existingDataDir ?? Path.Combine(Path.GetTempPath(), $"perf-int-test-{Guid.NewGuid():N}");
        if (!string.IsNullOrEmpty(subDir))
            _dataDir = Path.Combine(_dataDir, subDir);
        if (existingDataDir == null)
            Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        _audit = new CaptureAuditLogger();
        _tracer = tracer ?? new RecordingPerformanceTracer(_dataDir);
        _engine = new RecordingEngine(_audit, _tracer);
        _engine.SetTray(tray);
        _backend.ThrowOnStart = false;
        _backend.CompleteOnStart = false;
        _engine.BackendFactory = _ => (_backend, "fake");
        _engine.ConfirmationTimeout = TimeSpan.FromSeconds(60);
        var actualStore = ensureContextStore ?? new EnsureContextStore(_dataDir);
        _server = new ApiServer(_engine, _audit, tray, tracer: _tracer, ensureContextStore: actualStore);
        return _server;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        try { (_tracer as IDisposable)?.Dispose(); } catch { }
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        if (_dataDir != null)
        {
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    private List<JsonNode> ReadTraceEvents()
    {
        var path = Path.Combine(_dataDir!, "perf", "recording-traces.jsonl");
        if (!File.Exists(path))
            return new List<JsonNode>();

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private IReadOnlyList<JsonNode> EventsForTrace(string traceId)
    {
        return ReadTraceEvents()
            .Where(e => e["trace_id"]?.GetValue<string>() == traceId)
            .OrderBy(e => e["elapsed_from_intent_ms"]?.GetValue<double>() ?? -1.0)
            .ToList();
    }

    private static int CountEvent(IEnumerable<JsonNode> events, string name)
        => events.Count(e => e["event"]?.GetValue<string>() == name);

    private static void AssertNoEvent(IEnumerable<JsonNode> events, string name)
        => Assert.DoesNotContain(events, e => e["event"]?.GetValue<string>() == name);

    private static void WriteReadyFile(string dataDir, int pid = 12345, string startedAt = "2024-01-01T00:00:00Z",
        string readyAt = "2024-01-01T00:00:01Z", long startupElapsedMs = 50)
    {
        var readyPath = Path.Combine(dataDir, "runtime", "ready.json");
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
            DataDir = dataDir,
            ApiKeyFile = Path.Combine(dataDir, "config", "api-key.txt"),
            AuditLogPath = Path.Combine(dataDir, "logs", "audit.jsonl"),
            ReadyFile = readyPath,
            NamedEvent = @"Local\AgentRecorderReady"
        };
        File.WriteAllText(readyPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }));
    }

    private static string CreateEnsureContext(string dataDir, string startupKind = "cold", long ensureElapsedMs = 100,
        int pid = 12345, string startedAt = "2024-01-01T00:00:00Z", string readyAt = "2024-01-01T00:00:01Z",
        long serviceStartupElapsedMs = 50, DateTime? createdAtUtc = null)
    {
        var contextId = EnsureContextStore.GenerateContextId();
        var context = new EnsureContext
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
        var store = new EnsureContextStore(dataDir);
        store.TryCreate(context);
        return contextId;
    }

    [Fact]
    public async Task CreateRecording_Approved_EmitsOrderedEvents()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            // Wait briefly for background trace writes.
            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.True(events.Count >= 4, $"Expected at least 4 events, got {events.Count}");

            var names = events.Select(e => e["event"]!.GetValue<string>()).ToList();
            Assert.Contains("intent.accepted", names);
            Assert.Contains("intent.validated", names);
            Assert.Contains("confirmation.created", names);
            Assert.Contains("confirmation.approved", names);
            Assert.Contains("capture.start_requested", names);
            Assert.Contains("capture.backend_start_returned", names);

            var approvedIdx = names.IndexOf("confirmation.approved");
            var requestedIdx = names.IndexOf("capture.start_requested");
            var returnedIdx = names.IndexOf("capture.backend_start_returned");
            Assert.True(approvedIdx < requestedIdx, "approval must precede capture start request");
            Assert.True(requestedIdx < returnedIdx, "capture start request must precede backend return");

            var traceId = events[0]["trace_id"]!.GetValue<string>();
            Assert.All(events, e => Assert.Equal(traceId, e["trace_id"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(events[^1]["recording_id"]?.GetValue<string>()));

            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(0, CountEvent(byTrace, "intent.failed"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.created"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.approved"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.rejected"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.expired"));
            Assert.Equal(1, CountEvent(byTrace, "capture.start_requested"));
            Assert.Equal(1, CountEvent(byTrace, "capture.backend_start_returned"));
            Assert.Equal(0, CountEvent(byTrace, "capture.backend_start_failed"));
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));

            var accepted = byTrace.First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("recordings", accepted["endpoint"]?.GetValue<string>());
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("completed", terminal["data"]!["status"]!.GetValue<string>());
            Assert.Equal("display", terminal["source_type"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_Rejected_NoCaptureEvents()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Reject };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            var names = events.Select(e => e["event"]!.GetValue<string>()).ToList();
            Assert.Contains("confirmation.rejected", names);
            Assert.Contains("recording.terminal", names);
            Assert.DoesNotContain("capture.start_requested", names);
            Assert.DoesNotContain("capture.backend_start_returned", names);

            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(0, CountEvent(byTrace, "intent.failed"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.created"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.approved"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.rejected"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.expired"));
            AssertNoEvent(byTrace, "capture.start_requested");
            AssertNoEvent(byTrace, "capture.backend_start_returned");
            AssertNoEvent(byTrace, "capture.backend_start_failed");
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("rejected", terminal["data"]!["status"]!.GetValue<string>());
            Assert.Null(terminal["data"]!["error_code"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_Expired_NoCaptureEvents()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Timeout };
        var server = CreateServer(tray);
        try
        {
            _engine!.ConfirmationTimeout = TimeSpan.FromMilliseconds(50);
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (_audit!.Events.Any(e => e.Event == "confirmation.expired"))
                    break;
                await Task.Delay(20);
            }

            await Task.Delay(100);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            var names = events.Select(e => e["event"]!.GetValue<string>()).ToList();
            Assert.Contains("confirmation.expired", names);
            Assert.Contains("recording.terminal", names);
            Assert.DoesNotContain("capture.start_requested", names);

            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(0, CountEvent(byTrace, "intent.failed"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.created"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.approved"));
            Assert.Equal(0, CountEvent(byTrace, "confirmation.rejected"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.expired"));
            AssertNoEvent(byTrace, "capture.start_requested");
            AssertNoEvent(byTrace, "capture.backend_start_returned");
            AssertNoEvent(byTrace, "capture.backend_start_failed");
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("expired", terminal["data"]!["status"]!.GetValue<string>());
        }
        finally
        {
            await Task.Delay(100);
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_BackendStartFailed_RecordsFailedEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.ThrowOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            var names = events.Select(e => e["event"]!.GetValue<string>()).ToList();
            Assert.Contains("capture.start_requested", names);
            Assert.Contains("capture.backend_start_failed", names);
            Assert.DoesNotContain("capture.backend_start_returned", names);

            var failed = events.First(e => e["event"]!.GetValue<string>() == "capture.backend_start_failed");
            Assert.Equal("backend_start_exception", failed["data"]!["error_code"]!.GetValue<string>());
            Assert.Equal("InvalidOperationException", failed["data"]!["error_type"]!.GetValue<string>());

            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(1, CountEvent(byTrace, "capture.start_requested"));
            Assert.Equal(1, CountEvent(byTrace, "capture.backend_start_failed"));
            AssertNoEvent(byTrace, "capture.backend_start_returned");
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("failed", terminal["data"]!["status"]!.GetValue<string>());
            Assert.Equal("backend_start_exception", terminal["data"]!["error_code"]!.GetValue<string>());
            Assert.Equal("unexpected_exit", terminal["data"]!["stop_reason"]!.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_PrimaryDisplay_SameTraceThroughRecording()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add("X-Agent-Sent-At", DateTime.UtcNow.AddMilliseconds(-50).ToString("O"));
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var traceId = doc.RootElement.GetProperty("data").GetProperty("performance_trace_id").GetString();
            Assert.False(string.IsNullOrEmpty(traceId));

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.All(events, e => Assert.Equal(traceId, e["trace_id"]!.GetValue<string>()));
            var names = events.Select(e => e["event"]!.GetValue<string>()).ToList();
            Assert.Contains("intent.accepted", names);
            Assert.Contains("intent.validated", names);
            Assert.Contains("capture.backend_start_returned", names);

            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
            var accepted = byTrace.First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("recordings.quick", accepted["endpoint"]?.GetValue<string>());
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("display", terminal["source_type"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task MaliciousClientSentAt_DoesNotLeakIntoJsonl()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var uniqueKey = "ar_test_key_" + Guid.NewGuid().ToString("N");
            var uniquePath = @"C:\Users\TestUser\SecretProject\Window Title: Top Secret";
            var maliciousHeader = $"2026-07-15T00:00:00Z {uniqueKey} {uniquePath}";
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Agent-Sent-At", maliciousHeader);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var text = File.ReadAllText(Path.Combine(_dataDir!, "perf", "recording-traces.jsonl"));
            Assert.DoesNotContain(uniqueKey, text);
            Assert.DoesNotContain(uniquePath, text);
            Assert.DoesNotContain("SecretProject", text);
            Assert.DoesNotContain("Top Secret", text);

            // Normal business behavior must be unchanged.
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("requires_user_confirmation", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task InvalidJson_RecordsIntentFailedAndNoRecordingTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("not valid json"));
            Assert.Equal(400, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("INVALID_ARGUMENT", failed["data"]!["error_code"]!.GetValue<string>());
            Assert.Null(failed["recording_id"]?.GetValue<string>());
            AssertNoEvent(events, "recording.terminal");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ConfigParserFailure_RecordsIntentFailedAndNoRecordingTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{}}"));
            Assert.Equal(400, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            AssertNoEvent(events, "recording.terminal");
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("INVALID_ARGUMENT", failed["data"]!["error_code"]!.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task PreflightBeforeConfirmationFailure_RecordsIntentFailedAndNoRecordingTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            // Force encoder-unavailable preflight failure.
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            {
                ffmpeg = null;
                ffprobe = null;
                return false;
            };

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(400, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            AssertNoEvent(events, "recording.terminal");
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("ENCODER_UNAVAILABLE", failed["data"]!["error_code"]!.GetValue<string>());
        }
        finally
        {
            RecordingPreflightChecker.EncoderProvider = RecordingPreflightChecker.DefaultEncoderProvider;
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickSelectedRegion_Cancelled_RecordsIntentFailedAndNoRecordingTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            AssertNoEvent(events, "recording.terminal");
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("display_unavailable", failed["data"]!["error_code"]!.GetValue<string>());

            var accepted = events.First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("recordings.quick", accepted["endpoint"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ApiResponse_Pending_IncludesRecordingIdTopLevel()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.True(data.TryGetProperty("recording_id", out var recordingIdProp));
            Assert.False(string.IsNullOrEmpty(recordingIdProp.GetString()));
            Assert.True(data.TryGetProperty("confirmation_id", out var confirmationIdProp));
            Assert.False(string.IsNullOrEmpty(confirmationIdProp.GetString()));
            Assert.True(data.TryGetProperty("summary", out var summaryProp));
            Assert.False(string.IsNullOrEmpty(summaryProp.GetProperty("recording_id").GetString()));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task LongPollConfirmation_RecordsCompletedEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            var createBody = await createResponse.Content.ReadAsStringAsync();
            using var createDoc = JsonDocument.Parse(createBody);
            var confirmationId = createDoc.RootElement.GetProperty("data").GetProperty("confirmation_id").GetString();

            var pollTask = client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/{confirmationId}?wait_ms=2000&since_status=pending");
            await Task.WhenAny(pollTask, Task.Delay(3000));

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Contains(events, e => e["event"]!.GetValue<string>() == "long_poll.completed");
            var lp = events.First(e => e["event"]!.GetValue<string>() == "long_poll.completed");
            Assert.Equal("confirmation", lp["data"]!["kind"]!.GetValue<string>());
            Assert.True(lp["data"]!["changed"]!.GetValue<bool>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void ConfirmationForm_OnShown_EmitsConfirmationShown()
    {
        var traceId = "trace_cf_test";
        var recordingId = "rec_cf";
        var confirmationId = "conf_cf";
        var events = new List<(string TraceId, string EventName)>();

        var fakeTracer = new FakeTracer(events);
        var summary = new
        {
            source = "display: primary",
            source_type = "display",
            source_title = "primary",
            audio = "No audio",
            duration = "30s",
            output = "out.mp4",
            nested_role = "none",
            recording_id = recordingId,
            confirmation_id = confirmationId,
            trace_id = traceId,
            timeout_seconds = 60,
            expires_at = "2026-01-01T00:00:00Z"
        };

        var item = new PendingConfirmationItem(confirmationId, recordingId, summary, _ => { }, 60);

        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ConfirmationForm(item, 1, 1, tracer: fakeTracer);
                form.Show();
                Application.DoEvents();
                form.CloseWithoutResult();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);

        Assert.Contains(events, e => e.EventName == "confirmation.shown" && e.TraceId == traceId);
    }

    [Fact]
    public void ConfirmationForm_Constructor_DoesNotEmitShown()
    {
        var events = new List<(string TraceId, string EventName)>();
        var fakeTracer = new FakeTracer(events);
        var item = new PendingConfirmationItem(
            "conf_c", "rec_c",
            new { source = "test", recording_id = "rec_c", confirmation_id = "conf_c", trace_id = "trace_c", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
            _ => { }, 60);

        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ConfirmationForm(item, 1, 1, tracer: fakeTracer);
                // Do not call Show() / OnShown().
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);

        Assert.DoesNotContain(events, e => e.EventName == "confirmation.shown");
    }

    [Fact]
    public void TraceJson_DoesNotContainApiKeyOrFullPath()
    {
        var tmp = _tmpDir();
        ApiKeyAuth.InitializeForTesting(tmp.Path);
        try
        {
            var writer = new RollingJsonlWriter(Path.Combine(tmp.Path, "perf", "sanitize.jsonl"));
            using var tracer = new RecordingPerformanceTracer(writer);
            tracer.IntentAccepted("trace_s", "recordings");
            tracer.CorrelationSet("trace_s", "rec_s", "conf_s");
            tracer.RecordingTerminal("trace_s", "rec_s", "failed", errorCode: "SOME_ERROR");
            writer.Flush();

            var lines = File.ReadAllLines(writer.BasePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.True(lines.Count >= 1);
            var text = string.Join("\n", lines);
            Assert.DoesNotContain(ApiKeyAuth.CurrentApiKey, text);
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ApiKeyAuth.ResetForTesting(null);
        }
    }

    [Fact]
    public async Task OutputDirectoryOverrideFailure_RecordsConfirmationRejectedAndTerminal()
    {
        var tray = new ControllableTray
        {
            Mode = ControllableTray.DecisionMode.Approve,
            ApproveOutputDirectory = @"C:\Windows\perf-override-fail"
        };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var recordingId = doc.RootElement.GetProperty("data").GetProperty("recording_id").GetString();
            Assert.False(string.IsNullOrEmpty(recordingId));

            // Wait briefly for the synchronous confirmation callback to finish.
            await Task.Delay(100);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Equal(1, CountEvent(byTrace, "intent.accepted"));
            Assert.Equal(1, CountEvent(byTrace, "intent.validated"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.created"));
            Assert.Equal(1, CountEvent(byTrace, "confirmation.rejected"));
            Assert.Equal(1, CountEvent(byTrace, "recording.terminal"));
            var terminal = byTrace.Last(e => e["event"]!.GetValue<string>() == "recording.terminal");
            Assert.Equal("rejected", terminal["data"]!["status"]!.GetValue<string>());
            Assert.Equal("directory_override_failed", terminal["data"]!["error_code"]!.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_MissingTarget_RecordsIntentFailedAndNoTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"duration_seconds\":60}"));
            Assert.Equal(400, (int)response.StatusCode);

            await Task.Delay(100);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            AssertNoEvent(events, "recording.terminal");

            var accepted = events.First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("recordings.quick", accepted["endpoint"]?.GetValue<string>());
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("INVALID_ARGUMENT", failed["data"]!["error_code"]!.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_AudioRejected_RecordsIntentFailedAndNoTerminal()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"audio\":{\"microphone\":{\"enabled\":true}},\"duration_seconds\":60}"));
            Assert.Equal(400, (int)response.StatusCode);

            await Task.Delay(100);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, CountEvent(events, "intent.accepted"));
            Assert.Equal(1, CountEvent(events, "intent.failed"));
            AssertNoEvent(events, "recording.terminal");

            var accepted = events.First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("recordings.quick", accepted["endpoint"]?.GetValue<string>());
            var failed = events.First(e => e["event"]!.GetValue<string>() == "intent.failed");
            Assert.Equal("CAPABILITY_NOT_IMPLEMENTED", failed["data"]!["error_code"]!.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task LongPollConfirmation_Timeout_RecordsCompletedEvent()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            var createBody = await createResponse.Content.ReadAsStringAsync();
            using var createDoc = JsonDocument.Parse(createBody);
            var confirmationId = createDoc.RootElement.GetProperty("data").GetProperty("confirmation_id").GetString();

            var pollResponse = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/{confirmationId}?wait_ms=100&since_status=approved");
            Assert.Equal(200, (int)pollResponse.StatusCode);

            await Task.Delay(100);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            Assert.Contains(events, e => e["event"]!.GetValue<string>() == "long_poll.completed");
            var lp = events.Last(e => e["event"]!.GetValue<string>() == "long_poll.completed");
            Assert.Equal("confirmation", lp["data"]!["kind"]!.GetValue<string>());
            Assert.False(lp["data"]!["changed"]!.GetValue<bool>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_WithValidColdEnsureContext_AssociatesStartupFieldsAndConsumesContext()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!);
            var contextId = CreateEnsureContext(_dataDir!, "cold", 842, 12345, serviceStartupElapsedMs: 164);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var tracePath = Path.Combine(_dataDir!, "perf", "recording-traces.jsonl");
            var traceText = File.ReadAllText(tracePath);
            Assert.DoesNotContain(contextId, traceText);
            Assert.DoesNotContain(EnsureContextStore.HeaderName, traceText);
            Assert.DoesNotContain("ensure-contexts", traceText);

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.All(byTrace, e =>
            {
                Assert.Equal("cold", e["startup_kind"]?.GetValue<string>());
                Assert.Equal(842L, e["ensure_elapsed_ms"]?.GetValue<long>());
                Assert.Equal(164L, e["service_startup_elapsed_ms"]?.GetValue<long>());
                Assert.Equal("consumed", e["ensure_context_status"]?.GetValue<string>());
            });

            Assert.False(File.Exists(Path.Combine(_dataDir!, "runtime", "ensure-contexts", $"{contextId}.json")));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task QuickRecording_WithValidWarmEnsureContext_AssociatesStartupFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!);
            var contextId = CreateEnsureContext(_dataDir!, "warm", 120, 12345, serviceStartupElapsedMs: 200);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.All(byTrace, e =>
            {
                Assert.Equal("warm", e["startup_kind"]?.GetValue<string>());
                Assert.Equal(120L, e["ensure_elapsed_ms"]?.GetValue<long>());
                Assert.Equal(200L, e["service_startup_elapsed_ms"]?.GetValue<long>());
                Assert.Equal("consumed", e["ensure_context_status"]?.GetValue<string>());
            });
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_WithInvalidEnsureContext_RecordingSucceedsButNoTrustedStartupFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, "ensure_../ready.json");

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var traceText = File.ReadAllText(Path.Combine(_dataDir!, "perf", "recording-traces.jsonl"));
            Assert.DoesNotContain("ensure_../ready.json", traceText);
            Assert.DoesNotContain("\"startup_kind\":\"cold\"", traceText);
            Assert.DoesNotContain("\"startup_kind\":\"warm\"", traceText);

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var accepted = EventsForTrace(traceId).First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("invalid", accepted["ensure_context_status"]?.GetValue<string>());
            Assert.Null(accepted["startup_kind"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_WithoutEnsureContextHeader_OldFlowUnchanged()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            var traceText = File.ReadAllText(Path.Combine(_dataDir!, "perf", "recording-traces.jsonl"));
            Assert.DoesNotContain("\"startup_kind\":\"cold\"", traceText);
            Assert.DoesNotContain("\"startup_kind\":\"warm\"", traceText);

            var events = ReadTraceEvents();
            Assert.Contains(events, e => e["event"]!.GetValue<string>() == "intent.accepted");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task UnauthenticatedRequest_DoesNotConsumeValidContext()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!);
            var contextId = CreateEnsureContext(_dataDir!, "cold", 50, 12345);
            var contextPath = Path.Combine(_dataDir!, "runtime", "ensure-contexts", $"{contextId}.json");

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(401, (int)response.StatusCode);

            Assert.True(File.Exists(contextPath), "Context must not be consumed before authentication");

            client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var authedResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)authedResponse.StatusCode);

            await Task.Delay(200);
            _tracer!.Flush();

            Assert.False(File.Exists(contextPath), "Context must be consumed after authenticated request");
            var events = ReadTraceEvents();
            var traceId = events.Last()["trace_id"]!.GetValue<string>();
            var accepted = EventsForTrace(traceId).First(e => e["event"]!.GetValue<string>() == "intent.accepted");
            Assert.Equal("consumed", accepted["ensure_context_status"]?.GetValue<string>());
            Assert.Equal("cold", accepted["startup_kind"]?.GetValue<string>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_ConcurrentSameEnsureContext_OnlyOneTraceHasTrustedFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };

        // Precreate the data directory so the gated store can be constructed before
        // the server starts and so CreateServer can wire it in as the active store.
        var dataDir = Path.Combine(Path.GetTempPath(), $"perf-int-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var innerStore = new EnsureContextStore(dataDir);
        var gatedStore = new GatedEnsureContextStore(innerStore);
        var server = CreateServer(tray, ensureContextStore: gatedStore, existingDataDir: dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(dataDir);
            var contextId = CreateEnsureContext(dataDir, "cold", 842, 12345, serviceStartupElapsedMs: 164);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);

            // Use recordings/quick with selected_region to exercise the real HTTP handler
            // and ensure-context consumption without entering the single-recording engine
            // concurrency guard. The mocked tray immediately reports display_unavailable,
            // so no UI, confirmation, or capture backend is started.
            var body = "{\"target\":{\"type\":\"selected_region\"}}";
            var task1 = client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent(body));
            var task2 = client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent(body));

            var responses = await Task.WhenAll(task1, task2);
            Assert.All(responses, r => Assert.Equal(200, (int)r.StatusCode));
            Assert.True(gatedStore.BothArrived, "Both handlers must have reached the consume point before release.");

            var traceIds = new List<string>();
            foreach (var response in responses)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseBody);
                var traceId = doc.RootElement.GetProperty("data").GetProperty("performance_trace_id").GetString();
                Assert.False(string.IsNullOrEmpty(traceId));
                traceIds.Add(traceId!);
            }
            Assert.Equal(2, traceIds.Distinct().Count());

            _tracer!.Flush();

            var trustedCount = 0;
            var nonTrustedStatuses = new List<string?>();
            foreach (var traceId in traceIds)
            {
                var accepted = EventsForTrace(traceId).FirstOrDefault(e => e["event"]?.GetValue<string>() == "intent.accepted");
                Assert.NotNull(accepted);
                var status = accepted!["ensure_context_status"]?.GetValue<string>();
                var kind = accepted["startup_kind"]?.GetValue<string>();
                var elapsed = accepted["ensure_elapsed_ms"]?.GetValue<long?>();
                var serviceElapsed = accepted["service_startup_elapsed_ms"]?.GetValue<long?>();
                if (status == "consumed" && kind == "cold" && elapsed == 842L && serviceElapsed == 164L)
                {
                    trustedCount++;
                }
                else
                {
                    Assert.Null(kind);
                    Assert.Null(elapsed);
                    Assert.Null(serviceElapsed);
                    nonTrustedStatuses.Add(status);
                }
            }
            Assert.Equal(1, trustedCount);
            Assert.Single(nonTrustedStatuses, s => s == "reused" || s == "missing" || s == "unavailable");
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Test-only wrapper that forces two concurrent TryConsume calls to arrive at
    /// the inner store at the same time, proving the API handlers overlap at the
    /// context consumption point.
    /// </summary>
    private sealed class GatedEnsureContextStore : IEnsureContextStore
    {
        private readonly IEnsureContextStore _inner;
        private readonly Barrier _barrier = new(2);
        private int _arrivedCount;

        public GatedEnsureContextStore(IEnsureContextStore inner)
        {
            _inner = inner;
        }

        public bool BothArrived { get; private set; }

        public string ContextDirectory => _inner.ContextDirectory;

        public string? TryCreate(EnsureContext context) => _inner.TryCreate(context);

        public EnsureContextResult TryConsume(string contextId)
        {
            Interlocked.Increment(ref _arrivedCount);
            var entered = _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            BothArrived = _arrivedCount >= 2;
            if (!entered)
                throw new TimeoutException("The second handler did not reach the consume gate within the timeout.");
            return _inner.TryConsume(contextId);
        }
    }

    private static async Task<(HttpResponseMessage Response, string TraceId)> PostQuickRecordingWithEnsureContextAsync(HttpClient client, string contextId)
    {
        client.DefaultRequestHeaders.Remove(EnsureContextStore.HeaderName);
        client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);
        var body = "{\"target\":{\"type\":\"selected_region\"}}";
        var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
            JsonContent(body));
        var responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var traceId = doc.RootElement.GetProperty("data").GetProperty("performance_trace_id").GetString();
        Assert.False(string.IsNullOrEmpty(traceId));
        return (response, traceId!);
    }

    private void AssertAcceptedEnsureContextStatus(string traceId, string expectedStatus, string contextId)
    {
        _tracer!.Flush();
        var accepted = EventsForTrace(traceId).First(e => e["event"]?.GetValue<string>() == "intent.accepted");
        Assert.Equal(expectedStatus, accepted["ensure_context_status"]?.GetValue<string>());
        Assert.Null(accepted["startup_kind"]?.GetValue<string>());
        Assert.Null(accepted["ensure_elapsed_ms"]?.GetValue<long?>());
        Assert.Null(accepted["service_startup_elapsed_ms"]?.GetValue<long?>());

        var traceJson = string.Join("\n", EventsForTrace(traceId).Select(e => e.ToJsonString()));
        Assert.DoesNotContain(contextId, traceJson);
        Assert.DoesNotContain(EnsureContextStore.HeaderName, traceJson);
        Assert.DoesNotContain(ApiKeyAuth.CurrentApiKey, traceJson);
    }

    [Fact]
    public async Task CreateRecording_ExpiredEnsureContext_StatusIsExpiredAndNoTrustedFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!);
            var contextId = CreateEnsureContext(_dataDir!, "cold", 842, 12345,
                serviceStartupElapsedMs: 164, createdAtUtc: DateTime.UtcNow - TimeSpan.FromMinutes(6));

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var (response, traceId) = await PostQuickRecordingWithEnsureContextAsync(client, contextId);
            Assert.Equal(200, (int)response.StatusCode);
            AssertAcceptedEnsureContextStatus(traceId, "expired", contextId);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_InstanceMismatchReadyAt_StatusIsInstanceMismatchAndNoTrustedFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!, readyAt: "2024-01-01T00:00:02Z");
            var contextId = CreateEnsureContext(_dataDir!, "cold", 842, 12345,
                readyAt: "2024-01-01T00:00:01Z", serviceStartupElapsedMs: 164);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var (response, traceId) = await PostQuickRecordingWithEnsureContextAsync(client, contextId);
            Assert.Equal(200, (int)response.StatusCode);
            AssertAcceptedEnsureContextStatus(traceId, "instance_mismatch", contextId);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_ReusedEnsureContext_StatusIsReusedAndNoTrustedFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };

        // Use a single store instance for both the pre-consumption and the API so
        // the in-process tombstone is visible to the server and the second request
        // receives the expected `reused` status.
        var dataDir = Path.Combine(Path.GetTempPath(), $"perf-int-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var sharedStore = new EnsureContextStore(dataDir);
        var server = CreateServer(tray, ensureContextStore: sharedStore, existingDataDir: dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(dataDir);
            var contextId = EnsureContextStore.GenerateContextId();
            var context = new EnsureContext
            {
                SchemaVersion = 1,
                EnsureContextId = contextId,
                ServicePid = 12345,
                ServiceStartedAt = "2024-01-01T00:00:00Z",
                ServiceReadyAt = "2024-01-01T00:00:01Z",
                StartupKind = "cold",
                EnsureElapsedMs = 842,
                ServiceStartupElapsedMs = 164,
                CreatedAtUtc = DateTime.UtcNow
            };
            Assert.Equal(contextId, sharedStore.TryCreate(context));
            Assert.Equal(EnsureContextStatus.Consumed, sharedStore.TryConsume(contextId).Status);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var (response, traceId) = await PostQuickRecordingWithEnsureContextAsync(client, contextId);
            Assert.Equal(200, (int)response.StatusCode);
            AssertAcceptedEnsureContextStatus(traceId, "reused", contextId);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_MissingEnsureContext_StatusIsMissingAndNoTrustedFields()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var server = CreateServer(tray);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(_dataDir!);
            var contextId = CreateEnsureContext(_dataDir!, "cold", 842, 12345, serviceStartupElapsedMs: 164);
            var contextPath = Path.Combine(_dataDir!, "runtime", "ensure-contexts", $"{contextId}.json");
            Assert.True(File.Exists(contextPath));
            File.Delete(contextPath);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var (response, traceId) = await PostQuickRecordingWithEnsureContextAsync(client, contextId);
            Assert.Equal(200, (int)response.StatusCode);
            AssertAcceptedEnsureContextStatus(traceId, "missing", contextId);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_EnsureContextStoreThrows_RecordingContinues()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var throwingStore = new ThrowingEnsureContextStore();
        var server = CreateServer(tray, ensureContextStore: throwingStore);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, EnsureContextStore.GenerateContextId());

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            _tracer!.Flush();

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Contains(byTrace, e => e["event"]?.GetValue<string>() == "intent.accepted");
            Assert.Contains(byTrace, e => e["event"]?.GetValue<string>() == "recording.terminal");
            var accepted = byTrace.First(e => e["event"]?.GetValue<string>() == "intent.accepted");
            Assert.Null(accepted["startup_kind"]?.GetValue<string>());
            Assert.Null(accepted["ensure_elapsed_ms"]?.GetValue<long?>());
            Assert.Null(accepted["service_startup_elapsed_ms"]?.GetValue<long?>());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task CreateRecording_EnsureContextAssociationThrows_RecordingContinues()
    {
        var tray = new ControllableTray { Mode = ControllableTray.DecisionMode.Approve };
        var dataDir = Path.Combine(Path.GetTempPath(), $"perf-int-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var realTracer = new RecordingPerformanceTracer(dataDir);
        var throwingTracer = new ThrowingOnAssociationTracer(realTracer);
        var server = CreateServer(tray, tracer: throwingTracer, existingDataDir: dataDir);
        _backend.CompleteOnStart = true;
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            WriteReadyFile(dataDir);
            var contextId = CreateEnsureContext(dataDir, "cold", 842, 12345, serviceStartupElapsedMs: 164);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            client.DefaultRequestHeaders.Add(EnsureContextStore.HeaderName, contextId);

            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
            Assert.Equal(200, (int)response.StatusCode);

            realTracer.Flush();

            var events = ReadTraceEvents();
            var traceId = events.First()["trace_id"]!.GetValue<string>();
            var byTrace = EventsForTrace(traceId);
            Assert.Contains(byTrace, e => e["event"]?.GetValue<string>() == "intent.accepted");
            Assert.Contains(byTrace, e => e["event"]?.GetValue<string>() == "recording.terminal");
            var accepted = byTrace.First(e => e["event"]?.GetValue<string>() == "intent.accepted");
            Assert.Null(accepted["startup_kind"]?.GetValue<string>());
            Assert.Null(accepted["ensure_elapsed_ms"]?.GetValue<long?>());
            Assert.Null(accepted["service_startup_elapsed_ms"]?.GetValue<long?>());
        }
        finally
        {
            server.Stop();
            (realTracer as IDisposable)?.Dispose();
        }
    }

    private sealed class FakeTracer : IPerformanceTracer
    {
        private readonly List<(string TraceId, string EventName)> _events;

        public FakeTracer(List<(string TraceId, string EventName)> events) => _events = events;

        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) => _events.Add((traceId, "intent.accepted"));
        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) => _events.Add((traceId, "ensure_context.associated"));
        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) => _events.Add((traceId, "intent.validated"));
        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) => _events.Add((traceId, "correlation.set"));
        public bool HasValidationResult(string traceId) => false;
        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) => _events.Add((traceId, "confirmation.created"));
        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) => _events.Add((traceId, "confirmation.shown"));
        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) => _events.Add((traceId, "confirmation.approved"));
        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) => _events.Add((traceId, "confirmation.rejected"));
        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) => _events.Add((traceId, "confirmation.expired"));
        public void CaptureStartRequested(string traceId, string recordingId, string backendType) => _events.Add((traceId, "capture.start_requested"));
        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) => _events.Add((traceId, "capture.backend_start_returned"));
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) => _events.Add((traceId, "capture.backend_start_failed"));
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) => _events.Add((traceId, "capture.first_frame_observed"));
        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null) => _events.Add((traceId, "recording.terminal"));
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) => _events.Add((traceId, "long_poll.completed"));
        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
    }

    private sealed class ThrowingEnsureContextStore : IEnsureContextStore
    {
        public string ContextDirectory => throw new InvalidOperationException("Simulated store failure");
        public string? TryCreate(EnsureContext context) => throw new InvalidOperationException("Simulated store failure");
        public EnsureContextResult TryConsume(string contextId) => throw new InvalidOperationException("Simulated store failure");
    }

    private sealed class ThrowingOnAssociationTracer : IPerformanceTracer
    {
        private readonly IPerformanceTracer _inner;

        public ThrowingOnAssociationTracer(IPerformanceTracer inner) => _inner = inner;

        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) => _inner.IntentAccepted(traceId, endpoint, clientSentAtUtc);
        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) => throw new InvalidOperationException("Simulated association failure");
        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) => _inner.IntentValidated(traceId, endpoint, success, errorCode);
        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) => _inner.CorrelationSet(traceId, recordingId, confirmationId, sourceType);
        public bool HasValidationResult(string traceId) => _inner.HasValidationResult(traceId);
        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) => _inner.ConfirmationCreated(traceId, recordingId, confirmationId);
        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) => _inner.ConfirmationShown(traceId, recordingId, confirmationId);
        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) => _inner.ConfirmationApproved(traceId, recordingId, confirmationId);
        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) => _inner.ConfirmationRejected(traceId, recordingId, confirmationId);
        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) => _inner.ConfirmationExpired(traceId, recordingId, confirmationId);
        public void CaptureStartRequested(string traceId, string recordingId, string backendType) => _inner.CaptureStartRequested(traceId, recordingId, backendType);
        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) => _inner.CaptureBackendStartReturned(traceId, recordingId, backendType);
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) => _inner.CaptureBackendStartFailed(traceId, recordingId, backendType, errorCode, errorType);
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) => _inner.CaptureFirstFrameObserved(traceId, recordingId, evidence);
        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null) => _inner.RecordingTerminal(traceId, recordingId, status, stopReason, errorCode);
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) => _inner.LongPollCompleted(traceId, kind, requestedWaitMs, actualWaitMs, changed, recordingId, confirmationId);
        public void Flush() => _inner.Flush();
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => _inner.ResolveTraceId(recordingId, confirmationId);
    }

    private TempDirectory _tmpDir() => new TempDirectory();
}
