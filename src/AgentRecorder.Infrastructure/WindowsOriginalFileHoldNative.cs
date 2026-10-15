using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

internal enum OriginalFileCloseOutcome { NotNeeded, Closed, Failed, Unknown }
internal enum OriginalFileDispositionOutcome { Accepted, Rejected, Indeterminate }
internal sealed record OriginalFileDispositionResult(OriginalFileDispositionOutcome Outcome, int? Win32Error = null);
internal interface IOriginalFileHoldNative : IWindowsFileMetadataApi
{
    OriginalFileCloseOutcome Close(SafeFileHandle handle);
    // Existing test/unsupported adapters fail closed. Only the original leaf is supplied.
    OriginalFileDispositionResult RequestDisposition(SafeFileHandle handle) => new(OriginalFileDispositionOutcome.Rejected, 50);
}

// Existing metadata ABI, with a separately observable close result. No data or
// disposition ABI is classic FileDispositionInfo, never the Ex/POSIX variant.
internal sealed class WindowsOriginalFileHoldNative : IOriginalFileHoldNative
{
    internal static WindowsOriginalFileHoldNative Instance { get; } = new();
    private readonly IWindowsFileMetadataApi _metadata = WindowsFileMetadataApi.Instance;
    public bool Supported => _metadata.Supported;
    public uint DriveType(string root) => _metadata.DriveType(root);
    public SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error)
        => _metadata.Open(path, access, share, creation, flags, out error);
    public bool Basic(SafeFileHandle handle, out FileBasicInformation value, out int error) => _metadata.Basic(handle, out value, out error);
    public bool Standard(SafeFileHandle handle, out FileStandardInformation value, out int error) => _metadata.Standard(handle, out value, out error);
    public bool Identity(SafeFileHandle handle, out FileIdentityInformation value, out int error) => _metadata.Identity(handle, out value, out error);
    public uint FinalPath(SafeFileHandle handle, StringBuilder buffer, int capacity, out int error) => _metadata.FinalPath(handle, buffer, capacity, out error);
    public OriginalFileDispositionResult RequestDisposition(SafeFileHandle handle)
    {
        if (!Supported) return new(OriginalFileDispositionOutcome.Rejected, 50);
        var information = new DispositionInformation { DeleteFile = 1 };
        var accepted = SetFileInformationByHandle(handle, 4, ref information, (uint)Marshal.SizeOf<DispositionInformation>());
        var error = accepted ? 0 : Marshal.GetLastWin32Error();
        return new(accepted ? OriginalFileDispositionOutcome.Accepted : OriginalFileDispositionOutcome.Rejected, accepted ? null : error);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DispositionInformation { internal byte DeleteFile; } // SDK BOOLEAN, not BOOL
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref DispositionInformation information, uint bufferSize);
    public OriginalFileCloseOutcome Close(SafeFileHandle handle)
    {
        // The lifecycle serializes this call after all metadata workers exit.
        // IsClosed is never proof that our native close actually succeeded.
        if (handle.IsClosed || handle.IsInvalid) return OriginalFileCloseOutcome.Unknown;
        if (!CloseHandle(handle.DangerousGetHandle())) return OriginalFileCloseOutcome.Failed;
        handle.SetHandleAsInvalid(); // prevent a second close/finalizer on a reused numeric value
        handle.Dispose();
        return OriginalFileCloseOutcome.Closed;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
