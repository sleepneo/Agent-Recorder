using System.Net;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Windows;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests {
    [Fact]
    public async Task IndependentWindow383ConfiguredLastSlotConcurrentBuildsCannotOversellTheLastSlot()
    {
        using var env = new WindowBudgetEnvironment("4");
        using var f = IndependentWindow383Fixture();
        SystemQuery.SetWindowProvider((_, _) => new() {
            new("window_42", "One", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_43", "Two", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_44", "Three", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_45", "Four", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_46", "Five", "owned.exe", 7, false, false, new(0,0,64,64)) });
        f.StartApi(); for(int n=42;n<45;n++)await NestedWindow381Accept(f, IndependentWindow382Request(f,"window_"+n));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int arrived = 0;
        f.Engine.CapturePlanFactoryForTests = cfg => {
            if (Interlocked.Increment(ref arrived) == 2) entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owned-last-slot-gate");
            return CaptureBackendSelector.BuildPlan(cfg, new Probe());
        };
        var one = NestedWindow381Post(f, IndependentWindow382Request(f, "window_45"));
        var two = NestedWindow381Post(f, IndependentWindow382Request(f, "window_46"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.Set();
            var results = await Task.WhenAll(one, two);
            Assert.Single(results, x => x.Status == HttpStatusCode.OK);
            Assert.Contains("TOO_MANY_CONCURRENT_RECORDINGS", Assert.Single(results, x => x.Status == HttpStatusCode.Conflict).Body);
            Assert.Equal(4, f.Engine._recs.Count); Assert.Equal(4, f.Engine._confs.Count); Assert.Equal(0, f.FactoryCalls);
            Assert.Equal(4, f.Engine._recs.Values.Select(r => r.WindowIdentity!.Number).Distinct().Count());
        }
        finally { release.Set(); await Task.WhenAll(one, two); }
    }

}
