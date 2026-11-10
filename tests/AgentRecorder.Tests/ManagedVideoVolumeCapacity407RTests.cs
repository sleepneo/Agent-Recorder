using System.ComponentModel;
using System.Net;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class ManagedVideoVolumeCapacity407Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedNoStart407RReturnsUnavailableThenAllowsIdleExitOrSameHostProbe(bool recover)
    {
        var useMissingExecutable = true;
        var launches = 0;
        using var host = new Host("tray", provider: root => new ProcessStorageCapacityProvider
        {
            ExecutablePath = () => useMissingExecutable ? Path.Combine(root, "missing-probe.exe") : HostExe("App"),
            HooksForTests = new StorageProbeHooks { Started = _ => Interlocked.Increment(ref launches) }
        });
        var response = await host.Raw();
        Assert.Contains("HTTP/1.1 503", response);
        Assert.Contains("\"reason_code\":\"sample_unavailable\"", response);
        var failed = host.Provider.LastProbe!;
        Assert.False(failed.Started);
        Assert.Equal(0, launches);
        Assert.False(failed.ExitCompletion.IsCompleted);
        Assert.True(failed.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Null(failed.CleanupFailure);
        Assert.True(await host.Service.WaitForIdleAsync());
        Assert.Null(host.Service.ShutdownBusyReason);

        if (recover)
        {
            useMissingExecutable = false;
            Assert.Contains("HTTP/1.1 200", await host.Raw());
            Assert.Equal(1, launches);
            Assert.NotSame(failed, host.Provider.LastProbe);
            Assert.True(host.Provider.LastProbe!.ExitCompletion.IsCompletedSuccessfully);
            Assert.True(host.Provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
        }
        Assert.Equal(HttpStatusCode.Accepted, (await host.Shutdown()).Status);
        await host.ExitPosted.Task.WaitAsync(Bound);
    }

    [Fact]
    public async Task ConfirmedNoStart407RKeepsAdmissionDuringLaunchAndLateCleanup()
    {
        var launchEntered = Signal();
        using var allowLaunch = new ManualResetEventSlim();
        var cleanupEntered = Signal();
        var allowCleanup = Signal();
        var attempts = 0;
        using var host = new Host("tray", provider: root => new ProcessStorageCapacityProvider
        {
            ExecutablePath = () => Path.Combine(root, "missing-probe.exe"),
            HooksForTests = new StorageProbeHooks
            {
                BeforeStart = _ =>
                {
                    Interlocked.Increment(ref attempts);
                    launchEntered.TrySetResult();
                    Assert.True(allowLaunch.Wait(Bound));
                },
                BeforeDispose = () => { cleanupEntered.TrySetResult(); return allowCleanup.Task; }
            }
        });
        try
        {
            var first = host.Raw();
            await launchEntered.Task.WaitAsync(Bound);
            AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "probe_busy");
            allowLaunch.Set();

            await cleanupEntered.Task.WaitAsync(Bound);
            var life = host.Provider.LastProbe!;
            Assert.False(life.Started);
            Assert.False(life.RetirementCompletion.IsCompleted);
            Assert.Contains("\"reason_code\":\"timeout\"", await first.WaitAsync(Bound));
            var busy = await host.Raw();
            Assert.Contains("HTTP/1.1 503", busy);
            Assert.Contains("\"reason_code\":\"probe_busy\"", busy);
            AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "storage_capacity_probe_pending");
            Assert.Equal(1, attempts);

            allowCleanup.TrySetResult();
            Assert.True(await host.Service.WaitForIdleAsync().WaitAsync(Bound));
            Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Null(life.CleanupFailure);
            Assert.Equal(HttpStatusCode.Accepted, (await host.Shutdown()).Status);
            await host.ExitPosted.Task.WaitAsync(Bound);
        }
        finally
        {
            allowLaunch.Set();
            allowCleanup.TrySetResult();
        }
    }

    [Theory]
    [InlineData("unknown-start")]
    [InlineData("handle")]
    [InlineData("cleanup")]
    public async Task ConfirmedNoStart407RDoesNotAcceptUnprovenStartupHandleOrCleanup(string phase)
    {
        var launchedPid = 0;
        var attempts = 0;
        using var host = new Host("tray", provider: root => new ProcessStorageCapacityProvider
        {
            ExecutablePath = () => phase == "handle" ? HostExe("App") : Path.Combine(root, "missing-probe.exe"),
            HooksForTests = new StorageProbeHooks
            {
                BeforeStart = _ =>
                {
                    Interlocked.Increment(ref attempts);
                    if (phase == "unknown-start") throw new Win32Exception(2);
                },
                BeforeCaptureHandle = process =>
                {
                    if (phase != "handle") return;
                    launchedPid = process.Id;
                    throw new Win32Exception(2);
                },
                BeforeDispose = () => phase == "cleanup"
                    ? Task.FromException(new IOException("Injected cleanup failure."))
                    : Task.CompletedTask
            }
        });
        var first = await host.Raw();
        Assert.Contains("HTTP/1.1 503", first);
        Assert.Contains("\"reason_code\":\"sample_unavailable\"", first);
        var life = host.Provider.LastProbe!;
        Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Equal(phase == "handle", launchedPid > 0);
        Assert.Equal(phase == "handle", life.Started);
        Assert.Equal(phase == "handle", life.ExitCompletion.IsCompletedSuccessfully);
        Assert.Equal(phase == "cleanup", life.CleanupFailure is not null);
        Assert.False(await host.Service.WaitForIdleAsync());
        var busy = await host.Raw();
        Assert.Contains("HTTP/1.1 503", busy);
        Assert.Contains("\"reason_code\":\"probe_retirement_unknown\"", busy);
        AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "storage_capacity_probe_retirement_unknown");
        Assert.Same(life, host.Provider.LastProbe);
        Assert.Equal(1, attempts);
        Assert.Equal(0, host.ExitPosts);
    }

    [Fact]
    public async Task ConfirmedNoStart407RCertificateBindsExactQueryAndPreservesRecordingFailure()
    {
        using var host = new Host();
        var missing = true;
        var provider = new ProcessStorageCapacityProvider
        {
            ExecutablePath = () => missing ? Path.Combine(host.Root, "missing-probe.exe") : HostExe("Headless")
        };
        var failed = provider.QuerySingleVolumeAsync(host.Videos, default);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => failed);
        Assert.Equal(2, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        var life = provider.LastProbe!;
        Assert.False(life.Started);
        Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Null(life.CleanupFailure);
        Assert.True(provider.HasCleanRetirementEvidence(error, failed, allowConfirmedNoStart: true));
        Assert.False(provider.HasCleanRetirementEvidence(error, failed));
        var copied = Task.FromException<IReadOnlyList<StorageVolumeSample>>(error);
        Assert.Same(error, Assert.Single(copied.Exception!.InnerExceptions));
        Assert.False(provider.HasCleanRetirementEvidence(error, copied, allowConfirmedNoStart: true));
        Assert.False(new ProcessStorageCapacityProvider().HasCleanRetirementEvidence(error, failed, allowConfirmedNoStart: true));

        missing = false;
        var actual = provider.QuerySingleVolumeAsync(host.Videos, default);
        Assert.Single(await actual.WaitAsync(Bound));
        Assert.NotSame(life, provider.LastProbe);
        Assert.True(provider.HasCleanRetirementEvidence(error, failed, allowConfirmedNoStart: true));
        Assert.False(provider.HasCleanRetirementEvidence(error, actual, allowConfirmedNoStart: true));

        missing = true;
        using var recording = new WindowStorageSafety(new CaptureWritePaths(host.Videos, null, null), 1, provider);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await recording.CheckAsync(admission: true));
        await recording.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.NotNull(recording.ProbeRetirementFailure);
        Assert.Equal(WindowStorageSafety.UnavailableCode, Assert.Throws<StorageSafetyException>(recording.EnsureAdmission).Code);
        Assert.False(recording.TryCommit(() => Assert.Fail("Failed capacity cannot publish"), true));
    }
}
