using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
namespace AgentRecorder.Windows;

public static class SystemQuery
{
    public record Bounds(int x, int y, int width, int height);

    /// <summary>
    /// Public display information returned by <see cref="EnumDisplays"/>. This is the
    /// public API contract for <c>GET /api/v1/displays</c>; its ordinal ID is
    /// intentionally not a topology-stable identity.
    /// </summary>
    public record DisplayInfo(string id, string name, bool is_primary, Bounds bounds, double scale_factor);

    /// <summary>
    /// One active-display topology entry. <see cref="id"/> remains the public
    /// ordinal used by the API. <see cref="stable_identity"/> is an internal,
    /// fixed-format fingerprint used only for approval binding and revalidation;
    /// it must never be serialized as a display API DTO.
    /// </summary>
    public record DisplayTopologyInfo(
        string id,
        string name,
        bool is_primary,
        Bounds bounds,
        double scale_factor,
        string? stable_identity,
        DisplayIdentityResolutionStatus identity_status);

    /// <summary>
    /// Internal display information used by the floating stop-control layout logic.
    /// Contains the effective DPI and monitor handle needed for PerMonitorV2 sizing.
    /// </summary>
    internal record DisplayDetail(string id, string name, bool is_primary, Bounds bounds, double scale_factor, int dpiX, int dpiY, IntPtr handle);

    public record WindowInfo(string id, string title, string app_name, int process_id, bool is_active, bool is_minimized, Bounds bounds);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// Injectable display provider for testing. When set, EnumDisplays() returns
    /// displays from this provider instead of the real Win32 API.
    /// </summary>
    // Test overrides are scoped to the current async execution context. With
    // no override configured these values are null and the production Win32
    // enumeration path is unchanged. AsyncLocal prevents an unrelated test
    // from observing a provider installed by a sibling test while preserving
    // the intended flow into child tasks/UI work created by that test.
    private static readonly AsyncLocal<Func<List<DisplayInfo>>?> _displayProvider = new();

    /// <summary>
    /// Injectable complete-topology provider for identity and same-snapshot
    /// tests. Production callers use the active Win32 topology reader below.
    /// </summary>
    private static readonly AsyncLocal<Func<List<DisplayTopologyInfo>>?> _displayTopologyProvider = new();

    /// <summary>
    /// Injectable detail provider for tests that need to control DPI/handle values
    /// consumed by <see cref="DisplayDpiResolver"/>.
    /// </summary>
    private static readonly AsyncLocal<Func<List<DisplayDetail>>?> _displayDetailProvider = new();

    private static readonly AsyncLocal<Func<WindowInfo?>?> _activeWindowProvider = new();
    private static readonly AsyncLocal<Func<bool, bool, List<WindowInfo>>?> _windowProvider = new();

    public static void SetDisplayProvider(Func<List<DisplayInfo>>? provider) => _displayProvider.Value = provider;
    public static void SetDisplayTopologyProvider(Func<List<DisplayTopologyInfo>>? provider)
        => _displayTopologyProvider.Value = provider;
    internal static void SetDisplayDetailProvider(Func<List<DisplayDetail>>? provider) => _displayDetailProvider.Value = provider;
    public static void SetActiveWindowProvider(Func<WindowInfo?>? provider) => _activeWindowProvider.Value = provider;
    public static void SetWindowProvider(Func<bool, bool, List<WindowInfo>>? provider) => _windowProvider.Value = provider;

    public static List<DisplayInfo> EnumDisplays()
    {
        var displayProvider = _displayProvider.Value;
        if (displayProvider != null)
            return displayProvider();

        var topologyProvider = _displayTopologyProvider.Value;
        if (topologyProvider != null)
            return topologyProvider()
                .Select(d => new DisplayInfo(d.id, d.name, d.is_primary, d.bounds, d.scale_factor))
                .ToList();

        // The public metadata endpoint and virtual-screen helpers do not need
        // internal identity material. Keep them on the lightweight monitor
        // enumeration; the complete active topology reader is used by the
        // parser and approval barrier.
        return EnumDisplayDetails()
            .Select(d => new DisplayInfo(d.id, d.name, d.is_primary, d.bounds, d.scale_factor))
            .ToList();
    }

    /// <summary>
    /// Reads public ID, physical bounds, and internal identity status from one
    /// active-monitor enumeration. The production path binds all three values
    /// to the same MONITORINFOEX source entry before returning the snapshot.
    /// </summary>
    public static List<DisplayTopologyInfo> EnumDisplayTopology()
    {
        var topologyProvider = _displayTopologyProvider.Value;
        if (topologyProvider != null)
            return topologyProvider();

        var displayProvider = _displayProvider.Value;
        if (displayProvider != null)
        {
            return displayProvider()
                .Select(d => new DisplayTopologyInfo(
                    d.id,
                    d.name,
                    d.is_primary,
                    d.bounds,
                    d.scale_factor,
                    null,
                    DisplayIdentityResolutionStatus.Unresolved))
                .ToList();
        }

        var mappingsAvailable = TryReadActiveDisplayConfigMappings(
            out var mappings,
            out var deviceInfoFailure);
        var list = new List<DisplayTopologyInfo>();
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
                try
                {
                    if (GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _ ) == 0 && w > 0)
                        scale = dx / 96.0;
                }
                catch { }

                var identity = !mappingsAvailable
                    ? new DisplayIdentityResolution(null, DisplayIdentityResolutionStatus.Unresolved)
                    : deviceInfoFailure
                        ? new DisplayIdentityResolution(null, DisplayIdentityResolutionStatus.Unavailable)
                        : DisplayIdentityDeriver.Resolve(mi.szDevice, mappings);
                list.Add(new DisplayTopologyInfo(
                    $"display_{idx}",
                    $"Display {idx}",
                    primary,
                    new Bounds(r.left, r.top, w, h),
                    scale,
                    identity.Fingerprint,
                    identity.Status));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Internal display enumeration that includes effective DPI and monitor handle.
    /// Tests can inject <see cref="DisplayDetail"/> values via <see cref="SetDisplayDetailProvider"/>.
    /// </summary>
    internal static List<DisplayDetail> EnumDisplayDetails()
    {
        var displayDetailProvider = _displayDetailProvider.Value;
        if (displayDetailProvider != null)
            return displayDetailProvider();

        var topologyProvider = _displayTopologyProvider.Value;
        if (topologyProvider != null)
        {
            return topologyProvider()
                .Select(d => new DisplayDetail(d.id, d.name, d.is_primary, d.bounds, d.scale_factor, 96, 96, IntPtr.Zero))
                .ToList();
        }

        var displayProvider = _displayProvider.Value;
        if (displayProvider != null)
        {
            return displayProvider()
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
        var windowProvider = _windowProvider.Value;
        if (windowProvider != null)
            return windowProvider(includeMinimized, includeSystem);

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
        var activeWindowProvider = _activeWindowProvider.Value;
        if (activeWindowProvider != null)
            return activeWindowProvider();

        var fg = GetForegroundWindow();
        return EnumWindows(false, false).FirstOrDefault(w => w.id == $"window_{fg.ToInt64()}");
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    private const uint DISPLAYCONFIG_TARGET_IN_USE = 0x00000001;
    internal const int DISPLAYCONFIG_MODE_INFO_ABI_SIZE = 64;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        // Preserve the SDK field's four-byte slot without exposing or using it
        // as a path identity. The path target ABI above intentionally has no
        // connector-instance field.
        public uint targetNameReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    private static bool TryReadActiveDisplayConfigMappings(
        out IReadOnlyList<DisplayTargetMapping> mappings,
        out bool deviceInfoFailure)
    {
        mappings = Array.Empty<DisplayTargetMapping>();
        deviceInfoFailure = false;
        try
        {
            uint pathCount = 0;
            uint modeCount = 0;
            int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (result != 0 || pathCount == 0)
                return false;

            var paths = new DISPLAYCONFIG_PATH_INFO[checked((int)pathCount)];
            // DISPLAYCONFIG_MODE_INFO is a 64-byte native union on the
            // supported Windows ABI. It is intentionally not represented by
            // a managed 80-byte declaration because this reader does not need
            // to inspect mode contents.
            int modeBytes = checked((int)modeCount * DISPLAYCONFIG_MODE_INFO_ABI_SIZE);
            var modeBuffer = Marshal.AllocHGlobal(Math.Max(modeBytes, 1));
            try
            {
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths,
                    ref modeCount, modeBuffer, IntPtr.Zero);
                if (result != 0)
                    return false;
                if (pathCount > (uint)paths.Length)
                    return false;
            }
            finally
            {
                Marshal.FreeHGlobal(modeBuffer);
            }

            var resultMappings = new List<DisplayTargetMapping>();
            int returnedPathCount = checked((int)pathCount);
            for (int i = 0; i < returnedPathCount; i++)
            {
                var path = paths[i];
                bool pathActive = (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
                bool targetAvailable = path.targetInfo.targetAvailable != 0;
                bool targetInUse = (path.targetInfo.statusFlags & DISPLAYCONFIG_TARGET_IN_USE) != 0;

                if (!TryGetSourceName(path.sourceInfo, out var sourceName))
                {
                    // Without the source name this path cannot be associated
                    // with a MONITORINFOEX entry. Fail closed for the whole
                    // snapshot instead of deriving a clone fingerprint from
                    // only the paths whose device-info calls happened to work.
                    deviceInfoFailure = true;
                    continue;
                }

                if (!TryGetTargetPath(path.targetInfo, out var targetPath))
                {
                    deviceInfoFailure = true;
                    resultMappings.Add(new DisplayTargetMapping(
                        sourceName,
                        null,
                        pathActive,
                        targetAvailable,
                        targetInUse,
                        SourceDeviceInfoAvailable: true,
                        TargetDeviceInfoAvailable: false));
                    continue;
                }

                resultMappings.Add(new DisplayTargetMapping(
                    sourceName,
                    targetPath,
                    pathActive,
                    targetAvailable,
                    targetInUse));
            }

            mappings = resultMappings;
            return true;
        }
        catch
        {
            mappings = Array.Empty<DisplayTargetMapping>();
            return false;
        }
    }

    private static bool TryGetSourceName(DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo, out string sourceName)
    {
        sourceName = string.Empty;
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        try
        {
            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = sourceInfo.adapterId,
                    id = sourceInfo.id
                },
                viewGdiDeviceName = string.Empty
            };
            Marshal.StructureToPtr(request, buffer, false);
            if (DisplayConfigGetDeviceInfo(buffer) != 0)
                return false;
            var result = Marshal.PtrToStructure<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(buffer);
            sourceName = result.viewGdiDeviceName ?? string.Empty;
            return !string.IsNullOrWhiteSpace(sourceName);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTargetPath(DISPLAYCONFIG_PATH_TARGET_INFO targetInfo, out string targetPath)
    {
        targetPath = string.Empty;
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>());
        try
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = targetInfo.adapterId,
                    id = targetInfo.id
                },
                monitorFriendlyDeviceName = string.Empty,
                monitorDevicePath = string.Empty
            };
            Marshal.StructureToPtr(request, buffer, false);
            if (DisplayConfigGetDeviceInfo(buffer) != 0)
                return false;
            var result = Marshal.PtrToStructure<DISPLAYCONFIG_TARGET_DEVICE_NAME>(buffer);
            targetPath = result.monitorDevicePath ?? string.Empty;
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);
}
