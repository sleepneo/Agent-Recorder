using System.Net;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class ManagedVideoVolumeCapacity407Tests
{
    [Fact]
    public async Task AmbiguousStart407SRetainsAdmissionDespiteManagedCleanup()
    {
        var attempts = 0;
        var launched = 0;
        using var host = new Host("tray", provider: _ => new ProcessStorageCapacityProvider
        {
            ExecutablePath = () => HostExe("App"),
            HooksForTests = new StorageProbeHooks
            {
                BeforeStart = _ => Interlocked.Increment(ref attempts),
                ReturnFalseOnStart = true,
                Started = _ => Interlocked.Increment(ref launched)
            }
        });

        var first = await host.Raw();
        Assert.Contains("HTTP/1.1 503", first);
        Assert.Contains("\"reason_code\":\"sample_unavailable\"", first);
        var life = host.Provider.LastProbe!;
        Assert.False(life.Started);
        Assert.False(life.ExitCompletion.IsCompleted);
        Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Null(life.CleanupFailure);
        Assert.False(await host.Service.WaitForIdleAsync());

        var retry = await host.Raw();
        Assert.Contains("HTTP/1.1 503", retry);
        Assert.Contains("\"reason_code\":\"probe_retirement_unknown\"", retry);
        AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "storage_capacity_probe_retirement_unknown");
        Assert.Same(life, host.Provider.LastProbe);
        Assert.Equal(1, attempts);
        Assert.Equal(0, launched);
        Assert.Equal(0, host.ExitPosts);
    }
}
