using System.Drawing;

namespace AgentRecorder.App;

// Shared native presentation primitive. Keep the hidden-shell ShowNormal and
// foreground restoration protocol in the existing platform adapter.
internal static class LocalApprovalWindowPresentation
{
    internal static FutureWindowApprovalNativeState ShowAndInspect(Form form, IFutureWindowApprovalWindowPlatform platform)
    { platform.ShowNormal(form.Handle); return platform.Inspect(form.Handle); }
    internal static bool Usable(FutureWindowApprovalNativeState state) => state.IsVisible && !state.IsMinimized && state.IsWithinWorkingArea;
    internal static void Fit(Form form)
    {
        Rectangle workArea;
        try { workArea = Screen.FromPoint(Cursor.Position).WorkingArea; if (workArea.Width <= 0 || workArea.Height <= 0) throw new InvalidOperationException(); }
        catch { workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768); }
        var width = Math.Clamp(form.Size.Width, 1, Math.Max(1, workArea.Width - 24));
        var height = Math.Clamp(form.Size.Height, 1, Math.Max(1, workArea.Height - 24));
        form.MinimumSize = new Size(Math.Min(520, width), Math.Min(420, height)); form.Size = new(width, height);
        form.Bounds = new(workArea.Left + Math.Max(0, (workArea.Width - width) / 2), workArea.Top + Math.Max(0, (workArea.Height - height) / 2), width, height);
    }
}
