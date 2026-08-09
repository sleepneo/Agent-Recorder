using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

public sealed class RecordingEngine
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
    }

    private readonly ConcurrentDictionary<string, CountdownOperation> _countdownOps = new();

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
    private readonly IDisplayTopologyProvider _displayTopologyProvider;
    private bool _usesDefaultBackendFactory = true;
    private Func<CaptureConfig, CapturePlan>? _capturePlanFactory =
        CaptureBackendSelector.BuildPlan;
    private Func<CaptureConfig, CaptureBackendSelection>? _backendSelectionFactory =
        null;
    private readonly object _lock = new();
    private ITrayContext? _tray;

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
    /// Test seam: number of countdown steps. Production default is 3 (3-2-1).
    /// </summary>
    internal int CountdownSteps { get; set; } = 3;

    /// <summary>
    /// Test seam: timeout waiting for the first video frame after StartVideo.
    /// Production default is 10 seconds.
    /// </summary>
    internal TimeSpan FirstFrameTimeout { get; set; } = TimeSpan.FromSeconds(10);

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
        IDisplayTopologyProvider? displayTopologyProvider = null)
    {
        _audit = audit;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
        _bundleGenerator = bundleGenerator;
        _microphoneProvider = microphoneProvider ?? new EmptyMicrophoneProvider();
        _microphoneStatusProvider = microphoneStatusProvider ?? NullMicrophoneStatusProvider.Instance;
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

    private static string NormalizeStopReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "user_requested";
        return reason.Trim();
    }

    /// <summary>
    /// Computes a stable, finite, non-sensitive machine error code for a failed
    /// terminal recording. Never returns free-text messages, paths, or ffmpeg args.
    /// </summary>
    private static string ResolveTerminalErrorCode(string? backendType, bool microphoneRequested, OutputMeta meta, int exitCode,
        bool fileOk, bool durationOk, bool rangeOk, bool exitOk, bool allowWgcLifecycleReason)
    {
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
        if (microphoneRequested && !string.IsNullOrEmpty(meta.AudioHelperErrorCode))
            return meta.AudioHelperErrorCode;

        // Microphone-specific outcomes take precedence over generic validation so
        // callers get a stable, actionable code when audio evidence is missing.
        if (microphoneRequested)
        {
            if (string.Equals(meta.AudioStatus, "missing_audio_track", StringComparison.OrdinalIgnoreCase))
                return "microphone_missing_audio_track";
            if (string.Equals(meta.AudioStatus, "start_failed", StringComparison.OrdinalIgnoreCase))
                return "microphone_start_failed";
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

    public object CreateRecording(JsonNode cfg, string agent, ITrayContext tray, string? traceId = null, string? endpoint = null)
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
        object summary;
        try
        {
            rec = ConfigParser.Build(cfg, agent, out summary, _microphoneProvider, _microphoneStatusProvider);
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
            audio_microphone = rec.Microphone,
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

            // Add metadata to summary for tray UI
            var summaryWithMeta = new
            {
                source = GetSummaryField(summary, "source"),
                audio = GetSummaryField(summary, "audio"),
                duration = GetSummaryField(summary, "duration"),
                output = GetSummaryField(summary, "output"),
                nested_role = GetSummaryField(summary, "nested_role"),
                recording_id = rec.Id,
                confirmation_id = conf.Id,
                timeout_seconds = conf.TimeoutSeconds,
                expires_at = DateTime.UtcNow.AddSeconds(conf.TimeoutSeconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                source_type = rec.SourceType,
                source_title = rec.SourceTitle,
                source_application = rec.SourceApplication,
                window_id = capturePlan.TargetIdentity,
                trace_id = traceId,
                coordinate_space = "virtual_screen",
                capture_semantics = capturePlan.CaptureSemantics,
                planned_backend = capturePlan.PlannedBackend,
                preview_semantics = capturePlan.CaptureSemantics,
                selection_reason_code = capturePlan.Evidence.SelectionReasonCode,
                selection_availability_source = capturePlan.Evidence.AvailabilitySource,
                selection_fallback = capturePlan.FallbackOccurred,
                target_display_id = capturePlan.TargetDisplayId ?? "",
                target_display_bounds = capturePlan.DisplayBounds == null
                    ? null
                    : new
                    {
                        x = capturePlan.DisplayBounds.X,
                        y = capturePlan.DisplayBounds.Y,
                        width = capturePlan.DisplayBounds.Width,
                        height = capturePlan.DisplayBounds.Height
                    },
                capture_bounds = (rec.Config.Bounds.w > 0 && rec.Config.Bounds.h > 0)
                    ? new { x = rec.Config.Bounds.x, y = rec.Config.Bounds.y, width = rec.Config.Bounds.w, height = rec.Config.Bounds.h }
                    : null
            };

            tray.RequestConfirmation(summaryWithMeta, decision =>
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
            recording_id = rec.Id, status = "recording",
            started_at = Iso(rec.StartedAtUtc), expected_output = rec.OutputPath,
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
            var newPath = OutputPathResolver.MoveToDirectory(rec.OutputPath, decision.OutputDirectory);
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
        bool approvedRegion = string.Equals(approved.SourceKind, "region", StringComparison.Ordinal);
        if (approvedRegion && !TryValidateApprovedRegionTopology(
                approved,
                rec.Config,
                out currentTopology,
                out topologyFailure))
        {
            // Topology is checked before rebuilding the capability/backend plan.
            // A stale or malformed region must never reach a backend, helper,
            // countdown, or output-directory side effect.
        }

        CapturePlan? revalidated = null;
        string? failureType = null;
        if (topologyFailure == null)
        {
            try
            {
                revalidated = (_capturePlanFactory ?? CaptureBackendSelector.BuildPlan)(rec.Config);
            }
            catch (Exception ex)
            {
                failureType = ex.GetType().Name;
            }
        }

        bool changed = topologyFailure != null || revalidated == null || IsCapturePlanDrift(approved, revalidated);
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
                approved_reason_code = approved.Evidence.SelectionReasonCode,
                revalidated_backend = revalidated?.PlannedBackend ?? "unavailable",
                revalidated_semantics = revalidated?.CaptureSemantics ?? "unavailable",
                revalidated_reason_code = revalidated?.Evidence.SelectionReasonCode ?? "plan_unavailable",
                revalidated_availability_source = revalidated?.Evidence.AvailabilitySource ?? "not_run",
                approved_display_id = approved.TargetDisplayId ?? "",
                revalidated_display_id = currentTopology?.PublicId ?? revalidated?.TargetDisplayId ?? "",
                approved_display_identity_fingerprint = approved.TargetDisplayIdentity ?? "",
                revalidated_display_identity_fingerprint = ResolvedIdentityForAudit(currentTopology)
                    ?? revalidated?.TargetDisplayIdentity ?? "",
                approved_display_bounds = approvedDisplayBounds,
                revalidated_display_bounds = currentDisplayBounds,
                topology_status = approvedRegion
                    ? topologyFailure == null ? "passed" : "failed"
                    : "not_required",
                topology_reason = approvedRegion
                    ? topologyFailure ?? "matched"
                    : "not_required",
                semantics_changed = changed,
                failure_type = topologyFailure ?? failureType ?? ""
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
        MarkBundleNotApplicable(rec);
        lock (rec)
        {
            if (rec.IsFinalized)
                return false;

            rec.IsFinalized = true;
            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.StopReason = errorCode;
            rec.Error = errorCode;
            rec.Warnings.Add(errorCode);
            rec.State = RecState.failed;
            BumpStateVersion();
        }

        try
        {
            _audit.Log("recording.capture_semantics_changed", new
            {
                recording_id = rec.Id,
                source_type = rec.SourceType,
                approved_backend = approved.PlannedBackend,
                approved_semantics = approved.CaptureSemantics,
                approved_reason_code = approved.Evidence.SelectionReasonCode,
                revalidated_backend = revalidated?.PlannedBackend ?? "unavailable",
                revalidated_semantics = revalidated?.CaptureSemantics ?? "unavailable",
                revalidated_reason_code = revalidated?.Evidence.SelectionReasonCode ?? "plan_unavailable",
                approved_display_id = approved.TargetDisplayId ?? "",
                revalidated_display_id = currentTopology?.PublicId ?? revalidated?.TargetDisplayId ?? "",
                approved_display_identity_fingerprint = approved.TargetDisplayIdentity ?? "",
                revalidated_display_identity_fingerprint = ResolvedIdentityForAudit(currentTopology)
                    ?? revalidated?.TargetDisplayIdentity ?? "",
                approved_display_bounds = approvedDisplayBounds,
                revalidated_display_bounds = currentDisplayBounds,
                topology_status = approvedRegion
                    ? topologyFailure == null ? "passed" : "failed"
                    : "not_required",
                topology_reason = approvedRegion
                    ? topologyFailure ?? "matched"
                    : "not_required",
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
            return false;
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

        if (!string.Equals(approved.PlannedBackend, current.PlannedBackend, StringComparison.Ordinal))
            return true;

        if (!string.Equals(approved.CaptureSemantics, current.CaptureSemantics, StringComparison.Ordinal))
            return true;

        // A window-surface promise may never degrade to a desktop rectangle,
        // even if another selector result would otherwise be startable.
        return approved.IsWindowSurface && !current.IsWindowSurface;
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
                preview_semantics = plan.CaptureSemantics,
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
                fallback = plan.FallbackOccurred
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
            tray.SetIdle(rec);

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
        if (rec.Backend is IAudioReadyBackend audioReady && rec.Microphone)
        {
            audioReady.AudioReady += () => OnAudioReady(rec, traceId, tray);
        }

        // No-microphone backends capable of deferred capture start (WGC
        // continuous) take the 3-2-1 countdown path: the helper process is
        // launched now but capture authorization is withheld until the
        // countdown reaches zero, so nothing is captured during the countdown.
        // The authorization completion is pure audit; failures surface through
        // the normal first-frame timeout / natural-exit paths.
        bool useDeferredCountdown = !rec.Microphone && rec.Backend is IDeferredCaptureStartBackend;
        if (rec.Backend is IDeferredCaptureStartBackend deferredObservable)
        {
            deferredObservable.CaptureAuthorizationCompleted += ok => OnCaptureAuthorizationCompleted(rec, ok);
        }

        // Enter preparing: backend initialization (including microphone warmup)
        // has begun, but no REC UI, no elapsed timer, and no user-visible start
        // until credible first-frame evidence arrives.
        rec.State = RecState.preparing;
        rec.BackendStartAtUtc = DateTime.UtcNow;
        BumpStateVersion();

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

            _tracer.CaptureStartRequested(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");
            rec.Backend.Start(rec.Config);
            _tracer.CaptureBackendStartReturned(traceId ?? "trace_unknown", rec.Id, rec.BackendType ?? "unknown");

            _audit.Log("recording.started", new
            {
                recording_id = rec.Id,
                output_path = rec.OutputPath,
                backend = rec.BackendType,
                ffmpeg_args = rec.Config.CommandArgs ?? ""
            });

            // After the backend has started, catch the race where AudioReady fired
            // before the subscription above was attached.
            if (rec.Backend is IAudioReadyBackend audioReadyBackend && rec.Microphone && audioReadyBackend.IsAudioReady)
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
                tray.SetPreparing(rec);
                BeginDeferredCountdown(rec, traceId, tray);
            }
            // Split A/V backends with a microphone: show preparing UI and wait
            // for AudioReady before the 3-2-1 countdown.
            else if (rec.Backend is IAudioReadyBackend && rec.Microphone)
            {
                _tracer.MicrophonePrepareStarted(traceId ?? "trace_unknown", rec.Id);
                _audit.Log("recording.microphone_prepare_started", new
                {
                    recording_id = rec.Id,
                    device_id = rec.MicrophoneDeviceId ?? "",
                    device_name = rec.MicrophoneDeviceName ?? ""
                });
                tray.SetPreparing(rec);
            }
            // For other first-frame-observable backends (e.g. no-microphone FFmpeg),
            // show preparing until credible first-frame evidence arrives.
            else if (rec.Backend is IFirstFrameObservableCaptureBackend && !rec.IsFinalized)
            {
                tray.SetPreparing(rec);
            }
            // Non-observable backends cannot wait for evidence.
            else if (!rec.IsFinalized)
            {
                TransitionToRecording(rec, traceId, tray, firstFrameEvidence: null);
            }
        }
        catch (Exception ex)
        {
            _tracer.CaptureBackendStartFailed(traceId ?? "trace_unknown", rec.Id,
                rec.BackendType ?? "unknown", "backend_start_exception", ex.GetType().Name);

            // If the backend already finalized itself inside Start(), do NOT
            // overwrite its terminal state, error, stop reason, output metadata,
            // or audit/tray state. Record a non-terminal diagnostic audit only.
            if (rec.IsFinalized)
            {
                _audit.Log("recording.backend_start_exception_after_terminal", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType,
                    final_state = rec.State.ToString(),
                    exception_type = ex.GetType().Name
                });
                return;
            }

            // Backend.Start() threw before any terminal state was reached:
            // transition to failed with a single terminal event and one user error.
            lock (rec)
            {
                MarkBundleNotApplicable(rec);
                rec.CompletedAtUtc = DateTime.UtcNow;
                rec.Error = ex.Message;
                rec.Warnings.Add("launch_error: " + ex.Message);
                rec.State = RecState.failed;
                BumpStateVersion();
            }
            _tracer.RecordingTerminal(traceId ?? "trace_unknown", rec.Id, status: "failed",
                stopReason: "unexpected_exit", errorCode: "backend_start_exception");
            _audit.Log("recording.failed", new
            {
                recording_id = rec.Id,
                backend = rec.BackendType,
                error = ex.Message
            });
            tray.SetIdle(rec);
            tray.ShowError("Recording failed: " + ex.Message);
        }
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

            rec.State = RecState.recording;
            rec.StartedAtUtc = DateTime.UtcNow;
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
        tray.SetRecording(rec);
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
            tray.SetFinalizing(rec);
        }

        return transitioned;
    }

    /// <summary>
    /// Invoked when a split A/V backend reports that the microphone is ready.
    /// Starts the 3-2-1 countdown and then launches the video worker.
    /// </summary>
    private void OnAudioReady(Recording rec, string? traceId, ITrayContext tray)
    {
        lock (rec)
        {
            if (rec.State != RecState.preparing || rec.IsFinalized)
                return;

            rec.State = RecState.countdown;
            rec.CountdownStartedAtUtc = DateTime.UtcNow;
            BumpStateVersion();
        }

        _tracer.MicrophoneReady(traceId ?? "trace_unknown", rec.Id);
        _tracer.CountdownStarted(traceId ?? "trace_unknown", rec.Id);
        _audit.Log("recording.countdown_started", new
        {
            recording_id = rec.Id,
            trigger = "microphone_ready",
            microphone_ready_at = Iso(rec.CountdownStartedAtUtc.Value)
        });

        var op = new CountdownOperation();
        _countdownOps[rec.Id] = op;
        _ = RunCountdownAsync(rec, traceId, tray, op);
    }

    /// <summary>
    /// Starts the 3-2-1 countdown for a no-microphone deferred-start backend
    /// (WGC continuous). The backend process is already prepared but has not
    /// been authorized to capture; authorization is requested when the
    /// countdown reaches zero inside <see cref="RunCountdownAsync"/>.
    /// </summary>
    private void BeginDeferredCountdown(Recording rec, string? traceId, ITrayContext tray)
    {
        lock (rec)
        {
            if (rec.State != RecState.preparing || rec.IsFinalized)
                return;

            rec.State = RecState.countdown;
            rec.CountdownStartedAtUtc = DateTime.UtcNow;
            BumpStateVersion();
        }

        _tracer.CountdownStarted(traceId ?? "trace_unknown", rec.Id);
        _audit.Log("recording.countdown_started", new
        {
            recording_id = rec.Id,
            trigger = "deferred_capture_start",
            countdown_started_at = Iso(rec.CountdownStartedAtUtc.Value)
        });

        var op = new CountdownOperation();
        _countdownOps[rec.Id] = op;
        _ = RunCountdownAsync(rec, traceId, tray, op);
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

    /// <summary>
    /// Drives the 3-2-1 countdown overlay and starts video capture when it reaches zero.
    /// Keeps the recording in the countdown state until real first-frame evidence is
    /// observed. Uses Task.Delay so the UI thread is never blocked.
    /// </summary>
    private async Task RunCountdownAsync(Recording rec, string? traceId, ITrayContext tray, CountdownOperation op)
    {
        var ct = op.Cts.Token;
        Action<FirstFrameObservation>? firstFrameHandler = null;
        IFirstFrameObservableCaptureBackend? subscribedObservable = null;

        try
        {
            try
            {
                for (int remaining = CountdownSteps; remaining >= 1; remaining--)
                {
                    tray.SetCountdown(rec, remaining);
                    await Task.Delay(CountdownInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (rec)
            {
                if (rec.State != RecState.countdown || rec.IsFinalized)
                    return;

                // The visible countdown has completed normally. Transition the
                // operation into the first-frame-wait phase atomically with the
                // state check: a Stop from here on cancels the wait promptly
                // but must not report the visible countdown as cancelled.
                Volatile.Write(ref op.Phase, CountdownOperation.PhaseFirstFrameWait);
            }

            _audit.Log("recording.countdown_completed", new
            {
                recording_id = rec.Id,
                backend = rec.BackendType
            });

            tray.SetCountdown(rec, null);

            // Late-terminal guard: a stop/finalize that won the race after the
            // phase transition must not trigger a late StartVideo/StartCapture
            // or further UI updates from this operation.
            lock (rec)
            {
                if (rec.State != RecState.countdown || rec.IsFinalized)
                    return;
            }

            if (rec.Backend is IAudioReadyBackend audioReady)
            {
                try
                {
                    audioReady.StartVideo();
                }
                catch (Exception ex)
                {
                    _tracer.CaptureBackendStartFailed(traceId ?? "trace_unknown", rec.Id,
                        rec.BackendType ?? "unknown", "video_start_failed", ex.GetType().Name);
                    lock (rec)
                    {
                        MarkBundleNotApplicable(rec);
                        rec.CompletedAtUtc = DateTime.UtcNow;
                        rec.Error = "Failed to start video capture: " + ex.Message;
                        rec.Warnings.Add("video_start_failed: " + ex.Message);
                        rec.State = RecState.failed;
                        BumpStateVersion();
                    }
                    _tracer.RecordingTerminal(traceId ?? "trace_unknown", rec.Id, status: "failed",
                        stopReason: "video_start_failed", errorCode: "video_start_failed");
                    _audit.Log("recording.failed", new
                    {
                        recording_id = rec.Id,
                        backend = rec.BackendType,
                        error = rec.Error,
                        stage = "video_start"
                    });
                    tray.SetIdle(rec);
                    tray.ShowError(rec.Error);
                    return;
                }
            }
            else if (rec.Backend is IDeferredCaptureStartBackend deferred)
            {
                // Countdown reached zero: authorize the prepared helper to start
                // capturing now. Nothing was captured during the countdown.
                _audit.Log("recording.capture_authorization_requested", new
                {
                    recording_id = rec.Id,
                    backend = rec.BackendType
                });
                try
                {
                    deferred.StartCapture();
                }
                catch (Exception ex)
                {
                    _tracer.CaptureBackendStartFailed(traceId ?? "trace_unknown", rec.Id,
                        rec.BackendType ?? "unknown", "capture_start_failed", ex.GetType().Name);
                    lock (rec)
                    {
                        MarkBundleNotApplicable(rec);
                        rec.CompletedAtUtc = DateTime.UtcNow;
                        rec.Error = "Failed to authorize capture start: " + ex.Message;
                        rec.Warnings.Add("capture_start_failed: " + ex.Message);
                        rec.State = RecState.failed;
                        BumpStateVersion();
                    }
                    _tracer.RecordingTerminal(traceId ?? "trace_unknown", rec.Id, status: "failed",
                        stopReason: "capture_start_failed", errorCode: "capture_start_failed");
                    _audit.Log("recording.failed", new
                    {
                        recording_id = rec.Id,
                        backend = rec.BackendType,
                        error = rec.Error,
                        stage = "capture_start"
                    });
                    tray.SetIdle(rec);
                    tray.ShowError(rec.Error);
                    return;
                }
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
                    if (rec.IsFinalized || rec.State != RecState.countdown)
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
                tray.SetIdle(rec);
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

            rec.IsFinalized = true;

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

            bool microphoneRequested = rec.Microphone;
            bool audioOk = !microphoneRequested ||
                           string.Equals(meta.AudioStatus, "recorded", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(meta.AudioStatus, "lost", StringComparison.OrdinalIgnoreCase);
            bool wgcContinuousOutputValidationFailed =
                IsWgcContinuousBackend(rec.BackendType) &&
                IsWgcContinuousOutputValidationFailure(meta.StopReason);

            // A stable helper-declared audio failure can never be a successful
            // recording, even when the probed temp files look healthy and the
            // audio status is a recoverable-looking "lost". The helper's own
            // terminal verdict takes precedence over file heuristics.
            if (microphoneRequested && !string.IsNullOrEmpty(meta.AudioHelperErrorCode))
                audioOk = false;

            bool success = fileOk && durationOk && exitOk && rangeOk && audioOk &&
                           !wgcContinuousOutputValidationFailed;
            if (!success)
            {
                if (!fileOk) rec.Warnings.Add($"empty_output: file size {meta.SizeBytes} bytes < {minSize}");
                if (!durationOk) rec.Warnings.Add($"zero_duration: ffprobe returned duration=0");
                if (!rangeOk && expected > 0) rec.Warnings.Add($"duration_out_of_range: expected ~{expected}s got {meta.DurationSeconds:F1}s");
                if (!exitOk) rec.Warnings.Add($"non_zero_exit: ffmpeg exit_code={exitCode}");
                if (!audioOk) rec.Warnings.Add($"microphone_audio_failed: audio_status={meta.AudioStatus ?? "unknown"}");
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
                    width = meta.Width,
                    height = meta.Height,
                    ffmpeg_exit_code = exitCode,
                    audio_microphone = rec.Microphone,
                    audio_status = meta.AudioStatus ?? (rec.Microphone ? "unknown" : "not_requested"),
                    audio_continuity_status = meta.AudioContinuityStatus ?? (rec.Microphone ? "not_checked" : "not_checked"),
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
                    audio_discontinuity_count = meta.AudioDiscontinuityCount
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
                var stableErrorCode = ResolveTerminalErrorCode(rec.BackendType, rec.Microphone, meta, exitCode, fileOk, durationOk, rangeOk, exitOk, natural);
                rec.Error = stableErrorCode;
                rec.BundleSnapshot = RecordingBundleSnapshot.NotApplicable();
                rec.State = RecState.failed;
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
                    stage = meta.Stage ?? "",
                    hresult = meta.Hresult ?? "",
                    ffmpeg_exit_code = exitCode,
                    size_bytes = meta.SizeBytes,
                    duration_seconds = meta.DurationSeconds,
                    stderr_excerpt = rec.StderrExcerpt ?? "",
                    audio_microphone = rec.Microphone,
                    audio_status = meta.AudioStatus ?? (rec.Microphone ? "unknown" : "not_requested"),
                    audio_continuity_status = meta.AudioContinuityStatus ?? (rec.Microphone ? "not_checked" : "not_checked"),
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
                    audio_discontinuity_count = meta.AudioDiscontinuityCount
                });
            }
        }

        _tracer.FinalizationCompleted(GetTraceIdForRecording(rec.Id), rec.Id, finalizationSuccess);

        if (bundleRequest != null)
        {
            _ = Task.Run(() => RunBundleGenerationAsync(rec, bundleRequest));
        }

        tray.SetIdle(rec);

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
    }

    private static bool IsWgcLifecycleFailure(string? reason) => reason is
        "window_closed" or "window_minimized" or "size_changed";

    private static bool IsWgcContinuousOutputValidationFailure(string? reason) =>
        string.Equals(reason, "output_validation_failed", StringComparison.Ordinal);

    private static bool IsWgcContinuousBackend(string? backendType) =>
        string.Equals(backendType, "wgc-continuous", StringComparison.OrdinalIgnoreCase);

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
            _audit.Log("recording.countdown_cancelled", new { recording_id = recordingId });
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
            audioStatus: meta.AudioStatus ?? (rec.Microphone ? "unknown" : "not_requested"),
            audioContinuityStatus: meta.AudioContinuityStatus ?? (rec.Microphone ? "not_checked" : "not_checked"),
            audioDeviceId: rec.MicrophoneDeviceId,
            audioLostAtMs: meta.AudioLostAtMs,
            nestedRole: rec.NestedRole,
            nestedSessionId: rec.NestedSessionId,
            parentRecordingId: rec.ParentRecordingId,
            mediaPath: meta.OutputPath ?? rec.OutputPath,
            container: meta.Container ?? "mp4",
            codec: meta.Codec ?? "h264",
            width: meta.Width,
            height: meta.Height);
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
                rec.IsFinalized = true;
                MarkBundleNotApplicable(rec);
                BumpStateVersion();
            }
            else
            {
                rec.State = RecState.stopping;
                rec.StopReason = NormalizeStopReason(reason);
                BumpStateVersion();
            }
        }

        CancelCountdown(rec.Id);

        if (rec.State == RecState.cancelled)
        {
            _audit.Log("recording.cancelled", new { recording_id = rec.Id, reason = rec.StopReason });
            // Cancel the backend first so any synchronous first-frame observation
            // emitted during teardown can still be traced before the terminal tombstone
            // is recorded.
            try { rec.Backend?.Cancel(); } catch { }
            _tracer.RecordingTerminal(GetTraceIdForRecording(rec.Id), rec.Id, status: "cancelled", stopReason: rec.StopReason);
            _tray!.SetIdle(rec);
            return BuildStopResponse(rec);
        }

        _audit.Log("recording.stopping", new { recording_id = rec.Id, reason = rec.StopReason });

        var meta = rec.Backend?.Stop() ?? new OutputMeta();
        int exitCode = rec.Backend?.ExitCode ?? -1;

        FinalizeRecording(rec, meta, exitCode, natural: false, stopReason: rec.StopReason, _tray!);
        return BuildStopResponse(rec, meta);
    }

    private object BuildStopResponse(Recording rec, OutputMeta? meta = null)
    {
        var m = meta ?? rec.LastMeta;
        if (m == null)
            m = FfmpegCaptureBackend.Probe(rec.OutputPath);

        return new
        {
            recording_id = rec.Id,
            status = rec.State.ToString(),
            stop_reason = rec.StopReason ?? "",
            output = OutputObj(rec, m),
            bundle = BundleObj(rec)
        };
    }

    private object BuildStoppingResponse(Recording rec) => new
    {
        recording_id = rec.Id,
        status = rec.State.ToString(),
        stop_reason = rec.StopReason ?? "",
        output = (object?)null,
        bundle = BundleObj(rec)
    };

    public object GetStatus(string id)
    {
        var rec = Get(id);
        var elapsed = ComputeElapsedSeconds(rec);

        // Prefer the backend-reported output path when available.
        var meta = rec.LastMeta;
        string actualPath = meta?.OutputPath ?? rec.OutputPath;

        string container = meta?.Container ?? string.Empty;
        string codec = meta?.Codec ?? string.Empty;

        return new
        {
            recording_id = rec.Id,
            status = rec.State.ToString(),
            source = new { type = rec.SourceType, title = rec.SourceTitle },
            backend = rec.BackendType,
            started_at = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            completed_at = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            elapsed_seconds = elapsed,
            audio = new
            {
                microphone = new
                {
                    enabled = rec.Microphone,
                    device_id = (object?)(rec.MicrophoneDeviceId ?? "") ?? "",
                    status = rec.Microphone
                        ? (rec.LastMeta?.AudioStatus ?? (IsTerminalState(rec.State) ? "unknown" : "pending"))
                        : "not_requested",
                    continuity_status = rec.Microphone
                        ? (rec.LastMeta?.AudioContinuityStatus ?? (IsTerminalState(rec.State) ? "not_checked" : "pending"))
                        : "not_checked",
                    capture_strategy = rec.LastMeta?.AudioCaptureStrategy ?? "",
                    pair_evidence = rec.LastMeta?.AudioPairEvidence ?? "",
                    auto_hfp_pair_status = rec.LastMeta?.AudioAutoHfpPairStatus ?? "",
                    auto_hfp_pair_result_code = rec.LastMeta?.AudioAutoHfpPairResultCode ?? "",
                    auto_hfp_pair_transport_classification = rec.LastMeta?.AudioAutoHfpPairTransportClassification ?? "",
                    helper_failure_reason = rec.LastMeta?.AudioHelperFailureReason ?? "",
                    helper_failure_stage = rec.LastMeta?.AudioHelperFailureStage ?? "",
                    helper_failure_hresult = rec.LastMeta?.AudioHelperFailureHresult ?? "",
                    render_prime_ready_ms = rec.LastMeta?.AudioRenderPrimeReadyMs
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
            output = OutputObj(rec, meta, full: true),
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
        bool timedOut = WaitForStateChange(() => !string.Equals(rec.State.ToString(), sinceStatus, StringComparison.OrdinalIgnoreCase), waitMs);
        sw.Stop();

        bool changed = !string.Equals(rec.State.ToString(), sinceStatus, StringComparison.OrdinalIgnoreCase);
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
            Status = rec.State.ToString(),
            Source = new { Type = rec.SourceType, Title = rec.SourceTitle },
            Backend = rec.BackendType,
            StartedAt = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            CompletedAt = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            ElapsedSeconds = elapsed,
            Audio = new
            {
                Microphone = new
                {
                    Enabled = rec.Microphone,
                    DeviceId = (object?)(rec.MicrophoneDeviceId ?? "") ?? "",
                    Status = rec.Microphone
                        ? (rec.LastMeta?.AudioStatus ?? (IsTerminalState(rec.State) ? "unknown" : "pending"))
                        : "not_requested",
                    ContinuityStatus = rec.Microphone
                        ? (rec.LastMeta?.AudioContinuityStatus ?? (IsTerminalState(rec.State) ? "not_checked" : "pending"))
                        : "not_checked",
                    CaptureStrategy = rec.LastMeta?.AudioCaptureStrategy ?? "",
                    PairEvidence = rec.LastMeta?.AudioPairEvidence ?? "",
                    AutoHfpPairStatus = rec.LastMeta?.AudioAutoHfpPairStatus ?? "",
                    AutoHfpPairResultCode = rec.LastMeta?.AudioAutoHfpPairResultCode ?? "",
                    AutoHfpPairTransportClassification = rec.LastMeta?.AudioAutoHfpPairTransportClassification ?? "",
                    HelperFailureReason = rec.LastMeta?.AudioHelperFailureReason ?? "",
                    HelperFailureStage = rec.LastMeta?.AudioHelperFailureStage ?? "",
                    HelperFailureHresult = rec.LastMeta?.AudioHelperFailureHresult ?? "",
                    RenderPrimeReadyMs = rec.LastMeta?.AudioRenderPrimeReadyMs
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
        recording_id = r.Id, status = r.State.ToString(),
        started_at = r.StartedAtUtc == default ? null : Iso(r.StartedAtUtc),
        completed_at = r.CompletedAtUtc.HasValue ? Iso(r.CompletedAtUtc.Value) : null,
        output_path = r.OutputPath,
        nested_role = r.NestedRole ?? "none",
        parent_recording_id = r.ParentRecordingId ?? "",
        nested_session_id = r.NestedSessionId ?? "",
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

        var audioStatus = rec.Microphone
            ? (m.AudioStatus ?? (IsTerminalState(rec.State) ? "unknown" : "pending"))
            : "not_requested";
        var audioContinuityStatus = rec.Microphone
            ? (m.AudioContinuityStatus ?? (IsTerminalState(rec.State) ? "not_checked" : "pending"))
            : "not_checked";

        if (!full)
            return new { path = actualPath, size_bytes = m.SizeBytes, duration_seconds = m.DurationSeconds, container, codec, audio_status = audioStatus, audio_continuity_status = audioContinuityStatus, warnings };
        return new
        {
            path = actualPath, exists, size_bytes = m.SizeBytes,
            duration_seconds = m.DurationSeconds, created_at = Iso(rec.CompletedAtUtc ?? DateTime.UtcNow),
            container, codec, width = m.Width, height = m.Height, fps = m.Fps,
            capture_method = m.CaptureMethod ?? "",
            command_args = rec.Config?.CommandArgs ?? "",
            backend = rec.BackendType,
            source_type = rec.SourceType,
            audio_status = audioStatus,
            audio_continuity_status = audioContinuityStatus,
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

    private static string GetSummaryField(object summary, string field)
    {
        var type = summary.GetType();
        var prop = type.GetProperty(field);
        if (prop == null) return "";
        var value = prop.GetValue(summary);
        return value?.ToString() ?? "";
    }

    /// <summary>
    /// Null-object tray context used only by internal test seams that may run
    /// without a real tray. All operations are no-ops.
    /// </summary>
    private sealed class NullTrayContext : ITrayContext
    {
        public static ITrayContext Instance { get; } = new NullTrayContext();
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
