using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class FutureWindowAuthorizationApprovalNativeVisibilityTests
{
    private const uint WmClose = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [Theory]
    [InlineData("parent-close")]
    [InlineData("cancel-after-visible")]
    public void HiddenShellLaunch_ShowsNativeApprovalWindowAndClosesItsOwnStaModal(string mode)
    {
        var hostPath = GetHostPath();
        Assert.True(File.Exists(hostPath), $"Approval UI test host was not built: {hostPath}");
        var testDirectory = CreateTestDirectory();
        var auditPath = Path.Combine(testDirectory, "approval-ui.jsonl");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { auditPath, mode },
        })!;

        nint handle = nint.Zero;
        try
        {
            var deadline = Stopwatch.StartNew();
            NativeWindow? observed = null;
            while (deadline.Elapsed < TimeSpan.FromSeconds(8) && !process.HasExited)
            {
                observed = FindVisibleWindow(process.Id);
                if (observed is not null)
                {
                    handle = observed.Handle;
                    break;
                }
                Thread.Sleep(30);
            }

            Assert.False(process.HasExited, $"Isolated approval host exited before displaying; exit={SafeExitCode(process)}.");
            Assert.NotNull(observed);
            Assert.True(observed!.Visible, "The native HWND must be visible, not merely created or Form.Visible.");
            Assert.False(observed.Minimized, "The approval window must not be minimized.");
            Assert.True(observed.WorkingArea.Contains(observed.Bounds),
                $"Native bounds {observed.Bounds} are outside monitor work area {observed.WorkingArea}.");

            if (mode == "parent-close")
                Assert.True(PostMessage(handle, WmClose, nint.Zero, nint.Zero));
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(8)), "The isolated modal did not close within the bounded test wait.");
            Assert.Equal(0, process.ExitCode);

            var events = ReadAudit(auditPath);
            Assert.Contains(events, item => item.Stage == "handle_created");
            Assert.Contains(events, item => item.Stage == "native_visible");
            Assert.Contains(events, item => item.Stage == "decision_waiting");
            Assert.Contains(events, item => item.Stage == "foreground_result");
            Assert.Contains(events, item => item.Stage == "closed");
            Assert.DoesNotContain(events, item => item.Stage == "user_approved");
            Assert.All(events, item => Assert.StartsWith("fwa_", item.AuthorizationId, StringComparison.Ordinal));
            Assert.Contains(events, item => item.Stage == (mode == "parent-close" ? "user_rejected" : "cancelled"));
            var nativeDisplay = Assert.Single(events.Where(item => item.Stage == "native_visible"));
            Assert.True(nativeDisplay.Payload.GetProperty("native_visible").GetBoolean());
            var foreground = Assert.Single(events.Where(item => item.Stage == "foreground_result"));
            Assert.True(foreground.Payload.TryGetProperty("foreground_confirmed", out _));
            Assert.True(foreground.Payload.TryGetProperty("native_visible", out _));
        }
        finally
        {
            if (!process.HasExited)
            {
                if (handle != nint.Zero)
                    _ = PostMessage(handle, WmClose, nint.Zero, nint.Zero);
                if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
                    process.Kill(entireProcessTree: true);
            }
            TestDirectoryCleanup.DeleteOwnedDirectory(testDirectory);
        }
    }

    private static string GetHostPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "AgentRecorder.FutureWindowUiTestHost.exe");
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(TestHelper.ProjectRoot, ".local-data", "temp", "300t",
            "native-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static NativeWindow? FindVisibleWindow(int processId)
    {
        NativeWindow? result = null;
        _ = EnumWindows((handle, parameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(handle)) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(handle, title, title.Capacity) <= 0) return true;
            if (!GetWindowRect(handle, out var rect)) return true;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info)) return true;
            result = new NativeWindow(handle, title.ToString(), IsWindowVisible(handle), IsIconic(handle),
                rect.ToRectangle(), info.Work.ToRectangle());
            return false;
        }, nint.Zero);
        return result;
    }

    private static IReadOnlyList<AuditEvent> ReadAudit(string path)
    {
        Assert.True(File.Exists(path), "The isolated UI host did not write its lifecycle audit.");
        return File.ReadLines(path).Select(line => JsonSerializer.Deserialize<AuditEvent>(line)!).ToArray();
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private sealed record NativeWindow(nint Handle, string Title, bool Visible, bool Minimized,
        Rectangle Bounds, Rectangle WorkingArea);
    private sealed record AuditEvent(
        [property: JsonPropertyName("authorization_id")] string AuthorizationId,
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("payload")] JsonElement Payload);

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

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
}
