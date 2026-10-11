using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
using static AgentRecorder.Tests.OrdinaryTargetHistoryTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryTargetApiTests : IDisposable
{
    private readonly UnifiedHistoryTestDatabase _db=Database();
    private readonly string? _priorData=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private readonly string? _priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _priorRegion=Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);
    private readonly Tray _tray=new();
    private RecordingEngine? _engine;private ApiServer? _api;private UnifiedHistoryQueryService? _query;
    private HttpClient Start(bool headless=false)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_db.Root);ApiKeyAuth.InitializeForTesting(_db.Root);
        _tray.Headless=headless;
        if(_engine is null){_engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());_engine.ConfigureOrdinaryRecordingHistory(_db.Ordinary);}_engine.SetTray(_tray);
        _query=new(_db.Store,_db.Ordinary,headless);
        _api=new(_engine,new RetentionSharedOutputPathTests.QuietAudit(),_tray,null,null,null,null,null,null,null,null,null,0,
            unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(_query,()=> (Sid,Session)));_api.Start();
        return new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{_api.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private static void Auth(HttpClient client)=>client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return doc.RootElement.GetProperty("data").Clone(); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ThreeProjectionsExactFilterCapabilitiesNullOthersAndNoCapture(bool headless)
    {
        _db.Ordinary.Register(Row(_db,"a"));_db.Ordinary.Register(Row(_db,"b"));_db.Ordinary.Register(_db.Recording("window") with {SourceType="window",CaptureSemantics="window_capture"});
        _db.SeedPlan("p");_db.SeedOccurrence("p","o");_db.SeedFuture("f");var before=_db.Snapshot();
        using var client=Start(headless);_engine!.BackendFactory=_=>throw new InvalidOperationException("No capture permitted");Auth(client);
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/a","/api/v1/history?source=ordinary&target_key="+Key})
        {
            using var response=await client.GetAsync(path);var data=await Data(response);var item=path.Contains("/recording-history/")?data.GetProperty("recording"):data.GetProperty("items")[0];Assert.Equal(Key,item.GetProperty("target_key").GetString());
        }
        using(var window=await client.GetAsync("/api/v1/recording-history/window"))Assert.Equal(JsonValueKind.Null,(await Data(window)).GetProperty("recording").GetProperty("target_key").ValueKind);
        using(var all=await client.GetAsync("/api/v1/history"))foreach(var item in (await Data(all)).GetProperty("items").EnumerateArray())if(item.GetProperty("source").GetString()!="ordinary")Assert.Equal(JsonValueKind.Null,item.GetProperty("target_key").ValueKind);
        using(var cap=await client.GetAsync("/api/v1/capabilities"))
        {
            var h=(await Data(cap)).GetProperty("unified_history");Assert.Contains(h.GetProperty("filters").EnumerateArray(),e=>e.GetString()=="target_key");
            Assert.Equal("ordinary",Assert.Single(h.GetProperty("target_key_supported_sources").EnumerateArray()).GetString());
            Assert.Equal(new[]{"display","region","fixed_region_profile","window"},h.GetProperty("target_key_source_types").EnumerateArray().Select(e=>e.GetString()));Assert.True(h.GetProperty("target_key_window_supported").GetBoolean());
        }
        using(var first=await client.GetAsync("/api/v1/history?source=ordinary&target_key="+Key+"&limit=1"))
        {
            var cursor=Uri.EscapeDataString((await Data(first)).GetProperty("next_cursor").GetString()!);
            foreach(var suffix in new[]{"","&target_key="+OtherKey})using(var changed=await client.GetAsync("/api/v1/history?source=ordinary&cursor="+cursor+suffix))Assert.Equal(HttpStatusCode.BadRequest,changed.StatusCode);
            using var valid=await client.GetAsync("/api/v1/history?source=ordinary&cursor="+cursor+"&target_key="+Key);Assert.Single((await Data(valid)).GetProperty("items").EnumerateArray());
        }
        using(var unknown=await client.GetAsync("/api/v1/history?source=ordinary&target_key="+OtherKey))Assert.Empty((await Data(unknown)).GetProperty("items").EnumerateArray());
        Assert.Equal(before,_db.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnauthorizedPrecedesDatabaseCorruptionAndCanonicalBadArgumentsAre400(bool headless)
    {
        _db.Ordinary.Register(Row(_db,"a",true));using var client=Start(headless);var probes=0;_query!.OrdinaryFileProbeForTests=_=>{probes++;return true;};
        _db.RawExecute("DROP INDEX idx_ordinary_recording_targets_key_created;");
        foreach(var path in new[]{"/api/v1/history?source=ordinary&target_key="+Key,"/api/v1/recording-history","/api/v1/recording-history/a"})
        {using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);}
        Auth(client);
        var bad=new[]{"target_key="+Key,"source=all&target_key="+Key,"source=plan&target_key="+Key,"source=future_window&target_key="+Key,
            "source=ordinary&target_key=","source=ordinary&target_key="+Key.ToUpperInvariant(),"source=ordinary&target_key="+Key[..^1],
            "source=ordinary&target_key="+Key+"0","source=ordinary&target_key="+Key.Replace("display","window")+"g",
            "source=ordinary&target_key="+Key+"&target_key="+Key,"source=ordinary&target_key=%20"+Key,
            "source=ordinary&target_key="+Key+"&plan_id=p","source=ordinary&target_key="+Key+"&profile_version=2"};
        foreach(var query in bad){using var response=await client.GetAsync("/api/v1/history?"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        foreach(var path in new[]{"/api/v1/history?source=ordinary&target_key="+Key,"/api/v1/recording-history","/api/v1/recording-history/a"})
        {using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.DoesNotContain(_db.Root,await response.Content.ReadAsStringAsync());}
        Assert.Equal(0,probes);
    }

    [Theory]
    [InlineData("display")] [InlineData("region")] [InlineData("profile")]
    public async Task FormalCoreFreezesNormalizedPlanBeforeApprovalAndKeepsItAcrossTerminalApisAndReopen(string origin)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar,null);
        DataDirResolver.SetOverride(_db.Root);
        SystemQuery.SetDisplayTopologyProvider(()=>new(){new("display_1","Synthetic",true,new SystemQuery.Bounds(0,0,1920,1080),1.0,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
        _engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit(),displayTopologyProvider:new Topology());_engine.ConfigureOrdinaryRecordingHistory(_db.Ordinary);_engine.SetTray(_tray);
        var factoryCalls=0;FfmpegCaptureBackend? backend=null;var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.BackendFactory=_=>
        {
            factoryCalls++;backend=new FfmpegCaptureBackend {VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","3","-i","color=c=black:s=64x64:r=15"}};
            backend.FirstFrameObserved+=_=>first.TrySetResult();return (backend,origin=="display"?"ffmpeg":"ffmpeg-region");
        };
        var kind=origin=="display"?"display":"region";
        var source=new JsonObject{["type"]=kind,["display_id"]="display_1"};
        if(kind=="region"){source["coordinate_space"]="virtual_screen";source["bounds"]=new JsonObject{["x"]=10,["y"]=20,["width"]=643,["height"]=481};}
        var request=new JsonObject{["source"]=source,["video"]=new JsonObject{["fps"]=15},["duration_seconds"]=2,["countdown_seconds"]=0,["output"]=new JsonObject{["directory"]=_db.Root,["filename"]="core.mp4",["conflict_policy"]="fail"}};
        if(origin=="profile")_engine.CreateRecordingFromFixedRegionProfile(request,"task331-synthetic",_tray,new("P",2,RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64)),()=>null);
        else _engine.CreateRecording(request,"task331-synthetic",_tray);
        var rec=Assert.Single(_engine._recs.Values);var key=OrdinaryScreenTargetKey.Create(rec.ApprovedCapturePlan)!;
        Assert.NotNull(key);Assert.Equal(0,factoryCalls);Assert.Null(backend);Assert.Equal(RecState.pending_confirmation,rec.State);Assert.Equal(key,_db.Ordinary.Read(rec.Id)!.TargetKey);
        if(kind=="region")Assert.Equal(642,rec.ApprovedCapturePlan!.Bounds!.Width);
        if(kind=="region")Assert.Equal(OrdinaryScreenTargetKey.Create(OrdinaryScreenTargetKeyTests.Plan(identity:"synthetic-test-display:display_1",bounds:new(10,20,642,480),display:new(0,0,1920,1080))),key);
        _tray.Confirm!(ConfirmationDecision.Approve());
        var firstOrFailure=await Task.WhenAny(first.Task,Task.Delay(TimeSpan.FromSeconds(6)));
        Assert.True(firstOrFailure==first.Task,$"Synthetic capture failed to start: {rec.State}, {rec.Error}");await Until(()=>rec.State==RecState.recording);
        Assert.Equal(1,factoryCalls);Assert.NotNull(rec.AuthorizationProof); // Existing local authorization is still required.
        await Until(()=>rec.IsFinalized);Assert.True(rec.State==RecState.completed,$"Synthetic media terminal: {rec.State}, {rec.Error}");Assert.Equal(key,_db.Ordinary.Read(rec.Id)!.TargetKey);
        rec.Config.DisplayStableIdentity="changed-after-capture";rec.Config.Bounds=(100,200,64,64);rec.SourceTitle="changed title";
        // Replacing a mutable engine reference after capture cannot recompute persisted identity.
        rec.ApprovedCapturePlan=OrdinaryScreenTargetKeyTests.Plan(kind:kind);using var client=Start();Auth(client);
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+rec.Id,"/api/v1/history?source=ordinary&target_key="+key})
        {
            using var response=await client.GetAsync(path);var data=await Data(response);var item=path.Contains("/recording-history/")?data.GetProperty("recording"):Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(key,item.GetProperty("target_key").GetString());
            if(origin=="profile")Assert.Equal("fixed_region_profile",item.GetProperty("source_type").GetString());
        }
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(_db.Store);Assert.Equal(key,reopened.Read(rec.Id)!.TargetKey);backend!.Dispose();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingNullAndDriftDoNotChangeApprovalOrRecomputeTarget(bool unresolved)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(_db.Root);
        var approved=OrdinaryScreenTargetKeyTests.Plan(kind:"display",identity:"synthetic-test-display:display_1",bounds:new(0,0,1920,1080),display:new(0,0,1920,1080),
            status:unresolved?DisplayIdentityResolutionStatus.Unresolved:DisplayIdentityResolutionStatus.Resolved,backend:"ffmpeg");
        var changed=OrdinaryScreenTargetKeyTests.Plan(kind:"display",identity:"synthetic-test-display:display_1",bounds:new(10,0,1900,1080),display:new(0,0,1920,1080),backend:"ffmpeg");
        var plans=0;var backends=0;_engine=new(new RetentionSharedOutputPathTests.QuietAudit(),displayTopologyProvider:new Topology());
        _engine.CapturePlanFactoryForTests=_=>++plans==1?approved:changed;_engine.BackendFactory=_=>{backends++;throw new InvalidOperationException("Approval must abort before backend construction");};
        _engine.ConfigureOrdinaryRecordingHistory(_db.Ordinary);_engine.SetTray(_tray);
        _engine.CreateRecording(new JsonObject{["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["duration_seconds"]=2,["countdown_seconds"]=0,
            ["output"]=new JsonObject{["directory"]=_db.Root,["filename"]="pending.mp4",["conflict_policy"]="fail"}},"task331-synthetic",_tray);
        var rec=Assert.Single(_engine._recs.Values);var key=OrdinaryScreenTargetKey.Create(approved);Assert.Equal(key,_db.Ordinary.Read(rec.Id)!.TargetKey);
        Assert.Throws<InvalidOperationException>(()=>rec.FreezeOrdinaryTarget(changed));
        rec.Config.DisplayStableIdentity="changed mutable config";
        _tray.Confirm!(unresolved?ConfirmationDecision.Reject():ConfirmationDecision.Approve());
        Assert.Equal(unresolved?RecState.rejected:RecState.failed,rec.State);Assert.Equal(0,backends);Assert.Equal(key,_db.Ordinary.Read(rec.Id)!.TargetKey);
        if(!unresolved)Assert.Equal("capture_semantics_changed",rec.Error);else Assert.Null(key);
    }

    private static async Task Until(Func<bool> predicate)
    {var until=DateTime.UtcNow.AddSeconds(8);while(!predicate()&&DateTime.UtcNow<until)await Task.Delay(10);Assert.True(predicate());}
    public void Dispose()
    {
        _api?.Stop();_engine?.Dispose();_db.Dispose();ApiKeyAuth.ResetForTesting(null);DataDirResolver.ClearOverride();SystemQuery.SetDisplayTopologyProvider(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_priorData);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_priorMode);Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar,_priorRegion);
    }
    private sealed class Tray : ITrayContext
    {
        internal bool Headless;internal Action<ConfirmationDecision>? Confirm;public string HostMode=>Headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback)=>Confirm=callback;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> callback)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Topology : IDisplayTopologyProvider
    {
        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays()=>new[]{new DisplayTopologySnapshot("display_1","synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved,new(0,0,1920,1080))};
    }
}
