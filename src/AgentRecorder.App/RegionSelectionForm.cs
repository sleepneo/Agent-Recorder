using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using static AgentRecorder.Windows.SystemQuery;

namespace AgentRecorder.App;

/// <summary>
/// Full-screen region selection form with drag-to-create, move, resize handles,
/// precise coordinate/size inputs, common presets, and multi-monitor hints.
/// Uses Windows virtual screen coordinates (SystemInformation.VirtualScreen) to support
/// multi-monitor setups including negative coordinate displays.
/// Returns DialogResult.OK with virtual screen Bounds set, or DialogResult.Cancel.
/// </summary>
public sealed class RegionSelectionForm : Form
{
    private enum DragMode
    {
        None,
        Create,
        Move,
        ResizeNW, ResizeN, ResizeNE,
        ResizeE, ResizeW,
        ResizeSW, ResizeS, ResizeSE
    }

    private Rectangle _selection = Rectangle.Empty;
    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private Rectangle _dragOrig;
    private readonly Label _infoLabel;
    private readonly Label _coordsLabel;
    private readonly Label _displayLabel;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;
    private readonly Panel _controlPanel;
    private readonly NumericUpDown _inputX;
    private readonly NumericUpDown _inputY;
    private readonly NumericUpDown _inputW;
    private readonly NumericUpDown _inputH;
    private readonly Button _preset720;
    private readonly Button _preset900;
    private readonly Button _preset1080;
    private readonly Button _presetFit16x9;
    private bool _updatingInputs;
    private List<DisplayInfo>? _displays;

    private const int HandleSize = 10;
    private const int MinSize = 32;
    private const int SnapThreshold = 10;
    private const int ClickPickTolerance = 4;
    private Rectangle? _initialVirtualBounds;
    private readonly IWindowActivator _windowActivator;
    private int _foregroundAttempts;
    private const int ForegroundVerifyDelayMs = 150;
    private const int MaxForegroundAttempts = 2;
    private System.Windows.Forms.Timer? _foregroundVerifyTimer;
    private readonly IUiTextProvider _text;

    /// <summary>
    /// When true (default), OnShown schedules a single delayed foreground verification.
    /// Tests can set this to false to avoid real timer waits and then call
    /// <see cref="RunForegroundVerificationForTest"/> to simulate the delayed tick.
    /// </summary>
    internal bool EnableDelayedForegroundVerification { get; init; } = true;

    internal sealed record WindowPickCandidate(WindowInfo Window, Rectangle ClientBounds);

    /// <summary>
    /// Audit event raised by the form to report lifecycle and foregrounding stages.
    /// TrayContext subscribes to this event and forwards it to the audit logger.
    /// </summary>
    public sealed class RegionSelectionAuditEventArgs : EventArgs
    {
        public string EventName { get; init; } = "";
        public object Payload { get; init; } = new();
    }

    internal event EventHandler<RegionSelectionAuditEventArgs>? AuditEvent;

    private List<Rectangle> _snapTargets = new();
    private List<WindowPickCandidate> _windowCandidates = new();
    private Rectangle? _hoverWindowClientBounds;
    private string? _hoverWindowTitle;
    private Point _mouseDownPoint;
    private DragMode _mouseDownHitTest = DragMode.None;
    private Rectangle? _mouseDownWindowClientBounds;
    private bool _mouseMovedBeyondClickTolerance;
    private bool _isLeftMouseDownForSelection;

    /// <summary>
    /// Selected bounds in virtual screen coordinates.
    /// </summary>
    public Rectangle SelectedBounds { get; private set; }

    /// <summary>
    /// Display ID where the selection is located.
    /// </summary>
    public string DisplayId { get; private set; } = "";

    /// <summary>
    /// Coordinate space of the returned bounds.
    /// </summary>
    public string CoordinateSpace { get; private set; } = "virtual_screen";

    public RegionSelectionForm(Rectangle? initialVirtualBounds = null, IWindowActivator? windowActivator = null,
        Action<RegionSelectionAuditEventArgs>? onAuditEvent = null, IUiTextProvider? textProvider = null)
    {
        _initialVirtualBounds = initialVirtualBounds;
        _windowActivator = windowActivator ?? DefaultWindowActivator.Instance;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        if (onAuditEvent != null)
            AuditEvent += (_, e) => onAuditEvent(e);

        var buttonFont = new Font("Segoe UI", 11, FontStyle.Bold);
        var confirmSize = MeasureButtonSize(_text.Get("RegionSelection_Button_Confirm"), buttonFont);
        var cancelSize = MeasureButtonSize(_text.Get("RegionSelection_Button_Cancel"), buttonFont);

        // Initialize button fields FIRST (null guards) before any property that could trigger OnResize
        _confirmButton = new Button
        {
            Text = _text.Get("RegionSelection_Button_Confirm"),
            Size = confirmSize,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(0, 150, 0),
            FlatStyle = FlatStyle.Flat,
            Font = buttonFont,
            Enabled = false,
            Cursor = Cursors.Hand,
            Visible = true
        };
        _confirmButton.FlatAppearance.BorderSize = 1;
        _confirmButton.FlatAppearance.BorderColor = Color.White;
        _confirmButton.Click += (_, _) => ConfirmSelection();

        _cancelButton = new Button
        {
            Text = _text.Get("RegionSelection_Button_Cancel"),
            Size = cancelSize,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(180, 0, 0),
            FlatStyle = FlatStyle.Flat,
            Font = buttonFont,
            Cursor = Cursors.Hand,
            Visible = true
        };
        _cancelButton.FlatAppearance.BorderSize = 1;
        _cancelButton.FlatAppearance.BorderColor = Color.White;
        _cancelButton.Click += (_, _) => CancelSelection();

        // Info label
        _infoLabel = new Label
        {
            Text = _text.Get("RegionSelection_Info_Default"),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(12),
            AutoSize = true,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Location = new Point(20, 20),
            MaximumSize = new Size(800, 0)
        };

        // Coordinates label (virtual screen coordinates)
        _coordsLabel = new Label
        {
            Text = _text.Format("RegionSelection_Coords_VirtualScreen", 0, 0, 0, 0),
            ForeColor = Color.Cyan,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(12),
            AutoSize = true,
            Font = new Font("Consolas", 11),
            Location = new Point(20, 65)
        };

        _displayLabel = new Label
        {
            Text = _text.Get("RegionSelection_Display_Unknown"),
            ForeColor = Color.Yellow,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(12),
            AutoSize = true,
            Font = new Font("Consolas", 11),
            Location = new Point(20, 110)
        };

        // Control panel with coordinate inputs and presets
        _controlPanel = new Panel
        {
            BackColor = Color.FromArgb(180, 0, 0, 0),
            BorderStyle = BorderStyle.FixedSingle,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Visible = true
        };

        var labelX = new Label { Text = _text.Get("RegionSelection_Input_X"), ForeColor = Color.White, AutoSize = true };
        var labelY = new Label { Text = _text.Get("RegionSelection_Input_Y"), ForeColor = Color.White, AutoSize = true };
        var labelW = new Label { Text = _text.Get("RegionSelection_Input_W"), ForeColor = Color.White, AutoSize = true };
        var labelH = new Label { Text = _text.Get("RegionSelection_Input_H"), ForeColor = Color.White, AutoSize = true };

        _inputX = CreateInput(-99999, 99999);
        _inputY = CreateInput(-99999, 99999);
        _inputW = CreateInput(1, 99999);
        _inputH = CreateInput(1, 99999);

        _inputX.ValueChanged += (_, _) => OnInputValueChanged();
        _inputY.ValueChanged += (_, _) => OnInputValueChanged();
        _inputW.ValueChanged += (_, _) => OnInputValueChanged();
        _inputH.ValueChanged += (_, _) => OnInputValueChanged();

        _preset720 = CreatePresetButton(_text.Get("RegionSelection_Preset_1280x720"));
        _preset900 = CreatePresetButton(_text.Get("RegionSelection_Preset_1600x900"));
        _preset1080 = CreatePresetButton(_text.Get("RegionSelection_Preset_1920x1080"));
        _presetFit16x9 = CreatePresetButton(_text.Get("RegionSelection_Preset_Fit16x9"));

        _preset720.Click += (_, _) => ApplyPreset(new Size(1280, 720));
        _preset900.Click += (_, _) => ApplyPreset(new Size(1600, 900));
        _preset1080.Click += (_, _) => ApplyPreset(new Size(1920, 1080));
        _presetFit16x9.Click += (_, _) => ApplyFit16x9();

        // Layout: row 1 labels, row 2 inputs, row 3 presets
        int pad = 6;
        int col = 0;
        foreach (var lbl in new[] { labelX, labelY, labelW, labelH })
        {
            lbl.Location = new Point(col * 70 + pad, pad);
            _controlPanel.Controls.Add(lbl);
            col++;
        }

        col = 0;
        foreach (var input in new[] { _inputX, _inputY, _inputW, _inputH })
        {
            input.Size = new Size(62, 24);
            input.Location = new Point(col * 70 + pad, pad + 20);
            _controlPanel.Controls.Add(input);
            col++;
        }

        int presetY = pad + 50;
        col = 0;
        foreach (var btn in new[] { _preset720, _preset900, _preset1080, _presetFit16x9 })
        {
            btn.Location = new Point(col * 86 + pad, presetY);
            _controlPanel.Controls.Add(btn);
            col++;
        }

        _controlPanel.Size = new Size(4 * 86 + pad * 2, presetY + 32 + pad);

        // Add controls BEFORE setting bounds to avoid early OnResize calls
        Controls.Add(_infoLabel);
        Controls.Add(_coordsLabel);
        Controls.Add(_displayLabel);
        Controls.Add(_controlPanel);
        Controls.Add(_confirmButton);
        Controls.Add(_cancelButton);

        AcceptButton = _confirmButton;
        CancelButton = _cancelButton;

        // Now set form properties
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.3;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true; // Ensure key events are captured even when child controls have focus

        // Use virtual screen bounds to cover all monitors including negative coordinates
        var virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;

        // Cache displays for drawing boundaries and labels.
        try { _displays = SystemQuery.EnumDisplays().ToList(); }
        catch { _displays = null; }

        // Refresh window candidates and snap targets (displays + visible windows) for magnetic snapping.
        RefreshCandidatesAndTargets();

        // Apply initial selection immediately while still on the creating thread.
        // OnShown is message-pump dependent and may not run before tests inspect state.
        ApplyInitialSelection();

        RaiseAuditEvent("region_selection.ui_created", CreateLifecyclePayload("handle_created"));
    }

    private static NumericUpDown CreateInput(int min, int max)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = 1,
            TextAlign = HorizontalAlignment.Right,
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 60, 60)
        };
    }

    private static Button CreatePresetButton(string text)
    {
        var font = new Font("Segoe UI", 8);
        var size = MeasureButtonSize(text, font, horizontalPadding: 10, verticalPadding: 6, minHeight: 28);
        var btn = new Button
        {
            Text = text,
            Size = size,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(80, 80, 80),
            FlatStyle = FlatStyle.Flat,
            Font = font,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = Color.White;
        btn.FlatAppearance.BorderSize = 1;
        return btn;
    }

    private static Size MeasureButtonSize(string text, Font font, int horizontalPadding = 32, int verticalPadding = 16, int minHeight = 44)
    {
        var measured = TextRenderer.MeasureText(text, font);
        int width = measured.Width + horizontalPadding;
        int height = Math.Max(minHeight, measured.Height + verticalPadding);
        return new Size(width, height);
    }

    private void UpdateButtonPositions()
    {
        // Place buttons at the TOP center of the form so they are always visible
        // regardless of screen size or DPI scaling. Compute layout from measured
        // sizes; if the two buttons do not fit horizontally, stack them vertically.
        const int buttonSpacing = 12;
        const int topMargin = 160;
        const int sideMargin = 20;

        int totalWidth = _confirmButton.Width + buttonSpacing + _cancelButton.Width;
        int centerX = ClientSize.Width / 2;

        if (totalWidth + sideMargin * 2 <= ClientSize.Width)
        {
            int startX = centerX - totalWidth / 2;
            _confirmButton.Location = new Point(startX, topMargin);
            _cancelButton.Location = new Point(startX + _confirmButton.Width + buttonSpacing, topMargin);
        }
        else
        {
            // Narrow client area: stack buttons vertically and center them.
            int startX = Math.Max(sideMargin, centerX - _confirmButton.Width / 2);
            _confirmButton.Location = new Point(startX, topMargin);
            _cancelButton.Location = new Point(startX, topMargin + _confirmButton.Height + buttonSpacing);
        }

        _confirmButton.BringToFront();
        _cancelButton.BringToFront();

        // Place control panel at top-right, ensuring it stays inside the client area
        // and does not overlap the centered buttons when space is tight.
        int controlPanelX = ClientSize.Width - _controlPanel.Width - sideMargin;
        int controlPanelY = sideMargin;
        if (controlPanelX < _confirmButton.Right + sideMargin &&
            controlPanelY < Math.Max(_confirmButton.Bottom, _cancelButton.Bottom))
        {
            // Shift below the buttons if it would horizontally overlap.
            controlPanelY = Math.Max(_confirmButton.Bottom, _cancelButton.Bottom) + sideMargin;
        }

        _controlPanel.Location = new Point(Math.Max(sideMargin, controlPanelX), controlPanelY);
        _controlPanel.BringToFront();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateButtonPositions();
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        RaiseAuditEvent("region_selection.ui_shown", CreateLifecyclePayload("shown"));

        // Ensure window is foreground and has focus
        BringToFront();
        Activate();
        Focus();
        UpdateButtonPositions();

        // Apply initial selection after the form bounds are finalized.
        ApplyInitialSelection();

        // Perform explicit Win32 top-most placement and foregrounding.
        // An immediate attempt is followed by a single delayed verification tick
        // to handle races where another window briefly steals the z-order right
        // after the form becomes visible.
        _foregroundAttempts = 0;
        EnsureTopMostForeground();

        if (EnableDelayedForegroundVerification && IsHandleCreated && !IsDisposed)
        {
            ScheduleForegroundVerification();
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
            RecordForegroundError("get_foreground_before", ex, ref _foregroundError, ref _foregroundErrorStage);
        }

        RaiseAuditEvent("region_selection.foreground_attempt", new
        {
            attempt = _foregroundAttempts,
            max_attempts = MaxForegroundAttempts,
            form_handle = hWnd.ToInt64(),
            visible = Visible,
            topmost = TopMost,
            bounds = new { x = Bounds.X, y = Bounds.Y, w = Bounds.Width, h = Bounds.Height },
            foreground_before = beforeForeground.ToInt64()
        });

        bool setTopMostSuccess = false;
        bool setForegroundSuccess = false;
        bool bringToTopSuccess = false;

        try
        {
            setTopMostSuccess = _windowActivator.SetTopMost(hWnd);
        }
        catch (Exception ex)
        {
            RecordForegroundError("set_topmost", ex, ref _foregroundError, ref _foregroundErrorStage);
        }

        try
        {
            setForegroundSuccess = _windowActivator.SetForeground(hWnd);
        }
        catch (Exception ex)
        {
            RecordForegroundError("set_foreground", ex, ref _foregroundError, ref _foregroundErrorStage);
        }

        if (!setForegroundSuccess)
        {
            try
            {
                bringToTopSuccess = _windowActivator.BringToTop(hWnd);
            }
            catch (Exception ex)
            {
                RecordForegroundError("bring_to_top", ex, ref _foregroundError, ref _foregroundErrorStage);
            }
        }

        IntPtr afterForeground = IntPtr.Zero;
        try
        {
            afterForeground = _windowActivator.GetForegroundWindow();
        }
        catch (Exception ex)
        {
            RecordForegroundError("get_foreground_after", ex, ref _foregroundError, ref _foregroundErrorStage);
        }

        bool becameForeground = afterForeground == hWnd;

        RaiseAuditEvent("region_selection.foreground_result", new
        {
            attempt = _foregroundAttempts,
            form_handle = hWnd.ToInt64(),
            foreground_after = afterForeground.ToInt64(),
            became_foreground = becameForeground,
            set_window_pos_success = setTopMostSuccess,
            set_foreground_window_success = setForegroundSuccess,
            bring_window_to_top_success = bringToTopSuccess,
            error = _foregroundError,
            error_stage = _foregroundErrorStage
        });

        // Reset the transient error state so the next attempt starts clean.
        _foregroundError = null;
        _foregroundErrorStage = null;
    }

    private string? _foregroundError;
    private string? _foregroundErrorStage;

    private static void RecordForegroundError(string stage, Exception ex, ref string? error, ref string? errorStage)
    {
        error ??= ex.Message;
        errorStage ??= stage;
    }

    private void RaiseAuditEvent(string eventName, object payload)
    {
        AuditEvent?.Invoke(this, new RegionSelectionAuditEventArgs { EventName = eventName, Payload = payload });
    }

    private object CreateLifecyclePayload(string stage)
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        return new
        {
            stage,
            form_handle = IsHandleCreated ? Handle.ToInt64() : 0,
            visible = Visible,
            topmost = TopMost,
            bounds = new { x = Bounds.X, y = Bounds.Y, w = Bounds.Width, h = Bounds.Height },
            virtual_screen = new
            {
                x = virtualScreen.X,
                y = virtualScreen.Y,
                w = virtualScreen.Width,
                h = virtualScreen.Height
            }
        };
    }

    /// <summary>
    /// Pre-sets the selection rectangle using virtual screen coordinates.
    /// The form will translate it to client coordinates when shown.
    /// </summary>
    public void SetInitialVirtualBounds(Rectangle virtualBounds)
    {
        _initialVirtualBounds = virtualBounds;
    }

    private void ApplyInitialSelection()
    {
        if (!_initialVirtualBounds.HasValue)
            return;

        var clamped = RegionSelectionGeometry.ClampInitialSelection(Bounds, _initialVirtualBounds.Value, MinSize);
        if (!clamped.HasValue)
            return;

        _selection = clamped.Value;
        _confirmButton.Enabled = true;
        UpdateInfoLabel();
        Invalidate();
    }

    private void OnInputValueChanged()
    {
        if (_updatingInputs)
            return;

        int x = (int)_inputX.Value;
        int y = (int)_inputY.Value;
        int w = (int)_inputW.Value;
        int h = (int)_inputH.Value;

        var targetClientBounds = new Rectangle(x - Bounds.X, y - Bounds.Y, w, h);
        var clamped = RegionSelectionGeometry.ClampSizedSelectionToClientRectangle(Bounds, targetClientBounds, MinSize);

        if (clamped.HasValue)
        {
            _selection = clamped.Value;
            _confirmButton.Enabled = true;
            UpdateInfoLabel();
            Invalidate();
        }
    }

    private void ApplyPreset(Size targetSize)
    {
        var centerVirtual = GetPresetCenterVirtual();
        var newBounds = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(Bounds, centerVirtual, targetSize, MinSize);
        if (newBounds.HasValue)
        {
            _selection = newBounds.Value;
            _confirmButton.Enabled = true;
            UpdateInfoLabel();
            Invalidate();
        }
    }

    private void ApplyFit16x9()
    {
        var centerVirtual = GetPresetCenterVirtual();
        var newBounds = RegionSelectionGeometry.FitAspectRatio(Bounds, centerVirtual, 16.0 / 9.0, MinSize);
        if (newBounds.HasValue)
        {
            _selection = newBounds.Value;
            _confirmButton.Enabled = true;
            UpdateInfoLabel();
            Invalidate();
        }
    }

    private Point GetPresetCenterVirtual()
    {
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            return new Point(
                Bounds.X + _selection.X + _selection.Width / 2,
                Bounds.Y + _selection.Y + _selection.Height / 2);
        }

        var primaryCenter = GetPrimaryDisplayCenter();
        return primaryCenter ?? RegionSelectionGeometry.GetVirtualScreenCenter(Bounds);
    }

    private static Point? GetPrimaryDisplayCenter()
    {
        try
        {
            var displays = SystemQuery.EnumDisplays();
            var primary = displays.FirstOrDefault(d => d.is_primary);
            if (primary == null)
                return null;
            var b = primary.bounds;
            return new Point(b.x + b.width / 2, b.y + b.height / 2);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && _confirmButton.Enabled)
        {
            ConfirmSelection();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            CancelSelection();
        }
    }

    private void ConfirmSelection()
    {
        if (_selection.Width < MinSize || _selection.Height < MinSize)
        {
            _infoLabel.Text = _text.Format("RegionSelection_Info_TooSmall", MinSize);
            return;
        }

        // Normalize to even dimensions (x264/yuv420p requirement)
        int normalizedW = _selection.Width;
        int normalizedH = _selection.Height;
        if (normalizedW % 2 != 0) normalizedW--;
        if (normalizedH % 2 != 0) normalizedH--;

        // Ensure still at least MinSize after normalization
        if (normalizedW < MinSize) normalizedW = MinSize;
        if (normalizedH < MinSize) normalizedH = MinSize;

        // Convert from client area coordinates to virtual screen coordinates.
        // _selection is relative to form's client area (0,0 at form's top-left),
        // but we need absolute virtual screen coordinates where primary display
        // may start at (0,0) but secondary displays can have negative X.
        int virtualX = Bounds.X + _selection.X;
        int virtualY = Bounds.Y + _selection.Y;
        SelectedBounds = new Rectangle(virtualX, virtualY, normalizedW, normalizedH);

        // Find which display this selection is on
        DisplayId = FindDisplayForBounds(SelectedBounds);

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Finds the display that contains the center of the given bounds.
    /// Returns the display_id from SystemQuery.EnumDisplays().
    /// </summary>
    private static string FindDisplayForBounds(Rectangle bounds)
    {
        try
        {
            var displays = SystemQuery.EnumDisplays();
            return RegionSelectionGeometry.FindDisplayId(bounds, displays)
                ?? RegionSelectionGeometry.FindDisplayIdByOverlap(bounds, displays)
                ?? "display_1";
        }
        catch
        {
            return "display_1";
        }
    }

    private void CancelSelection()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void RefreshCandidatesAndTargets()
    {
        _windowCandidates = new List<WindowPickCandidate>();
        _snapTargets = new List<Rectangle>();

        List<DisplayInfo> displays = new();
        try
        {
            displays = _displays ?? SystemQuery.EnumDisplays().ToList();
        }
        catch { }

        IEnumerable<WindowInfo> windows = Enumerable.Empty<WindowInfo>();
        try
        {
            windows = SystemQuery.EnumWindows(includeMinimized: false, includeSystem: false);
        }
        catch { }

        try
        {
            foreach (var window in windows)
            {
                var bounds = RegionSelectionGeometry.ComputeWindowPickBounds(Bounds, window, MinSize);
                if (bounds.HasValue)
                    _windowCandidates.Add(new WindowPickCandidate(window, bounds.Value));
            }
        }
        catch
        {
            _windowCandidates = new List<WindowPickCandidate>();
        }

        try
        {
            _snapTargets = RegionSelectionGeometry.GenerateSnapTargets(Bounds, displays, windows, MinSize);
        }
        catch
        {
            _snapTargets = new List<Rectangle>();
        }
    }

    private Rectangle? GetWindowClientBoundsAtPoint(Point clientPoint)
    {
        foreach (var candidate in _windowCandidates)
        {
            if (candidate.ClientBounds.Contains(clientPoint))
                return candidate.ClientBounds;
        }
        return null;
    }

    private void UpdateHoverWindow(Point clientPoint)
    {
        // Existing selection handles take priority over window hover.
        if (GetHitTest(clientPoint) != DragMode.None)
        {
            _hoverWindowClientBounds = null;
            _hoverWindowTitle = null;
            return;
        }

        foreach (var candidate in _windowCandidates)
        {
            if (candidate.ClientBounds.Contains(clientPoint))
            {
                _hoverWindowClientBounds = candidate.ClientBounds;
                _hoverWindowTitle = candidate.Window.title;
                return;
            }
        }

        _hoverWindowClientBounds = null;
        _hoverWindowTitle = null;
    }

    private static SnapEdgeMask GetSnapEdgeMaskForDragMode(DragMode mode, Point current, Point dragStart)
    {
        return mode switch
        {
            DragMode.Move => SnapEdgeMask.All,
            DragMode.ResizeNW => SnapEdgeMask.Left | SnapEdgeMask.Top,
            DragMode.ResizeN => SnapEdgeMask.Top,
            DragMode.ResizeNE => SnapEdgeMask.Right | SnapEdgeMask.Top,
            DragMode.ResizeW => SnapEdgeMask.Left,
            DragMode.ResizeE => SnapEdgeMask.Right,
            DragMode.ResizeSW => SnapEdgeMask.Left | SnapEdgeMask.Bottom,
            DragMode.ResizeS => SnapEdgeMask.Bottom,
            DragMode.ResizeSE => SnapEdgeMask.Right | SnapEdgeMask.Bottom,
            DragMode.Create => GetCreateSnapEdgeMask(current, dragStart),
            _ => SnapEdgeMask.None
        };
    }

    private static SnapEdgeMask GetCreateSnapEdgeMask(Point current, Point dragStart)
    {
        SnapEdgeMask mask = SnapEdgeMask.None;
        if (current.X >= dragStart.X)
            mask |= SnapEdgeMask.Right;
        else
            mask |= SnapEdgeMask.Left;
        if (current.Y >= dragStart.Y)
            mask |= SnapEdgeMask.Bottom;
        else
            mask |= SnapEdgeMask.Top;
        return mask;
    }

    private bool IsAltPressed => (ModifierKeys & Keys.Alt) == Keys.Alt;

    private Rectangle ApplySnappingToSelection(Rectangle selection, DragMode mode, Point current)
    {
        var mask = GetSnapEdgeMaskForDragMode(mode, current, _dragStart);
        if (mask == SnapEdgeMask.None)
            return selection;

        bool preserveSize = mode == DragMode.Move;
        return RegionSelectionGeometry.ApplySnapping(
            selection,
            ClientRectangle,
            _snapTargets,
            SnapThreshold,
            mask,
            preserveSize,
            !IsAltPressed,
            MinSize);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left) return;

        _isLeftMouseDownForSelection = true;
        Capture = true;
        _mouseDownPoint = e.Location;
        _mouseMovedBeyondClickTolerance = false;
        _mouseDownWindowClientBounds = null;
        _mouseDownHitTest = GetHitTest(e.Location);

        if (_mouseDownHitTest == DragMode.None)
        {
            // Defer creating a selection; if the user does not drag beyond the
            // click tolerance we will treat this as a window-pick click.
            _mouseDownWindowClientBounds = GetWindowClientBoundsAtPoint(e.Location);
            _dragMode = DragMode.None;
        }
        else
        {
            _dragMode = _mouseDownHitTest;
            _dragStart = e.Location;
            _dragOrig = _selection;
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragMode == DragMode.None)
        {
            // Update hover and cursor first so the cursor reflects the current
            // state without a one-frame lag. Selection handles take priority.
            UpdateHoverWindow(e.Location);
            var mode = GetHitTest(e.Location);
            Cursor = mode switch
            {
                DragMode.Move => Cursors.SizeAll,
                DragMode.ResizeNW or DragMode.ResizeSE => Cursors.SizeNWSE,
                DragMode.ResizeNE or DragMode.ResizeSW => Cursors.SizeNESW,
                DragMode.ResizeN or DragMode.ResizeS => Cursors.SizeNS,
                DragMode.ResizeE or DragMode.ResizeW => Cursors.SizeWE,
                _ => _hoverWindowClientBounds.HasValue ? Cursors.Hand : Cursors.Cross
            };

            // Only convert a pending click into a drag-to-create when the left
            // button is actually held and the mouse was not pressed on an existing
            // selection or resize handle.
            if (_isLeftMouseDownForSelection &&
                !_mouseMovedBeyondClickTolerance &&
                _mouseDownHitTest == DragMode.None)
            {
                if (Math.Abs(e.X - _mouseDownPoint.X) > ClickPickTolerance ||
                    Math.Abs(e.Y - _mouseDownPoint.Y) > ClickPickTolerance)
                {
                    _mouseMovedBeyondClickTolerance = true;
                    _dragMode = DragMode.Create;
                    _dragStart = _mouseDownPoint;
                    _selection = new Rectangle(_dragStart.X, _dragStart.Y, 0, 0);
                    _confirmButton.Enabled = false;
                    UpdateInfoLabel();
                }
            }

            if (_dragMode == DragMode.None)
            {
                if (_hoverWindowClientBounds.HasValue)
                    Invalidate();
                return;
            }
        }

        // Clear hover highlight while dragging.
        if (_hoverWindowClientBounds.HasValue)
        {
            _hoverWindowClientBounds = null;
            _hoverWindowTitle = null;
            Invalidate();
        }

        Cursor = _dragMode switch
        {
            DragMode.Create => Cursors.Cross,
            DragMode.Move => Cursors.SizeAll,
            DragMode.ResizeNW or DragMode.ResizeSE => Cursors.SizeNWSE,
            DragMode.ResizeNE or DragMode.ResizeSW => Cursors.SizeNESW,
            DragMode.ResizeN or DragMode.ResizeS => Cursors.SizeNS,
            DragMode.ResizeE or DragMode.ResizeW => Cursors.SizeWE,
            _ => Cursor
        };

        if (_dragMode == DragMode.Create)
        {
            int x = Math.Min(_dragStart.X, e.X);
            int y = Math.Min(_dragStart.Y, e.Y);
            int w = Math.Abs(e.X - _dragStart.X);
            int h = Math.Abs(e.Y - _dragStart.Y);
            var raw = new Rectangle(x, y, w, h);
            _selection = ApplySnappingToSelection(raw, DragMode.Create, e.Location);
        }
        else if (_dragMode == DragMode.Move)
        {
            int dx = e.X - _dragStart.X;
            int dy = e.Y - _dragStart.Y;
            int newX = _dragOrig.X + dx;
            int newY = _dragOrig.Y + dy;

            var raw = new Rectangle(newX, newY, _dragOrig.Width, _dragOrig.Height);
            _selection = ApplySnappingToSelection(raw, DragMode.Move, e.Location);
        }
        else
        {
            ResizeSelection(e.Location);
            _selection = ApplySnappingToSelection(_selection, _dragMode, e.Location);
        }

        UpdateInfoLabel();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left)
            return;

        if (_dragMode == DragMode.None &&
            !_mouseMovedBeyondClickTolerance &&
            _mouseDownWindowClientBounds.HasValue)
        {
            // Treat as a window-pick click.
            _selection = _mouseDownWindowClientBounds.Value;
            _confirmButton.Enabled = true;
            UpdateInfoLabel();
            Invalidate();
        }
        else if (_dragMode != DragMode.None)
        {
            if (_selection.Width > 0 && _selection.Height > 0)
            {
                _confirmButton.Enabled = true;
            }
            _dragMode = DragMode.None;
            Invalidate();
        }

        ReleaseMouseState();
    }

    private void ReleaseMouseState()
    {
        _isLeftMouseDownForSelection = false;
        _mouseMovedBeyondClickTolerance = false;
        _mouseDownWindowClientBounds = null;
        _mouseDownHitTest = DragMode.None;
        if (Capture)
            Capture = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ReleaseMouseState();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopForegroundVerificationTimer();
        }

        base.Dispose(disposing);
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

    /// <summary>
    /// Test seam to manually run a foreground verification tick without waiting
    /// for the real timer. Does nothing if the form is disposed or the maximum
    /// number of attempts has already been reached.
    /// </summary>
    internal void RunForegroundVerificationForTest() => EnsureTopMostForeground();

    internal int ForegroundAttemptsForTest => _foregroundAttempts;

    private DragMode GetHitTest(Point p)
    {
        if (_selection.IsEmpty || _selection.Width < MinSize || _selection.Height < MinSize)
            return DragMode.None;

        int hs = HandleSize;

        // Corner handles
        if (IsWithin(p, _selection.Left - hs, _selection.Top - hs, hs * 2, hs * 2))
            return DragMode.ResizeNW;
        if (IsWithin(p, _selection.Right - hs, _selection.Top - hs, hs * 2, hs * 2))
            return DragMode.ResizeNE;
        if (IsWithin(p, _selection.Left - hs, _selection.Bottom - hs, hs * 2, hs * 2))
            return DragMode.ResizeSW;
        if (IsWithin(p, _selection.Right - hs, _selection.Bottom - hs, hs * 2, hs * 2))
            return DragMode.ResizeSE;

        // Edge handles
        if (IsWithin(p, _selection.Left + hs, _selection.Top - hs, _selection.Width - hs * 2, hs * 2))
            return DragMode.ResizeN;
        if (IsWithin(p, _selection.Left + hs, _selection.Bottom - hs, _selection.Width - hs * 2, hs * 2))
            return DragMode.ResizeS;
        if (IsWithin(p, _selection.Left - hs, _selection.Top + hs, hs * 2, _selection.Height - hs * 2))
            return DragMode.ResizeW;
        if (IsWithin(p, _selection.Right - hs, _selection.Top + hs, hs * 2, _selection.Height - hs * 2))
            return DragMode.ResizeE;

        // Interior (move)
        if (p.X >= _selection.Left + hs && p.X <= _selection.Right - hs &&
            p.Y >= _selection.Top + hs && p.Y <= _selection.Bottom - hs)
            return DragMode.Move;

        return DragMode.None;
    }

    private static bool IsWithin(Point p, int x, int y, int w, int h)
    {
        return p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h;
    }

    private void ResizeSelection(Point p)
    {
        int left = _dragOrig.Left;
        int right = _dragOrig.Right;
        int top = _dragOrig.Top;
        int bottom = _dragOrig.Bottom;

        switch (_dragMode)
        {
            case DragMode.ResizeNW: left = p.X; top = p.Y; break;
            case DragMode.ResizeN: top = p.Y; break;
            case DragMode.ResizeNE: right = p.X; top = p.Y; break;
            case DragMode.ResizeW: left = p.X; break;
            case DragMode.ResizeE: right = p.X; break;
            case DragMode.ResizeSW: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeS: bottom = p.Y; break;
            case DragMode.ResizeSE: right = p.X; bottom = p.Y; break;
        }

        int newW = right - left;
        int newH = bottom - top;

        if (newW < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeW or DragMode.ResizeSW)
                left = right - MinSize;
            else
                right = left + MinSize;
        }
        if (newH < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeN or DragMode.ResizeNE)
                top = bottom - MinSize;
            else
                bottom = top + MinSize;
        }

        // Clamp to virtual screen bounds
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Width, right);
        bottom = Math.Min(Height, bottom);

        _selection = new Rectangle(left, top, right - left, bottom - top);
    }

    private void UpdateInfoLabel()
    {
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // Convert client area coordinates to virtual screen coordinates for display
            int virtualX = Bounds.X + _selection.X;
            int virtualY = Bounds.Y + _selection.Y;
            _infoLabel.Text = _text.Format("RegionSelection_Info_Selected", virtualX, virtualY, _selection.Width, _selection.Height);
            _coordsLabel.Text = _text.Format("RegionSelection_Coords_FormBounds", Bounds.X, Bounds.Y, Bounds.Right, Bounds.Bottom);

            UpdateDisplayLabel();

            _updatingInputs = true;
            _inputX.Value = virtualX;
            _inputY.Value = virtualY;
            _inputW.Value = _selection.Width;
            _inputH.Value = _selection.Height;
            _updatingInputs = false;
        }
        else
        {
            _infoLabel.Text = _text.Get("RegionSelection_Info_Default");
            _coordsLabel.Text = _text.Format("RegionSelection_Coords_VirtualScreen", Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
            _displayLabel.Text = _text.Get("RegionSelection_Display_Unknown");

            _updatingInputs = true;
            _inputX.Value = Bounds.X;
            _inputY.Value = Bounds.Y;
            _inputW.Value = MinSize;
            _inputH.Value = MinSize;
            _updatingInputs = false;
        }
    }

    private void UpdateDisplayLabel()
    {
        try
        {
            var virtualBounds = new Rectangle(
                Bounds.X + _selection.X,
                Bounds.Y + _selection.Y,
                _selection.Width,
                _selection.Height);
            var displays = SystemQuery.EnumDisplays();
            var displayId = RegionSelectionGeometry.FindDisplayId(virtualBounds, displays)
                         ?? RegionSelectionGeometry.FindDisplayIdByOverlap(virtualBounds, displays)
                         ?? "unknown";
            _displayLabel.Text = _text.Format("RegionSelection_Display", displayId);
        }
        catch
        {
            _displayLabel.Text = _text.Format("RegionSelection_Display_UnknownWithVirtual", Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Draw multi-monitor boundaries and labels
        DrawDisplayBoundaries(g);

        // Draw window hover highlight (behind the selection so it never obscures it).
        if (_hoverWindowClientBounds.HasValue)
        {
            using var hoverPen = new Pen(Color.FromArgb(180, 0, 255, 255), 2)
            {
                DashStyle = DashStyle.Dash
            };
            g.DrawRectangle(hoverPen, _hoverWindowClientBounds.Value);
        }

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // Draw dark overlay outside selection
            using var region = new Region(ClientRectangle);
            region.Exclude(_selection);
            using var brush = new SolidBrush(Color.FromArgb(100, 0, 0, 0));
            g.FillRegion(brush, region);

            // Draw selection border
            using var pen = new Pen(Color.Yellow, 2);
            g.DrawRectangle(pen, _selection);

            // Draw resize handles
            using var handleBrush = new SolidBrush(Color.Yellow);
            int hs = HandleSize;
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left + _selection.Width / 2 - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left + _selection.Width / 2 - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Top + _selection.Height / 2 - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Top + _selection.Height / 2 - hs / 2, hs, hs);
        }
    }

    private void DrawDisplayBoundaries(Graphics g)
    {
        List<DisplayInfo>? displays = null;
        try
        {
            displays = _displays ?? SystemQuery.EnumDisplays().ToList();
        }
        catch
        {
            // Fall back to virtual screen bounds if enumeration fails.
        }

        if (displays == null || displays.Count == 0)
        {
            using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1)
            {
                DashStyle = DashStyle.Dash
            };
            var clientVirtualBounds = new Rectangle(-Bounds.X, -Bounds.Y, Bounds.Width, Bounds.Height);
            g.DrawRectangle(pen, clientVirtualBounds);
            return;
        }

        using var boundaryPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1)
        {
            DashStyle = DashStyle.Dash
        };
        using var labelBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        using var labelFont = new Font("Consolas", 10, FontStyle.Bold);

        foreach (var display in displays)
        {
            var b = display.bounds;
            // Convert virtual screen bounds to client coordinates.
            var clientBounds = new Rectangle(
                b.x - Bounds.X,
                b.y - Bounds.Y,
                b.width,
                b.height);

            g.DrawRectangle(boundaryPen, clientBounds);

            // Draw label near the top-left corner, inside the display if possible.
            var label = display.id;
            var labelSize = g.MeasureString(label, labelFont);
            int labelX = clientBounds.Left + 4;
            int labelY = clientBounds.Top + 4;
            g.DrawString(label, labelFont, labelBrush, labelX, labelY);
        }
    }

    /// <summary>
    /// Show region selection dialog on a new STA thread.
    /// Returns bounds in virtual screen coordinates.
    /// </summary>
    public static (DialogResult Result, Rectangle Bounds, string DisplayId) ShowSelection(Rectangle? initialVirtualBounds = null)
    {
        Rectangle bounds = Rectangle.Empty;
        string displayId = "";
        DialogResult result = DialogResult.Cancel;

        var thread = new Thread(() =>
        {
            using var form = new RegionSelectionForm(initialVirtualBounds);
            result = form.ShowDialog();
            bounds = form.SelectedBounds;
            displayId = form.DisplayId;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return (result, bounds, displayId);
    }

    // -------------------------------------------------------------------------
    // Internal test seams
    // -------------------------------------------------------------------------

    internal Rectangle CurrentSelection => _selection;
    internal Rectangle? HoverWindowClientBounds => _hoverWindowClientBounds;
    internal IReadOnlyList<Rectangle> SnapTargets => _snapTargets;
    internal IReadOnlyList<WindowPickCandidate> WindowCandidates => _windowCandidates;
    internal IReadOnlyList<Rectangle> WindowCandidateBoundsForTests => _windowCandidates.Select(c => c.ClientBounds).ToList();
    internal string CurrentDragModeForTests => _dragMode.ToString();

    internal void RefreshCandidatesAndTargetsForTest() => RefreshCandidatesAndTargets();

    internal void ApplyWindowPickForTest(Rectangle clientBounds)
    {
        _selection = clientBounds;
        _confirmButton.Enabled = true;
        UpdateInfoLabel();
        Invalidate();
    }
}
