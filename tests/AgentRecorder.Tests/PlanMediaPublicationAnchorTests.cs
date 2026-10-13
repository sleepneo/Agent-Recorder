using AgentRecorder.App;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Required = AgentRecorder.Tests.RequiredOncePlanSetupCoordinatorTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class PlanMediaPublicationAnchorTests
{
    private sealed class PublisherBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, ICaptureEndedObservableBackend
    {
        internal readonly string Clip; internal string Path = ""; internal int Starts; internal OutputMeta? Meta; internal bool Unknown; internal bool CleanupFault; internal bool Disposed;
        internal PublisherBackend(string clip) => Clip=clip;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public event Action<CaptureEndedObservation>? CaptureEnded;
        private Action<int,OutputMeta>? natural;
        public void Start(CaptureConfig config,CaptureAuthorizationProof proof) { Path=config.OutputPath; Begin(); }
        internal void Begin() { Starts++;FirstFrameObserved?.Invoke(new() { EvidenceKind="ffmpeg_progress_frame_and_output_bytes",FrameNumber=1,TotalSizeBytes=100 }); }
        internal OutputMeta Publish()
        {
            if(Meta is not null)return Meta;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var staging=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Clip)!,Guid.NewGuid()+".staging");File.Copy(Clip,staging);
            IStagingToFinalPublisher publisher=Unknown?new StagingToFinalPublisher(new PhysicalFileSystemOperations(),identityApi:new BundlePublicationIdentityTests.NoIdentityApi()):StagingToFinalPublisher.Instance;
            var result=publisher.PublishAsync(staging,Path).GetAwaiter().GetResult();Assert.True(result.Success);
            Meta=new FfmpegOutputProber().Probe(Path);Meta.OutputFileExists=true;Meta.AudioSourceKind="none";Meta.AudioStatus="not_requested";
            Meta.PublicationReceipt=result.Receipt;return Meta;
        }
        public OutputMeta Stop() { var meta=Publish();CaptureEnded?.Invoke(new(){ExitCode=0,Reason="manual"});return meta; }
        internal void Finish() { var meta=Publish();CaptureEnded?.Invoke(new(){ExitCode=0,Reason="natural"});natural?.Invoke(0,meta); }
        public int ExitCode=>0;public void OnNaturalExit(Action<int,OutputMeta> callback)=>natural=callback;
        public void Dispose() { Disposed=true;if(CleanupFault)throw new IOException("task348_cleanup"); }
    }
    private sealed class Fixture : IDisposable
    {
        internal static readonly string Parent=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"../../../../../..",".local-data/temp/task348"));
        private string OwnedParent=Parent;
        internal string Root=System.IO.Path.Combine(Parent,"plan-"+Guid.NewGuid().ToString("N"));
        internal string Kind="",Plan="",Occurrence="",Run="",Owner="",Sid="",Session="";
        internal SqliteOperationalStore Store=null!;internal PublisherBackend Backend=null!;internal string Clip=>System.IO.Path.Combine(Root,"input.mp4");
        internal DateTimeOffset Now;internal SqliteStandingLeaseLifecycleTransaction? Standing;internal SqliteRecurringLeaseLifecycleTransaction? Recurring;
        internal SqliteRequiredOnceExecutionRepository? RequiredRepository;internal RecordingEngine? Engine;
        internal RequiredOncePlanSetupCoordinator? Setup;internal RequiredOncePlanExecutionCoordinator? RequiredCoordinator;
        internal IDisposable? CaptureSession;internal Required.Fixture? RequiredSetup;
        internal static async Task<Fixture> Open(string kind,bool begin=true,bool unknown=false,Action<Fixture>? afterInitialize=null,string? ownedParent=null)
        {
            var f=new Fixture{Kind=kind};if(ownedParent is not null){f.OwnedParent=System.IO.Path.GetFullPath(ownedParent);f.Root=System.IO.Path.Combine(f.OwnedParent,Guid.NewGuid().ToString("N"));}Directory.CreateDirectory(f.Root);
            try { await f.Initialize(kind,begin,unknown);afterInitialize?.Invoke(f);return f; }
            catch(Exception first) { try{f.Dispose();}catch(Exception cleanup){throw new AggregateException(first,cleanup);}throw; }
        }
        private async Task Initialize(string kind,bool begin,bool unknown)
        {
            await AvMuxOwnershipTests.Generate(Clip,"color=c=blue:s=64x64:r=15",false);
            Backend=new(Clip){Unknown=unknown};Assert.Equal(0,Backend.Starts);
            if(kind=="standing")
            {
                var db=StandingLeasePreparedIntentNaturalWakeTests.CreateActivatedDatabase(ownedRoot:Root);Store=db.Store;
                var claimed=StandingLeasePreparedIntentNaturalWakeTests.CreateStandingTicket(db);var spec=claimed.Specification;
                Plan=spec.PlanId;Occurrence=spec.OccurrenceId;Run=spec.RunId;Owner=spec.LeaseId;Sid=claimed.Scope.CurrentUserSid;Session=claimed.Scope.SessionBinding;
                Now=new(2026,1,1,0,0,13,TimeSpan.Zero);Backend.Path=spec.OutputFilePath;Standing=new(Store,spec);
                var capture=new StandingLeaseOneShotExecutionSession(Store,spec,Backend,()=>Now);CaptureSession=capture;
                Assert.True(((IStandingLeaseCaptureLifecycleSession)capture).TryAttach(Backend,out var why),why);
                if(begin)Backend.Begin();
            }
            else if(kind=="recurring")
            {
                var context=RecurringOccurrenceReservationTests.ReservationContext.Create(ownedRoot:Root);
                Store=context.Fixture.Store;Now=context.Fixture.CreatedAt;
                new SqliteRecurringAdvancementTransaction(Store).AdvanceOne(context.Fixture.PlanId,1,"task348-cursor",0,context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),Now);
                var reserved=new RecurringOccurrenceReservationService(Store,()=>Now,()=>"task348-run-"+Guid.NewGuid().ToString("N"),()=>"task348-use-"+Guid.NewGuid().ToString("N")).Reserve(context.Lease.LeaseId,context.Slot.OccurrenceIdentity);
                Assert.Equal(RecurringOccurrenceReservationStatus.Reserved,reserved.Status);
                var committed=new RecurringOccurrenceStartCommitService(Store,()=>Now,new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider()).Commit(context.Lease.LeaseId,context.Slot.OccurrenceIdentity);
                Assert.True(committed.Succeeded,committed.ReasonCode);var receipt=committed.FirstCommitReceipt!;
                Plan=receipt.Plan.Id;Occurrence=receipt.Occurrence.Id;Run=receipt.Run.Id;Owner=receipt.Lease.LeaseId;
                Sid=receipt.Specification.ApprovedCurrentUserSid;Session=receipt.Specification.ApprovedSessionBinding;
                Now=Now.AddMinutes(3);Backend.Path=receipt.Specification.FrozenOutputFilePath;Recurring=new(Store,receipt);
                var capture=new RecurringLeaseCaptureExecutionSession(Store,receipt,Backend,()=>Now);CaptureSession=capture;
                Assert.True(((IRecurringLeaseCaptureLifecycleSession)capture).TryAttach(Backend,out var why),why);
                if(begin)Backend.Begin();
            }
            else
            {
                var db=new Required.TemporaryDatabase(Root);Store=db.Store;var clock=new Required.MutableClock(DateTimeOffset.UtcNow);
                var setup=Required.CreateFixture(db,clock,new Required.FakeUi());RequiredSetup=setup;Setup=setup.Coordinator;
                var created=Setup.CreateOrGet(setup.ApiRequest("task348-required",TimeSpan.FromSeconds(1)));await Setup.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Plan=Setup.Get(created.SetupIntentId!)!.PlanId!;clock.Set(setup.Request.ScheduledStartUtc);Now=clock.Now;Sid=Required.Sid;Session=Required.Session;
                Engine=Required.CreateRequiredOnceEngine(setup,Backend);RequiredRepository=new(Store);
                var approval=new Required.FakeExecutionUi{HoldApproval=true};RequiredCoordinator=Required.CreateExecutionCoordinator(db,setup,Engine,approval);
                var work=RequiredCoordinator.ProcessDueOnceAsync();await approval.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0,Backend.Starts);Assert.Equal(0,Scalar("SELECT count(*) FROM recording_runs;"));
                approval.Complete(RequiredOnceExecutionApprovalResult.Approved);await work.WaitAsync(TimeSpan.FromSeconds(10));
                var row=Status().LatestOccurrence!;Occurrence=row.OccurrenceId;Run=row.RunId!;
                using var c=Store.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT execution_id FROM required_once_execution_authorizations WHERE run_id=$run;";cmd.Parameters.AddWithValue("$run",Run);Owner=(string)cmd.ExecuteScalar()!;
            }
        }
        internal PlanExecutionStatusSnapshot Status()=>new PlanExecutionStatusQueryService(Store).Get(Plan,Sid,Session)!;
        internal PlanExecutionHistoryPageSnapshot History()=>new PlanExecutionStatusQueryService(Store).GetHistory(Plan,Sid,Session,100)!;
        internal UnifiedHistoryPage Unified() {using var writer=new SqliteOrdinaryRecordingHistoryStore(Store);return new UnifiedHistoryQueryService(Store,writer).GetPage(Sid,Session,100,new("plan"));}
        internal PublicationIdentityAnchor? Anchor()
        {
            using var c=Store.OpenReadOnlyConnection();using var t=c.BeginTransaction(System.Data.IsolationLevel.Serializable,deferred:true);
            var status=Status().LatestOccurrence!;var rows=new[]{new PlanMediaPublicationRow(Plan,Occurrence,Run,Kind,status.Status=="completed",status.OutputPath,Kind=="required_once"&&status.Status=="completed"?Backend.Meta?.SizeBytes:null)};
            var result=PlanMediaPublicationStorage.Read(c,t,rows,100)[Run];t.Commit();return result;
        }
        internal long Scalar(string sql) {using var c=Store.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return (long)cmd.ExecuteScalar()!;}
        internal void Execute(string sql) {using var c=Store.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
        internal string Snapshot()=>UnifiedHistoryProfileFilterTests.RawSnapshot(Store.DatabasePath);
        internal void Complete(bool manual=false)
        {
            Now=Now.AddSeconds(2);
            if(Kind=="required_once") {if(manual)Engine!.Stop(Run,"user_requested");else Backend.Finish();}
            else if(manual) {if(Kind=="standing")((IStandingLeaseCaptureLifecycleSession)CaptureSession!).Stop();else ((IRecurringLeaseCaptureLifecycleSession)CaptureSession!).Stop();}
            else Backend.Finish();
            Assert.Equal("completed",Status().LatestOccurrence!.Status);
        }
        internal void SettleDirect(OutputMeta meta,Action<SqliteConnection,SqliteTransaction>? commit=null)
        {
            if(Standing is not null)
            {
                var field=typeof(SqliteStandingLeaseLifecycleTransaction).GetField("_beforeCommitForTest",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;field.SetValue(Standing,commit);
                Assert.True(Standing.CompleteTermination(StandingLeaseLifecycleTerminationKind.NaturalExit,0,meta,Now.AddSeconds(1)).Succeeded);
            }
            else if(Recurring is not null)
            {
                var field=typeof(SqliteRecurringLeaseLifecycleTransaction).GetField("_beforeCommitForTest",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;field.SetValue(Recurring,commit);
                Assert.True(Recurring.CompleteTermination(RecurringLeaseLifecycleTerminationKind.NaturalExit,0,meta,Now.AddSeconds(1)).Succeeded);
            }
            else {RequiredRepository!.BeforePublicationCommitForTests=commit;Assert.True(RequiredRepository.SettleFinalization(Owner,true,meta,0,null,Now.AddSeconds(1)).Succeeded);}
        }
        public void Dispose()
        {
            var errors=new List<Exception>();void Clean(Action a){try{a();}catch(Exception e){errors.Add(e);}}
            Clean(()=>RequiredCoordinator?.Dispose());Clean(()=>Setup?.Dispose());Clean(()=>Engine?.Dispose());Clean(()=>CaptureSession?.Dispose());Clean(()=>Backend?.Dispose());Clean(SqliteConnection.ClearAllPools);
            if(errors.Count==0)Clean(()=>{Assert.Equal(OwnedParent,System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Root)));if(Directory.Exists(Root))Directory.Delete(Root,true);});
            if(errors.Count>0)throw new AggregateException(errors);
        }
    }
    private static PublicationIdentityAnchor Observe(string path)
    {using var stream=File.OpenRead(path);var observation=PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance,stream.SafeFileHandle,path)!;return new(path,stream.Length,observation.Snapshot.Identity!);}

    [Theory]
    [InlineData("standing",false)][InlineData("standing",true)][InlineData("recurring",false)][InlineData("recurring",true)][InlineData("required_once",false)][InlineData("required_once",true)]
    public async Task ApprovedClaimedSessionPublishesOriginalIdentityAndReopens(string kind,bool manual)
    {
        using var f=await Fixture.Open(kind);f.Complete(manual);var receipt=f.Backend.Meta!.PublicationReceipt;Assert.NotNull(receipt);
        Assert.Equal(receipt!.Anchor,f.Anchor());Assert.Equal(Observe(f.Backend.Path),f.Anchor());
        Assert.Single(f.History().Items);Assert.Equal("completed",Assert.Single(f.Unified().Items).Status);
        f.Store=new(f.Store.DatabasePath);f.Store.Initialize();Assert.Equal(receipt.Anchor,f.Anchor());
        Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_recording_history;"));Assert.Equal(0,f.Scalar("SELECT count(*) FROM future_window_authorizations;"));
        Assert.Equal(kind=="required_once"?0:1,f.Scalar("SELECT count(*) FROM recording_run_output_evidence;"));
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task NativeUnavailableIsExplicitUnknownAndLateReceiptCannotUpgrade(string kind)
    {
        using var f=await Fixture.Open(kind,unknown:true);f.Complete();Assert.Null(f.Anchor());var before=f.Snapshot();
        f.Backend.Meta!.PublicationReceipt=(await StagingToFinalPublisher.Instance.PublishAsync(f.Clip,f.Backend.Path+".foreign")).Receipt;
        f.SettleDirect(f.Backend.Meta);Assert.Equal(before,f.Snapshot());Assert.Null(f.Anchor());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task SameSizeReplacementPreservesOriginalDatabaseIdentity(string kind)
    {
        using var f=await Fixture.Open(kind);f.Complete();var before=f.Anchor();var bytes=File.ReadAllBytes(f.Backend.Path);File.Move(f.Backend.Path,f.Backend.Path+".old");File.WriteAllBytes(f.Backend.Path,bytes);
        Assert.NotEqual(before,Observe(f.Backend.Path));Assert.Equal(before,f.Anchor());Assert.Single(f.History().Items);Assert.Single(f.Unified().Items);
    }
    [Theory][InlineData("standing",false)][InlineData("standing",true)][InlineData("recurring",false)][InlineData("recurring",true)][InlineData("required_once",false)][InlineData("required_once",true)]
    public async Task AnchorWriteAndActualCommitFailureRollBackOriginalWinner(string kind,bool commit)
    {
        using var f=await Fixture.Open(kind);var meta=f.Backend.Publish();var before=f.Snapshot();
        if(!commit)f.Execute("CREATE TRIGGER task348_fail BEFORE UPDATE ON plan_media_publication_anchors BEGIN SELECT RAISE(ABORT,'task348_anchor_write'); END;");
        var reachedCommit=false;
        before=f.Snapshot();var error=Assert.Throws<Phase3PersistenceException>(()=>f.SettleDirect(meta,commit?(c,t)=>{reachedCommit=true;using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="PRAGMA defer_foreign_keys=ON; INSERT INTO plan_media_publication_anchors(run_id) VALUES('task348_missing_run');";cmd.ExecuteNonQuery();}:null));
        Assert.Equal(commit,reachedCommit);Assert.IsType<SqliteException>(error.InnerException);
        Assert.Equal(before,f.Snapshot());Assert.Null(f.Anchor());Assert.NotEqual("completed",f.Status().LatestOccurrence!.Status);
        if(!commit)f.Execute("DROP TRIGGER task348_fail;");f.SettleDirect(meta);Assert.Equal(meta.PublicationReceipt!.Anchor,f.Anchor());
    }
    [Theory]
    [InlineData("DELETE FROM plan_media_publication_anchors;")]
    [InlineData("UPDATE plan_media_publication_anchors SET plan_id='foreign';")]
    [InlineData("UPDATE plan_media_publication_anchors SET occurrence_id='foreign';")]
    [InlineData("UPDATE plan_media_publication_anchors SET owner_id='foreign';")]
    [InlineData("UPDATE plan_media_publication_anchors SET owner_kind='standing';")]
    [InlineData("UPDATE plan_media_publication_anchors SET output_path='C:\\foreign.mp4';")]
    [InlineData("UPDATE plan_media_publication_anchors SET frozen_output_path='C:\\foreign.mp4';")]
    [InlineData("UPDATE plan_media_publication_anchors SET output_size_bytes=output_size_bytes+1;")]
    [InlineData("UPDATE plan_media_publication_anchors SET output_size_bytes=1.5;")]
    [InlineData("UPDATE plan_media_publication_anchors SET owner_id=CAST(owner_id AS BLOB);")]
    [InlineData("UPDATE plan_media_publication_anchors SET volume_id=zeroblob(7);")]
    [InlineData("UPDATE plan_media_publication_anchors SET file_id=zeroblob(16);")]
    [InlineData("UPDATE plan_media_publication_anchors SET observation_version=2;")]
    [InlineData("UPDATE plan_media_publication_anchors SET source='foreign';")]
    [InlineData("DROP INDEX idx_plan_media_publication_run;")]
    [InlineData("DROP TRIGGER plan_media_publication_initial;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=41;")]
    [InlineData("UPDATE schema_migrations SET name=CAST(name AS BLOB) WHERE version=41;")]
    [InlineData("UPDATE plan_media_publication_boundary SET legacy_run_rowid=999999;")]
    public async Task ModernDamageFailsAllThreeQueriesWithoutRepair(string sql)
    {
        using var f=await Fixture.Open("required_once");f.Complete();f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);var before=f.Snapshot();
        Assert.Throws<Phase3PersistenceException>(()=>f.Status());Assert.Throws<Phase3PersistenceException>(()=>f.History());Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Unified());Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task OneIndexedBatchPerOriginalQuerySnapshot(string kind)
    {
        using var f=await Fixture.Open(kind);f.Complete();var calls=0;
        void ObserveBatch(SqliteConnection c,SqliteTransaction t,string sql,IReadOnlyList<KeyValuePair<string,object>> parameters)
        {calls++;using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var p in parameters)cmd.Parameters.AddWithValue(p.Key,p.Value);using var rd=cmd.ExecuteReader();var details=new List<string>();while(rd.Read())details.Add(rd.GetString(3));Assert.Contains(details,s=>s.Contains("idx_plan_media_publication_run"));Assert.DoesNotContain(details,s=>s.StartsWith("SCAN a"));}
        var query=new PlanExecutionStatusQueryService(f.Store){PublicationCommandForTests=ObserveBatch};query.Get(f.Plan,f.Sid,f.Session);Assert.Equal(1,calls);query.GetHistory(f.Plan,f.Sid,f.Session,100);Assert.Equal(2,calls);
        using var writer=new SqliteOrdinaryRecordingHistoryStore(f.Store);new UnifiedHistoryQueryService(f.Store,writer){PlanPublicationCommandForTests=ObserveBatch}.GetPage(f.Sid,f.Session,100,new("plan"));Assert.Equal(3,calls);
    }
}
