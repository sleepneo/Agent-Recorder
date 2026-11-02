using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class WgcContinuousCaptureBackendTests
{
    private sealed class Staging383SProcess : IWgcContinuousProcess
    {
        internal readonly TaskCompletionSource ObservationEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim ReleaseObservation=new();
        internal readonly TaskCompletionSource Exited=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block;
        internal bool TimeoutFinalizerWaits;
        private int waits;
        public int Id=>101;public int ExitCode=>0;
        public bool HasExited { get {if(Block){Block=false;ObservationEntered.TrySetResult();ReleaseObservation.Wait();}return Exited.Task.IsCompleted;} }
        public Stream StandardOutputStream=>Stream.Null;public Stream StandardErrorStream=>Stream.Null;
        public void Start(string path,IReadOnlyList<string> arguments) { }
        public Task WaitForExitAsync(CancellationToken token)=>TimeoutFinalizerWaits && Interlocked.Increment(ref waits)>1 && token.CanBeCanceled
            ? Task.FromException(new TimeoutException("synthetic bounded wait exhausted")):Exited.Task.WaitAsync(token);
        public void KillEntireTree() { }
        public void Dispose() { }
    }
    [Fact]
    public async Task Staging383SPendingExitMustPreserveOwnedDirectoryAndMarker()
    {
        var root=Path.Combine(_tempDir,"staging383s");var helper=CreateOptionsWithHelperPath().HelperExePath;
        var process=new Staging383SProcess();WgcContinuousManagedSession? session=null;string? staging=null;
        using var backend=new WgcContinuousCaptureBackend(options=>{
            staging=Path.GetDirectoryName(options.OutputPath)!;File.WriteAllText(Path.Combine(staging,"owned-marker"),"owned");
            return session=new WgcContinuousManagedSession(options,process);
        },StagingToFinalPublisher.Instance,_=>new OutputMeta(),()=>helper,root);
        var config=CreateValidConfig(Path.Combine(_finalDir,"pending383s.mp4"));config.DeferCaptureStart=true;
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,config);
        process.Block=true;var stop=session!.RequestStop();
        try {
            await process.ObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Run(backend.Dispose).WaitAsync(TimeSpan.FromSeconds(25));
            Assert.False(process.Exited.Task.IsCompleted);Assert.False(session.CompletionTask.IsCompleted);
            Assert.False(session.ProcessRetirement.IsCompleted);Assert.False(backend.OutputRetirement.IsCompleted);
            Assert.True(Directory.Exists(staging));Assert.True(File.Exists(Path.Combine(staging!,"owned-marker")));
        } finally {process.Exited.TrySetResult();process.ReleaseObservation.Set();await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15));await stop.WaitAsync(TimeSpan.FromSeconds(15));}
        await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(staging));
    }
    [Fact]
    public async Task Staging383SManagedFalseResultRetainsUntilLaterActualExit()
    {
        var process=new Staging383SProcess {TimeoutFinalizerWaits=true};WgcContinuousManagedSession? session=null;string? dir=null;
        using var backend=new WgcContinuousCaptureBackend(o=>{dir=Path.GetDirectoryName(o.OutputPath)!;File.WriteAllText(Path.Combine(dir,"marker"),"owned");return session=new(o,process);},
            StagingToFinalPublisher.Instance,_=>new(),()=>CreateOptionsWithHelperPath().HelperExePath,Path.Combine(_tempDir,"late383s"));
        var cfg=CreateValidConfig(Path.Combine(_finalDir,"late383s.mp4"));cfg.DeferCaptureStart=true;
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,cfg);
        try {
            await session!.RequestStop();var result=await session.CompletionTask;
            Assert.False(result.ProcessExitConfirmed);backend.Dispose();
            Assert.True(File.Exists(Path.Combine(dir!,"marker")));Assert.False(backend.OutputRetirement.IsCompleted);
            process.Exited.TrySetResult();await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(dir));
        } finally {process.Exited.TrySetResult();}
    }
}
