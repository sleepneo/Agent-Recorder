using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionPreviewKeep404Tests
{
    private static RetentionPreviewQueryService Service(RetentionPreviewQueryTests.Fixture f)=>new(f.Db.Query,f.Db.Root,f.Files,f.Files,TestRuntimeOutputUse.None);
    private static async Task<RetentionPreviewPage> Read(RetentionPreviewQueryService s,DateTimeOffset before,int n=1,int limit=1,string? cursor=null,string sid=Sid)
    {var r=await s.ReadWithKeepLatestAsync(limit,before,cursor,n,()=>(sid,Session));Assert.Equal("complete",r!.State);return r.Page!;}
    [Theory][InlineData(1)][InlineData(1000)]
    public async Task KeepStaysInPageWithoutProbes(int n)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var before=f.Db.Snapshot();
        var p=await Read(Service(f),f.Before,n);Assert.Equal("within_keep_latest",Assert.Single(p.Items).ReasonCode);
        Assert.Equal(1,p.AgeCandidateCount);Assert.Equal(0,p.ObservedCandidateBytes);RetentionRuntimeOutputTests.NoProbes(f);Assert.Equal(before,f.Db.Snapshot());
    }
    [Fact] public async Task LatestIsNotLimitedByPageOrCutoffAndTiesUseBinary()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("A",path:Path.Combine(f.Videos,"upper.mp4"));f.Seed("a",path:Path.Combine(f.Videos,"lower.mp4"));
        f.Seed("z",completed:f.Before.AddDays(1));var s=Service(f);
        var p=await Read(s,f.Before,2,100);var map=p.Items.ToDictionary(x=>x.RecordingId!);
        Assert.Equal("within_keep_latest",map["a"].ReasonCode);Assert.Equal("within_keep_latest",map["z"].ReasonCode);Assert.Equal("age_rule_match",map["A"].ReasonCode);Assert.Single(f.Files.Observations);
    }
    [Fact] public async Task RuleCursorBindsNPrincipalRootAndCutoffAndRejectsOldCursor()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");var s=Service(f);
        var first=await Read(s,f.Before);Assert.NotNull(first.NextCursor);
        await Assert.ThrowsAsync<UnifiedHistoryArgumentException>(()=>Read(s,f.Before,2,cursor:first.NextCursor));
        await Assert.ThrowsAsync<UnifiedHistoryArgumentException>(()=>Read(s,f.Before.AddSeconds(1),cursor:first.NextCursor));
        await Assert.ThrowsAsync<UnifiedHistoryArgumentException>(()=>Read(s,f.Before,cursor:first.NextCursor,sid:"other"));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>f.Service.GetPage(Sid,Session,1,f.Before,first.NextCursor));
        var old=f.Service.GetPage(Sid,Session,1,f.Before);Assert.NotNull(old.NextCursor);
        Assert.NotNull(f.Service.GetPage(Sid,Session,1,f.Before,old.NextCursor));
        await Assert.ThrowsAsync<UnifiedHistoryArgumentException>(()=>Read(s,f.Before,cursor:old.NextCursor));
        using var other=new RetentionPreviewQueryTests.Fixture();other.Seed("c");
        await Assert.ThrowsAsync<UnifiedHistoryArgumentException>(()=>Read(Service(other),other.Before,cursor:first.NextCursor));
        var second=await Read(s,f.Before,cursor:first.NextCursor);Assert.NotEqual(first.Items[0].RecordingId,second.Items[0].RecordingId);Assert.Null(second.NextCursor);
    }
    [Fact] public async Task WatermarkExcludesNewMembershipButLatestChangesOnLaterPage()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");var s=Service(f);
        var first=await Read(s,f.Before);f.Seed("z",completed:f.Before.AddSeconds(1));
        var next=await Read(s,f.Before,cursor:first.NextCursor);Assert.DoesNotContain(next.Items,i=>i.RecordingId=="z");
        Assert.Equal("age_rule_match",Assert.Single(next.Items).ReasonCode);Assert.Null(next.NextCursor);
    }
    [Fact] public async Task LatestAndReferencesShareOnePageTransaction()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("a");f.Seed("z",completed:f.Before.AddSeconds(1));
        var s=Service(f);using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Db.Query.LatestReadForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var pending=Read(s,f.Before,1,100);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Db.SeedFuture("new-reference",outputPath:row.OutputPath);}
        finally{release.Set();}
        Assert.Equal("age_rule_match",(await pending).Items.Single(x=>x.RecordingId=="a").ReasonCode);
        f.Db.Query.LatestReadForTests=null;
        Assert.Equal("planned_output_path",(await Read(s,f.Before,1,100)).Items.Single(x=>x.RecordingId=="a").ReasonCode);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ActualWorkerRemainsBusyAfterTimeoutOrCancellation(bool cancel)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var s=Service(f);using var release=new ManualResetEventSlim();using var cts=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var deadline=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.DeadlineForTests=t=>deadline.Task.WaitAsync(t);s.WorkerExitedForTests=()=>exited.TrySetResult();
        f.Db.Query.LatestReadForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var pending=s.ReadWithKeepLatestAsync(1,f.Before,null,1,()=>(Sid,Session),cts.Token);
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(cancel)cts.Cancel();else deadline.TrySetResult();Assert.Equal(cancel?"cancelled":"timeout",(await pending)!.State);
            Assert.Equal("artifact_metadata_worker_pending",HostReadOnlyWorkerState.BusyReason);
            Assert.Equal("busy",(await RetentionEvaluation387Tests.Service(f).ReadAsync(new[]{"a"},f.Before,Sid,Session))!.State);RetentionRuntimeOutputTests.NoProbes(f);
        } finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Null(HostReadOnlyWorkerState.BusyReason);
    }
    [Theory][InlineData("schema")][InlineData("ordering")]
    public async Task CorruptionCannotBecomeEmptyPage(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");
        f.Db.Corrupt(fault=="ordering"?"UPDATE ordinary_recording_history SET completed_at_utc=NULL;":"DROP INDEX idx_v47_ordinary_media_hash_namespace;");
        await Assert.ThrowsAnyAsync<Exception>(()=>Read(Service(f),f.Before));RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact] public async Task ExternalAndBundleAreKeptButUnkeptRemainProtected()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();
        f.Seed("external",path:Path.Combine(f.Db.Root,"elsewhere","out.mp4"));
        var row=f.Db.Recording("bundle",completed:true) with {OutputPath=Path.Combine(f.Videos,"bundle.mp4"),CompletedAtUtc=f.Before.AddSeconds(1)};
        row=PublicationAnchorTestFixture.Anchored(row);f.Db.Ordinary.Register(row with {BundleRegistration=new(OrdinaryRecordingBundleOperation.Create("bundle",row.OutputPath))});
        var s=Service(f);var both=await Read(s,f.Before,1000,100);
        Assert.All(both.Items,x=>Assert.Equal("within_keep_latest",x.ReasonCode));RetentionRuntimeOutputTests.NoProbes(f);
        var one=await Read(s,f.Before,1,100);Assert.Equal("outside_videos",one.Items.Single(x=>x.RecordingId=="external").ReasonCode);RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact] public async Task CompletionUpdatesCanChangeLatestWithoutChangingMembershipWatermark()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");var pending=f.Seed("c","recording");var s=Service(f);
        var first=await Read(s,f.Before);Assert.NotNull(first.NextCursor);
        var done=PublicationAnchorTestFixture.Anchored(pending with {Status="completed",StartedAtUtc=At.AddSeconds(1),CompletedAtUtc=f.Before.AddSeconds(1),OutputSizeBytes=12,OutputDurationSeconds=1});
        Assert.True(f.Db.Ordinary.TryPublish(done with {BundleRegistration=new(null)}));
        var second=await Read(s,f.Before,cursor:first.NextCursor);Assert.Equal("age_rule_match",Assert.Single(second.Items).ReasonCode);
        var third=await Read(s,f.Before,cursor:second.NextCursor);Assert.Equal("c",Assert.Single(third.Items).RecordingId);Assert.Equal("within_keep_latest",third.Items[0].ReasonCode);
    }
    private sealed class Tray:ITrayContext {
        public string HostMode=>"headless";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new Exception("Forbidden UI");
        public void RequestRegionSelection(int n,Action<string,int,int,int,int,string,string> c)=>throw new Exception("Forbidden capture");
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Harness:IDisposable {
        internal readonly RetentionPreviewQueryTests.Fixture F=new();internal readonly RecordingEngine Engine=new(new RetentionSharedOutputPathTests.QuietAudit());
        internal readonly ApiServer Server;internal readonly HttpClient Client;internal int Reads;
        internal Harness(bool supported=true) {
            F.Seed("a");F.Db.Query.LatestReadForTests=()=>Reads++;
            var gateway=new SqliteRetentionPreviewGateway(supported?Service(F):F.Service,()=>(Sid,Session));ApiKeyAuth.InitializeForTesting(F.Db.Root);
            Server=new(Engine,new RetentionSharedOutputPathTests.QuietAudit(),new Tray(),null,null,null,null,null,null,null,null,null,listenPort:0,retentionPreviewGateway:gateway);Server.Start();
            Client=new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Server.BoundPort}")};Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        }
        public void Dispose(){Client.Dispose();Server.Stop();Engine.Dispose();ApiKeyAuth.ResetForTesting(null);F.Dispose();}
    }
    [Theory][InlineData("")][InlineData("%20")][InlineData("%2B1")][InlineData("-1")][InlineData("0")][InlineData("1.0")][InlineData("1e0")][InlineData("1001")][InlineData("99999999999999999")][InlineData("1&keep_latest_count=2")][InlineData("1&unknown=x")][InlineData("1&limit=1%00")][InlineData("1&limit=%201")]
    public async Task InvalidQueriesRejectBeforeReads(string value)
    {
        using var h=new Harness();using var response=await h.Client.GetAsync("/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z&keep_latest_count="+value);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,h.Reads);RetentionRuntimeOutputTests.NoProbes(h.F);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ApiRequiresSupportedProviderAndKeepsOldResponse(bool supported)
    {
        using var h=new Harness(supported);const string path="/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z";
        using var response=await h.Client.GetAsync(path+"&keep_latest_count=1");Assert.Equal(supported?HttpStatusCode.OK:HttpStatusCode.ServiceUnavailable,response.StatusCode);
        if(supported){using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("page_local",d.RootElement.GetProperty("data").GetProperty("policy").GetProperty("snapshot_scope").GetString());}
        using var old=await h.Client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,old.StatusCode);using var doc=JsonDocument.Parse(await old.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("data").TryGetProperty("policy",out _));
        using var cap=await h.Client.GetAsync("/api/v1/capabilities");using var c=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());var branch=c.RootElement.GetProperty("data").GetProperty("retention_preview");Assert.Equal(supported,branch.GetProperty("keep_latest_supported").GetBoolean());Assert.Equal(1000,branch.GetProperty("keep_latest_count_max").GetInt32());
    }
}
