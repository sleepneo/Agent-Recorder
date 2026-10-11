using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

// Retention-private current observations. Never a persisted publication anchor.
internal enum FileObservationOutcome { Sampled, Missing, Inaccessible, Unavailable, ReparsePoint, Directory, MetadataChanged, ResolvedPathUnavailable }
internal sealed record ObservedFileIdentity(ulong VolumeSerialNumber, ulong IdLow, ulong IdHigh);
internal sealed record HandleMetadataSnapshot(long Length, long AllocationSize, uint Links, FileAttributes Attributes,
    byte DeletePending, byte Directory, long LastWriteTime, ObservedFileIdentity? Identity);
internal sealed record FileMetadataObservation(FileObservationOutcome Outcome, HandleMetadataSnapshot? Before = null,
    HandleMetadataSnapshot? After = null, string? BeforePath = null, string? AfterPath = null);
internal interface IReadOnlyFileMetadataProbe
{
    FileMetadataObservation? Observe(string path);
}

internal sealed class WindowsReadOnlyFileMetadataProbe : IReadOnlyFileMetadataProbe
{
    internal static WindowsReadOnlyFileMetadataProbe Instance { get; } = new(WindowsFileMetadataApi.Instance);
    internal const uint ReadAttributes = 0x80, ShareRead = 1, OpenExisting = 3;
    internal const uint OpenFlags = 0x00200000 | 0x02000000; // OPEN_REPARSE_POINT | BACKUP_SEMANTICS (directories too)
    internal const int FinalPathCapacity = 2048, MaximumRequestLength = 1024;
    private readonly IWindowsFileMetadataApi _api;
    internal WindowsReadOnlyFileMetadataProbe(IWindowsFileMetadataApi api) => _api = api;

    public FileMetadataObservation Observe(string path)
    {
        try
        {
            if (!_api.Supported || !IsCanonicalLocalPath(path) || _api.DriveType(path[..3]) is not (2 or 3 or 6))
                return new(FileObservationOutcome.Unavailable);
            // One existing leaf; no READ_DATA, WRITE_DATA or DELETE access.
            using var handle = _api.Open(@"\\?\" + path, ReadAttributes, ShareRead, OpenExisting, OpenFlags, out var error);
            if (handle.IsInvalid || handle.IsClosed) return Failure(error);
            if (!_api.Basic(handle, out var basic, out error)) return Failure(error);
            if (!ValidAttributes(basic.Attributes)) return new(FileObservationOutcome.Unavailable);
            if ((basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return new(FileObservationOutcome.ReparsePoint);
            if ((basic.Attributes & (uint)FileAttributes.Directory) != 0) return new(FileObservationOutcome.Directory);
            if (!ReadRest(handle, basic, out var first, out error)) return Failure(error);
            if (!FinalPath(handle, out var firstPath)) return new(FileObservationOutcome.ResolvedPathUnavailable);
            if (!_api.Basic(handle, out basic, out error)) return Failure(error);
            if (!ValidAttributes(basic.Attributes)) return new(FileObservationOutcome.Unavailable);
            if ((basic.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return new(FileObservationOutcome.MetadataChanged);
            if (!ReadRest(handle, basic, out var second, out error)) return Failure(error);
            if (!FinalPath(handle, out var secondPath)) return new(FileObservationOutcome.ResolvedPathUnavailable);
            return new(FileObservationOutcome.Sampled, first, second, firstPath, secondPath);
        }
        catch { return new(FileObservationOutcome.Unavailable); }
    }

    private bool ReadRest(SafeFileHandle handle, FileBasicInformation basic, out HandleMetadataSnapshot? sample, out int error)
    {
        sample = null;
        if (!_api.Standard(handle, out var standard, out error) || !_api.Identity(handle, out var id, out error)) return false;
        sample = new(standard.EndOfFile, standard.AllocationSize, standard.NumberOfLinks, (FileAttributes)basic.Attributes,
            standard.DeletePending, standard.Directory, basic.LastWriteTime, new(id.VolumeSerialNumber, id.IdLow, id.IdHigh));
        return true;
    }
    private bool FinalPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        var buffer = new StringBuilder(FinalPathCapacity);
        var length = _api.FinalPath(handle, buffer, FinalPathCapacity, out _);
        // Insufficient buffer returns REQUIRED size incl NUL, never a success.
        if (length == 0 || length >= FinalPathCapacity || length != buffer.Length) return false;
        var raw = buffer.ToString();
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal)) raw = raw[4..];
        if (!IsCanonicalLocalPath(raw)) return false;
        path = raw;
        return true;
    }
    internal static bool IsCanonicalLocalPath(string? path)
    {
        if (path is null || path.Length is < 4 or > MaximumRequestLength || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.Contains('/') || path.Contains('\0')) return false;
        var parts = path[3..].Split('\\');
        return parts.All(p => p.Length > 0 && p is not ("." or "..") && !p.EndsWith('.') && !p.EndsWith(' ') &&
            p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }
    internal static bool ValidAttributes(uint attributes) => attributes != 0 && (attributes & ~0x007fffffu) == 0 &&
        (attributes & (uint)FileAttributes.Device) == 0;
    private static FileMetadataObservation Failure(int error) => new(error switch
    {
        2 or 3 => FileObservationOutcome.Missing,
        5 or 32 or 33 => FileObservationOutcome.Inaccessible,
        _ => FileObservationOutcome.Unavailable
    });
}

// BOOLEAN is one byte (not Win32 BOOL); native trailing padding is retained.
[StructLayout(LayoutKind.Sequential)]
internal struct FileStandardInformation
{
    internal long AllocationSize, EndOfFile;
    internal uint NumberOfLinks;
    internal byte DeletePending, Directory;
}
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInformation
{
    internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
    internal uint Attributes;
}
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdentityInformation
{
    internal ulong VolumeSerialNumber, IdLow, IdHigh; // FILE_ID_INFO: full 128 bits, also on ReFS
}
internal interface IWindowsFileMetadataApi
{
    bool Supported { get; }
    uint DriveType(string root);
    SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error);
    bool Basic(SafeFileHandle handle, out FileBasicInformation information, out int error);
    bool Standard(SafeFileHandle handle, out FileStandardInformation information, out int error);
    bool Identity(SafeFileHandle handle, out FileIdentityInformation information, out int error);
    uint FinalPath(SafeFileHandle handle, StringBuilder buffer, int capacity, out int error);
}
internal sealed class WindowsFileMetadataApi : IWindowsFileMetadataApi
{
    internal static WindowsFileMetadataApi Instance { get; } = new();
    public bool Supported => OperatingSystem.IsWindows();
    public uint DriveType(string root) => GetDriveTypeW(root);
    public SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error)
    {
        var result = CreateFileW(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        error = Marshal.GetLastWin32Error(); return result;
    }
    public bool Basic(SafeFileHandle handle, out FileBasicInformation information, out int error)
    { var ok = BasicInfo(handle, 0, out information, (uint)Marshal.SizeOf<FileBasicInformation>()); error = Marshal.GetLastWin32Error(); return ok; }
    public bool Standard(SafeFileHandle handle, out FileStandardInformation information, out int error)
    { var ok = StandardInfo(handle, 1, out information, (uint)Marshal.SizeOf<FileStandardInformation>()); error = Marshal.GetLastWin32Error(); return ok; }
    public bool Identity(SafeFileHandle handle, out FileIdentityInformation information, out int error)
    { var ok = IdentityInfo(handle, 18, out information, (uint)Marshal.SizeOf<FileIdentityInformation>()); error = Marshal.GetLastWin32Error(); return ok; }
    public uint FinalPath(SafeFileHandle handle, StringBuilder buffer, int capacity, out int error)
    { var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0); error = Marshal.GetLastWin32Error(); return length; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BasicInfo(SafeFileHandle handle, int informationClass, out FileBasicInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StandardInfo(SafeFileHandle handle, int informationClass, out FileStandardInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IdentityInfo(SafeFileHandle handle, int informationClass, out FileIdentityInformation information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, uint capacity, uint flags);
}
