using System;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal sealed record StandingLeaseApprovalDetails(
    string DisplayName,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    TimeSpan MaximumDuration,
    DateTimeOffset LeaseValidUntilUtc,
    string OutputDirectory,
    string FrozenFileName);

/// <summary>
/// Dedicated local approval surface for a standing lease. It captures only a
/// temporary in-memory preview for this form and never serializes it.
/// </summary>
internal sealed class StandingLeaseApprovalForm : Form
{
    private readonly Button _approveButton;
    private readonly Button _rejectButton;
    private readonly PictureBox _previewBox;
    private readonly Label _previewFallbackLabel;
    private readonly Label _previewBoundsLabel;
    private readonly TextBox _detailsBox;
    private readonly Label _shortcutLabel;
    private readonly IUiTextProvider _text;
    private Bitmap? _previewBitmap;
    private readonly StandingLeaseApprovalDetails _details;
    private readonly IScreenPreviewProvider _provider;
    private StandingLeaseApprovalAudit? _audit;
    private StandingLeaseApprovalOptions? _options;
    private IFutureWindowApprovalWindowPlatform _platform = Win32FutureWindowApprovalWindowPlatform.Instance;
    private StandingLeasePreviewOwner? _previewOwner;
    private System.Windows.Forms.Timer? _previewTimer;
    private readonly Stopwatch _previewElapsed = new();
    private CancellationToken _cancellation;
    private StandingLeaseApprovalResult _result = new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_not_shown");
    private bool _nativeVisible, _visibilityAttempted;

    internal StandingLeaseApprovalForm(
        StandingLeaseApprovalDetails details,
        IScreenPreviewProvider? previewProvider = null,
        IUiTextProvider? textProvider = null)
    {
        ArgumentNullException.ThrowIfNull(details);

        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        _details = details;
        _provider = previewProvider ?? new GdiScreenPreviewProvider();

        Text = _text.Get("StandingLeaseApproval_Title");
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ShowInTaskbar = true;
        MinimumSize = new Size(620, 700);
        ClientSize = new Size(820, 820);
        BackColor = Color.FromArgb(30, 33, 38);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        shell.Controls.Add(scroll, 0, 0);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Heading"),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(125, 211, 252),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(title, 0, 0);

        var warning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Warning"),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(warning, 0, 1);

        var previewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(22, 24, 28),
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 12),
        };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var previewTitle = new Label
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_PreviewTitle"),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 6),
        };
        previewLayout.Controls.Add(previewTitle, 0, 0);
        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            BorderStyle = BorderStyle.FixedSingle,
        };
        previewLayout.Controls.Add(_previewBox, 0, 1);
        _previewFallbackLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 6, 0, 0),
            Visible = true,
            Text = _text.Get("StandingLeaseApproval_PreviewFallback"),
        };
        previewLayout.Controls.Add(_previewFallbackLabel, 0, 2);
        root.Controls.Add(previewLayout, 0, 2);

        _previewBoundsLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Format("StandingLeaseApproval_PreviewBounds", GetAbsoluteRegion(details).X, GetAbsoluteRegion(details).Y, GetAbsoluteRegion(details).Width, GetAbsoluteRegion(details).Height),
            ForeColor = Color.Silver,
            Margin = new Padding(0, 0, 0, 10),
        };

        _detailsBox = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 24, 28),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = FormatDetails(details),
            Margin = new Padding(0, 0, 0, 12),
        };
        var detailsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
        };
        detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailsLayout.Controls.Add(_previewBoundsLabel, 0, 0);
        detailsLayout.Controls.Add(_detailsBox, 0, 1);
        root.Controls.Add(detailsLayout, 0, 3);

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Note"),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 14),
        };
        root.Controls.Add(note, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = true,
        };
        _approveButton = new Button
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Approve"),
            BackColor = Color.FromArgb(22, 163, 74),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Padding = new Padding(14, 7, 14, 7),
            Margin = new Padding(8, 0, 0, 0),
        };
        _rejectButton = new Button
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Reject"),
            BackColor = Color.FromArgb(185, 28, 28),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
            Padding = new Padding(14, 7, 14, 7),
        };
        buttons.Controls.Add(_approveButton);
        buttons.Controls.Add(_rejectButton);
        _approveButton.Click += (_, _) => _result = !_cancellation.IsCancellationRequested && _nativeVisible
            ? new(StandingLeaseApprovalOutcome.Approved, "user_approved") : new(StandingLeaseApprovalOutcome.Cancelled, "lease_approval_cancelled");
        _rejectButton.Click += (_, _) => _result = new(StandingLeaseApprovalOutcome.Rejected, "lease_rejected_by_user");
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(footer, 0, 1);
        footer.Controls.Add(buttons, 0, 0);

        _shortcutLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Keyboard"),
            ForeColor = Color.Gray,
            Margin = new Padding(0, 10, 0, 0),
        };
        footer.Controls.Add(_shortcutLabel, 0, 1);

        // Enter is deliberately a safe rejection, never an approval shortcut.
        AcceptButton = _rejectButton;
        CancelButton = _rejectButton;
        var absolute = GetAbsoluteRegion(details);
        AbsolutePreviewBoundsForTests = new(absolute.X, absolute.Y, absolute.Width, absolute.Height);
        Shown += (_, _) =>
        {
            _rejectButton.Focus();
        };
    }

    internal bool HasPreviewImageForTests => _previewBox.Image is not null;
    internal string PreviewFallbackTextForTests => _previewFallbackLabel.Text;
    internal string PreviewBoundsTextForTests => _previewBoundsLabel.Text;
    internal bool AcceptButtonIsRejectForTests => ReferenceEquals(AcceptButton, _rejectButton);
    internal bool CancelButtonIsRejectForTests => ReferenceEquals(CancelButton, _rejectButton);
    internal DialogResult ApproveButtonResultForTests => _approveButton.DialogResult;
    internal DialogResult RejectButtonResultForTests => _rejectButton.DialogResult;
    internal string KeyboardTextForTests => _shortcutLabel.Text;
    internal string DetailsTextForTests => _detailsBox.Text;
    internal ScrollBars DetailsScrollBarsForTests => _detailsBox.ScrollBars;
    internal Rectangle AbsolutePreviewBoundsForTests { get; private set; }
    internal bool PreviewBitmapDisposedForTests { get; private set; }

    internal static bool ShowModal(
        StandingLeaseApprovalDetails details,
        IScreenPreviewProvider? previewProvider = null,
        IUiTextProvider? textProvider = null,
        CancellationToken cancellationToken = default)
    {
        var operation = StandingLeaseApprovalPresentation.Start("standing_local", details,
            textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault()), (_, _) => { }, cancellationToken,
            new() { PreviewProvider = previewProvider });
        var result = operation.Decision.GetAwaiter().GetResult();
        operation.Retired.GetAwaiter().GetResult();
        return result.Outcome == StandingLeaseApprovalOutcome.Approved;
    }

    internal StandingLeaseApprovalResult ShowOwnedModal(StandingLeaseApprovalAudit audit, StandingLeaseApprovalOptions options,
        CancellationToken cancellation, Action<StandingLeasePreviewOwner> ownPreview, Action visible)
    {
        _audit = audit; _options = options; _platform = options.WindowPlatform ?? Win32FutureWindowApprovalWindowPlatform.Instance;
        _cancellation = cancellation;
        if (options.DisableApprovalForTests) _approveButton.Enabled = false;
        using var registration = cancellation.Register(RequestClose);
        options.BeforeShowForTests?.Invoke(this);
        if (cancellation.IsCancellationRequested) return new(StandingLeaseApprovalOutcome.Cancelled, "lease_approval_cancelled");
        Shown += (_, _) =>
        {
            Log("shown");
            if (cancellation.IsCancellationRequested) { Close(); return; }
            if (!EnsureVisible()) return;
            if (cancellation.IsCancellationRequested) { Close(); return; }
            visible();
            if (cancellation.IsCancellationRequested) { Close(); return; }
            _previewOwner = new(() => ConfirmationPreviewBuilder.TryBuildPreview(GetAbsoluteRegion(_details), _provider, new Size(760, 260), out _), audit, cancellation);
            ownPreview(_previewOwner);
            _previewElapsed.Start();
            _previewTimer = new() { Interval = 50 };
            _previewTimer.Tick += (_, _) => ObservePreview();
            _previewTimer.Start();
            options.AfterVisibleForTests?.Invoke(this);
        };
        Log("show_begin");
        _ = ShowDialog();
        Log("modal_closed", _result.Reason);
        return cancellation.IsCancellationRequested
            ? new(StandingLeaseApprovalOutcome.Cancelled, "lease_approval_cancelled") : _result;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Log("handle_created");
        if (_cancellation.IsCancellationRequested) RequestClose();
        else if (_audit is not null)
            try { BeginInvoke(new Action(() => EnsureVisible())); } catch (InvalidOperationException) { }
    }
    protected override void OnLoad(EventArgs e) { LocalApprovalWindowPresentation.Fit(this, _options?.WorkingAreaForTests); base.OnLoad(e); Log("load"); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LocalApprovalWindowPresentation.Fit(this, _options?.WorkingAreaForTests); }
    private bool EnsureVisible()
    {
        if (_cancellation.IsCancellationRequested) { Close(); return false; }
        if (_visibilityAttempted) return _nativeVisible;
        _visibilityAttempted = true;
        try
        {
            var state = LocalApprovalWindowPresentation.ShowAndInspect(this, _platform);
            _nativeVisible = LocalApprovalWindowPresentation.Usable(state);
            _audit?.Log(_nativeVisible ? "native_visible" : "display_failed",
                _nativeVisible ? null : "lease_approval_native_not_visible", Handle, state);
            if (!_nativeVisible) { _result = new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_native_not_visible"); Close(); return false; }
            var foreground = _platform.Activate(Handle);
            Log("foreground", foreground.IsForeground ? "foreground_confirmed" : "foreground_denied");
            return true;
        }
        catch { _result = new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_display_failed"); Log("display_failed", _result.Reason); Close(); return false; }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cancellation.IsCancellationRequested) _result = new(StandingLeaseApprovalOutcome.Cancelled, "lease_approval_cancelled");
        else if (_result.Reason == "lease_approval_not_shown" && _nativeVisible)
            _result = new(StandingLeaseApprovalOutcome.Rejected, "lease_approval_window_closed");
        _previewTimer?.Stop(); _previewOwner?.Abandon();
        Log("closing", _result.Reason);
        base.OnFormClosing(e);
    }
    private void RequestClose()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(Close)); } catch (InvalidOperationException) { }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) is Keys.Enter or Keys.Escape)
        { _result = new(StandingLeaseApprovalOutcome.Rejected, "lease_rejected_by_user"); DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private void Log(string stage, string? reason = null)
    {
        FutureWindowApprovalNativeState? state = null;
        var handle = IsHandleCreated ? Handle : nint.Zero;
        try { if (handle != 0) state = _platform.Inspect(handle); } catch { }
        _audit?.Log(stage, reason, handle, state);
    }
    private void ObservePreview()
    {
        if (_previewElapsed.Elapsed >= _options!.PreviewBudget)
        {
            _previewTimer!.Stop(); _previewOwner!.Abandon(); Log("preview_fallback", "lease_preview_timeout"); return;
        }
        if (!_previewOwner!.TryTake(out var bitmap)) return;
        _previewTimer!.Stop();
        _previewBitmap = bitmap; _previewBox.Image = bitmap;
        _previewFallbackLabel.Visible = bitmap is null;
        Log(bitmap is null ? "preview_fallback" : "preview_ready", bitmap is null ? "lease_preview_unavailable" : null);
    }
    internal void DecideForTests(bool approve) { if (approve) _approveButton.PerformClick(); else _rejectButton.PerformClick(); }
    internal void KeyForTests(Keys key) { ActiveControl = _approveButton; var message = new Message(); ProcessCmdKey(ref message, key); }
    internal bool ButtonsReadableForTests => _approveButton.Visible && _rejectButton.Visible &&
        ClientRectangle.Contains(RectangleToClient(_approveButton.RectangleToScreen(_approveButton.ClientRectangle))) &&
        ClientRectangle.Contains(RectangleToClient(_rejectButton.RectangleToScreen(_rejectButton.ClientRectangle)));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audit?.Log("dispose_begin");
            _previewTimer?.Dispose(); _previewOwner?.Abandon();
            if (_previewBox?.Image is not null)
                _previewBox.Image = null;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
            PreviewBitmapDisposedForTests = true;
        }
        base.Dispose(disposing);
        if (disposing) _audit?.Log("form_disposed");
    }

    private string FormatDetails(StandingLeaseApprovalDetails details)
    {
        var display = details.DisplayBounds;
        var region = details.RegionWithinDisplay;
        return _text.Format(
            "StandingLeaseApproval_Details",
            details.DisplayName,
            display.X,
            display.Y,
            display.Width,
            display.Height,
            region.X,
            region.Y,
            region.Width,
            region.Height,
            details.ScheduledStartUtc.ToString("O"),
            details.LatestStartUtc.ToString("O"),
            details.PlannedEndUtc.ToString("O"),
            details.MaximumDuration.TotalSeconds.ToString("0"),
            details.LeaseValidUntilUtc.ToString("O"),
            details.OutputDirectory,
            details.FrozenFileName);
    }

    private static ConfirmationCaptureBounds GetAbsoluteRegion(StandingLeaseApprovalDetails details)
    {
        var display = details.DisplayBounds;
        var region = details.RegionWithinDisplay;
        return new ConfirmationCaptureBounds(
            checked(display.X + region.X),
            checked(display.Y + region.Y),
            region.Width,
            region.Height);
    }
}
