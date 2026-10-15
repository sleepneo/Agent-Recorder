using System.Text.Json.Nodes;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryRuntimeOutputLifecycleTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task351/lifecycle",Guid.NewGuid().ToString("N")));
        internal readonly RecordingEngine Engine;
        internal readonly Tray Tray=new();internal readonly Backend Backend=new();
        private readonly string? _priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        private readonly string? _priorDir=DataDirResolver.HasOverride?DataDirResolver.Resolve():null;
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root,"Videos"));Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(Root);
            SystemQuery.SetDisplayProvider(()=>new(){new("display_1","Synthetic",true,new(0,0,1920,1080),1.0)});
            Engine=new(new RetentionSharedOutputPathTests.QuietAudit()){CountdownSteps=1,CountdownInterval=TimeSpan.FromMilliseconds(300)};
            Engine.SetTray(Tray);Engine.BackendFactory=_=>(Backend,"fake");
        }
        internal Recording Create()
        {
            Engine.CreateRecording(new JsonObject{
                ["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject{["fps"]=15},
                ["duration_seconds"]=60,["countdown_seconds"]=1,["output"]=new JsonObject{["directory"]=Path.Combine(Root,"Videos"),["filename"]="main.mp4",["conflict_policy"]="fail"}
            },"controlled-runtime-test",Tray);
            return Assert.Single(Engine._recs.Values);
        }
        public void Dispose()
        {
            Engine.Dispose();SystemQuery.SetDisplayProvider(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_priorMode);
            if(_priorDir is null)DataDirResolver.ClearOverride();else DataDirResolver.SetOverride(_priorDir);
        }
    }

    [Fact]
    public async Task FormalCreateConfirmationCountdownRecordingStopAndTerminalKeepRegisteredClaim()
    {
        using var f=new Fixture();var rec=f.Create();var path=rec.OutputPath;
        Assert.Equal(RecState.pending_confirmation,rec.State);Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);Assert.Equal(0,f.Backend.Starts);
        f.Tray.Confirm!(ConfirmationDecision.Approve());Assert.Equal(RecState.countdown,rec.State);Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);
        await Until(()=>rec.State==RecState.recording);Assert.Equal(1,f.Backend.Starts);Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Backend.BeforeStop=()=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var stop=Task.Run(()=>f.Engine.Stop(rec.Id,"user_requested"));
        try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));Assert.Contains(rec.State,new[]{RecState.stopping,RecState.finalizing});Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);}
        finally{release.Set();await stop.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.True(rec.IsFinalized);Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);
    }

    [Fact]
    public async Task RealDirectoryApprovalReplacementCompetesWithReadAtTheExistingPathLock()
    {
        using var f=new Fixture();var rec=f.Create();var old=rec.OutputPath;var dir=Path.Combine(f.Root,"Videos","new");var next=Path.Combine(dir,Path.GetFileName(old));
        using var db=new UnifiedHistoryTestDatabase(f.Root);
        foreach(var row in new[]{("old_history",old),("new_history",next)})
            db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(db.Recording(row.Item1,completed:true) with{OutputPath=row.Item2,BundleRegistration=new(null)}));
        var files=new RetentionPreviewQueryTests.MetadataFake();
        var service=new RetentionPreviewQueryService(db.Query.GetOrdinaryEvidencePage,f.Root,files,files,()=>At.AddDays(2),f.Engine);
        var before=db.Snapshot();var initial=service.GetPage(Sid,Session,20,At.AddHours(1));
        Assert.Equal(old,initial.Items.Single(i=>i.ReasonCode=="runtime_output_in_use").RecordedPath);files.Observations.Clear();
        var frozen=Read(f.Engine,old,next)!;using var barrier=new Barrier(2);var observations=0;
        var approve=Task.Run(()=>{Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));f.Tray.Confirm!(ConfirmationDecision.Approve(dir));});
        var reader=Task.Run(()=>{Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));for(var i=0;i<20;i++){
            var page=service.GetPage(Sid,Session,20,At.AddHours(1));Assert.Single(page.Items,i=>i.ReasonCode=="runtime_output_in_use");
            Assert.Single(page.Items,i=>i.ReasonCode=="age_rule_match");Assert.Equal(1,page.UnknownCandidateFileCount);observations++;
        }});
        await Task.WhenAll(approve,reader).WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(20,observations);Assert.Equal(20,files.Observations.Count);
        Assert.Equal(next,rec.OutputPath);var after=Read(f.Engine,old,next)!;Assert.False(after.Paths[0].InUse);Assert.True(after.Paths[1].InUse);
        Assert.True(frozen.Paths[0].InUse);Assert.False(frozen.Paths[1].InUse);
        Assert.Equal(next,service.GetPage(Sid,Session,20,At.AddHours(1)).Items.Single(i=>i.ReasonCode=="runtime_output_in_use").RecordedPath);
        Assert.Equal(before,db.Snapshot());
        f.Engine.Stop(rec.Id,"user_requested");await Until(()=>f.Engine.ActiveCountdownOperationCountForTests==0);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task QuickSelectionPreservesExistingClaimsWithoutInventingAnUnregisteredOutput(bool existingClaim)
    {
        using var f=new Fixture();var directory=Path.Combine(f.Root,"Videos");var path=Path.Combine(directory,"main.mp4");
        if(existingClaim)Claim(f.Engine,new Recording{IsOrdinaryLocalRequest=true},path);
        ApiKeyAuth.InitializeForTesting(f.Root);
        var server=new ApiServer(f.Engine,new RetentionSharedOutputPathTests.QuietAudit(),f.Tray,null,null,null,null,null,null,null,null,null,0);
        server.Start();using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        Task<HttpResponseMessage>? selecting=null;
        try
        {
            selecting=client.PostAsync("/api/v1/recordings/quick",new StringContent(JsonSerializer.Serialize(new{
                target=new{type="selected_region"},duration_seconds=60,countdown_seconds=0,output=new{directory,filename="main.mp4"}
            }),Encoding.UTF8,"application/json"));
            var first=await Task.WhenAny(f.Tray.SelectionRequested.Task,selecting).WaitAsync(TimeSpan.FromSeconds(5));
            if(first==selecting)Assert.Fail("Selection was not requested: "+await (await selecting).Content.ReadAsStringAsync());
            await f.Tray.SelectionRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(f.Engine._recs);Assert.Equal(existingClaim,Read(f.Engine,path)!.Paths.Single().InUse);Assert.Equal(0,f.Backend.Starts);
            f.Tray.Selection!("selection_cancelled",0,0,0,0,"display_1","virtual_screen");
            using var response=await selecting.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("data").GetProperty("quick").GetProperty("recording_created").GetBoolean());
            Assert.Empty(f.Engine._recs);Assert.Equal(existingClaim,Read(f.Engine,path)!.Paths.Single().InUse);
        }
        finally
        {
            f.Tray.Selection?.Invoke("selection_cancelled",0,0,0,0,"display_1","virtual_screen");
            if(selecting is {IsCompleted:false}){using var response=await selecting.WaitAsync(TimeSpan.FromSeconds(5));}
            server.Stop();ApiKeyAuth.ResetForTesting(null);
        }
    }

    private static async Task Until(Func<bool> predicate)
    {var deadline=DateTime.UtcNow.AddSeconds(5);while(!predicate()&&DateTime.UtcNow<deadline)await Task.Delay(10);Assert.True(predicate());}

    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        internal Action<string,int,int,int,int,string,string>? Selection;
        internal readonly TaskCompletionSource SelectionRequested=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>true;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c){Selection=c;SelectionRequested.TrySetResult();}
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string t){}
    }
    private sealed class Backend : ICaptureBackend, IDeferredCaptureStartBackend, IFirstFrameObservableCaptureBackend
    {
        internal int Starts;internal Action? BeforeStop;
        public void Start(CaptureConfig c,CaptureAuthorizationProof p){Assert.NotNull(p);Starts++;}
        private bool _authorized;
        public bool IsAwaitingCaptureStart=>Starts>0&&!_authorized;
        public event Action<bool>? CaptureAuthorizationCompleted;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void StartCapture(CaptureAuthorizationProof proof)
        {
            Assert.NotNull(proof);_authorized=true;CaptureAuthorizationCompleted?.Invoke(true);
            FirstFrameObserved?.Invoke(new(){FrameNumber=1,OutTimeUs=0});
        }
        public OutputMeta Stop(){BeforeStop?.Invoke();return new(){SizeBytes=0,OutputFileExists=false,StopReason="synthetic_only"};}
        public void OnNaturalExit(Action<int,OutputMeta> c){}public int ExitCode=>0;public void Dispose(){}
    }
}
