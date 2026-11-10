using System.Net;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static async Task<(SyntheticProcess Process, WgcContinuousCaptureBackend Backend)> IndependentWindow382Approve(Fixture f, Recording rec)
    {
        f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve());
        await Until(() => rec.State == RecState.recording || rec.IsFinalized);
        Assert.True(rec.State == RecState.recording, $"{rec.State}: {rec.Error} / {rec.StopReason}");
        return (f.Process!, f.Wgc!);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow382BothApprovalOrdersPublishIndependentMediaBundlesAndHistory(bool reverse)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        var redClip = Path.Combine(f.Root,"prepared-red.mp4");
        await AvMuxOwnershipTests.Generate(redClip,"color=c=red:s=64x64:r=15",false);
        f.SyntheticProcessCreatedForTests = p => { if(p.Options.WindowHandle==(nint)43)p.MediaClipOverride=redClip; };
        try
        {
            var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            var identityA = a.WindowIdentity!; var identityB = b.WindowIdentity!;
            Assert.True(identityA.Number > 0 && identityB.Number > identityA.Number);
            Assert.Equal(identityA, f.Tray.Presentations[a.Id].WindowIdentity);
            Assert.Equal(identityB, f.Tray.Presentations[b.Id].WindowIdentity);
            Assert.NotSame(a.Config, b.Config); Assert.NotEqual(a.OutputPath, b.OutputPath);
            Assert.Null(a.NestedRole); Assert.Null(b.NestedRole); Assert.Null(b.ParentRecordingId); Assert.Null(b.NestedSessionId);
            var first = reverse ? b : a; var second = reverse ? a : b;
            var one = await IndependentWindow382Approve(f, first);
            Assert.Equal(RecState.pending_confirmation, second.State); Assert.Null(second.AuthorizationProof);
            var two = await IndependentWindow382Approve(f, second);
            Assert.NotSame(one.Backend, two.Backend); Assert.NotSame(one.Process, two.Process);
            Assert.Equal(first.Config.WindowHandle,one.Process.Options.WindowHandle);
            Assert.Equal(second.Config.WindowHandle,two.Process.Options.WindowHandle);
            Assert.NotEqual(one.Process.Options.RecordingId, two.Process.Options.RecordingId);
            Assert.NotEqual(one.Process.Options.BeginSignalPath, two.Process.Options.BeginSignalPath);
            Assert.NotEqual(one.Process.Options.StopSignalPath, two.Process.Options.StopSignalPath);
            Assert.NotEqual(Path.GetDirectoryName(one.Process.Options.OutputPath), Path.GetDirectoryName(two.Process.Options.OutputPath));
            Assert.NotEqual(first.AuthorizationProof!.ScopeDigest, second.AuthorizationProof!.ScopeDigest);
            Assert.Equal(first.Id, first.AuthorizationProof.RecordingId); Assert.Equal(second.Id, second.AuthorizationProof.RecordingId);
            f.Tray.Confirmations[first.Id](ConfirmationDecision.Approve()); Assert.Equal(2, f.FactoryCalls);
            f.Generator.Release.TrySetResult(); one.Process.Finish.TrySetResult();
            await Until(() => first.IsFinalized && first.BundleSnapshot.Status == "not_applicable");
            Assert.Equal(RecState.recording, second.State); Assert.False(two.Process.HasExited);
            Assert.False(two.Backend.OutputRetirement.IsCompleted);
            Assert.Equal(identityA, a.WindowIdentity); Assert.Equal(identityB, b.WindowIdentity);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine, second.OutputPath)!.Paths.Single().InUse);
            using (var client = f.Client())
            {
                Assert.Equal("completed", (await Get(client, "/api/v1/recordings/" + first.Id)).GetProperty("status").GetString());
                Assert.Equal("recording", (await Get(client, "/api/v1/recordings/" + second.Id)).GetProperty("status").GetString());
            }
            two.Process.Finish.TrySetResult(); await Until(() => second.IsFinalized && second.BundleSnapshot.Status == "not_applicable");
            await one.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5)); await two.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            await PathCanonical381RHistory(f, a); await PathCanonical381RHistory(f, b);
            Assert.Equal(Hash(f.Clip),Hash(a.OutputPath)); Assert.Equal(Hash(redClip),Hash(b.OutputPath));
            Assert.NotEqual(Hash(a.OutputPath),Hash(b.OutputPath));
            Assert.NotEqual(a.OrdinaryTargetKey, b.OrdinaryTargetKey); Assert.Null(a.OrdinaryBundleOperation); Assert.Null(b.OrdinaryBundleOperation);
            Assert.Null(f.Writer.Read(a.Id)!.NestedRole); Assert.Null(f.Writer.Read(b.Id)!.ParentRecordingId);
            Assert.All(f.Tray.UiEvents, p => Assert.Equal(f.Engine._recs[p.RecordingId].WindowIdentity, p.WindowIdentity));
            SaveSeriesBundleEvidence("independent382-two-" + reverse, new { reverse, first = first.Id, second = second.Id,
                numberA = identityA.Number, numberB = identityB.Number, aBundle = a.BundleSnapshot.Status,
                bBundle = b.BundleSnapshot.Status, targetA = a.OrdinaryTargetKey, targetB = b.OrdinaryTargetKey, factories = f.FactoryCalls });
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory]
    [InlineData("stop")][InlineData("natural")][InlineData("window_closed")]
    [InlineData("window_minimized")][InlineData("size_changed")][InlineData("backend_creation")]
    public async Task IndependentWindow382OneRunEndingNeverStopsItsPeer(string ending)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            var live = await IndependentWindow382Approve(f, a);
            if (ending == "backend_creation")
            {
                f.Engine.BackendFactory = _ => throw new IOException("owned-window382-backend-creation");
                f.Tray.Confirmations[b.Id](ConfirmationDecision.Approve());
            }
            else
            {
                var ended = await IndependentWindow382Approve(f, b);
                if (ending is not ("stop" or "natural"))
                { ended.Process.FailMedia = true; ended.Process.FailureCode = ending; ended.Process.FailureReason = ending; }
                if (ending == "stop") f.Engine.Stop(b.Id, "user_requested"); else ended.Process.Finish.TrySetResult();
                await ended.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Until(() => b.IsFinalized);
            Assert.Equal(ending is "stop" or "natural" ? RecState.completed : RecState.failed, b.State);
            if (ending is not ("stop" or "natural" or "backend_creation")) Assert.Equal(ending, b.StopReason);
            Assert.Equal(RecState.recording, a.State); Assert.False(live.Process.HasExited);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine, a.OutputPath)!.Paths.Single().InUse);
            live.Process.Finish.TrySetResult(); f.Generator.Release.TrySetResult();
            await Until(() => a.IsFinalized && a.BundleSnapshot.Status == "not_applicable");
            await PathCanonical381RHistory(f, a); Assert.Equal(b.State.ToString(), f.Writer.Read(b.Id)!.Status);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow382RejectOrExpiryLeavesPeerAndNextCreationNeedsNewConsent(bool expired)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            var running = await IndependentWindow382Approve(f, a); var old = f.Tray.Confirmations[b.Id];
            if (expired) f.Engine.TriggerConfirmationExpiryForTests(b.ConfirmationId!); else old(ConfirmationDecision.Reject());
            old(ConfirmationDecision.Approve()); Assert.Null(b.AuthorizationProof); Assert.Equal(1, f.FactoryCalls);
            Assert.Equal(RecState.recording, a.State); Assert.False(running.Process.HasExited);
            var next = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            Assert.NotEqual(b.ConfirmationId, next.ConfirmationId); Assert.True(next.WindowIdentity!.Number > b.WindowIdentity!.Number);
            Assert.Null(next.AuthorizationProof); Assert.Equal(RecState.pending_confirmation, next.State);
            f.Tray.Confirmations[next.Id](ConfirmationDecision.Reject());
            Assert.Equal(1, f.FactoryCalls); Assert.Equal(RecState.recording, a.State);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow382StopAllStopsEachStartedOrPendingRun(bool pendingSecond)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            await IndependentWindow382Approve(f, a);
            if (!pendingSecond) await IndependentWindow382Approve(f, b);
            f.Generator.Release.TrySetResult(); f.Engine.StopAllSync("user_requested");
            await Until(() => a.IsFinalized && b.IsFinalized);
            Assert.Equal(RecState.completed, a.State);
            Assert.Equal(pendingSecond ? RecState.cancelled : RecState.completed, b.State);
            Assert.Equal(pendingSecond ? 1 : 2, f.FactoryCalls);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Fact]
    public async Task IndependentWindow382CountdownAndFirstFrameGatesRemainPerRecording()
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        using var releaseA = new ManualResetEventSlim(); using var releaseB = new ManualResetEventSlim();
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new System.Collections.Concurrent.ConcurrentDictionary<nint,SyntheticProcess>();
        f.SyntheticProcessCreatedForTests = p => processes[p.Options.WindowHandle] = p;
        f.Engine.CountdownSteps = null; f.Engine.CountdownInterval = TimeSpan.Zero;
        var q = IndependentWindow382Request(f); q.Remove("countdown_seconds");
        var a = await NestedWindow381Accept(f, q);
        q = IndependentWindow382Request(f,"window_43"); q["countdown_seconds"] = 1;
        var b = await NestedWindow381Accept(f, q);
        f.Engine.BeforeStartActionForTests = (r, action) => {
            Assert.Equal("start_capture", action);
            var entered = r.Id == a.Id ? enteredA : enteredB;
            var release = r.Id == a.Id ? releaseA : releaseB;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owned-first-frame-gate");
        };
        var approvalA = Task.Run(() => f.Tray.Confirmations[a.Id](ConfirmationDecision.Approve()));
        var approvalB = Task.Run(() => f.Tray.Confirmations[b.Id](ConfirmationDecision.Approve()));
        try
        {
            await Task.WhenAll(enteredA.Task,enteredB.Task).WaitAsync(TimeSpan.FromSeconds(5));
            SaveSeriesBundleEvidence("independent382-countdown-before-assert",new {
                aState=a.State.ToString(),bState=b.State.ToString(),
                stepsA=f.Tray.UiEvents.Where(p=>p.RecordingId==a.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds).ToArray(),
                stepsB=f.Tray.UiEvents.Where(p=>p.RecordingId==b.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds).ToArray() });
            Assert.Equal(3,a.CountdownSeconds); Assert.Equal(1,b.CountdownSeconds);
            Assert.Equal(new[]{3,2,1}, f.Tray.UiEvents.Where(p=>p.RecordingId==a.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds!.Value));
            Assert.Equal(new[]{1}, f.Tray.UiEvents.Where(p=>p.RecordingId==b.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds!.Value));
            Assert.Equal(0,processes[(nint)42].BeginObserved); Assert.Equal(0,processes[(nint)43].BeginObserved);
            Assert.NotEqual(RecState.recording,a.State); Assert.NotEqual(RecState.recording,b.State);
            releaseB.Set(); await Until(()=>b.State==RecState.recording);
            Assert.NotEqual(RecState.recording,a.State); Assert.Equal(0,processes[(nint)42].BeginObserved);
            releaseA.Set(); await Until(()=>a.State==RecState.recording);
            Assert.Equal(1,processes[(nint)42].BeginObserved); Assert.Equal(RecState.recording,b.State);
            f.Engine.Stop(a.Id,"user_requested"); await Until(()=>a.IsFinalized);
            Assert.Equal(RecState.completed,a.State); Assert.Equal(RecState.recording,b.State);
            processes[(nint)43].Finish.TrySetResult(); f.Generator.Release.TrySetResult();
            await Until(()=>b.IsFinalized&&b.BundleSnapshot.Status == "not_applicable");
            Assert.Equal(1,processes[(nint)42].BeginObserved); await PathCanonical381RHistory(f,b);
        }
        catch(Exception e)
        {
            SaveSeriesBundleEvidence("independent382-countdown-first-failure",new { error=e.Message,
                a=a.State.ToString(),b=b.State.ToString(),aError=a.Error,bError=b.Error,
                stepsA=f.Tray.UiEvents.Where(p=>p.RecordingId==a.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds),
                stepsB=f.Tray.UiEvents.Where(p=>p.RecordingId==b.Id&&p.CountdownRemainingSeconds.HasValue).Select(p=>p.CountdownRemainingSeconds),
                enteredA=enteredA.Task.IsCompleted,enteredB=enteredB.Task.IsCompleted });
            throw;
        }
        finally
        {
            releaseA.Set();releaseB.Set();await Task.WhenAll(approvalA,approvalB);
            foreach(var p in processes.Values.Where(p=>p.BeginObserved==0&&!p.HasExited))p.KillEntireTree();
            await NestedWindow381Drain(f);
        }
    }

    [Fact]
    public async Task IndependentWindow382IdleExitWaitsForBothOriginalVideoWriters()
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        f.Engine.BundleGenerationDispatcherForTests=_=>throw new Xunit.Sdk.XunitException("Unexpected Bundle dispatch");
        var a=await NestedWindow381Accept(f,IndependentWindow382Request(f));
        var b=await NestedWindow381Accept(f,IndependentWindow382Request(f,"window_43"));
        var one=await IndependentWindow382Approve(f,a);var two=await IndependentWindow382Approve(f,b);
        try
        {
            Assert.NotNull(f.Engine.TryReserveIdleHostShutdown(()=>null));
            one.Process.Finish.TrySetResult();await Until(()=>a.IsFinalized);await one.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecState.recording,b.State);Assert.NotNull(f.Engine.TryReserveIdleHostShutdown(()=>null));
            two.Process.Finish.TrySetResult();await Until(()=>b.IsFinalized);await two.Backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==0);
            Assert.Equal(0,f.Generator.Calls);Assert.Equal(0,BundleUseCount(f));
            Assert.Null(f.Engine.TryReserveIdleHostShutdown(()=>null));Assert.True(f.Engine.RetentionHostStopping);
        }
        finally {await NestedWindow381Drain(f);}
    }
}
