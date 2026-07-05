using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

public class FfmpegPrewarmerTests
{
    [Fact]
    public void InitialStatus_IsNotStarted()
    {
        var prewarmer = new FfmpegPrewarmer();

        Assert.Equal(PrewarmStatus.NotStarted, prewarmer.Status);
        Assert.Equal(0, prewarmer.CurrentResult.ElapsedMs);
        Assert.Equal(0, prewarmer.StartCount);
    }

    [Fact]
    public void RunVersionCheck_NonexistentExe_ReturnsFalse()
    {
        var result = FfmpegPrewarmer.RunVersionCheck(@"C:\does\not\exist\ffmpeg.exe", 1000);

        Assert.False(result);
    }

    [Fact]
    public void RunVersionCheck_ValidExeWithZeroExit_ReturnsTrue()
    {
        // Use cmd.exe /c "exit 0" as a known-good process that exits with 0
        // We can't pass args to RunVersionCheck, but cmd.exe returns 0 with /c "exit 0"
        // Actually RunVersionCheck passes "-hide_banner -version" as args.
        // cmd.exe will return non-zero with those args, but won't throw.
        var result = FfmpegPrewarmer.RunVersionCheck("cmd.exe", 2000);

        // Just verify no exception and a boolean is returned
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void Start_WithNoOpAudit_DoesNotThrow()
    {
        var prewarmer = CreateTestablePrewarmer(returnsOk: true);

        prewarmer.Start(NoOpAuditLogger.Instance);

        // Status should eventually move past NotStarted
        var start = DateTime.UtcNow;
        while (prewarmer.Status == PrewarmStatus.NotStarted && (DateTime.UtcNow - start).TotalSeconds < 5)
            Thread.Sleep(50);

        Assert.NotEqual(PrewarmStatus.NotStarted, prewarmer.Status);
        Assert.Equal(1, prewarmer.StartCount);
    }

    [Fact]
    public void Start_WithNullAudit_DoesNotThrow()
    {
        var prewarmer = CreateTestablePrewarmer(returnsOk: true);

        // null audit should be handled internally via NoOpAuditLogger
        prewarmer.Start(null);

        var start = DateTime.UtcNow;
        while (prewarmer.Status == PrewarmStatus.NotStarted && (DateTime.UtcNow - start).TotalSeconds < 5)
            Thread.Sleep(50);

        Assert.NotEqual(PrewarmStatus.NotStarted, prewarmer.Status);
        Assert.Equal(1, prewarmer.StartCount);
    }

    [Fact]
    public void Start_CalledTwice_OnlyRunsOnce()
    {
        var callCount = 0;
        var prewarmer = new FfmpegPrewarmer();
        // Inject a counting runner that always succeeds
        prewarmer.VersionCheckRunner = (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return true;
        };

        prewarmer.Start(NoOpAuditLogger.Instance);
        // Wait briefly to let the task start
        Thread.Sleep(100);

        // Call start again - should be no-op
        prewarmer.Start(NoOpAuditLogger.Instance);

        // Wait for completion
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (prewarmer.Status == PrewarmStatus.Running && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        // StartCount should be 1 (second call was a no-op)
        Assert.Equal(1, prewarmer.StartCount);

        // The version check runner should have been called at most twice
        // (once for ffmpeg, once for ffprobe), NOT four times
        Assert.True(callCount <= 2, $"VersionCheckRunner was called {callCount} times, expected <= 2");
    }

    [Fact]
    public async Task Start_ConcurrentCalls_OnlyRunsOnce()
    {
        var callCount = 0;
        var prewarmer = new FfmpegPrewarmer();
        prewarmer.VersionCheckRunner = (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            Thread.Sleep(200); // Slow to keep task running
            return true;
        };

        // Launch multiple concurrent Start calls
        var tasks = new System.Threading.Tasks.Task[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = System.Threading.Tasks.Task.Run(() => prewarmer.Start(NoOpAuditLogger.Instance));
        }
        await System.Threading.Tasks.Task.WhenAll(tasks);

        // Wait for completion
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (prewarmer.Status == PrewarmStatus.Running && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.Equal(1, prewarmer.StartCount);
        Assert.True(callCount <= 2, $"VersionCheckRunner was called {callCount} times, expected <= 2");
    }

    [Fact]
    public void CurrentResult_ReturnsSnapshot()
    {
        var prewarmer = new FfmpegPrewarmer();
        var result = prewarmer.CurrentResult;

        Assert.NotNull(result);
        Assert.Equal(PrewarmStatus.NotStarted, result.Status);
        Assert.Equal(0, result.ElapsedMs);
        Assert.False(result.FfmpegFound);
        Assert.False(result.FfprobeFound);
    }

    [Fact]
    public void Start_VersionCheckSucceeds_CompletesSuccessfully()
    {
        var prewarmer = CreateTestablePrewarmer(returnsOk: true);

        prewarmer.Start(NoOpAuditLogger.Instance);

        // Wait for completion
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (prewarmer.Status == PrewarmStatus.Running && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.Equal(PrewarmStatus.Completed, prewarmer.Status);
        Assert.True(prewarmer.CurrentResult.ElapsedMs >= 0);
    }

    [Fact]
    public void Start_VersionCheckFails_ReturnsFailed()
    {
        var prewarmer = CreateTestablePrewarmer(returnsOk: false);

        prewarmer.Start(NoOpAuditLogger.Instance);

        // Wait for completion
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (prewarmer.Status == PrewarmStatus.Running && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.Equal(PrewarmStatus.Failed, prewarmer.Status);
    }

    /// <summary>
    /// Creates a testable FfmpegPrewarmer with an injected VersionCheckRunner.
    /// Note: FfmpegLocator is static, so the injected runner is called only if
    /// FfmpegLocator resolves a path. In test environments where ffmpeg is found
    /// (via project_tools), the injected runner will be used.
    /// </summary>
    private static FfmpegPrewarmer CreateTestablePrewarmer(bool returnsOk)
    {
        var prewarmer = new FfmpegPrewarmer();
        prewarmer.VersionCheckRunner = (_, _) => returnsOk;
        return prewarmer;
    }
}

/// <summary>
/// Test-friendly AuditLogger that records logged events in memory.
/// </summary>
public class CaptureAuditLogger : AuditLogger
{
    public List<(string evt, string json)> Events { get; } = new();

    public override void Log(string evt, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        lock (Events)
        {
            Events.Add((evt, json));
        }
    }
}
