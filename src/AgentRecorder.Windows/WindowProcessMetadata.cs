using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Windows;

internal sealed record WindowProcessMetadata(nint WindowHandle, int ProcessId, long CreationFileTimeUtc, int SessionId);

/// <summary>One bounded, non-capturing observation. No executable, token or screen reads.</summary>
internal static class WindowProcessMetadataReader
{
    internal static WindowProcessMetadata? Observe(nint window, int expectedPid)
        => ObserveForTests(window, expectedPid, null);

    // Scoped native handle observation for non-capturing lifecycle fixtures only.
    internal static WindowProcessMetadata? ObserveForTests(nint window, int expectedPid, Action<SafeProcessHandle>? opened)
    {
        if (!OperatingSystem.IsWindows() || window == 0 || expectedPid <= 0) return null;
        try
        {
            if (!Matches(window, expectedPid)) return null;
            using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (uint)expectedPid);
            opened?.Invoke(process);
            if (process.IsInvalid || !NativeProcessLifetime.TryRead(process, expectedPid, out var creation, out var session) ||
                !GetExitCodeProcess(process, out var exit) || exit != 259 || !Matches(window, expectedPid) ||
                !NativeProcessLifetime.TryRead(process, expectedPid, out var again, out var againSession) ||
                again != creation || againSession != session ||
                !GetExitCodeProcess(process, out exit) || exit != 259) return null;
            return new(window, expectedPid, creation, session);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static bool Matches(nint window, int pid) => IsWindow(window) &&
        GetWindowThreadProcessId(window, out var observed) != 0 && observed == (uint)pid;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exit);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}

/// <summary>The narrow lifetime/session subset also used by the existing future-window reader.</summary>
internal static class NativeProcessLifetime
{
    internal static bool TryRead(SafeProcessHandle process, int pid, out long creation, out int session)
    {
        creation = 0; session = -1;
        if (pid <= 0 || GetProcessId(process) != (uint)pid ||
            !GetProcessTimes(process, out var time, out _, out _, out _) ||
            !ProcessIdToSessionId((uint)pid, out var rawSession) || rawSession > int.MaxValue) return false;
        creation = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        session = (int)rawSession;
        return creation is > 0 and <= 2650467743999999999;
    }
    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out System.Runtime.InteropServices.ComTypes.FILETIME creation,
        out System.Runtime.InteropServices.ComTypes.FILETIME exit, out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
}
