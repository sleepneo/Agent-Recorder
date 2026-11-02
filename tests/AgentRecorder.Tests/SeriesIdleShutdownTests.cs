using System.Collections;
using System.Reflection;
using System.Threading;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesIdleRejectedCallbackTailBlocksShutdownUntilExactRetirement(bool localFailure)
    {
        var audit = new BundleUseAudit(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = SeriesBundleFixture(audit);
        var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        // Real HTTP screenshot creation, no video/Bundle/probe/other owner.
        f.StartApi(); using var client = f.Client();
        using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
            new System.Net.Http.StringContent(SeriesBundleRequest(f, System.IO.Path.Combine(f.Root, "output")).ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var rec = Assert.Single(f.Engine._recs.Values);
        var file = System.IO.Path.Combine(f.Root, "owned-file"); System.IO.File.WriteAllText(file, "owned");
        audit.Before = evt =>
        {
            if (evt != (localFailure ? "confirmation.output_directory_rejected" : "confirmation.rejected")) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("owned rejection gate");
        };
        var callback = Task.Run(() => f.Tray.Confirm!(localFailure
            ? ConfirmationDecision.Approve(System.IO.Path.Combine(file, "child")) : ConfirmationDecision.Reject()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(rec.IsFinalized); Assert.Equal(RecState.rejected, rec.State);
            Assert.Null(rec.Backend); Assert.Null(rec.Config.StorageSafety); Assert.Null(rec.AuthorizationProof);
            Assert.Equal(0, f.Engine.ActiveScreenshotSeriesOperationCountForTests); Assert.Equal(0, BundleUseCount(f));
            Assert.Equal(0, runner.Calls); Assert.Equal(0, f.FactoryCalls);
            Assert.True(SeriesBundleInUse(f, System.IO.Path.Combine(rec.OutputPath, "held.png")));
            var reason = f.Engine.TryReserveIdleHostShutdown(() => null);
            SaveSeriesBundleEvidence("idle-reject-" + localFailure, new { reason, stopping = f.Engine.RetentionHostStopping,
                closed = f.Engine.HostAdmission.IsClosed, operations = f.Engine.ActiveScreenshotSeriesOperationCountForTests,
                frameCalls = runner.Calls, state = rec.State.ToString() });
            Assert.Equal("recording_resources_pending", reason);
            Assert.False(f.Engine.RetentionHostStopping); Assert.False(f.Engine.HostAdmission.IsClosed);
        }
        finally { release.Set(); await callback.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.False(SeriesBundleInUse(f, System.IO.Path.Combine(rec.OutputPath, "held.png")));
        Assert.Null(f.Engine.TryReserveIdleHostShutdown(() => null));
        Assert.True(f.Engine.RetentionHostStopping);
    }
    [Theory]
    [InlineData("ack", false)]
    [InlineData("ack", true)]
    [InlineData("throw", false)]
    [InlineData("throw", true)]
    [InlineData("fault", false)]
    [InlineData("fault", true)]
    public async Task SeriesIdleActualHostTailAndOrphanBlockAfterOperationRemoval(string dispatch, bool orphan)
    {
        using var f = SeriesBundleFixture(); var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.ScreenshotSeriesHostTailForTests = _ => { entered.TrySetResult(); return release.Task; };
        Func<Task>? queued = null;
        f.Engine.ScreenshotSeriesDispatcherForTests = work =>
        {
            queued = work;
            if (dispatch == "throw") throw new System.IO.IOException("owned unknown dispatch");
            return dispatch == "fault" ? Task.FromException(new System.IO.IOException("owned unknown ack")) : Task.CompletedTask;
        };
        var rec = CreateSeriesBundle(f, System.IO.Path.Combine(f.Root, "output"));
        f.Tray.Confirm!(ConfirmationDecision.Approve());
        Assert.Equal(0, runner.Calls);
        Assert.NotNull(f.Engine.TryReserveIdleHostShutdown(() => null));
        Assert.False(f.Engine.RetentionHostStopping);
        var stop = Task.Run(() => f.Engine.Stop(rec.Id, "user_requested"));
        Task? actual = null;
        try
        {
            await Until(() => rec.State == RecState.stopping);
            actual = queued!();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(rec.IsFinalized); Assert.Equal(RecState.cancelled, rec.State); Assert.False(actual.IsCompleted);
            Assert.Equal(0, f.Engine.ActiveScreenshotSeriesOperationCountForTests);
            Assert.Null(rec.Backend); Assert.Null(rec.Config.StorageSafety); Assert.Equal(0, BundleUseCount(f));
            if (orphan)
            {
                // Supplemental identity mutation: retain the real dispatched host
                // in its independent set, without a current registration masking it.
                lock (RuntimeOutputTestEngine.OutputLock(f.Engine))
                    ((IDictionary)typeof(RecordingEngine).GetField("_ordinarySeriesOutputUses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Engine)!).Remove(rec.Id);
            }
            Assert.True(SeriesBundleInUse(f, System.IO.Path.Combine(rec.OutputPath, "held.png")));
            var reason = f.Engine.TryReserveIdleHostShutdown(() => null);
            SaveSeriesBundleEvidence($"idle-host-{dispatch}-{orphan}", new { reason, orphan, dispatch,
                stopping = f.Engine.RetentionHostStopping, closed = f.Engine.HostAdmission.IsClosed,
                operations = f.Engine.ActiveScreenshotSeriesOperationCountForTests, host = actual.Status.ToString(), state = rec.State.ToString() });
            Assert.Equal("recording_resources_pending", reason);
            Assert.False(f.Engine.RetentionHostStopping); Assert.False(f.Engine.HostAdmission.IsClosed);
        }
        finally
        {
            release.TrySetResult();
            if (actual is not null) await actual.WaitAsync(TimeSpan.FromSeconds(5));
            else await queued!().WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Until(() => !SeriesBundleInUse(f, System.IO.Path.Combine(rec.OutputPath, "held.png")));
        Assert.Null(f.Engine.TryReserveIdleHostShutdown(() => null));
        Assert.True(f.Engine.RetentionHostStopping); Assert.Equal(0, runner.Calls);
    }

}
