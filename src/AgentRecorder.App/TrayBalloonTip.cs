using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// A narrow seam for the shell tray balloon surface used by <see cref="TrayContext"/>.
/// Keeps the policy decision unit-testable without relying on real NotifyIcon balloons.
/// </summary>
internal interface ITrayBalloonTip
{
    void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon);
}

/// <summary>
/// Production implementation that forwards to a <see cref="NotifyIcon"/>.
/// </summary>
internal sealed class NotifyIconBalloonTip : ITrayBalloonTip
{
    private readonly NotifyIcon _icon;

    public NotifyIconBalloonTip(NotifyIcon icon)
    {
        _icon = icon;
    }

    public void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon)
    {
        _icon.ShowBalloonTip(timeout, title, body, icon);
    }
}
