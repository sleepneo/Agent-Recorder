using System.Collections;
using System.Reflection;
using System.Net;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static object SeriesBundleUse(Fixture f, Recording rec)
    {
        lock (OutputLock(f.Engine))
            return ((IDictionary)typeof(RecordingEngine).GetField("_ordinarySeriesOutputUses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Engine)!)[rec.Id]!;
    }



    [Theory]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("expire")]
    public void SeriesBundleNeverDispatchedTerminalPathsRetireWithoutProofOrFrames(string outcome)
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        var series = CreateSeriesBundle(f, Path.Combine(f.Root, "output")); var callback = f.Tray.Confirm!;
        Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "pending.png")));
        if (outcome == "reject") callback(ConfirmationDecision.Reject());
        if (outcome == "cancel") f.Engine.Stop(series.Id, "user_requested");
        if (outcome == "expire") f.Engine.TriggerConfirmationExpiryForTests(series.ConfirmationId!);
        Assert.True(series.IsFinalized); Assert.Null(series.AuthorizationProof); Assert.Equal(0, runner.Calls);
        Assert.False(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "pending.png")));
        callback(ConfirmationDecision.Approve());
        Assert.Equal(0, runner.Calls); Assert.Equal(0, f.Engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task SeriesBundleFrameCancellationDoesNotRetireBeforeActualRunnerAndHostReturn()
    {
        using var f = SeriesBundleFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new SeriesBundlePngRunner { Before = (_, _) => { entered.TrySetResult(); return release.Task; } };
        ConfigureSeriesBundle(f, runner); var series = CreateSeriesBundle(f, Path.Combine(f.Root, "output"));
        f.Tray.Confirm!(ConfirmationDecision.Approve());
        Task? stop = null; Task? host = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); host = await SeriesBundleHostTask(f, series);
            stop = Task.Run(() => f.Engine.Stop(series.Id, "user_requested"));
            await Until(() => series.State == RecState.stopping);
            Assert.False(host.IsCompleted); Assert.True(SeriesBundleInUse(f, Path.Combine(series.OutputPath, "held.png")));
        }
        finally
        {
            release.TrySetResult();
            if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Until(() => !SeriesBundleInUse(f, Path.Combine(series.OutputPath, "held.png")));
        Assert.Equal(RecState.cancelled, series.State); Assert.Equal(1, runner.Calls);
        Assert.False(Directory.Exists(series.OutputPath));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("fail")]
    [InlineData("overwrite")]
    public async Task SeriesBundleFormalPublicationKeepsStaticConflictPolicies(string policy)
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        var directory = Path.Combine(f.Root, "output"); var existing = Path.Combine(directory, "frames");
        Directory.CreateDirectory(existing); File.WriteAllText(Path.Combine(existing, "owned.txt"), "unchanged");
        var request = SeriesBundleRequest(f, directory); request["output"]!["conflict_policy"] = policy;
        f.Engine.CreateRecording(request, "task379", f.Tray);
        var series = f.Engine._recs.Values.Single(); f.Tray.Confirm!(ConfirmationDecision.Approve());
        await Until(() => series.IsFinalized && !SeriesBundleInUse(f, Path.Combine(series.OutputPath, "held.png")));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(existing, "owned.txt")));
        Assert.Equal(policy == "rename" ? RecState.completed : RecState.failed, series.State);
        if (policy == "rename")
        {
            Assert.Equal(existing + "-1", series.ScreenshotSeries!.FinalDirectory);
            Assert.True(File.Exists(Path.Combine(series.ScreenshotSeries.FinalDirectory!, "series.json")));
        }
        else Assert.Equal("series_publish_failed", series.Error);
    }

    [Fact]
    public async Task SeriesBundlePublicationRenameRechecksAnotherExactLiveDirectoryUse()
    {
        var audit = new BundleUseAudit(); using var releaseTail = new ManualResetEventSlim();
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = SeriesBundleFixture(audit); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        runner.Before = async (_, _) =>
        {
            if (runner.Calls != 1) return;
            frame.TrySetResult(); await releaseFrame.Task; throw new IOException("owned first frame failure");
        };
        var tails = 0;
        audit.Before = evt =>
        {
            if (evt != "recording.series_failed" || Interlocked.Increment(ref tails) != 1) return;
            tail.TrySetResult(); if (!releaseTail.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("owned failed series tail");
        };
        var directory = Path.Combine(f.Root, "output");
        var first = CreateSeriesBundle(f, directory, "frames-1"); f.Tray.Confirm!(ConfirmationDecision.Approve());
        Task? host = null;
        try
        {
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5)); host = await SeriesBundleHostTask(f, first);
            releaseFrame.TrySetResult(); await tail.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var existing = Path.Combine(directory, "frames"); Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "owned.txt"), "static output");
            var second = CreateSeriesBundle(f, directory); f.Tray.Confirm!(ConfirmationDecision.Approve());
            await Until(() => second.IsFinalized && !SeriesBundleInUse(f, Path.Combine(second.OutputPath, "held.png")));
            Assert.Equal(RecState.failed, second.State); Assert.Equal("series_publish_failed", second.Error);
            Assert.False(Directory.Exists(first.OutputPath));
            Assert.True(SeriesBundleInUse(f, Path.Combine(first.OutputPath, "held.png")));
            Assert.Equal("static output", File.ReadAllText(Path.Combine(existing, "owned.txt")));
        }
        finally { releaseFrame.TrySetResult(); releaseTail.Set(); if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(5)); }
        await Until(() => !SeriesBundleInUse(f, Path.Combine(first.OutputPath, "held.png")));
    }

    [Fact]
    public async Task SeriesBundleLateOriginalHostCannotReleaseReplacementIdentity()
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        Func<Task>? queued = null; f.Engine.ScreenshotSeriesDispatcherForTests = work => { queued = work; return Task.CompletedTask; };
        var series = CreateSeriesBundle(f, Path.Combine(f.Root, "old")); f.Tray.Confirm!(ConfirmationDecision.Approve());
        var oldPath = series.OutputPath; var newPath = Path.Combine(f.Root, "replacement", "frames");
        // Deliberately synthetic ID replacement, not a legal concurrent API run.
        // The original host use remains independently registered as an orphan.
        var replacement = new Recording(series.Id) { IsOrdinaryLocalRequest = true, Config = new() { Mode = "screenshot_series" }, OutputPath = newPath };
        var type = typeof(RecordingEngine).GetNestedType("OrdinarySeriesOutputUse", BindingFlags.NonPublic)!;
        var next = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { replacement, newPath }, null)!;
        lock (OutputLock(f.Engine))
            ((IDictionary)typeof(RecordingEngine).GetField("_ordinarySeriesOutputUses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Engine)!)[series.Id] = next;
        Assert.True(SeriesBundleInUse(f, Path.Combine(oldPath, "old.png")));
        Assert.True(SeriesBundleInUse(f, Path.Combine(newPath, "new.png")));
        await queued!();
        await Until(() => !SeriesBundleInUse(f, Path.Combine(oldPath, "old.png")));
        Assert.Same(next, SeriesBundleUse(f, replacement));
        Assert.True(SeriesBundleInUse(f, Path.Combine(newPath, "new.png")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued!());
        Assert.True(SeriesBundleInUse(f, Path.Combine(newPath, "new.png")));
    }

    [Fact]
    public async Task SeriesBundleSnapshotAndRetentionKeepBoundedUnknownSemantics()
    {
        using var f = SeriesBundleFixture(); ConfigureSeriesBundle(f, new SeriesBundlePngRunner());
        var series = CreateSeriesBundle(f, Path.Combine(f.Root, "output"));
        var paths = Enumerable.Range(0, 100).Select(i => Path.Combine(series.OutputPath, $"{i}.mp4")).ToArray();
        Assert.All(Read(f.Engine, paths)!.Paths, p => Assert.True(p.InUse));
        Assert.Null(Read(f.Engine, paths.Append(Path.Combine(series.OutputPath, "extra.mp4")).ToArray()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = Task.Run(() => { lock (OutputLock(f.Engine)) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); } });
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(Read(f.Engine, paths[0])); }
        finally { release.Set(); await held.WaitAsync(TimeSpan.FromSeconds(5)); }
        for (var i = 0; i < 499; i++)
        {
            var path = Path.Combine(f.Root, $"synthetic-{i}.mp4");
            InjectSyntheticClaimForBound(f.Engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = path }, path);
        }
        Assert.True(SeriesBundleInUse(f, paths[0]));
        var scopedRoot = Path.Combine(series.OutputPath, "Videos");
        var anchor = new OriginalFileScopeFixture().Anchor with { Path = Path.Combine(scopedRoot, "held.mp4") };
        var native = new OriginalFileScopeFixture.Native { Anchor = anchor };
        var result = await f.Engine.CreateRetentionPreparationService(scopedRoot, native).AcquireAsync(anchor);
        Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, result.Reason); Assert.Empty(native.Calls);
        var extra = Path.Combine(f.Root, "synthetic-500.mp4");
        InjectSyntheticClaimForBound(f.Engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = extra }, extra);
        Assert.Null(Read(f.Engine, paths[0]));
        result = await f.Engine.CreateRetentionPreparationService(scopedRoot, native).AcquireAsync(anchor);
        Assert.Equal(OrdinaryPreparationReason.Unavailable, result.Reason); Assert.Empty(native.Calls);
        Assert.Equal(RecState.pending_confirmation, series.State); // GET never retires the use
        f.Tray.Confirm!(ConfirmationDecision.Reject());
    }




}
