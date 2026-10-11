using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class BundlePublicationHistoryTests(ITestOutputHelper output)
{
    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task337r", Guid.NewGuid().ToString("N")));
        internal SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal Fixture() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); Writer = new(Store); }
        internal UnifiedHistoryQueryService Query => new(Store, Writer);
        internal OrdinaryRecordingHistorySnapshot Row(string id = "r") => new(id, At, "completed", "display", "display_capture", "Synthetic", 1, "none", null, null, null, null,
            Path.Combine(Root, "Videos", id + ".mp4"), null, null, null, null, null, null, At.AddSeconds(1), At.AddSeconds(2), "duration_reached", 100, 1)
            { TargetKey = OrdinaryTargetHistoryTests.Key };
        internal OrdinaryRecordingBundleOperation Seed(string id = "r")
        { var row = Row(id); var op = OrdinaryRecordingBundleOperation.Create(id, row.OutputPath); Writer.Register(row with { BundleRegistration = new(op) }); return op; }
        internal async Task<OrdinaryRecordingBundleUpdate> Produced(OrdinaryRecordingBundleOperation op)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(op.MediaPath)!); await File.WriteAllBytesAsync(op.MediaPath, new byte[100]);
            var stage = Path.Combine(Root, "stage-" + op.RecordingId); Directory.CreateDirectory(stage);
            var contents = new RecordingBundleContentsEvidence(BundleContentsGenerationTests.Items());
            foreach (var item in contents.Contents) await File.WriteAllBytesAsync(Path.Combine(stage, item.Name), new byte[item.SizeBytes]);
            var result = await new BundlePublicationReceipt.Publisher().PublishAsync(BundlePublicationIdentityTests.RequestFor(op.MediaPath, op.RecordingId, op.OperationId), stage, op.BundlePath, contents, default);
            Assert.NotNull(result.PublicationReceipt);
            return new(op, new("ready", op.BundlePath, null, result.ContentsEvidence) { IdentityEvidence = result.PublicationReceipt.Evidence });
        }
        internal void Accept(OrdinaryRecordingBundleUpdate update)
        { Assert.True(Writer.TryPublishBundle(update)); Assert.Equal(update.Result, Writer.Read(update.Operation.RecordingId)!.Bundle); }
        internal void Execute(string sql, bool raw = false)
        {
            using var c = raw ? new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False") : Store.OpenConnection();
            if (raw) { c.Open(); SqliteOutputPathCollation.Register(c); }
            using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
        internal long Scalar(string sql) { using var c = Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        internal void Reopen() { Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3))); Store = new(Store.DatabasePath); Store.Initialize(); Writer = new(Store); }
        public void Dispose() { Writer.Dispose(); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task PrivateValueReplayThreeProjectionsAndNativeChecksAfterRestart()
    {
        using var f = new Fixture(); var op = f.Seed(); var before = f.Writer.Read("r")!; var update = await f.Produced(op); f.Accept(update);
        var anchors = update.Result.IdentityEvidence!.Anchors;
        Assert.Throws<NotSupportedException>(() => ((IList<PublicationIdentityAnchor>)anchors)[0] = anchors[1]);
        var copied = new BundleIdentityEvidence(op.RecordingId, op.OperationId, op.MediaPath, op.BundlePath, update.Result.ContentsEvidence!, anchors.ToArray());
        f.Accept(update with { Result = update.Result with { IdentityEvidence = copied } }); Assert.Equal(update.Result.IdentityEvidence.GetHashCode(), copied.GetHashCode());
        Assert.Equal(before with { Bundle = null }, f.Writer.Read("r")! with { Bundle = null });
        Assert.Equal(update.Result, Assert.Single(f.Writer.ReadPage(20, null).Items).Bundle);
        Assert.Equal(update.Result, Assert.Single(f.Query.GetPage(Sid, Session, 20, new("ordinary")).Items).Bundle);
        f.Reopen(); f.Writer.RecoverIncomplete(At.AddHours(1)); Assert.Equal(update.Result, f.Writer.Read("r")!.Bundle);
        Assert.All((await new OrdinaryArtifactCheckService(f.Writer).ReadAsync("r"))!.Bundle.Contents!, item => Assert.Equal("match", item.IdentityState));
        Assert.Equal(5, f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_identity_items;"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SqlOrActualCommitFailureRollsBackAndExactRetryRepairs(bool commit)
    {
        using var f = new Fixture(); var op = f.Seed(); var update = await f.Produced(op); var before = f.Writer.Read("r")!;
        if (commit)
        {
            f.Execute("CREATE TABLE owned_commit_fault(id TEXT REFERENCES ordinary_recording_history(recording_id) DEFERRABLE INITIALLY DEFERRED);");
            f.Writer.BeforeBundleCommitForTests = (c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "INSERT INTO owned_commit_fault VALUES('missing');"; cmd.ExecuteNonQuery(); };
        }
        else f.Execute("CREATE TRIGGER fail_identity BEFORE INSERT ON ordinary_bundle_identity_items WHEN NEW.ordinal=3 BEGIN SELECT RAISE(ABORT,'owned fault'); END;");
        Assert.True(f.Writer.TryPublishBundle(update)); Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read("r"));
        foreach (var table in new[] { "ordinary_bundle_identity_items", "ordinary_bundle_contents_items" }) Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM " + table));
        using (var restarted = new SqliteOrdinaryRecordingHistoryStore(f.Store)) Assert.Equal(before, restarted.Read("r"));
        if (commit) f.Writer.BeforeBundleCommitForTests = null; else f.Execute("DROP TRIGGER fail_identity;");
        var opValue = op with { }; var identity = update.Result.IdentityEvidence!;
        f.Accept(new(opValue, update.Result with { IdentityEvidence = new(identity.RecordingId, identity.OperationId, identity.MediaPath, identity.BundlePath, update.Result.ContentsEvidence!, identity.Anchors.ToArray()) }));
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
        output.WriteLine(commit ? "Real deferred FK Commit failure rolled back complete Bundle manifest+identity transaction." : "Fourth identity INSERT fault rolled back status, manifest and prior identity items.");
    }
    [Theory]
    [InlineData("unknown")] [InlineData("identity")] [InlineData("length")]
    public async Task AcceptedEvidenceCannotBeReplacedOrFilledLate(string fault)
    {
        using var f = new Fixture(); var op = f.Seed(); var update = await f.Produced(op);
        if (fault == "unknown") f.Accept(update with { Result = update.Result with { IdentityEvidence = null } }); else f.Accept(update);
        var before = f.Writer.Read("r")!; var changed = update;
        if (fault == "identity")
        {
            var anchors = update.Result.IdentityEvidence!.Anchors.ToArray(); anchors[0] = anchors[0] with { Identity = anchors[0].Identity with { IdLow = anchors[0].Identity.IdLow + 1 } };
            changed = update with { Result = update.Result with { IdentityEvidence = new(op.RecordingId, op.OperationId, op.MediaPath, op.BundlePath, update.Result.ContentsEvidence!, anchors) } };
        }
        if (fault == "length") changed = update with { Result = update.Result with { ContentsEvidence = new(BundleContentsGenerationTests.Items(21)), IdentityEvidence = null } };
        Assert.True(f.Writer.TryPublishBundle(changed)); Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read("r"));
        using var restart = new SqliteOrdinaryRecordingHistoryStore(f.Store); Assert.Equal(before, restart.Read("r"));
    }
    [Theory]
    [InlineData("operation")] [InlineData("recording")] [InlineData("media")] [InlineData("bundle")] [InlineData("manifest")] [InlineData("failed")]
    public async Task ForeignIdentityCannotBeEnqueued(string fault)
    {
        using var f = new Fixture(); var op = f.Seed(); var update = await f.Produced(op); var before = f.Writer.Read("r")!;
        if (fault == "operation") update = update with { Operation = op with { OperationId = new string('a', 32) } };
        if (fault == "recording") update = update with { Operation = op with { RecordingId = "foreign" } };
        if (fault == "media") update = update with { Operation = OrdinaryRecordingBundleOperation.Create("r", Path.Combine(f.Root, "other.mp4")) };
        if (fault == "bundle") update = update with { Result = update.Result with { Path = Path.Combine(f.Root, "other.bundle") } };
        if (fault == "manifest") update = update with { Result = update.Result with { ContentsEvidence = null } };
        if (fault == "failed") update = update with { Result = update.Result with { Status = "failed", ErrorCode = RecordingBundleErrorCodes.GenerationFailed, ContentsEvidence = null } };
        Assert.False(f.Writer.TryPublishBundle(update)); Assert.Equal(before, f.Writer.Read("r"));
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_bundle_identity;")]
    [InlineData("DELETE FROM ordinary_bundle_identity_items;")]
    [InlineData("DELETE FROM ordinary_bundle_identity_items WHERE ordinal=2;")]
    [InlineData("DELETE FROM ordinary_recording_bundles;")]
    [InlineData("UPDATE ordinary_bundle_identity SET media_output_path='C:\\foreign.mp4';")]
    [InlineData("UPDATE ordinary_bundle_identity SET operation_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("UPDATE ordinary_bundle_identity SET bundle_path='C:\\foreign.bundle';")]
    [InlineData("UPDATE ordinary_bundle_identity SET evidence_version=2;")]
    [InlineData("UPDATE ordinary_bundle_identity SET evidence_version=NULL,item_count=0;")]
    [InlineData("UPDATE ordinary_bundle_identity SET item_count=4;")]
    [InlineData("UPDATE ordinary_bundle_identity SET item_count=5.5;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET output_path='C:\\foreign.bundle\\metadata.json' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET output_size_bytes=12 WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET output_size_bytes=11.5 WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET ordinal=1.5 WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET observation_version=2 WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET source='current_path/v1' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET volume_id=zeroblob(8) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET file_id=zeroblob(16) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET volume_id=zeroblob(7) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET file_id=zeroblob(15) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET file_id='0000000000000000' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET name='foreign.json' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET size_bytes=99 WHERE ordinal=0;")]
    [InlineData("DELETE FROM ordinary_bundle_contents_items WHERE ordinal=0;")]
    [InlineData("DROP INDEX idx_ordinary_bundle_identity_ordinal;")]
    [InlineData("DROP TRIGGER ordinary_bundle_identity_initial;")]
    [InlineData("ALTER TABLE ordinary_bundle_identity ADD COLUMN foreign_field TEXT;")]
    [InlineData("DELETE FROM ordinary_bundle_identity_boundary;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE version=35;")]
    public async Task CorruptionNeverDowngradesToUnknownOnAnyProjection(string sql)
    {
        using var f = new Fixture(); var op = f.Seed(); f.Accept(await f.Produced(op));
        f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;" + sql, raw: true);
        if (sql.Contains("schema_migrations", StringComparison.Ordinal)) Assert.Throws<SqliteOperationalStoreException>(() => f.Writer.Read("r"));
        else Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read("r"));
        if (sql.Contains("schema_migrations", StringComparison.Ordinal)) Assert.Throws<SqliteOperationalStoreException>(() => f.Writer.ReadPage(20, null));
        else Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.ReadPage(20, null));
        Assert.Throws<UnifiedHistoryUnavailableException>(() => f.Query.GetPage(Sid, Session, 20, new("ordinary")));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.ReadArtifactFacts("r"));
    }

    [Fact]
    public async Task ArtifactSnapshotFreezesReadyManifestAndAnchorsTogether()
    {
        using var f = new Fixture(); var op = f.Seed(); var update = await f.Produced(op);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.ArtifactSnapshotEstablishedForTests = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var read = Task.Run(() => f.Writer.ReadArtifactFacts("r"));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); f.Accept(update); } finally { release.Set(); }
        var prior = await read; Assert.Equal("generating", prior!.Media.Bundle!.Status); Assert.Null(prior.Media.Bundle.IdentityEvidence); Assert.Null(prior.Media.Bundle.ContentsEvidence);
        f.Writer.ArtifactSnapshotEstablishedForTests = null;
        var current = f.Writer.ReadArtifactFacts("r")!; Assert.Equal(update.Result, current.Media.Bundle);
    }
    [Fact]
    public async Task RecoveryFailureDoesNotConsumeEligibilityAndRejectsOldOperation()
    {
        using var f = new Fixture(); var readyOp = f.Seed("ready"); var ready = await f.Produced(readyOp); f.Accept(ready);
        var pending = f.Seed("pending"); var late = await f.Produced(pending); f.Reopen();
        f.Execute("CREATE TRIGGER fail_recovery BEFORE UPDATE ON ordinary_bundle_identity BEGIN SELECT RAISE(ABORT,'owned recovery fault'); END;");
        Assert.Throws<SqliteException>(() => f.Writer.RecoverIncomplete(At.AddHours(1)));
        f.Execute("DROP TRIGGER fail_recovery;"); f.Writer.RecoverIncomplete(At.AddHours(1));
        Assert.Equal("interrupted_unknown", f.Writer.Read("pending")!.Bundle!.Status);
        Assert.True(f.Writer.TryPublishBundle(late)); Assert.Equal("interrupted_unknown", f.Writer.Read("pending")!.Bundle!.Status);
        Assert.Null(f.Writer.Read("pending")!.Bundle!.IdentityEvidence); Assert.Equal(ready.Result, f.Writer.Read("ready")!.Bundle);
        Assert.Equal(5, f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_identity_items;"));
    }

    [Fact]
    public void TrueV34UpgradePreservesOldManifestAndUnknownIdentityBoundary()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "legacy-v34.db");
        var op = OrdinaryRecordingBundleOperation.Create("legacy", f.Row("legacy").OutputPath);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open(); SqliteOutputPathCollation.Register(c); using var cmd = c.CreateCommand();
            foreach (var m in SqliteSchemaCatalog.Migrations.Where(m => m.Version <= 34))
            {
                cmd.CommandText = m.Definition; cmd.ExecuteNonQuery(); cmd.CommandText = "INSERT INTO schema_migrations VALUES($v,$name,$hash,0);";
                cmd.Parameters.AddWithValue("$v", m.Version); cmd.Parameters.AddWithValue("$name", m.Name); cmd.Parameters.AddWithValue("$hash", m.Checksum); cmd.ExecuteNonQuery(); cmd.Parameters.Clear();
            }
            cmd.CommandText = """
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,started_at_utc,completed_at_utc,output_size_bytes,output_duration_ms,updated_at_utc)
                VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic','none',$media,$at,$at,100,1000,$at);
                INSERT INTO ordinary_recording_bundles VALUES('legacy',$media,$op,'ready',$bundle,NULL);
                UPDATE ordinary_bundle_contents SET evidence_version=1,contents_count=5;
                """;
            cmd.Parameters.AddWithValue("$at", At.UtcTicks); cmd.Parameters.AddWithValue("$media", op.MediaPath); cmd.Parameters.AddWithValue("$op", op.OperationId); cmd.Parameters.AddWithValue("$bundle", op.BundlePath); cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();
            for (var i = 0; i < 5; i++)
            {
                var item = BundleContentsGenerationTests.Items()[i]; cmd.CommandText = "INSERT INTO ordinary_bundle_contents_items VALUES('legacy',$ordinal,$name,$type,$size);";
                cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$ordinal", i); cmd.Parameters.AddWithValue("$name", item.Name); cmd.Parameters.AddWithValue("$type", item.MediaType); cmd.Parameters.AddWithValue("$size", item.SizeBytes); cmd.ExecuteNonQuery();
            }
        }
        var before = UnifiedHistoryProfileFilterTests.RawSnapshot(path, schemaThrough: 34);
        var store = new SqliteOperationalStore(path); store.Initialize(); store.Initialize();
        Assert.Equal(before, UnifiedHistoryProfileFilterTests.RawSnapshot(path, schemaThrough: 34));
        using var writer = new SqliteOrdinaryRecordingHistoryStore(store);
        var legacy = writer.Read("legacy")!; Assert.NotNull(legacy.Bundle!.ContentsEvidence); Assert.Null(legacy.Bundle.IdentityEvidence);
        using var c2 = store.OpenReadOnlyConnection(); using var count = c2.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM ordinary_bundle_identity;"; Assert.Equal(0L, count.ExecuteScalar());
        var fresh = f.Row("fresh"); var freshOp = OrdinaryRecordingBundleOperation.Create("fresh", fresh.OutputPath); writer.Register(fresh with { BundleRegistration = new(freshOp) });
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False"); raw.Open(); using var damage = raw.CreateCommand(); damage.CommandText = "PRAGMA foreign_keys=OFF;DELETE FROM ordinary_bundle_identity WHERE recording_id='fresh';"; damage.ExecuteNonQuery();
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => writer.Read("fresh")); Assert.Equal(legacy, writer.Read("legacy"));
        output.WriteLine("True V34 DB upgraded to V35 without changing V1-V34 data, DDL or receipts. Old manifest has no backfilled identity.");
    }
    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public async Task IndexedIdentityBatchIsOneBoundedQueryWithoutNPlusOne(int limit)
    {
        using var f = new Fixture(); for (var i = 0; i < 101; i++)
        { var op = f.Seed("r" + i.ToString("D3")); if (i < 5) f.Accept(await f.Produced(op)); }
        var query = f.Query; var calls = 0; var plans = new List<string>();
        query.BundleIdentityCommandForTests = (c, t, sql, args) =>
        {
            calls++; Assert.Equal(limit, args.Count); Assert.Contains("LIMIT 501", sql);
            using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value); using var reader = cmd.ExecuteReader(); while (reader.Read()) plans.Add(reader.GetString(3));
        };
        var page = query.GetPage(Sid, Session, limit, new("ordinary")); Assert.Equal(limit, page.Items.Count); Assert.Equal(1, calls);
        Assert.Contains(plans, p => p.Contains("SEARCH c USING INDEX sqlite_autoindex_ordinary_bundle_identity_1"));
        Assert.Contains(plans, p => p.Contains("SEARCH i USING INDEX " + SqliteSchemaV35.IndexName));
        output.WriteLine(string.Join("\n", plans));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FormalCoreLavfiProducerAndAuthenticatedExistingApiExposeOnlyObservations(bool headless)
    {
        using var f = new Fixture();
        var priorData = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"); var priorMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        RecordingEngine? engine = null; ApiServer? api = null; FfmpegCaptureBackend? backend = null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", f.Root); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(f.Root); ApiKeyAuth.InitializeForTesting(f.Root);
            SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, "synthetic-test-display:display_1", DisplayIdentityResolutionStatus.Resolved) });
            var audit = new RetentionSharedOutputPathTests.QuietAudit(); var tray = new ApiTray(headless); var generator = new FfmpegRecordingBundleGenerator();
            engine = new(audit, bundleGenerator: generator); engine.ConfigureOrdinaryRecordingHistory(f.Writer); engine.SetTray(tray);
            backend = new() { VideoInputArgumentsForTests = _ => new[] { "-re", "-f", "lavfi", "-t", "1", "-i", "color=c=black:s=64x64:r=15" } };
            engine.BackendFactory = _ => (backend, "ffmpeg");
            engine.CreateRecording(new JsonObject { ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 }, ["duration_seconds"] = 1, ["countdown_seconds"] = 0,
                ["output"] = new JsonObject { ["directory"] = f.Root, ["filename"] = "core.mp4", ["conflict_policy"] = "fail" } }, "task337-synthetic", tray);
            tray.Confirm!(ConfirmationDecision.Approve()); var recording = Assert.Single(engine._recs.Values);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && f.Writer.Read(recording.Id)?.Bundle?.Status is not ("ready" or "failed")) await Task.Delay(20);
            var row = f.Writer.Read(recording.Id)!; Assert.Equal(RecState.completed, recording.State); Assert.Equal("ready", row.Bundle!.Status); Assert.NotNull(row.Bundle.IdentityEvidence);
            Assert.Equal(recording.Id, row.Bundle.IdentityEvidence.RecordingId);
            Assert.Equal(recording.OrdinaryBundleOperation!.OperationId, row.Bundle.IdentityEvidence.OperationId);
            api = new(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
                unifiedHistoryGateway: new SqliteUnifiedHistoryGateway(new(f.Store, f.Writer, headless), () => (Sid, Session)), artifactCheckGateway: new SqliteOrdinaryArtifactCheckGateway(new(f.Writer))); api.Start();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{api.BoundPort}"), Timeout = TimeSpan.FromSeconds(8) };
            var endpoint = "/api/v1/recording-history/" + recording.Id + "/artifacts/check";
            using (var denied = await client.GetAsync(endpoint)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            using (var bad = await client.GetAsync(endpoint + "?path=foreign")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            using (var response = await client.GetAsync(endpoint))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text);
                Assert.All(json.RootElement.GetProperty("data").GetProperty("bundle").GetProperty("contents").EnumerateArray(), item => Assert.Equal("match", item.GetProperty("identity_state").GetString()));
                foreach (var privateName in new[] { "file_id", "volume_id", "operation_id", "receipt", "anchor" }) Assert.DoesNotContain(privateName, text, StringComparison.OrdinalIgnoreCase);
            }
            using (var response = await client.GetAsync("/api/v1/capabilities"))
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var capability = json.RootElement.GetProperty("data").GetProperty("ordinary_artifact_check");
                Assert.Equal("media_and_five_bundle_publication_anchors", capability.GetProperty("identity_scope").GetString());
                Assert.Equal(6, capability.GetProperty("max_files").GetInt32()); Assert.Equal(64, capability.GetProperty("max_distinct_ancestors").GetInt32()); Assert.Equal(3000, capability.GetProperty("deadline_ms").GetInt32());
                Assert.False(capability.GetProperty("deletion_supported").GetBoolean()); Assert.False(capability.GetProperty("content_verification").GetBoolean());
            }
            output.WriteLine("Authorized synthetic lavfi backend -> real Core operation -> formal Bundle generator -> V35 writer -> existing authenticated artifact API five matches, private evidence absent.");
        }
        finally
        {
            api?.Stop(); engine?.Dispose(); backend?.Dispose(); ApiKeyAuth.ResetForTesting(null); SystemQuery.SetDisplayTopologyProvider(null); DataDirResolver.ClearOverride();
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", priorData); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", priorMode);
        }
    }
    private sealed class ApiTray(bool headless) : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode => headless ? "headless" : "tray"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => Confirm = c;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { } public void SetAllIdle() { } public void ShowError(string s) { }
    }
}
