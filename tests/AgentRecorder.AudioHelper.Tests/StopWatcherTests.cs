using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class StopWatcherTests
{
    [Fact]
    public void Start_CalledTwice_StartsPollingThreadOnlyOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var stopSignal = Path.Combine(dir, "stop.signal");

        var watcher = new StopWatcher(stopSignal, () => { });

        Assert.Equal(0, watcher.StartCount);
        Assert.False(watcher.Started);

        watcher.Start();
        // A duplicate Start must be a safe no-op, not an opaque
        // ThreadStateException from a second Thread.Start().
        watcher.Start();

        Assert.Equal(1, watcher.StartCount);
        Assert.True(watcher.Started);

        watcher.Dispose();
        Assert.True(watcher.PollingExited, "Polling thread must have exited after Dispose; no background polling left behind");

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Dispose_WithoutStart_LeavesNoThreadAndKeepsStartCountZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ah_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var stopSignal = Path.Combine(dir, "stop.signal");

        var watcher = new StopWatcher(stopSignal, () => { });

        // Disposing a watcher that was never started must be safe and must not
        // implicitly start anything.
        watcher.Dispose();

        Assert.Equal(0, watcher.StartCount);
        Assert.False(watcher.Started);
        Assert.False(watcher.Triggered);

        Directory.Delete(dir, true);
    }
}
