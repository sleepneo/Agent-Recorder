using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class WgcContinuousCaptureBackendTests
{
    private sealed class Staging383SWitnessSession(WgcContinuousSessionOptions options,bool throwStart=false) : IWgcContinuousBackendSession,IWgcContinuousResourceOwner
    {
        internal readonly TaskCompletionSource<WgcContinuousSessionResult> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Retired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ResourceRetirement=>Retired.Task;public Task<WgcContinuousSessionResult> CompletionTask=>Result.Task;
        public event Action<FirstFrameObservation>? FirstFrameObserved {add{}remove{}}
        public Task StartAsync(CancellationToken token=default)=>throwStart?throw new IOException("owned setup failure"):Task.CompletedTask;
        public Task<bool> AuthorizeCapture(CancellationToken token=default)=>Task.FromResult(true);
        public Task<bool> RequestStop(CancellationToken token=default)=>Task.FromResult(false);
        public void Dispose()=>Result.TrySetResult(new(){State=WgcContinuousManagedSessionState.Failed,ProcessExitConfirmed=false,ExitCode=-1,OutputPath=options.OutputPath});
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Staging383SFalseResultAndRollbackWaitForExactLateWitness(bool rollback)
    {
        Staging383SWitnessSession? owner=null;string? dir=null;
        var helper=CreateOptionsWithHelperPath().HelperExePath;
        using var backend=new WgcContinuousCaptureBackend(o=>{
            dir=Path.GetDirectoryName(o.OutputPath)!;File.WriteAllText(Path.Combine(dir,"marker"),"owned");
            return owner=new(o,rollback);
        },StagingToFinalPublisher.Instance,_=>new(),()=>helper,Path.Combine(_tempDir,"owned383s"));
        var cfg=CreateValidConfig(Path.Combine(_finalDir,"witness383s.mp4"));cfg.DeferCaptureStart=true;
        try {
            if(rollback)Assert.Throws<IOException>(()=>CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,cfg));
            else {CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,cfg);owner!.Dispose();await owner.CompletionTask;backend.Dispose();}
            Assert.True(File.Exists(Path.Combine(dir!,"marker")));Assert.False(backend.OutputRetirement.IsCompleted);
            owner!.Retired.TrySetResult();await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(dir));Assert.False(File.Exists(cfg.OutputPath));
        } finally {owner?.Retired.TrySetResult();}
    }
    [Fact]
    public async Task Staging383SCleanupFailureStaysUnknownUntilExactRetry()
    {
        Staging383SWitnessSession? owner=null;string? dir=null;
        using var backend=new WgcContinuousCaptureBackend(o=>{dir=Path.GetDirectoryName(o.OutputPath)!;return owner=new(o);},
            StagingToFinalPublisher.Instance,_=>new(),()=>CreateOptionsWithHelperPath().HelperExePath,Path.Combine(_tempDir,"locked383s"));
        var cfg=CreateValidConfig(Path.Combine(_finalDir,"locked383s.mp4"));cfg.DeferCaptureStart=true;
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,cfg);
        var hold=new FileStream(Path.Combine(dir!,"marker"),FileMode.Create,FileAccess.ReadWrite,FileShare.None);
        try {
            owner!.Dispose();owner.Retired.TrySetResult();backend.Dispose();
            Assert.True(Directory.Exists(dir));Assert.False(backend.OutputRetirement.IsCompleted);
        } finally {hold.Dispose();}
        backend.Dispose();await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(Directory.Exists(dir));
    }
    [Fact]
    public void Staging383SNullFactoryIsNotNoResourcesEvidence()
    {
        string? dir=null;
        using var backend=new WgcContinuousCaptureBackend(o=>{dir=Path.GetDirectoryName(o.OutputPath)!;File.WriteAllText(Path.Combine(dir,"marker"),"unknown factory");return null!;},
            StagingToFinalPublisher.Instance,_=>new(),()=>CreateOptionsWithHelperPath().HelperExePath,Path.Combine(_tempDir,"null383s"));
        Assert.ThrowsAny<Exception>(()=>CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,CreateValidConfig(Path.Combine(_finalDir,"null.mp4"))));
        backend.Dispose();Assert.True(File.Exists(Path.Combine(dir!,"marker")));Assert.False(backend.OutputRetirement.IsCompleted);
    }
    [Fact]
    public async Task Staging383SDisposeFromOwnCallbackDoesNotWaitForItself()
    {
        FakeSession? session=null;
        using var backend=CreateBackend(o=>{CreatePlaceholderMp4(o.OutputPath,10000);session=new FakeSession(o);session.StartTcs.TrySetResult(null);session.AuthorizeTcs.TrySetResult(true);return session;},out _,out _);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnNaturalExit((_,_)=>{backend.Dispose();done.TrySetResult();});
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,CreateValidConfig(Path.Combine(_finalDir,"callback383s.mp4")));
        session!.CompletionTcs.TrySetResult(session.DefaultResult);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Disposed",backend.LifecycleStateNameForTests);Assert.Equal(1,session.DisposeCount);
    }
    [Fact]
    public void Staging383SFailureBeforeFactoryEntryHasSealedNoAllocationEvidence()
    {
        int calls=0;
        using var backend=new WgcContinuousCaptureBackend(_=>{calls++;throw new InvalidOperationException("must not enter");},
            StagingToFinalPublisher.Instance,_=>new(),()=>throw new IOException("owned pre-factory failure"),Path.Combine(_tempDir,"pre-factory383s"));
        Assert.Throws<IOException>(()=>CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,CreateValidConfig(Path.Combine(_finalDir,"never383s.mp4"))));
        Assert.Equal(0,calls);Assert.True(backend.OutputRetirement.IsCompletedSuccessfully);
        Assert.False(Directory.Exists(Path.Combine(_tempDir,"pre-factory383s")));
    }
    [Fact]
    public async Task Staging383SDisposeCannotRetireCallbackHostStillInFlight()
    {
        FakeSession? session=null;
        using var backend=CreateBackend(o=>{CreatePlaceholderMp4(o.OutputPath,10000);session=new FakeSession(o);session.StartTcs.TrySetResult(null);session.AuthorizeTcs.TrySetResult(true);return session;},out _,out _);
        // Exercise the same bounded-return boundary, without spending the full grace/drain wall time.
        backend.DisposeGraceTimeoutForTests=TimeSpan.FromMilliseconds(20);backend.DisposeDrainTimeoutForTests=TimeSpan.FromMilliseconds(20);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
        backend.OnNaturalExit((_,_)=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));});
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,CreateValidConfig(Path.Combine(_finalDir,"host383s.mp4")));
        try {
            session!.CompletionTcs.TrySetResult(session.DefaultResult);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(backend.Dispose).WaitAsync(TimeSpan.FromSeconds(5));Assert.False(backend.OutputRetirement.IsCompleted);
        } finally {release.Set();}
        await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
