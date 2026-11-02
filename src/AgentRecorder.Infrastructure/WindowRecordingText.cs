using System.Globalization;

namespace AgentRecorder.Infrastructure;

public static class WindowRecordingText
{
    // Bound only the title on compact controls; never truncate the display number.
    // Full title remains available in confirmation and stop-control tooltip.
    public static string Format(IUiTextProvider text, WindowRecordingIdentity window, bool compact = false)
    {
        var title = new string(window.Title.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (compact)
        {
            var starts = StringInfo.ParseCombiningCharacters(title);
            if (starts.Length > 20) title = title[..starts[20]] + "…";
        }
        var number = text.Format("Recording_WindowName", window.Number.ToString(CultureInfo.InvariantCulture));
        return string.IsNullOrEmpty(title) ? number : $"{number} · {title}";
    }
}
