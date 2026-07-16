using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests the real production orchestration paths inside <see cref="FfmpegCaptureBackend"/>
/// (natural-exit watcher drain/finalization and Stop drain/return) without
/// launching FFmpeg or any other real process.
/// </summary>
public class FfmpegCaptureBackendLifecycleTests
{
    [Fact]
    public async Task NaturalExit_WaitsForStdoutReader_BeforeInvokingCallback()
    {
        var backend = new FfmpegCaptureBackend();
        var readerTcs = new TaskCompletionSource();
        bool callbackFired = false;

        var task = Task.Run(() => backend.RunNaturalExitLifecycle(
            readerTcs.Task, 0, "nonexistent.mp4", new StringBuilder(), TimeSpan.FromSeconds(5),
            (code, meta) => callbackFired = true));

        // Give the orchestration method time to reach DrainTask.
        var timeoutEx = await Record.ExceptionAsync(async () => await task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.NotNull(timeoutEx);
        Assert.IsType<TimeoutException>(timeoutEx);
        Assert.False(callbackFired);
        Assert.False(task.IsCompleted);

        readerTcs.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(task.IsCompleted);
        Assert.True(callbackFired);
    }

    [Fact]
    public void NaturalExit_FirstFrameObservation_PrecedesCallback()
    {
        var backend = new FfmpegCaptureBackend();
        var parser = new FFmpegProgressParser();
        bool groupObserved = false;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                groupObserved = true;
        };
        bool callbackFired = false;

        using var reader = new StringReader("frame=1\ntotal_size=1234\nprogress=end");
        var readerTask = backend.RunStdoutReader(reader, parser);

        backend.RunNaturalExitLifecycle(readerTask, 0, "nonexistent.mp4", new StringBuilder(), TimeSpan.FromSeconds(5),
            (code, meta) => callbackFired = true);

        Assert.True(groupObserved, "Reader should have produced a first-frame progress group");
        Assert.True(callbackFired, "Natural-exit callback should have fired");
    }

    [Fact]
    public void NaturalExit_ReaderNeverCompletes_CallbackFiresAfterDrainTimeout()
    {
        var backend = new FfmpegCaptureBackend();
        var hungReader = new TaskCompletionSource().Task;
        bool callbackFired = false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.RunNaturalExitLifecycle(hungReader, 0, "nonexistent.mp4", new StringBuilder(), TimeSpan.FromMilliseconds(50),
            (code, meta) => callbackFired = true);
        sw.Stop();

        Assert.True(callbackFired);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(40), "Should wait roughly until drain timeout");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "Should not hang indefinitely");
    }

    [Fact]
    public async Task Stop_WaitsForStdoutReader_BeforeReturning()
    {
        var backend = new FfmpegCaptureBackend();
        var readerTcs = new TaskCompletionSource();

        var task = Task.Run(() => backend.RunStopLifecycle(readerTcs.Task, "nonexistent.mp4", "", TimeSpan.FromSeconds(5)));

        var timeoutEx = await Record.ExceptionAsync(async () => await task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.NotNull(timeoutEx);
        Assert.IsType<TimeoutException>(timeoutEx);
        Assert.False(task.IsCompleted);

        readerTcs.SetResult();
        var meta = await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(meta);
    }

    [Fact]
    public void Stop_ReaderNeverCompletes_ReturnsAfterDrainTimeout()
    {
        var backend = new FfmpegCaptureBackend();
        var hungReader = new TaskCompletionSource().Task;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meta = backend.RunStopLifecycle(hungReader, "nonexistent.mp4", "", TimeSpan.FromMilliseconds(50));
        sw.Stop();

        Assert.NotNull(meta);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(40), "Should wait roughly until drain timeout");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "Should not hang indefinitely");
    }

    [Fact]
    public void Stop_ReaderFault_ReturnsWithoutThrowing()
    {
        var backend = new FfmpegCaptureBackend();
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("reader fault"));

        var ex = Record.Exception(() => backend.RunStopLifecycle(tcs.Task, "nonexistent.mp4", "", TimeSpan.FromSeconds(2)));

        Assert.Null(ex);
    }
}
