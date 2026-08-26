using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AgentRecorder.App;

internal readonly record struct ConfirmationCountdownSnapshot(
    double Ratio,
    int RemainingSeconds,
    bool IsUrgent,
    bool IsExpired)
{
    public static ConfirmationCountdownSnapshot Expired => new(0, 0, false, true);
}

/// <summary>
/// Pure absolute-deadline countdown calculation used by the confirmation UI.
/// The timer only asks for a fresh snapshot; it never decrements stored state.
/// </summary>
internal static class ConfirmationCountdownCalculator
{
    internal static ConfirmationCountdownSnapshot Compute(
        TimeSpan totalDuration,
        DateTime deadlineUtc,
        DateTime nowUtc)
    {
        if (totalDuration <= TimeSpan.Zero)
            return ConfirmationCountdownSnapshot.Expired;

        var remaining = deadlineUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
            return ConfirmationCountdownSnapshot.Expired;

        // A clock observation before the calculated start is rendered as the
        // full safe range rather than allowing a ratio or integer to overflow.
        if (remaining > totalDuration)
            remaining = totalDuration;

        double ratio = remaining.TotalSeconds / totalDuration.TotalSeconds;
        ratio = Math.Clamp(ratio, 0d, 1d);

        double secondsValue = Math.Ceiling(Math.Max(0d, remaining.TotalSeconds));
        int seconds = secondsValue >= int.MaxValue
            ? int.MaxValue
            : Math.Max(0, (int)secondsValue);

        return new ConfirmationCountdownSnapshot(
            ratio,
            seconds,
            seconds is > 0 and <= 5,
            false);
    }
}

/// <summary>
/// Fixed-size, non-interactive owner-drawn countdown indicator for the formal
/// WinForms confirmation window. It is a progress indicator, never a button.
/// </summary>
internal sealed class ConfirmationCountdownRing : Control
{
    internal const int LogicalDiameter = 52;
    private const float LogicalStrokeWidth = 4f;

    private ConfirmationCountdownSnapshot _snapshot = ConfirmationCountdownSnapshot.Expired;
    private Color _trackColor = SystemColors.ControlDark;
    private Color _arcColor = SystemColors.Highlight;
    private Color _urgentArcColor = SystemColors.Highlight;
    private Color _textColor = SystemColors.ControlText;

    public ConfirmationCountdownRing()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        AutoSize = false;
        TabStop = false;
        Enabled = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Confirmation time remaining";
        AccessibleDescription = "Non-interactive confirmation timeout indicator";
        BackColor = SystemColors.Control;
        Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        Size = new Size(LogicalDiameter, LogicalDiameter);
    }

    internal ConfirmationCountdownSnapshot Snapshot => _snapshot;

    internal void ApplySnapshot(ConfirmationCountdownSnapshot snapshot)
    {
        if (_snapshot == snapshot)
            return;

        _snapshot = snapshot;
        Invalidate();
    }

    internal void ApplyPalette(ConfirmationThemePalette palette)
    {
        _trackColor = palette.CountdownTrack;
        _arcColor = palette.CountdownArc;
        _urgentArcColor = palette.CountdownUrgentArc;
        _textColor = palette.CountdownText;
        BackColor = palette.WindowBackground;
        Invalidate();
    }

    internal void ApplyAccessibilityText(string text)
    {
        AccessibleName = text;
        AccessibleDescription = text;
    }

    internal static int ScaleLogicalSize(int logicalSize, int dpi)
    {
        int effectiveDpi = dpi <= 0 ? 96 : dpi;
        return Math.Max(1, (int)Math.Round(logicalSize * effectiveDpi / 96d));
    }

    internal static RectangleF ComputePaintBounds(Size clientSize, float strokeWidth)
    {
        float inset = Math.Max(1f, strokeWidth / 2f + 1f);
        float width = Math.Max(0f, clientSize.Width - inset * 2f);
        float height = Math.Max(0f, clientSize.Height - inset * 2f);
        float diameter = Math.Min(width, height);
        float left = (clientSize.Width - diameter) / 2f;
        float top = (clientSize.Height - diameter) / 2f;
        return new RectangleF(left, top, diameter, diameter);
    }

    internal static Rectangle ComputeTextBounds(Size clientSize)
    {
        int width = Math.Max(0, clientSize.Width);
        int height = Math.Max(0, clientSize.Height);
        return new Rectangle(0, 0, width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiSize();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiSize();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        float strokeWidth = Math.Max(2f, LogicalStrokeWidth * scale);
        var bounds = ComputePaintBounds(ClientSize, strokeWidth);
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return;

        using var trackPen = new Pen(_trackColor, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var progressPen = new Pen(
            _snapshot.IsUrgent ? _urgentArcColor : _arcColor,
            strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        graphics.DrawArc(trackPen, bounds, -90f, 360f);
        if (_snapshot.Ratio > 0d)
            graphics.DrawArc(progressPen, bounds, -90f, (float)(360d * Math.Clamp(_snapshot.Ratio, 0d, 1d)));

        var textBounds = ComputeTextBounds(ClientSize);
        using var textBrush = new SolidBrush(_textColor);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(
            _snapshot.RemainingSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Font,
            textBrush,
            textBounds,
            format);
    }

    private void ApplyDpiSize()
    {
        int diameter = ScaleLogicalSize(LogicalDiameter, DeviceDpi);
        if (Width != diameter || Height != diameter)
            Size = new Size(diameter, diameter);
    }
}
