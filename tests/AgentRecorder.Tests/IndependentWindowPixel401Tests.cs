using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private const string PixelEnv401 = "AGENT_RECORDER_MAX_INDEPENDENT_WINDOW_PIXELS_PER_SECOND";
    private const long Cost401 = 64L * 64 * 15;
    private sealed class PixelEnvironment401 : IDisposable
    {
        private readonly string? old = Environment.GetEnvironmentVariable(PixelEnv401);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetEnvironmentVariableW(string name, string? value);
        internal PixelEnvironment401(string? value)
        {
            // .NET's setter treats "" as deletion. An inherited native empty
            // value is distinct and must not become the default policy.
            Assert.True(SetEnvironmentVariableW(PixelEnv401,value));
            Assert.Equal(value,Environment.GetEnvironmentVariable(PixelEnv401));
        }
        public void Dispose() => Environment.SetEnvironmentVariable(PixelEnv401, old);
    }
    private static void Budget401(Fixture f, int count, long pixels, long? remaining, long finishing = 0)
    {
        var b = f.Engine.GetIndependentWindowBudget();
        Assert.Equal(count, b.Reserved); Assert.Equal(pixels, b.ReservedPixelsPerSecond);
        Assert.Equal(remaining, b.RemainingPixelsPerSecond);
        Assert.Equal(finishing, b.FinalizingOrUnknownPixelsPerSecond);
    }
    private static CapturePlan Plan401(CapturePlan p, CapturePlanBounds? surface) => new(
        p.RequestedBackend, p.PlannedBackend, p.Evidence, p.CaptureSemantics, p.SourceKind,
        p.TargetIdentity, p.WindowHandle, p.Bounds, targetWindowProcessId: p.TargetWindowProcessId,
        targetWindowSurfaceBounds: surface);

    [Theory]
    [InlineData(null,497664000L)][InlineData("1",1L)]
    [InlineData("000123",123L)][InlineData("9223372036854775807",long.MaxValue)]
    public async Task Pixel401ConfigurationFrozenAndCapabilityConsistent(string? value, long expected)
    {
        using var pixels = new PixelEnvironment401(value); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi();
        Environment.SetEnvironmentVariable(PixelEnv401,"1");
        var policy = f.Engine.IndependentWindowPolicy;
        Assert.Equal(expected, policy.ConfiguredPixelsPerSecond); Assert.True(policy.Valid);
        Assert.Equal(value is null ? "default" : "environment", policy.PixelRateSource);
        using var client = f.Client(); var cap = await Get(client,"/api/v1/capabilities");
        var branch = cap.GetProperty("recording").GetProperty("window_surface").GetProperty("independent_windows");
        var b = branch.GetProperty("pixel_rate_budget");
        Assert.Equal(expected,b.GetProperty("configured_pixels_per_second").GetInt64());
        Assert.Equal(497664000,b.GetProperty("default_pixels_per_second").GetInt64());
        Assert.Equal(0,b.GetProperty("reserved_pixels_per_second").GetInt64());
        Assert.Equal(expected,b.GetProperty("remaining_pixels_per_second").GetInt64());
        Assert.Equal("pixels_per_second",b.GetProperty("unit").GetString());
        Assert.False(b.GetProperty("snapshot_is_reservation").GetBoolean());
        Assert.False(b.GetProperty("performance_guaranteed").GetBoolean());
        Assert.False(branch.GetProperty("stress_tested").GetBoolean());
        Budget401(f,0,0,expected);
    }

    [Theory][InlineData("")][InlineData(" ")][InlineData(" 1")][InlineData("1 ")]
    [InlineData("+1")][InlineData("-1")][InlineData("0")][InlineData("1.0")]
    [InlineData("1e3")][InlineData("1,000")][InlineData("9223372036854775808")][InlineData("１２")]
    [InlineData("\t1")][InlineData("1\n")]
    public async Task Pixel401InvalidConfigurationFailsBeforePlanOrConfirmation(string value)
    {
        using var pixels = new PixelEnvironment401(value); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi();
        Environment.SetEnvironmentVariable(PixelEnv401,"497664000");
        f.Engine.CapturePlanFactoryForTests = _ => throw new Xunit.Sdk.XunitException("No plan for invalid startup policy");
        var r = await NestedWindow381Post(f,IndependentWindow382Request(f));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);
        Assert.Contains("INDEPENDENT_WINDOW_CONFIGURATION_INVALID",r.Body); Assert.Contains(PixelEnv401,r.Body);
        Assert.Empty(f.Engine._recs); Assert.Empty(f.Engine._confs); Assert.Empty(f.Tray.Confirmations);
        Assert.Equal(0,f.FactoryCalls); Assert.False(Directory.Exists(Path.Combine(f.Root,"Videos")));
        using var client = f.Client(); var cap = await Get(client,"/api/v1/capabilities");
        var branch = cap.GetProperty("recording").GetProperty("window_surface").GetProperty("independent_windows");
        Assert.False(branch.GetProperty("available").GetBoolean()); Assert.False(branch.GetProperty("configuration_valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null,branch.GetProperty("pixel_rate_budget").GetProperty("remaining_pixels_per_second").ValueKind);
        Budget401(f,0,0,null);
    }

    [Theory][InlineData(15)][InlineData(24)][InlineData(30)][InlineData(60)]
    public async Task Pixel401UsesFullPhysicalSurfaceAndConfiguredFps(int fps)
    {
        using var pixels = new PixelEnvironment401("497664000"); using var f = IndependentWindow383Fixture();
        // The display/clipped rectangle is 64x64; the physical window surface is 128x96.
        SystemQuery.SetWindowProvider((_,_) => new() { new("window_42","owned","owned.exe",7,false,false,new(-32,-16,128,96)) });
        f.StartApi(); var q = IndependentWindow382Request(f); q["video"]!["fps"] = fps;
        q["requested_pixels_per_second"] = 1; q["configured_pixels_per_second"] = long.MaxValue;
        var rec = await NestedWindow381Accept(f,q);
        Assert.Equal(128,rec.ApprovedCapturePlan!.TargetWindowSurfaceBounds!.Width);
        Budget401(f,1,128L*96*fps,497664000L-128L*96*fps);
        Assert.False(rec.GenerateBundle); Assert.Equal(0,f.Generator.Calls); Assert.Equal(0,f.FactoryCalls);
    }

    [Theory][InlineData(61440L,true)][InlineData(61439L,false)]
    public async Task Pixel401ExactLimitAndOverLimitHaveNoHiddenSideEffects(long limit,bool accepted)
    {
        using var pixels = new PixelEnvironment401(limit.ToString()); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi();
        var q = IndependentWindow382Request(f); q["output"]!["directory"] = Path.Combine(f.Root,"new-output");
        var response = await NestedWindow381Post(f,q);
        if(accepted) { Assert.Equal(HttpStatusCode.OK,response.Status); Budget401(f,1,Cost401,0); }
        else {
            Assert.Equal(HttpStatusCode.Conflict,response.Status); Assert.Contains("INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",response.Body);
            using var doc = JsonDocument.Parse(response.Body); var d = doc.RootElement.GetProperty("error").GetProperty("details");
            Assert.Equal(Cost401,d.GetProperty("requested_pixels_per_second").GetInt64());
            Assert.Equal(0,d.GetProperty("reserved_pixels_per_second").GetInt64());
            Assert.Equal(limit,d.GetProperty("configured_pixels_per_second").GetInt64());
            Assert.Equal(limit,d.GetProperty("remaining_pixels_per_second").GetInt64());
            Assert.DoesNotContain(f.Root,response.Body); Assert.DoesNotContain("window_42",response.Body);
            Assert.Empty(f.Engine._recs); Assert.Empty(f.Engine._confs); Assert.Empty(f.Tray.Confirmations);
            Assert.False(Directory.Exists(Path.Combine(f.Root,"new-output"))); Budget401(f,0,0,limit);
        }
        Assert.Equal(0,f.FactoryCalls); Assert.Equal(0,f.Generator.Calls);
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task Pixel401CountAndPixelLimitsRemainIndependentWithStablePriority(bool count)
    {
        using var windows = new WindowBudgetEnvironment(count ? "1" : "10");
        using var pixels = new PixelEnvironment401(Cost401.ToString()); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi();
        await NestedWindow381Accept(f,IndependentWindow382Request(f));
        var r = await NestedWindow381Post(f,IndependentWindow382Request(f,"window_43"));
        Assert.Contains(count ? "TOO_MANY_CONCURRENT_RECORDINGS" : "INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",r.Body);
        // Same-window error precedes pixel cost once the coarse count gate passes.
        if(!count) Assert.Contains("WINDOW_TARGET_IN_USE",(await NestedWindow381Post(f,IndependentWindow382Request(f))).Body);
        Budget401(f,1,Cost401,0);
        using var client=f.Client();var cap=await Get(client,"/api/v1/capabilities");
        var branch=cap.GetProperty("recording").GetProperty("window_surface").GetProperty("independent_windows");
        Assert.Equal(1,branch.GetProperty("budget").GetProperty("reserved").GetInt32());
        var rate=branch.GetProperty("pixel_rate_budget");
        Assert.Equal(Cost401,rate.GetProperty("reserved_pixels_per_second").GetInt64());
        Assert.Equal(0,rate.GetProperty("remaining_pixels_per_second").GetInt64());
    }

    [Fact]
    public async Task Pixel401ConcurrentFinalSlotIsAtomic()
    {
        using var windows = new WindowBudgetEnvironment("10"); using var pixels = new PixelEnvironment401(Cost401.ToString());
        using var f = IndependentWindow383Fixture(); IndependentWindow383Providers(f);
        using var gate = new Barrier(2);
        int crossedBarrier=0;
        // Both requests have passed the trustworthy-cost coarse check and
        // preflight by this point. Hold them immediately before final admission.
        f.Engine.OrdinaryWindowMetadataForTests = (hwnd,pid) => {
            Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
            Interlocked.Increment(ref crossedBarrier);
            return OrdinaryWindowTargetKeyTests.Facts(hwnd,pid);
        };
        var requests = new[]{IndependentWindow382Request(f),IndependentWindow382Request(f,"window_43")};
        for(int i=0;i<2;i++) requests[i]["output"]!["directory"] = Path.Combine(f.Root,"race-"+i);
        var outcomes = await Task.WhenAll(requests.Select(q => Task.Run(() => {
            try { f.Engine.CreateRecording(q,"task401",f.Tray); return (ApiException?)null; }
            catch(ApiException ex) { return ex; }
        })));
        Assert.Equal(2,crossedBarrier);
        Assert.Single(outcomes,e=>e is null);
        Assert.Equal("INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",Assert.Single(outcomes,e=>e is not null)!.Code);
        var rec = Assert.Single(f.Engine._recs.Values); Assert.Single(f.Tray.Confirmations);
        Assert.Single(Directory.GetDirectories(f.Root,"race-*")); Budget401(f,1,Cost401,0);
        Assert.NotNull(RuntimeOutputTestEngine.Read(f.Engine,rec.OutputPath));
        Assert.Equal(0,f.FactoryCalls); Assert.Equal(0,f.Generator.Calls);
    }

    [Theory][InlineData("missing")][InlineData("zero")][InlineData("negative")][InlineData("overflow")][InlineData("null-plan")]
    public async Task Pixel401UnreliableCostRejectsBeforeOutputAndConfirmation(string fault)
    {
        using var pixels = new PixelEnvironment401(long.MaxValue.ToString()); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi();
        f.Engine.CapturePlanFactoryForTests = cfg => fault == "null-plan" ? null! : Plan401(CaptureBackendSelector.BuildPlan(cfg,new Probe()),
            fault == "missing" ? null : new(0,0,fault == "zero" ? 0 : fault == "negative" ? -1 : int.MaxValue,int.MaxValue));
        var r = await NestedWindow381Post(f,IndependentWindow382Request(f));
        Assert.Equal(HttpStatusCode.BadRequest,r.Status); Assert.Contains("INDEPENDENT_WINDOW_CAPTURE_PLAN_INVALID",r.Body);
        Assert.Empty(f.Tray.Confirmations); Assert.Empty(f.Engine._recs); Assert.Equal(0,f.FactoryCalls);
        Assert.False(Directory.Exists(Path.Combine(f.Root,"Videos"))); Budget401(f,0,0,long.MaxValue);
    }

    [Fact]
    public void Pixel401CheckedArithmeticRejectsMultiplicationAndAdditionOverflow()
    {
        var plan = OrdinaryWindowTargetKeyTests.Plan(width:int.MaxValue,height:int.MaxValue);
        Assert.Throws<ApiException>(() => RecordingEngine.CalculateIndependentWindowPixelRate(plan,60));
        Assert.Equal(15L*int.MaxValue,RecordingEngine.CalculateIndependentWindowPixelRate(OrdinaryWindowTargetKeyTests.Plan(width:int.MaxValue,height:1),15));
        foreach(var fps in new[]{0,-1,1,61}) Assert.Throws<ApiException>(() => RecordingEngine.CalculateIndependentWindowPixelRate(plan,fps));
        Assert.True(RecordingEngine.IndependentWindowPixelRateFits(long.MaxValue-1,1,long.MaxValue));
        Assert.False(RecordingEngine.IndependentWindowPixelRateFits(long.MaxValue-1,2,long.MaxValue));
        Assert.False(RecordingEngine.IndependentWindowPixelRateFits(2,3,4));
    }

    [Theory][InlineData("reject")][InlineData("expire")][InlineData("stop")][InlineData("backend")]
    public async Task Pixel401NoResourceEndingsReleaseOnlyExactReservation(string ending)
    {
        using var windows = new WindowBudgetEnvironment("10"); using var pixels = new PixelEnvironment401((Cost401*2).ToString());
        using var f = IndependentWindow383Fixture(); IndependentWindow383Providers(f); f.StartApi();
        var a = await NestedWindow381Accept(f,IndependentWindow382Request(f));
        var b = await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
        var late = f.Tray.Confirmations[a.Id]; Budget401(f,2,Cost401*2,0);
        if(ending=="reject") late(ConfirmationDecision.Reject());
        else if(ending=="expire") f.Engine.TriggerConfirmationExpiryForTests(a.ConfirmationId!);
        else if(ending=="stop") f.Engine.Stop(a.Id,"user_requested");
        else { f.Engine.BackendFactory = _ => throw new IOException("owned unknown creation"); late(ConfirmationDecision.Approve()); }
        await Until(()=>a.IsFinalized);
        Budget401(f,ending=="backend"?2:1,ending=="backend"?Cost401*2:Cost401,ending=="backend"?0:Cost401,ending=="backend"?Cost401:0);
        late(ConfirmationDecision.Approve()); Assert.Equal(RecState.pending_confirmation,b.State);
        Budget401(f,ending=="backend"?2:1,ending=="backend"?Cost401*2:Cost401,ending=="backend"?0:Cost401,ending=="backend"?Cost401:0);
    }

    [Fact]
    public async Task Pixel401HistoryRegistrationFailureRollsBackBothBudgets()
    {
        using var pixels = new PixelEnvironment401(Cost401.ToString());
        using var f = new Fixture(mediaOnlyEvidence:true,testRoot:Path.Combine(Path.GetTempPath(),"window401"),historyFactory:inner=>new RefuseRegistration(inner));
        IndependentWindow383Providers(f); f.StartApi();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await NestedWindow381Post(f,IndependentWindow382Request(f))).Status);
        Budget401(f,0,0,Cost401); Assert.Empty(f.Tray.Confirmations);
        await NestedWindow381Accept(f,IndependentWindow382Request(f)); Budget401(f,1,Cost401,0);
    }

    [Theory][InlineData("natural")][InlineData("stop")][InlineData("failure")]
    public async Task Pixel401PublicTerminalRetainsCostUntilActualWgcRetirement(string ending)
    {
        using var windows = new WindowBudgetEnvironment("10"); using var pixels = new PixelEnvironment401((Cost401*2).ToString());
        using var f = IndependentWindow383Fixture(); await f.Prepare(); IndependentWindow383Providers(f); f.StartApi();
        var a = await NestedWindow381Accept(f,IndependentWindow382Request(f));
        var b = await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
        var live = await IndependentWindow382Approve(f,a);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = a.OrdinaryHistoryPublication;
        a.OrdinaryHistoryPublication = r => { original!(r); if(r.IsFinalized) { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } };
        try {
            if(ending=="failure") { live.Process.FailMedia=true;live.Process.FailureCode="window_closed";live.Process.FailureReason="window_closed"; }
            var stop = ending=="stop" ? Task.Run(()=>f.Engine.Stop(a.Id,"user_requested")) : Task.CompletedTask;
            if(ending!="stop") live.Process.Finish.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(a.IsFinalized);
            if(ending=="stop")
            {
                Assert.True(live.Backend.OutputRetirement.IsCompletedSuccessfully);
                Budget401(f,1,Cost401,Cost401);
            }
            else
            {
                Assert.False(live.Backend.OutputRetirement.IsCompleted);
                Budget401(f,2,Cost401*2,0,Cost401);
                var denied = await NestedWindow381Post(f,IndependentWindow382Request(f,"window_44"));
                Assert.Contains("INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",denied.Body);
            }
            release.Set(); await stop; await live.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==1);
            Budget401(f,1,Cost401,Cost401); Assert.Equal(RecState.pending_confirmation,b.State);
            Assert.Equal(0,f.Generator.Calls); Assert.Null(a.OrdinaryBundleOperation);
        } finally { release.Set(); await NestedWindow381Drain(f); }
    }

    [Fact]
    public async Task Pixel401GeometryChangeAfterApprovalFailsWithoutIncreasingReservation()
    {
        using var pixels = new PixelEnvironment401(Cost401.ToString()); using var f = IndependentWindow383Fixture();
        IndependentWindow383Providers(f); f.StartApi(); var rec = await NestedWindow381Accept(f,IndependentWindow382Request(f));
        SystemQuery.SetWindowProvider((_,_) => new(){new("window_42","owned","owned.exe",7,false,false,new(0,0,128,128))});
        f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve()); await Until(()=>rec.IsFinalized);
        Assert.Equal(RecState.failed,rec.State); Assert.Equal(0,f.FactoryCalls);
        Budget401(f,0,0,Cost401); Assert.Equal(0,f.Generator.Calls);
    }

    private sealed class RegistrationGate401(IOrdinaryRecordingHistoryStore inner) : IOrdinaryRecordingHistoryStore
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public void Register(OrdinaryRecordingHistorySnapshot r)
        { Entered.TrySetResult(); Assert.True(Release.Wait(TimeSpan.FromSeconds(15))); inner.Register(r); }
        public bool TryPublish(OrdinaryRecordingHistorySnapshot r) => inner.TryPublish(r);
        public OrdinaryRecordingHistoryPage ReadPage(int n,string? c) => inner.ReadPage(n,c);
        public OrdinaryRecordingHistorySnapshot? Read(string id) => inner.Read(id);
        public void RecoverIncomplete(DateTimeOffset at) { }
    }

    [Fact]
    public async Task Pixel401StopDuringRegistrationCannotRetireAdmissionOrItsOutputEarly()
    {
        using var windows = new WindowBudgetEnvironment("10"); using var pixels = new PixelEnvironment401(Cost401.ToString());
        RegistrationGate401 gate = null!;
        using var f = new Fixture(mediaOnlyEvidence:true,testRoot:Path.Combine(Path.GetTempPath(),"window401"),historyFactory:inner=>gate=new(inner));
        IndependentWindow383Providers(f); f.StartApi();
        var create = Task.Run(()=>f.Engine.CreateRecording(IndependentWindow382Request(f),"task401",f.Tray));
        try {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var rec = Assert.Single(f.Engine._recs.Values);
            f.Engine.Stop(rec.Id,"user_requested"); await Until(()=>rec.IsFinalized);
            Budget401(f,1,Cost401,0,Cost401);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
            Assert.Contains("INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",(await NestedWindow381Post(f,IndependentWindow382Request(f,"window_43"))).Body);
            gate.Release.Set(); await create;
            Budget401(f,0,0,Cost401); Assert.Empty(f.Tray.Confirmations); Assert.Equal(0,f.FactoryCalls);
        } finally { gate.Release.Set(); await create; gate.Release.Dispose(); }
    }

    [Fact]
    public async Task Pixel401UnknownResourceAndLateRetirementNeverReleasePeerCost()
    {
        using var windows = new WindowBudgetEnvironment("10"); using var pixels = new PixelEnvironment401((Cost401*2).ToString());
        using var f = IndependentWindow383Fixture(); IndependentWindow383Providers(f); f.StartApi();
        Staging383SOwnedSession session = null!; WgcContinuousCaptureBackend backend = null!;
        f.Engine.BackendFactory = _ => {
            backend = new WgcContinuousCaptureBackend(options=>session=new(options),StagingToFinalPublisher.Instance,
                _=>new(),()=>Path.Combine(f.Root,"wgc-helper.exe"),Path.Combine(f.Root,"staging"));
            return (backend,"wgc-continuous");
        };
        try {
            var a = await NestedWindow381Accept(f,IndependentWindow382Request(f));
            await IndependentWindow382ApproveForStaging383S(f,a);
            var b = await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
            session.Fail(); await Until(()=>a.IsFinalized); backend.Dispose();
            Assert.False(backend.OutputRetirement.IsCompleted); Budget401(f,2,Cost401*2,0,Cost401);
            Assert.Contains("INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",(await NestedWindow381Post(f,IndependentWindow382Request(f,"window_44"))).Body);
            session.Retired.TrySetResult(); await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Budget401(f,1,Cost401,Cost401);
            var c = await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_44"));
            session.Fail(); session.Retired.TrySetResult(); f.Tray.Confirmations[a.Id](ConfirmationDecision.Approve());
            Budget401(f,2,Cost401*2,0); Assert.Equal(RecState.pending_confirmation,b.State); Assert.Equal(RecState.pending_confirmation,c.State);
            Assert.Equal(0,f.Generator.Calls);
        } finally { if(session is not null){session.Fail();session.Retired.TrySetResult();} await NestedWindow381Drain(f);backend?.Dispose(); }
    }

    [Fact]
    public async Task Pixel401PreparingCountdownRecordingAndStoppingKeepFrozenCost()
    {
        using var pixels = new PixelEnvironment401(Cost401.ToString()); using var f = IndependentWindow383Fixture();
        await f.Prepare(); IndependentWindow383Providers(f); f.StartApi();
        f.Engine.CountdownSteps=1;
        var q=IndependentWindow382Request(f);q["countdown_seconds"]=1;
        var rec=await NestedWindow381Accept(f,q);
        using var prepared=new ManualResetEventSlim();using var zero=new ManualResetEventSlim();using var stopGate=new ManualResetEventSlim();
        var preparation=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countdown=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SyntheticProcessCreatedForTests=_=>{preparation.TrySetResult();Assert.True(prepared.Wait(TimeSpan.FromSeconds(15)));};
        f.Engine.BeforeStartActionForTests=(_,action)=>{Assert.Equal("start_capture",action);countdown.TrySetResult();Assert.True(zero.Wait(TimeSpan.FromSeconds(15)));};
        var approve=Task.Run(()=>f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve()));
        try {
            await preparation.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(RecState.preparing,rec.State);Budget401(f,1,Cost401,0);
            prepared.Set();await countdown.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(RecState.countdown,rec.State);Budget401(f,1,Cost401,0);
            zero.Set();await approve;await Until(()=>rec.State==RecState.recording);Budget401(f,1,Cost401,0);
            f.Engine.BeforeStopForTests=_=>{stopping.TrySetResult();Assert.True(stopGate.Wait(TimeSpan.FromSeconds(15)));};
            var stop=Task.Run(()=>f.Engine.Stop(rec.Id,"user_requested"));
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Budget401(f,1,Cost401,0);
            stopGate.Set();await stop;await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Budget401(f,0,0,Cost401);Assert.Equal(0,f.Generator.Calls);
        } finally { prepared.Set();zero.Set();stopGate.Set();await approve;await NestedWindow381Drain(f); }
    }

    [Theory][InlineData("display")][InlineData("quick")][InlineData("nested")]
    public async Task Pixel401InvalidConfigurationDoesNotDisableOtherContracts(string contract)
    {
        using var pixels=new PixelEnvironment401("invalid");using var f=IndependentWindow383Fixture();IndependentWindow383Providers(f);
        if(contract=="quick")SystemQuery.SetActiveWindowProvider(()=>new("window_42","owned","owned.exe",7,false,false,new(0,0,64,64)));
        f.StartApi();
        var q=contract=="display"?f.Request("display"):IndependentWindow382Request(f);
        if(contract=="quick")
        {
            q.Remove("source");q["target"]=new JsonObject{["type"]="active_window"};
            q.Remove("stop_condition");q["duration_seconds"]=1;
        }
        if(contract=="nested")q["nested"]=new JsonObject{["role"]="outer",["session_id"]="owned401"};
        var result=await NestedWindow381Post(f,q,contract=="quick");
        Assert.True(result.Status==HttpStatusCode.OK,result.Body); Assert.Single(f.Engine._recs);Budget401(f,0,0,null);
    }
}
