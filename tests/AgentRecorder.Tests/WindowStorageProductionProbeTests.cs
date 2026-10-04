using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class WindowStorageProductionProbeTests
{
    [Fact]
    public async Task MissingOutputLeafUsesRealAncestorVolumeWithoutCreatingRuntimeOrDirectories()
    {
        using var temp = new TempDirectory();
        var paths = new[]
        {
            Path.Combine(temp.Path, "output", "not-created"),
            Path.Combine(temp.Path, "av", "not-created"),
            Path.Combine(temp.Path, "wgc", "not-created")
        };
        var provider = new ProcessStorageCapacityProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var samples = await provider.QueryAsync(paths, timeout.Token);
        var expectedVolume = WindowsStorageCapacityProbe.Query(temp.Path).VolumeIdentity;

        Assert.Equal(paths.Length, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.Contains(sample.Path, paths);
            Assert.Equal(expectedVolume, sample.VolumeIdentity);
            Assert.True(sample.AvailableBytes > 0);
        });
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(provider.LastProbeProcessId));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("av")]
    [InlineData("wgc")]
    public async Task ExistingFileInAnyWriteRoleRejectsTheWholeRealProbe(string blockedRole)
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "output");
        var av = Path.Combine(temp.Path, "av");
        var wgc = Path.Combine(temp.Path, "wgc");
        var blocked = blockedRole == "output" ? output : blockedRole == "av" ? av : wgc;
        File.WriteAllText(blocked, "owned storage probe fixture");
        var provider = new ProcessStorageCapacityProvider();
        using var guard = new WindowStorageSafety(new CaptureWritePaths(output, av, wgc), 1, provider);

        Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(admission: true));
        var failure = Assert.Throws<StorageSafetyException>(() => guard.EnsureAdmission());
        Assert.Equal(WindowStorageSafety.UnavailableCode, failure.Code);
        Assert.False(guard.TryCommit(() => Assert.Fail("Unavailable storage must not publish"), true));
        Assert.Equal("owned storage probe fixture", File.ReadAllText(blocked));
        Assert.Single(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task RealProbeDetectsDirectoryBecomingAFileAndMonitorAbortsOnce()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "output");
        var wgc = Path.Combine(temp.Path, "wgc");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(wgc);
        var provider = new ProcessStorageCapacityProvider();
        var clock = new StorageTestClock();
        using var guard = new WindowStorageSafety(new CaptureWritePaths(output, null, wgc), 1, provider)
        {
            Delay = clock.Delay
        };
        guard.EnsureAdmission();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        guard.Start(code =>
        {
            Interlocked.Increment(ref notifications);
            failure.TrySetResult(code);
        });
        await clock.WaitForDelay();

        // Only replace this test's newly created, empty, non-reparse directory.
        Assert.StartsWith(temp.Path + Path.DirectorySeparatorChar, Path.GetFullPath(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal((FileAttributes)0, File.GetAttributes(output) & FileAttributes.ReparsePoint);
        Directory.Delete(output, recursive: false);
        File.WriteAllText(output, "owned directory replacement fixture");
        clock.Tick();

        Assert.Equal(WindowStorageSafety.UnavailableCode,
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, notifications);
        Assert.True(guard.AbortToken.IsCancellationRequested);
        Assert.False(guard.TryCommit(() => Assert.Fail("Failed storage must not publish"), true));
        Assert.Equal("owned directory replacement fixture", File.ReadAllText(output));
        Assert.All(clock.Intervals, interval => Assert.Equal(TimeSpan.FromSeconds(5), interval));
    }
}
