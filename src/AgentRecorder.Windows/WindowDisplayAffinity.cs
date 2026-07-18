using System;

namespace AgentRecorder.Windows;

/// <summary>
/// A narrow seam for setting the display-affinity of windows owned by this process.
/// Abstracts <c>SetWindowDisplayAffinity</c> so success, failure and exception paths
/// can be unit-tested without relying on a real desktop capture environment.
/// </summary>
public interface IWindowDisplayAffinity
{
    /// <summary>
    /// Requests that the specified window be excluded from screen capture.
    /// Returns <c>true</c> when the API reports success; <c>false</c> otherwise.
    /// </summary>
    bool SetExcludeFromCapture(IntPtr hWnd);

    /// <summary>
    /// Reads the current display affinity of the specified window.
    /// </summary>
    bool GetAffinity(IntPtr hWnd, out uint affinity);
}

/// <summary>
/// Production implementation that forwards to the Win32 API.
/// </summary>
public sealed class WindowDisplayAffinity : IWindowDisplayAffinity
{
    public static readonly IWindowDisplayAffinity Instance = new WindowDisplayAffinity();

    private readonly Func<IntPtr, uint, bool>? _setHook;
    private readonly Func<IntPtr, (bool ok, uint affinity)>? _getHook;

    public WindowDisplayAffinity()
    {
    }

    internal WindowDisplayAffinity(
        Func<IntPtr, uint, bool>? setHook,
        Func<IntPtr, (bool ok, uint affinity)>? getHook)
    {
        _setHook = setHook;
        _getHook = getHook;
    }

    public bool SetExcludeFromCapture(IntPtr hWnd)
    {
        if (_setHook != null)
            return _setHook(hWnd, Native.WDA_EXCLUDEFROMCAPTURE);

        return Native.SetWindowDisplayAffinity(hWnd, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public bool GetAffinity(IntPtr hWnd, out uint affinity)
    {
        if (_getHook != null)
        {
            var result = _getHook(hWnd);
            affinity = result.affinity;
            return result.ok;
        }

        return Native.GetWindowDisplayAffinity(hWnd, out affinity);
    }
}
