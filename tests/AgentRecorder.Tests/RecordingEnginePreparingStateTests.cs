using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies the REC/preparing/first-frame state semantics introduced to hide
/// microphone/encoder warmup from the user and only show REC once recording
/// has credible first-frame evidence.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingEnginePreparingStateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuditLogger _auditLogger;
    private readonly string? _originalDataDir;

    public RecordingEnginePreparingStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"preparing-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _auditLogger = new AuditLogger(Path.Combine(_tempDir, "audit.jsonl"));

        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _tempDir);
    }

    public void Dispose()
    {
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public int SetRecordingCallCount;
        public int SetIdleCallCount;
        public int SetPreparingCallCount;
        public int SetFinalizingCallCount;
        public int? LastCountdownValue;
        public string? LastError;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(RecordingUiPresentation rec) { Interlocked.Increment(ref SetRecordingCallCount); }
        public void SetIdle(RecordingUiPresentation rec) { Interlocked.Increment(ref SetIdleCallCount); }
        public void SetAllIdle() { Interlocked.Increment(ref SetIdleCallCount); }
        public void ShowError(string text) { LastError = text; }
        public void SetPreparing(RecordingUiPresentation rec) { Interlocked.Increment(ref SetPreparingCallCount); }
        public void SetCountdown(RecordingUiPresentation rec) { LastCountdownValue = rec.CountdownRemainingSeconds; }
        public void SetFinalizing(RecordingUiPresentation rec) { Interlocked.Increment(ref SetFinalizingCallCount); }
    }

    private RecordingEngine CreateEngine(out FakeTray tray, Func<CaptureConfig, (ICaptureBackend, string)>? backendFactory = null)
    {
        tray = new FakeTray();
        var engine = new RecordingEngine(_auditLogger);
        engine.SetTray(tray);
        if (backendFactory != null)
            engine.BackendFactory = cfg => backendFactory!(cfg);
        return engine;
    }

    private static Recording CreateRecording(bool microphone = false, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "region",
            OutputPath = Path.Combine(Path.GetTempPath(), $"preparing-{Guid.NewGuid():N}.mp4"),
            Microphone = microphone,
            MicrophoneDeviceId = microphone ? "fake-mic" : null,
            DurationSeconds = durationSeconds,
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Microphone = microphone }
        };
    }

    [Fact]
    public void StartCapture_ObservableBackend_BeforeFirstFrame_StaysPreparingAndNoRecUi()
    {
        var engine = CreateEngine(out var tray, _ => (new FakeObservableBackend(), "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);

        Assert.Equal(RecState.preparing, rec.State);
        Assert.Equal(default, rec.StartedAtUtc);
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.Equal(0, tray.SetIdleCallCount);
    }

    [Fact]
    public void StartCapture_ObservableBackend_AfterFirstFrame_TransitionsToRecordingAndShowsRecOnce()
    {
        var backend = new FakeObservableBackend();
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.preparing, rec.State);

        backend.EmitFirstFrame();

        // Wait for the asynchronous first-frame observation to propagate.
        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        Assert.Equal(RecState.recording, rec.State);
        Assert.NotEqual(default, rec.StartedAtUtc);
        Assert.Equal(1, tray.SetRecordingCallCount);
        Assert.Equal(0, tray.SetIdleCallCount);
    }

    [Fact]
    public void StartCapture_ObservableBackend_FirstFrameEvidence_DoesNotSetBackendStartAsStartedAt()
    {
        var backend = new FakeObservableBackend(delayBeforeAutoFirstFrame: TimeSpan.FromMilliseconds(100));
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        var backendStartAt = rec.BackendStartAtUtc;
        Assert.True(backendStartAt != default);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        Assert.True(rec.StartedAtUtc > backendStartAt,
            "user-visible started_at must be after backend initialization start");
    }

    [Fact]
    public void StartCapture_NonObservableBackend_TransitionsToRecordingImmediatelyAfterStart()
    {
        var engine = CreateEngine(out var tray, _ => (new FakeNonObservableBackend(), "fake-non-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);

        Assert.Equal(RecState.recording, rec.State);
        Assert.NotEqual(default, rec.StartedAtUtc);
        Assert.Equal(1, tray.SetRecordingCallCount);
    }

    [Fact]
    public void StartCapture_ThrowingBackend_BeforeFirstFrame_FailsWithoutRecUi()
    {
        var engine = CreateEngine(out var tray, _ => (new ThrowingBackend("boom"), "fake-throwing"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(default, rec.StartedAtUtc);
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.Equal(1, tray.SetIdleCallCount);
    }

    [Fact]
    public void StartCapture_ObservableBackend_StopDuringPreparing_ConvergesToCancelled()
    {
        var backend = new FakeObservableBackend();
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.preparing, rec.State);

        engine.Stop(rec.Id, "test_stop_during_preparing");

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.Equal(0, tray.SetRecordingCallCount);
        Assert.True(tray.SetIdleCallCount >= 1);
    }

    [Fact]
    public void StartCapture_ObservableBackend_LateFirstFrameAfterTerminal_IsIgnored()
    {
        var backend = new FakeObservableBackend();
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        engine.Stop(rec.Id, "test_stop_before_first_frame");

        var terminalState = rec.State;
        var setRecordingBefore = tray.SetRecordingCallCount;

        backend.EmitFirstFrame();
        Thread.Sleep(100);

        Assert.Equal(terminalState, rec.State);
        Assert.Equal(setRecordingBefore, tray.SetRecordingCallCount);
    }

    [Fact]
    public void StartCapture_ObservableBackend_MultipleFirstFrameEvents_ShowRecOnlyOnce()
    {
        var backend = new FakeObservableBackend();
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        backend.EmitFirstFrame();
        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));
        backend.EmitFirstFrame();
        backend.EmitFirstFrame();
        Thread.Sleep(100);

        Assert.Equal(1, tray.SetRecordingCallCount);
    }

    [Fact]
    public void GetStatus_PreparingRecording_HasZeroElapsedSeconds()
    {
        var backend = new FakeObservableBackend();
        var engine = CreateEngine(out var tray, _ => (backend, "fake-observable"));
        var rec = CreateRecording();

        engine.StartCaptureForTests(rec, tray);
        Thread.Sleep(100);

        dynamic status = engine.GetStatus(rec.Id);
        Assert.Equal("preparing", (string)status.status);
        Assert.Equal(0, (int)status.elapsed_seconds);
    }

    [Fact]
    public void StartCapture_AudioReadySyncInStart_NotLost()
    {
        var backend = new FakeAudioReadyBackend(raiseAudioReadyOnStart: true);
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        var rec = CreateRecording(microphone: true);

        engine.StartCaptureForTests(rec, tray);

        Assert.Equal(RecState.countdown, rec.State);
        Assert.True(tray.SetPreparingCallCount >= 1);
    }

    [Fact]
    public void StartCapture_AudioReadyAsyncAfterStart_NotLost()
    {
        var backend = new FakeAudioReadyBackend(audioReadyDelay: TimeSpan.FromMilliseconds(50));
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        var rec = CreateRecording(microphone: true);

        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.preparing, rec.State);

        SpinWait.SpinUntil(() => rec.State == RecState.countdown, TimeSpan.FromSeconds(2));

        Assert.Equal(RecState.countdown, rec.State);
    }

    [Fact]
    public void StartVideo_ReturnsButFirstFrameNotYetArrived_DoesNotShowRec()
    {
        // Short 1-step countdown so StartVideo is invoked quickly, but do not
        // emit the first frame yet.
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.FromMilliseconds(500));
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true);
        engine.StartCaptureForTests(rec, tray);

        // Wait for countdown to complete and StartVideo to be called.
        SpinWait.SpinUntil(() => backend.StartVideoCalled, TimeSpan.FromSeconds(2));

        // Before the first frame arrives we must remain in countdown, and REC
        // must not be shown yet.
        Assert.Equal(RecState.countdown, rec.State);
        Assert.Equal(0, tray.SetRecordingCallCount);

        // Emit first frame and confirm we transition to recording.
        backend.EmitFirstFrame();
        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));
        Assert.Equal(RecState.recording, rec.State);
        Assert.Equal(1, tray.SetRecordingCallCount);
    }

    [Fact]
    public void FirstFrameArrived_ShowsRecExactlyOnce()
    {
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.Zero);
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true);
        engine.StartCaptureForTests(rec, tray);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        // Multiple first-frame events must not re-show REC.
        backend.EmitFirstFrame();
        backend.EmitFirstFrame();
        Thread.Sleep(100);

        Assert.Equal(1, tray.SetRecordingCallCount);
    }

    [Fact]
    public void FirstFrameTimeout_FailsAndCleans()
    {
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.FromDays(1));
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;
        engine.FirstFrameTimeout = TimeSpan.FromMilliseconds(100);

        var rec = CreateRecording(microphone: true);
        engine.StartCaptureForTests(rec, tray);

        // Wait for the state to leave countdown using the engine's own
        // Monitor-based synchronization instead of spinning on rec.State.
        dynamic status = engine.GetStatusWait(rec.Id, sinceStatus: "countdown", waitMs: 5000);

        Assert.Equal("failed", (string)status.Status);
        Assert.True(SpinWait.SpinUntil(() => backend.CancelCalled || backend.StopCalled, TimeSpan.FromSeconds(2)),
            "backend Cancel/Stop must be invoked on first-frame timeout");
        Assert.True(tray.SetIdleCallCount >= 1);
    }

    [Fact]
    public void CaptureDeadline_ReachesDuration_LeavesFinalizingAndCompletes()
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
            AudioStatus = "recorded",
            AudioContinuityStatus = "not_checked"
        };
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.Zero,
            naturalExitDelay: TimeSpan.FromDays(1))
        {
            StopResult = stopMeta
        };
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true, durationSeconds: 1);
        engine.StartCaptureForTests(rec, tray);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        // Wait until the recording has fully left the finalizing state.
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)),
            "deadline path must finalize the recording");

        Assert.NotEqual(RecState.finalizing, rec.State);
        Assert.Equal(RecState.completed, rec.State);
        Assert.True(rec.CaptureEndedAtUtc.HasValue);
        Assert.True(tray.SetFinalizingCallCount >= 1);
        Assert.True(tray.SetIdleCallCount >= 1);
        Assert.Equal(1, backend.StopCallCount);
        Assert.Same(stopMeta, rec.LastMeta);
        Assert.Equal("duration_reached", rec.StopReason);

        dynamic status = engine.GetStatus(rec.Id);
        Assert.Equal("completed", (string)status.status);
        Assert.Equal(1, (int)status.elapsed_seconds);
    }

    [Fact]
    public void CaptureDeadline_BackendReturnsFailure_GoesToFailed()
    {
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.Zero,
            naturalExitDelay: TimeSpan.FromDays(1))
        {
            StopResult = new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                DurationSeconds = 0,
                SizeBytes = 0,
                StderrLog = "deadline_backend_failure"
            }
        };
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true, durationSeconds: 1);
        engine.StartCaptureForTests(rec, tray);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(1, backend.StopCallCount);
        Assert.Equal("duration_reached", rec.StopReason);
        Assert.Contains("deadline_backend_failure", rec.LastMeta?.StderrLog ?? "");
    }

    [Fact]
    public void CaptureDeadline_RacesNaturalExit_FinalizesWithoutDeadlock()
    {
        var stopMeta = new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            OutputFileExists = true
        };
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.Zero,
            naturalExitDelay: TimeSpan.FromMilliseconds(950))
        {
            StopResult = stopMeta,
            NaturalExitMeta = stopMeta
        };
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true, durationSeconds: 1);
        engine.StartCaptureForTests(rec, tray);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        // Both the deadline watchdog and the natural-exit callback may claim
        // finalization ownership; the engine must not deadlock and must end in
        // a terminal state.
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)),
            "deadline/natural race must resolve to a terminal state");
        Assert.NotEqual(RecState.finalizing, rec.State);
        Assert.True(rec.IsFinalized);
        Assert.True(tray.SetIdleCallCount >= 1);
    }

    [Fact]
    public async Task StopDuringRecording_GoesToFinalizing()
    {
        var backend = new FakeAudioReadyBackend(
            raiseAudioReadyOnStart: true,
            firstFrameDelay: TimeSpan.Zero,
            stopDuration: TimeSpan.FromMilliseconds(300));
        var engine = CreateEngine(out var tray, _ => (backend, "fake-audio-ready"));
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.CountdownSteps = 1;

        var rec = CreateRecording(microphone: true);
        engine.StartCaptureForTests(rec, tray);

        SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(2));

        // Stop on a background thread so we can observe the finalizing state
        // before Stop() completes.
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "test_stop_during_recording"));

        bool sawFinalizing = SpinWait.SpinUntil(() =>
        {
            dynamic status = engine.GetStatus(rec.Id);
            return (string)status.status == "finalizing";
        }, TimeSpan.FromSeconds(2));

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sawFinalizing, "state should enter finalizing during Stop");
        Assert.True(tray.SetFinalizingCallCount >= 1);
        Assert.True(rec.IsFinalized);
    }

    private sealed class FakeObservableBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private readonly TimeSpan? _delayBeforeAutoFirstFrame;

        public FakeObservableBackend(TimeSpan? delayBeforeAutoFirstFrame = null)
        {
            _delayBeforeAutoFirstFrame = delayBeforeAutoFirstFrame;
        }

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public void Start(CaptureConfig cfg)
        {
            if (_delayBeforeAutoFirstFrame.HasValue)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(_delayBeforeAutoFirstFrame.Value);
                    EmitFirstFrame();
                });
            }
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }

        public void EmitFirstFrame()
        {
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "frame",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 0
            });
        }
    }

    private sealed class FakeAudioReadyBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend, ICaptureEndedObservableBackend
    {
        private readonly bool _raiseAudioReadyOnStart;
        private readonly TimeSpan? _audioReadyDelay;
        private readonly TimeSpan? _firstFrameDelay;
        private readonly TimeSpan? _naturalExitDelay;
        private readonly TimeSpan? _stopDuration;
        private Action<int, OutputMeta>? _onNaturalExit;

        public FakeAudioReadyBackend(
            bool raiseAudioReadyOnStart = false,
            TimeSpan? audioReadyDelay = null,
            TimeSpan? firstFrameDelay = null,
            TimeSpan? naturalExitDelay = null,
            TimeSpan? stopDuration = null)
        {
            _raiseAudioReadyOnStart = raiseAudioReadyOnStart;
            _audioReadyDelay = audioReadyDelay;
            _firstFrameDelay = firstFrameDelay;
            _naturalExitDelay = naturalExitDelay;
            _stopDuration = stopDuration;
        }

        public event Action? AudioReady;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public event Action<CaptureEndedObservation>? CaptureEnded;

        public bool IsAudioReady { get; private set; }
        public bool StartVideoCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public int StopCallCount { get; private set; }
        public bool CancelCalled { get; private set; }
        public int ExitCode => 0;
        public OutputMeta StopResult { get; set; } = new();
        public OutputMeta NaturalExitMeta { get; set; } = new();
        public int NaturalExitCallbackCount { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            if (_raiseAudioReadyOnStart)
            {
                IsAudioReady = true;
                AudioReady?.Invoke();
            }
            else if (_audioReadyDelay.HasValue)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(_audioReadyDelay.Value);
                    IsAudioReady = true;
                    AudioReady?.Invoke();
                });
            }

            if (_naturalExitDelay.HasValue)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(_naturalExitDelay.Value);
                    FireNaturalExit(0, NaturalExitMeta);
                });
            }
        }

        public void StartVideo()
        {
            StartVideoCalled = true;
            if (_firstFrameDelay.HasValue)
            {
                if (_firstFrameDelay.Value == TimeSpan.Zero)
                {
                    EmitFirstFrame();
                }
                else
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(_firstFrameDelay.Value);
                        EmitFirstFrame();
                    });
                }
            }
        }

        public OutputMeta Stop()
        {
            StopCalled = true;
            StopCallCount++;
            CaptureEnded?.Invoke(new CaptureEndedObservation
            {
                EndedAtUtc = DateTime.UtcNow,
                ExitCode = 0,
                Reason = "manual"
            });

            if (_stopDuration.HasValue)
                Thread.Sleep(_stopDuration.Value);

            return StopResult;
        }

        public void Cancel()
        {
            CancelCalled = true;
            StopCalled = true;
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback)
        {
            _onNaturalExit = callback;
        }

        public void FireNaturalExit(int exitCode, OutputMeta meta)
        {
            NaturalExitCallbackCount++;
            try { _onNaturalExit?.Invoke(exitCode, meta); }
            catch { }
            try
            {
                CaptureEnded?.Invoke(new CaptureEndedObservation
                {
                    EndedAtUtc = DateTime.UtcNow,
                    ExitCode = exitCode,
                    Reason = "natural"
                });
            }
            catch { }
        }

        public void Dispose() { }

        public void EmitFirstFrame()
        {
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "frame",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 0
            });
        }
    }

    private sealed class FakeNonObservableBackend : ICaptureBackend
    {
        public void Start(CaptureConfig cfg) { }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class ThrowingBackend : ICaptureBackend
    {
        private readonly string _message;
        public ThrowingBackend(string message) => _message = message;
        public void Start(CaptureConfig cfg) => throw new Exception(_message);
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => -1;
        public void Dispose() { }
    }
}
