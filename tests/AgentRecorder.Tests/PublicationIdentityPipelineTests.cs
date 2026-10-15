using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PublicationIdentityPipelineTests
{
    private sealed class Runner : IExternalProcessRunner
    {
        internal bool Fail;
        public Task<ExternalProcessResult> RunAsync(string executable,IReadOnlyList<string> args,TimeSpan timeout,bool captureStderr=true,Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();using(var file=new FileStream(args[^1],FileMode.Create,FileAccess.Write))file.SetLength(1024);
            return Task.FromResult(new ExternalProcessResult(Fail?1:0,false,""));
        }
    }
    private sealed class Prober : IOutputProber
    {
        internal PublicationReceipt? Contaminated;
        internal bool NoAudio;
        public OutputMeta Probe(string path)=>new(){SizeBytes=1024,DurationSeconds=2,Width=1920,Height=1080,Fps=15,Container="mp4",Codec="h264",OutputFileExists=true,HasAudioStream=!NoAudio,PublicationReceipt=Contaminated,
            ProbeStreams=NoAudio?new[]{new ProbeStreamInfo{Index=0,CodecType="video",CodecName="h264",StartTimeSeconds=0,DurationSeconds=2}}:new[]{new ProbeStreamInfo{Index=0,CodecType="video",CodecName="h264",StartTimeSeconds=0,DurationSeconds=2},new ProbeStreamInfo{Index=1,CodecType="audio",CodecName="aac",StartTimeSeconds=0,DurationSeconds=2}}};
    }
    internal static async Task<AvFinalizer.Result> Mux(string root,string output,bool noAudio=false,bool fail=false)
    {
        var prober=new Prober{NoAudio=noAudio,Contaminated=PublicationReceipt.IssueForTests(Path.Combine(root,"intermediate.mp4"),12,new(1,2,3))};
        var video=Path.Combine(root,"video.mp4");File.WriteAllBytes(video,new byte[1024]);File.WriteAllBytes(Path.Combine(root,"audio.wav"),new byte[12]);
        return await new AvFinalizer(new Runner{Fail=fail},TimeSpan.FromSeconds(5),StagingToFinalPublisher.Instance,prober)
            .FinalizeAsync(video,Path.Combine(root,"audio.wav"),output,TimeSpan.FromMilliseconds(1),noAudio?AudioCaptureSourceKind.None:AudioCaptureSourceKind.SystemLoopback,false);
    }
    [Theory]
    [InlineData("mux")][InlineData("no_audio")][InlineData("failed")]
    public async Task AvFinalizerSignsOnlySuccessfulMuxNeverInputVideoNoAudioCopyOrFailure(string branch)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var output=Path.Combine(f.Videos,"final.mp4");var result=await Mux(f.Root,output,branch=="no_audio",branch=="failed");
        if(branch!="failed")
        {
            Assert.True(result.ExitCode==0,result.Error+" "+string.Join(";",result.Meta.Warnings));Assert.NotNull(result.Meta.PublicationReceipt);Assert.Equal(output,result.Meta.PublicationReceipt!.Anchor.Path);
            Assert.NotEqual(new ObservedFileIdentity(1,2,3),result.Meta.PublicationReceipt.Anchor.Identity);
            Assert.Equal(WindowsReadOnlyFileMetadataProbe.Instance.Observe(output).Before!.Identity,result.Meta.PublicationReceipt.Anchor.Identity);
        }
        else Assert.Null(result.Meta.PublicationReceipt);
        if(branch=="failed"){Assert.NotEqual(0,result.ExitCode);Assert.False(File.Exists(output));}
        AvMuxOwnershipTests.AssertNoOwnedMux(output);
        Assert.False(File.Exists(output+".muxing.partial.mp4"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LaterPathLengthDoesNotOverwriteCommittedReceiptBinding(bool splitNormalization)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var output=Path.Combine(f.Videos,"final.mp4");var result=await Mux(f.Root,output);
        Assert.NotNull(result.Meta.PublicationReceipt);using(var changed=new FileStream(output,FileMode.Open,FileAccess.Write))changed.SetLength(2048);
        if(splitNormalization)
        {
            using var backend=new AvSplitCaptureBackend();typeof(AvSplitCaptureBackend).GetField("_finalOutputPath",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(backend,output);
            typeof(AvSplitCaptureBackend).GetMethod("NormalizeTerminalOutputMeta",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(backend,new object[]{result.Meta,true});
        }
        else Assert.True((bool)typeof(AvFinalizer).GetMethod("TrySetPublishedOutputMetadata",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{result.Meta,output})!);
        Assert.True(result.Meta.OutputFileExists);Assert.Equal(1024,result.Meta.SizeBytes);Assert.Equal(result.Meta.PublicationReceipt!.Anchor.Length,result.Meta.SizeBytes);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void WgcProductionPublishBranchPropagatesFinalReceiptAndClearsIntermediateVideo(bool intermediate)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var staging=f.Create("staging.mp4",1024);var final=Path.Combine(f.Videos,"final.mp4");
        using var backend=new WgcContinuousCaptureBackend(_=>throw new InvalidOperationException("No screen capture/session"),StagingToFinalPublisher.Instance,
            _=>new OutputMeta{Container="mp4",Codec="h264",Width=1920,Height=1080,Fps=30,DurationSeconds=5,SizeBytes=1024,PublicationReceipt=PublicationReceipt.IssueForTests(staging,1024,new(1,2,3))},()=>"unused",f.Root);
        var result=new WgcContinuousSessionResult{State=WgcContinuousManagedSessionState.Success,ExitCode=0,Summary=new(){State=ContinuousSessionState.Success,Width=1920,Height=1080,DurationMs=5000,HasFileSize=true,FileSize=1024,CaptureMethod="WGC_D3D11_FRAME_STREAM",EncoderMode="software",EncoderSelectionReason="software_default"}};
        var cfg=new CaptureConfig{SourceKind="display",OutputPath=final,StorageIntermediatePublication=intermediate};
        var method=typeof(WgcContinuousCaptureBackend).GetMethod("TryPublishAndProbe",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var meta=Assert.IsType<OutputMeta>(method.Invoke(backend,new object[]{result,cfg,staging}));Assert.True(meta.OutputFileExists,meta.StopReason+" "+meta.StderrLog+" "+string.Join(";",meta.Warnings));Assert.Equal(final,meta.OutputPath);
        if(intermediate)Assert.Null(meta.PublicationReceipt);else{Assert.NotNull(meta.PublicationReceipt);Assert.Equal(WindowsReadOnlyFileMetadataProbe.Instance.Observe(final).Before!.Identity,meta.PublicationReceipt!.Anchor.Identity);}
    }
    private sealed class Backend(string root) : ICaptureBackend
    {
        internal TaskCompletionSource<bool> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CaptureConfig? Config;
        public void Start(CaptureConfig cfg,CaptureAuthorizationProof proof){Config=cfg;Started.TrySetResult(true);}
        public OutputMeta Stop()=>Mux(root,Config!.OutputPath).GetAwaiter().GetResult().Meta;
        public void OnNaturalExit(Action<int,OutputMeta> callback){}
        public int ExitCode=>0;
        public void Dispose(){}
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback)=>Confirm=callback;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No selection UI");
        public void SetRecording(RecordingUiPresentation r){}public void SetIdle(RecordingUiPresentation r){}public void SetAllIdle(){}public void ShowError(string text){}
    }
    [Fact]
    public async Task FormalPublisherFinalizerCoreTerminalCallbackWriterAndDefaultPreviewAreConnected()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(f.Root);
        SystemQuery.SetDisplayProvider(()=>new(){new("display_1","Synthetic display",true,new SystemQuery.Bounds(0,0,1920,1080),1.0)});
        var store=new SqliteOperationalStore(Path.Combine(f.Root,"core.db"));store.Initialize();using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var tray=new Tray();var backend=new Backend(f.Root);
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());engine.SetTray(tray);engine.ConfigureOrdinaryRecordingHistory(writer);engine.BackendFactory=_=>(backend,"fake");
        try
        {
            var request=new JsonObject{["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject{["fps"]=15},["duration_seconds"]=10,["countdown_seconds"]=0,["output"]=new JsonObject{["directory"]=f.Videos,["filename"]="core.mp4",["conflict_policy"]="rename"}};
            engine.CreateRecording(request,"task325-synthetic",tray);Assert.NotNull(tray.Confirm);tray.Confirm!(ConfirmationDecision.Approve());await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rec=Assert.Single(engine._recs.Values);Assert.True(SpinWait.SpinUntil(()=>rec.State==RecState.recording,TimeSpan.FromSeconds(5)));engine.Stop(rec.Id,"user");Assert.True(rec.State==RecState.completed,rec.State+" "+rec.Error+" "+rec.StopReason+" "+rec.LastMeta?.StderrLog+" "+string.Join(";",rec.LastMeta?.Warnings??Array.Empty<string>()));Assert.NotNull(rec.LastMeta!.PublicationReceipt);
            var row=writer.Read(rec.Id)!;Assert.Equal("completed",row.Status);Assert.Null(row.PublicationReceipt);using(var c=store.OpenReadOnlyConnection())using(var command=c.CreateCommand()){command.CommandText="SELECT COUNT(*) FROM ordinary_publication_anchors;";Assert.Equal(1L,command.ExecuteScalar());}
            var query=new UnifiedHistoryQueryService(store,writer);var service=new RetentionPreviewQueryService(query,f.Root, TestRuntimeOutputUse.None);var before=DateTimeOffset.UtcNow;
            Assert.Equal("age_rule_match",Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,20,before).Items).ReasonCode);
            var path=row.OutputPath;var lastWrite=File.GetLastWriteTimeUtc(path);File.Move(path,Path.Combine(f.Videos,"held-original.mp4"));using(var copy=new FileStream(path,FileMode.CreateNew,FileAccess.Write))copy.SetLength(row.OutputSizeBytes!.Value);File.SetLastWriteTimeUtc(path,lastWrite);
            Assert.Equal("publication_identity_mismatch",Assert.Single(service.GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,20,before).Items).ReasonCode);
        }
        finally{engine.Dispose();writer.Dispose();SqliteConnection.ClearAllPools();SystemQuery.SetDisplayProvider(null);DataDirResolver.ClearOverride();Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",prior);}
    }
}
