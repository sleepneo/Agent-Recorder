using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

internal enum PublicationCleanupOutcome
{
    NotNeeded,
    MarkedForDeletion,
    RetainedHandleUnavailable,
    RetainedDeleteFailed
}

// Mutating publication operations deliberately do not extend the read-only probe API.
internal interface IPublicationFileOperations
{
    FileStream CreateNew(string path);
    void Rename(SafeFileHandle original, string finalPath);
    PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original);
}

internal sealed class WindowsPublicationFileOperations : IPublicationFileOperations
{
    internal static readonly WindowsPublicationFileOperations Instance = new();
    internal static readonly WindowsPublicationFileOperations NoReplace = new(false);
    private readonly bool _replaceIfExists;
    internal WindowsPublicationFileOperations(bool replaceIfExists = true) => _replaceIfExists = replaceIfExists;
    internal const uint CreateAccess = 0x40000000 | 0x10000 | 0x80; // GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES
    internal const uint CreateSharing = 1 | 4; // READ | DELETE, no WRITE
    internal const uint CreateDisposition = 1; // CREATE_NEW
    internal const uint CreateFlags = 0x80; // FILE_ATTRIBUTE_NORMAL, synchronous handle

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RenameHeader
    {
        internal uint ReplaceIfExists; // BOOLEAN/Flags union, four bytes including zero padding
        internal IntPtr RootDirectory;
        internal uint FileNameLength; // UTF-16 bytes, excluding terminating NUL
        internal char FirstFileNameCharacter;
    }

    internal static int NameOffset => Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FirstFileNameCharacter)).ToInt32();
    internal static int NameLengthOffset => Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FileNameLength)).ToInt32();

    public FileStream CreateNew(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = CreateFileW(ExtendedPath(path), CreateAccess, CreateSharing, IntPtr.Zero, CreateDisposition, CreateFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeFailure("Publication copy creation failed.", error);
        }
        try { return new FileStream(handle, FileAccess.Write, 81920, isAsync: false); }
        catch
        {
            // Constructor failure still owns only this CREATE_NEW object.
            try { DeleteOriginal(handle); } catch { }
            handle.Dispose();
            throw;
        }
    }

    public void Rename(SafeFileHandle original, string finalPath)
    {
        if (original.IsInvalid || original.IsClosed) throw new IOException("Publication copy handle is unavailable.");
        var name = Encoding.Unicode.GetBytes(ExtendedPath(finalPath));
        var size = checked(Math.Max(Marshal.SizeOf<RenameHeader>(), NameOffset + name.Length + 2));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, _replaceIfExists ? 1 : 0); // Immutable FileRenameInfo policy; padding zero
            Marshal.WriteInt32(buffer, NameLengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, NameOffset), name.Length);
            if (!SetFileInformationByHandle(original, 3, buffer, (uint)size))
                throw NativeFailure("Publication object rename failed.", Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original)
    {
        if (original.IsInvalid || original.IsClosed) return PublicationCleanupOutcome.RetainedHandleUnavailable;
        // FILE_DISPOSITION_INFO contains one BOOLEAN byte, not a marshalled four-byte BOOL.
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            return SetFileInformationByHandle(original, 4, buffer, 1)
                ? PublicationCleanupOutcome.MarkedForDeletion : PublicationCleanupOutcome.RetainedDeleteFailed;
        }
        catch { return PublicationCleanupOutcome.RetainedDeleteFailed; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.IndexOf('\0') >= 0 || full.Length > 32760) throw new ArgumentException("Publication path is invalid.");
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static IOException NativeFailure(string message, int error) => new(message, unchecked((int)(0x80070000u | (uint)error)));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr buffer, uint size);
}
