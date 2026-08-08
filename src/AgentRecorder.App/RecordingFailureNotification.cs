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
        "window_closed" or "window_minimized" or "size_changed";

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

    public RecordingFailureNotificationForm(
        RecordingFailureNotificationRequest request,
        IUiTextProvider textProvider,
        IWindowDisplayAffinity displayAffinity,
        Action<RecordingFailureNotificationCloseReason> onClosed)
    {
        _request = request;
        _displayAffinity = displayAffinity;
        _onClosed = onClosed;

        string title = textProvider.Get("Tray_RecordingFailure_Title");
        string body = textProvider.Get(request.ReasonCode switch
        {
            "window_closed" => "Tray_RecordingFailure_WindowClosedBody",
            "window_minimized" => "Tray_RecordingFailure_WindowMinimizedBody",
            "size_changed" => "Tray_RecordingFailure_SizeChangedBody",
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
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        Padding = new Padding(16, 14, 16, 12);

        TitleLabelForTests = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = title
        };
        BodyLabelForTests = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Text = body,
            MaximumSize = new Size(348, 0),
            Padding = new Padding(0, 8, 0, 8),
            AccessibleName = body
        };
        _closeButton = new Button
        {
            AutoSize = true,
            Text = textProvider.Get("Tray_RecordingFailure_Close"),
            AccessibleName = textProvider.Get("Tray_RecordingFailure_Close"),
            AccessibleRole = AccessibleRole.PushButton,
            TabIndex = 0,
            TabStop = true,
            MinimumSize = new Size(84, 30),
            Padding = new Padding(12, 2, 12, 2)
        };
        _closeButton.Click += (_, _) => CloseFor(RecordingFailureNotificationCloseReason.UserDismissed);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        buttonPanel.Controls.Add(_closeButton);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(TitleLabelForTests, 0, 0);
        layout.Controls.Add(BodyLabelForTests, 0, 1);
        layout.Controls.Add(buttonPanel, 0, 2);
        Controls.Add(layout);

        ClientSize = RecordingFailureNotificationLayout.MeasurePreferredSize(title, body, 96);
        MinimumSize = new Size(320, Math.Max(140, Height));
        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        _dismissTimer = new System.Windows.Forms.Timer { Interval = AutoDismissMilliseconds };
        _dismissTimer.Tick += (_, _) => CloseFor(RecordingFailureNotificationCloseReason.Timeout);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                CloseFor(RecordingFailureNotificationCloseReason.UserDismissed);
        };
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
        Rectangle area;
        try
        {
            area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        }
        catch
        {
            area = new Rectangle(0, 0, 1280, 720);
        }

        int margin = Math.Max(12, (int)Math.Round(12 * (DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0)));
        Location = new Point(
            Math.Max(area.Left, area.Right - Width - margin),
            Math.Max(area.Top, area.Bottom - Height - margin));
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
            TitleLabelForTests?.Dispose();
            BodyLabelForTests?.Dispose();
            _closeButton?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class RecordingFailureNotificationLayout
{
    internal static Size MeasurePreferredSize(string title, string body, int dpi)
    {
        int effectiveDpi = Math.Max(96, dpi);
        float scale = effectiveDpi / 96f;
        int width = (int)Math.Ceiling(380 * scale);
        int innerWidth = Math.Max(240, width - (int)Math.Ceiling(32 * scale));

        using var titleFont = new Font("Segoe UI", 10f * scale, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 9f * scale, FontStyle.Regular);
        var titleSize = TextRenderer.MeasureText(title, titleFont,
            new Size(innerWidth, 0), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var bodySize = TextRenderer.MeasureText(body, bodyFont,
            new Size(innerWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int buttonHeight = (int)Math.Ceiling(30 * scale);
        int height = (int)Math.Ceiling(26 * scale) + titleSize.Height +
                     bodySize.Height + (int)Math.Ceiling(16 * scale) + buttonHeight;
        return new Size(width, Math.Max((int)Math.Ceiling(140 * scale), height));
    }

    internal static bool FitsAtDpi(IUiTextProvider textProvider, string reasonCode, int dpi)
    {
        string bodyKey = reasonCode switch
        {
            "window_closed" => "Tray_RecordingFailure_WindowClosedBody",
            "window_minimized" => "Tray_RecordingFailure_WindowMinimizedBody",
            "size_changed" => "Tray_RecordingFailure_SizeChangedBody",
            _ => "Tray_RecordingFailure_GenericBody"
        };
        int effectiveDpi = Math.Max(96, dpi);
        float scale = effectiveDpi / 96f;
        int width = (int)Math.Ceiling(380 * scale);
        int innerWidth = Math.Max(240, width - (int)Math.Ceiling(32 * scale));
        using var titleFont = new Font("Segoe UI", 10f * scale, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 9f * scale, FontStyle.Regular);
        var titleSize = TextRenderer.MeasureText(textProvider.Get("Tray_RecordingFailure_Title"), titleFont,
            new Size(innerWidth, 0), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var bodySize = TextRenderer.MeasureText(textProvider.Get(bodyKey), bodyFont,
            new Size(innerWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var size = MeasurePreferredSize(textProvider.Get("Tray_RecordingFailure_Title"), textProvider.Get(bodyKey), dpi);
        int contentHeight = size.Height - (int)Math.Ceiling(26 * scale) -
                            titleSize.Height - (int)Math.Ceiling(16 * scale) -
                            (int)Math.Ceiling(30 * scale);
        return bodySize.Width <= innerWidth && bodySize.Height <= contentHeight;
    }
}
