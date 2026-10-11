using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// Unlike media rename copies, Bundle originals are renamed through their owned
// directory aliases. Deny DELETE sharing so no alias can be substituted between
// precommit validation and the directory rename; the original name stays outside it.
internal sealed class BundlePublicationFileOperations : IPublicationFileOperations
{
    internal static readonly BundlePublicationFileOperations Instance = new();
    public FileStream CreateNew(string path)
    {
        var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), WindowsPublicationFileOperations.CreateAccess | 0x20000,
            1, IntPtr.Zero, WindowsPublicationFileOperations.CreateDisposition, WindowsPublicationFileOperations.CreateFlags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("bundle_copy_create_failed:" + error); }
        try { return new FileStream(handle, FileAccess.Write, 81920, false); }
        catch { WindowsPublicationFileOperations.NoReplace.DeleteOriginal(handle); handle.Dispose(); throw; }
    }
    public void Rename(SafeFileHandle original, string finalPath) => throw new NotSupportedException("Bundle originals use directory commit.");
    public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original) => WindowsPublicationFileOperations.NoReplace.DeleteOriginal(original);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
}
