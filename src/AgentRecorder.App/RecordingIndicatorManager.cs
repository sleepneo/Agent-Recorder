using System;
using System.Collections.Generic;
using System.Drawing;
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
    private readonly AuditLogger _audit;
    private readonly Action<string> _onStopRequested;
    private readonly IDisplayDpiResolver _dpiResolver;
    private readonly Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> _formFactory;
    private readonly Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, RecordingStopControlForm> _stopControlFactory;
    private readonly Func<IUiTextProvider, Font, DisplayDpiInfo, Size> _stopControlSizeProvider;
    private Func<IUiTextProvider> _textProviderFactory = null!;

    public RecordingIndicatorManager(AuditLogger audit)
        : this(audit, _ => { })
    {
        _textProviderFactory = () => new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    public RecordingIndicatorManager(AuditLogger audit, Action<string> onStopRequested, IUiTextProvider? textProvider = null)
        : this(audit, onStopRequested, DefaultFormFactory, CreateStopControlFactory(textProvider), new DisplayDpiResolver())
    {
        _textProviderFactory = () => textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    /// <summary>
    /// Creates a manager that resolves the text provider fresh for each new stop control.
    /// This avoids capturing a stale <see cref="IUiTextProvider"/> when the UI language changes.
    /// </summary>
    public RecordingIndicatorManager(AuditLogger audit, Action<string> onStopRequested, Func<IUiTextProvider> textProviderFactory)
        : this(audit, onStopRequested, DefaultFormFactory, (id, bounds, size, dpi) => new RecordingStopControlForm(id, bounds, size, dpi, textProviderFactory()), new DisplayDpiResolver())
    {
        _textProviderFactory = textProviderFactory;
    }

    internal RecordingIndicatorManager(
        AuditLogger audit,
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> formFactory)
        : this(audit, _ => { }, formFactory, DefaultStopControlFactory, new DisplayDpiResolver())
    {
        _textProviderFactory = () => new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    internal RecordingIndicatorManager(
        AuditLogger audit,
        Action<string> onStopRequested,
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> formFactory,
        Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, RecordingStopControlForm> stopControlFactory,
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
        RecordingIndicatorBounds bounds,
        DateTime startedAtUtc,
        int? durationSeconds,
        string? nestedRole)
    {
        return new RecordingIndicatorForm(recordingId, bounds, startedAtUtc, durationSeconds, nestedRole);
    }

    private static RecordingStopControlForm DefaultStopControlFactory(
        string recordingId,
        RecordingStopControlBounds bounds,
        Size controlSize,
        DisplayDpiInfo dpiInfo)
    {
        return new RecordingStopControlForm(recordingId, bounds, controlSize, dpiInfo);
    }

    private static Size DefaultStopControlSizeProvider(IUiTextProvider text, Font font, DisplayDpiInfo dpi)
    {
        var measureBounds = dpi.MonitorBounds.IsEmpty ? SystemInformation.VirtualScreen : dpi.MonitorBounds;
        return RecordingStopControlLayout.MeasurePreferredSize(text, font, measureBounds);
    }

    private static Func<string, RecordingStopControlBounds, Size, DisplayDpiInfo, RecordingStopControlForm> CreateStopControlFactory(IUiTextProvider? textProvider)
    {
        return (recordingId, bounds, size, dpi) => new RecordingStopControlForm(recordingId, bounds, size, dpi, textProvider);
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
    /// Shows or replaces the indicator and stop control for the given recording.
    /// Each creation step is guarded so that a failure in one does not prevent the other
    /// or bubble up to the recording engine.
    /// </summary>
    public void ShowFor(Recording recording)
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
        }
        else
        {
            RecordingIndicatorForm? form = null;
            try
            {
                form = _formFactory(
                    recording.Id,
                    clamped,
                    recording.StartedAtUtc,
                    recording.DurationSeconds,
                    recording.NestedRole);

                _indicators[recording.Id] = form;

                _audit.Log("recording_indicator.shown", new
                {
                    recording_id = recording.Id,
                    source_type = recording.SourceType,
                    bounds = new { x = clamped.X, y = clamped.Y, w = clamped.Width, h = clamped.Height },
                    duration_seconds = recording.DurationSeconds,
                    nested_role = recording.NestedRole
                });

                try
                {
                    form.Show();
                }
                catch (Exception ex)
                {
                    _indicators.Remove(recording.Id);
                    try { form.Dispose(); } catch { }
                    form = null;
                    _audit.Log("recording_indicator.show_error", new
                    {
                        recording_id = recording.Id,
                        error = ex.Message
                    });
                }
            }
            catch (Exception ex)
            {
                try { form?.Dispose(); } catch { }
                _audit.Log("recording_indicator.show_error", new
                {
                    recording_id = recording.Id,
                    error = ex.Message,
                    stage = "factory"
                });
            }
        }

        // Show the floating stop button independently. A failure here must not remove the border indicator.
        ShowStopControl(recording, clamped ?? indicatorBounds, isRetry: false);
    }

    private void ShowStopControl(Recording recording, RecordingIndicatorBounds placementSource, bool isRetry, DisplayDpiInfo? forcedDpi = null)
    {
        var stopControlTextProvider = _textProviderFactory();
        using var stopControlFont = new Font("Segoe UI", 8, FontStyle.Bold);

        var targetArea = new Rectangle(placementSource.X, placementSource.Y, placementSource.Width, placementSource.Height);
        var dpiInfo = forcedDpi ?? _dpiResolver.Resolve(targetArea);

        var controlSize = _stopControlSizeProvider(stopControlTextProvider, stopControlFont, dpiInfo);

        var preferredStopBounds = RecordingStopControlGeometry.ComputeBounds(
            placementSource,
            controlSize,
            recording.NestedRole);

        // Resolve overlap with any already-active stop controls from other recordings.
        var occupied = _stopControls.Values.Select(s => s.PlacementBounds).ToList();
        var stopBounds = RecordingStopControlGeometry.ResolveCollision(
            preferredStopBounds,
            controlSize,
            SystemInformation.VirtualScreen,
            occupied);

        RecordingStopControlForm? stopControl = null;
        try
        {
            stopControl = _stopControlFactory(recording.Id, stopBounds, controlSize, dpiInfo);
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
                stopControl = null;
                _audit.Log("recording_stop_control.show_error", new
                {
                    recording_id = recording.Id,
                    error = ex.Message
                });
                return;
            }

            _audit.Log("recording_stop_control.shown", new
            {
                recording_id = recording.Id,
                source_type = recording.SourceType,
                target_monitor = dpiInfo.MonitorId,
                target_dpi_x = dpiInfo.DpiX,
                target_dpi_y = dpiInfo.DpiY,
                dpi_scale = dpiInfo.Scale,
                dpi_fallback = dpiInfo.IsFallback,
                dpi_fallback_reason = dpiInfo.FallbackReason,
                planned_bounds = new { x = stopBounds.X, y = stopBounds.Y, w = stopBounds.Width, h = stopBounds.Height },
                bounds = new { x = stopControl.Bounds.X, y = stopControl.Bounds.Y, w = stopControl.Bounds.Width, h = stopControl.Bounds.Height },
                actual_window_dpi = stopControl.ActualWindowDpiForTests,
                dpi_retry = isRetry,
                nested_role = recording.NestedRole
            });

            // If the HWND landed on a monitor with a different DPI, recompute once with the actual DPI.
            if (!isRetry && stopControl.DpiMismatchForTests)
            {
                var actualDpi = stopControl.ActualWindowDpiForTests;
                if (actualDpi > 0)
                {
                    stopControl.StopClicked -= OnStopControlClicked;
                    _stopControls.Remove(recording.Id);
                    try { stopControl.CloseWithoutResult(); } catch { }
                    try { stopControl.Dispose(); } catch { }

                    var actualScale = actualDpi / 96f;
                    var actualDpiInfo = new DisplayDpiInfo(
                        dpiInfo.MonitorId,
                        new Rectangle(stopControl.Bounds.X, stopControl.Bounds.Y, stopControl.Bounds.Width, stopControl.Bounds.Height),
                        actualDpi, actualDpi, actualScale, dpiInfo.IsFallback, dpiInfo.FallbackReason);

                    ShowStopControl(recording, placementSource, isRetry: true, forcedDpi: actualDpiInfo);
                }
            }
        }
        catch (Exception ex)
        {
            if (stopControl != null)
            {
                stopControl.StopClicked -= OnStopControlClicked;
                try { stopControl.Dispose(); } catch { }
            }
            _audit.Log("recording_stop_control.show_error", new
            {
                recording_id = recording.Id,
                error = ex.Message,
                stage = "factory"
            });
        }
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

        foreach (var id in ids)
        {
            CloseFor(id, reasonAuditEvent);
        }
    }
}
