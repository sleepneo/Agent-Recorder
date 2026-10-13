using System.Net;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class PlanMediaPublicationAnchorTests
{
    [Theory]
    [InlineData("plan_id")][InlineData("occurrence_id")][InlineData("owner_kind")][InlineData("owner_id")]
    [InlineData("frozen_output_path")][InlineData("output_path")][InlineData("source")]
    public async Task PrivateRawTextNulTailCannotMasqueradeAsOriginalBinding(string field)
    {
        using var f=await Fixture.Open("required_once");f.Complete();
        f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;UPDATE plan_media_publication_anchors SET "+field+"="+field+" || char(0) || 'foreign_tail';");var before=f.Snapshot();
        Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task OriginalReceiptLengthDamageIsRejectedWithoutMediaReads(string kind)
    {
        using var f=await Fixture.Open(kind);f.Complete();f.Execute("PRAGMA ignore_check_constraints=ON;UPDATE plan_media_publication_anchors SET receipt_size_bytes=receipt_size_bytes+1;");
        Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task RecordedSuccessfulSizeMustMatchOriginalNativeReceiptLength(string kind)
    {
        using var f=await Fixture.Open(kind);f.Complete();f.Execute("PRAGMA ignore_check_constraints=ON;UPDATE plan_media_publication_anchors SET output_size_bytes=output_size_bytes+1;");var before=f.Snapshot();
        Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task SafetyTerminationCannotReceiveLateSuccessfulPublication(string kind)
    {
        using var f=await Fixture.Open(kind);var meta=f.Backend.Publish();var at=f.Now.AddSeconds(1);
        if(f.Standing is not null)Assert.True(f.Standing.CompleteTermination(StandingLeaseLifecycleTerminationKind.SafetyStop,0,meta,at).Succeeded);
        else if(f.Recurring is not null)Assert.True(f.Recurring.CompleteTermination(RecurringLeaseLifecycleTerminationKind.SafetyStop,0,meta,at).Succeeded);
        else Assert.True(f.RequiredRepository!.MarkPostCommitFailure(f.Owner,"safety_stop",at,sessionInterrupted:true).Succeeded);
        var before=f.Snapshot();Assert.Null(f.Anchor());f.SettleDirect(meta);Assert.Equal(before,f.Snapshot());Assert.Null(f.Anchor());
    }
    [Fact]
    public async Task RequiredExecutionZeroRowSettlementRollsBackRunOccurrenceAndMarker()
    {
        using var f=await Fixture.Open("required_once");var meta=f.Backend.Publish();
        f.Execute("CREATE TRIGGER task348_ignore BEFORE UPDATE ON required_once_execution_authorizations WHEN NEW.status_code='settled' BEGIN SELECT RAISE(IGNORE); END;");var before=f.Snapshot();
        Assert.Throws<Phase3PersistenceException>(()=>f.SettleDirect(meta));Assert.Equal(before,f.Snapshot());Assert.Null(f.Anchor());
        f.Execute("DROP TRIGGER task348_ignore;");f.SettleDirect(meta);Assert.Equal(meta.PublicationReceipt!.Anchor,f.Anchor());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task PublicOnlyMetaSuccessRemainsUnknown(string kind)
    {
        using var f=await Fixture.Open(kind);var meta=f.Backend.Publish();meta.PublicationReceipt=null;f.SettleDirect(meta);
        Assert.Equal("completed",f.Status().LatestOccurrence!.Status);Assert.Null(f.Anchor());Assert.Single(f.History().Items);Assert.Single(f.Unified().Items);
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task DriverReportsPersistenceFailureDespitePublishedMedia(string kind)
    {
        using var f=await Fixture.Open(kind);f.Execute("CREATE TRIGGER task348_abort BEFORE UPDATE ON plan_media_publication_anchors BEGIN SELECT RAISE(ABORT,'task348_driver'); END;");
        f.Backend.Finish();Assert.True(File.Exists(f.Backend.Path));Assert.NotNull(f.Backend.Meta!.PublicationReceipt);Assert.NotEqual("completed",f.Status().LatestOccurrence!.Status);Assert.Null(f.Anchor());Assert.Equal(0,f.Scalar("SELECT count(*) FROM recording_run_output_evidence;"));
        if(f.Engine is not null)Assert.Equal(RecState.failed,f.Engine._recs[f.Run].State);
    }
    [Theory]
    [InlineData("standing",false)][InlineData("standing",true)][InlineData("recurring",false)][InlineData("recurring",true)][InlineData("required_once",false)][InlineData("required_once",true)]
    public async Task FailedOrInterruptedWinnerCannotAcquireLatePublication(string kind,bool interrupted)
    {
        using var f=await Fixture.Open(kind);var meta=f.Backend.Publish();var at=f.Now.AddSeconds(1);
        if(f.Standing is not null)Assert.True(f.Standing.CompleteTermination(interrupted?StandingLeaseLifecycleTerminationKind.SessionInterrupted:StandingLeaseLifecycleTerminationKind.BackendStartFailure,1,meta,at).Succeeded);
        else if(f.Recurring is not null)Assert.True(f.Recurring.CompleteTermination(interrupted?RecurringLeaseLifecycleTerminationKind.SessionInterrupted:RecurringLeaseLifecycleTerminationKind.BackendStartFailure,1,meta,at).Succeeded);
        else Assert.True(f.RequiredRepository!.MarkPostCommitFailure(f.Owner,"task348_failure",at,sessionInterrupted:interrupted,startOutcomeKnownFailed:!interrupted).Succeeded);
        Assert.Equal("blocked",f.Status().LatestOccurrence!.Status);Assert.Null(f.Anchor());Assert.Equal(0,f.Scalar("SELECT count(*) FROM recording_run_output_evidence;"));
        var before=f.Snapshot();f.SettleDirect(meta);Assert.Equal(before,f.Snapshot());Assert.Single(f.History().Items);Assert.Single(f.Unified().Items);
    }
    [Theory][InlineData("standing")][InlineData("recurring")]
    public async Task CommittedWithoutFrameRemainsUnknownAfterLateReceipt(string kind)
    {
        using var f=await Fixture.Open(kind,begin:false);var meta=f.Backend.Publish();
        if(f.Standing is not null)Assert.True(f.Standing.CompleteTermination(StandingLeaseLifecycleTerminationKind.SessionInterrupted,0,meta,f.Now.AddSeconds(1)).Succeeded);
        else Assert.True(f.Recurring!.CompleteTermination(RecurringLeaseLifecycleTerminationKind.SessionInterrupted,0,meta,f.Now.AddSeconds(1)).Succeeded);
        Assert.Equal("started_unknown",f.Status().LatestOccurrence!.Run!.Status);var before=f.Snapshot();f.SettleDirect(meta);Assert.Equal(before,f.Snapshot());Assert.Null(f.Anchor());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task TwoConcurrentLegalSettlementsHaveOnlyOneImmutableWinner(string kind)
    {
        using var f=await Fixture.Open(kind);var meta=f.Backend.Publish();await Task.WhenAll(Task.Run(()=>f.SettleDirect(meta)),Task.Run(()=>f.SettleDirect(meta)));
        var before=f.Snapshot();f.SettleDirect(meta);Assert.Equal(before,f.Snapshot());Assert.Equal(meta.PublicationReceipt!.Anchor,f.Anchor());Assert.Equal(1,f.Scalar("SELECT count(*) FROM plan_media_publication_anchors WHERE plan_id IS NOT NULL;"));
    }
    [Theory][InlineData("run")][InlineData("occurrence")][InlineData("plan")][InlineData("owner")][InlineData("kind")][InlineData("path")][InlineData("length")]
    public async Task ForeignPrivateWriteCannotSignTheOriginalWinner(string mutation)
    {
        using var f=await Fixture.Open("required_once");f.Complete();var before=f.Snapshot();var meta=f.Backend.Meta!;
        if(mutation=="length")meta.SizeBytes++;if(mutation=="path")meta.OutputPath=f.Backend.Path+".foreign";
        using var c=f.Store.OpenConnection();using var t=c.BeginTransaction();
        Assert.Throws<InvalidDataException>(()=>PlanMediaPublicationStorage.Write(c,t,mutation=="plan"?"foreign":f.Plan,mutation=="occurrence"?"foreign":f.Occurrence,mutation=="run"?"foreign":f.Run,mutation=="kind"?"standing":f.Kind,mutation=="owner"?"foreign":f.Owner,f.Backend.Path,meta));t.Rollback();Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData("detail")][InlineData("page")][InlineData("unified")]
    public async Task WalMutationCannotMixExistingProjectionAndPrivatePublication(string query)
    {
        using var f=await Fixture.Open("standing");f.Complete();var calls=0;
        void Mutation(SqliteConnection c,SqliteTransaction t,string sql,IReadOnlyList<KeyValuePair<string,object>> args){calls++;f.Execute("DELETE FROM plan_media_publication_anchors;");}
        var p=new PlanExecutionStatusQueryService(f.Store){PublicationCommandForTests=Mutation};using var writer=new SqliteOrdinaryRecordingHistoryStore(f.Store);
        if(query=="detail")p.Get(f.Plan,f.Sid,f.Session);else if(query=="page")p.GetHistory(f.Plan,f.Sid,f.Session,100);else new UnifiedHistoryQueryService(f.Store,writer){PlanPublicationCommandForTests=Mutation}.GetPage(f.Sid,f.Session,100,new("plan"));
        Assert.Equal(1,calls);Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task PendingOrphanAndMissingMarkerAreNeverHiddenByStatus(string kind)
    {
        using var f=await Fixture.Open(kind);var before=f.Snapshot();
        f.Execute("PRAGMA ignore_check_constraints=ON;UPDATE plan_media_publication_anchors SET source='orphan';");
        Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());
        f.Execute("UPDATE plan_media_publication_anchors SET source=NULL;");Assert.Equal(before,f.Snapshot());f.Execute("DELETE FROM plan_media_publication_anchors;");Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());
    }
    [Fact]
    public async Task GenuineV40UpgradePreservesLegacyRowsAndDefinitionsTwiceWithoutBackfill()
    {
        using var f=await Fixture.Open("standing");f.Complete();var path=Path.Combine(f.Root,"genuine-v40.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();
            foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=40))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="ATTACH DATABASE $seed AS seed;";cmd.Parameters.AddWithValue("$seed",f.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'schema_migrations';";var tables=new List<string>();using(var rd=cmd.ExecuteReader())while(rd.Read())tables.Add(rd.GetString(0));
            cmd.CommandText="PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON;";cmd.ExecuteNonQuery();
            var triggers=new List<(string Name,string Sql)>();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger';";using(var rd=cmd.ExecuteReader())while(rd.Read())triggers.Add((rd.GetString(0),rd.GetString(1)));
            foreach(var trigger in triggers){cmd.CommandText=$"DROP TRIGGER \"{trigger.Name}\";";cmd.ExecuteNonQuery();}
            foreach(var table in tables){cmd.CommandText=$"DELETE FROM \"{table}\"; INSERT INTO \"{table}\" SELECT * FROM seed.\"{table}\";";cmd.ExecuteNonQuery();}
            foreach(var trigger in triggers){cmd.CommandText=trigger.Sql;cmd.ExecuteNonQuery();}
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:40);var legacy=new SqliteOperationalStore(path);legacy.Initialize();Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:40));var once=UnifiedHistoryProfileFilterTests.RawSnapshot(path);legacy.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));
        var query=new PlanExecutionStatusQueryService(legacy);Assert.Equal("completed",query.Get(f.Plan,f.Sid,f.Session)!.LatestOccurrence!.Status);Assert.Single(query.GetHistory(f.Plan,f.Sid,f.Session,100)!.Items);
        using var writer=new SqliteOrdinaryRecordingHistoryStore(legacy);Assert.Single(new UnifiedHistoryQueryService(legacy,writer).GetPage(f.Sid,f.Session,100,new("plan")).Items);
        using var connection=legacy.OpenReadOnlyConnection();using var tx=connection.BeginTransaction(System.Data.IsolationLevel.Serializable,deferred:true);Assert.Equal(1L,SqliteSchemaV41.Validate(connection,tx));Assert.Null(PlanMediaPublicationStorage.Read(connection,tx,new[]{new PlanMediaPublicationRow(f.Plan,f.Occurrence,f.Run,"standing",true,f.Backend.Path,null)},100)[f.Run]);tx.Commit();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));
    }
    [Fact]
    public void Detail512AndPageUnified100UseOneIndexedBatchAndRejectOverflow()
    {
        var root=Path.Combine(Fixture.Parent,"scale-"+Guid.NewGuid().ToString("N"));using var db=new UnifiedHistoryTestDatabase(root);db.SeedPlan("scale");
        using(var c=db.Store.OpenConnection())using(var t=c.BeginTransaction())using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=t;cmd.CommandText="INSERT INTO plan_occurrences VALUES($occ,'scale','run_created',$at,$end,$run,NULL,$at,$at,0); INSERT INTO recording_runs VALUES($run,$occ,'created',0,NULL,NULL,NULL,$at,$at,0);";
            for(var i=0;i<512;i++){cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$occ","scale-occ-"+i);cmd.Parameters.AddWithValue("$run","scale-run-"+i);cmd.Parameters.AddWithValue("$at",UnifiedHistoryTestDatabase.At.UtcTicks+i);cmd.Parameters.AddWithValue("$end",UnifiedHistoryTestDatabase.At.AddMinutes(1).UtcTicks+i);cmd.ExecuteNonQuery();}t.Commit();
        }
        var counts=new List<int>();void Inspect(SqliteConnection c,SqliteTransaction t,string sql,IReadOnlyList<KeyValuePair<string,object>> args)
        {counts.Add(args.Count-1);using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);using var rd=cmd.ExecuteReader();var details=new List<string>();while(rd.Read())details.Add(rd.GetString(3));Assert.Contains(details,s=>s.Contains("idx_plan_media_publication_run"));Assert.DoesNotContain(details,s=>s.StartsWith("SCAN a"));}
        var q=new PlanExecutionStatusQueryService(db.Store){PublicationCommandForTests=Inspect};Assert.Equal(512,q.Get("scale",UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session)!.OccurrenceCount);Assert.Equal(100,q.GetHistory("scale",UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,100)!.Items.Count);db.Query.PlanPublicationCommandForTests=Inspect;Assert.Equal(100,db.Page(100,new("plan")).Items.Count);Assert.Equal(new[]{512,100,100},counts);
        db.SeedOccurrence("scale","overflow");Assert.Throws<Phase3PersistenceException>(()=>q.Get("scale",UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session));Assert.Equal(100,q.GetHistory("scale",UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,100)!.Items.Count);
    }
    [Fact]
    public async Task ConstructionAndDisposeFaultsPreservePrimaryAndCleanupAndGlobalState()
    {
        var capacity=RecordingPreflightChecker.StorageCapacityProvider;var helper=RecordingPreflightChecker.AudioHelperPathResolver;var probe=RecordingPreflightChecker.AudioHelperProbeRunner;
        var env=new[]{"TEMP","TMP","AGENT_RECORDER_DATA_DIR","AGENT_RECORDER_TEST_MODE",CaptureBackendSelector.WgcEnvVar}.ToDictionary(n=>n,Environment.GetEnvironmentVariable);
        PublisherBackend? backend=null;var error=await Assert.ThrowsAsync<AggregateException>(()=>Fixture.Open("standing",afterInitialize:f=>{backend=f.Backend;backend.CleanupFault=true;throw new IOException("task348_construction");}));
        Assert.Equal("task348_construction",error.InnerExceptions[0].Message);Assert.Contains("task348_cleanup",error.ToString());Assert.True(backend!.Disposed);Assert.Same(capacity,RecordingPreflightChecker.StorageCapacityProvider);Assert.Same(helper,RecordingPreflightChecker.AudioHelperPathResolver);Assert.Same(probe,RecordingPreflightChecker.AudioHelperProbeRunner);foreach(var pair in env)Assert.Equal(pair.Value,Environment.GetEnvironmentVariable(pair.Key));
    }
    private sealed class NoUi:ITrayContext
    {
        public string HostMode=>"headless";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> a)=>throw new InvalidOperationException();public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> a)=>throw new InvalidOperationException();public void SetRecording(RecordingUiPresentation p)=>throw new InvalidOperationException();public void SetIdle(RecordingUiPresentation p)=>throw new InvalidOperationException();public void SetAllIdle()=>throw new InvalidOperationException();public void ShowError(string s)=>throw new InvalidOperationException();
    }
    [Fact]
    public async Task AuthenticatedHttpKeepsThreeDtosIdentitySession404503AndNoMediaReads()
    {
        using var f=await Fixture.Open("required_once");f.Complete();var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var auth=typeof(ApiKeyAuth).GetFields(BindingFlags.NonPublic|BindingFlags.Static).Where(v=>v.Name is "_apiKey" or "_initialized" or "_initializationSource").ToDictionary(v=>v,v=>v.GetValue(null));
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);var audit=new AuditLogger();using var engine=new RecordingEngine(audit);var ui=new NoUi();engine.SetTray(ui);engine.BackendFactory=_=>throw new InvalidOperationException("GET capture");
            var q=new PlanExecutionStatusQueryService(f.Store);using var writer=new SqliteOrdinaryRecordingHistoryStore(f.Store);var unified=new SqliteUnifiedHistoryGateway(new UnifiedHistoryQueryService(f.Store,writer),()=> (f.Sid,f.Session));
            var server=new ApiServer(engine,audit,ui,null,null,null,null,null,null,null,null,new PlanExecutionStatusApiTests.StoreBackedGateway(q,f.Sid,f.Session),0,planExecutionHistoryGateway:new PlanExecutionStatusApiTests.StoreBackedHistoryGateway(q,f.Sid,f.Session),unifiedHistoryGateway:unified);server.Start();
            try {
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};using var denied=await client.GetAsync("/api/v1/history?source=plan");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            Assert.Null(q.Get(f.Plan,"foreign",f.Session));Assert.Null(q.Get(f.Plan,f.Sid,"foreign"));Assert.Empty(new UnifiedHistoryQueryService(f.Store,writer).GetPage("foreign",f.Session,100,new("plan")).Items);
            using var locked=new FileStream(f.Backend.Path,FileMode.Open,FileAccess.Read,FileShare.None);var before=f.Snapshot();
            foreach(var url in new[]{"/api/v1/plans/"+f.Plan+"/status","/api/v1/plans/"+f.Plan+"/history","/api/v1/history?source=plan"})
            {using var response=await client.GetAsync(url);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();foreach(var forbidden in new[]{"\"volume_id\"","\"file_id\"","PublicationReceipt","observation_version","proof_nonce","SELECT ",f.Sid,f.Session})Assert.DoesNotContain(forbidden,raw);}
            Assert.Equal(before,f.Snapshot());using var missing=await client.GetAsync("/api/v1/plans/foreign/status");Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);f.Execute("DELETE FROM plan_media_publication_anchors;");
            foreach(var url in new[]{"/api/v1/plans/"+f.Plan+"/status","/api/v1/plans/"+f.Plan+"/history","/api/v1/history?source=plan"}){using var response=await client.GetAsync(url);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var raw=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("Modern plan",raw);Assert.DoesNotContain("file_id",raw);Assert.DoesNotContain("SELECT",raw);}
            } finally {server.Stop();}
        }
        finally{Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);foreach(var pair in auth)pair.Key.SetValue(null,pair.Value);}
    }
}
