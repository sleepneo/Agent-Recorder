using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

internal static class WindowsDirectoryMetadata
{
    private const uint ListDirectoryOrReadData = 1;
    private static readonly WindowsFileMetadataApi Api = WindowsFileMetadataApi.Instance;
    private const uint ShareAll = 7;
    internal const int ObjectNameNotFound = unchecked((int)0xc0000034);
    internal const int ReparseEncountered = unchecked((int)0xc000050b);
    internal const int NoMoreFiles = unchecked((int)0x80000006);
    internal const int NoSuchFile = unchecked((int)0xc000000f);

    internal static SafeFileHandle OpenDrive(string path, uint sharing = ShareAll) => Api.Open(@"\\?\" + path,
        ListDirectoryOrReadData | WindowsReadOnlyFileMetadataProbe.ReadAttributes, sharing, WindowsReadOnlyFileMetadataProbe.OpenExisting,
        WindowsReadOnlyFileMetadataProbe.OpenFlags, out _);

    internal static unsafe SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, out int status,
        uint sharing = ShareAll, bool pinFile = false)
    {
        // A single component on the already observed directory. Keep the
        // directory handle alive, and open a reparse leaf itself, never its target.
        fixed (char* chars = name)
        {
            var bytes = checked((ushort)(name.Length * sizeof(char)));
            var nativeName = new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = (IntPtr)chars };
            var attributes = new ObjectAttributes
            {
                Length = sizeof(ObjectAttributes), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = (IntPtr)(&nativeName), Attributes = 0x40 | 0x1000 // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
            };
            const uint synchronize = 0x100000, fileOpen = 1;
            const uint openReparsePoint = 0x00200000, synchronousNonAlert = 0x20;
            var access = synchronize | WindowsReadOnlyFileMetadataProbe.ReadAttributes | (directory || pinFile ? ListDirectoryOrReadData : 0);
            status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0, sharing, fileOpen,
                openReparsePoint | synchronousNonAlert, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status == 0) return handle;
            handle.Dispose();
            return new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        }
    }

    internal static unsafe int ReadNext(SafeFileHandle handle, byte[] buffer, out long length,
        bool includeAliases = false, bool restart = false)
    {
        const int fileIdExtdDirectoryInformation = 60, fileIdExtdBothDirectoryInformation = 63;
        fixed (byte* address = buffer)
        {
            int status = NtQueryDirectoryFile(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var io,
                (IntPtr)address, (uint)buffer.Length, includeAliases ? fileIdExtdBothDirectoryInformation : fileIdExtdDirectoryInformation,
                true, IntPtr.Zero, restart);
            length = io.Information.ToInt64();
            return status;
        }
    }

    internal static bool TryReadEntry(ReadOnlySpan<byte> buffer, long length, out DirectoryEntry entry, bool includeAliases = false)
    {
        // The BOTH form adds an 8.3 name to the 128-bit identity record.
        int headerLength = includeAliases ? 114 : 88;
        entry = default;
        if (length < headerLength || length > buffer.Length || BinaryPrimitives.ReadInt32LittleEndian(buffer) != 0) return false;
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer[60..]);
        if (nameLength <= 0 || nameLength % 2 != 0 || nameLength > length - headerLength) return false;
        string shortName = "";
        if (includeAliases)
        {
            int bytes = buffer[88];
            if (bytes > 24 || bytes % 2 != 0) return false;
            shortName = new string(MemoryMarshal.Cast<byte, char>(buffer.Slice(90, bytes)));
        }
        var nameBytes = buffer.Slice(headerLength, nameLength);
        // Preserve exact UTF-16 code units for a subsequent native relative open.
        var name = includeAliases ? new string(MemoryMarshal.Cast<byte, char>(nameBytes)) : Encoding.Unicode.GetString(nameBytes);
        entry = new(name, (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(buffer[56..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[72..]), BinaryPrimitives.ReadUInt64LittleEndian(buffer[80..]),
            shortName, BinaryPrimitives.ReadInt64LittleEndian(buffer[40..]),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]), BinaryPrimitives.ReadInt64LittleEndian(buffer[32..]));
        return true;
    }

    internal readonly record struct DirectoryEntry(string Name, FileAttributes Attributes, ulong IdLow, ulong IdHigh,
        string ShortName, long Length, long LastWrite, long ChangeTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status, Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock io, IntPtr allocation, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr ea, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr @event, IntPtr apcRoutine, IntPtr apcContext,
        out IoStatusBlock io, IntPtr information, uint length, int informationClass,
        [MarshalAs(UnmanagedType.U1)] bool singleEntry, IntPtr fileName, [MarshalAs(UnmanagedType.U1)] bool restart);
}
