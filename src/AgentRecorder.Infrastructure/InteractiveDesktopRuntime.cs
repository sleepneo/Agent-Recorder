using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// A point-in-time description of the current process and the active input
/// desktop. Desktop names alone are deliberately not treated as proof: the
/// process must also belong to the active session and its logged-on user.
/// </summary>
public sealed record InteractiveDesktopObservation(
    int ProcessId,
    int ProcessSessionId,
    string ProcessUserSid,
    int ActiveSessionId,
    string ActiveUserSid,
    string WindowStation,
    string ThreadDesktop,
    string InputDesktop,
    string FailureCode)
{
    public bool HasActiveSession => ActiveSessionId >= 0 && ProcessSessionId == ActiveSessionId &&
        !string.IsNullOrWhiteSpace(ActiveUserSid);

    public bool HasActiveUserSession => HasActiveSession &&
        !string.IsNullOrWhiteSpace(ProcessUserSid) &&
        string.Equals(ProcessUserSid, ActiveUserSid, StringComparison.Ordinal);

    public bool IsOnInteractiveDesktop => HasActiveUserSession &&
        string.Equals(WindowStation, "WinSta0", StringComparison.Ordinal) &&
        string.Equals(ThreadDesktop, "Default", StringComparison.Ordinal) &&
        string.Equals(InputDesktop, "Default", StringComparison.Ordinal);

    /// <summary>The current-user Task Scheduler broker requires the caller to belong to the active user.</summary>
    public bool CanBrokerToInteractiveDesktop => HasActiveSession &&
        string.Equals(ProcessUserSid, ActiveUserSid, StringComparison.Ordinal) &&
        string.Equals(InputDesktop, "Default", StringComparison.Ordinal);

    public string Status => IsOnInteractiveDesktop ? "interactive" :
        CanBrokerToInteractiveDesktop ? "broker_required" : "unavailable";
}

/// <summary>
/// Windows-only, fail-closed inspection shared by the CLI and tray host.
/// </summary>
public static class InteractiveDesktopRuntime
{
    private const int UoiName = 2;
    private const int WtsConnectState = 8;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsActive = 0;
    private const uint DesktopReadObjects = 0x0001;

    public static InteractiveDesktopObservation ObserveCurrent()
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable("WINDOWS_REQUIRED");

        try
        {
            var processId = Environment.ProcessId;
            var processSession = Process.GetCurrentProcess().SessionId;
            var processSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(processSid))
                return Unavailable("USER_IDENTITY_UNAVAILABLE", processId, processSession);

            if (!TryGetSessionIdentity(processSession, out var sessionActive, out var activeSid))
                return Unavailable("ACTIVE_SESSION_UNAVAILABLE", processId, processSession, processSid);

            var windowStation = TryGetObjectName(GetProcessWindowStation());
            var threadDesktop = TryGetObjectName(GetThreadDesktop(GetCurrentThreadId()));
            var input = OpenInputDesktop(0, false, DesktopReadObjects);
            var inputDesktop = input == IntPtr.Zero ? string.Empty : TryGetObjectName(input);
            if (input != IntPtr.Zero)
                CloseDesktop(input);

            if (!sessionActive)
                return new(processId, processSession, processSid, -1, activeSid,
                    windowStation, threadDesktop, inputDesktop, "NO_ACTIVE_INTERACTIVE_SESSION");
            if (string.IsNullOrWhiteSpace(activeSid))
                return new(processId, processSession, processSid, processSession, "",
                    windowStation, threadDesktop, inputDesktop, "ACTIVE_USER_UNAVAILABLE");
            if (!string.Equals(processSid, activeSid, StringComparison.Ordinal))
                return new(processId, processSession, processSid, processSession, activeSid,
                    windowStation, threadDesktop, inputDesktop, "INTERACTIVE_USER_MISMATCH");
            if (inputDesktop.Length == 0)
                return new(processId, processSession, processSid, processSession, activeSid,
                    windowStation, threadDesktop, inputDesktop, "INPUT_DESKTOP_UNAVAILABLE");
            if (!string.Equals(inputDesktop, "Default", StringComparison.Ordinal))
                return new(processId, processSession, processSid, processSession, activeSid,
                    windowStation, threadDesktop, inputDesktop, "DESKTOP_LOCKED");
            if (!string.Equals(windowStation, "WinSta0", StringComparison.Ordinal) ||
                !string.Equals(threadDesktop, "Default", StringComparison.Ordinal))
                return new(processId, processSession, processSid, processSession, activeSid,
                    windowStation, threadDesktop, inputDesktop, "INTERACTIVE_DESKTOP_REQUIRED");

            return new(processId, processSession, processSid, processSession, activeSid,
                windowStation, threadDesktop, inputDesktop, string.Empty);
        }
        catch
        {
            return Unavailable("INTERACTIVE_DESKTOP_PROBE_FAILED");
        }
    }

    internal static string? GetProcessUserSid(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return null;

        IntPtr process = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        IntPtr userBuffer = IntPtr.Zero;
        try
        {
            process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero || !OpenProcessToken(process, 0x0008, out token)) // TOKEN_QUERY
                return null;
            _ = GetTokenInformation(token, 1, IntPtr.Zero, 0, out var required); // TokenUser
            if (required == 0)
                return null;
            userBuffer = Marshal.AllocHGlobal(checked((int)required));
            if (!GetTokenInformation(token, 1, userBuffer, required, out _))
                return null;
            var sidPointer = Marshal.ReadIntPtr(userBuffer);
            return new SecurityIdentifier(sidPointer).Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (userBuffer != IntPtr.Zero) Marshal.FreeHGlobal(userBuffer);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    public static bool IsProcessFromActiveUserAndImage(
        int processId,
        int sessionId,
        string expectedUserSid,
        string expectedImagePath,
        string expectedSha256)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 || sessionId < 0 ||
            string.IsNullOrWhiteSpace(expectedUserSid) || string.IsNullOrWhiteSpace(expectedImagePath) ||
            expectedSha256.Length != 64)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.SessionId != sessionId ||
                !string.Equals(GetProcessUserSid(processId), expectedUserSid, StringComparison.Ordinal))
                return false;

            var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero)
                return false;
            string imagePath;
            try
            {
                var buffer = new StringBuilder(32768);
                var length = (uint)buffer.Capacity;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
                    return false;
                imagePath = buffer.ToString();
            }
            finally { CloseHandle(handle); }

            if (!PathsEqual(imagePath, expectedImagePath) || !TryGetAppHash(imagePath, out var actualHash))
                return false;
            return string.Equals(actualHash, expectedSha256, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static bool IsTcpListenerOwnedByProcess(int processId, int port)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 || port is < 1 or > 65535)
            return false;

        const int errorInsufficientBuffer = 122;
        const int addressFamilyInet = 2;
        const int tcpTableOwnerPidListener = 3;
        const int tcpStateListen = 2;
        IntPtr table = IntPtr.Zero;
        try
        {
            var size = 0;
            var status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamilyInet,
                tcpTableOwnerPidListener, 0);
            if (status != errorInsufficientBuffer || size is <= 0 or > 4 * 1024 * 1024)
                return false;
            table = Marshal.AllocHGlobal(size);
            status = GetExtendedTcpTable(table, ref size, true, addressFamilyInet,
                tcpTableOwnerPidListener, 0);
            if (status != 0)
                return false;

            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 100_000 || 4L + count * 24L > size)
                return false;
            var expectedPort = unchecked((ushort)port);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, checked(4 + index * 24));
                var state = unchecked((uint)Marshal.ReadInt32(row));
                var localPortRaw = unchecked((uint)Marshal.ReadInt32(row, 8));
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)(localPortRaw & 0xffff)));
                var owner = Marshal.ReadInt32(row, 20);
                if (state == tcpStateListen && localPort == expectedPort && owner == processId)
                    return true;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (table != IntPtr.Zero)
                Marshal.FreeHGlobal(table);
        }
    }

    internal static bool IsReadySnapshotForActiveUser(
        ReadySnapshot snapshot,
        InteractiveDesktopObservation caller,
        Func<int, string?>? processUserSid = null)
    {
        if (snapshot is null || caller is null || !caller.CanBrokerToInteractiveDesktop ||
            !snapshot.InteractiveDesktopReady || snapshot.SessionId != caller.ActiveSessionId ||
            !string.Equals(snapshot.WindowStation, "WinSta0", StringComparison.Ordinal) ||
            !string.Equals(snapshot.Desktop, "Default", StringComparison.Ordinal))
            return false;

        try
        {
            using var process = Process.GetProcessById(snapshot.Pid);
            if (process.HasExited || process.SessionId != snapshot.SessionId)
                return false;
            var ownerSid = (processUserSid ?? GetProcessUserSid)(snapshot.Pid);
            return !string.IsNullOrWhiteSpace(ownerSid) &&
                string.Equals(ownerSid, caller.ActiveUserSid, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSessionIdentity(int sessionId, out bool active, out string userSid)
    {
        active = false;
        userSid = string.Empty;
        if (!WTSQuerySessionInformation(IntPtr.Zero, unchecked((uint)sessionId), WtsConnectState,
                out var stateBuffer, out var stateBytes))
            return false;

        try
        {
            active = stateBytes >= sizeof(int) && Marshal.ReadInt32(stateBuffer) == WtsActive;
        }
        finally { WTSFreeMemory(stateBuffer); }
        if (!active)
            return true;

        if (!TryReadSessionString(sessionId, WtsUserName, out var username) || string.IsNullOrWhiteSpace(username))
            return false;
        _ = TryReadSessionString(sessionId, WtsDomainName, out var domain);
        try
        {
            var account = string.IsNullOrWhiteSpace(domain)
                ? new NTAccount(username)
                : new NTAccount(domain, username);
            userSid = (account.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? string.Empty;
            return userSid.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSessionString(int sessionId, int infoClass, out string value)
    {
        value = string.Empty;
        if (!WTSQuerySessionInformation(IntPtr.Zero, unchecked((uint)sessionId), infoClass,
                out var buffer, out var bytes))
            return false;
        try
        {
            if (buffer == IntPtr.Zero || bytes < sizeof(char))
                return false;
            value = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return true;
        }
        finally { WTSFreeMemory(buffer); }
    }

    private static string TryGetObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return string.Empty;
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, UoiName, name, checked((uint)(name.Capacity * sizeof(char))), out _)
            ? name.ToString()
            : string.Empty;
    }

    private static InteractiveDesktopObservation Unavailable(
        string code,
        int processId = 0,
        int sessionId = -1,
        string userSid = "") =>
        new(processId, sessionId, userSid, -1, "", "", "", "", code);

    private static bool TryGetAppHash(string path, out string hash)
    {
        hash = string.Empty;
        try
        {
            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.Read);
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(left).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetFullPath(right).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, StringBuilder info, uint length, out uint needed);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, StringBuilder imageName, ref uint size);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
