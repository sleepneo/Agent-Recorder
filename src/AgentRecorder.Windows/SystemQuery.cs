using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
namespace AgentRecorder.Windows;

public static class SystemQuery
{
    public record Bounds(int x, int y, int width, int height);

    /// <summary>
    /// Public display information returned by <see cref="EnumDisplays"/>. This is the
    /// stable API contract for <c>GET /api/v1/displays</c>; it intentionally does not
    /// contain Win32 handles or internal DPI values.
    /// </summary>
    public record DisplayInfo(string id, string name, bool is_primary, Bounds bounds, double scale_factor);

    /// <summary>
    /// Internal display information used by the floating stop-control layout logic.
    /// Contains the effective DPI and monitor handle needed for PerMonitorV2 sizing.
    /// </summary>
    internal record DisplayDetail(string id, string name, bool is_primary, Bounds bounds, double scale_factor, int dpiX, int dpiY, IntPtr handle);

    public record WindowInfo(string id, string title, string app_name, int process_id, bool is_active, bool is_minimized, Bounds bounds);
    public record AudioDevice(string id, string name, bool is_default, string state);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// Injectable display provider for testing. When set, EnumDisplays() returns
    /// displays from this provider instead of the real Win32 API.
    /// </summary>
    private static Func<List<DisplayInfo>>? _displayProvider;

    /// <summary>
    /// Injectable detail provider for tests that need to control DPI/handle values
    /// consumed by <see cref="DisplayDpiResolver"/>.
    /// </summary>
    private static Func<List<DisplayDetail>>? _displayDetailProvider;

    private static Func<WindowInfo?>? _activeWindowProvider;
    private static Func<bool, bool, List<WindowInfo>>? _windowProvider;

    public static void SetDisplayProvider(Func<List<DisplayInfo>>? provider) => _displayProvider = provider;
    internal static void SetDisplayDetailProvider(Func<List<DisplayDetail>>? provider) => _displayDetailProvider = provider;
    public static void SetActiveWindowProvider(Func<WindowInfo?>? provider) => _activeWindowProvider = provider;
    public static void SetWindowProvider(Func<bool, bool, List<WindowInfo>>? provider) => _windowProvider = provider;

    public static List<DisplayInfo> EnumDisplays()
    {
        if (_displayProvider != null)
            return _displayProvider();

        return EnumDisplayDetails().Select(d => new DisplayInfo(d.id, d.name, d.is_primary, d.bounds, d.scale_factor)).ToList();
    }

    /// <summary>
    /// Internal display enumeration that includes effective DPI and monitor handle.
    /// Tests can inject <see cref="DisplayDetail"/> values via <see cref="SetDisplayDetailProvider"/>.
    /// </summary>
    internal static List<DisplayDetail> EnumDisplayDetails()
    {
        if (_displayDetailProvider != null)
            return _displayDetailProvider();

        if (_displayProvider != null)
        {
            return _displayProvider()
                .Select(d => new DisplayDetail(d.id, d.name, d.is_primary, d.bounds, d.scale_factor, 96, 96, IntPtr.Zero))
                .ToList();
        }

        var list = new List<DisplayDetail>();
        int idx = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                idx++;
                bool primary = (mi.dwFlags & 1) != 0;
                var r = mi.rcMonitor;
                int w = r.right - r.left;
                int h = r.bottom - r.top;
                double scale = 1.0;
                int dpiX = 96;
                int dpiY = 96;
                try
                {
                    if (GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out uint dy) == 0)
                    {
                        dpiX = (int)dx;
                        dpiY = (int)dy;
                        if (w > 0)
                            scale = dpiX / 96.0;
                    }
                }
                catch { }
                list.Add(new DisplayDetail(
                    $"display_{idx}", $"Display {idx}", primary,
                    new Bounds(r.left, r.top, w, h), scale, dpiX, dpiY, hMon));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Returns the union of all display bounds (virtual screen).
    /// Uses the injectable display provider when set for test stability.
    /// </summary>
    public static Bounds VirtualScreenBounds()
    {
        var displays = EnumDisplays();
        if (displays.Count == 0)
            return new Bounds(0, 0, 0, 0);

        int minX = displays[0].bounds.x;
        int minY = displays[0].bounds.y;
        int maxRight = displays[0].bounds.x + displays[0].bounds.width;
        int maxBottom = displays[0].bounds.y + displays[0].bounds.height;

        foreach (var d in displays.Skip(1))
        {
            var b = d.bounds;
            if (b.x < minX) minX = b.x;
            if (b.y < minY) minY = b.y;
            int right = b.x + b.width;
            int bottom = b.y + b.height;
            if (right > maxRight) maxRight = right;
            if (bottom > maxBottom) maxBottom = bottom;
        }

        return new Bounds(minX, minY, maxRight - minX, maxBottom - minY);
    }

    public static List<WindowInfo> EnumWindows(bool includeMinimized, bool includeSystem)
    {
        if (_windowProvider != null)
            return _windowProvider(includeMinimized, includeSystem);

        var list = new List<WindowInfo>();
        var fg = GetForegroundWindow();
        EnumWindowsApi((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) && !includeMinimized) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            bool min = IsIconic(hWnd);
            if (min && !includeMinimized) return true;

            GetWindowThreadProcessId(hWnd, out int pid);
            string app = "";
            try { app = Process.GetProcessById(pid).ProcessName + ".exe"; } catch { }
            if (!includeSystem && app is "TextInputHost.exe" or "ApplicationFrameHost.exe" && title.Length < 2)
                return true;

            var bounds = TryGetVisibleWindowBounds(hWnd);
            list.Add(new WindowInfo(
                $"window_{hWnd.ToInt64()}", title, app, pid,
                hWnd == fg, min, bounds));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Attempts to get the visible client-area bounds of a window.
    /// Prefers DWM extended frame bounds (excludes invisible resize borders),
    /// falling back to GetWindowRect if DWM is unavailable.
    /// </summary>
    private static Bounds TryGetVisibleWindowBounds(IntPtr hWnd)
    {
        try
        {
            var hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT dwmRect, Marshal.SizeOf<RECT>());
            if (hr == 0)
            {
                int w = dwmRect.right - dwmRect.left;
                int h = dwmRect.bottom - dwmRect.top;
                if (w > 0 && h > 0)
                    return new Bounds(dwmRect.left, dwmRect.top, w, h);
            }
        }
        catch { }

        GetWindowRect(hWnd, out var r);
        return new Bounds(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    public static WindowInfo? ActiveWindow()
    {
        if (_activeWindowProvider != null)
            return _activeWindowProvider();

        var fg = GetForegroundWindow();
        return EnumWindows(false, false).FirstOrDefault(w => w.id == $"window_{fg.ToInt64()}");
    }

    public static List<AudioDevice> AudioInputs() => new()
    {
        new AudioDevice("mic_default", "Default Microphone", true, "active")
    };

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")] private static extern bool EnumWindowsApi(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
