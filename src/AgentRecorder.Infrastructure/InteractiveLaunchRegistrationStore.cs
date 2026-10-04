using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

public sealed record InteractiveLaunchRegistration(
    int SchemaVersion,
    string RegistrationId,
    string UserSid,
    string AppPath,
    string AppSha256,
    string DataDir,
    string TaskName,
    DateTimeOffset EnrolledAtUtc,
    string? AgentSid = null);

/// <summary>
/// Stores the one-time, current-user trust decision used by the interactive
/// Task Scheduler bridge. This is not an API credential or a general launcher.
/// </summary>
public static class InteractiveLaunchRegistrationStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRegistrationBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string RegistrationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentRecorder", "interactive-launch.json");

    public static string CreateTaskName(string userSid)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userSid));
        return "AgentRecorder.InteractiveLaunch.v1." + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static bool TryCreate(
        string appPath,
        string dataDir,
        string userSid,
        out InteractiveLaunchRegistration? registration,
        out string failureCode,
        Func<string, bool>? userSidValidator = null)
    {
        registration = null;
        failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_INVALID";
        var validateUserSid = userSidValidator ?? IsUserSid;
        if (!OperatingSystem.IsWindows() || !validateUserSid(userSid) ||
            !TryCanonicalTrustedAppPath(appPath, out var canonicalAppPath) ||
            !TryCanonicalDirectory(dataDir, out var canonicalDataDir) ||
            !TryGetAppHash(canonicalAppPath, out var hash))
        {
            failureCode = "INTERACTIVE_LAUNCH_TRUST_PATH_INVALID";
            return false;
        }

        registration = new(
            CurrentSchemaVersion,
            InteractiveLaunchProtocol.NewId(),
            userSid,
            canonicalAppPath,
            hash,
            canonicalDataDir,
            CreateTaskName(userSid),
            DateTimeOffset.UtcNow);
        failureCode = string.Empty;
        return true;
    }

    public static bool TryLoad(out InteractiveLaunchRegistration? registration, out string failureCode)
        => TryLoad(RegistrationPath, out registration, out failureCode);

    internal static bool TryLoad(string path, out InteractiveLaunchRegistration? registration, out string failureCode)
    {
        registration = null;
        failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_MISSING";
        try
        {
            if (!File.Exists(path))
                return false;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumRegistrationBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_INVALID";
                return false;
            }

            registration = JsonSerializer.Deserialize<InteractiveLaunchRegistration>(File.ReadAllText(path), JsonOptions);
            if (registration is { SchemaVersion: 2 } or { AgentSid: not null })
            {
                registration = null;
                failureCode = "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED";
                return false;
            }
            if (!IsWellFormed(registration))
            {
                registration = null;
                failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_INVALID";
                return false;
            }

            failureCode = string.Empty;
            return true;
        }
        catch
        {
            registration = null;
            failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_INVALID";
            return false;
        }
    }

    public static bool TrySave(InteractiveLaunchRegistration registration, out string failureCode)
        => TrySave(registration, RegistrationPath, out failureCode);

    internal static bool TrySaveNew(InteractiveLaunchRegistration registration, string path, out string failureCode)
    {
        failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_WRITE_FAILED";
        if (!IsWellFormed(registration))
            return false;
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return false;
            temporary = Path.Combine(directory, ".interactive-launch-" + InteractiveLaunchProtocol.NewId() + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(registration, JsonOptions));
            File.Move(temporary, path, overwrite: false);
            temporary = null;
            failureCode = string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    internal static bool TrySave(InteractiveLaunchRegistration registration, string path, out string failureCode)
    {
        failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_WRITE_FAILED";
        if (!IsWellFormed(registration))
            return false;
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return false;
            temporary = Path.Combine(directory, ".interactive-launch-" + InteractiveLaunchProtocol.NewId() + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(registration, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            failureCode = string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    public static bool TryDelete(string expectedRegistrationId, out string failureCode)
        => TryDelete(expectedRegistrationId, RegistrationPath, out failureCode);

    internal static bool TryDelete(string expectedRegistrationId, string path, out string failureCode)
    {
        failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_MISMATCH";
        if (!TryLoad(path, out var registration, out _) || registration is null ||
            !string.Equals(registration.RegistrationId, expectedRegistrationId, StringComparison.Ordinal))
            return false;
        try
        {
            File.Delete(path);
            failureCode = string.Empty;
            return true;
        }
        catch
        {
            failureCode = "INTERACTIVE_LAUNCH_REGISTRATION_DELETE_FAILED";
            return false;
        }
    }

    public static string Validate(
        InteractiveLaunchRegistration registration,
        string expectedRegistrationId,
        string currentUserSid,
        string currentAppPath,
        string? expectedDataDir = null)
    {
        if (registration.SchemaVersion == 2 || registration.AgentSid is not null)
            return "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED";
        if (!IsWellFormed(registration) ||
            !string.Equals(registration.RegistrationId, expectedRegistrationId, StringComparison.Ordinal))
            return "INTERACTIVE_LAUNCH_REGISTRATION_MISMATCH";
        if (!string.Equals(registration.UserSid, currentUserSid, StringComparison.Ordinal))
            return "INTERACTIVE_LAUNCH_USER_MISMATCH";
        if (!string.Equals(registration.TaskName, CreateTaskName(registration.UserSid), StringComparison.Ordinal))
            return "INTERACTIVE_LAUNCH_REGISTRATION_MISMATCH";
        if (!TryCanonicalTrustedAppPath(currentAppPath, out var canonicalAppPath))
            return "INTERACTIVE_LAUNCH_APP_IDENTITY_UNAVAILABLE";
        if (!PathsEqual(registration.AppPath, canonicalAppPath))
            return "INTERACTIVE_LAUNCH_APP_PATH_MISMATCH";
        if (!TryGetAppHash(canonicalAppPath, out var currentHash))
            return "INTERACTIVE_LAUNCH_APP_IDENTITY_UNAVAILABLE";
        if (!string.Equals(registration.AppSha256, currentHash, StringComparison.Ordinal))
            return "INTERACTIVE_LAUNCH_STALE_BINARY";
        if (expectedDataDir is not null &&
            (!TryCanonicalDirectory(expectedDataDir, out var canonicalDataDir) ||
             !PathsEqual(registration.DataDir, canonicalDataDir)))
            return "INTERACTIVE_LAUNCH_DATA_DIR_MISMATCH";
        return string.Empty;
    }

    public static bool TryValidateForCurrentProcess(
        string registrationId,
        out InteractiveLaunchRegistration? registration,
        out string failureCode)
    {
        registration = null;
        if (!TryLoad(out var loaded, out failureCode) || loaded is null)
            return false;
        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        var appPath = Environment.ProcessPath ?? string.Empty;
        failureCode = Validate(loaded, registrationId, userSid, appPath);
        if (failureCode.Length != 0)
            return false;
        registration = loaded;
        return true;
    }

    internal static bool TryCanonicalTrustedAppPath(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length > 2048 ||
                !string.Equals(Path.GetFileName(fullPath), "AgentRecorder.App.exe", StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                Path.GetPathRoot(fullPath) is not { } appRoot || new DriveInfo(appRoot).DriveType != DriveType.Fixed ||
                !File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return false;
            canonicalPath = fullPath;
            return true;
        }
        catch { return false; }
    }

    private static bool TryCanonicalDirectory(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            canonicalPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(canonicalPath);
            return canonicalPath.Length <= 2048 && Encoding.UTF8.GetByteCount(canonicalPath) <= 2048 &&
                Path.IsPathFullyQualified(canonicalPath) && !canonicalPath.StartsWith("\\\\", StringComparison.Ordinal) &&
                root is not null && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    private static bool TryGetAppHash(string path, out string hash)
    {
        hash = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    private static bool IsWellFormed(InteractiveLaunchRegistration? value) =>
        value is { SchemaVersion: CurrentSchemaVersion, AgentSid: null } &&
        InteractiveLaunchProtocol.IsId(value.RegistrationId) &&
        IsSidSyntax(value.UserSid) &&
        TryCanonicalTrustedAppPath(value.AppPath, out var appPath) && PathsEqual(appPath, value.AppPath) &&
        TryCanonicalDirectory(value.DataDir, out var dataDir) && PathsEqual(dataDir, value.DataDir) &&
        string.Equals(value.TaskName, CreateTaskName(value.UserSid), StringComparison.Ordinal) &&
        value.AppSha256.Length == 64 && value.AppSha256 == value.AppSha256.ToLowerInvariant();

    private static bool IsUserSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !OperatingSystem.IsWindows())
            return false;
        try
        {
            var parsed = new SecurityIdentifier(sid);
            if (!string.Equals(parsed.Value, sid, StringComparison.Ordinal))
                return false;
            var sidBytes = new byte[parsed.BinaryLength];
            parsed.GetBinaryForm(sidBytes, 0);
            var name = new StringBuilder(256);
            var domain = new StringBuilder(256);
            uint nameLength = (uint)name.Capacity;
            uint domainLength = (uint)domain.Capacity;
            if (!LookupAccountSid(null, sidBytes, name, ref nameLength, domain, ref domainLength, out var use))
            {
                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122)
                    return false;
                name = new StringBuilder(checked((int)nameLength));
                domain = new StringBuilder(checked((int)domainLength));
                if (!LookupAccountSid(null, sidBytes, name, ref nameLength, domain, ref domainLength, out use))
                    return false;
            }
            return use == 1; // SID_NAME_USE.SidTypeUser only; never a group SID.
        }
        catch { return false; }
    }

    private static bool IsSidSyntax(string sid)
    {
        try { return !string.IsNullOrWhiteSpace(sid) && new SecurityIdentifier(sid).Value == sid; }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "LookupAccountSidW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(
        string? systemName,
        byte[] sid,
        StringBuilder name,
        ref uint nameLength,
        StringBuilder referencedDomainName,
        ref uint referencedDomainNameLength,
        out int use);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record InteractiveLaunchResult(string RequestId, int ProcessId, string Status, string FailureCode);

public static class InteractiveLaunchResultStore
{
    private const int MaximumResultBytes = 2048;
    private const int MaximumOutstandingResults = 32;
    private static readonly TimeSpan MaximumResultAge = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static string GetPath(string dataDir, string requestId) => Path.Combine(
        dataDir, "runtime", "interactive-launch", requestId + ".json");

    public static bool TryWrite(string dataDir, string requestId, string status, string failureCode = "")
    {
        if (!InteractiveLaunchProtocol.IsId(requestId) || string.IsNullOrWhiteSpace(status) ||
            status.Length > 48 || failureCode.Length > 64)
            return false;
        string? temporary = null;
        try
        {
            var path = GetPath(dataDir, requestId);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            Prune(directory);
            temporary = Path.Combine(directory, "." + requestId + "." + InteractiveLaunchProtocol.NewId() + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new InteractiveLaunchResult(requestId, Environment.ProcessId, status, failureCode), JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch { return false; }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch { } } }
    }

    public static bool TryReadAndDelete(string dataDir, string requestId, out InteractiveLaunchResult? result)
    {
        result = null;
        if (!InteractiveLaunchProtocol.IsId(requestId))
            return false;
        var path = GetPath(dataDir, requestId);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumResultBytes)
                return false;
            var parsed = JsonSerializer.Deserialize<InteractiveLaunchResult>(File.ReadAllText(path), JsonOptions);
            if (parsed is null || parsed.RequestId != requestId || parsed.ProcessId <= 0 ||
                parsed.Status.Length > 48 || parsed.FailureCode.Length > 64)
                return false;
            result = parsed;
            try { File.Delete(path); } catch { }
            return true;
        }
        catch { return false; }
    }

    private static void Prune(string directory)
    {
        try
        {
            var now = DateTime.UtcNow;
            var files = Directory.EnumerateFiles(directory, "*.json")
                .Where(path => InteractiveLaunchProtocol.IsId(Path.GetFileNameWithoutExtension(path)))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            for (var i = 0; i < files.Length; i++)
            {
                if (now - files[i].LastWriteTimeUtc > MaximumResultAge || i >= MaximumOutstandingResults - 1)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            foreach (var temporary in Directory.EnumerateFiles(directory, ".*.tmp"))
            {
                var age = now - File.GetLastWriteTimeUtc(temporary);
                if (age > MaximumResultAge)
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }
        catch { }
    }
}
