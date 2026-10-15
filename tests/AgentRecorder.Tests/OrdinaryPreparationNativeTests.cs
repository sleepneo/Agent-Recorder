using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OrdinaryPreparationFixture;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryPreparationNativeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualHeldScopeBlocksFormalEngineRegistrationAndOnlyConfirmedRetirementRestoresIt()
    {
        using var f = new OrdinaryPreparationEngineFixture(realFile: true); var bytes = File.ReadAllBytes(f.Media); var anchor = f.Anchor;
        var held = await f.Service(native: true).AcquireAsync(anchor); Assert.Equal(OrdinaryPreparationReason.Prepared, held.Reason);
        try
        {
            Assert.True(Read(f.Engine, f.Media)!.Paths.Single().InUse);
            var error = Assert.Throws<ApiException>(() => f.Create(policy: "rename")); Assert.Equal("OUTPUT_PATH_IN_USE", error.Code);
            Assert.Empty(f.Engine._recs); Assert.Equal(0, f.Store.Registered); Assert.Equal(0, f.Ui.Requests); Assert.Equal(0, f.Capture.Starts);
            Assert.Equal(OriginalFileHoldReason.Observed, (await held.Owner!.ObserveAsync()).Reason); Assert.False(held.Retirement.IsCompleted);
            output.WriteLine("Actual directory-chain/original-leaf handles alive: engine original-lock reservation blocks requested path even with existing on-disk rename policy; no history/backend/confirmation created.");
        }
        finally { if (held.Owner is not null) await Retire(held.Owner); }
        var retired = await held.Retirement; Assert.True(retired.Retired); Assert.True(retired.ReservationReleased); Assert.True(retired.Scope!.Retired);
        Assert.All(retired.Scope.Resources, r => Assert.Equal(OriginalFileCloseOutcome.Closed, r.Outcome));
        Assert.Equal(bytes, File.ReadAllBytes(f.Media)); Assert.Equal(anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Media).Before!.Identity);
        var rec = f.Create(policy: "rename"); Assert.Equal(RecState.pending_confirmation, rec.State); Assert.NotEqual(f.Media, rec.OutputPath);
        Assert.Equal(1, f.Store.Registered); Assert.Equal(1, f.Ui.Requests); Assert.Equal(0, f.Capture.Starts);
        output.WriteLine("All native resources confirmed Closed and exact reservation released: same requested path formal engine create succeeds via original rename policy, unchanged original bytes/128-bit identity, backend never started.");
    }
}
