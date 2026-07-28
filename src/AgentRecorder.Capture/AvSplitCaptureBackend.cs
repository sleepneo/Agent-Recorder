using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

/// <summary>
/// Audio/video split capture backend. Runs microphone capture and screen capture
/// in independent FFmpeg processes, then muxes them in a finalization step.
/// Avoids blocking gdigrab on dshow initialization, which is the root cause of
/// AirPods-style Bluetooth microphone A/V discontinuities.
/// </summary>
public sealed class AvSplitCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, IAudioReadyBackend, ICaptureEndedObservableBackend, IMicrophoneStatusConsumer
{
    private CaptureConfig? _cfg;
    private string _finalOutputPath = "";
    private IAudioCaptureWorker? _audioWorker;
    private IVideoCaptureWorker? _videoWorker;
    private readonly object _lock = new();
    private Action<int, OutputMeta>? _onNaturalExit;
    private OutputMeta? _completionMeta;
    private bool _hasExited;
    private bool _manualStopped;
    private bool _captureEndedRaised;
    private bool _concluded;
    private string? _tempVideoPath;
    private string? _tempAudioPath;
    private readonly IAvWorkerFactory _workerFactory;
    private readonly IExternalProcessRunner _runner;
    private readonly TempRetentionPolicy _retentionPolicy;
    private IMicrophoneStatusProvider? _microphoneStatusProvider;

    private int _audioPrematureExitCode;
    private string _audioPrematureStderr = "";
    private bool _audioPrematureExited;
    private int _firstFrameRaised;

    // Convergence primitive: the first caller that owns finalization completes
    // this TCS with the single final OutputMeta. All other concurrent callers
    // wait on the same task and receive the same result.
    private TaskCompletionSource<OutputMeta>? _convergenceTcs;

    private static readonly TimeSpan VideoStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VideoStabilizeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AudioExitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WavStabilizeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WavStabilizeInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Upper bound on the entire owner finalization sequence: video stop/drain,
    /// file stability, audio stop/drain, WAV stability, mux, continuity check and
    /// a small buffer. Waiters use this so they never give up while the owner is
    /// still legitimately converging.
    /// </summary>
    private static readonly TimeSpan TotalConvergenceTimeout =
        VideoStopTimeout +
        VideoStabilizeTimeout +
        AudioExitTimeout +
        WavStabilizeTimeout +
        AvFinalizer.DefaultMuxTimeout +
        AvFinalizer.SilenceDetectTimeout +
        TimeSpan.FromSeconds(15);

    /// <summary>
    /// Test seam: overrides <see cref="TotalConvergenceTimeout"/> so deterministic
    /// tests can exercise slow-owner/waiter races without waiting minutes.
    /// </summary>
    internal TimeSpan? ConvergenceTimeoutOverride { get; set; }

    private TimeSpan EffectiveConvergenceTimeout => ConvergenceTimeoutOverride ?? TotalConvergenceTimeout;

    private const int WavStabilizeMaxAttempts = 15;
    private const int VideoStabilizeMaxAttempts = 15;

    /// <summary>
    /// Test seam: when false, the backend skips the expensive audio continuity
    /// check during finalization. Defaults to true in production.
    /// </summary>
    internal bool ApplyContinuityCheck { get; set; } = true;

    public event Action<FirstFrameObservation>? FirstFrameObserved;
    public event Action? AudioReady;
    public event Action<CaptureEndedObservation>? CaptureEnded;

    public AvSplitCaptureBackend()
        : this(new AvWorkerFactory(), new ExternalProcessRunner(), new TempRetentionPolicy()) { }

    public AvSplitCaptureBackend(IAvWorkerFactory workerFactory, IExternalProcessRunner runner, TempRetentionPolicy retentionPolicy)
    {
        _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _retentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
    }

    public IMicrophoneStatusProvider MicrophoneStatusProvider
    {
        set => _microphoneStatusProvider = value;
    }

    public bool IsAudioReady => _audioWorker?.IsAudioReady ?? false;

    public void Start(CaptureConfig cfg)
    {
        _cfg = cfg;
        _finalOutputPath = cfg.OutputPath;

        var tempDir = Path.Combine(DataDirResolver.Resolve(), "temp");
        Directory.CreateDirectory(tempDir);

        var recordingId = Path.GetFileNameWithoutExtension(_finalOutputPath) ?? "rec_unknown";
        _tempVideoPath = Path.Combine(tempDir, recordingId + "_video.mp4");
        _tempAudioPath = Path.Combine(tempDir, recordingId + "_audio.wav");

        if (cfg.Microphone && !string.IsNullOrEmpty(cfg.MicDevice))
        {
            _audioWorker = _workerFactory.CreateAudioWorker();
            _audioWorker.SetMicrophoneStatusProvider(_microphoneStatusProvider);
            _audioWorker.AudioReady += OnAudioReady;
            _audioWorker.NaturalExit += OnAudioNaturalExit;
            _audioWorker.Start(cfg, _tempAudioPath);
        }
        // Video worker is started by the engine via StartVideo() after the
        // optional preparation/countdown phases. No-microphone recordings still
        // receive the countdown state.
    }

    /// <summary>
    /// Starts the video worker. Called by the engine after the countdown phase.
    /// </summary>
    public void StartVideo()
    {
        var cfg = _cfg ?? throw new InvalidOperationException("Start() must be called before StartVideo()");
        if (_videoWorker != null)
            return;

        var tempVideoPath = _tempVideoPath ?? throw new InvalidOperationException("Temp video path not initialized");
        StartVideoInternal(cfg, tempVideoPath);
    }

    private void StartVideoInternal(CaptureConfig cfg, string tempVideoPath)
    {
        _videoWorker = _workerFactory.CreateVideoWorker();
        _videoWorker.FirstFrameObserved += obs =>
        {
            if (Interlocked.Exchange(ref _firstFrameRaised, 1) != 0)
                return;
            try { FirstFrameObserved?.Invoke(obs); }
            catch { }
        };
        _videoWorker.NaturalExit += OnVideoNaturalExit;
        _videoWorker.Start(cfg, tempVideoPath);
    }

    private void OnAudioReady()
    {
        try { AudioReady?.Invoke(); }
        catch { }
    }

    private void OnAudioNaturalExit(int exitCode, string stderr)
    {
        // Audio worker exiting naturally is unusual; rely on video worker to drive
        // finalization. If video is not running, report failure. If video is still
        // running, remember the audio stderr so the final metadata can explain why
        // the audio track disappeared.
        lock (_lock)
        {
            if (_manualStopped || _hasExited) return;
            if (_videoWorker == null)
            {
                var summary = (_audioWorker as IAudioHelperSummaryProvider)?.GetTerminalSummary();
                var meta = new OutputMeta
                {
                    StderrLog = stderr,
                    Warnings = new[] { "audio_worker_exited_before_video_started" },
                    AudioHelperErrorCode = ResolveAudioHelperErrorCode(summary)
                };
                _completionMeta = meta;
                _hasExited = true;
                _concluded = true;
                try { _onNaturalExit?.Invoke(exitCode, meta); }
                catch { }
            }
            else
            {
                _audioPrematureExitCode = exitCode;
                _audioPrematureStderr = stderr;
                _audioPrematureExited = true;
            }
        }
    }

    private void OnVideoNaturalExit(int exitCode, string stderr)
    {
        EnterConcludeCapture(exitCode, stderr, invokeNaturalExit: true);
    }

    public OutputMeta Stop()
    {
        return EnterConcludeCapture(0, "", invokeNaturalExit: false);
    }

    /// <summary>
    /// Exactly-once convergence entry point for both natural video exit and
    /// manual Stop(). The first caller becomes the owner and executes the full
    /// stop/finalize sequence; all concurrent callers wait for and receive the
    /// same final <see cref="OutputMeta"/>. The waiter's timeout covers the
    /// owner's entire legitimate convergence window, so a timeout can only mean
    /// the owner itself failed to converge, never a premature placeholder result.
    /// </summary>
    private OutputMeta EnterConcludeCapture(int videoExitCode, string videoStderr, bool invokeNaturalExit)
    {
        TaskCompletionSource<OutputMeta>? tcs;
        bool isOwner;

        lock (_lock)
        {
            if (_manualStopped || _concluded)
            {
                // Already finalizing or finalized. If the result is already
                // available, return it immediately; otherwise wait on the
                // convergence task (but do so outside the lock).
                if (_completionMeta != null)
                    return _completionMeta;

                tcs = _convergenceTcs ??= new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
                isOwner = false;
            }
            else
            {
                _manualStopped = true;
                _concluded = true;
                tcs = _convergenceTcs = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
                isOwner = true;
            }
        }

        if (!isOwner)
        {
            // This must happen outside the lock to avoid blocking other callers
            // while finalization runs. The timeout is the full legitimate owner
            // convergence budget, so reaching it means the owner truly stalled.
            // When a waiter times out, it atomically arbitrates the canonical
            // result via the TCS; the owner later reads the same canonical
            // result and cannot override it.
            try
            {
                return tcs.Task.WaitAsync(EffectiveConvergenceTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                var timeoutResult = new OutputMeta
                {
                    StderrLog = "convergence_owner_timeout",
                    Warnings = new[] { "convergence_owner_timeout" }
                };
                tcs.TrySetResult(timeoutResult);
            }
            return tcs.Task.Result;
        }

        OutputMeta candidate;
        try
        {
            candidate = ConcludeCapture(videoExitCode, videoStderr, invokeNaturalExit);
        }
        catch (Exception ex)
        {
            candidate = new OutputMeta
            {
                StderrLog = "finalize_exception: " + ex,
                Warnings = new[] { "finalize_exception: " + ex.Message }
            };
        }

        // The TCS is the single source of truth. The owner publishes its
        // candidate; if a waiter already arbitrated a timeout result, that
        // canonical result wins and the owner returns it too.
        tcs.TrySetResult(candidate);
        var canonicalResult = tcs.Task.Result;

        // Make the canonical result visible before notifying the engine so that
        // any synchronous inspection sees the final metadata.
        lock (_lock)
        {
            _completionMeta = canonicalResult;
            _hasExited = true;
        }

        // Natural owner must notify the engine exactly once, even if an
        // exception was caught above. The callback is external code, so it is
        // isolated from the rest of the backend state and is invoked after the
        // TCS has been completed so waiters are never blocked by it.
        if (invokeNaturalExit)
        {
            try { _onNaturalExit?.Invoke(videoExitCode, canonicalResult); }
            catch { }
        }

        return canonicalResult;
    }

    public void Cancel()
    {
        TaskCompletionSource<OutputMeta>? tcs = null;
        OutputMeta? cancelMeta = null;

        lock (_lock)
        {
            // If cancellation is the first concluding event, become the owner
            // and complete the convergence TCS so no waiter is left dangling.
            if (!_concluded)
            {
                _manualStopped = true;
                _hasExited = true;
                _concluded = true;
                tcs = _convergenceTcs = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancelMeta = _completionMeta = new OutputMeta
                {
                    AudioStatus = "not_requested",
                    AudioContinuityStatus = "not_checked"
                };
            }
            else
            {
                // Another caller is already the owner; reuse its TCS and let
                // that owner complete it with the canonical result.
                tcs = _convergenceTcs;
            }
        }

        if (tcs != null && cancelMeta != null)
        {
            tcs.TrySetResult(cancelMeta);
        }

        // Stop audio worker if it was started. Do not start/stop video worker
        // when cancellation happens during warmup.
        try { _audioWorker?.Stop(); } catch { }
        try { _audioWorker?.WaitForExit(AudioExitTimeout); } catch { }

        // Delete temp files; do not run finalizer on cancellation.
        TryDeleteTempFile(_tempVideoPath);
        TryDeleteTempFile(_tempAudioPath);
    }

    public void OnNaturalExit(Action<int, OutputMeta> callback)
    {
        _onNaturalExit = callback;
    }

    public bool HasExited => _videoWorker?.HasExited ?? _hasExited;
    public OutputMeta? LastMeta => _completionMeta;
    public int ExitCode => _videoWorker?.ExitCode ?? 0;

    /// <summary>
    /// Unified finalization path used by both natural video exit and manual Stop().
    /// Must only be called by the convergence owner. Raises CaptureEnded exactly
    /// once, stops and drains the audio worker, verifies the WAV file is stable,
    /// combines stderr, calls the finalizer, and cleans up temp files.
    /// </summary>
    private OutputMeta ConcludeCapture(int videoExitCode, string videoStderr, bool invokeNaturalExit)
    {
        // Active stop path: terminate and drain the video worker first so the
        // temporary MP4 is fully closed before we stop audio or run the finalizer.
        var video = _videoWorker;
        string localVideoStderr = videoStderr;
        int localVideoExitCode = videoExitCode;
        bool videoExited = true;
        bool videoStable = true;

        if (invokeNaturalExit)
        {
            // Natural exit: the video worker has already exited. Still ensure the
            // temp file is closed and stable before proceeding.
            videoExited = video?.HasExited ?? true;
            if (videoExited && _tempVideoPath != null)
                videoStable = WaitForFileStable(_tempVideoPath, VideoStabilizeTimeout);
        }
        else if (video != null && !video.HasExited)
        {
            video.Stop();
            videoExited = video.WaitForExit(VideoStopTimeout);
            localVideoExitCode = video.ExitCode;
            localVideoStderr = CombineStderr(localVideoStderr, video.GetStderrLog());
            if (videoExited && _tempVideoPath != null)
                videoStable = WaitForFileStable(_tempVideoPath, VideoStabilizeTimeout);
        }
        else
        {
            localVideoExitCode = video?.ExitCode ?? localVideoExitCode;
            localVideoStderr = CombineStderr(localVideoStderr, video?.GetStderrLog() ?? "");
            if (_tempVideoPath != null)
                videoStable = WaitForFileStable(_tempVideoPath, VideoStabilizeTimeout);
        }

        RaiseCaptureEnded(localVideoExitCode, invokeNaturalExit ? "natural" : "manual");

        // Stop the audio worker. On the natural-exit path the video worker has
        // already stopped, but the audio worker may still be running with its
        // output file open.
        try { _audioWorker?.Stop(); } catch { }
        bool audioExited = _audioWorker?.WaitForExit(AudioExitTimeout) ?? true;

        // Wait for the WAV file to be closed and stable before muxing.
        var tempAudioPath = _tempAudioPath;
        bool wavStable = true;
        bool audioRequested = _cfg?.Microphone == true && !string.IsNullOrEmpty(tempAudioPath);
        if (audioRequested)
        {
            if (!File.Exists(tempAudioPath))
            {
                wavStable = false;
            }
            else
            {
                wavStable = WaitForWavStable(tempAudioPath, WavStabilizeTimeout);
            }
        }

        var audioStderr = _audioWorker?.GetStderrLog() ?? "";
        if (_audioPrematureExited)
        {
            audioStderr = CombineStderr(audioStderr, _audioPrematureStderr);
        }
        var combinedStderr = CombineStderr(audioStderr, localVideoStderr);

        // Video exit, video stability, audio exit, or WAV stability failures block
        // finalization and produce a clear failure result while preserving temp
        // files for diagnosis.
        bool preconditionsOk = true;
        var preconditionsStderr = combinedStderr;
        if (!videoExited)
        {
            preconditionsOk = false;
            preconditionsStderr = CombineStderr(preconditionsStderr, "video_worker_exit_timeout");
        }
        else if (!videoStable)
        {
            preconditionsOk = false;
            preconditionsStderr = CombineStderr(preconditionsStderr, "video_file_not_stable");
        }
        else if (audioRequested && !audioExited)
        {
            preconditionsOk = false;
            preconditionsStderr = CombineStderr(preconditionsStderr, "audio_worker_exit_timeout");
        }
        else if (audioRequested && !wavStable)
        {
            preconditionsOk = false;
            preconditionsStderr = CombineStderr(preconditionsStderr, "wav_file_not_stable");
        }

        OutputMeta meta;
        bool success;
        if (!preconditionsOk)
        {
            var videoMeta = FfmpegCaptureBackend.Probe(_tempVideoPath ?? "");
            videoMeta.StderrLog = preconditionsStderr;
            videoMeta.AudioStatus = audioRequested ? "lost" : "not_requested";
            string warningKey;
            if (!videoExited) warningKey = "video_worker_exit_timeout";
            else if (!videoStable) warningKey = "video_file_not_stable";
            else if (!audioExited) warningKey = "audio_worker_exit_timeout";
            else warningKey = "wav_file_not_stable";
            videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                .Append(warningKey)
                .ToArray();

            if (audioRequested && _audioWorker is IAudioHelperSummaryProvider summaryProvider)
            {
                var summary = summaryProvider.GetTerminalSummary();
                videoMeta.AudioHelperErrorCode = ResolveAudioHelperErrorCode(summary);
                videoMeta.AudioCaptureBackend = "wasapi-helper";
            }

            meta = videoMeta;
            success = false;
        }
        else
        {
            var result = FinalizeOutput(combinedStderr, localVideoStderr, audioStderr);
            meta = result.Meta;
            success = result.Success;
        }

        // Propagate CoreAudio-detected microphone loss time into final metadata.
        var audioLostAtMs = _audioWorker?.RuntimeAudioLostAtMs;
        if (audioLostAtMs > 0)
        {
            meta.AudioLostAtMs = audioLostAtMs;
            // CoreAudio provided explicit evidence that the microphone endpoint
            // became inactive/unavailable. Override status values that would
            // otherwise hide this fact (recorded, empty, or missing track).
            if (string.IsNullOrEmpty(meta.AudioStatus) ||
                meta.AudioStatus == "recorded" ||
                meta.AudioStatus == "missing_audio_track")
            {
                meta.AudioStatus = "lost";
            }
        }

        // Clean up temp files according to the finalization outcome.
        var tempVideoPath = _tempVideoPath;
        var recordingId = Path.GetFileNameWithoutExtension(_finalOutputPath) ?? "rec_unknown";
        if (success)
        {
            _retentionPolicy.OnSuccess(tempVideoPath, tempAudioPath);
        }
        else
        {
            var retention = _retentionPolicy.OnFailure(recordingId, tempVideoPath, tempAudioPath);
            if (retention.Errors.Count > 0)
            {
                meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                    .Concat(retention.Errors)
                    .ToArray();
            }
        }

        return meta;
    }

    private void RaiseCaptureEnded(int exitCode, string reason)
    {
        lock (_lock)
        {
            if (_captureEndedRaised) return;
            _captureEndedRaised = true;
        }

        try
        {
            CaptureEnded?.Invoke(new CaptureEndedObservation
            {
                EndedAtUtc = DateTime.UtcNow,
                ExitCode = exitCode,
                Reason = reason
            });
        }
        catch { }
    }

    private (OutputMeta Meta, bool Success) FinalizeOutput(string combinedStderr, string videoStderr, string audioStderr)
    {
        var cfg = _cfg;
        var tempVideoPath = _tempVideoPath ?? "";
        var tempAudioPath = _tempAudioPath;

        var video = FfmpegCaptureBackend.Probe(tempVideoPath);
        video.StderrLog = videoStderr;
        var audioExists = !string.IsNullOrEmpty(tempAudioPath) && File.Exists(tempAudioPath);

        if (cfg?.Microphone != true || !audioExists)
        {
            // No audio requested or available: just move/copy the video file.
            try
            {
                if (File.Exists(_finalOutputPath)) File.Delete(_finalOutputPath);
                File.Move(tempVideoPath, _finalOutputPath);
            }
            catch (Exception ex)
            {
                video.StderrLog = CombineStderr(combinedStderr, ex.Message);
                video.Warnings = (video.Warnings ?? Array.Empty<string>())
                    .Append("finalize_move_failed: " + ex.Message).ToArray();
                return (video, false);
            }
            video.StderrLog = combinedStderr;
            video.OutputPath = _finalOutputPath;
            video.AudioStatus = "not_requested";
            video.AudioContinuityStatus = "not_checked";
            return (video, File.Exists(_finalOutputPath) && video.DurationSeconds > 0);
        }

        // Compute audio pre-roll relative to video start using monotonic anchors.
        // A positive value means the audio worker's media zero is before the first
        // video frame, which is the normal case. If anchors are missing or the
        // audio appears to start at/after video, the finalizer will reject it.
        TimeSpan? audioPreRoll = null;
        var videoAnchor = _videoWorker?.FirstFrameAnchorTicks ?? 0;
        var audioAnchor = _audioWorker?.MediaStartAnchorTicks ?? 0;
        if (videoAnchor > 0 && audioAnchor > 0)
        {
            var deltaTicks = videoAnchor - audioAnchor;
            if (deltaTicks > 0)
            {
                audioPreRoll = MediaAnchorHelper.ToTimeSpan(deltaTicks);
            }
        }

        var result = new AvFinalizer(_runner).FinalizeAsync(
            tempVideoPath,
            tempAudioPath!,
            _finalOutputPath,
            audioPreRoll,
            microphoneRequested: true,
            applyContinuityCheck: ApplyContinuityCheck,
            audioStderr,
            videoAnchorAvailable: videoAnchor > 0,
            audioAnchorAvailable: audioAnchor > 0).GetAwaiter().GetResult();

        var meta = result.Meta;
        meta.AudioCaptureBackend = _audioWorker is WasapiAudioCaptureWorker ? "wasapi-helper" : "dshow";
        meta.AudioTimestampCompensationApplied = _audioWorker is not WasapiAudioCaptureWorker;
        meta.AudioHelperProtocol = _audioWorker is WasapiAudioCaptureWorker ? "audio-helper-v1" : null;

        if (_audioWorker is WasapiAudioCaptureWorker wasapiWorker)
        {
            var summary = wasapiWorker.GetTerminalSummary();
            if (summary != null)
            {
                meta.AudioSampleRate = summary.SampleRate;
                meta.AudioChannels = summary.Channels;
                meta.AudioBitsPerSample = summary.BitsPerSample;
                meta.AudioCaptureMethod = summary.CaptureMethod;
                meta.AudioEstimatedGapMs = summary.EstimatedGapMs;
                meta.AudioHelperErrorCode = ResolveAudioHelperErrorCode(summary);
            }
        }

        meta.StderrLog = CombineStderr(combinedStderr, result.Stderr);

        if (!string.IsNullOrEmpty(result.Error))
        {
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append(result.Error).ToArray();
        }

        bool success = !result.TimedOut && string.IsNullOrEmpty(result.Error) && File.Exists(_finalOutputPath) && meta.DurationSeconds > 0;
        return (meta, success);
    }

    /// <summary>
    /// Returns true when the file can be opened for exclusive read access and
    /// has non-zero length. Returns false on timeout or any non-IO error.
    /// </summary>
    private static bool WaitForWavStable(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int attempts = 0;
        while (DateTime.UtcNow < deadline && attempts < WavStabilizeMaxAttempts)
        {
            attempts++;
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (new FileInfo(path).Length > 0)
                    return true;

                // File exists but is still empty; wait a bit and retry.
                Thread.Sleep(WavStabilizeInterval);
            }
            catch (IOException)
            {
                Thread.Sleep(WavStabilizeInterval);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true when the temporary video file can be opened for exclusive
    /// read access and has non-zero length. Returns false on timeout or error.
    /// </summary>
    private static bool WaitForFileStable(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int attempts = 0;
        while (DateTime.UtcNow < deadline && attempts < VideoStabilizeMaxAttempts)
        {
            attempts++;
            try
            {
                if (!File.Exists(path))
                {
                    Thread.Sleep(WavStabilizeInterval);
                    continue;
                }

                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (new FileInfo(path).Length > 0)
                    return true;

                Thread.Sleep(WavStabilizeInterval);
            }
            catch (IOException)
            {
                Thread.Sleep(WavStabilizeInterval);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static string CombineStderr(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b ?? "";
        if (string.IsNullOrEmpty(b)) return a;
        return a + "\n" + b;
    }

    /// <summary>
    /// Maps an audio helper session summary to a stable error code only when the
    /// helper actually failed. Success or clean user stop must return null so
    /// unrelated video/mux failures are not mis-routed as audio helper failures.
    /// </summary>
    private static string? ResolveAudioHelperErrorCode(AudioHelperSessionSummary? summary)
    {
        if (summary == null)
            return null;

        return summary.State switch
        {
            AudioHelperSessionState.Success or AudioHelperSessionState.Stopped => null,
            _ => AudioHelperErrorCodeResolver.Normalize(summary.ErrorCode)
        };
    }

    private static void TryDeleteTempFile(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        _audioWorker?.Dispose();
        _videoWorker?.Dispose();
    }
}
