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
    private CaptureAbortReason? _abortReason;
    private string? _tempVideoPath;
    private string? _tempAudioPath;
    private readonly IAvWorkerFactory _workerFactory;
    private readonly IExternalProcessRunner _runner;
    private readonly TempRetentionPolicy _retentionPolicy;
    private IMicrophoneStatusProvider? _microphoneStatusProvider;

    private int _audioPrematureExitCode;
    private string _audioPrematureStderr = "";
    private bool _audioPrematureExited;
    private bool _audioFinalizationStarted;
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
        // Normalize and validate the audio source BEFORE creating the temp
        // directory or computing any output side effects. An illegal audio
        // configuration must fail without creating directories or workers.
        cfg.NormalizeAudioSource();
        var validationError = cfg.ValidateAudioSource();
        if (validationError != null)
            throw new ArgumentException($"Invalid audio source configuration: {validationError}", nameof(cfg));

        _cfg = cfg;
        _finalOutputPath = cfg.OutputPath;

        var tempDir = Path.Combine(DataDirResolver.Resolve(), "temp");
        Directory.CreateDirectory(tempDir);

        var recordingId = Path.GetFileNameWithoutExtension(_finalOutputPath) ?? "rec_unknown";
        _tempVideoPath = Path.Combine(tempDir, recordingId + "_video.mp4");
        _tempAudioPath = Path.Combine(tempDir, recordingId + "_audio.wav");

        var audioRequested = cfg.AudioRequested;

        if (audioRequested)
        {
            _audioWorker = _workerFactory.CreateAudioWorker(cfg.AudioSourceKind);
            _audioWorker.SetMicrophoneStatusProvider(cfg.IsMicrophone ? _microphoneStatusProvider : null);
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
    /// If the backend has already concluded (e.g., audio helper failed before
    /// video started), video capture is not started.
    /// </summary>
    public void StartVideo()
    {
        var cfg = _cfg ?? throw new InvalidOperationException("Start() must be called before StartVideo()");

        lock (_lock)
        {
            if (_videoWorker != null || _concluded)
                return;
        }

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
        // finalization. If video is not running, report failure immediately. If
        // video is still running and the helper has declared any non-success/stopped
        // state (Failed, MalformedSequence, exit mismatch, no terminal event, etc.),
        // drive finalization promptly so the video worker is stopped with a bounded
        // timeout instead of running to the full duration.
        bool driveFinalization;
        lock (_lock)
        {
            if (_manualStopped || _hasExited || _concluded || _audioFinalizationStarted)
                return;

            var summary = (_audioWorker as IAudioHelperSummaryProvider)?.GetTerminalSummary();
            if (_videoWorker == null)
            {
                var meta = new OutputMeta
                {
                    StderrLog = stderr,
                    Warnings = new[] { "audio_worker_exited_before_video_started" },
                    AudioStatus = InferAudioStatusFromSummary(summary),
                    AudioContinuityStatus = InferAudioContinuityStatusFromSummary(summary),
                    AudioHelperErrorCode = ResolveEffectiveHelperErrorCode(summary)
                };
                ApplyHelperSummaryMetrics(meta, summary);
                _completionMeta = meta;
                _hasExited = true;
                _concluded = true;
                NormalizeTerminalOutputMeta(meta, published: false);
                try { _onNaturalExit?.Invoke(exitCode, meta); }
                catch { }
                return;
            }

            _audioPrematureExitCode = exitCode;
            _audioPrematureStderr = stderr;
            _audioPrematureExited = true;

            // Treat any helper state other than explicit Success/Stopped as a
            // failure that must stop the video and converge to a single failed
            // terminal state.
            bool isHelperFailure = summary == null ||
                summary.State is not (AudioHelperSessionState.Success or AudioHelperSessionState.Stopped);
            if (!isHelperFailure && exitCode != 0)
            {
                // A clean protocol state with a non-zero exit code is also a
                // failure; FinalValidateStream will mark it as a mismatch.
                isHelperFailure = true;
            }

            if (!isHelperFailure)
                return;

            _audioFinalizationStarted = true;
            driveFinalization = true;
        }

        if (driveFinalization)
        {
            // Offload to the thread pool to avoid synchronous re-entrancy. Stop the
            // video worker with a bounded timeout, then drive finalization exactly
            // once as if the video had exited naturally.
            var task = Task.Run(() =>
            {
                try
                {
                    var video = _videoWorker;
                    int videoExitCode = 0;
                    string videoStderr = "";
                    if (video != null && !video.HasExited)
                    {
                        video.Stop();
                        video.WaitForExit(VideoStopTimeout);
                        videoExitCode = video.ExitCode;
                        videoStderr = video.GetStderrLog();
                    }

                    EnterConcludeCapture(videoExitCode, videoStderr, invokeNaturalExit: true);
                }
                catch (Exception ex)
                {
                    // Ensure the convergence TCS is not left dangling and the
                    // exception is observed rather than becoming unobserved.
                    try
                    {
                        EnterConcludeCapture(
                            _audioPrematureExitCode,
                            CombineStderr(_audioPrematureStderr, "audio_finalization_task_exception: " + ex),
                            invokeNaturalExit: true);
                    }
                    catch { }
                }
            });

            _ = task.ContinueWith(
                t => { var _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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
    /// Stops both split workers for an application-owned lifecycle failure.
    /// The convergence owner preserves the typed reason and skips mux/final
    /// publication, even when the temporary video is probeable.
    /// </summary>
    public OutputMeta Abort(CaptureAbortReason reason)
    {
        // Keep the backend's single natural/finalization callback contract for
        // lifecycle aborts. ConcludeCapture still actively stops the workers
        // because the typed abort is not a process-natural exit.
        lock (_lock)
        {
            // A natural worker may already own convergence while it is still
            // draining/stabilizing. Upgrade that in-flight convergence before
            // it can reach mux/publish; a completed result is already terminal
            // and is left untouched for the engine's exactly-once gate.
            if (_completionMeta == null)
                _abortReason = reason;
        }
        return EnterConcludeCapture(0, "", invokeNaturalExit: true, abortReason: reason);
    }

    /// <summary>
    /// Exactly-once convergence entry point for both natural video exit and
    /// manual Stop(). The first caller becomes the owner and executes the full
    /// stop/finalize sequence; all concurrent callers wait for and receive the
    /// same final <see cref="OutputMeta"/>. The waiter's timeout covers the
    /// owner's entire legitimate convergence window, so a timeout can only mean
    /// the owner itself failed to converge, never a premature placeholder result.
    /// </summary>
    private OutputMeta EnterConcludeCapture(
        int videoExitCode,
        string videoStderr,
        bool invokeNaturalExit,
        CaptureAbortReason? abortReason = null)
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
                _abortReason = abortReason;
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
                NormalizeTerminalOutputMeta(timeoutResult, published: false);
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
            NormalizeTerminalOutputMeta(candidate, published: false);
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
                NormalizeTerminalOutputMeta(cancelMeta, published: false);
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
    /// Bounded diagnostics exposed for the internal manual media-pipeline
    /// acceptance entry. These are read-only observations; they do not alter
    /// the worker lifecycle or provide a second recording implementation.
    /// </summary>
    public string? TempVideoPath => _tempVideoPath;
    public string? TempAudioPath => _tempAudioPath;
    public string? FailedArtifactsDirectory
    {
        get
        {
            var recordingId = Path.GetFileNameWithoutExtension(_finalOutputPath);
            return string.IsNullOrEmpty(recordingId)
                ? null
                : Path.Combine(DataDirResolver.Resolve(), "failed", recordingId);
        }
    }

    public string GetStderrLog()
    {
        var video = _videoWorker?.GetStderrLog() ?? "";
        var audio = _audioWorker?.GetStderrLog() ?? "";
        return string.IsNullOrEmpty(video) ? audio :
            string.IsNullOrEmpty(audio) ? video : video + "\n" + audio;
    }

    /// <summary>
    /// Unified finalization path used by both natural video exit and manual Stop().
    /// Must only be called by the convergence owner. Raises CaptureEnded exactly
    /// once, stops and drains the audio worker, verifies the WAV file is stable,
    /// combines stderr, calls the finalizer, and cleans up temp files.
    /// </summary>
    private OutputMeta ConcludeCapture(int videoExitCode, string videoStderr, bool invokeNaturalExit)
    {
        var abortCode = ReadAbortCode();

        // Active stop path: terminate and drain the video worker first so the
        // temporary MP4 is fully closed before we stop audio or run the finalizer.
        var video = _videoWorker;
        string localVideoStderr = videoStderr;
        int localVideoExitCode = videoExitCode;
        bool videoExited = true;
        bool videoStable = true;

        if (invokeNaturalExit && abortCode == null)
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

        // Abort may have arrived while a natural owner was waiting for the
        // temp video to stabilize. Re-read the typed reason at this boundary
        // so that the capture-ended observation and the finalization branch
        // agree and the natural path cannot proceed to mux/publish.
        abortCode = ReadAbortCode() ?? abortCode;
        RaiseCaptureEnded(localVideoExitCode, abortCode ?? (invokeNaturalExit ? "natural" : "manual"));

        // Stop the audio worker. On the natural-exit path the video worker has
        // already stopped, but the audio worker may still be running with its
        // output file open.
        try { _audioWorker?.Stop(); } catch { }
        bool audioExited = _audioWorker?.WaitForExit(AudioExitTimeout) ?? true;

        // Wait for the WAV file to be closed and stable before muxing.
        var tempAudioPath = _tempAudioPath;
        bool wavStable = true;
        bool audioRequested = _cfg?.AudioRequested == true && !string.IsNullOrEmpty(tempAudioPath);
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

        abortCode = ReadAbortCode() ?? abortCode;

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
            videoMeta.AudioSourceKind = _cfg?.AudioSourceKind switch
            {
                AudioCaptureSourceKind.SystemLoopback => "system-loopback",
                AudioCaptureSourceKind.Microphone => "microphone",
                _ => "none"
            };
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
                var helperErrorCode = ResolveEffectiveHelperErrorCode(summary);
                videoMeta.AudioHelperErrorCode = helperErrorCode;
                videoMeta.AudioCaptureBackend = "wasapi-helper";
                if (summary?.EstimatedGapMs.HasValue == true)
                    videoMeta.AudioEstimatedGapMs = summary.EstimatedGapMs;
                ApplyHelperSummaryMetrics(videoMeta, summary);
                if (summary != null)
                {
                    videoMeta.AudioStatus = InferAudioStatusFromSummary(summary);
                    videoMeta.AudioContinuityStatus = InferAudioContinuityStatusFromSummary(summary);
                }
                if (!string.IsNullOrEmpty(helperErrorCode))
                {
                    videoMeta.Warnings = (videoMeta.Warnings ?? Array.Empty<string>())
                        .Append($"audio_helper_failed: {helperErrorCode}")
                        .ToArray();
                }
            }

            meta = videoMeta;
            success = false;
        }
        else if (abortCode != null)
        {
            // A runtime lifecycle abort must never enter the mux/publish path.
            // Keep the probe as diagnostics only; TempRetentionPolicy will move
            // any partial workers' output into the controlled failed directory.
            var abortedMeta = FfmpegCaptureBackend.Probe(_tempVideoPath ?? "");
            abortedMeta.StderrLog = combinedStderr;
            abortedMeta.StopReason = abortCode;
            abortedMeta.AudioStatus = audioRequested ? "lost" : "not_requested";
            abortedMeta.AudioSourceKind = _cfg?.AudioSourceKind switch
            {
                AudioCaptureSourceKind.SystemLoopback => "system-loopback",
                AudioCaptureSourceKind.Microphone => "microphone",
                _ => "none"
            };
            abortedMeta.Warnings = (abortedMeta.Warnings ?? Array.Empty<string>())
                .Append(abortCode)
                .ToArray();
            meta = abortedMeta;
            success = false;
        }
        else
        {
            var result = FinalizeOutput(combinedStderr, localVideoStderr, audioStderr);
            meta = result.Meta;
            success = result.Success;
        }

        if (abortCode != null)
        {
            // Preserve the engine-owned lifecycle reason even when a worker
            // shutdown precondition failed before the abort-only branch ran.
            meta.StopReason = abortCode;
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append(abortCode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            success = false;
        }

        // Preserve both the launch anchor used for A/V alignment and the
        // progress-derived evidence on every success or failure path.
        ApplyVideoAnchorDiagnostics(meta);

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

            TryWriteFailureDiagnostics(retention.FailedDirectoryPath, recordingId, meta);
        }

        // Probe() is intentionally allowed to describe staging files while
        // collecting diagnostics. Once the split pipeline reaches its single
        // terminal result, expose only the approved final target and make the
        // file evidence correspond to this run's publication outcome. Failed
        // artifacts remain owned by TempRetentionPolicy and are not encoded in
        // the public OutputMeta path.
        NormalizeTerminalOutputMeta(meta, published: success);

        return meta;
    }

    private void NormalizeTerminalOutputMeta(OutputMeta meta, bool published)
    {
        meta.OutputPath = _finalOutputPath;
        if (!published)
        {
            meta.OutputFileExists = false;
            meta.SizeBytes = 0;
            return;
        }

        try
        {
            var finalFile = new FileInfo(_finalOutputPath);
            meta.OutputFileExists = finalFile.Exists && finalFile.Length > 0;
            meta.SizeBytes = finalFile.Exists ? finalFile.Length : 0;
        }
        catch
        {
            meta.OutputFileExists = false;
            meta.SizeBytes = 0;
        }
    }

    private string? ReadAbortCode()
    {
        lock (_lock)
        {
            return _abortReason.HasValue
                ? CaptureAbortReasonCodes.ToCode(_abortReason.Value)
                : null;
        }
    }

    /// <summary>
    /// Writes a small diagnostics.json next to the retained failed artifacts so
    /// the real audio root cause (helper error code, status, gap and recovery
    /// metrics) survives with the raw video/audio. Best effort, bounded size;
    /// never a heavy "validation bundle".
    /// </summary>
    private static void TryWriteFailureDiagnostics(string? failedDir, string recordingId, OutputMeta meta)
    {
        if (string.IsNullOrEmpty(failedDir))
            return;

        try
        {
            string? stderrExcerpt = null;
            if (!string.IsNullOrEmpty(meta.StderrLog))
            {
                int start = Math.Max(0, meta.StderrLog.Length - 2000);
                stderrExcerpt = meta.StderrLog.Substring(start);
            }

            var diagnostics = new Dictionary<string, object?>
            {
                ["recording_id"] = recordingId,
                ["created_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["audio_source_kind"] = meta.AudioSourceKind,
                ["audio_capture_backend"] = meta.AudioCaptureBackend,
                ["audio_helper_protocol"] = meta.AudioHelperProtocol,
                ["audio_helper_error_code"] = meta.AudioHelperErrorCode,
                ["audio_status"] = meta.AudioStatus,
                ["audio_continuity_status"] = meta.AudioContinuityStatus,
                ["audio_estimated_gap_ms"] = meta.AudioEstimatedGapMs,
                ["audio_max_estimated_gap_ms"] = meta.AudioMaxEstimatedGapMs,
                ["audio_recovery_count"] = meta.AudioRecoveryCount,
                ["audio_recovery_attempts"] = meta.AudioRecoveryAttempts,
                ["audio_gap_filled_bytes"] = meta.AudioGapFilledBytes,
                ["audio_gap_filled_ms"] = meta.AudioGapFilledMs,
                ["audio_discontinuity_count"] = meta.AudioDiscontinuityCount,
                ["audio_qpc_outlier_count"] = meta.AudioQpcOutlierCount,
                ["audio_capture_method"] = meta.AudioCaptureMethod,
                ["audio_capture_strategy"] = meta.AudioCaptureStrategy,
                ["audio_pair_evidence"] = meta.AudioPairEvidence,
                ["audio_auto_hfp_pair_status"] = meta.AudioAutoHfpPairStatus,
                ["audio_auto_hfp_pair_result_code"] = meta.AudioAutoHfpPairResultCode,
                ["audio_auto_hfp_pair_transport_classification"] = meta.AudioAutoHfpPairTransportClassification,
                ["audio_helper_failure_reason"] = meta.AudioHelperFailureReason,
                ["audio_helper_failure_stage"] = meta.AudioHelperFailureStage,
                ["audio_helper_failure_hresult"] = meta.AudioHelperFailureHresult,
                ["audio_render_prime_ready_ms"] = meta.AudioRenderPrimeReadyMs,
                ["video_launch_anchor_ticks"] = meta.VideoLaunchAnchorTicks,
                ["video_progress_anchor_ticks"] = meta.VideoProgressAnchorTicks,
                ["video_progress_anchor_delta_ms"] = meta.VideoProgressAnchorDeltaMs,
                ["video_first_progress_frame"] = meta.VideoFirstProgressFrame,
                ["video_first_progress_out_time_us"] = meta.VideoFirstProgressOutTimeUs,
                ["warnings"] = meta.Warnings ?? Array.Empty<string>(),
                ["stderr_excerpt"] = stderrExcerpt
            };

            var json = System.Text.Json.JsonSerializer.Serialize(diagnostics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(failedDir, "diagnostics.json"), json);
        }
        catch
        {
            // Diagnostics must never affect the failure path itself.
        }
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

        if (cfg?.AudioRequested != true || (!audioExists && cfg?.IsSystemLoopback != true))
        {
            // No audio requested or available: just move/copy the video file.
            // System loopback with missing audio must still go through the
            // finalizer so it can report a proper mux failure rather than
            // silently producing a video-only output.
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
            try
            {
                var finalFile = new FileInfo(_finalOutputPath);
                video.SizeBytes = finalFile.Exists ? finalFile.Length : 0;
                video.OutputFileExists = finalFile.Exists && finalFile.Length > 0;
            }
            catch
            {
                video.SizeBytes = 0;
                video.OutputFileExists = false;
            }
            return (video, video.OutputFileExists && video.DurationSeconds > 0);
        }

        // If the WASAPI helper already declared a terminal failure, do not run
        // the muxer or continuity check. Surface the helper's stable error code
        // and a clear audio status so the engine routes the root cause instead
        // of a generic mux/output validation failure.
        var helperSummary = (_audioWorker as IAudioHelperSummaryProvider)?.GetTerminalSummary();
        var helperErrorCode = ResolveEffectiveHelperErrorCode(helperSummary);
        bool severeGapOnCleanTerminal = ResolveAudioHelperErrorCode(helperSummary) == null && IsSeverelyDiscontinuous(helperSummary);

        if (!string.IsNullOrEmpty(helperErrorCode))
        {
            var failedMeta = FfmpegCaptureBackend.Probe(tempVideoPath);
            failedMeta.StderrLog = CombineStderr(combinedStderr, audioStderr);
            failedMeta.AudioStatus = InferAudioStatusFromSummary(helperSummary);
            failedMeta.AudioContinuityStatus = InferAudioContinuityStatusFromSummary(helperSummary);
            failedMeta.AudioHelperErrorCode = helperErrorCode;
            failedMeta.AudioCaptureBackend = _audioWorker is WasapiAudioCaptureWorker ? "wasapi-helper" : "dshow";
            failedMeta.AudioHelperProtocol = _audioWorker is WasapiAudioCaptureWorker ? "audio-helper-v1" : null;
            failedMeta.AudioCaptureStrategy = helperSummary?.CaptureStrategy;
            failedMeta.AudioPairEvidence = helperSummary?.PairEvidence;
            failedMeta.AudioRenderPrimeReadyMs = helperSummary?.RenderPrimeReadyMs;
            if (helperSummary?.EstimatedGapMs.HasValue == true)
                failedMeta.AudioEstimatedGapMs = helperSummary.EstimatedGapMs;
            ApplyHelperSummaryMetrics(failedMeta, helperSummary);
            failedMeta.Warnings = (failedMeta.Warnings ?? Array.Empty<string>())
                .Append($"audio_helper_failed: {helperErrorCode}")
                .ToArray();
            if (severeGapOnCleanTerminal)
            {
                failedMeta.Warnings = (failedMeta.Warnings ?? Array.Empty<string>())
                    .Append($"audio_capture_discontinuous: helper terminal state was clean but the media timeline lost " +
                            $"{helperSummary!.EstimatedGapMs ?? 0}ms (max observed {helperSummary.MaxEstimatedGapMs ?? 0}ms)")
                    .ToArray();
            }
            return (failedMeta, false);
        }

        // Compute audio pre-roll relative to video start using monotonic anchors.
        // A positive value means the audio worker's media zero is before the first
        // video frame, which is the normal case. If anchors are missing or the
        // audio appears to start at/after video, the finalizer will reject it.
        TimeSpan? audioPreRoll = null;
        var videoAnchor = _videoWorker?.LaunchAnchorTicks ?? 0;
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
            audioSourceKind: cfg?.AudioSourceKind ?? AudioCaptureSourceKind.None,
            applyContinuityCheck: ApplyContinuityCheck,
            audioStderr,
            videoAnchorAvailable: videoAnchor > 0,
            audioAnchorAvailable: audioAnchor > 0).GetAwaiter().GetResult();

        var meta = result.Meta;
        meta.AudioSourceKind = _cfg?.AudioSourceKind switch
        {
            AudioCaptureSourceKind.SystemLoopback => "system-loopback",
            AudioCaptureSourceKind.Microphone => "microphone",
            _ => "none"
        };
        meta.AudioCaptureBackend = _audioWorker is WasapiAudioCaptureWorker ? "wasapi-helper" : "dshow";
        meta.AudioTimestampCompensationApplied = _audioWorker is not WasapiAudioCaptureWorker;
        meta.AudioHelperProtocol = _audioWorker is WasapiAudioCaptureWorker ? "audio-helper-v1" : null;

        if (_cfg?.IsSystemLoopback == true)
        {
            // Only override AudioStatus on success; mux failure/timeout must
            // preserve the finalizer's own failure status.
            meta.AudioCaptureBackend = "wasapi-helper-loopback";
            if (!result.TimedOut && string.IsNullOrEmpty(result.Error))
            {
                meta.AudioStatus = "system_loopback_recorded";
                meta.AudioContinuityStatus ??= "continuous";
            }
        }

        if (_audioWorker is IAudioHelperSummaryProvider helperSummaryProvider)
        {
            var summary = helperSummaryProvider.GetTerminalSummary();
            if (summary != null)
            {
                meta.AudioSampleRate = summary.SampleRate;
                meta.AudioChannels = summary.Channels;
                meta.AudioBitsPerSample = summary.BitsPerSample;
                meta.AudioCaptureMethod = summary.CaptureMethod;
                meta.AudioCaptureStrategy = summary.CaptureStrategy;
                meta.AudioPairEvidence = summary.PairEvidence;
                meta.AudioRenderPrimeReadyMs = summary.RenderPrimeReadyMs;
                meta.AudioEstimatedGapMs = summary.EstimatedGapMs;
                meta.AudioHelperErrorCode = ResolveAudioHelperErrorCode(summary);
                ApplyHelperSummaryMetrics(meta, summary);

                // The helper's own continuity declaration is authoritative for
                // gaps it measured and gap-filled during runtime recovery: a
                // recovered recording keeps a complete timeline (mux succeeds)
                // but must stay marked degraded with its recovery metrics.
                if (string.Equals(summary.ContinuityStatus, "degraded", StringComparison.OrdinalIgnoreCase))
                    meta.AudioContinuityStatus = "degraded";
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

    private void ApplyVideoAnchorDiagnostics(OutputMeta meta)
    {
        var video = _videoWorker;
        var launchAnchor = video?.LaunchAnchorTicks ?? 0;
        var progressAnchor = video?.FirstFrameAnchorTicks ?? 0;
        meta.VideoAnchorStatus = launchAnchor > 0 ? "available" : "missing";
        meta.VideoLaunchAnchorTicks = launchAnchor > 0 ? launchAnchor : null;
        meta.VideoProgressAnchorTicks = progressAnchor > 0 ? progressAnchor : null;
        meta.VideoProgressAnchorDeltaMs = video?.ProgressAnchorDeltaMs;
        meta.VideoFirstProgressFrame = video?.FirstProgressFrame;
        meta.VideoFirstProgressOutTimeUs = video?.FirstProgressOutTimeUs;
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

    /// <summary>
    /// Gap threshold above which a nominally clean terminal state (STOPPED/OK)
    /// is treated as a severely discontinuous capture: the media timeline lost
    /// far more than the muxer's coverage tolerance (0.25s), so the output can
    /// never validate and the failure must surface the real audio root cause
    /// instead of a generic output validation error. 500ms is 2x the muxer
    /// tolerance and far above normal anchor jitter (tens of ms).
    /// </summary>
    internal const long DiscontinuityGapThresholdMs = 500;

    private static bool IsSeverelyDiscontinuous(AudioHelperSessionSummary? summary)
    {
        if (summary == null)
            return false;
        // Only the residual (unrepaired) gap counts: a recovered session has a
        // high MaxEstimatedGapMs by construction (that is why it recovered) but
        // its timeline was gap-filled and is complete.
        return (summary.EstimatedGapMs ?? 0) > DiscontinuityGapThresholdMs;
    }

    /// <summary>
    /// Effective helper root cause: the helper's own declared failure code, or
    /// <c>audio_capture_discontinuous</c> when a nominally clean terminal state
    /// shows a severe wall/media gap (the media timeline was materially
    /// shortened even though the helper did not declare an error). Used by every
    /// failure path so the discontinuous root cause can never be downgraded to a
    /// generic validation error.
    /// </summary>
    private static string? ResolveEffectiveHelperErrorCode(AudioHelperSessionSummary? summary)
    {
        var code = ResolveAudioHelperErrorCode(summary);
        if (code == null && IsSeverelyDiscontinuous(summary))
            return "audio_capture_discontinuous";
        return code;
    }

    /// <summary>
    /// Copies the helper's stream-health/recovery metrics into the metadata,
    /// regardless of whether the final mux succeeded or failed.
    /// </summary>
    private static void ApplyHelperSummaryMetrics(OutputMeta meta, AudioHelperSessionSummary? summary)
    {
        if (summary == null)
            return;

        meta.AudioCaptureStrategy = summary.CaptureStrategy;
        meta.AudioPairEvidence = summary.PairEvidence;
        meta.AudioAutoHfpPairStatus = summary.AutoHfpPairStatus;
        meta.AudioAutoHfpPairResultCode = summary.AutoHfpPairResultCode;
        meta.AudioAutoHfpPairTransportClassification = summary.AutoHfpPairTransportClassification;
        meta.AudioHelperFailureReason = summary.Reason;
        meta.AudioHelperFailureStage = summary.FailureStage;
        meta.AudioHelperFailureHresult = summary.Hresult;
        if (!string.IsNullOrEmpty(summary.FailureStage))
            meta.Stage = summary.FailureStage;
        if (!string.IsNullOrEmpty(summary.Hresult))
            meta.Hresult = summary.Hresult;
        meta.AudioRenderPrimeReadyMs = summary.RenderPrimeReadyMs;
        if (summary.EstimatedGapMs.HasValue)
            meta.AudioEstimatedGapMs = summary.EstimatedGapMs;
        if (summary.MaxEstimatedGapMs.HasValue)
            meta.AudioMaxEstimatedGapMs = summary.MaxEstimatedGapMs;
        if (summary.RecoveryCount.HasValue)
            meta.AudioRecoveryCount = summary.RecoveryCount;
        if (summary.RecoveryAttempts.HasValue)
            meta.AudioRecoveryAttempts = summary.RecoveryAttempts;
        if (summary.GapFilledBytes.HasValue)
            meta.AudioGapFilledBytes = summary.GapFilledBytes;
        if (summary.GapFilledMs.HasValue)
            meta.AudioGapFilledMs = summary.GapFilledMs;
        if (summary.DiscontinuityCount.HasValue)
            meta.AudioDiscontinuityCount = summary.DiscontinuityCount;
        if (summary.QpcOutlierCount.HasValue)
            meta.AudioQpcOutlierCount = summary.QpcOutlierCount;
    }

    /// <summary>
    /// Infers the high-level <see cref="OutputMeta.AudioStatus"/> from a helper
    /// summary. The result is always a known value; <c>unknown</c> is never
    /// returned for a recognized terminal state. A clean terminal state with a
    /// severe wall/media gap produced audio that is materially incomplete, so
    /// it is <c>lost</c>, not <c>recorded</c>.
    /// </summary>
    private static string InferAudioStatusFromSummary(AudioHelperSessionSummary? summary)
    {
        if (summary == null)
            return "lost";

        if (IsSeverelyDiscontinuous(summary))
            return "lost";

        if (summary.State is AudioHelperSessionState.Success or AudioHelperSessionState.Stopped)
            return "recorded";

        var code = summary.ErrorCode ?? "";
        if (IsStartFailureCode(code))
            return "start_failed";

        return "lost";
    }

    /// <summary>
    /// Infers <see cref="OutputMeta.AudioContinuityStatus"/> from a helper
    /// summary. The helper's own declared continuity takes precedence when
    /// present. Initialization-time failures have not produced any timeline, so
    /// they are <c>not_checked</c>. Failures after capture started (stalled,
    /// discontinuous, lost, protocol errors, etc.) are <c>degraded</c>.
    /// </summary>
    private static string InferAudioContinuityStatusFromSummary(AudioHelperSessionSummary? summary)
    {
        if (summary == null)
            return "degraded";

        if (string.Equals(summary.ContinuityStatus, "degraded", StringComparison.OrdinalIgnoreCase))
            return "degraded";

        if (IsSeverelyDiscontinuous(summary))
            return "degraded";

        if (summary.State is AudioHelperSessionState.Success or AudioHelperSessionState.Stopped)
            return "continuous";

        var code = summary.ErrorCode ?? "";
        if (IsStartFailureCode(code))
            return "not_checked";

        return "degraded";
    }

    private static bool IsStartFailureCode(string code)
    {
        return code.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("format", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("start_failed", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("first_packet_timeout", StringComparison.OrdinalIgnoreCase) ||
               code.Contains("no_packets_captured", StringComparison.OrdinalIgnoreCase);
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
