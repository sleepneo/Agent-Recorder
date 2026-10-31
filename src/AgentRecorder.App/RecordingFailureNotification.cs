using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal sealed record RecordingFailureNotificationRequest(string RecordingId, string ReasonCode);

internal enum RecordingFailureNotificationCloseReason
{
    UserDismissed,
    Timeout,
    ApplicationExit,
    External
}

internal readonly record struct NotificationPresentationResult(
    bool Shown,
    bool DisplayAffinityRequested,
    bool DisplayAffinityApplied);

/// <summary>
/// Narrow presenter seam used by the tray manager. The real implementation owns
/// a WinForms form; tests inject a deterministic fake and never show Shell UI.
/// </summary>
internal interface IRecordingFailureNotificationPresenter : IDisposable
{
    NotificationPresentationResult TryShow(
        RecordingFailureNotificationRequest request,
        IUiTextProvider textProvider,
        bool requireCaptureExclusion,
        Action<RecordingFailureNotificationCloseReason> onClosed);

    void Close(RecordingFailureNotificationCloseReason reason);
}

/// <summary>
/// Serializes lifecycle failures on the tray UI thread, suppresses duplicate
/// completion races, and defers a capture-visible notification when display
/// exclusion cannot be proven while another recording is active.
/// </summary>
internal sealed class RecordingFailureNotificationManager : IDisposable
{
    private sealed class PendingNotification
    {
        public PendingNotification(RecordingFailureNotificationRequest request, int requestActiveCount)
        {
            Request = request;
            RequestActiveCount = requestActiveCount;
        }

        public RecordingFailureNotificationRequest Request { get; }
        public int RequestActiveCount { get; }
    }

    private readonly AuditLogger _audit;
    private readonly Func<IUiTextProvider> _textProvider;
    private readonly Func<int> _activeRecordingCount;
    private readonly IRecordingFailureNotificationPresenter _presenter;
    private readonly Queue<PendingNotification> _queue = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private PendingNotification? _current;
    private bool _disposed;

    public RecordingFailureNotificationManager(
        AuditLogger audit,
        Func<IUiTextProvider> textProvider,
        Func<int> activeRecordingCount,
        IRecordingFailureNotificationPresenter? presenter = null)
    {
        _audit = audit;
        _textProvider = textProvider;
        _activeRecordingCount = activeRecordingCount;
        _presenter = presenter ?? new WinFormsRecordingFailureNotificationPresenter();
    }

    internal bool IsShowingForTests => _current != null;
    internal int PendingCountForTests => _queue.Count;

    public void Request(string recordingId, string reasonCode)
    {
        if (_disposed)
            return;

        if (string.IsNullOrWhiteSpace(recordingId) || !IsSupportedReason(reasonCode))
        {
            _audit.Log("recording_failure_notification.suppressed", new
            {
                recording_id = string.IsNullOrWhiteSpace(recordingId) ? "missing" : "present",
                reason_code = IsSupportedReason(reasonCode) ? reasonCode : "unsupported",
                close_reason = "invalid_request"
            });
            return;
        }

        var request = new RecordingFailureNotificationRequest(recordingId, reasonCode);
        var key = recordingId + "\u001f" + reasonCode;
        if (!_seen.Add(key))
        {
            _audit.Log("recording_failure_notification.suppressed", new
            {
                recording_id = recordingId,
                reason_code = reasonCode,
                close_reason = "duplicate"
            });
            return;
        }

        int activeCount = GetActiveRecordingCount();
        _queue.Enqueue(new PendingNotification(request, activeCount));
        _audit.Log("recording_failure_notification.requested", new
        {
            recording_id = recordingId,
            reason_code = reasonCode,
            language = _textProvider().Language.ToCultureName(),
            active_recording_count = activeCount,
            queue_position = _queue.Count
        });

        TryPresentNext();
    }

    /// <summary>
    /// Called after a recording is removed from the tray's active set. This is
    /// the only release point for requests deferred by a failed exclusion call.
    /// </summary>
    public void ActiveRecordingCountChanged()
    {
        if (!_disposed)
            TryPresentNext();
    }

    private void TryPresentNext()
    {
        if (_disposed || _current != null || !_queue.TryPeek(out var pending))
            return;

        int activeCount = GetActiveRecordingCount();
        bool requireCaptureExclusion = activeCount > 0;
        NotificationPresentationResult result;
        try
        {
            result = _presenter.TryShow(
                pending.Request,
                _textProvider(),
                requireCaptureExclusion,
                reason => OnPresentedClosed(pending, reason));
        }
        catch
        {
            result = new NotificationPresentationResult(false, false, false);
        }

        // The manager remains fail-closed even if a test or future presenter
        // reports Shown=true without proving exclusion for an active capture.
        if (result.Shown && requireCaptureExclusion && !result.DisplayAffinityApplied)
        {
            _presenter.Close(RecordingFailureNotificationCloseReason.External);
            result = result with { Shown = false };
        }

        if (result.Shown)
        {
            _queue.Dequeue();
            _current = pending;
            _audit.Log("recording_failure_notification.shown", new
            {
                recording_id = pending.Request.RecordingId,
                reason_code = pending.Request.ReasonCode,
                language = _textProvider().Language.ToCultureName(),
                active_recording_count = activeCount,
                queue_position = 1,
                display_affinity_requested = result.DisplayAffinityRequested,
                display_affinity_succeeded = result.DisplayAffinityApplied
            });
            return;
        }

        if (activeCount > 0)
        {
            // Keep the request queued. No window was allowed to become visible;
            // it will be retried only after a later active-count transition.
            _audit.Log("recording_failure_notification.deferred", new
            {
                recording_id = pending.Request.RecordingId,
                reason_code = pending.Request.ReasonCode,
                active_recording_count = activeCount,
                display_affinity_requested = result.DisplayAffinityRequested,
                display_affinity_succeeded = result.DisplayAffinityApplied,
                close_reason = "capture_exclusion_unavailable"
            });
            return;
        }

        _queue.Dequeue();
        _audit.Log("recording_failure_notification.suppressed", new
        {
            recording_id = pending.Request.RecordingId,
            reason_code = pending.Request.ReasonCode,
            active_recording_count = 0,
            display_affinity_requested = result.DisplayAffinityRequested,
            display_affinity_succeeded = result.DisplayAffinityApplied,
            close_reason = "presenter_unavailable"
        });
    }

    private void OnPresentedClosed(
        PendingNotification pending,
        RecordingFailureNotificationCloseReason reason)
    {
        if (_current == null || !ReferenceEquals(_current, pending))
            return;

        _current = null;
        _audit.Log("recording_failure_notification.closed", new
        {
            recording_id = pending.Request.RecordingId,
            reason_code = pending.Request.ReasonCode,
            close_reason = reason switch
            {
                RecordingFailureNotificationCloseReason.UserDismissed => "user_dismissed",
                RecordingFailureNotificationCloseReason.Timeout => "timeout",
                RecordingFailureNotificationCloseReason.ApplicationExit => "application_exit",
                _ => "external"
            }
        });

        if (!_disposed)
            TryPresentNext();
    }

    private int GetActiveRecordingCount()
    {
        try { return Math.Max(0, _activeRecordingCount()); }
        catch { return 0; }
    }

    internal static bool IsSupportedReason(string? reasonCode) => reasonCode is
        "backend_creation_failed" or "unexpected_exit" or
        "storage_space_low" or "storage_capacity_unavailable" or
        "display_unavailable" or "window_closed" or "window_minimized" or "size_changed" or "capture_semantics_changed" or
        "audio_capture_discontinuous";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _presenter.Close(RecordingFailureNotificationCloseReason.ApplicationExit);

        while (_queue.Count > 0)
        {
            var pending = _queue.Dequeue();
            _audit.Log("recording_failure_notification.suppressed", new
            {
                recording_id = pending.Request.RecordingId,
                reason_code = pending.Request.ReasonCode,
                close_reason = "application_exit"
            });
        }

        _current = null;
        _presenter.Dispose();
    }
}

internal sealed class WinFormsRecordingFailureNotificationPresenter : IRecordingFailureNotificationPresenter
{
    private readonly IWindowDisplayAffinity _displayAffinity;
    private RecordingFailureNotificationForm? _form;

    public WinFormsRecordingFailureNotificationPresenter(IWindowDisplayAffinity? displayAffinity = null)
    {
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;
    }

    internal RecordingFailureNotificationForm? FormForTests => _form;

    public NotificationPresentationResult TryShow(
        RecordingFailureNotificationRequest request,
        IUiTextProvider textProvider,
        bool requireCaptureExclusion,
        Action<RecordingFailureNotificationCloseReason> onClosed)
    {
        if (_form != null)
            return new NotificationPresentationResult(false, false, false);

        RecordingFailureNotificationForm? form = null;
        try
        {
            form = new RecordingFailureNotificationForm(
                request,
                textProvider,
                _displayAffinity,
                reason =>
                {
                    if (ReferenceEquals(_form, form))
                        _form = null;
                    onClosed(reason);
                });

            // Handle creation and display-affinity application happen before
            // Show, so an active capture never sees an unclassified window.
            _ = form.Handle;
            var result = new NotificationPresentationResult(
                Shown: false,
                DisplayAffinityRequested: form.DisplayAffinityRequestedForTests,
                DisplayAffinityApplied: form.DisplayAffinityAppliedForTests);

            if (requireCaptureExclusion && !result.DisplayAffinityApplied)
            {
                form.Dispose();
                return result;
            }

            _form = form;
            form.Show();
            return result with { Shown = true };
        }
        catch
        {
            if (form != null)
            {
                try { form.Dispose(); } catch { }
            }
            _form = null;
            return new NotificationPresentationResult(false, false, false);
        }
    }

    public void Close(RecordingFailureNotificationCloseReason reason)
    {
        try { _form?.CloseFor(reason); } catch { }
    }

    public void Dispose()
    {
        try { _form?.CloseFor(RecordingFailureNotificationCloseReason.ApplicationExit); } catch { }
        try { _form?.Dispose(); } catch { }
        _form = null;
    }
}

internal sealed class RecordingFailureNotificationForm : Form
{
    private readonly RecordingFailureNotificationRequest _request;
    private readonly IWindowDisplayAffinity _displayAffinity;
    private readonly Action<RecordingFailureNotificationCloseReason> _onClosed;
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private readonly Button _closeButton;
    private int _closeStarted;
    private RecordingFailureNotificationCloseReason _closeReason = RecordingFailureNotificationCloseReason.External;
    private bool _displayAffinityRequested;
    private bool _displayAffinityApplied;
    private Exception? _displayAffinityError;
    private bool _timerDisposed;
    private bool _visualReady, _layingOut;
    private Font? _titleFont, _bodyFont, _buttonFont;
    private (int Dpi, Rectangle Area, bool? Contrast)? _testViewport;
    private int _visualDpi = 96;
    private float _contentScale = 1;

    internal const int AutoDismissMilliseconds = 8000;
    internal const string WindowClassPurpose = "recording_failure_notification";

    internal bool DisplayAffinityRequestedForTests => _displayAffinityRequested;
    internal bool DisplayAffinityAppliedForTests => _displayAffinityApplied;
    internal Exception? DisplayAffinityErrorForTests => _displayAffinityError;
    internal bool TimerEnabledForTests => _dismissTimer.Enabled;
    internal bool TimerDisposedForTests => _timerDisposed;
    internal Button CloseButtonForTests => _closeButton;
    internal bool ShowWithoutActivationForTests => ShowWithoutActivation;
    internal int ExtendedStyleForTests => CreateParams.ExStyle;
    internal Label BodyLabelForTests { get; }
    internal Label TitleLabelForTests { get; }
    internal int BorderPixelsForTests => RecordingFailureNotificationLayout.Pixels(3, _visualDpi / 96f);
    internal Color BorderColorForTests => (_testViewport?.Contrast ?? SystemInformation.HighContrast)
        ? SystemColors.WindowText : Color.FromArgb(0xD6, 0xA0, 0x00);
    internal float ContentScaleForTests => _contentScale;

    public RecordingFailureNotificationForm(
        RecordingFailureNotificationRequest request,
        IUiTextProvider textProvider,
        IWindowDisplayAffinity displayAffinity,
        Action<RecordingFailureNotificationCloseReason> onClosed)
    {
        _request = request;
        _displayAffinity = displayAffinity;
        _onClosed = onClosed;

        string title = textProvider.Get(RecordingFailureNotificationLayout.TitleKey(request.ReasonCode));
        string body = textProvider.Get(request.ReasonCode switch
        {
            "backend_creation_failed" => "Tray_BackendCreationFailed",
            "storage_space_low" => "Tray_RecordingFailure_StorageSpaceLowBody",
            "storage_capacity_unavailable" => "Tray_RecordingFailure_StorageCapacityUnavailableBody",
            "display_unavailable" => "Tray_RecordingFailure_DisplayUnavailableBody",
            "window_closed" => "Tray_RecordingFailure_WindowClosedBody",
            "window_minimized" => "Tray_RecordingFailure_WindowMinimizedBody",
            "size_changed" => "Tray_RecordingFailure_SizeChangedBody",
            "capture_semantics_changed" => "Tray_RecordingFailure_CaptureSemanticsChangedBody",
            "audio_capture_discontinuous" => "Tray_RecordingFailure_SystemAudioBody",
            _ => "Tray_RecordingFailure_GenericBody"
        });

        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        // This one container uses explicit pixel fonts/geometry at the current
        // monitor DPI, avoiding a second WinForms autoscale of measured bounds.
        AutoScaleMode = AutoScaleMode.None;
        Text = title;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        TitleLabelForTests = new Label
        {
            AutoSize = false,
            UseMnemonic = false,
            UseCompatibleTextRendering = false,
            Text = title,
            TextAlign = ContentAlignment.TopLeft,
            AccessibleName = title
        };
        BodyLabelForTests = new Label
        {
            AutoSize = false,
            UseMnemonic = false,
            UseCompatibleTextRendering = false,
            Text = body,
            TextAlign = ContentAlignment.TopLeft,
            AccessibleName = body
        };
        _closeButton = new Button
        {
            AutoSize = false,
            UseCompatibleTextRendering = false,
            Text = textProvider.Get("Tray_RecordingFailure_Close"),
            AccessibleName = textProvider.Get("Tray_RecordingFailure_Close"),
            AccessibleRole = AccessibleRole.PushButton,
            TabIndex = 0,
            TabStop = true,
            UseMnemonic = false
        };
        _closeButton.Click += (_, _) => CloseFor(RecordingFailureNotificationCloseReason.UserDismissed);

        Controls.AddRange([TitleLabelForTests, BodyLabelForTests, _closeButton]);
        MinimumSize = Size.Empty;
        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        _dismissTimer = new System.Windows.Forms.Timer { Interval = AutoDismissMilliseconds };
        _dismissTimer.Tick += (_, _) => CloseFor(RecordingFailureNotificationCloseReason.Timeout);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                CloseFor(RecordingFailureNotificationCloseReason.UserDismissed);
        };
        _visualReady = true;
        PositionNearWorkingArea();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x8000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDisplayAffinity(Handle);
        PositionNearWorkingArea();
    }

    internal void ApplyDisplayAffinity(IntPtr hWnd)
    {
        _displayAffinityRequested = false;
        _displayAffinityApplied = false;
        _displayAffinityError = null;
        if (hWnd == IntPtr.Zero)
            return;

        _displayAffinityRequested = true;
        try
        {
            _displayAffinityApplied = _displayAffinity.SetExcludeFromCapture(hWnd);
        }
        catch (Exception ex)
        {
            _displayAffinityError = ex;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionNearWorkingArea();
        _dismissTimer.Start();
    }

    private void PositionNearWorkingArea()
    {
        if (!_visualReady || _layingOut || IsDisposed) return;
        Rectangle area;
        try
        {
            area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        }
        catch
        {
            area = new Rectangle(0, 0, 1280, 720);
        }

        ApplyVisualLayout(_testViewport?.Dpi ?? Math.Max(96, DeviceDpi), _testViewport?.Area ?? area);
    }

    internal void ApplyViewportForTests(int dpi, Rectangle area, bool? highContrast = null)
    {
        _testViewport = (dpi, area, highContrast);
        ApplyVisualLayout(dpi, area);
    }

    private void ApplyVisualLayout(int dpi, Rectangle area)
    {
        if (!_visualReady || _layingOut || IsDisposed) return;
        _layingOut = true;
        try
        {
            _visualDpi = Math.Max(96, dpi);
            if (area.Width <= 0 || area.Height <= 0) area = new Rectangle(0, 0, 1280, 720);
            var scale = _visualDpi / 96f;
            int margin = Math.Min(RecordingFailureNotificationLayout.Pixels(12, scale), Math.Min(area.Width, area.Height) / 8);
            var frame = SizeFromClientSize(Size.Empty);
            var available = new Size(Math.Max(1, area.Width - 2 * margin - frame.Width), Math.Max(1, area.Height - 2 * margin - frame.Height));
            var layout = RecordingFailureNotificationLayout.Arrange(TitleLabelForTests.Text, BodyLabelForTests.Text, _closeButton.Text, _visualDpi, available);
            _contentScale = layout.Scale;
            ReplaceFont(ref _titleFont, TitleLabelForTests, RecordingFailureNotificationLayout.TitleFont(layout.Scale));
            ReplaceFont(ref _bodyFont, BodyLabelForTests, RecordingFailureNotificationLayout.BodyFont(layout.Scale));
            ReplaceFont(ref _buttonFont, _closeButton, RecordingFailureNotificationLayout.BodyFont(layout.Scale));
            ClientSize = layout.Client;
            TitleLabelForTests.Bounds = layout.Title;
            BodyLabelForTests.Bounds = layout.Body;
            _closeButton.Bounds = layout.Button;
            Location = new Point(Math.Max(area.Left, area.Right - Width - margin), Math.Max(area.Top, area.Bottom - Height - margin));
            Invalidate();
        }
        finally { _layingOut = false; }
    }

    private static void ReplaceFont(ref Font? previous, Control control, Font next)
    {
        // Control.Font can keep the original object when values are equal.
        // Retain that owned instance instead of disposing a font still in use.
        if (previous is not null && previous.Equals(next)) { next.Dispose(); return; }
        var old = previous; previous = next; control.Font = next; old?.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        PositionNearWorkingArea();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Solid strips remain entirely inside the client area. Child bounds
        // start beyond them, so Dock/OS non-client painting cannot cover them.
        int edge = Math.Min(BorderPixelsForTests, Math.Min(ClientSize.Width, ClientSize.Height) / 2);
        if (edge <= 0) return;
        using var brush = new SolidBrush(BorderColorForTests);
        e.Graphics.FillRectangle(brush, 0, 0, ClientSize.Width, edge);
        e.Graphics.FillRectangle(brush, 0, ClientSize.Height - edge, ClientSize.Width, edge);
        e.Graphics.FillRectangle(brush, 0, edge, edge, ClientSize.Height - 2 * edge);
        e.Graphics.FillRectangle(brush, ClientSize.Width - edge, edge, edge, ClientSize.Height - 2 * edge);
    }

    internal void CloseFor(RecordingFailureNotificationCloseReason reason)
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            return;

        _closeReason = reason;
        try { _dismissTimer.Stop(); } catch { }
        try { Close(); } catch { Dispose(); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _dismissTimer.Stop(); } catch { }
        try { _dismissTimer.Dispose(); } catch { }
        _timerDisposed = true;
        base.OnFormClosed(e);
        _onClosed(_closeReason);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_timerDisposed)
        {
            try { _dismissTimer.Stop(); } catch { }
            try { _dismissTimer.Dispose(); } catch { }
            _timerDisposed = true;
        }

        if (disposing)
        {
            _visualReady = false;
            TitleLabelForTests?.Dispose();
            BodyLabelForTests?.Dispose();
            _closeButton?.Dispose();
            _titleFont?.Dispose(); _bodyFont?.Dispose(); _buttonFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class RecordingFailureNotificationLayout
{
    internal const int WidthDip = 460;
    internal const float TitlePoints = 12, BodyPoints = 11;
    internal sealed record Geometry(Size Client, Rectangle Title, Rectangle Body, Rectangle Button, float Scale, bool Fits);
    internal static int Pixels(float dip, float scale) => Math.Max(1, (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero));
    internal static Font TitleFont(float scale) => new("Segoe UI", TitlePoints * 96 / 72 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    internal static Font BodyFont(float scale) => new("Segoe UI", BodyPoints * 96 / 72 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    internal static string TitleKey(string reasonCode) => reasonCode == "backend_creation_failed"
        ? "Tray_RecordingFailure_BackendCreationTitle" : "Tray_RecordingFailure_Title";
    internal static string BodyKey(string reasonCode) => reasonCode switch
    {
        "backend_creation_failed" => "Tray_BackendCreationFailed",
        "storage_space_low" => "Tray_RecordingFailure_StorageSpaceLowBody",
        "storage_capacity_unavailable" => "Tray_RecordingFailure_StorageCapacityUnavailableBody",
        "display_unavailable" => "Tray_RecordingFailure_DisplayUnavailableBody",
        "window_closed" => "Tray_RecordingFailure_WindowClosedBody",
        "window_minimized" => "Tray_RecordingFailure_WindowMinimizedBody",
        "size_changed" => "Tray_RecordingFailure_SizeChangedBody",
        "capture_semantics_changed" => "Tray_RecordingFailure_CaptureSemanticsChangedBody",
        "audio_capture_discontinuous" => "Tray_RecordingFailure_SystemAudioBody",
        _ => "Tray_RecordingFailure_GenericBody"
    };
    private static Size TextSize(string text, Font font, int width) => TextRenderer.MeasureText(text, font,
        new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);

    internal static Geometry Arrange(string title, string body, string close, int dpi, Size available)
    {
        float requested = Math.Max(96, dpi) / 96f;
        int width = Math.Max(1, Math.Min(Pixels(WidthDip, requested), available.Width));
        int edge = Pixels(3, requested);
        Geometry result;
        float scale = requested;
        // Small work areas keep the OS frame and border DPI, but compact the
        // content together rather than clipping text or imposing a large minimum.
        for (int attempt = 0; ; attempt++)
        {
            using var titleFont = TitleFont(scale);
            using var bodyFont = BodyFont(scale);
            int side = Math.Min(edge + Pixels(18, scale), Math.Max(edge + 1, width / 6));
            int inner = Math.Max(1, width - side * 2);
            int y = edge + Pixels(16, scale);
            var titleRect = new Rectangle(side, y, inner, TextSize(title, titleFont, inner).Height + Pixels(2, scale));
            var bodyRect = new Rectangle(side, titleRect.Bottom + Pixels(10, scale), inner, TextSize(body, bodyFont, inner).Height + Pixels(2, scale));
            var closeSize = TextSize(close, bodyFont, inner);
            int buttonWidth = Math.Min(inner, Math.Max(Pixels(96, requested), closeSize.Width + Pixels(24, scale)));
            int buttonHeight = Math.Max(Pixels(34, requested), closeSize.Height + Pixels(10, scale));
            var buttonRect = new Rectangle(width - side - buttonWidth, bodyRect.Bottom + Pixels(14, scale), buttonWidth, buttonHeight);
            int height = buttonRect.Bottom + edge + Pixels(14, scale);
            bool fits = height <= available.Height;
            result = new(new Size(width, Math.Min(height, Math.Max(1, available.Height))), titleRect, bodyRect, buttonRect, scale, fits);
            if (fits || attempt >= 40) return result;
            scale *= .94f;
        }
    }

    internal static Size MeasurePreferredSize(string title, string body, int dpi)
        => Arrange(title, body, "Close", dpi, new Size(int.MaxValue, int.MaxValue)).Client;

    internal static bool FitsAtDpi(IUiTextProvider textProvider, string reasonCode, int dpi)
    {
        var layout = Arrange(textProvider.Get(TitleKey(reasonCode)), textProvider.Get(BodyKey(reasonCode)),
            textProvider.Get("Tray_RecordingFailure_Close"), dpi, new Size(int.MaxValue, int.MaxValue));
        return layout.Fits && layout.Title.Bottom <= layout.Body.Top && layout.Body.Bottom <= layout.Button.Top && layout.Button.Bottom < layout.Client.Height;
    }
}
