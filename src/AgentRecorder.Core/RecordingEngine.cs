using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Security;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Core;

public sealed class RecordingEngine : IDisposable
{
    internal readonly ConcurrentDictionary<string, Recording> _recs = new();
    internal readonly ConcurrentDictionary<string, Confirmation> _confs = new();

    /// <summary>
    /// Owns one complete countdown-plus-first-frame-wait operation for a
    /// recording. The single CTS spans both phases. The owning
    /// <see cref="RunCountdownAsync"/> is the sole disposer: it detaches its
    /// local first-frame handler and disposes the CTS in a finally block after
    /// the operation's final consumer exits. <see cref="CancelCountdown"/> only
    /// cancels (never disposes) operations it can still see in the registry,
    /// and audits <c>recording.countdown_cancelled</c> only when the visible
    /// countdown phase was truly in flight, at most once per operation.
    /// </summary>
    private sealed class CountdownOperation
    {
        public const int PhaseVisibleCountdown = 0;
        public const int PhaseFirstFrameWait = 1;
        public const int PhaseRetired = 2;

        public CancellationTokenSource Cts { get; } = new();
        public int Phase = PhaseVisibleCountdown;
        public int CancelAuditEmitted;
        public bool StartActionClaimed;
    }

    private readonly ConcurrentDictionary<string, CountdownOperation> _countdownOps = new();

    private sealed class ScreenshotSeriesOperation
    {
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => Completion.Task;
        public bool StopRequested;
        public int FrameInFlight;
        public bool FinalizationClaimed;
    }

    private readonly ConcurrentDictionary<string, ScreenshotSeriesOperation> _seriesOps = new();
    internal int ActiveScreenshotSeriesOperationCountForTests => _seriesOps.Count;

    /// <summary>
    /// Diagnostic seam for resource-lifecycle tests: number of countdown
    /// operations still registered (not yet retired and disposed).
    /// </summary>
    internal int ActiveCountdownOperationCountForTests => _countdownOps.Count;
    private readonly AuditLogger _audit;
    private readonly IPerformanceTracer _tracer;
    private readonly IRecordingBundleGenerator? _bundleGenerator;
    private readonly IMicrophoneDeviceProvider _microphoneProvider;
    private readonly IMicrophoneStatusProvider _microphoneStatusProvider;
    private readonly ISystemAudioEndpointProvider _systemAudioEndpointProvider;
    private readonly IDisplayTopologyProvider _displayTopologyProvider;
    private bool _usesDefaultBackendFactory = true;
    private Func<CaptureConfig, CapturePlan>? _capturePlanFactory =
        cfg => cfg.IsScreenshotSeries
            ? CaptureBackendSelector.BuildScreenshotSeriesPlan(cfg)
            : CaptureBackendSelector.BuildPlan(cfg);
    private Func<CaptureConfig, CaptureBackendSelection>? _backendSelectionFactory =
        null;
    private readonly object _lock = new();
    private ITrayContext? _tray;

    /// <summary>
    /// Injectable only for deterministic mark timestamp tests. Production uses
    /// the current UTC wall clock for public metadata; mark positions use the
    /// separate monotonic provider below.
    /// </summary>
    internal Func<DateTime> UtcNowForTests { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Narrow test seam for the monotonic media timeline. Production uses
    /// Stopwatch.GetTimestamp and never exposes this provider through HTTP.
    /// </summary>
    internal Func<long> MonotonicTimestampProviderForTests { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>
    /// Test-only frequency seam paired with MonotonicTimestampProviderForTests.
    /// Production remains Stopwatch.Frequency.
    /// </summary>
    internal long MonotonicFrequencyForTests { get; set; } = Stopwatch.Frequency;

    // State change notification: incremented on every recording/confirmation state transition,
    // used by GetConfirmationWait/GetStatusWait to detect changes via Monitor.Wait/PulseAll.
    internal int _stateVersion = 0;

    /// <summary>
    /// Factory used to select an ICaptureBackend for a given source type.
    /// Default: <c>CaptureBackendSelector.Select(cfg)</c>.
    /// Replaceable for tests that need an injected capture backend.
    /// </summary>
    public Func<CaptureConfig, (ICaptureBackend Backend, string BackendType)> BackendFactory
    {
        get => _backendFactory;
        set
        {
            _backendFactory = value ?? throw new ArgumentNullException(nameof(value));
            _usesDefaultBackendFactory = false;
            _backendSelectionFactory = null;
        }
    }

    private Func<CaptureConfig, (ICaptureBackend Backend, string BackendType)> _backendFactory =
        cfg =>
        {
            var plan = CaptureBackendSelector.BuildPlan(cfg);
            return (CaptureBackendSelector.CreateBackend(plan.PlannedBackend), plan.PlannedBackend);
        };

    /// <summary>
    /// Legacy test seam: set a factory that only needs the source kind.
    /// </summary>
    internal void SetBackendFactory(Func<string, (ICaptureBackend Backend, string BackendType)> factory)
    {
        BackendFactory = cfg => factory(cfg.SourceKind);
    }

    /// <summary>
    /// Test seam: overrides the confirmation expiry delay. Production default is
    /// 60 seconds; tests can set a short value to exercise the expiry path
    /// without waiting for the real timeout.
    /// </summary>
    internal TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Test seam: per-step countdown interval. Production default is 1 second.
    /// </summary>
    internal TimeSpan CountdownInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Test-only override for countdown steps. Production uses the normalized
    /// per-recording value; a non-null value is reserved for deterministic
    /// lifecycle tests.
    /// </summary>
    internal int? CountdownSteps { get; set; }

    /// <summary>
    /// Deterministic scheduling seam used only by race tests. The callback is
    /// invoked after countdown completion and immediately before the per-
    /// recording start-action gate is entered. Production leaves it null.
    /// </summary>
    internal Action<Recording, string>? BeforeStartActionForTests { get; set; }

    /// <summary>
    /// Deterministic race-test seam invoked when Stop has obtained the recording
    /// reference and is about to enter its lifecycle lock. Production leaves it
    /// null.
    /// </summary>
    internal Action<Recording>? BeforeStopForTests { get; set; }

    /// <summary>
    /// Deterministic scheduling seam used only by startup-exception race tests.
    /// The callback is invoked after a startup action throws and immediately
    /// before the exception path attempts to claim the failed terminal state.
    /// Production leaves it null.
    /// </summary>
    internal Action<Recording, string>? BeforeStartFailureForTests { get; set; }

    /// <summary>
    /// Test seam: timeout waiting for the first video frame after StartVideo.
    /// Production default is 10 seconds.
    /// </summary>
    internal TimeSpan FirstFrameTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan ScreenshotFrameTimeout { get; set; } = TimeSpan.FromSeconds(15);
    internal Func<CaptureConfig, IScreenshotFrameRunner> ScreenshotFrameRunnerFactoryForTests { get; set; } =
        _ => new FfmpegScreenshotFrameRunner();

    /// <summary>
    /// Deterministic screenshot-series race seams. The first callback runs
    /// before the per-frame start claim; the second runs after the claim and
    /// immediately before the runner is invoked.
    /// </summary>
    internal Action<Recording, int>? BeforeScreenshotFrameStartClaimForTests { get; set; }
    internal Action<Recording, int>? BeforeScreenshotFrameRunnerForTests { get; set; }
    internal Action<Recording, int>? BeforeScreenshotCountdownStepForTests { get; set; }
    internal Func<long, CancellationToken, Task>? ScreenshotDelaySchedulerForTests { get; set; }

    /// <summary>
    /// Test seam for supplying a detailed selection result without replacing
    /// the legacy tuple factory used by production composition roots.
    /// </summary>
    internal Func<CaptureConfig, CaptureBackendSelection>? BackendSelectionFactoryForTests
    {
        get => _backendSelectionFactory;
        set => _backendSelectionFactory = value;
    }

    /// <summary>
    /// Test seam for deterministic non-capturing plan revalidation. Production
    /// uses CaptureBackendSelector.BuildPlan for both plan snapshots.
    /// </summary>
    internal Func<CaptureConfig, CapturePlan>? CapturePlanFactoryForTests
    {
        get => _capturePlanFactory;
        set => _capturePlanFactory = value;
    }

    public RecordingEngine(AuditLogger audit, IPerformanceTracer? tracer = null,
        IRecordingBundleGenerator? bundleGenerator = null,
        IMicrophoneDeviceProvider? microphoneProvider = null,
        IMicrophoneStatusProvider? microphoneStatusProvider = null,
        IDisplayTopologyProvider? displayTopologyProvider = null,
        ISystemAudioEndpointProvider? systemAudioEndpointProvider = null)
    {
        _audit = audit;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
        _bundleGenerator = bundleGenerator;
        _microphoneProvider = microphoneProvider ?? new EmptyMicrophoneProvider();
        _microphoneStatusProvider = microphoneStatusProvider ?? NullMicrophoneStatusProvider.Instance;
        _systemAudioEndpointProvider = systemAudioEndpointProvider ?? new CoreAudioSystemAudioEndpointProvider();
        _displayTopologyProvider = displayTopologyProvider ?? SystemQueryDisplayTopologyProvider.Instance;
    }

    /// <summary>
    /// Injectable microphone device provider used by request parsing and bundle
    /// metadata. Production composition roots pass a single shared instance.
    /// Tests that do not inject a provider get a safe <see cref="EmptyMicrophoneProvider"/>
    /// instead of a mutable global fallback.
    /// </summary>
    public IMicrophoneDeviceProvider MicrophoneProvider => _microphoneProvider;

    /// <summary>
    /// Injectable microphone status provider used for fresh mute/volume checks
    /// during request parsing. Production composition roots pass a single shared
    /// instance; tests get <see cref="NullMicrophoneStatusProvider"/> by default.
    /// </summary>
    public IMicrophoneStatusProvider MicrophoneStatusProvider => _microphoneStatusProvider;

    public ISystemAudioEndpointProvider SystemAudioEndpointProvider => _systemAudioEndpointProvider;

    public void SetTray(ITrayContext tray) => _tray = tray;

    /// <summary>
    /// Bumps _stateVersion and pulses all waiters on _lock.
    /// Called after every recording/confirmation state transition.
    /// </summary>
    internal void BumpStateVersion()
    {
        lock (_lock)
        {
            _stateVersion++;
            Monitor.PulseAll(_lock);
        }
    }

    private string GetTraceIdForRecording(string recordingId)
        => _tracer.ResolveTraceId(recordingId) ?? "trace_unknown";

    /// <summary>
    /// Computes the wall-clock elapsed seconds for a recording.
    /// - 0 if capture has not started.
    /// - CompletedAtUtc - StartedAtUtc when a completion timestamp exists.
    /// - UtcNow - StartedAtUtc only for active recordings (recording, stopping).
    /// - 0 for terminal recordings missing CompletedAtUtc, to keep elapsed stable.
    /// - 0 for invalid, negative, or extreme deltas.
    /// </summary>
    private static int ComputeElapsedSeconds(Recording rec)
    {
        if (rec.StartedAtUtc == default)
            return 0;

        DateTime end;
        if (rec.CompletedAtUtc.HasValue)
        {
            end = rec.CompletedAtUtc.Value;
        }
        else if (rec.State is RecState.preparing or RecState.countdown)
        {
            // Microphone/encoder warmup and countdown are not user-visible recording time.
            return 0;
        }
        else if (rec.State == RecState.finalizing && rec.CaptureEndedAtUtc.HasValue)
        {
            // Once screen capture has ended, freeze elapsed time at the capture-ended boundary.
            end = rec.CaptureEndedAtUtc.Value;
        }
        else if (rec.State is RecState.recording or RecState.stopping or RecState.finalizing)
        {
            end = DateTime.UtcNow;
        }
        else
        {
            // Terminal or non-active state without a completion timestamp:
            // do not let elapsed grow by falling back to UtcNow.
            return 0;
        }

        var delta = end - rec.StartedAtUtc;
        if (delta < TimeSpan.Zero)
            return 0;

        double seconds = delta.TotalSeconds;
        if (seconds > int.MaxValue || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return 0;

        return (int)seconds;
    }

    /// <summary>
    /// Adds a chapter mark to an actively recording session. This is the one
    /// domain operation shared by the authenticated API and local hotkey path.
    /// </summary>
    public RecordingMark AddMark(string recordingId, string label, string source = "agent")
    {
        var rec = Get(recordingId);

        if (!string.Equals(source, "agent", StringComparison.Ordinal) &&
            !string.Equals(source, "hotkey", StringComparison.Ordinal))
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark source.",
                new { field = "source", allowed = new[] { "agent", "hotkey" } });
        }

        if (label is null)
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark label.",
                new { field = "label", reason = "required" });
        }

        RecordingMark mark;
        lock (rec)
        {
            if (rec.IsScreenshotSeries)
                throw new ApiException(409, "UNSUPPORTED_FEATURE",
                    "Chapter marks are not applicable to screenshot_series recordings.",
                    new { suggested_action = "use_video_recording_for_chapter_marks" });

            if (rec.State != RecState.recording)
            {
                throw RecordingNotActive(rec);
            }

            // Both the UTC metadata and monotonic mark anchor are established
            // by the trusted first-frame transition. Never synthesize either
            // from request/approval/backend/countdown/bundle timestamps.
            if (rec.StartedAtUtc == default ||
                !rec.MarkTimelineAnchorTicks.HasValue ||
                rec.MarkTimelineAnchorTicks.Value < 0)
            {
                throw new ApiException(409, "RECORDING_NOT_ACTIVE",
                    "Recording timeline has not observed its first frame yet.",
                    new
                    {
                        current_state = rec.State.ToString(),
                        suggested_action = "wait_for_first_frame"
                    });
            }

            long nowTicks;
            try
            {
                nowTicks = MonotonicTimestampProviderForTests();
            }
            catch
            {
                throw TimelineNotReady(rec, "monotonic_clock_unavailable");
            }

            if (!TryConvertMonotonicDeltaToMilliseconds(
                    rec.MarkTimelineAnchorTicks.Value, nowTicks,
                    MonotonicFrequencyForTests, out var tMs))
            {
                throw TimelineNotReady(rec, "monotonic_clock_invalid");
            }

            try
            {
                mark = new RecordingMark(tMs, label, source);
            }
            catch (ArgumentException)
            {
                // Do not copy the label or constructor text into an API error.
                throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark label.",
                    new { field = "label", reason = "must_be_valid_unicode_text" });
            }

            rec.AddMark(mark);
            _audit.Log("recording.mark_added", new
            {
                recording_id = rec.Id,
                t_ms = mark.TMs,
                source = mark.Source
            });
        }

        return mark;
    }

    private static ApiException RecordingNotActive(Recording rec) =>
        new(409, "RECORDING_NOT_ACTIVE",
            "Marks can only be added while the recording is actively recording.",
            new
            {
                current_state = rec.State.ToString(),
                suggested_action = "add_mark_while_recording"
            });

    private static ApiException TimelineNotReady(Recording rec, string reason) =>
        new(409, "RECORDING_NOT_ACTIVE",
            "Recording timeline is not ready.",
            new
            {
                current_state = rec.State.ToString(),
                suggested_action = "wait_for_first_frame",
                reason
            });

    private static bool TryConvertMonotonicDeltaToMilliseconds(
        long anchorTicks, long nowTicks, long frequency, out long milliseconds)
    {
        milliseconds = 0;
        if (anchorTicks < 0 || nowTicks < 0 || nowTicks < anchorTicks || frequency <= 0)
            return false;

        // Decimal keeps the product within a bounded exact range for the full
        // non-negative long tick delta, then checked conversion rejects values
        // that cannot be represented as a non-negative long millisecond value.
        ulong deltaTicks = (ulong)(nowTicks - anchorTicks);
        decimal elapsedMilliseconds = (decimal)deltaTicks * 1000m / frequency;
        if (elapsedMilliseconds < 0 || elapsedMilliseconds > long.MaxValue)
            return false;

        milliseconds = checked((long)decimal.Truncate(elapsedMilliseconds));
        return milliseconds >= 0;
    }

    private static string NormalizeStopReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "user_requested";
        return reason.Trim();
    }

    private static string AudioSourceKindName(AudioCaptureSourceKind sourceKind) => sourceKind switch
    {
        AudioCaptureSourceKind.Microphone => "microphone",
        AudioCaptureSourceKind.SystemLoopback => "system-loopback",
        _ => "none"
    };

    /// <summary>
    /// Computes a stable, finite, non-sensitive machine error code for a failed
    /// terminal recording. Never returns free-text messages, paths, or ffmpeg args.
    /// </summary>
    private static string ResolveTerminalErrorCode(string? backendType, AudioCaptureSourceKind audioSourceKind, OutputMeta meta, int exitCode,
        bool fileOk, bool durationOk, bool rangeOk, bool exitOk, bool allowWgcLifecycleReason)
    {
        bool audioRequested = audioSourceKind != AudioCaptureSourceKind.None;
        // Native WGC lifecycle reasons are already authenticated by the
        // helper and must outrank generic exit/file heuristics.
        if (IsWgcContinuousBackend(backendType) &&
            IsWgcContinuousOutputValidationFailure(meta.StopReason))
            return meta.StopReason!;

        if (allowWgcLifecycleReason &&
            IsWgcContinuousBackend(backendType) &&
            IsWgcLifecycleFailure(meta.StopReason))
            return meta.StopReason!;

        // WASAPI helper failures: if a stable, normalized helper error code was
        // captured and a microphone was requested, prioritize it over generic
        // ffmpeg-style codes like non_zero_exit. This must work for all real
        // AvSplit backend types (ffmpeg-av-split, ffmpeg-region-av-split,
        // ffmpeg-window-region-av-split), not only for a fictional wasapi-helper
        // backend type.
        if (audioRequested && !string.IsNullOrEmpty(meta.AudioHelperErrorCode))
            return meta.AudioHelperErrorCode;

        // Microphone-specific outcomes take precedence over generic validation so
        // callers get a stable, actionable code when audio evidence is missing.
        if (audioRequested)
        {
            if (string.Equals(meta.AudioStatus, "missing_audio_track", StringComparison.OrdinalIgnoreCase))
                return audioSourceKind == AudioCaptureSourceKind.SystemLoopback
                    ? "system_audio_missing_audio_track"
                    : "microphone_missing_audio_track";
            if (string.Equals(meta.AudioStatus, "start_failed", StringComparison.OrdinalIgnoreCase))
                return audioSourceKind == AudioCaptureSourceKind.SystemLoopback
                    ? "system_audio_start_failed"
                    : "microphone_start_failed";
        }

        if (!exitOk) return "non_zero_exit";
        if (!fileOk) return "empty_output";
        if (!durationOk) return "zero_duration";
        if (!rangeOk) return "duration_out_of_range";
        return "output_validation_failed";
    }

    private static bool IsTerminalState(RecState state) =>
        state is RecState.completed or RecState.failed or RecState.cancelled
            or RecState.rejected or RecState.expired;

    private enum StartFailureOwnership
    {
        Failed,
        Stop,
        ExistingTerminal
    }

    private static string AudioStatusFor(Recording rec, OutputMeta? meta)
    {
        if (!rec.Config.AudioRequested)
            return "not_requested";
        return meta?.AudioStatus ?? (IsTerminalState(rec.State) ? "unknown" : "pending");
    }

    private static string AudioContinuityFor(Recording rec, OutputMeta? meta)
    {
        if (!rec.Config.AudioRequested)
            return "not_checked";
        return meta?.AudioContinuityStatus ?? (IsTerminalState(rec.State) ? "not_checked" : "pending");
    }

    /// <summary>
    /// Sets the bundle snapshot to not_applicable for recordings that did not
    /// successfully complete with a bundle-eligible FFmpeg MP4. Called on every
    /// non-success terminal transition so a recording can never end with
    /// bundle.status=pending.
    /// </summary>
    private static void MarkBundleNotApplicable(Recording rec)
    {
        rec.BundleSnapshot = RecordingBundleSnapshot.NotApplicable();
    }

    public object CreateRecording(
        JsonNode cfg,
        string agent,
        ITrayContext tray,
        string? traceId = null,
        string? endpoint = null,
        SystemAudioEndpointInfo? preResolvedSystemAudioEndpoint = null)
    {
        traceId ??= "trace_" + Guid.NewGuid().ToString("N")[..16];
        endpoint ??= "recordings";

        // =====================================================================
        // Phase 1: Extract nested metadata (outside lock, no expensive work)
        // =====================================================================
        string? nestedRole = cfg["nested"]?["role"]?.GetValue<string>();
        string? parentId = cfg["nested"]?["parent_recording_id"]?.GetValue<string>();
        string? sessionId = cfg["nested"]?["session_id"]?.GetValue<string>();

        bool isNested = nestedRole == "outer" || nestedRole == "inner";

        // =====================================================================
        // Phase 2: Pre-flight concurrency + nested role/parent gate
        //         (coarse check, before expensive Build)
        // =====================================================================
        lock (_lock)
        {
            var active = _recs.Values
                .Where(r => r.State is RecState.preparing or RecState.countdown or RecState.recording or RecState.stopping or RecState.pending_confirmation or RecState.finalizing)
                .ToList();

            if (isNested)
            {
                // For explicit nested requests, prioritize role-specific errors over
                // the generic count error. This gives users actionable error messages.
                if (nestedRole == "outer")
                {
                    if (active.Any(r => r.NestedRole == "outer"))
                        throw new ApiException(409, "OUTER_RECORDING_ALREADY_EXISTS",
                            "A nested outer recording already exists. Only one outer recording is allowed.");
                }
                else if (nestedRole == "inner")
                {
                    if (string.IsNullOrEmpty(parentId))
                        throw new ApiException(400, "INVALID_ARGUMENT",
                            "nested.role=inner requires parent_recording_id");
                    if (!_recs.TryGetValue(parentId!, out var parent))
                        throw new ApiException(404, "PARENT_RECORDING_NOT_FOUND",
                            $"Parent recording '{parentId}' not found.");
                    // Strict parent state requirement: parent must be ACTIVELY RECORDING,
                    // not pending_confirmation, not stopping, not completed, etc.
                    // This prevents the "ghost parent" anti-pattern where an inner is
                    // created before the outer's confirmation flow is complete.
                    if (parent.State != RecState.recording)
                        throw new ApiException(409, "PARENT_NOT_RECORDING",
                            $"Parent recording '{parentId}' is not in 'recording' state (current state={parent.State}). " +
                            "Inner recording can only be created when the parent outer is actively recording.");
                    if (parent.NestedRole != "outer")
                        throw new ApiException(400, "PARENT_NOT_OUTER",
                            $"Parent recording '{parentId}' does not have nested.role='outer'.");
                    if (active.Any(r => r.NestedRole == "inner"))
                        throw new ApiException(409, "INNER_RECORDING_ALREADY_EXISTS",
                            "A nested inner recording already exists. Only one inner recording is allowed.");
                    if (!string.IsNullOrEmpty(sessionId) &&
                        !string.IsNullOrEmpty(parent.NestedSessionId) &&
                        sessionId != parent.NestedSessionId)
                        throw new ApiException(400, "SESSION_ID_MISMATCH",
                            "nested.session_id does not match parent's session_id.");
                }

                // Count check only reached if role-specific checks passed
                if (active.Count >= 2)
                    throw new ApiException(409, "TOO_MANY_CONCURRENT_RECORDINGS",
                        "Nested recording MVP supports at most 2 concurrent recordings (1 outer + 1 inner).");
            }
            else
            {
                if (active.Count >= 1)
                    throw new ApiException(409, "RECORDING_ALREADY_RUNNING",
                        "Another recording is already running. Stop it before starting a new one. " +
                        "To use nested recording, specify nested.role=outer/inner in the request body.");
            }
        }

        // =====================================================================
        // Phase 3: Build recording config (may enumerate displays/windows,
        // validate nested.role in ConfigParser Step 0, etc. This is expensive
        // so it runs outside lock.)
        // =====================================================================
        Recording rec;
        RecordingRequestSummary summary;
        try
        {
            rec = ConfigParser.Build(
                cfg,
                agent,
                out summary,
                _microphoneProvider,
                _microphoneStatusProvider,
                _systemAudioEndpointProvider,
                preResolvedSystemAudioEndpoint);
        }
        catch (ApiException ex)
        {
            // Config/validation failures are intent-level failures. No recording was
            // created, so recording.terminal must not be emitted.
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

        // =====================================================================
        // Phase 3.5: Preflight dry-run before creating a pending confirmation.
        // Fail fast for output-directory, disk-space, encoder, or bounds issues
        // that do not require user interaction.
        // =====================================================================
        var beforeConfirmationPreflight = RecordingPreflightChecker.CheckBeforeConfirmation(rec);
        if (!beforeConfirmationPreflight.Passed)
        {
            _audit.Log("recording.preflight_failed", new
            {
                recording_id = rec.Id,
                stage = "before_confirmation",
                source_type = rec.SourceType,
                output_path = rec.OutputPath,
                error_code = beforeConfirmationPreflight.ErrorCode,
                message = beforeConfirmationPreflight.Message,
                suggested_action = beforeConfirmationPreflight.SuggestedAction
            });
            // Recording object exists but was never registered; treat as an intent-level
            // validation failure rather than a recording terminal event.
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: beforeConfirmationPreflight.ErrorCode);
            throw new ApiException(400, beforeConfirmationPreflight.ErrorCode!,
                beforeConfirmationPreflight.Message!,
                new
                {
                    suggested_action = beforeConfirmationPreflight.SuggestedAction,
                    stage = "before_confirmation"
                });
        }

        // Construct the immutable, non-capturing decision before any local
        // confirmation is queued. Capability probing is allowed here; backend
        // construction and all pixel-producing work remain approval-gated.
        var capturePlan = (_capturePlanFactory ?? CaptureBackendSelector.BuildPlan)(rec.Config);
        if (rec.IsScreenshotSeries)
            ValidateScreenshotSeriesPlan(capturePlan, rec.Config);
        rec.ApprovedCapturePlan = capturePlan;
        rec.BackendType = capturePlan.PlannedBackend;

        // =====================================================================
        // Phase 4: Final guard + atomic register (prevents race condition where
        // two requests pass Phase-2 check, then both register.)
        // IMPORTANT: This Phase-4 must re-execute the COMPLETE guard logic,
        // not just the count check. During Phase-3 (Build), another request
        // may have registered an outer/inner that changes the guard outcome.
        // =====================================================================
        lock (_lock)
        {
            var currentActive = _recs.Values
                .Where(r => r.State is RecState.preparing or RecState.countdown or RecState.recording or RecState.stopping or RecState.pending_confirmation or RecState.finalizing)
                .ToList();

            if (isNested)
            {
                // For explicit nested requests, prioritize role-specific errors over
                // the generic count error. This gives users actionable error messages:
                // "you already have an outer/inner" is more useful than "too many recordings".
                if (nestedRole == "outer")
                {
                    // Re-check 1: no other active outer (race condition defense)
                    if (currentActive.Any(r => r.NestedRole == "outer"))
                        throw new ApiException(409, "OUTER_RECORDING_ALREADY_EXISTS",
                            "A nested outer recording already exists. Only one outer recording is allowed.");
                }
                else if (nestedRole == "inner")
                {
                    // Re-check 2: parent must still exist and be valid
                    if (string.IsNullOrEmpty(parentId))
                        throw new ApiException(400, "INVALID_ARGUMENT",
                            "nested.role=inner requires parent_recording_id");
                    if (!_recs.TryGetValue(parentId!, out var parent))
                        throw new ApiException(404, "PARENT_RECORDING_NOT_FOUND",
                            $"Parent recording '{parentId}' not found.");
                    // Re-check 3: parent must still be recording (may have transitioned during Build)
                    if (parent.State != RecState.recording)
                        throw new ApiException(409, "PARENT_NOT_RECORDING",
                            $"Parent recording '{parentId}' is no longer in 'recording' state (current state={parent.State}).");
                    // Re-check 4: parent must still be outer
                    if (parent.NestedRole != "outer")
                        throw new ApiException(400, "PARENT_NOT_OUTER",
                            $"Parent recording '{parentId}' does not have nested.role='outer'.");
                    // Re-check 5: no other active inner (race condition defense)
                    if (currentActive.Any(r => r.NestedRole == "inner"))
                        throw new ApiException(409, "INNER_RECORDING_ALREADY_EXISTS",
                            "A nested inner recording already exists. Only one inner recording is allowed.");
                    // Re-check 6: session_id must still match
                    if (!string.IsNullOrEmpty(sessionId) &&
                        !string.IsNullOrEmpty(parent.NestedSessionId) &&
                        sessionId != parent.NestedSessionId)
                        throw new ApiException(400, "SESSION_ID_MISMATCH",
                            "nested.session_id does not match parent's session_id.");
                }

                // Re-check 7: concurrent count (only reached if role-specific checks passed)
                if (currentActive.Count >= 2)
                    throw new ApiException(409, "TOO_MANY_CONCURRENT_RECORDINGS",
                        "Nested recording MVP supports at most 2 concurrent recordings (1 outer + 1 inner).");
            }
            else
            {
                // Re-check: non-nested still enforces single recording
                if (currentActive.Count >= 1)
                    throw new ApiException(409, "RECORDING_ALREADY_RUNNING",
                        "Another recording is already running. Stop it before starting a new one. " +
                        "To use nested recording, specify nested.role=outer/inner in the request body.");
            }

            _recs[rec.Id] = rec;
        }

        // Intent validation is complete: config parsed, preflight passed, and the
        // recording is now registered. Establish correlation/source context first
        // so that the validation event already carries recording_id and source_type.
        _tracer.CorrelationSet(traceId, rec.Id, sourceType: rec.SourceType);
        _tracer.IntentValidated(traceId, endpoint, success: true);

        _audit.Log("recording.requested", new
        {
            recording_id = rec.Id,
            agent, source_type = rec.SourceType,
            mode = rec.Mode,
            series_interval_ms = rec.ScreenshotSeries?.IntervalMs,
            series_planned_frame_count = rec.ScreenshotSeries?.PlannedFrameCount,
            audio_microphone = rec.Microphone,
            audio_source_kind = AudioSourceKindName(rec.AudioSourceKind),
            audio_endpoint_id = rec.SystemAudioEndpointId ?? "",
            audio_endpoint_name = rec.SystemAudioEndpointName ?? "",
            audio_endpoint_is_default = rec.SystemAudioEndpointIsDefault,
            audio_device_id = rec.MicrophoneDeviceId ?? "",
            requires_confirmation = true,
            nested_role = rec.NestedRole ?? "none",
            parent_recording_id = rec.ParentRecordingId ?? ""
        });
        LogCapturePlan("recording.capture_plan_created", rec, capturePlan, traceId);

        bool needConfirm = PolicyEngine.RequiresConfirmation();

        if (needConfirm)
        {
            var conf = new Confirmation { RecordingId = rec.Id };
            rec.ConfirmationId = conf.Id;
            rec.State = RecState.pending_confirmation;
            _confs[conf.Id] = conf;
            BumpStateVersion();
            _audit.Log("confirmation.created", new { recording_id = rec.Id, confirmation_id = conf.Id, nested_role = rec.NestedRole ?? "none" });
            _tracer.ConfirmationCreated(traceId, rec.Id, conf.Id);

            var presentation = BuildConfirmationPresentation(summary, rec, conf, capturePlan, traceId);
            var summaryWithMeta = RecordingConfirmationApiProjection.ToObject(presentation);

            tray.RequestConfirmation(presentation, decision =>
            {
                if (decision.Approved)
                {
                    // Atomically claim the decision. If another callback or the
                    // timeout has already claimed it, this call must not modify
                    // recording state or emit events.
                    if (!conf.TryDecide("approved"))
                        return;

                    BumpStateVersion();
                    _audit.Log("confirmation.approved", new { recording_id = rec.Id, confirmation_id = conf.Id });
                    _tracer.ConfirmationApproved(traceId, rec.Id, conf.Id);

                    // Revalidate the non-capturing decision before applying any
                    // output override or starting a countdown/backend. A window
                    // semantic change fails closed and requires a fresh request.
                    if (!TryRevalidateCapturePlan(rec, traceId, tray))
                        return;

                    var applied = ApplyConfirmationOutputDirectory(rec, decision, conf.Id);
                    if (!applied)
                    {
                        // This thread already won TryDecide("approved"); setting Status
                        // here is an owned-state adjustment after the output-directory
                        // override failed, not a bypass of the atomic decision gate.
                        conf.Status = "rejected";
                        rec.State = RecState.rejected;
                        MarkBundleNotApplicable(rec);
                        BumpStateVersion();
                        _audit.Log("confirmation.output_directory_rejected", new
                        {
                            recording_id = rec.Id,
                            confirmation_id = conf.Id,
                            directory = decision.OutputDirectory,
                            reason = "directory_override_failed"
                        });
                        _tracer.ConfirmationRejected(traceId, rec.Id, conf.Id);
                        _tracer.RecordingTerminal(traceId, rec.Id, status: "rejected", errorCode: "directory_override_failed");
                        tray.ShowError("保存目录不可用，录制未开始。");
                        TrySetIdleOnAllDone(tray);
                        return;
                    }

                    if (!TryPreflightBeforeStart(rec, conf, tray))
                        return;

                    StartCapture(rec, traceId, tray);
                }
                else
                {
                    if (!conf.TryDecide("rejected"))
                        return;

                    rec.State = RecState.rejected;
                    MarkBundleNotApplicable(rec);
                    BumpStateVersion();
                    _audit.Log("confirmation.rejected", new { recording_id = rec.Id, confirmation_id = conf.Id });
                    _tracer.ConfirmationRejected(traceId, rec.Id, conf.Id);
                    _tracer.RecordingTerminal(traceId, rec.Id, status: "rejected");
                    TrySetIdleOnAllDone(tray);
                }
            });

            Task.Delay(ConfirmationTimeout).ContinueWith(_ => ApplyConfirmationExpiry(conf, rec, traceId, tray));

            return new
            {
                status = "requires_user_confirmation",
                recording_id = rec.Id,
                confirmation_id = conf.Id,
                summary = summaryWithMeta,
                bundle = BundleObj(rec),
                performance_trace_id = traceId
            };
        }

        if (!TryPreflightBeforeStart(rec, null, tray))
        {
            return new
            {
                recording_id = rec.Id,
                status = "failed",
                error = rec.Error,
                expected_output = rec.OutputPath,
                bundle = BundleObj(rec),
                performance_trace_id = traceId
            };
        }

        StartCapture(rec, traceId, tray);
            return new
            {
                recording_id = rec.Id, mode = rec.Mode,
                status = rec.IsScreenshotSeries ? PublicScreenshotSeriesStatus(rec) : "recording",
                started_at = Iso(rec.StartedAtUtc), expected_output = rec.OutputPath,
                config = new { countdown_seconds = rec.CountdownSeconds, duration_seconds = rec.DurationSeconds },
                series = rec.IsScreenshotSeries ? ScreenshotSeriesStatus(rec) : null,
                bundle = BundleObj(rec),
                performance_trace_id = traceId
            };
    }

    private bool ApplyConfirmationOutputDirectory(Recording rec, ConfirmationDecision decision, string confirmationId)
    {
        if (string.IsNullOrWhiteSpace(decision.OutputDirectory))
        {
            // No override requested: still honor "remember default" if a directory is somehow absent.
            return true;
        }

        try
        {
            PolicyEngine.ValidateDirectory(decision.OutputDirectory);
            Directory.CreateDirectory(decision.OutputDirectory);
            var newPath = rec.IsScreenshotSeries
                ? OutputPathResolver.MoveScreenshotSeriesToDirectory(rec.OutputPath, decision.OutputDirectory, rec)
                : OutputPathResolver.MoveToDirectory(rec.OutputPath, decision.OutputDirectory);
            rec.OutputPath = newPath;
            if (rec.Config != null)
                rec.Config.OutputPath = newPath;

            _audit.Log("confirmation.output_directory_selected", new
            {
                recording_id = rec.Id,
                confirmation_id = confirmationId,
                directory = decision.OutputDirectory,
                remember_default = decision.RememberOutputDirectory
            });
        }
        catch (Exception ex)
        {
            _audit.Log("confirmation.output_directory_override_failed", new
            {
                recording_id = rec.Id,
                confirmation_id = confirmationId,
                directory = decision.OutputDirectory,
                error = ex.Message
            });
            return false;
        }

        if (decision.RememberOutputDirectory)
        {
            var saved = OutputSettingsStore.SaveDefaultOutputDir(decision.OutputDirectory);
            if (saved)
            {
                _audit.Log("output.default_directory_saved", new
                {
                    directory = decision.OutputDirectory
                });
            }
            else
            {
                _audit.Log("output.default_directory_save_failed", new
                {
                    directory = decision.OutputDirectory
                });
            }
        }

        return true;
    }

    private bool TryRevalidateCapturePlan(Recording rec, string traceId, ITrayContext tray)
    {
        var approved = rec.ApprovedCapturePlan;
        if (approved == null)
            return true;

        DisplayTopologySnapshot? currentTopology = null;
        string? topologyFailure = null;
        string? audioEndpointFailure = null;
        bool approvedRegion = string.Equals(approved.SourceKind, "region", StringComparison.Ordinal);
        bool screenshotDisplay = rec.IsScreenshotSeries &&
            string.Equals(approved.SourceKind, "display", StringComparison.Ordinal);
        bool topologyRequired = approvedRegion || screenshotDisplay;
        if (topologyRequired && !TryValidateApprovedRegionTopology(
                approved,
                rec.Config,
                out currentTopology,
                out topologyFailure))
        {
            // Topology is checked before rebuilding the capability/backend plan.
            // A stale or malformed region must never reach a backend, helper,
            // countdown, or output-directory side effect.
        }

        if (topologyFailure == null && rec.Config.IsSystemLoopback &&
            !IsApprovedSystemAudioEndpointCurrent(rec, out audioEndpointFailure))
        {
            // The approved endpoint is a capture-plan input. If it disappeared,
            // became inactive, changed direction, or changed display identity,
            // fail closed before backend construction.
        }

        CapturePlan? revalidated = null;
        string? failureType = null;
        if (topologyFailure == null && audioEndpointFailure == null)
        {
            try
            {
                revalidated = (_capturePlanFactory ?? CaptureBackendSelector.BuildPlan)(rec.Config);
                if (rec.IsScreenshotSeries)
                    ValidateScreenshotSeriesPlan(revalidated, rec.Config);
            }
            catch (Exception ex)
            {
                failureType = ex.GetType().Name;
            }
        }

        bool changed = topologyFailure != null || audioEndpointFailure != null || revalidated == null || IsCapturePlanDrift(approved, revalidated);
        var approvedDisplayBounds = approved.DisplayBounds == null
            ? null
            : new
            {
                x = approved.DisplayBounds.X,
                y = approved.DisplayBounds.Y,
                width = approved.DisplayBounds.Width,
                height = approved.DisplayBounds.Height
            };
        var currentDisplayBounds = currentTopology.HasValue
            ? new
            {
                x = currentTopology.Value.Bounds.X,
                y = currentTopology.Value.Bounds.Y,
                width = currentTopology.Value.Bounds.Width,
                height = currentTopology.Value.Bounds.Height
            }
            : revalidated?.DisplayBounds == null
                ? null
                : new
                {
                    x = revalidated.DisplayBounds.X,
                    y = revalidated.DisplayBounds.Y,
                    width = revalidated.DisplayBounds.Width,
                    height = revalidated.DisplayBounds.Height
                };
        try
        {
            _audit.Log("recording.capture_plan_revalidated", new
            {
                recording_id = rec.Id,
                source_type = rec.SourceType,
                approved_backend = approved.PlannedBackend,
                approved_semantics = approved.CaptureSemantics,
                approved_preview_semantics = approved.PreviewSemantics,
                approved_coordinate_space = approved.CoordinateSpace,
                approved_reason_code = approved.Evidence.SelectionReasonCode,
                revalidated_backend = revalidated?.PlannedBackend ?? "unavailable",
                revalidated_semantics = revalidated?.CaptureSemantics ?? "unavailable",
                revalidated_preview_semantics = revalidated?.PreviewSemantics ?? "unavailable",
                revalidated_coordinate_space = revalidated?.CoordinateSpace ?? "unavailable",
                revalidated_reason_code = revalidated?.Evidence.SelectionReasonCode ?? "plan_unavailable",
                revalidated_availability_source = revalidated?.Evidence.AvailabilitySource ?? "not_run",
                approved_display_id = approved.TargetDisplayId ?? "",
                revalidated_display_id = currentTopology?.PublicId ?? revalidated?.TargetDisplayId ?? "",
                approved_display_identity_fingerprint = approved.TargetDisplayIdentity ?? "",
                revalidated_display_identity_fingerprint = ResolvedIdentityForAudit(currentTopology)
                    ?? revalidated?.TargetDisplayIdentity ?? "",
                approved_display_bounds = approvedDisplayBounds,
                revalidated_display_bounds = currentDisplayBounds,
                topology_status = topologyRequired
                    ? topologyFailure == null ? "passed" : "failed"
                    : "not_required",
                topology_reason = topologyRequired
                    ? topologyFailure ?? "matched"
                    : "not_required",
                approved_audio_source_kind = AudioSourceKindName(approved.AudioSourceKind),
                approved_audio_endpoint_id = approved.AudioEndpointId ?? "",
                approved_audio_endpoint_name = approved.AudioEndpointName ?? "",
                audio_endpoint_status = audioEndpointFailure == null ? "matched" : audioEndpointFailure,
                semantics_changed = changed,
                failure_type = topologyFailure ?? audioEndpointFailure ?? failureType ?? ""
            });
        }
        catch { }

        if (_tracer is ICapturePlanPerformanceTracer planTracer)
        {
            try
            {
                planTracer.CapturePlanRevalidated(
                    traceId,
                    rec.Id,
                    approved.PlannedBackend,
                    approved.CaptureSemantics,
                    approved.Evidence.SelectionReasonCode,
                    revalidated?.PlannedBackend ?? "unavailable",
                    revalidated?.CaptureSemantics ?? "unavailable",
                    revalidated?.Evidence.SelectionReasonCode ?? "plan_unavailable",
                    changed);
            }
            catch { }
        }

        if (!changed)
            return true;

        const string errorCode = "capture_semantics_changed";
        lock (rec)
        {
            if (rec.IsFinalized)
                return false;

            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.StopReason = errorCode;
            rec.Error = errorCode;
            rec.Warnings.Add(errorCode);
            MarkBundleNotApplicable(rec);
            rec.State = RecState.failed;
            BumpStateVersion();
            rec.PublishFinalized();
        }

        try
        {
            _audit.Log("recording.capture_semantics_changed", new
            {
                recording_id = rec.Id,
                source_type = rec.SourceType,
                approved_backend = approved.PlannedBackend,
                approved_semantics = approved.CaptureSemantics,
                approved_coordinate_space = approved.CoordinateSpace,
                approved_reason_code = approved.Evidence.SelectionReasonCode,
                revalidated_backend = revalidated?.PlannedBackend ?? "unavailable",
                revalidated_semantics = revalidated?.CaptureSemantics ?? "unavailable",
                revalidated_coordinate_space = revalidated?.CoordinateSpace ?? "unavailable",
                revalidated_reason_code = revalidated?.Evidence.SelectionReasonCode ?? "plan_unavailable",
                approved_display_id = approved.TargetDisplayId ?? "",
                revalidated_display_id = currentTopology?.PublicId ?? revalidated?.TargetDisplayId ?? "",
                approved_display_identity_fingerprint = approved.TargetDisplayIdentity ?? "",
                revalidated_display_identity_fingerprint = ResolvedIdentityForAudit(currentTopology)
                    ?? revalidated?.TargetDisplayIdentity ?? "",
                approved_display_bounds = approvedDisplayBounds,
                revalidated_display_bounds = currentDisplayBounds,
                topology_status = topologyRequired
                    ? topologyFailure == null ? "passed" : "failed"
                    : "not_required",
                topology_reason = topologyRequired
                    ? topologyFailure ?? "matched"
                    : "not_required",
                approved_audio_source_kind = AudioSourceKindName(approved.AudioSourceKind),
                approved_audio_endpoint_id = approved.AudioEndpointId ?? "",
                approved_audio_endpoint_name = approved.AudioEndpointName ?? "",
                audio_endpoint_status = audioEndpointFailure == null ? "matched" : audioEndpointFailure,
                error_code = errorCode
            });
        }
        catch { }

        _tracer.RecordingTerminal(traceId, rec.Id, status: "failed", stopReason: errorCode, errorCode: errorCode);

        if (tray is IRecordingFailureNotifier notifier)
        {
            notifier.ShowRecordingFailure(rec.Id, errorCode);
        }
        else
        {
            tray.ShowError("Capture semantics changed; retry the request. / 捕获语义已改变，请重新发起请求。" );
        }

        TrySetIdleOnAllDone(tray);
        return false;
    }

    private bool TryValidateApprovedRegionTopology(
        CapturePlan approved,
        CaptureConfig currentConfig,
        out DisplayTopologySnapshot? observedCurrent,
        out string? failureReason)
    {
        observedCurrent = null;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(approved.TargetDisplayIdentity) ||
            approved.DisplayBounds == null ||
            approved.Bounds == null)
        {
            failureReason = approved.TargetDisplayIdentityStatus == DisplayIdentityResolutionStatus.Unavailable
                ? "identity_unavailable"
                : "identity_unresolved";
            return false;
        }

        if (approved.TargetDisplayIdentityStatus != DisplayIdentityResolutionStatus.Resolved)
        {
            failureReason = approved.TargetDisplayIdentityStatus == DisplayIdentityResolutionStatus.Unavailable
                ? "identity_unavailable"
                : "identity_unresolved";
            return false;
        }

        IReadOnlyList<DisplayTopologySnapshot> displays;
        try
        {
            displays = _displayTopologyProvider.GetCurrentDisplays();
        }
        catch
        {
            failureReason = "topology_provider_failed";
            return false;
        }

        if (displays == null)
        {
            failureReason = "topology_provider_failed";
            return false;
        }

        var matches = displays
            .Where(display => display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                string.Equals(
                display.StableIdentity,
                approved.TargetDisplayIdentity,
                StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            var samePublicId = displays
                .Where(display => string.Equals(
                    display.PublicId,
                    approved.TargetDisplayId,
                    StringComparison.Ordinal))
                .ToArray();
            if (samePublicId.Length > 0)
            {
                observedCurrent = samePublicId[0];
                failureReason = samePublicId.Any(display =>
                    display.IdentityStatus == DisplayIdentityResolutionStatus.Ambiguous)
                        ? "identity_ambiguous"
                    : samePublicId.Any(display =>
                        display.IdentityStatus == DisplayIdentityResolutionStatus.Unavailable)
                        ? "identity_unavailable"
                    : samePublicId.Any(display =>
                        display.IdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
                        string.IsNullOrWhiteSpace(display.StableIdentity))
                        ? "identity_unresolved"
                        : "identity_mismatch";
                return false;
            }

            // A single observed display is safe to include in the audit as the
            // current candidate. It contains only public metadata and bounds.
            if (displays.Count == 1)
                observedCurrent = displays[0];
            failureReason = "identity_missing";
            return false;
        }

        observedCurrent = matches[0];
        if (matches.Length != 1)
        {
            failureReason = "identity_ambiguous";
            return false;
        }

        var current = matches[0];
        if (current.IdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
            string.IsNullOrWhiteSpace(current.StableIdentity))
        {
            failureReason = current.IdentityStatus == DisplayIdentityResolutionStatus.Ambiguous
                ? "identity_ambiguous"
                : "identity_unresolved";
            return false;
        }

        if (current.Bounds != approved.DisplayBounds)
        {
            failureReason = "topology_display_bounds_changed";
            return false;
        }

        var approvedDisplay = new WgcRegionRect(
            current.Bounds.X,
            current.Bounds.Y,
            current.Bounds.Width,
            current.Bounds.Height);
        var approvedRegion = new WgcRegionRect(
            approved.Bounds.X,
            approved.Bounds.Y,
            approved.Bounds.Width,
            approved.Bounds.Height);
        if (!WgcRegionGeometry.TryGetCrop(approvedDisplay, approvedRegion, out _, out _))
        {
            failureReason = "topology_region_not_contained";
            return false;
        }

        // The approved plan is immutable, but keep the live request geometry
        // under the same containment lock so a stale/mutated request fails
        // before the backend selection/probe path.
        var currentRegion = new WgcRegionRect(
            currentConfig.Bounds.x,
            currentConfig.Bounds.y,
            currentConfig.Bounds.w,
            currentConfig.Bounds.h);
        if (!WgcRegionGeometry.TryGetCrop(approvedDisplay, currentRegion, out _, out _))
        {
            failureReason = "topology_region_not_contained";
            return false;
        }

        return true;
    }

    private static bool IsCapturePlanDrift(CapturePlan approved, CapturePlan current)
    {
        if (!string.Equals(approved.PreviewSemantics, current.PreviewSemantics, StringComparison.Ordinal))
            return true;

        // Bounds are meaningful only in their approved coordinate space. A
        // backend that preserves the other semantic fields must still fail
        // closed when that space changes after confirmation.
        if (!string.Equals(approved.CoordinateSpace, current.CoordinateSpace, StringComparison.Ordinal))
            return true;

        if (approved.AudioSourceKind != current.AudioSourceKind ||
            !string.Equals(approved.AudioEndpointId, current.AudioEndpointId, StringComparison.Ordinal) ||
            !string.Equals(approved.AudioEndpointName, current.AudioEndpointName, StringComparison.Ordinal) ||
            approved.AudioEndpointIsDefault != current.AudioEndpointIsDefault)
            return true;

        bool approvedRegion = string.Equals(approved.SourceKind, "region", StringComparison.Ordinal);
        bool currentRegion = string.Equals(current.SourceKind, "region", StringComparison.Ordinal);
        if (approvedRegion || currentRegion)
        {
            if (!approvedRegion || !currentRegion)
                return true;
            if (!string.Equals(approved.TargetDisplayIdentity, current.TargetDisplayIdentity, StringComparison.Ordinal))
                return true;
            if (approved.TargetDisplayIdentityStatus != current.TargetDisplayIdentityStatus)
                return true;
            if (approved.DisplayBounds != current.DisplayBounds || approved.Bounds != current.Bounds)
                return true;
            if (!string.Equals(approved.PlannedBackend, current.PlannedBackend, StringComparison.Ordinal))
                return true;
            return !string.Equals(approved.CaptureSemantics, current.CaptureSemantics, StringComparison.Ordinal);
        }

        bool approvedWindow = string.Equals(approved.SourceKind, "window", StringComparison.Ordinal);
        bool currentWindow = string.Equals(current.SourceKind, "window", StringComparison.Ordinal);
        if (!approvedWindow && !currentWindow)
        {
            bool approvedDisplay = string.Equals(approved.SourceKind, "display", StringComparison.Ordinal);
            bool currentDisplay = string.Equals(current.SourceKind, "display", StringComparison.Ordinal);
            if (!approvedDisplay && !currentDisplay)
                return false;
            if (!approvedDisplay || !currentDisplay)
                return true;
            return !string.Equals(approved.TargetDisplayId, current.TargetDisplayId, StringComparison.Ordinal)
                || !string.Equals(approved.TargetDisplayIdentity, current.TargetDisplayIdentity, StringComparison.Ordinal)
                || approved.TargetDisplayIdentityStatus != current.TargetDisplayIdentityStatus
                || approved.DisplayBounds != current.DisplayBounds
                || approved.Bounds != current.Bounds
                || !string.Equals(approved.PlannedBackend, current.PlannedBackend, StringComparison.Ordinal)
                || !string.Equals(approved.CaptureSemantics, current.CaptureSemantics, StringComparison.Ordinal);
        }
        if (!approvedWindow || !currentWindow)
            return true;

        if (!string.Equals(approved.TargetIdentity, current.TargetIdentity, StringComparison.Ordinal))
            return true;

        // TargetIdentity is the privacy-safe summary value, but retain the
        // native HWND check as an independent lock. A future caller must not
        // be able to keep a stale identity string while switching the actual
        // source window handle after confirmation.
        if (approved.WindowHandle != current.WindowHandle)
            return true;

        if (approved.Bounds != current.Bounds)
            return true;

        if (!string.Equals(approved.PlannedBackend, current.PlannedBackend, StringComparison.Ordinal))
            return true;

        if (!string.Equals(approved.CaptureSemantics, current.CaptureSemantics, StringComparison.Ordinal))
            return true;

        // A window-surface promise may never degrade to a desktop rectangle,
        // even if another selector result would otherwise be startable.
        return approved.IsWindowSurface && !current.IsWindowSurface;
    }

    private static void ValidateScreenshotSeriesPlan(CapturePlan plan, CaptureConfig cfg)
    {
        string expectedSemantics = cfg.SourceKind switch
        {
            "display" => "display_surface",
            "region" => "region_rectangle",
            "window" => "screen_rectangle",
            _ => ""
        };

        bool valid = string.Equals(plan.PlannedBackend, "ffmpeg-single-frame", StringComparison.Ordinal)
            && string.Equals(plan.SourceKind, cfg.SourceKind, StringComparison.Ordinal)
            && string.Equals(plan.CaptureSemantics, expectedSemantics, StringComparison.Ordinal)
            && string.Equals(plan.PreviewSemantics, expectedSemantics, StringComparison.Ordinal)
            && string.Equals(plan.CoordinateSpace, "virtual_screen", StringComparison.Ordinal)
            && plan.Bounds is not null
            && plan.Bounds.X == cfg.Bounds.x
            && plan.Bounds.Y == cfg.Bounds.y
            && plan.Bounds.Width == cfg.Bounds.w
            && plan.Bounds.Height == cfg.Bounds.h
            && plan.AudioSourceKind == AudioCaptureSourceKind.None
            && !plan.IsWindowSurface;

        if (!valid)
        {
            throw new ApiException(400, "UNSUPPORTED_FEATURE",
                "The approved screenshot-series capture plan is not supported by the single-frame runner.",
                new
                {
                    mode = ScreenshotSeriesConfig.ModeName,
                    planned_backend = plan.PlannedBackend,
                    capture_semantics = plan.CaptureSemantics,
                    suggested_action = "retry_with_a_supported_screenshot_target"
                });
        }
    }

    private bool IsApprovedSystemAudioEndpointCurrent(Recording rec, out string? failure)
    {
        failure = null;
        var endpointId = rec.Config.SystemLoopbackEndpoint;
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            failure = "system_audio_endpoint_missing";
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var endpoint = _systemAudioEndpointProvider.GetEndpointAsync(endpointId, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (endpoint == null)
            {
                failure = "system_audio_endpoint_not_found";
                return false;
            }

            if (!string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal) ||
                !string.Equals(endpoint.Direction, "render", StringComparison.OrdinalIgnoreCase))
            {
                failure = "system_audio_endpoint_changed";
                return false;
            }

            if (!string.Equals(endpoint.State, "active", StringComparison.OrdinalIgnoreCase))
            {
                failure = "system_audio_endpoint_inactive";
                return false;
            }

            if (!string.Equals(endpoint.Name, rec.SystemAudioEndpointName, StringComparison.Ordinal))
            {
                failure = "system_audio_endpoint_changed";
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            failure = "system_audio_endpoint_revalidation_timeout";
            return false;
        }
        catch
        {
            failure = "system_audio_endpoint_revalidation_unavailable";
            return false;
        }
    }

    private static string? ResolvedIdentityForAudit(DisplayTopologySnapshot? topology)
        => topology.HasValue &&
            topology.Value.IdentityStatus == DisplayIdentityResolutionStatus.Resolved
            ? topology.Value.StableIdentity
            : null;

    private void LogCapturePlan(string eventName, Recording rec, CapturePlan plan, string traceId)
    {
        try
        {
            _audit.Log(eventName, new
            {
                recording_id = rec.Id,
                source_type = rec.SourceType,
                target_identity = plan.TargetIdentity ?? "",
                requested_backend = plan.RequestedBackend,
                planned_backend = plan.PlannedBackend,
                capture_semantics = plan.CaptureSemantics,
                preview_semantics = plan.PreviewSemantics,
                target_display_id = plan.TargetDisplayId ?? "",
                target_display_identity_fingerprint = plan.TargetDisplayIdentity ?? "",
                target_display_bounds = plan.DisplayBounds == null
                    ? null
                    : new
                    {
                        x = plan.DisplayBounds.X,
                        y = plan.DisplayBounds.Y,
                        width = plan.DisplayBounds.Width,
                        height = plan.DisplayBounds.Height
                    },
                selection_reason_code = plan.Evidence.SelectionReasonCode,
                availability_source = plan.Evidence.AvailabilitySource,
                availability_elapsed_ms = plan.Evidence.AvailabilityElapsedMs,
                fallback = plan.FallbackOccurred,
                audio_source_kind = plan.AudioSourceKind switch
                {
                    AudioCaptureSourceKind.Microphone => "microphone",
                    AudioCaptureSourceKind.SystemLoopback => "system-loopback",
                    _ => "none"
                },
                audio_endpoint_id = plan.AudioEndpointId ?? "",
                audio_endpoint_name = plan.AudioEndpointName ?? "",
                audio_endpoint_is_default = plan.AudioEndpointIsDefault
            });
        }
        catch { }

        if (_tracer is ICapturePlanPerformanceTracer planTracer)
        {
            try
            {
                planTracer.CapturePlanCreated(
                    traceId,
                    rec.Id,
                    plan.RequestedBackend,
                    plan.PlannedBackend,
                    plan.CaptureSemantics,
                    plan.Evidence.SelectionReasonCode,
                    plan.Evidence.AvailabilitySource,
                    plan.FallbackOccurred);
            }
            catch { }
        }
    }

    /// <summary>
    /// Runs the before-start preflight checks. If they fail, marks the recording
    /// as failed, logs a <c>recording.preflight_failed</c> audit event, shows a
    /// local error, and transitions the tray to idle. Returns true if capture
    /// may proceed.
    /// </summary>
    private bool TryPreflightBeforeStart(Recording rec, Confirmation? conf, ITrayContext tray)
    {
        var preflight = RecordingPreflightChecker.CheckBeforeStart(rec);
        if (preflight.Passed)
            return true;

        MarkBundleNotApplicable(rec);
        rec.Error = preflight.Message;
        rec.Warnings.Add($"preflight_failed: {preflight.ErrorCode}");
        rec.State = RecState.failed;
        BumpStateVersion();
        _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "failed", errorCode: preflight.ErrorCode);
        _audit.Log("recording.preflight_failed", new
        {
            recording_id = rec.Id,
            stage = "before_start",
            source_type = rec.SourceType,
            output_path = rec.OutputPath,
            error_code = preflight.ErrorCode,
            message = preflight.Message,
            suggested_action = preflight.SuggestedAction
        });
        tray.ShowError(preflight.Message!);

        if (conf != null)
            TrySetIdleOnAllDone(tray);
        else
            tray.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));

        return false;
    }

    /// <summary>
    /// Test-only helper: directly call StartCapture with a Recording that
    /// has already been populated (SourceType, Config, Backend, etc.).
    /// Bypasses CreateRecording and its window / display enum lookups.
    /// </summary>
    public void StartCaptureForTests(Recording rec, ITrayContext tray, string? traceId = null)
    {
        if (rec == null) throw new ArgumentNullException(nameof(rec));
        // Mimic what CreateRecording does: register by id so GetStatus /
        // GetOutput / List can find it.
        _recs[rec.Id] = rec;
        traceId ??= "trace_" + Guid.NewGuid().ToString("N")[..16];
        _tracer.IntentAccepted(traceId, "test_direct");
        _tracer.CorrelationSet(traceId, rec.Id, rec.ConfirmationId, rec.SourceType);
        StartCapture(rec, traceId, tray);
    }

    private void StartCapture(Recording rec, string? traceId, ITrayContext tray)
    {
        rec.Config.NormalizeAudioSource();
        NormalizeRecordingCountdown(rec);
        if (rec.AudioSourceKind == AudioCaptureSourceKind.None)
            rec.AudioSourceKind = rec.Config.AudioSourceKind;

        if (rec.IsScreenshotSeries)
        {
            StartScreenshotSeries(rec, traceId, tray);
            return;
        }

        // Production creates the backend only from the already-approved plan.
        // Legacy test seams may still supply a concrete selection or factory.
        CaptureBackendSelection? selectionEvidence = null;
        (ICaptureBackend Backend, string BackendType) selection;
        if (_backendSelectionFactory != null)
        {
            selectionEvidence = _backendSelectionFactory(rec.Config);
            selection = selectionEvidence.AsTuple();
        }
        else if (_usesDefaultBackendFactory && rec.ApprovedCapturePlan != null)
        {
            selection = (
                CaptureBackendSelector.CreateBackend(rec.ApprovedCapturePlan.PlannedBackend),
                rec.ApprovedCapturePlan.PlannedBackend);
        }
        else
        {
            selection = BackendFactory(rec.Config);
        }
        rec.Backend = selection.Backend;
        rec.BackendType = selection.BackendType;
        var evidence = selectionEvidence?.Evidence ?? rec.ApprovedCapturePlan?.Evidence ?? new CaptureBackendSelectionEvidence(
            "default",
            rec.BackendType,
            "custom_backend_factory",
            "not_run",
            null,
            false);

        // Inject the microphone status provider into backends that can use it
        // for runtime endpoint supervision.
        if (rec.Backend is IMicrophoneStatusConsumer consumer)
        {
            consumer.MicrophoneStatusProvider = _microphoneStatusProvider;
        }

        try
        {
            _audit.Log("recording.backend_selected", new
            {
                recording_id = rec.Id,
                source_type = rec.SourceType,
                backend = rec.BackendType,
                requested_backend = evidence.RequestedBackend,
                selected_backend = evidence.SelectedBackend,
                selection_reason_code = evidence.SelectionReasonCode,
                availability_source = evidence.AvailabilitySource,
                availability_elapsed_ms = evidence.AvailabilityElapsedMs,
                fallback = evidence.Fallback
            });
        }
        catch
        {
            // Backend-selection diagnostics must not block an approved start.
        }

        if (_tracer is IBackendSelectionPerformanceTracer selectionTracer)
        {
            try
            {
                selectionTracer.CaptureBackendSelected(
                    traceId ?? "trace_unknown",
                    rec.Id,
                    evidence.RequestedBackend,
                    evidence.SelectedBackend,
                    evidence.SelectionReasonCode,
                    evidence.AvailabilitySource,
                    evidence.AvailabilityElapsedMs,
                    evidence.Fallback);
            }
            catch
            {
                // Diagnostic tracing must never affect backend selection or start.
            }
        }

        // Hook natural exit BEFORE setting state and BEFORE calling
        // Backend.Start(). This way a synchronous backend can
        // FinalizeRecording() from inside Start(),
        // which will bump state preparing -> completed/failed.
        rec.Backend.OnNaturalExit((exitCode, meta) =>
        {
            FinalizeRecording(rec, meta, exitCode, natural: true, stopReason: null, tray);
        });

        // Subscribe to first-frame progress evidence BEFORE calling Start().
        // This catches synchronous observations that happen inside Start().
        if (rec.Backend is IFirstFrameObservableCaptureBackend observable)
        {
            observable.FirstFrameObserved += obs => OnFirstFrameObserved(rec, obs, traceId, tray);
        }

        // Subscribe to capture-ended events so the UI can switch to "saving"
        // before muxing/probing/bundle generation complete.
        if (rec.Backend is ICaptureEndedObservableBackend endedObservable)
        {
            endedObservable.CaptureEnded += obs => OnCaptureEnded(rec, obs, traceId, tray);
        }

        // Split A/V backends with a microphone first warm up the audio worker.
        // Subscribe to AudioReady BEFORE starting the backend so a synchronous
        // ready signal cannot be missed, then check IsAudioReady for catch-up.
        if (rec.Backend is IAudioReadyBackend audioReady && rec.Config.AudioRequested)
        {
            audioReady.AudioReady += () => OnAudioReady(rec, traceId, tray);
        }

        // No-microphone backends capable of deferred capture start (WGC
        // continuous) take the configurable countdown path: the helper process is
        // launched now but capture authorization is withheld until the
        // countdown reaches zero, so nothing is captured during the countdown.
        // The authorization completion is pure audit; failures surface through
        // the normal first-frame timeout / natural-exit paths.
        bool useDeferredCountdown = !rec.Config.AudioRequested && rec.Backend is IDeferredCaptureStartBackend;
        bool useOrdinaryFfmpegCountdown = !rec.Config.AudioRequested &&
            !useDeferredCountdown &&
            CaptureBackendSelector.IsFfmpegMp4Backend(rec.BackendType);
        if (rec.Backend is IDeferredCaptureStartBackend deferredObservable)
        {
            deferredObservable.CaptureAuthorizationCompleted += ok => OnCaptureAuthorizationCompleted(rec, ok);
        }

        // Enter preparing: backend initialization (including microphone warmup)
        // has begun, but no REC UI, no elapsed timer, and no user-visible start
        // until credible first-frame evidence arrives.
        rec.State = RecState.preparing;
        BumpStateVersion();

        // Ordinary no-audio FFmpeg must not be started and discarded during
        // the countdown: its first backend.Start happens only at countdown
        // zero. This path still uses the same first-frame and cancellation
        // machinery as audio/deferred recordings.
        if (useOrdinaryFfmpegCountdown)
        {
            tray.SetPreparing(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            BeginOrdinaryFfmpegCountdown(rec, traceId, tray);
            return;
        }

        // Start the backend FIRST to populate CommandArgs,
        // THEN record audit with the actual ffmpeg_args.
        try
        {
            if (useDeferredCountdown)
            {
                // Engine-internal switch (not API-settable): withhold capture
                // authorization until the countdown reaches zero.
                rec.Config.DeferCaptureStart = true;
            }

            lock (rec)
            {
                if (rec.IsFinalized)
                    return;

                rec.BackendStartAtUtc = DateTime.UtcNow;
                _tracer.CaptureStartRequested(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");
                rec.Backend.Start(rec.Config);
                _tracer.CaptureBackendStartReturned(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");
            }

            _audit.Log("recording.started", new
            {
                recording_id = rec.Id,
                output_path = rec.OutputPath,
                backend = rec.BackendType,
                ffmpeg_args = rec.Config.CommandArgs ?? ""
            });

            // After the backend has started, catch the race where AudioReady fired
            // before the subscription above was attached.
            if (rec.Backend is IAudioReadyBackend audioReadyBackend && rec.Config.AudioRequested && audioReadyBackend.IsAudioReady)
            {
                OnAudioReady(rec, traceId, tray);
            }

            // No-microphone deferred-start backends (WGC continuous): the helper
            // process is prepared but not yet authorized to capture. Show
            // preparing UI and run the 3-2-1 countdown; authorization happens
            // when the countdown reaches zero.
            if (useDeferredCountdown && !rec.IsFinalized)
            {
                _audit.Log("recording.capture_backend_prepared", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    awaiting_authorization = true
                });
                tray.SetPreparing(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
                BeginDeferredCountdown(rec, traceId, tray);
            }
            // Split A/V backends with a microphone: show preparing UI and wait
            // for AudioReady before the 3-2-1 countdown.
            else if (rec.Backend is IAudioReadyBackend && rec.Config.AudioRequested)
            {
                if (rec.Microphone)
                    _tracer.MicrophonePrepareStarted(traceId ?? "trace_unknown", rec.Id);
                _audit.Log(rec.Microphone
                    ? "recording.microphone_prepare_started"
                    : "recording.system_audio_prepare_started", new
                {
                    recording_id = rec.Id,
                    device_id = rec.MicrophoneDeviceId ?? "",
                    device_name = rec.MicrophoneDeviceName ?? "",
                    audio_source_kind = AudioSourceKindName(rec.AudioSourceKind),
                    endpoint_id = rec.SystemAudioEndpointId ?? "",
                    endpoint_name = rec.SystemAudioEndpointName ?? ""
                });
                tray.SetPreparing(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            }
            // For other first-frame-observable backends (e.g. no-microphone FFmpeg),
            // show preparing until credible first-frame evidence arrives.
            else if (rec.Backend is IFirstFrameObservableCaptureBackend && !rec.IsFinalized)
            {
                tray.SetPreparing(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            }
            // Non-observable backends cannot wait for evidence.
            else if (!rec.IsFinalized)
            {
                TransitionToRecording(rec, traceId, tray, firstFrameEvidence: null);
            }
        }
        catch (Exception ex)
        {
            BeforeStartFailureForTests?.Invoke(rec, "preparation.backend.start");
            var ownership = TryClaimStartFailure(
                rec,
                error: ex.Message,
                warning: "launch_error: " + ex.Message,
                stopReason: "unexpected_exit");

            if (ownership == StartFailureOwnership.Failed)
            {
                EmitStartFailure(
                    rec,
                    traceId,
                    tray,
                    errorCode: "backend_start_exception",
                    errorType: ex.GetType().Name,
                    stopReason: "unexpected_exit",
                    error: "Recording failed: " + ex.Message,
                    stage: null);
            }
            else if (ownership == StartFailureOwnership.ExistingTerminal && rec.IsFinalized)
            {
                // Preserve the existing non-terminal diagnostic for a backend
                // that finalized itself synchronously and then threw. A Stop-
                // owned cancellation returns StartFailureOwnership.Stop and
                // emits no failed tracer/audit/UI evidence.
                _tracer.CaptureBackendStartFailed(traceId ?? "trace_unknown", rec.Id,
                    rec.BackendType ?? "unknown", "backend_start_exception", ex.GetType().Name);
                _audit.Log("recording.backend_start_exception_after_terminal", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    final_state = rec.State.ToString(),
                    exception_type = ex.GetType().Name
                });
            }
        }
    }

    private void StartScreenshotSeries(Recording rec, string? traceId, ITrayContext tray)
    {
        var config = rec.Config.ScreenshotSeries
            ?? throw new InvalidOperationException("Screenshot-series configuration is missing.");

        var plan = rec.ApprovedCapturePlan
            ?? (_capturePlanFactory ?? CaptureBackendSelector.BuildScreenshotSeriesPlan)(rec.Config);
        ValidateScreenshotSeriesPlan(plan, rec.Config);
        rec.ApprovedCapturePlan = plan;

        var runtime = rec.ScreenshotSeries ??= new ScreenshotSeriesRuntime
        {
            IntervalMs = config.IntervalMs,
            MaxCount = config.MaxCount,
            MaxDurationSeconds = config.MaxDurationSeconds,
            PlannedFrameCount = config.PlannedFrameCount,
            OutputDirectory = rec.OutputPath,
            Status = "preparing"
        };
        runtime.OutputDirectory = rec.OutputPath;
        rec.BackendType = plan.PlannedBackend;
        rec.State = RecState.preparing;
        BumpStateVersion();

        var op = new ScreenshotSeriesOperation();
        if (!_seriesOps.TryAdd(rec.Id, op))
            throw new InvalidOperationException("A screenshot-series worker is already active for this recording.");

        tray.SetPreparing(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
        // Never pass the operation CTS as Task.Run's scheduling token. Stop can
        // cancel before the delegate is scheduled; the worker must still start,
        // observe ownership, and retire the operation deterministically.
        _ = Task.Run(() => RunScreenshotSeriesAsync(rec, op, traceId, tray));
    }

    private async Task RunScreenshotSeriesAsync(
        Recording rec,
        ScreenshotSeriesOperation op,
        string? traceId,
        ITrayContext tray)
    {
        var runtime = rec.ScreenshotSeries!;
        var config = rec.Config.ScreenshotSeries!;
        try
        {
            runtime.StagingDirectory = ScreenshotSeriesArtifacts.CreateStagingDirectory(rec.Id);
            await RunScreenshotSeriesCountdownAsync(rec, op, tray).ConfigureAwait(false);
            op.Cts.Token.ThrowIfCancellationRequested();

            var firstFrameDueTicks = ScreenshotMonotonicNow();
            var frequency = MonotonicFrequencyForTests;
            if (frequency <= 0)
                throw new ScreenshotSeriesFailureException("screenshot_clock_invalid");

            for (int index = 1; index <= config.PlannedFrameCount; index++)
            {
                var offsetMs = (long)(index - 1) * config.IntervalMs;
                long anchorTicks;
                bool durationElapsed;
                lock (rec)
                {
                    if (rec.IsFinalized || op.StopRequested)
                        throw new OperationCanceledException(op.Cts.Token);
                    durationElapsed = index > 1 && IsScreenshotDurationElapsed(runtime, config, frequency);
                    if (durationElapsed)
                    {
                        runtime.NextCaptureDueAtUtc = null;
                        anchorTicks = 0;
                    }
                    else
                    {
                        anchorTicks = runtime.StartedAtUtc.HasValue ? runtime.AnchorTicks : firstFrameDueTicks;
                    }
                }

                if (durationElapsed)
                    break;

                var dueTicks = anchorTicks + (long)((decimal)offsetMs * frequency / 1000m);
                runtime.NextCaptureDueAtUtc = ScreenshotDueAtUtc(dueTicks, frequency);

                await DelayUntilScreenshotTicksAsync(dueTicks, op.Cts.Token).ConfigureAwait(false);
                op.Cts.Token.ThrowIfCancellationRequested();

                long captureClaimTicks = 0;
                DateTime captureClaimAtUtc = default;
                BeforeScreenshotFrameStartClaimForTests?.Invoke(rec, index);
                lock (rec)
                {
                    if (rec.IsFinalized || op.StopRequested)
                        throw new OperationCanceledException(op.Cts.Token);
                    durationElapsed = index > 1 && IsScreenshotDurationElapsed(runtime, config, frequency);
                    if (durationElapsed)
                        runtime.NextCaptureDueAtUtc = null;
                    else
                    {
                        // This is the single ownership claim for the frame.
                        // Lateness starts here, before process launch and
                        // excludes the frame's own capture/encode duration.
                        captureClaimTicks = ScreenshotMonotonicNow();
                        captureClaimAtUtc = UtcNowForTests();
                        op.FrameInFlight = 1;
                    }
                }

                if (durationElapsed)
                    break;

                var tempPath = Path.Combine(runtime.StagingDirectory!, $"frame-{index:0000}.tmp");
                var runner = ScreenshotFrameRunnerFactoryForTests(rec.Config);
                BeforeScreenshotFrameRunnerForTests?.Invoke(rec, index);
                ScreenshotFrameResult result;
                try
                {
                    result = await runner.CaptureAsync(
                        new ScreenshotFrameRequest(
                            rec.Config,
                            tempPath,
                            ScreenshotFrameTimeout,
                            index,
                            rec.ApprovedCapturePlan!.PlannedBackend,
                            rec.ApprovedCapturePlan.CaptureSemantics,
                            rec.ApprovedCapturePlan.SourceKind,
                            rec.ApprovedCapturePlan.TargetIdentity,
                            rec.ApprovedCapturePlan.CoordinateSpace),
                        op.Cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (rec) { op.FrameInFlight = 0; }
                }

                if (!result.Success)
                    throw new ScreenshotSeriesFailureException(result.ErrorCode);

                if (op.StopRequested || op.Cts.IsCancellationRequested)
                {
                    try { File.Delete(tempPath); } catch { }
                    throw new OperationCanceledException(op.Cts.Token);
                }

                if (!ScreenshotSeriesArtifacts.TryValidatePng(tempPath, out var width, out var height, out var size) ||
                    width != rec.Config.Bounds.w || height != rec.Config.Bounds.h)
                    throw new ScreenshotSeriesFailureException("invalid_png_frame");

                var finalName = $"frame-{index:0000}.png";
                var finalPath = Path.Combine(runtime.StagingDirectory!, finalName);
                File.Move(tempPath, finalPath, overwrite: false);
                // The valid PNG is now atomically submitted to the series. The
                // duration intentionally ends here, before hashing and manifest
                // serialization, so it describes capture/encode/validation and
                // file submission rather than unrelated finalization work.
                var capturedTicks = ScreenshotMonotonicNow();
                var capturedAt = UtcNowForTests();
                var frameHash = ScreenshotSeriesArtifacts.Sha256File(finalPath);
                var captureDurationMs = ElapsedScreenshotMilliseconds(
                    captureClaimTicks, capturedTicks, frequency);
                long capturedOffsetMs;
                long latenessMs;
                int frameCount;
                DateTime? nextDueAtUtc;

                lock (rec)
                {
                    // Stop wins until the validated PNG is committed. Once the
                    // frame is committed, the same worker owns finalization and
                    // may publish a truthful partial series.
                    if (runtime.StartedAtUtc == null && (op.StopRequested || rec.IsFinalized))
                    {
                        try { File.Delete(finalPath); } catch { }
                        throw new OperationCanceledException(op.Cts.Token);
                    }

                    if (runtime.StartedAtUtc == null)
                    {
                        // The first successful atomic rename is the timeline
                        // anchor. Capture duration therefore cannot consume any
                        // part of the interval before frame two is scheduled.
                        runtime.StartedAtUtc = capturedAt;
                        runtime.AnchorTicks = capturedTicks;
                        runtime.Status = "capturing";
                        rec.StartedAtUtc = capturedAt;
                        rec.MarkTimelineAnchorTicks = capturedTicks;
                        rec.State = RecState.recording;
                        BumpStateVersion();
                        _audit.Log("recording.started", new
                        {
                            recording_id = rec.Id,
                            mode = ScreenshotSeriesConfig.ModeName,
                            planned_frame_count = runtime.PlannedFrameCount
                        });
                        capturedOffsetMs = 0;
                        latenessMs = 0;
                    }
                    else
                    {
                        capturedOffsetMs = Math.Max(0, (long)((decimal)(capturedTicks - runtime.AnchorTicks) * 1000m / frequency));
                        latenessMs = Math.Max(0, (long)((decimal)Math.Max(0, captureClaimTicks - dueTicks) * 1000m / frequency));
                    }

                    runtime.Frames.Add(new ScreenshotSeriesFrame
                    {
                        Index = index,
                        FileName = finalName,
                        ScheduledOffsetMs = offsetMs,
                        CapturedOffsetMs = capturedOffsetMs,
                        LatenessMs = latenessMs,
                        CaptureDurationMs = captureDurationMs,
                        CaptureStartedAtUtc = captureClaimAtUtc,
                        CompletedAtUtc = capturedAt,
                        CapturedAtUtc = capturedAt,
                        Width = width,
                        Height = height,
                        SizeBytes = size,
                        Sha256 = frameHash
                    });
                    frameCount = runtime.Frames.Count;
                    nextDueAtUtc = index < config.PlannedFrameCount &&
                        !IsScreenshotDurationElapsed(runtime, config, frequency)
                        ? ScreenshotDueAtUtc(runtime.AnchorTicks +
                            (long)((decimal)index * config.IntervalMs * frequency / 1000m), frequency)
                        : null;
                    runtime.NextCaptureDueAtUtc = nextDueAtUtc;
                }

                _audit.Log("recording.frame_captured", new
                {
                    recording_id = rec.Id,
                    frame_index = index,
                    scheduled_offset_ms = offsetMs,
                    captured_offset_ms = capturedOffsetMs,
                    lateness_ms = latenessMs,
                    capture_duration_ms = captureDurationMs,
                    width,
                    height,
                    size_bytes = size
                });
                tray.SetSeriesProgress(CreateRecordingUiPresentation(
                    rec,
                    RecordingUiState.Recording,
                    seriesCapturedFrameCount: frameCount,
                    seriesPlannedFrameCount: runtime.PlannedFrameCount,
                    seriesNextCaptureDueAtUtc: nextDueAtUtc));
            }

            FinishScreenshotSeries(rec, op, tray, "completed", null, null);
        }
        catch (OperationCanceledException)
        {
            FinishScreenshotSeries(rec, op, tray, "cancelled", null, rec.StopReason ?? "user_requested");
        }
        catch (ScreenshotSeriesFailureException ex)
        {
            FinishScreenshotSeries(rec, op, tray, "failed", ex.ErrorCode, null);
        }
        catch
        {
            FinishScreenshotSeries(rec, op, tray, "failed", "series_worker_failed", null);
        }
        finally
        {
            _seriesOps.TryRemove(rec.Id, out _);
            try { op.Cts.Dispose(); } catch { }
            op.Completion.TrySetResult(null);
        }
    }

    private async Task RunScreenshotSeriesCountdownAsync(Recording rec, ScreenshotSeriesOperation op, ITrayContext tray)
    {
        int seconds = rec.CountdownSeconds;
        if (seconds <= 0)
        {
            tray.SetCountdown(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            return;
        }

        lock (rec)
        {
            if (op.StopRequested || rec.IsFinalized)
                throw new OperationCanceledException(op.Cts.Token);
            rec.State = RecState.countdown;
            rec.CountdownStartedAtUtc = UtcNowForTests();
            BumpStateVersion();
        }
        int steps = CountdownSteps ?? seconds;
        for (int remaining = steps; remaining > 0; remaining--)
        {
            op.Cts.Token.ThrowIfCancellationRequested();
            tray.SetCountdown(CreateRecordingUiPresentation(
                rec,
                RecordingUiState.Countdown,
                countdownRemainingSeconds: remaining));
            BeforeScreenshotCountdownStepForTests?.Invoke(rec, remaining);
            await Task.Delay(CountdownInterval, op.Cts.Token).ConfigureAwait(false);
        }
        lock (rec)
        {
            if (op.StopRequested || rec.IsFinalized)
                throw new OperationCanceledException(op.Cts.Token);
            tray.SetCountdown(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            rec.State = RecState.preparing;
            BumpStateVersion();
        }
    }

    private long ScreenshotMonotonicNow()
    {
        long value;
        try { value = MonotonicTimestampProviderForTests(); }
        catch { throw new ScreenshotSeriesFailureException("screenshot_clock_invalid"); }
        if (value < 0)
            throw new ScreenshotSeriesFailureException("screenshot_clock_invalid");
        return value;
    }

    private static long ElapsedScreenshotMilliseconds(long startTicks, long endTicks, long frequency)
    {
        if (endTicks <= startTicks || frequency <= 0)
            return 0;

        // Milliseconds are an integer public contract. Round a positive
        // sub-millisecond capture upward so a real completed frame is never
        // reported as zero duration.
        return Math.Max(1, (long)Math.Ceiling((decimal)(endTicks - startTicks) * 1000m / frequency));
    }

    private DateTime ScreenshotDueAtUtc(long dueTicks, long frequency)
    {
        var remaining = Math.Max(0d, (double)(dueTicks - ScreenshotMonotonicNow()) * 1000d / frequency);
        return UtcNowForTests().AddMilliseconds(remaining);
    }

    private async Task DelayUntilScreenshotTicksAsync(long dueTicks, CancellationToken token)
    {
        if (ScreenshotDelaySchedulerForTests is { } scheduler)
        {
            await scheduler(dueTicks, token).ConfigureAwait(false);
            return;
        }

        while (true)
        {
            var remaining = dueTicks - ScreenshotMonotonicNow();
            if (remaining <= 0) return;
            var ms = (int)Math.Min(100, Math.Max(1, remaining * 1000d / MonotonicFrequencyForTests));
            await Task.Delay(ms, token).ConfigureAwait(false);
        }
    }

    private long ScreenshotDurationDeadlineTicks(
        ScreenshotSeriesRuntime runtime,
        ScreenshotSeriesConfig config,
        long frequency)
    {
        if (!runtime.StartedAtUtc.HasValue || !config.MaxDurationSeconds.HasValue)
            return long.MaxValue;

        return checked(runtime.AnchorTicks +
            (long)((decimal)config.MaxDurationSeconds.Value * frequency));
    }

    private bool IsScreenshotDurationElapsed(
        ScreenshotSeriesRuntime runtime,
        ScreenshotSeriesConfig config,
        long frequency)
    {
        if (!config.MaxDurationSeconds.HasValue || !runtime.StartedAtUtc.HasValue)
            return false;
        return ScreenshotMonotonicNow() >= ScreenshotDurationDeadlineTicks(runtime, config, frequency);
    }

    private void FinishScreenshotSeries(
        Recording rec,
        ScreenshotSeriesOperation op,
        ITrayContext tray,
        string status,
        string? errorCode,
        string? stopReason)
    {
        var runtime = rec.ScreenshotSeries!;
        ScreenshotSeriesFrame[] frameSnapshot;
        string? finalStopReason;
        lock (rec)
        {
            if (rec.IsFinalized || op.FinalizationClaimed) return;
            op.FinalizationClaimed = true;
            runtime.Status = status;
            runtime.ErrorCode = errorCode;
            runtime.StopReason = stopReason ?? rec.StopReason;
            runtime.CompletedAtUtc = DateTime.UtcNow;
            rec.CompletedAtUtc = runtime.CompletedAtUtc;
            rec.Error = errorCode;
            if (stopReason != null) rec.StopReason = stopReason;
            rec.State = RecState.finalizing;
            frameSnapshot = runtime.Frames.ToArray();
            finalStopReason = rec.StopReason;
            BumpStateVersion();
        }

        string terminalStatus = status;
        try
        {
            if (status == "completed" || (status == "cancelled" && frameSnapshot.Length > 0))
            {
                ScreenshotSeriesArtifacts.WriteManifest(rec, runtime, status, errorCode, finalStopReason, frameSnapshot);
                ScreenshotSeriesArtifacts.Publish(rec, runtime, rec.Config.OutputConflictPolicy);
            }
            else
            {
                ScreenshotSeriesArtifacts.DeleteStaging(runtime);
            }
        }
        catch
        {
            terminalStatus = "failed";
            runtime.Status = terminalStatus;
            runtime.ErrorCode = "series_publish_failed";
            ScreenshotSeriesArtifacts.DeleteStaging(runtime);
        }

        lock (rec)
        {
            if (terminalStatus == "completed")
                runtime.Status = "completed";
            rec.Error = runtime.ErrorCode;
            rec.State = terminalStatus switch
            {
                "completed" => RecState.completed,
                "cancelled" => RecState.cancelled,
                _ => RecState.failed
            };
            MarkBundleNotApplicable(rec);
            BumpStateVersion();
            rec.PublishFinalized();
        }

        _audit.Log(terminalStatus == "completed" ? "recording.completed" : terminalStatus == "cancelled" ? "recording.cancelled" : "recording.failed", new
        {
            recording_id = rec.Id,
            mode = ScreenshotSeriesConfig.ModeName,
            frame_count = frameSnapshot.Length,
            planned_frame_count = runtime.PlannedFrameCount,
            error_code = runtime.ErrorCode ?? "",
            stop_reason = rec.StopReason ?? ""
        });
        _audit.Log(terminalStatus == "completed" ? "recording.series_completed" : terminalStatus == "cancelled" ? "recording.series_cancelled" : "recording.series_failed", new
        {
            recording_id = rec.Id,
            frame_count = frameSnapshot.Length,
            planned_frame_count = runtime.PlannedFrameCount,
            error_code = runtime.ErrorCode ?? ""
        });
        _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id,
            status: rec.State.ToString(), errorCode: runtime.ErrorCode, stopReason: rec.StopReason);
        tray.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));
    }

    private sealed class ScreenshotSeriesFailureException : Exception
    {
        public string ErrorCode { get; }
        public ScreenshotSeriesFailureException(string errorCode) => ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "frame_capture_failed" : errorCode;
    }

    private void OnFirstFrameObserved(Recording rec, FirstFrameObservation obs, string? traceId, ITrayContext tray)
    {
        try
        {
            _tracer.CaptureFirstFrameObserved(traceId ?? "trace_unknown", rec.Id,
                new FirstFrameEvidence
                {
                    EvidenceKind = obs.EvidenceKind,
                    FrameNumber = obs.FrameNumber,
                    TotalSizeBytes = obs.TotalSizeBytes,
                    OutTimeUs = obs.OutTimeUs
                });
        }
        catch
        {
            // First-frame diagnostics must never change recording state.
        }

        TransitionToRecording(rec, traceId, tray, obs);
    }

    /// <summary>
    /// Moves a recording from <see cref="RecState.preparing"/> or
    /// <see cref="RecState.countdown"/> to <see cref="RecState.recording"/> exactly once.
    /// Sets <see cref="Recording.StartedAtUtc"/> to the credible-recording time,
    /// starts the optional duration deadline watchdog, and notifies the tray.
    /// Thread-safe against concurrent stop, failure, or natural exit.
    /// </summary>
    private void TransitionToRecording(Recording rec, string? traceId, ITrayContext tray, FirstFrameObservation? firstFrameEvidence)
    {
        lock (rec)
        {
            if (rec.State is not (RecState.preparing or RecState.countdown))
                return;

            long? monotonicAnchor = null;
            try
            {
                var candidate = MonotonicTimestampProviderForTests();
                if (candidate >= 0)
                    monotonicAnchor = candidate;
            }
            catch
            {
                // A missing/invalid anchor is fail-closed for marks. Keep the
                // trusted recording transition intact; AddMark will return the
                // stable first-frame/timeline 409 until a future recording.
            }

            rec.State = RecState.recording;
            // Public wall-clock metadata and the private monotonic mark anchor
            // are established by this same trusted first-frame transition.
            rec.StartedAtUtc = UtcNowForTests();
            rec.MarkTimelineAnchorTicks = monotonicAnchor;
            BumpStateVersion();
        }

        _audit.Log("recording.first_frame_observed", new
        {
            recording_id = rec.Id,
            backend = rec.BackendType,
            evidence_kind = firstFrameEvidence?.EvidenceKind ?? "none",
            frame_number = firstFrameEvidence?.FrameNumber ?? 0,
            out_time_us = firstFrameEvidence?.OutTimeUs ?? 0
        });

        StartDeadlineWatchdog(rec, traceId, tray);
        tray.SetRecording(CreateRecordingUiPresentation(rec, RecordingUiState.Recording));
    }

    private void StartDeadlineWatchdog(Recording rec, string? traceId, ITrayContext tray)
    {
        var duration = rec.DurationSeconds;
        if (duration == null || duration <= 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(duration.Value)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // CaptureEndedAtUtc is the exactly-once gate: the first caller records
            // the tracer/audit/tray transition. Even if a backend event already won
            // the race, the deadline path must still stop the backend and drive
            // finalization so the recording never stalls in an intermediate state.
            TryRecordCaptureEnded(rec, DateTime.UtcNow, 0, "duration_reached", traceId, tray);

            OutputMeta meta;
            int exitCode;
            try
            {
                meta = rec.Backend?.Stop() ?? new OutputMeta();
                exitCode = rec.Backend?.ExitCode ?? -1;
            }
            catch (Exception ex)
            {
                meta = new OutputMeta { StderrLog = "deadline_stop_failed: " + ex };
                exitCode = -1;
            }

            // FinalizeRecording is guarded by rec.IsFinalized, so a race with an
            // explicit Stop(...) or a synchronous natural-exit callback is safe.
            // This ensures the deadline path always leaves the finalizing state.
            FinalizeRecording(rec, meta, exitCode, natural: false, stopReason: "duration_reached", tray);
        });
    }

    /// <summary>
    /// Records the capture-ended transition exactly once per recording.
    /// <see cref="Recording.CaptureEndedAtUtc"/> is the durable gate: the first
    /// caller (deadline, backend event, or manual stop sequence) writes the
    /// tracer/audit/tray transition; later callers observe the timestamp and
    /// return false without producing duplicate events.
    /// </summary>
    private bool TryRecordCaptureEnded(Recording rec, DateTime endedAtUtc, int exitCode, string reason, string? traceId, ITrayContext tray)
    {
        bool transitioned = false;
        lock (rec)
        {
            if (rec.IsFinalized || rec.CaptureEndedAtUtc.HasValue)
                return false;

            if (rec.State is RecState.recording or RecState.stopping or RecState.countdown or RecState.finalizing)
            {
                if (rec.State != RecState.finalizing)
                {
                    rec.State = RecState.finalizing;
                    BumpStateVersion();
                }
                rec.CaptureEndedAtUtc = endedAtUtc;
                transitioned = true;
            }
        }

        if (transitioned)
        {
            _tracer.CaptureEnded(traceId ?? "trace_unknown", rec.Id);
            _audit.Log("recording.capture_ended", new
            {
                recording_id = rec.Id,
                ended_at = Iso(endedAtUtc),
                exit_code = exitCode,
                reason
            });
            tray.SetFinalizing(CreateRecordingUiPresentation(rec, RecordingUiState.Finalizing));
        }

        return transitioned;
    }

    /// <summary>
    private static void NormalizeRecordingCountdown(Recording rec)
    {
        // Production parser assigns both fields. The small reconciliation rule
        // keeps direct test-created Recording instances useful when only the
        // CaptureConfig value is populated.
        int seconds = rec.CountdownSeconds;
        if (seconds == CaptureConfig.DefaultCountdownSeconds &&
            rec.Config.CountdownSeconds != CaptureConfig.DefaultCountdownSeconds)
        {
            seconds = rec.Config.CountdownSeconds;
        }

        seconds = Math.Clamp(seconds, CaptureConfig.MinCountdownSeconds, CaptureConfig.MaxCountdownSeconds);
        rec.CountdownSeconds = seconds;
        rec.Config.CountdownSeconds = seconds;
    }

    private int CountdownStepsFor(Recording rec) => Math.Clamp(
        CountdownSteps ?? rec.CountdownSeconds,
        CaptureConfig.MinCountdownSeconds,
        CaptureConfig.MaxCountdownSeconds);

    private static string CountdownTriggerFor(Recording rec)
    {
        if (rec.Config.AudioRequested)
            return rec.Microphone ? "microphone_ready" : "system_audio_ready";
        if (rec.Backend is IDeferredCaptureStartBackend)
            return "deferred_capture_start";
        return "ordinary_ffmpeg";
    }

    /// <summary>
    /// Invoked when a split A/V backend reports that the microphone or system
    /// audio input is ready. A positive value shows the configurable countdown;
    /// zero keeps the existing immediate video-start path without visible
    /// countdown events.
    /// </summary>
    private CountdownOperation? TryClaimCountdownOperation(
        Recording rec,
        int steps,
        bool visibleCountdown,
        out DateTime? countdownStartedAtUtc)
    {
        countdownStartedAtUtc = null;

        lock (rec)
        {
            if (rec.IsFinalized || rec.State != RecState.preparing)
                return null;

            // The dictionary is consulted and updated while holding the same
            // per-recording lock used by Stop and the start-action gate. This
            // makes AudioReady event/catch-up delivery exactly-once without a
            // last-writer-wins replacement of an existing operation.
            if (_countdownOps.ContainsKey(rec.Id))
                return null;

            if (visibleCountdown)
            {
                rec.State = RecState.countdown;
                rec.CountdownStartedAtUtc = DateTime.UtcNow;
                countdownStartedAtUtc = rec.CountdownStartedAtUtc;
            }

            var op = new CountdownOperation
            {
                Phase = visibleCountdown
                    ? CountdownOperation.PhaseVisibleCountdown
                    : CountdownOperation.PhaseFirstFrameWait
            };
            if (!_countdownOps.TryAdd(rec.Id, op))
                return null;

            BumpStateVersion();
            return op;
        }
    }

    private void OnAudioReady(Recording rec, string? traceId, ITrayContext tray)
    {
        int steps = CountdownStepsFor(rec);
        var op = TryClaimCountdownOperation(rec, steps, visibleCountdown: steps > 0, out var countdownStartedAt);
        if (op == null)
            return;

        if (rec.Microphone)
            _tracer.MicrophoneReady(traceId ?? "trace_unknown", rec.Id);

        if (steps == 0)
        {
            _ = Task.Run(() => RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: false));
            return;
        }

        var visibleCountdownStartedAt = countdownStartedAt ?? DateTime.UtcNow;
        _tracer.CountdownStarted(traceId ?? "trace_unknown", rec.Id);
        _audit.Log("recording.countdown_started", new
        {
            recording_id = rec.Id,
            trigger = CountdownTriggerFor(rec),
            backend = rec.BackendType,
            countdown_seconds = rec.CountdownSeconds,
            audio_source_kind = AudioSourceKindName(rec.AudioSourceKind),
            audio_ready_at = Iso(visibleCountdownStartedAt)
        });

        _ = Task.Run(() => RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: true));
    }

    /// <summary>
    /// Starts the configurable countdown for a no-microphone deferred-start backend
    /// (WGC continuous). The backend process is already prepared but has not
    /// been authorized to capture; authorization is requested when the
    /// countdown reaches zero inside <see cref="RunCountdownAsync"/>.
    /// </summary>
    private void BeginDeferredCountdown(Recording rec, string? traceId, ITrayContext tray)
    {
        int steps = CountdownStepsFor(rec);
        var op = TryClaimCountdownOperation(rec, steps, visibleCountdown: steps > 0, out var countdownStartedAt);
        if (op == null)
            return;

        if (steps == 0)
        {
            _ = RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: false);
            return;
        }

        var visibleCountdownStartedAt = countdownStartedAt ?? DateTime.UtcNow;
        _tracer.CountdownStarted(traceId ?? "trace_unknown", rec.Id);
        _audit.Log("recording.countdown_started", new
        {
            recording_id = rec.Id,
            trigger = "deferred_capture_start",
            backend = rec.BackendType,
            countdown_seconds = rec.CountdownSeconds,
            countdown_started_at = Iso(visibleCountdownStartedAt)
        });

        _ = RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: true);
    }

    private void BeginOrdinaryFfmpegCountdown(Recording rec, string? traceId, ITrayContext tray)
    {
        int steps = CountdownStepsFor(rec);
        var op = TryClaimCountdownOperation(rec, steps, visibleCountdown: steps > 0, out var countdownStartedAt);
        if (op == null)
            return;

        if (steps == 0)
        {
            _ = RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: false, startBackendAtZero: true);
            return;
        }

        var visibleCountdownStartedAt = countdownStartedAt ?? DateTime.UtcNow;
        _tracer.CountdownStarted(traceId ?? "trace_unknown", rec.Id);
        _audit.Log("recording.countdown_started", new
        {
            recording_id = rec.Id,
            trigger = "ordinary_ffmpeg",
            backend = rec.BackendType,
            countdown_seconds = rec.CountdownSeconds,
            countdown_started_at = Iso(visibleCountdownStartedAt)
        });

        _ = RunCountdownAsync(rec, traceId, tray, op, visibleCountdown: true, startBackendAtZero: true);
    }

    /// <summary>
    /// Pure-audit observer for deferred capture authorization completion. The
    /// recording state machine is driven by first-frame evidence, terminal
    /// session events, and the bounded first-frame timeout; this callback must
    /// never change recording state.
    /// </summary>
    private void OnCaptureAuthorizationCompleted(Recording rec, bool authorized)
    {
        try
        {
            _audit.Log(authorized
                    ? "recording.capture_authorization_succeeded"
                    : "recording.capture_authorization_failed",
                new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType
                });
        }
        catch
        {
            // Audit must never affect the recording flow.
        }
    }

    private bool TryStartBackendAtCountdownZero(
        Recording rec,
        string? traceId,
        ITrayContext tray,
        CountdownOperation op)
    {
        try
        {
            return TryClaimAndRunStartAction(rec, op, () =>
            {
                rec.BackendStartAtUtc = DateTime.UtcNow;
                _tracer.CaptureStartRequested(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");
                rec.Backend!.Start(rec.Config);
                _tracer.CaptureBackendStartReturned(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");

                _audit.Log("recording.started", new
                {
                    recording_id = rec.Id,
                    output_path = rec.OutputPath,
                    backend = rec.BackendType,
                    ffmpeg_args = rec.Config.CommandArgs ?? ""
                });
            });
        }
        catch (Exception ex)
        {
            BeforeStartFailureForTests?.Invoke(rec, "countdown.backend.start");
            var ownership = TryClaimStartFailure(
                rec,
                error: ex.Message,
                warning: "launch_error: " + ex.Message,
                stopReason: "unexpected_exit");
            if (ownership != StartFailureOwnership.Failed)
                return false;

            EmitStartFailure(
                rec,
                traceId,
                tray,
                errorCode: "backend_start_exception",
                errorType: ex.GetType().Name,
                stopReason: "unexpected_exit",
                error: "Recording failed: " + ex.Message,
                stage: "backend_start");
            return false;
        }
    }

    /// <summary>
    /// Claims and executes one real start action under the recording's own
    /// monitor. Stop/finalize uses the same monitor: it either wins before this
    /// method claims the action, or waits until the already-started action
    /// returns and then cancels/stops that backend. There is no global lock, so
    /// nested recordings retain independent lifecycle concurrency.
    /// </summary>
    private bool TryClaimAndRunStartAction(
        Recording rec,
        CountdownOperation op,
        Action action)
    {
        lock (rec)
        {
            if (rec.IsFinalized || rec.State is not (RecState.preparing or RecState.countdown))
                return false;

            if (op.StartActionClaimed)
                return false;

            op.StartActionClaimed = true;
            action();
            return !rec.IsFinalized;
        }
    }

    /// <summary>
    /// Attempts to own the failed terminal transition for a startup exception.
    /// The ownership decision and all terminal recording mutations happen under
    /// the same per-recording monitor used by Stop and the start-action gate.
    /// External tracer/audit/tray effects are deliberately emitted only by the
    /// caller that receives <see cref="StartFailureOwnership.Failed"/>.
    /// </summary>
    private StartFailureOwnership TryClaimStartFailure(
        Recording rec,
        string error,
        string warning,
        string stopReason)
    {
        lock (rec)
        {
            // Stop can hold the recording in stopping while it is outside the
            // monitor performing backend teardown. That state is already owned
            // by Stop even though IsFinalized has not been written yet.
            if (rec.State == RecState.stopping ||
                (rec.State == RecState.cancelled && rec.IsFinalized && !string.IsNullOrEmpty(rec.StopReason)))
            {
                return StartFailureOwnership.Stop;
            }

            // Natural exit/finalization or an earlier terminal transition owns
            // the lifecycle already. Never overwrite its state, timestamps,
            // error, warnings, stop reason, or bundle snapshot.
            if (rec.IsFinalized || IsTerminalState(rec.State) || rec.State == RecState.finalizing)
                return StartFailureOwnership.ExistingTerminal;

            MarkBundleNotApplicable(rec);
            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.StopReason = stopReason;
            rec.Error = error;
            rec.Warnings.Add(warning);
            rec.State = RecState.failed;
            BumpStateVersion();
            rec.PublishFinalized();
            return StartFailureOwnership.Failed;
        }
    }

    private void EmitStartFailure(
        Recording rec,
        string? traceId,
        ITrayContext tray,
        string errorCode,
        string errorType,
        string stopReason,
        string error,
        string? stage)
    {
        _tracer.CaptureBackendStartFailed(traceId ?? "trace_unknown", rec.Id,
            rec.BackendType ?? "unknown", errorCode, errorType);
        _tracer.RecordingTerminal(traceId ?? "trace_unknown", rec.Id, status: "failed",
            stopReason: stopReason, errorCode: errorCode);

        _audit.Log("recording.failed", new
        {
            recording_id = rec.Id,
            backend = rec.BackendType,
            error,
            stage = stage ?? ""
        });
        tray.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));
        tray.ShowError(error);
    }

    /// <summary>
    /// Drives the configurable countdown overlay and starts capture when it
    /// reaches zero. A zero-second operation skips all visible countdown UI
    /// and audit events but still uses the same first-frame wait/cancellation
    /// contract.
    /// Keeps the recording in the countdown state until real first-frame evidence is
    /// observed. Uses Task.Delay so the UI thread is never blocked.
    /// </summary>
    private async Task RunCountdownAsync(Recording rec, string? traceId, ITrayContext tray,
        CountdownOperation op, bool visibleCountdown, bool startBackendAtZero = false)
    {
        var ct = op.Cts.Token;
        Action<FirstFrameObservation>? firstFrameHandler = null;
        IFirstFrameObservableCaptureBackend? subscribedObservable = null;

        try
        {
            // A backend may synchronously raise AudioReady from inside its
            // initial Backend.Start() call, which itself is protected by the
            // recording monitor. Yield before any zero-countdown start action
            // so StartVideo/StartCapture exceptions and Stop can contend at
            // the normal per-recording boundary rather than under that
            // reentrant callback stack.
            try
            {
                if (visibleCountdown)
                {
                    for (int remaining = CountdownStepsFor(rec); remaining >= 1; remaining--)
                    {
                        tray.SetCountdown(CreateRecordingUiPresentation(
                            rec,
                            RecordingUiState.Countdown,
                            countdownRemainingSeconds: remaining));
                        await Task.Delay(CountdownInterval, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (rec)
            {
                bool validState = visibleCountdown
                    ? rec.State == RecState.countdown
                    : rec.State == RecState.preparing;
                if (!validState || rec.IsFinalized)
                    return;

                // The visible countdown has completed normally (or the zero
                // path is entering first-frame wait). Transition the operation
                // atomically with the state check so Stop cannot report a
                // completed visible countdown as cancelled.
                Volatile.Write(ref op.Phase, CountdownOperation.PhaseFirstFrameWait);
            }

            if (visibleCountdown)
            {
                _audit.Log("recording.countdown_completed", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    trigger = CountdownTriggerFor(rec),
                    countdown_seconds = rec.CountdownSeconds
                });

                tray.SetCountdown(CreateRecordingUiPresentation(rec, RecordingUiState.Preparing));
            }

            // Late-terminal guard: a stop/finalize that won the race after the
            // phase transition must not trigger a late StartVideo/StartCapture
            // or further UI updates from this operation.
            lock (rec)
            {
                bool validState = visibleCountdown
                    ? rec.State == RecState.countdown
                    : rec.State == RecState.preparing;
                if (!validState || rec.IsFinalized)
                    return;
            }

            if (startBackendAtZero)
            {
                BeforeStartActionForTests?.Invoke(rec, "backend.start");
                if (!TryStartBackendAtCountdownZero(rec, traceId, tray, op))
                    return;
            }
            else if (rec.Backend is IAudioReadyBackend audioReady)
            {
                try
                {
                    BeforeStartActionForTests?.Invoke(rec, "start_video");
                    if (!TryClaimAndRunStartAction(rec, op, audioReady.StartVideo))
                        return;
                }
                catch (Exception ex)
                {
                    var error = "Failed to start video capture: " + ex.Message;
                    BeforeStartFailureForTests?.Invoke(rec, "countdown.start_video");
                    var ownership = TryClaimStartFailure(
                        rec,
                        error,
                        warning: "video_start_failed: " + ex.Message,
                        stopReason: "video_start_failed");
                    if (ownership != StartFailureOwnership.Failed)
                        return;

                    EmitStartFailure(
                        rec,
                        traceId,
                        tray,
                        errorCode: "video_start_failed",
                        errorType: ex.GetType().Name,
                        stopReason: "video_start_failed",
                        error: error,
                        stage: "video_start");
                    return;
                }
            }
            else if (rec.Backend is IDeferredCaptureStartBackend deferred)
            {
                // Countdown reached zero: authorize the prepared helper to start
                // capturing now. Nothing was captured during the countdown.
                try
                {
                    BeforeStartActionForTests?.Invoke(rec, "start_capture");
                    if (!TryClaimAndRunStartAction(rec, op, () =>
                    {
                        // This audit is deliberately adjacent to the real
                        // authorization call and occurs only after the start
                        // action claim has been acquired. A Stop-before-claim
                        // path therefore cannot claim that authorization was
                        // requested when StartCapture was never invoked.
                        _audit.Log("recording.capture_authorization_requested", new
                        {
                            recording_id = rec.Id,
                            backend = rec.BackendType
                        });
                        deferred.StartCapture();
                    }))
                        return;
                }
                catch (Exception ex)
                {
                    var error = "Failed to authorize capture start: " + ex.Message;
                    BeforeStartFailureForTests?.Invoke(rec, "countdown.start_capture");
                    var ownership = TryClaimStartFailure(
                        rec,
                        error,
                        warning: "capture_start_failed: " + ex.Message,
                        stopReason: "capture_start_failed");
                    if (ownership != StartFailureOwnership.Failed)
                        return;

                    EmitStartFailure(
                        rec,
                        traceId,
                        tray,
                        errorCode: "capture_start_failed",
                        errorType: ex.GetType().Name,
                        stopReason: "capture_start_failed",
                        error: error,
                        stage: "capture_start");
                    return;
                }
            }

            // Preserve the established fallback for test/custom backends that
            // do not expose first-frame evidence. Observable production paths
            // remain anchored to a credible frame; a non-observable backend is
            // considered recording once its real start action returns.
            if (rec.Backend is not IFirstFrameObservableCaptureBackend)
            {
                TransitionToRecording(rec, traceId, tray, firstFrameEvidence: null);
                return;
            }

            // Wait for real first-frame evidence before showing REC. If no first frame
            // arrives within the bounded timeout, the recording has failed.
            var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            firstFrameHandler = _ =>
            {
                if (rec.Backend is IFirstFrameObservableCaptureBackend observable)
                    observable.FirstFrameObserved -= firstFrameHandler;
                firstFrameTcs.TrySetResult(true);
            };

            if (rec.Backend is IFirstFrameObservableCaptureBackend frameObservable)
            {
                frameObservable.FirstFrameObserved += firstFrameHandler;
                subscribedObservable = frameObservable;
            }

            // If the first frame was already observed synchronously during StartVideo,
            // we are already recording.
            lock (rec)
            {
                if (rec.State == RecState.recording)
                    firstFrameTcs.TrySetResult(true);
            }

            Task timeoutTask;
            try
            {
                timeoutTask = Task.Delay(FirstFrameTimeout, ct);
            }
            catch (ObjectDisposedException)
            {
                // The operation token was concurrently cancelled and disposed by
                // a stop that won the race. The stop path owns terminal handling.
                return;
            }
            Task completed;
            try
            {
                completed = await Task.WhenAny(firstFrameTcs.Task, timeoutTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (completed == timeoutTask)
            {
                // A cancelled timeout task means a stop/finalize cancelled the
                // wait promptly; that path owns the terminal state and this
                // operation must not emit a timeout failure, audit, or UI update.
                if (timeoutTask.IsCanceled || ct.IsCancellationRequested)
                    return;

                // Timeout: clean up and fail the recording.
                lock (rec)
                {
                    if (rec.IsFinalized || rec.State is not (RecState.countdown or RecState.preparing))
                        return;

                    MarkBundleNotApplicable(rec);
                    rec.CompletedAtUtc = DateTime.UtcNow;
                    rec.Error = "First video frame was not observed within the timeout.";
                    rec.Warnings.Add("first_frame_timeout");
                    rec.State = RecState.failed;
                    BumpStateVersion();
                }
                _tracer.RecordingTerminal(traceId ?? "trace_unknown", rec.Id, status: "failed",
                    stopReason: "first_frame_timeout", errorCode: "first_frame_timeout");
                _audit.Log("recording.failed", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    error = rec.Error,
                    stage = "first_frame_wait"
                });
                try { rec.Backend?.Cancel(); } catch { }
                tray.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));
                tray.ShowError(rec.Error);
                return;
            }

            // First frame observed; OnFirstFrameObserved has already transitioned to recording.
        }
        finally
        {
            // Sole-owner cleanup, executed on EVERY exit path (success,
            // synchronous first-frame catch-up, timeout, authorization failure,
            // stop, natural terminal, start exception, disposal):
            // 1. detach the local first-frame handler (idempotent),
            // 2. mark the operation retired and unregister it so no later
            //    CancelCountdown can reach the CTS,
            // 3. dispose the CTS exactly once. Cancellation paths only ever
            //    call Cancel on an operation they found in the registry and
            //    tolerate ObjectDisposedException, so this ownership protocol
            //    never disposes a CTS under an active canceller's feet.
            if (firstFrameHandler != null && subscribedObservable != null)
            {
                try { subscribedObservable.FirstFrameObserved -= firstFrameHandler; } catch { }
            }

            Volatile.Write(ref op.Phase, CountdownOperation.PhaseRetired);
            _countdownOps.TryRemove(rec.Id, out _);
            try { op.Cts.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Invoked when the actual screen capture ends (before muxing/probing).
    /// Switches the recording to <see cref="RecState.finalizing"/> and updates
    /// the tray so the REC border/timer disappears immediately. Delegates to
    /// <see cref="TryRecordCaptureEnded"/> so the tracer/audit/tray transition
    /// is recorded exactly once per recording.
    /// </summary>
    private void OnCaptureEnded(Recording rec, CaptureEndedObservation obs, string? traceId, ITrayContext tray)
    {
        TryRecordCaptureEnded(rec, obs.EndedAtUtc, obs.ExitCode, obs.Reason, traceId, tray);
    }

    private void FinalizeRecording(Recording rec, OutputMeta meta, int exitCode, bool natural, string? stopReason, ITrayContext tray)
    {
        CancelCountdown(rec.Id);
        RecordingBundleRequest? bundleRequest = null;
        bool finalizationSuccess = false;
        lock (rec)
        {
            if (rec.IsFinalized)
                return;

            rec.AudioContinuityStatus = meta.AudioContinuityStatus;

            // If a user-initiated stop is already in flight (rec.StopReason set by Stop(...)),
            // treat the backend's natural-exit callback as part of that explicit stop. This
            // prevents a race where the natural-exit callback would otherwise mark a short,
            // user-stopped output as failed due to the planned-duration range check.
            bool treatAsUserStop = natural && !string.IsNullOrEmpty(rec.StopReason);
            if (treatAsUserStop)
            {
                natural = false;
                stopReason = rec.StopReason;
            }

            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.ExitCode = exitCode;
            rec.LastMeta = meta;

            if (!natural)
            {
                rec.StopReason = NormalizeStopReason(stopReason);
                _audit.Log("recording.stopped", new
                {
                    recording_id = rec.Id,
                    reason = rec.StopReason
                });
            }
            // For natural exits, delay setting StopReason until after success/failure is known:
            // success -> duration_reached, failure -> unexpected_exit.

            if (!string.IsNullOrEmpty(meta.StderrLog))
            {
                int start = Math.Max(0, meta.StderrLog.Length - 1000);
                rec.StderrExcerpt = meta.StderrLog.Substring(start);
            }

            var expected = rec.DurationSeconds ?? 0;
            long minSize = 512;
            bool fileOk = meta.SizeBytes > minSize;
            bool durationOk = meta.DurationSeconds > 0;
            // Duration range check only applies to natural completions. Explicit user/agent
            // stops may legitimately be much shorter than the planned duration.
            bool rangeOk = !natural || expected == 0 || (meta.DurationSeconds >= expected * 0.3 && meta.DurationSeconds <= expected * 1.5);
            bool exitOk = exitCode == 0;

            // Keep recordings created by older direct callers compatible with
            // the legacy Recording.Microphone field. Product parsing already
            // populates both fields; this bridge only matters for test and
            // embedding seams that set the legacy field after construction.
            if (rec.Microphone)
                rec.Config.Microphone = true;
            rec.Config.NormalizeAudioSource();
            if (rec.AudioSourceKind == AudioCaptureSourceKind.None)
                rec.AudioSourceKind = rec.Config.AudioSourceKind;

            bool audioRequested = rec.Config.AudioRequested;
            bool audioOk = !audioRequested ||
                           (rec.Config.IsMicrophone
                               ? string.Equals(meta.AudioStatus, "recorded", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(meta.AudioStatus, "lost", StringComparison.OrdinalIgnoreCase)
                               : string.Equals(meta.AudioStatus, "system_loopback_recorded", StringComparison.OrdinalIgnoreCase));
            bool wgcContinuousOutputValidationFailed =
                IsWgcContinuousBackend(rec.BackendType) &&
                IsWgcContinuousOutputValidationFailure(meta.StopReason);

            // A stable helper-declared audio failure can never be a successful
            // recording, even when the probed temp files look healthy and the
            // audio status is a recoverable-looking "lost". The helper's own
            // terminal verdict takes precedence over file heuristics.
            if (audioRequested && !string.IsNullOrEmpty(meta.AudioHelperErrorCode))
                audioOk = false;

            bool success = fileOk && durationOk && exitOk && rangeOk && audioOk &&
                           !wgcContinuousOutputValidationFailed;
            if (!success)
            {
                if (!fileOk) rec.Warnings.Add($"empty_output: file size {meta.SizeBytes} bytes < {minSize}");
                if (!durationOk) rec.Warnings.Add($"zero_duration: ffprobe returned duration=0");
                if (!rangeOk && expected > 0) rec.Warnings.Add($"duration_out_of_range: expected ~{expected}s got {meta.DurationSeconds:F1}s");
                if (!exitOk) rec.Warnings.Add($"non_zero_exit: ffmpeg exit_code={exitCode}");
                if (!audioOk) rec.Warnings.Add($"{AudioSourceKindName(rec.AudioSourceKind)}_audio_failed: audio_status={meta.AudioStatus ?? "unknown"}");
            }

            // Merge backend-produced warnings (e.g. microphone failure evidence)
            // into the recording so they are visible to API consumers.
            if (meta.Warnings is { Length: > 0 })
            {
                foreach (var w in meta.Warnings)
                {
                    if (!rec.Warnings.Contains(w))
                        rec.Warnings.Add(w);
                }
            }

            if (success)
            {
                finalizationSuccess = true;
                if (natural)
                    rec.StopReason = "duration_reached";

                // Decide bundle eligibility and snapshot BEFORE publishing the completed
                // state. Long-polling waiters must never observe completed + pending.
                bool bundleEligible = TryPrepareBundleGeneration(rec, meta, out var req);
                bundleRequest = req;
                rec.BundleSnapshot = bundleEligible
                    ? RecordingBundleSnapshot.Generating(DeriveBundlePath(req!.MediaPath))
                    : RecordingBundleSnapshot.NotApplicable();

                rec.State = RecState.completed;
                TraceEncoderSelection(rec, meta);
                _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "completed", stopReason: rec.StopReason);
                BumpStateVersion();
                _audit.Log("recording.completed", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    stop_reason = rec.StopReason,
                    duration_seconds = meta.DurationSeconds,
                    size_bytes = meta.SizeBytes,
                    container = meta.Container ?? "mp4",
                    codec = meta.Codec ?? "h264",
                    capture_method = meta.CaptureMethod ?? "",
                    encoder_mode = meta.VideoEncoderMode ?? "",
                    encoder_selection_reason = meta.VideoEncoderSelectionReason ?? "",
                    width = meta.Width,
                    height = meta.Height,
                    ffmpeg_exit_code = exitCode,
                    audio_microphone = rec.Microphone,
                    audio_source_kind = AudioSourceKindName(rec.AudioSourceKind),
                    audio_endpoint_id = rec.SystemAudioEndpointId ?? "",
                    audio_endpoint_name = rec.SystemAudioEndpointName ?? "",
                    audio_status = AudioStatusFor(rec, meta),
                    audio_continuity_status = AudioContinuityFor(rec, meta),
                    audio_capture_strategy = meta.AudioCaptureStrategy ?? "",
                    audio_pair_evidence = meta.AudioPairEvidence ?? "",
                    audio_auto_hfp_pair_status = meta.AudioAutoHfpPairStatus ?? "",
                    audio_auto_hfp_pair_result_code = meta.AudioAutoHfpPairResultCode ?? "",
                    audio_auto_hfp_pair_transport_classification = meta.AudioAutoHfpPairTransportClassification ?? "",
                    audio_helper_failure_reason = meta.AudioHelperFailureReason ?? "",
                    audio_helper_failure_stage = meta.AudioHelperFailureStage ?? "",
                    audio_helper_failure_hresult = meta.AudioHelperFailureHresult ?? "",
                    audio_render_prime_ready_ms = meta.AudioRenderPrimeReadyMs,
                    audio_estimated_gap_ms = meta.AudioEstimatedGapMs,
                    audio_max_estimated_gap_ms = meta.AudioMaxEstimatedGapMs,
                    audio_recovery_count = meta.AudioRecoveryCount,
                    audio_recovery_attempts = meta.AudioRecoveryAttempts,
                    audio_gap_filled_ms = meta.AudioGapFilledMs,
                    audio_discontinuity_count = meta.AudioDiscontinuityCount,
                    audio_qpc_outlier_count = meta.AudioQpcOutlierCount
                });
            }
            else
            {
                if (natural)
                {
                    rec.StopReason = IsWgcContinuousBackend(rec.BackendType) &&
                                      (IsWgcLifecycleFailure(meta.StopReason) ||
                                       IsWgcContinuousOutputValidationFailure(meta.StopReason))
                        ? meta.StopReason
                        : "unexpected_exit";
                }
                var stableErrorCode = ResolveTerminalErrorCode(rec.BackendType, rec.AudioSourceKind, meta, exitCode, fileOk, durationOk, rangeOk, exitOk, natural);
                rec.Error = stableErrorCode;
                rec.BundleSnapshot = RecordingBundleSnapshot.NotApplicable();
                rec.State = RecState.failed;
                TraceEncoderSelection(rec, meta);
                _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "failed", stopReason: rec.StopReason, errorCode: stableErrorCode);
                BumpStateVersion();
                _audit.Log("recording.failed", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    error = rec.Error,
                    stop_reason = rec.StopReason,
                    container = meta.Container ?? "mp4",
                    codec = meta.Codec ?? "h264",
                    capture_method = meta.CaptureMethod ?? "",
                    encoder_mode = meta.VideoEncoderMode ?? "",
                    encoder_selection_reason = meta.VideoEncoderSelectionReason ?? "",
                    stage = meta.Stage ?? "",
                    hresult = meta.Hresult ?? "",
                    ffmpeg_exit_code = exitCode,
                    size_bytes = meta.SizeBytes,
                    duration_seconds = meta.DurationSeconds,
                    stderr_excerpt = rec.StderrExcerpt ?? "",
                    audio_microphone = rec.Microphone,
                    audio_source_kind = AudioSourceKindName(rec.AudioSourceKind),
                    audio_endpoint_id = rec.SystemAudioEndpointId ?? "",
                    audio_endpoint_name = rec.SystemAudioEndpointName ?? "",
                    audio_status = AudioStatusFor(rec, meta),
                    audio_continuity_status = AudioContinuityFor(rec, meta),
                    audio_capture_strategy = meta.AudioCaptureStrategy ?? "",
                    audio_pair_evidence = meta.AudioPairEvidence ?? "",
                    audio_auto_hfp_pair_status = meta.AudioAutoHfpPairStatus ?? "",
                    audio_auto_hfp_pair_result_code = meta.AudioAutoHfpPairResultCode ?? "",
                    audio_auto_hfp_pair_transport_classification = meta.AudioAutoHfpPairTransportClassification ?? "",
                    audio_helper_failure_reason = meta.AudioHelperFailureReason ?? "",
                    audio_helper_failure_stage = meta.AudioHelperFailureStage ?? "",
                    audio_helper_failure_hresult = meta.AudioHelperFailureHresult ?? "",
                    audio_render_prime_ready_ms = meta.AudioRenderPrimeReadyMs,
                    audio_estimated_gap_ms = meta.AudioEstimatedGapMs,
                    audio_max_estimated_gap_ms = meta.AudioMaxEstimatedGapMs,
                    audio_recovery_count = meta.AudioRecoveryCount,
                    audio_recovery_attempts = meta.AudioRecoveryAttempts,
                    audio_gap_filled_ms = meta.AudioGapFilledMs,
                    audio_discontinuity_count = meta.AudioDiscontinuityCount,
                    audio_qpc_outlier_count = meta.AudioQpcOutlierCount
                });
            }

            rec.PublishFinalized();
        }

        _tracer.FinalizationCompleted(GetTraceIdForRecording(rec.Id), rec.Id, finalizationSuccess);

        if (bundleRequest != null)
        {
            _ = Task.Run(() => RunBundleGenerationAsync(rec, bundleRequest));
        }

        tray.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));

        // The idle transition closes the REC/floating controls first. The
        // notifier then applies the tray host's language and bubble policy,
        // producing exactly one local message for native lifecycle failures.
        if (!finalizationSuccess &&
            IsWgcContinuousBackend(rec.BackendType) &&
            IsWgcLifecycleFailure(rec.StopReason) &&
            string.Equals(rec.StopReason, meta.StopReason, StringComparison.Ordinal))
        {
            if (tray is IRecordingFailureNotifier notifier)
                notifier.ShowRecordingFailure(rec.Id, meta.StopReason!);
            else
                tray.ShowError("Recording failed: " + meta.StopReason);
        }

        // System-loopback terminal failures are reported by the app-owned
        // notification surface after REC/floating controls are closed. Keep
        // this separate from user stops, pre-confirmation failures, and normal
        // completion; no tray balloon is used for this product path.
        if (!finalizationSuccess &&
            natural &&
            rec.StartedAtUtc != default &&
            rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback &&
            !(IsWgcContinuousBackend(rec.BackendType) && IsWgcLifecycleFailure(rec.StopReason)) &&
            IsTerminalSystemAudioFailure(meta.AudioHelperErrorCode))
        {
            if (tray is IRecordingFailureNotifier notifier)
                notifier.ShowRecordingFailure(rec.Id, "audio_capture_discontinuous");
            else
                _audit.Log("recording_failure_notification.unavailable", new
                {
                    recording_id = rec.Id,
                    reason_code = "audio_capture_discontinuous",
                    host_mode = tray.HostMode
                });
        }
    }

    private void TraceEncoderSelection(Recording rec, OutputMeta meta)
    {
        if (string.IsNullOrWhiteSpace(meta.VideoEncoderMode) ||
            string.IsNullOrWhiteSpace(meta.VideoEncoderSelectionReason) ||
            _tracer is not IEncoderSelectionPerformanceTracer encoderTracer)
        {
            return;
        }

        try
        {
            encoderTracer.EncoderSelected(
                GetTraceIdForRecording(rec.Id),
                rec.Id,
                meta.VideoEncoderMode,
                meta.VideoEncoderSelectionReason);
        }
        catch
        {
            // Encoder diagnostics must never change recording finalization.
        }
    }

    private static bool IsWgcLifecycleFailure(string? reason) => reason is
        "window_closed" or "window_minimized" or "size_changed";

    private static bool IsWgcContinuousOutputValidationFailure(string? reason) =>
        string.Equals(reason, "output_validation_failed", StringComparison.Ordinal);

    private static bool IsWgcContinuousBackend(string? backendType) =>
        string.Equals(backendType, "wgc-continuous", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalSystemAudioFailure(string? errorCode) => errorCode is
        "audio_capture_discontinuous" or
        "audio_capture_stalled" or
        "audio_capture_error" or
        "audio_write_failure" or
        "audio_helper_runtime_failure" or
        "audio_helper_failure";

    /// <summary>
    /// Cancels and disposes the countdown timer for a recording, if one is running.
    /// Called on stop, failure, or terminal finalization to prevent late StartVideo calls.
    /// </summary>
    /// <summary>
    /// Cancels the countdown-plus-first-frame-wait operation for a recording, if
    /// one is still registered. Called on stop, failure, or terminal
    /// finalization. Audits <c>recording.countdown_cancelled</c> at most once
    /// per operation and only when the visible 3-2-1 countdown was truly in
    /// flight; cancelling the post-zero first-frame wait is prompt but silent.
    /// Never disposes the CTS: the owning <see cref="RunCountdownAsync"/>
    /// disposes it after its final consumer exits.
    /// </summary>
    private void CancelCountdown(string recordingId)
    {
        if (!_countdownOps.TryGetValue(recordingId, out var op))
            return;

        // Snapshot the phase before cancelling. The phase transition to
        // first-frame-wait happens under the recording lock together with the
        // countdown-state check, so a Stop can never observe a stale
        // visible-countdown phase for an already-completed countdown.
        bool wasVisibleCountdown =
            Volatile.Read(ref op.Phase) == CountdownOperation.PhaseVisibleCountdown;

        try { op.Cts.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The owning operation retired and disposed the source between our
            // registry lookup and the cancel; nothing left to do.
            return;
        }
        catch { /* best effort */ }

        if (wasVisibleCountdown &&
            Interlocked.CompareExchange(ref op.CancelAuditEmitted, 1, 0) == 0)
        {
            _recs.TryGetValue(recordingId, out var rec);
            _audit.Log("recording.countdown_cancelled", new
            {
                recording_id = recordingId,
                backend = rec?.BackendType ?? "unknown",
                trigger = rec == null ? "unknown" : CountdownTriggerFor(rec),
                countdown_seconds = rec?.CountdownSeconds ?? CaptureConfig.DefaultCountdownSeconds
            });
        }
    }

    private static string DeriveBundlePath(string mediaPath)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, stem + ".bundle");
    }

    private bool TryPrepareBundleGeneration(Recording rec, OutputMeta meta, out RecordingBundleRequest request)
    {
        request = null!;

        // Bundle is only for successful FFmpeg MP4 recordings.
        bool isFfmpegMp4 = CaptureBackendSelector.IsFfmpegMp4Backend(rec.BackendType) &&
                           string.Equals(meta.Container ?? "mp4", "mp4", StringComparison.Ordinal);

        if (_bundleGenerator == null || !isFfmpegMp4)
            return false;

        // Exactly-once guard: natural exit and Stop() may both reach here.
        if (Interlocked.CompareExchange(ref rec.BundleGenerationStarted, 1, 0) != 0)
            return false;

        request = new RecordingBundleRequest(
            recordingId: rec.Id,
            confirmationId: rec.ConfirmationId,
            sourceType: rec.SourceType,
            sourceTitle: rec.SourceTitle,
            sourceBounds: rec.Config.Bounds,
            coordinateSpace: "virtual_screen",
            startedAtUtc: rec.StartedAtUtc,
            completedAtUtc: rec.CompletedAtUtc ?? DateTime.UtcNow,
            requestedDurationSeconds: rec.DurationSeconds,
            actualDurationSeconds: meta.DurationSeconds,
            fps: meta.Fps == 0 ? rec.Config.Fps : meta.Fps,
            backend: rec.BackendType,
            stopReason: rec.StopReason ?? "duration_reached",
            audioMicrophone: rec.Microphone,
            audioStatus: AudioStatusFor(rec, meta),
            audioContinuityStatus: AudioContinuityFor(rec, meta),
            audioDeviceId: rec.MicrophoneDeviceId ?? rec.SystemAudioEndpointId,
            audioLostAtMs: meta.AudioLostAtMs,
            nestedRole: rec.NestedRole,
            nestedSessionId: rec.NestedSessionId,
            parentRecordingId: rec.ParentRecordingId,
            mediaPath: meta.OutputPath ?? rec.OutputPath,
            container: meta.Container ?? "mp4",
            codec: meta.Codec ?? "h264",
            width: meta.Width,
            height: meta.Height,
            audioSourceKind: AudioSourceKindName(rec.AudioSourceKind),
            audioDeviceName: rec.MicrophoneDeviceName ?? rec.SystemAudioEndpointName,
            marks: rec.SnapshotMarks());
        return true;
    }

    private async Task RunBundleGenerationAsync(Recording rec, RecordingBundleRequest request)
    {
        RecordingBundleGenerationResult result;
        try
        {
            result = await _bundleGenerator!.GenerateAsync(request);
        }
        catch (OperationCanceledException)
        {
            result = RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed, "cancelled");
        }
        catch (Exception ex)
        {
            result = RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed, ex.Message);
        }

        lock (rec)
        {
            rec.BundleSnapshot = result.Success
                ? RecordingBundleSnapshot.Ready(result.BundlePath!, BuildBundleContents(result.BundlePath!))
                : RecordingBundleSnapshot.Failed(DeriveBundlePath(request.MediaPath), result.ErrorCode!);
        }
        BumpStateVersion();

        if (result.Success)
        {
            LogBundleReady(rec, result.BundlePath!);
        }
        else
        {
            LogBundleFailed(rec, result.ErrorCode!);
        }
    }

    private static IReadOnlyList<RecordingBundleContentItem> BuildBundleContents(string bundlePath)
    {
        var contents = new List<RecordingBundleContentItem>
        {
            new("metadata.json", "application/json", SafeSize(Path.Combine(bundlePath, "metadata.json"))),
            new("thumbnail.jpg", "image/jpeg", SafeSize(Path.Combine(bundlePath, "thumbnail.jpg"))),
            new("first_frame.png", "image/png", SafeSize(Path.Combine(bundlePath, "first_frame.png"))),
            new("last_frame.png", "image/png", SafeSize(Path.Combine(bundlePath, "last_frame.png"))),
            new("marks.json", "application/json", SafeSize(Path.Combine(bundlePath, "marks.json")))
        };
        return contents;
    }

    private void LogBundleReady(Recording rec, string bundlePath)
    {
        _audit.Log("recording.bundle_ready", new
        {
            recording_id = rec.Id,
            confirmation_id = rec.ConfirmationId ?? "",
            bundle_path = bundlePath
        });
    }

    private void LogBundleFailed(Recording rec, string errorCode)
    {
        DiagnosticLog.Write("recording.bundle_failed", rec.Id, errorCode);
        _audit.Log("recording.bundle_failed", new
        {
            recording_id = rec.Id,
            confirmation_id = rec.ConfirmationId ?? "",
            error_code = errorCode
        });
    }

    public object Stop(string id, string reason)
    {
        var rec = Get(id);
        BeforeStopForTests?.Invoke(rec);
        if (rec.IsScreenshotSeries)
            return StopScreenshotSeries(rec, reason);

        bool enteredStopping = false;

        lock (rec)
        {
            // Terminal states are idempotent: do not touch state, error, warnings,
            // stop reason, backend, or audit events.
            if (IsTerminalState(rec.State))
                return BuildStopResponse(rec);

            // If capture has already ended and finalization is in progress,
            // do not restart the stop sequence; the natural-exit path will complete.
            if (rec.State == RecState.finalizing)
                return BuildStoppingResponse(rec);

            // First explicit stop request becomes the owner. Subsequent concurrent
            // requests see the stopping state and return immediately without calling
            // backend.Stop() again or overwriting the first stop reason.
            if (rec.State == RecState.stopping)
                return BuildStoppingResponse(rec);

            // If the recording has not reached active capture yet, cancel it instead
            // of finalizing. This avoids starting a video worker or producing output
            // for a recording that never really began.
            if (rec.State is RecState.preparing or RecState.countdown)
            {
                rec.State = RecState.cancelled;
                rec.StopReason = NormalizeStopReason(reason);
                rec.CompletedAtUtc = DateTime.UtcNow;
                MarkBundleNotApplicable(rec);
                BumpStateVersion();
                rec.PublishFinalized();
            }
            else
            {
                rec.State = RecState.stopping;
                rec.StopReason = NormalizeStopReason(reason);
                enteredStopping = true;
                BumpStateVersion();
            }
        }

        if (enteredStopping)
            _tray?.SetStopping(CreateRecordingUiPresentation(rec, RecordingUiState.Stopping));

        CancelCountdown(rec.Id);

        if (rec.State == RecState.cancelled)
        {
            _audit.Log("recording.cancelled", new { recording_id = rec.Id, reason = rec.StopReason });
            // Cancel the backend first so any synchronous first-frame observation
            // emitted during teardown can still be traced before the terminal tombstone
            // is recorded.
            try { rec.Backend?.Cancel(); } catch { }
            _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "cancelled", stopReason: rec.StopReason);
            _tray!.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));
            return BuildStopResponse(rec);
        }

        _audit.Log("recording.stopping", new { recording_id = rec.Id, reason = rec.StopReason });

        var meta = rec.Backend?.Stop() ?? new OutputMeta();
        int exitCode = rec.Backend?.ExitCode ?? -1;

        FinalizeRecording(rec, meta, exitCode, natural: false, stopReason: rec.StopReason, _tray!);
        return BuildStopResponse(rec, meta);
    }

    private object StopScreenshotSeries(Recording rec, string reason)
    {
        ScreenshotSeriesOperation? op;
        bool cancelledWithoutOperation = false;
        lock (rec)
        {
            if (IsTerminalState(rec.State))
                return BuildStopResponse(rec);
            if (rec.State == RecState.finalizing)
                return BuildStoppingResponse(rec);
            if (!_seriesOps.TryGetValue(rec.Id, out op))
            {
                rec.State = RecState.cancelled;
                rec.StopReason = NormalizeStopReason(reason);
                rec.CompletedAtUtc = DateTime.UtcNow;
                rec.ScreenshotSeries!.Status = "cancelled";
                rec.ScreenshotSeries.StopReason = rec.StopReason;
                rec.ScreenshotSeries.CompletedAtUtc = rec.CompletedAtUtc;
                MarkBundleNotApplicable(rec);
                BumpStateVersion();
                rec.PublishFinalized();
                cancelledWithoutOperation = true;
            }
            else if (rec.State == RecState.stopping)
                return BuildStoppingResponse(rec);
            else
            {
                rec.State = RecState.stopping;
                rec.StopReason = NormalizeStopReason(reason);
                op.StopRequested = true;
                BumpStateVersion();
            }
        }

        if (cancelledWithoutOperation)
        {
            _audit.Log("recording.cancelled", new { recording_id = rec.Id, mode = ScreenshotSeriesConfig.ModeName, reason = rec.StopReason });
            _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "cancelled", stopReason: rec.StopReason);
            _tray?.SetIdle(CreateRecordingUiPresentation(rec, RecordingUiState.Idle));
            return BuildStopResponse(rec);
        }

        try { op!.Cts.Cancel(); } catch { }
        try { op!.Task?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        return IsTerminalState(rec.State) ? BuildStopResponse(rec) : BuildStoppingResponse(rec);
    }

    private object BuildStopResponse(Recording rec, OutputMeta? meta = null)
    {
        if (rec.IsScreenshotSeries)
            return BuildScreenshotSeriesResponse(rec);

        var m = meta ?? rec.LastMeta;
        if (m == null)
            m = FfmpegCaptureBackend.Probe(rec.OutputPath);

        return new
        {
            recording_id = rec.Id,
            mode = rec.Mode,
            status = rec.State.ToString(),
            stop_reason = rec.StopReason ?? "",
            output = OutputObj(rec, m),
            series = rec.IsScreenshotSeries ? ScreenshotSeriesStatus(rec) : null,
            bundle = BundleObj(rec)
        };
    }

    private object BuildStoppingResponse(Recording rec) => new
    {
        recording_id = rec.Id,
        status = rec.State.ToString(),
        stop_reason = rec.StopReason ?? "",
        output = rec.IsScreenshotSeries ? ScreenshotSeriesOutput(rec) : (object?)null,
        mode = rec.Mode,
        series = rec.IsScreenshotSeries ? ScreenshotSeriesStatus(rec) : null,
        bundle = BundleObj(rec)
    };

    private static string PublicScreenshotSeriesStatus(Recording rec)
    {
        if (rec.State == RecState.recording) return "capturing";
        if (rec.State == RecState.preparing) return "preparing";
        if (rec.State == RecState.countdown) return "countdown";
        if (rec.State == RecState.finalizing) return "finalizing";
        return rec.ScreenshotSeries?.Status ?? rec.State.ToString();
    }

    private static object ScreenshotSeriesStatus(Recording rec)
    {
        lock (rec)
        {
            var series = rec.ScreenshotSeries;
            if (series == null) return new { };
            return new
            {
                interval_ms = series.IntervalMs,
                max_count = series.MaxCount,
                max_duration_seconds = series.MaxDurationSeconds,
                planned_frame_count = series.PlannedFrameCount,
                captured_frame_count = series.Frames.Count,
                next_capture_due_at = series.NextCaptureDueAtUtc.HasValue ? Iso(series.NextCaptureDueAtUtc.Value) : null,
                output_directory = series.FinalDirectory ?? series.OutputDirectory,
                staging = series.StagingDirectory != null,
                started_at = series.StartedAtUtc.HasValue ? Iso(series.StartedAtUtc.Value) : null,
                completed_at = series.CompletedAtUtc.HasValue ? Iso(series.CompletedAtUtc.Value) : null,
                status = series.Status,
                error_code = series.ErrorCode ?? ""
            };
        }
    }

    private static object ScreenshotSeriesOutput(Recording rec)
    {
        lock (rec)
        {
            var series = rec.ScreenshotSeries;
            if (series == null)
                return new { path = (string?)null, format = "png_sequence", frame_count = 0, manifest = (string?)null };
            var final = series.FinalDirectory;
            return new
            {
                path = final,
                format = "png_sequence",
                frame_count = series.Frames.Count,
                manifest = final == null ? null : Path.Combine(final, "series.json"),
                directory_published = final != null
            };
        }
    }

    private object BuildScreenshotSeriesResponse(Recording rec)
    {
        return new
        {
            recording_id = rec.Id,
            mode = ScreenshotSeriesConfig.ModeName,
            status = PublicScreenshotSeriesStatus(rec),
            source = new { type = rec.SourceType, title = rec.SourceTitle },
            backend = rec.BackendType,
            config = new { countdown_seconds = rec.CountdownSeconds },
            output = ScreenshotSeriesOutput(rec),
            series = ScreenshotSeriesStatus(rec),
            started_at = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            completed_at = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            elapsed_seconds = ComputeElapsedSeconds(rec),
            stop_reason = rec.StopReason ?? "",
            error = rec.Error ?? "",
            warnings = rec.Warnings.ToArray(),
            bundle = BundleObj(rec)
        };
    }

    public object GetStatus(string id)
    {
        var rec = Get(id);
        if (rec.IsScreenshotSeries)
            return BuildScreenshotSeriesResponse(rec);
        var elapsed = ComputeElapsedSeconds(rec);

        // Prefer the backend-reported output path when available.
        var meta = rec.LastMeta;
        string actualPath = meta?.OutputPath ?? rec.OutputPath;

        string container = meta?.Container ?? string.Empty;
        string codec = meta?.Codec ?? string.Empty;

        return new
        {
            recording_id = rec.Id,
            mode = rec.Mode,
            status = rec.State.ToString(),
            source = new { type = rec.SourceType, title = rec.SourceTitle },
            backend = rec.BackendType,
            config = new { countdown_seconds = rec.CountdownSeconds, duration_seconds = rec.DurationSeconds },
            started_at = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            completed_at = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            elapsed_seconds = elapsed,
            audio = new
            {
                source_kind = AudioSourceKindName(rec.AudioSourceKind),
                microphone = new
                {
                    enabled = rec.Microphone,
                    device_id = (object?)(rec.MicrophoneDeviceId ?? "") ?? "",
                    status = rec.Microphone ? AudioStatusFor(rec, rec.LastMeta) : "not_requested",
                    continuity_status = rec.Microphone ? AudioContinuityFor(rec, rec.LastMeta) : "not_checked",
                    capture_strategy = rec.LastMeta?.AudioCaptureStrategy ?? "",
                    pair_evidence = rec.LastMeta?.AudioPairEvidence ?? "",
                    auto_hfp_pair_status = rec.LastMeta?.AudioAutoHfpPairStatus ?? "",
                    auto_hfp_pair_result_code = rec.LastMeta?.AudioAutoHfpPairResultCode ?? "",
                    auto_hfp_pair_transport_classification = rec.LastMeta?.AudioAutoHfpPairTransportClassification ?? "",
                    helper_failure_reason = rec.LastMeta?.AudioHelperFailureReason ?? "",
                    helper_failure_stage = rec.LastMeta?.AudioHelperFailureStage ?? "",
                    helper_failure_hresult = rec.LastMeta?.AudioHelperFailureHresult ?? "",
                    render_prime_ready_ms = rec.LastMeta?.AudioRenderPrimeReadyMs
                },
                system_audio = new
                {
                    enabled = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback,
                    endpoint_id = (object?)(rec.SystemAudioEndpointId ?? "") ?? "",
                    endpoint_name = (object?)(rec.SystemAudioEndpointName ?? "") ?? "",
                    is_default_multimedia = rec.SystemAudioEndpointIsDefault,
                    status = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback
                        ? AudioStatusFor(rec, rec.LastMeta)
                        : "not_requested",
                    continuity_status = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback
                        ? AudioContinuityFor(rec, rec.LastMeta)
                        : "not_checked"
                }
            },
            output = new
            {
                path = actualPath,
                bytes_written = SafeSize(actualPath),
                duration_seconds = meta?.DurationSeconds ?? 0,
                container,
                codec,
                width = meta?.Width ?? 0,
                height = meta?.Height ?? 0,
                capture_method = meta?.CaptureMethod ?? "",
                encoder_mode = meta?.VideoEncoderMode ?? "",
                encoder_selection_reason = meta?.VideoEncoderSelectionReason ?? "",
                ffmpeg_exit_code = rec.ExitCode
            },
            stop_reason = rec.StopReason ?? "",
            warnings = rec.Warnings.ToArray(),
            stderr_excerpt = rec.StderrExcerpt ?? "",
            nested = new
            {
                role = rec.NestedRole ?? "none",
                session_id = rec.NestedSessionId ?? "",
                parent_recording_id = rec.ParentRecordingId ?? "",
                is_parent = rec.IsNestedParent
            },
            bundle = BundleObj(rec)
        };
    }

    public object GetOutput(string id)
    {
        var rec = Get(id);
        if (rec.IsScreenshotSeries)
            return new
            {
                recording_id = rec.Id,
                mode = ScreenshotSeriesConfig.ModeName,
                output = ScreenshotSeriesOutput(rec),
                series = ScreenshotSeriesStatus(rec),
                stop_reason = rec.StopReason ?? "",
                error = rec.Error ?? "",
                warnings = rec.Warnings.ToArray(),
                bundle = BundleObj(rec)
            };
        // Prefer metadata already produced by the backend. Fall back to probing
        // the FFmpeg output path when no metadata is available.
        var meta = rec.LastMeta;
        if (meta == null)
        {
            meta = FfmpegCaptureBackend.Probe(rec.OutputPath);
        }
        return new
        {
            recording_id = rec.Id,
            mode = rec.Mode,
            output = OutputObj(rec, meta, full: true),
            series = rec.IsScreenshotSeries ? ScreenshotSeriesStatus(rec) : null,
            stop_reason = rec.StopReason ?? "",
            warnings = rec.Warnings.ToArray(),
            stderr_excerpt = rec.StderrExcerpt ?? "",
            nested = new
            {
                role = rec.NestedRole ?? "none",
                session_id = rec.NestedSessionId ?? "",
                parent_recording_id = rec.ParentRecordingId ?? "",
                is_parent = rec.IsNestedParent
            },
            bundle = BundleObj(rec)
        };
    }

    public object GetConfirmation(string id)
    {
        if (!_confs.TryGetValue(id, out var c))
            throw new ApiException(404, "RECORDING_NOT_FOUND", "Confirmation not found");
        return new { ConfirmationId = c.Id, Status = c.Status, RecordingId = c.RecordingId };
    }

    /// <summary>
    /// Long-polling wait for confirmation status change.
    /// Returns immediately if status != since_status or if wait_ms expires.
    /// Uses case-insensitive status comparison and deadline-based remaining time.
    /// </summary>
    public object GetConfirmationWait(string id, string sinceStatus, int waitMs)
    {
        if (!_confs.TryGetValue(id, out var c))
            throw new ApiException(404, "RECORDING_NOT_FOUND", "Confirmation not found");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = WaitForStateChange(() => !string.Equals(c.Status, sinceStatus, StringComparison.OrdinalIgnoreCase), waitMs);
        sw.Stop();

        bool changed = !string.Equals(c.Status, sinceStatus, StringComparison.OrdinalIgnoreCase);
        int? nextPollHintMs = string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase) ? 500 : null;

        var traceId = _tracer.ResolveTraceId(c.RecordingId, c.Id);
        if (traceId != null)
        {
            _tracer.LongPollCompleted(traceId, "confirmation", waitMs, (int)sw.ElapsedMilliseconds,
                changed, recordingId: c.RecordingId, confirmationId: c.Id);
        }

        return new
        {
            ConfirmationId = c.Id,
            Status = c.Status,
            RecordingId = c.RecordingId,
            Wait = new { RequestedMs = waitMs, ElapsedMs = (int)sw.ElapsedMilliseconds, TimedOut = timedOut },
            NextPollHintMs = nextPollHintMs
        };
    }

    /// <summary>
    /// Long-polling wait for recording status change.
    /// Returns immediately if status != since_status or if wait_ms expires.
    /// Uses case-insensitive status comparison and deadline-based remaining time.
    /// </summary>
    public object GetStatusWait(string id, string sinceStatus, int waitMs)
    {
        var rec = Get(id);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = WaitForStateChange(() => !string.Equals(
            rec.IsScreenshotSeries ? PublicScreenshotSeriesStatus(rec) : rec.State.ToString(),
            sinceStatus, StringComparison.OrdinalIgnoreCase), waitMs);
        sw.Stop();

        bool changed = !string.Equals(
            rec.IsScreenshotSeries ? PublicScreenshotSeriesStatus(rec) : rec.State.ToString(),
            sinceStatus, StringComparison.OrdinalIgnoreCase);
        var traceId = _tracer.ResolveTraceId(rec.Id);
        if (traceId != null)
        {
            _tracer.LongPollCompleted(traceId, "recording", waitMs, (int)sw.ElapsedMilliseconds,
                changed, recordingId: rec.Id);
        }

        return BuildStatusWaitResponse(rec, waitMs, (int)sw.ElapsedMilliseconds, timedOut);
    }

    /// <summary>
    /// Shared wait logic: blocks on _lock using Monitor.Wait with remaining time.
    /// _stateVersion is only a wake-up signal; after waking, the predicate is re-evaluated.
    /// This prevents unrelated state changes from causing premature returns.
    /// </summary>
    private bool WaitForStateChange(Func<bool> predicate, int waitMs)
    {
        if (predicate())
            return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);

        lock (_lock)
        {
            while (!predicate())
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    return true;

                Monitor.Wait(_lock, remaining);
                // After waking (spurious or PulseAll), re-evaluate predicate.
                // Do NOT check _stateVersion; unrelated changes must not break the loop.
            }
        }

        return false;
    }

    private object BuildStatusWaitResponse(Recording rec, int requestedMs, int elapsedMs, bool timedOut)
    {
        if (rec.IsScreenshotSeries)
        {
            return new
            {
                recording_id = rec.Id,
                mode = ScreenshotSeriesConfig.ModeName,
                status = PublicScreenshotSeriesStatus(rec),
                source = new { type = rec.SourceType, title = rec.SourceTitle },
                output = ScreenshotSeriesOutput(rec),
                series = ScreenshotSeriesStatus(rec),
                stop_reason = rec.StopReason ?? "",
                error = rec.Error ?? "",
                wait = new { requested_ms = requestedMs, elapsed_ms = elapsedMs, timed_out = timedOut },
                next_poll_hint_ms = IsTerminalState(rec.State) ? (int?)null : 1000,
                bundle = BundleObj(rec)
            };
        }

        var elapsed = ComputeElapsedSeconds(rec);
        var meta = rec.LastMeta;
        string actualPath = meta?.OutputPath ?? rec.OutputPath;

        // next_poll_hint_ms: null for terminal states, 1000 for active states.
        bool isTerminal = rec.State is RecState.completed or RecState.failed or RecState.cancelled
            or RecState.rejected or RecState.expired;
        int? nextPollHintMs = isTerminal ? null : 1000;

        return new
        {
            RecordingId = rec.Id,
            Mode = rec.Mode,
            Status = rec.State.ToString(),
            Source = new { Type = rec.SourceType, Title = rec.SourceTitle },
            Backend = rec.BackendType,
            Config = new { CountdownSeconds = rec.CountdownSeconds, DurationSeconds = rec.DurationSeconds },
            StartedAt = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            CompletedAt = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            ElapsedSeconds = elapsed,
            Audio = new
            {
                SourceKind = AudioSourceKindName(rec.AudioSourceKind),
                Microphone = new
                {
                    Enabled = rec.Microphone,
                    DeviceId = (object?)(rec.MicrophoneDeviceId ?? "") ?? "",
                    Status = rec.Microphone ? AudioStatusFor(rec, rec.LastMeta) : "not_requested",
                    ContinuityStatus = rec.Microphone ? AudioContinuityFor(rec, rec.LastMeta) : "not_checked",
                    CaptureStrategy = rec.LastMeta?.AudioCaptureStrategy ?? "",
                    PairEvidence = rec.LastMeta?.AudioPairEvidence ?? "",
                    AutoHfpPairStatus = rec.LastMeta?.AudioAutoHfpPairStatus ?? "",
                    AutoHfpPairResultCode = rec.LastMeta?.AudioAutoHfpPairResultCode ?? "",
                    AutoHfpPairTransportClassification = rec.LastMeta?.AudioAutoHfpPairTransportClassification ?? "",
                    HelperFailureReason = rec.LastMeta?.AudioHelperFailureReason ?? "",
                    HelperFailureStage = rec.LastMeta?.AudioHelperFailureStage ?? "",
                    HelperFailureHresult = rec.LastMeta?.AudioHelperFailureHresult ?? "",
                    RenderPrimeReadyMs = rec.LastMeta?.AudioRenderPrimeReadyMs
                },
                SystemAudio = new
                {
                    Enabled = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback,
                    EndpointId = (object?)(rec.SystemAudioEndpointId ?? "") ?? "",
                    EndpointName = (object?)(rec.SystemAudioEndpointName ?? "") ?? "",
                    IsDefaultMultimedia = rec.SystemAudioEndpointIsDefault,
                    Status = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback
                        ? AudioStatusFor(rec, rec.LastMeta)
                        : "not_requested",
                    ContinuityStatus = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback
                        ? AudioContinuityFor(rec, rec.LastMeta)
                        : "not_checked"
                }
            },
            Output = new
            {
                Path = actualPath,
                BytesWritten = SafeSize(actualPath),
                DurationSeconds = meta?.DurationSeconds ?? 0,
                Container = meta?.Container ?? "",
                Codec = meta?.Codec ?? "",
                Width = meta?.Width ?? 0,
                Height = meta?.Height ?? 0,
                CaptureMethod = meta?.CaptureMethod ?? "",
                FfmpegExitCode = rec.ExitCode
            },
            Series = rec.IsScreenshotSeries ? ScreenshotSeriesStatus(rec) : null,
            stop_reason = rec.StopReason ?? "",
            Warnings = rec.Warnings.ToArray(),
            StderrExcerpt = rec.StderrExcerpt ?? "",
            Nested = new
            {
                Role = rec.NestedRole ?? "none",
                SessionId = rec.NestedSessionId ?? "",
                ParentRecordingId = rec.ParentRecordingId ?? "",
                IsParent = rec.IsNestedParent
            },
            Wait = new { RequestedMs = requestedMs, ElapsedMs = elapsedMs, TimedOut = timedOut },
            NextPollHintMs = nextPollHintMs,
            Bundle = BundleObj(rec)
        };
    }

    public IEnumerable<object> List() => _recs.Values.Select(r => new
    {
        recording_id = r.Id, mode = r.Mode, status = r.IsScreenshotSeries ? PublicScreenshotSeriesStatus(r) : r.State.ToString(),
        started_at = r.StartedAtUtc == default ? null : Iso(r.StartedAtUtc),
        completed_at = r.CompletedAtUtc.HasValue ? Iso(r.CompletedAtUtc.Value) : null,
        output_path = r.OutputPath,
        nested_role = r.NestedRole ?? "none",
        parent_recording_id = r.ParentRecordingId ?? "",
        nested_session_id = r.NestedSessionId ?? "",
        series = r.IsScreenshotSeries ? ScreenshotSeriesStatus(r) : null,
        bundle = BundleObj(r)
    });

    private void TrySetIdleOnAllDone(ITrayContext tray)
    {
        lock (_lock)
        {
            var anyActive = _recs.Values.Any(r =>
                r.State is RecState.preparing or RecState.countdown or RecState.recording or RecState.stopping or RecState.pending_confirmation or RecState.finalizing);
            if (!anyActive)
                tray.SetAllIdle();
        }
    }

    public void StopAllSync(string reason)
    {
        foreach (var r in _recs.Values.Where(r => r.State is RecState.preparing or RecState.countdown or RecState.recording))
            try { Stop(r.Id, reason); } catch { }
    }

    public void Dispose()
    {
        foreach (var pair in _seriesOps)
        {
            var operation = pair.Value;
            if (_recs.TryGetValue(pair.Key, out var rec))
            {
                lock (rec)
                {
                    if (!rec.IsFinalized && rec.State is (RecState.preparing or RecState.countdown or RecState.recording))
                    {
                        rec.State = RecState.stopping;
                        rec.StopReason ??= "dispose";
                        BumpStateVersion();
                    }
                    operation.StopRequested = true;
                }
            }
            else
            {
                operation.StopRequested = true;
            }

            try { operation.Cts.Cancel(); } catch { }
        }
        foreach (var operation in _seriesOps.Values)
        {
            try { operation.Task?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        }
        StopAllSync("dispose");
    }

    /// <summary>
    /// Test-only helper: deterministically trigger confirmation expiry using the
    /// same atomic decision path as the production timeout continuation. This
    /// lets race tests release the user callback and expiry simultaneously
    /// without waiting for Task.Delay.
    /// </summary>
    internal void TriggerConfirmationExpiryForTests(string confirmationId)
    {
        if (!_confs.TryGetValue(confirmationId, out var conf))
            return;

        if (!_recs.TryGetValue(conf.RecordingId, out var rec))
            return;

        ApplyConfirmationExpiry(conf, rec,
            _tracer.ResolveTraceId(rec.Id, conf.Id) ?? "trace_unknown",
            _tray ?? NullTrayContext.Instance);
    }

    private void ApplyConfirmationExpiry(Confirmation conf, Recording rec, string traceId, ITrayContext tray)
    {
        if (!conf.TryDecide("expired"))
            return;

        if (rec.State == RecState.pending_confirmation)
        {
            rec.State = RecState.expired;
            MarkBundleNotApplicable(rec);
        }
        BumpStateVersion();
        _audit.Log("confirmation.expired", new { recording_id = rec.Id, confirmation_id = conf.Id });
        _tracer.ConfirmationExpired(traceId, rec.Id, conf.Id);
        _tracer.RecordingTerminal(traceId, rec.Id, status: "expired");
        TrySetIdleOnAllDone(tray);
    }

    private Recording Get(string id) =>
        _recs.TryGetValue(id, out var r) ? r
        : throw new ApiException(404, "RECORDING_NOT_FOUND", $"Recording {id} not found");

    private object StatusObj(Recording r) => new { recording_id = r.Id, status = r.State.ToString() };

    private static object OutputObj(Recording rec, OutputMeta m, bool full = false)
    {
        string actualPath = m.OutputPath ?? rec.OutputPath;
        bool exists = File.Exists(actualPath);
        string container = m.Container ?? "mp4";
        string codec = m.Codec ?? "h264";

        var expectedSecs = rec.DurationSeconds ?? 0;
        var warnings = new List<string>(m.Warnings ?? Array.Empty<string>());

        // Explicit user/agent stops may legitimately be shorter than planned;
        // skip duration warnings then.
        bool isUserInitiatedStop = !string.IsNullOrEmpty(rec.StopReason) && rec.StopReason != "duration_reached";
        if (!isUserInitiatedStop)
        {
            if (expectedSecs > 0 && m.DurationSeconds < expectedSecs * 0.5 && m.DurationSeconds > 0)
                warnings.Add($"Actual duration ({m.DurationSeconds:F1}s) is less than expected ({expectedSecs}s). This may indicate a capture issue.");
            if (m.DurationSeconds == 0 && expectedSecs > 0)
                warnings.Add("Duration is 0 - no video content was captured. FFmpeg/gdigrab may have failed silently.");
        }

        var audioStatus = AudioStatusFor(rec, m);
        var audioContinuityStatus = AudioContinuityFor(rec, m);
        var audioSourceKind = AudioSourceKindName(rec.AudioSourceKind);

        if (!full)
            return new
            {
                path = actualPath,
                size_bytes = m.SizeBytes,
                duration_seconds = m.DurationSeconds,
                container,
                codec,
                encoder_mode = m.VideoEncoderMode ?? "",
                encoder_selection_reason = m.VideoEncoderSelectionReason ?? "",
                audio_source_kind = audioSourceKind,
                audio_status = audioStatus,
                audio_continuity_status = audioContinuityStatus,
                audio_codec = m.AudioCodec ?? "",
                has_audio_stream = m.HasAudioStream,
                warnings
            };
        return new
        {
            path = actualPath, exists, size_bytes = m.SizeBytes,
            duration_seconds = m.DurationSeconds, created_at = Iso(rec.CompletedAtUtc ?? DateTime.UtcNow),
            container, codec, width = m.Width, height = m.Height, fps = m.Fps,
            capture_method = m.CaptureMethod ?? "",
            encoder_mode = m.VideoEncoderMode ?? "",
            encoder_selection_reason = m.VideoEncoderSelectionReason ?? "",
            command_args = rec.Config?.CommandArgs ?? "",
            backend = rec.BackendType,
            source_type = rec.SourceType,
            audio_source_kind = audioSourceKind,
            audio_status = audioStatus,
            audio_continuity_status = audioContinuityStatus,
            audio_codec = m.AudioCodec ?? "",
            has_audio_stream = m.HasAudioStream,
            probe_streams = m.ProbeStreams.Select(s => new
            {
                index = s.Index,
                codec_type = s.CodecType ?? "",
                codec_name = s.CodecName ?? "",
                start_time_seconds = s.StartTimeSeconds,
                duration_seconds = s.DurationSeconds
            }).ToArray(),
            audio_capture_strategy = m.AudioCaptureStrategy ?? "",
            audio_pair_evidence = m.AudioPairEvidence ?? "",
            audio_auto_hfp_pair_status = m.AudioAutoHfpPairStatus ?? "",
            audio_auto_hfp_pair_result_code = m.AudioAutoHfpPairResultCode ?? "",
            audio_auto_hfp_pair_transport_classification = m.AudioAutoHfpPairTransportClassification ?? "",
            audio_helper_failure_reason = m.AudioHelperFailureReason ?? "",
            audio_helper_failure_stage = m.AudioHelperFailureStage ?? "",
            audio_helper_failure_hresult = m.AudioHelperFailureHresult ?? "",
            audio_render_prime_ready_ms = m.AudioRenderPrimeReadyMs,
            warnings
        };
    }

    private static object BundleObj(Recording rec)
    {
        var snapshot = rec.BundleSnapshot;
        return new
        {
            bundle_version = RecordingBundleSnapshot.BundleVersion,
            status = snapshot.Status,
            path = snapshot.Path,
            contents = snapshot.Contents.Select(c => new
            {
                name = c.Name,
                media_type = c.MediaType,
                size_bytes = c.SizeBytes
            }).ToArray(),
            error_code = (object?)snapshot.ErrorCode ?? null
        };
    }

    private static long SafeSize(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }
    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static RecordingConfirmationPresentation BuildConfirmationPresentation(
        RecordingRequestSummary summary,
        Recording rec,
        Confirmation confirmation,
        CapturePlan capturePlan,
        string traceId)
    {
        var createdAtUtc = DateTime.SpecifyKind(confirmation.CreatedAtUtc, DateTimeKind.Utc);
        var captureBounds = rec.Config.Bounds.w > 0 && rec.Config.Bounds.h > 0
            ? new ConfirmationCaptureBounds(
                rec.Config.Bounds.x,
                rec.Config.Bounds.y,
                rec.Config.Bounds.w,
                rec.Config.Bounds.h)
            : null;
        var targetDisplayBounds = capturePlan.DisplayBounds == null
            ? null
            : new ConfirmationCaptureBounds(
                capturePlan.DisplayBounds.X,
                capturePlan.DisplayBounds.Y,
                capturePlan.DisplayBounds.Width,
                capturePlan.DisplayBounds.Height);

        return new RecordingConfirmationPresentation
        {
            Summary = summary,
            RecordingId = rec.Id,
            ConfirmationId = confirmation.Id,
            TimeoutSeconds = confirmation.TimeoutSeconds,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddSeconds(confirmation.TimeoutSeconds),
            SourceType = rec.SourceType,
            SourceTitle = rec.SourceTitle,
            SourceApplication = rec.SourceApplication,
            WindowId = capturePlan.TargetIdentity,
            TraceId = traceId,
            CoordinateSpace = capturePlan.CoordinateSpace,
            CaptureSemantics = capturePlan.CaptureSemantics,
            PlannedBackend = capturePlan.PlannedBackend,
            PreviewSemantics = capturePlan.PreviewSemantics,
            SelectionReasonCode = capturePlan.Evidence.SelectionReasonCode,
            SelectionAvailabilitySource = capturePlan.Evidence.AvailabilitySource,
            SelectionFallback = capturePlan.FallbackOccurred,
            TargetDisplayId = capturePlan.TargetDisplayId ?? "",
            TargetDisplayBounds = targetDisplayBounds,
            CaptureBounds = captureBounds,
            OutputKind = rec.IsScreenshotSeries ? "png_sequence_directory" : "mp4_file"
        };
    }

    /// <summary>
    /// Creates the immutable UI boundary value immediately before a tray
    /// notification. The recording monitor is the existing lifecycle lock used
    /// by state transitions, so all mutable fields are copied as one snapshot.
    /// Optional countdown/series values are supplied by the worker that owns the
    /// corresponding progress event; they are copied into the DTO rather than
    /// leaving the UI to read a mutable runtime object later.
    /// </summary>
    private static RecordingUiPresentation CreateRecordingUiPresentation(
        Recording rec,
        RecordingUiState state,
        int? countdownRemainingSeconds = null,
        int? seriesCapturedFrameCount = null,
        int? seriesPlannedFrameCount = null,
        DateTime? seriesNextCaptureDueAtUtc = null)
    {
        lock (rec)
        {
            var bounds = rec.Config.Bounds;
            var series = rec.ScreenshotSeries;
            return new RecordingUiPresentation
            {
                RecordingId = rec.Id,
                State = state,
                SourceType = rec.SourceType,
                CaptureBounds = new RecordingUiBounds(bounds.x, bounds.y, bounds.w, bounds.h),
                DurationSeconds = rec.DurationSeconds,
                StartedAtUtc = rec.StartedAtUtc,
                IsScreenshotSeries = rec.IsScreenshotSeries,
                SeriesCapturedFrameCount = seriesCapturedFrameCount,
                SeriesPlannedFrameCount = seriesPlannedFrameCount ?? series?.PlannedFrameCount,
                SeriesNextCaptureDueAtUtc = seriesNextCaptureDueAtUtc,
                CountdownRemainingSeconds = countdownRemainingSeconds,
                NestedRole = rec.NestedRole,
                ParentRecordingId = rec.ParentRecordingId,
                NestedSessionId = rec.NestedSessionId
            };
        }
    }

    internal static RecordingUiPresentation CreateRecordingUiPresentationForTests(
        Recording rec,
        RecordingUiState state,
        int? countdownRemainingSeconds = null,
        int? seriesCapturedFrameCount = null,
        int? seriesPlannedFrameCount = null,
        DateTime? seriesNextCaptureDueAtUtc = null)
        => CreateRecordingUiPresentation(
            rec,
            state,
            countdownRemainingSeconds,
            seriesCapturedFrameCount,
            seriesPlannedFrameCount,
            seriesNextCaptureDueAtUtc);

    /// <summary>
    /// Null-object tray context used only by internal test seams that may run
    /// without a real tray. All operations are no-ops.
    /// </summary>
    private sealed class NullTrayContext : ITrayContext
    {
        public static ITrayContext Instance { get; } = new NullTrayContext();
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
