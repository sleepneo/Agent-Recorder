using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.OrdinaryPreparationFixture;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryPreparationEngineTests
{
    [Fact]
    public async Task FormalInitialConflictLeavesNoRecordingHistoryBackendOrConfirmationAndDifferentPathIsUnaffected()
    {
        using var f = new OrdinaryPreparationEngineFixture(); var held = await f.Service().AcquireAsync(f.Anchor);
        try
        {
            var error = Assert.Throws<ApiException>(() => f.Create()); Assert.Equal("OUTPUT_PATH_IN_USE", error.Code); Assert.Equal(409, error.Status);
            Assert.Empty(f.Engine._recs); Assert.Empty(f.Engine._confs); Assert.Equal(0, f.Store.Registered); Assert.Equal(0, f.Ui.Requests); Assert.Equal(0, f.Capture.Starts); Assert.Equal(0, OwnerCount(f.Engine));
            var other = f.Create(name: "不同.mp4"); Assert.Equal(RecState.pending_confirmation, other.State); Assert.Equal(1, f.Store.Registered); Assert.Equal(1, f.Ui.Requests); Assert.Equal(0, f.Capture.Starts);
        }
        finally { await Retire(held.Owner!); }
    }
    [Fact]
    public async Task FormalInitialRegistrationWinsAndPreparationCannotRetireTerminalClaim()
    {
        using var f = new OrdinaryPreparationEngineFixture(); var rec = f.Create(); f.Engine.Stop(rec.Id, "test_stop"); Assert.True(rec.IsFinalized);
        var result = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, result.Reason);
        Assert.Empty(f.Api.Calls); Assert.Equal(1, OwnerCount(f.Engine)); Assert.True(Read(f.Engine, f.Media)!.Paths.Single().InUse);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ApprovalReplacementCompetesAtOriginalLockAndLoserKeepsFrozenOldPath(bool preparationFirst)
    {
        using var f = new OrdinaryPreparationEngineFixture(); var oldDir = Path.Combine(f.Root, "Old"); var rec = f.Create(oldDir); var old = rec.OutputPath;
        if (preparationFirst)
        {
            var held = await f.Service().AcquireAsync(f.Anchor);
            try
            {
                f.Ui.Confirm!(ConfirmationDecision.Approve(f.Videos)); Assert.Equal(RecState.rejected, rec.State); Assert.Equal("OUTPUT_PATH_IN_USE", rec.StopReason);
                Assert.Equal(old, rec.OutputPath); Assert.Equal(old, rec.Config.OutputPath); Assert.Equal(0, f.Capture.Starts); Assert.Equal(1, OwnerCount(f.Engine));
                Assert.True(Read(f.Engine, old, f.Media)!.Paths.All(p => p.InUse)); Assert.Equal("OUTPUT_PATH_IN_USE", f.Store.Read(rec.Id)!.ReasonCode);
            }
            finally { await Retire(held.Owner!); }
            Assert.True(Read(f.Engine, old)!.Paths.Single().InUse);
        }
        else
        {
            using var beforeReserve = new Phase();
            var service = f.Engine.CreateRetentionPreparationService(f.Videos, f.Api, f.Admission,
                dispatch: action => Task.Run(() => { beforeReserve.Block(); action(); }));
            var pending = service.AcquireAsync(f.Anchor); await beforeReserve.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Ui.Confirm!(ConfirmationDecision.Approve(f.Videos)); Assert.Equal(f.Media, rec.OutputPath); beforeReserve.Release();
            Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, (await pending).Reason); Assert.Empty(f.Api.Calls);
            // The successful approval may reach our throwing fake backend. Only
            // the losing approval branch above must have zero Start calls.
            Assert.InRange(f.Capture.Starts, 0, 1);
            Assert.False(Read(f.Engine, old)!.Paths.Single().InUse); Assert.True(Read(f.Engine, f.Media)!.Paths.Single().InUse);
            f.Engine.Stop(rec.Id, "test_stop");
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ExistingFileRenamePolicyCannotBypassPreparationRequestedPathOnCreateOrApproval(bool approval)
    {
        using var f = new OrdinaryPreparationEngineFixture(); File.WriteAllBytes(f.Media, new byte[12]); // independent resolver sees an existing owned placeholder
        Recording? rec = approval ? f.Create(Path.Combine(f.Root, "Old")) : null;
        var held = await f.Service().AcquireAsync(f.Anchor);
        try
        {
            if (approval)
            {
                var old = rec!.OutputPath; f.Ui.Confirm!(ConfirmationDecision.Approve(f.Videos)); Assert.Equal(RecState.rejected, rec.State);
                Assert.Equal("OUTPUT_PATH_IN_USE", rec.StopReason); Assert.Equal(old, rec.OutputPath); Assert.Equal(1, OwnerCount(f.Engine));
            }
            else
            {
                var e = Assert.Throws<ApiException>(() => f.Create(policy: "rename")); Assert.Equal("OUTPUT_PATH_IN_USE", e.Code);
                Assert.Empty(f.Engine._recs); Assert.Equal(0, f.Store.Registered); Assert.Equal(0, f.Ui.Requests);
            }
            Assert.Equal(0, f.Capture.Starts);
        }
        finally { await Retire(held.Owner!); }
    }
    [Fact]
    public void WithoutPreparationExistingAvOwnerKeepsOriginalIndependentOnDiskRenameAdmission()
    {
        using var f = new OrdinaryPreparationEngineFixture(); File.WriteAllBytes(f.Media, new byte[12]);
        var prior = Recording(f.Media, "ffmpeg-av-split", "microphone"); Assert.True(RecordingStoragePolicy.IsOrdinaryAv(prior));
        OrdinaryPreparationFixture.Claim(f.Engine, prior, prior.OutputPath);
        var rec = f.Create(policy: "rename"); Assert.Equal(RecState.pending_confirmation, rec.State); Assert.NotEqual(prior.OutputPath, rec.OutputPath);
        Assert.Equal(2, OwnerCount(f.Engine)); Assert.Equal(0, f.Capture.Starts); Assert.Empty(f.Api.Calls);
    }
}
