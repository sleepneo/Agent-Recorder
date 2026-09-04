using System.IO;
using System.Security.Principal;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Metadata-only snapshot of one currently connected display. Nullable
/// members represent an unknown value; the standing execution validator never
/// substitutes a default for an unknown value.
/// </summary>
internal sealed record StandingLeaseDisplayMetadata
{
    internal StandingLeaseDisplayMetadata(
        string publicId,
        string? stableDisplayFingerprint,
        DisplayIdentityResolutionStatus identityStatus,
        AuthorizedPhysicalRectangle? physicalBounds,
        int? dpiX,
        int? dpiY,
        int? physicalWidth,
        int? physicalHeight,
        AuthorizedDisplayOrientation? orientation)
    {
        PublicId = publicId;
        StableDisplayFingerprint = stableDisplayFingerprint;
        IdentityStatus = identityStatus;
        PhysicalBounds = physicalBounds;
        DpiX = dpiX;
        DpiY = dpiY;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        Orientation = orientation;
    }

    internal string PublicId { get; init; }

    internal string? StableDisplayFingerprint { get; init; }

    internal DisplayIdentityResolutionStatus IdentityStatus { get; init; }

    internal AuthorizedPhysicalRectangle? PhysicalBounds { get; init; }

    internal int? DpiX { get; init; }

    internal int? DpiY { get; init; }

    internal int? PhysicalWidth { get; init; }

    internal int? PhysicalHeight { get; init; }

    internal AuthorizedDisplayOrientation? Orientation { get; init; }
}

/// <summary>
/// Non-capturing output filesystem observations for the frozen scope target.
/// This value contains no media data and is never a public/API payload.
/// </summary>
internal sealed record StandingLeaseOutputFileSystemSnapshot
{
    internal StandingLeaseOutputFileSystemSnapshot(
        string? normalizedOutputDirectory,
        string? frozenOutputFilePath,
        bool directoryExists,
        bool frozenFileExists,
        bool directoryWritable,
        bool freeSpaceAvailable,
        long availableFreeBytes,
        long requiredFreeBytes)
    {
        NormalizedOutputDirectory = normalizedOutputDirectory;
        FrozenOutputFilePath = frozenOutputFilePath;
        DirectoryExists = directoryExists;
        FrozenFileExists = frozenFileExists;
        DirectoryWritable = directoryWritable;
        FreeSpaceAvailable = freeSpaceAvailable;
        AvailableFreeBytes = availableFreeBytes;
        RequiredFreeBytes = requiredFreeBytes;
    }

    internal string? NormalizedOutputDirectory { get; init; }

    internal string? FrozenOutputFilePath { get; init; }

    internal bool DirectoryExists { get; init; }

    internal bool FrozenFileExists { get; init; }

    internal bool DirectoryWritable { get; init; }

    internal bool FreeSpaceAvailable { get; init; }

    internal long AvailableFreeBytes { get; init; }

    internal long RequiredFreeBytes { get; init; }
}

/// <summary>
/// Trusted current process/environment snapshot used by the standing gate.
/// It is internal and immutable so a caller cannot provide a public DTO or a
/// string token in its place.
/// </summary>
internal sealed class StandingLeaseExecutionEnvironment
{
    internal StandingLeaseExecutionEnvironment(
        DateTimeOffset nowUtc,
        string? currentUserSid,
        string? sessionBinding,
        bool isInteractiveDesktop)
        : this(
            nowUtc,
            currentUserSid,
            sessionBinding,
            isInteractiveDesktop,
            displays: null,
            topologyDigest: null,
            outputFileSystem: null)
    {
    }

    internal StandingLeaseExecutionEnvironment(
        DateTimeOffset nowUtc,
        string? currentUserSid,
        string? sessionBinding,
        bool isInteractiveDesktop,
        IReadOnlyList<StandingLeaseDisplayMetadata>? displays,
        string? topologyDigest,
        StandingLeaseOutputFileSystemSnapshot? outputFileSystem)
    {
        NowUtc = nowUtc;
        CurrentUserSid = currentUserSid?.Trim() ?? "";
        SessionBinding = sessionBinding?.Trim() ?? "";
        IsInteractiveDesktop = isInteractiveDesktop;
        Displays = displays?.ToArray() ?? Array.Empty<StandingLeaseDisplayMetadata>();
        TopologyDigest = topologyDigest?.Trim() ?? "";
        OutputFileSystem = outputFileSystem;
    }

    internal DateTimeOffset NowUtc { get; }

    internal string CurrentUserSid { get; }

    internal string SessionBinding { get; }

    internal bool IsInteractiveDesktop { get; }

    internal IReadOnlyList<StandingLeaseDisplayMetadata> Displays { get; }

    internal string TopologyDigest { get; }

    internal StandingLeaseOutputFileSystemSnapshot? OutputFileSystem { get; }

    internal static StandingLeaseExecutionEnvironment CaptureCurrent(AuthorizedFixedRegionScope scope) =>
        SystemQueryStandingLeaseExecutionEnvironmentProvider.Instance.Capture(scope);
}

/// <summary>
/// Internal provider boundary for the current standing execution environment.
/// Production uses the SystemQuery implementation; tests inject an explicit
/// fake provider without reading the real user profile, Videos directory, or
/// physical display topology.
/// </summary>
internal interface IStandingLeaseExecutionEnvironmentProvider
{
    StandingLeaseExecutionEnvironment Capture(AuthorizedFixedRegionScope scope);
}

internal sealed class SystemQueryStandingLeaseExecutionEnvironmentProvider : IStandingLeaseExecutionEnvironmentProvider
{
    internal static readonly SystemQueryStandingLeaseExecutionEnvironmentProvider Instance = new();

    private SystemQueryStandingLeaseExecutionEnvironmentProvider()
    {
    }

    public StandingLeaseExecutionEnvironment Capture(AuthorizedFixedRegionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var displays = SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata();
        var topologyDigest = StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var digest)
            ? digest
            : null;

        return new StandingLeaseExecutionEnvironment(
            DateTimeOffset.UtcNow,
            ReadCurrentUserSid(),
            CaptureAuthorizationSessionBinding.Current,
            IsCurrentProcessOnInteractiveDesktop(),
            displays,
            topologyDigest,
            CaptureOutputFileSystem(scope));
    }

    private static StandingLeaseOutputFileSystemSnapshot CaptureOutputFileSystem(
        AuthorizedFixedRegionScope scope)
    {
        string? directory = null;
        string? filePath = null;
        try
        {
            directory = StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory);
            filePath = Path.GetFullPath(Path.Combine(directory, scope.FrozenFileName));
        }
        catch
        {
            return new StandingLeaseOutputFileSystemSnapshot(
                null,
                null,
                directoryExists: false,
                frozenFileExists: false,
                directoryWritable: false,
                freeSpaceAvailable: false,
                availableFreeBytes: 0,
                requiredFreeBytes: 0);
        }

        var directoryExists = Directory.Exists(directory);
        var targetExists = File.Exists(filePath) || Directory.Exists(filePath);
        var writable = directoryExists && CanWriteNonCaptureProbe(directory);
        var freeSpaceAvailable = false;
        long freeBytes = 0;
        try
        {
            freeSpaceAvailable = RecordingPreflightChecker.FreeSpaceProvider(
                directory,
                out freeBytes) && freeBytes >= 0;
        }
        catch
        {
            freeSpaceAvailable = false;
            freeBytes = 0;
        }

        return new StandingLeaseOutputFileSystemSnapshot(
            directory,
            filePath,
            directoryExists,
            targetExists,
            writable,
            freeSpaceAvailable,
            freeBytes,
            RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration));
    }

    private static bool CanWriteNonCaptureProbe(string directory)
    {
        var probePath = Path.Combine(
            directory,
            ".agent-recorder-standing-environment-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan))
            {
            }
            File.Delete(probePath);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
            }
            return false;
        }
    }

    private static string ReadCurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsCurrentProcessOnInteractiveDesktop()
    {
        nint desktop = 0;
        try
        {
            desktop = OpenInputDesktop(
                flags: 0,
                inherit: false,
                desiredAccess: DesktopReadObjects);
            return desktop != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (desktop != 0)
                _ = CloseDesktop(desktop);
        }
    }

    private const uint DesktopReadObjects = 0x0001;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(
        uint flags,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}

internal static class StandingLeaseOutputPath
{
    internal static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new Phase3DomainException("execution_output_directory_invalid", "The output directory is not a fully qualified path.");

        var fullPath = Path.GetFullPath(value);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            throw new Phase3DomainException("execution_output_directory_invalid", "The output directory has no root.");

        return string.Equals(fullPath, root, StringComparison.Ordinal)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
