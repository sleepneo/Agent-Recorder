using System;
using AgentRecorder.App;

namespace AgentRecorder.Tests;

/// <summary>
/// Test-only <see cref="IGlobalStopHotkey"/> substitute that never creates a native window
/// and never calls real Win32 RegisterHotKey. Tests can inspect registration/disposal counts
/// and manually trigger the callback.
/// </summary>
internal sealed class FakeGlobalStopHotkey : IGlobalStopHotkey
{
    private readonly Action _onPressed;
    private readonly bool _registrationResult;
    private bool _registered;
    private bool _disposed;
    private bool _unregisterFailurePending;

    public FakeGlobalStopHotkey(Action onPressed, bool registered = true)
    {
        _onPressed = onPressed;
        _registrationResult = registered;
    }

    public int RegisterCallCount { get; private set; }
    public int UnregisterCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }
    public bool UnregisterSucceeds { get; set; } = true;
    public bool ThrowOnUnregister { get; set; }
    public int LastErrorCode { get; set; }

    public bool Registered => _registered && !_disposed;

    public bool Register()
    {
        if (_disposed)
            return false;

        RegisterCallCount++;
        _registered = _registrationResult;
        return _registered;
    }

    public bool Unregister()
    {
        if (!_registered)
            return true;

        _registered = false;
        UnregisterCallCount++;
        if (ThrowOnUnregister)
        {
            _registered = true;
            _unregisterFailurePending = true;
            throw new InvalidOperationException("unregister failed");
        }

        if (!UnregisterSucceeds)
        {
            _registered = true;
            _unregisterFailurePending = true;
            return false;
        }

        _unregisterFailurePending = false;
        LastErrorCode = 0;
        return true;
    }

    public void SetRegistered(bool value) => _registered = value;

    public void SimulatePressed()
    {
        if (_registered && !_disposed)
        {
            _onPressed();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeCallCount++;
        try
        {
            if (!_unregisterFailurePending)
                Unregister();
        }
        catch { }
        _registered = false;
    }
}

/// <summary>
/// Static helper to create a fake hotkey factory with controlled registration outcome.
/// </summary>
internal static class FakeGlobalStopHotkeyFactory
{
    public static Func<Action, IGlobalStopHotkey> Create(bool registered = true)
    {
        return onPressed => new FakeGlobalStopHotkey(onPressed, registered);
    }
}
