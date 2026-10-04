using System.Drawing;
using System.Runtime.InteropServices;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal enum FutureWindowApprovalOutcome
{
    Approved,
    Rejected,
    Cancelled,
    Unavailable,
}

internal sealed record FutureWindowApprovalNativeState(
    bool IsVisible,
    bool IsMinimized,
    bool IsWithinWorkingArea,
    Rectangle Bounds,
    Rectangle WorkingArea,
    int ProcessId,
    int ThreadId,
    nint ForegroundWindow);

internal sealed record FutureWindowApprovalForegroundState(
    bool SetForegroundSucceeded,
    bool BringToTopSucceeded,
    bool IsForeground);

internal interface IFutureWindowApprovalWindowPlatform
{
    void ShowNormal(nint handle);
    FutureWindowApprovalNativeState Inspect(nint handle);
    FutureWindowApprovalForegroundState Activate(nint handle);
}

internal sealed class Win32FutureWindowApprovalWindowPlatform : IFutureWindowApprovalWindowPlatform
{
    internal static Win32FutureWindowApprovalWindowPlatform Instance { get; } = new();

    private const int SwShowNormal = 1;
    private const uint MonitorDefaultToNearest = 2;

    private Win32FutureWindowApprovalWindowPlatform() { }

    public void ShowNormal(nint handle)
    {
        // The first ShowWindow call may honor STARTUPINFO.wShowWindow (SW_HIDE)
        // supplied by the CLI's intentional hidden-shell process launch. A
        // second, explicit display request applies SW_SHOWNORMAL to this dialog.
        _ = ShowWindow(handle, SwShowNormal);
        _ = ShowWindow(handle, SwShowNormal);
        _ = UpdateWindow(handle);
    }

    public FutureWindowApprovalNativeState Inspect(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        _ = GetWindowRect(handle, out var nativeBounds);
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var hasWorkArea = monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo);
        var bounds = nativeBounds.ToRectangle();
        var workingArea = hasWorkArea ? monitorInfo.Work.ToRectangle() : Rectangle.Empty;
        return new FutureWindowApprovalNativeState(
            IsWindowVisible(handle), IsIconic(handle),
            hasWorkArea && workingArea.Width > 0 && workingArea.Height > 0 && workingArea.Contains(bounds),
            bounds, workingArea, checked((int)processId),
            checked((int)GetWindowThreadProcessId(handle, out _)), GetForegroundWindow());
    }

    public FutureWindowApprovalForegroundState Activate(nint handle)
    {
        var setForeground = SetForegroundWindow(handle);
        var isForeground = GetForegroundWindow() == handle;
        var bringToTop = false;
        if (!isForeground && !setForeground)
        {
            bringToTop = BringWindowToTop(handle);
            isForeground = GetForegroundWindow() == handle;
        }
        return new FutureWindowApprovalForegroundState(setForeground, bringToTop, isForeground);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }
}

internal interface IFutureWindowAuthorizationApprovalUi
{
    Task<FutureWindowApprovalOutcome> ShowAsync(
        string authorizationId,
        FutureWindowAuthorizationApprovalDetails details,
        IUiTextProvider textProvider,
        Action<string, object> audit,
        CancellationToken cancellationToken);
}

internal sealed class FutureWindowAuthorizationApprovalUi : IFutureWindowAuthorizationApprovalUi
{
    public Task<FutureWindowApprovalOutcome> ShowAsync(
        string authorizationId,
        FutureWindowAuthorizationApprovalDetails details,
        IUiTextProvider textProvider,
        Action<string, object> audit,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<FutureWindowApprovalOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(FutureWindowAuthorizationApprovalForm.ShowModal(
                    authorizationId, details, textProvider, audit, cancellationToken));
            }
            catch (Exception exception)
            {
                try
                {
                    audit("thread_exception", new
                    {
                        process_id = Environment.ProcessId,
                        thread_id = Environment.CurrentManagedThreadId,
                        exception_type = exception.GetType().Name,
                        reason_code = "approval_ui_thread_exception",
                    });
                }
                catch { }
                completion.TrySetResult(FutureWindowApprovalOutcome.Unavailable);
            }
        })
        {
            IsBackground = true,
            Name = "FutureWindowAuthorizationApproval",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
