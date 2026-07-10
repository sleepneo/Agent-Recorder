using System;
using System.Runtime.InteropServices;

namespace AgentRecorder.Windows;

/// <summary>
/// Low-level Win32 API bindings for system operations.
/// </summary>
public static class Native
{
    public const int WM_CLOSE = 0x0010;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    /// <summary>
    /// Get the current process session ID.
    /// </summary>
    public static int GetCurrentSessionId()
    {
        int sessionId;
        if (ProcessIdToSessionId(Environment.ProcessId, out sessionId))
            return sessionId;
        return 0; // Default to session 0 if failed
    }
}
