using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that helper stream-health/recovery metrics propagate from the
/// helper terminal summary through the backend into recording metadata, that a
/// clean terminal state with a severe wall/media gap fails as
/// audio_capture_discontinuous (never unknown/not_checked), and that failed
/// retention carries a small diagnostics.json.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RuntimeRecoveryMetadataTests : IDisposable
{
    private readonly string _tmpDir;

    public RuntimeRecoveryMetadataTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"runtime-recovery-{Guid.NewGuid():N}");
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
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private RecordingEngine CreateEngine(ITrayContext tray)
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit, NoOpPerformanceTracer.Instance);
        engine.SetTray(tray);
        engine.CountdownSteps = 0;
        engine.CountdownInterval = TimeSpan.Zero;
        engine.FirstFrameTimeout = TimeSpan.FromSeconds(5);
        return engine;
    }

    private static Recording CreateRecording(string outputPath, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "display",
            SourceTitle = "Display 1",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            Microphone = true,
            MicrophoneDeviceId = "fake-mic",
            Config = new CaptureConfig
            {
                SourceKind = "display",
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

    private string CreateValidVideo(double seconds = 2)
    {
        var path = Path.Combine(_tmpDir, $"fixture-video-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration={seconds}:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 -t {seconds} \"{path}\"");
        return path;
    }

    private string CreateValidVideoWithAudio(double seconds = 2)
    {
        var path = Path.Combine(_tmpDir, $"fixture-av-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration={seconds}:size=320x240:rate=10 -f lavfi -i sine=frequency=1000:duration={seconds} -pix_fmt yuv420p -c:v libx264 -c:a aac -shortest \"{path}\"");
        return path;
    }

    private string CreateValidAudio(double seconds)
    {
        var path = Path.Combine(_tmpDir, $"fixture-audio-{Guid.NewGuid():N}.wav");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i sine=frequency=1000:duration={seconds} -acodec pcm_s16le -ar 16000 -ac 1 \"{path}\"");
        return path;
    }

    /// <summary>
    /// Places a real 2-second video at the temp path the backend will finalize,
    /// so the preconditions pass and the test exercises the FinalizeOutput path
    /// (the same path the real AirPods failure took).
    /// </summary>
    private string StageTempVideo(string outputPath)
    {
        var recordingId = Path.GetFileNameWithoutExtension(outputPath);
        var tempDir = Path.Combine(DataDirResolver.Resolve(), "temp");
        Directory.CreateDirectory(tempDir);
        var tempVideo = Path.Combine(tempDir, recordingId + "_video.mp4");
        File.Copy(CreateValidVideo(2), tempVideo);
        return tempVideo;
    }

    private static void RunFfmpeg(string arguments)
    {
        var psi = new ProcessStartInfo { FileName = FfmpegLocator.FfmpegPath, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed");
        proc.BeginOutputReadLine();
        if (!proc.WaitForExit(30000)) { try { proc.Kill(true); } catch { } throw new InvalidOperationException("ffmpeg generation timed out"); }
        if (proc.ExitCode != 0) throw new InvalidOperationException("ffmpeg generation failed: " + proc.StandardError.ReadToEnd());
    }

    // -----------------------------------------------------------------
    // 1. Clean helper terminal + severe coverage gap -> discontinuous,
    //    never unknown/not_checked, diagnostics.json retained
    // -----------------------------------------------------------------

    [Fact]
    public void Stop_HelperCleanStopWithSevereGap_FailsDiscontinuous_NotUnknownNotChecked()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray);
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var validAudio = CreateValidAudio(5.4);

        // Helper reported STOPPED (clean user stop) but the media timeline only
        // covers 5.4s of a ~19s wall span: the real AirPods failure shape.
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio, stderrLog: "audio-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Stopped,
            EstimatedGapMs = 13493,
            DurationMs = 5400,
            BytesWritten = 172800,
            SampleRate = 16000,
            Channels = 1,
            BitsPerSample = 16
        });
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 0);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording(outputPath);
        StageTempVideo(outputPath);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.failed, TimeSpan.FromSeconds(10)),
            "Recording must reach the failed terminal state with the discontinuous root cause");

        // The root cause is the audio discontinuity, not a generic validation error.
        Assert.Equal("audio_capture_discontinuous", rec.Error);
        Assert.DoesNotContain("output_validation_failed", rec.Error);

        var meta = rec.LastMeta;
        Assert.NotNull(meta);
        Assert.Equal("audio_capture_discontinuous", meta!.AudioHelperErrorCode);
        Assert.Equal("lost", meta.AudioStatus);
        Assert.Equal("degraded", meta.AudioContinuityStatus);
        Assert.Equal(13493, meta.AudioEstimatedGapMs);
        Assert.Contains("audio_helper_failed: audio_capture_discontinuous", meta.Warnings ?? Array.Empty<string>());

        // The helper root cause is also visible in the recording warnings/stderr.
        Assert.Contains(rec.Warnings, w => w.Contains("audio_capture_discontinuous"));

        // Failed retention: raw audio is preserved and a small diagnostics.json
        // carries the structured root cause.
        var recordingId = Path.GetFileNameWithoutExtension(outputPath);
        var failedDir = Path.Combine(_tmpDir, "failed", recordingId);
        Assert.True(File.Exists(Path.Combine(failedDir, "audio.wav")), "Raw failed audio must be retained");
        var diagnosticsPath = Path.Combine(failedDir, "diagnostics.json");
        Assert.True(File.Exists(diagnosticsPath), "diagnostics.json must be written next to retained artifacts");
        using var doc = JsonDocument.Parse(File.ReadAllText(diagnosticsPath));
        var root = doc.RootElement;
        Assert.Equal("audio_capture_discontinuous", root.GetProperty("audio_helper_error_code").GetString());
        Assert.Equal("lost", root.GetProperty("audio_status").GetString());
        Assert.Equal("degraded", root.GetProperty("audio_continuity_status").GetString());
        Assert.Equal(13493, root.GetProperty("audio_estimated_gap_ms").GetInt64());
        Assert.True(root.GetProperty("video_launch_anchor_ticks").GetInt64() > 0);
    }

    // -----------------------------------------------------------------
    // 2. Recovered recording: mux succeeds, continuity degraded, metrics present
    // -----------------------------------------------------------------

    [Fact]
    public void Stop_RecoveredRecording_MuxSucceeds_ContinuityDegradedWithMetrics()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray);
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var validAudio = CreateValidAudio(3);
        var muxOutput = CreateValidVideoWithAudio(2);

        // Helper recovered once on the same endpoint, padded the measured gap,
        // and ended with a clean STOPPED + degraded continuity.
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio, stderrLog: "audio-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Stopped,
            ContinuityStatus = "degraded",
            RecoveryCount = 1,
            RecoveryAttempts = 2,
            GapFilledBytes = 96000,
            GapFilledMs = 3000,
            DiscontinuityCount = 4,
            QpcOutlierCount = 1,
            MaxEstimatedGapMs = 3050,
            EstimatedGapMs = 20,
            DurationMs = 2100,
            SampleRate = 16000,
            Channels = 1,
            BitsPerSample = 16
        });
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 0);
        var runner = new FakeExternalProcessRunner(outputFileToCopy: muxOutput);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir, runner);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording(outputPath);
        StageTempVideo(outputPath);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.completed, TimeSpan.FromSeconds(10)),
            "Recovered recording must complete with a muxed output and degraded continuity");

        var meta = rec.LastMeta;
        Assert.NotNull(meta);
        // Helper-declared degraded continuity overrides the (skipped) mux-time
        // silencedetect classification, and recovery metrics are propagated.
        Assert.Equal("degraded", meta!.AudioContinuityStatus);
        Assert.Equal("recorded", meta.AudioStatus);
        Assert.Null(meta.AudioHelperErrorCode);
        Assert.Equal(1, meta.AudioRecoveryCount);
        Assert.Equal(2, meta.AudioRecoveryAttempts);
        Assert.Equal(96000, meta.AudioGapFilledBytes);
        Assert.Equal(3000, meta.AudioGapFilledMs);
        Assert.Equal(4, meta.AudioDiscontinuityCount);
        Assert.Equal(1, meta.AudioQpcOutlierCount);
        Assert.Equal(3050, meta.AudioMaxEstimatedGapMs);
        Assert.Equal(16000, meta.AudioSampleRate);
    }

    // -----------------------------------------------------------------
    // 3. Unrecoverable helper failure: prompt video stop + artifact retention
    // -----------------------------------------------------------------

    [Fact]
    public void Stop_UnrecoverableDiscontinuous_VideoStopsPromptly_ArtifactsRetainedWithDiagnostics()
    {
        var tray = new NoOpTray();
        var engine = CreateEngine(tray);
        var outputPath = Path.Combine(_tmpDir, $"rec-{Guid.NewGuid():N}.mp4");
        var validAudio = CreateValidAudio(2);

        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio, naturalExitDelayMs: 50, stderrLog: "audio-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "audio_capture_discontinuous",
            Reason = "Audio capture stream became discontinuous (callback_starvation): runtime recovery budget exhausted",
            EstimatedGapMs = 8000,
            MaxEstimatedGapMs = 8000,
            RecoveryCount = 2,
            RecoveryAttempts = 4
        });
        // The video would run for a long time; the audio failure must stop it promptly.
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 0, naturalExitDelayMs: 30000);
        var backend = CreateAvSplitBackend(audio, video, _tmpDir);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");
        var rec = CreateRecording(outputPath, durationSeconds: 30);
        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.failed, TimeSpan.FromSeconds(10)),
            "Recording must finalize promptly to failed after the unrecoverable audio failure");
        Assert.Equal("audio_capture_discontinuous", rec.Error);
        Assert.True(video.StopCalled, "Video worker must be stopped when the audio helper fails");

        var meta = rec.LastMeta;
        Assert.NotNull(meta);
        Assert.Equal("audio_capture_discontinuous", meta!.AudioHelperErrorCode);
        Assert.Equal("lost", meta.AudioStatus);
        Assert.Equal("degraded", meta.AudioContinuityStatus);
        Assert.Equal(2, meta.AudioRecoveryCount);
        Assert.Equal(4, meta.AudioRecoveryAttempts);

        var recordingId = Path.GetFileNameWithoutExtension(outputPath);
        var failedDir = Path.Combine(_tmpDir, "failed", recordingId);
        Assert.True(File.Exists(Path.Combine(failedDir, "audio.wav")), "Failed audio must be retained");
        var diagnosticsPath = Path.Combine(failedDir, "diagnostics.json");
        Assert.True(File.Exists(diagnosticsPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(diagnosticsPath));
        Assert.Equal("audio_capture_discontinuous", doc.RootElement.GetProperty("audio_helper_error_code").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("audio_recovery_count").GetInt64());
    }
}
