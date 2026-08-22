using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.App;

/// <summary>
/// Manages the lifecycle of <see cref="RecordingIndicatorForm"/> border windows and
/// <see cref="RecordingStopControlForm"/> floating stop buttons for active recordings.
/// Thread-safe for UI-thread use; all public methods must be called on the WinForms UI thread.
/// </summary>
internal sealed class RecordingIndicatorManager
{
    private readonly Dictionary<string, RecordingIndicatorForm> _indicators = new();
    private readonly Dictionary<string, RecordingStopControlForm> _stopControls = new();
    private readonly Dictionary<string, CountdownOverlayForm> _countdownOverlays = new();
    private readonly AuditLogger _audit;
    private readonly Action<string> _onStopRequested;
    private readonly IDisplayDpiResolver _dpiResolver;
    private readonly Func<string, RecordingIndicatorPresentation, DateTime, int?, string?, Func<IUiTextProvider>, RecordingIndicatorForm> _formFactory;
    private readonly Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, CaptureVisibilityMode, RecordingStopControlForm> _stopControlFactory;
    private readonly Func<IUiTextProvider, Font, DisplayDpiInfo, Size> _stopControlSizeProvider;
    private Func<IUiTextProvider> _textProviderFactory = null!;

    public RecordingIndicatorManager(AuditLogger audit)
        : this(audit, _ => { })
    {
        _textProviderFactory = () => new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    public RecordingIndicatorManager(AuditLogger audit, Action<string> onStopRequested, IUiTextProvider? textProvider = null)
        : this(audit, onStopRequested, (id, p, s, d, r, factory) => DefaultFormFactory(id, p, s, d, r, factory), CreateStopControlFactory(textProvider), new DisplayDpiResolver())
    {
        _textProviderFactory = () => textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    /// <summary>
    /// Creates a manager that resolves the text provider fresh for each new stop control.
    /// This avoids capturing a stale <see cref="IUiTextProvider"/> when the UI language changes.
    /// </summary>
    public RecordingIndicatorManager(AuditLogger audit, Action<string> onStopRequested, Func<IUiTextProvider> textProviderFactory)
        : this(audit, onStopRequested, (id, p, s, d, r, factory) => DefaultFormFactory(id, p, s, d, r, factory), (id, bounds, size, dpi, mode) => new RecordingStopControlForm(id, bounds, size, dpi, mode, textProviderFactory()), new DisplayDpiResolver())
    {
        _textProviderFactory = textProviderFactory;
    }

    internal RecordingIndicatorManager(
        AuditLogger audit,
        Func<string, RecordingIndicatorPresentation, DateTime, int?, string?, RecordingIndicatorForm> formFactory)
        : this(audit, _ => { }, (id, p, s, d, r, _) => formFactory(id, p, s, d, r), DefaultStopControlFactory, new DisplayDpiResolver())
    {
        _textProviderFactory = () => new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    /// <summary>
    /// Legacy constructor for tests that create forms directly from presentation and visibility mode.
    /// </summary>
    internal RecordingIndicatorManager(
        AuditLogger audit,
        Action<string> onStopRequested,
        Func<string, RecordingIndicatorPresentation, DateTime, int?, string?, RecordingIndicatorForm> formFactory,
        Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, CaptureVisibilityMode, RecordingStopControlForm> stopControlFactory,
        IDisplayDpiResolver? dpiResolver = null,
        Func<IUiTextProvider, Font, DisplayDpiInfo, Size>? stopControlSizeProvider = null)
        : this(audit, onStopRequested, (id, p, s, d, r, _) => formFactory(id, p, s, d, r), stopControlFactory, dpiResolver, stopControlSizeProvider)
    {
    }

    /// <summary>
    /// Legacy constructor for tests that create forms directly from bounds and size without
    /// needing to construct a <see cref="RecordingIndicatorPresentation"/>.
    /// </summary>
    internal RecordingIndicatorManager(
        AuditLogger audit,
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> formFactory)
        : this(audit, _ => { }, WrapLegacyFormFactory(formFactory), DefaultStopControlFactory, new DisplayDpiResolver())
    {
    }

    /// <summary>
    /// Legacy constructor for tests that create forms directly from bounds and size without
    /// needing to construct a <see cref="RecordingIndicatorPresentation"/> or visibility mode.
    /// </summary>
    internal RecordingIndicatorManager(
        AuditLogger audit,
        Action<string> onStopRequested,
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> formFactory,
        Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, RecordingStopControlForm> stopControlFactory,
        IDisplayDpiResolver? dpiResolver = null,
        Func<IUiTextProvider, Font, DisplayDpiInfo, Size>? stopControlSizeProvider = null)
        : this(audit, onStopRequested, WrapLegacyFormFactory(formFactory), (id, bounds, size, dpi, _) => stopControlFactory(id, bounds, size, dpi), dpiResolver, stopControlSizeProvider)
    {
    }

    internal RecordingIndicatorManager(
        AuditLogger audit,
        Action<string> onStopRequested,
        Func<string, RecordingIndicatorPresentation, DateTime, int?, string?, Func<IUiTextProvider>, RecordingIndicatorForm> formFactory,
        Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, CaptureVisibilityMode, RecordingStopControlForm> stopControlFactory,
        IDisplayDpiResolver? dpiResolver = null,
        Func<IUiTextProvider, Font, DisplayDpiInfo, Size>? stopControlSizeProvider = null)
    {
        _audit = audit;
        _onStopRequested = onStopRequested;
        _formFactory = formFactory;
        _stopControlFactory = stopControlFactory;
        _dpiResolver = dpiResolver ?? new DisplayDpiResolver();
        _stopControlSizeProvider = stopControlSizeProvider ?? DefaultStopControlSizeProvider;
        _textProviderFactory = () => new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    private static RecordingIndicatorForm DefaultFormFactory(
        string recordingId,
        RecordingIndicatorPresentation presentation,
        DateTime startedAtUtc,
        int? durationSeconds,
        string? nestedRole,
        Func<IUiTextProvider> textProviderFactory)
    {
        return new RecordingIndicatorForm(recordingId, presentation, startedAtUtc, durationSeconds, nestedRole, null, textProviderFactory);
    }

    private static RecordingStopControlForm DefaultStopControlFactory(
        string recordingId,
        RecordingStopControlBounds bounds,
        Size controlSize,
        DisplayDpiInfo dpiInfo,
        CaptureVisibilityMode mode)
    {
        return new RecordingStopControlForm(recordingId, bounds, controlSize, dpiInfo, mode);
    }

    private static Func<string, RecordingIndicatorPresentation, DateTime, int?, string?, Func<IUiTextProvider>, RecordingIndicatorForm> WrapLegacyFormFactory(
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> legacy)
    {
        return (id, presentation, started, duration, role, _) =>
            legacy(id, presentation.WindowBounds, started, duration, role);
    }

    private static Size DefaultStopControlSizeProvider(IUiTextProvider text, Font font, DisplayDpiInfo dpi)
    {
        // Measure at the current process DPI, then scale to the target monitor DPI so that
        // forced DisplayDpiInfo values are respected in tests and the combined plan uses the
        // same physical pixel semantics for both label and stop control.
        var screenSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);
        int screenDpi = GetSystemDpi();
        if (screenDpi <= 0)
            screenDpi = 96;

        int effectiveDpi = Math.Max(dpi.DpiX, dpi.DpiY);
        if (effectiveDpi <= 0)
            effectiveDpi = 96;

        float scale = effectiveDpi / (float)screenDpi;
        return new Size(
            (int)Math.Ceiling(screenSize.Width * scale),
            (int)Math.Ceiling(screenSize.Height * scale));
    }

    private static int GetSystemDpi()
    {
        using var temp = new Control();
        return temp.DeviceDpi;
    }

    private static Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, CaptureVisibilityMode, RecordingStopControlForm> CreateStopControlFactory(IUiTextProvider? textProvider)
    {
        return (recordingId, bounds, size, dpi, mode) => new RecordingStopControlForm(recordingId, bounds, size, dpi, mode, textProvider);
    }

    /// <summary>
    /// Returns a snapshot of current indicator forms for tests.
    /// </summary>
    internal IReadOnlyDictionary<string, RecordingIndicatorForm> IndicatorsForTests => new Dictionary<string, RecordingIndicatorForm>(_indicators);

    /// <summary>
    /// Returns a snapshot of current stop-control forms for tests.
    /// </summary>
    internal IReadOnlyDictionary<string, RecordingStopControlForm> StopControlsForTests => new Dictionary<string, RecordingStopControlForm>(_stopControls);

    /// <summary>
    /// Shows one aggregate Chapter Marks result in the existing indicator layer.
    /// A preferred recording (outer first, supplied by TrayContext) is used when
    /// its indicator is actually visible; otherwise a deterministic visible
    /// indicator is selected. No second popup or independent timer is created.
    /// </summary>
    internal void ShowChapterMarkFeedback(string text, TimeSpan duration, string? preferredRecordingId = null)
    {
        _audit.Log("tray.chapter_mark_feedback_presenter_called", new
        {
            preferred_recording_id = preferredRecordingId ?? "none",
            duration_ms = Math.Clamp((int)Math.Round(duration.TotalMilliseconds), 1, 60_000)
        });

        var selected = SelectFeedbackIndicator(preferredRecordingId);
        if (selected == null)
        {
            _audit.Log("tray.chapter_mark_feedback_error", new
            {
                recording_id = preferredRecordingId ?? "none",
                error_code = "indicator_not_visible"
            });
            throw new InvalidOperationException("No visible recording indicator is available.");
        }

        var (recordingId, indicator) = selected.Value;
        _audit.Log("tray.chapter_mark_feedback_indicator_selected", new
        {
            recording_id = recordingId,
            indicator_visible = indicator.Visible,
            indicator_handle_created = indicator.IsHandleCreated,
            actual_window_dpi = indicator.ActualWindowDpiForTests,
            capture_visibility_mode = indicator.CaptureVisibilityModeForTests.ToString().ToLowerInvariant()
        });

        try
        {
            indicator.ShowTransientFeedback(text, duration);
        }
        catch
        {
            _audit.Log("tray.chapter_mark_feedback_error", new
            {
                recording_id = recordingId,
                error_code = "feedback_submission_failed"
            });
            throw;
        }

        if (!indicator.FeedbackVisibleForTests
            || !indicator.FeedbackControlVisibleForTests
            || !indicator.FeedbackBoundsInsideClientForTests
            || !indicator.FeedbackBoundsNonEmptyForTests)
        {
            _audit.Log("tray.chapter_mark_feedback_error", new
            {
                recording_id = recordingId,
                error_code = "feedback_not_visible_after_submit"
            });
            throw new InvalidOperationException("Chapter mark feedback was not visible after submission.");
        }

        _audit.Log("tray.chapter_mark_feedback_submitted", new
        {
            recording_id = recordingId,
            feedback_visible = indicator.FeedbackVisibleForTests,
            feedback_handle_created = indicator.FeedbackControlHandleCreatedForTests,
            bounds_non_empty = indicator.FeedbackBoundsNonEmptyForTests,
            bounds_inside_client = indicator.FeedbackBoundsInsideClientForTests,
            frontmost_child = indicator.FeedbackIsFrontmostChildForTests,
            opaque_background = indicator.FeedbackBackgroundOpaqueForTests,
            actual_window_dpi = indicator.ActualWindowDpiForTests
        });
    }

    private KeyValuePair<string, RecordingIndicatorForm>? SelectFeedbackIndicator(string? preferredRecordingId)
    {
        if (!string.IsNullOrEmpty(preferredRecordingId)
            && _indicators.TryGetValue(preferredRecordingId, out var preferred)
            && IsFeedbackIndicatorVisible(preferred))
        {
            return new KeyValuePair<string, RecordingIndicatorForm>(preferredRecordingId, preferred);
        }

        var selected = _indicators
            .Where(pair => IsFeedbackIndicatorVisible(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected.Key == null ? null : selected;
    }

    private static bool IsFeedbackIndicatorVisible(RecordingIndicatorForm indicator)
    {
        return !indicator.IsDisposed && indicator.IsHandleCreated && indicator.Visible;
    }

    /// <summary>
    /// Computes the combined presentation plan for the indicator and stop control before any UI
    /// is created. If the indicator can safely enter parent-visible mode but the stop control
    /// cannot be placed safely, the whole plan falls back to exclude-from-capture mode so that
    /// indicator, label and stop control are decided jointly.
    /// </summary>
    internal RecordingControlPlan? ComputeControlPlan(
        Recording recording,
        Recording? parentRecording,
        Rectangle virtualScreen,
        DisplayDpiInfo? forcedDpi = null,
        string? parentFallbackReason = null)
    {
        var bounds = recording.Config.Bounds;
        var indicatorBounds = new RecordingIndicatorBounds(bounds.x, bounds.y, bounds.w, bounds.h);
        var clamped = RecordingIndicatorGeometry.TryClampToVirtualScreen(indicatorBounds);
        if (clamped == null)
            return null;

        var targetArea = new Rectangle(clamped.X, clamped.Y, clamped.Width, clamped.Height);
        var dpiInfo = forcedDpi ?? _dpiResolver.Resolve(targetArea);

        using var labelFont = new Font("Segoe UI", 9, FontStyle.Bold);
        var labelSize = recording.IsScreenshotSeries
            ? RecordingIndicatorForm.MeasureSeriesLabelSize(labelFont, new Padding(4, 2, 4, 2), dpiInfo)
            : RecordingIndicatorForm.MeasureLabelSize(
                recording.NestedRole,
                recording.DurationSeconds,
                labelFont,
                new Padding(4, 2, 4, 2),
                dpiInfo);

        using var stopControlFont = new Font("Segoe UI", 8, FontStyle.Bold);
        var stopControlTextProvider = _textProviderFactory();
        var controlSize = _stopControlSizeProvider(stopControlTextProvider, stopControlFont, dpiInfo);

        var indicatorPlan = RecordingIndicatorGeometry.ComputePresentationPlan(
            recording,
            clamped,
            parentRecording,
            labelSize,
            virtualScreen,
            parentFallbackReason);

        // For parent-visible mode, the stop control must also fit safely outside the inner
        // capture rectangle and inside the parent capture rectangle, without overlapping any
        // already-active stop controls. If any of these constraints cannot be satisfied, the
        // entire recording falls back to exclude-from-capture mode.
        if (indicatorPlan.Mode == CaptureVisibilityMode.ParentVisible)
        {
            RecordingIndicatorBounds? parentBounds = indicatorPlan.ParentCaptureBounds;
            var preferredStopBounds = RecordingStopControlGeometry.ComputeBounds(
                clamped,
                controlSize,
                recording.NestedRole,
                virtualScreen,
                parentBounds,
                CaptureVisibilityMode.ParentVisible);

            var occupied = _stopControls.Values.Select(s => s.PlacementBounds).ToList();
            var forbiddenZone = new Rectangle(clamped.X, clamped.Y, clamped.Width, clamped.Height);
            Rectangle? allowedZone = parentBounds != null
                ? new Rectangle(parentBounds.X, parentBounds.Y, parentBounds.Width, parentBounds.Height)
                : null;

            bool hasSafeStop = RecordingStopControlGeometry.TryResolveCollision(
                preferredStopBounds,
                controlSize,
                virtualScreen,
                occupied,
                forbiddenZone,
                allowedZone,
                out var safeStopBounds);

            if (hasSafeStop && safeStopBounds != null)
            {
                return new RecordingControlPlan(indicatorPlan, safeStopBounds, controlSize, dpiInfo, null);
            }

            // Joint fallback: recompute the indicator in exclude mode and place an ordinary stop control.
            var excludeIndicatorPlan = RecordingIndicatorGeometry.ComputeExcludedPlan(
                clamped,
                parentRecording,
                labelSize,
                virtualScreen,
                "no_safe_stop_position");

            var excludeStopBounds = RecordingStopControlGeometry.ComputeBounds(
                clamped,
                controlSize,
                recording.NestedRole,
                virtualScreen);
            var excludeOccupied = _stopControls.Values.Select(s => s.PlacementBounds).ToList();
            var resolvedExcludeStop = RecordingStopControlGeometry.ResolveCollision(
                excludeStopBounds,
                controlSize,
                virtualScreen,
                excludeOccupied);

            return new RecordingControlPlan(excludeIndicatorPlan, resolvedExcludeStop, controlSize, dpiInfo, "no_safe_stop_position");
        }

        // Exclude mode: ordinary stop placement with last-resort fallback permitted.
        var ordinaryStopBounds = RecordingStopControlGeometry.ComputeBounds(
            clamped,
            controlSize,
            recording.NestedRole,
            virtualScreen);
        var ordinaryOccupied = _stopControls.Values.Select(s => s.PlacementBounds).ToList();
        var resolvedOrdinaryStop = RecordingStopControlGeometry.ResolveCollision(
            ordinaryStopBounds,
            controlSize,
            virtualScreen,
            ordinaryOccupied);

        return new RecordingControlPlan(indicatorPlan, resolvedOrdinaryStop, controlSize, dpiInfo, indicatorPlan.FallbackReason);
    }

    /// <summary>
    /// Switches an existing indicator to the preparing phase (amber border + label).
    /// If no indicator exists yet, shows one first.
    /// </summary>
    public void ShowPreparing(Recording recording, Recording? parentRecording = null, string? parentFallbackReason = null)
    {
        EnsureIndicator(recording, parentRecording, parentFallbackReason);
        if (_indicators.TryGetValue(recording.Id, out var indicator))
        {
            indicator.SetPhase(RecordingIndicatorPhase.Preparing);
        }
    }

    /// <summary>
    /// Shows the large countdown overlay in the center of the capture region.
    /// </summary>
    public void ShowCountdown(Recording recording, int remainingSeconds)
    {
        // Keep the indicator in countdown phase (amber border) while the large overlay shows the digit.
        EnsureIndicator(recording, null, null);
        if (_indicators.TryGetValue(recording.Id, out var indicator))
        {
            indicator.SetPhase(RecordingIndicatorPhase.Countdown, remainingSeconds);
        }

        if (!_countdownOverlays.TryGetValue(recording.Id, out var overlay))
        {
            var bounds = recording.Config.Bounds;
            var overlayBounds = ComputeCountdownBounds(
                new Rectangle(bounds.x, bounds.y, bounds.w, bounds.h),
                SystemInformation.VirtualScreen);
            if (overlayBounds.Width <= 0 || overlayBounds.Height <= 0)
            {
                _audit.Log("recording_countdown_overlay.bounds_error", new
                {
                    recording_id = recording.Id,
                    target_bounds = new { bounds.x, bounds.y, bounds.w, bounds.h },
                    virtual_screen = SystemInformation.VirtualScreen
                });
                return;
            }
            overlay = new CountdownOverlayForm(overlayBounds);
            _countdownOverlays[recording.Id] = overlay;
            overlay.SetNumber(remainingSeconds);
            try { overlay.Show(); }
            catch (Exception ex)
            {
                _audit.Log("recording_countdown_overlay.show_error", new
                {
                    recording_id = recording.Id,
                    error = ex.Message
                });
            }
        }
        else
        {
            overlay.SetNumber(remainingSeconds);
        }
    }

    /// <summary>
    /// Hides the countdown overlay and switches the indicator to the recording phase.
    /// </summary>
    public void HideCountdownAndShowRecording(Recording recording)
    {
        if (_countdownOverlays.TryGetValue(recording.Id, out var overlay))
        {
            _countdownOverlays.Remove(recording.Id);
            try { overlay.CloseWithoutResult(); } catch { }
        }

        if (_indicators.TryGetValue(recording.Id, out var indicator))
        {
            indicator.SetPhase(RecordingIndicatorPhase.Recording);
        }
    }

    /// <summary>
    /// Hides the countdown overlay but keeps the indicator in the amber preparing
    /// phase. Used when the countdown reaches zero: the red REC phase is shown
    /// only when real first-frame evidence arrives, never at countdown zero.
    /// </summary>
    public void HideCountdownOverlay(Recording recording)
    {
        if (_countdownOverlays.TryGetValue(recording.Id, out var overlay))
        {
            _countdownOverlays.Remove(recording.Id);
            try { overlay.CloseWithoutResult(); } catch { }
        }

        if (_indicators.TryGetValue(recording.Id, out var indicator))
        {
            indicator.SetPhase(RecordingIndicatorPhase.Preparing);
        }
    }

    internal void ShowSeriesProgress(Recording recording, int captured, int planned, DateTime? nextCaptureDueAtUtc)
    {
        EnsureIndicator(recording, null, null);
        if (_indicators.TryGetValue(recording.Id, out var indicator))
            indicator.SetSeriesProgress(captured, planned);
    }

    /// <summary>
    /// Switches an existing indicator to the finalizing phase (gray border + saving label).
    /// Does not close the indicator; the engine closes it after terminal state.
    /// </summary>
    public void ShowFinalizing(Recording recording)
    {
        if (_indicators.TryGetValue(recording.Id, out var indicator))
        {
            indicator.SetPhase(RecordingIndicatorPhase.Finalizing);
        }
    }

    /// <summary>
    /// Shows or replaces the indicator and stop control for the given recording.
    /// A single combined plan is computed before any UI is created so that failures in any part
    /// cause a joint fallback to exclude-from-capture mode.
    /// </summary>
    public void ShowFor(Recording recording, Recording? parentRecording = null, string? parentFallbackReason = null)
    {
        if (recording == null) throw new ArgumentNullException(nameof(recording));

        CloseFor(recording.Id, "recording_indicator.replaced");

        var bounds = recording.Config.Bounds;
        var indicatorBounds = new RecordingIndicatorBounds(bounds.x, bounds.y, bounds.w, bounds.h);
        var clamped = RecordingIndicatorGeometry.TryClampToVirtualScreen(indicatorBounds);

        if (clamped == null)
        {
            var reason = bounds.w <= 0 || bounds.h <= 0 ? "invalid_bounds" : "outside_virtual_screen";
            _audit.Log("recording_indicator.skipped", new
            {
                recording_id = recording.Id,
                reason,
                source_type = recording.SourceType,
                bounds = new { x = bounds.x, y = bounds.y, w = bounds.w, h = bounds.h }
            });
            return;
        }

        var plan = ComputeControlPlan(recording, parentRecording, SystemInformation.VirtualScreen, parentFallbackReason: parentFallbackReason);
        if (plan == null)
        {
            // Should not happen because clamped != null was checked above, but guard anyway.
            _audit.Log("recording_indicator.skipped", new
            {
                recording_id = recording.Id,
                reason = "plan_unavailable",
                source_type = recording.SourceType,
                bounds = new { x = bounds.x, y = bounds.y, w = bounds.w, h = bounds.h }
            });
            return;
        }

        var verified = VerifyAndBuildForms(recording, plan, parentRecording);
        if (verified == null)
            return;

        ShowFinalForms(recording, verified.Indicator, verified.StopControl, verified.FinalPlan, verified.Retried);
    }


    /// <summary>
    /// Creates the indicator and stop-control windows while they are still hidden, reads the
    /// actual HWND DPI, and recomputes the combined plan if the real DPI differs from the
    /// planned DPI. The returned forms are the final forms and must be shown by the caller.
    /// </summary>
    internal sealed record VerificationResult(
        RecordingIndicatorForm Indicator,
        RecordingStopControlForm StopControl,
        RecordingControlPlan FinalPlan,
        bool Retried);

    internal VerificationResult? VerifyAndBuildForms(Recording recording, RecordingControlPlan plan, Recording? parentRecording)
    {
        RecordingIndicatorForm? indicator = null;
        RecordingStopControlForm? stopControl = null;
        try
        {
            indicator = _formFactory(
                recording.Id,
                plan.IndicatorPresentation,
                recording.StartedAtUtc,
                recording.DurationSeconds,
                recording.NestedRole,
                _textProviderFactory);
            stopControl = _stopControlFactory(
                recording.Id,
                plan.StopBounds,
                plan.StopControlSize,
                plan.DpiInfo,
                plan.IndicatorPresentation.Mode);

            // Create hidden HWNDs so we can read the actual DPI before any window becomes visible.
            _ = indicator.Handle;
            _ = stopControl.Handle;

            int plannedDpi = (int)Math.Round(plan.DpiInfo.Scale * 96);
            int actualDpi = stopControl.ActualWindowDpiForTests > 0
                ? stopControl.ActualWindowDpiForTests
                : indicator.ActualWindowDpiForTests;

            if (actualDpi > 0 && actualDpi != plannedDpi)
            {
                DisposeCandidate(indicator);
                DisposeCandidate(stopControl);
                indicator = null;
                stopControl = null;

                var actualDpiInfo = plan.DpiInfo with
                {
                    DpiX = actualDpi,
                    DpiY = actualDpi,
                    Scale = actualDpi / 96f
                };

                var retryPlan = ComputeControlPlan(
                    recording,
                    parentRecording,
                    SystemInformation.VirtualScreen,
                    actualDpiInfo,
                    parentFallbackReason: plan.IndicatorPresentation.FallbackReason);
                if (retryPlan == null)
                {
                    _audit.Log("recording_indicator.skipped", new
                    {
                        recording_id = recording.Id,
                        reason = "retry_plan_unavailable",
                        source_type = recording.SourceType,
                        bounds = new { x = recording.Config.Bounds.x, y = recording.Config.Bounds.y, w = recording.Config.Bounds.w, h = recording.Config.Bounds.h }
                    });
                    return null;
                }

                indicator = _formFactory(
                    recording.Id,
                    retryPlan.IndicatorPresentation,
                    recording.StartedAtUtc,
                    recording.DurationSeconds,
                    recording.NestedRole,
                    _textProviderFactory);
                stopControl = _stopControlFactory(
                    recording.Id,
                    retryPlan.StopBounds,
                    retryPlan.StopControlSize,
                    retryPlan.DpiInfo,
                    retryPlan.IndicatorPresentation.Mode);

                return new VerificationResult(indicator, stopControl, retryPlan, true);
            }

            return new VerificationResult(indicator, stopControl, plan, false);
        }
        catch (Exception ex)
        {
            DisposeCandidate(indicator);
            DisposeCandidate(stopControl);
            _audit.Log("recording_indicator.show_error", new
            {
                recording_id = recording.Id,
                error = ex.Message,
                stage = "verify_and_build"
            });
            return null;
        }
    }

    private void ShowFinalForms(
        Recording recording,
        RecordingIndicatorForm indicator,
        RecordingStopControlForm stopControl,
        RecordingControlPlan plan,
        bool retried)
    {
        _indicators[recording.Id] = indicator;

        if (recording.IsScreenshotSeries)
            indicator.SetSeriesProgress(0, recording.ScreenshotSeries?.PlannedFrameCount ?? 0);

        _audit.Log("recording_indicator.shown", new
        {
            recording_id = recording.Id,
            source_type = recording.SourceType,
            bounds = new { x = plan.IndicatorPresentation.WindowBounds.X, y = plan.IndicatorPresentation.WindowBounds.Y, w = plan.IndicatorPresentation.WindowBounds.Width, h = plan.IndicatorPresentation.WindowBounds.Height },
            duration_seconds = recording.DurationSeconds,
            nested_role = recording.NestedRole,
            capture_visibility_mode = plan.IndicatorPresentation.Mode.ToString().ToLowerInvariant(),
            display_affinity_requested = plan.IndicatorPresentation.DisplayAffinityRequested,
            parent_recording_id = recording.ParentRecordingId,
            fallback_reason = plan.IndicatorPresentation.FallbackReason
        });

        try
        {
            indicator.Show();
        }
        catch (Exception ex)
        {
            _indicators.Remove(recording.Id);
            try { indicator.Dispose(); } catch { }
            _audit.Log("recording_indicator.show_error", new
            {
                recording_id = recording.Id,
                error = ex.Message
            });
            try { stopControl.Dispose(); } catch { }
            return;
        }

        stopControl.StopClicked += OnStopControlClicked;
        _stopControls[recording.Id] = stopControl;

        try
        {
            stopControl.Show();
        }
        catch (Exception ex)
        {
            stopControl.StopClicked -= OnStopControlClicked;
            _stopControls.Remove(recording.Id);
            try { stopControl.Dispose(); } catch { }
            _audit.Log("recording_stop_control.show_error", new
            {
                recording_id = recording.Id,
                error = ex.Message
            });
            return;
        }

        var stopBounds = plan.StopBounds;
        _audit.Log("recording_stop_control.shown", new
        {
            recording_id = recording.Id,
            source_type = recording.SourceType,
            target_monitor = plan.DpiInfo.MonitorId,
            target_dpi_x = plan.DpiInfo.DpiX,
            target_dpi_y = plan.DpiInfo.DpiY,
            dpi_scale = plan.DpiInfo.Scale,
            dpi_fallback = plan.DpiInfo.IsFallback,
            dpi_fallback_reason = plan.DpiInfo.FallbackReason,
            planned_bounds = new { x = stopBounds.X, y = stopBounds.Y, w = stopBounds.Width, h = stopBounds.Height },
            bounds = new { x = stopControl.Bounds.X, y = stopControl.Bounds.Y, w = stopControl.Bounds.Width, h = stopControl.Bounds.Height },
            actual_window_dpi = stopControl.ActualWindowDpiForTests,
            dpi_retry = retried,
            nested_role = recording.NestedRole,
            capture_visibility_mode = plan.IndicatorPresentation.Mode.ToString().ToLowerInvariant(),
            display_affinity_requested = plan.IndicatorPresentation.DisplayAffinityRequested,
            parent_recording_id = recording.ParentRecordingId,
            fallback_reason = plan.FallbackReason
        });
    }

    private static void DisposeCandidate(Form? form)
    {
        if (form == null)
            return;
        try { form.Close(); } catch { }
        try { form.Dispose(); } catch { }
    }

    private void OnStopControlClicked(string recordingId)
    {
        _audit.Log("recording_stop_control.clicked", new { recording_id = recordingId });
        _onStopRequested(recordingId);
    }

    /// <summary>
    /// Resets the stop control for the given recording id after a stop failure so the user can retry.
    /// Safe no-op if the control does not exist or has already been closed.
    /// </summary>
    public void ResetStopControlAfterFailure(string recordingId)
    {
        if (_stopControls.TryGetValue(recordingId, out var stopControl))
        {
            try
            {
                stopControl.ResetForRetry();
            }
            catch (Exception ex)
            {
                _audit.Log("recording_stop_control.reset_error", new
                {
                    recording_id = recordingId,
                    error = ex.Message
                });
            }
        }
    }

    /// <summary>
    /// Closes the indicator and stop control for the given recording id.
    /// </summary>
    public void CloseFor(string recordingId, string reasonAuditEvent)
    {
        if (_indicators.TryGetValue(recordingId, out var indicator))
        {
            _indicators.Remove(recordingId);
            try { indicator.CloseWithoutResult(); } catch { }

            _audit.Log("recording_indicator.closed", new
            {
                recording_id = recordingId,
                reason = reasonAuditEvent
            });
        }

        if (_stopControls.TryGetValue(recordingId, out var stopControl))
        {
            _stopControls.Remove(recordingId);
            stopControl.StopClicked -= OnStopControlClicked;
            try { stopControl.CloseWithoutResult(); } catch { }

            _audit.Log("recording_stop_control.closed", new
            {
                recording_id = recordingId,
                reason = reasonAuditEvent
            });
        }

        if (_countdownOverlays.TryGetValue(recordingId, out var overlay))
        {
            _countdownOverlays.Remove(recordingId);
            try { overlay.CloseWithoutResult(); } catch { }
        }
    }

    private void EnsureIndicator(Recording recording, Recording? parentRecording, string? parentFallbackReason)
    {
        if (_indicators.ContainsKey(recording.Id))
            return;

        ShowFor(recording, parentRecording, parentFallbackReason);
    }

    /// <summary>
    /// Testable geometry seam for the countdown overlay. The result is always
    /// contained by both the approved target and the virtual screen; unlike
    /// the indicator border, the countdown overlay is never enlarged beyond
    /// the approved capture rectangle.
    /// </summary>
    internal static Rectangle ComputeCountdownBoundsForTests(Rectangle targetBounds, Rectangle virtualScreen)
        => ComputeCountdownBounds(targetBounds, virtualScreen);

    private static Rectangle ComputeCountdownBounds(Rectangle targetBounds, Rectangle virtualScreen)
    {
        var visibleTarget = Rectangle.Intersect(targetBounds, virtualScreen);
        if (visibleTarget.Width <= 0 || visibleTarget.Height <= 0)
            return Rectangle.Empty;

        int size = Math.Min(visibleTarget.Width, visibleTarget.Height);
        int left = visibleTarget.X + (visibleTarget.Width - size) / 2;
        int top = visibleTarget.Y + (visibleTarget.Height - size) / 2;
        return new Rectangle(left, top, size, size);
    }

    /// <summary>
    /// Closes all indicators and stop controls. Uses a union snapshot of both dictionaries
    /// so that a partially-successful ShowFor (e.g. indicator shown but stop control failed)
    /// still leaves no TopMost window behind.
    /// </summary>
    public void CloseAll(string reasonAuditEvent)
    {
        var ids = new HashSet<string>(_indicators.Keys);
        foreach (var id in _stopControls.Keys)
            ids.Add(id);
        foreach (var id in _countdownOverlays.Keys)
            ids.Add(id);

        foreach (var id in ids)
        {
            CloseFor(id, reasonAuditEvent);
        }
    }
}
