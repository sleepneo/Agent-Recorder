using System;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Abstraction over the Win32 hotkey registration so tests can substitute a fake registrar.
/// </summary>
internal interface IHotkeyRegistrar
{
    bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Default Win32 implementation of <see cref="IHotkeyRegistrar"/>.
/// </summary>
internal sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    public bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) =>
        Native.RegisterHotKey(hWnd, id, fsModifiers, vk);

    public bool UnregisterHotKey(IntPtr hWnd, int id) =>
        Native.UnregisterHotKey(hWnd, id);
}

/// <summary>
/// Minimal seam used by <see cref="TrayContext"/> so tests can substitute a fake hotkey
/// without creating a message-only <see cref="NativeWindow"/> or calling real Win32 APIs.
/// </summary>
internal interface IGlobalStopHotkey : IDisposable
{
    bool Registered { get; }
    bool Register(uint modifiers = GlobalStopHotkey.DefaultModifiers, uint key = GlobalStopHotkey.DefaultKey);
}

/// <summary>
/// Registers a process-global hotkey using a message-only window.
/// The default gesture is Ctrl+Shift+F10 and the semantic is "stop all active recordings".
/// Registration failures are captured in <see cref="Registered"/> and logged; they do not crash the app.
/// </summary>
internal class GlobalStopHotkey : IGlobalStopHotkey
{
    public const uint DefaultModifiers = Native.MOD_CONTROL | Native.MOD_SHIFT;
    public const uint DefaultKey = Native.VK_F10;
    public const int WM_HOTKEY = Native.WM_HOTKEY;

    private static int _nextId = 100;

    private readonly HotkeyMessageWindow _window;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Action _onPressed;
    private readonly Action<Exception>? _onError;
    private readonly int _hotkeyId;
    private bool _registered;
    private bool _disposed;

    public virtual bool Registered => _registered;
    public int HotkeyId => _hotkeyId;
    public bool IsDisposed => _disposed;

    public GlobalStopHotkey(Action onPressed, IHotkeyRegistrar? registrar = null, Action<Exception>? onError = null)
    {
        _onPressed = onPressed;
        _onError = onError;
        _registrar = registrar ?? new Win32HotkeyRegistrar();
        _hotkeyId = Interlocked.Increment(ref _nextId);
        _window = new HotkeyMessageWindow(this);
    }

    /// <summary>
    /// Registers the hotkey. Returns whether registration succeeded.
    /// </summary>
    public virtual bool Register(uint modifiers = DefaultModifiers, uint key = DefaultKey)
    {
        if (_registered || _disposed)
            return _registered;

        _registered = _registrar.RegisterHotKey(_window.Handle, _hotkeyId, modifiers, key);
        return _registered;
    }

    internal void OnHotkeyReceived()
    {
        if (_disposed)
            return;

        try
        {
            _onPressed?.Invoke();
        }
        catch (Exception ex)
        {
            try
            {
                _onError?.Invoke(ex);
            }
            catch
            {
                // The error reporter itself failed. Do not let this secondary exception
                // escape the message-only window's WndProc and break the message loop.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_registered)
        {
            try { _registrar.UnregisterHotKey(_window.Handle, _hotkeyId); }
            catch { }
            _registered = false;
        }

        try { _window.DestroyHandle(); }
        catch { }
    }

    /// <summary>
    /// Message-only window that receives WM_HOTKEY without appearing on the desktop or taskbar.
    /// Filters by hotkey id, registration state and disposed state before notifying the owner.
    /// </summary>
    private sealed class HotkeyMessageWindow : NativeWindow
    {
        private readonly GlobalStopHotkey _owner;

        public HotkeyMessageWindow(GlobalStopHotkey owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                ExStyle = 0,
                Parent = Native.HWND_MESSAGE
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY
                && _owner.Registered
                && !_owner.IsDisposed
                && m.WParam.ToInt32() == _owner.HotkeyId)
            {
                _owner.OnHotkeyReceived();
            }
            base.WndProc(ref m);
        }
    }
}
