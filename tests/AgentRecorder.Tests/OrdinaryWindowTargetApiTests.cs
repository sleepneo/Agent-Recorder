using System.Diagnostics;
using System.Net;
using System.Text;
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
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
using static AgentRecorder.Tests.OrdinaryWindowTargetHistoryTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryWindowTargetApiTests
{
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback)=>Confirm=callback;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> callback)=>throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Probe : IWgcContinuousAvailabilityProbe
    {public WgcContinuousAvailabilityResult Check(CaptureConfig cfg)=>new(true,"owned_fixture","test");}
    private sealed class Capacity : IStorageCapacityProvider
    {public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths,CancellationToken t)=>Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"synthetic",100L*1024*1024*1024)).ToArray());}
    private sealed class Devices : ISystemAudioEndpointProvider
    {
        private static readonly SystemAudioEndpointInfo Endpoint=new("task336-endpoint","Synthetic output","render","active",true);
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken t=default)=>Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[]{Endpoint});
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken t=default)=>Task.FromResult<SystemAudioEndpointInfo?>(Endpoint);
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id,CancellationToken t=default)=>Task.FromResult<SystemAudioEndpointInfo?>(id==Endpoint.Id?Endpoint:null);
    }
    private sealed class Backend : ICaptureBackend,IFirstFrameObservableCaptureBackend
    {
        internal int Starts;internal CaptureAuthorizationProof? Proof;internal CaptureConfig? Config;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void Start(CaptureConfig cfg,CaptureAuthorizationProof proof) {Starts++;Proof=proof;Config=cfg;FirstFrameObserved?.Invoke(new(){FrameNumber=1,TotalSizeBytes=1,MediaStartSystemRelativeTimeHns=100});}
        public OutputMeta Stop()=>new(){OutputPath=Config!.OutputPath,SizeBytes=1024,DurationSeconds=1,AudioStatus=Config.IsSystemLoopback?"system_loopback_recorded":"not_requested",HasAudioStream=Config.IsSystemLoopback,Width=320,Height=240,
            PublicationReceipt=PublicationReceipt.IssueForTests(Config.OutputPath,1024,new ObservedFileIdentity(0x123456789abcdef0,0xfedcba9876543210,0x876543210abcdef0))};
        public void OnNaturalExit(Action<int,OutputMeta> callback){}public int ExitCode=>0;public void Dispose(){}
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly UnifiedHistoryTestDatabase Db=Database();internal readonly Tray Tray=new();internal readonly Backend Backend=new();
        internal readonly RecordingEngine Engine;internal readonly UnifiedHistoryQueryService Query;internal readonly ApiServer Api;internal readonly HttpClient Client;
        internal int FactoryCalls;internal int Observations;internal Func<nint,int,WindowProcessMetadata?> Metadata=(_,_)=>OrdinaryWindowTargetKeyTests.Facts();
        private readonly string? _data=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"),_mode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        private readonly string? _priorDir=DataDirResolver.HasOverride?DataDirResolver.Resolve():null;
        private readonly IStorageCapacityProvider _capacity=RecordingPreflightChecker.StorageCapacityProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _helper=RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly RecordingPreflightChecker.RunAudioHelperProbe _probe=RecordingPreflightChecker.AudioHelperProbeRunner;
        internal Fixture()
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",Db.Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",null);DataDirResolver.SetOverride(Db.Root);
            RecordingPreflightChecker.StorageCapacityProvider=new Capacity();
            RecordingPreflightChecker.AudioHelperPathResolver=()=>"task336-version-only.exe";
            RecordingPreflightChecker.AudioHelperProbeRunner=(_,_)=>new(){Success=true,ProtocolVersion="audio-helper-v1",TimestampFrequency=Stopwatch.Frequency};
            SystemQuery.SetWindowProvider((_,_)=>new(){new("window_42","Owned synthetic title","task336.exe",7,false,false,new(0,0,320,240))});
            SystemQuery.SetDisplayProvider(()=>new(){new("display_1","Synthetic",true,new(0,0,1920,1080),1)});
            Engine=new(new RetentionSharedOutputPathTests.QuietAudit(),systemAudioEndpointProvider:new Devices()){CountdownSteps=0,DisableDeadlineWatchdogForTests=true};
            Engine.CapturePlanFactoryForTests=cfg=>CaptureBackendSelector.BuildPlan(cfg,new Probe());
            Engine.OrdinaryWindowMetadataForTests=(hwnd,pid)=>{Observations++;Assert.False(Monitor.IsEntered(Engine));Assert.Equal(0,FactoryCalls);Assert.Equal(0,Backend.Starts);return Metadata(hwnd,pid);};
            Engine.BackendFactory=cfg=>{FactoryCalls++;return (Backend,cfg.IsSystemLoopback?"wgc-window-av-split":CaptureBackendSelector.WgcContinuousBackend);};
            Engine.ConfigureOrdinaryRecordingHistory(Db.Ordinary);Engine.SetTray(Tray);Query=new(Db.Store,Db.Ordinary);
            ApiKeyAuth.InitializeForTesting(Db.Root);Api=new(Engine,new RetentionSharedOutputPathTests.QuietAudit(),Tray,null,null,null,null,null,null,null,null,null,0,
                unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(Query,()=> (Sid,Session)));Api.Start();
            Client=new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Api.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
            Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        }
        internal JsonObject Request(bool audio=false)=>new(){["mode"]="video",["source"]=new JsonObject{["type"]="window",["window_id"]="window_42"},
            ["required_capture_semantics"]="window_surface",["stop_condition"]=new JsonObject{["type"]="duration",["seconds"]=60},["countdown_seconds"]=0,
            ["audio"]=audio?new JsonObject{["system_audio"]=new JsonObject{["enabled"]=true}}:null,
            ["output"]=new JsonObject{["directory"]=Db.Root,["filename"]=Guid.NewGuid().ToString("N")+".mp4",["conflict_policy"]="fail"}};
        internal async Task Accepted(JsonObject request) {using var response=await Client.PostAsync("/api/v1/recordings?creation_wait_ms=0",new StringContent(request.ToJsonString(),Encoding.UTF8,"application/json"));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());}
        public void Dispose()
        {
            Api.Stop();Engine.Dispose();Client.Dispose();Db.Dispose();ApiKeyAuth.ResetForTesting(null);SystemQuery.SetWindowProvider(null);SystemQuery.SetDisplayProvider(null);
            RecordingPreflightChecker.StorageCapacityProvider=_capacity;RecordingPreflightChecker.AudioHelperPathResolver=_helper;RecordingPreflightChecker.AudioHelperProbeRunner=_probe;
            if(_priorDir is null)DataDirResolver.ClearOverride();else DataDirResolver.SetOverride(_priorDir);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_data);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_mode);
        }
    }
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return d.RootElement.GetProperty("data").Clone();}
    private static async Task Until(Func<bool> p) {var end=DateTime.UtcNow.AddSeconds(8);while(!p()&&DateTime.UtcNow<end)await Task.Delay(10);Assert.True(p());}

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task FormalStrictWindowFreezesBeforeApprovalAndReusesAcrossDirectoryTerminalAndRestart(bool audio,bool reject)
    {
        using var f=new Fixture();await f.Accepted(f.Request(audio));var rec=Assert.Single(f.Engine._recs.Values);
        var key=OrdinaryWindowTargetKey.Create(rec.ApprovedCapturePlan,OrdinaryWindowTargetKeyTests.Facts());Assert.NotNull(key);
        Assert.Equal(RecState.pending_confirmation,rec.State);Assert.Equal(key,f.Db.Ordinary.Read(rec.Id)!.TargetKey);Assert.Equal(1,f.Observations);Assert.Equal(0,f.FactoryCalls);Assert.Equal(0,f.Backend.Starts);Assert.Null(rec.AuthorizationProof);
        f.Metadata=(_,_)=>throw new InvalidOperationException("Frozen facts must not be re-observed");
        var selected=Path.Combine(f.Db.Root,"approved");f.Tray.Confirm!(reject?ConfirmationDecision.Reject():ConfirmationDecision.Approve(selected));
        if(reject){Assert.Equal(RecState.rejected,rec.State);Assert.Equal(0,f.Backend.Starts);}
        else {await Until(()=>rec.State==RecState.recording);Assert.Equal(1,f.Backend.Starts);Assert.NotNull(f.Backend.Proof);Assert.Equal(selected,Path.GetDirectoryName(rec.OutputPath));f.Engine.Stop(rec.Id,"user_requested");await Until(()=>rec.IsFinalized);Assert.Equal(RecState.completed,rec.State);}
        Assert.Equal(key,f.Db.Ordinary.Read(rec.Id)!.TargetKey);Assert.Equal(1,f.Observations);
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+rec.Id,"/api/v1/history?source=ordinary&target_key="+key}) {
            using var response=await f.Client.GetAsync(path);var data=await Data(response);var item=path.Contains("/recording-history/")?data.GetProperty("recording"):Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(key,item.GetProperty("target_key").GetString());
        }
        using var restarted=new SqliteOrdinaryRecordingHistoryStore(f.Db.Store);Assert.Equal(key,restarted.Read(rec.Id)!.TargetKey);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("exception")] [InlineData("changed")] [InlineData("request_injection")]
    public async Task MissingOrChangedMetadataDoesNotDisableLegalRecordingAndBodyCannotSetHistoryKey(string outcome)
    {
        using var f=new Fixture();f.Metadata=outcome switch {"exception"=>(_,_)=>throw new IOException("metadata unavailable"),"changed"=>(_,_)=>OrdinaryWindowTargetKeyTests.Facts(pid:8),_=>(_,_)=>null};
        var request=f.Request();if(outcome=="request_injection")request["target_key"]=Key;
        await f.Accepted(request);var rec=Assert.Single(f.Engine._recs.Values);Assert.Null(f.Db.Ordinary.Read(rec.Id)!.TargetKey);f.Metadata=(_,_)=>OrdinaryWindowTargetKeyTests.Facts();
        f.Tray.Confirm!(ConfirmationDecision.Approve());await Until(()=>rec.State==RecState.recording);Assert.Equal(1,f.Backend.Starts);Assert.Null(f.Db.Ordinary.Read(rec.Id)!.TargetKey);
        f.Engine.Stop(rec.Id,"user_requested");await Until(()=>rec.IsFinalized);Assert.Equal(RecState.completed,rec.State);Assert.Null(f.Db.Ordinary.Read(rec.Id)!.TargetKey);Assert.Equal(1,f.Observations);
    }

    [Fact]
    public async Task ObservationIsOutsideGlobalLockAndRegistrationOnlyOccursAfterBoundedObservationReturns()
    {
        using var f=new Fixture();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Metadata=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return OrdinaryWindowTargetKeyTests.Facts();};
        var request=f.Accepted(f.Request());Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try {Assert.Empty(f.Engine._recs);Assert.Equal(0,f.Db.Scalar("SELECT COUNT(*) FROM ordinary_recording_history;"));
            var engineLock=typeof(RecordingEngine).GetField("_lock",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Engine)!;
            Assert.True(Monitor.TryEnter(engineLock,TimeSpan.FromSeconds(1)));Monitor.Exit(engineLock);Assert.Equal(0,f.FactoryCalls);
        }finally{release.Set();}
        await request;var rec=Assert.Single(f.Engine._recs.Values);Assert.Equal(Key,f.Db.Ordinary.Read(rec.Id)!.TargetKey);f.Tray.Confirm!(ConfirmationDecision.Reject());
    }

    [Fact]
    public async Task WindowHistoryHttpRejectsBadFiltersAndCorruptionAfterAuthWithoutMetadataOrCapture()
    {
        using var f=new Fixture();f.Db.Ordinary.Register(Row(f.Db,"r"));
        using(var unknown=await f.Client.GetAsync("/api/v1/history?source=ordinary&target_key="+OtherKey))Assert.Empty((await Data(unknown)).GetProperty("items").EnumerateArray());
        foreach(var q in new[]{"source=all&target_key="+Key,"source=plan&target_key="+Key,"source=ordinary&target_key="+Key.ToUpperInvariant(),"source=ordinary&target_key="+Key+"&plan_id=p","source=ordinary&target_key="+Key+"&target_key="+Key})
            {using var response=await f.Client.GetAsync("/api/v1/history?"+q);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        f.Db.RawExecute("DROP INDEX idx_ordinary_window_targets_key_created;");f.Client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/r","/api/v1/history?source=ordinary&target_key="+Key})
            {using var response=await f.Client.GetAsync(path);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);}
        f.Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        var probes=0;f.Query.OrdinaryFileProbeForTests=_=>{probes++;return true;};
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/r","/api/v1/history?source=ordinary&target_key="+Key})
            {using var response=await f.Client.GetAsync(path);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);}
        Assert.Equal(0,probes);Assert.Equal(0,f.Observations);Assert.Equal(0,f.FactoryCalls);
    }

    [Fact]
    public async Task ActualHistoryHttpKeepsWindowMediaBundleInSameSnapshotDuringCommit()
    {
        using var f=new Fixture();f.Db.Ordinary.Register(Row(f.Db,"r"));
        var end=PublicationAnchorTestFixture.Anchored(Row(f.Db,"r",true));var op=OrdinaryRecordingBundleOperation.Create("r",end.OutputPath);end=end with {BundleRegistration=new(op)};
        f.Query.SnapshotEstablishedForTests=()=> {
            Assert.True(f.Db.Ordinary.TryPublish(end));Assert.True(f.Db.Ordinary.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));Assert.Equal("ready",f.Db.Ordinary.Read("r")!.Bundle!.Status);
            f.Db.Ordinary.Register(Row(f.Db,"late") with{TargetKey=OtherKey});f.Query.SnapshotEstablishedForTests=null;
        };
        using(var first=await f.Client.GetAsync("/api/v1/history?source=ordinary&target_key="+Key)) {
            var item=Assert.Single((await Data(first)).GetProperty("items").EnumerateArray());Assert.Equal(Key,item.GetProperty("target_key").GetString());Assert.Equal("preparing",item.GetProperty("status").GetString());Assert.Equal(JsonValueKind.Null,item.GetProperty("bundle").ValueKind);
        }
        using(var next=await f.Client.GetAsync("/api/v1/history?source=ordinary&target_key="+Key)) {
            var item=Assert.Single((await Data(next)).GetProperty("items").EnumerateArray());Assert.Equal("completed",item.GetProperty("status").GetString());Assert.Equal("ready",item.GetProperty("bundle").GetProperty("status").GetString());Assert.Equal(Key,item.GetProperty("target_key").GetString());
        }
        Assert.Equal(0,f.Observations);Assert.Equal(0,f.FactoryCalls);
    }

    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public async Task ActualHistoryApiWindowSeekMixesFiltersCapsPageBindsCursorAndKeepsWatermark(int limit)
    {
        using var f=new Fixture();
        using(var c=f.Db.Store.OpenConnection())using(var cmd=c.CreateCommand()) {
            cmd.CommandText="WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1200) INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,profile_id,profile_version,profile_digest,updated_at_utc) SELECT 'r'||printf('%05d',x),x,$at-(x/600),'failed','window','window_surface','Synthetic','none',$p,'P',2,$digest,$at FROM n; UPDATE ordinary_recording_window_targets SET target_key=CASE WHEN CAST(substr(recording_id,2) AS INTEGER)%10=0 THEN $key ELSE $other END;";
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$p",Path.Combine(f.Db.Root,"none.mp4"));cmd.Parameters.AddWithValue("$digest",AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64));cmd.Parameters.AddWithValue("$key",Key);cmd.Parameters.AddWithValue("$other",OtherKey);cmd.ExecuteNonQuery();
        }
        var creationKey=OrdinaryWindowTargetKey.Create(OrdinaryWindowTargetKeyTests.Plan(),OrdinaryWindowTargetKeyTests.Facts(creation:133000000000000001))!;
        f.Db.Ordinary.Register(Row(f.Db,"other-process-generation") with {TargetKey=creationKey});
        var plans=new List<string>();f.Query.CandidatesSelectedForTests=(_,n)=>Assert.InRange(n,0,limit+1);f.Query.EntriesValidatedForTests=(_,n)=>Assert.InRange(n,1,limit);
        f.Query.CandidateCommandForTests=(_,sql,args)=> {
            Assert.Contains("t.target_key=$target",sql);Assert.DoesNotContain(Key,sql);Assert.DoesNotContain("OFFSET",sql);
            Assert.Contains(args,a=>a.Key=="$target"&&(string)a.Value==Key);
            using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var rd=cmd.ExecuteReader();while(rd.Read())plans.Add(rd.GetString(3));
        };
        var targetReads=0;f.Query.TargetCommandForTests=(sql,args)=>{targetReads++;Assert.Contains("WITH requested",sql);Assert.InRange(args.Count,1,limit);};
        var prefix="/api/v1/history?source=ordinary&source_type=window&status=failed&profile_id=P&profile_version=2&target_key="+Key+"&created_from="+Uri.EscapeDataString(At.AddTicks(-3).ToString("O"))+"&created_before="+Uri.EscapeDataString(At.AddTicks(1).ToString("O"))+"&limit="+limit;
        var seen=new List<string>();string? cursor=null;var first=true;var pages=0;
        do {
            using var response=await f.Client.GetAsync(prefix+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)));var data=await Data(response);var items=data.GetProperty("items").EnumerateArray().ToArray();Assert.InRange(items.Length,1,limit);Assert.All(items,i=>Assert.Equal(Key,i.GetProperty("target_key").GetString()));seen.AddRange(items.Select(i=>i.GetProperty("recording_id").GetString()!));pages++;
            cursor=data.GetProperty("next_cursor").ValueKind==JsonValueKind.Null?null:data.GetProperty("next_cursor").GetString();
            if(first){first=false;f.Db.Ordinary.Register(Row(f.Db,"late") with {Status="failed",ProfileId="P",ProfileVersion=2,ProfileDigest=AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64)});
                if(cursor is not null){using var changed=await f.Client.GetAsync(prefix.Replace(Key,OtherKey)+"&cursor="+Uri.EscapeDataString(cursor));Assert.Equal(HttpStatusCode.BadRequest,changed.StatusCode);}}
        }while(cursor is not null);
        Assert.Equal(120,seen.Count);Assert.Equal(120,seen.Distinct().Count());Assert.DoesNotContain("late",seen);Assert.DoesNotContain("other-process-generation",seen);
        Assert.Contains(plans,p=>p.Contains("SEARCH t USING COVERING INDEX "+SqliteSchemaV34.IndexName)&&p.Contains("target_key=?"));Assert.DoesNotContain(plans,p=>p.Contains("TEMP B-TREE"));Assert.Contains(plans,p=>p.Contains("created_at_utc=? AND recording_id>?"));Assert.Contains(plans,p=>p.Contains("created_at_utc<?"));Assert.Equal(pages,targetReads);
        f.Query.CandidateCommandForTests=null;f.Query.CandidatesSelectedForTests=null;f.Query.EntriesValidatedForTests=null;f.Query.TargetCommandForTests=null;
        using var tooMany=await f.Client.GetAsync("/api/v1/history?source=ordinary&target_key="+Key+"&limit=101");Assert.Equal(HttpStatusCode.BadRequest,tooMany.StatusCode);
        using var generation=await f.Client.GetAsync("/api/v1/history?source=ordinary&target_key="+creationKey);Assert.Equal("other-process-generation",Assert.Single((await Data(generation)).GetProperty("items").EnumerateArray()).GetProperty("recording_id").GetString());
    }
}
