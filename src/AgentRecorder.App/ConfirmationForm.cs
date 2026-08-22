using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.App;

/// <summary>
/// Directory picker seam so the form can be unit-tested without showing a real FolderBrowserDialog.
/// </summary>
internal interface IOutputDirectoryPicker
{
    string? PickDirectory(string initialDirectory);
}

/// <summary>
/// Testable abstraction over the confirmation form lifecycle. Production code
/// in <see cref="TrayContext"/> uses the events to implement the capture-safe
/// barrier before starting recording.
/// </summary>
internal interface IConfirmationDialog
{
    bool IsHandleCreated { get; }
    bool IsDisposed { get; }
    bool Visible { get; }

    /// <summary>
    /// Raised when the form becomes hidden during close.
    /// </summary>
    event EventHandler<ConfirmationDialogLifecycleEventArgs>? Hidden;

    /// <summary>
    /// Raised after the form has closed. The handle may still exist at this point.
    /// </summary>
    event EventHandler<ConfirmationDialogLifecycleEventArgs>? Closed;

    /// <summary>
    /// Raised after the native window handle has been destroyed.
    /// </summary>
    event EventHandler<ConfirmationDialogLifecycleEventArgs>? HandleDestroyed;

    /// <summary>
    /// Closes the form and associates a user decision with the close. The
    /// <see cref="Closed"/> and <see cref="HandleDestroyed"/> events will
    /// carry the decision so the caller can complete the capture-safe barrier.
    /// </summary>
    void CloseWithDecision(ConfirmationDecision decision, string? closeReason = null);

    /// <summary>
    /// Closes the form without invoking any callback or raising decision events.
    /// </summary>
    void CloseWithoutResult(string? reason = null);
}

/// <summary>
/// Arguments for <see cref="IConfirmationDialog"/> lifecycle events.
/// </summary>
internal sealed class ConfirmationDialogLifecycleEventArgs : EventArgs
{
    public ConfirmationDecision? Decision { get; }
    public string? CloseReason { get; }
    public long FormHandle { get; }
    public bool Visible { get; }

    public ConfirmationDialogLifecycleEventArgs(ConfirmationDecision? decision, string? closeReason, long formHandle, bool visible)
    {
        Decision = decision;
        CloseReason = closeReason;
        FormHandle = formHandle;
        Visible = visible;
    }
}

internal sealed class FolderBrowserDirectoryPicker : IOutputDirectoryPicker
{
    private readonly IUiTextProvider _text;

    public FolderBrowserDirectoryPicker(IUiTextProvider? text = null)
    {
        _text = text ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
    }

    public string? PickDirectory(string initialDirectory)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = _text.Get("Confirmation_FolderBrowser_Description"),
            UseDescriptionForTitle = true
        };
        if (Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        var result = dlg.ShowDialog();
        return result == DialogResult.OK ? dlg.SelectedPath : null;
    }
}

/// <summary>
/// Non-modal confirmation form for recording requests.
/// Displays recording metadata and allows user to approve/reject.
/// Default Enter = Reject, Esc = Reject, Close X = Reject.
/// Approve requires explicit click or focused confirmation button.
/// </summary>
internal sealed class ConfirmationForm : Form, IConfirmationDialog
{
    private readonly PendingConfirmationItem _item;
    private readonly int _queuePosition;
    private readonly int _totalCount;
    private readonly Action<ConfirmationDecision>? _onResult;
    private readonly IScreenPreviewProvider _previewProvider;
    private readonly IOutputDirectoryPicker _directoryPicker;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly string _initialOutputDirectory;
    private string? _selectedOutputDirectory;
    private bool _resultHandled;
    private bool _suppressCloseResult;
    private ConfirmationDecision? _pendingDecision;

    public event EventHandler<ConfirmationDialogLifecycleEventArgs>? Hidden;
    public new event EventHandler<ConfirmationDialogLifecycleEventArgs>? Closed;
    public new event EventHandler<ConfirmationDialogLifecycleEventArgs>? HandleDestroyed;

    private readonly IWindowActivator _windowActivator;
    private readonly Action<string, object>? _auditLogger;
    private readonly IPerformanceTracer _tracer;
    private readonly IReadOnlyList<Rectangle> _workingAreas;
    private readonly Rectangle _fallbackWorkingArea;
    private CaptureBounds? _captureBounds;
    private Rectangle _targetWorkingArea;
    private int _targetScreenIndex = -1;
    private int _foregroundAttempts;
    private System.Windows.Forms.Timer? _foregroundVerifyTimer;
    private string? _closeReason;
    private bool _closeAudited;
    private string? _foregroundError;
    private string? _foregroundErrorStage;
    private readonly IUiTextProvider _text;
    private readonly IDwmThumbnailProvider _dwmThumbnailProvider;
    private readonly ToolTip _tooltip;
    private readonly List<(Label Label, Label Value)> _infoRows = new();
    private JsonNode? _summaryNode;
    private IDwmThumbnail? _dwmThumbnail;
    private Size _dwmSourceSize;
    private Rectangle _dwmDestination;
    private bool _windowSurfacePreview;
    private bool _dwmEnsurePosted;
    private bool _dwmDisposed;
    private bool _restoreDwmAfterHandleCreated;
    private int _dwmHandleGeneration;

    private const int ForegroundVerifyDelayMs = 150;
    private const int MaxForegroundAttempts = 2;

    private const float InfoColumnProportion = 0.52f;
    private const float PreviewColumnProportion = 0.48f;

    /// <summary>
    /// Ideal client size for confirmation forms on space-rich desktops.
    /// The actual window is still clamped to the target working area when needed.
    /// </summary>
    private static readonly Size IdealClientSize = new(1020, 860);

    private static readonly Size MinimumClientSize = new(760, 640);

    /// <summary>
    /// Minimum usable confirmation size used when the target working area is very small.
    /// This is a safety floor, not the design-time default.
    /// </summary>
    private static readonly Size MinConfirmationSize = new(480, 360);

    internal bool EnableDelayedForegroundVerification { get; init; } = true;
    internal int ForegroundAttemptsForTest => _foregroundAttempts;
    internal bool ForegroundVerificationTimerEnabledForTests => _foregroundVerifyTimer?.Enabled ?? false;
    internal Rectangle TargetWorkingAreaForTests => _targetWorkingArea;
    internal Rectangle BoundsForTests => Bounds;

    private Button _approveButton = null!;
    private Button _rejectButton = null!;
    private Button _changeOutputButton = null!;
    private CheckBox _rememberOutputCheckBox = null!;
    private Label _outputPathLabel = null!;
    private Panel _outputPanel = null!;
    private PictureBox _previewBox = null!;
    private Label _previewFallbackLabel = null!;
    private Label _previewBoundsLabel = null!;
    private Panel _previewPanel = null!;
    private ProgressBar _timeoutProgressBar = null!;
    private Label _timeoutLabel = null!;
    private Label _warningLabel = null!;
    private System.Windows.Forms.Timer _countdownTimer = null!;
    private FlowLayoutPanel _buttonPanel = null!;
    private Panel _mainContentPanel = null!;
    private Panel _infoPanel = null!;
    private TableLayoutPanel _infoTable = null!;
    private TableLayoutPanel _outputTable = null!;

    internal bool HasPreviewAreaForTests => _previewPanel != null;
    internal bool HasContentScrollPanelForTests => _mainContentPanel != null;
    internal Rectangle ContentScrollPanelBoundsForTests => _mainContentPanel?.Bounds ?? Rectangle.Empty;
    internal bool HasPreviewImageForTests => _previewBox?.Image != null;
    internal string PreviewBoundsTextForTests => _previewBoundsLabel?.Text ?? "";
    internal string PreviewFallbackTextForTests => _previewFallbackLabel?.Text ?? "";
    internal bool WindowSurfacePreviewForTests => _windowSurfacePreview;
    internal bool DwmThumbnailActiveForTests => _dwmThumbnail != null;
    internal Rectangle DwmThumbnailDestinationForTests => _dwmDestination;
    internal nint DwmDestinationWindowForTests => IsHandleCreated ? Handle : nint.Zero;
    internal nint PreviewPanelHandleForTests =>
        _previewPanel?.IsHandleCreated == true ? _previewPanel.Handle : nint.Zero;
    internal Rectangle PreviewPanelFormBoundsForTests => GetFormRelativeBounds(_previewPanel);
    internal bool WindowSurfacePreviewSurfaceIsTransparentForTests =>
        _windowSurfacePreview && _previewPanel != null && _previewPanel.BackColor == Color.Transparent;
    internal bool WindowSurfacePreviewChildrenAreHiddenForTests =>
        _windowSurfacePreview && _previewBox != null && !_previewBox.Visible &&
        _previewFallbackLabel != null && !_previewFallbackLabel.Visible;
    internal void EnsureDwmThumbnailForTests() => EnsureWindowSurfaceThumbnail();
    internal void RecreateHandleForTests() => RecreateHandle();
    internal string TimeoutTextForTests => _timeoutLabel?.Text ?? "";
    internal bool ApproveButtonEnabledForTests => _approveButton?.Enabled ?? false;
    internal bool CountdownTimerEnabledForTests => _countdownTimer?.Enabled ?? false;
    internal Button? DefaultActionForTests => AcceptButton as Button;
    internal Button? CancelActionForTests => CancelButton as Button;
    internal int TimeoutProgressValueForTests => _timeoutProgressBar?.Value ?? 0;
    internal string OutputPathTextForTests => _outputPathLabel?.Text ?? "";
    internal bool ChangeOutputButtonEnabledForTests => _changeOutputButton?.Enabled ?? false;
    internal bool RememberOutputCheckedForTests
    {
        get => _rememberOutputCheckBox?.Checked ?? false;
        set { if (_rememberOutputCheckBox != null) _rememberOutputCheckBox.Checked = value; }
    }

    internal Rectangle OutputPanelBoundsForTests => _outputPanel?.Bounds ?? Rectangle.Empty;
    internal Rectangle TimeoutProgressBoundsForTests => _timeoutProgressBar?.Bounds ?? Rectangle.Empty;
    internal Rectangle TimeoutLabelBoundsForTests => _timeoutLabel?.Bounds ?? Rectangle.Empty;
    internal Rectangle WarningLabelBoundsForTests => _warningLabel?.Bounds ?? Rectangle.Empty;
    internal string WarningTextForTests => _warningLabel?.Text ?? "";
    internal Rectangle ApproveButtonBoundsForTests => GetFormRelativeBounds(_approveButton);
    internal Rectangle RejectButtonBoundsForTests => GetFormRelativeBounds(_rejectButton);
    internal string ApproveButtonTextForTests => _approveButton?.Text ?? "";
    internal string RejectButtonTextForTests => _rejectButton?.Text ?? "";
    internal Button? ApproveButtonForTests => _approveButton;
    internal Button? RejectButtonForTests => _rejectButton;

    private Rectangle GetFormRelativeBounds(Control? control)
    {
        if (control?.Parent == null) return Rectangle.Empty;
        var screenLoc = control.Parent.PointToScreen(control.Location);
        var clientLoc = PointToClient(screenLoc);
        return new Rectangle(clientLoc, control.Size);
    }

    private Rectangle GetOutputPanelRelativeBounds(Control? control)
    {
        if (control?.Parent == null || _outputPanel == null) return Rectangle.Empty;
        var screenLoc = control.Parent.PointToScreen(control.Location);
        var outputLoc = _outputPanel.PointToClient(screenLoc);
        return new Rectangle(outputLoc, control.Size);
    }
    internal bool OutputPathLabelAutoEllipsisForTests => _outputPathLabel?.AutoEllipsis ?? false;
    internal Rectangle MainContentPanelBoundsForTests => _mainContentPanel?.Bounds ?? Rectangle.Empty;
    internal Rectangle InfoPanelBoundsForTests => _infoPanel?.Bounds ?? Rectangle.Empty;
    internal Rectangle InfoPanelClientRectangleForTests => _infoPanel?.ClientRectangle ?? Rectangle.Empty;
    internal Rectangle PreviewPanelBoundsForTests => _previewPanel?.Bounds ?? Rectangle.Empty;
    internal Rectangle PreviewBoundsLabelBoundsForTests => GetFormRelativeBounds(_previewBoundsLabel);
    internal int PreviewBoundsLabelPreferredHeightForTests => _previewBoundsLabel?.PreferredSize.Height ?? 0;
    internal int PreviewBoundsLabelHeightForTests => _previewBoundsLabel?.Height ?? 0;
    internal bool ContentScrollPanelVerticalScrollVisibleForTests => _mainContentPanel?.VerticalScroll.Visible ?? false;
    internal bool ContentScrollPanelHorizontalScrollVisibleForTests => _mainContentPanel?.HorizontalScroll.Visible ?? false;
    internal Rectangle OutputPanelClientRectangleForTests => _outputPanel?.ClientRectangle ?? Rectangle.Empty;
    internal Rectangle OutputTitleBoundsForTests => GetFormRelativeBounds(_outputTable?.GetControlFromPosition(0, 0));
    internal Rectangle OutputPathBoundsForTests => GetFormRelativeBounds(_outputPathLabel);
    internal Rectangle OutputChangeButtonBoundsForTests => GetFormRelativeBounds(_changeOutputButton);
    internal Rectangle OutputRememberCheckBoxBoundsForTests => GetFormRelativeBounds(_rememberOutputCheckBox);
    internal Rectangle OutputActionsPanelBoundsForTests => GetFormRelativeBounds(_outputTable?.GetControlFromPosition(0, 2));
    internal Rectangle OutputTitleBoundsRelativeToOutputPanelForTests => GetOutputPanelRelativeBounds(_outputTable?.GetControlFromPosition(0, 0));
    internal Rectangle OutputPathBoundsRelativeToOutputPanelForTests => GetOutputPanelRelativeBounds(_outputPathLabel);
    internal Rectangle OutputActionsPanelBoundsRelativeToOutputPanelForTests => GetOutputPanelRelativeBounds(_outputTable?.GetControlFromPosition(0, 2));
    internal Rectangle OutputChangeButtonBoundsRelativeToOutputPanelForTests => GetOutputPanelRelativeBounds(_changeOutputButton);
    internal Rectangle OutputRememberCheckBoxBoundsRelativeToOutputPanelForTests => GetOutputPanelRelativeBounds(_rememberOutputCheckBox);
    internal int OutputPathLabelHeightForTests => _outputPathLabel?.Height ?? 0;
    internal int OutputPathLabelMeasuredTextHeightForTests => _outputPathLabel == null ? 0 : TextRenderer.MeasureText(_outputPathLabel.Text, _outputPathLabel.Font).Height;
    internal string OutputPathTooltipForTests => _tooltip?.GetToolTip(_outputPathLabel) ?? "";
    internal IReadOnlyList<(Rectangle LabelBounds, Rectangle ValueBounds)> GetInfoRowBoundsForTests()
    {
        var result = new List<(Rectangle, Rectangle)>();
        foreach (var (label, value) in _infoRows)
        {
            result.Add((GetFormRelativeBounds(label), GetFormRelativeBounds(value)));
        }
        return result;
    }

    internal IReadOnlyList<(string Label, string Value)> GetInfoRowTextsForTests()
    {
        var result = new List<(string, string)>();
        foreach (var (label, value) in _infoRows)
            result.Add((label.Text, value.Text));
        return result;
    }

    internal IReadOnlyList<(Rectangle LabelBounds, Rectangle ValueBounds)> GetInfoRowBoundsRelativeToInfoPanelForTests()
    {
        var result = new List<(Rectangle, Rectangle)>();
        foreach (var (label, value) in _infoRows)
        {
            result.Add((
                new Rectangle(label.Location, label.Size),
                new Rectangle(value.Location, value.Size)));
        }
        return result;
    }

    public ConfirmationForm(PendingConfirmationItem item, int queuePosition, int totalCount,
        Action<ConfirmationDecision>? onResult = null,
        IScreenPreviewProvider? previewProvider = null,
        Func<DateTime>? utcNowProvider = null,
        string? defaultOutputDirectory = null,
        IOutputDirectoryPicker? directoryPicker = null,
        IWindowActivator? windowActivator = null,
        Action<string, object>? auditLogger = null,
        IReadOnlyList<Rectangle>? workingAreas = null,
        Rectangle? fallbackWorkingArea = null,
        IUiTextProvider? textProvider = null,
        IPerformanceTracer? tracer = null,
        IDwmThumbnailProvider? dwmThumbnailProvider = null)
    {
        _item = item;
        _queuePosition = queuePosition;
        _totalCount = totalCount;
        _onResult = onResult;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        _previewProvider = previewProvider ?? new GdiScreenPreviewProvider();
        _directoryPicker = directoryPicker ?? new FolderBrowserDirectoryPicker(_text);
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        _initialOutputDirectory = GetInitialOutputDirectory(defaultOutputDirectory);
        _selectedOutputDirectory = null;
        _resultHandled = false;
        _suppressCloseResult = false;
        _windowActivator = windowActivator ?? DefaultWindowActivator.Instance;
        _auditLogger = auditLogger;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
        _dwmThumbnailProvider = dwmThumbnailProvider ?? new DwmThumbnailProvider();
        _workingAreas = workingAreas ?? Array.Empty<Rectangle>();
        _fallbackWorkingArea = fallbackWorkingArea ?? Rectangle.Empty;

        _tooltip = new ToolTip();

        SetupForm();
        BuildLayout();
        SetupCountdownTimer();

        LogAudit("confirmation.form_created", CreateLifecyclePayload("handle_created"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dwmDisposed = true;
            _dwmHandleGeneration++;
            _dwmEnsurePosted = false;
            StopCountdownTimer();
            StopForegroundVerificationTimer();
            _countdownTimer?.Dispose();
            _tooltip?.Dispose();
            DisposeDwmThumbnail();
            _previewBox?.Image?.Dispose();
            if (_previewBox != null)
                _previewBox.Image = null;

            // Lifecycle audit now happens in OnVisibleChanged/OnFormClosed/OnHandleDestroyed.
            // Keep a final fallback only if those events were suppressed (e.g., constructor failure).
            if (!_closeAudited)
            {
                _closeAudited = true;
                LogAudit("confirmation.form_closed", CreateLifecyclePayload("closed_fallback"));
            }
        }
        base.Dispose(disposing);
    }

    private void SetupForm()
    {
        Text = _text.Get("Confirmation_Title");
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = IdealClientSize;
        MinimumSize = MinimumClientSize;

        // Handle keyboard shortcuts and close button
        KeyPreview = true;
        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
        VisibleChanged += OnVisibleChanged;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Default Enter is handled by AcceptButton (= reject) so that approving
        // requires an explicit click or focused approve button.
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Reject();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        ApplyWindowLocation();

        var traceId = GetTraceIdFromSummary();
        if (!string.IsNullOrEmpty(traceId))
            _tracer.ConfirmationShown(traceId, _item.RecordingId, _item.ConfirmationId);

        LogAudit("confirmation.form_shown", CreateLifecyclePayload("shown"));

        _foregroundAttempts = 0;
        EnsureTopMostForeground();
        EnsureWindowSurfaceThumbnail();

        if (EnableDelayedForegroundVerification && IsHandleCreated && !IsDisposed)
        {
            ScheduleForegroundVerification();
        }

        // Safe default: put focus on reject so a stray Enter does not approve.
        _rejectButton?.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        bool restoreAfterRecreation = _restoreDwmAfterHandleCreated;
        _restoreDwmAfterHandleCreated = false;
        if (_windowSurfacePreview && (Visible || restoreAfterRecreation))
            ScheduleWindowSurfaceThumbnailEnsure(restoreAfterRecreation);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_suppressCloseResult) return;
        if (!_resultHandled)
        {
            _closeReason ??= _item.IsExpiredLocal ? _text.Get("Confirmation_Close_Expired") : _text.Get("Confirmation_Close_Rejected");
            _pendingDecision = ConfirmationDecision.Reject();
            _resultHandled = true;
            StopCountdownTimer();
            StopForegroundVerificationTimer();
        }
    }

    private void OnVisibleChanged(object? sender, EventArgs e)
    {
        if (!Visible)
        {
            LogAudit("confirmation.form_hidden", CreateLifecyclePayload("hidden"));
            Hidden?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(
                _pendingDecision, _closeReason,
                IsHandleCreated ? Handle.ToInt64() : 0, Visible));
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        DisposeDwmThumbnail();
        _closeAudited = true;
        LogAudit("confirmation.form_closed", CreateLifecyclePayload("closed"));
        Closed?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(
            _pendingDecision, _closeReason,
            IsHandleCreated ? Handle.ToInt64() : 0, Visible));

        // For tests and legacy callers that supplied a direct callback, invoke it
        // exactly once after the form is closed.
        if (!_suppressCloseResult && _pendingDecision != null)
        {
            var decision = _pendingDecision;
            _pendingDecision = null;
            try { _onResult?.Invoke(decision); }
            catch { /* Callback failures must not break form teardown. */ }
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _restoreDwmAfterHandleCreated = _windowSurfacePreview && Visible && !_dwmDisposed;
        _dwmHandleGeneration++;
        _dwmEnsurePosted = false;
        DisposeDwmThumbnail();
        base.OnHandleDestroyed(e);
        LogAudit("confirmation.handle_destroyed", CreateLifecyclePayload("handle_destroyed"));
        HandleDestroyed?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(
            _pendingDecision, _closeReason, 0, false));
    }

    private void EnsureWindowSurfaceThumbnail()
    {
        if (!_windowSurfacePreview || _dwmThumbnail != null ||
            !IsHandleCreated || IsDisposed || _summaryNode == null)
            return;

        if (!WindowIdParser.TryParse(GetString(_summaryNode, "window_id"), out var sourceWindow) ||
            sourceWindow == nint.Zero)
            return;

        try
        {
            if (!_dwmThumbnailProvider.TryRegister(Handle, sourceWindow, out var thumbnail))
                return;

            if (!thumbnail.TryQuerySourceSize(out var sourceSize))
            {
                thumbnail.Dispose();
                return;
            }

            _dwmThumbnail = thumbnail;
            _dwmSourceSize = sourceSize;
            if (!UpdateWindowSurfaceThumbnail())
                DisposeDwmThumbnail();
        }
        catch
        {
            DisposeDwmThumbnail();
        }
    }

    private void ScheduleWindowSurfaceThumbnailEnsure(bool allowTransientlyHiddenForm)
    {
        if (_dwmDisposed || _dwmEnsurePosted || !_windowSurfacePreview ||
            !IsHandleCreated || IsDisposed || _summaryNode == null)
            return;

        int generation = _dwmHandleGeneration;
        _dwmEnsurePosted = true;
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                if (generation != _dwmHandleGeneration)
                    return;

                _dwmEnsurePosted = false;
                if (_dwmDisposed || IsDisposed || !IsHandleCreated ||
                    (!allowTransientlyHiddenForm && !Visible))
                    return;

                EnsureWindowSurfaceThumbnail();
            }));
        }
        catch
        {
            if (generation == _dwmHandleGeneration)
                _dwmEnsurePosted = false;
        }
    }

    private bool UpdateWindowSurfaceThumbnail()
    {
        if (!_windowSurfacePreview || _dwmThumbnail == null || _previewPanel == null ||
            _dwmSourceSize.Width <= 0 || _dwmSourceSize.Height <= 0)
            return false;

        try
        {
            var panelScreenOrigin = _previewPanel.PointToScreen(Point.Empty);
            var panelClientOrigin = PointToClient(panelScreenOrigin);
            var panelClient = new Rectangle(panelClientOrigin, _previewPanel.ClientSize);
            // PointToClient and ClientSize already use WinForms device-pixel
            // coordinates. DWM consumes the destination in this top-level
            // form client space; applying DeviceDpi/96 here would scale the
            // same rectangle a second time on 150%/200% monitors.
            var destination = DwmThumbnailGeometry.Fit(panelClient, _dwmSourceSize);
            if (destination == Rectangle.Empty ||
                !_dwmThumbnail.TryUpdateDestination(destination, sourceClientAreaOnly: false))
                return false;

            _dwmDestination = destination;
            _previewBox.Visible = false;
            _previewFallbackLabel.Visible = false;
            _previewPanel.BackColor = Color.Transparent;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DisposeDwmThumbnail()
    {
        var thumbnail = _dwmThumbnail;
        _dwmThumbnail = null;
        _dwmSourceSize = Size.Empty;
        _dwmDestination = Rectangle.Empty;
        if (thumbnail == null)
            return;

        try { thumbnail.Dispose(); }
        catch { }

        if (_windowSurfacePreview && _previewFallbackLabel != null && !_previewFallbackLabel.IsDisposed)
        {
            _previewPanel.BackColor = Color.FromArgb(32, 32, 32);
            _previewFallbackLabel.Text = BuildWindowSurfaceFallback(_summaryNode);
            _previewFallbackLabel.Visible = true;
        }
    }

    private string BuildWindowSurfaceFallback(JsonNode? summary)
    {
        string title = summary == null ? "N/A" : GetString(summary, "source_title");
        string application = summary == null ? "" : GetString(summary, "source_application");
        string identity = string.IsNullOrWhiteSpace(application) || application == "N/A"
            ? title
            : $"{title} ({application})";
        return _text.Format("Confirmation_Preview_WindowSurface_Fallback", identity);
    }

    private string GetCaptureSemanticsDisplay(JsonNode summary)
    {
        string semantics = GetString(summary, "capture_semantics");
        return semantics switch
        {
            "window_surface" => _text.Get("Confirmation_CaptureSemantics_WindowSurface"),
            "screen_rectangle" => _text.Get("Confirmation_CaptureSemantics_ScreenRectangle"),
            "display_surface" => _text.Get("Confirmation_CaptureSemantics_Display"),
            "region_rectangle" => _text.Get("Confirmation_CaptureSemantics_Region"),
            _ => semantics
        };
    }

    private string BuildPreviewSemanticsLabel(JsonNode summary)
    {
        string semantics = GetString(summary, "capture_semantics");
        string label = semantics switch
        {
            "window_surface" => _text.Get("Confirmation_Preview_WindowSurface_Label"),
            "screen_rectangle" => _text.Get("Confirmation_Preview_ScreenRectangle_Label"),
            "display_surface" => _text.Get("Confirmation_Preview_Display_Label"),
            "region_rectangle" => _text.Get("Confirmation_Preview_Region_Label"),
            _ => _text.Get("Confirmation_Preview_Fallback")
        };

        return _captureBounds != null
            ? $"{label} | X={_captureBounds.X} Y={_captureBounds.Y} W={_captureBounds.Width} H={_captureBounds.Height}"
            : label + " | " + _text.Get("Confirmation_Preview_NoBounds");
    }

    /// <summary>
    /// Closes the form without triggering approve/reject callback.
    /// Used for programmatic close (queue advance, SetAllIdle, etc.).
    /// </summary>
    public void CloseWithoutResult(string? reason = null)
    {
        StopCountdownTimer();
        StopForegroundVerificationTimer();
        _closeReason ??= reason ?? _text.Get("Confirmation_Close_QueueAdvanced");
        _suppressCloseResult = true;
        Close();
    }

    /// <summary>
    /// Closes the form with the user decision. The decision is delivered via
    /// the <see cref="Closed"/> and <see cref="HandleDestroyed"/> lifecycle
    /// events so the caller can implement the capture-safe barrier.
    /// </summary>
    public void CloseWithDecision(ConfirmationDecision decision, string? closeReason = null)
    {
        if (_resultHandled) return;
        _resultHandled = true;
        StopCountdownTimer();
        StopForegroundVerificationTimer();
        _pendingDecision = decision;
        _closeReason ??= closeReason ?? (decision.Approved
            ? _text.Get("Confirmation_Close_Approved")
            : _text.Get("Confirmation_Close_Rejected"));
        Close();
    }

    private void BuildLayout()
    {
        var s = JsonNode.Parse(JsonSerializer.Serialize(_item.Summary))!;
        _summaryNode = s;

        var rootTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 7,
            ColumnCount = 1,
            AutoSize = false
        };
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 0 header
        rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // 1 main content (info + preview)
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 2 output
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 3 progress
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 4 timeout
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 5 warning
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 6 buttons

        var maxTextWidth = Math.Max(200, ClientSize.Width - rootTable.Padding.Horizontal - 20);

        // Header (outside scrollable area)
        var titleLabel = new Label
        {
            Text = _text.Get("Confirmation_RequestTitle"),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 4)
        };

        var queueLabel = new Label
        {
            Text = _text.Format("Confirmation_QueuePosition", _queuePosition, _totalCount),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        var headerPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(queueLabel);
        rootTable.Controls.Add(headerPanel, 0, 0);

        // Main content: info + preview, scrollable only when below preferred minimum height.
        _mainContentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, 12)
        };

        var contentTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        contentTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, InfoColumnProportion * 100f));
        contentTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, PreviewColumnProportion * 100f));

        // Info panel
        _infoPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 10, 0)
        };

        _infoTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 12,
            AutoSize = false
        };
        _infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        for (int i = 0; i < 12; i++)
            _infoTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 12f));

        int row = 0;
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Source"), GetString(s, "source"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_SourceType"), GetString(s, "source_type"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_CaptureSemantics"),
            GetCaptureSemanticsDisplay(s));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_SourceTitle"), GetString(s, "source_title"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Duration"), GetString(s, "duration"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Countdown"), GetCountdownDisplay(s));
        var rawAudio = GetString(s, "audio");
        var audioSourceKind = GetString(s, "audio_source_kind");
        string audioLabel;
        string audioDisplayValue;
        if (string.Equals(audioSourceKind, "system-loopback", StringComparison.Ordinal))
        {
            var outputName = TryGetString(s, "audio_system_output_name", out var systemOutputName)
                ? systemOutputName
                : GetString(s, "audio_system_default_output");
            var outputSelection = GetString(s, "audio_system_output_selection");
            audioLabel = _text.Get("Confirmation_Info_SystemAudio");
            audioDisplayValue = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                string.Equals(outputSelection, "selected", StringComparison.Ordinal)
                    ? _text.Get("Confirmation_Info_SystemAudioSelectedOn")
                    : _text.Get("Confirmation_Info_SystemAudioOn"),
                outputName);
        }
        else
        {
            audioLabel = _text.Get("Confirmation_Info_Audio");
            audioDisplayValue = TryGetString(s, "audio_device", out var audioDeviceName)
                ? audioDeviceName
                : (rawAudio == "No audio" ? _text.Get("Confirmation_Info_NoAudio") : rawAudio);
        }
        AddInfoRow(_infoTable, row++, audioLabel, audioDisplayValue);
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_NestedRole"), GetString(s, "nested_role"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_RecordingId"), GetString(s, "recording_id"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_ConfirmationId"), GetString(s, "confirmation_id"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Timeout"), GetString(s, "timeout_seconds"));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_ExpiresAt"), GetString(s, "expires_at"));

        if (string.Equals(GetString(s, "mode"), "screenshot_series", StringComparison.Ordinal))
        {
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Mode"), _text.Get("Confirmation_Value_ScreenshotSeries"));
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Interval"), GetString(s, "series_interval_ms") + " ms");
            var bound = GetString(s, "series_max_count");
            if (bound == "N/A" || string.IsNullOrWhiteSpace(bound) || bound == "0")
                bound = GetString(s, "series_max_duration_seconds") + " s";
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Bound"), bound);
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_PlannedFrames"), GetString(s, "series_planned_frame_count"));
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_OutputKind"), _text.Get("Confirmation_Value_PngSequence"));
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_OutputDirectory"), GetString(s, "output"));
        }

        _infoPanel.Controls.Add(_infoTable);
        contentTable.Controls.Add(_infoPanel, 0, 0);

        // Preview panel
        _previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(32, 32, 32),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0),
            MinimumSize = new Size(120, 120)
        };

        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = false
        };

        _previewFallbackLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            Visible = false
        };

        _previewPanel.Controls.Add(_previewBox);
        _previewPanel.Controls.Add(_previewFallbackLabel);

        _captureBounds = ConfirmationPreviewBuilder.ParseBounds(s);
        _windowSurfacePreview = string.Equals(
            GetString(s, "capture_semantics"),
            "window_surface",
            StringComparison.Ordinal);

        if (_windowSurfacePreview)
        {
            // The DWM thumbnail is registered against the top-level
            // confirmation HWND, not this Panel child HWND. Keep the panel as
            // a reserved, transparent visual surface and ensure no opaque
            // PictureBox/fallback child can paint over the DWM destination.
            _previewPanel.BackColor = Color.Transparent;
        }

        _previewBoundsLabel = new Label
        {
            Dock = DockStyle.Bottom,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        _previewBoundsLabel.Text = BuildPreviewSemanticsLabel(s);

        var previewContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        previewContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        previewContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewContainer.Controls.Add(_previewPanel, 0, 0);
        previewContainer.Controls.Add(_previewBoundsLabel, 0, 1);
        contentTable.Controls.Add(previewContainer, 1, 0);

        _mainContentPanel.Controls.Add(contentTable);
        rootTable.Controls.Add(_mainContentPanel, 0, 1);

        // Keep header/warning labels wrapped/ellipsed after DPI scaling or size changes.
        SizeChanged += (_, _) =>
        {
            var available = Math.Max(200, ClientSize.Width - rootTable.Padding.Horizontal - 20);
            titleLabel.MaximumSize = new Size(available, 0);
            queueLabel.MaximumSize = new Size(available, 0);
            if (_warningLabel != null)
                _warningLabel.MaximumSize = new Size(available, 0);
            if (_timeoutLabel != null)
                _timeoutLabel.MaximumSize = new Size(available, 0);
            if (_windowSurfacePreview && _dwmThumbnail != null && !UpdateWindowSurfaceThumbnail())
                DisposeDwmThumbnail();
        };

        if (_windowSurfacePreview)
        {
            // A window_surface promise can never use a desktop screenshot.
            // DWM registration is deferred until the confirmation HWND exists.
            _previewBox.Visible = false;
            _previewFallbackLabel.Text = BuildWindowSurfaceFallback(s);
            _previewFallbackLabel.Visible = true;
        }
        else
        {
            // Display/region/window-rectangle plans truthfully show composed
            // desktop pixels using the existing GDI provider.
            var previewMaxSize = ComputePreviewMaxSize();
            var previewBitmap = ConfirmationPreviewBuilder.TryBuildPreview(s, _previewProvider, previewMaxSize, out var fallbackMessage);
            if (previewBitmap != null)
            {
                _previewBox.Image = previewBitmap;
                _previewBox.Visible = true;
                _previewFallbackLabel.Visible = false;
            }
            else
            {
                _previewBox.Visible = false;
                _previewFallbackLabel.Text = fallbackMessage ?? _text.Get("Confirmation_Preview_Fallback");
                _previewFallbackLabel.Visible = true;
            }
        }

        // Set the minimum scrollable height based on the content's preferred size.
        // Width scroll is not desired; the table fills horizontally.
        ApplyMainContentMinSize();

        // Output directory panel (layout container, no absolute coordinates)
        BuildOutputPanel();
        rootTable.Controls.Add(_outputPanel, 0, 2);

        // Countdown progress bar
        _timeoutProgressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 16,
            Minimum = 0,
            Maximum = 1000,
            Value = 1000,
            Style = ProgressBarStyle.Continuous,
            Margin = new Padding(0, 0, 0, 8)
        };
        rootTable.Controls.Add(_timeoutProgressBar, 0, 3);

        _timeoutLabel = new Label
        {
            Text = _text.Get("Confirmation_Timeout_Initializing"),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        rootTable.Controls.Add(_timeoutLabel, 0, 4);

        // Warning label
        _warningLabel = new Label
        {
            Text = _text.Get("Confirmation_Warning"),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.DarkRed,
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 16)
        };

        // Low-volume warning: if the microphone is enabled and the volume is
        // below 10%, show an explicit warning but do not block recording.
        // Muted devices are rejected before the confirmation form is created.
        if (s["audio_volume_percent"]?.GetValue<int?>() is int volumePercent && volumePercent >= 0 && volumePercent < 10)
        {
            _warningLabel.Text = _text.Format("Confirmation_Warning_LowVolume", volumePercent);
        }

        rootTable.Controls.Add(_warningLabel, 0, 5);

        // Buttons
        _buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0)
        };

        var buttonFont = new Font("Segoe UI", 10, FontStyle.Bold);

        _rejectButton = new Button
        {
            Text = _text.Get("Confirmation_Button_Reject"),
            Size = MeasureButtonSize(_text.Get("Confirmation_Button_Reject"), buttonFont),
            BackColor = Color.FromArgb(200, 50, 50),
            ForeColor = Color.White,
            Font = buttonFont,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        _rejectButton.FlatAppearance.BorderColor = Color.FromArgb(150, 50, 50);
        _rejectButton.Click += (_, _) => Reject();

        _approveButton = new Button
        {
            Text = _text.Get("Confirmation_Button_Approve"),
            Size = MeasureButtonSize(_text.Get("Confirmation_Button_Approve"), buttonFont),
            BackColor = Color.FromArgb(0, 128, 0),
            ForeColor = Color.White,
            Font = buttonFont,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0)
        };
        _approveButton.FlatAppearance.BorderColor = Color.FromArgb(0, 100, 0);
        _approveButton.Click += (_, _) => Approve();

        _buttonPanel.Controls.Add(_rejectButton);
        _buttonPanel.Controls.Add(_approveButton);
        rootTable.Controls.Add(_buttonPanel, 0, 6);

        Controls.Add(rootTable);

        // Safe default: Enter maps to reject, not approve.
        AcceptButton = _rejectButton;
        CancelButton = _rejectButton;
    }

    private Size ComputePreviewMaxSize()
    {
        var available = new Size(
            Math.Max(400, (int)(ClientSize.Width * PreviewColumnProportion) - 40),
            Math.Max(260, (int)(ClientSize.Height * 0.55) - 40));
        return available;
    }

    private void ApplyMainContentMinSize()
    {
        if (_mainContentPanel == null || _infoTable == null || _previewPanel == null || _previewBoundsLabel == null)
            return;

        _mainContentPanel.SuspendLayout();
        try
        {
            var infoRowSample = TextRenderer.MeasureText("Xy", new Font("Segoe UI", 9));
            int rowMinHeight = infoRowSample.Height + 4;
            int infoPreferredHeight = (rowMinHeight * 12) + _infoPanel.Padding.Vertical;

            int previewLabelHeight = _previewBoundsLabel.PreferredSize.Height + _previewBoundsLabel.Margin.Vertical;
            int previewPreferredHeight = Math.Max(_previewPanel.MinimumSize.Height, 260) + previewLabelHeight;

            int preferredHeight = Math.Max(infoPreferredHeight, previewPreferredHeight);
            _mainContentPanel.AutoScrollMinSize = new Size(0, preferredHeight);
        }
        finally
        {
            _mainContentPanel.ResumeLayout(true);
        }
    }

    private void BuildOutputPanel()
    {
        _outputPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 12),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        _outputTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _outputTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        _outputTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // path
        _outputTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // actions

        var outputTitleLabel = new Label
        {
            Text = _text.Get("Confirmation_Output_Title"),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.Gray,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        };

        var pathFont = new Font("Segoe UI", 9);
        var pathText = GetCurrentOutputPath(null);
        int pathTextHeight = TextRenderer.MeasureText(pathText, pathFont).Height;

        _outputPathLabel = new Label
        {
            Text = pathText,
            Font = pathFont,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.Black,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 8),
            MinimumSize = new Size(0, pathTextHeight)
        };
        _tooltip.SetToolTip(_outputPathLabel, _outputPathLabel.Text);

        var outputActionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        _changeOutputButton = new Button
        {
            Text = _text.Get("Confirmation_Output_Change"),
            Size = MeasureButtonSize(_text.Get("Confirmation_Output_Change"), new Font("Segoe UI", 9), horizontalPadding: 16, verticalPadding: 6, minHeight: 28),
            FlatStyle = FlatStyle.Standard,
            Margin = new Padding(0, 0, 12, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        _changeOutputButton.Click += (_, _) => ChangeOutputDirectory();

        _rememberOutputCheckBox = new CheckBox
        {
            Text = _text.Get("Confirmation_Output_Remember"),
            AutoSize = true,
            Checked = false,
            Margin = new Padding(0, 2, 0, 0)
        };

        outputActionsPanel.Controls.Add(_changeOutputButton);
        outputActionsPanel.Controls.Add(_rememberOutputCheckBox);

        _outputTable.Controls.Add(outputTitleLabel, 0, 0);
        _outputTable.Controls.Add(_outputPathLabel, 0, 1);
        _outputTable.Controls.Add(outputActionsPanel, 0, 2);
        _outputPanel.Controls.Add(_outputTable);
    }

    internal static Size MeasureButtonSize(string text, Font font, int horizontalPadding = 32, int verticalPadding = 16, int minHeight = 44)
    {
        var measured = TextRenderer.MeasureText(text, font);
        int width = measured.Width + horizontalPadding;
        int height = Math.Max(minHeight, measured.Height + verticalPadding);
        return new Size(width, height);
    }

    private void AddInfoRow(TableLayoutPanel table, int row, string label, string value)
    {
        var labelLabel = new Label
        {
            Text = label + ":",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 8, 2)
        };

        var valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI", 9),
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 0, 2)
        };
        _tooltip.SetToolTip(valueLabel, value);

        table.Controls.Add(labelLabel, 0, row);
        table.Controls.Add(valueLabel, 1, row);
        _infoRows.Add((labelLabel, valueLabel));
    }

    private static string GetString(JsonNode node, string key)
    {
        var val = node[key];
        if (val == null) return "N/A";
        if (val is JsonValue jsonValue)
        {
            try
            {
                return jsonValue.GetValue<string?>() ?? "N/A";
            }
            catch
            {
                // Numeric and boolean metadata still use their JSON text form.
            }
        }
        return val.ToString();
    }

    private static bool TryGetString(JsonNode node, string key, out string value)
    {
        value = GetString(node, key);
        return value != "N/A" && !string.IsNullOrEmpty(value);
    }

    private string GetCountdownDisplay(JsonNode summary)
    {
        var raw = GetString(summary, "countdown_seconds");
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return _text.Get("Confirmation_Value_NA");

        return seconds <= 0
            ? _text.Get("Confirmation_Info_Countdown_Off")
            : _text.Format("Confirmation_Info_Countdown_Seconds", seconds);
    }

    private string GetInitialOutputDirectory(string? defaultOutputDirectory)
    {
        // Picker should start from the directory of the current recording output,
        // so the user is anchored to where this specific recording will go.
        try
        {
            var outputPath = GetCurrentOutputPathFromSummary();
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(defaultOutputDirectory) && Directory.Exists(defaultOutputDirectory))
            return defaultOutputDirectory;

        return Paths.DefaultOutputDir;
    }

    private string GetCurrentOutputPath(JsonNode? summary)
    {
        var path = GetCurrentOutputPathFromSummary();
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        // Fallback: show the configured default directory.
        return Path.Combine(_initialOutputDirectory, _text.Get("Confirmation_Output_AutoName"));
    }

    private string? GetCurrentOutputPathFromSummary()
    {
        try
        {
            var s = JsonNode.Parse(JsonSerializer.Serialize(_item.Summary));
            var output = s?["output"];
            if (output != null)
                return output.GetValue<string?>();
        }
        catch { }
        return null;
    }

    private string? GetTraceIdFromSummary()
    {
        try
        {
            var s = JsonNode.Parse(JsonSerializer.Serialize(_item.Summary, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }));
            var traceId = s?["trace_id"];
            if (traceId != null)
                return traceId.GetValue<string?>();
        }
        catch { }
        return null;
    }

    private void ChangeOutputDirectory()
    {
        if (_resultHandled || (_item.IsExpiredLocal && !_approveButton.Enabled))
            return;

        var initial = _selectedOutputDirectory ?? _initialOutputDirectory;
        var selected = _directoryPicker.PickDirectory(initial);
        if (string.IsNullOrWhiteSpace(selected))
            return;

        _selectedOutputDirectory = selected;
        UpdateOutputPathLabel();
    }

    private void UpdateOutputPathLabel()
    {
        var summaryPath = GetCurrentOutputPathFromSummary();
        if (!string.IsNullOrWhiteSpace(summaryPath))
        {
            var name = Path.GetFileName(summaryPath);
            _outputPathLabel.Text = Path.Combine(_selectedOutputDirectory ?? _initialOutputDirectory, name);
        }
        else
        {
            _outputPathLabel.Text = Path.Combine(_selectedOutputDirectory ?? _initialOutputDirectory, _text.Get("Confirmation_Output_AutoName"));
        }

        // Keep the path row height in sync with the current text and font.
        int pathTextHeight = TextRenderer.MeasureText(_outputPathLabel.Text, _outputPathLabel.Font).Height;
        _outputPathLabel.MinimumSize = new Size(0, pathTextHeight);

        _tooltip.SetToolTip(_outputPathLabel, _outputPathLabel.Text);
    }

    private void Reject()
    {
        CloseWithDecision(ConfirmationDecision.Reject());
    }

    private void SetupCountdownTimer()
    {
        _countdownTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();
        UpdateCountdown();
    }

    private void StopCountdownTimer()
    {
        _countdownTimer?.Stop();
    }

    private void UpdateCountdown()
    {
        var now = _utcNowProvider();
        var total = _item.ExpiresAtUtc - _item.CreatedAtUtc;
        var remaining = _item.ExpiresAtUtc - now;

        if (remaining <= TimeSpan.Zero || _item.IsExpiredLocal)
        {
            _timeoutProgressBar.Value = 0;
            _timeoutLabel.Text = _text.Get("Confirmation_Timeout_Expired");
            _timeoutLabel.ForeColor = Color.DarkRed;
            _approveButton.Enabled = false;
            _changeOutputButton.Enabled = false;
            StopCountdownTimer();
            return;
        }

        var totalMs = Math.Max(1, (int)total.TotalMilliseconds);
        var remainingMs = (int)remaining.TotalMilliseconds;
        var ratio = Math.Max(0, Math.Min(1.0, (double)remainingMs / totalMs));
        _timeoutProgressBar.Value = (int)(ratio * _timeoutProgressBar.Maximum);

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        _timeoutLabel.Text = seconds <= 5
            ? _text.Format("Confirmation_Timeout_SecondsUrgent", seconds)
            : _text.Format("Confirmation_Timeout_Seconds", seconds);
        _timeoutLabel.ForeColor = seconds <= 5 ? Color.DarkRed : Color.Gray;
    }

    private void Approve()
    {
        var rememberOutputDirectory = _rememberOutputCheckBox?.Checked ?? false;
        var outputDirectory = _selectedOutputDirectory;
        if (rememberOutputDirectory && string.IsNullOrWhiteSpace(outputDirectory))
            outputDirectory = _initialOutputDirectory;

        var decision = ConfirmationDecision.Approve(
            outputDirectory,
            rememberOutputDirectory);
        CloseWithDecision(decision, _text.Get("Confirmation_Close_Approved"));
    }

    private void ApplyWindowLocation()
    {
        Rectangle? captureBounds = _captureBounds != null
            ? new Rectangle(_captureBounds.X, _captureBounds.Y, _captureBounds.Width, _captureBounds.Height)
            : null;

        var computed = ComputeConfirmationBounds(
            captureBounds,
            Size,
            _workingAreas,
            _fallbackWorkingArea);

        _targetWorkingArea = computed.WorkingArea;
        _targetScreenIndex = computed.ScreenIndex;

        if (computed.Bounds != Rectangle.Empty)
        {
            // Adjust size constraints so that the computed bounds are achievable
            // even when the static MinimumSize (scaled by DPI) is larger than the
            // target working area. The scrollable content area absorbs the shrink.
            var desiredSize = computed.Bounds.Size;
            MinimumSize = new Size(
                Math.Min(MinimumSize.Width, desiredSize.Width),
                Math.Min(MinimumSize.Height, desiredSize.Height));
            MaximumSize = desiredSize;

            Bounds = computed.Bounds;
        }
    }

    private void ScheduleForegroundVerification()
    {
        if (_foregroundVerifyTimer == null)
        {
            _foregroundVerifyTimer = new System.Windows.Forms.Timer
            {
                Interval = ForegroundVerifyDelayMs
            };
            _foregroundVerifyTimer.Tick += OnForegroundVerifyTimerTick;
        }

        _foregroundVerifyTimer.Stop();
        _foregroundVerifyTimer.Start();
    }

    private void OnForegroundVerifyTimerTick(object? sender, EventArgs e)
    {
        _foregroundVerifyTimer?.Stop();
        EnsureTopMostForeground();
    }

    private void StopForegroundVerificationTimer()
    {
        if (_foregroundVerifyTimer != null)
        {
            _foregroundVerifyTimer.Stop();
            _foregroundVerifyTimer.Tick -= OnForegroundVerifyTimerTick;
            _foregroundVerifyTimer.Dispose();
            _foregroundVerifyTimer = null;
        }
    }

    private void EnsureTopMostForeground()
    {
        if (IsDisposed || Disposing)
            return;

        if (_foregroundAttempts >= MaxForegroundAttempts)
            return;

        _foregroundAttempts++;

        var hWnd = Handle;
        IntPtr beforeForeground = IntPtr.Zero;
        try
        {
            beforeForeground = _windowActivator.GetForegroundWindow();
        }
        catch (Exception ex)
        {
            RecordForegroundError("get_foreground_before", ex);
        }

        LogAudit("confirmation.foreground_attempt", CreateForegroundPayload(
            _foregroundAttempts,
            hWnd,
            beforeForeground,
            stage: "foreground_attempt"));

        bool setTopMostSuccess = false;
        bool setForegroundSuccess = false;
        bool bringToTopSuccess = false;

        try
        {
            setTopMostSuccess = _windowActivator.SetTopMost(hWnd);
        }
        catch (Exception ex)
        {
            RecordForegroundError("set_topmost", ex);
        }

        try
        {
            setForegroundSuccess = _windowActivator.SetForeground(hWnd);
        }
        catch (Exception ex)
        {
            RecordForegroundError("set_foreground", ex);
        }

        if (!setForegroundSuccess)
        {
            try
            {
                bringToTopSuccess = _windowActivator.BringToTop(hWnd);
            }
            catch (Exception ex)
            {
                RecordForegroundError("bring_to_top", ex);
            }
        }

        IntPtr afterForeground = IntPtr.Zero;
        try
        {
            afterForeground = _windowActivator.GetForegroundWindow();
        }
        catch (Exception ex)
        {
            RecordForegroundError("get_foreground_after", ex);
        }

        bool becameForeground = afterForeground == hWnd;

        LogAudit("confirmation.foreground_result", new
        {
            confirmation_id = _item.ConfirmationId,
            recording_id = _item.RecordingId,
            attempt = _foregroundAttempts,
            max_attempts = MaxForegroundAttempts,
            form_handle = hWnd.ToInt64(),
            visible = Visible,
            topmost = TopMost,
            bounds = new { x = Bounds.X, y = Bounds.Y, w = Bounds.Width, h = Bounds.Height },
            target_screen_index = _targetScreenIndex,
            target_working_area = new
            {
                x = _targetWorkingArea.X,
                y = _targetWorkingArea.Y,
                w = _targetWorkingArea.Width,
                h = _targetWorkingArea.Height
            },
            foreground_before = beforeForeground.ToInt64(),
            foreground_after = afterForeground.ToInt64(),
            became_foreground = becameForeground,
            set_window_pos_success = setTopMostSuccess,
            set_foreground_window_success = setForegroundSuccess,
            bring_window_to_top_success = bringToTopSuccess,
            error = _foregroundError,
            error_stage = _foregroundErrorStage
        });

        _foregroundError = null;
        _foregroundErrorStage = null;
    }

    private object CreateForegroundPayload(int attempt, IntPtr hWnd, IntPtr foregroundBefore, string stage)
    {
        return new
        {
            confirmation_id = _item.ConfirmationId,
            recording_id = _item.RecordingId,
            attempt,
            max_attempts = MaxForegroundAttempts,
            stage,
            form_handle = hWnd.ToInt64(),
            visible = Visible,
            topmost = TopMost,
            bounds = new { x = Bounds.X, y = Bounds.Y, w = Bounds.Width, h = Bounds.Height },
            target_screen_index = _targetScreenIndex,
            target_working_area = new
            {
                x = _targetWorkingArea.X,
                y = _targetWorkingArea.Y,
                w = _targetWorkingArea.Width,
                h = _targetWorkingArea.Height
            },
            foreground_before = foregroundBefore.ToInt64()
        };
    }

    private object CreateLifecyclePayload(string stage)
    {
        return new
        {
            confirmation_id = _item.ConfirmationId,
            recording_id = _item.RecordingId,
            stage,
            close_reason = _closeReason ?? "unknown",
            form_handle = IsHandleCreated ? Handle.ToInt64() : 0,
            visible = Visible,
            topmost = TopMost,
            bounds = new { x = Bounds.X, y = Bounds.Y, w = Bounds.Width, h = Bounds.Height },
            target_screen_index = _targetScreenIndex,
            target_working_area = new
            {
                x = _targetWorkingArea.X,
                y = _targetWorkingArea.Y,
                w = _targetWorkingArea.Width,
                h = _targetWorkingArea.Height
            }
        };
    }

    private void LogAudit(string eventName, object payload)
    {
        try
        {
            _auditLogger?.Invoke(eventName, payload);
        }
        catch
        {
            // Audit failures must not break the confirmation UI.
        }
    }

    private void RecordForegroundError(string stage, Exception ex)
    {
        _foregroundError ??= ex.Message;
        _foregroundErrorStage ??= stage;
    }

    /// <summary>
    /// Test seam to manually run a foreground verification tick without waiting
    /// for the real timer. Does nothing if the form is disposed or the maximum
    /// number of attempts has already been reached.
    /// </summary>
    internal void RunForegroundVerificationForTest() => EnsureTopMostForeground();

    /// <summary>
    /// Computes the final window bounds for the confirmation form and the
    /// target working area used for the calculation.
    ///
    /// The returned bounds are guaranteed to fit entirely inside the target
    /// working area, even when the ideal <paramref name="formSize"/> is larger
    /// than the working area. In that case the size is scaled down
    /// proportionally until it fits.
    /// </summary>
    internal static ComputedBounds ComputeConfirmationBounds(
        Rectangle? captureBounds,
        Size formSize,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea)
    {
        var (targetArea, screenIndex) = SelectTargetWorkingArea(captureBounds, workingAreas, fallbackWorkingArea);

        if (targetArea == Rectangle.Empty)
        {
            // No screen information available; keep the form at the origin.
            return new ComputedBounds(
                new Rectangle(0, 0, formSize.Width, formSize.Height),
                Rectangle.Empty,
                -1);
        }

        // Scale down proportionally if the ideal form size does not fit.
        int desiredWidth = formSize.Width;
        int desiredHeight = formSize.Height;

        if (desiredWidth > targetArea.Width || desiredHeight > targetArea.Height)
        {
            float scaleX = (float)targetArea.Width / desiredWidth;
            float scaleY = (float)targetArea.Height / desiredHeight;
            float scale = Math.Min(scaleX, scaleY);
            desiredWidth = (int)(desiredWidth * scale);
            desiredHeight = (int)(desiredHeight * scale);
        }

        // Prefer a minimum usable size, but never let it exceed the working area.
        desiredWidth = Math.Max(desiredWidth, Math.Min(MinConfirmationSize.Width, targetArea.Width));
        desiredHeight = Math.Max(desiredHeight, Math.Min(MinConfirmationSize.Height, targetArea.Height));

        if (desiredWidth > targetArea.Width)
            desiredWidth = targetArea.Width;
        if (desiredHeight > targetArea.Height)
            desiredHeight = targetArea.Height;

        int x = targetArea.X + (targetArea.Width - desiredWidth) / 2;
        int y = targetArea.Y + (targetArea.Height - desiredHeight) / 2;

        // Clamp so the entire window stays inside the working area.
        if (x + desiredWidth > targetArea.Right)
            x = targetArea.Right - desiredWidth;
        if (y + desiredHeight > targetArea.Bottom)
            y = targetArea.Bottom - desiredHeight;
        if (x < targetArea.X)
            x = targetArea.X;
        if (y < targetArea.Y)
            y = targetArea.Y;

        return new ComputedBounds(
            new Rectangle(x, y, desiredWidth, desiredHeight),
            targetArea,
            screenIndex);
    }

    private static (Rectangle WorkingArea, int ScreenIndex) SelectTargetWorkingArea(
        Rectangle? captureBounds,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea)
    {
        if (captureBounds.HasValue && workingAreas.Count > 0)
        {
            var center = new Point(
                captureBounds.Value.X + captureBounds.Value.Width / 2,
                captureBounds.Value.Y + captureBounds.Value.Height / 2);

            for (int i = 0; i < workingAreas.Count; i++)
            {
                if (workingAreas[i].Contains(center))
                    return (workingAreas[i], i);
            }
        }

        return (fallbackWorkingArea, -1);
    }

    internal readonly record struct ComputedBounds(Rectangle Bounds, Rectangle WorkingArea, int ScreenIndex);
}
