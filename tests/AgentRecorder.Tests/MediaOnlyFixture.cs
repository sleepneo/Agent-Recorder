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
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

// Synthetic capture input; actual App generator, FFmpeg publisher, history and
// deletion coordinator. No IssueForTests/Register-completed or null generator.
internal sealed class MediaOnlyFixture : IDisposable
{
    internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362","media-"+Guid.NewGuid().ToString("N")));
    internal string Videos=>Path.Combine(Root,"Videos");
    internal readonly SqliteOperationalStore Store;
    internal readonly SqliteOrdinaryRecordingHistoryStore Writer;
    internal readonly RecordingEngine Engine;
    internal readonly FfmpegRecordingBundleGenerator Generator=AgentRecorder.App.Program.CreateRecordingBundleGenerator();
    internal readonly RecordingTray Tray=new();
    internal readonly LocalRetentionApprovalFixture.Ui Ui=new();
    internal readonly Native NativeApi;
    internal readonly LocalRetentionApproval Authority;
    internal readonly RetentionDeletionCoordinator Coordinator;
    internal readonly ApiServer Server;
    internal readonly HttpClient Client;
    internal readonly ManualResetEventSlim GenerateRelease=new(true);
    internal int GeneratorCalls,BackendCalls;
    internal readonly TripMicrophone Devices=new();
    internal bool FailBundle;
    internal bool FailMedia;
    internal Action? BeforeVideoCleanup;
    private readonly List<FfmpegCaptureBackend> _backends=new();
    private readonly string? _mode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE"),_data=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private readonly string? _prior=DataDirResolver.HasOverride?DataDirResolver.Resolve():null;
    internal MediaOnlyFixture()
    {
        Directory.CreateDirectory(Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",Root);DataDirResolver.SetOverride(Root);
        SystemQuery.SetDisplayTopologyProvider(()=>new(){new("display_1","Synthetic",true,new(0,0,1920,1080),1,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
        Store=new(Path.Combine(Root,"history.db"));Store.Initialize();Writer=new(Store);
        Generator.BeforeMediaHashForTests=_=>{Interlocked.Increment(ref GeneratorCalls);Assert.True(GenerateRelease.Wait(TimeSpan.FromSeconds(12)));if(FailBundle)throw new IOException("owned generation fault");};
        Engine=new(new RetentionSharedOutputPathTests.QuietAudit(),bundleGenerator:Generator,microphoneProvider:Devices);Engine.ConfigureOrdinaryRecordingHistory(Writer);Engine.SetTray(Tray);
        Engine.BackendFactory=_=>
        {
            Interlocked.Increment(ref BackendCalls);
            var b=new FfmpegCaptureBackend{VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15"},BeforeVideoOnlyCleanupForTests=()=>BeforeVideoCleanup?.Invoke()};if(FailMedia)b.VideoOnlyProberForTests=new FailedFinalProbe();_backends.Add(b);return(b,"ffmpeg");
        };
        NativeApi=new(Videos);Authority=new(Engine,Store,Writer,UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,Videos,Ui,admission:new(),native:NativeApi);
        Coordinator=new(Store,UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,Videos,Authority,admission:new(),queryAdmission:new());
        ApiKeyAuth.InitializeForTesting(Root);Server=new(Engine,new RetentionSharedOutputPathTests.QuietAudit(),Tray,null,null,null,null,null,null,null,null,null,0,retentionDeletionGateway:new SqliteRetentionDeletionGateway(Coordinator));Server.Start();
        Client=new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    }
    internal JsonObject Request(int policy=0,bool initialOtherDirectory=false)=>new()
    {
        ["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject{["fps"]=15},["duration_seconds"]=1,["countdown_seconds"]=0,
        ["output"]=new JsonObject{["directory"]=initialOtherDirectory?Path.Combine(Root,"Before"):Videos,["filename"]=Guid.NewGuid().ToString("N")+".mp4",["conflict_policy"]="fail"},
        ["artifacts"]=policy==0?null:new JsonObject{["bundle"]=policy>0}
    };
    internal async Task<(System.Net.HttpStatusCode Status,JsonElement Json)> Post(string path,string body,string? key=null)
    {using var r=new HttpRequestMessage(HttpMethod.Post,path){Content=new StringContent(body,Encoding.UTF8,"application/json")};if(key is not null)r.Headers.Add("Idempotency-Key",key);using var reply=await Client.SendAsync(r);return(reply.StatusCode,JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.Clone());}
    internal async Task<Recording> Create(int policy,bool quick=false,bool changeDirectory=false)
    {
        var cfg=Request(policy,changeDirectory);if(policy==0)cfg.Remove("artifacts");
        if(quick){cfg.Remove("source");cfg["target"]=new JsonObject{["type"]="primary_display"};}
        var r=await Post(quick?"/api/v1/recordings/quick":"/api/v1/recordings",cfg.ToJsonString());Assert.InRange((int)r.Status,200,299);
        var rec=Engine._recs[r.Json.GetProperty("data").GetProperty("recording_id").GetString()!];Assert.Equal(RecState.pending_confirmation,rec.State);
        Assert.Equal(policy>=0,rec.GenerateBundle);Assert.Equal(rec.GenerateBundle,Tray.Presentation!.Summary.Artifacts!.Bundle);
        Assert.Equal(rec.GenerateBundle,r.Json.GetProperty("data").GetProperty("summary").GetProperty("artifacts").GetProperty("bundle").GetBoolean());return rec;
    }
    internal async Task Complete(Recording rec,bool manual=false,bool changed=false)
    {
        Tray.Confirm!(ConfirmationDecision.Approve(changed?Videos:null));
        if(manual){await Until(()=>rec.State==RecState.recording||rec.IsFinalized);Assert.False(rec.IsFinalized);await Task.Delay(180);Engine.Stop(rec.Id,"user");}
        await Until(()=>rec.IsFinalized);Assert.Equal(RecState.completed,rec.State);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        Writer.FlushBeforeUnifiedRead(TimeSpan.FromSeconds(3));Assert.Equal("completed",Writer.Read(rec.Id)!.Status);
    }
    internal async Task<string> DeleteRequest(Recording rec,string key="owned-media-request")
    {var r=await Post(DeletionRequestFixture.Endpoint,JsonSerializer.Serialize(new{recording_id=rec.Id}),key);Assert.Equal(System.Net.HttpStatusCode.Accepted,r.Status);return r.Json.GetProperty("data").GetProperty("deletion_request_id").GetString()!;}
    internal async Task<JsonElement> ReadRequest(string id)
    {using var r=await Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(System.Net.HttpStatusCode.OK,r.StatusCode);return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();}
    internal long Scalar(string sql)
    {using var c=Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return (long)cmd.ExecuteScalar()!;}
    internal LocalDeletionResult? DeletionResult()
    {
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var last=typeof(RetentionDeletionCoordinator).GetField("_last",flags)!.GetValue(Coordinator);
        return (last?.GetType().GetField("Operation",flags)?.GetValue(last) as LocalRetentionApproval.DeletionOperation)?.Current;
    }
    internal static async Task Until(Func<bool> condition)
    {var end=DateTime.UtcNow.AddSeconds(15);while(!condition()&&DateTime.UtcNow<end)await Task.Delay(10);Assert.True(condition(),"Owned pipeline did not settle.");}
    public void Dispose()
    {
        GenerateRelease.Set();Coordinator.Dispose();Ui.Retire.TrySetResult(true);Assert.True(Server.Stop(TimeSpan.FromSeconds(12)));Client.Dispose();Engine.Dispose();foreach(var b in _backends)b.Dispose();Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(5)));
        SqliteConnection.ClearAllPools();SystemQuery.SetDisplayTopologyProvider(null);ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_mode);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_data);if(_prior is null)DataDirResolver.ClearOverride();else DataDirResolver.SetOverride(_prior);GenerateRelease.Dispose();
    }
    internal sealed class TripMicrophone:IMicrophoneDeviceProvider
    { internal int Calls;public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken c=default){Calls++;throw new IOException("No real device enumeration allowed.");} }
    private sealed class FailedFinalProbe:IOutputProber
    {private int calls;public OutputMeta Probe(string path){var meta=FfmpegCaptureBackend.Probe(path);if(++calls==2)meta.DurationSeconds=0;return meta;}}
    internal sealed class RecordingTray:ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;internal RecordingConfirmationPresentation? Presentation;internal int Windows,Regions;
        public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>true;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c){Presentation=p;Confirm=c;Windows++;}
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c){Regions++;throw new IOException("No region UI allowed.");}
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    internal sealed class Native(string root):IOriginalFileHoldNative
    {
        private readonly IOriginalFileHoldNative _n=WindowsOriginalFileHoldNative.Instance;
        internal PublicationIdentityAnchor? Expected;internal Action? Before;internal int Calls;internal bool DeletePending;
        public bool Supported=>_n.Supported;public uint DriveType(string r)=>_n.DriveType(r);
        public SafeFileHandle Open(string p,uint a,uint s,uint d,uint f,out int e)=>_n.Open(p,a,s,d,f,out e);
        public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>_n.Basic(h,out v,out e);public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>_n.Standard(h,out v,out e);public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e)=>_n.Identity(h,out v,out e);public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>_n.FinalPath(h,b,c,out e);public OriginalFileCloseOutcome Close(SafeFileHandle h)=>_n.Close(h);
        public OriginalFileDispositionResult RequestDisposition(SafeFileHandle h)
        {
            Calls++;var expected=Assert.IsType<PublicationIdentityAnchor>(Expected);Assert.Equal(root,Path.GetDirectoryName(expected.Path));Assert.StartsWith(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362"))+Path.DirectorySeparatorChar,expected.Path,StringComparison.OrdinalIgnoreCase);
            var path=new StringBuilder(2048);Assert.True(_n.FinalPath(h,path,2048,out _)>0);Assert.Equal(@"\\?\"+expected.Path,path.ToString());Assert.True(_n.Identity(h,out var id,out _));Assert.Equal(expected.Identity,new ObservedFileIdentity(id.VolumeSerialNumber,id.IdLow,id.IdHigh));Before?.Invoke();
            var result=_n.RequestDisposition(h);if(result.Outcome==OriginalFileDispositionOutcome.Accepted){Assert.True(_n.Standard(h,out var s,out _));DeletePending=s.DeletePending!=0;Assert.True(DeletePending);}return result;
        }
    }
}
