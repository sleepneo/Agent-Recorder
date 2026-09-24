using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class StandingPlanApiTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "AgentRecorderStandingPlanApi_" + Guid.NewGuid().ToString("N"));
    private ApiServer? _server;

    [Fact]
    public void ParserRejectsUnsupportedTargetDetailsAndNonZeroCountdown()
    {
        var body = ValidBody()
            .Replace("\"countdown_seconds\": 0", "\"countdown_seconds\": 1", StringComparison.Ordinal);
        var exception = Assert.Throws<ApiException>(() => StandingPlanApiRequestParser.Parse(body, "key-1"));
        Assert.Equal("INVALID_ARGUMENT", exception.Code);

        var unsupported = ValidBody().Replace(
            "\"type\": \"fixed_region\"",
            "\"type\": \"fixed_region\", \"x\": 0",
            StringComparison.Ordinal);
        var unsupportedException = Assert.Throws<ApiException>(() => StandingPlanApiRequestParser.Parse(unsupported, "key-1"));
        Assert.Equal("INVALID_ARGUMENT", unsupportedException.Code);
    }

    [Fact]
    public void ParserRequiresTheFullDurationAfterLatestStartAndMapsTimeOverflowTo400()
    {
        var now = DateTimeOffset.UtcNow;
        var exact = BuildBody(
            now.AddMinutes(1),
            now.AddMinutes(2),
            now.AddMinutes(3),
            durationSeconds: 60,
            now.AddMinutes(10));
        Assert.NotNull(StandingPlanApiRequestParser.Parse(exact, "exact-key"));

        var late = BuildBody(
            now.AddMinutes(1),
            now.AddMinutes(2),
            now.AddMinutes(2).AddSeconds(59),
            durationSeconds: 60,
            now.AddMinutes(10));
        var lateException = Assert.Throws<ApiException>(() => StandingPlanApiRequestParser.Parse(late, "late-key"));
        Assert.Equal(400, lateException.Status);
        Assert.Equal("INVALID_ARGUMENT", lateException.Code);

        var maximum = DateTimeOffset.MaxValue;
        var overflow = BuildBody(
            maximum.AddMinutes(-10),
            maximum.AddSeconds(-2),
            maximum.AddSeconds(-1),
            durationSeconds: 60,
            maximum);
        var overflowException = Assert.Throws<ApiException>(() => StandingPlanApiRequestParser.Parse(overflow, "overflow-key"));
        Assert.Equal(400, overflowException.Status);
        Assert.Equal("INVALID_ARGUMENT", overflowException.Code);
    }

    [Fact]
    public async Task PostPlanRequiresIdempotencyAndReturnsAcceptedSetupProjection()
    {
        var gateway = new FakeGateway();
        _server = CreateServer(gateway);
        _server.Start();

        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        var content = new StringContent(ValidBody(), Encoding.UTF8, "application/json");

        var missing = await client.PostAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/plans", content);
        Assert.Equal(400, (int)missing.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ApiServer.Port}/api/v1/plans")
        {
            Content = new StringContent(ValidBody(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "plan-key-1");
        var accepted = await client.SendAsync(request);
        Assert.Equal(202, (int)accepted.StatusCode);
        using var acceptedDocument = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var data = acceptedDocument.RootElement.GetProperty("data");
        Assert.Equal("setup_pending", data.GetProperty("status").GetString());
        Assert.Equal("local_region_selection", data.GetProperty("next_action").GetString());
        Assert.True(data.GetProperty("requires_local_action").GetBoolean());
        Assert.Equal(JsonValueKind.Number, data.GetProperty("status_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("run_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("recording_status_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("started_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("completed_at").ValueKind);
        Assert.Equal(1, gateway.CreateCalls);

        using var status = await client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/plan-setups/standing-setup-test-1?since_status=setup_pending&wait_ms=10");
        Assert.Equal(200, (int)status.StatusCode);
        using var statusDocument = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("standing-setup-test-1", statusDocument.RootElement.GetProperty("data").GetProperty("setup_intent_id").GetString());
    }

    [Fact]
    public async Task HeadlessPlanCreationIsRejectedBeforePersistenceAndCapabilitiesStayHonest()
    {
        _server = CreateServer(gateway: null);
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "headless-key");

        using var response = await client.PostAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/plans",
            new StringContent(ValidBody(), Encoding.UTF8, "application/json"));
        Assert.Equal(409, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INTERACTIVE_DESKTOP_REQUIRED", body, StringComparison.Ordinal);

        using var capabilities = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
        using var capabilitiesDocument = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        var unattended = capabilitiesDocument.RootElement.GetProperty("data").GetProperty("unattended_lease");
        Assert.False(unattended.GetProperty("supported").GetBoolean());
        Assert.Equal("fixed_region", unattended.GetProperty("supported_targets")[0].GetString());
        Assert.False(unattended.GetProperty("profile_crud_supported").GetBoolean());
        Assert.False(unattended.GetProperty("execution_supported").GetBoolean());
    }

    [Fact]
    public async Task PlanStatusRejectsMalformedWaitAndSinceVersionParameters()
    {
        var gateway = new FakeGateway();
        _server = CreateServer(gateway);
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        foreach (var query in new[]
        {
            "wait_ms=not-a-number",
            "since_version=-1",
            "since_status_version=bad",
            "since_status_version=9223372036854775808",
            "since_status_version=v1:0:0:0:0:0",
        })
        {
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/plan-setups/standing-setup-test-1?{query}");
            Assert.Equal(400, (int)response.StatusCode);
            Assert.Contains("INVALID_ARGUMENT", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PlanStatusProjectsCurrentRunFieldsAndLeavesRestartedRunUrlStale()
    {
        var gateway = new FakeGateway();
        gateway.SetState(new StandingPlanSetupState(
            "standing-setup-test-1",
            "recording",
            12,
            "plan-1",
            "occ-1",
            "lease-1",
            false,
            "natural_wake",
            null,
            "run-1",
            "/api/v1/recordings/run-1",
            new DateTimeOffset(2035, 1, 1, 0, 0, 2, TimeSpan.Zero),
            null,
            "v1:2:1:3:4:2"));
        _server = CreateServer(gateway);
        _server.Start();

        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        using var current = await client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/plan-setups/standing-setup-test-1");
        using var currentDocument = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        var currentData = currentDocument.RootElement.GetProperty("data");
        Assert.Equal("run-1", currentData.GetProperty("run_id").GetString());
        Assert.Equal("/api/v1/recordings/run-1", currentData.GetProperty("recording_status_url").GetString());
        Assert.Equal(JsonValueKind.String, currentData.GetProperty("started_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, currentData.GetProperty("completed_at").ValueKind);
        Assert.Equal(JsonValueKind.Number, currentData.GetProperty("status_version").ValueKind);

        gateway.SetState(currentData: new StandingPlanSetupState(
            "standing-setup-test-1",
            "session_interrupted",
            17,
            "plan-1",
            "occ-1",
            "lease-1",
            false,
            null,
            "restarted_without_engine_registry",
            "run-1",
            null,
            new DateTimeOffset(2035, 1, 1, 0, 0, 2, TimeSpan.Zero),
            new DateTimeOffset(2035, 1, 1, 0, 0, 4, TimeSpan.Zero),
            "v1:2:1:4:5:3"));

        using var stale = await client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/plan-setups/standing-setup-test-1");
        using var staleDocument = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        var staleData = staleDocument.RootElement.GetProperty("data");
        Assert.Equal("run-1", staleData.GetProperty("run_id").GetString());
        Assert.Equal(JsonValueKind.Null, staleData.GetProperty("recording_status_url").ValueKind);
        Assert.Equal(JsonValueKind.String, staleData.GetProperty("completed_at").ValueKind);
    }

    [Fact]
    public async Task PlanStatusLongPollReturnsWhenOnlyChildVersionAdvances()
    {
        var gateway = new FakeGateway();
        gateway.SetState(new StandingPlanSetupState(
            "standing-setup-test-1",
            "recording",
            10,
            "plan-1",
            "occ-1",
            "lease-1",
            false,
            "natural_wake",
            null,
            "run-1",
            null,
            new DateTimeOffset(2035, 1, 1, 0, 0, 2, TimeSpan.Zero),
            null,
            "v1:2:1:3:3:3:1"));
        _server = CreateServer(gateway);
        _server.Start();

        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        var started = Stopwatch.GetTimestamp();
        var pending = client.GetAsync(
            $"http://127.0.0.1:{ApiServer.Port}/api/v1/plan-setups/standing-setup-test-1?since_status_version=v1:2:1:3:3:3:1&wait_ms=2000");
        await gateway.FirstGet.Task.WaitAsync(TimeSpan.FromSeconds(1));
        gateway.SetState(currentData: new StandingPlanSetupState(
            "standing-setup-test-1",
            "recording",
            11,
            "plan-1",
            "occ-1",
            "lease-1",
            false,
            "natural_wake",
            null,
            "run-1",
            null,
            new DateTimeOffset(2035, 1, 1, 0, 0, 2, TimeSpan.Zero),
            null,
            "v1:2:1:4:3:3:1"));

        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1500), $"Long poll took {elapsed.TotalMilliseconds:F1} ms.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(11, document.RootElement.GetProperty("data").GetProperty("status_version").GetInt64());
    }

    [Fact]
    public void StatusVersionNumericCursorIsMonotonicAndFailsClosedAtInvalidBounds()
    {
        var before = StandingPlanStatusVersionCursor.CreateNumeric(2, 1, 3, 3, 3, 1);
        var afterChildUpdate = StandingPlanStatusVersionCursor.CreateNumeric(2, 1, 4, 3, 3, 1);
        Assert.True(afterChildUpdate > before);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StandingPlanStatusVersionCursor.CreateNumeric(-1, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StandingPlanStatusVersionCursor.CreateNumeric(long.MaxValue, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StandingPlanStatusVersionCursor.CreateNumeric(long.MaxValue - 1, 2, 0, 0, 0, 0));
    }

    [Fact]
    public async Task SameIdempotencyKeyWithDifferentRequestReturnsConflict()
    {
        var gateway = new FakeGateway();
        _server = CreateServer(gateway);
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var first = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ApiServer.Port}/api/v1/plans")
        {
            Content = new StringContent(ValidBody(), Encoding.UTF8, "application/json"),
        };
        first.Headers.Add("Idempotency-Key", "same-key");
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(202, (int)firstResponse.StatusCode);

        using var second = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ApiServer.Port}/api/v1/plans")
        {
            Content = new StringContent(
                ValidBody()
                    .Replace("\"duration_seconds\": 60", "\"duration_seconds\": 59", StringComparison.Ordinal)
                    .Replace("\"max_duration_seconds\": 60", "\"max_duration_seconds\": 59", StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json"),
        };
        second.Headers.Add("Idempotency-Key", "same-key");
        using var secondResponse = await client.SendAsync(second);
        Assert.Equal(409, (int)secondResponse.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await secondResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        ApiKeyAuth.ResetForTesting(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
    }

    private ApiServer CreateServer(IStandingPlanSetupGateway? gateway)
    {
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTray();
        engine.SetTray(tray);
        return new ApiServer(engine, audit, tray, standingPlanSetupGateway: gateway);
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static string ValidBody()
    {
        var now = DateTimeOffset.UtcNow;
        return """
        {
          "recording_spec": {
            "source": { "type": "fixed_region" },
            "audio": { "mode": "none" },
            "duration_seconds": 60,
            "countdown_seconds": 0,
            "backend": "ffmpeg-region",
            "output": { "directory": "C:\\Recordings\\Agent", "filename": "one-shot.mp4" }
          },
          "schedule": {
            "kind": "once",
            "start_at": "2099-09-01T10:00:00+08:00",
            "latest_start_at": "2099-09-01T10:05:00+08:00",
            "planned_end_at": "2099-09-01T10:10:00+08:00"
          },
          "requested_authorization": {
            "mode": "standing_lease",
            "expires_at": "2099-09-01T11:00:00+08:00",
            "max_runs": 1,
            "max_duration_seconds": 60
          }
        }
        """
        .Replace("2099-09-01T10:00:00+08:00", now.AddMinutes(1).ToString("O"), StringComparison.Ordinal)
        .Replace("2099-09-01T10:05:00+08:00", now.AddMinutes(2).ToString("O"), StringComparison.Ordinal)
        .Replace("2099-09-01T10:10:00+08:00", now.AddMinutes(3).ToString("O"), StringComparison.Ordinal)
        .Replace("2099-09-01T11:00:00+08:00", now.AddMinutes(10).ToString("O"), StringComparison.Ordinal);
    }

    private static string BuildBody(
        DateTimeOffset start,
        DateTimeOffset latest,
        DateTimeOffset plannedEnd,
        int durationSeconds,
        DateTimeOffset expires) => JsonSerializer.Serialize(new
        {
            recording_spec = new
            {
                source = new { type = "fixed_region" },
                audio = new { mode = "none" },
                duration_seconds = durationSeconds,
                countdown_seconds = 0,
                backend = "ffmpeg-region",
                output = new { directory = @"C:\Recordings\Agent", filename = "one-shot.mp4" },
            },
            schedule = new
            {
                kind = "once",
                start_at = start.ToString("O"),
                latest_start_at = latest.ToString("O"),
                planned_end_at = plannedEnd.ToString("O"),
            },
            requested_authorization = new
            {
                mode = "standing_lease",
                expires_at = expires.ToString("O"),
                max_runs = 1,
                max_duration_seconds = durationSeconds,
            },
        });

    private sealed class FakeGateway : IStandingPlanSetupGateway
    {
        private readonly ConcurrentDictionary<string, StandingPlanSetupState> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, StandingPlanApiRequest> _requests = new(StringComparer.Ordinal);
        public int CreateCalls { get; private set; }
        public TaskCompletionSource<object?> FirstGet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInteractiveDesktopAvailable => true;
        public bool IsUnattendedEnabled => true;

        public StandingPlanSetupCreateResult CreateOrGet(StandingPlanApiRequest request)
        {
            CreateCalls++;
            if (_requests.TryGetValue(request.IdempotencyKey, out var previous) && previous != request)
            {
                return new StandingPlanSetupCreateResult(
                    StandingPlanSetupCreateStatus.Conflict,
                    "standing-setup-test-1", "setup_pending", 0, null, null, null,
                    "idempotency_conflict");
            }
            _requests.TryAdd(request.IdempotencyKey, request);
            var state = _states.GetOrAdd(request.IdempotencyKey, _ => new StandingPlanSetupState(
                "standing-setup-test-1", "setup_pending", 0, null, null, null,
                true, "local_region_selection", null));
            return new StandingPlanSetupCreateResult(
                _requests.Count == 1 ? StandingPlanSetupCreateStatus.Created : StandingPlanSetupCreateStatus.Existing,
                state.SetupIntentId, state.StatusCode, state.StatusVersion,
                state.PlanId, state.OccurrenceId, state.LeaseId, state.ReasonCode);
        }

        public StandingPlanSetupState? Get(string setupIntentId)
        {
            FirstGet.TrySetResult(null);
            return _states.Values.FirstOrDefault(state => state.SetupIntentId == setupIntentId);
        }

        public void SetState(StandingPlanSetupState currentData) =>
            _states[currentData.SetupIntentId] = currentData;
    }

    private sealed class HeadlessTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
