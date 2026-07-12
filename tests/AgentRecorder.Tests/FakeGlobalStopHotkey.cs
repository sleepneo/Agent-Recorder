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
    private bool _registered;
    private bool _disposed;

    public FakeGlobalStopHotkey(Action onPressed, bool registered = true)
    {
        _onPressed = onPressed;
        _registered = registered;
    }

    public int RegisterCallCount { get; private set; }
    public int UnregisterCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public bool Registered => _registered && !_disposed;

    public bool Register(uint modifiers = GlobalStopHotkey.DefaultModifiers, uint key = GlobalStopHotkey.DefaultKey)
    {
        if (_disposed)
            return false;

        RegisterCallCount++;
        return _registered;
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
        UnregisterCallCount++;
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
