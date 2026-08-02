using System.Diagnostics;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Owns the WASAPI capture session: wires up the audio input, writes to the
/// temporary WAV file, emits the IPC event stream, and converges to exactly
/// one terminal event.
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultStallDetectionThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultFirstPacketTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan MaxStallCheckInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Default wall-minus-media gap that, sustained across consecutive stall
    /// checks, indicates the capture stream is starving even though callbacks
    /// may still trickle in (the real AirPods failure mode). 2s is far above
    /// normal scheduling jitter (observed normal operation gap is tens of ms)
    /// and detects a fully starved stream within a few seconds.
    /// </summary>
    private static readonly TimeSpan DefaultRuntimeGapThreshold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Number of consecutive stall checks that must observe the gap above
    /// <see cref="DefaultRuntimeGapThreshold"/> before recovery starts. Hysteresis
    /// so a single jittery reading never triggers a recovery.
    /// </summary>
    private const int GapConsecutiveChecks = 2;

    /// <summary>
    /// Maximum number of successful runtime recoveries per session. Bounded so a
    /// permanently starving endpoint converges to a stable failure instead of
    /// reopening forever. 1-2 is the recommended bound; we use 2.
    /// </summary>
    internal const int MaxRuntimeRecoveries = 2;

    /// <summary>
    /// Maximum open/start attempts per starvation event. A transient post-reconnect
    /// state usually resolves on the immediate retry; more attempts would just
    /// delay the stable failure.
    /// </summary>
    private const int MaxRecoveryOpenAttempts = 2;

    /// <summary>
    /// Monotonic deadline for a single recovery open attempt (endpoint open +
    /// format negotiation + formal Start). Independent of the startup budget.
    /// </summary>
    private static readonly TimeSpan DefaultRecoveryOpenBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on zero-padding inserted for a single measured gap, and on the
    /// total padding per session. Padding only ever covers objectively measured
    /// missing wall time (wall elapsed minus media bytes); the caps are a backstop
    /// against anchor/bookkeeping bugs so padding can never run unbounded.
    /// </summary>
    private static readonly TimeSpan DefaultMaxSingleGapPad = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTotalGapPad = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Total monotonic budget for the entire startup sequence, from the first
    /// endpoint open attempt through the final formal StartRecording attempt.
    /// A single deadline prevents the outer retry loop and the inner Open
    /// retry loop from multiplying attempts beyond the intended boundary.
    /// </summary>
    internal static readonly TimeSpan TotalStartupBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of formal StartRecording attempts. Each attempt may
    /// perform up to <see cref="WasapiAudioInput.MaxAttempts"/> endpoint open
    /// tries, all charged against <see cref="TotalStartupBudget"/>.
    /// </summary>
    internal const int MaxStartAttempts = 2;

    private readonly AudioHelperOptions _options;
    private readonly PathCheckResult _paths;
    private readonly EventWriter _events;
    private readonly StopWatcher _watcher;
    private readonly CancellationTokenSource _cts;
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly Func<TimeSpan, (IAudioInput? Input, string? ErrorCode, string? Reason)>? _inputFactory;
    private readonly IHfpDuplexInputFactory? _hfpFactory;
    private readonly IHfpPairResolver? _hfpPairResolver;
    private readonly TimeSpan _stallDetectionThreshold;
    private readonly TimeSpan _firstPacketTimeout;
    private readonly ISystemClock _clock;
    private readonly IStopwatch _startupStopwatch;
    private readonly TimeSpan _runtimeGapThreshold;
    private readonly TimeSpan _recoveryOpenBudget;
    private readonly TimeSpan _maxSingleGapPad;

    private readonly object _stateLock = new();
    private readonly object _writerLock = new();
    private readonly object _firstPacketLock = new();
    private readonly HashSet<IAudioInput> _finalizedInputs = new(ReferenceEqualityComparer.Instance);

    private IAudioInput? _input;
    private WaveFileWriter? _writer;
    private WaveFormat? _waveFormat;
    private Timer? _stallTimer;
    private Timer? _firstPacketTimer;

    private long _bytesWritten;
    private long _firstCallbackTimestamp;
    private long _lastCallbackTimestamp;
    private long _lastProgressTimestamp;
    private long _firstSampleAnchorTicks;
    private long _stopTimestamp;
    private long _startRecordingTimestamp;

    private long _lastProgressBytes;
    private long _lastProgressElapsedMs;
    private long _lastProgressWallElapsedMs;

    private long _stallCheckLastBytes = -1;

    private int _startedEventRaised;
    private int _terminalEventRaised;
    private int _userStopRequested;
    private long _exitCode = 1;

    // Runtime starvation/recovery state.
    private int _gapOverThresholdChecks;
    private int _runtimeRecoveryInProgress;
    private int _runtimeRecoveryThreadId;
    private int _successfulRecoveries;
    private long _recoveryAttemptCount;
    private long _gapFilledBytesTotal;
    private long _gapFilledMsTotal;
    private long _maxEstimatedGapMsObserved;
    private long _lastStreamResumeTimestamp;
    private long _discontinuityCountCarry;
    private int _inputFinalizationInProgress;
    private int _continuityDegraded;

    private string? _pendingErrorCode;
    private string _pendingReason = "";
    private string _pendingPartialPath = "";
    private string _pendingHresult = "";
    private string _pendingFailureStage = "";
    private string _captureStrategy = "";
    private string _pairEvidence = "";
    private long _renderPrimeReadyMs = -1;
    private int _autoHfpPairResolutionAttempted;
    private string? _resolvedHfpRenderEndpointId;
    private HfpPairDiscoveryResult _autoHfpPairResult =
        HfpPairDiscoveryResult.NotApplicable("Automatic HFP pair discovery was not requested");

    public CaptureSession(AudioHelperOptions options, PathCheckResult paths, EventWriter events, StopWatcher watcher, CancellationTokenSource cts)
        : this(options, paths, events, watcher, cts, null, DefaultStallDetectionThreshold, DefaultFirstPacketTimeout, null, null, null, null) { }

    internal CaptureSession(AudioHelperOptions options, PathCheckResult paths, EventWriter events, StopWatcher watcher, CancellationTokenSource cts,
        Func<TimeSpan, (IAudioInput? Input, string? ErrorCode, string? Reason)>? inputFactory,
        TimeSpan? stallDetectionThreshold = null,
        TimeSpan? firstPacketTimeout = null,
        ISystemClock? clock = null,
        TimeSpan? runtimeGapThreshold = null,
        TimeSpan? recoveryOpenBudget = null,
        TimeSpan? maxSingleGapPad = null,
        IHfpDuplexInputFactory? hfpFactory = null,
        IHfpPairResolver? hfpPairResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));
        _inputFactory = inputFactory;
        _hfpFactory = hfpFactory;
        _hfpPairResolver = hfpPairResolver;
        _stallDetectionThreshold = stallDetectionThreshold ?? DefaultStallDetectionThreshold;
        _firstPacketTimeout = firstPacketTimeout ?? DefaultFirstPacketTimeout;
        _clock = clock ?? SystemClock.Instance;
        _startupStopwatch = _clock.StartStopwatch();
        _runtimeGapThreshold = runtimeGapThreshold ?? DefaultRuntimeGapThreshold;
        _recoveryOpenBudget = recoveryOpenBudget ?? DefaultRecoveryOpenBudget;
        _maxSingleGapPad = maxSingleGapPad ?? DefaultMaxSingleGapPad;
    }

    public int Run()
    {
        try
        {
            RunCore();
            _completed.Wait(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal stop path; ensure we still emit a terminal event.
            ConvergeTerminal(userRequested: true);
            _completed.Wait(StopWaitTimeout);
        }
        catch (Exception ex)
        {
            ConvergeTerminal(userRequested: false, "audio_helper_runtime_failure", ex.Message, "");
            _completed.Wait(StopWaitTimeout);
        }

        return (int)Interlocked.Read(ref _exitCode);
    }

    private void RunCore()
    {
        string? lastStartErrorCode = null;
        string? lastStartReason = null;
        string? lastStartHresult = null;
        string? lastStartFailureStage = null;

        EnsureAutomaticHfpPairResolved();
        if (_autoHfpPairResult.IsBlockingFailure)
        {
            CaptureHfpFailureMetadata(AudioInputOpenResult.Failure(
                _autoHfpPairResult.ResultCode ?? "audio_hfp_pair_discovery_failed",
                _autoHfpPairResult.Reason,
                HfpFailureStages.PairDiscovery,
                pairEvidence: _autoHfpPairResult.PairEvidence,
                captureStrategy: "hfp-auto-pair-discovery"));
            ConvergeTerminal(
                userRequested: false,
                _autoHfpPairResult.ResultCode ?? "audio_hfp_pair_discovery_failed",
                _autoHfpPairResult.Reason,
                _paths.PartialPath,
                failureStage: HfpFailureStages.PairDiscovery);
            return;
        }

        for (int startAttempt = 0; startAttempt < MaxStartAttempts; startAttempt++)
        {
            var remainingBudget = TotalStartupBudget - _startupStopwatch.Elapsed;
            if (remainingBudget <= TimeSpan.Zero)
            {
                // Deadline expired before this attempt could begin. Report the
                // most specific error we have, otherwise a generic budget error.
                if (lastStartErrorCode != null)
                {
                    ConvergeTerminal(userRequested: false, lastStartErrorCode, lastStartReason ?? "", _paths.PartialPath,
                        hresult: lastStartHresult, failureStage: lastStartFailureStage);
                }
                else
                {
                    ConvergeTerminal(userRequested: false, "audio_startup_budget_exceeded", "Audio startup retry budget exhausted", "");
                }
                return;
            }

            var openResult = OpenInput(remainingBudget);
            var input = openResult.Input;
            if (input == null)
            {
                // If we already have a StartRecording failure pending, keep it
                // unless the open attempt returned a more specific root cause
                // (e.g. endpoint disconnected/not found).
                if (lastStartErrorCode != null && !IsMoreSpecificOpenError(openResult.ErrorCode))
                {
                    ConvergeTerminal(userRequested: false, lastStartErrorCode, lastStartReason ?? "", _paths.PartialPath,
                        hresult: lastStartHresult, failureStage: lastStartFailureStage);
                }
                else
                {
                    CaptureHfpFailureMetadata(openResult);
                    ConvergeTerminal(userRequested: false, openResult.ErrorCode ?? "audio_endpoint_not_found",
                        openResult.Reason, "", hresult: HfpDuplexAudioInputFactory.FormatHresult(openResult.Hresult),
                        failureStage: IsHfpMode || _autoHfpPairResult.IsBlockingFailure
                            ? openResult.FailureStage
                            : null);
                }
                return;
            }

            _input = input;
            CaptureHfpMetadata(input);
            var format = input.Format ?? throw new InvalidOperationException("Audio input has no wave format");
            _waveFormat = format;

            Stream? partialStream = null;
            WaveFileWriter? writer = null;
            try
            {
                partialStream = _paths.OpenPartialStream?.Invoke()
                    ?? throw new InvalidOperationException("Partial output stream is not configured");
                writer = new WaveFileWriter(partialStream, format);
            }
            catch (Exception ex)
            {
                try { writer?.Dispose(); } catch { }
                try { partialStream?.Dispose(); } catch { }
                ConvergeTerminal(userRequested: false, "audio_output_conflict", "Failed to reserve partial output file: " + ex.Message, _paths.PartialPath);
                return;
            }

            _writer = writer;

            // Wire handlers before starting so no packets are dropped between
            // AudioClient.Start and the capture thread publishing data.
            input.DataAvailable += OnDataAvailable;
            input.RecordingStopped += OnRecordingStopped;

            try
            {
                var startResult = input.StartRecording();
                if (startResult == StartRecordingResult.Started)
                {
                    // Success: exit the retry loop.
                    break;
                }

                // Start was cancelled or the input was disposed by a concurrent
                // Stop/Dispose. Do not treat this as a retryable Start failure.
                var cancelledInput = TakeCurrentInputForFinalization();
                if (cancelledInput != null)
                    FinalizeOwnedInput(cancelledInput);

                _writer = null;
                try { writer.Dispose(); } catch { }
                try { partialStream?.Dispose(); } catch { }
                try { if (File.Exists(_paths.PartialPath)) File.Delete(_paths.PartialPath); } catch { }

                _waveFormat = null;

                if (startResult == StartRecordingResult.Disposed)
                {
                    // Dispose won the race; no RecordingStopped will be raised.
                    // Converge to a terminal event now.
                    ConvergeTerminal(userRequested: _userStopRequested != 0);
                    return;
                }

                // Cancelled (user stop / Dispose requested). RecordingStopped
                // may already be in flight; wait for terminal convergence.
                return;
            }
            catch (AudioCaptureStartException ex)
            {
                lastStartErrorCode = ex.ErrorCode ?? "audio_capture_start_failed";
                lastStartReason = $"StartRecording failed: {ex.Message}";
                lastStartHresult = $"0x{ex.Hresult:X8}";
                lastStartFailureStage = ex.Stage;

                var failedInput = TakeCurrentInputForFinalization();
                if (failedInput != null)
                    FinalizeOwnedInput(failedInput);

                _writer = null;
                try { writer.Dispose(); } catch { }
                try { partialStream?.Dispose(); } catch { }
                try { if (File.Exists(_paths.PartialPath)) File.Delete(_paths.PartialPath); } catch { }

                _waveFormat = null;

                if (startAttempt == MaxStartAttempts - 1)
                {
                    ConvergeTerminal(userRequested: false, lastStartErrorCode, lastStartReason, _paths.PartialPath,
                        hresult: lastStartHresult, failureStage: lastStartFailureStage);
                    return;
                }
                // Retry: the next iteration will call _inputFactory / WasapiAudioInput.Open again.
            }
            catch (Exception ex)
            {
                ConvergeTerminal(
                    userRequested: false,
                    "audio_capture_start_failed",
                    "StartRecording failed: " + ex.Message,
                    _paths.PartialPath,
                    failureStage: IsHfpMode ? HfpFailureStages.CaptureStart : null);
                return;
            }
        }

        Interlocked.Exchange(ref _startRecordingTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _lastStreamResumeTimestamp, _startRecordingTimestamp);

        // Only arm the first-packet timer if no packet has already arrived and
        // the capture thread has not already reached a terminal state before
        // StartRecording returned. The check is performed under the same lock
        // used by terminal convergence to avoid creating orphaned timers/watcher.
        lock (_stateLock)
        {
            if (_terminalEventRaised != 0)
                return;

            ArmFirstPacketTimer();
            StartStallMonitor();
            _watcher.Start();
        }

        // If the caller cancels, request a graceful stop.
        _cts.Token.Register(() => RequestStop());
    }

    /// <summary>
    /// Endpoint-level errors are considered more specific than a generic formal
    /// Start failure because they explain why the device could not be opened at
    /// all (disconnected, removed, disabled) rather than why the stream start
    /// call failed.
    /// </summary>
    private static bool IsMoreSpecificOpenError(string? errorCode)
    {
        return errorCode is "audio_endpoint_not_found"
                         or "audio_endpoint_inactive"
                         or "audio_endpoint_unavailable";
    }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _userStopRequested, 1) != 0)
            return;

        Volatile.Read(ref _input)?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        if (!ReferenceEquals(sender, Volatile.Read(ref _input)))
            return;

        if (Interlocked.CompareExchange(ref _terminalEventRaised, 0, 0) != 0)
            return;

        long now = Stopwatch.GetTimestamp();
        WaveFormat? format;
        long bytesWrittenAfter;
        bool firstPacket = false;
        long firstSampleAnchorTicks = 0;

        lock (_writerLock)
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _input)))
                return;

            if (Interlocked.CompareExchange(ref _terminalEventRaised, 0, 0) != 0)
                return;

            var writer = _writer;
            if (writer == null)
                return;

            format = writer.WaveFormat;

            try
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                SetPendingError("audio_write_failure", "Failed to write audio sample: " + ex.Message, _paths.PartialPath);
                _input?.StopRecording();
                return;
            }

            bytesWrittenAfter = Interlocked.Add(ref _bytesWritten, e.BytesRecorded);

            if (Interlocked.CompareExchange(ref _firstCallbackTimestamp, now, 0) == 0)
            {
                _lastCallbackTimestamp = now;
                _lastProgressTimestamp = now;
                double packetSeconds = e.BytesRecorded / (double)format.AverageBytesPerSecond;
                long packetTicks = (long)(packetSeconds * Stopwatch.Frequency);
                firstSampleAnchorTicks = now - packetTicks;
                _firstSampleAnchorTicks = firstSampleAnchorTicks;
                firstPacket = true;
            }
            else
            {
                _lastCallbackTimestamp = now;
            }
        }

        if (firstPacket)
        {
            DisarmFirstPacketTimer();
            EmitStarted(format, firstSampleAnchorTicks, bytesWrittenAfter);
        }

        long last = _lastProgressTimestamp;
        var elapsedSinceLast = Stopwatch.GetElapsedTime(last, now);
        if (elapsedSinceLast > ProgressInterval)
        {
            if (Interlocked.CompareExchange(ref _lastProgressTimestamp, now, last) == last)
            {
                TryEmitProgress(bytesWrittenAfter, _firstCallbackTimestamp);
            }
        }

        if (_userStopRequested != 0)
        {
            Volatile.Read(ref _input)?.StopRecording();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Ignore stale events from an input that runtime recovery has already
        // replaced; only the currently installed input may converge the session.
        if (!ReferenceEquals(sender, Volatile.Read(ref _input)))
            return;

        // A user stop may synchronously raise RecordingStopped while recovery is
        // still inside candidate StartRecording. The recovery owner must finish
        // that candidate lifecycle and publish the single terminal event after
        // its metrics are committed; handling it here would race that commit.
        if (Volatile.Read(ref _runtimeRecoveryInProgress) != 0 && _userStopRequested != 0)
            return;

        if (e.Exception != null)
        {
            string? hresult = null;
            string? failureStage = null;
            if (e.Exception is AudioCaptureRuntimeException runtimeEx)
            {
                hresult = $"0x{runtimeEx.Hresult:X8}";
                failureStage = runtimeEx.Stage;
            }

            if (Volatile.Read(ref _runtimeRecoveryInProgress) != 0 && _userStopRequested == 0)
            {
                SetPendingError(
                    "audio_capture_discontinuous",
                    FormatRecoveryStopReason(e.Exception),
                    _paths.PartialPath,
                    hresult,
                    failureStage);
            }
            else
            {
                SetPendingError(
                    e.Exception is AudioCaptureRuntimeException classified && !string.IsNullOrEmpty(classified.ErrorCode)
                        ? classified.ErrorCode
                        : "audio_capture_error",
                    e.Exception.Message,
                    _paths.PartialPath,
                    hresult,
                    failureStage);
            }
        }
        else if (Volatile.Read(ref _runtimeRecoveryInProgress) != 0 && _userStopRequested == 0)
        {
            SetPendingError(
                "audio_capture_discontinuous",
                "Runtime recovery candidate stopped before it could resume capture",
                _paths.PartialPath);
        }

        ConvergeTerminal(userRequested: _userStopRequested != 0, fromInputCallback: true);
    }

    private void StartStallMonitor()
    {
        var threshold = _stallDetectionThreshold;
        if (threshold <= TimeSpan.Zero)
            return;

        var interval = TimeSpan.FromMilliseconds(Math.Min(MaxStallCheckInterval.TotalMilliseconds, threshold.TotalMilliseconds / 2));
        _stallTimer = new Timer(_ => CheckStall(), null, interval, interval);
    }

    private void StopStallMonitor()
    {
        var timer = Interlocked.Exchange(ref _stallTimer, null);
        if (timer == null)
            return;

        try { timer.Dispose(); } catch { }
    }

    private void CheckStall()
    {
        string trigger;
        string triggerMetrics;
        lock (_stateLock)
        {
            if (_terminalEventRaised != 0 || _userStopRequested != 0 || _startedEventRaised == 0)
                return;
            if (_runtimeRecoveryInProgress != 0)
                return;

            var now = Stopwatch.GetTimestamp();

            // Activity anchor: the last real callback, or the last stream
            // (re)start. After a recovery the new input gets one full threshold
            // window to deliver before starvation can fire again.
            var lastCallback = Interlocked.Read(ref _lastCallbackTimestamp);
            var lastResume = Interlocked.Read(ref _lastStreamResumeTimestamp);
            var lastActivity = lastCallback > lastResume ? lastCallback : lastResume;
            if (lastActivity == 0)
                return;

            var bytes = Interlocked.Read(ref _bytesWritten);
            var lastBytes = Interlocked.Read(ref _stallCheckLastBytes);
            Interlocked.Exchange(ref _stallCheckLastBytes, bytes);

            var lastCallbackAge = Stopwatch.GetElapsedTime(lastActivity, now);

            long wallElapsedMs = 0, mediaElapsedMs = 0, gapMs = 0;
            var firstCallback = Interlocked.Read(ref _firstCallbackTimestamp);
            var format = _waveFormat;
            if (firstCallback > 0 && format != null && format.AverageBytesPerSecond > 0)
            {
                wallElapsedMs = (long)Stopwatch.GetElapsedTime(firstCallback, now).TotalMilliseconds;
                mediaElapsedMs = (long)(bytes / (double)format.AverageBytesPerSecond * 1000.0);
                gapMs = Math.Max(0, wallElapsedMs - mediaElapsedMs);
                TrackMaxGap(gapMs);
            }

            // Class 1: no new callbacks at all (bytes unchanged across checks
            // and the stream has been silent past the threshold).
            bool starved = lastCallbackAge > _stallDetectionThreshold && bytes == lastBytes;

            // Class 2: callbacks/bytes still grow occasionally, but the media
            // timeline keeps falling behind the wall clock. Sustained over
            // consecutive checks so ordinary scheduling jitter cannot trigger.
            if (gapMs > (long)_runtimeGapThreshold.TotalMilliseconds)
                _gapOverThresholdChecks++;
            else
                _gapOverThresholdChecks = 0;
            bool gapDiverged = _gapOverThresholdChecks >= GapConsecutiveChecks;

            if (!starved && !gapDiverged)
                return;

            trigger = starved ? "callback_starvation" : "media_wall_gap_divergence";
            triggerMetrics = FormatRuntimeMetrics(wallElapsedMs, mediaElapsedMs, gapMs, bytes, (long)lastCallbackAge.TotalMilliseconds);
            _runtimeRecoveryInProgress = 1;
            Volatile.Write(ref _continuityDegraded, 1);
        }

        AttemptRuntimeRecovery(trigger, triggerMetrics);
    }

    /// <summary>
    /// Bounded runtime recovery on the same approved endpoint: stop and release
    /// the starved input, reopen through the existing factory (same endpoint id,
    /// monotonic per-attempt deadline), rebind handlers, formally start, and pad
    /// the objectively measured gap so the WAV timeline is not silently shortened.
    /// Stop/Dispose always takes priority and can never be revived by a recovery.
    /// </summary>
    private void AttemptRuntimeRecovery(string trigger, string triggerMetrics)
    {
        Volatile.Write(ref _runtimeRecoveryThreadId, Environment.CurrentManagedThreadId);
        try
        {
            // 1. Detach the starved input. Once _input is cleared the recovery
            //    path owns the old input; the session only ever touches _input.
            IAudioInput? oldInput;
            lock (_stateLock)
            {
                if (_terminalEventRaised != 0 || _userStopRequested != 0)
                    return;

                oldInput = TakeCurrentInputForFinalizationLocked();
            }

            if (oldInput != null)
                FinalizeOwnedInput(oldInput);

            bool budgetExhausted = false;
            lock (_stateLock)
            {
                if (_terminalEventRaised != 0 || _userStopRequested != 0)
                    return;

                budgetExhausted = Volatile.Read(ref _successfulRecoveries) >= MaxRuntimeRecoveries;
            }

            if (budgetExhausted)
            {
                FailDiscontinuous(
                    trigger,
                    $"runtime recovery budget exhausted ({MaxRuntimeRecoveries} recoveries already used)",
                    triggerMetrics);
                return;
            }

            string? lastFailure = null;
            string? lastFailureHresult = null;
            string? lastFailureStage = null;
            for (int openAttempt = 0; openAttempt < MaxRecoveryOpenAttempts; openAttempt++)
            {
                lock (_stateLock)
                {
                    if (_terminalEventRaised != 0 || _userStopRequested != 0)
                        return;
                }

                Interlocked.Increment(ref _recoveryAttemptCount);

                AudioInputOpenResult openResult;
                try
                {
                    openResult = OpenInput(_recoveryOpenBudget);
                }
                catch (Exception ex)
                {
                    openResult = AudioInputOpenResult.Failure("audio_helper_runtime_failure", ex.Message,
                        IsHfpMode ? HfpFailureStages.PairValidation : "AudioOpen");
                }

                var candidate = openResult.Input;
                if (candidate == null)
                {
                    CaptureHfpFailureMetadata(openResult);
                    lastFailure = $"reopen attempt {openAttempt + 1} failed ({openResult.ErrorCode ?? "unknown"}: {openResult.Reason})";
                    lastFailureHresult = HfpDuplexAudioInputFactory.FormatHresult(openResult.Hresult);
                    lastFailureStage = openResult.FailureStage;
                    continue;
                }

                // The recovered stream must carry the same sample format before
                // it is allowed to start. The WAV writer was created with the
                // original format; starting a different format could deliver a
                // synchronous first packet into the old WAV shape.
                if (!SameFormat(candidate.Format, _waveFormat))
                {
                    lastFailure = $"reopen attempt {openAttempt + 1} returned a different wave format";
                    lastFailureHresult = null;
                    lastFailureStage = IsHfpMode ? HfpFailureStages.CaptureOpen : null;
                    DetachStopAndDispose(candidate);
                    continue;
                }

                bool abortBeforeStart;
                lock (_stateLock)
                {
                    abortBeforeStart = _terminalEventRaised != 0 || _userStopRequested != 0;
                }

                if (abortBeforeStart)
                {
                    DetachStopAndDispose(candidate);
                    ConvergeTerminal(userRequested: _userStopRequested != 0);
                    return;
                }

                // Fill the objectively measured hole before publishing or
                // starting the replacement. While _input is null, stale old
                // callbacks and not-yet-current candidate callbacks cannot write.
                PadMeasuredGap();

                candidate.DataAvailable += OnDataAvailable;
                candidate.RecordingStopped += OnRecordingStopped;

                bool publishedForStart;
                lock (_stateLock)
                {
                    publishedForStart = _terminalEventRaised == 0 && _userStopRequested == 0;
                    if (publishedForStart)
                    {
                        _input = candidate;
                        CaptureHfpMetadata(candidate);
                    }
                }

                if (!publishedForStart)
                {
                    DetachStopAndDispose(candidate);
                    ConvergeTerminal(userRequested: _userStopRequested != 0);
                    return;
                }

                StartRecordingResult startResult;
                string? startFailure = null;
                string? startFailureHresult = null;
                string? startFailureStage = null;
                try
                {
                    startResult = candidate.StartRecording();
                }
                catch (Exception ex)
                {
                    startResult = StartRecordingResult.Cancelled;
                    startFailure = FormatRecoveryStartFailure(ex, out startFailureHresult, out startFailureStage);
                }

                if (startResult != StartRecordingResult.Started)
                {
                    bool terminalAlreadyRaised;
                    bool userStopRequested;
                    IAudioInput? candidateToClean;
                    lock (_stateLock)
                    {
                        terminalAlreadyRaised = _terminalEventRaised != 0;
                        userStopRequested = _userStopRequested != 0;
                        candidateToClean = !terminalAlreadyRaised && ReferenceEquals(_input, candidate)
                            ? candidate
                            : null;
                        if (candidateToClean != null)
                        {
                            _input = null;
                            _inputFinalizationInProgress++;
                        }
                    }

                    if (terminalAlreadyRaised)
                        return;

                    lastFailure = $"reopen attempt {openAttempt + 1} start failed ({startFailure ?? startResult.ToString()})";
                    lastFailureHresult = startFailureHresult;
                    lastFailureStage = startFailureStage ?? (IsHfpMode ? HfpFailureStages.CaptureStart : lastFailureStage);

                    if (candidateToClean != null)
                        FinalizeOwnedInput(candidateToClean);

                    if (userStopRequested)
                    {
                        ConvergeTerminal(userRequested: true);
                        return;
                    }

                    if (startResult == StartRecordingResult.Disposed)
                    {
                    FailDiscontinuous(trigger, lastFailure, triggerMetrics, lastFailureHresult, lastFailureStage);
                        return;
                    }

                    continue;
                }

                bool terminalAfterStart;
                bool stopAfterStart;
                bool committed;
                IAudioInput? candidateToStop = null;
                lock (_stateLock)
                {
                    terminalAfterStart = _terminalEventRaised != 0;
                    stopAfterStart = !terminalAfterStart && _userStopRequested != 0 && ReferenceEquals(_input, candidate);
                    committed = false;

                    if (stopAfterStart)
                    {
                        candidateToStop = candidate;
                        _input = null;
                        _inputFinalizationInProgress++;
                    }
                    else if (!terminalAfterStart && ReferenceEquals(_input, candidate))
                    {
                        _gapOverThresholdChecks = 0;
                        Interlocked.Increment(ref _successfulRecoveries);
                        Interlocked.Exchange(ref _lastStreamResumeTimestamp, Stopwatch.GetTimestamp());
                        committed = true;
                    }
                }

                if (terminalAfterStart)
                    return;

                if (candidateToStop != null)
                {
                    FinalizeOwnedInput(candidateToStop);
                    ConvergeTerminal(userRequested: true);
                    return;
                }

                if (!committed)
                {
                    lastFailure = $"reopen attempt {openAttempt + 1} lost candidate ownership after successful start";
                    lastFailureHresult = null;
                    continue;
                }

                Interlocked.Exchange(ref _stallCheckLastBytes, Interlocked.Read(ref _bytesWritten));
                TryEmitProgress(Interlocked.Read(ref _bytesWritten), _firstCallbackTimestamp, force: true, allowDuringRecovery: true);
                return;
            }

            FailDiscontinuous(trigger, lastFailure ?? "reopen failed", triggerMetrics, lastFailureHresult, lastFailureStage);
        }
        catch (Exception ex)
        {
            FailDiscontinuous(trigger, "recovery exception: " + ex.Message, triggerMetrics);
        }
        finally
        {
            lock (_stateLock)
            {
                Volatile.Write(ref _runtimeRecoveryThreadId, 0);
                Volatile.Write(ref _runtimeRecoveryInProgress, 0);
                Monitor.PulseAll(_stateLock);
            }
        }
    }

    private void DetachStopAndDispose(IAudioInput input)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_input, input))
                _input = null;
            _inputFinalizationInProgress++;
        }

        FinalizeOwnedInput(input);
    }

    private IAudioInput? TakeCurrentInputForFinalization()
    {
        lock (_stateLock)
            return TakeCurrentInputForFinalizationLocked();
    }

    private IAudioInput? TakeCurrentInputForFinalizationLocked()
    {
        var input = _input;
        _input = null;
        if (input != null)
            _inputFinalizationInProgress++;
        return input;
    }

    private void WaitForInputFinalization()
    {
        lock (_stateLock)
        {
            while (_inputFinalizationInProgress != 0)
                Monitor.Wait(_stateLock);
        }
    }

    private void WaitForRuntimeRecovery(bool fromInputCallback)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (_stateLock)
        {
            while (_runtimeRecoveryInProgress != 0 &&
                   _runtimeRecoveryThreadId != currentThreadId &&
                   !(fromInputCallback && _userStopRequested != 0))
            {
                Monitor.Wait(_stateLock);
            }
        }
    }

    private void FinalizeOwnedInput(IAudioInput input)
    {
        try { input.DataAvailable -= OnDataAvailable; } catch { }
        try { input.RecordingStopped -= OnRecordingStopped; } catch { }
        try { input.StopRecording(); } catch { }
        try { input.Dispose(); } catch { }

        // AudioClientAudioInput joins its capture thread during Dispose, and the
        // fake inputs model the same lifecycle. Read only after both operations
        // so discontinuities raised while stopping or releasing are included.
        long finalCount = 0;
        try { finalCount = input.DiscontinuityCount; } catch { }

        lock (_stateLock)
        {
            if (_finalizedInputs.Add(input))
                _discontinuityCountCarry += finalCount;
            _inputFinalizationInProgress--;
            Monitor.PulseAll(_stateLock);
        }
    }

    private static string FormatRecoveryStopReason(Exception ex)
    {
        if (ex is AudioCaptureRuntimeException runtimeEx)
        {
            return $"Runtime recovery candidate stopped during {runtimeEx.Stage} (HRESULT=0x{runtimeEx.Hresult:X8}): {runtimeEx.Message}";
        }

        return $"Runtime recovery candidate stopped with {ex.GetType().Name}: {ex.Message}";
    }

    private static string FormatRecoveryStartFailure(Exception ex, out string? hresult, out string? failureStage)
    {
        switch (ex)
        {
            case AudioCaptureStartException startEx:
                hresult = $"0x{startEx.Hresult:X8}";
                failureStage = startEx.Stage;
                return $"AudioCaptureStartException: {startEx.Message}";
            case AudioCaptureRuntimeException runtimeEx:
                hresult = $"0x{runtimeEx.Hresult:X8}";
                failureStage = runtimeEx.Stage;
                return $"AudioCaptureRuntimeException during {runtimeEx.Stage} (HRESULT={hresult}): {runtimeEx.Message}";
            default:
                hresult = null;
                failureStage = null;
                return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Records the stable discontinuous-stream failure and converges to the
    /// single terminal FAIL.
    /// </summary>
    private void FailDiscontinuous(string trigger, string detail, string triggerMetrics, string? hresult = null, string? failureStage = null)
    {
        lock (_stateLock)
        {
            if (_terminalEventRaised != 0)
                return;

            SetPendingErrorLocked(
                "audio_capture_discontinuous",
                $"Audio capture stream became discontinuous ({trigger}): {detail}. {triggerMetrics}",
                _paths.PartialPath,
                hresult,
                failureStage);
        }

        ConvergeTerminal(userRequested: false);
    }

    /// <summary>
    /// Writes zero samples for the objectively measured media deficit (wall
    /// elapsed minus media bytes since the first-sample anchor) into the live
    /// WAV writer. Block-align aligned and strictly capped; runs under
    /// <see cref="_writerLock"/> so padded bytes and real packets never
    /// interleave mid-chunk.
    /// </summary>
    private void PadMeasuredGap()
    {
        var format = _waveFormat;
        if (format == null || format.AverageBytesPerSecond <= 0 || format.BlockAlign <= 0)
            return;

        var anchor = Interlocked.Read(ref _firstCallbackTimestamp);
        if (anchor == 0)
            return;

        var now = Stopwatch.GetTimestamp();

        lock (_writerLock)
        {
            if (_terminalEventRaised != 0 || _userStopRequested != 0)
                return;

            var writer = _writer;
            if (writer == null)
                return;

            long bytes = Interlocked.Read(ref _bytesWritten);
            double wallSeconds = Stopwatch.GetElapsedTime(anchor, now).TotalSeconds;
            double mediaSeconds = bytes / (double)format.AverageBytesPerSecond;
            double deficitSeconds = wallSeconds - mediaSeconds;
            if (deficitSeconds <= 0)
                return;

            long padBytes = (long)(deficitSeconds * format.AverageBytesPerSecond);
            padBytes -= padBytes % format.BlockAlign;

            // Strict caps: a single pad never exceeds MaxSingleGapPad and the
            // session total never exceeds MaxTotalGapPad.
            long singleCap = (long)(_maxSingleGapPad.TotalSeconds * format.AverageBytesPerSecond);
            long totalCap = (long)(MaxTotalGapPad.TotalSeconds * format.AverageBytesPerSecond);
            long remaining = totalCap - Interlocked.Read(ref _gapFilledBytesTotal);
            long cap = Math.Min(singleCap, remaining);
            if (padBytes > cap)
                padBytes = cap - (cap % format.BlockAlign);
            if (padBytes <= 0)
                return;

            var zeroChunk = new byte[(int)Math.Min(padBytes, 65536)];
            long remainingBytes = padBytes;
            while (remainingBytes > 0)
            {
                int chunk = (int)Math.Min(zeroChunk.Length, remainingBytes);
                writer.Write(zeroChunk, 0, chunk);
                remainingBytes -= chunk;
            }

            Interlocked.Add(ref _bytesWritten, padBytes);
            Interlocked.Add(ref _gapFilledBytesTotal, padBytes);
            Interlocked.Add(ref _gapFilledMsTotal, (long)(padBytes / (double)format.AverageBytesPerSecond * 1000.0));
        }
    }

    private void TrackMaxGap(long gapMs)
    {
        long current;
        while ((current = Interlocked.Read(ref _maxEstimatedGapMsObserved)) < gapMs)
        {
            if (Interlocked.CompareExchange(ref _maxEstimatedGapMsObserved, gapMs, current) == current)
                break;
        }
    }

    private long TotalDiscontinuityCount()
    {
        var input = Volatile.Read(ref _input);
        return Interlocked.Read(ref _discontinuityCountCarry) + (input?.DiscontinuityCount ?? 0);
    }

    private string FormatRuntimeMetrics(long wallElapsedMs, long mediaElapsedMs, long gapMs, long bytesWritten, long lastCallbackAgeMs)
    {
        return $"wall_elapsed_ms={wallElapsedMs};media_elapsed_ms={mediaElapsedMs};estimated_gap_ms={gapMs};" +
               $"bytes_written={bytesWritten};last_callback_age_ms={lastCallbackAgeMs};" +
               $"discontinuity_count={TotalDiscontinuityCount()};recovery_attempts={Interlocked.Read(ref _recoveryAttemptCount)};" +
               $"successful_recoveries={Volatile.Read(ref _successfulRecoveries)};gap_filled_ms={Interlocked.Read(ref _gapFilledMsTotal)}";
    }

    private static bool SameFormat(WaveFormat? a, WaveFormat? b)
    {
        if (a == null || b == null)
            return false;
        return a.SampleRate == b.SampleRate &&
               a.Channels == b.Channels &&
               a.BitsPerSample == b.BitsPerSample &&
               a.Encoding == b.Encoding;
    }

    private void ArmFirstPacketTimer()
    {
        var timeout = _firstPacketTimeout;
        if (timeout <= TimeSpan.Zero)
            return;

        lock (_firstPacketLock)
        {
            // Re-check under the lock so a synchronous first packet that
            // already disarmed the timer cannot be followed by a stale create.
            if (Interlocked.Read(ref _firstCallbackTimestamp) != 0)
                return;
            if (_firstPacketTimer != null)
                return;

            _firstPacketTimer = new Timer(_ => CheckFirstPacket(), null, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmFirstPacketTimer()
    {
        Timer? timer;
        lock (_firstPacketLock)
        {
            timer = _firstPacketTimer;
            _firstPacketTimer = null;
        }

        if (timer == null)
            return;

        try { timer.Dispose(); } catch { }
    }

    private void CheckFirstPacket()
    {
        bool shouldStopInput = false;
        lock (_stateLock)
        {
            if (_terminalEventRaised != 0 || _userStopRequested != 0)
                return;

            if (Interlocked.Read(ref _firstCallbackTimestamp) != 0)
                return;

            var start = Interlocked.Read(ref _startRecordingTimestamp);
            if (start == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(start, now);
            if (elapsed <= _firstPacketTimeout)
                return;

            SetPendingErrorLocked(
                "audio_first_packet_timeout",
                $"No audio packet received within {elapsed.TotalSeconds:F1}s after StartRecording",
                _paths.PartialPath);
            shouldStopInput = true;
        }

        if (shouldStopInput)
            _input?.StopRecording();
    }

    private void ConvergeTerminal(bool userRequested, string? initialErrorCode = null, string initialReason = "", string? initialPartialPath = null,
        string? hresult = null, string? failureStage = null, bool fromInputCallback = false)
    {
        // Track errors discovered during convergence in locals. SetPendingErrorLocked
        // refuses to write once _terminalEventRaised is set, so any error raised
        // by the owner itself must be captured here rather than in _pendingErrorCode.
        string? localErrorCode = initialErrorCode;
        string localReason = initialReason;
        string localPartialPath = initialPartialPath ?? "";
        string? localHresult = hresult;
        string? localFailureStage = failureStage;

        // Claim terminal ownership first. Watchdog callbacks also acquire this
        // lock and check _terminalEventRaised before writing a root cause, so
        // once claimed no watchdog can overwrite the reason.
        lock (_stateLock)
        {
            if (_terminalEventRaised != 0)
                return;
            _terminalEventRaised = 1;

            // If no initial error was supplied but a watchdog/data callback already
            // recorded one, propagate it. The caller's explicit error takes priority.
            if (localErrorCode == null && _pendingErrorCode != null)
            {
                localErrorCode = _pendingErrorCode;
                localReason = _pendingReason;
                localPartialPath = _pendingPartialPath;
                localHresult = _pendingHresult;
                localFailureStage = _pendingFailureStage;
            }
        }

        if (userRequested)
            Interlocked.Exchange(ref _userStopRequested, 1);

        StopStallMonitor();
        DisarmFirstPacketTimer();

        // Recovery owns the current generation and may still be inside a bounded
        // reopen. Wait for that owner to finish candidate cleanup before terminal
        // convergence takes its snapshot. Recovery-thread terminal calls skip
        // this wait so they cannot deadlock themselves.
        WaitForRuntimeRecovery(fromInputCallback);

        // Recovery may own a detached input while Stop/Dispose drains its final
        // callbacks. Wait for that owner to publish its count as well.
        WaitForInputFinalization();

        // Owner-only path: detach the current input from session ownership, stop
        // and release it, then merge its final count exactly once before the
        // terminal event is built.
        var terminalInput = TakeCurrentInputForFinalization();
        if (terminalInput != null)
            FinalizeOwnedInput(terminalInput);

        long stopTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _stopTimestamp, stopTimestamp);

        WaveFileWriter? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        long bytesWritten = Interlocked.Read(ref _bytesWritten);

        if (writer != null)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                // Do not overwrite a more specific root cause with a finalize error.
                if (localErrorCode == null)
                {
                    localErrorCode = "audio_writer_finalize_failed";
                    localReason = "Failed to finalize WAV writer: " + ex.Message;
                    localPartialPath = _paths.PartialPath;
                }
            }
        }

        if (localErrorCode == null && bytesWritten == 0)
        {
            localErrorCode = "audio_no_packets_captured";
            localReason = "No audio packets were captured";
            localPartialPath = _paths.PartialPath;
        }

        if (localErrorCode != null)
        {
            EmitFailEvent(localErrorCode, localReason, localPartialPath, localHresult, localFailureStage);
            CleanupPartial();
            _completed.Set();
            return;
        }

        try
        {
            if (File.Exists(_paths.CanonicalPath))
            {
                EmitFailEvent("audio_output_conflict", "Output file appeared after capture", _paths.PartialPath);
                CleanupPartial();
                _completed.Set();
                return;
            }

            File.Move(_paths.PartialPath, _paths.CanonicalPath);
            Interlocked.Exchange(ref _exitCode, 0);

            var info = BuildTerminalEventInfo(bytesWritten, stopTimestamp);
            if (userRequested)
                _events.Stopped(info);
            else
                _events.Ok(info);
        }
        catch (Exception ex)
        {
            EmitFailEvent("audio_publish_failed", "Failed to publish output file: " + ex.Message, _paths.PartialPath);
        }
        finally
        {
            _completed.Set();
        }
    }

    private void SetPendingError(string errorCode, string reason, string partialPath, string? hresult = null, string? failureStage = null)
    {
        lock (_stateLock)
        {
            SetPendingErrorLocked(errorCode, reason, partialPath, hresult, failureStage);
        }
    }

    private void SetPendingErrorLocked(string errorCode, string reason, string partialPath, string? hresult = null, string? failureStage = null)
    {
        if (_terminalEventRaised != 0)
            return;

        if (_pendingErrorCode == null)
        {
            _pendingErrorCode = errorCode;
            _pendingReason = reason;
            _pendingPartialPath = partialPath;
            _pendingHresult = hresult ?? "";
            _pendingFailureStage = failureStage ?? "";
        }
    }

    private void EmitStarted(WaveFormat format, long anchorTicks, long bytesWritten)
    {
        if (Interlocked.Exchange(ref _startedEventRaised, 1) != 0)
            return;

        Interlocked.Exchange(ref _stallCheckLastBytes, bytesWritten);

        var info = new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            FirstSampleAnchorTicks = anchorTicks,
            TimestampFrequency = Stopwatch.Frequency,
            BytesWritten = bytesWritten,
            CaptureMethod = "WASAPI_SHARED_CAPTURE",
            CaptureEngine = "wasapi-direct"
        };
        ApplyHfpMetadata(info);
        _events.Started(info);
    }

    private void TryEmitProgress(long bytesWritten, long firstCallbackTimestamp, bool force = false, bool allowDuringRecovery = false)
    {
        if (!allowDuringRecovery && Volatile.Read(ref _runtimeRecoveryInProgress) != 0)
            return;

        var info = BuildEventInfo(bytesWritten, firstCallbackTimestamp, Stopwatch.GetTimestamp());

        if (info.BytesWritten < _lastProgressBytes ||
            info.ElapsedMs < _lastProgressElapsedMs ||
            info.WallElapsedMs < _lastProgressWallElapsedMs)
        {
            // Regression values would be a protocol error; ignore the progress tick.
            return;
        }

        _lastProgressBytes = info.BytesWritten;
        _lastProgressElapsedMs = info.ElapsedMs;
        _lastProgressWallElapsedMs = info.WallElapsedMs;

        _events.Progress(info);
    }

    private AudioHelperEventInfo BuildTerminalEventInfo(long bytesWritten, long stopTimestamp)
    {
        var info = BuildEventInfo(bytesWritten, _firstCallbackTimestamp, stopTimestamp);
        info.DurationMs = info.ElapsedMs;
        return info;
    }

    private AudioHelperEventInfo BuildEventInfo(long bytesWritten, long firstCallbackTimestamp, long stopTimestamp)
    {
        var format = _waveFormat;
        long elapsedMs = 0;
        long wallElapsedMs = 0;
        long estimatedGapMs = 0;
        long lastCallbackAgeMs = 0;

        if (format != null && format.AverageBytesPerSecond > 0)
        {
            elapsedMs = (long)(bytesWritten / (double)format.AverageBytesPerSecond * 1000.0);
        }

        if (firstCallbackTimestamp > 0)
        {
            wallElapsedMs = (long)Stopwatch.GetElapsedTime(firstCallbackTimestamp, stopTimestamp).TotalMilliseconds;
            estimatedGapMs = Math.Max(0, wallElapsedMs - elapsedMs);
        }

        var lastCallback = Interlocked.Read(ref _lastCallbackTimestamp);
        if (lastCallback > 0 && stopTimestamp >= lastCallback)
        {
            lastCallbackAgeMs = (long)Stopwatch.GetElapsedTime(lastCallback, stopTimestamp).TotalMilliseconds;
        }

        TrackMaxGap(estimatedGapMs);

        var info = new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = format?.SampleRate ?? 0,
            Channels = format?.Channels ?? 0,
            BitsPerSample = format?.BitsPerSample ?? 0,
            BytesWritten = bytesWritten,
            ElapsedMs = elapsedMs,
            WallElapsedMs = wallElapsedMs,
            EstimatedGapMs = estimatedGapMs,
            DurationMs = elapsedMs,
            FirstSampleAnchorTicks = _firstSampleAnchorTicks,
            TimestampFrequency = Stopwatch.Frequency,
            PartialOutputPath = _paths.PartialPath,
            LastCallbackAgeMs = lastCallbackAgeMs,
            DiscontinuityCount = TotalDiscontinuityCount(),
            RecoveryCount = Volatile.Read(ref _successfulRecoveries),
            RecoveryAttempts = Interlocked.Read(ref _recoveryAttemptCount),
            GapFilledBytes = Interlocked.Read(ref _gapFilledBytesTotal),
            GapFilledMs = Interlocked.Read(ref _gapFilledMsTotal),
            MaxEstimatedGapMs = Interlocked.Read(ref _maxEstimatedGapMsObserved),
            ContinuityStatus = Volatile.Read(ref _continuityDegraded) != 0 ? "degraded" : "continuous"
        };
        ApplyHfpMetadata(info);
        return info;
    }

    private void EmitFailEvent(string errorCode, string reason, string partialPath, string? hresult = null, string? failureStage = null)
    {
        Interlocked.Exchange(ref _exitCode, 1);

        var continuity = Volatile.Read(ref _continuityDegraded) != 0 ||
                         errorCode == "audio_capture_discontinuous"
            ? "degraded"
            : Volatile.Read(ref _startedEventRaised) != 0
                ? "continuous"
                : "not_checked";

        var info = new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            ErrorCode = errorCode,
            Reason = reason,
            Hresult = hresult ?? "",
            PartialOutputPath = partialPath,
            BytesWritten = Interlocked.Read(ref _bytesWritten),
            FirstSampleAnchorTicks = _firstSampleAnchorTicks,
            TimestampFrequency = Stopwatch.Frequency,
            DiscontinuityCount = TotalDiscontinuityCount(),
            RecoveryCount = Volatile.Read(ref _successfulRecoveries),
            RecoveryAttempts = Interlocked.Read(ref _recoveryAttemptCount),
            GapFilledBytes = Interlocked.Read(ref _gapFilledBytesTotal),
            GapFilledMs = Interlocked.Read(ref _gapFilledMsTotal),
            MaxEstimatedGapMs = Interlocked.Read(ref _maxEstimatedGapMsObserved),
            ContinuityStatus = continuity
        };
        ApplyHfpMetadata(info);
        if (IsHfpMode || _autoHfpPairResult.IsBlockingFailure)
        {
            info.EndpointId = _options.EndpointId;
            info.FailureStage = failureStage ?? "HfpUnknown";
        }
        _events.Fail(info);
    }

    private string? EffectiveHfpRenderEndpointId
        => !string.IsNullOrEmpty(_options.HfpRenderEndpointId)
            ? _options.HfpRenderEndpointId
            : _resolvedHfpRenderEndpointId;

    private bool IsHfpMode => !string.IsNullOrEmpty(EffectiveHfpRenderEndpointId) &&
                               _options.CaptureEngine == AudioCaptureEngine.WasapiDirect;

    private void EnsureAutomaticHfpPairResolved()
    {
        if (!_options.AutoHfpPairDiscovery ||
            _options.CaptureEngine != AudioCaptureEngine.WasapiDirect ||
            !string.IsNullOrEmpty(_options.HfpRenderEndpointId) ||
            Interlocked.Exchange(ref _autoHfpPairResolutionAttempted, 1) != 0)
            return;

        try
        {
            var resolver = _hfpPairResolver ?? new HfpPairResolver();
            _autoHfpPairResult = resolver.Resolve(_options.EndpointId);
            _resolvedHfpRenderEndpointId = _autoHfpPairResult.RenderEndpointId;
        }
        catch
        {
            _autoHfpPairResult = HfpPairDiscoveryResult.EvidenceFailure(
                "Automatic HFP pair resolver failed", HfpTransportClassification.Unknown);
            _resolvedHfpRenderEndpointId = null;
        }
    }

    private void ApplyHfpMetadata(AudioHelperEventInfo info)
    {
        info.AutoHfpPairStatus = DiscoveryStatusText(_autoHfpPairResult.Status);
        info.AutoHfpPairResultCode = _autoHfpPairResult.ResultCode ?? "";
        info.AutoHfpPairTransportClassification = TransportClassificationText(
            _autoHfpPairResult.TransportClassification);

        if (IsHfpMode)
        {
            info.CaptureStrategy = string.IsNullOrEmpty(_captureStrategy)
                ? "hfp-duplex-prime-classic"
                : _captureStrategy;
            info.PairEvidence = string.IsNullOrEmpty(_pairEvidence) ? "unverified" : _pairEvidence;
            info.RenderPrimeReadyMs = _renderPrimeReadyMs;
            return;
        }

        info.CaptureStrategy = _autoHfpPairResult.IsBlockingFailure
            ? "hfp-auto-pair-discovery"
            : "wasapi-direct";
        info.PairEvidence = string.IsNullOrEmpty(_autoHfpPairResult.PairEvidence)
            ? "not_applicable"
            : _autoHfpPairResult.PairEvidence;
    }

    private void CaptureHfpMetadata(IAudioInput input)
    {
        if (!IsHfpMode || input is not IHfpAudioInputMetadata metadata)
            return;
        _captureStrategy = metadata.CaptureStrategy;
        _pairEvidence = metadata.PairEvidence;
        _renderPrimeReadyMs = metadata.RenderPrimeReadyMs;
    }

    private void CaptureHfpFailureMetadata(AudioInputOpenResult result)
    {
        if (!IsHfpMode && !_autoHfpPairResult.IsBlockingFailure)
            return;
        _captureStrategy = string.IsNullOrEmpty(result.CaptureStrategy)
            ? (_autoHfpPairResult.IsBlockingFailure ? "hfp-auto-pair-discovery" : "hfp-duplex-prime-classic")
            : result.CaptureStrategy;
        _pairEvidence = string.IsNullOrEmpty(result.PairEvidence)
            ? (_autoHfpPairResult.IsBlockingFailure ? _autoHfpPairResult.PairEvidence : "unverified")
            : result.PairEvidence;
        _renderPrimeReadyMs = -1;
    }

    private AudioInputOpenResult OpenInput(TimeSpan budget)
    {
        EnsureAutomaticHfpPairResolved();
        if (_autoHfpPairResult.IsBlockingFailure)
        {
            return AudioInputOpenResult.Failure(
                _autoHfpPairResult.ResultCode ?? "audio_hfp_pair_discovery_failed",
                _autoHfpPairResult.Reason,
                HfpFailureStages.PairDiscovery,
                pairEvidence: _autoHfpPairResult.PairEvidence,
                captureStrategy: "hfp-auto-pair-discovery");
        }

        if (IsHfpMode)
        {
            var factory = _hfpFactory ?? new HfpDuplexAudioInputFactory();
            return factory.Open(_options.EndpointId, EffectiveHfpRenderEndpointId!, budget);
        }

        return _inputFactory != null
            ? AudioInputOpenResult.FromTuple(_inputFactory(budget), "AudioOpen")
            : AudioInputOpenResult.FromTuple(WasapiAudioInput.Open(_options.EndpointId, budget), "AudioOpen");
    }

    private static string DiscoveryStatusText(HfpPairDiscoveryStatus status)
        => status switch
        {
            HfpPairDiscoveryStatus.Paired => "paired",
            HfpPairDiscoveryStatus.NoCandidate => "no_candidate",
            HfpPairDiscoveryStatus.Ambiguous => "ambiguous",
            HfpPairDiscoveryStatus.EvidenceFailure => "evidence_failure",
            _ => "not_applicable"
        };

    private static string TransportClassificationText(HfpTransportClassification classification)
        => classification switch
        {
            HfpTransportClassification.HfpCandidate => "hfp_candidate",
            HfpTransportClassification.NotHfp => "not_hfp",
            _ => "unknown"
        };

    private void CleanupPartial()
    {
        try { if (File.Exists(_paths.PartialPath)) File.Delete(_paths.PartialPath); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        RequestStop();
        StopStallMonitor();
        DisarmFirstPacketTimer();

        try
        {
            // Fast path: if neither input nor writer was ever initialized,
            // the session never ran; no need to wait for terminal convergence.
            if (_input == null && _writer == null)
            {
                _completed.Set();
            }
            else if (!_completed.Wait(StopWaitTimeout))
            {
                _completed.Set();
            }
        }
        catch { /* best effort */ }

        WaveFileWriter? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        try { writer?.Dispose(); } catch { }
        IAudioInput? input;
        lock (_stateLock)
        {
            input = _input;
            _input = null;
        }
        try { input?.Dispose(); } catch { }
        _completed.Dispose();
    }
}
