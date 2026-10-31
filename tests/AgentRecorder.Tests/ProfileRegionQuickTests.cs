using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;
using AgentRecorder.App;
using AgentRecorder.Windows;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using System.Text.Json.Nodes;

namespace AgentRecorder.Tests;
public sealed partial class CodexProfileRecordingRegressionTests
{
    private sealed class RegionBundleSpy : IRecordingBundleGenerator
    {
        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(RecordingBundleGenerationResult.Failed("synthetic_no_media"));
    }
    private readonly RegionEndpoints _regionAudio=new();
    private readonly RegionMicrophones _regionMic=new();
    private sealed class RegionMicrophones : IMicrophoneDeviceProvider, IMicrophoneStatusProvider
    {
        internal int Calls;
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken=default){Calls++;return Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(new[]{new MicrophoneDeviceInfo("mic-fixed","Fake mic",true,"active",false)});}
        public Task<MicrophoneStatus> GetStatusAsync(string id,CancellationToken cancellationToken=default){Calls++;return Task.FromResult(new MicrophoneStatus(false,50,true,"active"));}
    }
    private readonly RecordingPreflightChecker.TryResolveAudioHelper _regionOldHelper=RecordingPreflightChecker.AudioHelperPathResolver;
    private readonly RecordingPreflightChecker.RunAudioHelperProbe _regionOldProbe=RecordingPreflightChecker.AudioHelperProbeRunner;
    private readonly Func<bool> _regionOldWasapi=RecordingPreflightChecker.ShouldUseWasapiBackend;
    private void RegionHelper()
    {
        RecordingPreflightChecker.AudioHelperPathResolver=()=>"private-task367-helper";
        RecordingPreflightChecker.AudioHelperProbeRunner=(_,_)=>new(){Success=true,ProtocolVersion="audio-helper-v1",TimestampFrequency=System.Diagnostics.Stopwatch.Frequency};
        RecordingPreflightChecker.ShouldUseWasapiBackend=()=>true;
    }
    private sealed class RegionEndpoints : ISystemAudioEndpointProvider
    {
        internal int Calls;
        private readonly SystemAudioEndpointInfo endpoint=new("render-fixed","Fake speakers","render","active",true);
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken cancellationToken=default){Calls++;return Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[]{endpoint});}
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken cancellationToken=default){Calls++;return Task.FromResult<SystemAudioEndpointInfo?>(endpoint);}
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string endpointId,CancellationToken cancellationToken=default){Calls++;return Task.FromResult<SystemAudioEndpointInfo?>(endpointId==endpoint.Id?endpoint:null);}
    }
    private string RegionBody(object? audio=null,int countdown=3,int oldCountdown=5)
    {
        var p=CreateProfile(countdown:oldCountdown);
        return JsonSerializer.Serialize(new{target=new{type="profile_region",profile_ref=new{id=p.ProfileId,version=p.ProfileVersion,digest=p.ProfileDigest}},duration_seconds=20,countdown_seconds=countdown,audio=audio??new{},output=new{directory=_temp.Path}});
    }
    private Task<HttpResponseMessage> RegionPost(string body)=>_client.PostAsync("/api/v1/recordings/quick?creation_wait_ms=0",new StringContent(body,Encoding.UTF8,"application/json"));
    [Theory]
    [InlineData(5)] [InlineData(10)] [InlineData(60)]
    public async Task RegionOnlyFreezesNewParametersWithoutInheritingProfile(int oldCountdown)
    {
        if(oldCountdown==60)
        {
            // Keep the original fixture's case visible: this checkout cannot create a legal
            // 60-second Profile. Do not change the immutable Profile domain to invent one.
            Assert.Throws<AgentRecorder.Core.Automation.Phase3DomainException>(()=>CreateProfile(countdown:60));return;
        }
        var body=RegionBody(oldCountdown:oldCountdown);
        using var response=await RegionPost(body);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,text);
        using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");
        Assert.Equal("region_only",data.GetProperty("profile_reuse_mode").GetString());
        var rec=_engine._recs[data.GetProperty("recording_id").GetString()!];
        Assert.Equal(20,rec.DurationSeconds);Assert.Equal(3,rec.CountdownSeconds);Assert.False(rec.Microphone);Assert.Equal(0,_regionAudio.Calls);
        Assert.Equal((-90,70,640,480),rec.Config.Bounds);Assert.Equal(0,_backend.StartCalls);
        Assert.Equal(_temp.Path,Path.GetDirectoryName(rec.OutputPath));Assert.Equal(60,_versions.Get(rec.FixedRegionProfileReference!.Value.ProfileId,1).Duration.TotalSeconds);
        var current=_management.Get(rec.FixedRegionProfileReference.Value.ProfileId)!;
        _management.Patch(current.CurrentVersion.ProfileId,AgentRecorder.Core.Automation.FixedRegionProfileETag.Compute(current),"renamed",
            new AgentRecorder.Core.Automation.FixedRegionProfileChanges(DurationSeconds:120),DateTimeOffset.UtcNow);
        Assert.Equal(1,rec.FixedRegionProfileReference.Value.ProfileVersion);Assert.Equal(20,rec.DurationSeconds);
        var selected=Path.Combine(_temp.Path,"new-location");_tray.Decision!(ConfirmationDecision.Approve(selected));
        Assert.True(SpinWait.SpinUntil(()=>rec.State==RecState.recording,TimeSpan.FromSeconds(3)));
        Assert.Equal(1,_backend.StartCalls);Assert.Equal(selected,Path.GetDirectoryName(_backend.Config!.OutputPath));Assert.Equal(CaptureAuthorizationProofState.Consumed,_backend.Proof!.State);
        var evidence=Environment.GetEnvironmentVariable("TASK367_EVIDENCE_ROOT");
        if(evidence is not null)File.WriteAllText(Path.Combine(evidence,$"silent-{oldCountdown}.json"),JsonSerializer.Serialize(new{request=JsonNode.Parse(body),response=JsonNode.Parse(text),effective=new{duration=rec.DurationSeconds,countdown=rec.CountdownSeconds,bounds=rec.Config.Bounds,output=rec.Config.OutputPath},proof=new{kind=_backend.Proof.Kind,state=_backend.Proof.State}}));
    }
    [Theory]
    [InlineData("render-fixed",true)] [InlineData("missing",false)]
    public async Task RegionOnlySystemAudioUsesExactOrdinaryEndpoint(string id,bool ok)
    {
        RegionHelper();
        using var response=await RegionPost(RegionBody(new{system_audio=new{enabled=true,device_id=id}}));
        if(!ok){Assert.False(response.IsSuccessStatusCode);Assert.Equal(0,_backend.StartCalls);return;}
        var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,text);
        var rec=Assert.Single(_engine._recs.Values);Assert.Equal("render-fixed",rec.SystemAudioEndpointId);Assert.Equal(20,rec.DurationSeconds);Assert.Equal(0,_backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Approve());Assert.True(SpinWait.SpinUntil(()=>rec.State==RecState.recording,TimeSpan.FromSeconds(3)));
        Assert.Equal("render-fixed",_backend.Config!.SystemLoopbackEndpoint);Assert.Equal(CaptureAuthorizationProofState.Consumed,_backend.Proof!.State);
    }
    [Theory]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":null")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20.0")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":true")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":601")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20,\"duration_seconds\":10")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20,\"artifacts\":false")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20,\"nested\":{}")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20,\"stop_condition\":{}")]
    [InlineData("\"duration_seconds\":20","\"duration_seconds\":20,\"unknown\":0")]
    [InlineData("\"type\":\"profile_region\"","\"type\":\"profile_region\",\"type\":\"primary_display\"")]
    public async Task RegionOnlyInvalidTokensRejectBeforeProviders(string old,string replacement)
    {
        using var response=await RegionPost(RegionBody().Replace(old,replacement));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal(0,_regionAudio.Calls);Assert.Empty(_engine._recs);Assert.Null(_tray.Decision);Assert.Equal(0,_backend.StartCalls);
    }
    [Fact]
    public async Task RegionOnlyApprovalRechecksOriginalEnvironment()
    {
        using var response=await RegionPost(RegionBody());Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=Assert.Single(_engine._recs.Values);_display.Metadata=_display.Metadata with{DpiX=120};
        _tray.Decision!(ConfirmationDecision.Approve());Assert.True(SpinWait.SpinUntil(()=>rec.IsFinalized,TimeSpan.FromSeconds(3)));Assert.Equal(0,_backend.StartCalls);
    }
    [Fact]
    public async Task RegionOnlyMicrophoneKeepsApprovedDeviceAndDoesNotSelectAgain()
    {
        RegionHelper();using var response=await RegionPost(RegionBody(new{microphone=new{enabled=true,device_id="mic-fixed"}},countdown:5));
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var rec=Assert.Single(_engine._recs.Values);
        Assert.Equal(5,rec.CountdownSeconds);Assert.Equal("mic-fixed",rec.MicrophoneDeviceId);Assert.Equal(0,_backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Approve());Assert.True(SpinWait.SpinUntil(()=>rec.State==RecState.recording,TimeSpan.FromSeconds(3)));
        Assert.Equal("mic-fixed",_backend.Config!.MicDevice);Assert.Equal(CaptureAuthorizationProofState.Consumed,_backend.Proof!.State);
    }
    [Fact]
    public async Task RegionOnlyOmissionsRemainSilentAndHistorySurvivesNewReader()
    {
        using var history=new SqliteOrdinaryRecordingHistoryStore(new SqliteOperationalStore(Path.Combine(_temp.Path,"state.db")));
        _engine.ConfigureOrdinaryRecordingHistory(history);
        var body=JsonNode.Parse(RegionBody())!.AsObject();body.Remove("audio");body.Remove("countdown_seconds");body.Remove("output");
        using var response=await RegionPost(body.ToJsonString());Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=Assert.Single(_engine._recs.Values);Assert.Equal(3,rec.CountdownSeconds);Assert.Equal(0,_regionAudio.Calls);
        Assert.NotEqual(Path.Combine(_temp.Path,"profile-output"),Path.GetDirectoryName(rec.OutputPath));
        using var reader=new SqliteOrdinaryRecordingHistoryStore(new SqliteOperationalStore(Path.Combine(_temp.Path,"state.db")));
        var row=reader.Read(rec.Id)!;Assert.Equal("region_only",row.ProfileReuseMode);Assert.Equal(rec.FixedRegionProfileReference!.Value.ProfileDigest,row.ProfileDigest);Assert.Equal(20,row.RequestedDurationSeconds);Assert.Equal("none",row.AudioMode);
        _tray.Decision!(ConfirmationDecision.Reject());Assert.Equal(0,_backend.StartCalls);
    }
    [Theory]
    [InlineData("audio", "{\"system_audio\":{\"enabled\":true,\"enabled\":false}}")]
    [InlineData("audio", "{\"system_audio\":{\"enabled\":true},\"microphone\":{\"enabled\":true}}")]
    [InlineData("video", "{\"fps\":\"30\"}")]
    [InlineData("output", "{\"directory\":null}")]
    [InlineData("countdown_seconds", "null")]
    public async Task RegionOnlyDeepMalformedFieldsHaveNoSideEffects(string field,string invalid)
    {
        var node=JsonNode.Parse(RegionBody())!.AsObject();node.Remove(field);var body=node.ToJsonString();body=body[..^1]+",\""+field+"\":"+invalid+"}";
        using var response=await RegionPost(body);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,_regionAudio.Calls);Assert.Null(_tray.Decision);Assert.Empty(_engine._recs);
    }
    [Theory]
    [InlineData("digest")] [InlineData("missing")] [InlineData("version")] [InlineData("deleted")]
    public async Task RegionOnlyExactReferenceCannotFollowLatestOrFallback(string kind)
    {
        var body=JsonNode.Parse(RegionBody())!;var reference=body["target"]!["profile_ref"]!;
        if(kind=="digest"){var digest=reference["digest"]!.GetValue<string>();reference["digest"]=digest[..^1]+(digest[^1]=='a'?"b":"a");}
        else if(kind=="version")reference["version"]=999;
        else if(kind=="deleted"){var d=_management.Get(reference["id"]!.GetValue<string>())!;_management.Delete(d.CurrentVersion.ProfileId,AgentRecorder.Core.Automation.FixedRegionProfileETag.Compute(d),DateTimeOffset.UtcNow);}
        else reference["id"]="missing-profile";
        using var response=await RegionPost(body.ToJsonString());Assert.False(response.IsSuccessStatusCode);Assert.Empty(_engine._recs);Assert.Null(_tray.Decision);Assert.Equal(0,_regionAudio.Calls);
        Assert.Equal(kind is "digest" or "deleted"?HttpStatusCode.Conflict:HttpStatusCode.NotFound,response.StatusCode);
    }
    [Fact]
    public async Task RegionOnlyHeadlessCapabilityMatchesExecutionRefusal()
    {
        RegionHelper();_tray.RegionUi=false;
        using var cap=await _client.GetAsync("/api/v1/capabilities");using var json=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("profile_region").GetProperty("supported").GetBoolean());
        using var response=await RegionPost(RegionBody());Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Empty(_engine._recs);Assert.Null(_tray.Decision);
    }
    [Fact]
    public async Task RegionOnlyDurationCannotBeImplicit()
    {
        var body=JsonNode.Parse(RegionBody())!.AsObject();body.Remove("duration_seconds");
        using var response=await RegionPost(body.ToJsonString());Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Empty(_engine._recs);Assert.Equal(0,_regionAudio.Calls);
    }
    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(600)]
    public async Task RegionOnlyOrdinaryDurationBoundsRemainLocalApprovalGated(int duration)
    {
        var body=JsonNode.Parse(RegionBody())!;body["duration_seconds"]=duration;
        using var response=await RegionPost(body.ToJsonString());Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=Assert.Single(_engine._recs.Values);Assert.Equal(duration,rec.DurationSeconds);Assert.Equal(0,_backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Reject());Assert.Equal(0,_backend.StartCalls);
    }
    [Fact]
    public async Task RegionOnlyBundleRequestAndMetadataRetainOriginalRefAndEffectiveParameters()
    {
        using var response=await RegionPost(RegionBody());Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=Assert.Single(_engine._recs.Values);_tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(()=>rec.State==RecState.recording,TimeSpan.FromSeconds(3)));
        // Exercise the actual completed-recording projection without pretending synthetic PCM is a real MP4.
        var method=typeof(RecordingEngine).GetMethod("TryPrepareBundleGeneration",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var args=new object?[]{rec,new OutputMeta{DurationSeconds=20,Container="mp4",Width=640,Height=480,Fps=30},null};
        Assert.True((bool)method.Invoke(_engine,args)!);var request=Assert.IsType<RecordingBundleRequest>(args[2]);
        Assert.Equal(rec.FixedRegionProfileReference!.Value.ProfileDigest,request.ProfileDigest);Assert.Equal("region_only",request.ProfileReuseMode);
        Assert.Equal(20,request.RequestedDurationSeconds);Assert.Equal(3,request.CountdownSeconds);Assert.Equal("medium",request.VideoQuality);Assert.Equal(rec.OutputPath,request.MediaPath);
        var metadata=typeof(FfmpegRecordingBundleGenerator).GetMethod("BuildMetadata",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,new object[]{request,request.MediaPath,0L,"synthetic"});
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(metadata));var recording=json.RootElement.GetProperty("recording");
        Assert.Equal("region_only",recording.GetProperty("profile_reuse_mode").GetString());Assert.Equal(request.ProfileDigest,recording.GetProperty("profile_ref").GetProperty("digest").GetString());
    }
    [Fact]
    public async Task RegionOnlyCreationWaitReturnsActualRecordingAndFrozenProvenance()
    {
        var pending=_client.PostAsync("/api/v1/recordings/quick?wait_for=recording&wait_ms=2000",new StringContent(RegionBody(),Encoding.UTF8,"application/json"));
        Assert.True(SpinWait.SpinUntil(()=>_tray.Decision is not null,TimeSpan.FromSeconds(2)));Assert.Equal(0,_backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Approve());
        using var response=await pending.WaitAsync(TimeSpan.FromSeconds(3));var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,text);
        using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");
        Assert.Equal("recording",data.GetProperty("status").GetString());Assert.Equal("region_only",data.GetProperty("profile_reuse_mode").GetString());
        var rec=Assert.Single(_engine._recs.Values);Assert.Equal(rec.FixedRegionProfileReference!.Value.ProfileDigest,data.GetProperty("profile_ref").GetProperty("digest").GetString());
        Assert.Equal(1,_backend.StartCalls);Assert.Null(_tray.LastSelectionPurpose);
    }
    [Fact]
    public async Task RegionOnlyCountdownUsesOrdinaryIntegerSemantics()
    {
        using var response=await RegionPost(RegionBody().Replace("\"countdown_seconds\":3","\"countdown_seconds\":-0"));
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());Assert.Equal(0,Assert.Single(_engine._recs.Values).CountdownSeconds);
        _tray.Decision!(ConfirmationDecision.Reject());Assert.Equal(0,_backend.StartCalls);
    }
    [Fact]
    public async Task RegionOnlyConsecutiveRequestDoesNotReuseEarlierAudioOrCountdown()
    {
        RegionHelper();var body=JsonNode.Parse(RegionBody(new{system_audio=new{enabled=true,device_id="render-fixed"}},countdown:5))!.AsObject();
        using var first=await RegionPost(body.ToJsonString());Assert.True(first.IsSuccessStatusCode,await first.Content.ReadAsStringAsync());
        var old=Assert.Single(_engine._recs.Values);Assert.Equal("render-fixed",old.SystemAudioEndpointId);Assert.Equal(5,old.CountdownSeconds);
        _tray.Decision!(ConfirmationDecision.Reject());var calls=_regionAudio.Calls;
        body.Remove("audio");body.Remove("countdown_seconds");
        using var second=await RegionPost(body.ToJsonString());Assert.True(second.IsSuccessStatusCode,await second.Content.ReadAsStringAsync());
        var current=Assert.Single(_engine._recs.Values.Where(r=>!r.IsFinalized));
        Assert.Equal(old.FixedRegionProfileReference,current.FixedRegionProfileReference);Assert.Equal(3,current.CountdownSeconds);
        Assert.Equal(AudioCaptureSourceKind.None,current.AudioSourceKind);Assert.Null(current.SystemAudioEndpointId);Assert.False(current.Microphone);Assert.Equal(calls,_regionAudio.Calls);Assert.Equal(0,_backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Reject());
    }
}
