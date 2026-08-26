using System.Drawing;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Central palette for the selection overlay. Alpha values describe the
/// overlay layer; they are intentionally not scattered through OnPaint.
/// </summary>
internal sealed record RegionSelectionVisualPalette(
    Color SelectionMask,
    Color SelectionAccent,
    Color SelectionBoundary,
    Color EdgeHandleFill,
    Color EdgeHandleOutline,
    Color SelectionLabelSurface,
    Color SelectionLabelText,
    Color HoverWindowBorder,
    Color DisplayBoundary,
    Color DisplayLabelText,
    bool IsHighContrast)
{
    internal static RegionSelectionVisualPalette Create(bool? highContrast = null)
    {
        bool isHighContrast = highContrast ?? SystemInformation.HighContrast;
        if (isHighContrast)
        {
            return new RegionSelectionVisualPalette(
                Color.FromArgb(150, SystemColors.WindowText),
                SystemColors.Highlight,
                SystemColors.WindowText,
                SystemColors.Highlight,
                SystemColors.WindowText,
                SystemColors.Window,
                SystemColors.WindowText,
                SystemColors.Highlight,
                SystemColors.WindowText,
                SystemColors.WindowText,
                true);
        }

        return new RegionSelectionVisualPalette(
            Color.FromArgb(100, 0, 0, 0),
            Color.FromArgb(0, 220, 190),
            Color.FromArgb(175, 155, 245, 230),
            Color.FromArgb(220, 20, 200, 185),
            Color.FromArgb(230, 235, 255, 250),
            Color.FromArgb(225, 12, 28, 38),
            Color.White,
            Color.FromArgb(185, 70, 230, 240),
            Color.FromArgb(105, 255, 255, 255),
            Color.FromArgb(185, 255, 255, 255),
            false);
    }
}
