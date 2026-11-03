using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionEvaluation387Tests(ITestOutputHelper output)
{
    internal static RetentionEvaluationService Service(RetentionPreviewQueryTests.Fixture f,RetentionPreviewQueryService? kernel=null)
        =>new(f.Db.Query.GetOrdinaryEvidenceByIds,kernel??f.Service);

    [Fact]
    public async Task MixedExplicitIdsPreserveOrderAndUseOriginalEvaluationMeaning()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("old");f.Seed("new",completed:f.Before.AddSeconds(1));f.Seed("active","recording");
        var bundle=f.Db.Recording("bundle",completed:true) with {OutputPath=Path.Combine(f.Videos,"bundle.mp4")};
        bundle=PublicationAnchorTestFixture.Anchored(bundle);bundle=bundle with {BundleRegistration=new(OrdinaryRecordingBundleOperation.Create("bundle",bundle.OutputPath))};f.Db.Ordinary.Register(bundle);
        var exact=f.Seed("exact");RetentionBundle385Tests.Insert(f.Db,"ordinary_bundle_contents:path",exact.OutputPath,exact.PublicationReceipt!.Anchor);
        var physical=f.Seed("physical");RetentionBundleAttachment384Tests.InsertDb(f.Db,physical.PublicationReceipt!.Anchor,owner:"FOREIGN_IDENTITY");
        var scope=f.Seed("scope",path:Path.Combine(f.Videos,"saved.bundle","inside.mp4"));RetentionBundleNamespace386Tests.Insert(f.Db,"ordinary_media_hash",Path.GetDirectoryName(scope.OutputPath)!,scope.PublicationReceipt!.Anchor);
        var planned=f.Seed("planned");f.Db.SeedFuture("SECRET_FOREIGN",sid:"SECRET_SID",session:"SECRET_SESSION",outputPath:planned.OutputPath);
        f.Seed("runtime");f.Seed("missing_file");f.Seed("denied");f.Seed("changed");
        f.Seed("Case",path:Path.Combine(f.Videos,"upper-content.mp4"));f.Seed("case",path:Path.Combine(f.Videos,"lower-content.mp4"));
        var baseline=new RetentionPreviewQueryTests.MetadataFake();
        f.Files.Observation=path=>
        {
            if(path.EndsWith("missing_file.mp4"))throw new FileNotFoundException();
            if(path.EndsWith("denied.mp4"))throw new UnauthorizedAccessException();
            var sample=baseline.Observe(path)!;
            if(path.EndsWith("changed.mp4"))
            {
                var identity=sample.Before!.Identity! with {IdLow=sample.Before.Identity!.IdLow^1};
                return sample with {Before=sample.Before with {Identity=identity},After=sample.After! with {Identity=identity}};
            }
            return sample;
        };
        var runtime=new RetentionRuntimeOutputTests.Provider {Read=paths=>new(paths.Select(p=>new OrdinaryRuntimeOutputUse(p,p.EndsWith("runtime.mp4"))))};
        var kernel=new RetentionPreviewQueryService(f.Db.Query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(2),runtime);
        var requested=new[] {"changed","old","SECRET_FOREIGN","new","active","bundle","exact","physical","scope","planned","runtime","missing_file","denied","absent","Case","case"};
        var before=f.Db.Snapshot();var result=await Service(f,kernel).ReadAsync(requested,f.Before,Sid,Session);
        Assert.Equal("complete",result!.State);var page=result.Page!;Assert.Equal(requested,page.Items.Select(i=>i.RecordingId));
        var expected=new[] {"publication_identity_mismatch","age_rule_match","not_found_or_not_visible","completion_not_before_cutoff","not_completed",
            "bundle_generation_in_progress","shared_output_path","shared_publication_identity","shared_output_path","planned_output_path",
            "runtime_output_in_use","file_missing","file_inaccessible","not_found_or_not_visible","age_rule_match","age_rule_match"};
        Assert.Equal(expected,page.Items.Select(i=>i.ReasonCode));Assert.Equal(36,page.ObservedCandidateBytes);
        Assert.DoesNotContain("SECRET_SID",JsonSerializer.Serialize(result));Assert.DoesNotContain("SECRET_SESSION",JsonSerializer.Serialize(result));
        Assert.Equal(0,f.Reads);Assert.Equal(0,f.Selected);Assert.Equal(before,f.Db.Snapshot());
        var oldPage=kernel.GetPage(Sid,Session,100,f.Before);
        foreach(var item in page.Items.Where(i=>i.ReasonCode!="not_found_or_not_visible"))Assert.Equal(oldPage.Items.Single(i=>i.RecordingId==item.RecordingId),item);
    }

    [Fact]
    public async Task TwentyIdsHaveOneSnapshotIndexedPointsAndBoundedNativeObservation()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var ids=Enumerable.Range(0,20).Select(i=>"r"+i).ToArray();foreach(var id in ids)f.Seed(id);
        SqliteConnection? connection=null;SqliteTransaction? transaction=null;var sqls=new List<string>();var threads=new HashSet<int>();
        SQLitePCL.strdelegate_trace trace=(_,sql)=>{sqls.Add(sql);threads.Add(Environment.CurrentManagedThreadId);};
        f.Db.Query.EvaluationSnapshotForTests=(c,t)=>{Assert.Null(connection);connection=c;transaction=t;SQLitePCL.raw.sqlite3_trace(c.Handle,trace,null);};
        var namespaces=0;
        f.Db.Query.RetentionNamespaceCommandForTests=(c,t,sql,args)=>{Assert.Same(connection,c);Assert.Same(transaction,t);Assert.InRange(args.Count,2,512);namespaces++;};
        f.Db.Query.RetentionIdentityCommandForTests=(c,t,sql,args)=>{Assert.Same(connection,c);Assert.Same(transaction,t);Assert.Equal(60,args.Count);};
        var result=await Service(f).ReadAsync(ids,f.Before,Sid,Session);Assert.Equal("complete",result!.State);Assert.Equal(240,result.Page!.ObservedCandidateBytes);
        Assert.Equal(20,sqls.Count(s=>s.StartsWith("SELECT created_at_utc,(")));
        Assert.Equal(20,sqls.Count(s=>s.Contains("FROM ordinary_recording_history WHERE recording_id =")));
        Assert.InRange(sqls.Count,40,2048);Assert.InRange(namespaces,1,6);Assert.Single(threads);Assert.Equal(System.Data.ConnectionState.Closed,connection!.State);
        Assert.Equal(20,f.Files.Observations.Count);Assert.InRange(f.Files.Attributes.Count,1,640);Assert.Equal(0,f.Selected);Assert.Equal(0,f.Reads);
        GC.KeepAlive(trace);output.WriteLine($"SQL statements after BEGIN={sqls.Count}; plus fixed connection policy/BEGIN <=32. 40 indexed candidate projections; namespace batches={namespaces}; ancestor calls={f.Files.Attributes.Count}; leaf observations={f.Files.Observations.Count}.");
    }

    [Fact]
    public async Task WorstLegalDepthTwentyAndLaterReferencesUseTheSameSnapshot()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var depth=f.Videos[3..].Split('\\').Length;
        var directory=Path.Combine(f.Videos,Path.Combine(Enumerable.Repeat("x",31-depth).ToArray()));
        var ids=Enumerable.Range(0,20).Select(i=>"r"+i).ToArray();foreach(var id in ids)f.Seed(id,path:Path.Combine(directory,id+".mp4"));
        var calls=0;var argsTotal=0;SqliteConnection? connection=null;SqliteTransaction? transaction=null;
        f.Db.Query.EvaluationSnapshotForTests=(c,t)=>{connection=c;transaction=t;};
        f.Db.Query.RetentionNamespaceCommandForTests=(c,t,sql,args)=>{Assert.Same(connection,c);Assert.Same(transaction,t);calls++;argsTotal+=args.Count;};
        var result=await Service(f).ReadAsync(ids,f.Before,Sid,Session);Assert.Equal("complete",result!.State);
        Assert.Equal(6,calls);Assert.Equal(2600,argsTotal);Assert.Equal(640,f.Files.Attributes.Count);Assert.Equal(20,f.Files.Observations.Count);
        output.WriteLine("Worst 20-ID depth: 1300 keys, 2600 parameters, 6 namespace queries, 640 ancestor/20 leaf calls.");
    }

    [Fact]
    public async Task DatabaseResourcesStayOnWorkerAfterFrozenDeadline()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);SqliteConnection? c=null;
        f.Db.Query.EvaluationSnapshotForTests=(connection,transaction)=>{c=connection;entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var service=Service(f);service.DeadlineForTests=token=>deadline.Task.WaitAsync(token);service.WorkerExitedForTests=()=>retired.TrySetResult();
        var pending=service.ReadAsync(new[] {"a"},f.Before,Sid,Session);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));deadline.TrySetResult();Assert.Equal("timeout",(await pending)!.State);
            Assert.Equal(System.Data.ConnectionState.Open,c!.State);Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);RetentionRuntimeOutputTests.NoProbes(f);
        }
        finally {release.Set();await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Equal(System.Data.ConnectionState.Closed,c!.State);Assert.Null(HostReadOnlyWorkerState.BusyReason);
    }

    [Fact]
    public async Task ReadSnapshotDoesNotMixReferencesInsertedDuringPointProjection()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("a");
        var inserted=false;
        f.Db.Query.RetentionIdentityCommandForTests=(c,t,sql,args)=>
        { if(!inserted){inserted=true;RetentionBundleNamespace386Tests.Insert(f.Db,"ordinary_media_hash",Path.GetDirectoryName(row.OutputPath)!,row.PublicationReceipt!.Anchor);} };
        var first=await Service(f).ReadAsync(new[] {"a"},f.Before,Sid,Session);Assert.Equal("age_rule_match",Assert.Single(first!.Page!.Items).ReasonCode);
        var next=await Service(f).ReadAsync(new[] {"a"},f.Before,Sid,Session);Assert.Equal("shared_output_path",Assert.Single(next!.Page!.Items).ReasonCode);
    }

    [Fact]
    public async Task PendingSetupPurposeProtectsWithoutInvokingAuthorizationUi()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("pending");
        var request=StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter("pending-target","pending-key","foreign-sid","foreign-session",At,
            At.AddMinutes(5),At.AddSeconds(10),At.AddSeconds(20),At.AddSeconds(80),TimeSpan.FromSeconds(30),At.AddHours(1),
            f.Videos,Path.GetFileName(row.OutputPath));
        new StandingSetupIntentService(f.Db.Store,()=>At).CreateOrGet(request);
        var before=f.Db.Snapshot();var result=await Service(f).ReadAsync(new[] {"pending"},f.Before,Sid,Session);
        Assert.Equal("setup_output_path",Assert.Single(result!.Page!.Items).ReasonCode);RetentionRuntimeOutputTests.NoProbes(f);Assert.Equal(before,f.Db.Snapshot());
    }

    [Fact]
    public async Task CorruptOwnTargetAssociationCannotBecomeANormalCandidate()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        f.Db.Corrupt("UPDATE ordinary_recording_targets SET target_key='SECRET_INVALID';");
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());RetentionRuntimeOutputTests.NoProbes(f);
        var result=await Service(f).ReadAsync(new[] {"a"},f.Before,Sid,Session);
        Assert.Equal("failed",result!.State);Assert.Null(result.Page);RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public async Task IdentityResolutionRetainsQuotaAndCallerIdsAreFrozen()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var ids=new[] {"a"};using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var service=Service(f);
        service.DeadlineForTests=token=>deadline.Task.WaitAsync(token);service.WorkerExitedForTests=()=>retired.TrySetResult();
        var pending=service.ReadAsync(ids,f.Before,()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return(Sid,Session);});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));ids[0]="not-the-request";deadline.TrySetResult();
            Assert.Equal("timeout",(await pending)!.State);Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);RetentionRuntimeOutputTests.NoProbes(f);
        }
        finally {release.Set();await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        // A separate completed dispatch freezes its own ID array before the worker callback.
        ids[0]="a";Assert.Equal("a",Assert.Single((await Service(f).ReadAsync(ids,f.Before,Sid,Session))!.Page!.Items).RecordingId);
    }

    [Fact]
    public async Task CallerMutationCannotChangeTheDispatchedReadOnlyIdSet()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var ids=new[] {"a"};using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=new RetentionEvaluationService((sid,session,requested,token)=>
        {entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));Assert.Equal("a",requested[0]);
            Assert.Throws<NotSupportedException>(()=>((IList<string>)requested)[0]="foreign");
            return f.Db.Query.GetOrdinaryEvidenceByIds(sid,session,requested,token);},f.Service);
        var pending=service.ReadAsync(ids,f.Before,Sid,Session);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));ids[0]="foreign";}
        finally{release.Set();}
        Assert.Equal("a",Assert.Single((await pending)!.Page!.Items).RecordingId);
    }

    [Theory]
    [InlineData("missing")][InlineData("foreign")][InlineData("null")][InlineData("wrong_entry")]
    public async Task BrokenEvidenceSetFailsBeforeAnyProbe(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        var service=new RetentionEvaluationService((sid,session,ids,token)=>
        {
            var data=f.Db.Query.GetOrdinaryEvidenceByIds(sid,session,ids,token);
            return fault switch {
                "missing"=>new Dictionary<string,OrdinaryRetentionEvidence?>(),"foreign"=>new Dictionary<string,OrdinaryRetentionEvidence?> {["SECRET"]=data["a"]},
                "null"=>null!,_=>new Dictionary<string,OrdinaryRetentionEvidence?> {["a"]=data["a"]! with {Entry=data["a"]!.Entry with {RecordingId="SECRET"}}}};
        },f.Service);
        var result=await service.ReadAsync(new[] {"a"},f.Before,Sid,Session);Assert.Equal("failed",result!.State);Assert.Null(result.Page);RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("identity")][InlineData("namespace")][InlineData("schema")][InlineData("runtime")]
    public async Task SharedProviderAndSchemaDamageDiscardWholeGroup(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");RetentionPreviewQueryService? kernel=null;
        if(fault=="identity")f.Db.Query.RetentionIdentityResultsForTests=rows=>new[] {rows[0],rows[0]};
        if(fault=="namespace")f.Db.Query.RetentionNamespaceResultsForTests=rows=>Array.Empty<(object,object)>();
        if(fault=="schema")f.Db.RawExecute("DROP INDEX idx_v47_ordinary_media_hash_namespace;");
        if(fault=="runtime")kernel=new(f.Db.Query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(2),null);
        var result=await Service(f,kernel).ReadAsync(new[] {"a","b"},f.Before,Sid,Session);Assert.Equal("failed",result!.State);Assert.Null(result.Page);RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public async Task CheckedByteOverflowNeverReturnsPartialCandidateGroup()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");
        f.Files.DefaultLength=long.MaxValue;
        var result=await Service(f).ReadAsync(new[] {"a","b"},f.Before,Sid,Session);
        Assert.Equal("failed",result!.State);Assert.Null(result.Page);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DeadlineOrCancellationRetainsActualSharedWorkerAndIdleBusyUntilRelease(bool cancelled)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");
        using var release=new ManualResetEventSlim();using var cts=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var baseline=new RetentionPreviewQueryTests.MetadataFake();
        f.Files.Observation=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return baseline.Observe(path);};
        var service=Service(f);service.WorkerExitedForTests=()=>retired.TrySetResult();
        if(!cancelled)service.DeadlineForTests=token=>deadline.Task.WaitAsync(token);
        var pending=service.ReadAsync(new[] {"a","b"},f.Before,Sid,Session,cts.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(cancelled)cts.Cancel();else deadline.TrySetResult();
            var response=await pending.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(cancelled?"cancelled":"timeout",response!.State);Assert.Null(response.Page);
            Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);
            Assert.Equal("busy",(await Service(f).ReadAsync(new[] {"a"},f.Before,Sid,Session))!.State);
            Assert.Equal("busy",(await new OrdinaryArtifactCheckService(f.Db.Ordinary).ReadAsync("a"))!.ProbeState);
            var admission=new HostWorkAdmission();Assert.Equal("artifact_metadata_worker_pending",admission.TryClose(()=>HostReadOnlyWorkerState.BusyReason));
            Assert.Single(f.Files.Observations);
        }
        finally {release.Set();await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Null(HostReadOnlyWorkerState.BusyReason);Assert.Single(f.Files.Observations);
        f.Files.Observation=null;Assert.Equal("complete",(await Service(f).ReadAsync(new[] {"a"},f.Before,Sid,Session))!.State);
    }
}
