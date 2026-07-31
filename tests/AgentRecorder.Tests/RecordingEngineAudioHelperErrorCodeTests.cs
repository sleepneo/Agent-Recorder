using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingEngineAudioHelperErrorCodeTests : IDisposable
{
    private readonly string _tmpDir;

    public RecordingEngineAudioHelperErrorCodeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"audiohelper-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        DataDirResolver.SetOverride(_tmpDir);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class CapturingTracer : IPerformanceTracer, IDisposable
    {
        public string? LastTerminalErrorCode { get; private set; }
        public ManualResetEventSlim TerminalRecorded { get; } = new(false);

        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null)
        {
            if (status == "failed")
            {
                LastTerminalErrorCode = errorCode;
                TerminalRecorded.Set();
            }
        }

        public void Dispose()
        {
            TerminalRecorded.Dispose();
        }

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
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) { }
        public void MicrophonePrepareStarted(string traceId, string recordingId) { }
        public void MicrophoneReady(string traceId, string recordingId) { }
        public void CountdownStarted(string traceId, string recordingId) { }
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) { }
        public void CaptureEnded(string traceId, string recordingId) { }
        public void FinalizationCompleted(string traceId, string recordingId, bool success) { }
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) { }
        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
    }

    private RecordingEngine CreateEngine(ITrayContext tray, out CapturingTracer tracer)
    {
        var audit = new CaptureAuditLogger();
        tracer = new CapturingTracer();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);
        engine.CountdownSteps = 0;
        engine.CountdownInterval = TimeSpan.Zero;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);
        return engine;
    }

    private static Recording CreateRecording(string sourceKind, string outputPath)
    {
        return new Recording
        {
            SourceType = sourceKind,
            SourceTitle = sourceKind == "display" ? "Display 1" : "Test Window",
            OutputPath = outputPath,
            DurationSeconds = 30,
            Microphone = true,
            MicrophoneDeviceId = "fake-mic",
            Config = new CaptureConfig
            {
                SourceKind = sourceKind,
                Bounds = (0, 0, 1920, 1080),
                Fps = 30,
                OutputPath = outputPath,
                Microphone = true,
                MicDevice = "fake-mic"
            }
        };
    }

    private static AvSplitCaptureBackend CreateAvSplitBackend(FakeAudioCaptureWorker audio, FakeVideoCaptureWorker video, string tmpDir, FakeExternalProcessRunner? runner = null)
    {
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        return new AvSplitCaptureBackend(factory, runner ?? new FakeExternalProcessRunner(), new TempRetentionPolicy(tmpDir)) { ApplyContinuityCheck = false };
    }

    private static FakeAudioCaptureWorker CreateFailingAudioWorker(string errorCode, bool raiseReady = false, int naturalExitDelayMs = 0)
    {
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: raiseReady,
            naturalExitDelayMs: naturalExitDelayMs,
            stderrLog: "audio-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = errorCode
        });
        return audio;
    }

    private static FakeAudioCaptureWorker CreateSuccessAudioWorker()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, stderrLog: "audio-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary { State = AudioHelperSessionState.Success });
        return audio;
    }

    private static FakeAudioCaptureWorker CreateStalledAudioWorker(long estimatedGapMs, int naturalExitDelayMs)
    {
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            naturalExitDelayMs: naturalExitDelayMs,
            stderrLog: "audio-stalled-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "audio_capture_stalled",
            Reason = "No audio samples received",
            EstimatedGapMs = estimatedGapMs
        });
        return audio;
    }

    private string CreateValidVideo()
    {
        var path = Path.Combine(_tmpDir, $"fixture-video-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration=2:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 -t 2 \"{path}\"");
        return path;
    }

    private string CreateValidAudio()
    {
        var path = Path.Combine(_tmpDir, $"fixture-audio-{Guid.NewGuid():N}.wav");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i sine=frequency=1000:duration=2 -acodec pcm_s16le -ar 44100 -ac 2 \"{path}\"");
        return path;
    }

    private static void RunFfmpeg(string arguments)
    {
        var psi = new ProcessStartInfo { FileName = FfmpegLocator.FfmpegPath, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed");
        proc.BeginOutputReadLine();
        if (!proc.WaitForExit(30000)) { try { proc.Kill(true); } catch { } throw new InvalidOperationException("ffmpeg generation timed out"); }
        if (proc.ExitCode != 0) throw new InvalidOperationException("ffmpeg generation failed: " + proc.StandardError.ReadToEnd());
    }

    [Theory]
    [InlineData("display", "ffmpeg-av-split")]
    [InlineData("region", "ffmpeg-region-av-split")]
    [InlineData("window", "ffmpeg-window-region-av-split")]
    public void Stop_RealAvSplitBackend_AudioEndpointInactive_FinalErrorIsStableCodeNotNonZeroExit(string sourceKind, string backendType)
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateFailingAudioWorker("audio_endpoint_inactive");
        var video = new FakeVideoCaptureWorker();
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, backendType);
        var rec = CreateRecording(sourceKind, outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.failed, TimeSpan.FromSeconds(5)), "Recording should reach failed state after helper exits before video starts.");
        Assert.Equal("audio_endpoint_inactive", rec.Error);
        Assert.DoesNotContain("non_zero_exit", rec.Error);
        Assert.True(tracer.TerminalRecorded.Wait(TimeSpan.FromSeconds(5)), "Tracer should record the terminal event.");
        Assert.Equal("audio_endpoint_inactive", tracer.LastTerminalErrorCode);
        Assert.Equal(backendType, rec.BackendType);
    }

    [Theory]
    [InlineData("display", "ffmpeg-av-split")]
    [InlineData("region", "ffmpeg-region-av-split")]
    [InlineData("window", "ffmpeg-window-region-av-split")]
    public void Stop_RealAvSplitBackend_FailedStateAtomicallyPublishesError(string sourceKind, string backendType)
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateFailingAudioWorker("audio_endpoint_inactive");
        var video = new FakeVideoCaptureWorker();
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, backendType);
        var rec = CreateRecording(sourceKind, outputPath);
        engine.StartCaptureForTests(rec, tray);

        string? observedErrorAtTransition = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (rec.State == RecState.failed)
            {
                observedErrorAtTransition = rec.Error;
                return true;
            }
            return false;
        }, TimeSpan.FromSeconds(5)), "Recording should reach failed state after helper exits before video starts.");

        // Recording atomic-final-state contract: at the first observation of failed, Error is already set.
        Assert.Equal("audio_endpoint_inactive", observedErrorAtTransition);
        Assert.Equal("audio_endpoint_inactive", rec.Error);

        // Tracer eventual-visibility contract: tracer records the terminal event on its own channel.
        Assert.True(tracer.TerminalRecorded.Wait(TimeSpan.FromSeconds(5)), "Tracer should record the terminal event.");
        Assert.Equal("audio_endpoint_inactive", tracer.LastTerminalErrorCode);
        Assert.Equal(backendType, rec.BackendType);
    }

    [Fact]
    public void Stop_AudioHelperFailsBeforeVideoStarts_FinalErrorIsStableCode()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateFailingAudioWorker("audio_endpoint_inactive");
        var video = new FakeVideoCaptureWorker();
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.failed, TimeSpan.FromSeconds(5)));
        Assert.Equal("audio_endpoint_inactive", rec.Error);
        Assert.DoesNotContain("non_zero_exit", rec.Error);
        Assert.True(tracer.TerminalRecorded.Wait(TimeSpan.FromSeconds(5)), "Tracer should record the terminal event.");
        Assert.Equal("audio_endpoint_inactive", tracer.LastTerminalErrorCode);
    }

    [Fact]
    public void Stop_AudioHelperFailsDuringRecording_FinalErrorIsStableCode()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateFailingAudioWorker("audio_endpoint_inactive", raiseReady: true, naturalExitDelayMs: 50);
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 200);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)), "Recording should finalize after video exits.");
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("audio_endpoint_inactive", rec.Error);
        Assert.DoesNotContain("non_zero_exit", rec.Error);
        Assert.True(tracer.TerminalRecorded.Wait(TimeSpan.FromSeconds(5)), "Tracer should record the terminal event.");
        Assert.Equal("audio_endpoint_inactive", tracer.LastTerminalErrorCode);
    }

    [Fact]
    public void Stop_AudioHelperSuccess_VideoFailure_FinalErrorIsNotAudioHelperFailure()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateSuccessAudioWorker();
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 0);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecState.failed, rec.State);
        Assert.NotEqual("audio_helper_failure", rec.Error);
        Assert.Null(rec.LastMeta?.AudioHelperErrorCode);
        Assert.NotEqual("non_zero_exit", rec.Error ?? "");
    }

    [Fact]
    public void Stop_AudioHelperSuccess_MuxFailure_FinalErrorIsNotAudioHelperFailure()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio);
        audio.SetTerminalSummary(new AudioHelperSessionSummary { State = AudioHelperSessionState.Success });
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 0);
        var runner = new FakeExternalProcessRunner(exitCode: 1, stderr: "mux-failed");
        var backend = CreateAvSplitBackend(audio, video, _tmpDir, runner);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecState.failed, rec.State);
        Assert.NotEqual("audio_helper_failure", rec.Error);
        Assert.Null(rec.LastMeta?.AudioHelperErrorCode);
        Assert.NotEqual("non_zero_exit", rec.Error ?? "");
    }

    [Fact]
    public void Stop_WavPreconditionFailure_FinalErrorIsNotNonZeroExitOrAudioHelperFailure()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateSuccessAudioWorker();
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 0);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecState.failed, rec.State);
        Assert.NotEqual("audio_helper_failure", rec.Error);
        Assert.DoesNotContain("non_zero_exit", rec.Error ?? "");
    }

    [Fact]
    public void Stop_AudioHelperStallsAfterVideoStarts_BoundedStopAndSingleFailedState()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateStalledAudioWorker(estimatedGapMs: 12455, naturalExitDelayMs: 50);
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 30000);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(10)),
            "Recording should finalize promptly after audio helper stalls, without waiting for full duration.");
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("audio_capture_stalled", rec.Error);
        Assert.True(video.StopCalled, "Video worker must be stopped when audio helper fails.");
        Assert.True(tracer.TerminalRecorded.Wait(TimeSpan.FromSeconds(5)), "Tracer should record the terminal event.");
        Assert.Equal("audio_capture_stalled", tracer.LastTerminalErrorCode);
    }

    [Fact]
    public void Stop_AudioHelperFailure_PropagatesCodeGapAndStatus_NotUnknown()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateStalledAudioWorker(estimatedGapMs: 12455, naturalExitDelayMs: 50);
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 200);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecState.failed, rec.State);

        var meta = rec.LastMeta;
        Assert.NotNull(meta);
        Assert.Equal("audio_capture_stalled", meta.AudioHelperErrorCode);
        Assert.Equal(12455, meta.AudioEstimatedGapMs);
        Assert.NotEqual("unknown", meta.AudioStatus);
        Assert.NotEqual("not_checked", meta.AudioContinuityStatus);
        Assert.Contains("audio_helper_failed: audio_capture_stalled", meta.Warnings ?? Array.Empty<string>());
    }

    [Fact]
    public void Stop_AllPaths_LeaveNoHelperOrFileResidues()
    {
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var audio = CreateFailingAudioWorker("audio_endpoint_inactive");
        var video = new FakeVideoCaptureWorker();
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        var tray = new NoOpTray();
        var engine = CreateEngine(tray, out var tracer);
        using var _ = tracer;
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording("display", outputPath);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));

        // No leftover stop signal, temp files should be gone or moved to failed/.
        var recordingId = Path.GetFileNameWithoutExtension(outputPath);
        var dataDir = DataDirResolver.Resolve();
        var tempDir = Path.Combine(dataDir, "temp");
        var stopSignal = Path.Combine(tempDir, $"{recordingId}_audio_stop.signal");
        Assert.False(File.Exists(stopSignal), "Audio helper stop signal should be cleaned up.");

        // The backend's temp video/audio should not remain in temp/.
        var residualVideo = Directory.GetFiles(tempDir, "*.mp4");
        var residualAudio = Directory.GetFiles(tempDir, "*.wav");
        Assert.DoesNotContain(residualVideo, f => f.Contains(recordingId));
        Assert.DoesNotContain(residualAudio, f => f.Contains(recordingId));
    }

    [Theory]
    [InlineData("audio_native_initialize_failed")]
    [InlineData("audio_native_start_failed")]
    [InlineData("audio_native_recording_failed")]
    [InlineData("audio_native_stop_failed")]
    [InlineData("audio_native_finalize_failed")]
    [InlineData("audio_native_device_mapping_not_found")]
    [InlineData("audio_native_device_mapping_ambiguous")]
    [InlineData("audio_native_device_mapping_disabled")]
    [InlineData("audio_native_device_enumeration_failed")]
    public void Normalize_NativeMediaCaptureErrorCodes_PreservesStableCode(string code)
    {
        Assert.True(AudioHelperErrorCodeResolver.IsAllowed(code));
        Assert.Equal(code, AudioHelperErrorCodeResolver.Normalize(code));
    }
}
