using System.Diagnostics;
using System.Net;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    private sealed class Staging383SOwnedSession(WgcContinuousSessionOptions options) : IWgcContinuousBackendSession,IWgcContinuousResourceOwner
    {
        internal readonly TaskCompletionSource Retired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<WgcContinuousSessionResult> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Marker=>Path.Combine(Path.GetDirectoryName(options.OutputPath)!,"marker");
        public Task ResourceRetirement=>Retired.Task;public Task<WgcContinuousSessionResult> CompletionTask=>Result.Task;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public Task StartAsync(CancellationToken token=default)=>Task.CompletedTask;
        public Task<bool> AuthorizeCapture(CancellationToken token=default) {
            FirstFrameObserved?.Invoke(new(){EvidenceKind="wgc_continuous_first_frame",FrameNumber=1,OutTimeUs=1000,
                MediaStartSystemRelativeTimeHns=(long)((decimal)Stopwatch.GetTimestamp()*10000000/Stopwatch.Frequency)});
            return Task.FromResult(true);
        }
        public void Fail()=>Result.TrySetResult(new(){State=WgcContinuousManagedSessionState.Failed,ProcessExitConfirmed=false,
            ExitCode=-1,OutputPath=options.OutputPath,FailureCategory="owned_staging_test"});
        public Task<bool> RequestStop(CancellationToken token=default) {Fail();return Task.FromResult(false);}
        public void Dispose()=>Fail();
    }
    [Fact]
    public async Task Staging383SActualApiKeepsBudgetAndOutputUntilOwnWitnessWithoutTouchingPeer()
    {
        using var env=new WindowBudgetEnvironment("2");using var f=IndependentWindow383Fixture();IndependentWindow383Providers(f);f.StartApi();
        var sessions=new List<Staging383SOwnedSession>();var backends=new List<WgcContinuousCaptureBackend>();
        f.Engine.BackendFactory=_=> {
            var backend=new WgcContinuousCaptureBackend(options=> {
                var session=new Staging383SOwnedSession(options);File.WriteAllText(session.Marker,"owned");sessions.Add(session);return session;
            },StagingToFinalPublisher.Instance,_=>new(),()=>Path.Combine(f.Root,"wgc-helper.exe"),Path.Combine(f.Root,"staging"));
            backends.Add(backend);return (backend,"wgc-continuous");
        };
        try {
            var a=await NestedWindow381Accept(f,IndependentWindow382Request(f));await IndependentWindow382ApproveForStaging383S(f,a);
            var b=await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));await IndependentWindow382ApproveForStaging383S(f,b);
            sessions[0].Fail();await Until(()=>a.IsFinalized);backends[0].Dispose();
            Assert.True(File.Exists(sessions[0].Marker));Assert.True(File.Exists(sessions[1].Marker));
            Assert.Equal(RecState.recording,b.State);Assert.Equal(2,f.Engine.GetIndependentWindowBudget().Reserved);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine,a.OutputPath)!.Paths.Single().InUse);
            Assert.Equal(HttpStatusCode.Conflict,(await NestedWindow381Post(f,IndependentWindow382Request(f,"window_44"))).Status);
            sessions[0].Retired.TrySetResult();await backends[0].OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(sessions[0].Marker));Assert.True(File.Exists(sessions[1].Marker));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==1);
            await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_44"));Assert.Equal(RecState.recording,b.State);
        } finally {
            foreach(var session in sessions){session.Fail();session.Retired.TrySetResult();}
            await NestedWindow381Drain(f);foreach(var backend in backends)backend.Dispose();
        }
    }
    private static async Task IndependentWindow382ApproveForStaging383S(Fixture f,Recording rec)
    {
        f.Tray.Confirmations[rec.Id](AgentRecorder.Infrastructure.ConfirmationDecision.Approve());
        await Until(()=>rec.State==RecState.recording||rec.IsFinalized);Assert.Equal(RecState.recording,rec.State);
    }
}
