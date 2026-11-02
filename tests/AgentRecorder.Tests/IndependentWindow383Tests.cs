using System.Net;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using AgentRecorder.Infrastructure;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    private static Fixture IndependentWindow383Fixture() => new(mediaOnlyEvidence:true,testRoot:Path.Combine(Path.GetTempPath(),"window383"));
    private sealed class WindowBudgetEnvironment : IDisposable
    {
        private readonly string? old = Environment.GetEnvironmentVariable("AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS");
        internal WindowBudgetEnvironment(string? value) => Environment.SetEnvironmentVariable("AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS", value);
        public void Dispose() => Environment.SetEnvironmentVariable("AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS", old);
    }
    private static void IndependentWindow383Providers(Fixture f) {
        f.Engine.OrdinaryWindowMetadataForTests = (hwnd,pid) => OrdinaryWindowTargetKeyTests.Facts(hwnd,pid);
        SystemQuery.SetWindowProvider((_, _) =>
        Enumerable.Range(42, 11).Select(n => new SystemQuery.WindowInfo("window_" + n, "Synthetic " + n, "owned.exe", 7, false, false, new(0,0,64,64))).ToList());
    }

    [Fact]
    public async Task IndependentWindow383ExplicitFourAdmitsThirdRawWindow()
    {
        using var env = new WindowBudgetEnvironment("4");
        using var f = IndependentWindow383Fixture(); IndependentWindow383Providers(f); f.StartApi();
        for (int i=42;i<45;i++)
        {
            var result = await NestedWindow381Post(f, IndependentWindow382Request(f,"window_"+i));
            SaveSeriesBundleEvidence("independent383-red-"+i,new { http=(int)result.Status,result.Body });
            Assert.True(result.Status==HttpStatusCode.OK,result.Body);
        }
        Assert.Equal(3,f.Engine._recs.Count); Assert.Equal(0,f.FactoryCalls);
    }

    [Theory][InlineData(null,2)][InlineData("1",1)][InlineData("4",4)][InlineData("10",10)]
    public async Task IndependentWindow383FrozenLimitAndCapabilitiesAgree(string? setting,int limit)
    {
        using var env = new WindowBudgetEnvironment(setting);
        using var f = IndependentWindow383Fixture(); IndependentWindow383Providers(f); f.StartApi();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS","1");
        Assert.Equal(limit,f.Engine.IndependentWindowPolicy.ConfiguredLimit);
        for(int i=0;i<limit;i++) await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_"+(42+i)));
        var full = await NestedWindow381Post(f,IndependentWindow382Request(f,"window_52"));
        Assert.Equal(HttpStatusCode.Conflict,full.Status); Assert.Contains("independent_window_budget_exhausted",full.Body);
        Assert.Contains("\"configured_limit\":"+limit,full.Body);
        var snapshot=f.Engine.GetIndependentWindowBudget(); Assert.Equal(limit,snapshot.Reserved);Assert.Equal(0,snapshot.Remaining);
        using var client=f.Client();var cap=await Get(client,"/api/v1/capabilities");
        var branch=cap.GetProperty("recording").GetProperty("window_surface").GetProperty("independent_windows");
        Assert.Equal(limit,branch.GetProperty("max_concurrent").GetInt32());Assert.Equal(2,branch.GetProperty("default_limit").GetInt32());
        Assert.Equal(10,branch.GetProperty("hard_limit").GetInt32());Assert.False(branch.GetProperty("stress_tested").GetBoolean());
        Assert.Equal(limit,branch.GetProperty("budget").GetProperty("reserved").GetInt32());
        Assert.Equal(2,cap.GetProperty("recording").GetProperty("window_surface").GetProperty("nested").GetProperty("max_concurrent").GetInt32());
        Assert.Equal(0,f.FactoryCalls);
    }

    [Theory][InlineData("0")][InlineData("11")][InlineData("-1")][InlineData("999999999999999999999999")]
    [InlineData("four")][InlineData("1.0")][InlineData(" ")][InlineData("+4")]
    public async Task IndependentWindow383InvalidStartupConfigurationFailsBeforeConfirmation(string value)
    {
        using var env=new WindowBudgetEnvironment(value);using var f=IndependentWindow383Fixture();IndependentWindow383Providers(f);f.StartApi();
        Assert.False(f.Engine.IndependentWindowPolicy.Valid);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS","10");
        var response=await NestedWindow381Post(f,IndependentWindow382Request(f));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.Status);Assert.Contains("INDEPENDENT_WINDOW_CONFIGURATION_INVALID",response.Body);
        Assert.Empty(f.Engine._recs);Assert.Empty(f.Engine._confs);Assert.Equal(0,f.FactoryCalls);
        using var client=f.Client();var cap=await Get(client,"/api/v1/capabilities");
        var branch=cap.GetProperty("recording").GetProperty("window_surface").GetProperty("independent_windows");
        Assert.False(branch.GetProperty("available").GetBoolean());Assert.False(branch.GetProperty("configuration_valid").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,branch.GetProperty("max_concurrent").ValueKind);
        Assert.Null(f.Engine.GetIndependentWindowBudget().Remaining);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow383TenSessionsPublishOwnBundlesAndHistory(bool reverse)
    {
        using var env=new WindowBudgetEnvironment("10");using var f=IndependentWindow383Fixture();await f.Prepare();IndependentWindow383Providers(f);f.StartApi();
        var runs=new List<(Recording Rec,SyntheticProcess Process,WgcContinuousCaptureBackend Backend)>();
        try {
            var records=new List<Recording>();
            for(int i=42;i<52;i++) records.Add(await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_"+i)));
            var excess=await NestedWindow381Post(f,IndependentWindow382Request(f,"window_52"));Assert.Equal(HttpStatusCode.Conflict,excess.Status);
            foreach(var rec in reverse?records.AsEnumerable().Reverse():records) {
                Assert.Null(rec.AuthorizationProof);var live=await IndependentWindow382Approve(f,rec);runs.Add((rec,live.Process,live.Backend));
            }
            Assert.Equal(10,f.FactoryCalls);Assert.Equal(10,runs.Select(x=>x.Rec.WindowIdentity!.Number).Distinct().Count());
            Assert.Equal(10,runs.Select(x=>x.Rec.AuthorizationProof!.ScopeDigest).Distinct().Count());
            Assert.Equal(10,runs.Select(x=>x.Process.Options.BeginSignalPath).Distinct().Count());
            Assert.Equal(10,runs.Select(x=>x.Rec.OutputPath).Distinct().Count());Assert.Equal(10,runs.Select(x=>x.Backend).Distinct().Count());
            f.Generator.Release.TrySetResult();
            foreach(var run in runs) {
                f.Tray.Confirmations[run.Rec.Id](ConfirmationDecision.Approve());
                run.Process.Finish.TrySetResult();await Until(()=>run.Rec.IsFinalized && run.Rec.BundleSnapshot.Status=="ready" && f.Writer.Read(run.Rec.Id)?.Bundle?.Status=="ready");
                await PathCanonical381RHistory(f,run.Rec);Assert.Equal(Hash(f.Clip),Hash(run.Rec.OutputPath));
            }
            await Task.WhenAll(runs.Select(x=>x.Backend.OutputRetirement)).WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==0);
            Assert.Equal(10,records.Select(x=>x.OrdinaryTargetKey).Distinct().Count());
            Assert.Equal(10,records.Select(x=>x.OrdinaryBundleOperation).Distinct().Count());
            SaveSeriesBundleEvidence("independent383-ten-"+reverse,new {count=10,reserved=f.Engine.GetIndependentWindowBudget().Reserved,ids=records.Select(x=>x.Id).ToArray(),native_gpu_test=false});
        } finally {await NestedWindow381Drain(f);}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow383TerminalBundleQueueOrUnknownDispatchKeepsBudget(bool unknown)
    {
        using var env=new WindowBudgetEnvironment("1");using var f=IndependentWindow383Fixture();await f.Prepare();IndependentWindow383Providers(f);f.StartApi();
        Func<Task>? queued=null;
        f.Engine.BundleGenerationDispatcherForTests=run=>{queued=run;if(unknown)throw new IOException("owned dispatch unknown");return Task.CompletedTask;};
        var a=await NestedWindow381Accept(f,IndependentWindow382Request(f));var live=await IndependentWindow382Approve(f,a);
        try {
            live.Process.Finish.TrySetResult();await Until(()=>a.IsFinalized && queued is not null);
            await live.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            var budget=f.Engine.GetIndependentWindowBudget();Assert.Equal(1,budget.Reserved);Assert.Equal(1,budget.FinalizingOrUnknown);
            var response=await NestedWindow381Post(f,IndependentWindow382Request(f,"window_43"));Assert.Equal(HttpStatusCode.Conflict,response.Status);
            Assert.Contains("\"finalizing_or_unknown\":1",response.Body);Assert.NotNull(f.Engine.TryReserveIdleHostShutdown(()=>null));
            f.Generator.Release.TrySetResult();await queued!();queued=null;await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==0);
            var next=await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));Assert.True(next.WindowIdentity!.Number>a.WindowIdentity!.Number);
        } finally {f.Generator.Release.TrySetResult();if(queued is not null)await queued();await NestedWindow381Drain(f);}
    }

    [Theory][InlineData("reject")][InlineData("stop")][InlineData("expire")][InlineData("backend")]
    public async Task IndependentWindow383NoResourcesRetirementReturnsOnlyItsOwnBudget(string ending)
    {
        using var env=new WindowBudgetEnvironment("2");using var f=IndependentWindow383Fixture();IndependentWindow383Providers(f);f.StartApi();
        if(ending=="expire")f.Engine.ConfirmationTimeout=TimeSpan.FromMilliseconds(150);
        var a=await NestedWindow381Accept(f,IndependentWindow382Request(f));
        if(ending=="expire")await Until(()=>a.IsFinalized);
        else if(ending=="reject")f.Tray.Confirmations[a.Id](ConfirmationDecision.Reject());
        else if(ending=="stop")f.Engine.Stop(a.Id,"user_requested");
        else {f.Engine.BackendFactory=_=>throw new InvalidOperationException("owned creation fault");f.Tray.Confirmations[a.Id](ConfirmationDecision.Approve());await Until(()=>a.IsFinalized);}
        await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==(ending=="backend"?1:0));
        f.Engine.ConfirmationTimeout=TimeSpan.FromSeconds(60);
        var peer=await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
        f.Tray.Confirmations[a.Id](ConfirmationDecision.Approve());Assert.Equal(ending=="backend"?2:1,f.Engine.GetIndependentWindowBudget().Reserved);
        Assert.Equal(RecState.pending_confirmation,peer.State);
    }

    [Theory][InlineData("same")][InlineData("display")][InlineData("region")][InlineData("series")]
    [InlineData("system")][InlineData("mic")][InlineData("nested-window")][InlineData("nested-display")]
    [InlineData("quick")][InlineData("equivalent-output")][InlineData("profile")]
    public async Task IndependentWindow383TenBudgetDoesNotExpandOtherContracts(string fault)
    {
        using var env=new WindowBudgetEnvironment("10");
        await IndependentWindow382IncompatibleRequestsFailBeforeNewConfirmationOrWorker(fault);
    }

    [Fact]
    public async Task IndependentWindow383RequestFieldsCannotOverrideHostBudget()
    {
        using var env=new WindowBudgetEnvironment("1");using var f=IndependentWindow383Fixture();IndependentWindow383Providers(f);f.StartApi();
        var q=IndependentWindow382Request(f);q["max_concurrent"]=10;q["configured_limit"]=10;
        await NestedWindow381Accept(f,q);
        var response=await NestedWindow381Post(f,IndependentWindow382Request(f,"window_43"));
        Assert.Equal(HttpStatusCode.Conflict,response.Status);Assert.Equal(1,f.Engine.GetIndependentWindowBudget().Reserved);
    }
}
