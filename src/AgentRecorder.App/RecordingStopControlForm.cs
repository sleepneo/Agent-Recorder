using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// A small floating stop button for a single active recording.
/// Top-most, borderless, not shown in the taskbar, and does not steal activation.
/// Only the button itself is clickable; the form is sized exactly to the button.
/// </summary>
internal sealed class RecordingStopControlForm : Form
{
    private readonly string _recordingId;
    private readonly IUiTextProvider _text;
    private readonly DisplayDpiInfo? _dpiInfo;
    private readonly Size? _explicitControlSize;
    private Button _button = null!;
    private ToolTip _tooltip = null!;
    private int _clicked;
    private int _paintCount;
    private int _stoppingPaintCount;
    private int _actualWindowDpi;
    private bool _dpiMismatch;

    /// <summary>
    /// Raised once when the user clicks the stop button.
    /// </summary>
    public event Action<string>? StopClicked;

    internal RecordingStopControlBounds PlacementBounds { get; }
    internal bool ButtonEnabledForTests => _button?.Enabled ?? false;
    internal string ButtonTextForTests => _button?.Text ?? "";
    internal string TooltipTextForTests => _tooltip?.GetToolTip(_button) ?? "";
    internal Size MeasuredSizeForTests => RecordingStopControlLayout.MeasurePreferredSize(
        _text, _button?.Font ?? new Font("Segoe UI", 8, FontStyle.Bold));
    internal Rectangle ButtonBoundsForTests => _button?.Bounds ?? Rectangle.Empty;
    internal int ButtonPaintCountForTests => _button != null ? _paintCount : 0;
    internal int StoppingPaintCountForTests => _stoppingPaintCount;
    internal int PlannedDpiForTests => _dpiInfo != null ? (int)Math.Round(_dpiInfo.Scale * 96) : 0;
    internal int ActualWindowDpiForTests => _actualWindowDpi;
    internal bool DpiMismatchForTests => _dpiMismatch;

    public RecordingStopControlForm(string recordingId, RecordingStopControlBounds bounds, IUiTextProvider? textProvider = null)
    {
        _recordingId = recordingId;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        PlacementBounds = bounds;
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
        IUiTextProvider? textProvider = null)
    {
        _recordingId = recordingId;
        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        _explicitControlSize = controlSize;
        _dpiInfo = dpiInfo;
        PlacementBounds = bounds;
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
        BackColor = Color.White;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "";
        AutoScaleMode = _explicitControlSize.HasValue ? AutoScaleMode.None : AutoScaleMode.Dpi;

        var font = new Font("Segoe UI", 8, FontStyle.Bold);
        var measuredSize = _explicitControlSize ?? RecordingStopControlLayout.MeasurePreferredSize(_text, font);

        _button = new Button
        {
            Text = _text.Get("StopControl_Button_Stop"),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(220, 255, 0, 0),
            ForeColor = Color.White,
            Font = font,
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
            Padding = RecordingStopControlLayout.ButtonPadding
        };
        _button.FlatAppearance.BorderSize = 0;
        _button.Click += OnButtonClick;
        _button.Paint += OnButtonPaint;
        Controls.Add(_button);

        _tooltip = new ToolTip();
        _tooltip.SetToolTip(_button, _text.Get("StopControl_Tooltip"));

        ClientSize = measuredSize;
    }

    private void OnButtonClick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _clicked, 1) == 1)
            return;

        _button.Enabled = false;
        _button.Text = _text.Get("StopControl_Button_Stopping");
        _button.Invalidate();
        _button.Update(); // synchronously paint at least one frame of the stopping state

        StopClicked?.Invoke(_recordingId);
    }

    private void OnButtonPaint(object? sender, PaintEventArgs e)
    {
        Interlocked.Increment(ref _paintCount);
        if (string.Equals(_button.Text, _text.Get("StopControl_Button_Stopping"), StringComparison.Ordinal))
            Interlocked.Increment(ref _stoppingPaintCount);
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
            _button.Enabled = true;
            _button.Text = _text.Get("StopControl_Button_Stop");
        }
    }

    protected override bool ShowWithoutActivation => true;

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
}
