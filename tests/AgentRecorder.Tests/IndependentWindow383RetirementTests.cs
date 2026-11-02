using System.Net;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    [Fact]
    public async Task IndependentWindow383PublicCompletedBeforeWgcCallbackRetiresKeepsReservation()
    {
        using var env=new WindowBudgetEnvironment("1");using var f=IndependentWindow383Fixture();await f.Prepare();IndependentWindow383Providers(f);f.StartApi();
        var q=IndependentWindow382Request(f);q["artifacts"]=new System.Text.Json.Nodes.JsonObject{["bundle"]=false};
        var a=await NestedWindow381Accept(f,q);var live=await IndependentWindow382Approve(f,a);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=a.OrdinaryHistoryPublication;
        a.OrdinaryHistoryPublication=rec=>{original!(rec);if(rec.IsFinalized){entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("owned terminal callback gate");}};
        try {
            live.Process.Finish.TrySetResult();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("completed",f.Writer.Read(a.Id)!.Status);Assert.False(live.Backend.OutputRetirement.IsCompleted);
            Assert.Equal(1,f.Engine.GetIndependentWindowBudget().FinalizingOrUnknown);
            var response=await NestedWindow381Post(f,IndependentWindow382Request(f,"window_43"));Assert.Equal(HttpStatusCode.Conflict,response.Status);
            release.Set();await live.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==0);
            await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
        } finally {release.Set();await NestedWindow381Drain(f);}
    }

    private sealed class RefuseRegistration(IOrdinaryRecordingHistoryStore inner) : IOrdinaryRecordingHistoryStore
    {
        private int calls;
        public void Register(OrdinaryRecordingHistorySnapshot snapshot) { if(Interlocked.Increment(ref calls)==1)throw new IOException("owned registration fault");inner.Register(snapshot); }
        public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot)=>inner.TryPublish(snapshot);
        public OrdinaryRecordingHistoryPage ReadPage(int limit,string? cursor)=>inner.ReadPage(limit,cursor);
        public OrdinaryRecordingHistorySnapshot? Read(string id)=>inner.Read(id);
        public void RecoverIncomplete(DateTimeOffset at) { }
    }
    [Fact]
    public async Task IndependentWindow383UnregisteredFailureRollsBackExactReservation()
    {
        using var env=new WindowBudgetEnvironment("1");using var f=new Fixture(mediaOnlyEvidence:true,testRoot:Path.Combine(Path.GetTempPath(),"window383"),historyFactory: inner=>new RefuseRegistration(inner));IndependentWindow383Providers(f);f.StartApi();
        var result=await NestedWindow381Post(f,IndependentWindow382Request(f));Assert.Equal(HttpStatusCode.ServiceUnavailable,result.Status);
        Assert.Contains("RECORDING_HISTORY_UNAVAILABLE",result.Body);Assert.Equal(0,f.Engine.GetIndependentWindowBudget().Reserved);
        Assert.Empty(f.Engine._recs);Assert.Empty(f.Engine._confs);Assert.Equal(0,f.FactoryCalls);
        await NestedWindow381Accept(f,IndependentWindow382Request(f));
    }
}
