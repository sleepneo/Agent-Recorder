using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using static AgentRecorder.Tests.ObservationControllerTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationHealthGatewayTests : IDisposable
{
    private readonly string _root=Path.GetFullPath(Path.Combine(".local-data/temp/task316-health",Guid.NewGuid().ToString("N")));
    public ObservationHealthGatewayTests(){ Directory.CreateDirectory(_root);DataDirResolver.SetOverride(_root); }
    public void Dispose(){DataDirResolver.ClearOverride();Directory.Delete(_root,true);}
    internal static ObservationHealthOwner Owner()=>new(new(ApiServer.BuildVersion,"tray",Environment.ProcessId,ApiListenerState.NotStarted,null,true,true),
        null,Path.GetFullPath(".local-data/temp/task316-health"),new(true,true,"Ctrl+Shift+F10","always",true,0),
        new(true,false,"Ctrl+Shift+F11","while_recording",false,0),DateTimeOffset.UtcNow,"default","default","default");
    internal static InteractiveDesktopObservation Desktop(string failure="")=>new(Environment.ProcessId,1,"SID_SECRET",1,"SID_SECRET","WinSta0","Default",failure==""?"Default":"Winlogon",failure);
    internal static ObservationHealthProbes Probes()=>new(){Ffmpeg=()=>@"C:\Synthetic\ffmpeg.exe",Ffprobe=()=>@"C:\Synthetic\ffprobe.exe",FfmpegSource=()=>"project_tools",
        WgcHelper=()=>@"C:\Synthetic\wgc.exe",AudioHelper=()=>@"C:\Synthetic\audio.exe",FileExists=_=>true,Desktop=()=>Desktop()};
    internal static IReadOnlyList<ObservationHealthRow> Rows(ObservationHealthSnapshot sample)=>ObservationHealthFormatting.Rows(sample,new UiTextProvider(UiLanguage.EnUs));
    internal static HealthState State(ObservationHealthSnapshot sample,string id)=>Rows(sample).Single(row=>row.Id==id).State;

    [Fact]
    public void FormalAppCompositionSharesActualRuntimeFrozenRootToolsAndTrayOwner()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            var audit=new AuditFake();var engine=new RecordingEngine(audit);
            using var tray=new TrayContext(engine,audit,FakeGlobalStopHotkeyFactory.Create());engine.SetTray(tray);
            ((NotifyIcon)typeof(TrayContext).GetField("_icon",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!).Visible=false;
            using var readiness=new RuntimeReadiness("tray",0,Desktop());
            var server=new ApiServer(engine,audit,tray,readiness,null,null,null,null,null,null,null,null,0);
            try
            {
            var root=_root;var oldReady=Path.Combine(root,"runtime","ready.json");Directory.CreateDirectory(Path.GetDirectoryName(oldReady)!);
            File.WriteAllText(oldReady,"old instance API_KEY_SECRET NONCE_SECRET");
            var apiKey=Path.Combine(root,"api-key.txt");File.WriteAllText(apiKey,"API_KEY_SECRET");
            var database=Path.Combine(root,"never-open.db");File.WriteAllText(database,"DATABASE_SENTINEL");
            var probes=new ObservationHealthProbes { WgcHelper=()=>null,AudioHelper=()=>null,Desktop=()=>Desktop("DESKTOP_LOCKED") };
            var gateway=Program.CreateObservationHealthGateway(tray,server,readiness,root,probes);tray.SetObservationHealthGateway(gateway);
            var before=gateway.CaptureOwner();Assert.Equal(ApiServer.BuildVersion,before.Api!.Version);Assert.Equal(Environment.ProcessId,before.Api.Pid);
            Assert.Null(before.Api.ListeningPort);Assert.Equal(HealthState.NotStarted,State(ObservationHealthSnapshot.Partial(before,HealthProbeState.NotChecked),"api"));
            Assert.Null(before.Readiness!.PublishedReadyFile);Assert.True(before.Stop.Registered);Assert.False(before.Chapter.Registered);Assert.False(before.Chapter.Expected);
            server.Start();var listening=gateway.CaptureOwner();Assert.Equal(server.BoundPort,listening.Api!.ListeningPort);Assert.True(listening.Api.ListeningPort>0);
            Assert.Equal(HealthState.Listening,State(ObservationHealthSnapshot.Partial(listening,HealthProbeState.NotChecked),"api"));
            readiness.MarkReady();var ready=gateway.CaptureOwner();Assert.Equal(HealthState.Ready,State(ObservationHealthSnapshot.Partial(ready,HealthProbeState.NotChecked),"api"));
            var beforeFiles=Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(path=>path,File.ReadAllBytes);
            var other=Path.Combine(root,"other");Directory.CreateDirectory(other);DataDirResolver.SetOverride(other);
            try
            {
                var frozen=gateway.CaptureOwner();Assert.Equal(root,frozen.DataDirectory);Assert.Equal(oldReady,frozen.Readiness!.PublishedReadyFile);
                var sample=gateway.SampleAsync(frozen,CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(FfmpegLocator.FfmpegPath,sample.Ffmpeg.Path);Assert.Equal(FfmpegLocator.FfprobePath,sample.Ffprobe.Path);
                Assert.Equal(HealthState.Available,sample.Ffmpeg.State);Assert.Equal(HealthState.Available,sample.Ffprobe.State);
                Assert.Equal(HealthState.Unavailable,sample.Desktop.State);Assert.Equal(HealthState.Available,State(sample,"startup_desktop"));
                Assert.Equal(HealthState.Idle,State(sample,"chapter"));Assert.Equal(HealthState.NotChecked,State(sample,"wgc"));
                Assert.All(beforeFiles,pair=>Assert.Equal(pair.Value,File.ReadAllBytes(pair.Key)));Assert.Empty(engine._recs);
                Assert.Empty(Directory.GetFiles(other,"*",SearchOption.AllDirectories));
                var ownerError=Task.Run(()=>Record.Exception(()=>gateway.CaptureOwner())).GetAwaiter().GetResult();Assert.IsType<InvalidOperationException>(ownerError);
                using var form=tray.GetObservationCenter();Assert.False(form.Visible);Assert.Equal(3,form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single().TabCount);
                server.Stop();Assert.Equal(ApiListenerState.Stopped,gateway.CaptureOwner().Api!.ListenerState);Assert.Null(gateway.CaptureOwner().Api!.ListeningPort);
                readiness.Dispose();Assert.True(gateway.CaptureOwner().Readiness!.Disposed);
            }
            finally { DataDirResolver.SetOverride(root); }
            }
            finally {server.Stop();}
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualChapterRegistrationFailureDiffersFromIdleAndStopFailure(bool registered)
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            var audit=new AuditFake();var engine=new RecordingEngine(audit);
            using var tray=new TrayContext(engine,audit,FakeGlobalStopHotkeyFactory.Create(registered),indicatorPresenter:new NoIndicator(),chapterMarksHotkeyFactory:FakeGlobalStopHotkeyFactory.Create(registered));
            ((NotifyIcon)typeof(TrayContext).GetField("_icon",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!).Visible=false;
            var idle=tray.CaptureHealthHotkeys();Assert.False(idle.Chapter.Expected);Assert.False(idle.Chapter.Registered);
            tray.SetRecording(Presentation("fixture"));var active=tray.CaptureHealthHotkeys();Assert.True(active.Chapter.Expected);Assert.Equal(registered,active.Chapter.Registered);
            var owner=Owner() with {Stop=active.Stop,Chapter=active.Chapter};var sample=ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked);
            Assert.Equal(registered?HealthState.Available:HealthState.Unavailable,State(sample,"chapter"));Assert.Equal(registered?HealthState.Available:HealthState.Unavailable,State(sample,"stop"));
            Assert.Empty(engine._recs);
        });
    }

    [Theory]
    [InlineData("missing_ffmpeg")] [InlineData("missing_ffprobe")] [InlineData("error_ffprobe")] [InlineData("inaccessible")] [InlineData("both")]
    public async Task ToolResultsAreIndependentAndNeverExposeRawExceptions(string scenario)
    {
        var source=Probes();var probes=new ObservationHealthProbes { Ffmpeg=()=>scenario is "missing_ffmpeg" or "both"?throw new ApiException(500,"ENCODER_ERROR","API_KEY_SECRET"):source.Ffmpeg(),
            Ffprobe=()=>scenario switch {"missing_ffprobe" or "both"=>throw new FileNotFoundException("PROOF_SECRET"),"error_ffprobe"=>throw new IOException("NONCE_SECRET"),_=>source.Ffprobe()},
            FfmpegSource=source.FfmpegSource,WgcHelper=source.WgcHelper,AudioHelper=source.AudioHelper,Desktop=source.Desktop,FileExists=_=>scenario!="inaccessible"};
        var gateway=new ObservationHealthGateway(Owner,probes);var sample=await gateway.SampleAsync(Owner(),CancellationToken.None);
        Assert.Equal(scenario is "missing_ffmpeg" or "both" or "inaccessible"?HealthState.Unavailable:HealthState.Available,sample.Ffmpeg.State);
        Assert.Equal(scenario=="error_ffprobe"?HealthState.Unknown:scenario is "missing_ffprobe" or "both" or "inaccessible"?HealthState.Unavailable:HealthState.Available,sample.Ffprobe.State);
        if(scenario=="inaccessible") Assert.Equal("tool_not_found",sample.Ffmpeg.Reason);
        var summary=ObservationHealthFormatting.Summary(sample,new UiTextProvider(UiLanguage.EnUs),Rows(sample));Assert.DoesNotContain("SECRET",summary);
    }

    [Theory]
    [InlineData("DESKTOP_LOCKED","Unavailable")] [InlineData("INTERACTIVE_DESKTOP_PROBE_FAILED","Unknown")]
    [InlineData("INPUT_DESKTOP_UNAVAILABLE","Unavailable")] [InlineData("NONCE_SECRET","Unknown")]
    public async Task DynamicDesktopHasItsOwnTimestampAndSanitizesIdentityAndFailures(string failure,string state)
    {
        var source=Probes();var probes=new ObservationHealthProbes {Ffmpeg=source.Ffmpeg,Ffprobe=source.Ffprobe,WgcHelper=source.WgcHelper,AudioHelper=source.AudioHelper,FileExists=source.FileExists,Desktop=()=>Desktop(failure)};
        var gateway=new ObservationHealthGateway(Owner,probes);var sample=await gateway.SampleAsync(Owner(),CancellationToken.None);
        Assert.Equal(state,sample.Desktop.State.ToString());Assert.NotNull(sample.Desktop.SampledAt);Assert.DoesNotContain("SECRET",ObservationHealthFormatting.Summary(sample,new UiTextProvider(UiLanguage.EnUs),Rows(sample)));
    }

    [Fact]
    public async Task SharedGateRetainsUncancellableProbeAcrossCancellationUntilReleased()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var calls=0;var lateCalls=0;
        var probes=new ObservationHealthProbes {Ffmpeg=()=>{Interlocked.Increment(ref calls);entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));return null;},Ffprobe=()=>{Interlocked.Increment(ref lateCalls);return null;}};
        var gateway=new ObservationHealthGateway(Owner,probes);using var cancel=new CancellationTokenSource();var pending=gateway.SampleAsync(Owner(),cancel.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));cancel.Cancel();
        try { for(var i=0;i<20;i++) Assert.Equal(HealthProbeState.Busy,(await gateway.SampleAsync(Owner(),CancellationToken.None)).ProbeState);Assert.Equal(1,calls);Assert.False(pending.IsCompleted); }
        finally {release.Set();}
        Assert.Equal(HealthProbeState.Cancelled,(await pending).ProbeState);Assert.Equal(0,lateCalls);
        Assert.NotEqual(HealthProbeState.Busy,(await gateway.SampleAsync(Owner(),CancellationToken.None)).ProbeState);Assert.Equal(2,calls);
    }

    [Theory]
    [InlineData("same", "Ready")] [InlineData("other_pid", "Listening")] [InlineData("other_root", "Listening")]
    [InlineData("disposed", "Listening")] [InlineData("unknown", "Unknown")] [InlineData("stopped", "Stopped")]
    public void ReadinessRequiresSameLiveInstanceAndListenerInsteadOfPresetPortOrFile(string scenario,string expected)
    {
        var owner=Owner();var path=Path.Combine(owner.DataDirectory!,"runtime","ready.json");
        owner=owner with {Api=owner.Api! with {ListenerState=scenario=="stopped"?ApiListenerState.Stopped:ApiListenerState.Listening,ListeningPort=scenario=="stopped"?null:54321},
            Readiness=new("tray",scenario=="other_pid"?Environment.ProcessId+1:Environment.ProcessId,12345,
                scenario=="other_root"?Path.Combine(_root,"different","ready.json"):path,scenario=="disposed",true,DateTimeOffset.UtcNow)};
        if(scenario=="unknown")owner=owner with {Api=null};
        var snapshot=ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked);Assert.Equal(expected,State(snapshot,"api").ToString());
        Assert.DoesNotContain("12345",Rows(snapshot).Single(row=>row.Id=="api").Details);
    }

    [Fact]
    public void UnsupportedBackendRemainsDistinctFromUncheckedDeviceRecording()
    {
        var owner=Owner();owner=owner with{Api=owner.Api! with{MicrophoneBackendSupported=false,SystemAudioBackendSupported=false},Stop=new(false,false,null,"always",false,0)};
        var snapshot=ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked);
        Assert.Equal(HealthState.Unsupported,State(snapshot,"microphone"));Assert.Equal(HealthState.Unsupported,State(snapshot,"system_audio"));Assert.Equal(HealthState.Unsupported,State(snapshot,"stop"));
        Assert.Equal(HealthState.NotChecked,State(snapshot,"wgc"));
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)] [InlineData(UiLanguage.EnUs)]
    public void MissingDependenciesAndWhitelistMalformedSecretsRemainUnknownWithoutDump(UiLanguage language)
    {
        var owner=UnavailableObservationHealthGateway.Instance.CaptureOwner();var sample=ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked);
        Assert.Equal(HealthState.Unknown,State(sample,"api"));Assert.Equal(HealthState.Unknown,State(sample,"stop"));Assert.Equal(HealthState.Unknown,State(sample,"microphone"));
        var malicious=Owner() with {Api=new("API_KEY_SECRET","TOKEN_SECRET",123,ApiListenerState.NotStarted,null,true,true),
            Stop=new(true,true,"NONCE_SECRET","PROOF_SECRET",true,0),WindowPolicy="LEASE_SECRET"};
        sample=ObservationHealthSnapshot.Partial(malicious,HealthProbeState.Failed) with {Ffmpeg=new(HealthState.Unknown,"API_KEY_SECRET","NONCE_SECRET","PROOF_SECRET"),Desktop=new(HealthState.Unknown,"SID_SECRET")};
        var text=new UiTextProvider(language);var summary=ObservationHealthFormatting.Summary(sample,text,ObservationHealthFormatting.Rows(sample,text));
        Assert.DoesNotContain("SECRET",summary);Assert.DoesNotContain("Health_",summary);Assert.DoesNotContain("Observation_",summary);
        Assert.Contains(text.Get("Health_SupportedOnly"),summary);Assert.Contains(text.Get("Health_Experimental"),summary);
    }
    private sealed class AuditFake:AuditLogger {public override void Log(string evt,object payload) { }}
    private sealed class NoIndicator:IIndicatorPresenter {public void ShowFor(RecordingUiPresentation presentation,RecordingUiPresentation? parent,string? parentFallbackReason=null) { }}
}
