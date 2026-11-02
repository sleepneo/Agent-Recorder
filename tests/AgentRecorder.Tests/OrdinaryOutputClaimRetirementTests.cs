using System.Collections.Concurrent;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryOutputClaimRetirementTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public void Dispose() => _temp.Dispose();
    private string PathFor(string name) => Path.Combine(_temp.Path, name + ".mp4");
    private static Recording Owner(string path, ICaptureBackend? backend = null, bool attempted = true, string? id = null) => new(id ?? "claim_" + Guid.NewGuid().ToString("N"), false)
    {
        IsOrdinaryLocalRequest = true, Backend = backend, BackendStartAttempted = attempted,
        OutputPath = path, BackendType = "unknown", Config = new() { SourceKind = "display", Bounds = (0, 0, 1920, 1080), OutputPath = path, DurationSeconds = 5, CountdownSeconds = 0 }, CountdownSeconds = 0
    };
    private static void Terminal(Recording rec)
    { lock (rec) { rec.State = RecState.completed; rec.PublishFinalized(); } }
    private void Other(RecordingEngine engine, string name = "other")
    { var path = PathFor(name); Claim(engine, Owner(path, attempted: false), path); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, Private)!.SetValue(value, data);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FormalUnknownBackendTerminalAndStartFailureKeepClaimWhenAnotherOutputRegisters(bool throwStart)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        var backend = new UnknownBackend(throwStart); var rec = Owner(PathFor("unknown"), backend);
        var tray = new NoUi(); engine.SetTray(tray); engine.BackendFactory = _ => (backend, "unknown");
        Claim(engine, rec, rec.OutputPath);
        engine.StartCaptureForTests(rec, tray);
        if (!throwStart) { Assert.Equal(RecState.recording, rec.State); engine.Stop(rec.Id, "test_stop"); }
        Assert.True(rec.IsFinalized); Assert.True(rec.BackendStartAttempted); Assert.Equal(1, backend.Starts);
        Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        Other(engine);
        Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        Assert.Equal(2, OwnerCount(engine));
        var conflict = Assert.Throws<TargetInvocationException>(() => Claim(engine, Owner(rec.OutputPath, attempted: false), rec.OutputPath));
        Assert.Equal("OUTPUT_PATH_IN_USE", Assert.IsType<ApiException>(conflict.InnerException).Code);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("null")] [InlineData("ffmpeg-unknown-start")]
    public void UnattemptedFlagAndNullOrUnknownResourcesCannotRetireClaim(string kind)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        ICaptureBackend? backend = kind == "unknown" ? new UnknownBackend(false) : kind == "null" ? null : new FfmpegCaptureBackend();
        if (backend is FfmpegCaptureBackend ffmpeg)
        {
            var boundary = Field<WorkerStartBoundary>(ffmpeg, "_hostStart");
            Assert.Throws<InvalidOperationException>(() => boundary.Start(attempt => attempt.Launch(() => throw new InvalidOperationException("unknown launch"), false), () => false));
            ffmpeg.Dispose(); Assert.False(ffmpeg.HostResourcesRetired);
        }
        var rec = Owner(PathFor(kind), backend, attempted: false); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        Other(engine); Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        backend?.Dispose();
    }

    [Theory]
    [InlineData("ffmpeg")] [InlineData("av")] [InlineData("wgc")]
    public void ExplicitNeverStartedRetirementReleasesOnlyAfterRealBackendCleanup(string kind)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        ICaptureBackend backend = kind switch { "ffmpeg" => new FfmpegCaptureBackend(), "av" => new AvSplitCaptureBackend(), _ => new WgcContinuousCaptureBackend() };
        var rec = Owner(PathFor(kind), backend, attempted: false); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        Other(engine, "before"); Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        var conflict = Assert.Throws<TargetInvocationException>(() => Claim(engine, Owner(rec.OutputPath, attempted: false), rec.OutputPath));
        Assert.Equal("OUTPUT_PATH_IN_USE", Assert.IsType<ApiException>(conflict.InnerException).Code);
        if (backend is FfmpegCaptureBackend ffmpeg) { ffmpeg.Dispose(); Assert.True(ffmpeg.HostResourcesRetired); }
        if (backend is AvSplitCaptureBackend av) { av.RetireUnstartedHostResources(); Assert.True(av.HostResourcesRetired); }
        if (backend is WgcContinuousCaptureBackend wgc) { wgc.RetireUnstartedHostResources(); Assert.True(wgc.OutputRetirement.IsCompletedSuccessfully); }
        Other(engine, "after"); Assert.False(Read(engine, rec.OutputPath)!.Paths.Single().InUse); backend.Dispose();
        Claim(engine, Owner(rec.OutputPath, attempted: false), rec.OutputPath);
        Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
    }

    [Theory]
    [InlineData("pending")] [InlineData("faulted")] [InlineData("cancelled")]
    public void SyntheticWgcRetirementUncertaintyIsNeverSuccess(string state)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger()); using var backend = new WgcContinuousCaptureBackend();
        var completion = Field<TaskCompletionSource<object?>>(backend, "_completionSignal");
        var staging = Field<TaskCompletionSource<object?>>(backend, "_stagingReleased");
        staging.TrySetResult(null);
        if (state == "faulted") completion.TrySetException(new IOException("synthetic retirement fault"));
        if (state == "cancelled") completion.TrySetCanceled();
        var rec = Owner(PathFor("wgc"), backend); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        Other(engine); Assert.False(backend.OutputRetirement.IsCompletedSuccessfully);
        Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        completion.TrySetResult(null); // pending case cleanup; fault/cancel remain explicitly uncertain.
    }

    [Theory]
    [InlineData("ffmpeg")] [InlineData("av")]
    public void KnownWriterSettlementRetainsPendingAndReleasesSettledOwner(string kind)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        ICaptureBackend backend = kind == "ffmpeg" ? new FfmpegCaptureBackend() : new AvSplitCaptureBackend();
        object? run = null;
        if (backend is FfmpegCaptureBackend ffmpeg)
            run = typeof(FfmpegTerminalPublicationTests).GetMethod("NewRun", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { ffmpeg, new CaptureConfig { OutputPath = PathFor("writer") } });
        var rec = Owner(PathFor("writer"), backend); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        Other(engine, "before"); Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        if (run is not null) Set(run, "OutputSettled", 1); else Set(backend, "_outputSettled", 1);
        Other(engine, "after"); Assert.False(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        if (run is not null) Set(backend, "_videoOnlyRun", null);
        backend.Dispose();
    }

    [Fact]
    public async Task UnknownClaimStaysProtectedInSqlitePreviewPreparationAndAvAdmissionAfterOtherRegistration()
    {
        using var preparation = new OrdinaryPreparationFixture(); var engine = preparation.Engine;
        var rec = Owner(preparation.Anchor.Path, new UnknownBackend(false));
        rec.Config.AudioSourceKind = AudioCaptureSourceKind.Microphone; rec.BackendType = "ffmpeg-av-split";
        rec.ApprovedCapturePlan = OrdinaryPreparationFixture.Recording(rec.OutputPath, "ffmpeg-av-split", "microphone").ApprovedCapturePlan;
        Assert.True(RecordingStoragePolicy.IsOrdinaryAv(rec));
        Claim(engine, rec, rec.OutputPath); Terminal(rec); Other(engine);
        var result = await preparation.Service().AcquireAsync(preparation.Anchor);
        Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, result.Reason); Assert.Empty(preparation.Api.Calls);
        OrdinaryPreparationFixture.AssertConflict(engine, rec.OutputPath, "ffmpeg-av-split", "microphone");
        using var db = new UnifiedHistoryTestDatabase(_temp.Path);
        db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(db.Recording("old_history", completed: true) with { OutputPath = rec.OutputPath, BundleRegistration = new(null) }));
        var files = new RetentionPreviewQueryTests.MetadataFake();
        var service = new RetentionPreviewQueryService(db.Query.GetOrdinaryEvidencePage, Path.GetDirectoryName(Path.GetDirectoryName(rec.OutputPath))!, files, files, () => At.AddDays(2), engine);
        var before = db.Snapshot(); var page = service.GetPage(Sid, Session, 20, At.AddHours(1));
        Assert.Equal("runtime_output_in_use", Assert.Single(page.Items).ReasonCode);
        Assert.Empty(files.Observations); Assert.Empty(files.Attributes); Assert.Equal(before, db.Snapshot());
    }

    [Theory]
    [InlineData("recording")]
    [InlineData("directory")]
    [InlineData("registration")]
    [InlineData("backend")]
    [InlineData("cleanup")]
    [InlineData("shutdown")]
    public void LateRetiredOwnerReleaseCannotRemoveReplacementOrProtectedClaim(string change)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        using var backend = new WgcContinuousCaptureBackend(); backend.RetireUnstartedHostResources();
        var rec = Owner(PathFor("late-old"), backend); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        var owners = Field<System.Collections.IDictionary>(engine, "_ordinaryOutputs");
        var token = owners[rec.Id]!;
        var path = rec.OutputPath;
        if (change == "recording")
        {
            var next = Owner(PathFor("new-recording"), new UnknownBackend(false), id: rec.Id);
            path = next.OutputPath; Claim(engine, next, path);
            // An old unregistered-create failure is also forbidden to remove the new owner.
            Release(engine, rec);
        }
        if (change == "directory") { path = rec.OutputPath = PathFor("new-directory"); Claim(engine, rec, path); }
        if (change == "registration") Claim(engine, rec, path); // same rec/backend/path, different immutable booking token
        if (change == "backend") rec.Backend = new WgcContinuousCaptureBackend();
        var cleanup = Field<ConcurrentDictionary<string, byte>>(engine, "_ordinaryOutputCleanup");
        if (change == "cleanup") cleanup[rec.Id] = 0;
        if (change == "shutdown") engine.BeginHostShutdown("test_late_retirement");
        // Exercise the exact continuation target with the old captured registration.
        typeof(RecordingEngine).GetMethod("ReleaseSettledMediaOnlyOutput", Private)!.Invoke(engine, new[] { token, backend });
        Assert.Equal(1, OwnerCount(engine));
        if (change != "shutdown") Assert.True(Read(engine, path)!.Paths.Single().InUse);
        else { Other(engine); Assert.Equal(2, OwnerCount(engine)); Assert.Null(Read(engine, path)); }
        if (change == "cleanup")
        {
            Other(engine); Assert.True(Read(engine, path)!.Paths.Single().InUse);
            cleanup.TryRemove(rec.Id, out _);
            Other(engine, "cleaned"); Assert.False(Read(engine, path)!.Paths.Single().InUse);
        }
        if (change == "backend") rec.Backend!.Dispose();
    }

    [Fact]
    public async Task CachedAdmissionWitnessDoesNotReleaseConcurrentSameIdNewRegistration()
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        using var backend = new WgcContinuousCaptureBackend(); backend.RetireUnstartedHostResources();
        var old = Owner(PathFor("cached"), backend); Claim(engine, old, old.OutputPath); Terminal(old);
        var witnesses = (Array)typeof(RecordingEngine).GetMethod("ObserveRetiredOrdinaryOutputs", Private)!.Invoke(engine, null)!;
        var witness = Assert.Single(witnesses.Cast<object>());
        var token = witness.GetType().GetProperty("Owner")!.GetValue(witness)!;
        using var registered = new ManualResetEventSlim(); using var gate = new Barrier(2);
        var next = Owner(PathFor("replacement"), new UnknownBackend(false), id: old.Id);
        var register = Task.Run(() =>
        {
            Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(5)));
            Claim(engine, next, next.OutputPath); registered.Set();
        });
        var stale = Task.Run(() =>
        {
            Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(5))); Assert.True(registered.Wait(TimeSpan.FromSeconds(5)));
            lock (OutputLock(engine)) typeof(RecordingEngine).GetMethod("ReleaseRetiredOrdinaryOutputUnderLock", Private)!.Invoke(engine, new[] { token, backend });
        });
        await Task.WhenAll(register, stale).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Read(engine, next.OutputPath)!.Paths.Single().InUse); Assert.Equal(1, OwnerCount(engine));
    }

    [Fact]
    public void WorkerEvidenceWaitOccursOutsideOutputLockAndCleanupIsRechecked()
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        using var backend = new FfmpegCaptureBackend(); backend.Dispose();
        var rec = Owner(PathFor("worker-gate"), backend, attempted: false); Claim(engine, rec, rec.OutputPath); Terminal(rec);
        var workerGate = Field<object>(Field<WorkerStartBoundary>(backend, "_hostStart"), "_gate");
        using var entered = new ManualResetEventSlim(); Exception? error = null;
        var worker = new Thread(() => { entered.Set(); try { Other(engine); } catch (Exception ex) { error = ex; } });
        Monitor.Enter(workerGate);
        try
        {
            worker.Start(); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => worker.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
            // The claim writer is blocked by this real worker lock, not a sleep.
            Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
            Field<ConcurrentDictionary<string, byte>>(engine, "_ordinaryOutputCleanup")[rec.Id] = 0;
        }
        finally { Monitor.Exit(workerGate); Assert.True(worker.Join(TimeSpan.FromSeconds(5))); }
        Assert.Null(error); Assert.True(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
        Field<ConcurrentDictionary<string, byte>>(engine, "_ordinaryOutputCleanup").TryRemove(rec.Id, out _);
        Other(engine, "clean"); Assert.False(Read(engine, rec.OutputPath)!.Paths.Single().InUse);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegisteredPathTextPreservesAbsoluteParentDirectoryAndInitialFailureRelease(bool unregisteredFailure)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        using var backend = new WgcContinuousCaptureBackend(); backend.RetireUnstartedHostResources();
        var path = Path.Combine(_temp.Path, "parent", "..", "main.mp4");
        var rec = Owner(path, backend); Claim(engine, rec, path);
        var canonical = Path.GetFullPath(path);
        Assert.True(Read(engine, canonical)!.Paths.Single().InUse);
        if (unregisteredFailure) Release(engine, rec);
        else { Terminal(rec); Other(engine); }
        Assert.False(Read(engine, canonical)!.Paths.Single().InUse);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FormalPendingCancellationProvesNoResourcesWhileThrowingFactoryRemainsUnknown(bool factoryThrows)
    {
        using var f = new OrdinaryPreparationEngineFixture();
        var calls = 0;
        f.Engine.BackendFactory = _ => { calls++; throw new IOException("factory resource ownership unknown"); };
        var rec = f.Create(directory: _temp.Path);
        if (factoryThrows)
        {
            Assert.Null(Record.Exception(() => f.Ui.Confirm!(ConfirmationDecision.Approve())));
            Assert.True(rec.IsFinalized);
            Assert.Equal(RecState.failed, rec.State); Assert.Equal(1, calls);
            Assert.Equal("backend_creation_failed", rec.Error); Assert.Equal("backend_creation_failed", rec.StopReason);
        }
        else
        {
            f.Engine.Stop(rec.Id, "user_requested");
            Assert.Equal(RecState.cancelled, rec.State); Assert.Equal(0, calls);
        }
        Assert.Null(rec.Backend); Assert.False(rec.BackendStartAttempted);
        Assert.Equal(!factoryThrows, rec.OrdinaryBackendCreation!.ObserveRetired(() => false));
        Other(f.Engine);
        Assert.Equal(factoryThrows, Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse);
        if (factoryThrows)
        {
            Assert.Equal("worker_retirement_unknown", f.Engine.TryReserveIdleHostShutdown(() => null));
            Assert.False(f.Engine.RetentionHostStopping);
        }
        if (!factoryThrows)
        {
            Assert.Throws<ObjectDisposedException>(() => rec.OrdinaryBackendCreation!.Start(_ => { calls++; }, () => false));
            Assert.Equal(0, calls);
        }
    }

    [Theory]
    [InlineData("rejected")] [InlineData("expired")]
    public void FormalNeverMaterializedTerminalClosesEvidenceAndAllowsPathReuse(string outcome)
    {
        using var f = new OrdinaryPreparationEngineFixture(); var calls = 0;
        f.Engine.BackendFactory = _ => { calls++; throw new InvalidOperationException("must not create backend"); };
        var rec = f.Create(directory: _temp.Path);
        if (outcome == "rejected") f.Ui.Confirm!(ConfirmationDecision.Reject());
        else f.Engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!);
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(outcome, rec.State.ToString()); Assert.Equal(0, calls); Assert.Null(rec.Backend);
        Assert.True(rec.OrdinaryBackendCreation!.ObserveRetired(() => false));
        Assert.True(Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse); // GET does not prune.
        Other(f.Engine); Assert.False(Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse);
        Assert.Throws<ObjectDisposedException>(() => rec.OrdinaryBackendCreation.Start(_ => { calls++; }, () => false));
        Assert.Equal(0, calls);
    }

    private sealed class NoUi : ITrayContext
    {
        public string HostMode => "headless"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) { }
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) { }
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { } public void ShowError(string text) { }
    }
    private sealed class UnknownBackend(bool throwStart) : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        internal int Starts;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof proof)
        { Starts++; if (throwStart) throw new InvalidOperationException("unknown resource state"); FirstFrameObserved?.Invoke(new() { FrameNumber = 1, TotalSizeBytes = 1 }); }
        public OutputMeta Stop() => new() { OutputFileExists = false, StopReason = "synthetic_end" };
        public void OnNaturalExit(Action<int, OutputMeta> c) { }
        public int ExitCode => 0; public void Dispose() { }
    }
}
