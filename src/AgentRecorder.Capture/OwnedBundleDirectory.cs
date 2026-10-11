using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// NT FILE_CREATE returns the newly created directory's original handle atomically.
// The parent and directory deny DELETE sharing: foreign rename/replacement is refused.
// Rename/delete use the held object, never a historical pathname or recursive delete.
internal sealed class OwnedBundleDirectory : IDisposable
{
    private readonly SafeFileHandle _parent;
    internal SafeFileHandle Handle { get; }
    internal string Path { get; }
    private OwnedBundleDirectory(string path, SafeFileHandle parent, SafeFileHandle directory)
    { Path = path; _parent = parent; Handle = directory; }
    internal static OwnedBundleDirectory Create(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var api = WindowsFileMetadataApi.Instance;
        var parentPath = System.IO.Path.GetDirectoryName(path)!;
        var parent = api.Open(@"\\?\" + parentPath, 0x80, 3, 3, 0x02200000, out _);
        try
        {
            if (parent.IsInvalid || !IsDirectoryAt(parent, parentPath)) throw new IOException("bundle_parent_unavailable");
            var name = System.IO.Path.GetFileName(path);
            var nameBuffer = Marshal.StringToHGlobalUni(name);
            var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = nameBuffer }, unicodeBuffer, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer, Attributes = 0x40 };
                var status = NtCreateFile(out var handle, 0x10000 | 0x80 | 0x100000 | 0x2 | 0x4 | 0x40, ref attributes, out _, IntPtr.Zero, 0x10, 3, 2, 0x1 | 0x20 | 0x00200000, IntPtr.Zero, 0);
                if (status < 0) { handle?.Dispose(); throw new IOException("bundle_directory_create_failed:" + status.ToString("x8")); }
                if (!IsDirectoryAt(handle, path)) { WindowsPublicationFileOperations.NoReplace.DeleteOriginal(handle); handle.Dispose(); throw new IOException("bundle_directory_invalid"); }
                return new(path, parent, handle);
            }
            finally { Marshal.FreeHGlobal(unicodeBuffer); Marshal.FreeHGlobal(nameBuffer); }
        }
        catch { parent.Dispose(); throw; }
    }
    internal void Rename(string finalPath)
    {
        if (!IsDirectoryAt(Handle, Path)) throw new IOException("bundle_directory_binding_changed");
        try { WindowsPublicationFileOperations.NoReplace.Rename(Handle, finalPath); }
        catch (IOException ex) { throw new IOException("bundle_directory_rename_failed:" + (ex.HResult & 0xffff), ex); }
    }
    internal PublicationCleanupOutcome DeleteEmptyOriginal() => WindowsPublicationFileOperations.NoReplace.DeleteOriginal(Handle);
    internal void LinkOriginal(SafeFileHandle original, string basename)
    {
        // A bounded, explicitly created alias of our original copy. Its entry has
        // never been opened, so NTFS can atomically rename the complete directory.
        var name = Encoding.Unicode.GetBytes(basename);
        var size = Math.Max(Marshal.SizeOf<WindowsPublicationFileOperations.RenameHeader>(), WindowsPublicationFileOperations.NameOffset + name.Length + 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size); // ReplaceIfExists=false
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<WindowsPublicationFileOperations.RenameHeader>(nameof(WindowsPublicationFileOperations.RenameHeader.RootDirectory)).ToInt32(), Handle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, WindowsPublicationFileOperations.NameLengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, WindowsPublicationFileOperations.NameOffset), name.Length);
            var result = NtSetInformationFile(original, out _, buffer, (uint)size, 11); // FileLinkInformation
            if (result < 0) throw new IOException("bundle_original_link_failed:" + result.ToString("x8"));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool IsDirectoryAt(SafeFileHandle handle, string path)
    {
        var api = WindowsFileMetadataApi.Instance;
        if (handle.IsInvalid || handle.IsClosed || !api.Basic(handle, out var basic, out _) ||
            (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != (uint)FileAttributes.Directory) return false;
        var buffer = new StringBuilder(32768);
        var count = api.FinalPath(handle, buffer, buffer.Capacity, out _);
        return count > 0 && count < buffer.Capacity && string.Equals(buffer.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { Handle.Dispose(); _parent.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr RootDirectory, ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status, Information; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocation, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr buffer, uint size, int informationClass);
}
