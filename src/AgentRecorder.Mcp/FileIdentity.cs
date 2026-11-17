using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Mcp;

internal static class FileIdentity
{
    // Read-only Windows file identity, not content verification or a publisher signature.
    public static string Read(string path)
    {
        LocalPaths.Normalize(path);
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var info, 24))
            throw new ContractException("data_identity_unavailable");
        return $"{info.Volume:X16}:{info.Low:X16}{info.High:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume; public ulong Low; public ulong High; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out IdInfo info, uint size);
}
