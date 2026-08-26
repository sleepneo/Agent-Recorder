using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// A small floating stop button for a single active recording.
/// Top-most, borderless, not shown in the taskbar, and does not steal activation.
/// Only the native Button control is clickable; the form is sized exactly to it.
/// </summary>
internal sealed class RecordingStopControlForm : Form
{
    private readonly string _recordingId;
    private readonly IUiTextProvider _text;
    private readonly DisplayDpiInfo? _dpiInfo;
    private readonly Size? _explicitControlSize;
    private readonly CaptureVisibilityMode _captureVisibilityMode;
    private readonly string? _nestedRole;
    private RecordingStopButton _button = null!;
    private ToolTip _tooltip = null!;
    private int _clicked;
    private int _actualWindowDpi;
    private bool _dpiMismatch;
    private readonly IWindowDisplayAffinity _displayAffinity;
    private bool _displayAffinityApplied;
    private Exception? _displayAffinityError;
    private RecordingStopControlVisualPalette _palette;
    private Region? _capsuleRegion;
    private int _capsuleRegionGeneration;

    /// <summary>
    /// Raised once when the user clicks the stop button.
    /// </summary>
    public event Action<string>? StopClicked;

    internal RecordingStopControlBounds PlacementBounds { get; }
    internal string? NestedRoleForTests => _nestedRole;
    internal bool ButtonEnabledForTests => _button?.Enabled ?? false;
    internal string ButtonTextForTests => _button?.Text ?? "";
    internal string TooltipTextForTests => _tooltip?.GetToolTip(_button) ?? "";
    internal Size MeasuredSizeForTests => RecordingStopControlLayout.MeasurePreferredSize(
        _text,
        _button?.Font ?? new Font("Segoe UI", 8, FontStyle.Bold),
        _nestedRole);
    internal Rectangle ButtonBoundsForTests => _button?.Bounds ?? Rectangle.Empty;
    internal int ButtonPaintCountForTests => _button?.PaintCountForTests ?? 0;
    internal int StoppingPaintCountForTests => _button?.StoppingPaintCountForTests ?? 0;
    internal int PlannedDpiForTests => _dpiInfo != null ? (int)Math.Round(_dpiInfo.Scale * 96) : 0;
    internal int ActualWindowDpiForTests => _actualWindowDpi;
    internal bool DpiMismatchForTests => _dpiMismatch;
    internal bool DisplayAffinityAppliedForTests => _displayAffinityApplied;
    internal Exception? DisplayAffinityErrorForTests => _displayAffinityError;
    internal CaptureVisibilityMode CaptureVisibilityModeForTests => _captureVisibilityMode;
    internal RecordingStopControlVisualState VisualStateForTests => _button?.VisualStateForTests ?? RecordingStopControlVisualState.Disabled;
    internal RecordingStopControlVisualPalette PaletteForTests => _palette;
    internal int CapsuleCornerRadiusForTests => _button?.CapsuleCornerRadiusForTests ?? 0;
    internal Region? CapsuleRegionForTests => _capsuleRegion;
    internal Region? ButtonRegionForTests => _button?.CapsuleRegionForTests;
    internal int CapsuleRegionGenerationForTests => _capsuleRegionGeneration;
    internal int ButtonRegionGenerationForTests => _button?.CapsuleRegionGenerationForTests ?? 0;
    internal bool CapsuleRegionContainsForTests(Point point)
        => _capsuleRegion?.IsVisible(point) == true;
    internal bool ButtonRegionContainsForTests(Point point)
        => _button?.CapsuleRegionContainsForTests(point) == true;

    public RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
        : this(recordingId, bounds, CaptureVisibilityMode.ExcludeFromCapture, null, textProvider, displayAffinity)
    {
    }

    internal RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        CaptureVisibilityMode captureVisibilityMode,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
        : this(recordingId, bounds, captureVisibilityMode, null, textProvider, displayAffinity)
    {
    }

    internal RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        CaptureVisibilityMode captureVisibilityMode,
        string? nestedRole,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
    {
        _recordingId = recordingId;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        PlacementBounds = bounds;
        _captureVisibilityMode = captureVisibilityMode;
        _nestedRole = nestedRole;
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;
        InitializeComponent();
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// Production constructor used by <see cref="RecordingIndicatorManager"/>. The caller has
    /// already measured the button at the target monitor DPI, so the form uses the supplied
    /// <paramref name="controlSize"/> and disables AutoScale to avoid a second scaling pass.
    /// </summary>
    internal RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        Size controlSize,
        DisplayDpiInfo dpiInfo,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
        : this(recordingId, bounds, controlSize, dpiInfo, CaptureVisibilityMode.ExcludeFromCapture, null, textProvider, displayAffinity)
    {
    }

    internal RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        Size controlSize,
        DisplayDpiInfo dpiInfo,
        CaptureVisibilityMode captureVisibilityMode,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
        : this(recordingId, bounds, controlSize, dpiInfo, captureVisibilityMode, null, textProvider, displayAffinity)
    {
    }

    internal RecordingStopControlForm(
        string recordingId,
        RecordingStopControlBounds bounds,
        Size controlSize,
        DisplayDpiInfo dpiInfo,
        CaptureVisibilityMode captureVisibilityMode,
        string? nestedRole,
        IUiTextProvider? textProvider = null,
        IWindowDisplayAffinity? displayAffinity = null)
    {
        _recordingId = recordingId;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        _explicitControlSize = controlSize;
        _dpiInfo = dpiInfo;
        PlacementBounds = bounds;
        _captureVisibilityMode = captureVisibilityMode;
        _nestedRole = nestedRole;
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;
        InitializeComponent();
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void InitializeComponent()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "";
        AutoScaleMode = _explicitControlSize.HasValue ? AutoScaleMode.None : AutoScaleMode.Dpi;

        var font = new Font("Segoe UI", 8, FontStyle.Bold);
        var measuredSize = _explicitControlSize
            ?? RecordingStopControlLayout.MeasurePreferredSize(_text, font, _nestedRole);

        _palette = RecordingStatusVisualModel.StopControlPalette(SystemInformation.HighContrast);
        _button = new RecordingStopButton(_palette)
        {
            Text = GetButtonText(stopping: false),
            FlatStyle = FlatStyle.Flat,
            BackColor = _palette.Normal,
            ForeColor = _palette.Foreground,
            Font = font,
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Padding = RecordingStopControlLayout.ButtonPadding,
            TabStop = false,
            AccessibleName = GetButtonText(stopping: false),
            AccessibleRole = AccessibleRole.PushButton
        };
        _button.FlatAppearance.BorderSize = 0;
        _button.Click += OnButtonClick;
        Controls.Add(_button);

        _tooltip = new ToolTip();
        _tooltip.SetToolTip(_button, GetTooltipText());

        ClientSize = measuredSize;
        RebuildCapsuleRegion();
    }

    private void RebuildCapsuleRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || IsDisposed)
            return;

        var next = RecordingStatusVisualModel.CreateCapsuleRegion(ClientSize);
        var previous = _capsuleRegion;
        _capsuleRegion = next;
        Region = next;
        previous?.Dispose();
        _capsuleRegionGeneration++;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        RebuildCapsuleRegion();
    }

    private string GetButtonText(bool stopping)
        => RecordingStatusVisualModel.StopButtonText(_text, _nestedRole, stopping);

    private string GetTooltipText()
        => RecordingStatusVisualModel.StopTooltip(_text, _nestedRole);

    private void OnButtonClick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _clicked, 1) == 1)
            return;

        _button.SetStopping(true);
        _button.Enabled = false;
        _button.Text = GetButtonText(stopping: true);
        _button.AccessibleName = _button.Text;
        _button.Cursor = Cursors.Default;
        _button.Invalidate();
        _button.Update(); // synchronously paint at least one frame of the stopping state

        StopClicked?.Invoke(_recordingId);
    }

    /// <summary>
    /// Resets the button to its initial clickable state after a stop failure, allowing the user to retry.
    /// Safe to call multiple times and safe if the form has been closed.
    /// </summary>
    internal void ResetForRetry()
    {
        if (IsDisposed)
            return;

        Interlocked.Exchange(ref _clicked, 0);
        if (_button != null && !_button.IsDisposed)
        {
            _button.SetStopping(false);
            _button.Text = GetButtonText(stopping: false);
            _button.AccessibleName = _button.Text;
            _button.Cursor = Cursors.Hand;
            _button.Enabled = true;
            _button.Invalidate();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        _palette = RecordingStatusVisualModel.StopControlPalette(SystemInformation.HighContrast);
        _button?.ApplyPalette(_palette);
        _button?.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            _actualWindowDpi = Native.GetDpiForWindow(Handle);
        }
        catch
        {
            _actualWindowDpi = 0;
        }

        if (_dpiInfo != null && _actualWindowDpi > 0)
        {
            int plannedDpi = (int)Math.Round(_dpiInfo.Scale * 96);
            _dpiMismatch = _actualWindowDpi != plannedDpi;
        }

        ApplyDisplayAffinity(Handle);
    }

    /// <summary>
    /// Applies the display-affinity setting and records the diagnostic outcome.
    /// Internal for unit-testing only; production code calls this from <see cref="OnHandleCreated"/>.
    /// </summary>
    internal void ApplyDisplayAffinity(IntPtr hWnd)
    {
        // Reset per-handle state so a recreated handle does not keep a stale error
        // from a previous attempt.
        _displayAffinityApplied = false;
        _displayAffinityError = null;

        if (_captureVisibilityMode == CaptureVisibilityMode.ParentVisible)
        {
            // Parent-visible stop controls must be capturable by the parent outer recording.
            return;
        }

        try
        {
            _displayAffinityApplied = _displayAffinity.SetExcludeFromCapture(hWnd);
        }
        catch (Exception ex)
        {
            // Display-affinity is a best-effort optimization. Failure must not break
            // the stop button, stop recording, or disturb the UI message loop.
            _displayAffinityError = ex;
            _displayAffinityApplied = false;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x8000000;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.Style &= ~(0x00C00000 | 0x00040000 | 0x00010000); // WS_CAPTION, WS_THICKFRAME, WS_SYSMENU
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tooltip?.Dispose();
            _button?.Dispose();
            var region = _capsuleRegion;
            _capsuleRegion = null;
            Region = null;
            region?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Closes the stop control without side effects. Safe to call multiple times.
    /// </summary>
    internal void CloseWithoutResult()
    {
        try { Close(); } catch { }
    }

    private sealed class RecordingStopButton : Button
    {
        private RecordingStopControlVisualPalette _palette;
        private bool _hover;
        private bool _pressed;
        private bool _stopping;

        public RecordingStopButton(RecordingStopControlVisualPalette palette)
        {
            _palette = palette;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        internal bool IsStoppingForTests => _stopping;
        internal int PaintCountForTests { get; private set; }
        internal int StoppingPaintCountForTests { get; private set; }
        internal int CapsuleCornerRadiusForTests => RecordingStatusVisualModel.CapsuleCornerRadius(Size);
        internal RecordingStopControlVisualState VisualStateForTests
            => _stopping
                ? RecordingStopControlVisualState.Stopping
                : !Enabled
                    ? RecordingStopControlVisualState.Disabled
                    : _pressed
                        ? RecordingStopControlVisualState.Pressed
                        : _hover
                            ? RecordingStopControlVisualState.Hover
                            : RecordingStopControlVisualState.Normal;

        internal void SetStopping(bool stopping)
        {
            _stopping = stopping;
            Invalidate();
        }

        internal void ApplyPalette(RecordingStopControlVisualPalette palette)
        {
            _palette = palette;
            BackColor = palette.Normal;
            ForeColor = palette.Foreground;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
                _pressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The Button remains a native accessible/clickable control. Only its
            // paint is customized so the visible surface is a compact capsule.
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            e.Graphics.Clear(GetBackgroundColor());

            using var path = RecordingStatusVisualModel.CreateCapsulePath(ClientSize);
            using var brush = new SolidBrush(GetBackgroundColor());
            using var pen = new Pen(_palette.CapsuleBorder, 1f);
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);

            var textColor = _stopping || !Enabled ? _palette.DisabledForeground : _palette.Foreground;
            TextRenderer.DrawText(
                e.Graphics,
                Text ?? "",
                Font,
                ClientRectangle,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            // Keep the existing synchronous paint diagnostic without calling
            // Button.OnPaint, which would draw the standard rectangular button
            // over the transparent, rounded owner-drawn surface.
            PaintCountForTests++;
            if (_stopping)
                StoppingPaintCountForTests++;
        }

        private Color GetBackgroundColor()
            => RecordingStatusVisualModel.StopControlBackground(_palette, VisualStateForTests);

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RebuildCapsuleRegion();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RebuildCapsuleRegion();
        }

        private Region? _capsuleRegion;
        private int _capsuleRegionGeneration;

        internal Region? CapsuleRegionForTests => _capsuleRegion;
        internal int CapsuleRegionGenerationForTests => _capsuleRegionGeneration;
        internal bool CapsuleRegionContainsForTests(Point point)
            => _capsuleRegion?.IsVisible(point) == true;

        private void RebuildCapsuleRegion()
        {
            if (Width <= 0 || Height <= 0 || IsDisposed)
                return;

            var next = RecordingStatusVisualModel.CreateCapsuleRegion(ClientSize);
            var previous = _capsuleRegion;
            _capsuleRegion = next;
            Region = next;
            previous?.Dispose();
            _capsuleRegionGeneration++;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var region = _capsuleRegion;
                _capsuleRegion = null;
                Region = null;
                region?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
