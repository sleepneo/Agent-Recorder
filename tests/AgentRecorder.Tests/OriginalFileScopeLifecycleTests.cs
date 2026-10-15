using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.OriginalFileScopeFixture;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;
using Clock = AgentRecorder.Tests.OriginalFileHoldFixture.Clock;

namespace AgentRecorder.Tests;

public sealed class OriginalFileScopeLifecycleTests
{
    public static IEnumerable<object[]> BlockedStages => new[] { new object[] { "dispatch", -1 } }
        .Concat(from index in Enumerable.Range(0, 4) from stage in new[] { "open", "verify", "close" } select new object[] { stage, index });
    [Theory, MemberData(nameof(BlockedStages))]
    public async Task OneAbsoluteDeadlineFreezesButKeepsAllResourcesUntilTrueWorkerAndClosesExit(string stage, int layer)
    {
        await Abandon(stage, layer, false);
    }
    [Theory, MemberData(nameof(BlockedStages))]
    public async Task CancellationNeverHandsOffLateOpenAndBusyDoesNoIo(string stage, int layer)
    {
        await Abandon(stage, layer, true);
    }
    private static async Task Abandon(string stage, int layer, bool cancel)
    {
        var f = new OriginalFileScopeFixture(); using var blocked = new Phase(); using var token = new CancellationTokenSource();
        var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = c => { if (c.Index == layer && (c.Stage == stage || stage == "verify" && c.Stage == "basic" && (layer == 3 || c.Number == 2))) blocked.Block(); };
        if (stage == "close") f.Api.Fail = c => c.Index == 3 && c.Stage == "basic";
        var service = f.Service(clock, deadline.Arm, stage == "dispatch" ? work => Task.Run(() => { blocked.Block(); work(); }) : null);
        var pending = service.AcquireAsync(f.Anchor, token.Token); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) token.Cancel(); else { clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(result.Owner);
        Assert.Equal(cancel ? OriginalFileHoldReason.Cancelled : OriginalFileHoldReason.TimedOut, result.Reason);
        Assert.Equal(stage == "close" ? OriginalFileHoldReason.MetadataUnavailable : result.Reason, result.PrimaryReason);
        Assert.False(result.Retirement.IsCompleted); Assert.Equal(1, deadline.Arms); var calls = f.Api.Calls.Count;
        Assert.Equal(OriginalFileHoldReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.Equal(calls, f.Api.Calls.Count);
        blocked.Release(); var retired = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(retired.Retired);
        AssertClosedInReverse(f.Api); Assert.Equal(result.PrimaryReason, retired.PrimaryReason);
        f.Api.Before = null; f.Api.Fail = null; var recovered = await f.Service().AcquireAsync(f.Anchor); await Retire(recovered.Owner!);
    }
    [Fact]
    public async Task BudgetIsNotRestartedAtEachDirectoryOrLeafAndDoesNotDependOnTimerScheduling()
    {
        var f = new OriginalFileScopeFixture(); var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = c => { if (c.Stage == "open") clock.Advance(TimeSpan.FromSeconds(1)); };
        var result = await f.Service(clock, deadline.Arm).AcquireAsync(f.Anchor);
        Assert.Equal(OriginalFileHoldReason.TimedOut, result.Reason); Assert.Null(result.Owner);
        Assert.Equal(3, f.Api.Opened.Count); Assert.DoesNotContain(f.Api.Opened, n => n.Leaf); Assert.Equal(1, deadline.Arms);
        Assert.True((await result.Retirement).Retired); AssertClosedInReverse(f.Api);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task ObserveDisposeAndEveryCloseRemainSerializedOnExactHandles(int observeLayer)
    {
        var f = new OriginalFileScopeFixture(); var service = f.Service(); var held = await service.AcquireAsync(f.Anchor); var owner = held.Owner!;
        using var observe = new Phase(); using var close = new Phase();
        f.Api.Before = c => { if (c.Stage == "basic" && c.Index == observeLayer) observe.Block(); if (c.Stage == "close" && c.Index == 2) close.Block(); };
        var pending = owner.ObserveAsync(); await observe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OriginalFileHoldReason.Busy, (await owner.ObserveAsync()).Reason); owner.Dispose(); owner.Dispose();
        Assert.DoesNotContain(f.Api.Calls, c => c.Stage == "close"); Assert.False(held.Retirement.IsCompleted);
        Assert.Equal(OriginalFileHoldReason.Disposed, (await owner.ObserveAsync()).Reason); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
        observe.Release(); Assert.Equal(OriginalFileHoldReason.Disposed, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
        await close.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(held.Retirement.IsCompleted);
        Assert.Equal(new[] { 3, 2 }, f.Api.Calls.Where(c => c.Stage == "close").Select(c => c.Index));
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); close.Release();
        Assert.True((await held.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); AssertClosedInReverse(f.Api);
    }
    public static IEnumerable<object[]> CloseFaults => from index in Enumerable.Range(0, 4)
        from fault in new[] { "failed", "unknown", "throw", "closed", "invalid_enum" } select new object[] { index, fault };
    [Theory, MemberData(nameof(CloseFaults))]
    public async Task OneBadCloseCannotSkipOtherResourcesOrReleaseSharedHostCapacity(int layer, string fault)
    {
        var f = new OriginalFileScopeFixture(); var service = f.Service(); var held = await service.AcquireAsync(f.Anchor);
        f.Api.CloseValue = n => n.Index != layer ? OriginalFileCloseOutcome.Closed : fault switch
        { "failed" => OriginalFileCloseOutcome.Failed, "throw" => throw new IOException(), "invalid_enum" => (OriginalFileCloseOutcome)99, _ => OriginalFileCloseOutcome.Unknown };
        if (fault == "closed") f.Api.Opened.ElementAt(layer).Handle.Dispose();
        held.Owner!.Dispose(); var retired = await held.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retired.Retired);
        Assert.Equal(4, retired.Resources.Count); Assert.Equal(new[] { 3, 2, 1, 0 }, retired.Resources.Select(r => r.Leaf ? 3 : r.DirectoryIndex));
        Assert.Equal(fault == "failed" ? OriginalFileCloseOutcome.Failed : OriginalFileCloseOutcome.Unknown, retired.Resources.Single(r => (r.Leaf ? 3 : r.DirectoryIndex) == layer).Outcome);
        Assert.All(retired.Resources.Where(r => (r.Leaf ? 3 : r.DirectoryIndex) != layer), r => Assert.Equal(OriginalFileCloseOutcome.Closed, r.Outcome));
        var count = f.Api.Calls.Count; Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
        held.Owner.Dispose(); Assert.Equal(count, f.Api.Calls.Count); // no numeric retry
    }
    [Fact]
    public async Task MultipleCloseFailuresAndPrimaryMetadataFaultAreIndependentlyRecorded()
    {
        var f = new OriginalFileScopeFixture(); f.Api.Fail = c => c.Stage == "identity" && c.Index == 3;
        f.Api.CloseValue = n => n.Index switch { 3 => OriginalFileCloseOutcome.Failed, 2 => throw new IOException(), 0 => OriginalFileCloseOutcome.Failed, _ => OriginalFileCloseOutcome.Closed };
        var service = f.Service(); var failed = await service.AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, failed.Reason);
        var retired = await failed.Retirement; Assert.False(retired.Retired); Assert.Equal(failed.Reason, retired.PrimaryReason);
        Assert.Equal(new[] { OriginalFileCloseOutcome.Failed, OriginalFileCloseOutcome.Unknown, OriginalFileCloseOutcome.Closed, OriginalFileCloseOutcome.Failed }, retired.Resources.Select(r => r.Outcome));
        AssertClosedInReverse(f.Api); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AbandonedResponseKeepsPrimaryAndEveryLateCloseFaultAndNeverReturnsCapacity(bool cancel)
    {
        var f = new OriginalFileScopeFixture(); using var blocked = new Phase(); using var token = new CancellationTokenSource();
        var clock = new Clock(); var deadline = new Deadline();
        f.Api.Fail = c => c.Stage == "basic" && c.Index == 3;
        f.Api.Before = c => { if (c.Stage == "close" && c.Index == 3) blocked.Block(); };
        f.Api.CloseValue = n => n.Index switch { 3 => throw new IOException(), 2 => OriginalFileCloseOutcome.Failed, _ => OriginalFileCloseOutcome.Closed };
        var service = f.Service(clock, deadline.Arm); var pending = service.AcquireAsync(f.Anchor, token.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) token.Cancel(); else { clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult(); }
        var result = await pending; Assert.Equal(cancel ? OriginalFileHoldReason.Cancelled : OriginalFileHoldReason.TimedOut, result.Reason);
        Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, result.PrimaryReason); Assert.False(result.Retirement.IsCompleted);
        blocked.Release(); var retired = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retired.Retired);
        Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, retired.PrimaryReason);
        Assert.Equal(new[] { OriginalFileCloseOutcome.Unknown, OriginalFileCloseOutcome.Failed, OriginalFileCloseOutcome.Closed, OriginalFileCloseOutcome.Closed }, retired.Resources.Select(r => r.Outcome));
        AssertClosedInReverse(f.Api); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
    }
    public static IEnumerable<object[]> UnknownOpens => from index in Enumerable.Range(0, 4) from fault in new[] { "null", "closed" } select new object[] { index, fault };
    [Theory, MemberData(nameof(UnknownOpens))]
    public async Task UnknownOpenOwnershipKeepsAdmissionAndStillClosesOtherKnownDirectories(int index, string fault)
    {
        var f = new OriginalFileScopeFixture(); if (fault == "null") f.Api.NullOpen = index; else f.Api.ClosedOpen = index;
        var service = f.Service(); var result = await service.AcquireAsync(f.Anchor); Assert.Null(result.Owner); var retired = await result.Retirement;
        Assert.False(retired.Retired); Assert.Equal(index + 1, retired.Resources.Count);
        Assert.Equal(OriginalFileCloseOutcome.Unknown, retired.Resources[0].Outcome); Assert.Equal(index, f.Api.Calls.Count(c => c.Stage == "close"));
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LeafAndScopeShareOneAdmissionWithoutWaitingOrOpeningDirectoriesForBusyLeaf(bool defaultHost)
    {
        var f = new OriginalFileScopeFixture(); var old = new OriginalFileHoldFixture(); old.Api.Id = old.Anchor.Identity; old.Api.Path = old.Anchor.Path;
        var admission = defaultHost ? null : f.Admission;
        var leaf = new OriginalFileHoldService(OriginalFileHoldFixture.Root, old.Api, old.Metadata, admission);
        var scope = new OriginalFileScopeHoldService(f.Root, f.Api, admission); f.Api.Anchor = f.Anchor;
        var heldLeaf = await leaf.AcquireAsync(old.Anchor);
        try { Assert.NotNull(heldLeaf.Owner); Assert.Equal(OriginalFileHoldReason.Busy, (await scope.AcquireAsync(f.Anchor)).Reason); Assert.Empty(f.Api.Calls); }
        finally { if (heldLeaf.Owner is not null) await OriginalFileHoldFixture.Retire(heldLeaf.Owner); }
        var heldScope = await scope.AcquireAsync(f.Anchor);
        try { Assert.NotNull(heldScope.Owner); var count = old.Api.Calls.Count; Assert.Equal(OriginalFileHoldReason.Busy, (await leaf.AcquireAsync(old.Anchor)).Reason); Assert.Equal(count, old.Api.Calls.Count); }
        finally { if (heldScope.Owner is not null) await Retire(heldScope.Owner); }
    }
    [Fact]
    public async Task PreCancelledAndDispatchFailureHaveEmptyConfirmedRetirementAndRecover()
    {
        var f = new OriginalFileScopeFixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = await f.Service().AcquireAsync(f.Anchor, cts.Token); Assert.Equal(OriginalFileHoldReason.Cancelled, cancelled.Reason); Assert.Empty(f.Api.Calls);
        var failed = await f.Service(dispatch: _ => throw new IOException()).AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.WorkerFailed, failed.Reason);
        Assert.True((await failed.Retirement).Retired); Assert.Empty((await failed.Retirement).Resources); Assert.Empty(f.Api.Calls);
        var good = await f.Service().AcquireAsync(f.Anchor); await Retire(good.Owner!);
    }
}
