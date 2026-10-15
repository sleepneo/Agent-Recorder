using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OriginalFileHoldFixture;

namespace AgentRecorder.Tests;

public sealed class OriginalFileHoldLifecycleTests(ITestOutputHelper output)
{
    private sealed class Deadline
    {
        internal readonly TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Arm(TimeSpan budget, CancellationToken token)
        { Assert.Equal(TimeSpan.FromSeconds(3), budget); token.Register(() => Signal.TrySetCanceled(token)); return Signal.Task; }
    }
    [Theory]
    [InlineData("dispatch")][InlineData("open")][InlineData("basic")][InlineData("close")]
    public async Task DeadlineFreezesBeforeActualWorkerExitAndPreservesAdmission(string stage)
    {
        var f = new OriginalFileHoldFixture(); using var blocked = new Phase(); var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = n => { if (n == stage) blocked.Block(); }; if (stage == "close") f.Api.FailIdentity = true;
        var service = f.Service(clock: clock, deadline: deadline.Arm,
            dispatch: stage == "dispatch" ? action => Task.Run(() => { blocked.Block(); action(); }) : null);
        var pending = service.AcquireAsync(f.Anchor); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(OriginalFileHoldReason.TimedOut, result.Reason); Assert.Null(result.Owner);
        Assert.False(result.Retirement.IsCompleted); var count = f.Api.Calls.Count;
        Assert.Equal(OriginalFileHoldReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.Equal(count, f.Api.Calls.Count);
        blocked.Release(); var retirement = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(retirement.Retired);
        Assert.Equal(stage == "close" ? OriginalFileHoldReason.MetadataUnavailable : OriginalFileHoldReason.TimedOut, retirement.PrimaryReason);
        Assert.Equal(stage == "dispatch" ? 0 : 1, f.Api.CloseCalls);
        f.Api.FailIdentity = false; f.Api.Before = null;
        var again = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.Held, again.Reason); Assert.NotNull(again.Owner);
        await Retire(again.Owner!);
        output.WriteLine("3s deadline from before dispatch; result frozen while actual stage=" + stage + "; admission remains busy until native worker and close complete.");
    }
    [Theory]
    [InlineData("open")][InlineData("basic")][InlineData("close")]
    public async Task CancellationFreezesAndLateOpenNeverHandsOffOwner(string stage)
    {
        var f = new OriginalFileHoldFixture(); using var blocked = new Phase(); using var cancellation = new CancellationTokenSource();
        f.Api.Before = n => { if (n == stage) blocked.Block(); }; if (stage == "close") f.Api.FailBasic = true;
        var pending = f.Service().AcquireAsync(f.Anchor, cancellation.Token); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(OriginalFileHoldReason.Cancelled, result.Reason); Assert.Null(result.Owner);
        Assert.False(result.Retirement.IsCompleted); Assert.Equal(OriginalFileHoldReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason);
        blocked.Release(); Assert.True((await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); Assert.Equal(1, f.Api.CloseCalls);
        Assert.Equal(OriginalFileHoldReason.Cancelled, result.Reason); // late completion cannot rewrite it
        f.Api.FailBasic = false; f.Api.Before = null;
        var again = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.Held, again.Reason); await Retire(again.Owner!);
    }
    [Fact]
    public async Task LateTimerCannotPublishAfterMonotonicThreeSecondBoundary()
    {
        var f = new OriginalFileHoldFixture(); using var blocked = new Phase(); var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = n => { if (n == "path") blocked.Block(); }; var pending = f.Service(clock: clock, deadline: deadline.Arm).AcquireAsync(f.Anchor);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(3)); blocked.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(OriginalFileHoldReason.TimedOut, result.Reason); Assert.Null(result.Owner);
        Assert.True((await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); Assert.Equal(1, f.Api.CloseCalls);
        f.Api.Before = null; var recovered = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.Held, recovered.Reason); await Retire(recovered.Owner!);
    }
    [Fact]
    public async Task PreCancelledDoesNotTakeCapacityOrStartAnything()
    {
        var f = new OriginalFileHoldFixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var service = f.Service(); Assert.Equal(OriginalFileHoldReason.Cancelled, (await service.AcquireAsync(f.Anchor, cancel.Token)).Reason);
        Assert.Empty(f.Api.Calls); var good = await service.AcquireAsync(f.Anchor); await Retire(good.Owner!);
    }
    [Fact]
    public async Task SuccessfulRequestCompletionKeepsHostAdmissionAcrossServiceInstances()
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var first = await service.AcquireAsync(f.Anchor); Assert.NotNull(first.Owner);
        var count = f.Api.Calls.Count; Assert.Equal(OriginalFileHoldReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.Equal(count, f.Api.Calls.Count);
        Assert.False(first.Retirement.IsCompleted); await Retire(first.Owner!); var second = await service.AcquireAsync(f.Anchor); await Retire(second.Owner!);
        Assert.Equal(2, f.Api.Opened.Count); Assert.Equal(2, f.Api.CloseCalls);
    }
    [Fact]
    public async Task ProductionDefaultAdmissionIsSharedAndRecoversOnlyAfterOwnerExit()
    {
        var f = new OriginalFileHoldFixture(); f.Api.Path = f.Anchor.Path; f.Api.Id = f.Anchor.Identity;
        var a = new OriginalFileHoldService(Root, f.Api, f.Metadata); var b = new OriginalFileHoldService(Root, f.Api, f.Metadata);
        var first = await a.AcquireAsync(f.Anchor);
        try { Assert.NotNull(first.Owner); Assert.Equal(OriginalFileHoldReason.Busy, (await b.AcquireAsync(f.Anchor)).Reason); }
        finally { if (first.Owner is { } owner) await Retire(owner); }
        var next = await b.AcquireAsync(f.Anchor); await Retire(next.Owner!);
    }
    [Fact]
    public async Task ObserveDisposeCloseAreSerializedAndDisposeNeverWaitsForNative()
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var acquired = await service.AcquireAsync(f.Anchor); var owner = acquired.Owner!;
        using var observe = new Phase(); using var close = new Phase(); f.Api.Before = n => { if (n == "basic") observe.Block(); if (n == "close") close.Block(); };
        var pending = owner.ObserveAsync(); await observe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OriginalFileHoldReason.Busy, (await owner.ObserveAsync()).Reason); var beforeDispose = f.Api.Calls.Count;
        owner.Dispose(); owner.Dispose(); Assert.Equal(0, f.Api.CloseCalls); Assert.False(owner.Retirement.IsCompleted);
        Assert.Equal(OriginalFileHoldReason.Disposed, (await owner.ObserveAsync()).Reason); Assert.Equal(beforeDispose, f.Api.Calls.Count);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
        observe.Release(); Assert.Equal(OriginalFileHoldReason.Disposed, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
        await close.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(owner.Retirement.IsCompleted);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); close.Release();
        Assert.True((await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); Assert.Equal(1, f.Api.CloseCalls);
        Assert.Single(f.Api.Opened); Assert.All(f.Api.Observed, h => Assert.Same(f.Api.Opened[0], h));
        f.Api.Before = null; var recovered = await service.AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.Held, recovered.Reason); await Retire(recovered.Owner!);
    }
    [Theory]
    [InlineData("path")][InlineData("parent")][InlineData("identity")][InlineData("unknown")]
    public async Task FailedReobservationReportsUnavailableAndConservativelyKeepsOriginalObject(string fault)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var owner = (await service.AcquireAsync(f.Anchor)).Owner!;
        var old = await owner.ObserveAsync(); Assert.Equal(OriginalFileHoldReason.Observed, old.Reason);
        if (fault == "path") f.Api.PathValue = _ => @"\\?\C:\moved\Videos\视频.mp4";
        if (fault == "parent") f.Metadata.Read = _ => throw new IOException("missing-parent");
        if (fault == "identity") f.Api.Id = new(1, 2, 3);
        if (fault == "unknown") f.Api.FailBasic = true;
        var changed = await owner.ObserveAsync(); Assert.NotEqual(OriginalFileHoldReason.Observed, changed.Reason); Assert.Null(changed.Snapshot);
        Assert.Equal(f.Anchor.Identity, old.Snapshot!.Identity); Assert.False(owner.Retirement.IsCompleted); Assert.Equal(0, f.Api.CloseCalls); Assert.Single(f.Api.Opened);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); await Retire(owner);
        Assert.Equal(changed.Reason, (await owner.Retirement).PrimaryReason);
        f.Api.PathValue = null; f.Metadata.Read = _ => FileAttributes.Directory; f.Api.FailBasic = false;
        var recovered = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.Held, recovered.Reason); await Retire(recovered.Owner!);
    }
    [Theory]
    [InlineData("failed")][InlineData("unknown")][InlineData("exception")][InlineData("isclosed")][InlineData("invalid_enum")]
    public async Task CloseFailureOrUnknownNeverReleasesCapacityEvenIfSafeHandleSaysClosed(string fault)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var result = await service.AcquireAsync(f.Anchor);
        f.Api.CloseOutcome = fault switch { "failed" or "isclosed" => OriginalFileCloseOutcome.Failed, "invalid_enum" => (OriginalFileCloseOutcome)999, _ => OriginalFileCloseOutcome.Unknown };
        f.Api.ThrowClose = fault == "exception"; f.Api.PretendClosed = fault == "isclosed"; result.Owner!.Dispose();
        var retired = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retired.Retired);
        Assert.Equal(fault is "failed" or "isclosed" ? OriginalFileCloseOutcome.Failed : OriginalFileCloseOutcome.Unknown, retired.Cleanup);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); var calls = f.Api.Calls.Count;
        result.Owner.Dispose(); Assert.Equal(OriginalFileHoldReason.Disposed, (await result.Owner.ObserveAsync()).Reason); Assert.Equal(calls, f.Api.Calls.Count); Assert.Equal(1, f.Api.CloseCalls);
    }
    [Theory]
    [InlineData("open_throw")][InlineData("closed_open")]
    public async Task UnknownOpenOwnershipHasNoUsableOwnerAndCannotInferRetirement(string fault)
    {
        var f = new OriginalFileHoldFixture(); f.Api.ThrowOpen = fault == "open_throw"; f.Api.ClosedOpen = fault == "closed_open"; var service = f.Service();
        var result = await service.AcquireAsync(f.Anchor); Assert.Null(result.Owner); Assert.False((await result.Retirement).Retired);
        Assert.Equal(OriginalFileCloseOutcome.Unknown, (await result.Retirement).Cleanup); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); Assert.Equal(0, f.Api.CloseCalls);
    }
    [Fact]
    public async Task PrimaryObservationFailureAndCleanupFailureAreBothRetained()
    {
        var f = new OriginalFileHoldFixture(); f.Api.FailIdentity = true; f.Api.CloseOutcome = OriginalFileCloseOutcome.Failed; var service = f.Service();
        var result = await service.AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, result.Reason); Assert.Null(result.Owner);
        var cleanup = await result.Retirement; Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, cleanup.PrimaryReason); Assert.Equal(OriginalFileCloseOutcome.Failed, cleanup.Cleanup); Assert.False(cleanup.Retired);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
    }
    [Theory]
    [InlineData("deadline")][InlineData("dispatch")]
    public async Task DispatchInfrastructureFailureBeforeOpenReleasesConfirmedEmptyLifecycle(string fault)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(deadline: fault == "deadline" ? (_, _) => throw new IOException() : null,
            dispatch: fault == "dispatch" ? _ => throw new IOException() : null);
        var result = await service.AcquireAsync(f.Anchor); Assert.Equal(OriginalFileHoldReason.WorkerFailed, result.Reason); Assert.Null(result.Owner);
        Assert.True((await result.Retirement).Retired); Assert.Empty(f.Api.Opened); var next = await f.Service().AcquireAsync(f.Anchor); await Retire(next.Owner!);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OuterAbandonmentAndLateFailedCleanupKeepBothReasonsAndCapacity(bool cancelled)
    {
        var f = new OriginalFileHoldFixture(); using var close = new Phase(); using var token = new CancellationTokenSource();
        var clock = new Clock(); var deadline = new Deadline(); f.Api.FailStandard = true; f.Api.CloseOutcome = OriginalFileCloseOutcome.Failed;
        f.Api.Before = n => { if (n == "close") close.Block(); }; var service = f.Service(clock: clock, deadline: deadline.Arm);
        var pending = service.AcquireAsync(f.Anchor, token.Token); await close.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelled) token.Cancel(); else { clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(result.Owner);
        Assert.Equal(cancelled ? OriginalFileHoldReason.Cancelled : OriginalFileHoldReason.TimedOut, result.Reason);
        Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, result.PrimaryReason); close.Release();
        var retirement = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retirement.Retired);
        Assert.Equal(OriginalFileHoldReason.MetadataUnavailable, retirement.PrimaryReason); Assert.Equal(OriginalFileCloseOutcome.Failed, retirement.Cleanup);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
    }
    [Fact]
    public async Task ClosedManagedWrapperCannotProduceObservationOrProveResourceRetirement()
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var owner = (await service.AcquireAsync(f.Anchor)).Owner!;
        f.Api.Opened[0].Dispose(); var calls = f.Api.Calls.Count;
        Assert.Equal(OriginalFileHoldReason.WorkerFailed, (await owner.ObserveAsync()).Reason); Assert.Equal(calls, f.Api.Calls.Count);
        owner.Dispose(); var retired = await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retired.Retired);
        Assert.Equal(OriginalFileCloseOutcome.Unknown, retired.Cleanup); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason);
        Assert.Equal(0, f.Api.CloseCalls);
    }
}
