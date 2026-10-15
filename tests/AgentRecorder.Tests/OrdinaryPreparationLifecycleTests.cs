using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.OrdinaryPreparationFixture;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;
using Clock = AgentRecorder.Tests.OriginalFileHoldFixture.Clock;
using Deadline = AgentRecorder.Tests.OriginalFileScopeFixture.Deadline;

namespace AgentRecorder.Tests;

public sealed class OrdinaryPreparationLifecycleTests
{
    public static IEnumerable<object[]> Stages => from index in Enumerable.Range(0, 4)
        from stage in new[] { "open", "verify", "close" } from cancel in new[] { false, true } select new object[] { index, stage, cancel };
    [Theory, MemberData(nameof(Stages))]
    public async Task CancellationOrOneTotalDeadlineKeepsRuntimeReservationUntilEveryActualScopeResourceExits(int index, string stage, bool cancel)
    {
        using var f = new OrdinaryPreparationFixture(); using var block = new Phase(); using var cts = new CancellationTokenSource();
        var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = c => { if (c.Index == index && (c.Stage == stage || stage == "verify" && c.Stage == "basic" && (index == 3 || c.Number == 2))) block.Block(); };
        if (stage == "close") f.Api.Fail = c => c.Stage == "basic" && c.Index == 3;
        var pending = f.Service(clock, deadline.Arm).AcquireAsync(f.Anchor, cts.Token); await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        if (cancel) cts.Cancel(); else { clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult(); }
        var abandoned = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(abandoned.Owner);
        Assert.Equal(cancel ? OrdinaryPreparationReason.Cancelled : OrdinaryPreparationReason.TimedOut, abandoned.Reason);
        Assert.False(abandoned.Retirement.IsCompleted); AssertConflict(f.Engine, f.Anchor.Path);
        var count = f.Api.Calls.Count; Assert.Equal(OrdinaryPreparationReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.Equal(count, f.Api.Calls.Count);
        block.Release(); var retired = await abandoned.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(retired.Retired); Assert.True(retired.ReservationReleased);
        Assert.True(retired.Scope!.Retired); OriginalFileScopeFixture.AssertClosedInReverse(f.Api); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        Claim(f.Engine, Recording(f.Anchor.Path), f.Anchor.Path);
    }
    [Fact]
    public async Task DispatchAndReservationTimeAreIncludedInAbsoluteScopeBudget()
    {
        using var f = new OrdinaryPreparationFixture(); using var dispatch = new Phase(); var clock = new Clock(); var deadline = new Deadline();
        f.Api.Before = c => { if (c.Stage == "open") clock.Advance(TimeSpan.FromSeconds(1)); };
        var pending = f.Service(clock, deadline.Arm, work => Task.Run(() => { dispatch.Block(); work(); })).AcquireAsync(f.Anchor);
        await dispatch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(2)); dispatch.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(OrdinaryPreparationReason.TimedOut, result.Reason);
        Assert.True((await result.Retirement).Retired); Assert.Single(f.Api.Opened); Assert.Equal(1, deadline.Arms);
        Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
    }
    [Fact]
    public async Task ExpiredDispatchNeverBooksOrEntersNativeAndPrecancelledIsEmpty()
    {
        using var f = new OrdinaryPreparationFixture(); using var dispatch = new Phase(); var clock = new Clock(); var deadline = new Deadline();
        var pending = f.Service(clock, deadline.Arm, action => Task.Run(() => { dispatch.Block(); action(); })).AcquireAsync(f.Anchor);
        await dispatch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult();
        var result = await pending; Assert.Equal(OrdinaryPreparationReason.TimedOut, result.Reason); Assert.False(result.Retirement.IsCompleted); dispatch.Release();
        Assert.True((await result.Retirement).Retired); Assert.Empty(f.Api.Calls); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Equal(OrdinaryPreparationReason.Cancelled, (await f.Service().AcquireAsync(f.Anchor, cts.Token)).Reason); Assert.Empty(f.Api.Calls);
    }
    [Theory]
    [InlineData("busy")][InlineData("unsupported")][InlineData("metadata")]
    public async Task ConfirmedEmptyOrFailedScopeReleasesOnlyThisReservation(string fault)
    {
        using var f = new OrdinaryPreparationFixture(); object? blocker = null;
        if (fault == "busy") { blocker = new object(); Assert.True(f.Files.Admission.Claim(blocker)); }
        if (fault == "unsupported") f.Api.Platform = false;
        if (fault == "metadata") f.Api.Fail = c => c.Stage == "identity" && c.Index == 2;
        var result = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OrdinaryPreparationReason.ScopeUnavailable, result.Reason);
        Assert.Null(result.Owner); Assert.True((await result.Retirement).Retired); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        if (blocker is not null) f.Files.Admission.Release(blocker);
        f.Api.Platform = true; f.Api.Fail = null; var next = await f.Service().AcquireAsync(f.Anchor); await Retire(next.Owner!);
    }
    public static IEnumerable<object[]> BadCloses => from index in Enumerable.Range(0, 4)
        from fault in new[] { "failed", "unknown", "throw", "closed" } select new object[] { index, fault };
    [Theory, MemberData(nameof(BadCloses))]
    public async Task UnknownOrFailedSpecificCloseKeepsLogicalReservationEvenAfterAllOtherHandlesClose(int index, string fault)
    {
        using var f = new OrdinaryPreparationFixture(); var held = await f.Service().AcquireAsync(f.Anchor);
        f.Api.CloseValue = n => n.Index != index ? OriginalFileCloseOutcome.Closed : fault switch
        { "failed" => OriginalFileCloseOutcome.Failed, "throw" => throw new IOException(), _ => OriginalFileCloseOutcome.Unknown };
        if (fault == "closed") f.Api.Opened.ElementAt(index).Handle.Dispose();
        held.Owner!.Dispose(); var retired = await held.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(retired.Retired); Assert.False(retired.ReservationReleased);
        Assert.False(retired.Scope!.Retired); Assert.Equal(4, retired.Scope.Resources.Count); AssertConflict(f.Engine, f.Anchor.Path);
        Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse); Assert.Equal(OrdinaryPreparationReason.Busy, (await f.Service().AcquireAsync(f.Anchor)).Reason);
        var calls = f.Api.Calls.Count; held.Owner.Dispose(); Assert.Equal(calls, f.Api.Calls.Count);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ReobservationExitAndShutdownAreSerializedAndKeepPathUntilRealExit(bool shutdown)
    {
        using var f = new OrdinaryPreparationFixture(); var held = await f.Service().AcquireAsync(f.Anchor); using var observe = new Phase(); using var close = new Phase();
        f.Api.Before = c => { if (c.Stage == "basic" && c.Index == 1) observe.Block(); if (c.Stage == "close" && c.Index == 3) close.Block(); };
        var pending = held.Owner!.ObserveAsync(); await observe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OriginalFileHoldReason.Busy, (await held.Owner.ObserveAsync()).Reason);
        if (shutdown) f.Engine.BeginHostShutdown("test_shutdown"); else held.Owner.Dispose();
        Assert.DoesNotContain(f.Api.Calls, c => c.Stage == "close"); Assert.False(held.Retirement.IsCompleted); AssertConflict(f.Engine, f.Anchor.Path);
        observe.Release(); Assert.Equal(OriginalFileHoldReason.Disposed, (await pending).Reason); await close.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(held.Retirement.IsCompleted); AssertConflict(f.Engine, f.Anchor.Path); close.Release(); Assert.True((await held.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired);
        if (shutdown) Assert.Equal(OrdinaryPreparationReason.ShuttingDown, (await f.Service().AcquireAsync(f.Anchor)).Reason);
        else Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
    }
    [Fact]
    public async Task FailedReobservationKeepsReservationAndSuppliesNoOldSnapshot()
    {
        using var f = new OrdinaryPreparationFixture(); var held = await f.Service().AcquireAsync(f.Anchor);
        f.Api.PathValue = (n, _, path) => n.Index == 1 ? @"\\?\C:\replacement" : path;
        var result = await held.Owner!.ObserveAsync(); Assert.Equal(OriginalFileHoldReason.PathMismatch, result.Reason); Assert.Null(result.Snapshot);
        Assert.False(held.Retirement.IsCompleted); AssertConflict(f.Engine, f.Anchor.Path); await Retire(held.Owner);
    }
    [Fact]
    public async Task CleanupOutputLockTimeoutRetainsRootedReservationAndControlledWorkerReleasesAfterLockReturns()
    {
        using var f = new OrdinaryPreparationFixture(); var held = await f.Service().AcquireAsync(f.Anchor); using var locked = new Phase();
        var holder = Task.Run(() => { lock (OutputLock(f.Engine)) locked.Block(); }); await locked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            held.Owner!.Dispose(); Assert.Equal(OrdinaryPreparationReleaseAttempt.LockUnavailable, await held.Owner.ReleaseAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(held.Retirement.IsCompleted); Assert.All(f.Api.Opened, n => Assert.True(n.Handle.IsClosed));
        }
        finally { locked.Release(); await holder; }
        Assert.True((await held.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        Claim(f.Engine, Recording(f.Anchor.Path), f.Anchor.Path);
    }
    [Fact]
    public async Task ShutdownDuringAcquisitionRequestsLateScopeCleanupAndStopAllAloneNeverReleasesEarly()
    {
        using var f = new OrdinaryPreparationFixture(); using var opening = new Phase();
        f.Api.Before = c => { if (c.Stage == "open" && c.Index == 0) opening.Block(); };
        var pending = f.Service().AcquireAsync(f.Anchor); await opening.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Engine.StopAllSync("test_stop_all"); Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse); AssertConflict(f.Engine, f.Anchor.Path);
        f.Engine.BeginHostShutdown("test_shutdown"); Assert.Equal(OrdinaryPreparationReason.ShuttingDown, (await f.Service().AcquireAsync(f.Anchor)).Reason);
        opening.Release(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(result.Owner);
        Assert.True((await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); Assert.Single(f.Api.Opened); OriginalFileScopeFixture.AssertClosedInReverse(f.Api);
    }
    [Fact]
    public async Task ExactSessionReleaseCannotRemoveANewerReservationOrOrdinaryOwner()
    {
        using var f = new OrdinaryPreparationFixture(); var first = await f.Service().AcquireAsync(f.Anchor);
        var field = typeof(RecordingEngine).GetField("_ordinaryRetentionPreparation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var original = (OrdinaryRetentionPreparationSession)field.GetValue(f.Engine)!; await Retire(first.Owner!);
        var second = await f.Service().AcquireAsync(f.Anchor);
        try
        {
            var other = Recording(f.Files.Root + @"\other.mp4"); Claim(f.Engine, other, other.OutputPath);
            Assert.False(f.Engine.TryReleaseOrdinaryRetention(original)); Assert.True(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
            Assert.Equal(1, OwnerCount(f.Engine)); AssertConflict(f.Engine, f.Anchor.Path);
        }
        finally { await Retire(second.Owner!); }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LateSuccessfulScopeNeverHandsOffAndKeepsBookingUntilHandoffWorkerActuallyExits(bool cancel)
    {
        using var f = new OrdinaryPreparationFixture(); using var handoff = new Phase(); using var token = new CancellationTokenSource();
        var clock = new Clock(); var deadline = new Deadline(); var dispatches = 0;
        var pending = f.Service(clock, deadline.Arm, action => Task.Run(() => { if (Interlocked.Increment(ref dispatches) == 2) handoff.Block(); action(); })).AcquireAsync(f.Anchor, token.Token);
        await handoff.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, f.Api.Opened.Count); Assert.All(f.Api.Opened, n => Assert.False(n.Handle.IsClosed));
        if (cancel) token.Cancel(); else { clock.Advance(TimeSpan.FromSeconds(3)); deadline.Signal.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(result.Owner); Assert.Equal(cancel ? OrdinaryPreparationReason.Cancelled : OrdinaryPreparationReason.TimedOut, result.Reason);
        Assert.False(result.Retirement.IsCompleted); AssertConflict(f.Engine, f.Anchor.Path); handoff.Release();
        Assert.True((await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); OriginalFileScopeFixture.AssertClosedInReverse(f.Api);
        Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
    }
    [Fact]
    public async Task BusyActualScopeRetainsItsOwnResourcesWhileRejectedPreparationReleasesOnlyBooking()
    {
        using var f = new OrdinaryPreparationFixture(); var scope = await f.Files.Service().AcquireAsync(f.Anchor); var calls = f.Api.Calls.Count;
        try
        {
            var result = await f.Service().AcquireAsync(f.Anchor); Assert.Equal(OrdinaryPreparationReason.ScopeUnavailable, result.Reason); Assert.Equal(OriginalFileHoldReason.Busy, result.ScopeReason);
            Assert.True((await result.Retirement).Retired); Assert.False(scope.Retirement.IsCompleted); Assert.Equal(calls, f.Api.Calls.Count);
            Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
        }
        finally { await OriginalFileScopeFixture.Retire(scope.Owner!); }
    }
    [Theory]
    [InlineData("deadline")][InlineData("dispatch")][InlineData("handoff")]
    public async Task InfrastructureFaultPreservesActualScopeRetirementDetailAndReleasesOnlyKnownResources(string stage)
    {
        using var f = new OrdinaryPreparationFixture(); var dispatched = 0;
        Func<Action, Task> dispatcher = action =>
        {
            var n = Interlocked.Increment(ref dispatched);
            if (stage == "dispatch" || stage == "handoff" && n == 2) throw new IOException("controlled-dispatch");
            return Task.Run(action);
        };
        var result = await f.Service(deadline: stage == "deadline" ? (_, _) => throw new IOException("controlled-deadline") : null, dispatch: dispatcher).AcquireAsync(f.Anchor);
        Assert.Equal(OrdinaryPreparationReason.Unavailable, result.Reason); Assert.Null(result.Owner);
        var retired = await result.Retirement.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(retired.Retired); Assert.True(retired.ReservationReleased);
        if (stage == "handoff") { Assert.NotNull(retired.Scope); Assert.True(retired.Scope!.Retired); Assert.Equal(4, retired.Scope.Resources.Count); OriginalFileScopeFixture.AssertClosedInReverse(f.Api); }
        else Assert.Empty(f.Api.Calls);
        Assert.False(Read(f.Engine, f.Anchor.Path)!.Paths.Single().InUse);
    }
}
