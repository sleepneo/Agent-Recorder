using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace AgentRecorder.Windows;

public static class SystemQuery
{
    public record Bounds(int x, int y, int width, int height);
    public record DisplayInfo(string id, string name, bool is_primary, Bounds bounds, double scale_factor);
    public record WindowInfo(string id, string title, string app_name, int process_id, bool is_active, bool is_minimized, Bounds bounds);
    public record AudioDevice(string id, string name, bool is_default, string state);

    /// <summary>
    /// Injectable display provider for testing. When set, EnumDisplays() returns
    /// displays from this provider instead of the real Win32 API.
    /// </summary>
    private static Func<List<DisplayInfo>>? _displayProvider;

    /// <summary>
    /// Set a custom display provider for testing purposes.
    /// Pass null to restore the default Win32-based implementation.
    /// </summary>
    public static void SetDisplayProvider(Func<List<DisplayInfo>>? provider)
    {
        _displayProvider = provider;
    }

    public static List<DisplayInfo> EnumDisplays()
    {
        if (_displayProvider != null)
            return _displayProvider();

        var list = new List<DisplayInfo>();
        int idx = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                idx++;
                bool primary = (mi.dwFlags & 1) != 0;
                var r = mi.rcMonitor;
                list.Add(new DisplayInfo(
                    $"display_{idx}", $"Display {idx}", primary,
                    new Bounds(r.left, r.top, r.right - r.left, r.bottom - r.top), 1.0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<WindowInfo> EnumWindows(bool includeMinimized, bool includeSystem)
    {
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

            GetWindowRect(hWnd, out var r);
            list.Add(new WindowInfo(
                $"window_{hWnd.ToInt64()}", title, app, pid,
                hWnd == fg, min,
                new Bounds(r.left, r.top, r.right - r.left, r.bottom - r.top)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? ActiveWindow()
    {
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
}
