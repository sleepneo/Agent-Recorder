using System.Net;
using System.Security.Principal;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PlanStatusDurableBindingTests
{
    [Fact]
    public void CanonicalDurableStandingBindingIsReadableThroughActualQueryAndGateway()
    {
        using var f = new Fixture();
        var query = new PlanExecutionStatusQueryService(f.Store);
        Assert.True(DurableAuthorizationIdentity.IsCanonical(f.Binding));
        Assert.Equal(f.Binding, query.GetOwnedSessionBinding(f.PlanId, f.Sid));
        Assert.NotNull(query.Get(f.PlanId, f.Sid, f.Binding));
        Assert.NotNull(query.GetHistory(f.PlanId, f.Sid, f.Binding, 1));
        var gateway = new PlanExecutionStatusGateway(query);
        Assert.Equal("authorized", gateway.Get(f.PlanId)!.NextOccurrence!.Status);
        Assert.Single(gateway.GetPage(f.PlanId, 1, null)!.Items);
    }

    [Fact]
    public async Task ActualApiUsesProductionGatewayForDurableStatusAndHistory()
    {
        using var f = new Fixture();
        using var host = new Host(f.Store);
        using var status = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/status");
        using var history = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/history?limit=1");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var text = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain(f.Binding, text);
        Assert.DoesNotContain(f.Sid, text);
    }

    [Fact]
    public async Task ReopenedDatabaseExposesAuthorizedRecordingAndTerminalWithoutAnotherRun()
    {
        using var f = new Fixture();
        await Check("authorized", null);
        var p = f.Prepared;
        var plan = new SqlitePlanDefinitionRepository(f.Store).Get(f.PlanId);
        var occurrence = new SqlitePlanOccurrenceRepository(f.Store).Get(p.OccurrenceId!);
        var lease = new SqliteConsentLeaseRepository(f.Store).Get(p.LeaseId!);
        var scope = new SqliteAuthorizedCaptureScopeRepository(f.Store).Get(p.ScopeId!);
        var at = f.Now.AddMinutes(1);
        var commit = new SqlitePhase3StartGateTransaction(f.Store).Commit(new(f.PlanId, occurrence.Id, lease.Id,
            scope.ScopeId, scope.ScopeDigest, "run-status", "use-status", plan.Version, occurrence.Version, lease.Version,
            scope.ReservedDuration, at));
        var environment = new StandingLeaseExecutionEnvironment(at, f.Sid, f.Binding, true,
            new[] { new StandingLeaseDisplayMetadata("synthetic-display", scope.StableDisplayFingerprint,
                DisplayIdentityResolutionStatus.Resolved, scope.DisplayBounds, scope.DpiX, scope.DpiY,
                scope.PhysicalWidth, scope.PhysicalHeight, scope.Orientation) }, scope.TopologyDigest,
            new StandingLeaseOutputFileSystemSnapshot(scope.OutputDirectory, scope.OutputFilePath, true, false, true, true,
                long.MaxValue, RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration)));
        Assert.True(new SqliteStandingLeaseExecutionSnapshotLoader(f.Store).TryAuthorizeAndConsumeStandingLeaseUseForTest(
            commit.FirstCommitProof, environment, out var authorization, out var reason), reason);
        Assert.True(StandingLeaseCaptureSpecification.TryCreate(authorization, out var specification, out reason), reason);
        var lifecycle = new SqliteStandingLeaseLifecycleTransaction(f.Store, specification!);
        Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation
            { EvidenceKind = "synthetic", FrameNumber = 1, TotalSizeBytes = 1 }, at.AddSeconds(1)).Succeeded);
        await Check("run_created", "recording");
        Assert.True(lifecycle.CompleteTermination(StandingLeaseLifecycleTerminationKind.SessionInterrupted,
            -1, null, at.AddSeconds(2)).Succeeded);
        await Check("blocked", "session_interrupted");
        Assert.Equal(1L, f.Scalar("SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, f.Scalar("SELECT COUNT(*) FROM lease_uses;"));
        Assert.Empty(Directory.GetFiles(f.Root, "*.mp4", SearchOption.AllDirectories));

        async Task Check(string occurrenceStatus, string? runStatus)
        {
            // A fresh store/gateway/HTTP host is composed at every lifecycle boundary.
            var reopened = new SqliteOperationalStore(f.Store.DatabasePath);
            var before = State(reopened);
            var gateway = new PlanExecutionStatusGateway(new(reopened));
            var status = gateway.Get(f.PlanId)!;
            var item = Assert.Single(gateway.GetPage(f.PlanId, 1, null)!.Items);
            Assert.Equal(occurrenceStatus, (status.NextOccurrence ?? status.LatestOccurrence)!.Status);
            Assert.Equal(occurrenceStatus, item.Status);
            Assert.Equal(runStatus, item.Run?.Status);
            using var host = new Host(reopened);
            using var response = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/status");
            using var history = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/history?limit=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Equal(before, State(reopened));
        }
    }

    [Theory]
    [InlineData("legacy-session-token")]
    [InlineData("ZG9tYWluXFx1c2VyfHNlc3Npb246MQ")]
    [InlineData("different-login")]
    [InlineData("different-boot")]
    public void SameSidDiagnosticsDoNotRestoreExecutionIdentity(string variant)
    {
        var sid = CurrentSid();
        var current = DurableAuthorizationIdentity.Format(sid, 99, 12345, 1)!;
        var stored = variant switch
        {
            "different-login" => DurableAuthorizationIdentity.Format(sid, 99, 54321, 1)!,
            "different-boot" => DurableAuthorizationIdentity.Format(sid, 100, 12345, 1)!,
            _ => variant,
        };
        using var f = new Fixture(stored);
        var before = State(f.Store);
        using var identity = CaptureAuthorizationSessionBinding.UseIdentityForTests(new(() => new(current, null)));
        var gateway = new PlanExecutionStatusGateway(new(f.Store));
        Assert.NotNull(gateway.Get(f.PlanId));
        Assert.Single(gateway.GetPage(f.PlanId, 1, null)!.Items);
        var candidates = new StandingLeaseNaturalWakeCandidateQuery(f.Store);
        Assert.Empty(candidates.ListCandidates(sid, CaptureAuthorizationSessionBinding.Persistent));
        Assert.Null(new PlanExecutionStatusQueryService(f.Store).Get(f.PlanId, sid, current));
        Assert.Equal(before, State(f.Store));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void DiagnosticGatewayDoesNotQueryOrRefreshNativeLoginAuthority()
    {
        using var f = new Fixture();
        var calls = 0;
        using var identity = CaptureAuthorizationSessionBinding.UseIdentityForTests(new(() =>
        { Interlocked.Increment(ref calls); return DurableIdentityObservation.Unavailable("synthetic-unavailable"); }));
        var before = State(f.Store);
        var gateway = new PlanExecutionStatusGateway(new(f.Store));
        Assert.NotNull(gateway.Get(f.PlanId));
        Assert.Single(gateway.GetPage(f.PlanId, 1, null)!.Items);
        Assert.Equal(0, calls);
        Assert.Equal("", CaptureAuthorizationSessionBinding.Persistent);
        Assert.Empty(new StandingLeaseNaturalWakeCandidateQuery(f.Store).ListCandidates(f.Sid, CaptureAuthorizationSessionBinding.Persistent));
        Assert.Equal(before, State(f.Store));
    }

    public static IEnumerable<object?[]> InvalidBindings()
    {
        yield return new object?[] { null };
        foreach (var value in new[] { "", " ", "legacy\0suffix", "legacy\nvalue", "legacy\tvalue", " legacy", "legacy ",
            new string('a', 257), "arbitrary|punctuation", "legacy/other", "legacy' OR 1=1--",
            "winlogon-v2|S-1-5-18|00000001|0000000000000001|00000001",
            "winlogon-v1|S-1-5-18|00000000|0000000000000001|00000001",
            "winlogon-v1|S-1-5-18|00000001|0000000000000000|00000001",
            "winlogon-v1|S-1-5-18|1|0000000000000001|00000001",
            "winlogon-v1|S-1-5-18|0000000A|0000000000000001|00000001",
            "winlogon-v1|invalid-sid|00000001|0000000000000001|00000001",
            "winlogon-v1|S-1-5-18|00000001|0000000000000001|00000001\0suffix" })
            yield return new object?[] { value };
    }

    [Theory]
    [MemberData(nameof(InvalidBindings))]
    public void InvalidIdentityArgumentsFailClosedWithoutWideningIdentifiers(string? binding)
    {
        using var f = new Fixture();
        var before = State(f.Store);
        var query = new PlanExecutionStatusQueryService(f.Store);
        Assert.Null(query.Get(f.PlanId, f.Sid, binding));
        Assert.Null(query.GetHistory(f.PlanId, f.Sid, binding, 1));
        Assert.Null(query.Get(f.PlanId, binding, f.Binding));
        foreach (var id in new[] { "plan|other", "plan/other", "plan\0suffix", new string('a', 129) })
        {
            Assert.Equal("invalid_argument", Assert.Throws<Phase3PersistenceException>(() => query.Get(id, f.Sid, f.Binding)).Code);
            Assert.Equal("invalid_argument", Assert.Throws<Phase3PersistenceException>(() => query.GetHistory(id, f.Sid, f.Binding, 1)).Code);
        }
        Assert.Equal(before, State(f.Store));
    }

    [Theory]
    [InlineData("legacy\0hidden")]
    [InlineData("legacy\ninvalid")]
    [InlineData("")]
    [InlineData("winlogon-v9|invalid")]
    [InlineData("durable-nul")]
    [InlineData("overlong")]
    public async Task CorruptStoredOwnerIsRejectedByActualGatewayAndApi(string binding)
    {
        using var f = new Fixture();
        binding = binding switch { "durable-nul" => f.Binding + "\0hidden", "overlong" => new string('a', 257), _ => binding };
        f.Corrupt("UPDATE setup_intents SET session_binding=$binding;", binding);
        var query = new PlanExecutionStatusQueryService(f.Store);
        Assert.Equal("plan_status_data_invalid", Assert.Throws<Phase3PersistenceException>(() => query.GetOwnedSessionBinding(f.PlanId, f.Sid)).Code);
        using var host = new Host(f.Store);
        foreach (var endpoint in new[] { "status", "history" })
        {
            using var response = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/{endpoint}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousOwnersNeverResolveToAReadablePlan(bool sameBinding)
    {
        using var f = new Fixture();
        f.CloneOwner(sameBinding);
        var gateway = new PlanExecutionStatusGateway(new(f.Store));
        Assert.Equal("plan_status_data_invalid", Assert.Throws<Phase3PersistenceException>(() => gateway.Get(f.PlanId)).Code);
        Assert.Equal("plan_status_data_invalid", Assert.Throws<Phase3PersistenceException>(() => gateway.GetPage(f.PlanId, 1, null)).Code);
        using var host = new Host(f.Store);
        using var response = await host.Http.GetAsync($"api/v1/plans/{f.PlanId}/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ForeignSidAndUnknownPlanRemain404EvenForCorruptForeignEvidence()
    {
        using var f = new Fixture(sid: "S-1-5-18");
        f.Corrupt("UPDATE setup_intents SET session_binding=$binding;", "foreign\0invalid");
        using var host = new Host(f.Store);
        foreach (var plan in new[] { f.PlanId, "unknown-plan" })
        foreach (var endpoint in new[] { "status", "history" })
        {
            using var response = await host.Http.GetAsync($"api/v1/plans/{plan}/{endpoint}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(f.Binding, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task DurableRecurringAndRequiredOnceUseProductionStatusAndHistoryGateway()
    {
        var sid = CurrentSid();
        var binding = DurableAuthorizationIdentity.Format(sid, 99, 12345, 1)!;
        using var f = new RecurringPlanSetupTestFixture();
        f.Create("durable-recurring", prepared: true, sid: sid, session: binding);
        f.Activate("durable-recurring", sid, binding);
        var plan = f.Query.Get("durable-recurring", sid, binding)!.PlanId!;
        var advancement = new SqliteRecurringAdvancementTransaction(f.Store);
        advancement.AdvanceOne(plan, 1, "durable-advance-1", 0, RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(10));
        advancement.AdvanceOne(plan, 1, "durable-advance-2", 1, RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(11));
        var required = new SqliteRequiredOncePlanSetupRepository(f.Store);
        var now = RecurringPlanSetupTestFixture.At(10);
        Assert.Equal(RequiredOncePlanSetupWriteStatus.Created, required.CreateOrGet(new("durable-required", "required-key", sid, binding,
            now, now.AddMinutes(4), now.AddMinutes(1), now.AddMinutes(2), now.AddMinutes(3), TimeSpan.FromSeconds(10), f.Root, "required.mp4"), now).Status);
        var selected = required.SaveSelection("durable-required", sid, binding, new(now, "synthetic-display", new(0, 0, 800, 600),
            new(10, 20, 64, 48), 96, 96, 800, 600, AuthorizedDisplayOrientation.Landscape, new string('a', 64)), now);
        Assert.Equal(RequiredOncePlanSetupWriteStatus.Updated, selected.Status);
        var activated = required.Activate("durable-required", sid, binding, selected.State!.Version, "required-approval", now);
        Assert.Equal(RequiredOncePlanSetupWriteStatus.Updated, activated.Status);
        var before = f.State();
        var gateway = new PlanExecutionStatusGateway(new(new SqliteOperationalStore(f.Store.DatabasePath)));
        Assert.Equal(2L, gateway.Get(plan)!.OccurrenceCount);
        // Recurring IDs retain the existing cursor grammar boundary; this
        // binding regression verifies all rows in one bounded history page.
        var page = gateway.GetPage(plan, 100, null)!;
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Items.Select(item => item.OccurrenceId).Distinct().Count());
        Assert.Null(page.NextCursor);
        Assert.NotNull(gateway.Get(activated.State!.PlanId!));
        Assert.Single(gateway.GetPage(activated.State.PlanId!, 1, null)!.Items);
        using var host = new Host(f.Store);
        foreach (var id in new[] { plan, activated.State.PlanId! })
        foreach (var endpoint in new[] { "status", "history?limit=1" })
        {
            using var response = await host.Http.GetAsync($"api/v1/plans/{id}/{endpoint}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(before, f.State());
    }

    private static string CurrentSid() { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }

    private static string State(SqliteOperationalStore store)
    {
        using var c = store.OpenReadOnlyConnection();
        var tables = new List<string>();
        using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var rows = new List<string>();
        foreach (var table in tables)
        {
            using var command = c.CreateCommand(); command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(table + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        }
        return string.Join("\n", rows);
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "durable-status-" + Guid.NewGuid().ToString("N"));
        internal readonly SqliteOperationalStore Store;
        internal readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal string Sid { get; }
        internal string Binding { get; }
        internal string PlanId => Prepared.PlanId!;
        internal readonly StandingLeasePreparationResult Prepared;
        internal object? Scalar(string sql)
        { using var c = Store.OpenReadOnlyConnection(); using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        internal void Corrupt(string sql, string binding)
        {
            using var c = Store.OpenConnection();
            var triggers = new List<(string Name, string Sql)>();
            using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger';";
                using var reader = command.ExecuteReader(); while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
            void Execute(string text)
            { using var command = c.CreateCommand(); command.CommandText = text; command.Parameters.AddWithValue("$binding", binding); command.ExecuteNonQuery(); }
            Execute("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON;");
            try { foreach (var t in triggers) Execute($"DROP TRIGGER \"{t.Name}\";"); Execute(sql); }
            finally { foreach (var t in triggers) Execute(t.Sql); Execute("PRAGMA foreign_keys=ON; PRAGMA ignore_check_constraints=OFF;"); }
        }
        internal void CloneOwner(bool sameBinding)
        {
            var columns = new List<string>();
            using (var c = Store.OpenReadOnlyConnection()) using (var command = c.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(setup_intents);";
                using var reader = command.ExecuteReader(); while (reader.Read()) columns.Add(reader.GetString(1));
            }
            var values = columns.Select(name => name switch
            { "intent_id" => "'ambiguous-owner'", "idempotency_key" => "'ambiguous-key'", "session_binding" => "$binding", _ => $"\"{name}\"" });
            Corrupt($"INSERT INTO setup_intents SELECT {string.Join(',', values)} FROM setup_intents LIMIT 1;", sameBinding ? Binding : "other-legacy-binding");
        }
        internal Fixture(string? binding = null, string? sid = null)
        {
            using var identity = WindowsIdentity.GetCurrent(); Sid = sid ?? identity.User!.Value;
            Binding = binding ?? DurableAuthorizationIdentity.Format(Sid, 99, 12345, 1)!;
            Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "private.db")); Store.Initialize();
            using (var c = Store.OpenConnection()) using (var command = c.CreateCommand())
            { command.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code='enabled';"; command.ExecuteNonQuery(); }
            var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter("setup-status", "key-status", Sid, Binding,
                Now, Now.AddMinutes(4), Now.AddMinutes(1), Now.AddMinutes(2), Now.AddMinutes(3), TimeSpan.FromSeconds(10), Now.AddMinutes(4), Root, "never-created.mp4");
            Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(Store, () => Now).CreateOrGet(intent).Result);
            var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter("setup-status", Sid, Binding, Now.AddSeconds(1),
                "synthetic-display", new(0, 0, 800, 600), new(10, 20, 64, 48), 96, 96, 800, 600, AuthorizedDisplayOrientation.Landscape, new string('a', 64));
            Prepared = new StandingLeasePreparationService(Store, () => Now.AddSeconds(1)).Prepare(selection);
            Assert.Equal(StandingLeasePreparationResultStatus.Prepared, Prepared.Status);
            var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest("synthetic-approval", PlanId, Prepared.OccurrenceId!, Prepared.LeaseId!,
                Prepared.ScopeId!, Prepared.ScopeDigest!, Sid, Binding, Now.AddSeconds(2));
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated,
                new StandingLeasePreparedIntentApprovalActivationService(Store, () => Now.AddSeconds(2)).Activate("setup-status", receipt).Status);
        }
        public void Dispose() => TestDirectoryCleanup.DeleteOwnedDirectory(Root);
    }
    private sealed class Host : IDisposable
    {
        private readonly RecordingEngine _engine;
        private readonly ApiServer _server;
        internal readonly HttpClient Http;
        internal Host(SqliteOperationalStore store)
        {
            var root = Path.GetDirectoryName(store.DatabasePath)!;
            ApiKeyAuth.InitializeForTesting(root);
            var audit = new AuditLogger(Path.Combine(root, "audit.jsonl")); _engine = new(audit);
            var gateway = new PlanExecutionStatusGateway(new(store));
            _server = new ApiServer(_engine, audit, new Tray(), readiness: null, autoStart: null, ffmpegPrewarmer: null, tracer: null,
                ensureContextStore: null, performanceSummaryProvider: null, standingPlanSetupGateway: null,
                recurringPlanSetupGateway: null, planExecutionStatusGateway: gateway, listenPort: 0, planExecutionHistoryGateway: gateway);
            _server.Start();
            Http = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{_server.BoundPort}/"), Timeout = TimeSpan.FromSeconds(5) };
            Http.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        }
        public void Dispose() { _server.Stop(); _engine.Dispose(); Http.Dispose(); ApiKeyAuth.ResetForTesting(null); }
    }
    private sealed class Tray : ITrayContext
    {
        public string HostMode => "headless"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => throw new InvalidOperationException();
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { } public void SetAllIdle() { } public void ShowError(string text) { }
    }
}
