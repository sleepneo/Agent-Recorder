using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using System.Diagnostics;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingNaturalWakeRuntimeTests
{
    [Fact]
    public async Task SchedulerDispatchesDueCandidateOnceAndDisposeStopsTheLoop()
    {
        using var database = new TestDatabase();
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(database.Store);
        var dispatchCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 1;
        var dispatchCount = 0;

        var candidate = new StandingLeaseNaturalWakeCandidate(
            "intent-1",
            "plan-1",
            "occ-1",
            "lease-1",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1,
            1,
            1);

        using var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            dispatchForTest: (intentId, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                dispatchCompleted.TrySetResult(intentId);
                return Task.CompletedTask;
            },
            candidateSourceForTest: (_, _) =>
                Interlocked.Exchange(ref remaining, 0) == 1
                    ? new[] { candidate }
                    : Array.Empty<StandingLeaseNaturalWakeCandidate>());

        Assert.True(scheduler.Start());
        Assert.Equal("intent-1", await dispatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, dispatchCount);

        scheduler.Dispose();
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public void NaturalWakeCandidateQueryIsBoundToCurrentIdentityAndDoesNotInventRows()
    {
        using var database = new TestDatabase();
        var query = new StandingLeaseNaturalWakeCandidateQuery(database.Store);

        Assert.Empty(query.ListCandidates("S-1", "session-1"));
        Assert.Empty(query.ListRecoveryCandidates("S-1", "session-1"));
    }

    [Fact]
    public async Task TimerWinsDoNotLeaveHistoricalWaitersThatConsumeTheNextSignal()
    {
        using var database = new TestDatabase();
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(database.Store);
        var dispatchCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerWins = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emptyPolls = 0;
        var candidateVisible = 0;
        var candidate = DueCandidate("signal-wakeup");

        using var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            pollInterval: TimeSpan.FromMilliseconds(250),
            candidateSourceForTest: (_, _) =>
            {
                if (Volatile.Read(ref candidateVisible) == 0)
                {
                    var count = Interlocked.Increment(ref emptyPolls);
                    if (count >= 3)
                        timerWins.TrySetResult(null);
                    return Array.Empty<StandingLeaseNaturalWakeCandidate>();
                }

                return new[] { candidate };
            },
            dispatchForTest: (_, _) =>
            {
                dispatchCompleted.TrySetResult(null);
                return Task.CompletedTask;
            });

        Assert.True(scheduler.Start());
        await timerWins.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(dispatchCompleted.Task.IsCompleted);
        Volatile.Write(ref candidateVisible, 1);
        var signaledAt = Stopwatch.GetTimestamp();
        scheduler.Signal();
        await dispatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var elapsed = Stopwatch.GetElapsedTime(signaledAt);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(150), $"Signal dispatch took {elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task MultipleSignalsRemainSingleFlightAndLoopRetiresWithoutFault()
    {
        using var database = new TestDatabase();
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(database.Store);
        var dispatchStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var dispatchCount = 0;

        using var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => "S-1",
            () => "session-1",
            pollInterval: TimeSpan.FromMilliseconds(20),
            candidateSourceForTest: (_, _) => new[] { DueCandidate("single-flight") },
            dispatchForTest: async (_, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maximumActive, now);
                dispatchStarted.TrySetResult(null);
                await releaseDispatch.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref active);
            });

        Assert.True(scheduler.Start());
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 20; i++)
            scheduler.Signal();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref maximumActive));
        releaseDispatch.TrySetResult(null);
        scheduler.Dispose();
        Assert.True(scheduler.LoopTaskForTests?.IsCompleted == true);
        Assert.Equal(1, Volatile.Read(ref maximumActive));
        Assert.True(Volatile.Read(ref dispatchCount) >= 1);
    }

    [Fact]
    public async Task DisposeIsBoundedWhenDispatchIgnoresCancellationAndDoesNotDisposeLivePrimitives()
    {
        using var database = new TestDatabase();
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(database.Store);
        var dispatchStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;

        var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => "S-1",
            () => "session-1",
            pollInterval: TimeSpan.FromMilliseconds(20),
            candidateSourceForTest: (_, _) => new[] { DueCandidate("dispose-boundary") },
            dispatchForTest: async (_, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                dispatchStarted.TrySetResult(null);
                await releaseDispatch.Task.ConfigureAwait(false);
            });

        Assert.True(scheduler.Start());
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disposeTask = Task.Run(scheduler.Dispose);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2.5));
        Assert.False(scheduler.IsStarted);

        scheduler.Signal();
        releaseDispatch.TrySetResult(null);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref dispatchCount));
        scheduler.Dispose();
    }

    private static StandingLeaseNaturalWakeCandidate DueCandidate(string intentId) =>
        new(
            intentId,
            "plan-1",
            "occ-1",
            "lease-1",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1,
            1,
            1);

    private static void InterlockedMax(ref int location, int value)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref location);
            if (snapshot >= value || Interlocked.CompareExchange(ref location, value, snapshot) == snapshot)
                return;
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "agent-recorder-task247-" + Guid.NewGuid().ToString("N"));

        internal TestDatabase()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(_root, SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();
        }

        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
