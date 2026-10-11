using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// Continuously hold the CREATE_NEW object while retiring its data-write handle
// for TxF. No DACL, owner, privilege or persistent permission is ever changed.
internal sealed class BundleOriginalAccess : IDisposable
{
    internal SafeFileHandle Handle { get; }
    private BundleOriginalAccess(SafeFileHandle handle) => Handle = handle;
    internal static BundleOriginalAccess Hold(SafeFileHandle original)
    {
        var handle = ReOpenFile(original, 0x80 | 0x20000, 7, 0);
        if (handle.IsInvalid)
        { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("bundle_original_hold_failed:" + error); }
        return new(handle);
    }
    internal PublicationCleanupOutcome DeleteOriginal()
    {
        using var deletion = ReOpenFile(Handle, 0x10000 | 0x80, 7, 0);
        return deletion.IsInvalid ? PublicationCleanupOutcome.RetainedHandleUnavailable : WindowsPublicationFileOperations.NoReplace.DeleteOriginal(deletion);
    }
    public void Dispose() => Handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint sharing, uint flags);
}
