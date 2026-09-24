using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using static AgentRecorder.Tests.RecurringPlanSetupTestFixture;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RecurringPlanApiTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringPlanApi_" + Guid.NewGuid().ToString("N"));
    private ApiServer? _server;

    [Fact]
    public async Task PostPlansRoutesOnceAndRecurringKindsAndReturnsRecurringProjection()
    {
        var standing = new FakeStandingGateway();
        var recurring = new FakeRecurringGateway();
        StartServer(standing, recurring);
        using var client = AuthenticatedClient();

        using var daily = await PostPlan(client, ValidRecurringBody("daily"), "daily-key");
        Assert.Equal(202, (int)daily.StatusCode);
        using var dailyDocument = JsonDocument.Parse(await daily.Content.ReadAsStringAsync());
        var dailyData = dailyDocument.RootElement.GetProperty("data");
        Assert.Equal("recurring-setup-api-1", dailyData.GetProperty("setup_intent_id").GetString());
        Assert.Equal("setup_pending", dailyData.GetProperty("status").GetString());
        Assert.Equal(0, dailyData.GetProperty("status_version").GetInt64());
        Assert.Equal("recurring-setup/v1:0", dailyData.GetProperty("status_version_cursor").GetString());
        Assert.Equal("/api/v1/plan-setups/recurring-setup-api-1", dailyData.GetProperty("status_url").GetString());
        Assert.Equal(JsonValueKind.Null, dailyData.GetProperty("occurrence_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, dailyData.GetProperty("run_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, dailyData.GetProperty("recording_status_url").ValueKind);
        Assert.True(dailyData.GetProperty("requires_local_action").GetBoolean());
        Assert.Equal("local_region_selection", dailyData.GetProperty("next_action").GetString());
        Assert.Equal(1, recurring.CreateCalls);
        Assert.Equal(0, standing.CreateCalls);

        using var replay = await PostPlan(client, ValidRecurringBody("daily"), "daily-key");
        Assert.Equal(202, (int)replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("recurring-setup-api-1", replayDocument.RootElement.GetProperty("data").GetProperty("setup_intent_id").GetString());

        using var weekly = await PostPlan(client, ValidRecurringBody("weekly"), "weekly-key");
        Assert.Equal(202, (int)weekly.StatusCode);
        using var once = await PostPlan(client, ValidStandingBody(), "once-key");
        Assert.True((int)once.StatusCode == 202, await once.Content.ReadAsStringAsync());
        Assert.Equal(3, recurring.CreateCalls);
        Assert.Equal(new[] { "daily", "daily", "weekly" }, recurring.CreatedKinds.OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, standing.CreateCalls);
        Assert.Empty(recurring.CreatedKinds.Where(kind => kind == "once"));
    }

    [Fact]
    public async Task PostPlanRequiresAuthenticationAndIdempotencyKey()
    {
        var recurring = new FakeRecurringGateway();
        StartServer(null, recurring);
        using var unauthenticated = CreateClient();
        using var missingAuth = await PostPlan(unauthenticated, ValidRecurringBody("daily"), "key");
        Assert.Equal(401, (int)missingAuth.StatusCode);

        using var client = AuthenticatedClient();
        using var missingKey = await client.PostAsync(PlanUrl,
            new StringContent(ValidRecurringBody("daily"), Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)missingKey.StatusCode);
        Assert.Equal(0, recurring.CreateCalls);
    }

    [Fact]
    public async Task MalformedOrUnsupportedScheduleKindNeverCallsEitherGateway()
    {
        var standing = new FakeStandingGateway();
        var recurring = new FakeRecurringGateway();
        StartServer(standing, recurring);
        using var client = AuthenticatedClient();
        var valid = ValidRecurringBody("daily");
        var invalidBodies = new[]
        {
            "{",
            valid.Replace("\"kind\":\"daily\",", "", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"daily\"", "\"Kind\":\"daily\"", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"daily\"", "\"kind\":7", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"daily\"", "\"kind\":\"Daily\"", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"daily\"", "\"kind\":\"monthly\"", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"daily\"", "\"kind\":\"daily\",\"kind\":\"weekly\"", StringComparison.Ordinal),
            valid.Replace("\"schedule\":{", "\"schedule\":{\"kind\":\"daily\"},\"schedule\":{", StringComparison.Ordinal),
        };

        var index = 0;
        foreach (var body in invalidBodies)
        {
            using var response = await PostPlan(client, body, "invalid-kind-" + index++);
            Assert.Equal(400, (int)response.StatusCode);
            Assert.Contains("INVALID_ARGUMENT", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(0, standing.CreateCalls);
        Assert.Equal(0, recurring.CreateCalls);
    }

    [Fact]
    public async Task RecurringParserKeepsAuthorizationCaptureQuotaAndPathRestrictionsStrict()
    {
        var recurring = new FakeRecurringGateway();
        StartServer(null, recurring);
        using var client = AuthenticatedClient();
        var valid = ValidRecurringBody("daily");
        var invalidBodies = new[]
        {
            valid.Replace("\"mode\":\"recurring_lease\"", "\"mode\":\"standing_lease\"", StringComparison.Ordinal),
            valid.Replace("\"mode\":\"none\"", "\"mode\":\"microphone\"", StringComparison.Ordinal),
            valid.Replace("\"type\":\"fixed_region\"", "\"type\":\"active_window\"", StringComparison.Ordinal),
            valid.Replace("\"countdown_seconds\":0", "\"countdown_seconds\":1", StringComparison.Ordinal),
            valid.Replace("\"backend\":\"ffmpeg-region\"", "\"backend\":\"wgc\"", StringComparison.Ordinal),
            valid.Replace("\"max_total_duration_seconds\":120", "\"max_total_duration_seconds\":60", StringComparison.Ordinal),
            valid.Replace("C:\\\\Recordings\\\\Agent", "C:\\\\Recordings\\\\..\\\\Escape", StringComparison.Ordinal),
        };

        var index = 0;
        foreach (var body in invalidBodies)
        {
            using var response = await PostPlan(client, body, "invalid-recurring-" + index++);
            Assert.Equal(400, (int)response.StatusCode);
            Assert.Contains("INVALID_ARGUMENT", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Equal(0, recurring.CreateCalls);
    }

    [Fact]
    public async Task DesktopUnattendedAndExecutionGatesFailBeforeGatewayCreation()
    {
        var recurring = new FakeRecurringGateway();
        StartServer(null, recurring);
        using var client = AuthenticatedClient();

        recurring.Interactive = false;
        using var desktopUnavailable = await PostPlan(client, ValidRecurringBody("daily"), "desktop-off");
        Assert.Contains("INTERACTIVE_DESKTOP_REQUIRED", await desktopUnavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        recurring.Interactive = true;
        recurring.Unattended = false;
        using var unattendedDisabled = await PostPlan(client, ValidRecurringBody("daily"), "unattended-off");
        Assert.Contains("UNATTENDED_DISABLED", await unattendedDisabled.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        recurring.Unattended = true;
        recurring.Execution = false;
        using var executionUnavailable = await PostPlan(client, ValidRecurringBody("daily"), "runtime-off");
        Assert.Contains("RECURRING_EXECUTION_UNAVAILABLE", await executionUnavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, recurring.CreateCalls);
    }

    [Fact]
    public async Task CapabilitiesRequireTheRecurringGatewayDesktopAndLiveRuntime()
    {
        var standing = new FakeStandingGateway();
        var recurring = new FakeRecurringGateway { Interactive = false, Execution = true };
        StartServer(standing, recurring);
        using var client = AuthenticatedClient();

        using var first = await client.GetAsync(CapabilitiesUrl);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstDocument.RootElement.GetProperty("data");
        Assert.False(firstData.TryGetProperty("recurring", out _));
        var firstUnattended = firstData.GetProperty("unattended_lease");
        var firstRecurring = firstUnattended.GetProperty("recurring");
        Assert.False(firstRecurring.GetProperty("supported").GetBoolean());
        Assert.False(firstRecurring.GetProperty("setup_supported").GetBoolean());
        Assert.True(firstRecurring.GetProperty("execution_supported").GetBoolean());
        Assert.True(firstUnattended.GetProperty("supported").GetBoolean());
        Assert.True(firstUnattended.GetProperty("current_enabled").GetBoolean());
        Assert.False(firstUnattended.GetProperty("default_enabled").GetBoolean());
        Assert.Equal(new[] { "fixed_region" }, firstUnattended.GetProperty("supported_targets").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(firstUnattended.GetProperty("audio_allowed").GetBoolean());
        Assert.Equal(3600, firstUnattended.GetProperty("max_lease_seconds").GetInt32());
        Assert.Equal(600, firstUnattended.GetProperty("max_run_seconds").GetInt32());
        Assert.Equal(300, firstUnattended.GetProperty("max_latest_start_grace_seconds").GetInt32());
        Assert.Equal("natural_wake_only", firstUnattended.GetProperty("wake_policy").GetString());
        Assert.True(firstUnattended.GetProperty("requires_interactive_desktop").GetBoolean());
        Assert.True(firstUnattended.GetProperty("revocation_supported").GetBoolean());
        Assert.False(firstUnattended.GetProperty("profile_crud_supported").GetBoolean());
        Assert.Equal("/api/v1/plans", firstUnattended.GetProperty("create_endpoint").GetString());
        Assert.Equal("/api/v1/plan-setups/{setup_intent_id}", firstUnattended.GetProperty("status_endpoint").GetString());
        Assert.True(firstUnattended.GetProperty("execution_supported").GetBoolean());
        Assert.Equal(new[] { "daily", "weekly" }, firstRecurring.GetProperty("schedule_kinds").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("fixed_region", firstRecurring.GetProperty("supported_targets")[0].GetString());
        Assert.False(firstRecurring.GetProperty("audio_allowed").GetBoolean());
        Assert.Equal(0, firstRecurring.GetProperty("countdown_seconds").GetInt32());
        Assert.Equal("natural_wake_only", firstRecurring.GetProperty("wake_policy").GetString());
        Assert.True(firstRecurring.GetProperty("requires_interactive_desktop").GetBoolean());
        Assert.Equal(1, firstRecurring.GetProperty("concurrent_runs").GetInt32());
        Assert.False(firstRecurring.GetProperty("nested_recording_allowed").GetBoolean());
        Assert.False(firstRecurring.GetProperty("profile_crud_supported").GetBoolean());
        Assert.Equal("/api/v1/plans", firstRecurring.GetProperty("create_endpoint").GetString());
        Assert.Equal("/api/v1/plan-setups/{setup_intent_id}", firstRecurring.GetProperty("status_endpoint").GetString());

        recurring.Interactive = true;
        recurring.Execution = false;
        using var second = await client.GetAsync(CapabilitiesUrl);
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondData = secondDocument.RootElement.GetProperty("data");
        Assert.False(secondData.TryGetProperty("recurring", out _));
        var secondRecurring = secondData.GetProperty("unattended_lease").GetProperty("recurring");
        Assert.False(secondRecurring.GetProperty("supported").GetBoolean());
        Assert.False(secondRecurring.GetProperty("setup_supported").GetBoolean());
        Assert.False(secondRecurring.GetProperty("execution_supported").GetBoolean());

        recurring.Execution = true;
        using var third = await client.GetAsync(CapabilitiesUrl);
        using var thirdDocument = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        var thirdData = thirdDocument.RootElement.GetProperty("data");
        Assert.False(thirdData.TryGetProperty("recurring", out _));
        var thirdRecurring = thirdData.GetProperty("unattended_lease").GetProperty("recurring");
        Assert.True(thirdRecurring.GetProperty("supported").GetBoolean());
        Assert.True(thirdRecurring.GetProperty("setup_supported").GetBoolean());
        Assert.True(thirdRecurring.GetProperty("execution_supported").GetBoolean());

    }

    [Fact]
    public async Task CapabilitiesWithoutRecurringGatewayRemainUnsupported()
    {
        StartServer(null, null);
        using var client = AuthenticatedClient();
        using var response = await client.GetAsync(CapabilitiesUrl);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("recurring", out _));
        var recurring = data.GetProperty("unattended_lease").GetProperty("recurring");
        Assert.False(recurring.GetProperty("supported").GetBoolean());
        Assert.False(recurring.GetProperty("setup_supported").GetBoolean());
        Assert.False(recurring.GetProperty("execution_supported").GetBoolean());
        using var create = await PostPlan(client, ValidRecurringBody("daily"), "no-gateway-key");
        Assert.Equal(409, (int)create.StatusCode);
        Assert.Contains("INTERACTIVE_DESKTOP_REQUIRED", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusRoutingProjectsRecurringStatesAndNeverProbesUiOrLeaksMissingIds()
    {
        var recurring = new FakeRecurringGateway();
        StartServer(null, recurring);
        using var client = AuthenticatedClient();
        var id = "recurring-setup-api-status";
        var states = new[] { "setup_pending", "pending_lease_approval", "scheduled", "rejected", "expired", "blocked" };
        for (var index = 0; index < states.Length; index++)
        {
            recurring.SetState(new RecurringPlanSetupState(id, states[index], index,
                "recurring-setup/v1:" + index, "plan-api", "lease-api", index < 2,
                index == 0 ? "local_region_selection" : null, null));
            using var response = await client.GetAsync(SetupUrl(id));
            Assert.Equal(200, (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(states[index], data.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("occurrence_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("run_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("recording_status_url").ValueKind);
        }

        Assert.Equal(0, recurring.InteractiveReads);
        using var unknown = await client.GetAsync(SetupUrl("recurring-setup-unknown"));
        recurring.Visible = false;
        using var foreign = await client.GetAsync(SetupUrl("recurring-setup-api-status"));
        Assert.Equal(404, (int)unknown.StatusCode);
        Assert.Contains("PLAN_SETUP_NOT_FOUND", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(404, (int)foreign.StatusCode);
        Assert.Contains("PLAN_SETUP_NOT_FOUND", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, recurring.InteractiveReads);
    }

    [Fact]
    public async Task RecurringCursorFamilyIsStrictAndLongPollReturnsAdvanceOrCurrentSnapshot()
    {
        var recurring = new FakeRecurringGateway();
        var id = "recurring-setup-api-cursor";
        recurring.SetState(new RecurringPlanSetupState(id, "setup_pending", 1, "recurring-setup/v1:1",
            null, null, true, "local_region_selection", null));
        StartServer(null, recurring);
        using var client = AuthenticatedClient();

        using var advanced = await client.GetAsync(SetupUrl(id) + "?since_status_version=recurring-setup%2Fv1%3A0&wait_ms=2000");
        Assert.Equal(200, (int)advanced.StatusCode);
        using (var document = JsonDocument.Parse(await advanced.Content.ReadAsStringAsync()))
            Assert.Equal(1, document.RootElement.GetProperty("data").GetProperty("status_version").GetInt64());

        recurring.ResetGetSignal();
        var waiting = client.GetAsync(SetupUrl(id) + "?since_status_version=recurring-setup%2Fv1%3A1&wait_ms=2000");
        await recurring.FirstGet.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recurring.SetState(new RecurringPlanSetupState(id, "scheduled", 2, "recurring-setup/v1:2",
            "plan-api", "lease-api", false, "natural_wake", null));
        using var changed = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        using (var document = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("scheduled", data.GetProperty("status").GetString());
            Assert.Equal(2, data.GetProperty("status_version").GetInt64());
        }

        using var unchanged = await client.GetAsync(SetupUrl(id) + "?since_status=scheduled&since_version=2&wait_ms=30");
        using (var document = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync()))
            Assert.Equal(2, document.RootElement.GetProperty("data").GetProperty("status_version").GetInt64());

        foreach (var cursor in new[] { "recurring-setup/v1:01", "recurring-setup/v1:-1", "v1:1:0:0:0:0:0", "-1" })
        {
            using var invalid = await client.GetAsync(SetupUrl(id) + "?since_status_version=" + Uri.EscapeDataString(cursor));
            Assert.Equal(400, (int)invalid.StatusCode);
            Assert.Contains("INVALID_ARGUMENT", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ApiServerStopIsIdempotentAndCancelsBoundedStatusWaits()
    {
        var recurring = new FakeRecurringGateway();
        var id = "recurring-setup-api-shutdown";
        recurring.SetState(new RecurringPlanSetupState(id, "setup_pending", 0, "recurring-setup/v1:0",
            null, null, true, "local_region_selection", null));
        StartServer(null, recurring);
        using var client = AuthenticatedClient();
        var pending = client.GetAsync(SetupUrl(id) + "?since_status=setup_pending&since_version=0&wait_ms=25000");
        await recurring.FirstGet.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.WhenAll(
            Task.Run(() => _server!.Stop()),
            Task.Run(() => _server!.Stop())).WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(200, (int)response.StatusCode);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        Assert.True(pending.IsCompleted);
    }

    [Fact]
    public async Task PendingRealCoordinatorReplayReturnsSameIntentWithoutOpeningSecondUiFlight()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        var ui = new BlockingRecurringUi();
        using var coordinator = new RecurringPlanSetupCoordinator(
            fixture.Store, fixture.Audit, ui, () => fixture.Principal, () => fixture.Now,
            () => fixture.Displays, fixture.Output, executionSupportedProvider: () => true);
        StartServer(null, coordinator);
        using var client = AuthenticatedClient();

        using var first = await PostPlan(client, ValidRecurringBody("daily", "same-prefix"), "real-pending-key");
        Assert.Equal(202, (int)first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstDocument.RootElement.GetProperty("data").GetProperty("setup_intent_id").GetString();
        await ui.SelectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var visibleStatus = await client.GetAsync(SetupUrl(firstId!));
        Assert.Equal(200, (int)visibleStatus.StatusCode);
        fixture.Principal = new RecurringSetupPrincipal("S-1-5-21-foreign", "foreign-session");
        using var foreignStatus = await client.GetAsync(SetupUrl(firstId!));
        using var unknownStatus = await client.GetAsync(SetupUrl("recurring-setup-does-not-exist"));
        Assert.Equal(404, (int)foreignStatus.StatusCode);
        Assert.Equal(404, (int)unknownStatus.StatusCode);
        using var foreignDocument = JsonDocument.Parse(await foreignStatus.Content.ReadAsStringAsync());
        using var unknownDocument = JsonDocument.Parse(await unknownStatus.Content.ReadAsStringAsync());
        Assert.Equal(foreignDocument.RootElement.GetProperty("error").GetProperty("code").GetString(),
            unknownDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        fixture.Principal = new RecurringSetupPrincipal(Sid, Session);

        using var replay = await PostPlan(client, ValidRecurringBody("daily", "same-prefix"), "real-pending-key");
        Assert.Equal(202, (int)replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstId, replayDocument.RootElement.GetProperty("data").GetProperty("setup_intent_id").GetString());

        using var conflict = await PostPlan(client, ValidRecurringBody("daily", "changed-prefix"), "real-pending-key");
        Assert.Equal(409, (int)conflict.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, ui.SelectionCalls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM setup_intents;"));

        coordinator.Dispose();
        await ui.SelectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.AssertNoExecution();
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        ApiKeyAuth.ResetForTesting(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
    }

    private const string PlanUrl = "http://127.0.0.1:37891/api/v1/plans";
    private const string CapabilitiesUrl = "http://127.0.0.1:37891/api/v1/capabilities";
    private static string SetupUrl(string id) => $"http://127.0.0.1:37891/api/v1/plan-setups/{Uri.EscapeDataString(id)}";

    private void StartServer(IStandingPlanSetupGateway? standing, IRecurringPlanSetupGateway? recurring)
    {
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTray();
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray,
            standingPlanSetupGateway: standing,
            recurringPlanSetupGateway: recurring);
        _server.Start();
    }

    private HttpClient AuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        return client;
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static async Task<HttpResponseMessage> PostPlan(HttpClient client, string body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PlanUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static string ValidRecurringBody(string kind, string prefix = "api-capture") => JsonSerializer.Serialize(new
    {
        recording_spec = new
        {
            source = new { type = "fixed_region" },
            audio = new { mode = "none" },
            duration_seconds = 60,
            countdown_seconds = 0,
            backend = "ffmpeg-region",
            output = new { directory = @"C:\Recordings\Agent", filename_prefix = prefix },
        },
        schedule = new
        {
            kind,
            time_zone_id = "UTC",
            local_start_date = "2099-01-01",
            local_end_date = kind == "weekly" ? "2099-01-15" : "2099-01-03",
            local_time = "10:00:00",
            weekdays = kind == "weekly" ? new[] { "monday", "wednesday" } : null,
            maximum_occurrences = 2,
            latest_start_grace_seconds = 300,
        },
        requested_authorization = new
        {
            mode = "recurring_lease",
            valid_until = "2099-02-01T00:00:00Z",
            max_runs = 2,
            max_total_duration_seconds = 120,
        },
    });

    private static string ValidStandingBody()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(2);
        var latest = start.AddMinutes(2);
        var end = latest.AddSeconds(60);
        return JsonSerializer.Serialize(new
        {
            recording_spec = new
            {
                source = new { type = "fixed_region" },
                audio = new { mode = "none" },
                duration_seconds = 60,
                countdown_seconds = 0,
                backend = "ffmpeg-region",
                output = new { directory = @"C:\Recordings\Agent", filename = "once.mp4" },
            },
            schedule = new
            {
                kind = "once",
                start_at = start.ToString("O"),
                latest_start_at = latest.ToString("O"),
                planned_end_at = end.ToString("O"),
            },
            requested_authorization = new
            {
                mode = "standing_lease",
                expires_at = end.AddMinutes(40).ToString("O"),
                max_runs = 1,
                max_duration_seconds = 60,
            },
        });
    }

    private sealed class FakeRecurringGateway : IRecurringPlanSetupGateway
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Canonical, RecurringPlanSetupState State)> _byKey = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RecurringPlanSetupState> _states = new(StringComparer.Ordinal);
        private int _createCalls;
        private int _interactiveReads;
        private int _nextId;

        internal bool Interactive = true;
        internal bool Unattended = true;
        internal bool Execution = true;
        internal bool Visible = true;
        internal TaskCompletionSource<object?> FirstGet { get; private set; } = NewSignal();
        internal ConcurrentBag<string> CreatedKinds { get; } = new();
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal int InteractiveReads => Volatile.Read(ref _interactiveReads);
        public bool IsInteractiveDesktopAvailable { get { Interlocked.Increment(ref _interactiveReads); return Interactive; } }
        public bool IsUnattendedEnabled => Unattended;
        public bool IsExecutionSupported => Execution;

        public RecurringPlanSetupCreateResult CreateOrGet(RecurringPlanApiRequest request)
        {
            Interlocked.Increment(ref _createCalls);
            CreatedKinds.Add(request.Schedule.Kind == AgentRecorder.Core.Automation.RecurringScheduleKind.Daily ? "daily" : "weekly");
            var canonical = string.Join("|", request.Schedule.CanonicalDigest, request.LeaseValidUntilUtc.UtcTicks,
                request.MaxRuns, request.MaxTotalDuration.Ticks, request.OutputDirectory, request.FilenamePrefix);
            lock (_gate)
            {
                if (_byKey.TryGetValue(request.IdempotencyKey, out var prior))
                    return prior.Canonical == canonical
                        ? new(RecurringPlanSetupCreateStatus.Existing, prior.State, "existing")
                        : new(RecurringPlanSetupCreateStatus.Conflict, prior.State, "idempotency_conflict");
                var id = "recurring-setup-api-" + Interlocked.Increment(ref _nextId);
                var state = new RecurringPlanSetupState(id, "setup_pending", 0, "recurring-setup/v1:0",
                    null, null, true, "local_region_selection", null);
                _byKey.Add(request.IdempotencyKey, (canonical, state));
                _states[id] = state;
                return new(RecurringPlanSetupCreateStatus.Created, state, "created");
            }
        }

        public RecurringPlanSetupState? Get(string setupIntentId)
        {
            FirstGet.TrySetResult(null);
            return Visible && _states.TryGetValue(setupIntentId, out var state) ? state : null;
        }

        internal void SetState(RecurringPlanSetupState state) => _states[state.SetupIntentId] = state;
        internal void ResetGetSignal() => FirstGet = NewSignal();
        private static TaskCompletionSource<object?> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeStandingGateway : IStandingPlanSetupGateway
    {
        private int _createCalls;
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        public bool IsInteractiveDesktopAvailable => true;
        public bool IsUnattendedEnabled => true;
        public bool IsExecutionSupported => true;

        public StandingPlanSetupCreateResult CreateOrGet(StandingPlanApiRequest request)
        {
            Interlocked.Increment(ref _createCalls);
            return new(StandingPlanSetupCreateStatus.Created, "standing-setup-api-once", "setup_pending", 0,
                null, null, null, null, "v1:0:0:0:0:0:0");
        }

        public StandingPlanSetupState? Get(string setupIntentId) => setupIntentId == "standing-setup-api-once"
            ? new StandingPlanSetupState(setupIntentId, "setup_pending", 0, null, null, null,
                true, "local_region_selection", null, StatusVersionCursor: "v1:0:0:0:0:0:0")
            : null;
    }

    private sealed class BlockingRecurringUi : IRecurringPlanSetupUi
    {
        private int _selectionCalls;
        internal int SelectionCalls => Volatile.Read(ref _selectionCalls);
        internal TaskCompletionSource<object?> SelectionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<object?> SelectionClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInteractiveDesktopAvailable => true;

        public async Task<RecurringPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _selectionCalls);
            SelectionEntered.TrySetResult(null);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new(RecurringRegionSelectionStatus.Cancelled);
            }
            finally { SelectionClosed.TrySetResult(null); }
        }

        public Task<RecurringLeaseApprovalResult> ShowApprovalAsync(RecurringLeaseApprovalDetails details, CancellationToken cancellationToken) =>
            Task.FromResult(RecurringLeaseApprovalResult.Rejected);
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
