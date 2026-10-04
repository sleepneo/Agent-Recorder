using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// Runs only in a disposable query child, before any UI/instance/capture startup.
internal static class WindowsStorageCapacityProbe
{
    internal static int Run()
    {
        try
        {
            var input = Console.ReadLine();
            if (input is null || input.Length > 16384) return 64;
            var paths = JsonSerializer.Deserialize<string[]>(input);
            if (paths is null || paths.Length is < 1 or > 3) return 64;
            Console.Write(JsonSerializer.Serialize(paths.Select(Query).ToArray()));
            return 0;
        }
        catch { return 74; }
    }

    internal static StorageVolumeSample Query(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path))
            throw new IOException("unsupported_storage");
        var existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing)) throw new IOException("storage_directory_invalid");
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing) throw new IOException("storage_volume_unavailable");
            if (GetFileAttributesW(existing) != uint.MaxValue || Marshal.GetLastWin32Error() is not (2 or 3))
                throw new IOException("storage_volume_unavailable");
            existing = parent;
        }
        using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("storage_volume_unavailable");
        var canonical = new StringBuilder(32768);
        // VOLUME_NAME_GUID follows directory mount points and reparse targets.
        var length = GetFinalPathNameByHandleW(handle, canonical, (uint)canonical.Capacity, 1);
        if (length == 0 || length >= canonical.Capacity) throw new IOException("storage_volume_unavailable");
        var finalPath = canonical.ToString();
        if (!finalPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
            throw new IOException("unsupported_storage");
        var end = finalPath.IndexOf('}');
        if (end < 0) throw new IOException("unsupported_storage");
        var volume = finalPath[..(end + 1)] + @"\";
        if (GetDriveTypeW(volume) != 3) throw new IOException("unsupported_storage");
        if (!GetDiskFreeSpaceExW(finalPath, out ulong free, out ulong total, out ulong physicalFree) ||
            total == 0 || free > total || physicalFree > total || free > physicalFree || free > long.MaxValue)
            throw new IOException("storage_capacity_unavailable");
        return new StorageVolumeSample(path, volume.ToUpperInvariant(), (long)free);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong total, out ulong physicalFree);
}
