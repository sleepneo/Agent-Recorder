using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class PlanExecutionStatusApiTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "AgentRecorderPlanStatusApi_" + Guid.NewGuid().ToString("N"));
    private ApiServer? _server;

    [Fact]
    public async Task AuthenticatedEndpointReturnsDurableProjectionAndCapabilitiesTemplate()
    {
        var gateway = new FakeGateway(BuildState());
        _server = CreateServer(gateway);
        _server.Start();
        using var client = CreateClient();

        using var unauthorized = await client.GetAsync(Url("standing-plan-1"));
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)unauthorized.StatusCode);

        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        using var response = await client.GetAsync(Url("standing-plan-1"));
        Assert.Equal((int)HttpStatusCode.OK, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("request_id").GetString()));
        var data = root.GetProperty("data");
        Assert.Equal("standing-plan-1", data.GetProperty("plan_id").GetString());
        Assert.Equal("once", data.GetProperty("kind").GetString());
        Assert.Equal("enabled", data.GetProperty("plan_status").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("schedule_exhausted").ValueKind);
        Assert.Equal("recording", data.GetProperty("next_occurrence").GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("recording", data.GetProperty("next_occurrence").GetProperty("execution_status_code").GetString());
        Assert.Equal("recording", data.GetProperty("latest_occurrence").GetProperty("execution_status_code").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("latest_occurrence").GetProperty("output_path").ValueKind);
        Assert.False(data.GetProperty("latest_occurrence").GetProperty("output_path_recorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("latest_occurrence").GetProperty("output_file_exists").ValueKind);

        using var capabilities = await client.GetAsync($"http://127.0.0.1:{_server.BoundPort}/api/v1/capabilities");
        using var capabilityDocument = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.Equal("/api/v1/plans/{plan_id}/status",
            capabilityDocument.RootElement.GetProperty("data").GetProperty("unattended_lease")
                .GetProperty("execution_status_endpoint").GetString());
    }

    [Fact]
    public async Task UnknownMalformedAndUnavailableStatusesFailClosed()
    {
        _server = CreateServer(new FakeGateway(null));
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var unknown = await client.GetAsync(Url("foreign-plan"));
        Assert.Equal((int)HttpStatusCode.NotFound, (int)unknown.StatusCode);
        Assert.Contains("PLAN_NOT_FOUND", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var malformed = await client.GetAsync(Url("bad%2Fid"));
        Assert.Equal((int)HttpStatusCode.BadRequest, (int)malformed.StatusCode);
        Assert.Contains("INVALID_ARGUMENT", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProductionGatewayReturnsStableUnavailableError()
    {
        _server = CreateServer(null);
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var response = await client.GetAsync(Url("plan-1"));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, (int)response.StatusCode);
        Assert.Contains("PLAN_STATUS_UNAVAILABLE", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedStatusSurvivesQueryAndApiRecreationOnTheSameDatabase()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "api-status-restart-intent";
        fixture.Create(intentId, prepared: true);
        fixture.Activate(intentId);
        var planId = fixture.Query.Get(intentId, RecurringPlanSetupTestFixture.Sid,
            RecurringPlanSetupTestFixture.Session)!.PlanId!;
        var initialSnapshot = new PlanExecutionStatusQueryService(fixture.Store).Get(planId,
            RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session);
        Assert.NotNull(initialSnapshot);
        Assert.False(initialSnapshot.ScheduleExhausted);
        Assert.Equal(0, initialSnapshot.OccurrenceCount);

        _server = CreateServer(new StoreBackedGateway(new PlanExecutionStatusQueryService(fixture.Store),
            RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session));
        _server.Start();
        using var firstClient = CreateClient();
        firstClient.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        using var firstResponse = await firstClient.GetAsync(Url(planId));
        Assert.Equal((int)HttpStatusCode.OK, (int)firstResponse.StatusCode);
        using var firstDocument = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstData = firstDocument.RootElement.GetProperty("data").GetRawText();
        Assert.Equal(0, firstDocument.RootElement.GetProperty("data").GetProperty("occurrence_count").GetInt32());

        _server.Stop();
        var reopenedStore = new SqliteOperationalStore(fixture.Store.DatabasePath);
        _server = CreateServer(new StoreBackedGateway(new PlanExecutionStatusQueryService(reopenedStore),
            RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session));
        _server.Start();
        using var secondClient = CreateClient();
        secondClient.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        using var secondResponse = await secondClient.GetAsync(Url(planId));
        Assert.Equal((int)HttpStatusCode.OK, (int)secondResponse.StatusCode);
        using var secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(firstData, secondDocument.RootElement.GetProperty("data").GetRawText());
    }

    [Fact]
    public async Task ForeignPlanWithCorruptOccurrenceRelationHasTheSameBlackBox404AsUnknownId()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "foreign-damaged-status-intent";
        const string foreignSid = "S-1-5-21-foreign-status-owner";
        const string foreignSession = "foreign-status-session";
        fixture.Create(intentId, prepared: true, sid: foreignSid, session: foreignSession);
        fixture.Activate(intentId, foreignSid, foreignSession);
        var planId = fixture.Query.Get(intentId, foreignSid, foreignSession)!.PlanId!;
        var advancement = new SqliteRecurringAdvancementTransaction(fixture.Store).AdvanceOne(
            planId, 1, "foreign-damaged-status-advance", 0,
            RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(10));
        Assert.Equal("scheduled", advancement.ResultCode);
        var occurrenceId = fixture.Text(
            $"SELECT occurrence_id FROM recurring_occurrence_slots WHERE occurrence_identity = '{advancement.OccurrenceIdentity}';");
        fixture.Corrupt($"UPDATE plan_occurrences SET status_code = 'run_created', run_id = 'missing-foreign-run' WHERE id = '{occurrenceId}';");

        const string currentSid = "S-1-5-21-status-query-caller";
        const string currentSession = "status-query-caller-session";
        _server = CreateServer(new StoreBackedGateway(new PlanExecutionStatusQueryService(fixture.Store), currentSid, currentSession));
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var unknown = await client.GetAsync(Url("unknown-status-plan"));
        using var foreign = await client.GetAsync(Url(planId));
        Assert.Equal((int)HttpStatusCode.NotFound, (int)unknown.StatusCode);
        Assert.Equal((int)unknown.StatusCode, (int)foreign.StatusCode);
        Assert.Equal("PLAN_NOT_FOUND", await ErrorCode(unknown));
        Assert.Equal("PLAN_NOT_FOUND", await ErrorCode(foreign));
    }

    [Fact]
    public async Task ForeignCorruptPlanAndApprovalRemainIndistinguishableFromUnknownId()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "foreign-corrupt-plan-status-intent";
        const string foreignSid = "S-1-5-21-foreign-status-owner";
        const string foreignSession = "foreign-status-session";
        fixture.Create(intentId, prepared: true, sid: foreignSid, session: foreignSession);
        fixture.Activate(intentId, foreignSid, foreignSession);
        var planId = fixture.Query.Get(intentId, foreignSid, foreignSession)!.PlanId!;
        fixture.Corrupt($"UPDATE plans SET status_code = 'corrupt_foreign_status' WHERE id = '{planId}'; UPDATE recurring_lease_local_approvals SET approval_digest = 'corrupt_foreign_digest' WHERE plan_id = '{planId}';");

        _server = CreateServer(new StoreBackedGateway(new PlanExecutionStatusQueryService(fixture.Store),
            RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session));
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var unknown = await client.GetAsync(Url("unknown-corrupt-status-plan"));
        using var foreign = await client.GetAsync(Url(planId));
        Assert.Equal((int)HttpStatusCode.NotFound, (int)unknown.StatusCode);
        Assert.Equal((int)unknown.StatusCode, (int)foreign.StatusCode);
        Assert.Equal("PLAN_NOT_FOUND", await ErrorCode(unknown));
        Assert.Equal("PLAN_NOT_FOUND", await ErrorCode(foreign));
    }

    [Fact]
    public async Task OwnedCorruptPlanStillFailsClosedAsUnavailable()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        const string intentId = "owned-corrupt-plan-status-intent";
        fixture.Create(intentId, prepared: true);
        fixture.Activate(intentId);
        var planId = fixture.Query.Get(intentId, RecurringPlanSetupTestFixture.Sid,
            RecurringPlanSetupTestFixture.Session)!.PlanId!;
        fixture.Corrupt($"UPDATE plans SET version = -1 WHERE id = '{planId}';");

        _server = CreateServer(new StoreBackedGateway(new PlanExecutionStatusQueryService(fixture.Store),
            RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session));
        _server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var response = await client.GetAsync(Url(planId));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, (int)response.StatusCode);
        Assert.Equal("PLAN_STATUS_UNAVAILABLE", await ErrorCode(response));
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        ApiKeyAuth.ResetForTesting(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
    }

    private ApiServer CreateServer(IPlanExecutionStatusGateway? gateway)
    {
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTray();
        engine.SetTray(tray);
        return new ApiServer(engine, audit, tray,
            readiness: null,
            autoStart: null,
            ffmpegPrewarmer: null,
            tracer: null,
            ensureContextStore: null,
            performanceSummaryProvider: null,
            standingPlanSetupGateway: null,
            recurringPlanSetupGateway: null,
            planExecutionStatusGateway: gateway,
            listenPort: 0);
    }

    private string Url(string planId) =>
        $"http://127.0.0.1:{_server!.BoundPort}/api/v1/plans/{planId}/status";

    private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static PlanExecutionStatusState BuildState()
    {
        var start = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
        var occurrence = new PlanExecutionOccurrenceState(
            "occ-1", start, start.AddMinutes(5), "run_created", null, "run-1",
            new PlanExecutionRunState("run-1", "recording", null), null, false, null, "recording");
        return new PlanExecutionStatusState("standing-plan-1", "once", "enabled", null, 1, occurrence, occurrence);
    }

    private sealed class FakeGateway(PlanExecutionStatusState? state) : IPlanExecutionStatusGateway
    {
        public PlanExecutionStatusState? Get(string planId) =>
            string.Equals(planId, state?.PlanId, StringComparison.Ordinal) ? state : null;
    }

    private sealed class StoreBackedGateway(
        PlanExecutionStatusQueryService query,
        string sid,
        string session) : IPlanExecutionStatusGateway
    {
        public PlanExecutionStatusState? Get(string planId)
        {
            var snapshot = query.Get(planId, sid, session);
            return snapshot is null
                ? null
                : new PlanExecutionStatusState(
                    snapshot.PlanId,
                    snapshot.Kind,
                    snapshot.PlanStatus,
                    snapshot.ScheduleExhausted,
                    snapshot.OccurrenceCount,
                    Map(snapshot.NextOccurrence),
                    Map(snapshot.LatestOccurrence));
        }

        private static PlanExecutionOccurrenceState? Map(PlanExecutionStatusOccurrenceSnapshot? occurrence) =>
            occurrence is null
                ? null
                : new PlanExecutionOccurrenceState(
                    occurrence.OccurrenceId,
                    occurrence.WindowStartUtc,
                    occurrence.WindowEndUtc,
                    occurrence.Status,
                    occurrence.TerminalReasonCode,
                    occurrence.RunId,
                    occurrence.Run is null
                        ? null
                        : new PlanExecutionRunState(
                            occurrence.Run.RunId,
                            occurrence.Run.Status,
                            occurrence.Run.TerminalReasonCode),
                    occurrence.OutputPath,
                    occurrence.OutputPathRecorded,
                    occurrence.OutputFileExists);
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
