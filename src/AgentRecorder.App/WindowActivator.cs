using System;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Abstraction over the small set of Win32 calls needed to bring the region
/// selection overlay to the foreground without coupling the form to Native directly.
/// </summary>
public interface IWindowActivator
{
    /// <summary>Places the window in the HWND_TOPMOST z-order slot.</summary>
    bool SetTopMost(IntPtr hWnd);

    /// <summary>Attempts to make the window the foreground window.</summary>
    bool SetForeground(IntPtr hWnd);

    /// <summary>Brings the window to the top of the z-order for its thread.</summary>
    bool BringToTop(IntPtr hWnd);

    /// <summary>Returns the handle of the current foreground window.</summary>
    IntPtr GetForegroundWindow();
}

/// <summary>
/// Default Win32 implementation of <see cref="IWindowActivator"/>.
/// </summary>
public sealed class DefaultWindowActivator : IWindowActivator
{
    public static DefaultWindowActivator Instance { get; } = new();

    public bool SetTopMost(IntPtr hWnd)
    {
        return Native.SetWindowPos(
            hWnd,
            Native.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }

    public bool SetForeground(IntPtr hWnd)
    {
        return Native.SetForegroundWindow(hWnd);
    }

    public bool BringToTop(IntPtr hWnd)
    {
        return Native.BringWindowToTop(hWnd);
    }

    public IntPtr GetForegroundWindow()
    {
        return Native.GetForegroundWindow();
    }
}
