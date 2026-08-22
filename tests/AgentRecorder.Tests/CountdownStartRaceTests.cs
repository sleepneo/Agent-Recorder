using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class CountdownStartRaceTests
{
    [Fact]
    public void AudioReadyZero_EventAndCatchUpClaimOneOperationAndStartVideoOnce()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ConcurrentAudioBackend();
        var engine = new RecordingEngine(audit, tracer)
        {
            CountdownInterval = TimeSpan.FromMilliseconds(5)
        };
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");

        var rec = CreateRecording(0, microphone: true);
        engine.StartCaptureForTests(rec, tray, "trace_audio_zero_race");

        Assert.True(backend.StartVideoEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, backend.StartVideoCount);
        Assert.Equal(1, engine.ActiveCountdownOperationCountForTests);

        backend.ReleaseAudioReadyCallback.Set();
        Assert.True(backend.AudioReadyCallbackCompleted.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, backend.StartVideoCount);
        Assert.Equal(1, tracer.MicrophoneReadyCount);
        Assert.DoesNotContain("recording.countdown_started", audit.Events);
        Assert.DoesNotContain("recording.countdown_completed", audit.Events);
        Assert.DoesNotContain("recording.countdown_cancelled", audit.Events);

        backend.EmitFirstFrame();
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void StopBeforeZeroAction_OrdinaryFfmpegDoesNotStartOrPublishRecording()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledBackend();
        var engine = CreateEngine(audit, tray, backend, "ffmpeg");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var actionEntered = new ManualResetEventSlim();
        var releaseAction = new ManualResetEventSlim();
        engine.BeforeStartActionForTests = (_, action) =>
        {
            Assert.Equal("backend.start", action);
            actionEntered.Set();
            releaseAction.Wait(TimeSpan.FromSeconds(5));
        };

        var rec = CreateRecording(1);
        engine.StartCaptureForTests(rec, tray, "trace_stop_before_ffmpeg");

        Assert.True(actionEntered.Wait(TimeSpan.FromSeconds(2)));
        var stop = engine.Stop(rec.Id, "user_requested");
        releaseAction.Set();

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, backend.StartCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.DoesNotContain("recording.started", audit.Events);
        Assert.Equal(0, tray.RecordingCount);
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));
        _ = stop;
    }

    [Fact]
    public void StopBeforeZeroAction_AudioDoesNotStartVideoOrPublishRecording()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledAudioBackend();
        var engine = CreateEngine(audit, tray, backend, "ffmpeg-av-split");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var actionEntered = new ManualResetEventSlim();
        var releaseAction = new ManualResetEventSlim();
        engine.BeforeStartActionForTests = (_, action) =>
        {
            Assert.Equal("start_video", action);
            actionEntered.Set();
            releaseAction.Wait(TimeSpan.FromSeconds(5));
        };

        var rec = CreateRecording(1, microphone: true);
        engine.StartCaptureForTests(rec, tray, "trace_stop_before_video");

        Assert.True(actionEntered.Wait(TimeSpan.FromSeconds(2)));
        engine.Stop(rec.Id, "user_requested");
        releaseAction.Set();

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, backend.StartVideoCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.Equal(0, tray.RecordingCount);
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void StopBeforeZeroAction_DeferredDoesNotAuthorizeCaptureOrPublishRecording()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledDeferredBackend();
        var engine = CreateEngine(audit, tray, backend, "wgc-continuous");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var actionEntered = new ManualResetEventSlim();
        var releaseAction = new ManualResetEventSlim();
        engine.BeforeStartActionForTests = (_, action) =>
        {
            Assert.Equal("start_capture", action);
            actionEntered.Set();
            releaseAction.Wait(TimeSpan.FromSeconds(5));
        };

        var rec = CreateRecording(1);
        engine.StartCaptureForTests(rec, tray, "trace_stop_before_capture");

        Assert.True(actionEntered.Wait(TimeSpan.FromSeconds(2)));
        engine.Stop(rec.Id, "user_requested");
        releaseAction.Set();

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, backend.StartCaptureCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.Equal(0, tray.RecordingCount);
        Assert.DoesNotContain("recording.capture_authorization_requested", audit.Events);
        Assert.DoesNotContain("recording.capture_authorization_succeeded", audit.Events);
        Assert.DoesNotContain("recording.capture_authorization_failed", audit.Events);
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StopAfterStartClaim_StopsOrCancelsStartedOrdinaryBackend()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledBackend { BlockStart = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var stopAttempted = new ManualResetEventSlim();
        engine.BeforeStopForTests = _ => stopAttempted.Set();

        var rec = CreateRecording(1);
        engine.StartCaptureForTests(rec, tray, "trace_start_then_stop_ffmpeg");
        Assert.True(backend.StartEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopTask = Task.Run(() => engine.Stop(rec.Id, "user_requested"));
        Assert.True(stopAttempted.Wait(TimeSpan.FromSeconds(2)));
        backend.ReleaseStart.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.Equal(RecState.cancelled, rec.State);
    }

    [Fact]
    public async Task StopAfterStartClaim_StopsOrCancelsStartedAudioBackend()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledAudioBackend { BlockStartVideo = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg-av-split");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var stopAttempted = new ManualResetEventSlim();
        engine.BeforeStopForTests = _ => stopAttempted.Set();

        var rec = CreateRecording(1, microphone: true);
        engine.StartCaptureForTests(rec, tray, "trace_start_then_stop_video");
        Assert.True(backend.StartVideoEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopTask = Task.Run(() => engine.Stop(rec.Id, "user_requested"));
        Assert.True(stopAttempted.Wait(TimeSpan.FromSeconds(2)));
        backend.ReleaseStartVideo.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, backend.StartVideoCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.Equal(RecState.cancelled, rec.State);
    }

    [Fact]
    public async Task StopAfterStartClaim_StopsOrCancelsStartedDeferredBackend()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledDeferredBackend { BlockStartCapture = true };
        var engine = CreateEngine(audit, tray, backend, "wgc-continuous");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(5);
        var stopAttempted = new ManualResetEventSlim();
        engine.BeforeStopForTests = _ => stopAttempted.Set();

        var rec = CreateRecording(1);
        engine.StartCaptureForTests(rec, tray, "trace_start_then_stop_capture");
        Assert.True(backend.StartCaptureEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopTask = Task.Run(() => engine.Stop(rec.Id, "user_requested"));
        Assert.True(stopAttempted.Wait(TimeSpan.FromSeconds(2)));
        backend.ReleaseStartCapture.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, backend.StartCaptureCount);
        Assert.Equal(1, backend.CancelCount);
        Assert.Equal(RecState.cancelled, rec.State);
    }

    [Fact]
    public void PreparationBackendStartException_StopWinsAndOwnsCancelledTerminal()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledBackend { ThrowOnStart = true };
        var engine = CreateEngine(audit, tray, backend, "fake", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionStopWins(
            engine, audit, tracer, tray, rec, "preparation.backend.start",
            () => engine.StartCaptureForTests(rec, tray, "trace_prepare_exception_stop"),
            () => backend.StartCount, () => backend.CancelCount,
            deferred: false);
    }

    [Fact]
    public void PreparationBackendStartException_FailureWinsAndStopIsIdempotent()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledBackend { ThrowOnStart = true };
        var engine = CreateEngine(audit, tray, backend, "fake", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionFailureWins(
            engine, audit, tracer, tray, rec, "preparation.backend.start",
            () => engine.StartCaptureForTests(rec, tray, "trace_prepare_exception_fail"),
            () => backend.StartCount, () => backend.CancelCount,
            expectedError: "start failed", deferred: false);
    }

    [Fact]
    public void OrdinaryZeroBackendStartException_StopWinsAndOwnsCancelledTerminal()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledBackend { ThrowOnStart = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionStopWins(
            engine, audit, tracer, tray, rec, "countdown.backend.start",
            () => engine.StartCaptureForTests(rec, tray, "trace_ordinary_exception_stop"),
            () => backend.StartCount, () => backend.CancelCount,
            deferred: false);
    }

    [Fact]
    public void OrdinaryZeroBackendStartException_FailureWinsAndStopIsIdempotent()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledBackend { ThrowOnStart = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionFailureWins(
            engine, audit, tracer, tray, rec, "countdown.backend.start",
            () => engine.StartCaptureForTests(rec, tray, "trace_ordinary_exception_fail"),
            () => backend.StartCount, () => backend.CancelCount,
            expectedError: "start failed", deferred: false);
    }

    [Fact]
    public void AudioZeroStartVideoException_StopWinsAndOwnsCancelledTerminal()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledAudioBackend { ThrowOnStartVideo = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg-av-split", tracer);
        var rec = CreateRecording(0, microphone: true);

        AssertStartExceptionStopWins(
            engine, audit, tracer, tray, rec, "countdown.start_video",
            () => engine.StartCaptureForTests(rec, tray, "trace_audio_exception_stop"),
            () => backend.StartVideoCount, () => backend.CancelCount,
            deferred: false);
    }

    [Fact]
    public void AudioZeroStartVideoException_FailureWinsAndStopIsIdempotent()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledAudioBackend { ThrowOnStartVideo = true };
        var engine = CreateEngine(audit, tray, backend, "ffmpeg-av-split", tracer);
        var rec = CreateRecording(0, microphone: true);

        AssertStartExceptionFailureWins(
            engine, audit, tracer, tray, rec, "countdown.start_video",
            () => engine.StartCaptureForTests(rec, tray, "trace_audio_exception_fail"),
            () => backend.StartVideoCount, () => backend.CancelCount,
            expectedError: "Failed to start video capture: video start failed", deferred: false);
    }

    [Fact]
    public void DeferredZeroStartCaptureException_StopWinsAndOwnsCancelledTerminal()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledDeferredBackend { ThrowOnStartCapture = true };
        var engine = CreateEngine(audit, tray, backend, "wgc-continuous", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionStopWins(
            engine, audit, tracer, tray, rec, "countdown.start_capture",
            () => engine.StartCaptureForTests(rec, tray, "trace_deferred_exception_stop"),
            () => backend.StartCaptureCount, () => backend.CancelCount,
            deferred: true);
    }

    [Fact]
    public void DeferredZeroStartCaptureException_FailureWinsAndStopIsIdempotent()
    {
        var audit = new MemoryAudit();
        var tracer = new CountingTracer();
        var tray = new RaceTray();
        var backend = new ControlledDeferredBackend { ThrowOnStartCapture = true };
        var engine = CreateEngine(audit, tray, backend, "wgc-continuous", tracer);
        var rec = CreateRecording(0);

        AssertStartExceptionFailureWins(
            engine, audit, tracer, tray, rec, "countdown.start_capture",
            () => engine.StartCaptureForTests(rec, tray, "trace_deferred_exception_fail"),
            () => backend.StartCaptureCount, () => backend.CancelCount,
            expectedError: "Failed to authorize capture start: capture start failed", deferred: true);
    }

    [Fact]
    public void ZeroCountdown_DeferredSkipsVisibleEventsButStillWaitsForFirstFrame()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var backend = new ControlledDeferredBackend { EmitFirstFrame = true };
        var engine = CreateEngine(audit, tray, backend, "wgc-continuous");

        var rec = CreateRecording(0);
        engine.StartCaptureForTests(rec, tray, "trace_zero_deferred");

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, backend.StartCaptureCount);
        Assert.Empty(tray.CountdownValuesFor(rec.Id));
        Assert.DoesNotContain("recording.countdown_started", audit.Events);
        Assert.DoesNotContain("recording.countdown_completed", audit.Events);
        Assert.DoesNotContain("recording.countdown_cancelled", audit.Events);
    }

    [Fact]
    public void NestedRecordingsKeepIndependentCountdownsFirstFramesAndMarks()
    {
        var audit = new MemoryAudit();
        var tray = new RaceTray();
        var outerBackend = new ControlledBackend { EmitFirstFrame = true };
        var innerBackend = new ControlledBackend { EmitFirstFrame = true };
        var engine = new RecordingEngine(audit)
        {
            CountdownInterval = TimeSpan.FromMilliseconds(5)
        };
        engine.SetTray(tray);
        engine.BackendFactory = cfg => cfg.OutputPath.Contains("outer", StringComparison.Ordinal)
            ? (outerBackend, "ffmpeg")
            : (innerBackend, "ffmpeg");

        var outer = CreateRecording(1, outputStem: "outer");
        var inner = CreateRecording(3, outputStem: "inner");
        engine.StartCaptureForTests(outer, tray, "trace_outer");
        engine.StartCaptureForTests(inner, tray, "trace_inner");

        Assert.True(SpinWait.SpinUntil(() => outer.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => inner.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { 1 }, tray.CountdownValuesFor(outer.Id).ToArray());
        Assert.Equal(new[] { 3, 2, 1 }, tray.CountdownValuesFor(inner.Id).ToArray());
        Assert.NotEqual(default, outer.StartedAtUtc);
        Assert.NotEqual(default, inner.StartedAtUtc);

        var outerMark = engine.AddMark(outer.Id, "outer-mark");
        var innerMark = engine.AddMark(inner.Id, "inner-mark");
        Assert.Equal("outer-mark", outer.SnapshotMarks().Single().Label);
        Assert.Equal("inner-mark", inner.SnapshotMarks().Single().Label);
        Assert.True(outerMark.TMs >= 0);
        Assert.True(innerMark.TMs >= 0);

        engine.Stop(outer.Id, "test_cleanup");
        engine.Stop(inner.Id, "test_cleanup");
    }

    private static void AssertStartExceptionStopWins(
        RecordingEngine engine,
        MemoryAudit audit,
        CountingTracer tracer,
        RaceTray tray,
        Recording rec,
        string expectedStage,
        Action start,
        Func<int> startCount,
        Func<int> cancelCount,
        bool deferred)
    {
        using var failureEntered = new ManualResetEventSlim();
        using var releaseFailure = new ManualResetEventSlim();
        engine.BeforeStartFailureForTests = (actualRec, stage) =>
        {
            Assert.Same(rec, actualRec);
            Assert.Equal(expectedStage, stage);
            failureEntered.Set();
            releaseFailure.Wait(TimeSpan.FromSeconds(5));
        };

        var startTask = Task.Run(start);
        Assert.True(failureEntered.Wait(TimeSpan.FromSeconds(2)), "startup exception did not reach the failure barrier");

        engine.Stop(rec.Id, "user_requested");
        releaseFailure.Set();
        startTask.Wait(TimeSpan.FromSeconds(3));

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.cancelled, TimeSpan.FromSeconds(2)),
            $"state={rec.State}, finalized={rec.IsFinalized}, stop_reason={rec.StopReason}, error={rec.Error}");
        Assert.Equal(RecState.cancelled, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.Equal("user_requested", rec.StopReason);
        Assert.Null(rec.Error);
        Assert.Empty(rec.Warnings);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.NotEqual(default, rec.CompletedAtUtc);
        Assert.Equal(1, startCount());
        Assert.Equal(1, cancelCount());
        Assert.Equal(0, tray.RecordingCount);
        Assert.Equal(0, tray.ShowErrorCount);
        Assert.Equal(1, tray.IdleCount);
        Assert.Equal(0, tracer.CaptureBackendStartFailedCount);
        Assert.Equal(new[] { "cancelled" }, tracer.RecordingTerminalStatuses.ToArray());
        Assert.Equal(1, audit.Events.Count(e => e == "recording.cancelled"));
        Assert.Equal(0, audit.Events.Count(e => e == "recording.failed"));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));

        if (deferred)
        {
            Assert.Equal(1, audit.Events.Count(e => e == "recording.capture_authorization_requested"));
            Assert.DoesNotContain("recording.capture_authorization_succeeded", audit.Events);
            Assert.DoesNotContain("recording.capture_authorization_failed", audit.Events);
        }
    }

    private static void AssertStartExceptionFailureWins(
        RecordingEngine engine,
        MemoryAudit audit,
        CountingTracer tracer,
        RaceTray tray,
        Recording rec,
        string expectedStage,
        Action start,
        Func<int> startCount,
        Func<int> cancelCount,
        string expectedError,
        bool deferred)
    {
        using var failureEntered = new ManualResetEventSlim();
        using var releaseFailure = new ManualResetEventSlim();
        engine.BeforeStartFailureForTests = (actualRec, stage) =>
        {
            Assert.Same(rec, actualRec);
            Assert.Equal(expectedStage, stage);
            failureEntered.Set();
            releaseFailure.Wait(TimeSpan.FromSeconds(5));
        };

        var startTask = Task.Run(start);
        Assert.True(failureEntered.Wait(TimeSpan.FromSeconds(2)), "startup exception did not reach the failure barrier");
        releaseFailure.Set();
        startTask.Wait(TimeSpan.FromSeconds(3));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.failed, TimeSpan.FromSeconds(2)));

        var completedAt = rec.CompletedAtUtc;
        var stopReason = rec.StopReason;
        var error = rec.Error;
        var warningCount = rec.Warnings.Count;

        Assert.Equal(RecState.failed, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.Equal(expectedError, error);
        Assert.False(string.IsNullOrWhiteSpace(stopReason));
        Assert.NotEqual(default, completedAt);
        Assert.Single(rec.Warnings);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Equal(1, startCount());
        Assert.Equal(0, cancelCount());
        Assert.Equal(0, tray.RecordingCount);
        Assert.Equal(1, tray.ShowErrorCount);
        Assert.Equal(1, tray.IdleCount);
        Assert.Equal(1, tracer.CaptureBackendStartFailedCount);
        Assert.Equal(new[] { "failed" }, tracer.RecordingTerminalStatuses.ToArray());
        Assert.Equal(1, audit.Events.Count(e => e == "recording.failed"));
        Assert.Equal(0, audit.Events.Count(e => e == "recording.cancelled"));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0, TimeSpan.FromSeconds(2)));

        if (deferred)
        {
            Assert.Equal(1, audit.Events.Count(e => e == "recording.capture_authorization_requested"));
            Assert.DoesNotContain("recording.capture_authorization_succeeded", audit.Events);
            Assert.DoesNotContain("recording.capture_authorization_failed", audit.Events);
        }

        engine.Stop(rec.Id, "late_stop");

        Assert.Equal(RecState.failed, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.Equal(completedAt, rec.CompletedAtUtc);
        Assert.Equal(stopReason, rec.StopReason);
        Assert.Equal(error, rec.Error);
        Assert.Equal(warningCount, rec.Warnings.Count);
        Assert.Equal(0, cancelCount());
        Assert.Equal(1, tray.ShowErrorCount);
        Assert.Equal(1, tray.IdleCount);
        Assert.Equal(1, tracer.CaptureBackendStartFailedCount);
        Assert.Equal(new[] { "failed" }, tracer.RecordingTerminalStatuses.ToArray());
        Assert.Equal(1, audit.Events.Count(e => e == "recording.failed"));
        Assert.Equal(0, audit.Events.Count(e => e == "recording.cancelled"));
    }

    private static RecordingEngine CreateEngine(
        MemoryAudit audit,
        RaceTray tray,
        ICaptureBackend backend,
        string backendType,
        IPerformanceTracer? tracer = null)
    {
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, backendType);
        return engine;
    }

    private static Recording CreateRecording(int countdownSeconds, bool microphone = false, string outputStem = "race")
    {
        var output = Path.Combine(Path.GetTempPath(), $"countdown-{outputStem}-{Guid.NewGuid():N}.mp4");
        return new Recording
        {
            SourceType = "display",
            OutputPath = output,
            Microphone = microphone,
            CountdownSeconds = countdownSeconds,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 320, 240),
                OutputPath = output,
                Microphone = microphone,
                CountdownSeconds = countdownSeconds
            }
        };
    }

    private sealed class MemoryAudit : AuditLogger
    {
        public MemoryAudit() : base(Path.Combine(Path.GetTempPath(), $"countdown-race-audit-{Guid.NewGuid():N}.jsonl")) { }
        public List<string> Events { get; } = new();
        public override void Log(string evt, object payload)
        {
            lock (Events)
                Events.Add(evt);
        }
    }

    private sealed class RaceTray : ITrayContext
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<int>> _countdowns = new();
        private int _recordingCount;
        private int _idleCount;
        private int _showErrorCount;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public int RecordingCount => Volatile.Read(ref _recordingCount);
        public int IdleCount => Volatile.Read(ref _idleCount);
        public int ShowErrorCount => Volatile.Read(ref _showErrorCount);
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec)
        {
            Interlocked.Increment(ref _recordingCount);
        }
        public void SetIdle(object rec) { Interlocked.Increment(ref _idleCount); }
        public void SetAllIdle() { }
        public void ShowError(string text) { Interlocked.Increment(ref _showErrorCount); }
        public void SetPreparing(object rec) { }
        public void SetCountdown(object rec, int? remainingSeconds)
        {
            if (remainingSeconds is not int value)
                return;

            var recording = (Recording)rec;
            lock (_gate)
            {
                if (!_countdowns.TryGetValue(recording.Id, out var values))
                    _countdowns[recording.Id] = values = new List<int>();
                values.Add(value);
            }
        }
        public void SetFinalizing(object rec) { }
        public IReadOnlyList<int> CountdownValuesFor(string id)
        {
            lock (_gate)
                return _countdowns.TryGetValue(id, out var values) ? values.ToArray() : Array.Empty<int>();
        }
    }

    private sealed class ControlledBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool BlockStart { get; init; }
        public bool EmitFirstFrame { get; init; }
        public bool ThrowOnStart { get; init; }
        public ManualResetEventSlim StartEntered { get; } = new();
        public ManualResetEventSlim ReleaseStart { get; } = new();
        public ManualResetEventSlim CancelEntered { get; } = new();
        public int StartCount;
        public int CancelCount;

        public void Start(CaptureConfig cfg)
        {
            Interlocked.Increment(ref StartCount);
            StartEntered.Set();
            if (BlockStart)
                ReleaseStart.Wait(TimeSpan.FromSeconds(5));
            if (ThrowOnStart)
                throw new InvalidOperationException("start failed");
            if (EmitFirstFrame)
                EmitFrame();
        }

        public OutputMeta Stop() => new();
        public void Cancel()
        {
            Interlocked.Increment(ref CancelCount);
            CancelEntered.Set();
        }
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void EmitFrame() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "deterministic_test_frame",
            FrameNumber = 1,
            TotalSizeBytes = 1
        });
        public void Dispose() { }
    }

    private sealed class ControlledAudioBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action? AudioReady;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool IsAudioReady => Volatile.Read(ref _audioReady) != 0;
        public bool BlockStartVideo { get; init; }
        public bool ThrowOnStartVideo { get; init; }
        public ManualResetEventSlim StartVideoEntered { get; } = new();
        public ManualResetEventSlim ReleaseStartVideo { get; } = new();
        public int StartVideoCount;
        public int CancelCount;
        private int _audioReady;

        public void Start(CaptureConfig cfg)
        {
            Volatile.Write(ref _audioReady, 1);
            AudioReady?.Invoke();
        }
        public void StartVideo()
        {
            Interlocked.Increment(ref StartVideoCount);
            StartVideoEntered.Set();
            if (BlockStartVideo)
                ReleaseStartVideo.Wait(TimeSpan.FromSeconds(5));
            if (ThrowOnStartVideo)
                throw new InvalidOperationException("video start failed");
        }
        public OutputMeta Stop() => new();
        public void Cancel() => Interlocked.Increment(ref CancelCount);
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void EmitFirstFrame() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "controlled_audio_test_frame",
            FrameNumber = 1,
            TotalSizeBytes = 1
        });
        public void Dispose() { }
    }

    private sealed class ConcurrentAudioBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action? AudioReady;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool IsAudioReady => Volatile.Read(ref _audioReady) != 0;
        public ManualResetEventSlim AudioReadyCallbackEntered { get; } = new();
        public ManualResetEventSlim ReleaseAudioReadyCallback { get; } = new();
        public ManualResetEventSlim AudioReadyCallbackCompleted { get; } = new();
        public ManualResetEventSlim StartVideoEntered { get; } = new();
        public int StartVideoCount;
        private int _audioReady;

        public void Start(CaptureConfig cfg)
        {
            Volatile.Write(ref _audioReady, 1);
            _ = Task.Run(() =>
            {
                AudioReadyCallbackEntered.Set();
                ReleaseAudioReadyCallback.Wait(TimeSpan.FromSeconds(5));
                try { AudioReady?.Invoke(); }
                finally { AudioReadyCallbackCompleted.Set(); }
            });
        }
        public void StartVideo()
        {
            Interlocked.Increment(ref StartVideoCount);
            StartVideoEntered.Set();
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void EmitFirstFrame() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "concurrent_audio_test_frame",
            FrameNumber = 1,
            TotalSizeBytes = 1
        });
        public void Dispose() { }
    }

    private sealed class ControlledDeferredBackend : ICaptureBackend, IDeferredCaptureStartBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<bool>? CaptureAuthorizationCompleted;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool IsAwaitingCaptureStart => _prepared && StartCaptureCount == 0;
        public bool BlockStartCapture { get; init; }
        public bool EmitFirstFrame { get; init; }
        public bool ThrowOnStartCapture { get; init; }
        public ManualResetEventSlim StartCaptureEntered { get; } = new();
        public ManualResetEventSlim ReleaseStartCapture { get; } = new();
        public int StartCaptureCount;
        public int CancelCount;
        private bool _prepared;

        public void Start(CaptureConfig cfg) => _prepared = true;
        public void StartCapture()
        {
            Interlocked.Increment(ref StartCaptureCount);
            StartCaptureEntered.Set();
            if (BlockStartCapture)
                ReleaseStartCapture.Wait(TimeSpan.FromSeconds(5));
            if (ThrowOnStartCapture)
                throw new InvalidOperationException("capture start failed");
            if (EmitFirstFrame)
                FirstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    EvidenceKind = "deferred_test_frame",
                    FrameNumber = 1,
                    TotalSizeBytes = 1
                });
            CaptureAuthorizationCompleted?.Invoke(true);
        }
        public OutputMeta Stop() => new();
        public void Cancel() => Interlocked.Increment(ref CancelCount);
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }
    }

    private sealed class CountingTracer : IPerformanceTracer
    {
        public int MicrophoneReadyCount;
        public int CaptureBackendStartFailedCount;
        public List<string> RecordingTerminalStatuses { get; } = new();
        public void MicrophoneReady(string traceId, string recordingId) => Interlocked.Increment(ref MicrophoneReadyCount);
        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) { }
        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) { }
        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) { }
        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) { }
        public bool HasValidationResult(string traceId) => false;
        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) { }
        public void CaptureStartRequested(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) =>
            Interlocked.Increment(ref CaptureBackendStartFailedCount);
        public void MicrophonePrepareStarted(string traceId, string recordingId) { }
        public void CountdownStarted(string traceId, string recordingId) { }
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) { }
        public void CaptureEnded(string traceId, string recordingId) { }
        public void FinalizationCompleted(string traceId, string recordingId, bool success) { }
        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null)
        {
            lock (RecordingTerminalStatuses)
                RecordingTerminalStatuses.Add(status);
        }
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) { }
        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
    }
}
