using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionBundleProtectionTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        internal readonly UnifiedHistoryTestDatabase Db = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task330", Guid.NewGuid().ToString("N")));
        internal readonly RetentionPreviewQueryTests.MetadataFake Files = new();
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal UnifiedHistoryQueryService Query;
        internal RetentionPreviewQueryService Preview;
        internal int OldProbes, Selected;
        internal readonly DateTimeOffset Before = At.AddHours(1);
        private readonly bool _headless;
        private readonly string? _prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        private ApiServer? _api;
        private RecordingEngine? _engine;
        internal Fixture(bool headless = false)
        {
            _headless = headless; Writer = Db.Ordinary; Query = null!; Preview = null!; Bind();
        }
        private void Bind()
        {
            Query = new(Db.Store, Writer, _headless);
            Query.OrdinaryFileProbeForTests = _ => { OldProbes++; throw new InvalidOperationException("SECRET_EARLY_PROBE"); };
            Query.CandidatesSelectedForTests = (_, n) => Selected += n;
            Preview = new(Query.GetOrdinaryEvidencePage, Db.Root, Files, Files, () => At.AddDays(5));
        }
        internal OrdinaryRecordingBundleOperation? Seed(string id, string? state, bool anchor = true)
        {
            var row = Db.Recording(id, completed: true) with { OutputPath = Path.Combine(Db.Root, "Videos", id + ".mp4") };
            if (anchor) row = PublicationAnchorTestFixture.Anchored(row); // Synthetic signing; no native publication assertion.
            var op = state is null or "not_applicable" ? null : OrdinaryRecordingBundleOperation.Create(id, row.OutputPath);
            if (state is not null) row = row with { BundleRegistration = new(op) };
            Writer.Register(row);
            if (state is "ready" or "failed") Complete(op!, state);
            if (state == "interrupted_unknown")
            {
                Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3))); Writer = new(Db.Store);
                Writer.RecoverIncomplete(At.AddHours(2)); Bind();
            }
            return op;
        }
        internal void Complete(OrdinaryRecordingBundleOperation op, string state)
        {
            Assert.True(Writer.TryPublishBundle(new(op, new(state, op.BundlePath, state == "failed" ? RecordingBundleErrorCodes.FrameExtractFailed : null))));
            Assert.Equal(state, Writer.Read(op.RecordingId)!.Bundle!.Status);
        }
        internal RetentionPreviewPage Page(int limit = 20, string? cursor = null) => Preview.GetPage(Sid, Session, limit, Before, cursor);
        internal void NoProbes() { Assert.Empty(Files.Attributes); Assert.Empty(Files.Observations); Assert.Empty(Files.Lengths); Assert.Equal(0, OldProbes); }
        internal HttpClient Api(bool auth = true)
        {
            if (_api is null)
            {
                Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Db.Root); ApiKeyAuth.InitializeForTesting(Db.Root);
                var audit = new RetentionSharedOutputPathTests.QuietAudit(); var tray = new Tray(_headless);
                _engine = new(audit); _engine.SetTray(tray); _engine.BackendFactory = _ => throw new InvalidOperationException("No capture");
                _api = new(_engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
                    unifiedHistoryGateway: new SqliteUnifiedHistoryGateway(Query, () => (Sid, Session)),
                    retentionPreviewGateway: new SqliteRetentionPreviewGateway(Preview, () => (Sid, Session)));
                _api.Start();
            }
            var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{_api.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            if (auth) client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            return client;
        }
        public void Dispose()
        {
            _api?.Stop(); _engine?.Dispose(); Writer.Dispose(); Db.Dispose();
            ApiKeyAuth.ResetForTesting(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _prior);
        }
    }
    private sealed class Tray(bool headless) : ITrayContext
    {
        public string HostMode => headless ? "headless" : "tray";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => throw new InvalidOperationException("No UI");
        public void RequestRegionSelection(int t, Action<string,int,int,int,int,string,string> c) => throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p) => throw new InvalidOperationException("No capture");
        public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { }
        public void ShowError(string s) => throw new InvalidOperationException("No UI");
    }
    private const string Endpoint = "/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z";
    private static async Task<JsonElement> Data(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.GetProperty("data").Clone();
    }
    private static string Reason(string? state) => state switch
    {
        null => "bundle_evidence_unknown", "generating" => "bundle_generation_in_progress", "ready" => "bundle_artifact_reference",
        "failed" or "interrupted_unknown" => "bundle_artifact_state_unknown", _ => "age_rule_match"
    };

    [Theory]
    [InlineData(null, false)] [InlineData("generating", false)] [InlineData("ready", false)]
    [InlineData("failed", false)] [InlineData("interrupted_unknown", false)] [InlineData("not_applicable", false)]
    [InlineData(null, true)] [InlineData("generating", true)] [InlineData("ready", true)]
    [InlineData("failed", true)] [InlineData("interrupted_unknown", true)] [InlineData("not_applicable", true)]
    public async Task FormalWriterSnapshotAndApiProtectSixFactsInBothHostScopes(string? state, bool headless)
    {
        using var f = new Fixture(headless); f.Seed("target", state); var before = f.Db.Snapshot();
        using var client = f.Api(); var data = await Data(client, Endpoint);
        var item = Assert.Single(data.GetProperty("items").EnumerateArray()); Assert.Equal(Reason(state), item.GetProperty("reason_code").GetString());
        Assert.Equal(state == "not_applicable" ? "age_candidate" : "protected", item.GetProperty("evaluation").GetString());
        Assert.True(item.GetProperty("age_candidate").GetBoolean()); Assert.Equal(12, item.GetProperty("recorded_bytes").GetInt64());
        Assert.Equal(Path.Combine(f.Db.Root, "Videos", "target.mp4"), item.GetProperty("recorded_path").GetString());
        var sampled = state == "not_applicable"; Assert.Equal(sampled ? 12 : 0, data.GetProperty("observed_candidate_bytes").GetInt64());
        Assert.Equal(sampled ? 1 : 0, data.GetProperty("observed_candidate_file_count").GetInt32());
        Assert.Equal(sampled ? 0 : 1, data.GetProperty("unknown_candidate_file_count").GetInt32());
        Assert.Equal(1, data.GetProperty("age_candidate_count").GetInt32()); Assert.Equal(sampled, data.GetProperty("observed_bytes_complete").GetBoolean());
        if (sampled) Assert.Single(f.Files.Observations); else { Assert.Equal(JsonValueKind.Null, item.GetProperty("observed_file_bytes").ValueKind); f.NoProbes(); }
        Assert.True(data.GetProperty("read_only").GetBoolean());
        foreach (var name in new[] { "deletion_supported", "reference_protection_ready", "physical_identity_protection_ready", "bundle_included" }) Assert.False(data.GetProperty(name).GetBoolean());
        var json = data.GetRawText(); foreach (var secret in new[] { "operation_id", "proof", "file_id", "volume_id", Sid, Session }) Assert.DoesNotContain(secret, json);
        Assert.Equal(before, f.Db.Snapshot()); Assert.Equal(0, f.OldProbes); Assert.True(f.Selected <= 21);
        output.WriteLine($"{state ?? "null"}/{headless}: reason={Reason(state)}, ancestor={f.Files.Attributes.Count}, leaf={f.Files.Observations.Count}, old={f.OldProbes}; unknown={(sampled ? 0 : 1)}, bytes={(sampled ? 12 : 0)}");
    }

    [Theory] [InlineData("ready")] [InlineData("failed")]
    public async Task BoundResultChangesProtectionWithoutChangingMediaOrAnchor(string terminal)
    {
        using var f = new Fixture(); var op = f.Seed("target", "generating")!; using var client = f.Api();
        var media = f.Writer.Read("target")!; var anchor = f.Db.Snapshot().Split('\n').Single(s => s.StartsWith("ordinary_publication_anchors:"));
        Assert.Equal(Reason("generating"), (await Data(client, Endpoint)).GetProperty("items")[0].GetProperty("reason_code").GetString());
        f.Complete(op, terminal);
        Assert.Equal(Reason(terminal), (await Data(client, Endpoint)).GetProperty("items")[0].GetProperty("reason_code").GetString());
        Assert.Equal(media with { Bundle = f.Writer.Read("target")!.Bundle }, f.Writer.Read("target"));
        Assert.Contains(anchor, f.Db.Snapshot().Split('\n')); f.NoProbes();
    }

    [Theory] [InlineData("ready")] [InlineData("failed")]
    public async Task BarrierCommitKeepsWholePageOldAndNextPageNew(string terminal)
    {
        using var f = new Fixture(); var a = f.Seed("a", "generating")!; var b = f.Seed("b", "generating")!;
        using var client = f.Api(); using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Query.SnapshotEstablishedForTests = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var reading = Data(client, Endpoint); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { f.Complete(a, terminal); f.Complete(b, terminal); } finally { release.Set(); }
        var old = await reading; Assert.All(old.GetProperty("items").EnumerateArray(), i => Assert.Equal(Reason("generating"), i.GetProperty("reason_code").GetString()));
        f.Query.SnapshotEstablishedForTests = null;
        var next = await Data(client, Endpoint); Assert.All(next.GetProperty("items").EnumerateArray(), i => Assert.Equal(Reason(terminal), i.GetProperty("reason_code").GetString())); f.NoProbes();
        output.WriteLine($"Read transaction barrier: both old generating; both new {terminal}; probes=0.");
    }

    [Fact]
    public async Task CursorMembershipIsStableWhileBundleProtectionIsLiveAndAuthPrecedesCorruptDatabase()
    {
        using var f = new Fixture(); f.Seed("a", "generating"); var b = f.Seed("b", "generating")!;
        using var client = f.Api(); var first = await Data(client, Endpoint + "&limit=1"); var cursor = first.GetProperty("next_cursor").GetString()!;
        f.Seed("new", "ready"); f.Complete(b, "ready");
        var next = await Data(client, Endpoint + "&limit=100&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal("ordinary:b", Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("entry_id").GetString());
        Assert.Equal(Reason("ready"), next.GetProperty("items")[0].GetProperty("reason_code").GetString()); Assert.Equal(JsonValueKind.Null, next.GetProperty("next_cursor").ValueKind);
        using var mismatch = await client.GetAsync(Endpoint.Replace("02:00:00Z", "02:00:01Z") + "&cursor=" + Uri.EscapeDataString(cursor)); Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        f.Db.RawExecute("PRAGMA foreign_keys=OFF; DROP TABLE ordinary_recording_bundles;"); var selected = f.Selected;
        using var deniedClient = f.Api(false); using var denied = await deniedClient.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); Assert.Equal(selected, f.Selected);
        using var unavailable = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode); f.NoProbes();
    }

    [Theory]
    [InlineData("status", "not_completed")] [InlineData("cutoff", "completion_not_before_cutoff")]
    [InlineData("lexical", "not_mp4")] [InlineData("outside", "outside_videos")]
    [InlineData("nested", "nested_relationship")] [InlineData("shared", "shared_output_path")]
    [InlineData("planned", "planned_output_path")] [InlineData("anchor", "publication_identity_missing")]
    public async Task EarlierReasonsPrecedeUnknownOrGeneratingBundle(string kind, string reason)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Db.Root, "Videos", "target.mp4");
        if (kind == "planned")
        {
            var scope = new FutureWindowAuthorizationScope("frozen", "private-key", new string('a', 64), Sid, Session,
                new FutureWindowExecutableIdentity(1, @"C:\Nonexistent\Player.exe", "fixture-identity", new string('a', 64), null, null),
                null, null, 60, 300, Path.GetDirectoryName(path)!, Path.GetFileName(path), "pending", At, null, null, null, null, 0);
            f.Db.Future.CreateOrGet(scope);
        }
        var row = f.Db.Recording("target", completed: true) with { OutputPath = kind == "lexical" ? path + ".png" : kind == "outside" ? Path.Combine(f.Db.Root, "Outside", "target.mp4") : path };
        if (kind == "status") row = f.Db.Recording("target");
        if (kind == "cutoff") row = row with { CompletedAtUtc = f.Before };
        if (kind == "nested") row = row with { NestedRole = "outer" };
        if (kind != "anchor") row = PublicationAnchorTestFixture.Anchored(row);
        if (row.Status == "completed") row = row with { BundleRegistration = new(OrdinaryRecordingBundleOperation.Create(row.RecordingId, row.OutputPath)) };
        f.Writer.Register(row);
        if (kind == "shared") f.Writer.Register(f.Db.Recording("other", f.Before.AddDays(1)) with { OutputPath = path });
        using var client = f.Api(); var data = await Data(client, Endpoint);
        Assert.Equal(reason, Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("reason_code").GetString()); f.NoProbes();
    }

    [Theory]
    [InlineData("status")] [InlineData("path")] [InlineData("missing_path")] [InlineData("error")]
    [InlineData("ready_error")] [InlineData("failed_error")] [InlineData("operation")] [InlineData("missing_operation")]
    [InlineData("media")] [InlineData("na_path")] [InlineData("unknown_operation")] [InlineData("schema")] [InlineData("receipt")]
    public async Task FormalDatabaseDamageFailsWholeApiPageBeforeEarlierReasonsOrProbes(string fault)
    {
        using var f = new Fixture(); f.Seed("a", "not_applicable"); f.Seed("b", "generating");
        // Even an earlier nested reason cannot conceal invalid stored Bundle facts.
        f.Db.Execute("UPDATE ordinary_recording_history SET nested_role='outer' WHERE recording_id='b';");
        var change = fault switch
        {
            "status" => "status_code='other'", "path" => "bundle_path='D:\\secret\\other.bundle'", "missing_path" => "bundle_path=NULL",
            "error" => "error_code='SECRET_ERROR'", "ready_error" => "status_code='ready',error_code='bundle_generation_failed'",
            "failed_error" => "status_code='failed',error_code=NULL", "operation" => "operation_id='BAD'", "missing_operation" => "operation_id=NULL",
            "media" => "media_output_path='D:\\secret\\other.mp4'", "na_path" => "status_code='not_applicable',operation_id=NULL",
            "unknown_operation" => "status_code='interrupted_unknown',error_code='bundle_interrupted_unknown'", _ => ""
        };
        if (fault == "schema") f.Db.Execute("ALTER TABLE ordinary_recording_bundles ADD COLUMN unexpected TEXT;");
        else if (fault == "receipt") f.Db.Execute("UPDATE schema_migrations SET definition_checksum=$checksum WHERE version=31;", ("$checksum", new string('f', 64)));
        else f.Db.Corrupt("UPDATE ordinary_recording_bundles SET " + change + " WHERE recording_id='b';");
        using var client = f.Api(); using var response = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(); Assert.Contains("RETENTION_PREVIEW_UNAVAILABLE", json);
        foreach (var secret in new[] { "SECRET", "Sqlite", "items", "operation_id", "proof", "file_id" }) Assert.DoesNotContain(secret, json); f.NoProbes();
    }

    [Theory]
    [InlineData("status")] [InlineData("path")] [InlineData("missing_path")] [InlineData("generating_error")]
    [InlineData("ready_error")] [InlineData("failed_error")] [InlineData("unknown_error")] [InlineData("na_path")] [InlineData("na_error")] [InlineData("media_status")]
    public void InjectedProviderInvalidTupleCannotFallThroughOrHideBehindCutoff(string fault)
    {
        using var f = new Fixture(); f.Seed("target", "generating"); var page = f.Query.GetOrdinaryEvidencePage(Sid, Session, 20, f.Before, null);
        var evidence = Assert.Single(page.Items); var b = evidence.Entry.Bundle!;
        b = fault switch
        {
            "status" => b with { Status = "other" }, "path" => b with { Path = "D:\\secret\\other.bundle" }, "missing_path" => b with { Path = null },
            "ready_error" => b with { Status = "ready", ErrorCode = "bundle_generation_failed" }, "failed_error" => b with { Status = "failed" },
            "unknown_error" => b with { Status = "interrupted_unknown" }, "na_path" => b with { Status = "not_applicable" },
            "na_error" => new("not_applicable", null, "SECRET"), "generating_error" => b with { ErrorCode = "SECRET" }, _ => b
        };
        var entry = evidence.Entry with { Bundle = b, CompletedAtUtc = f.Before, Status = fault == "media_status" ? "failed" : "completed" };
        var service = new RetentionPreviewQueryService((_,_,_,_,_) => page with { Items = new[] { evidence with { Entry = entry, PublicationAnchor = null } } }, f.Db.Root, f.Files, f.Files, () => At.AddDays(5));
        Assert.Throws<RetentionPreviewUnavailableException>(() => service.GetPage(Sid, Session, 20, f.Before)); f.NoProbes();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task V30LegacyUpgradeKeepsNullAndMissingAnchorPriorityWithoutBackfill(bool anchored)
    {
        using var f = new Fixture(); f.Seed("legacy", null, anchored);
        f.Db.Execute("DROP TRIGGER ordinary_bundle_identity_initial; DROP TABLE ordinary_bundle_identity_items; DROP TABLE ordinary_bundle_identity; DROP TABLE ordinary_bundle_identity_boundary; DELETE FROM schema_migrations WHERE version=35; DROP TRIGGER ordinary_window_target_initial; DROP TABLE ordinary_recording_window_targets; DROP TABLE ordinary_window_target_boundary; DELETE FROM schema_migrations WHERE version=34; DROP TRIGGER ordinary_bundle_contents_initial; DROP TABLE ordinary_bundle_contents_items; DROP TABLE ordinary_bundle_contents; DROP TABLE ordinary_bundle_contents_boundary; DELETE FROM schema_migrations WHERE version=33; DROP TRIGGER ordinary_recording_target_initial; DROP TABLE ordinary_recording_targets; DROP TABLE ordinary_recording_target_boundary; DELETE FROM schema_migrations WHERE version=32; DROP TABLE ordinary_recording_bundles; DELETE FROM schema_migrations WHERE version=31;");
        using (var c = new SqliteConnection($"Data Source={f.Db.Store.DatabasePath};Pooling=False;Mode=ReadOnly"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(30L, cmd.ExecuteScalar()); // Current business connection correctly refuses v30 until upgrade.
        }
        var receipts = UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath, schemaThrough: 30);
        f.Db.Store.Initialize(); f.Db.Store.Initialize(); Assert.Equal(receipts, UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath, schemaThrough: 30));
        Assert.Equal(0, f.Db.Scalar("SELECT COUNT(*) FROM ordinary_recording_bundles;")); Assert.Null(f.Writer.Read("legacy")!.Bundle);
        // The public history projection still has its old existence-probe contract;
        // only retention disables it. No physical file is needed for this fixture.
        f.Query.OrdinaryFileProbeForTests = _ => false;
        using var client = f.Api(); var history = await Data(client, "/api/v1/history?source=ordinary"); Assert.Equal(JsonValueKind.Null, history.GetProperty("items")[0].GetProperty("bundle").ValueKind);
        f.Query.OrdinaryFileProbeForTests = _ => { f.OldProbes++; throw new InvalidOperationException("Early retention probe"); };
        var before = f.Db.Snapshot(); var preview = await Data(client, Endpoint);
        Assert.Equal(anchored ? "bundle_evidence_unknown" : "publication_identity_missing", preview.GetProperty("items")[0].GetProperty("reason_code").GetString());
        Assert.Equal(before, f.Db.Snapshot()); f.NoProbes();
    }

    [Fact]
    public async Task MixedPageCountsOnlyNotApplicableReliableMediaBytes()
    {
        using var f = new Fixture(); f.Seed("interrupted", "interrupted_unknown");
        foreach (var state in new string?[] { null, "generating", "ready", "failed", "not_applicable" }) f.Seed(state ?? "legacy", state);
        using var client = f.Api(); var before = f.Db.Snapshot(); var data = await Data(client, Endpoint);
        Assert.Equal(6, data.GetProperty("evaluated_count").GetInt32()); Assert.Equal(6, data.GetProperty("age_candidate_count").GetInt32());
        Assert.Equal(5, data.GetProperty("unknown_candidate_file_count").GetInt32()); Assert.Equal(1, data.GetProperty("observed_candidate_file_count").GetInt32());
        Assert.Equal(12, data.GetProperty("observed_candidate_bytes").GetInt64()); Assert.False(data.GetProperty("observed_bytes_complete").GetBoolean());
        Assert.Single(f.Files.Observations); Assert.EndsWith("not_applicable.mp4", f.Files.Observations[0]); Assert.Equal(0, f.OldProbes); Assert.Equal(before, f.Db.Snapshot());
        output.WriteLine("Six facts / six age candidates / five unknown samples / one stable MP4 sample = 12 bytes; attachment sizes excluded.");
    }
}
