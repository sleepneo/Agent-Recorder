using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

internal sealed record PublicationHandleObservation(HandleMetadataSnapshot Snapshot, string Path)
{
    internal static PublicationHandleObservation? Read(IWindowsFileMetadataApi api, SafeFileHandle handle, string requested)
    {
        try
        {
            if (!api.Supported || !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(requested) ||
                api.DriveType(requested[..3]) is not (2 or 3 or 6) || handle.IsInvalid || handle.IsClosed ||
                !api.Basic(handle, out var basic, out _) || !api.Standard(handle, out var standard, out _) ||
                !api.Identity(handle, out var identity, out _)) return null;
            var buffer = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
            var count = api.FinalPath(handle, buffer, buffer.Capacity, out _);
            if (count == 0 || count >= buffer.Capacity || count != buffer.Length) return null;
            var path = buffer.ToString();
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path) ||
                !string.Equals(path, requested, StringComparison.OrdinalIgnoreCase) ||
                !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) ||
                (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                standard.EndOfFile <= 0 || standard.AllocationSize < 0 || standard.NumberOfLinks != 1 ||
                standard.DeletePending != 0 || standard.Directory != 0 || basic.LastWriteTime <= 0 ||
                basic.LastWriteTime > 2650467743999999999L || identity.VolumeSerialNumber == 0 ||
                (identity.IdLow == 0 && identity.IdHigh == 0)) return null;
            return new(new(standard.EndOfFile, standard.AllocationSize, standard.NumberOfLinks,
                (FileAttributes)basic.Attributes, standard.DeletePending, standard.Directory, basic.LastWriteTime,
                new(identity.VolumeSerialNumber, identity.IdLow, identity.IdHigh)), path);
        }
        catch { return null; } // Optional evidence failure does not undo a committed media result.
    }
}
