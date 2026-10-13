using System.Diagnostics;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Core;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class StorageProbeRetirementTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<IReadOnlyList<StorageVolumeSample>> Result() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static StorageVolumeSample[] Samples(IReadOnlyList<string> paths) => paths.Select(p => new StorageVolumeSample(p, "volume", 1L << 40)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineReturnsBeforeUncooperativeFinallyButProtectsCopyAndSerializesQueries(bool lateFault)
    {
        await using var f = new OwnedFixture();
        var entered = Signal(); var finallyEntered = Signal(); var release = Signal();
        f.Release.Add(() => release.TrySetResult());
        var p = new FakeStorageProvider { Query = async (paths, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { finallyEntered.SetResult(); await release.Task; }
            return Samples(paths);
        } };
        // Preserve the finally window while allowing both an eventual ordinary
        // cancellation and a late non-cancellation fault to be observed.
        if (lateFault) p.Query = async (paths, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { finallyEntered.SetResult(); await release.Task; throw new IOException("late-finally-fault"); }
        };
        var guard = f.Guard(p, 50); guard.ReserveFfmpegVideoOnlyPublication();
        var gate = (IPublicationCopyCapacityGate)guard.CreateCommitGate(null, true);
        var copy = (await gate.BeginCopyAsync(128, default))!;
        var query = guard.CheckAsync(false);
        await entered.Task.WaitAsync(Bound);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await query.WaitAsync(Bound));
        await finallyEntered.Task.WaitAsync(Bound);
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        using var stream = new FileStream(Path.Combine(f.Root, "copy.tmp"), FileMode.CreateNew);
        using var cancelWrite = new CancellationTokenSource();
        var write = copy.WriteAsync(stream, new byte[16], cancelWrite.Token);
        Assert.False(write.IsCompleted); // The real query still holds the sample.
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false).WaitAsync(Bound));
        Assert.Equal(1, p.Calls);
        var watch = Stopwatch.StartNew(); copy.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(128, guard.RemainingPublicationCopyBytes);
        cancelWrite.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        release.SetResult();
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Equal(0, guard.RemainingPublicationCopyBytes);
        Assert.Equal(lateFault, guard.ProbeRetirementFailure is not null);
        if (lateFault) Assert.Contains("late-finally-fault", guard.ProbeRetirementFailure!.ToString());
        else
        {
            p.Query = (paths, _) => Task.FromResult<IReadOnlyList<StorageVolumeSample>>(Samples(paths));
            Assert.Null(await guard.CheckAsync(false));
        }
    }

    [Fact]
    public async Task MonitorCanConvergeBeforeActualRetirementWithoutLosingLateException()
    {
        await using var f = new OwnedFixture(); var clock = new StorageTestClock();
        var late = Result(); var entered = Signal(); f.Release.Add(() => late.TrySetException(new IOException("late-worker-fault")));
        var provider = new FakeStorageProvider { Query = (_, _) => { entered.SetResult(); return late.Task; } };
        var guard = f.Guard(provider, 50, clock.Delay); var failed = Signal();
        guard.Start(_ => { guard.Dispose(); failed.TrySetResult(); });
        await clock.WaitForDelay(); clock.Tick(); await entered.Task.WaitAsync(Bound);
        await failed.Task.WaitAsync(Bound); await guard.MonitorCompletion.WaitAsync(Bound);
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        Assert.Equal(WindowStorageSafety.UnavailableCode, guard.FailureCode);
        late.SetException(new IOException("late-worker-fault"));
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Contains("late-worker-fault", guard.ProbeRetirementFailure!.ToString());
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("dispose")]
    [InlineData("commit")]
    public async Task CancellationOrTerminalWinnerIsBoundedButDoesNotRetireIgnoredCancellation(string mode)
    {
        await using var f = new OwnedFixture(); var entered = Signal(); var late = Result();
        f.Release.Add(() => late.TrySetException(new IOException("terminal-late-fault")));
        var provider = new FakeStorageProvider { Query = (_, _) => { entered.SetResult(); return late.Task; } };
        var guard = f.Guard(provider); using var caller = new CancellationTokenSource();
        var check = guard.CheckAsync(false, caller.Token); await entered.Task.WaitAsync(Bound);
        var watch = Stopwatch.StartNew();
        if (mode == "caller") caller.Cancel();
        if (mode == "dispose") guard.Dispose();
        if (mode == "commit") Assert.True(guard.TryCommit(() => { }, true));
        Assert.Null(await check.WaitAsync(Bound)); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        late.SetException(new IOException("terminal-late-fault")); await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Contains("terminal-late-fault", guard.ProbeRetirementFailure!.ToString());
        Assert.Null(guard.FailureCode); Assert.Equal(mode == "commit", guard.Committed);
        if (mode == "caller") Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndNormalCompletionHaveExplicitOrder(bool cancelFirst)
    {
        await using var f = new OwnedFixture(); var entered = Signal(); var late = Result();
        f.Release.Add(() => late.TrySetResult(Samples(new[] { f.Root })));
        var guard = f.Guard(new FakeStorageProvider { Query = (_, _) => { entered.SetResult(); return late.Task; } });
        using var caller = new CancellationTokenSource(); var check = guard.CheckAsync(false, caller.Token);
        await entered.Task.WaitAsync(Bound);
        if (cancelFirst) { caller.Cancel(); Assert.Null(await check.WaitAsync(Bound)); Assert.False(guard.ProbeRetirementCompletion.IsCompleted); }
        late.SetResult(Samples(new[] { f.Root }));
        Assert.Null(await check.WaitAsync(Bound)); await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        caller.Cancel(); Assert.Null(guard.ProbeRetirementFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderSynchronousAndAsynchronousFaultsAreObserved(bool asynchronous)
    {
        await using var f = new OwnedFixture();
        var error = new IOException("provider-fault");
        var guard = f.Guard(new FakeStorageProvider { Query = (_, _) => asynchronous ? Task.FromException<IReadOnlyList<StorageVolumeSample>>(error) : throw error });
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(true));
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Contains(error, guard.ProbeRetirementFailure!.InnerExceptions);
    }

    [Fact]
    public async Task RealPrivateCapacityResponseRetiresPipesHandleAndDeployment()
    {
        var f = new OwnedFixture();
        try
        {
            var provider = f.Provider(hang: false); var guard = f.Guard(provider);
            Assert.Null(await guard.CheckAsync(true).WaitAsync(Bound));
            await guard.ProbeRetirementCompletion.WaitAsync(Bound);
            var life = provider.LastProbe!;
            Assert.True(life.ExitCompletion.IsCompletedSuccessfully); Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Null(life.CleanupFailure); Assert.True(Assert.Single(f.Workers).HasExited);
            Assert.Null(guard.ProbeRetirementFailure);
        }
        finally { await f.DisposeAsync(); }
        Assert.False(Directory.Exists(f.Root));
    }

    [Fact]
    public async Task RealDeadlineLeavesSameExecutableBystanderAliveAndRetiresOnlyOriginalChild()
    {
        var f = new OwnedFixture();
        try
        {
            var bystander = f.StartHang(); var provider = f.Provider(hang: true); var guard = f.Guard(provider, 100);
            var watch = Stopwatch.StartNew(); Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            await guard.ProbeRetirementCompletion.WaitAsync(Bound);
            Assert.False(bystander.HasExited);
            Assert.True(provider.LastProbe!.ExitCompletion.IsCompletedSuccessfully);
            Assert.True(provider.LastProbe.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Null(provider.LastProbe.CleanupFailure);
            Assert.True(f.Workers.Single(p => p.Id == provider.LastProbe.ProcessId).HasExited);
        }
        finally { await f.DisposeAsync(); }
        Assert.False(Directory.Exists(f.Root)); Assert.All(f.ExitFacts, exited => Assert.True(exited));
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("dispose")]
    [InlineData("commit")]
    public async Task RealOwnedChildRetiresAfterCallerCancellationOrGuardTerminalWinner(string winner)
    {
        await using var f = new OwnedFixture(); var started = Signal();
        var provider = f.Provider(true, new StorageProbeHooks { Started = _ => started.SetResult() });
        var guard = f.Guard(provider); using var cancel = new CancellationTokenSource();
        var check = guard.CheckAsync(false, cancel.Token); await started.Task.WaitAsync(Bound);
        var watch = Stopwatch.StartNew();
        if (winner == "caller") cancel.Cancel();
        else if (winner == "dispose") guard.Dispose();
        else Assert.True(guard.TryCommit(() => { }, true));
        Assert.Null(await check.WaitAsync(Bound)); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.True(Assert.Single(f.Workers).HasExited);
        Assert.True(provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Null(provider.LastProbe.CleanupFailure); Assert.Null(guard.FailureCode);
    }

    [Fact]
    public async Task HeldPidLookupObjectRemainsQueryableAfterOriginalExitBeforeRetirement()
    {
        await using var f = new OwnedFixture(); var exited = Signal(); var release = Signal();
        f.Release.Add(() => release.TrySetResult());
        var provider = f.Provider(false, new StorageProbeHooks { BeforeDispose = async () => { exited.SetResult(); await release.Task; } });
        var guard = f.Guard(provider, 100); var check = guard.CheckAsync(false);
        await exited.Task.WaitAsync(Bound);
        var heldLookup = Assert.Single(f.Workers); var life = provider.LastProbe!;
        // This Process was obtained by numeric lookup while live. Its original
        // cached handle and identity remain queryable after exit. No assertion
        // requires the OS to keep enumerating the number after native exit.
        Assert.Equal(life.ProcessId, heldLookup.Id); Assert.Equal(life.StartTime, heldLookup.StartTime);
        Assert.True(heldLookup.HasExited); Assert.False(heldLookup.SafeHandle.IsClosed);
        Assert.True(life.ExitCompletion.IsCompletedSuccessfully); Assert.False(life.RetirementCompletion.IsCompleted);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await check.WaitAsync(Bound));
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        release.SetResult(); await guard.ProbeRetirementCompletion.WaitAsync(Bound);
    }

    [Fact]
    public async Task ExactLiveChildCanRemainAfterDeadlineThenExitBeforePipesAndHandleRetire()
    {
        await using var f = new OwnedFixture(); var cleanup = Signal(); var allowKill = Signal();
        var beforeDispose = Signal(); var allowDispose = Signal();
        f.Release.Add(() => allowKill.TrySetResult()); f.Release.Add(() => allowDispose.TrySetResult());
        var provider = f.Provider(true, new StorageProbeHooks
        {
            Kill = p => { if (allowKill.Task.IsCompleted) p.Kill(); },
            BeforeCleanup = async () => { cleanup.SetResult(); await allowKill.Task; },
            BeforeDispose = async () => { beforeDispose.SetResult(); await allowDispose.Task; }
        });
        var guard = f.Guard(provider, 100);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false).WaitAsync(Bound));
        await cleanup.Task.WaitAsync(Bound);
        var life = provider.LastProbe!; var original = Assert.Single(f.Workers);
        Assert.False(original.HasExited); Assert.False(life.ExitCompletion.IsCompleted); Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        using (var lookup = Process.GetProcessById(life.ProcessId)) Assert.Equal(life.StartTime, lookup.StartTime);
        output.WriteLine("deadline returned while exact original PID {0}, started {1:o}, remained alive", life.ProcessId, life.StartTime);
        allowKill.SetResult(); await beforeDispose.Task.WaitAsync(Bound);
        Assert.True(original.HasExited); Assert.True(life.ExitCompletion.IsCompletedSuccessfully);
        Assert.False(life.RetirementCompletion.IsCompleted); Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        // Retain the signalled original handles. Numeric lookup availability is
        // observed separately; it is never used as the retirement assertion.
        try
        {
            using var lookup = Process.GetProcessById(life.ProcessId);
            Assert.True(lookup.HasExited);
            output.WriteLine("exited original PID remains queryable with retained handles");
        }
        catch (ArgumentException) { output.WriteLine("exited original PID is no longer enumerated; original handle is signalled"); }
        allowDispose.SetResult(); await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("pipe")]
    [InlineData("dispose-hook")]
    public async Task RealStartPipeAndCleanupFaultsAreDistinctAndNeverSuccessfulCapacity(string fault)
    {
        await using var f = new OwnedFixture();
        var hooks = new StorageProbeHooks
        {
            BeforeStart = _ => { if (fault == "start") throw new IOException("injected-start"); },
            BeforeRead = () => fault == "pipe" ? Task.FromException(new IOException("injected-pipe")) : Task.CompletedTask,
            BeforeDispose = () => fault == "dispose-hook" ? Task.FromException(new IOException("injected-dispose-hook")) : Task.CompletedTask
        };
        var provider = f.Provider(fault == "pipe", hooks); var guard = f.Guard(provider);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false).WaitAsync(Bound));
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Contains("injected-" + fault, guard.ProbeRetirementFailure!.ToString());
        Assert.True(provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Equal(fault != "start", provider.LastProbe.ExitCompletion.IsCompletedSuccessfully);
        Assert.Equal(fault == "dispose-hook", provider.LastProbe.CleanupFailure is not null);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("wait")]
    public async Task FailedKillOrWaitKeepsOriginalLiveOwnerUntilExplicitExitAndPreservesFailure(string fault)
    {
        await using var f = new OwnedFixture(); var cleanup = Signal(); var bypassWait = Signal(); var waitEntered = Signal();
        f.Release.Add(() => bypassWait.TrySetResult());
        var provider = f.Provider(true, new StorageProbeHooks
        {
            Kill = _ => { if (fault == "kill") throw new IOException("injected-kill"); },
            BeforeCleanup = () => { cleanup.TrySetResult(); return Task.CompletedTask; },
            WaitForExit = async _ => { waitEntered.SetResult(); await bypassWait.Task; throw new IOException("injected-wait"); }
        });
        var guard = f.Guard(provider, 100); Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
        await cleanup.Task.WaitAsync(Bound);
        // BeforeCleanup is earlier than registration disposal and Kill. Waiting
        // for the actual exit-wait entry orders the recorded Kill failure.
        await waitEntered.Task.WaitAsync(Bound); bypassWait.SetResult();
        var original = Assert.Single(f.Workers); Assert.False(original.HasExited);
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        if (fault == "kill") Assert.Contains("injected-kill", provider.LastProbe!.CleanupFailure!.ToString());
        original.Kill(); await original.WaitForExitAsync().WaitAsync(Bound);
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.True(provider.LastProbe!.ExitCompletion.IsCompletedSuccessfully);
        Assert.True(provider.LastProbe.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Contains("injected-" + fault, provider.LastProbe.CleanupFailure!.ToString());
        Assert.NotNull(guard.ProbeRetirementFailure);
    }

    [Fact]
    public async Task CleanupWarningDeadlineDoesNotRetireLiveOwnerOrAbandonOriginalWait()
    {
        await using var f = new OwnedFixture(); var cleanup = Signal(); var lateWait = Signal(); var warning = Signal();
        f.Release.Add(() => lateWait.TrySetException(new IOException("late-wait-fault")));
        var provider = f.Provider(true, new StorageProbeHooks
        {
            Kill = _ => { }, BeforeCleanup = () => { cleanup.SetResult(); return Task.CompletedTask; },
            WaitForExit = _ => lateWait.Task, CleanupWarning = _ => warning.TrySetResult()
        });
        var guard = f.Guard(provider, 100); Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
        await cleanup.Task.WaitAsync(Bound);
        // Observe the unchanged actual 500ms warning deadline through a signal
        // driven cancellation callback, without sleeping or polling retries.
        // The wait remains pending even after actual child exit, so its late
        // exception must still be observed before retirement.
        await warning.Task.WaitAsync(Bound);
        Assert.Contains(provider.LastProbe!.CleanupFailure!.InnerExceptions, e => e is TimeoutException);
        Assert.False(guard.ProbeRetirementCompletion.IsCompleted);
        var original = Assert.Single(f.Workers); Assert.False(original.HasExited);
        original.Kill(); await original.WaitForExitAsync().WaitAsync(Bound);
        await provider.LastProbe.ExitCompletion.WaitAsync(Bound);
        Assert.False(provider.LastProbe.RetirementCompletion.IsCompleted);
        lateWait.SetException(new IOException("late-wait-fault"));
        await guard.ProbeRetirementCompletion.WaitAsync(Bound);
        Assert.Contains("late-wait-fault", guard.ProbeRetirementFailure!.ToString());
        Assert.True(provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionAfterOwnedWorkerFailureStopsExactWorkersAndPreservesCleanupFailure(bool cleanupFault)
    {
        OwnedFixture? failed = null;
        var originalProvider = RecordingPreflightChecker.StorageCapacityProvider;
        var env = SnapshotEnvironment();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var fixture = new OwnedFixture(); failed = fixture;
            try { fixture.StartHang(); throw new IOException("constructor-primary"); }
            catch (Exception primary)
            {
                fixture.CleanupFault = cleanupFault;
                try { await fixture.DisposeAsync(); }
                catch (Exception cleanupError) { throw new AggregateException(primary, cleanupError); }
                throw;
            }
        });
        Assert.Contains("constructor-primary", error.ToString());
        Assert.Equal(cleanupFault, error is AggregateException);
        if (cleanupFault) Assert.Contains("fixture-cleanup", error.ToString());
        Assert.All(failed!.ExitFacts, value => Assert.True(value));
        Assert.Equal(cleanupFault, Directory.Exists(failed.Root));
        Assert.Same(originalProvider, RecordingPreflightChecker.StorageCapacityProvider);
        Assert.Equal(env, SnapshotEnvironment());
    }

    [Fact]
    public void PartialDeploymentConstructionFailureRestoresGlobalsAndDeletesOnlyItsOwnedRoot()
    {
        var parent = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data", "temp", "task349"));
        Directory.CreateDirectory(parent); var root = Path.Combine(parent, "construct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = RecordingPreflightChecker.StorageCapacityProvider;
        var resolver = RecordingPreflightChecker.AudioHelperPathResolver; var env = SnapshotEnvironment();
        try
        {
            var error = Assert.Throws<IOException>(() => new FakeAudioHelperDeployment(root, _ => throw new IOException("partial-copy"), stopOwnedProcesses: false));
            Assert.Equal("partial-copy", error.Message); Assert.False(Directory.Exists(Path.Combine(root, "fake-audio-helper")));
            Assert.Same(provider, RecordingPreflightChecker.StorageCapacityProvider); Assert.Same(resolver, RecordingPreflightChecker.AudioHelperPathResolver);
            Assert.Equal(env, SnapshotEnvironment());
        }
        finally { Assert.Equal(parent, Path.GetDirectoryName(Path.GetFullPath(root))); TestDirectoryCleanup.DeleteOwnedDirectory(root); }
    }

    private static string?[] SnapshotEnvironment() => new[] { "TEMP", "TMP", "AGENT_RECORDER_FAKE_HANG", "AGENT_RECORDER_AUDIO_HELPER_EXE", "AGENT_RECORDER_DATA_DIR" }.Select(Environment.GetEnvironmentVariable).ToArray();

    private sealed class OwnedFixture : IAsyncDisposable
    {
        internal readonly string Root;
        internal readonly FakeAudioHelperDeployment Helper;
        internal readonly List<Process> Workers = new();
        internal readonly List<bool> ExitFacts = new();
        internal readonly List<Action> Release = new();
        private readonly List<WindowStorageSafety> _guards = new();
        private readonly string?[] _environment = SnapshotEnvironment();
        private readonly IStorageCapacityProvider _provider = RecordingPreflightChecker.StorageCapacityProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _resolver = RecordingPreflightChecker.AudioHelperPathResolver;
        internal bool CleanupFault;
        internal OwnedFixture()
        {
            var parent = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data", "temp", "task349"));
            Directory.CreateDirectory(parent);
            Root = Path.Combine(parent, "retirement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            try { Helper = new FakeAudioHelperDeployment(Root, stopOwnedProcesses: false); }
            catch (Exception primary)
            {
                try { TestDirectoryCleanup.DeleteOwnedDirectory(Root); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
        }
        internal WindowStorageSafety Guard(IStorageCapacityProvider provider, int timeoutMs = 2000,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            var guard = new WindowStorageSafety(new CaptureWritePaths(Root, null, null), 60, provider)
                { QueryTimeout = TimeSpan.FromMilliseconds(timeoutMs), Delay = delay ?? Task.Delay };
            _guards.Add(guard); return guard;
        }
        internal ProcessStorageCapacityProvider Provider(bool hang, StorageProbeHooks? hooks = null) => new()
        {
            ExecutablePath = () => Helper.ExecutablePath,
            HooksForTests = new StorageProbeHooks
            {
                BeforeStart = info => { if (hang) info.Environment["AGENT_RECORDER_FAKE_HANG"] = "1"; else info.Environment.Remove("AGENT_RECORDER_FAKE_HANG"); hooks?.BeforeStart?.Invoke(info); },
                Started = p => { var held = Process.GetProcessById(p.Id); _ = held.SafeHandle; Assert.Equal(p.StartTime, held.StartTime); Workers.Add(held); hooks?.Started?.Invoke(p); },
                BeforeRead = hooks?.BeforeRead, BeforeCleanup = hooks?.BeforeCleanup, BeforeDispose = hooks?.BeforeDispose,
                Kill = hooks?.Kill, WaitForExit = hooks?.WaitForExit, CleanupWarning = hooks?.CleanupWarning
            }
        };
        internal Process StartHang()
        {
            var p = new Process { StartInfo = new(Helper.ExecutablePath, "--version") { UseShellExecute = false, CreateNoWindow = true } };
            p.StartInfo.Environment["AGENT_RECORDER_FAKE_HANG"] = "1";
            p.Start(); _ = p.SafeHandle; Workers.Add(p); return p;
        }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            foreach (var action in Release) try { action(); } catch (Exception e) { errors.Add(e); }
            foreach (var guard in _guards) try { guard.Dispose(); } catch (Exception e) { errors.Add(e); }
            foreach (var p in Workers)
            {
                try { if (!p.HasExited) p.Kill(); await p.WaitForExitAsync().WaitAsync(Bound); ExitFacts.Add(p.HasExited); }
                catch (Exception e) { errors.Add(e); }
            }
            foreach (var guard in _guards) try { await guard.ProbeRetirementCompletion.WaitAsync(Bound); } catch (Exception e) { errors.Add(e); }
            foreach (var p in Workers) try { p.Dispose(); } catch (Exception e) { errors.Add(e); }
            try
            {
                Assert.Same(_provider, RecordingPreflightChecker.StorageCapacityProvider);
                Assert.Same(_resolver, RecordingPreflightChecker.AudioHelperPathResolver); Assert.Equal(_environment, SnapshotEnvironment());
                if (errors.Count != 0 || ExitFacts.Count != Workers.Count || ExitFacts.Any(exited => !exited))
                    throw new IOException("Owned resources were not retired; private deployment retained.");
                if (CleanupFault) throw new IOException("fixture-cleanup");
                Helper.Dispose();
                var parent = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data", "temp", "task349"));
                Assert.Equal(parent, Path.GetDirectoryName(Path.GetFullPath(Root)));
                TestDirectoryCleanup.DeleteOwnedDirectory(Root);
            }
            catch (Exception e) { errors.Add(e); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }
}
