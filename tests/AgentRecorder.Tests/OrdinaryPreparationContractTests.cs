using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.OrdinaryPreparationFixture;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

public sealed class OrdinaryPreparationContractTests
{
    public static TheoryData<string> InvalidPaths => OriginalFileHoldContractTests.InvalidPaths;
    [Theory, MemberData(nameof(InvalidPaths))]
    public async Task IllegalInputRejectsBeforeReservationAndNative(string path)
    {
        using var f = new OrdinaryPreparationFixture(); var failed = await f.Service().AcquireAsync(f.Anchor with { Path = path });
        Assert.Equal(OrdinaryPreparationReason.InvalidInput, failed.Reason); Assert.Empty(f.Api.Calls); Assert.True((await failed.Retirement).Retired);
        Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
    }
    [Theory]
    [InlineData("ffmpeg", "none")][InlineData("ffmpeg", "microphone")][InlineData("ffmpeg-av-split", "system_audio")]
    [InlineData("ffmpeg-region-av-split", "microphone")][InlineData("wgc-continuous", "none")][InlineData("wgc-window-av-split", "system_audio")]
    public async Task PreparationWinsAgainstAllOrdinaryVideoModesAndRequestedOrFinalPaths(string backend, string audio)
    {
        using var f = new OrdinaryPreparationFixture(); var held = await f.Service().AcquireAsync(f.Anchor); Assert.NotNull(held.Owner);
        try
        {
            AssertConflict(f.Engine, f.Anchor.Path.ToUpperInvariant(), backend, audio);
            AssertConflict(f.Engine, f.Files.Root + @"\renamed.mp4", backend, audio, f.Anchor.Path);
            Assert.Empty(f.Engine._recs); Assert.Equal(0, OwnerCount(f.Engine));
            var other = Recording(f.Files.Root + @"\不同.mp4", backend, audio); Claim(f.Engine, other, other.OutputPath);
            Assert.Equal(1, OwnerCount(f.Engine)); var snapshot = Read(f.Engine, f.Anchor.Path.ToUpperInvariant(), other.OutputPath)!; Assert.All(snapshot.Paths, p => Assert.True(p.InUse));
        }
        finally { await Retire(held.Owner!); }
        Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse); Assert.Equal(1, OwnerCount(f.Engine));
        var recovered = Recording(f.Anchor.Path, backend, audio); Claim(f.Engine, recovered, recovered.OutputPath); Assert.Equal(2, OwnerCount(f.Engine));
    }
    [Theory]
    [InlineData(RecState.pending_confirmation)][InlineData(RecState.recording)][InlineData(RecState.finalizing)]
    [InlineData(RecState.completed)][InlineData(RecState.failed)][InlineData(RecState.cancelled)]
    public async Task AlreadyRegisteredOwnerWinsRegardlessOfTerminalOrCompletionState(RecState state)
    {
        using var f = new OrdinaryPreparationFixture(); var rec = Recording(f.Anchor.Path); Claim(f.Engine, rec, rec.OutputPath);
        rec.State = state; if (state is RecState.completed or RecState.failed or RecState.cancelled) rec.PublishFinalized();
        var failed = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, failed.Reason);
        Assert.Empty(f.Api.Calls); Assert.Equal(1, OwnerCount(f.Engine)); Assert.True(Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse); Assert.True((await failed.Retirement).Retired);
    }
    [Theory]
    [InlineData(500)][InlineData(501)]
    public async Task FullClaimBoundAndSeparatePreparationCountArePreserved(int count)
    {
        using var f = new OrdinaryPreparationFixture(); var unrelated = f.Files.Root + @"\other.mp4";
        for (var i = 0; i < count; i++) Claim(f.Engine, Recording(unrelated), unrelated);
        var result = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(count, OwnerCount(f.Engine));
        if (count == 501) { Assert.Equal(OrdinaryPreparationReason.Unavailable, result.Reason); Assert.Empty(f.Api.Calls); Assert.Null(Read(f.Engine, f.Anchor.Path)); }
        else
        {
            Assert.Equal(OrdinaryPreparationReason.Prepared, result.Reason); Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
            await Retire(result.Owner!); Assert.Equal(500, OwnerCount(f.Engine)); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        }
    }
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("relative.mp4")][InlineData("C:\\host\\Videos\\..\\bad.mp4")]
    [InlineData("C:\\host\\Videos\\bad\0.mp4")][InlineData("null_owner")]
    public async Task UntrustedImmutableRegistrationFailsClosedBeforeNative(string? path)
    {
        using var f = new OrdinaryPreparationFixture(); var rec = Recording(f.Files.Root + @"\other.mp4"); Claim(f.Engine, rec, rec.OutputPath);
        Corrupt(f.Engine, rec, path, path == "null_owner"); var result = await f.Service().AcquireAsync(f.Anchor);
        Assert.Equal(OrdinaryPreparationReason.Unavailable, result.Reason); Assert.Empty(f.Api.Calls); Assert.Equal(1, OwnerCount(f.Engine)); Assert.True((await result.Retirement).Retired);
    }
    [Fact]
    public async Task MultipleServicesShareEngineAndSnapshotIsImmutableAndPure()
    {
        using var f = new OrdinaryPreparationFixture(); var first = await f.Service().AcquireAsync(f.Anchor); Assert.NotNull(first.Owner);
        var frozen = Read(f.Engine, f.Anchor.Path, f.Files.Root + @"\other.mp4")!; var calls = f.Api.Calls.Count;
        for (var i = 0; i < 3; i++) { Assert.Equal(OrdinaryPreparationReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse); }
        Assert.Equal(calls, f.Api.Calls.Count); Assert.Equal(0, OwnerCount(f.Engine)); await Retire(first.Owner!);
        Assert.True(frozen.Paths[0].InUse); Assert.False(frozen.Paths[1].InUse); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        Assert.Throws<NotSupportedException>(() => ((IList<OrdinaryRuntimeOutputUse>)frozen.Paths)[0] = new(f.Anchor.Path, false));
    }
    [Fact]
    public async Task ActualOutputLockTimeoutAndDeterministicRegistrationWinnerNeverStartNative()
    {
        using var f = new OrdinaryPreparationFixture(); using var locked = new Phase();
        var holder = Task.Run(() => { lock (OutputLock(f.Engine)) locked.Block(); }); await locked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { var result = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OrdinaryPreparationReason.Unavailable, result.Reason); Assert.Empty(f.Api.Calls); }
        finally { locked.Release(); await holder; }
        using var dispatched = new Phase(); var pending = f.Service(dispatch: work => Task.Run(() => { dispatched.Block(); work(); })).AcquireAsync(f.Anchor);
        await dispatched.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var rec = Recording(f.Anchor.Path); Claim(f.Engine, rec, rec.OutputPath); dispatched.Release();
        Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, (await pending).Reason); Assert.Empty(f.Api.Calls); Assert.Equal(1, OwnerCount(f.Engine));
    }
    [Fact]
    public async Task NativeWorkHoldsNoEngineRecordingOrOutputBusinessLock()
    {
        using var f = new OrdinaryPreparationFixture(); using var block = new Phase(); f.Api.Before = c => { if (c.Stage == "open" && c.Index == 0) block.Block(); };
        var pending = f.Service().AcquireAsync(f.Anchor); await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = await Task.Run(() => Read(f.Engine, f.Anchor.Path)).WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(read!.Paths.Single().InUse);
        var other = Recording(f.Files.Root + @"\other.mp4"); await Task.Run(() => Claim(f.Engine, other, other.OutputPath)).WaitAsync(TimeSpan.FromSeconds(5));
        block.Release(); var held = await pending; await Retire(held.Owner!);
    }
}
