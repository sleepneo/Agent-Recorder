using System.Diagnostics;
using System.Threading.Channels;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class WindowStorageSafetyTests
{
    private static readonly CaptureWritePaths Paths = new(@"D:\output", @"D:\data\temp", @"C:\temp\AgentRecorder");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdmissionAggregatesSimultaneousMediaByActualVolume(bool audio, bool sameVolume)
    {
        var paths = audio ? Paths : Paths with { AvTempDirectory = null };
        var provider = new FakeStorageProvider { Volume = p => sameVolume ? "mounted-volume" : p };
        using var guard = new WindowStorageSafety(paths, 1800, provider);
        var samples = await provider.QueryAsync(paths.Directories, default);
        var budgets = guard.AdmissionBudgets(samples);
        const long video = 1800L * 2 * 1024 * 1024;
        long sum = (audio ? video * 5 + 4096 : video * 2)
            + budgets.Count * WindowStorageSafety.LowWaterBytes;
        Assert.Equal(sameVolume ? 1 : audio ? 3 : 2, budgets.Count);
        Assert.Equal(sum, budgets.Values.Sum());
        provider.Free = p => budgets[provider.Volume(p)];
        Assert.Null(await guard.CheckAsync(true));
        provider.Free = p => budgets[provider.Volume(p)] - 1;
        Assert.Equal(WindowStorageSafety.LowSpaceCode, await guard.CheckAsync(true));
    }

    [Fact]
    public async Task OutputAndAvOnSameVolumeSumBothRolesAndOnlyOneWatermark()
    {
        var provider = new FakeStorageProvider { Volume = p => p == Paths.WgcTempRoot ? "wgc-volume" : "output-av-volume" };
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        var budgets = guard.AdmissionBudgets(await provider.QueryAsync(Paths.Directories, default));
        const long video = 60L * 2 * 1024 * 1024;
        Assert.Equal(2, budgets.Count);
        Assert.Equal(video * 4 + 4096 + WindowStorageSafety.LowWaterBytes, budgets["output-av-volume"]);
        Assert.Equal(video + WindowStorageSafety.LowWaterBytes, budgets["wgc-volume"]);
        provider.Free = p => budgets[provider.Volume(p)];
        Assert.Null(await guard.CheckAsync(true));
        provider.Free = p => budgets[provider.Volume(p)] - (p == Paths.AvTempDirectory ? 1 : 0);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, await guard.CheckAsync(true));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("av")]
    [InlineData("wgc")]
    public async Task SufficientOutputNeverMasksAnInsufficientTemporaryVolume(string lowPath)
    {
        var selected = lowPath == "output" ? Paths.OutputDirectory : lowPath == "av" ? Paths.AvTempDirectory! : Paths.WgcTempRoot;
        var provider = new FakeStorageProvider { Free = p => p == selected ? 1 : 100L * 1024 * 1024 * 1024 };
        using var guard = new WindowStorageSafety(Paths, 1800, provider);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, await guard.CheckAsync(true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RuntimeWatermarkIsInclusive(long delta)
    {
        var provider = new FakeStorageProvider { Free = _ => WindowStorageSafety.LowWaterBytes + delta };
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        Assert.Equal(delta < 0 ? WindowStorageSafety.LowSpaceCode : null, await guard.CheckAsync(false));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("exception")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("identity")]
    public async Task InvalidOrUnavailableCapacityFailsClosed(string fault)
    {
        var provider = new FakeStorageProvider
        {
            Query = (paths, _) =>
            {
                if (fault == "exception") throw new IOException("volume gone");
                var samples = paths.Select(p => new StorageVolumeSample(p, fault == "identity" ? "" : p,
                    fault == "negative" ? -1 : long.MaxValue)).ToList();
                if (fault == "missing") samples.RemoveAt(0);
                if (fault == "duplicate") samples[0] = samples[1];
                return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(samples);
            }
        };
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(true));
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
    }

    [Fact]
    public async Task RemountCannotSilentlyChangeTheAdmittedVolume()
    {
        var provider = new FakeStorageProvider();
        using var guard = new WindowStorageSafety(Paths, 60, provider);
        Assert.Null(await guard.CheckAsync(true));
        provider.Volume = _ => "replacement-volume";
        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorSamplesAtFiveSecondsAndAbortsOnceWithoutSelfWaiting(bool unavailable)
    {
        var provider = new FakeStorageProvider();
        var ticks = new StorageTestClock();
        using var guard = new WindowStorageSafety(Paths, 60, provider) { Delay = ticks.Delay };
        guard.EnsureAdmission();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        guard.Start(code => { Interlocked.Increment(ref callbacks); guard.Dispose(); failure.TrySetResult(code); });
        guard.Start(_ => throw new Exception("duplicate monitor"));
        await ticks.WaitForDelay();
        Assert.Equal(1, provider.Calls);
        provider.Free = _ => WindowStorageSafety.LowWaterBytes - 1;
        if (unavailable) provider.Query = (_, _) => throw new IOException("removed volume");
        ticks.Tick();
        Assert.Equal(unavailable ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode,
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, callbacks);
        Assert.All(ticks.Intervals, interval => Assert.Equal(TimeSpan.FromSeconds(5), interval));
        Assert.False(guard.TryCommit(() => Assert.Fail("failed storage cannot publish"), true));
    }

    [Fact]
    public async Task BlockedProbeTimesOutWithOneQueryAndMonitorConverges()
    {
        var provider = new FakeStorageProvider();
        var ticks = new StorageTestClock();
        using var guard = new WindowStorageSafety(Paths, 60, provider)
            { Delay = ticks.Delay, QueryTimeout = TimeSpan.FromMilliseconds(30) };
        guard.EnsureAdmission();
        var blocked = new TaskCompletionSource<IReadOnlyList<StorageVolumeSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Query = (_, _) => blocked.Task;
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Start(code => failure.TrySetResult(code));
        await ticks.WaitForDelay(); ticks.Tick();
        Assert.Equal(WindowStorageSafety.UnavailableCode, await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.Calls);
        blocked.SetResult(Paths.Directories.Select(p => new StorageVolumeSample(p, p, 0)).ToArray());
        Assert.Equal(WindowStorageSafety.UnavailableCode, guard.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateProbeCannotChangeCommittedOrDisposedRunOrNextRun(bool dispose)
    {
        var provider = new FakeStorageProvider();
        var ticks = new StorageTestClock();
        using var guard = new WindowStorageSafety(Paths, 60, provider) { Delay = ticks.Delay };
        guard.EnsureAdmission();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<IReadOnlyList<StorageVolumeSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Query = (_, _) => { entered.TrySetResult(); return late.Task; };
        int failures = 0;
        guard.Start(_ => Interlocked.Increment(ref failures));
        await ticks.WaitForDelay(); ticks.Tick(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        if (dispose) guard.Dispose();
        else Assert.True(guard.TryCommit(() => { }, true));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        late.TrySetResult(Paths.Directories.Select(p => new StorageVolumeSample(p, p, 0)).ToArray());
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(guard.FailureCode);
        Assert.Equal(0, failures);
        using var next = new WindowStorageSafety(Paths, 60, new FakeStorageProvider());
        Assert.Null(await next.CheckAsync(true));
    }

    [Fact]
    public void AbortAndCommitHaveOneAtomicOrderAndIntermediatePublicationDoesNotEndProtection()
    {
        using var guard = new WindowStorageSafety(Paths, 60, new FakeStorageProvider());
        int moves = 0;
        Assert.True(guard.TryCommit(() => moves++, false));
        Assert.False(guard.Committed);
        Assert.True(guard.TryAbort(WindowStorageSafety.LowSpaceCode));
        Assert.False(guard.TryCommit(() => moves++, true));
        Assert.Equal(1, moves);
        using var committed = new WindowStorageSafety(Paths, 60, new FakeStorageProvider());
        Assert.True(committed.TryCommit(() => moves++, true));
        Assert.False(committed.TryAbort(WindowStorageSafety.UnavailableCode));
        Assert.Null(committed.FailureCode);
    }

    [Fact]
    public async Task ProductionProbeResolvesRealVolumeAndDoesNotInitializeAppRuntime()
    {
        using var temp = new TempDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var provider = new ProcessStorageCapacityProvider();
        var samples = await provider.QueryAsync(new[] { temp.Path }, timeout.Token);
        var direct = WindowsStorageCapacityProbe.Query(temp.Path);
        Assert.Equal(direct.VolumeIdentity, Assert.Single(samples).VolumeIdentity);
        Assert.True(samples[0].AvailableBytes > 0);
        Assert.StartsWith(@"\\?\VOLUME{", samples[0].VolumeIdentity);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void FrozenPathsCannotHideChangedOutputDirectoryAndAvAdapterUsesFrozenAvDirectory()
    {
        var cfg = new CaptureConfig { OutputPath = @"D:\output\final.mp4" };
        Paths.ValidateOutputPath(cfg);
        cfg.OutputPath = @"E:\elsewhere\final.mp4";
        Assert.Equal(WindowStorageSafety.UnavailableCode,
            Assert.Throws<StorageSafetyException>(() => Paths.ValidateOutputPath(cfg)).Code);
        cfg.StorageIntermediatePublication = true;
        cfg.OutputPath = @"D:\data\temp\video.mp4";
        Paths.ValidateOutputPath(cfg);
    }

    [Fact]
    public void DirectoryJunctionUsesTargetVolumeRatherThanLexicalDriveRoot()
    {
        using var temp = new TempDirectory();
        // The link belongs to this test; the existing target is read-only.
        var target = Path.GetPathRoot(Environment.SystemDirectory)!;
        var link = Path.Combine(temp.Path, "volume-junction");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var actual = WindowsStorageCapacityProbe.Query(Path.Combine(link, "nonexistent-child"));
            var expected = WindowsStorageCapacityProbe.Query(target);
            Assert.Equal(expected.VolumeIdentity, actual.VolumeIdentity);
        }
        finally { Directory.Delete(link, recursive: false); }
    }
}

internal sealed class FakeStorageProvider : IStorageCapacityProvider
{
    internal Func<string, long> Free = _ => 100L * 1024 * 1024 * 1024;
    internal Func<string, string> Volume = p => p;
    internal Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<StorageVolumeSample>>>? Query;
    internal int Calls;
    public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        return Query?.Invoke(paths, token) ?? Task.FromResult<IReadOnlyList<StorageVolumeSample>>(
            paths.Select(p => new StorageVolumeSample(p, Volume(p), Free(p))).ToArray());
    }
}

internal sealed class StorageTestClock
{
    private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> _waiting = Channel.CreateUnbounded<bool>();
    internal List<TimeSpan> Intervals { get; } = new();
    internal async Task Delay(TimeSpan interval, CancellationToken token)
    {
        Intervals.Add(interval);
        _waiting.Writer.TryWrite(true);
        await _ticks.Reader.ReadAsync(token);
    }
    internal async Task WaitForDelay() => await _waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    internal void Tick() => _ticks.Writer.TryWrite(true);
}
