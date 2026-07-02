using System;
using System.Runtime.InteropServices;

namespace AgentRecorder.Windows;

/// <summary>
/// Low-level Win32 API bindings for system operations.
/// </summary>
public static class Native
{
    public const int WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

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
