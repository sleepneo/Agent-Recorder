#if DEBUG
using System.Drawing;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Debug-only real desktop preview for the selection overlay. It creates only
/// the selection form; no API, configuration, capture, or recording service is
/// started. Closing by Confirm, Cancel, Enter, or Escape simply ends preview.
/// </summary>
internal static class RegionSelectionStylePreviewHost
{
    private const string Argument = "--region-selection-style-preview";

    public static bool TryRun(string[] args)
    {
        if (!args.Any(arg => arg.Equals(Argument, StringComparison.OrdinalIgnoreCase)))
            return false;

        ApplicationConfiguration.Initialize();

        var virtualBounds = SystemInformation.VirtualScreen;
        int width = Math.Min(960, Math.Max(320, virtualBounds.Width / 2));
        int height = Math.Min(540, Math.Max(240, virtualBounds.Height / 2));
        width = Math.Min(width, virtualBounds.Width);
        height = Math.Min(height, virtualBounds.Height);
        var initial = new Rectangle(
            virtualBounds.X + Math.Max(0, (virtualBounds.Width - width) / 2),
            virtualBounds.Y + Math.Max(0, (virtualBounds.Height - height) / 2),
            width,
            height);

        using var form = new RegionSelectionForm(initial)
        {
            Text = "Agent Recorder — Region Selection Style Preview",
            // Keep the real overlay style while exposing a stable target to
            // the desktop acceptance harness. Production selection windows
            // remain ShowInTaskbar=false.
            ShowInTaskbar = true,
            // The borderless production form is not surfaced by some desktop
            // capture providers. Debug-only chrome keeps the same client
            // painting, hit-test, snapping, and DPI paths observable.
            FormBorderStyle = FormBorderStyle.FixedSingle,
            ControlBox = false
        };
        Application.Run(form);
        return true;
    }
}
#endif
