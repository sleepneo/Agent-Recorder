using System.Diagnostics;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed class OrdinaryRuntimeOutputSnapshotTests
{
    private static Recording Owner(string path, string backend = "ffmpeg") => new()
    {
        IsOrdinaryLocalRequest = true, OutputPath = path, BackendType = backend,
        Config = new() { Mode = "video", SourceKind = "display", OutputPath = path }
    };

    [Theory]
    [InlineData("ffmpeg", "none")][InlineData("ffmpeg", "microphone")]
    [InlineData("ffmpeg-av-split", "system_audio")][InlineData("ffmpeg-region-av-split", "microphone")]
    [InlineData("wgc-continuous", "none")][InlineData("wgc-window-av-split", "system_audio")]
    public void ExistingOrdinaryClaimModesMatchOnlyExactCanonicalMainOutput(string backend, string audio)
    {
        using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var path = @"C:\sample\Videos\main.mp4"; var rec = Owner(path, backend);
        rec.Config.AudioSourceKind = audio == "none" ? AudioCaptureSourceKind.None : audio == "microphone" ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.SystemLoopback;
        if(backend.StartsWith("wgc"))rec.Config.SourceKind="window";
        Claim(engine, rec, path);
        var snapshot = Read(engine, path.ToUpperInvariant(), @"C:\sample\Videos\other.mp4", path+".temp", @"C:\sample\Videos-old\main.mp4")!;
        Assert.True(snapshot.Paths[0].InUse); Assert.All(snapshot.Paths.Skip(1), p => Assert.False(p.InUse));
        Assert.Equal(1,OwnerCount(engine));Assert.Empty(engine._recs); // No history scan fallback.
        rec.OutputPath=@"C:\mutated-recording.mp4"; Assert.True(Read(engine,path)!.Paths[0].InUse);
        Assert.False(Read(engine,rec.OutputPath)!.Paths[0].InUse); // The registered immutable text wins.
    }

    [Theory]
    [InlineData(RecState.created)][InlineData(RecState.pending_confirmation)][InlineData(RecState.preparing)]
    [InlineData(RecState.countdown)][InlineData(RecState.recording)][InlineData(RecState.paused)][InlineData(RecState.stopping)]
    [InlineData(RecState.finalizing)][InlineData(RecState.completed)][InlineData(RecState.cancelled)]
    [InlineData(RecState.failed)][InlineData(RecState.rejected)][InlineData(RecState.expired)]
    public void ReadIncludesEveryRegisteredStateAndNeverRetiresOrTouchesBackend(RecState state)
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var path=@"C:\sample\Videos\retained.mp4";var rec=Owner(path);Claim(engine,rec,path);
        var backend=new PoisonBackend();rec.Backend=backend;rec.BackendStartAttempted=true;rec.State=state;
        if(state is RecState.completed or RecState.cancelled or RecState.failed or RecState.rejected or RecState.expired)rec.PublishFinalized();
        for(var i=0;i<3;i++)Assert.True(Read(engine,path)!.Paths[0].InUse);
        Assert.Equal(1,OwnerCount(engine));Assert.Equal(0,backend.Calls);Assert.Equal(state,rec.State);
    }

    [Theory][InlineData(500)][InlineData(501)]
    public void ClaimLimitCountsOwnersRatherThanTruncatingOrDeduplicatingTheirPaths(int count)
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var path=@"C:\sample\Videos\same.mp4";
        for(var i=0;i<count;i++)Claim(engine,Owner(path),path);
        Assert.Equal(count,OwnerCount(engine));var snapshot=Read(engine,path);
        if(count==500)Assert.True(snapshot!.Paths.Single().InUse);else Assert.Null(snapshot);
        Assert.Equal(count,OwnerCount(engine));
    }

    [Fact]
    public async Task LockedRegistrationReturnsUnavailableAtFiftyMillisecondBudgetWithoutBlockingOtherLocks()
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var holder=Task.Run(()=>{lock(OutputLock(engine)){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));}});
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));var clock=Stopwatch.StartNew();
            Assert.Null(Read(engine,@"C:\sample\Videos\a.mp4"));clock.Stop();
            Assert.InRange(clock.ElapsedMilliseconds,30,1500);Assert.Equal(50,OrdinaryRuntimeOutputSnapshot.LockTimeoutMilliseconds);
        }
        finally{release.Set();await holder.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.False(Read(engine,@"C:\sample\Videos\a.mp4")!.Paths.Single().InUse);
    }

    [Fact]
    public async Task SnapshotReadsDoNotAcquireEngineRecordingOrBackendLocks()
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var path=@"C:\sample\Videos\a.mp4";var rec=Owner(path);Claim(engine,rec,path);
        var engineLock=typeof(RecordingEngine).GetField("_lock",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var holder=Task.Run(()=>{lock(engineLock)lock(rec){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));}});
        try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));Assert.True(Read(engine,path)!.Paths.Single().InUse);}
        finally{release.Set();await holder.WaitAsync(TimeSpan.FromSeconds(5));}
    }

    [Fact]
    public async Task AtomicReplacementAndRegistrationReturnCompleteImmutableSnapshotsWithoutDeadlock()
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var old=@"C:\sample\Videos\old.mp4";var next=@"C:\sample\Videos\new.mp4";var extra=@"C:\sample\Videos\extra.mp4";
        var rec=Owner(old);Claim(engine,rec,old);var frozen=Read(engine,old,next,extra)!;
        using var barrier=new Barrier(2);var observations=0;
        var writer=Task.Run(()=>{Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));for(var i=0;i<300;i++)Claim(engine,rec,i%2==0?next:old);Claim(engine,Owner(extra),extra);});
        var reader=Task.Run(()=>{Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));for(var i=0;i<300;i++){var s=Read(engine,old,next,extra);if(s is null)continue;Assert.NotEqual(s.Paths[0].InUse,s.Paths[1].InUse);observations++;}});
        await Task.WhenAll(writer,reader).WaitAsync(TimeSpan.FromSeconds(10));Assert.True(observations>0);
        Assert.True(frozen.Paths[0].InUse);Assert.False(frozen.Paths[1].InUse);Assert.False(frozen.Paths[2].InUse);
        Assert.True(Read(engine,extra)!.Paths.Single().InUse);Assert.Equal(2,OwnerCount(engine));
        Assert.Throws<NotSupportedException>(()=>((IList<OrdinaryRuntimeOutputUse>)frozen.Paths)[0]=new(next,true));
    }

    [Fact]
    public void UnregisteredCancellationReleasesOnlyItsOwnClaimAndShutdownIsUnavailable()
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var path=@"C:\sample\Videos\a.mp4";var old=Owner(path);var other=Owner(@"C:\sample\Videos\b.mp4");
        Claim(engine,old,path);Claim(engine,other,other.OutputPath);old.State=RecState.cancelled;
        Release(engine,old);var s=Read(engine,path,other.OutputPath)!;Assert.False(s.Paths[0].InUse);Assert.True(s.Paths[1].InUse);
        engine.BeginHostShutdown("test_shutdown");Assert.Null(Read(engine,path,other.OutputPath));Assert.Equal(1,OwnerCount(engine));
    }

    [Fact]
    public void HundredPathsAreAcceptedButOneHundredOneAndCaseDuplicatesAreUnavailable()
    {
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var paths=Enumerable.Range(0,101).Select(i=>@"C:\sample\Videos\"+i+".mp4").ToArray();
        Assert.Equal(100,Read(engine,paths[..100])!.Paths.Count);Assert.Null(Read(engine,paths));
        Assert.Null(Read(engine,paths[0],paths[0].ToUpperInvariant()));
    }

    private sealed class PoisonBackend : ICaptureBackend
    {
        internal int Calls;
        private Exception Fail(){Calls++;return new InvalidOperationException("Read must not touch backend");}
        public void Start(CaptureConfig c,CaptureAuthorizationProof p)=>throw Fail();
        public OutputMeta Stop()=>throw Fail();public void OnNaturalExit(Action<int,OutputMeta> callback)=>throw Fail();
        public int ExitCode=>throw Fail();public void Dispose()=>throw Fail();
    }
}
