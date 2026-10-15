using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.RetentionGuardFixture;

namespace AgentRecorder.Tests;

public sealed class RetentionGuardLifecycleTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnsettledOrdinaryOrBundleQueueFailurePreventsOpeningDatabase(bool bundle)
    {
        using var f = new RetentionGuardFixture(); var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new SqliteOrdinaryRecordingHistoryStore(f.Db.Store, (_, _) => failed.TrySetResult());
        var row = f.Db.Recording("unsettled", completed: bundle);
        var operation = AgentRecorder.Core.OrdinaryRecordingBundleOperation.Create(row.RecordingId, row.OutputPath);
        if (bundle) row = row with { BundleRegistration = new(operation) };
        writer.Register(row);
        if (bundle)
        {
            writer.BeforeBundlePersistForTests = _ => throw new IOException("PRIVATE_BUNDLE_QUEUE");
            Assert.True(writer.TryPublishBundle(new(operation, new("failed", operation.BundlePath, "bundle_generation_failed"))));
        }
        else
        {
            writer.BeforePersistForTests = _ => throw new IOException("PRIVATE_ORDINARY_QUEUE");
            Assert.True(writer.TryPublish(row with { Status = "failed", ReasonCode = "fixture_failed", CompletedAtUtc = UnifiedHistoryTestDatabase.At.AddSeconds(10) }));
        }
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var adapter = new TraceDatabase();
        using var g = new RetentionReferenceGuard(f.Db.Store, writer, UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, new(), adapter);
        Assert.Equal(RetentionGuardReason.Unavailable, await Finish(f.Execute(g)));
        Assert.Null(adapter.Connection); Assert.Equal(0, adapter.Begins);
    }
    [Theory]
    [InlineData("timeout")][InlineData("cancel")][InlineData("dispose")][InlineData("host")]
    public async Task FrozenResponseDoesNotReleaseLiveTransactionOrWorker(string stop)
    {
        using var f = new RetentionGuardFixture(); var clock = new Clock(); using var cancelled = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var g = f.Guard(clock: clock); using var replacementService = f.Guard(); RetentionReferenceGuard.ScopeContext? saved = null;
        var session = f.Execute(g, context => { saved = context; entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(12))); return RetentionGuardContinuationResult.Completed; }, cancelled.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (stop == "timeout") clock.Expire(); else if (stop == "cancel") cancelled.Cancel(); else if (stop == "host") g.Dispose(); else session.Dispose();
            var frozen = (await session.Response.WaitAsync(TimeSpan.FromSeconds(3))).Reason;
            Assert.Equal(stop == "timeout" ? RetentionGuardReason.TimedOut : stop == "host" ? RetentionGuardReason.HostStopped : RetentionGuardReason.Cancelled, frozen);
            Assert.False(session.Retirement.IsCompleted); Assert.Equal(RetentionGuardReason.Busy, await Finish(f.Execute(replacementService)));
            using var writer = f.Writer(); var busy = Assert.Throws<SqliteException>(() => Write(writer, "UPDATE ordinary_recording_history SET source_title='after';")); Assert.Equal(5, busy.SqliteErrorCode);
            release.Set(); Assert.Equal(frozen, await Finish(session)); Assert.Throws<InvalidOperationException>(() => saved!.RequireActive());
            Write(writer, "UPDATE ordinary_recording_history SET source_title='after';");
            Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(replacementService)));
            Assert.Equal(frozen, (await session.Response).Reason);
        }
        finally { release.Set(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
    [Theory]
    [InlineData("rollback")][InlineData("transaction_dispose")][InlineData("close")][InlineData("connection_dispose")]
    public async Task CleanupUnknownKeepsPrimaryResourcesAndAdmission(string fault)
    {
        using var f = new RetentionGuardFixture(); var adapter = new TraceDatabase { Fault = fault }; using var g = f.Guard(adapter);
        var calls = 0; var session = f.Execute(g, _ => { calls++; throw new IOException("PRIVATE_CALLBACK"); });
        Assert.Equal(RetentionGuardReason.Unavailable, (await session.Response.WaitAsync(TimeSpan.FromSeconds(8))).Reason);
        var result = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(RetentionGuardReason.ContinuationFailed, result.Primary); Assert.Equal(RetentionGuardCleanup.Unknown, result.Cleanup); Assert.False(result.Retired);
        Assert.Equal(1, calls); Assert.Single(adapter.Threads.Distinct()); Assert.NotNull(adapter.Connection);
        Assert.Equal(1, adapter.Rollbacks); Assert.Equal(1, adapter.TransactionDisposes); Assert.Equal(1, adapter.Closes); Assert.Equal(1, adapter.ConnectionDisposes);
        using var other = f.Guard(); Assert.Equal(RetentionGuardReason.Busy, await Finish(f.Execute(other)));
    }
    [Fact]
    public async Task CallbackExceptionNoRetryAndRollbackRestoresWriter()
    {
        using var f = new RetentionGuardFixture(); using var g = f.Guard(); var calls = 0;
        Assert.Equal(RetentionGuardReason.ContinuationFailed, await Finish(f.Execute(g, _ => { calls++; throw new InvalidOperationException("PRIVATE_CALLBACK"); })));
        Assert.Equal(1, calls); using var writer = f.Writer(); Write(writer, "UPDATE ordinary_recording_history SET source_title='ok';");
        Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(g)));
    }
    [Fact]
    public async Task ExpiredContextCannotBeUsedOnOtherThreadEvenDuringCallback()
    {
        using var f = new RetentionGuardFixture(); using var g = f.Guard();
        Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(g, context =>
        {
            Exception? error = null; var thread = new Thread(() => { try { context.RequireActive(); } catch (Exception e) { error = e; } });
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(1))); Assert.IsType<InvalidOperationException>(error);
            context.RequireActive(); return RetentionGuardContinuationResult.Completed;
        })));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task CancelOrHostStopBeforeDispatchNeverOpens(bool hostStop)
    {
        using var f = new RetentionGuardFixture(); var adapter = new TraceDatabase(); using var g = f.Guard(adapter); using var cancel = new CancellationTokenSource();
        if (hostStop) g.Dispose(); else cancel.Cancel();
        Assert.Equal(hostStop ? RetentionGuardReason.HostStopped : RetentionGuardReason.Cancelled, await Finish(f.Execute(g, cancellation: cancel.Token)));
        Assert.Null(adapter.Connection); Assert.Equal(0, adapter.Begins);
    }
    [Fact]
    public async Task BudgetIncludesDelayedOpenDispatchAndNeverEntersBeginOrCallback()
    {
        using var f = new RetentionGuardFixture(); var clock = new Clock(); var adapter = new TraceDatabase { BeforeOpen = clock.Expire };
        using var g = f.Guard(adapter, clock); var calls = 0;
        Assert.Equal(RetentionGuardReason.TimedOut, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, adapter.Begins); Assert.Equal(0, calls); Assert.Equal(1, adapter.Closes);
    }
    [Fact]
    public async Task CleanupStillOwnsSlotAfterCancellationUntilRollbackActuallyReturns()
    {
        using var f = new RetentionGuardFixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var adapter = new TraceDatabase { BeforeRollback = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(12))); } };
        using var g = f.Guard(adapter); var session = f.Execute(g);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); session.Dispose(); Assert.Equal(RetentionGuardReason.Cancelled, (await session.Response).Reason);
            Assert.False(session.Retirement.IsCompleted); Assert.Equal(RetentionGuardReason.Busy, await Finish(f.Execute(g)));
            using var writer = f.Writer(); Assert.Equal(5, Assert.Throws<SqliteException>(() => Write(writer, "UPDATE ordinary_recording_history SET source_title='ok';")).SqliteErrorCode);
            release.Set(); Assert.Equal(RetentionGuardReason.Cancelled, await Finish(session)); Write(writer, "UPDATE ordinary_recording_history SET source_title='ok';");
        }
        finally { release.Set(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(15)); }
    }
}
