using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    internal enum InstallationPidState { Unknown, Present, Absent, Exited }
    private const uint WaitSignaled = 0, WaitTimeout = 258;

    private const int MaximumInstallationReadyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions InstallationReadyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static InstallationSafetyResult CheckInstallationReady(string root, string data, bool firstInstall,
        bool installedMaintenance, InstallationSafetyChecks checks)
    {
        var runtime = Path.Combine(data, "runtime");
        var path = Path.Combine(runtime, "ready.json");
        try
        {
            CheckReadyParent(runtime);
            var api = WindowsFileMetadataApi.Instance;
            using var handle = OpenInstallationReady(path, out var error);
            if (handle.IsInvalid && error is not (2 or 3)) return new(false, "INSTALL_READY_UNREADABLE");

            ReadyFileStamp? stamp = null;
            byte[]? bytes = null;
            var legacy = false;
            ReadySnapshot? record = null;
            string? uninstallPath = null;
            using var stream = handle.IsInvalid ? null : new FileStream(handle, FileAccess.Read);
            if (stream != null)
            {
                stamp = ObserveInstallationReadyFile(api, handle, path);
                bytes = ReadInstallationReadyBytes(stream);
                record = ParseInstallationReady(bytes, data, path, checks.CurrentUserSid, out legacy);
                if (record == null) return new(false, "INSTALL_READY_INVALID");
                if (legacy && installedMaintenance)
                {
                    uninstallPath = ObserveLegacyMaintenanceOwner(root, data, checks);
                    if (uninstallPath == null) return new(false, "INSTALL_READY_LEGACY_OWNER_UNKNOWN");
                }
            }

            if (firstInstall && !FirstInstallIsClear(root, checks))
                return FirstInstallRefused(legacy);
            if (record != null)
            {
                switch (checks.ObserveReadyPid(record.Pid))
                {
                    case InstallationPidState.Absent:
                    case InstallationPidState.Exited: break;
                    case InstallationPidState.Present: return new(false, "INSTALL_READY_PID_PRESENT");
                    default: return new(false, "INSTALL_READY_PID_UNKNOWN");
                }
            }

            // Keep the original file open without write/delete sharing until all gates finish.
            if (!checks.PortAbsent()) return new(false, "INSTALL_API_UNKNOWN");
            if (!checks.NoIntegrations(root)) return new(false, "INSTALL_INTEGRATION_REQUIRES_REVIEW");
            CheckReadyParent(runtime);
            using var latest = OpenInstallationReady(path, out error);
            if (stream == null)
            {
                if (!latest.IsInvalid || error is not (2 or 3)) return new(false, "INSTALL_READY_CHANGED");
            }
            else
            {
                stream.Position = 0;
                if (latest.IsInvalid || stamp != ObserveInstallationReadyFile(api, latest, path) ||
                    !bytes.AsSpan().SequenceEqual(ReadInstallationReadyBytes(stream)))
                    return new(false, "INSTALL_READY_CHANGED");
            }
            if (legacy && installedMaintenance &&
                !string.Equals(uninstallPath, ObserveLegacyMaintenanceOwner(root, data, checks), StringComparison.OrdinalIgnoreCase))
                return new(false, "INSTALL_READY_LEGACY_OWNER_CHANGED");
            if (firstInstall && !FirstInstallIsClear(root, checks))
                return FirstInstallRefused(legacy);
            return new(true, "INSTALL_SAFE_WHILE_LEASE_HELD");
        }
        catch (InvalidDataException) { return new(false, "INSTALL_READY_UNTRUSTED"); }
        catch (JsonException) { return new(false, "INSTALL_READY_INVALID"); }
        catch (IOException) { return new(false, "INSTALL_READY_UNREADABLE"); }
        catch (UnauthorizedAccessException) { return new(false, "INSTALL_READY_UNREADABLE"); }
    }

    private static void CheckReadyParent(string directory)
    {
        InstallationContext.RequireNonReparse(directory);
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw new InvalidDataException();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static SafeFileHandle OpenInstallationReady(string path, out int error) =>
        WindowsFileMetadataApi.Instance.Open(path, 1 | WindowsReadOnlyFileMetadataProbe.ReadAttributes,
            WindowsReadOnlyFileMetadataProbe.ShareRead, WindowsReadOnlyFileMetadataProbe.OpenExisting,
            WindowsReadOnlyFileMetadataProbe.OpenFlags, out error);

    private sealed record ReadyFileStamp(FileIdentityInformation Identity, long ChangeTime, long LastWriteTime);

    private static ReadyFileStamp ObserveInstallationReadyFile(IWindowsFileMetadataApi api, SafeFileHandle handle, string path)
    {
        if (!api.Basic(handle, out var basic, out _) || !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) ||
            (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !api.Identity(handle, out var identity, out _) || (identity.IdLow == 0 && identity.IdHigh == 0))
            throw new InvalidDataException();
        var resolved = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var length = api.FinalPath(handle, resolved, resolved.Capacity, out _);
        if (length == 0 || length >= resolved.Capacity ||
            !string.Equals(resolved.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return new(identity, basic.ChangeTime, basic.LastWriteTime);
    }

    private static byte[] ReadInstallationReadyBytes(FileStream stream)
    {
        var buffer = new byte[MaximumInstallationReadyBytes + 1];
        var count = stream.ReadAtLeast(buffer, buffer.Length, false);
        if (count is 0 or > MaximumInstallationReadyBytes) throw new InvalidDataException();
        return buffer[..count];
    }

    private static readonly string[] ReadyProcessIdentityFields =
        ["instance_id", "process_started_at_utc_ticks", "executable_path", "process_user_sid"];

    // 56d720d RuntimeReadiness serialized all 17 properties, including defaults.
    private static readonly string[] LegacyReadyFields =
    [
        "ready", "pid", "port", "api_version", "mode", "started_at", "ready_at",
        "startup_elapsed_ms", "data_dir", "api_key_file", "audit_log_path", "ready_file",
        "named_event", "interactive_desktop_ready", "session_id", "window_station", "desktop"
    ];

    private static ReadySnapshot? ParseInstallationReady(byte[] bytes, string data, string path,
        Func<string?> getUserSid, out bool legacy)
    {
        legacy = false;
        // RuntimeReadiness writes UTF-8 JSON. Also accept a UTF-8 BOM like the original CLI reader.
        using var doc = JsonDocument.Parse(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsMemory(3) : bytes);
        var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
            if (!names.Add(property.Name)) return null;
        var identityFieldCount = ReadyProcessIdentityFields.Count(names.Contains);
        if (identityFieldCount != 0 && identityFieldCount != ReadyProcessIdentityFields.Length) return null;
        legacy = identityFieldCount == 0;
        if (legacy && (names.Count != LegacyReadyFields.Length ||
            !LegacyReadyFields.All(field => obj.TryGetProperty(field, out _)))) return null;
        var ready = obj.Deserialize<ReadySnapshot>(InstallationReadyJson);
        if (ready == null || !ready.Ready || ready.Pid <= 0 || ready.Port != ApiPort ||
            !names.Contains("api_version") || ready.ApiVersion != "v1" ||
            ready.StartupElapsedMs < 0 ||
            !ExactReadyPath(ready.DataDir, data) || !ExactReadyPath(ready.ReadyFile, path) ||
            !ExactReadyPath(ready.ApiKeyFile, Path.Combine(data, "config", "api-key.txt")))
            return null;

        const string utcSeconds = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (!DateTime.TryParseExact(ready.StartedAt, utcSeconds, CultureInfo.InvariantCulture, utc, out var started) ||
            !DateTime.TryParseExact(ready.ReadyAt, utcSeconds, CultureInfo.InvariantCulture, utc, out var recorded) ||
            started > recorded || recorded > DateTime.UtcNow)
            return null;

        if (legacy)
        {
            // The historical timestamps were truncated to seconds independently.
            var durationMs = (long)(recorded - started).TotalMilliseconds;
            return (ready.Mode is "tray" or "headless") &&
                (ready.Mode != "headless" || ready.InteractiveDesktopReady) &&
                ready.SessionId >= -1 && ready.WindowStation != null && ready.Desktop != null &&
                ready.NamedEvent == RuntimeReadiness.NamedEventName &&
                ExactReadyPath(ready.AuditLogPath, Path.Combine(data, "logs", "audit.jsonl")) &&
                Math.Abs(durationMs - ready.StartupElapsedMs) < 1000 ? ready : null;
        }

        var userSid = getUserSid();
        if (!Guid.TryParseExact(ready.InstanceId, "D", out var instance) || instance == Guid.Empty ||
            ready.ProcessStartedAtUtcTicks <= 0 ||
            ready.ProcessStartedAtUtcTicks / TimeSpan.TicksPerSecond > started.Ticks / TimeSpan.TicksPerSecond ||
            string.IsNullOrEmpty(userSid) || ready.ProcessUserSid != userSid ||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(ready.ExecutablePath))
            return null;
        var executable = Path.GetFileName(ready.ExecutablePath);
        if ((ready.Mode, executable) is not (("tray", "AgentRecorder.App.exe") or ("headless", "AgentRecorder.Headless.exe")))
            return null;
        return ready;
    }

    private static InstallationSafetyResult FirstInstallRefused(bool legacy) =>
        new(false, legacy ? "INSTALL_READY_LEGACY_REQUIRES_FIRST_INSTALL" : "INSTALL_FIRST_INSTALL_OWNER_UNKNOWN");

    private static bool FirstInstallIsClear(string root, InstallationSafetyChecks checks)
    {
        InstallationContext.RequireNonReparse(root);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.Directory) == 0 ||
                Directory.EnumerateFileSystemEntries(root).Any()) return false;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return checks.NoInstallationRegistration();
    }

    internal const string ProductRegistrationKey = @"Software\AgentRecorder\Installation";
    internal const string UninstallRegistrationKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{49E81E5C-57DF-4F8E-93CA-23DE6F10F755}_is1";
    internal const int InstallationRegistry64 = 0x0100, InstallationRegistry32 = 0x0200;
    private const int RegistrationMissing = 2;
    private static readonly string[] ProductRegistrationValues =
        ["Product", "InstallLocation", "CliPath", "AgentInstructions", "DataDirectory"];
    private static readonly string[] UninstallRegistrationValues =
        ["InstallLocation", "Inno Setup: App Path", "UninstallString", "QuietUninstallString"];

    private static string? ObserveLegacyMaintenanceOwner(string root, string data, InstallationSafetyChecks checks)
    {
        try
        {
            if (!InstallationContext.IsInstalled(root, checks.LocalRoot)) return null;
            string[] expected = [InstallationContext.ProductId, root,
                Path.Combine(root, "AgentRecorder.Cli", "AgentRecorder.Cli.exe"),
                Path.Combine(root, "AGENT-INSTRUCTIONS.zh-CN.md"), data];
            string? uninstaller = null;
            foreach (var view in new[] { InstallationRegistry64, InstallationRegistry32 })
            {
                var product = checks.ReadInstallationRegistration(view, ProductRegistrationKey, ProductRegistrationValues);
                var uninstall = checks.ReadInstallationRegistration(view, UninstallRegistrationKey, UninstallRegistrationValues);
                if (view == InstallationRegistry32 && product == null && uninstall == null) continue;
                if (product == null || !expected.SequenceEqual(product, StringComparer.OrdinalIgnoreCase) ||
                    uninstall is not { Length: 4 } ||
                    (!string.Equals(uninstall[0], root, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(uninstall[0], root + "\\", StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals(uninstall[1], root, StringComparison.OrdinalIgnoreCase))
                    return null;

                // Inno 6.7.4 writes a quoted fixed-root uninsNNN.exe and a matching silent entry.
                var command = uninstall[2];
                var prefix = '"' + root + "\\unins";
                if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    command.Length != prefix.Length + 8 || !command.EndsWith(".exe\"", StringComparison.OrdinalIgnoreCase) ||
                    !command.Substring(prefix.Length, 3).All(char.IsAsciiDigit) ||
                    !string.Equals(uninstall[3], command + " /SILENT", StringComparison.OrdinalIgnoreCase))
                    return null;
                var path = command[1..^1];
                if (uninstaller != null && !string.Equals(uninstaller, path, StringComparison.OrdinalIgnoreCase)) return null;
                uninstaller = path;
            }
            if (uninstaller == null || !IsOrdinaryUninstallFile(uninstaller) ||
                !IsOrdinaryUninstallFile(Path.ChangeExtension(uninstaller, ".dat"))) return null;
            return uninstaller;
        }
        catch { return null; }
    }

    private static bool IsOrdinaryUninstallFile(string path)
    {
        InstallationContext.RequireNonReparse(path);
        return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    }

    internal enum InstallationKeyState { Unknown, Missing, Empty, Populated }

    private static bool NoReadyInstallationRegistration() =>
        FirstInstallRegistrationIsClear(ObserveInstallationKey);

    internal static bool FirstInstallRegistrationIsClear(Func<int, string, InstallationKeyState> observe)
    {
        foreach (var view in new[] { InstallationRegistry64, InstallationRegistry32 })
        {
            if (observe(view, ProductRegistrationKey) is not (InstallationKeyState.Missing or InstallationKeyState.Empty) ||
                observe(view, UninstallRegistrationKey) != InstallationKeyState.Missing)
                return false;
        }
        return true;
    }

    internal static InstallationKeyState ObserveInstallationKey(int view, string subkey)
    {
        var status = OpenReadyRegistrationKey(view, subkey, out var key);
        using (key)
        {
            if (status == RegistrationMissing) return InstallationKeyState.Missing;
            if (status != 0 || key.IsInvalid ||
                RegQueryInfoKeyW(key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var subkeys,
                    IntPtr.Zero, IntPtr.Zero, out var values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                return InstallationKeyState.Unknown;
            // The value count includes the unnamed default value.
            return subkeys == 0 && values == 0 ? InstallationKeyState.Empty : InstallationKeyState.Populated;
        }
    }

    private static string[]? ReadReadyInstallationRegistration(int view, string subkey, string[] names)
    {
        var status = OpenReadyRegistrationKey(view, subkey, out var key);
        using (key)
        {
            if (status == RegistrationMissing) return null;
            if (status != 0 || key.IsInvalid) throw new InvalidDataException();
            var values = new string[names.Length];
            var buffer = new byte[4096];
            for (var i = 0; i < names.Length; i++)
            {
                var length = buffer.Length;
                if (RegQueryValueExW(key, names[i], IntPtr.Zero, out var type, buffer, ref length) != 0 ||
                    type != 1 /* REG_SZ */ || length < 2 || length > buffer.Length || length % 2 != 0)
                    throw new InvalidDataException();
                var value = Encoding.Unicode.GetString(buffer, 0, length);
                if (!value.EndsWith('\0') || value.AsSpan(0, value.Length - 1).Contains('\0'))
                    throw new InvalidDataException();
                values[i] = value[..^1];
            }
            return values;
        }
    }

    private static int OpenReadyRegistrationKey(int view, string subkey, out SafeRegistryHandle key)
    {
        const int openLink = 0x0008, queryValue = 0x0001, accessDenied = 5;
        key = new SafeRegistryHandle(new IntPtr(unchecked((int)0x80000001)), ownsHandle: false);
        // Walk under the original handles so a link in any ancestor cannot redirect this lookup.
        foreach (var segment in subkey.Split('\\'))
        {
            using var parent = key;
            var status = RegOpenKeyExW(parent, segment, openLink, queryValue | view, out key);
            if (status != 0) return status;
            if (key.IsInvalid) return accessDenied;
            var length = 0;
            if (RegQueryValueExW(key, "SymbolicLinkValue", IntPtr.Zero, out _, null, ref length) != RegistrationMissing)
                return accessDenied;
            // A dangling link without a target value cannot open normally.
            status = RegOpenKeyExW(parent, segment, 0, queryValue | view, out var normal);
            using (normal)
                if (status != 0 || normal.IsInvalid) return accessDenied;
        }
        return 0;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryInfoKeyW(SafeRegistryHandle key, IntPtr keyClass, IntPtr classLength,
        IntPtr reserved, out uint subkeys, IntPtr maxSubkeyLength, IntPtr maxClassLength,
        out uint values, IntPtr maxValueNameLength, IntPtr maxValueLength, IntPtr securityLength, IntPtr lastWrite);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string valueName, IntPtr reserved, out int type,
        [Out] byte[]? data, ref int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle key, string subkey, int options, int access,
        out SafeRegistryHandle result);

    private static bool ExactReadyPath(string? candidate, string expected) =>
        WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(candidate) &&
        string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

    internal static InstallationPidState ObserveInstallationReadyPid(int pid)
    {
        if (pid <= 0) return InstallationPidState.Unknown;
        const uint synchronize = 0x00100000, queryLimitedInformation = 0x1000;
        using var handle = OpenProcessForInstallation(synchronize | queryLimitedInformation, false, (uint)pid);
        var error = Marshal.GetLastWin32Error();
        if (!handle.IsInvalid)
        {
            var wait = WaitForSingleObject(handle, 0);
            if (wait == WaitSignaled && GetProcessTimes(handle, out var creation, out var exit, out _, out _))
                return ClassifyInstallationPidWait(wait, ToFileTime(creation), ToFileTime(exit));
            return ClassifyInstallationPidWait(wait, 0, 0);
        }
        if (error != 87 /* ERROR_INVALID_PARAMETER */) return InstallationPidState.Unknown;

        // Independently enumerate local PIDs. An unsuccessful query alone is not proof of absence.
        try
        {
            var processes = Process.GetProcesses();
            try
            {
                return processes.Any(process => process.Id == pid)
                    ? InstallationPidState.Present : InstallationPidState.Absent;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch { return InstallationPidState.Unknown; }
    }

    internal static InstallationPidState ClassifyInstallationPidWait(uint wait, long creation, long exit)
    {
        if (wait == WaitTimeout) return InstallationPidState.Present;
        return wait == WaitSignaled && creation > 0 && exit >= creation && exit <= DateTime.UtcNow.ToFileTimeUtc()
            ? InstallationPidState.Exited
            : InstallationPidState.Unknown;
    }

    private static long ToFileTime(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out FILETIME creation, out FILETIME exit,
        out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true, ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcessForInstallation(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
}
