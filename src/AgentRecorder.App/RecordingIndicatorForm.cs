using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Immutable description of the recording area shown by the indicator.
/// </summary>
internal sealed record RecordingIndicatorBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Geometry helpers for placing the recording indicator and its label on screen.
/// </summary>
internal static class RecordingIndicatorGeometry
{
    public const int BorderWidth = 4;
    public const int MinIndicatorSize = 32;

    /// <summary>
    /// Attempts to clamp the recording bounds to the virtual screen and enforce
    /// the minimum indicator size. Returns <c>null</c> when the bounds are not
    /// displayable (zero/negative size, completely outside the virtual screen,
    /// or the virtual screen itself has no area).
    /// </summary>
    public static RecordingIndicatorBounds? TryClampToVirtualScreen(RecordingIndicatorBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var vs = SystemInformation.VirtualScreen;
        if (vs.Width <= 0 || vs.Height <= 0)
            return null;

        int left = Math.Max(bounds.X, vs.X);
        int top = Math.Max(bounds.Y, vs.Y);
        int right = Math.Min(bounds.X + bounds.Width, vs.X + vs.Width);
        int bottom = Math.Min(bounds.Y + bounds.Height, vs.Y + vs.Height);

        // No overlap with the virtual screen.
        if (right <= left || bottom <= top)
            return null;

        // Try to expand a small intersection to the minimum size while staying
        // inside the virtual screen. If the virtual screen is smaller than the
        // minimum size, fall back to the full available area.
        if (right - left < MinIndicatorSize)
        {
            int extra = MinIndicatorSize - (right - left);
            int expandLeft = extra / 2;
            int expandRight = extra - expandLeft;

            int newLeft = left - expandLeft;
            int newRight = right + expandRight;

            if (newLeft < vs.X) newLeft = vs.X;
            if (newRight > vs.X + vs.Width) newRight = vs.X + vs.Width;

            if (newRight - newLeft < MinIndicatorSize)
            {
                newLeft = vs.X;
                newRight = vs.X + vs.Width;
            }

            left = newLeft;
            right = newRight;
        }

        if (bottom - top < MinIndicatorSize)
        {
            int extra = MinIndicatorSize - (bottom - top);
            int expandTop = extra / 2;
            int expandBottom = extra - expandTop;

            int newTop = top - expandTop;
            int newBottom = bottom + expandBottom;

            if (newTop < vs.Y) newTop = vs.Y;
            if (newBottom > vs.Y + vs.Height) newBottom = vs.Y + vs.Height;

            if (newBottom - newTop < MinIndicatorSize)
            {
                newTop = vs.Y;
                newBottom = vs.Y + vs.Height;
            }

            top = newTop;
            bottom = newBottom;
        }

        int width = right - left;
        int height = bottom - top;

        if (width <= 0 || height <= 0)
            return null;

        return new RecordingIndicatorBounds(left, top, width, height);
    }

    /// <summary>
    /// Clamps the recording bounds to the virtual screen. Throws when the bounds
    /// cannot be displayed at all; callers that need to handle undisplayable
    /// bounds should use <see cref="TryClampToVirtualScreen"/>.
    /// </summary>
    public static RecordingIndicatorBounds ClampToVirtualScreen(RecordingIndicatorBounds bounds)
    {
        var clamped = TryClampToVirtualScreen(bounds)
            ?? throw new ArgumentException("Recording indicator bounds are not displayable.", nameof(bounds));
        return clamped;
    }

    /// <summary>
    /// Computes a top-left label location that stays inside the indicator bounds.
    /// </summary>
    public static Point ComputeLabelLocation(RecordingIndicatorBounds bounds, Size labelSize)
    {
        int x = bounds.X + BorderWidth + 2;
        int y = bounds.Y + BorderWidth + 2;

        // If label would overflow right/bottom, move it inside.
        var vs = SystemInformation.VirtualScreen;
        int maxRight = Math.Min(bounds.X + bounds.Width, vs.X + vs.Width);
        int maxBottom = Math.Min(bounds.Y + bounds.Height, vs.Y + vs.Height);

        if (x + labelSize.Width > maxRight)
            x = Math.Max(bounds.X, maxRight - labelSize.Width);
        if (y + labelSize.Height > maxBottom)
            y = Math.Max(bounds.Y, maxBottom - labelSize.Height);

        return new Point(x, y);
    }
}

/// <summary>
/// Top-most, click-through, non-activating border window that indicates an active recording region.
/// Displays a red border and a small REC timer label. Does not capture focus or block user input.
/// </summary>
internal sealed class RecordingIndicatorForm : Form
{
    private readonly string _recordingId;
    private readonly RecordingIndicatorBounds _bounds;
    private readonly DateTime _startedAtUtc;
    private readonly int? _durationSeconds;
    private readonly string? _nestedRole;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _label = null!;

    internal RecordingIndicatorBounds BoundsForTests => _bounds;
    internal bool TimerEnabledForTests => _timer?.Enabled ?? false;
    internal string LabelTextForTests => _label?.Text ?? "";
    internal Rectangle LabelBoundsForTests => _label?.Bounds ?? Rectangle.Empty;
    internal Size LabelMeasuredSizeForTests => MeasureLabelSize(_nestedRole, _durationSeconds, _label?.Font ?? new Font("Segoe UI", 9, FontStyle.Bold), _label?.Padding ?? new Padding(4, 2, 4, 2));

    public RecordingIndicatorForm(
        string recordingId,
        RecordingIndicatorBounds bounds,
        DateTime startedAtUtc,
        int? durationSeconds = null,
        string? nestedRole = null)
    {
        _recordingId = recordingId;
        _bounds = RecordingIndicatorGeometry.ClampToVirtualScreen(bounds);
        _startedAtUtc = startedAtUtc;
        _durationSeconds = durationSeconds;
        _nestedRole = nestedRole;

        InitializeComponent();
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
    }

    private void InitializeComponent()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 1.0;
        DoubleBuffered = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "";
        AutoScaleMode = AutoScaleMode.Dpi;

        var font = new Font("Segoe UI", 9, FontStyle.Bold);
        var padding = new Padding(4, 2, 4, 2);
        var size = MeasureLabelSize(_nestedRole, _durationSeconds, font, padding);

        _label = new Label
        {
            AutoSize = false,
            BackColor = Color.FromArgb(180, 255, 0, 0),
            ForeColor = Color.White,
            Font = font,
            Padding = padding,
            Text = FormatLabel(TimeSpan.Zero),
            Visible = true,
            Size = size,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 500
        };
        _timer.Tick += (_, _) => UpdateLabel();
    }

    /// <summary>
    /// Maximum manual recording duration supported by the product API. Used as the
    /// conservative upper bound for label sizing when no explicit duration is set.
    /// </summary>
    internal const int MaxManualRecordingSeconds = 7200;

    /// <summary>
    /// Formats a non-negative time span for the REC label.
    /// Uses mm:ss below one hour and h:mm:ss at or above one hour to avoid
    /// minute-component wrap-around after 59:59.
    /// </summary>
    internal static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    /// <summary>
    /// Measures the label size required to display the longest possible text for this
    /// recording without resizing during the recording. The elapsed and total portions
    /// share the same formatting helper so measurement and runtime rendering stay in sync.
    /// </summary>
    internal static Size MeasureLabelSize(string? nestedRole, int? durationSeconds, Font font, Padding padding)
    {
        var prefix = string.IsNullOrEmpty(nestedRole)
            ? "REC"
            : $"REC {nestedRole.ToUpperInvariant()}";

        string maxText;
        if (durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            var total = TimeSpan.FromSeconds(durationSeconds.Value);
            var longestElapsed = total; // elapsed never exceeds total
            maxText = $"{prefix} {FormatTime(longestElapsed)} / {FormatTime(total)}";
        }
        else
        {
            var longestElapsed = TimeSpan.FromSeconds(MaxManualRecordingSeconds);
            maxText = $"{prefix} {FormatTime(longestElapsed)}";
        }

        var textSize = TextRenderer.MeasureText(maxText, font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(textSize.Width + padding.Horizontal, textSize.Height + padding.Vertical);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionLabel();
        UpdateLabel();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.Red, RecordingIndicatorGeometry.BorderWidth);
        var rect = ClientRectangle;
        // Draw slightly inside so the full border is visible.
        float offset = RecordingIndicatorGeometry.BorderWidth / 2.0f;
        e.Graphics.DrawRectangle(pen, offset, offset, rect.Width - RecordingIndicatorGeometry.BorderWidth, rect.Height - RecordingIndicatorGeometry.BorderWidth);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionLabel();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Ensure the form does not steal activation when shown.
        // WM_SHOWWINDOW with SW_SHOWNOACTIVATE is handled by ShowWithoutActivation.
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_NOACTIVATE = 0x8000000;
            const int WS_EX_TOOLWINDOW = 0x80;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            // Keep click-through behavior: no caption, no thick frame.
            cp.Style &= ~(0x00C00000 | 0x00040000 | 0x00010000); // WS_CAPTION, WS_THICKFRAME, WS_SYSMENU
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _label?.Font?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Closes the indicator without side effects. Safe to call multiple times.
    /// </summary>
    internal void CloseWithoutResult()
    {
        try { Close(); } catch { }
    }

    private void PositionLabel()
    {
        if (_label == null) return;
        var size = _label.Size;
        var loc = RecordingIndicatorGeometry.ComputeLabelLocation(_bounds, size);
        _label.Location = new Point(loc.X - _bounds.X, loc.Y - _bounds.Y);
    }

    private void UpdateLabel()
    {
        var elapsed = DateTime.UtcNow - _startedAtUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        _label.Text = FormatLabel(elapsed);
    }

    private string FormatLabel(TimeSpan elapsed)
    {
        var prefix = string.IsNullOrEmpty(_nestedRole)
            ? "REC"
            : $"REC {_nestedRole.ToUpperInvariant()}";

        if (_durationSeconds.HasValue && _durationSeconds.Value > 0)
        {
            var total = TimeSpan.FromSeconds(_durationSeconds.Value);
            return $"{prefix} {FormatTime(elapsed)} / {FormatTime(total)}";
        }
        return $"{prefix} {FormatTime(elapsed)}";
    }
}
