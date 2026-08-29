using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
public sealed class CreationRecordingWaitTests : IDisposable
{
    private sealed class ObservableFakeBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public bool StartCalled { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            StartCalled = true;
            cfg.CommandArgs = "fake args";
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }

        public void EmitFirstFrame()
            => FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "test_frame",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 0
            });
    }

    private sealed class ControllableTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;
        public int RequestConfirmationCallCount { get; private set; }
        public int RequestRegionSelectionCallCount { get; private set; }
        public bool RejectImmediately { get; set; }
        public ManualResetEventSlim ConfirmationRequested { get; } = new(false);
        public ManualResetEventSlim CountdownShown { get; } = new(false);
        private Action<ConfirmationDecision>? _decision;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation,
            Action<ConfirmationDecision> callback)
        {
            RequestConfirmationCallCount++;
            _decision = callback;
            ConfirmationRequested.Set();
            if (RejectImmediately)
                callback(ConfirmationDecision.Reject());
        }

        public void Approve()
            => _decision?.Invoke(ConfirmationDecision.Approve());

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            RequestRegionSelectionCallCount++;
            callback("selection_cancelled", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }

        public void SetPreparing(RecordingUiPresentation rec) { }

        // ITrayContext has no separate countdown interface; the engine exposes
        // countdown through SetCountdown in the concrete contract used below.
        public void SetCountdown(RecordingUiPresentation rec)
            => CountdownShown.Set();
    }

    private sealed class CountingSystemProvider
    {
        public int DisplayCalls;
        public int WindowCalls;
    }

    private string? _dataDir;
    private ApiServer? _server;
    private RecordingEngine? _engine;

    private (ApiServer Server, RecordingEngine Engine, ControllableTray Tray, ObservableFakeBackend Backend) CreateServer(
        bool useObservableBackend = false,
        RecordingPerformanceTracer? tracer = null)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"creation-wait-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, tracer);
        var tray = new ControllableTray();
        var backend = new ObservableFakeBackend();
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, useObservableBackend ? "ffmpeg" : "fake");
        var server = new ApiServer(engine, audit, tray, tracer: tracer);

        _engine = engine;
        _server = server;
        return (server, engine, tray, backend);
    }

    private static HttpClient CreateClient()
        => new(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data))
            throw new Xunit.Sdk.XunitException($"Expected data response, got HTTP {(int)response.StatusCode}: {body}");
        return data.Clone();
    }

    private static async Task<string> ReadErrorCode(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static void ConfigureOneDisplay(CountingSystemProvider? counts = null)
    {
        SystemQuery.SetDisplayProvider(() =>
        {
            if (counts != null) Interlocked.Increment(ref counts.DisplayCalls);
            return new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true,
                    new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            };
        });
        SystemQuery.SetActiveWindowProvider(() =>
        {
            if (counts != null) Interlocked.Increment(ref counts.WindowCalls);
            return null;
        });
    }

    [Fact]
    public async Task OmittedWaitFor_PreservesRawAndQuickCreationShapeAndDoesNotWait()
    {
        var (server, _, tray, _) = CreateServer();
        tray.RejectImmediately = true;
        ConfigureOneDisplay();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var rawResponse = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
            JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));
        var raw = await ReadData(rawResponse);
        Assert.Equal(200, (int)rawResponse.StatusCode);
        Assert.Equal("requires_user_confirmation", raw.GetProperty("status").GetString());
        Assert.True(raw.TryGetProperty("recording_id", out _));
        Assert.True(raw.TryGetProperty("confirmation_id", out _));
        Assert.False(raw.TryGetProperty("wait", out _));
        Assert.False(raw.TryGetProperty("recording", out _));

        var quickResponse = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
            JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
        var quick = await ReadData(quickResponse);
        Assert.Equal(200, (int)quickResponse.StatusCode);
        Assert.Equal("requires_user_confirmation", quick.GetProperty("status").GetString());
        Assert.Equal("primary_display", quick.GetProperty("quick").GetProperty("target_type").GetString());
        Assert.False(quick.TryGetProperty("wait", out _));
        Assert.False(quick.TryGetProperty("recording", out _));
    }

    [Fact]
    public async Task InvalidCreationWaitQuery_IsRejectedBeforeTargetOrUiSideEffects()
    {
        var (server, _, tray, _) = CreateServer();
        var counts = new CountingSystemProvider();
        SystemQuery.SetDisplayProvider(() =>
        {
            Interlocked.Increment(ref counts.DisplayCalls);
            throw new InvalidOperationException("display resolution must not run");
        });
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var raw = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings?wait_ms=1",
            JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"}}"));
        var quick = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick?wait_for=unsupported",
            JsonContent("{\"target\":{\"type\":\"primary_display\"}}"));

        Assert.Equal(400, (int)raw.StatusCode);
        Assert.Equal("INVALID_ARGUMENT", await ReadErrorCode(raw));
        Assert.Equal(400, (int)quick.StatusCode);
        Assert.Equal("INVALID_ARGUMENT", await ReadErrorCode(quick));
        Assert.Equal(0, counts.DisplayCalls);
        Assert.Equal(0, tray.RequestConfirmationCallCount);
        Assert.Equal(0, tray.RequestRegionSelectionCallCount);
    }

    [Fact]
    public async Task QuickSelectedRegionCancellation_DoesNotReturnSyntheticWait()
    {
        var (server, _, tray, _) = CreateServer();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick?wait_for=recording&wait_ms=25000",
            JsonContent("{\"target\":{\"type\":\"selected_region\"},\"duration_seconds\":60}"));
        var data = await ReadData(response);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("selection_cancelled", data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("quick").GetProperty("recording_created").GetBoolean());
        Assert.False(data.TryGetProperty("recording_id", out _));
        Assert.False(data.TryGetProperty("recording", out _));
        Assert.False(data.TryGetProperty("wait", out _));
        Assert.Equal(1, tray.RequestRegionSelectionCallCount);
    }

    [Fact]
    public async Task CreationWait_TimeoutReturnsCurrentPendingTruthAndPreservesCreationFields()
    {
        var (server, _, tray, backend) = CreateServer();
        ConfigureOneDisplay();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick?wait_for=recording&wait_ms=100",
            JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
        var data = await ReadData(response);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("pending_confirmation", data.GetProperty("status").GetString());
        Assert.True(data.TryGetProperty("recording_id", out _));
        Assert.True(data.TryGetProperty("confirmation_id", out _));
        Assert.True(data.TryGetProperty("summary", out _));
        Assert.True(data.TryGetProperty("performance_trace_id", out _));
        Assert.Equal("pending_confirmation", data.GetProperty("recording").GetProperty("status").GetString());
        Assert.Equal("recording", data.GetProperty("wait").GetProperty("wait_for").GetString());
        Assert.Equal(100, data.GetProperty("wait").GetProperty("requested_ms").GetInt32());
        Assert.True(data.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("goal_reached").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("terminal").GetBoolean());
        Assert.False(backend.StartCalled);
        Assert.Equal(1, tray.RequestConfirmationCallCount);
    }

    [Fact]
    public async Task CreationWait_RejectionReturnsTerminalImmediately()
    {
        var (server, _, tray, backend) = CreateServer();
        tray.RejectImmediately = true;
        ConfigureOneDisplay();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings?wait_for=recording&wait_ms=1",
            JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));

        var data = await ReadData(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("rejected", data.GetProperty("status").GetString());
        Assert.Equal("rejected", data.GetProperty("recording").GetProperty("status").GetString());
        Assert.True(data.GetProperty("wait").GetProperty("terminal").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("goal_reached").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        Assert.False(backend.StartCalled);
    }

    [Fact]
    public async Task CreationWait_BlocksThroughApprovalCountdownUntilTrustedFirstFrame()
    {
        var (server, engine, tray, backend) = CreateServer(useObservableBackend: true);
        ConfigureOneDisplay();
        engine.CountdownInterval = TimeSpan.FromMilliseconds(80);
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var requestTask = client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings?wait_for=recording&wait_ms=5000",
            JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":2,\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));

        Assert.True(tray.ConfirmationRequested.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(requestTask.IsCompleted);
        Assert.False(backend.StartCalled);

        tray.Approve();
        Assert.True(tray.CountdownShown.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(requestTask.IsCompleted);
        Assert.False(backend.StartCalled);

        Assert.True(SpinWait.SpinUntil(() => backend.StartCalled, TimeSpan.FromSeconds(2)));
        Assert.False(requestTask.IsCompleted);
        backend.EmitFirstFrame();

        var response = await requestTask;
        var data = await ReadData(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("recording", data.GetProperty("status").GetString());
        Assert.Equal("recording", data.GetProperty("recording").GetProperty("status").GetString());
        Assert.True(data.GetProperty("wait").GetProperty("goal_reached").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        Assert.False(data.GetProperty("wait").GetProperty("terminal").GetBoolean());
        Assert.True(data.TryGetProperty("confirmation_id", out _));
    }

    [Fact]
    public async Task Capabilities_PublishCreationWaitContract()
    {
        var (server, _, _, _) = CreateServer();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
        var data = await ReadData(response);
        var wait = data.GetProperty("interaction").GetProperty("creation_wait");

        Assert.True(wait.GetProperty("supported").GetBoolean());
        Assert.Equal(new[] { "/api/v1/recordings", "/api/v1/recordings/quick" },
            wait.GetProperty("endpoints").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "recording" },
            wait.GetProperty("wait_for").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(25000, wait.GetProperty("default_wait_ms").GetInt32());
        Assert.Equal(25000, wait.GetProperty("max_wait_ms").GetInt32());
        Assert.Equal("trusted_first_frame_or_terminal", wait.GetProperty("milestone").GetString());
    }

    [Fact]
    public void CreationWait_TraceUsesStableKindWithoutSensitiveFields()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"creation-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var tracePath = Path.Combine(temp, "recording-traces.jsonl");
        var writer = new RollingJsonlWriter(tracePath);
        using (var tracer = new RecordingPerformanceTracer(writer))
        {
            using var engine = new RecordingEngine(new AuditLogger(), tracer);
            var recording = new Recording
            {
                State = RecState.pending_confirmation,
                SourceType = "display",
                SourceTitle = "Test display",
                OutputPath = Path.Combine(temp, "private-output.mp4")
            };
            engine._recs[recording.Id] = recording;
            tracer.CorrelationSet("trace_creation", recording.Id, recording.ConfirmationId, recording.SourceType);

            engine.GetCreationRecordingWait(recording.Id, 30);
            writer.Flush();
            var lines = File.ReadAllLines(tracePath);
            var waitLine = lines.Single(line => line.Contains("long_poll.completed", StringComparison.Ordinal));

            Assert.Contains("\"kind\":\"creation_recording\"", waitLine);
            Assert.Contains("\"recording_id\":\"" + recording.Id + "\"", waitLine);
            Assert.DoesNotContain("private-output.mp4", waitLine);
            Assert.DoesNotContain("output_path", waitLine);
        }

        try { Directory.Delete(temp, recursive: true); } catch { }
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        try
        {
            if (_dataDir != null && Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        ApiKeyAuth.ResetForTesting(null);
    }
}
