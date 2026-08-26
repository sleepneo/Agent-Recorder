using System.Drawing;
using System.Drawing.Drawing2D;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

/// <summary>
/// Pure visual states used by the recording indicator and its stop capsule.
/// The model deliberately contains no lifecycle, timer, or window ownership.
/// </summary>
internal enum RecordingStopControlVisualState
{
    Normal,
    Hover,
    Pressed,
    Stopping,
    Disabled
}

internal readonly record struct RecordingIndicatorVisualPalette(
    Color Border,
    Color RecordingLow,
    Color RecordingHigh,
    Color LabelBackgroundLow,
    Color LabelBackgroundHigh,
    Color LabelForeground,
    bool IsHighContrast)
{
    public Color LabelBackground => LabelBackgroundLow;
}

internal readonly record struct RecordingStopControlVisualPalette(
    Color Normal,
    Color Hover,
    Color Pressed,
    Color Stopping,
    Color Disabled,
    Color Foreground,
    Color DisabledForeground,
    Color CapsuleBorder,
    bool IsHighContrast);

/// <summary>
/// Centralized recording-status visual mapping. All ordinary colors and all
/// high-contrast colors are selected here so the two WinForms surfaces do not
/// grow independent color constants.
/// </summary>
internal static class RecordingStatusVisualModel
{
    private static readonly Color RecordingLow = Color.FromArgb(255, 204, 39, 55);
    private static readonly Color RecordingHigh = Color.FromArgb(255, 235, 61, 76);
    private static readonly Color RecordingLabelLow = Color.FromArgb(255, 184, 25, 42);
    private static readonly Color RecordingLabelHigh = Color.FromArgb(255, 205, 34, 50);
    private static readonly Color PreparingBorder = Color.FromArgb(255, 218, 142, 0);
    private static readonly Color PreparingLabel = Color.FromArgb(255, 183, 113, 0);
    private static readonly Color FinalizingBorder = Color.FromArgb(255, 128, 128, 128);
    private static readonly Color FinalizingLabel = Color.FromArgb(255, 96, 96, 96);

    private static readonly Color StopNormal = Color.FromArgb(255, 190, 28, 48);
    private static readonly Color StopHover = Color.FromArgb(255, 215, 43, 62);
    private static readonly Color StopPressed = Color.FromArgb(255, 158, 18, 36);
    private static readonly Color StopStopping = Color.FromArgb(255, 112, 112, 112);
    private static readonly Color StopDisabled = Color.FromArgb(255, 150, 150, 150);

    public static RecordingIndicatorVisualPalette IndicatorPalette(
        RecordingIndicatorPhase phase,
        bool highContrast)
    {
        if (highContrast)
        {
            return new RecordingIndicatorVisualPalette(
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.HighlightText,
                IsHighContrast: true);
        }

        return phase switch
        {
            RecordingIndicatorPhase.Preparing or RecordingIndicatorPhase.Countdown =>
                new RecordingIndicatorVisualPalette(
                    PreparingBorder,
                    PreparingBorder,
                    PreparingBorder,
                    PreparingLabel,
                    PreparingLabel,
                    Color.White,
                    IsHighContrast: false),
            RecordingIndicatorPhase.Finalizing =>
                new RecordingIndicatorVisualPalette(
                    FinalizingBorder,
                    FinalizingBorder,
                    FinalizingBorder,
                    FinalizingLabel,
                    FinalizingLabel,
                    Color.White,
                    IsHighContrast: false),
            RecordingIndicatorPhase.Series =>
                new RecordingIndicatorVisualPalette(
                    RecordingLow,
                    RecordingLow,
                    RecordingLow,
                    RecordingLabelLow,
                    RecordingLabelLow,
                    Color.White,
                    IsHighContrast: false),
            _ =>
                new RecordingIndicatorVisualPalette(
                    RecordingLow,
                    RecordingLow,
                    RecordingHigh,
                    RecordingLabelLow,
                    RecordingLabelHigh,
                    Color.White,
                    IsHighContrast: false)
        };
    }

    public static RecordingStopControlVisualPalette StopControlPalette(bool highContrast)
    {
        if (highContrast)
        {
            return new RecordingStopControlVisualPalette(
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.HighlightText,
                SystemColors.GrayText,
                SystemColors.WindowText,
                IsHighContrast: true);
        }

        return new RecordingStopControlVisualPalette(
            StopNormal,
            StopHover,
            StopPressed,
            StopStopping,
            StopDisabled,
            Color.White,
            Color.White,
            StopPressed,
            IsHighContrast: false);
    }

    public static Color StopControlBackground(
        RecordingStopControlVisualPalette palette,
        RecordingStopControlVisualState state)
        => state switch
        {
            RecordingStopControlVisualState.Hover => palette.Hover,
            RecordingStopControlVisualState.Pressed => palette.Pressed,
            RecordingStopControlVisualState.Stopping => palette.Stopping,
            RecordingStopControlVisualState.Disabled => palette.Disabled,
            _ => palette.Normal
        };

    public static int CapsuleCornerRadius(Size size)
        => Math.Min(Math.Max(0, size.Width), Math.Max(0, size.Height));

    public static bool IsInsideCapsule(Size size, Point point)
    {
        int radius = CapsuleCornerRadius(size);
        if (radius <= 1 || point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)
            return point.X >= 0 && point.Y >= 0 && point.X < size.Width && point.Y < size.Height;

        int half = radius / 2;
        if (point.X >= half && point.X < size.Width - half)
            return true;
        if (point.Y >= half && point.Y < size.Height - half)
            return true;

        int centerX = point.X < half ? half : size.Width - half - 1;
        int centerY = point.Y < half ? half : size.Height - half - 1;
        int dx = point.X - centerX;
        int dy = point.Y - centerY;
        return dx * dx + dy * dy <= half * half;
    }

    public static Color IndicatorRecordingColor(
        RecordingIndicatorVisualPalette palette,
        double amount)
        => RecordingIndicatorMotion.BlendOpaque(palette.RecordingLow, palette.RecordingHigh, amount);

    public static Color IndicatorLabelColor(
        RecordingIndicatorVisualPalette palette,
        double amount)
        => RecordingIndicatorMotion.BlendOpaque(palette.LabelBackgroundLow, palette.LabelBackgroundHigh, amount);

    public static GraphicsPath CreateCapsulePath(Size size)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        var bounds = new Rectangle(0, 0, width, height);
        int diameter = Math.Min(width, height);
        var path = new GraphicsPath();
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Region CreateCapsuleRegion(Size size)
    {
        using var path = CreateCapsulePath(size);
        return new Region(path);
    }

    public static string RecordingPrefix(IUiTextProvider text, string? nestedRole)
    {
        if (string.Equals(nestedRole, "outer", StringComparison.OrdinalIgnoreCase))
            return text.Get("Indicator_Recording_Outer");
        if (string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase))
            return text.Get("Indicator_Recording_Inner");
        return text.Get("Indicator_Recording");
    }

    public static string StopButtonText(IUiTextProvider text, string? nestedRole, bool stopping)
    {
        var stateText = text.Get(stopping
            ? "StopControl_Button_Stopping"
            : "StopControl_Button_Stop");
        var roleText = StopRoleText(text, nestedRole);
        return roleText.Length == 0 ? stateText : $"{roleText} · {stateText}";
    }

    public static string StopTooltip(IUiTextProvider text, string? nestedRole)
    {
        var baseText = text.Get("StopControl_Tooltip");
        var roleText = StopRoleText(text, nestedRole);
        return roleText.Length == 0 ? baseText : $"{baseText} ({roleText})";
    }

    private static string StopRoleText(IUiTextProvider text, string? nestedRole)
    {
        if (string.Equals(nestedRole, "outer", StringComparison.OrdinalIgnoreCase))
            return text.Get("StopControl_Role_Outer");
        if (string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase))
            return text.Get("StopControl_Role_Inner");
        return "";
    }
}
