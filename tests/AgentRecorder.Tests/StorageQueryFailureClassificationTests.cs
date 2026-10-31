using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class StorageQueryFailureClassificationTests
{
    [Theory]
    [InlineData("App")]
    [InlineData("Headless")]
    public async Task FormalNonzeroQueryKeepsBusinessFailureButCleanRetirementAllowsIdle(string host)
    {
        using var temp = new TempDirectory();
        var blocked = Path.Combine(temp.Path, "owned-file");
        File.WriteAllText(blocked, "owned non-directory");
        var flavor = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Name;
        var exe = Path.Combine(TestHelper.ProjectRoot, $"src/AgentRecorder.{host}/bin/{flavor}/Release/net8.0-windows10.0.19041.0/AgentRecorder.{host}.exe");
        Assert.True(File.Exists(exe));
        var provider = new ProcessStorageCapacityProvider { ExecutablePath = () => exe };
        using var guard = new WindowStorageSafety(new CaptureWritePaths(blocked, null, null), 10, provider);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(true));
        await guard.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        var life = provider.LastProbe!;
        Assert.True(life.ExitCompletion.IsCompletedSuccessfully);
        Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Null(life.CleanupFailure);
        Assert.Null(guard.ProbeRetirementFailure);
        Assert.NotNull(guard.ProbeQueryFailure);
        Assert.Throws<StorageSafetyException>(() => guard.EnsureAdmission());
        Assert.Equal(WindowStorageSafety.UnavailableCode, guard.FailureCode);
        Assert.False(guard.TryCommit(() => Assert.Fail("Failed query cannot publish"), true));
        Assert.Equal("owned non-directory", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task EvidenceBindsExactTaskProviderAndLifetimeDespiteLastProbeChanging()
    {
        using var temp = new TempDirectory();
        var blocked = Path.Combine(temp.Path, "owned-file");
        File.WriteAllText(blocked, "owned");
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new ProcessStorageCapacityProvider
        {
            HooksForTests = new StorageProbeHooks { BeforeRead = async () =>
            {
                if (Interlocked.Increment(ref calls) == 1) { firstRead.SetResult(); await release.Task; }
            } }
        };
        var first = provider.QueryAsync(new[] { blocked }, default);
        try
        {
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var firstLife = provider.LastProbe!;
            var second = provider.QueryAsync(new[] { temp.Path }, default);
            await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(firstLife, provider.LastProbe);
            Assert.False(firstLife.RetirementCompletion.IsCompleted);
            release.SetResult();
            var error = await Assert.ThrowsAnyAsync<IOException>(() => first);
            Assert.True(provider.HasCleanRetirementEvidence(error, first));
            Assert.False(provider.HasCleanRetirementEvidence(error, second));
            var copiedTask = Task.FromException<IReadOnlyList<StorageVolumeSample>>(error);
            Assert.False(provider.HasCleanRetirementEvidence(error, copiedTask));
            Assert.Same(error, Assert.Single(copiedTask.Exception!.InnerExceptions));
            Assert.False(new ProcessStorageCapacityProvider().HasCleanRetirementEvidence(error, first));
            using var guard = new WindowStorageSafety(new CaptureWritePaths(temp.Path, null, null), 1,
                new FakeStorageProvider { Query = (_, _) => Task.FromException<IReadOnlyList<StorageVolumeSample>>(error) });
            Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
            await guard.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(guard.ProbeRetirementFailure); // A replayed certificate is not this provider/query.
        }
        finally { release.TrySetResult(); try { await first.WaitAsync(TimeSpan.FromSeconds(10)); } catch (IOException) { } }
    }

    [Fact]
    public async Task FormalCancellationPreservesCancelledTaskAndExactRetirementEvidence()
    {
        using var temp = new TempDirectory();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ProcessStorageCapacityProvider
        {
            HooksForTests = new StorageProbeHooks { BeforeRead = () =>
            {
                entered.SetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token);
            } }
        };
        var query = provider.QueryAsync(new[] { temp.Path }, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
            Assert.True(query.IsCanceled);
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.True(provider.HasCleanRetirementEvidence(error, query));
            Assert.True(provider.LastProbe!.ExitCompletion.IsCompletedSuccessfully);
            Assert.True(provider.LastProbe.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Null(provider.LastProbe.CleanupFailure);
        }
        finally { cancel.Cancel(); try { await query.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownProviderCancellationCannotProveRetirement(bool synchronous)
    {
        using var temp = new TempDirectory();
        var provider = new FakeStorageProvider { Query = (_, _) => synchronous
            ? throw new OperationCanceledException("unknown owner")
            : Task.FromCanceled<IReadOnlyList<StorageVolumeSample>>(new CancellationToken(true)) };
        using var guard = new WindowStorageSafety(new CaptureWritePaths(temp.Path, null, null), 1, provider);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
        await guard.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(guard.ProbeRetirementFailure);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("dispose")]
    [InlineData("commit")]
    public async Task KnownFailedQueryRetainsSamplingAndCopyUntilExactRetirement(string winner)
    {
        using var temp = new TempDirectory();
        var blocked = Path.Combine(temp.Path, "owned-file");
        File.WriteAllText(blocked, "owned");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ProcessStorageCapacityProvider
        {
            HooksForTests = new StorageProbeHooks { BeforeDispose = async () => { entered.SetResult(); await release.Task; } }
        };
        using var guard = new WindowStorageSafety(new CaptureWritePaths(blocked, null, null), 1, provider)
            { QueryTimeout = winner == "failure" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5) };
        guard.ReserveFfmpegVideoOnlyPublication();
        var gate = (IPublicationCopyCapacityGate)guard.CreateCommitGate(null, true);
        using var copy = (await gate.BeginCopyAsync(128, default))!;
        using var destination = new FileStream(Path.Combine(temp.Path, "copy.tmp"), FileMode.CreateNew);
        using var cancel = new CancellationTokenSource();
        var check = guard.CheckAsync(false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(provider.LastProbe!.ExitCompletion.IsCompletedSuccessfully);
            Assert.False(provider.LastProbe.RetirementCompletion.IsCompleted);
            if (winner == "dispose") guard.Dispose();
            if (winner == "commit") Assert.True(guard.TryCommit(() => { }, true));
            Assert.Equal(winner == "failure" ? WindowStorageSafety.UnavailableCode : null,
                await check.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
            var write = copy.WriteAsync(destination, new byte[16], cancel.Token);
            Assert.False(write.IsCompleted);
            copy.Dispose();
            Assert.Equal(128, guard.RemainingPublicationCopyBytes);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            release.SetResult();
            await guard.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, guard.RemainingPublicationCopyBytes);
            Assert.Null(guard.ProbeRetirementFailure);
            Assert.NotNull(guard.ProbeQueryFailure);
            Assert.Equal(winner == "commit", guard.Committed);
            Assert.Null(guard.FailureCode); // Check is advisory; the terminal arbiter is unchanged.
            Assert.Equal(0, destination.Length);
        }
        finally
        {
            release.TrySetResult(); cancel.Cancel(); copy.Dispose(); guard.Dispose();
            await guard.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
