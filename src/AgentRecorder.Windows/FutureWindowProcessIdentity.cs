using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Windows;

/// <summary>
/// Locally measured identity of one executable image. Publisher fields are
/// certificate metadata only; they are not a Windows trust decision.
/// </summary>
internal sealed record FutureWindowExecutableIdentity(
    int Version,
    string CanonicalPath,
    string FileIdentity,
    string Sha256,
    string? SignerSubject,
    string? SignerCertificateSha256);

internal sealed record FutureWindowProcessSnapshot(
    string WindowId,
    nint WindowHandle,
    int ProcessId,
    long ProcessCreationFileTimeUtc,
    string ProcessUserSid,
    int SessionId,
    string CanonicalImagePath,
    FutureWindowExecutableIdentity ExecutableIdentity);

/// <summary>
/// Keeps the exact approved image object open without write/delete sharing for
/// the lifetime of a pending/active grant. This closes the launch-to-check
/// path replacement gap across an agent starting its future process.
/// </summary>
internal sealed class FutureWindowExecutablePin : IDisposable
{
    private int _disposed;
    internal FutureWindowExecutablePin(FutureWindowExecutableIdentity identity, SafeFileHandle fileHandle)
    { Identity = identity; FileHandle = fileHandle; }
    internal FutureWindowExecutableIdentity Identity { get; }
    internal SafeFileHandle FileHandle { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) FileHandle.Dispose();
    }
}

internal sealed class FutureWindowIdentityHold : IDisposable
{
    private int _disposed;

    internal FutureWindowIdentityHold(
        FutureWindowProcessSnapshot snapshot,
        SafeProcessHandle processHandle,
        SafeFileHandle imageFileHandle)
    {
        Snapshot = snapshot;
        ProcessHandle = processHandle;
        ImageFileHandle = imageFileHandle;
    }

    internal FutureWindowProcessSnapshot Snapshot { get; }
    internal SafeProcessHandle ProcessHandle { get; }
    internal SafeFileHandle ImageFileHandle { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        ImageFileHandle.Dispose();
        ProcessHandle.Dispose();
    }
}

internal sealed record FutureWindowIdentityValidation(
    bool Valid,
    string ReasonCode,
    FutureWindowIdentityHold? Hold)
{
    internal static FutureWindowIdentityValidation Rejected(string reason) => new(false, reason, null);
    internal static FutureWindowIdentityValidation Accepted(FutureWindowIdentityHold hold) => new(true, "", hold);
}

/// <summary>
/// Fail-closed Win32 verifier for a future window target. It binds a local
/// canonical executable path, Windows file ID, and SHA-256 to a post-approval
/// process in the current user/session, then requires exactly one content
/// candidate according to <see cref="FutureWindowCandidateEligibility"/>.
/// </summary>
internal static class FutureWindowProcessIdentity
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRamdisk = 6;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint StillActive = 259;
    private const uint GaRoot = 2;
    private const int DiagnosticMaximumCandidates = 64;
    private const int DiagnosticMaximumEnumeratedWindows = 4096;

    internal static bool TryResolveExecutable(
        string requestedPath,
        out FutureWindowExecutableIdentity? identity,
        out string reasonCode)
    {
        identity = null;
        reasonCode = "executable_identity_unavailable";
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(requestedPath) ||
            !Path.IsPathFullyQualified(requestedPath))
        {
            reasonCode = "executable_path_not_absolute";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(requestedPath);
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "executable_path_not_exe";
                return false;
            }

            using var file = OpenImageFile(fullPath);
            if (file is null || file.IsInvalid || file.IsClosed)
            {
                reasonCode = "executable_file_unavailable";
                return false;
            }

            if (!TryGetFileInformation(file, out var information) ||
                (information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
            {
                reasonCode = "executable_file_identity_unavailable";
                return false;
            }

            var canonicalPath = GetFinalPath(file);
            if (canonicalPath is null || !IsLocalExecutablePath(canonicalPath))
            {
                reasonCode = "executable_path_not_local";
                return false;
            }

            using var stream = new FileStream(file, FileAccess.Read, 128 * 1024, isAsync: false);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var fileIdentity = FormatFileIdentity(information);
            ReadSignerMetadata(canonicalPath, out var signerSubject, out var signerHash);
            identity = new FutureWindowExecutableIdentity(
                1, canonicalPath, fileIdentity, digest, signerSubject, signerHash);
            reasonCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException or
                                               System.Security.Cryptography.CryptographicException)
        {
            reasonCode = "executable_identity_unavailable";
            return false;
        }
    }

    internal static bool TryPinExecutable(
        FutureWindowExecutableIdentity expected,
        out FutureWindowExecutablePin? pin,
        out string reasonCode)
    {
        pin = null;
        reasonCode = "executable_pin_unavailable";
        if (!OperatingSystem.IsWindows() || expected is null || expected.Version != 1)
            return false;
        SafeFileHandle? handle = null;
        try
        {
            handle = OpenImageFile(expected.CanonicalPath);
            if (handle is null || handle.IsInvalid ||
                !TryGetFileInformation(handle, out var information) ||
                (information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                !string.Equals(GetFinalPath(handle), expected.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(FormatFileIdentity(information), expected.FileIdentity, StringComparison.Ordinal) ||
                !TryHashOpenImage(handle, out var digest) ||
                !string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
            {
                handle?.Dispose();
                handle = null;
                reasonCode = "executable_identity_changed";
                return false;
            }
            ReadSignerMetadata(expected.CanonicalPath, out var signerSubject, out var signerHash);
            if (!string.Equals(signerSubject, expected.SignerSubject, StringComparison.Ordinal) ||
                !string.Equals(signerHash, expected.SignerCertificateSha256, StringComparison.Ordinal))
            {
                handle.Dispose();
                handle = null;
                reasonCode = "executable_publisher_evidence_changed";
                return false;
            }
            pin = new FutureWindowExecutablePin(expected, handle);
            handle = null;
            reasonCode = string.Empty;
            return true;
        }
        catch
        {
            reasonCode = "executable_pin_unavailable";
            return false;
        }
        finally { handle?.Dispose(); }
    }

    internal static FutureWindowIdentityValidation ValidateOneEligibleWindow(
        FutureWindowExecutableIdentity approvedIdentity,
        DateTimeOffset approvedAtUtc,
        string requestedWindowId,
        string currentUserSid,
        int currentSessionId)
    {
        if (!OperatingSystem.IsWindows())
            return FutureWindowIdentityValidation.Rejected("future_window_windows_only");
        if (approvedIdentity is null || approvedIdentity.Version != 1 ||
            string.IsNullOrWhiteSpace(currentUserSid) || approvedAtUtc.Offset != TimeSpan.Zero)
            return FutureWindowIdentityValidation.Rejected("future_window_identity_scope_invalid");
        if (!TryParseWindowId(requestedWindowId, out var requestedHandle))
            return FutureWindowIdentityValidation.Rejected("window_id_invalid");

        var eligible = new List<FutureWindowIdentityHold>();
        var processCache = new Dictionary<int, (FutureWindowIdentityHold? Hold, string Reason)>();
        FutureWindowIdentityHold? accepted = null;
        try
        {
            var enumerationSucceeded = EnumWindows((window, parameter) =>
            {
                if (!FutureWindowCandidateEligibility.TryDescribe(window, out var candidate) ||
                    !candidate.IsEligible)
                    return true;

                var pid = candidate.ProcessId;

                if (!processCache.TryGetValue(pid, out var inspected))
                {
                    inspected = TryOpenMatchingProcess(
                        window,
                        pid,
                        approvedIdentity,
                        approvedAtUtc,
                        currentUserSid,
                        currentSessionId);
                    processCache.Add(pid, inspected);
                }

                if (inspected.Hold is { } hold &&
                    eligible.All(existing => existing.Snapshot.ProcessId != pid ||
                                             existing.Snapshot.WindowHandle != window))
                {
                    // Reusing the same hold is intentional: a second top-level
                    // HWND owned by this process makes the eligible set
                    // ambiguous and must fail closed.
                    eligible.Add(hold);
                }

                return true;
            }, nint.Zero);

            if (!enumerationSucceeded)
                return FutureWindowIdentityValidation.Rejected("window_identity_enumeration_failed");

            var eligibleSnapshots = eligible.Select(hold => hold.Snapshot).ToArray();
            var selectionFailure = ValidateEligibleSnapshots(
                eligibleSnapshots, requestedWindowId, out var selectedSnapshot);
            if (selectionFailure is not null)
                return FutureWindowIdentityValidation.Rejected(selectionFailure);

            var sole = eligible.First(hold => ReferenceEquals(hold.Snapshot, selectedSnapshot));
            if (sole.Snapshot.WindowHandle != requestedHandle)
                return FutureWindowIdentityValidation.Rejected("window_id_not_unique_eligible_target");

            accepted = sole;
            return FutureWindowIdentityValidation.Accepted(sole);
        }
        catch
        {
            return FutureWindowIdentityValidation.Rejected("window_identity_enumeration_failed");
        }
        finally
        {
            foreach (var cached in processCache.Values.Select(value => value.Hold).Where(value => value is not null).Distinct())
            {
                if (!ReferenceEquals(cached, accepted))
                    cached!.Dispose();
            }
        }
    }

    internal static bool RevalidatePinnedTarget(
        FutureWindowIdentityHold hold,
        FutureWindowExecutableIdentity approvedIdentity,
        DateTimeOffset approvedAtUtc,
        string currentUserSid,
        int currentSessionId,
        out string reasonCode)
    {
        reasonCode = "window_target_changed_before_start";
        if (hold is null || hold.ProcessHandle.IsClosed || hold.ProcessHandle.IsInvalid ||
            hold.ImageFileHandle.IsClosed || hold.ImageFileHandle.IsInvalid)
            return false;

        try
        {
            if (!GetExitCodeProcess(hold.ProcessHandle, out var exitCode) || exitCode != StillActive ||
                !TryReadProcessEvidence(hold.ProcessHandle, hold.Snapshot.ProcessId, out var processEvidence) ||
                processEvidence.CreationFileTimeUtc != hold.Snapshot.ProcessCreationFileTimeUtc ||
                processEvidence.CreationFileTimeUtc <= approvedAtUtc.UtcDateTime.ToFileTimeUtc() ||
                processEvidence.SessionId != currentSessionId ||
                !string.Equals(processEvidence.UserSid, currentUserSid, StringComparison.Ordinal) ||
                !string.Equals(processEvidence.ImagePath, approvedIdentity.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                !IsWindow(hold.Snapshot.WindowHandle) ||
                !IsWindowVisible(hold.Snapshot.WindowHandle) || IsIconic(hold.Snapshot.WindowHandle) ||
                GetAncestor(hold.Snapshot.WindowHandle, GaRoot) != hold.Snapshot.WindowHandle)
                return false;

            _ = GetWindowThreadProcessId(hold.Snapshot.WindowHandle, out var ownerPid);
            if (ownerPid != (uint)hold.Snapshot.ProcessId ||
                !TryGetFileInformation(hold.ImageFileHandle, out var fileInfo) ||
                !string.Equals(FormatFileIdentity(fileInfo), approvedIdentity.FileIdentity, StringComparison.Ordinal) ||
                !TryHashOpenImage(hold.ImageFileHandle, out var digest) ||
                !string.Equals(digest, approvedIdentity.Sha256, StringComparison.Ordinal))
                return false;

            var currentTarget = ValidateOneEligibleWindow(
                approvedIdentity,
                approvedAtUtc,
                hold.Snapshot.WindowId,
                currentUserSid,
                currentSessionId);
            if (!currentTarget.Valid || currentTarget.Hold is null)
                return false;
            using (currentTarget.Hold)
            {
                if (!IsSamePinnedTarget(hold.Snapshot, currentTarget.Hold.Snapshot, approvedIdentity))
                    return false;
            }

            reasonCode = string.Empty;
            return true;
        }
        catch
        {
            reasonCode = "window_identity_revalidation_failed";
            return false;
        }
    }

    /// <summary>
    /// Bounded, read-only diagnostics for windows owned by one exact image.
    /// Includes only HWND/PID and structural attributes; window-title content,
    /// pixels, keyboard input, user SID and executable hashes are not returned.
    /// </summary>
    internal static FutureWindowCandidateDiagnosticResult DiagnoseExecutableWindows(
        string executablePath,
        int maximumCandidates = 32)
    {
        var candidates = new List<FutureWindowWindowCandidate>();
        if (!OperatingSystem.IsWindows())
            return new(candidates, 0, false, 0, "windows_only", 0, false);
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            return new(candidates, 0, false, 0, "executable_path_invalid", 0, false);

        string exactPath;
        try { exactPath = Path.GetFullPath(executablePath); }
        catch { return new(candidates, 0, false, 0, "executable_path_unavailable", 0, false); }

        maximumCandidates = Math.Clamp(maximumCandidates, 1, DiagnosticMaximumCandidates);
        var scanned = 0;
        var inspectionFailures = 0;
        var truncated = false;
        var completed = false;
        var exactImagePids = new HashSet<int>();
        var rejectedImagePids = new HashSet<int>();
        try
        {
            completed = EnumWindows((window, parameter) =>
            {
                scanned++;
                if (scanned > DiagnosticMaximumEnumeratedWindows)
                {
                    truncated = true;
                    return false;
                }

                _ = GetWindowThreadProcessId(window, out var rawPid);
                if (rawPid == 0 || rawPid > int.MaxValue) return true;
                var pid = (int)rawPid;
                if (!exactImagePids.Contains(pid) && !rejectedImagePids.Contains(pid))
                {
                    SafeProcessHandle? process = null;
                    try
                    {
                        process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
                        if (process is not null && !process.IsInvalid &&
                            TryReadProcessEvidence(process, pid, out var evidence) &&
                            string.Equals(evidence.ImagePath, exactPath, StringComparison.OrdinalIgnoreCase))
                            exactImagePids.Add(pid);
                        else
                            rejectedImagePids.Add(pid);
                    }
                    catch { rejectedImagePids.Add(pid); }
                    finally { process?.Dispose(); }
                }

                if (!exactImagePids.Contains(pid)) return true;
                if (!FutureWindowCandidateEligibility.TryDescribe(window, out var candidate))
                {
                    inspectionFailures++;
                    return true;
                }
                if (candidates.Count >= maximumCandidates)
                {
                    truncated = true;
                    return false;
                }
                candidates.Add(candidate);
                return true;
            }, nint.Zero);
        }
        catch (Exception exception)
        {
            return new(candidates, scanned, truncated, inspectionFailures,
                "native_enumeration_failed_" + exception.GetType().Name, 0, false);
        }

        // EnumWindows returns false when the callback deliberately stops at a
        // documented bound as well as when the native call fails.
        var succeeded = completed || truncated;
        var nativeError = succeeded ? 0 : Marshal.GetLastWin32Error();
        return new(candidates, scanned, truncated, inspectionFailures,
            succeeded ? string.Empty : "native_enumeration_failed", nativeError, succeeded);
    }

    internal static int? GetCurrentSessionId()
    {
        return ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId)
            ? checked((int)sessionId)
            : null;
    }

    internal static bool IsInteractiveDesktopAvailable()
    {
        nint inputDesktop = nint.Zero;
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
                return false;
            inputDesktop = OpenInputDesktop(0, false, 1);
            var currentDesktop = GetThreadDesktop(GetCurrentThreadId());
            return inputDesktop != nint.Zero && currentDesktop != nint.Zero &&
                string.Equals(GetDesktopName(inputDesktop), "Default", StringComparison.Ordinal) &&
                string.Equals(GetDesktopName(currentDesktop), "Default", StringComparison.Ordinal);
        }
        catch { return false; }
        finally { if (inputDesktop != nint.Zero) CloseDesktop(inputDesktop); }
    }

    private static string? GetDesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }

    internal static string? ValidateEligibleSnapshots(
        IReadOnlyList<FutureWindowProcessSnapshot> candidates,
        string requestedWindowId,
        out FutureWindowProcessSnapshot? selected)
    {
        selected = null;
        if (candidates.Count == 0) return "eligible_window_not_found";
        if (candidates.Count != 1) return "multiple_eligible_windows";
        if (!string.Equals(candidates[0].WindowId, requestedWindowId, StringComparison.Ordinal))
            return "window_id_not_unique_eligible_target";
        selected = candidates[0];
        return null;
    }

    internal static bool TryGetValidatedHold(
        FutureWindowIdentityValidation validation,
        out FutureWindowIdentityHold? hold,
        out string reasonCode)
    {
        hold = null;
        reasonCode = "future_window_identity_validation_invalid";
        if (validation is null) return false;
        if (!validation.Valid || validation.Hold is null)
        {
            if (!string.IsNullOrWhiteSpace(validation.ReasonCode))
                reasonCode = validation.ReasonCode;
            return false;
        }

        hold = validation.Hold;
        reasonCode = string.Empty;
        return true;
    }

    internal static bool IsSamePinnedTarget(
        FutureWindowProcessSnapshot approved,
        FutureWindowProcessSnapshot current,
        FutureWindowExecutableIdentity approvedIdentity) =>
        approved.WindowHandle == current.WindowHandle &&
        approved.ProcessId == current.ProcessId &&
        approved.ProcessCreationFileTimeUtc == current.ProcessCreationFileTimeUtc &&
        approved.SessionId == current.SessionId &&
        string.Equals(approved.WindowId, current.WindowId, StringComparison.Ordinal) &&
        string.Equals(approved.ProcessUserSid, current.ProcessUserSid, StringComparison.Ordinal) &&
        string.Equals(approved.CanonicalImagePath, current.CanonicalImagePath, StringComparison.OrdinalIgnoreCase) &&
        SameExecutableIdentity(approved.ExecutableIdentity, approvedIdentity) &&
        SameExecutableIdentity(current.ExecutableIdentity, approvedIdentity);

    private static bool SameExecutableIdentity(
        FutureWindowExecutableIdentity left,
        FutureWindowExecutableIdentity right) =>
        left.Version == right.Version &&
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal) &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal) &&
        string.Equals(left.SignerSubject, right.SignerSubject, StringComparison.Ordinal) &&
        string.Equals(left.SignerCertificateSha256, right.SignerCertificateSha256, StringComparison.Ordinal);

    internal static string? GetCurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private static (FutureWindowIdentityHold? Hold, string Reason) TryOpenMatchingProcess(
        nint window,
        int pid,
        FutureWindowExecutableIdentity approvedIdentity,
        DateTimeOffset approvedAtUtc,
        string currentUserSid,
        int currentSessionId)
    {
        SafeProcessHandle? process = null;
        SafeFileHandle? image = null;
        try
        {
            process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process is null || process.IsInvalid ||
                !TryReadProcessEvidence(process, pid, out var evidence) ||
                evidence.CreationFileTimeUtc <= approvedAtUtc.UtcDateTime.ToFileTimeUtc() ||
                evidence.SessionId != currentSessionId ||
                !string.Equals(evidence.UserSid, currentUserSid, StringComparison.Ordinal))
                return (null, "process_identity_unavailable");

            if (!string.Equals(evidence.ImagePath, approvedIdentity.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                return (null, "executable_path_mismatch");

            image = OpenImageFile(evidence.ImagePath);
            if (image is null || image.IsInvalid ||
                !TryGetFileInformation(image, out var info) ||
                (info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                !string.Equals(GetFinalPath(image), approvedIdentity.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(FormatFileIdentity(info), approvedIdentity.FileIdentity, StringComparison.Ordinal) ||
                !TryHashOpenImage(image, out var digest) ||
                !string.Equals(digest, approvedIdentity.Sha256, StringComparison.Ordinal))
                return (null, "executable_identity_mismatch");

            if (!FutureWindowCandidateEligibility.TryDescribe(window, out var candidate) ||
                !candidate.IsEligible || candidate.ProcessId != pid)
                return (null, "window_owner_changed");

            var snapshot = new FutureWindowProcessSnapshot(
                $"window_{window.ToInt64().ToString(CultureInfo.InvariantCulture)}",
                window,
                pid,
                evidence.CreationFileTimeUtc,
                evidence.UserSid,
                evidence.SessionId,
                evidence.ImagePath,
                approvedIdentity);
            var hold = new FutureWindowIdentityHold(snapshot, process, image);
            process = null;
            image = null;
            return (hold, string.Empty);
        }
        catch
        {
            return (null, "process_identity_unavailable");
        }
        finally
        {
            image?.Dispose();
            process?.Dispose();
        }
    }

    private static bool TryReadProcessEvidence(
        SafeProcessHandle process,
        int expectedPid,
        out ProcessEvidence evidence)
    {
        evidence = default;
        var buffer = new StringBuilder(32768);
        var size = (uint)buffer.Capacity;
        if (GetProcessId(process) != (uint)expectedPid ||
            !GetProcessTimes(process, out var creation, out _, out _, out _) ||
            !QueryFullProcessImageName(process, 0, buffer, ref size) || size == 0 ||
            !OpenProcessToken(process, TokenQuery, out var token))
            return false;

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User?.Value is not { Length: > 0 } sid ||
                !ProcessIdToSessionId((uint)expectedPid, out var sessionId))
                return false;
            var rawPath = buffer.ToString();
            var canonicalPath = Path.GetFullPath(rawPath);
            evidence = new ProcessEvidence(
                FileTimeToLong(creation), sid, checked((int)sessionId), canonicalPath);
            return true;
        }
    }

    private static bool TryParseWindowId(string value, out nint hwnd)
    {
        hwnd = nint.Zero;
        const string prefix = "window_";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(value.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var raw) || raw == 0)
            return false;
        var parsed = new nint(raw);
        if (parsed.ToInt64() != raw ||
            !string.Equals(value, prefix + parsed.ToInt64().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return false;
        hwnd = parsed;
        return true;
    }

    private static SafeFileHandle? OpenImageFile(string path)
    {
        var handle = CreateFile(path, GenericRead, FileShareRead, nint.Zero, OpenExisting,
            FileAttributeNormal, nint.Zero);
        return handle.IsInvalid ? DisposeAndNull(handle) : handle;
    }

    private static SafeFileHandle? DisposeAndNull(SafeFileHandle handle)
    {
        handle.Dispose();
        return null;
    }

    private static bool TryGetFileInformation(SafeFileHandle file, out ByHandleFileInformation information) =>
        GetFileInformationByHandle(file, out information);

    private static string FormatFileIdentity(ByHandleFileInformation information)
    {
        var index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return string.Create(CultureInfo.InvariantCulture,
            $"{information.VolumeSerialNumber:X8}:{index:X16}");
    }

    private static string? GetFinalPath(SafeFileHandle file)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            return null;
        var result = buffer.ToString();
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + result[8..];
        if (result.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            return result[4..];
        return result;
    }

    private static bool IsLocalExecutablePath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root)) return false;
        var driveType = GetDriveType(root);
        return driveType is DriveFixed or DriveRemovable or DriveRamdisk;
    }

    private static bool TryHashOpenImage(SafeFileHandle file, out string digest)
    {
        digest = string.Empty;
        try
        {
            var borrowedHandle = new SafeFileHandle(file.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowedHandle, FileAccess.Read, 128 * 1024, isAsync: false);
            stream.Position = 0;
            digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    private static void ReadSignerMetadata(string path, out string? subject, out string? sha256)
    {
        subject = null;
        sha256 = null;
        try
        {
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
            subject = certificate.Subject;
            sha256 = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
        }
        catch { }
    }

    internal readonly record struct ProcessEvidence(
        long CreationFileTimeUtc,
        string UserSid,
        int SessionId,
        string ImagePath);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME fileTime) =>
        ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder? imageName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private delegate bool EnumWindowsProc(nint window, nint parameter);
}
