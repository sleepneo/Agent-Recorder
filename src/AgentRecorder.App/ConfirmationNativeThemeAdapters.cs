using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgentRecorder.App;

internal interface IConfirmationNativeChromeAdapter : IDisposable
{
    bool Apply(IntPtr windowHandle, ConfirmationThemeKind themeKind);
}

internal interface IConfirmationScrollThemeAdapter : IDisposable
{
    bool Apply(Control scrollHost, ConfirmationThemeKind themeKind);
}

/// <summary>
/// Best-effort DWM adapter for the top-level confirmation non-client area.
/// Attribute 20 is used by newer Windows builds; attribute 19 is retained for
/// the Windows 10 1809/1903-era implementation. Both light and High Contrast
/// explicitly clear dark mode so a previous Dark application cannot leak.
/// </summary>
internal sealed class WindowsConfirmationNativeChromeAdapter : IConfirmationNativeChromeAdapter
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private bool _disposed;

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    internal static int RequestedDarkModeValue(ConfirmationThemeKind themeKind)
        => themeKind == ConfirmationThemeKind.Dark ? 1 : 0;

    public bool Apply(IntPtr windowHandle, ConfirmationThemeKind themeKind)
    {
        if (_disposed || windowHandle == IntPtr.Zero)
            return false;

        int value = RequestedDarkModeValue(themeKind);
        foreach (var attribute in new[] { DwmwaUseImmersiveDarkMode, DwmwaUseImmersiveDarkModeLegacy })
        {
            try
            {
                if (DwmSetWindowAttribute(windowHandle, attribute, ref value, sizeof(int)) == 0)
                    return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch
            {
                // DWM is optional for the confirmation experience. Continue
                // with the system default chrome if this call is unavailable.
                return false;
            }
        }

        return false;
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// Applies the Windows theme data only to the confirmation form's AutoScroll
/// host. This keeps the existing scrollbar, wheel and keyboard behavior while
/// preventing DarkMode from inheriting a bright Explorer track.
/// </summary>
internal sealed class WindowsConfirmationScrollThemeAdapter : IConfirmationScrollThemeAdapter
{
    private bool _disposed;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(
        IntPtr hwnd,
        string? subAppName,
        string? subIdList);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(
        IntPtr hWnd,
        IntPtr rect,
        bool erase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    internal static string? ThemeDataName(ConfirmationThemeKind themeKind) => themeKind switch
    {
        ConfirmationThemeKind.Dark => "DarkMode_Explorer",
        ConfirmationThemeKind.Light => "Explorer",
        _ => null
    };

    public bool Apply(Control scrollHost, ConfirmationThemeKind themeKind)
    {
        if (_disposed || scrollHost == null || scrollHost.IsDisposed || !scrollHost.IsHandleCreated)
            return false;

        try
        {
            int result = SetWindowTheme(scrollHost.Handle, ThemeDataName(themeKind), null);
            InvalidateRect(scrollHost.Handle, IntPtr.Zero, erase: true);
            UpdateWindow(scrollHost.Handle);
            return result == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _disposed = true;
}
