using System.Collections;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static Fixture SeriesBundleFixture(BundleUseAudit? audit = null) => new(
        testRoot: Path.Combine(Path.GetTempPath(), "series-bundle"), runtimeAudit: audit);
    private static Recording CreateSeriesBundle(Fixture f, string directory, string name = "frames")
    {
        f.Engine.CreateRecording(SeriesBundleRequest(f, directory, name), "task379", f.Tray);
        return f.Engine._recs.Values.Single(r => r.IsScreenshotSeries && r.State == RecState.pending_confirmation);
    }
    private static async Task<Task> SeriesBundleHostTask(Fixture f, Recording rec)
    {
        Task? host = null;
        await Until(() =>
        {
            lock (OutputLock(f.Engine))
            {
                var map = (IDictionary)typeof(RecordingEngine).GetField("_ordinarySeriesOutputUses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Engine)!;
                var use = map[rec.Id];
                host = use?.GetType().GetField("Worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(use) as Task;
                return host is not null;
            }
        });
        return host!;
    }
    private static bool SeriesBundleInUse(Fixture f, string path) => Read(f.Engine, path)!.Paths.Single().InUse;

    [Theory]
    [InlineData("queued", false, "case")]
    [InlineData("queued", true, "slashes")]
    [InlineData("generating", false, "slashes")]
    [InlineData("generating", true, "exact")]
    [InlineData("ready", false, "exact")]
    [InlineData("ready", true, "case")]
    [InlineData("failed", false, "case")]
    [InlineData("failed", true, "slashes")]
    public async Task SeriesBundleActualApiAndLocalMovesRespectEveryLiveBundlePhase(string phase, bool local, string spelling)
    {
        var audit = new BundleUseAudit();
        using var tail = new ManualResetEventSlim();
        var atTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = SeriesBundleFixture(audit); await f.Prepare();
        var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        if (phase is "ready" or "failed") audit.Before = evt =>
        {
            if (evt != "recording.bundle_" + phase) return;
            atTail.TrySetResult(); if (!tail.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("owned Bundle tail gate");
        };
        if (phase == "failed") f.Generator.Fault = _ => Task.FromResult(
            RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed, "owned_fault"));
        Func<Task>? queued = null;
        if (phase == "queued") f.Engine.BundleGenerationDispatcherForTests = work => { queued = work; return Task.CompletedTask; };
        var video = f.Create("display"); await f.Approve(video); await f.Terminal(video, false);
        var directory = Path.ChangeExtension(video.OutputPath, ".bundle");
        var child = Path.Combine(directory, "denied");
        if (spelling == "case") child = child.ToUpperInvariant();
        if (spelling == "slashes") child = child.Replace('\\', '/');
        Task? host = null;
        try
        {
            if (phase != "queued")
            {
                await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); host = HostTask(f);
                if (phase is "ready" or "failed")
                { f.Generator.Release.TrySetResult(); await atTail.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(host.IsCompleted); }
            }
            var videoHash = ScreenshotSeriesArtifacts.Sha256File(video.OutputPath);
            Assert.True(SeriesBundleInUse(f, Path.Combine(directory, "any.png")));
            Assert.False(SeriesBundleInUse(f, Path.Combine(directory + "-neighbor", "any.png")));
            var beforeConfirmations = f.Engine._confs.Count;
            f.StartApi(); using var client = f.Client();
            using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0", new StringContent(
                SeriesBundleRequest(f, local ? Path.Combine(f.Root, "old") : child).ToJsonString(), Encoding.UTF8, "application/json"));
            if (local)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var series = f.Engine._recs.Values.Single(r => r.IsScreenshotSeries);
                var old = series.OutputPath;
                Assert.True(SeriesBundleInUse(f, Path.Combine(old, "frame.png")));
                f.Tray.Confirm!(ConfirmationDecision.Approve(child));
                Assert.Equal(RecState.rejected, series.State); Assert.Equal("OUTPUT_PATH_IN_USE", series.StopReason);
                Assert.Null(series.AuthorizationProof);
                Assert.False(SeriesBundleInUse(f, Path.Combine(old, "frame.png")));
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
                Assert.Equal(beforeConfirmations, f.Engine._confs.Count); Assert.Single(f.Engine._recs);
            }
            Assert.False(Directory.Exists(child)); Assert.Equal(0, runner.Calls);
            Assert.Equal(0, f.Engine.ActiveScreenshotSeriesOperationCountForTests); Assert.Equal(1, f.FactoryCalls);
            Assert.Equal(videoHash, ScreenshotSeriesArtifacts.Sha256File(video.OutputPath));
            SaveSeriesBundleEvidence($"phase-{phase}-{local}-{spelling}", new { phase, local, spelling, frameCalls = runner.Calls,
                childCreated = Directory.Exists(child), mainIntact = true, bundle = video.BundleSnapshot.Status });
        }
        finally
        {
            tail.Set(); f.Generator.Release.TrySetResult();
            if (queued is not null) await queued();
            if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Until(() => BundleUseCount(f) == 0);
        Assert.Equal(phase == "failed" ? "failed" : "ready", video.BundleSnapshot.Status);
        Assert.Equal(RecState.completed, video.State);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task SeriesBundleReverseOrderUsesActualSeriesHostTailWithoutRollingBackVideo(string status)
    {
        var audit = new BundleUseAudit(); using var release = new ManualResetEventSlim();
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = SeriesBundleFixture(audit); await f.Prepare();
        var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        runner.Before = async (_, token) =>
        {
            frame.TrySetResult(); await releaseFrame.Task.WaitAsync(token);
            if (status == "failed") throw new IOException("owned frame fault");
        };
        audit.Before = evt =>
        {
            if (evt != "recording.series_" + status) return;
            tail.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("owned series tail");
        };
        var bundlePath = Path.Combine(f.Root, "Videos", "capture.bundle");
        var series = CreateSeriesBundle(f, Path.Combine(bundlePath, "screens"));
        f.Tray.Confirm!(ConfirmationDecision.Approve());
        Task? stop = null; Task? seriesHost = null;
        try
        {
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5)); seriesHost = await SeriesBundleHostTask(f, series);
            if (status == "cancelled") stop = Task.Run(() => f.Engine.Stop(series.Id, "user_requested"));
            else releaseFrame.TrySetResult();
            await tail.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(series.IsFinalized); Assert.False(seriesHost.IsCompleted);
            Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "late.png")));
            f.StartApi(); using var client = f.Client();
            using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0", new StringContent(
                f.Request("display").ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var video = f.Engine._recs.Values.Single(r => r.Mode == "video");
            await f.Approve(video); await f.Terminal(video, false);
            await Until(() => video.BundleSnapshot.Status == "failed");
            Assert.Equal(0, f.Generator.Calls); Assert.Equal(RecState.completed, video.State);
            Assert.True(File.Exists(video.OutputPath));
            Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "late.png")));
            if (status == "completed")
            {
                Assert.True(File.Exists(Path.Combine(series.ScreenshotSeries!.FinalDirectory!, "series.json")));
                Assert.Single(Directory.EnumerateFiles(series.ScreenshotSeries.FinalDirectory!, "*.png"));
            }
        }
        finally
        {
            releaseFrame.TrySetResult(); release.Set();
            if (seriesHost is not null) await seriesHost.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Until(() => !SeriesBundleInUse(f, Path.Combine(series.OutputPath, "late.png")));
        Assert.Equal(status, series.ScreenshotSeries!.Status);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("throws")]
    [InlineData("faults")]
    [InlineData("null")]
    public async Task SeriesBundleQueuedOrUnknownDispatchRetainsUntilOriginalHostExits(string dispatch)
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        Func<Task>? queued = null;
        f.Engine.ScreenshotSeriesDispatcherForTests = work =>
        {
            queued = work;
            if (dispatch == "throws") throw new IOException("owned unknown dispatch");
            return dispatch == "null" ? null! : dispatch == "faults" ? Task.FromException(new IOException("owned unknown ack")) : Task.CompletedTask;
        };
        var series = CreateSeriesBundle(f, Path.Combine(f.Root, "output"));
        f.Tray.Confirm!(ConfirmationDecision.Approve());
        Assert.NotNull(queued); Assert.Equal(0, runner.Calls); Assert.NotNull(series.AuthorizationProof);
        Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "queued.png")));
        var stop = Task.Run(() => f.Engine.Stop(series.Id, "user_requested"));
        try
        {
            await Until(() => series.State == RecState.stopping);
            Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "queued.png")));
            await queued!().WaitAsync(TimeSpan.FromSeconds(5)); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (!stop.IsCompleted) await queued!().WaitAsync(TimeSpan.FromSeconds(5)); }
        await Until(() => !SeriesBundleInUse(f, Path.Combine(series.OutputPath, "queued.png")));
        Assert.Equal(RecState.cancelled, series.State); Assert.Equal(0, runner.Calls);
        var next = CreateSeriesBundle(f, Path.Combine(f.Root, "output"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued!());
        Assert.True(SeriesBundleInUse(f, Path.Combine(next.OutputPath, "new.png")));
        f.Tray.Confirm!(ConfirmationDecision.Reject());
        Assert.False(SeriesBundleInUse(f, Path.Combine(next.OutputPath, "new.png")));
    }

    [Fact]
    public async Task SeriesBundleSuccessfulLocalReplacementKeepsNewUseThroughPublication()
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        Func<Task>? queued = null;
        f.Engine.ScreenshotSeriesDispatcherForTests = work => { queued = work; return Task.CompletedTask; };
        var series = CreateSeriesBundle(f, Path.Combine(f.Root, "old")); var old = series.OutputPath;
        var callback = f.Tray.Confirm!;
        callback(ConfirmationDecision.Approve(Path.Combine(f.Root, "new")));
        var next = series.OutputPath;
        Assert.NotEqual(old, next); Assert.False(SeriesBundleInUse(f, Path.Combine(old, "old.png")));
        Assert.True(SeriesBundleInUse(f, Path.Combine(next, "new.png")));
        callback(ConfirmationDecision.Reject()); // losing callback cannot retire the replacement
        Assert.True(SeriesBundleInUse(f, Path.Combine(next, "new.png")));
        await queued!();
        await Until(() => !SeriesBundleInUse(f, Path.Combine(next, "new.png")));
        Assert.Equal(RecState.completed, series.State); Assert.Equal(1, runner.Calls);
        Assert.Single(Directory.EnumerateFiles(series.ScreenshotSeries!.FinalDirectory!, "*.png"));
        Assert.True(File.Exists(Path.Combine(series.ScreenshotSeries.FinalDirectory!, "series.json")));
    }
}
