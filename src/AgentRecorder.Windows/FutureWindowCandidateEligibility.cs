using System.Runtime.InteropServices;

namespace AgentRecorder.Windows;

/// <summary>
/// Non-sensitive structural evidence used to decide whether a top-level HWND
/// is an independently selectable, user-facing content surface. Window text is
/// never retained; only its presence is observed.
/// </summary>
internal sealed record FutureWindowWindowCandidate(
    nint WindowHandle,
    int ProcessId,
    bool IsVisible,
    bool IsIconic,
    bool IsRoot,
    bool HasOwner,
    bool IsToolWindow,
    bool DoesNotActivate,
    bool HasTitle,
    int X,
    int Y,
    int Width,
    int Height,
    bool IntersectsActiveDisplay)
{
    internal string? IneligibilityReasonCode => FutureWindowCandidateEligibility.GetReasonCode(this);
    internal bool IsEligible => IneligibilityReasonCode is null;
}

internal sealed record FutureWindowCandidateDiagnosticResult(
    IReadOnlyList<FutureWindowWindowCandidate> Candidates,
    int ScannedWindowCount,
    bool Truncated,
    int InspectionFailureCount,
    string FailureCode,
    int NativeErrorCode,
    bool EnumerationSucceeded);

/// <summary>
/// A structural, auditable rule shared by public window enumeration and the
/// future-window security validator. It intentionally excludes owned/tool or
/// non-activating implementation windows, and requires a titled, non-empty
/// surface intersecting an active monitor so an agent can select the same
/// user-facing content surface that the backend will validate.
/// </summary>
internal static class FutureWindowCandidateEligibility
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const uint MonitorDefaultToNull = 0;

    internal static string? GetReasonCode(FutureWindowWindowCandidate candidate)
    {
        if (candidate.WindowHandle == nint.Zero || candidate.ProcessId <= 0)
            return "window_candidate_identity_unavailable";
        if (!candidate.IsVisible) return "window_not_visible";
        if (candidate.IsIconic) return "window_minimized";
        if (!candidate.IsRoot) return "window_not_root";
        if (candidate.HasOwner) return "window_owned_auxiliary";
        if (candidate.IsToolWindow) return "window_tool_window";
        if (candidate.DoesNotActivate) return "window_nonactivating_transient";
        if (!candidate.HasTitle) return "window_title_missing";
        if (candidate.Width <= 0 || candidate.Height <= 0) return "window_zero_area";
        if (!candidate.IntersectsActiveDisplay) return "window_off_surface";
        return null;
    }

    internal static bool TryDescribe(nint window, out FutureWindowWindowCandidate candidate)
    {
        candidate = new FutureWindowWindowCandidate(
            window, 0, false, false, false, false, false, false, false,
            0, 0, 0, 0, false);
        try
        {
            if (!OperatingSystem.IsWindows() || window == nint.Zero || !IsWindow(window))
                return false;

            _ = GetWindowThreadProcessId(window, out var rawPid);
            if (rawPid == 0 || rawPid > int.MaxValue) return false;

            var exStyle = IntPtr.Size == 8
                ? GetWindowLongPtr64(window, GwlExStyle).ToInt64()
                : GetWindowLong32(window, GwlExStyle);
            if (!TryGetVisibleBounds(window, out var bounds)) return false;

            var rectangle = new NativeRect
            {
                Left = bounds.X,
                Top = bounds.Y,
                Right = unchecked(bounds.X + bounds.Width),
                Bottom = unchecked(bounds.Y + bounds.Height)
            };
            var hasOwner = GetWindow(window, GwOwner) != nint.Zero;
            var root = GetAncestor(window, GaRoot) == window;
            candidate = new FutureWindowWindowCandidate(
                window,
                checked((int)rawPid),
                IsWindowVisible(window),
                IsIconic(window),
                root,
                hasOwner,
                (exStyle & WsExToolWindow) != 0,
                (exStyle & WsExNoActivate) != 0,
                GetWindowTextLength(window) > 0,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                MonitorFromRect(ref rectangle, MonitorDefaultToNull) != nint.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetVisibleBounds(nint window, out Bounds bounds)
    {
        if (DwmGetWindowAttribute(window, 9, out var extended, Marshal.SizeOf<NativeRect>()) == 0)
        {
            var width = extended.Right - extended.Left;
            var height = extended.Bottom - extended.Top;
            if (width > 0 && height > 0)
            {
                bounds = new Bounds(extended.Left, extended.Top, width, height);
                return true;
            }
        }

        if (!GetWindowRect(window, out var rect))
        {
            bounds = default;
            return false;
        }
        bounds = new Bounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }

    internal readonly record struct Bounds(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint window);

    private static int GetWindowTextLength(nint window) => GetWindowTextLengthW(window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);
}
