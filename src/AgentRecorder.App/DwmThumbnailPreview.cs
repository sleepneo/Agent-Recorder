using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Disposable seam for a compositor-owned DWM thumbnail. The application never
/// receives source pixels from this abstraction.
/// </summary>
internal interface IDwmThumbnailProvider
{
    bool TryRegister(nint destinationWindow, nint sourceWindow, out IDwmThumbnail thumbnail);
}

internal interface IDwmThumbnail : IDisposable
{
    bool TryQuerySourceSize(out Size sourceSize);
    bool TryUpdateDestination(Rectangle destination, bool sourceClientAreaOnly);
}

/// <summary>
/// Pure geometry for DWM thumbnail destinations. Both the input panel and the
/// result are rectangles in the destination top-level form's client area,
/// whose WinForms coordinates are already device pixels. The source is fitted
/// and centered without any DPI conversion. Negative origins are valid for a
/// pure geometry seam and are retained as ordinary rectangle coordinates.
/// </summary>
internal static class DwmThumbnailGeometry
{
    public static Rectangle Fit(Rectangle panelClient, Size sourceSize)
    {
        if (panelClient.Width <= 0 || panelClient.Height <= 0 ||
            sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return Rectangle.Empty;

        double scale = Math.Min(
            (double)panelClient.Width / sourceSize.Width,
            (double)panelClient.Height / sourceSize.Height);
        int width = Math.Clamp(
            (int)Math.Round(sourceSize.Width * scale, MidpointRounding.AwayFromZero),
            1,
            panelClient.Width);
        int height = Math.Clamp(
            (int)Math.Round(sourceSize.Height * scale, MidpointRounding.AwayFromZero),
            1,
            panelClient.Height);
        int x = panelClient.X + (panelClient.Width - width) / 2;
        int y = panelClient.Y + (panelClient.Height - height) / 2;
        return new Rectangle(x, y, width, height);
    }
}

/// <summary>
/// Maps a child HWND's client area into the top-level DWM destination HWND and
/// intersects it with every ancestor client area. DWM thumbnails are composed
/// into the destination window, not clipped by the child preview control, so
/// the mapped rectangle must already be restricted to the actually visible
/// preview viewport.
/// </summary>
internal static class DwmThumbnailDestinationGeometry
{
    public static bool TryGetVisibleClientBounds(
        Control child,
        Control destination,
        out Rectangle visibleBounds)
    {
        visibleBounds = Rectangle.Empty;
        if (child == null || destination == null || child.IsDisposed || destination.IsDisposed ||
            !child.Visible || !destination.IsHandleCreated || !child.IsHandleCreated)
            return false;

        try
        {
            if (!TryMapClientRectangle(child, destination, out visibleBounds))
                return false;

            bool reachedDestination = ReferenceEquals(child, destination);
            for (Control? ancestor = child.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!ancestor.Visible || !ancestor.IsHandleCreated ||
                    !TryMapClientRectangle(ancestor, destination, out var ancestorClient))
                {
                    visibleBounds = Rectangle.Empty;
                    return false;
                }

                visibleBounds = Rectangle.Intersect(visibleBounds, ancestorClient);
                if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
                {
                    visibleBounds = Rectangle.Empty;
                    return false;
                }

                if (ReferenceEquals(ancestor, destination))
                {
                    reachedDestination = true;
                    break;
                }
            }

            return reachedDestination && visibleBounds.Width > 0 && visibleBounds.Height > 0;
        }
        catch
        {
            visibleBounds = Rectangle.Empty;
            return false;
        }
    }

    private static bool TryMapClientRectangle(Control from, Control to, out Rectangle mapped)
    {
        mapped = Rectangle.Empty;
        if (!from.IsHandleCreated || !to.IsHandleCreated ||
            !GetClientRect(from.Handle, out var nativeRect) ||
            nativeRect.right <= nativeRect.left || nativeRect.bottom <= nativeRect.top)
            return false;

        var topLeft = new NativePoint(nativeRect.left, nativeRect.top);
        var bottomRight = new NativePoint(nativeRect.right, nativeRect.bottom);
        if (!TryMapPoint(from.Handle, to.Handle, ref topLeft) ||
            !TryMapPoint(from.Handle, to.Handle, ref bottomRight))
            return false;

        mapped = Rectangle.FromLTRB(
            Math.Min(topLeft.x, bottomRight.x),
            Math.Min(topLeft.y, bottomRight.y),
            Math.Max(topLeft.x, bottomRight.x),
            Math.Max(topLeft.y, bottomRight.y));
        return mapped.Width > 0 && mapped.Height > 0;
    }

    private static bool TryMapPoint(nint from, nint to, ref NativePoint point)
    {
        Marshal.SetLastPInvokeError(0);
        _ = MapWindowPoints(from, to, ref point, 1);
        return Marshal.GetLastPInvokeError() == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int x;
        public int y;

        public NativePoint(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int MapWindowPoints(nint hWndFrom, nint hWndTo, ref NativePoint points, uint pointCount);
}

internal sealed class DwmThumbnailProvider : IDwmThumbnailProvider
{
    public bool TryRegister(nint destinationWindow, nint sourceWindow, out IDwmThumbnail thumbnail)
    {
        thumbnail = null!;
        if (destinationWindow == nint.Zero || sourceWindow == nint.Zero)
            return false;

        try
        {
            int hr = DwmRegisterThumbnail(destinationWindow, sourceWindow, out var handle);
            if (hr < 0 || handle == nint.Zero)
                return false;

            thumbnail = new DwmThumbnailHandle(handle);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Win32Exception) { return false; }
        catch { return false; }
    }

    private sealed class DwmThumbnailHandle : IDwmThumbnail
    {
        private nint _handle;

        public DwmThumbnailHandle(nint handle) => _handle = handle;

        public bool TryQuerySourceSize(out Size sourceSize)
        {
            sourceSize = Size.Empty;
            var handle = _handle;
            if (handle == nint.Zero)
                return false;

            try
            {
                int hr = DwmQueryThumbnailSourceSize(handle, out var nativeSize);
                if (hr < 0 || nativeSize.cx <= 0 || nativeSize.cy <= 0)
                    return false;
                sourceSize = new Size(nativeSize.cx, nativeSize.cy);
                return true;
            }
            catch { return false; }
        }

        public bool TryUpdateDestination(Rectangle destination, bool sourceClientAreaOnly)
        {
            var handle = _handle;
            if (handle == nint.Zero || destination.Width <= 0 || destination.Height <= 0)
                return false;

            try
            {
                var props = new DwmThumbnailProperties
                {
                    dwFlags = DwmThumbnailPropertiesFlags.RectDestination |
                              DwmThumbnailPropertiesFlags.Visible |
                              DwmThumbnailPropertiesFlags.Opacity |
                              (sourceClientAreaOnly
                                  ? DwmThumbnailPropertiesFlags.SourceClientAreaOnly
                                  : 0),
                    rcDestination = new NativeRect(destination),
                    opacity = 255,
                    fVisible = true,
                    // The native WGC window helper uses CreateForWindow and
                    // item.Size(), after validating GetWindowRect. Keep the
                    // DWM source range at the full window surface as well.
                    fSourceClientAreaOnly = sourceClientAreaOnly
                };
                return DwmUpdateThumbnailProperties(handle, ref props) >= 0;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            var handle = System.Threading.Interlocked.Exchange(ref _handle, nint.Zero);
            if (handle == nint.Zero)
                return;

            try { DwmUnregisterThumbnail(handle); }
            catch { }
        }
    }

    [Flags]
    private enum DwmThumbnailPropertiesFlags : int
    {
        RectDestination = 0x00000001,
        Opacity = 0x00000004,
        Visible = 0x00000008,
        SourceClientAreaOnly = 0x00000010
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public NativeRect(Rectangle rectangle)
        {
            left = rectangle.Left;
            top = rectangle.Top;
            right = rectangle.Right;
            bottom = rectangle.Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public DwmThumbnailPropertiesFlags dwFlags;
        public NativeRect rcDestination;
        public NativeRect rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmRegisterThumbnail(nint hwndDestination, nint hwndSource, out nint hThumbnailId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUpdateThumbnailProperties(nint hThumbnailId, ref DwmThumbnailProperties props);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmQueryThumbnailSourceSize(nint hThumbnailId, out NativeSize size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUnregisterThumbnail(nint hThumbnailId);
}
