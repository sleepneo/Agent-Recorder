using System;
using System.Runtime.InteropServices;
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
    int LastErrorCode => 0;
    bool Register();
    bool Unregister();
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
    private readonly uint _registrationKey;
    private bool _registered;
    private bool _disposed;
    private bool _unregisterFailurePending;
    private int _lastErrorCode;

    public virtual bool Registered => _registered;
    public int HotkeyId => _hotkeyId;
    public bool IsDisposed => _disposed;
    public int LastErrorCode => _lastErrorCode;

    protected virtual uint RegistrationKey => _registrationKey;

    public GlobalStopHotkey(Action onPressed, IHotkeyRegistrar? registrar = null, Action<Exception>? onError = null)
        : this(onPressed, registrar, onError, DefaultKey)
    {
    }

    protected GlobalStopHotkey(Action onPressed, IHotkeyRegistrar? registrar, Action<Exception>? onError, uint registrationKey)
    {
        _onPressed = onPressed;
        _onError = onError;
        _registrar = registrar ?? new Win32HotkeyRegistrar();
        _hotkeyId = Interlocked.Increment(ref _nextId);
        _registrationKey = registrationKey;
        _window = new HotkeyMessageWindow(this);
    }

    /// <summary>
    /// Registers the hotkey. Returns whether registration succeeded.
    /// </summary>
    public virtual bool Register(uint modifiers = DefaultModifiers, uint key = DefaultKey)
    {
        if (_registered || _disposed)
            return _registered;

        try
        {
            _registered = _registrar.RegisterHotKey(_window.Handle, _hotkeyId, modifiers, key);
            _lastErrorCode = _registered ? 0 : Marshal.GetLastWin32Error();
        }
        catch (Exception)
        {
            _registered = false;
            _lastErrorCode = Marshal.GetLastWin32Error();
        }
        return _registered;
    }

    bool IGlobalStopHotkey.Register() => Register(DefaultModifiers, RegistrationKey);

    /// <summary>
    /// Explicitly unregisters the hotkey. The operation is idempotent and leaves the
    /// message window alive so a later genuine recording transition can register it again.
    /// </summary>
    public virtual bool Unregister()
    {
        if (!_registered)
        {
            _lastErrorCode = 0;
            return true;
        }

        try
        {
            var result = _registrar.UnregisterHotKey(_window.Handle, _hotkeyId);
            if (result)
            {
                _registered = false;
                _unregisterFailurePending = false;
                _lastErrorCode = 0;
                return true;
            }

            // Capture the native error immediately after the false result. Keep the
            // conservative registered state until a later retry succeeds or the
            // message window is destroyed.
            var nativeError = Marshal.GetLastWin32Error();
            _unregisterFailurePending = true;
            _lastErrorCode = nativeError != 0 ? nativeError : 1;
            return false;
        }
        catch (Exception ex)
        {
            _unregisterFailurePending = true;
            var nativeError = Marshal.GetLastWin32Error();
            _lastErrorCode = nativeError != 0
                ? nativeError
                : (ex.HResult != 0 ? ex.HResult : 1);
            return false;
        }
    }

    bool IGlobalStopHotkey.Unregister() => Unregister();

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

        // A failed explicit unregister has already been reported by the owner. Do
        // not issue a second native attempt during retirement; destroying the
        // message-only window is the release guarantee for this failed instance.
        try
        {
            if (!_unregisterFailurePending)
                Unregister();
        }
        catch { }

        try { _window.DestroyHandle(); }
        catch { }
        finally { _registered = false; }
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

/// <summary>
/// Reuses the stop hotkey's message-window implementation for the local Chapter Marks
/// gesture. The only difference is the virtual key; the process-local id is still allocated
/// by the shared base so F10 and F11 cannot collide.
/// </summary>
internal sealed class GlobalChapterMarkHotkey : GlobalStopHotkey
{
    public new const uint DefaultKey = Native.VK_F11;

    public GlobalChapterMarkHotkey(Action onPressed, IHotkeyRegistrar? registrar = null, Action<Exception>? onError = null)
        : base(onPressed, registrar, onError, DefaultKey)
    {
    }

    public override bool Register(uint modifiers = GlobalStopHotkey.DefaultModifiers, uint key = DefaultKey) =>
        base.Register(modifiers, key);
}
