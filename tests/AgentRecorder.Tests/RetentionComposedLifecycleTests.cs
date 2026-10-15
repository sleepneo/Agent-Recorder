using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionComposedLifecycleTests
{
    [Theory]
    [InlineData("timeout")][InlineData("cancel")][InlineData("dispose")][InlineData("host")][InlineData("stopall")][InlineData("shutdown")][InlineData("owner")]
    public async Task ActiveBorrowKeepsAllResourcesUntilRealReturn(string stop)
    {
        using var f = new RetentionComposedFixture(); using var phase = new Phase(); var clock = new RetentionComposedFixture.Clock();
        using var cancel = new CancellationTokenSource(); using var service = f.Service(clock: clock); var calls = 0;
        var session = f.Execute(service, context => { context.RequireActive(); calls++; phase.Block(); return RetentionComposedContinuationResult.Completed; }, cancel.Token);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var owner = f.PreparedOwner(); var before = f.Files.Api.Calls.Count;
            Assert.Equal(OriginalFileHoldReason.Busy, (await owner.ObserveAsync()).Reason);
            Stop(stop, f, service, session, cancel, clock);
            if (stop is not ("stopall" or "shutdown" or "owner"))
            {
                var frozen = await session.Response.WaitAsync(TimeSpan.FromSeconds(2)); Assert.NotEqual(RetentionComposedReason.Checked, frozen.Reason);
            }
            Assert.False(session.Retirement.IsCompleted); f.AssertProtected(); Assert.Equal(before, f.Files.Api.Calls.Count);
            Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close");
            using var other = f.Service(); Assert.Equal(stop == "shutdown" ? RetentionComposedReason.HostStopped : RetentionComposedReason.Busy,
                (await RetentionComposedFixture.Finish(f.Execute(other))).Reason);
            phase.Release(); Assert.NotEqual(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(session)).Reason); Assert.Equal(1, calls);
            Assert.True((await session.Retirement).Preparation!.ReservationReleased); OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
            if (stop is not ("shutdown" or "host")) Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(f.Execute(other))).Reason);
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("dispose")][InlineData("stopall")][InlineData("shutdown")][InlineData("owner")]
    public async Task DatabaseRollbackMustActuallyReturnBeforeAnyScopeClose(string stop)
    {
        using var f = new RetentionComposedFixture(); using var phase = new Phase(); using var cancel = new CancellationTokenSource();
        var db = new RetentionGuardFixture.TraceDatabase { BeforeRollback = phase.Block }; var clock = new RetentionComposedFixture.Clock();
        using var service = f.Service(db, clock); var session = f.Execute(service);
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Stop(stop, f, service, session, cancel, clock);
            f.AssertProtected(); Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close"); Assert.False(session.Retirement.IsCompleted);
            using var writer = RetentionComposedContractTests.Writer(f);
            Assert.Equal(5, Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => RetentionGuardFixture.Write(writer, "UPDATE ordinary_recording_history SET source_title='busy';")).SqliteErrorCode);
            phase.Release(); await RetentionComposedFixture.Finish(session);
            Assert.True((await session.Retirement).Database!.Retired); OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
            RetentionGuardFixture.Write(writer, "UPDATE ordinary_recording_history SET source_title='released';");
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("rollback")][InlineData("transaction_dispose")][InlineData("close")][InlineData("connection_dispose")]
    public async Task DatabaseCleanupUnknownRetainsNativeLeaseAndBookingEvenAfterStopAll(string fault)
    {
        using var f = new RetentionComposedFixture(); var db = new RetentionGuardFixture.TraceDatabase { Fault = fault }; using var service = f.Service(db); var calls = 0;
        var session = f.Execute(service, _ => { calls++; throw new IOException("PRIVATE_CALLBACK"); });
        Assert.Equal(RetentionComposedReason.Unavailable, (await session.Response.WaitAsync(TimeSpan.FromSeconds(8))).Reason);
        var retired = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(retired.Retired);
        Assert.Equal(RetentionComposedReason.ContinuationFailed, retired.Primary); Assert.False(retired.Database!.Retired);
        Assert.Equal(RetentionGuardCleanup.Unknown, retired.Database.Cleanup); Assert.Null(retired.Preparation); Assert.Equal(1, calls);
        f.Engine.StopAllSync("fixture_stop"); f.PreparedOwner().Dispose(); session.Dispose(); service.Dispose();
        f.AssertProtected(); Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close");
        using var other = f.Service(); Assert.Equal(RetentionComposedReason.Busy, (await RetentionComposedFixture.Finish(f.Execute(other))).Reason);
        Assert.Equal(1, db.Rollbacks); Assert.Equal(1, db.TransactionDisposes); Assert.Equal(1, db.Closes); Assert.Equal(1, db.ConnectionDisposes);
        GC.Collect(); GC.WaitForPendingFinalizers(); f.AssertProtected(); Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close");
    }

    [Theory]
    [InlineData("leaf", "unknown")][InlineData("leaf", "failed")][InlineData("leaf", "throw")]
    [InlineData("directory", "unknown")][InlineData("directory", "failed")][InlineData("directory", "throw")]
    public async Task NativeCleanupUnknownDoesNotReleaseBookingOrRetry(string target, string fault)
    {
        using var f = new RetentionComposedFixture(); f.Files.Api.CloseValue = n =>
        {
            if (target == "leaf" ? n.Leaf : n.Index == 0)
            { if (fault == "throw") throw new IOException("PRIVATE_CLOSE"); return fault == "failed" ? OriginalFileCloseOutcome.Failed : OriginalFileCloseOutcome.Unknown; }
            return OriginalFileCloseOutcome.Closed;
        };
        using var service = f.Service(); var session = f.Execute(service);
        Assert.Equal(RetentionComposedReason.Unavailable, (await session.Response.WaitAsync(TimeSpan.FromSeconds(8))).Reason);
        var retired = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(retired.Retired);
        Assert.True(retired.Database!.Retired); Assert.False(retired.Preparation!.Retired); Assert.False(retired.Preparation.ReservationReleased);
        f.AssertProtected(); var closes = f.Files.Api.Calls.Count(c => c.Stage == "close"); session.Dispose(); f.PreparedOwner().Dispose();
        Assert.Equal(closes, f.Files.Api.Calls.Count(c => c.Stage == "close"));
        using var other = f.Service(); Assert.Equal(RetentionComposedReason.Busy, (await RetentionComposedFixture.Finish(f.Execute(other))).Reason);
        OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
    }

    [Fact]
    public async Task SharedBudgetIncludesPreparationAndDatabaseInsteadOfRestartingAtEachStage()
    {
        using var f = new RetentionComposedFixture(); var clock = new RetentionComposedFixture.Clock(); var calls = 0; var dispatched = 0;
        var db = new RetentionGuardFixture.TraceDatabase { BeforeOpen = () => clock.Advance(TimeSpan.FromSeconds(1.6)) };
        using var service = f.Service(db, clock, action => Task.Run(() => { if (Interlocked.Increment(ref dispatched) == 1) clock.Advance(TimeSpan.FromSeconds(1.5)); action(); }));
        var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.TimedOut, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Equal(0, calls); Assert.Equal(0, db.Begins); Assert.NotEmpty(f.Files.Api.Opened); Assert.NotNull(db.Connection);
    }

    [Theory]
    [InlineData("prepare")][InlineData("database")][InlineData("borrow")][InlineData("close")]
    public async Task FrozenTimeoutWaitsForActualBlockedWorkerAndKeepsAdmission(string stage)
    {
        using var f = new RetentionComposedFixture(); using var phase = new Phase(); var clock = new RetentionComposedFixture.Clock(); var calls = 0;
        var db = new RetentionGuardFixture.TraceDatabase();
        if (stage == "database") db.BeforeOpen = phase.Block;
        if (stage == "borrow") db.BeforeOpen = () => f.Files.Api.Before = c => { if (c.Stage == "basic") phase.Block(); };
        if (stage == "close") f.Files.Api.Before = c => { if (c.Stage == "close") phase.Block(); };
        using var service = f.Service(db, clock, stage == "prepare" ? action => Task.Run(() => { phase.Block(); action(); }) : null);
        var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(3.1));
            var frozen = await session.Response.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(RetentionComposedReason.TimedOut, frozen.Reason);
            Assert.False(session.Retirement.IsCompleted); using var other = f.Service();
            Assert.Equal(RetentionComposedReason.Busy, (await RetentionComposedFixture.Finish(f.Execute(other))).Reason);
            if (stage != "prepare") f.AssertProtected();
            phase.Release(); Assert.Equal(frozen, await RetentionComposedFixture.Finish(session)); Assert.Equal(stage == "close" ? 1 : 0, calls);
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task ConcurrentObserveWinsBorrowWithoutClosingItsLiveWorker()
    {
        using var f = new RetentionComposedFixture(); using var phase = new Phase(); Task<OriginalFileHoldObservation>? observation = null;
        var db = new RetentionGuardFixture.TraceDatabase { BeforeOpen = () =>
        {
            f.Files.Api.Before = c => { if (c.Stage == "basic") phase.Block(); };
            observation = f.PreparedOwner().ObserveAsync(); Assert.True(phase.Entered.Task.Wait(TimeSpan.FromSeconds(5)));
        } };
        using var service = f.Service(db); var calls = 0; var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // DB cleanup proceeds, but preparation retirement still waits for this actual Observe worker.
            await Task.Run(() => SpinWait.SpinUntil(() => Volatile.Read(ref db.ConnectionDisposes) == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, db.ConnectionDisposes); Assert.False(session.Retirement.IsCompleted); f.AssertProtected();
            Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close"); phase.Release();
            Assert.Equal(RetentionComposedReason.BorrowFailed, (await RetentionComposedFixture.Finish(session)).Reason);
            Assert.Equal(OriginalFileBorrowReason.Busy, (await session.Retirement).Borrow!.Reason); await observation!; Assert.Equal(0, calls);
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PreDispatchCancellationOrHostStopNeverAcquiresResources(bool host)
    {
        using var f = new RetentionComposedFixture(); using var cancel = new CancellationTokenSource(); using var service = f.Service();
        if (host) service.Dispose(); else cancel.Cancel();
        Assert.Equal(host ? RetentionComposedReason.HostStopped : RetentionComposedReason.Cancelled,
            (await RetentionComposedFixture.Finish(f.Execute(service, cancellation: cancel.Token))).Reason); Assert.Empty(f.Files.Api.Calls);
    }

    private static void Stop(string stop, RetentionComposedFixture f, RetentionComposedScope service, RetentionComposedScope.Session session,
        CancellationTokenSource cancel, RetentionComposedFixture.Clock clock)
    {
        switch (stop)
        {
            case "timeout": clock.Advance(TimeSpan.FromSeconds(3.1)); break;
            case "cancel": cancel.Cancel(); break;
            case "dispose": session.Dispose(); break;
            case "host": service.Dispose(); break;
            case "stopall": f.Engine.StopAllSync("fixture_stop"); break;
            case "shutdown": f.Engine.BeginHostShutdown("fixture_shutdown"); break;
            case "owner": f.PreparedOwner().Dispose(); break;
        }
    }

    [Theory]
    [InlineData("stopall")][InlineData("shutdown")][InlineData("owner")]
    public async Task StopBeforeBorrowRunsZeroContinuationAndRetainsScopeThroughDatabaseExit(string stop)
    {
        using var f = new RetentionComposedFixture(); using var phase = new Phase(); var db = new RetentionGuardFixture.TraceDatabase { BeforeOpen = phase.Block };
        using var service = f.Service(db); using var cancel = new CancellationTokenSource(); var clock = new RetentionComposedFixture.Clock(); var calls = 0;
        var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Stop(stop, f, service, session, cancel, clock);
            f.AssertProtected(); Assert.DoesNotContain(f.Files.Api.Calls, c => c.Stage == "close"); Assert.False(session.Retirement.IsCompleted);
            phase.Release(); Assert.NotEqual(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(session)).Reason); Assert.Equal(0, calls);
            Assert.True((await session.Retirement).Database!.Retired); OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
