using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.AudioHelper;

// In split A/V the host exclusively creates the WAV and holds its namespace.
// The helper opens that object without a create/truncate disposition.
internal sealed class OwnedWavTarget(string path, string expected) : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint ReadWrite = 0xc0000000;
    private const uint ShareRead = 1;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private SafeFileHandle? _original;

    internal static bool IsValidIdentity(string value) =>
        value.Length == 48 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        value.AsSpan(0, 16).ContainsAnyExcept('0') && value.AsSpan(16).ContainsAnyExcept('0');

    internal Stream OpenForWrite()
    {
        var handle = CreateFileW(@"\\?\" + path, ReadWrite, ShareRead, IntPtr.Zero,
            OpenExisting, OpenReparsePoint, IntPtr.Zero);
        try
        {
            if (Observe(handle, requireData: false, requireEmpty: _original is null) is null)
                throw new IOException("Owned WAV identity or path did not match the reserved target.");
            if (_original is null)
            {
                var hold = ReOpenFile(handle, ReadAttributes, ShareAll, OpenReparsePoint);
                if (hold.IsInvalid)
                {
                    hold.Dispose();
                    throw new IOException("Could not hold the verified WAV object.");
                }
                _original = hold;
            }

            // Retries truncate only after checking the same actual handle
            // that the WAV writer will use, including its full file ID.
            var stream = new FileStream(handle, FileAccess.ReadWrite);
            try
            {
                stream.SetLength(0);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal string? Evidence(bool requireData = false) =>
        _original is { IsClosed: false } original ? Observe(original, requireData, requireEmpty: false) : null;

    private string? Observe(SafeFileHandle handle, bool requireData, bool requireEmpty)
    {
        if (handle.IsInvalid || !ReadTag(handle, 9, out var tag, (uint)Marshal.SizeOf<AttributeTag>()) ||
            (tag.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !ReadStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) ||
            standard.Links != 1 || standard.DeletePending != 0 || standard.Directory != 0 ||
            standard.Length < (requireData ? 1 : 0) || (requireEmpty && standard.Length != 0))
            return null;
        var identity = ReadIdentity(handle);
        if (!string.Equals(identity, expected, StringComparison.Ordinal)) return null;
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        return length > 0 && length < name.Capacity && length == name.Length &&
            string.Equals(name.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) ? identity : null;
    }

    internal static string? ReadIdentity(SafeFileHandle handle)
    {
        if (!ReadId(handle, 18, out var id, (uint)Marshal.SizeOf<FileId>()) ||
            id.Volume == 0 || (id.Low == 0 && id.High == 0))
            return null;
        return FormattableString.Invariant($"{id.Volume:x16}{id.Low:x16}{id.High:x16}");
    }

    public void Dispose() => _original?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        internal long Allocation, Length;
        internal uint Links;
        internal byte DeletePending, Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadId(SafeFileHandle handle, int type, out FileId info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadTag(SafeFileHandle handle, int type, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadStandard(SafeFileHandle handle, int type, out StandardInfo info, uint size);
}
