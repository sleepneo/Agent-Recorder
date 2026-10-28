using System.Net;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ShutdownResourceOwnerTests
{
    [Fact]
    public async Task PendingDeletionIsNotCancelledAndIdleExitWaitsForActualRetirement()
    {
        using var fixture = new DeletionRequestFixture();
        int exits = 0;
        fixture.Server.ConfigureHostShutdown(() => fixture.Coordinator.HostShutdownBusyReason, () => Interlocked.Increment(ref exits));
        var id = await fixture.Create();
        await fixture.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("deletion_in_flight", fixture.Coordinator.HostShutdownBusyReason);
        using var busy = await fixture.Client.PostAsync("/api/v1/host/shutdown", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(0, exits);
        Assert.Equal(0, fixture.F.Native.Calls);
        Assert.False(fixture.F.Engine.HostAdmission.IsClosed);
        fixture.F.Ui.Decide(LocalRetentionUiChoice.Reject);
        fixture.F.Ui.Retire.TrySetResult(true);
        await fixture.Finish(id);
        Assert.Null(fixture.Coordinator.HostShutdownBusyReason);
        using var accepted = await fixture.Client.PostAsync("/api/v1/host/shutdown", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(0, fixture.F.Native.Calls);
    }

    [Fact]
    public async Task HealthProbeRetainsAdmissionAfterDeadlineUntilNativeWorkerReallyReturns()
    {
        var admission = new HostWorkAdmission();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = ObservationHealthGatewayTests.Probes();
        var probes = new ObservationHealthProbes { Ffmpeg = () => { entered.Set(); release.Wait(); return original.Ffmpeg(); },
            Ffprobe = original.Ffprobe, FfmpegSource = original.FfmpegSource, FileExists = original.FileExists,
            WgcHelper = original.WgcHelper, AudioHelper = original.AudioHelper, Desktop = original.Desktop };
        var gateway = new ObservationHealthGateway(ObservationHealthGatewayTests.Owner, probes,
            _ => Task.FromResult(ObservationHealthGatewayTests.Owner()))
            { HostAdmission = admission, DeadlineForTests = _ => deadline.Task };
        try
        {
            var request = gateway.ReadAsync(default);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            deadline.TrySetResult();
            Assert.Equal(HealthProbeState.Timeout, (await request).ProbeState);
            Assert.Equal("health_worker_in_flight", admission.TryClose(() => null));
            Assert.Equal(HealthProbeState.Busy, (await gateway.ReadAsync(default)).ProbeState);
            release.Set();
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => admission.TryClose(() => null) is null, TimeSpan.FromSeconds(3))));
            Assert.Equal(HealthProbeState.Busy, (await gateway.SampleAsync(ObservationHealthGatewayTests.Owner(), default)).ProbeState);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void ReadOnlyContentQuotaReportsRealWorkIndependentlyOfRequestLifetime()
    {
        Assert.True(OrdinaryContentVerificationAdmission.TryAcquire());
        try { Assert.Equal("content_verification_worker_pending", HostReadOnlyWorkerState.BusyReason); }
        finally { OrdinaryContentVerificationAdmission.Release(); }
        Assert.Null(HostReadOnlyWorkerState.BusyReason);
    }
}
