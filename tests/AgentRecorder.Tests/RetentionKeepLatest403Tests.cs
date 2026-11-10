using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionKeepLatest403Tests
{
    private static Task<RetentionEvaluationResult?> Read(RetentionPreviewQueryTests.Fixture f,int n,string[]? ids=null)
        =>new RetentionEvaluationService(f.Db.Query,f.Service).ReadAsync(ids??new[]{"a"},f.Before,()=>(Sid,Session),default,n);
    [Theory][InlineData(1)][InlineData(1000)]
    public async Task KeepIsReadOnlyAndDoesNotProbe(int n)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var before=f.Db.Snapshot();
        var result=await Read(f,n);Assert.Equal("complete",result!.State);
        Assert.Equal("within_keep_latest",Assert.Single(result.Page!.Items).ReasonCode);
        Assert.Equal(1,result.Page.AgeCandidateCount);Assert.Equal(0,result.Page.ObservedCandidateBytes);
        RetentionRuntimeOutputTests.NoProbes(f);Assert.Equal(before,f.Db.Snapshot());
    }
    [Fact] public async Task BinaryTiesAndUnrequestedNewerRecordsIgnoreCutoffAndRequestOrder()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a",path:Path.Combine(f.Videos,"lower.mp4"));f.Seed("A",path:Path.Combine(f.Videos,"upper.mp4"));f.Seed("z",completed:f.Before.AddDays(1));
        var r=await Read(f,2,new[]{"A","a","missing"});Assert.Equal("complete",r!.State);
        Assert.Equal(new[]{"age_rule_match","within_keep_latest","not_found_or_not_visible"},r.Page!.Items.Select(i=>i.ReasonCode));
        Assert.Single(f.Files.Observations);Assert.DoesNotContain("ordinary:z",JsonSerializer.Serialize(r));
    }
    [Fact] public async Task ThousandLimitDoesNotProbeThousandFiles()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();
        for(int i=0;i<=1000;i++)f.Seed("r"+i.ToString("D4"));
        var r=await Read(f,1000,new[]{"r0000","r0001"});Assert.Equal("complete",r!.State);
        Assert.Equal(new[]{"age_rule_match","within_keep_latest"},r.Page!.Items.Select(i=>i.ReasonCode));Assert.Single(f.Files.Observations);
    }
    [Fact] public async Task BundleAndExternalCountButDoNotAcquireDeletionEligibility()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        f.Seed("external",completed:f.Before.AddSeconds(1),path:Path.Combine(f.Db.Root,"elsewhere","x.mp4"));
        var row=f.Db.Recording("bundle",completed:true) with {OutputPath=Path.Combine(f.Videos,"bundle.mp4"),CompletedAtUtc=f.Before.AddSeconds(2)};
        row=PublicationAnchorTestFixture.Anchored(row);f.Db.Ordinary.Register(row with {BundleRegistration=new(OrdinaryRecordingBundleOperation.Create("bundle",row.OutputPath))});
        var r=await Read(f,2,new[]{"external","a","bundle"});Assert.Equal("complete",r!.State);
        Assert.Equal(new[]{"within_keep_latest","age_rule_match","within_keep_latest"},r.Page!.Items.Select(i=>i.ReasonCode));Assert.Single(f.Files.Observations);
    }
    [Fact] public async Task RootAndSourceIsolationAndInvalidIdentity()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();using var other=new RetentionPreviewQueryTests.Fixture();
        f.Seed("a");f.Seed("active","recording");other.Seed("newer",completed:f.Before.AddDays(1));f.Db.SeedPlan("foreign",sid:"other",session:"other");
        var r=await Read(f,1,new[]{"a","active","foreign","newer"});Assert.Equal("complete",r!.State);
        Assert.Equal(new[]{"within_keep_latest","not_completed","not_found_or_not_visible","not_found_or_not_visible"},r.Page!.Items.Select(i=>i.ReasonCode));
        var bad=await new RetentionEvaluationService(f.Db.Query,f.Service).ReadAsync(new[]{"a"},f.Before,()=> ("",Session),default,1);
        Assert.Equal("failed",bad!.State);RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Theory][InlineData("NULL")][InlineData("'bad'")][InlineData("9223372036854775807")]
    public async Task CorruptOrderingNeverBecomesEmptySet(string value)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Db.Corrupt("UPDATE ordinary_recording_history SET completed_at_utc="+value);
        Assert.Equal("failed",(await Read(f,1))!.State);RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact] public async Task LatestAndRequestedFactsUseOneSnapshotAcrossConcurrentCommit()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Db.Query.LatestReadForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var pending=Read(f,1,new[]{"a","z"});
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Seed("z",completed:f.Before.AddSeconds(1));}
        finally{release.Set();}
        var first=await pending;Assert.Equal("complete",first!.State);Assert.Equal(new[]{"within_keep_latest","not_found_or_not_visible"},first.Page!.Items.Select(i=>i.ReasonCode));
        f.Db.Query.LatestReadForTests=null;
        var next=await Read(f,1,new[]{"a","z"});Assert.Equal(new[]{"age_rule_match","within_keep_latest"},next!.Page!.Items.Select(i=>i.ReasonCode));
    }
    [Fact] public async Task KeepDoesNotHideCorruptRequestedRelationsAndUnkeptUsesFullKernel()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("a");f.Seed("z",completed:f.Before.AddSeconds(1));
        f.Db.SeedFuture("future",outputPath:row.OutputPath);
        Assert.Equal("planned_output_path",Assert.Single((await Read(f,1))!.Page!.Items).ReasonCode);
        f.Db.Corrupt("UPDATE ordinary_recording_targets SET target_key='INVALID';");
        Assert.Equal("failed",(await Read(f,1000))!.State);RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task TimeoutKeepsSameWorkerBusyUntilDatabaseAndCallbackClose(bool cancel)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Db.Query.LatestReadForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        using var cancellation=new CancellationTokenSource();
        var service=new RetentionEvaluationService(f.Db.Query,f.Service){DeadlineForTests=t=>deadline.Task.WaitAsync(t),WorkerExitedForTests=()=>exited.TrySetResult()};
        var pending=service.ReadAsync(new[]{"a"},f.Before,()=>(Sid,Session),cancellation.Token,1);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(cancel)cancellation.Cancel();else deadline.TrySetResult();Assert.Equal(cancel?"cancelled":"timeout",(await pending)!.State);
            Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);Assert.Equal("busy",(await Read(f,1))!.State);RetentionRuntimeOutputTests.NoProbes(f);}
        finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Null(HostReadOnlyWorkerState.BusyReason);
    }
    [Fact] public async Task UnsupportedInternalProviderFailsClosed()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        var r=await RetentionEvaluation387Tests.Service(f).ReadAsync(new[]{"a"},f.Before,()=>(Sid,Session),default,1);
        Assert.Equal("failed",r!.State);RetentionRuntimeOutputTests.NoProbes(f);
    }
    private sealed class Tray:ITrayContext {
        public string HostMode=>"headless";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> cb)=>throw new Exception("Forbidden UI");
        public void RequestRegionSelection(int n,Action<string,int,int,int,int,string,string> cb)=>throw new Exception("Forbidden capture");
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    [Theory][InlineData("null")][InlineData("0")][InlineData("-1")][InlineData("1.0")][InlineData("1e0")][InlineData("1001")][InlineData("\"1\"")][InlineData("true")][InlineData("9223372036854775808")][InlineData("1,\"keep_latest_count\":2")]
    public async Task InvalidApiFieldFailsBeforeRead(string raw)
    {await ApiCase(raw,false);}
    [Theory][InlineData("1")][InlineData("1000")][InlineData(null)]public async Task ApiPolicyAndOldResponse(string? raw){await ApiCase(raw,true);}
    private static async Task ApiCase(string? raw,bool valid)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var calls=0;f.Db.Query.EvaluationSnapshotForTests=(_,_)=>calls++;
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());var tray=new Tray();
        ApiKeyAuth.InitializeForTesting(f.Db.Root);
        var gateway=new SqliteRetentionEvaluationGateway(new RetentionEvaluationService(f.Db.Query,f.Service),()=>(Sid,Session));
        var server=new ApiServer(engine,new RetentionSharedOutputPathTests.QuietAudit(),tray,null,null,null,null,null,null,null,null,null,listenPort:0,retentionEvaluationGateway:gateway);server.Start();
        try {
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            var body="{\"recording_ids\":[\"a\"],\"completed_before\":\"2026-10-05T02:00:00Z\""+(raw is null?"":",\"keep_latest_count\":"+raw)+"}";
            using var response=await client.PostAsync("/api/v1/retention/evaluations",new StringContent(body,Encoding.UTF8,"application/json"));
            Assert.Equal(valid?HttpStatusCode.OK:HttpStatusCode.BadRequest,response.StatusCode);
            if(!valid){Assert.Equal(0,calls);RetentionRuntimeOutputTests.NoProbes(f);}
            else {using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var data=doc.RootElement.GetProperty("data");Assert.Equal(raw is not null,data.TryGetProperty("policy",out _));}
            using var cap=await client.GetAsync("/api/v1/capabilities");using var json=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());Assert.True(json.RootElement.GetProperty("data").GetProperty("retention_evaluations").GetProperty("keep_latest_supported").GetBoolean());
        }finally{server.Stop();ApiKeyAuth.ResetForTesting(null);}
    }
}
