using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Releaser seam for native icon handles created by <see cref="TrayIconFactory"/>.
/// </summary>
internal interface IIconNativeHandleReleaser
{
    void Release(IntPtr hIcon);
}

/// <summary>
/// Default Win32 implementation that calls <c>DestroyIcon</c>.
/// </summary>
internal sealed class Win32IconNativeHandleReleaser : IIconNativeHandleReleaser
{
    public void Release(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero)
        {
            try { Native.DestroyIcon(hIcon); }
            catch { }
        }
    }
}

/// <summary>
/// Generates and owns the NotifyIcon icons used by <see cref="TrayContext"/>.
/// Icons are created on demand and disposed when the factory is disposed.
/// </summary>
internal sealed class TrayIconFactory : IDisposable
{
    private readonly List<Bitmap> _bitmaps = new();
    private readonly IIconNativeHandleReleaser _handleReleaser;
    private Icon? _idleIcon;
    private Icon? _recordingIcon;
    private Icon? _stoppingIcon;
    private bool _disposed;

    public TrayIconFactory(IIconNativeHandleReleaser? handleReleaser = null)
    {
        _handleReleaser = handleReleaser ?? new Win32IconNativeHandleReleaser();
    }

    public Icon IdleIcon => _disposed
        ? throw new ObjectDisposedException(nameof(TrayIconFactory))
        : _idleIcon ??= CreateOwnedIcon(Color.DodgerBlue);

    public Icon RecordingIcon => _disposed
        ? throw new ObjectDisposedException(nameof(TrayIconFactory))
        : _recordingIcon ??= CreateOwnedIcon(Color.Red);

    public Icon StoppingIcon => _disposed
        ? throw new ObjectDisposedException(nameof(TrayIconFactory))
        : _stoppingIcon ??= CreateOwnedIcon(Color.Orange);

    /// <summary>
    /// Creates a managed-owned icon from a temporary native HICON handle,
    /// then immediately releases the temporary handle with <see cref="DestroyIcon"/>.
    /// </summary>
    private Icon CreateOwnedIcon(Color color)
    {
        IntPtr tempHandle = IntPtr.Zero;
        Bitmap? bitmap = null;
        Graphics? g = null;
        Brush? brush = null;

        try
        {
            bitmap = new Bitmap(16, 16);
            _bitmaps.Add(bitmap);

            g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);

            tempHandle = bitmap.GetHicon();
            // Icon.FromHandle does not own the native HICON. Clone it to obtain a managed-owned copy,
            // then destroy the temporary native handle ourselves.
            using (var tempIcon = Icon.FromHandle(tempHandle))
            {
                return (Icon)tempIcon.Clone();
            }
        }
        finally
        {
            brush?.Dispose();
            g?.Dispose();
            _handleReleaser.Release(tempHandle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _idleIcon?.Dispose();
        _recordingIcon?.Dispose();
        _stoppingIcon?.Dispose();
        _idleIcon = null;
        _recordingIcon = null;
        _stoppingIcon = null;

        foreach (var bitmap in _bitmaps)
        {
            try { bitmap.Dispose(); }
            catch { }
        }
        _bitmaps.Clear();
    }
}
