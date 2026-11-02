using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class WgcContinuousManagedSessionTests
{
    // The watcher registers its wait first. Only the finalizer's subsequent
    // process-exit wait releases the encoder's terminal block. No timing retry.
    private sealed class FinalizeGatedProcess(FakeWgcContinuousProcess inner) : IWgcContinuousProcess
    {
        private int waits;
        public int Id=>inner.Id;public int ExitCode=>inner.ExitCode;public bool HasExited=>inner.HasExited;
        public Stream StandardOutputStream=>inner.StandardOutputStream;public Stream StandardErrorStream=>Stream.Null;
        public void Start(string exe,IReadOnlyList<string> args)=>inner.Start(exe,args);
        public void KillEntireTree()=>inner.KillEntireTree();
        public Task WaitForExitAsync(CancellationToken token)
        {if(Interlocked.Increment(ref waits)==2)inner.Continue();return inner.WaitForExitAsync(token);}
        public void Dispose()=>inner.Dispose();
    }
    [Theory][InlineData(180000)][InlineData(90000)][InlineData(30000)]
    public async Task WgcTerminal383RStopMustKeepReadingWhileEncoderFinalizes(int duration)
    {
        var options=CreateOptions("terminal383r",o=>{o.DurationMs=duration;o.TargetKind=WgcContinuousTargetKind.Window;o.AllowExtendedWindowSurfaceDuration=true;o.WindowHandle=(nint)42;});
        var inner=new FakeWgcContinuousProcess(Started(options.RecordingId,options.OutputPath).Select(s=>s.Replace("WGC_D3D11_FRAME_STREAM","WGC_D3D11_WINDOW_FRAME_STREAM")).Concat(Progress(1,1)),
            finalStdout:Stopped(5401,duration,64),createOutputFile:true,outputFileSize:64,outputFilePath:options.OutputPath,
            waitForBeginSignalPath:options.BeginSignalPath);
        using var process=new FinalizeGatedProcess(inner);using var session=new WgcContinuousManagedSession(options,process);
        await session.StartAsync();Assert.True(await session.AuthorizeCapture());
        await WaitForConditionAsync(()=>session.State==WgcContinuousManagedSessionState.Started,TimeSpan.FromSeconds(5));
        Assert.True(await session.RequestStop());
        var result=await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WgcContinuousManagedSessionState.Stopped,result.State);Assert.True(process.HasExited);
        Assert.Equal(duration,result.Summary!.DurationMs);Assert.False(inner.WasKilled);
    }

    [Theory][InlineData("missing")][InlineData("duplicate")][InlineData("malformed")][InlineData("fail")]
    public async Task WgcTerminal383RStopDrainStillRejectsInvalidTerminal(string kind)
    {
        var options=CreateOptions("invalid383r");
        IEnumerable<string> terminal=kind switch {"missing"=>Array.Empty<string>(),"duplicate"=>Stopped().Concat(Stopped()),
            "malformed"=>new[]{"RESULT: STOPPED","DurationMs: bad",""},_=>Fail("owned fault","encoding_error")};
        // Keep a final block queued even for the genuinely missing-terminal case.
        if(kind=="missing")terminal=new[]{"ignored: value",""};
        var inner=new FakeWgcContinuousProcess(Started(options.RecordingId,options.OutputPath).Concat(Progress(1,1)),terminal,
            createOutputFile:true,outputFileSize:64,outputFilePath:options.OutputPath,waitForBeginSignalPath:options.BeginSignalPath);
        using var process=new FinalizeGatedProcess(inner);using var session=new WgcContinuousManagedSession(options,process);
        await session.StartAsync();await session.AuthorizeCapture();await WaitForConditionAsync(()=>session.State==WgcContinuousManagedSessionState.Started,TimeSpan.FromSeconds(5));
        Assert.False(await session.RequestStop());Assert.Equal(WgcContinuousManagedSessionState.Failed,(await session.CompletionTask).State);
    }
}
