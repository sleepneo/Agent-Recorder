using System.Drawing;

namespace AgentRecorder.App;

internal enum ConfirmationThemeKind
{
    Light,
    Dark,
    HighContrast
}

internal readonly record struct ConfirmationThemeSnapshot(
    ConfirmationThemeKind Kind,
    ConfirmationThemePalette Palette);

/// <summary>
/// All colors used by the confirmation surface. Keeping the palette immutable
/// makes theme application deterministic and keeps color decisions out of the
/// individual layout builders.
/// </summary>
internal readonly record struct ConfirmationThemePalette(
    Color WindowBackground,
    Color Surface,
    Color SecondarySurface,
    Color PrimaryText,
    Color SecondaryText,
    Color Border,
    Color Divider,
    Color WarningText,
    Color ErrorText,
    Color NeutralButtonBackground,
    Color NeutralButtonHover,
    Color NeutralButtonPressed,
    Color NeutralButtonText,
    Color NeutralButtonBorder,
    Color ApproveBackground,
    Color ApproveHover,
    Color ApprovePressed,
    Color ApproveDisabled,
    Color ApproveText,
    Color RejectBackground,
    Color RejectHover,
    Color RejectPressed,
    Color RejectDisabled,
    Color RejectText,
    Color DisabledText,
    Color FocusBorder,
    Color SelectionBackground,
    Color SelectionText,
    Color PreviewBackground,
    Color PreviewFallbackText,
    Color CountdownTrack,
    Color CountdownArc,
    Color CountdownUrgentArc,
    Color CountdownText)
{
    public static ConfirmationThemePalette For(ConfirmationThemeKind kind) => kind switch
    {
        ConfirmationThemeKind.Dark => Dark,
        ConfirmationThemeKind.HighContrast => HighContrast,
        _ => Light
    };

    // Neutral surfaces intentionally avoid pure white/black so the form stays
    // calm while the primary text still meets normal-text contrast targets.
    public static ConfirmationThemePalette Light { get; } = new(
        WindowBackground: Color.FromArgb(250, 250, 250),
        Surface: Color.FromArgb(245, 245, 245),
        SecondarySurface: Color.FromArgb(250, 250, 250),
        PrimaryText: Color.FromArgb(32, 32, 32),
        SecondaryText: Color.FromArgb(82, 82, 82),
        Border: Color.FromArgb(178, 178, 178),
        Divider: Color.FromArgb(215, 215, 215),
        WarningText: Color.FromArgb(126, 62, 0),
        ErrorText: Color.FromArgb(150, 0, 0),
        NeutralButtonBackground: Color.FromArgb(235, 235, 235),
        NeutralButtonHover: Color.FromArgb(224, 224, 224),
        NeutralButtonPressed: Color.FromArgb(210, 210, 210),
        NeutralButtonText: Color.FromArgb(32, 32, 32),
        NeutralButtonBorder: Color.FromArgb(150, 150, 150),
        ApproveBackground: Color.FromArgb(18, 112, 75),
        ApproveHover: Color.FromArgb(13, 94, 62),
        ApprovePressed: Color.FromArgb(9, 77, 50),
        ApproveDisabled: Color.FromArgb(190, 205, 198),
        ApproveText: Color.White,
        RejectBackground: Color.FromArgb(165, 42, 42),
        RejectHover: Color.FromArgb(143, 31, 31),
        RejectPressed: Color.FromArgb(119, 22, 22),
        RejectDisabled: Color.FromArgb(218, 192, 192),
        RejectText: Color.White,
        DisabledText: Color.FromArgb(105, 105, 105),
        FocusBorder: Color.FromArgb(0, 92, 165),
        SelectionBackground: Color.FromArgb(215, 232, 248),
        SelectionText: Color.FromArgb(20, 45, 70),
        PreviewBackground: Color.FromArgb(38, 38, 38),
        PreviewFallbackText: Color.FromArgb(224, 224, 224),
        CountdownTrack: Color.FromArgb(215, 215, 215),
        CountdownArc: Color.FromArgb(18, 112, 75),
        CountdownUrgentArc: Color.FromArgb(150, 0, 0),
        CountdownText: Color.FromArgb(32, 32, 32));

    public static ConfirmationThemePalette Dark { get; } = new(
        WindowBackground: Color.FromArgb(32, 32, 32),
        Surface: Color.FromArgb(43, 43, 43),
        SecondarySurface: Color.FromArgb(49, 49, 49),
        PrimaryText: Color.FromArgb(242, 242, 242),
        SecondaryText: Color.FromArgb(195, 195, 195),
        Border: Color.FromArgb(105, 105, 105),
        Divider: Color.FromArgb(75, 75, 75),
        WarningText: Color.FromArgb(255, 193, 92),
        ErrorText: Color.FromArgb(255, 125, 125),
        NeutralButtonBackground: Color.FromArgb(65, 65, 65),
        NeutralButtonHover: Color.FromArgb(82, 82, 82),
        NeutralButtonPressed: Color.FromArgb(98, 98, 98),
        NeutralButtonText: Color.FromArgb(242, 242, 242),
        NeutralButtonBorder: Color.FromArgb(132, 132, 132),
        ApproveBackground: Color.FromArgb(28, 126, 85),
        ApproveHover: Color.FromArgb(37, 145, 98),
        ApprovePressed: Color.FromArgb(50, 160, 110),
        ApproveDisabled: Color.FromArgb(72, 92, 82),
        ApproveText: Color.White,
        RejectBackground: Color.FromArgb(178, 54, 54),
        RejectHover: Color.FromArgb(198, 69, 69),
        RejectPressed: Color.FromArgb(214, 84, 84),
        RejectDisabled: Color.FromArgb(104, 75, 75),
        RejectText: Color.White,
        DisabledText: Color.FromArgb(145, 145, 145),
        FocusBorder: Color.FromArgb(108, 183, 238),
        SelectionBackground: Color.FromArgb(55, 86, 116),
        SelectionText: Color.FromArgb(245, 245, 245),
        PreviewBackground: Color.FromArgb(22, 22, 22),
        PreviewFallbackText: Color.FromArgb(225, 225, 225),
        CountdownTrack: Color.FromArgb(75, 75, 75),
        CountdownArc: Color.FromArgb(28, 126, 85),
        CountdownUrgentArc: Color.FromArgb(255, 125, 125),
        CountdownText: Color.FromArgb(242, 242, 242));

    public static ConfirmationThemePalette HighContrast { get; } = new(
        WindowBackground: SystemColors.Window,
        Surface: SystemColors.Window,
        SecondarySurface: SystemColors.Control,
        PrimaryText: SystemColors.WindowText,
        SecondaryText: SystemColors.GrayText,
        Border: SystemColors.WindowText,
        Divider: SystemColors.WindowText,
        WarningText: SystemColors.Highlight,
        ErrorText: SystemColors.Highlight,
        NeutralButtonBackground: SystemColors.Control,
        NeutralButtonHover: SystemColors.Highlight,
        NeutralButtonPressed: SystemColors.Highlight,
        NeutralButtonText: SystemColors.ControlText,
        NeutralButtonBorder: SystemColors.WindowText,
        ApproveBackground: SystemColors.Highlight,
        ApproveHover: SystemColors.Highlight,
        ApprovePressed: SystemColors.Highlight,
        ApproveDisabled: SystemColors.Control,
        ApproveText: SystemColors.HighlightText,
        RejectBackground: SystemColors.Highlight,
        RejectHover: SystemColors.Highlight,
        RejectPressed: SystemColors.Highlight,
        RejectDisabled: SystemColors.Control,
        RejectText: SystemColors.HighlightText,
        DisabledText: SystemColors.GrayText,
        FocusBorder: SystemColors.Highlight,
        SelectionBackground: SystemColors.Highlight,
        SelectionText: SystemColors.HighlightText,
        PreviewBackground: SystemColors.Window,
        PreviewFallbackText: SystemColors.WindowText,
        CountdownTrack: SystemColors.WindowText,
        CountdownArc: SystemColors.Highlight,
        CountdownUrgentArc: SystemColors.Highlight,
        CountdownText: SystemColors.WindowText);
}

internal static class ConfirmationThemeContrast
{
    public static double Ratio(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            double normalized = value / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        double foregroundLuminance =
            0.2126 * Channel(foreground.R) +
            0.7152 * Channel(foreground.G) +
            0.0722 * Channel(foreground.B);
        double backgroundLuminance =
            0.2126 * Channel(background.R) +
            0.7152 * Channel(background.G) +
            0.0722 * Channel(background.B);

        double lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        double darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
