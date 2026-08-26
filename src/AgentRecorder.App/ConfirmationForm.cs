using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
    private ConfirmationCaptureBounds? _captureBounds;
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
    private readonly IConfirmationThemeProvider _themeProvider;
    private readonly IConfirmationNativeChromeAdapter _nativeChromeAdapter;
    private readonly IConfirmationScrollThemeAdapter _scrollThemeAdapter;
    private readonly bool _previewOnly;
    private ConfirmationThemeSnapshot _themeSnapshot;
    private int _themeApplyCount;
    private readonly ToolTip _tooltip;
    private readonly List<(Label Label, Label Value)> _infoRows = new();
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
    private ConfirmationCountdownRing _countdownRing = null!;
    private Label _timeoutLabel = null!;
    private Label _warningLabel = null!;
    private System.Windows.Forms.Timer _countdownTimer = null!;
    private FlowLayoutPanel _buttonPanel = null!;
    private Panel _mainContentPanel = null!;
    private Panel _infoPanel = null!;
    private TableLayoutPanel _infoTable = null!;
    private TableLayoutPanel _outputTable = null!;
    private TableLayoutPanel _rootTable = null!;
    private FlowLayoutPanel _headerPanel = null!;
    private TableLayoutPanel _contentTable = null!;
    private TableLayoutPanel _previewContainer = null!;
    private FlowLayoutPanel _outputActionsPanel = null!;
    private Label _titleLabel = null!;
    private Label _queueLabel = null!;
    private Label _outputTitleLabel = null!;
    private bool _timeoutIsExpired;
    private bool _timeoutIsUrgent;

    internal bool HasPreviewAreaForTests => _previewPanel != null;
    internal bool HasContentScrollPanelForTests => _mainContentPanel != null;
    internal Rectangle ContentScrollPanelBoundsForTests => _mainContentPanel?.Bounds ?? Rectangle.Empty;
    internal Size ContentScrollAutoScrollMinSizeForTests => _mainContentPanel?.AutoScrollMinSize ?? Size.Empty;
    internal Point ContentScrollPositionForTests => _mainContentPanel?.AutoScrollPosition ?? Point.Empty;
    internal void ScrollContentToBottomForTests()
    {
        if (_mainContentPanel != null && !_mainContentPanel.IsDisposed)
            _mainContentPanel.AutoScrollPosition = new Point(0, _mainContentPanel.AutoScrollMinSize.Height);
    }
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
    internal double CountdownRingRatioForTests => _countdownRing?.Snapshot.Ratio ?? 0d;
    internal int CountdownRingSecondsForTests => _countdownRing?.Snapshot.RemainingSeconds ?? 0;
    internal bool CountdownRingUrgentForTests => _countdownRing?.Snapshot.IsUrgent ?? false;
    internal bool CountdownRingExpiredForTests => _countdownRing?.Snapshot.IsExpired ?? true;
    internal bool CountdownRingEnabledForTests => _countdownRing?.Enabled ?? false;
    internal bool CountdownRingTabStopForTests => _countdownRing?.TabStop ?? true;
    internal AccessibleRole CountdownRingAccessibleRoleForTests => _countdownRing?.AccessibleRole ?? AccessibleRole.None;
    internal string CountdownRingAccessibleNameForTests => _countdownRing?.AccessibleName ?? "";
    internal Rectangle CountdownRingBoundsForTests => GetFormRelativeBounds(_countdownRing);
    internal string OutputPathTextForTests => _outputPathLabel?.Text ?? "";
    internal bool ChangeOutputButtonEnabledForTests => _changeOutputButton?.Enabled ?? false;
    internal bool RememberOutputCheckedForTests
    {
        get => _rememberOutputCheckBox?.Checked ?? false;
        set { if (_rememberOutputCheckBox != null) _rememberOutputCheckBox.Checked = value; }
    }

    internal Rectangle OutputPanelBoundsForTests => _outputPanel?.Bounds ?? Rectangle.Empty;
    internal Rectangle TimeoutLabelBoundsForTests => _timeoutLabel?.Bounds ?? Rectangle.Empty;
    internal Rectangle WarningLabelBoundsForTests => _warningLabel?.Bounds ?? Rectangle.Empty;
    internal string WarningTextForTests => _warningLabel?.Text ?? "";
    internal Rectangle ApproveButtonBoundsForTests => GetFormRelativeBounds(_approveButton);
    internal Rectangle RejectButtonBoundsForTests => GetFormRelativeBounds(_rejectButton);
    internal string ApproveButtonTextForTests => _approveButton?.Text ?? "";
    internal string RejectButtonTextForTests => _rejectButton?.Text ?? "";
    internal Button? ApproveButtonForTests => _approveButton;
    internal Button? RejectButtonForTests => _rejectButton;
    internal ConfirmationThemeKind ThemeKindForTests => _themeSnapshot.Kind;
    internal ConfirmationThemePalette ThemePaletteForTests => _themeSnapshot.Palette;
    internal int ThemeApplyCountForTests => _themeApplyCount;
    internal bool PreviewOnlyForTests => _previewOnly;
    internal Color InfoPanelBackColorForTests => _infoPanel?.BackColor ?? Color.Empty;
    internal Color OutputPanelBackColorForTests => _outputPanel?.BackColor ?? Color.Empty;
    internal Color TimeoutLabelForeColorForTests => _timeoutLabel?.ForeColor ?? Color.Empty;
    internal Color WarningLabelForeColorForTests => _warningLabel?.ForeColor ?? Color.Empty;
    internal Color PreviewFallbackForeColorForTests => _previewFallbackLabel?.ForeColor ?? Color.Empty;
    internal void ApplyThemeChangeForTests() => ApplyThemeFromProvider();
    internal void RefreshCountdownForTests() => UpdateCountdown();

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
        IDwmThumbnailProvider? dwmThumbnailProvider = null,
        IConfirmationThemeProvider? themeProvider = null,
        bool previewOnly = false,
        IConfirmationNativeChromeAdapter? nativeChromeAdapter = null,
        IConfirmationScrollThemeAdapter? scrollThemeAdapter = null)
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
        _themeProvider = themeProvider ?? new WindowsConfirmationThemeProvider();
        _nativeChromeAdapter = nativeChromeAdapter ?? new WindowsConfirmationNativeChromeAdapter();
        _scrollThemeAdapter = scrollThemeAdapter ?? new WindowsConfirmationScrollThemeAdapter();
        _themeSnapshot = ResolveInitialTheme();
        _previewOnly = previewOnly;
        _workingAreas = workingAreas ?? Array.Empty<Rectangle>();
        _fallbackWorkingArea = fallbackWorkingArea ?? Rectangle.Empty;

        _tooltip = new ToolTip();

        SetupForm();
        BuildLayout();
        ApplyTheme(_themeSnapshot);
        try { _themeProvider.ThemeChanged += OnThemeChanged; }
        catch { }
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
            try { _themeProvider.ThemeChanged -= OnThemeChanged; }
            catch { }
            try { _themeProvider.Dispose(); }
            catch { }
            try { _nativeChromeAdapter.Dispose(); }
            catch { }
            try { _scrollThemeAdapter.Dispose(); }
            catch { }
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

    private ConfirmationThemeSnapshot ResolveInitialTheme()
    {
        try { return _themeProvider.Resolve(); }
        catch { return new ConfirmationThemeSnapshot(ConfirmationThemeKind.Light, ConfirmationThemePalette.Light); }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
            return;

        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke((MethodInvoker)ApplyThemeFromProvider); }
            catch { }
            return;
        }

        ApplyThemeFromProvider();
    }

    private void ApplyThemeFromProvider()
    {
        if (IsDisposed || Disposing)
            return;

        var snapshot = _themeSnapshot;
        try { snapshot = _themeProvider.Resolve(); }
        catch { }

        try { ApplyTheme(snapshot); }
        catch { }
    }

    private void ApplyTheme(ConfirmationThemeSnapshot snapshot)
    {
        var palette = snapshot.Palette;
        _themeSnapshot = snapshot;
        _themeApplyCount++;

        BackColor = palette.WindowBackground;
        ForeColor = palette.PrimaryText;

        _rootTable.BackColor = palette.WindowBackground;
        _headerPanel.BackColor = palette.WindowBackground;
        _contentTable.BackColor = palette.WindowBackground;
        _mainContentPanel.BackColor = palette.WindowBackground;
        _infoPanel.BackColor = palette.Surface;
        _infoTable.BackColor = palette.Surface;
        _previewContainer.BackColor = palette.WindowBackground;
        _outputPanel.BackColor = palette.SecondarySurface;
        _outputTable.BackColor = palette.SecondarySurface;
        _outputActionsPanel.BackColor = palette.SecondarySurface;
        _buttonPanel.BackColor = palette.WindowBackground;

        _titleLabel.BackColor = Color.Transparent;
        _titleLabel.ForeColor = palette.PrimaryText;
        _queueLabel.BackColor = Color.Transparent;
        _queueLabel.ForeColor = palette.SecondaryText;

        foreach (var (label, value) in _infoRows)
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = palette.SecondaryText;
            value.BackColor = Color.Transparent;
            value.ForeColor = palette.PrimaryText;
        }

        _previewPanel.BackColor = _windowSurfacePreview
            ? Color.Transparent
            : palette.PreviewBackground;
        _previewBox.BackColor = palette.PreviewBackground;
        _previewFallbackLabel.BackColor = Color.Transparent;
        _previewFallbackLabel.ForeColor = palette.PreviewFallbackText;
        _previewBoundsLabel.BackColor = Color.Transparent;
        _previewBoundsLabel.ForeColor = palette.SecondaryText;

        _outputTitleLabel.BackColor = Color.Transparent;
        _outputTitleLabel.ForeColor = palette.SecondaryText;
        _outputPathLabel.BackColor = Color.Transparent;
        _outputPathLabel.ForeColor = palette.PrimaryText;
        _rememberOutputCheckBox.BackColor = palette.SecondarySurface;
        _rememberOutputCheckBox.ForeColor = palette.PrimaryText;

        _countdownRing.ApplyPalette(palette);
        _timeoutLabel.BackColor = Color.Transparent;
        _timeoutLabel.ForeColor = _timeoutIsExpired || _timeoutIsUrgent
            ? palette.ErrorText
            : palette.SecondaryText;
        _warningLabel.BackColor = Color.Transparent;
        _warningLabel.ForeColor = palette.WarningText;

        ApplyThemeToButtons(palette);
        ApplyNativeSurfaceThemes();
    }

    private void ApplyNativeSurfaceThemes()
    {
        if (IsDisposed || Disposing)
            return;

        if (IsHandleCreated)
        {
            try { _nativeChromeAdapter.Apply(Handle, _themeSnapshot.Kind); }
            catch { }
        }

        if (_mainContentPanel != null && _mainContentPanel.IsHandleCreated && !_mainContentPanel.IsDisposed)
        {
            try { _scrollThemeAdapter.Apply(_mainContentPanel, _themeSnapshot.Kind); }
            catch { }
        }
    }

    private void ApplyThemeToButtons(ConfirmationThemePalette palette)
    {
        ApplyButtonTheme(
            _changeOutputButton,
            palette.NeutralButtonBackground,
            palette.NeutralButtonHover,
            palette.NeutralButtonPressed,
            palette.NeutralButtonText,
            palette.NeutralButtonBorder,
            palette.NeutralButtonBackground,
            palette.DisabledText);
        ApplyButtonTheme(
            _approveButton,
            palette.ApproveBackground,
            palette.ApproveHover,
            palette.ApprovePressed,
            palette.ApproveText,
            palette.FocusBorder,
            palette.ApproveDisabled,
            palette.DisabledText);
        ApplyButtonTheme(
            _rejectButton,
            palette.RejectBackground,
            palette.RejectHover,
            palette.RejectPressed,
            palette.RejectText,
            palette.FocusBorder,
            palette.RejectDisabled,
            palette.DisabledText);
    }

    private static void ApplyButtonTheme(
        Button button,
        Color background,
        Color hover,
        Color pressed,
        Color text,
        Color border,
        Color disabledBackground,
        Color disabledText)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = button.Enabled ? background : disabledBackground;
        button.ForeColor = button.Enabled ? text : disabledText;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = pressed;
        button.FlatAppearance.BorderSize = 1;
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

        ApplyNativeSurfaceThemes();

        ApplyWindowLocation();

        var traceId = _item.Presentation.TraceId;
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

        ApplyNativeSurfaceThemes();
        try { BeginInvoke((MethodInvoker)ApplyNativeSurfaceThemes); }
        catch { }

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
            !IsHandleCreated || IsDisposed || string.IsNullOrWhiteSpace(_item.Presentation.WindowId))
            return;

        if (!WindowIdParser.TryParse(_item.Presentation.WindowId, out var sourceWindow) ||
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
            !IsHandleCreated || IsDisposed || string.IsNullOrWhiteSpace(_item.Presentation.WindowId))
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
            _previewPanel.BackColor = _themeSnapshot.Palette.PreviewBackground;
            _previewFallbackLabel.Text = BuildWindowSurfaceFallback(_item.Presentation);
            _previewFallbackLabel.Visible = true;
        }
    }

    private string BuildWindowSurfaceFallback(RecordingConfirmationPresentation presentation)
    {
        string title = DisplayValue(presentation.SourceTitle);
        string application = presentation.SourceApplication ?? "";
        string identity = string.IsNullOrWhiteSpace(application) || application == "N/A"
            ? title
            : $"{title} ({application})";
        return _text.Format("Confirmation_Preview_WindowSurface_Fallback", identity);
    }

    private string GetCaptureSemanticsDisplay()
    {
        string semantics = _item.Presentation.CaptureSemantics;
        return semantics switch
        {
            "window_surface" => _text.Get("Confirmation_CaptureSemantics_WindowSurface"),
            "screen_rectangle" => _text.Get("Confirmation_CaptureSemantics_ScreenRectangle"),
            "display_surface" => _text.Get("Confirmation_CaptureSemantics_Display"),
            "region_rectangle" => _text.Get("Confirmation_CaptureSemantics_Region"),
            _ => semantics
        };
    }

    private string BuildPreviewSemanticsLabel()
    {
        string semantics = _item.Presentation.CaptureSemantics;
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
        var presentation = _item.Presentation;
        var summary = presentation.Summary;

        _rootTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 6,
            ColumnCount = 1,
            AutoSize = false
        };
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 0 header
        _rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // 1 main content (info + preview)
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 2 output
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 3 timeout
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 4 warning
        _rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 5 buttons

        var maxTextWidth = Math.Max(200, ClientSize.Width - _rootTable.Padding.Horizontal - 20);

        // Header (outside scrollable area)
        _titleLabel = new Label
        {
            Text = _text.Get("Confirmation_RequestTitle"),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 4)
        };

        _queueLabel = new Label
        {
            Text = _text.Format("Confirmation_QueuePosition", _queuePosition, _totalCount),
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 12)
        };

        _headerPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_queueLabel);
        _rootTable.Controls.Add(_headerPanel, 0, 0);

        // Main content: info + preview, scrollable only when below preferred minimum height.
        _mainContentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, 12)
        };

        _contentTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        _contentTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, InfoColumnProportion * 100f));
        _contentTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, PreviewColumnProportion * 100f));

        // Info panel
        _infoPanel = new Panel
        {
            Dock = DockStyle.Fill,
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
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Source"), DisplayValue(summary.Source));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_SourceType"), DisplayValue(presentation.SourceType));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_CaptureSemantics"),
            GetCaptureSemanticsDisplay());
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_SourceTitle"), DisplayValue(presentation.SourceTitle));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Duration"), DisplayValue(summary.Duration));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Countdown"), GetCountdownDisplay());
        var rawAudio = DisplayValue(summary.Audio);
        var audioSourceKind = DisplayValue(summary.AudioSourceKind);
        string audioLabel;
        string audioDisplayValue;
        if (string.Equals(audioSourceKind, "system-loopback", StringComparison.Ordinal))
        {
            var outputName = DisplayValue(summary.AudioSystemOutputName ?? summary.AudioSystemDefaultOutput);
            var outputSelection = DisplayValue(summary.AudioSystemOutputSelection);
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
            audioDisplayValue = !string.IsNullOrWhiteSpace(summary.AudioDevice)
                ? summary.AudioDevice
                : (rawAudio == "No audio" ? _text.Get("Confirmation_Info_NoAudio") : rawAudio);
        }
        AddInfoRow(_infoTable, row++, audioLabel, audioDisplayValue);
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_NestedRole"), DisplayValue(summary.NestedRole));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_RecordingId"), DisplayValue(presentation.RecordingId));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_ConfirmationId"), DisplayValue(presentation.ConfirmationId));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Timeout"), presentation.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_ExpiresAt"), FormatUtc(presentation.ExpiresAtUtc));

        if (string.Equals(summary.Mode, "screenshot_series", StringComparison.Ordinal))
        {
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Mode"), _text.Get("Confirmation_Value_ScreenshotSeries"));
            var series = summary.Series;
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Interval"),
                (series?.IntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A") + " ms");
            var bound = series?.MaxCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A";
            if (bound == "N/A" || string.IsNullOrWhiteSpace(bound) || bound == "0")
                bound = (series?.MaxDurationSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A") + " s";
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_Bound"), bound);
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_PlannedFrames"),
                series?.PlannedFrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A");
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_OutputKind"), _text.Get("Confirmation_Value_PngSequence"));
            AddInfoRow(_infoTable, row++, _text.Get("Confirmation_Info_OutputDirectory"), DisplayValue(summary.Output));
        }

        _infoPanel.Controls.Add(_infoTable);
        _contentTable.Controls.Add(_infoPanel, 0, 0);

        // Preview panel
        _previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _themeSnapshot.Palette.PreviewBackground,
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
            BackColor = Color.Transparent,
            Visible = false
        };

        _previewPanel.Controls.Add(_previewBox);
        _previewPanel.Controls.Add(_previewFallbackLabel);

        _captureBounds = presentation.CaptureBounds;
        _windowSurfacePreview = string.Equals(
            presentation.CaptureSemantics,
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
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        _previewBoundsLabel.Text = BuildPreviewSemanticsLabel();

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
        _previewContainer = previewContainer;
        _contentTable.Controls.Add(_previewContainer, 1, 0);

        _mainContentPanel.Controls.Add(_contentTable);
        _rootTable.Controls.Add(_mainContentPanel, 0, 1);

        // Keep header/warning labels wrapped/ellipsed after DPI scaling or size changes.
        SizeChanged += (_, _) =>
        {
            var available = Math.Max(200, ClientSize.Width - _rootTable.Padding.Horizontal - 20);
            _titleLabel.MaximumSize = new Size(available, 0);
            _queueLabel.MaximumSize = new Size(available, 0);
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
            _previewFallbackLabel.Text = BuildWindowSurfaceFallback(presentation);
            _previewFallbackLabel.Visible = true;
        }
        else
        {
            // Display/region/window-rectangle plans truthfully show composed
            // desktop pixels using the existing GDI provider.
            var previewMaxSize = ComputePreviewMaxSize();
            var previewBitmap = ConfirmationPreviewBuilder.TryBuildPreview(presentation.CaptureBounds, _previewProvider, previewMaxSize, out var fallbackMessage);
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
        _rootTable.Controls.Add(_outputPanel, 0, 2);

        _timeoutLabel = new Label
        {
            Text = _text.Get("Confirmation_Timeout_Initializing"),
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        _rootTable.Controls.Add(_timeoutLabel, 0, 3);

        // Warning label
        _warningLabel = new Label
        {
            Text = _text.Get("Confirmation_Warning"),
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Margin = new Padding(0, 0, 0, 16)
        };

        // Low-volume warning: if the microphone is enabled and the volume is
        // below 10%, show an explicit warning but do not block recording.
        // Muted devices are rejected before the confirmation form is created.
        if (summary.AudioVolumePercent is int volumePercent && volumePercent >= 0 && volumePercent < 10)
        {
            _warningLabel.Text = _text.Format("Confirmation_Warning_LowVolume", volumePercent);
        }

        _rootTable.Controls.Add(_warningLabel, 0, 4);

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
            Font = buttonFont,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0)
        };
        _rejectButton.Click += (_, _) => Reject();

        _approveButton = new Button
        {
            Text = _text.Get("Confirmation_Button_Approve"),
            Size = MeasureButtonSize(_text.Get("Confirmation_Button_Approve"), buttonFont),
            Font = buttonFont,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0)
        };
        _approveButton.Enabled = !_previewOnly;
        _approveButton.Click += (_, _) => Approve();

        _countdownRing = new ConfirmationCountdownRing
        {
            Margin = new Padding(8, 0, 8, 0),
            TabStop = false,
            Enabled = false
        };

        _buttonPanel.Controls.Add(_rejectButton);
        _buttonPanel.Controls.Add(_approveButton);
        // RightToLeft keeps the ring immediately to the left of Confirm while
        // leaving both command buttons as independent hit targets.
        _buttonPanel.Controls.Add(_countdownRing);
        _rootTable.Controls.Add(_buttonPanel, 0, 5);

        Controls.Add(_rootTable);

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

        _outputTitleLabel = new Label
        {
            Text = _text.Get("Confirmation_Output_Title"),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        };

        var pathFont = new Font("Segoe UI", 9);
        var pathText = GetCurrentOutputPath();
        int pathTextHeight = TextRenderer.MeasureText(pathText, pathFont).Height;

        _outputPathLabel = new Label
        {
            Text = pathText,
            Font = pathFont,
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 8),
            MinimumSize = new Size(0, pathTextHeight)
        };
        _tooltip.SetToolTip(_outputPathLabel, _outputPathLabel.Text);

        _outputActionsPanel = new FlowLayoutPanel
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

        _outputActionsPanel.Controls.Add(_changeOutputButton);
        _outputActionsPanel.Controls.Add(_rememberOutputCheckBox);

        _outputTable.Controls.Add(_outputTitleLabel, 0, 0);
        _outputTable.Controls.Add(_outputPathLabel, 0, 1);
        _outputTable.Controls.Add(_outputActionsPanel, 0, 2);
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

    private static string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? "N/A" : value;

    private static string FormatUtc(DateTime value) => value.ToUniversalTime()
        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private string GetCountdownDisplay()
    {
        var seconds = _item.Presentation.Summary.CountdownSeconds;

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

    private string GetCurrentOutputPath()
    {
        var path = GetCurrentOutputPathFromSummary();
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        // Fallback: show the configured default directory.
        return Path.Combine(_initialOutputDirectory, _text.Get("Confirmation_Output_AutoName"));
    }

    private string? GetCurrentOutputPathFromSummary()
    {
        return _item.Presentation.Summary.Output;
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
        var snapshot = ConfirmationCountdownCalculator.Compute(total, _item.ExpiresAtUtc, now);
        _countdownRing.ApplySnapshot(snapshot);

        if (snapshot.IsExpired)
        {
            _timeoutIsExpired = true;
            _timeoutIsUrgent = false;
            _timeoutLabel.Text = _text.Get("Confirmation_Timeout_Expired");
            _timeoutLabel.ForeColor = _themeSnapshot.Palette.ErrorText;
            _countdownRing.ApplyAccessibilityText(_timeoutLabel.Text);
            _approveButton.Enabled = false;
            _changeOutputButton.Enabled = false;
            ApplyThemeToButtons(_themeSnapshot.Palette);
            StopCountdownTimer();
            return;
        }

        var seconds = snapshot.RemainingSeconds;
        _timeoutIsExpired = false;
        _timeoutIsUrgent = snapshot.IsUrgent;
        _timeoutLabel.Text = seconds <= 5
            ? _text.Format("Confirmation_Timeout_SecondsUrgent", seconds)
            : _text.Format("Confirmation_Timeout_Seconds", seconds);
        _timeoutLabel.ForeColor = _timeoutIsUrgent
            ? _themeSnapshot.Palette.ErrorText
            : _themeSnapshot.Palette.SecondaryText;
        _countdownRing.ApplyAccessibilityText(_timeoutLabel.Text);
    }

    private void Approve()
    {
        if (_previewOnly)
            return;

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
