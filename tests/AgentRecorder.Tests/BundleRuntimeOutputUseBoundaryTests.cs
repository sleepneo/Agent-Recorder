using System.Collections.Concurrent;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private sealed class BundleUseAudit : AuditLogger
    {
        internal Action<string>? Before;
        internal readonly ConcurrentQueue<string> Events = new();
        internal BundleUseAudit() : base(Path.Combine(Path.GetTempPath(), "bundle-use-audit-" + Guid.NewGuid().ToString("N"), "audit.jsonl")) { }
        public override void Log(string name, object value) { Events.Enqueue(name); Before?.Invoke(name); }
    }
    private sealed class BundleUseHistory(SqliteOrdinaryRecordingHistoryStore inner) : IOrdinaryRecordingHistoryStore, IOrdinaryRecordingBundleHistoryWriter
    {
        internal Action? Tail;
        public void Register(OrdinaryRecordingHistorySnapshot s) => inner.Register(s);
        public bool TryPublish(OrdinaryRecordingHistorySnapshot s) => inner.TryPublish(s);
        public OrdinaryRecordingHistoryPage ReadPage(int n, string? c) => inner.ReadPage(n,c);
        public OrdinaryRecordingHistorySnapshot? Read(string id) => inner.Read(id);
        public void RecoverIncomplete(DateTimeOffset t) => inner.RecoverIncomplete(t);
        public bool TryPublishBundle(OrdinaryRecordingBundleUpdate u)
        { var accepted = inner.TryPublishBundle(u); Tail?.Invoke(); return accepted; }
    }
    private static int BundleUseCount(Fixture f)
    {
        lock(OutputLock(f.Engine))
            return ((System.Collections.IEnumerable)typeof(RecordingEngine).GetField("_ordinaryBundleOutputUses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Engine)!).Cast<object>().Count();
    }
    private static Task HostTask(Fixture f)
    {
        Task? host=null;
        Assert.True(SpinWait.SpinUntil(()=>
        {
            lock(OutputLock(f.Engine))
            {
                var uses=(System.Collections.IEnumerable)typeof(RecordingEngine).GetField("_ordinaryBundleOutputUses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Engine)!;
                var use=Assert.Single(uses.Cast<object>());
                host=(Task?)use.GetType().GetField("Worker",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(use);
                return host is not null;
            }
        },TimeSpan.FromSeconds(5)));
        return host!;
    }
    private static string RuntimeDbFingerprint(Fixture f)
    {
        using var c = f.Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(recording_id||':'||status_code||':'||output_path) FROM (SELECT recording_id,status_code,output_path FROM ordinary_recording_history ORDER BY recording_id);";
        var h = cmd.ExecuteScalar()?.ToString();
        cmd.CommandText = "SELECT group_concat(recording_id||':'||status_code||':'||operation_id) FROM (SELECT recording_id,status_code,operation_id FROM ordinary_recording_bundles ORDER BY recording_id);";
        return h + "|" + cmd.ExecuteScalar();
    }
    private static void AssertRuntimeDbProtected(Fixture f, Recording rec, string reason)
    {
        _ = f.Writer.Read(rec.Id); var before = RuntimeDbFingerprint(f);
        var files = new RetentionPreviewQueryTests.MetadataFake(); var query = new UnifiedHistoryQueryService(f.Store, f.Writer, true);
        var service = new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage, f.Root, files, files, () => DateTimeOffset.UtcNow.AddDays(3), f.Engine);
        var page = service.GetPage(Sid, Session, 20, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(reason, Assert.Single(page.Items).ReasonCode);
        Assert.Empty(files.Attributes); Assert.Empty(files.Observations); Assert.Equal(before, RuntimeDbFingerprint(f));
    }

    [Fact]
    public async Task BundleRuntimeUseProtectsQueueGapAndSqlitePreparationUntilActualWorkRuns()
    {
        using var f = RuntimeFixture(); await f.Prepare();
        Func<Task>? queued = null; var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.BundleGenerationDispatcherForTests = run => { queued = run; return dispatch.Task; };
        var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec,false);
        await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(queued); Assert.Equal(0,f.Generator.Calls); RegisterOtherOutput(f);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        AssertRuntimeDbProtected(f,rec,"bundle_generation_in_progress");
        var native = new OriginalFileScopeFixture.Native { Anchor = rec.LastMeta!.PublicationReceipt!.Anchor };
        var preparation = f.Engine.CreateRetentionPreparationService(Path.Combine(f.Root,"Videos"),native);
        var acquired = await preparation.AcquireAsync(rec.LastMeta.PublicationReceipt.Anchor);
        Assert.Equal(OrdinaryPreparationReason.RuntimeInUse,acquired.Reason); Assert.Empty(native.Calls);
        Assert.Equal("bundle_in_flight",f.Engine.TryReserveIdleHostShutdown(()=>null)); Assert.False(f.Engine.RetentionHostStopping);
        f.Generator.Release.TrySetResult(); await queued!().WaitAsync(TimeSpan.FromSeconds(10)); dispatch.TrySetResult();
        await Until(()=>!Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        await Until(()=>f.Writer.Read(rec.Id)!.Bundle!.Status=="ready");
        AssertRuntimeDbProtected(f,rec,"bundle_artifact_reference"); Assert.Equal(1,f.Generator.Calls);
    }

    [Theory]
    [InlineData("history",false,false)] [InlineData("history",true,false)]
    [InlineData("audit",false,false)] [InlineData("audit",true,false)]
    [InlineData("history",false,true)] [InlineData("audit",false,true)]
    public async Task BundleRuntimeUseIncludesHostTailAfterGeneratorReturnsAndPublicStatusIsTerminal(string stage,bool fail,bool throws)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var audit = new BundleUseAudit(); BundleUseHistory? history = null;
        using var f = new Fixture(testRoot:Path.Combine(Path.GetTempPath(),"bundle-runtime"),runtimeAudit:audit,historyFactory:w=>history=new(w));
        await f.Prepare();
        if(fail)f.Generator.Fault=_=>Task.FromResult(RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed));
        void Block() { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(15)));if(throws)throw new IOException("owned host tail fault"); }
        if(stage=="history")history!.Tail=Block;
        else audit.Before=name=>{if(name==(fail?"recording.bundle_failed":"recording.bundle_ready"))Block();};
        var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var host=HostTask(f);
        f.Generator.Release.TrySetResult();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));Assert.True(f.Generator.Work!.IsCompleted);Assert.False(host.IsCompleted);
            Assert.Equal(fail?"failed":"ready",rec.BundleSnapshot.Status);
            await BundleSourceDenied(f, rec);
            await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));RegisterOtherOutput(f);
            Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
            Assert.True(Read(f.Engine,Path.Combine(Path.ChangeExtension(rec.OutputPath,".bundle"),"tail.mp4"))!.Paths.Single().InUse);
            Assert.Equal("bundle_in_flight",f.Engine.TryReserveIdleHostShutdown(()=>null));Assert.False(f.Engine.RetentionHostStopping);
            AssertRuntimeDbProtected(f,rec,fail?"bundle_artifact_state_unknown":"bundle_artifact_reference");
        }
        finally { release.Set(); }
        if(throws)await Assert.ThrowsAsync<IOException>(()=>host);else await host.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(()=>!Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        Assert.Equal(RecState.completed,rec.State);Assert.True(File.Exists(rec.OutputPath));Assert.Equal(1,f.Generator.Calls);
    }

    [Theory]
    [InlineData("throw")] [InlineData("cancel")]
    public async Task BundleRuntimeUseRetiresOnlyAfterActualExceptionalGeneratorExit(string outcome)
    {
        using var f=RuntimeFixture();await f.Prepare();
        f.Generator.Fault=_=>outcome=="throw"?Task.FromException<RecordingBundleGenerationResult>(new IOException("owned generator failure")):Task.FromCanceled<RecordingBundleGenerationResult>(new CancellationToken(true));
        var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);RegisterOtherOutput(f);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));
        f.Generator.ObservedFault=f.Generator.Work;try{await f.Generator.Work!;}catch(Exception) { }
        await Until(()=>!Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        Assert.Equal("failed",rec.BundleSnapshot.Status);Assert.Equal(RecState.completed,rec.State);Assert.True(File.Exists(rec.OutputPath));Assert.Equal(1,f.Generator.Calls);
    }

    [Theory]
    [InlineData("throws")] [InlineData("faults")] [InlineData("cancelled")]
    public async Task BundleRuntimeUseUnknownDispatchCannotPretendQueuedWorkExited(string failure)
    {
        var audit=new BundleUseAudit();using var f=new Fixture(testRoot:Path.Combine(Path.GetTempPath(),"bundle-runtime"),runtimeAudit:audit);await f.Prepare();
        Func<Task>? queued=null;
        f.Engine.BundleGenerationDispatcherForTests=run=>
        {
            queued=run;
            if(failure=="throws")throw new IOException("dispatch result unknown");
            return failure=="faults"?Task.FromException(new IOException("queue report failed")):Task.FromCanceled(new CancellationToken(true));
        };
        var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(queued);Assert.Equal(0,f.Generator.Calls);RegisterOtherOutput(f);
        await BundleSourceDenied(f, rec);
        Assert.True(Read(f.Engine,Path.Combine(Path.ChangeExtension(rec.OutputPath,".bundle"),"late.mp4"))!.Paths.Single().InUse);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);Assert.Contains("recording.bundle_output_use",audit.Events);
        Assert.Equal("bundle_in_flight",f.Engine.TryReserveIdleHostShutdown(()=>null));Assert.False(f.Engine.RetentionHostStopping);
        // A late arrival is the original queued job, not an automatic retry.
        f.Generator.Release.TrySetResult();await queued!().WaitAsync(TimeSpan.FromSeconds(10));
        await Until(()=>!Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);Assert.Equal(1,f.Generator.Calls);
    }

    [Theory]
    [InlineData("recording")] [InlineData("directory")] [InlineData("registration")] [InlineData("backend")]
    public async Task BundleRuntimeUseLateCompletionCannotReleaseNewBinding(string replacement)
    {
        using var f=RuntimeFixture();await f.Prepare();var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);
        var original=rec.OutputPath;var path=original;
        if(replacement=="recording")
        {
            path=Path.Combine(f.Root,"replacement.mp4");var next=new Recording(rec.Id,false){IsOrdinaryLocalRequest=true,OutputPath=path};Claim(f.Engine,next,path);
        }
        if(replacement=="directory") {path=rec.OutputPath=Path.Combine(f.Root,"moved.mp4");Claim(f.Engine,rec,path);}
        if(replacement=="registration")Claim(f.Engine,rec,path);
        if(replacement=="backend")rec.Backend=new WgcContinuousCaptureBackend();
        Assert.True(Read(f.Engine,original)!.Paths.Single().InUse);
        if (replacement is "recording" or "backend")
        {
            // A normal admission can legitimately retire the settled replacement
            // in the other two synthetic cases. Keep their late-callback-only
            // assertion free of an intervening business-path retirement.
            var conflict = Assert.Throws<TargetInvocationException>(() => Claim(f.Engine,
                new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = original }, original));
            Assert.Equal("OUTPUT_PATH_IN_USE", Assert.IsType<ApiException>(conflict.InnerException).Code);
        }
        f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(()=>BundleUseCount(f)==0);
        Assert.True(Read(f.Engine,path)!.Paths.Single().InUse);
        if(replacement=="backend")rec.Backend!.Dispose();
    }

    [Fact]
    public async Task BundleRuntimeUseOrphanCountsAgainstOriginalClaimBudgetWithoutDoubleCountingCurrentOwner()
    {
        using var f=RuntimeFixture();await f.Prepare();var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);
        for(var i=0;i<499;i++)RegisterOtherOutput(f,"book-"+i);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        var next=new Recording(rec.Id,false){IsOrdinaryLocalRequest=true,OutputPath=Path.Combine(f.Root,"new-owner.mp4")};Claim(f.Engine,next,next.OutputPath);
        Assert.Null(Read(f.Engine,rec.OutputPath)); // 500 owners + 1 orphan use
        f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(()=>Read(f.Engine,next.OutputPath) is not null);Assert.True(Read(f.Engine,next.OutputPath)!.Paths.Single().InUse);
    }
    [Fact]
    public async Task BundleRuntimeUseFinishingStillWaitsForFormalWgcWriterOwner()
    {
        using var f=RuntimeFixture();await f.Prepare();var rec=f.Create("display");await f.Approve(rec);
        var backend=f.Wgc!;
        var callback=(Action<int,OutputMeta>)typeof(WgcContinuousCaptureBackend).GetField("_onNaturalExit",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(backend)!;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        backend.OnNaturalExit((code,meta)=>{callback(code,meta);entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(15)));});
        try
        {
            await f.Terminal(rec,false);Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);
            Assert.False(backend.OutputRetirement.IsCompleted);f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(()=>BundleUseCount(f)==0);RegisterOtherOutput(f);
            Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse); // actual writer owner is still blocked
            var conflict = Assert.Throws<TargetInvocationException>(() => Claim(f.Engine,
                new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = rec.OutputPath }, rec.OutputPath));
            Assert.Equal("OUTPUT_PATH_IN_USE", Assert.IsType<ApiException>(conflict.InnerException).Code);
        }
        finally {release.Set();await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));}
        await Until(()=>!Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
    }

    [Theory]
    [InlineData("faulted")] [InlineData("cancelled")]
    public async Task BundleRuntimeUseEndingCannotReleaseSyntheticUncertainWriter(string state)
    {
        using var f=RuntimeFixture();await f.Prepare();var rec=f.Create("display");await f.Approve(rec);
        // Deliberately synthetic ownership uncertainty; production WGC retirement
        // TCS normally reports completion, not these fault/cancel outcomes.
        using var uncertain=new WgcContinuousCaptureBackend();
        var completion=(TaskCompletionSource<object?>)typeof(WgcContinuousCaptureBackend).GetField("_completionSignal",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(uncertain)!;
        var staging=(TaskCompletionSource<object?>)typeof(WgcContinuousCaptureBackend).GetField("_stagingReleased",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(uncertain)!;
        staging.TrySetResult(null);
        if(state=="faulted")completion.TrySetException(new IOException("synthetic writer uncertainty"));else completion.TrySetCanceled();
        rec.Backend=uncertain;
        await f.Terminal(rec,false);await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);
        f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));await Until(()=>BundleUseCount(f)==0);
        RegisterOtherOutput(f);Assert.False(uncertain.OutputRetirement.IsCompletedSuccessfully);Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
    }

    [Fact]
    public async Task BundleRuntimeUseStopAndShutdownCannotEraseRunningWork()
    {
        using var f=RuntimeFixture();await f.Prepare();var rec=f.Create("display");await f.Approve(rec);await f.Terminal(rec,false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var host=HostTask(f);
        f.Engine.Stop(rec.Id,"user_requested");Assert.False(host.IsCompleted);Assert.Equal(1,BundleUseCount(f));
        f.Engine.BeginHostShutdown("private_bundle_shutdown");RegisterOtherOutput(f);
        Assert.Null(Read(f.Engine,rec.OutputPath));Assert.Equal(1,BundleUseCount(f));Assert.True(OwnerCount(f.Engine)>=2);
        f.Generator.Release.TrySetResult();await host.WaitAsync(TimeSpan.FromSeconds(10));await Until(()=>BundleUseCount(f)==0);
        Assert.Equal(RecState.completed,rec.State);Assert.True(File.Exists(rec.OutputPath));Assert.Equal(1,f.Generator.Calls);
    }

}
