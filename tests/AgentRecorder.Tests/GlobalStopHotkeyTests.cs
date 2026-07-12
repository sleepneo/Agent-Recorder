using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for <see cref="GlobalStopHotkey"/> using a fake registrar so we never
/// actually occupy the user's Ctrl+Shift+F10.
/// </summary>
public class GlobalStopHotkeyTests
{
    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<(IntPtr hWnd, int id, uint modifiers, uint key)> Registrations { get; } = new();
        public List<(IntPtr hWnd, int id)> Unregistrations { get; } = new();
        public bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)
        {
            Registrations.Add((hWnd, id, fsModifiers, vk));
            return ShouldSucceed;
        }
        public bool UnregisterHotKey(IntPtr hWnd, int id)
        {
            Unregistrations.Add((hWnd, id));
            return true;
        }
        public bool ShouldSucceed { get; set; } = true;
    }

    [Fact]
    public void Register_Success_SetsRegisteredTrue()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            using var hotkey = new GlobalStopHotkey(() => { }, registrar);

            bool result = hotkey.Register();

            Assert.True(result);
            Assert.True(hotkey.Registered);
            Assert.Single(registrar.Registrations);
            Assert.Equal(GlobalStopHotkey.DefaultModifiers, registrar.Registrations[0].modifiers);
            Assert.Equal(GlobalStopHotkey.DefaultKey, registrar.Registrations[0].key);
        });
    }

    [Fact]
    public void Register_Failure_SetsRegisteredFalse()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar { ShouldSucceed = false };
            using var hotkey = new GlobalStopHotkey(() => { }, registrar);

            bool result = hotkey.Register();

            Assert.False(result);
            Assert.False(hotkey.Registered);
        });
    }

    [Fact]
    public void Dispose_UnregistersAndDestroysWindow()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            var hotkey = new GlobalStopHotkey(() => { }, registrar);
            hotkey.Register();
            var handle = GetMessageWindowHandle(hotkey);

            hotkey.Dispose();

            Assert.Single(registrar.Unregistrations);
            Assert.True(handle != IntPtr.Zero);
        });
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            var hotkey = new GlobalStopHotkey(() => { }, registrar);
            hotkey.Register();

            hotkey.Dispose();
            hotkey.Dispose();

            Assert.Single(registrar.Unregistrations);
        });
    }

    [Fact]
    public void HotkeyMessage_CorrectId_InvokesCallbackOnce()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int callCount = 0;
            using var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            // Simulate WM_HOTKEY with the correct hotkey id.
            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)hotkey.HotkeyId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(1, callCount);
        });
    }

    [Fact]
    public void HotkeyMessage_WrongId_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int callCount = 0;
            using var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var wrongId = hotkey.HotkeyId + 999;
            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)wrongId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(0, callCount);
        });
    }

    [Fact]
    public void HotkeyMessage_ZeroId_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int callCount = 0;
            using var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, IntPtr.Zero, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(0, callCount);
        });
    }

    [Fact]
    public void HotkeyMessage_NotRegistered_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar { ShouldSucceed = false };
            int callCount = 0;
            using var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)hotkey.HotkeyId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(0, callCount);
        });
    }

    [Fact]
    public void HotkeyMessage_AfterDispose_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int callCount = 0;
            var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);
            hotkey.Dispose();

            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)hotkey.HotkeyId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(0, callCount);
        });
    }

    [Fact]
    public void HotkeyMessage_CallbackException_DoesNotPropagateAndReportsError()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            Exception? captured = null;
            using var hotkey = new GlobalStopHotkey(
                () => throw new InvalidOperationException("boom"),
                registrar,
                onError: ex => captured = ex);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)hotkey.HotkeyId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.NotNull(captured);
            Assert.IsType<InvalidOperationException>(captured);
            Assert.Equal("boom", captured!.Message);
        });
    }

    [Fact]
    public void HotkeyMessage_CallbackAndOnErrorBothThrow_DoesNotPropagate()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int onPressedCount = 0;
            int onErrorCount = 0;
            using var hotkey = new GlobalStopHotkey(
                () => { onPressedCount++; throw new InvalidOperationException("callback boom"); },
                registrar,
                onError: ex => { onErrorCount++; throw new InvalidOperationException("error boom"); });
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var msg = Message.Create(window.Handle, GlobalStopHotkey.WM_HOTKEY, (IntPtr)hotkey.HotkeyId, IntPtr.Zero);
            InvokeWndProc(window, ref msg);

            Assert.Equal(1, onPressedCount);
            Assert.Equal(1, onErrorCount);
        });
    }

    [Fact]
    public void OtherMessage_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            int callCount = 0;
            using var hotkey = new GlobalStopHotkey(() => Interlocked.Increment(ref callCount), registrar);
            hotkey.Register();
            var window = GetMessageWindow(hotkey);

            var msg = Message.Create(window.Handle, 0x0001, (IntPtr)hotkey.HotkeyId, IntPtr.Zero); // WM_CREATE
            InvokeWndProc(window, ref msg);

            Assert.Equal(0, callCount);
        });
    }

    private static NativeWindow GetMessageWindow(GlobalStopHotkey hotkey)
    {
        var field = typeof(GlobalStopHotkey).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (NativeWindow)field!.GetValue(hotkey)!;
    }

    private static IntPtr GetMessageWindowHandle(GlobalStopHotkey hotkey)
    {
        return GetMessageWindow(hotkey).Handle;
    }

    private static void InvokeWndProc(NativeWindow window, ref Message msg)
    {
        var method = typeof(NativeWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, new object[] { msg });
    }
}
