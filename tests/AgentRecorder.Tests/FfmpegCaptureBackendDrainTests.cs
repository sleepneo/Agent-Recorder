using System;
using System.IO;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class FfmpegCaptureBackendDrainTests
{
    [Fact]
    public async Task RunStdoutReader_ReadsToEof_AndFlushes()
    {
        var backend = new FfmpegCaptureBackend();
        var parser = new FFmpegProgressParser();
        FirstFrameObservation? observed = null;
        parser.GroupCompleted += g =>
        {
            if (g.HasFirstFrameEvidence)
                observed = new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize,
                    OutTimeUs = g.OutTimeUs
                };
        };

        // Simulate FFmpeg writing its final progress group after the process has
        // exited but before stdout is closed. A loop that checks HasExited after
        // each ReadLine would discard the final line; this reader must read to EOF.
        using var reader = new StringReader("frame=1\ntotal_size=1234\nout_time_us=0\nprogress=end");
        var task = backend.RunStdoutReader(reader, parser);

        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FrameNumber);
        Assert.Equal(1234, observed.TotalSizeBytes);
    }

    [Fact]
    public async Task RunStdoutReader_SwallowsObserverException()
    {
        var backend = new FfmpegCaptureBackend();
        var parser = new FFmpegProgressParser();
        parser.GroupCompleted += _ => throw new InvalidOperationException("boom");

        using var reader = new StringReader("frame=1\ntotal_size=100\nprogress=continue");
        var ex = await Record.ExceptionAsync(async () =>
        {
            var task = backend.RunStdoutReader(reader, parser);
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void DrainReaderTask_WaitsForCompletion()
    {
        var backend = new FfmpegCaptureBackend();
        var tcs = new TaskCompletionSource();
        var readerTask = tcs.Task;

        Task.Run(async () =>
        {
            await Task.Delay(200);
            tcs.SetResult();
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.DrainReaderTask(readerTask, TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(readerTask.IsCompleted);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), "Drain should wait for the reader task");
    }

    [Fact]
    public void DrainReaderTask_TimesOutOnHungReader()
    {
        var backend = new FfmpegCaptureBackend();
        var tcs = new TaskCompletionSource();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.DrainReaderTask(tcs.Task, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        Assert.False(tcs.Task.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "Drain must return after timeout");
    }

    [Fact]
    public void DrainReaderTask_SwallowsFaultedReader()
    {
        var backend = new FfmpegCaptureBackend();
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("reader fault"));

        var ex = Record.Exception(() => backend.DrainReaderTask(tcs.Task, TimeSpan.FromSeconds(2)));
        Assert.Null(ex);
    }
}
