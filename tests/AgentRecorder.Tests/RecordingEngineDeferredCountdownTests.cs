using System;
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

/// <summary>
/// Verifies the no-microphone WGC deferred-start countdown lifecycle: the
/// backend/helper is prepared but not authorized while the 3-2-1 countdown is
/// visible, countdown zero authorizes capture exactly once without showing red
/// REC, and only explicit first-frame evidence transitions to recording with
/// truthful public timing.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingEngineDeferredCountdownTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _auditPath;
    private readonly InMemoryAuditLogger _auditLogger;
    private readonly string? _originalDataDir;

    public RecordingEngineDeferredCountdownTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"deferred-countdown-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _auditPath = Path.Combine(_tempDir, "audit.jsonl");
        _auditLogger = new InMemoryAuditLogger(_auditPath);

        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _tempDir);
    }

    public void Dispose()
    {
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        // The audit sink is in-memory and per-test: any late asynchronous
        // engine callback can only append to this dead sink, never to a file
        // inside _tempDir, so no audit write can outlive this Dispose or lock
        // the directory deletion below.
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Narrow in-memory audit sink for these lifecycle tests (repository
    /// convention: AuditLogger.Log is virtual and several test classes
    /// subclass it). It deliberately does NOT call base.Log, so no file
    /// writer exists and event-name assertions can never race
    /// File.AppendAllText on audit.jsonl.
    /// </summary>
    private sealed class InMemoryAuditLogger : AuditLogger
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();

        public InMemoryAuditLogger(string path) : base(path) { }

        public override void Log(string evt, object payload)
        {
            lock (_gate) _events.Add(evt);
        }

        public List<string> SnapshotEventNames()
        {
            lock (_gate) return _events.ToList();
        }
    }

    private sealed class FakeTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public int SetRecordingCallCount;
        public int SetIdleCallCount;
        public int SetPreparingCallCount;
        public int SetFinalizingCallCount;
        public string? LastError;
        public readonly List<int?> CountdownValues = new();
        public readonly object CountdownGate = new();

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(object rec) { Interlocked.Increment(ref SetRecordingCallCount); }
        public void SetIdle(object rec) { Interlocked.Increment(ref SetIdleCallCount); }
        public void SetAllIdle() { Interlocked.Increment(ref SetIdleCallCount); }
        public void ShowError(string text) { LastError = text; }
        public void SetPreparing(object rec) { Interlocked.Increment(ref SetPreparingCallCount); }
        public void SetCountdown(object rec, int? remainingSeconds)
        {
            lock (CountdownGate) CountdownValues.Add(remainingSeconds);
        }
        public void SetFinalizing(object rec) { Interlocked.Increment(ref SetFinalizingCallCount); }

        public int?[] SnapshotCountdownValues()
        {
            lock (CountdownGate) return CountdownValues.ToArray();
        }
    }

    private sealed class FakeDeferredBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, IDeferredCaptureStartBackend
    {
        private readonly object _eventGate = new();
        private Action<FirstFrameObservation>? _firstFrameHandlers;

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add { lock (_eventGate) _firstFrameHandlers += value; }
            remove { lock (_eventGate) _firstFrameHandlers -= value; }
        }

        public event Action<bool>? CaptureAuthorizationCompleted;

        /// <summary>
        /// Live subscriber count for the FirstFrameObserved event. The engine
        /// attaches exactly one long-lived handler at StartCapture; the
        /// countdown operation's local handler must bring this to 2 during the
        /// wait and back to 1 after retirement.
        /// </summary>
        public int FirstFrameSubscriberCount
        {
            get { lock (_eventGate) return _firstFrameHandlers?.GetInvocationList().Length ?? 0; }
        }

        public int StartCaptureCallCount;
        public bool StartCalled;
        public bool CancelCalled;
        public int StopCallCount;
        public bool ThrowOnStartCapture;
        public bool AuthorizationResult = true;
        public bool NotifyAuthorizationCompleted = true;
        public bool AutoFirstFrameOnStartCapture;
        public OutputMeta StopResult = new();
        public int ExitCode => 0;

        public bool IsAwaitingCaptureStart => StartCalled && StartCaptureCallCount == 0 && !CancelCalled;

        public void Start(CaptureConfig cfg) { StartCalled = true; }

        public void StartCapture()
        {
            Interlocked.Increment(ref StartCaptureCallCount);
            if (ThrowOnStartCapture)
                throw new InvalidOperationException("cannot authorize");

            if (AutoFirstFrameOnStartCapture)
                EmitFirstFrame();

            if (NotifyAuthorizationCompleted)
            {
                bool result = AuthorizationResult;
                Task.Run(() =>
                {
                    Thread.Sleep(10);
                    CaptureAuthorizationCompleted?.Invoke(result);
                });
            }
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref StopCallCount);
            return StopResult;
        }

        public void Cancel() { CancelCalled = true; }
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }

        public void EmitFirstFrame()
        {
            Action<FirstFrameObservation>? handlers;
            lock (_eventGate) handlers = _firstFrameHandlers;
            handlers?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "wgc_continuous_first_frame",
                FrameNumber = 1,
                TotalSizeBytes = 0,
                OutTimeUs = 0
            });
        }
    }

    private RecordingEngine CreateEngine(out FakeTray tray, FakeDeferredBackend backend)
    {
        tray = new FakeTray();
        var engine = new RecordingEngine(_auditLogger);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, "wgc-continuous");
        return engine;
    }

    private static Recording CreateRecording(int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "display",
            OutputPath = Path.Combine(Path.GetTempPath(), $"deferred-{Guid.NewGuid():N}.mp4"),
            Microphone = false,
            DurationSeconds = durationSeconds,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 100, 100),
                Microphone = false,
                DurationSeconds = durationSeconds,
                OutputPath = Path.Combine(Path.GetTempPath(), $"deferred-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    private List<string> ReadAuditEventNames() => _auditLogger.SnapshotEventNames();

    /// <summary>
    /// Bounded wait for an audit event that may be appended by a legitimate
    /// asynchronous engine/backend callback. Deterministic: fails the test
    /// after the timeout instead of racing the writer.
    /// </summary>
    private void WaitForAuditEvent(string name, int timeoutMs = 3000)
    {
        Assert.True(SpinWait.SpinUntil(() => ReadAuditEventNames().Contains(name),
            TimeSpan.FromMilliseconds(timeoutMs)),
            $"audit event '{name}' must be recorded within {timeoutMs} ms");
    }

    [Fact]
    public void DeferredBackend_EntersCountdownBeforeAuthorization_NoCaptureStartDuringCountdown()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(120);
        engine.CountdownSteps = 3;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        // The countdown must begin immediately (no microphone needed) and the
        // backend must remain unauthorized while digits are visible.
        Assert.Equal(RecState.countdown, rec.State);
        Assert.True(rec.CountdownStartedAtUtc.HasValue);
        Assert.True(backend.StartCalled);
        Assert.Equal(0, backend.StartCaptureCallCount);
        Assert.True(backend.IsAwaitingCaptureStart);
        Assert.True(tray.SetPreparingCallCount >= 1);

        // Wait until the countdown has visibly stepped to 2 and still prove no
        // capture-start call has occurred.
        Assert.True(SpinWait.SpinUntil(() =>
            tray.SnapshotCountdownValues().Contains(2), TimeSpan.FromSeconds(3)),
            "countdown digit 2 must have been shown");
        Assert.Equal(0, backend.StartCaptureCallCount);

        // At countdown zero the engine authorizes exactly once.
        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(3)),
            "capture must be authorized exactly once at countdown zero");
        Assert.Equal(1, backend.StartCaptureCallCount);

        // Settle the recording so no pending first-frame timeout outlives the test.
        backend.EmitFirstFrame();
        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CountdownDigits_Sequence321_ThenOverlayCleared_NoRecAtCountdownZero()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 3;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(3)));

        var values = tray.SnapshotCountdownValues();
        Assert.True(values.Length >= 4, $"expected 3 digits plus the clearing call, got {values.Length}");
        Assert.Equal(new int?[] { 3, 2, 1, null }, values.Take(4).ToArray());

        // Countdown zero is NOT first-frame evidence: still countdown state,
        // still amber (preparing), and no red REC presentation.
        Assert.Equal(RecState.countdown, rec.State);
        Assert.Equal(default, rec.StartedAtUtc);
        Assert.Equal(0, tray.SetRecordingCallCount);

        // Settle the recording so no pending first-frame timeout outlives the test.
        backend.EmitFirstFrame();
        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void FirstFrame_AfterDeferredAuthorization_TransitionsOnce_StartsTruthfulTiming()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(RecState.countdown, rec.State);
        Assert.Equal(0, tray.SetRecordingCallCount);

        backend.EmitFirstFrame();
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));

        Assert.NotEqual(default, rec.StartedAtUtc);
        Assert.True(rec.StartedAtUtc > rec.CountdownStartedAtUtc,
            "public started_at must be the first-frame evidence time, not countdown start");
        Assert.Equal(1, tray.SetRecordingCallCount);

        // Duplicate first-frame events must not re-show REC.
        backend.EmitFirstFrame();
        backend.EmitFirstFrame();
        Thread.Sleep(100);
        Assert.Equal(1, tray.SetRecordingCallCount);
    }

    [Fact]
    public void StopDuringCountdown_CancelledDeterministically_NoAuthorization_NoRecUi()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(500);
        engine.CountdownSteps = 3;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.countdown, rec.State);

        engine.Stop(rec.Id, "user_stop_during_countdown");

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.True(backend.CancelCalled);
        Assert.Equal(0, backend.StartCaptureCallCount);
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.True(tray.SetIdleCallCount >= 1);

        // The countdown task must observe cancellation and never authorize later.
        Thread.Sleep(200);
        Assert.Equal(0, backend.StartCaptureCallCount);

        WaitForAuditEvent("recording.countdown_cancelled");
        var events = ReadAuditEventNames();
        Assert.Contains("recording.countdown_cancelled", events);
        Assert.DoesNotContain("recording.countdown_completed", events);
        Assert.DoesNotContain("recording.capture_authorization_requested", events);
    }

    [Fact]
    public void StartCaptureThrow_AtCountdownZero_FailsDeterministically()
    {
        var backend = new FakeDeferredBackend { ThrowOnStartCapture = true };
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        dynamic status = engine.GetStatusWait(rec.Id, sinceStatus: "countdown", waitMs: 5000);

        Assert.Equal("failed", (string)status.Status);
        Assert.Contains("authorize capture start", rec.Error);
        Assert.Contains(rec.Warnings, w => w.StartsWith("capture_start_failed", StringComparison.Ordinal));
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.True(tray.SetIdleCallCount >= 1);
        Assert.NotNull(tray.LastError);
    }

    [Fact]
    public void AuthorizationFailureAfterCountdown_SingleFailedTerminal_NoRecUi()
    {
        var backend = new FakeDeferredBackend { AuthorizationResult = false };
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromMilliseconds(150);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        dynamic status = engine.GetStatusWait(rec.Id, sinceStatus: "countdown", waitMs: 5000);

        Assert.Equal("failed", (string)status.Status);
        Assert.Contains(rec.Warnings, w => w == "first_frame_timeout");
        Assert.True(SpinWait.SpinUntil(() => backend.CancelCalled, TimeSpan.FromSeconds(2)),
            "failed authorization path must cancel the backend to terminate the waiting helper");
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.True(tray.SetIdleCallCount >= 1);

        // authorization_failed is appended by the asynchronous authorization
        // callback; wait for it deterministically before asserting counts.
        WaitForAuditEvent("recording.capture_authorization_failed");
        var events = ReadAuditEventNames();
        Assert.Contains("recording.capture_authorization_requested", events);
        Assert.Contains("recording.capture_authorization_failed", events);
        Assert.DoesNotContain("recording.capture_authorization_succeeded", events);
        Assert.Equal(1, events.Count(e => e == "recording.failed"));
    }

    [Fact]
    public void SyntheticCompletion_PublicElapsedIsCredibleNonZero_RecStaysUntilCaptureEnd()
    {
        var stopMeta = new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            OutputFileExists = true,
            AudioStatus = "not_requested",
            AudioContinuityStatus = "not_checked"
        };
        var backend = new FakeDeferredBackend
        {
            AutoFirstFrameOnStartCapture = true,
            StopResult = stopMeta
        };
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording(durationSeconds: 1);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)),
            "one-second synthetic recording must finalize via the duration watchdog");

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("duration_reached", rec.StopReason);

        // Public timing must be credibly close to the one-second capture, not zero.
        Assert.NotEqual(default, rec.StartedAtUtc);
        var publicInterval = rec.CompletedAtUtc - rec.StartedAtUtc;
        Assert.True(publicInterval >= TimeSpan.FromMilliseconds(800),
            $"public interval {publicInterval} must be credibly close to the 1s capture");
        Assert.True(publicInterval <= TimeSpan.FromSeconds(3),
            $"public interval {publicInterval} must not be inflated by countdown/finalization");

        // Red REC was shown exactly once and remained until capture end.
        Assert.Equal(1, tray.SetRecordingCallCount);
        Assert.True(tray.SetFinalizingCallCount >= 1);
        Assert.True(tray.SetIdleCallCount >= 1);

        dynamic status = engine.GetStatus(rec.Id);
        Assert.Equal("completed", (string)status.status);
        Assert.True((int)status.elapsed_seconds >= 0);
    }

    [Fact]
    public void DeferredPath_AuditSequence_IsCompleteAndOrdered()
    {
        var backend = new FakeDeferredBackend { AutoFirstFrameOnStartCapture = true };
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));

        engine.Stop(rec.Id, "user_stop_for_audit_check");
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(3)));
        WaitForAuditEvent("recording.capture_authorization_succeeded");

        var events = ReadAuditEventNames();
        int prepared = events.IndexOf("recording.capture_backend_prepared");
        int countdownStarted = events.IndexOf("recording.countdown_started");
        int countdownCompleted = events.IndexOf("recording.countdown_completed");
        int authRequested = events.IndexOf("recording.capture_authorization_requested");
        int firstFrame = events.IndexOf("recording.first_frame_observed");

        Assert.True(prepared >= 0, "missing recording.capture_backend_prepared");
        Assert.True(countdownStarted > prepared, "countdown must start after backend preparation");
        Assert.True(countdownCompleted > countdownStarted, "countdown must complete after it started");
        Assert.True(authRequested > countdownCompleted, "authorization must be requested at countdown zero");
        Assert.True(firstFrame > authRequested, "first frame must follow authorization");
        Assert.Contains("recording.capture_authorization_succeeded", events);
        Assert.DoesNotContain("recording.countdown_cancelled", events);
    }

    // -----------------------------------------------------------------
    // Countdown/first-frame-wait resource lifecycle tests (Task 196B)
    // -----------------------------------------------------------------

    [Fact]
    public void StopDuringVisibleCountdown_RetiresOperationAndSubscriptions_AuditsCancelExactlyOnce()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        // Very long steps: only a prompt cancellation can retire the operation.
        engine.CountdownInterval = TimeSpan.FromMinutes(10);
        engine.CountdownSteps = 3;
        engine.FirstFrameTimeout = TimeSpan.FromMinutes(10);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.countdown, rec.State);
        Assert.Equal(1, engine.ActiveCountdownOperationCountForTests);
        Assert.Equal(1, backend.FirstFrameSubscriberCount);

        engine.Stop(rec.Id, "user_stop_visible_countdown");

        Assert.Equal(RecState.cancelled, rec.State);
        // Prompt retirement: the 10-minute countdown delay must have been
        // cancelled, the CTS disposed, and the operation unregistered.
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0,
            TimeSpan.FromSeconds(2)), "countdown operation must be retired promptly after stop");
        // The local first-frame handler was never attached (countdown phase),
        // so only the engine's own long-lived handler remains.
        Assert.Equal(1, backend.FirstFrameSubscriberCount);

        WaitForAuditEvent("recording.countdown_cancelled");
        var events = ReadAuditEventNames();
        Assert.Equal(1, events.Count(e => e == "recording.countdown_cancelled"));
        Assert.DoesNotContain("recording.countdown_completed", events);
    }

    [Fact]
    public void StopAfterCountdownZero_CancelsWaitPromptly_NoFalseCancelAudit_RetiresResources()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        // Very long first-frame timeout: only a prompt cancellation of the wait
        // can retire the operation quickly.
        engine.FirstFrameTimeout = TimeSpan.FromMinutes(10);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(RecState.countdown, rec.State);
        // During the first-frame wait the local handler is attached on top of
        // the engine's own handler.
        Assert.True(SpinWait.SpinUntil(() => backend.FirstFrameSubscriberCount == 2, TimeSpan.FromSeconds(2)),
            "local first-frame wait handler must be attached during the wait");

        engine.Stop(rec.Id, "user_stop_after_countdown_zero");

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0,
            TimeSpan.FromSeconds(2)), "first-frame wait must be cancelled promptly after stop");
        Assert.True(SpinWait.SpinUntil(() => backend.FirstFrameSubscriberCount == 1,
            TimeSpan.FromSeconds(2)), "local first-frame handler must be detached after stop");

        WaitForAuditEvent("recording.countdown_completed");
        var events = ReadAuditEventNames();
        Assert.Contains("recording.countdown_completed", events);
        Assert.DoesNotContain("recording.countdown_cancelled", events);
    }

    [Fact]
    public void FirstFrameSuccess_RetiresOperationAndLocalSubscriptionPromptly()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        // Long timeout: if the operation were not retired on success, it would
        // linger far beyond the test window.
        engine.FirstFrameTimeout = TimeSpan.FromMinutes(10);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(3)));
        backend.EmitFirstFrame();
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));

        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0,
            TimeSpan.FromSeconds(2)), "operation must be retired promptly after first-frame success");
        Assert.True(SpinWait.SpinUntil(() => backend.FirstFrameSubscriberCount == 1,
            TimeSpan.FromSeconds(2)), "local first-frame handler must be detached after success");

        WaitForAuditEvent("recording.countdown_completed");
        var events = ReadAuditEventNames();
        Assert.Contains("recording.countdown_completed", events);
        Assert.DoesNotContain("recording.countdown_cancelled", events);

        // Settle the recording.
        engine.Stop(rec.Id, "cleanup_stop");
    }

    [Fact]
    public void FirstFrameTimeoutFailure_RetiresOperationAndLocalSubscription()
    {
        var backend = new FakeDeferredBackend();
        var engine = CreateEngine(out var tray, backend);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromMilliseconds(120);

        var rec = CreateRecording();
        engine.StartCaptureForTests(rec, tray);

        dynamic status = engine.GetStatusWait(rec.Id, sinceStatus: "countdown", waitMs: 5000);
        Assert.Equal("failed", (string)status.Status);
        Assert.Contains(rec.Warnings, w => w == "first_frame_timeout");

        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0,
            TimeSpan.FromSeconds(2)), "operation must be retired after first-frame timeout");
        Assert.True(SpinWait.SpinUntil(() => backend.FirstFrameSubscriberCount == 1,
            TimeSpan.FromSeconds(2)), "local first-frame handler must be detached after timeout");
    }

    [Fact]
    public void MicrophoneCountdownPath_RetiresOperationAndLocalSubscription()
    {
        var backend = new FakeCountingAudioReadyBackend();
        var tray = new FakeTray();
        var engine = new RecordingEngine(_auditLogger);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, "fake-audio-ready");
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromMinutes(10);

        var rec = CreateRecording();
        rec.Microphone = true;
        rec.MicrophoneDeviceId = "fake-mic";
        rec.Config.Microphone = true;

        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => backend.StartVideoCallCount == 1, TimeSpan.FromSeconds(3)));
        backend.EmitFirstFrame();
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));

        Assert.True(SpinWait.SpinUntil(() => engine.ActiveCountdownOperationCountForTests == 0,
            TimeSpan.FromSeconds(2)), "microphone-path operation must be retired after first frame");
        Assert.True(SpinWait.SpinUntil(() => backend.FirstFrameSubscriberCount == 1,
            TimeSpan.FromSeconds(2)), "microphone-path local handler must be detached");

        engine.Stop(rec.Id, "cleanup_stop");
    }

    private sealed class FakeCountingAudioReadyBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend
    {
        private readonly object _eventGate = new();
        private Action<FirstFrameObservation>? _firstFrameHandlers;

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add { lock (_eventGate) _firstFrameHandlers += value; }
            remove { lock (_eventGate) _firstFrameHandlers -= value; }
        }

        public event Action? AudioReady;

        public int FirstFrameSubscriberCount
        {
            get { lock (_eventGate) return _firstFrameHandlers?.GetInvocationList().Length ?? 0; }
        }

        public bool IsAudioReady { get; private set; }
        public int StartVideoCallCount;
        public int ExitCode => 0;

        public void Start(CaptureConfig cfg)
        {
            // Synchronously ready: the engine catch-up path starts the countdown.
            IsAudioReady = true;
            AudioReady?.Invoke();
        }

        public void StartVideo()
        {
            Interlocked.Increment(ref StartVideoCallCount);
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }

        public void EmitFirstFrame()
        {
            Action<FirstFrameObservation>? handlers;
            lock (_eventGate) handlers = _firstFrameHandlers;
            handlers?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "frame",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 0
            });
        }
    }
}
