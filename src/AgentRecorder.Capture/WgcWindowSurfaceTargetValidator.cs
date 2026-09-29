using System.Linq;
using AgentRecorder.Windows;

namespace AgentRecorder.Capture;

/// <summary>
/// Revalidates the fixed HWND identity and visible size at the last managed
/// boundary before WGC is launched. The native helper separately rechecks
/// HWND existence/minimized state immediately before StartCapture.
/// </summary>
internal static class WgcWindowSurfaceTargetValidator
{
    public static string? Validate(CaptureConfig config)
    {
        if (!config.RequireWindowSurface)
            return null;
        if (config.WindowHandle == nint.Zero ||
            config.WindowProcessId is not int expectedProcessId || expectedProcessId <= 0 ||
            config.WindowSurfaceBounds is not { } expectedBounds)
            return "window_identity_unavailable";

        try
        {
            var id = $"window_{config.WindowHandle.ToInt64()}";
            var window = SystemQuery.EnumWindows(includeMinimized: true, includeSystem: false)
                .FirstOrDefault(candidate => candidate.id == id);
            if (window == null)
                return "window_closed";
            if (window.is_minimized)
                return "window_minimized";
            if (window.process_id != expectedProcessId)
                return "window_identity_changed";
            if (window.bounds.width != expectedBounds.w || window.bounds.height != expectedBounds.h)
                return "window_size_changed";
            return null;
        }
        catch
        {
            return "window_state_unavailable";
        }
    }
}
