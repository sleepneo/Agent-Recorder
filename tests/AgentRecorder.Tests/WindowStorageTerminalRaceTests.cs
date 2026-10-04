using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class WindowStorageTerminalRaceTests
{
    private static readonly CaptureWritePaths Paths = new(@"D:\output", null, @"C:\temp\AgentRecorder");

    [Theory]
    [InlineData("low", false)]
    [InlineData("empty", false)]
    [InlineData("negative", false)]
    [InlineData("remount", false)]
    [InlineData("exception", false)]
    [InlineData("low", true)]
    [InlineData("empty", true)]
    [InlineData("negative", true)]
    [InlineData("remount", true)]
    [InlineData("exception", true)]
    public void LateProbeAfterFinalCommitNeverThrows(string fault, bool admission)
    {
        var provider = new FakeStorageProvider();
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        guard.EnsureAdmission();
        int moves = 0;
        provider.Query = (paths, _) =>
        {
            Assert.True(guard.TryCommit(() => moves++, true));
            return Fault(provider, paths, fault);
        };
        var exception = Record.Exception(() => Ensure(guard, admission));
        Assert.Null(exception);
        Assert.True(guard.Committed); Assert.Null(guard.FailureCode);
        Assert.Equal(1, moves);
        guard.Dispose();
        Assert.Null(Record.Exception(() => Ensure(guard, admission)));
    }

    [Theory]
    [InlineData("commit", false)]
    [InlineData("dispose", false)]
    [InlineData("abort", false)]
    [InlineData("commit", true)]
    [InlineData("dispose", true)]
    [InlineData("abort", true)]
    public void FinalArbiterWinsBetweenCalculatedFailureAndClaim(string winner, bool admission)
    {
        var provider = new FakeStorageProvider();
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        guard.EnsureAdmission();
        provider.Free = _ => 0;
        int decisions = 0;
        guard.BeforeFailureClaimForTests = () =>
        {
            decisions++;
            if (winner == "commit") Assert.True(guard.TryCommit(() => { }, true));
            else if (winner == "dispose") guard.Dispose();
            else Assert.True(guard.TryAbort(WindowStorageSafety.UnavailableCode));
        };
        var exception = Record.Exception(() => Ensure(guard, admission));
        Assert.Equal(1, decisions);
        if (winner == "commit") { Assert.Null(exception); Assert.True(guard.Committed); Assert.Null(guard.FailureCode); }
        else if (winner == "dispose") { Assert.IsAssignableFrom<OperationCanceledException>(exception); Assert.Null(guard.FailureCode); }
        else
        {
            Assert.Equal(WindowStorageSafety.UnavailableCode, Assert.IsType<StorageSafetyException>(exception).Code);
            Assert.False(guard.TryCommit(() => Assert.Fail("abort must win"), true));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeoutCandidateCannotOverrideCommitOrBecomeCapacityFailureAfterDispose(bool dispose)
    {
        var provider = new FakeStorageProvider();
        using var guard = new WindowStorageSafety(Paths, 60, provider) { QueryTimeout = TimeSpan.FromMilliseconds(30) };
        guard.EnsureAdmission();
        provider.Query = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Array.Empty<StorageVolumeSample>(); };
        int decisions = 0;
        guard.BeforeFailureClaimForTests = () =>
        {
            decisions++;
            if (dispose) guard.Dispose(); else Assert.True(guard.TryCommit(() => { }, true));
        };
        var exception = Record.Exception(guard.EnsureRuntimeCapacity);
        Assert.Equal(1, decisions); // Deadline really produced a candidate first.
        if (dispose) Assert.IsAssignableFrom<OperationCanceledException>(exception); else Assert.Null(exception);
        Assert.Null(guard.FailureCode);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("empty")]
    [InlineData("exception")]
    public async Task FailureClaimFirstBlocksFinalCommitAndNotifiesOnlyOnce(string fault)
    {
        var provider = new FakeStorageProvider();
        var clock = new StorageTestClock();
        using var guard = new WindowStorageSafety(Paths, 60, provider) { Delay = clock.Delay };
        guard.EnsureAdmission();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        guard.Start(_ => { Interlocked.Increment(ref notifications); notified.TrySetResult(); });
        await clock.WaitForDelay();
        provider.Query = (paths, _) => Fault(provider, paths, fault);
        var expected = fault == "low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode;
        Assert.Equal(expected, Assert.Throws<StorageSafetyException>(guard.EnsureRuntimeCapacity).Code);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, Assert.Throws<StorageSafetyException>(guard.EnsureAdmission).Code);
        Assert.False(guard.TryAbort(expected));
        Assert.False(guard.TryCommit(() => Assert.Fail("failed capacity cannot publish"), true));
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, notifications); Assert.False(guard.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntermediateCommitDoesNotSuppressLaterInvalidProbe(bool throws)
    {
        var provider = new FakeStorageProvider();
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        guard.EnsureAdmission();
        provider.Query = (paths, _) =>
        {
            Assert.True(guard.TryCommit(() => { }, false));
            return Fault(provider, paths, throws ? "exception" : "empty");
        };
        Assert.Equal(WindowStorageSafety.UnavailableCode, Assert.Throws<StorageSafetyException>(guard.EnsureRuntimeCapacity).Code);
        Assert.False(guard.Committed);
        Assert.False(guard.TryCommit(() => Assert.Fail("intermediate commit cannot release protection"), true));
    }

    private static void Ensure(WindowStorageSafety guard, bool admission)
    {
        if (admission) guard.EnsureAdmission(); else guard.EnsureRuntimeCapacity();
    }

    private static Task<IReadOnlyList<StorageVolumeSample>> Fault(FakeStorageProvider provider, IReadOnlyList<string> paths, string fault)
    {
        if (fault == "exception") throw new IOException("late probe exception");
        IReadOnlyList<StorageVolumeSample> samples = fault == "empty" ? Array.Empty<StorageVolumeSample>()
            : paths.Select(p => new StorageVolumeSample(p, fault == "remount" ? "changed-volume" : provider.Volume(p),
                fault == "negative" ? -1 : 0)).ToArray();
        return Task.FromResult(samples);
    }
}
