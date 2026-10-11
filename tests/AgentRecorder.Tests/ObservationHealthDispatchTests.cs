using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.ObservationHealthGatewayTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationHealthDispatchTests
{
    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void QueuedOwnerTimeoutOrCancellationRetainsOnePostUntilDrainedAndRetryWorks(bool cancel)
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            using var control=new Control();_=control.Handle;var callbacks=new ConcurrentQueue<Action>();var posted=Gate();var deadline=Gate();var captures=0;var probes=0;var ownerThread=Environment.CurrentManagedThreadId;
            var dispatcher=new ObservationHealthOwnerDispatcher(control,()=>{Assert.Equal(ownerThread,Environment.CurrentManagedThreadId);captures++;return Owner();}){PostForTests=callback=>{callbacks.Enqueue(callback);posted.TrySetResult();}};
            var source=Probes();var native=new ObservationHealthProbes{Ffmpeg=()=>{Interlocked.Increment(ref probes);return source.Ffmpeg();},Ffprobe=source.Ffprobe,FileExists=source.FileExists,Desktop=source.Desktop};
            var gateway=new ObservationHealthGateway(Owner,native,dispatcher.PostAsync,Owner){DeadlineForTests=_=>deadline.Task};using var cancellation=new CancellationTokenSource();
            var pending=Task.Run(()=>gateway.ReadAsync(cancellation.Token));Assert.True(posted.Task.Wait(TimeSpan.FromSeconds(5)));
            if(cancel)cancellation.Cancel();deadline.TrySetResult();Assert.Equal(cancel?HealthProbeState.Failed:HealthProbeState.Timeout,pending.GetAwaiter().GetResult().ProbeState);
            for(var i=0;i<12;i++)Assert.Equal(HealthProbeState.Busy,gateway.ReadAsync(default).GetAwaiter().GetResult().ProbeState);
            Assert.Equal(HealthProbeState.Busy,gateway.SampleAsync(Owner(),default).GetAwaiter().GetResult().ProbeState);Assert.Single(callbacks);Assert.Equal(0,captures);Assert.Equal(0,probes);
            Assert.True(callbacks.TryDequeue(out var callback));callback();Assert.Equal(0,captures);Assert.Equal(0,probes);
            gateway.DeadlineForTests=token=>Task.Delay(TimeSpan.FromSeconds(3),token);
            var watch=Stopwatch.StartNew();ObservationHealthSnapshot retry;
            do{retry=gateway.ReadAsync(default).GetAwaiter().GetResult();Thread.Yield();}while(retry.ProbeState==HealthProbeState.Busy && watch.Elapsed<TimeSpan.FromSeconds(5));
            Assert.Equal(HealthProbeState.Complete,retry.ProbeState);Assert.Equal(1,captures);Assert.Equal(1,probes);
        });
    }
    [Fact]
    public void DestroyedOwnerDiscardsLateCallbackAndReleasesQuotaWithoutWaitingForQueue()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            var control=new Control();_=control.Handle;Action? callback=null;var posted=Gate();var captures=0;
            var dispatcher=new ObservationHealthOwnerDispatcher(control,()=>{captures++;return Owner();}){PostForTests=c=>{callback=c;posted.TrySetResult();}};
            var gateway=new ObservationHealthGateway(Owner,Probes(),dispatcher.PostAsync,Owner);var pending=Task.Run(()=>gateway.ReadAsync(default));Assert.True(posted.Task.Wait(TimeSpan.FromSeconds(5)));
            control.Dispose();Assert.Equal(HealthProbeState.Failed,pending.GetAwaiter().GetResult().ProbeState);callback!();Assert.Equal(0,captures);
            Assert.Equal(HealthProbeState.Failed,gateway.ReadAsync(default).GetAwaiter().GetResult().ProbeState);
        });
    }
    [Fact]
    public void PostingFailureAndWrongThreadCallbackAreNormalizedAndDoNotCapture()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            using var control=new Control();_=control.Handle;var captures=0;var dispatcher=new ObservationHealthOwnerDispatcher(control,()=>{captures++;return Owner();});
            var gateway=new ObservationHealthGateway(Owner,Probes(),dispatcher.PostAsync,Owner);
            dispatcher.PostForTests=_=>throw new Exception("SECRET_POST");Assert.Equal(HealthProbeState.Failed,Task.Run(()=>gateway.ReadAsync(default)).GetAwaiter().GetResult().ProbeState);
            dispatcher.PostForTests=callback=>callback();Assert.Equal(HealthProbeState.Failed,Task.Run(()=>gateway.ReadAsync(default)).GetAwaiter().GetResult().ProbeState);Assert.Equal(0,captures);
        });
    }
    [Fact]
    public void OneDeadlineSpansOwnerThenNativeAndKeepsQuotaAfterNativeTimeout()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            using var control=new Control();_=control.Handle;Action? callback=null;var posted=Gate();var deadline=Gate();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var count=0;var deadlineCalls=0;var source=Probes();
            var dispatcher=new ObservationHealthOwnerDispatcher(control,Owner){PostForTests=c=>{callback=c;posted.TrySetResult();}};
            var probes=new ObservationHealthProbes{Ffmpeg=()=>{Interlocked.Increment(ref count);entered.Set();release.Wait();return source.Ffmpeg();},Ffprobe=source.Ffprobe,FileExists=source.FileExists,Desktop=source.Desktop};
            var gateway=new ObservationHealthGateway(Owner,probes,dispatcher.PostAsync,Owner){DeadlineForTests=_=>{Interlocked.Increment(ref deadlineCalls);return deadline.Task;}};
            var pending=Task.Run(()=>gateway.ReadAsync(default));Assert.True(posted.Task.Wait(TimeSpan.FromSeconds(5)));Assert.False(pending.IsCompleted);callback!();Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                deadline.TrySetResult();Assert.Equal(HealthProbeState.Timeout,pending.GetAwaiter().GetResult().ProbeState);Assert.Equal(1,deadlineCalls);
                for(var i=0;i<8;i++)Assert.Equal(HealthProbeState.Busy,gateway.ReadAsync(default).GetAwaiter().GetResult().ProbeState);Assert.Equal(1,count);
            }finally{release.Set();}
        });
    }
    [Theory]
    [InlineData("Complete","complete")][InlineData("Busy","busy")][InlineData("Timeout","timeout")]
    [InlineData("Failed","failed")][InlineData("Cancelled","failed")][InlineData("NotChecked","not_checked")]
    public void StableTopLevelCodes(string probe,string code)
    {Assert.Equal(code,ObservationHealthResponse.From(ObservationHealthSnapshot.Partial(Owner(),Enum.Parse<HealthProbeState>(probe))).SampleStatus);}
    [Theory]
    [InlineData(false,false,"idle")][InlineData(true,false,"registration_failed")][InlineData(true,true,"registered")]
    public void ChapterIdleAndFailureHaveDistinctMachineCodes(bool expected,bool registered,string reason)
    {
        var owner=Owner();owner=owner with{Chapter=owner.Chapter with{Expected=expected,Registered=registered}};
        Assert.Equal(reason,ObservationHealthResponse.From(ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked)).Hotkeys.Chapter.ReasonCode);
    }
    [Fact]
    public void ProjectionWhitelistsMaliciousCodesMetadataEnumsAndGestureAndKeepsEvidenceSeparated()
    {
        var owner=Owner() with{Api=new("SECRET_VERSION","SECRET_MODE",7,ApiListenerState.Listening,49123,true,true),DataDirectory="relative_SECRET",
            Stop=new(true,false,"SECRET_TOKEN","SECRET_POLICY",true,0),WindowPolicy="SECRET_BACKEND"};
        var badTool=new HealthTool((HealthState)999,"bad\0SECRET_PATH","SECRET_SOURCE","SECRET_REASON");
        var sample=new ObservationHealthSnapshot(owner,badTool,badTool,badTool,badTool,new((HealthState)999,"SECRET_SID"),(HealthProbeState)999);
        var dto=ObservationHealthResponse.From(sample);var text=JsonSerializer.Serialize(dto,ApiResponse.Json);Assert.DoesNotContain("SECRET",text);Assert.Equal("failed",dto.SampleStatus);
        Assert.Null(dto.Instance.DataDirectory);Assert.Equal("unknown",dto.Instance.Version);Assert.Equal("unknown",dto.Instance.Mode);Assert.Equal("not_checked",dto.Hotkeys.Stop.ReasonCode);Assert.Null(dto.Hotkeys.Stop.Gesture);
        Assert.Equal("dynamic_read_only_probe",dto.Desktop.Current.EvidenceType);Assert.Equal("static_startup_receipt",dto.Desktop.Startup.EvidenceType);
        Assert.Equal("unknown",dto.Backends.Window.Policy);Assert.Equal("not_checked",dto.Backends.Window.CaptureCheck);
    }
    [Fact]
    public void OldReadyFileAndWrongInstanceDisposedReceiptsNeverBecomeReady()
    {
        var owner=Owner();var directory=owner.DataDirectory!;var api=owner.Api! with{ListenerState=ApiListenerState.Listening,ListeningPort=49123};
        var receipt=new RuntimeReadinessObservation("tray",api.Pid,37891,Path.Combine(directory,"runtime","ready.json"),false,true,DateTimeOffset.UtcNow);
        owner=owner with{Api=api};Assert.Equal("listening",ObservationHealthResponse.From(ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked)).Instance.ReadyState);
        foreach(var stale in new[]{receipt with{Pid=api.Pid+1},receipt with{Mode="headless"},receipt with{Disposed=true},receipt with{PublishedReadyFile=Path.Combine(directory,"other","ready.json")}})
            Assert.Equal("listening",ObservationHealthResponse.From(ObservationHealthSnapshot.Partial(owner with{Readiness=stale},HealthProbeState.NotChecked)).Instance.ReadyState);
        Assert.Equal("ready",ObservationHealthResponse.From(ObservationHealthSnapshot.Partial(owner with{Readiness=receipt},HealthProbeState.NotChecked)).Instance.ReadyState);
    }
}
