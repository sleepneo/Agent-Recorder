using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

internal static class WindowsShortFileName
{
    internal sealed record DirectorySnapshot(ObservedFileIdentity Identity, string? ShortLeaf);

    internal static DirectorySnapshot? ReadDirectory(IWindowsFileMetadataApi api, SafeFileHandle handle, string path)
    {
        if (handle.IsInvalid || handle.IsClosed ||
            !api.Basic(handle, out var basic, out _) || !api.Standard(handle, out var standard, out _) ||
            !api.Identity(handle, out var identity, out _) ||
            !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) ||
            (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != (uint)FileAttributes.Directory ||
            standard.Directory != 1 || standard.DeletePending != 0 || standard.AllocationSize < 0 || standard.EndOfFile < 0 ||
            identity.VolumeSerialNumber == 0 || (identity.IdLow == 0 && identity.IdHigh == 0)) return null;
        var final = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var count = api.FinalPath(handle, final, final.Capacity, out _);
        if (count == 0 || count >= final.Capacity || count != final.Length ||
            !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) return null;
        var leaf = path.Length == 3 ? "" : api.ReadShortLeaf(handle);
        return new(new(identity.VolumeSerialNumber, identity.IdLow, identity.IdHigh), leaf);
    }

    internal static bool TryGetAliasPaths(string originalPath, string? fileLeaf, string? dataRootLeaf,
        out IReadOnlyList<string> paths)
    {
        paths = Array.Empty<string>();
        var videos = Path.GetDirectoryName(originalPath)!;
        var root = Path.GetDirectoryName(videos)!;
        if (!TryGetAliasPath(originalPath, fileLeaf, out var fileAlias) ||
            !TryGetAliasPath(root, dataRootLeaf, out var rootAlias)) return false;
        var aliases = new List<string>(3);
        if (fileAlias is not null) aliases.Add(fileAlias);
        if (rootAlias is not null)
        {
            var aliasVideos = Path.Combine(rootAlias, Path.GetFileName(videos));
            aliases.Add(Path.Combine(aliasVideos, Path.GetFileName(originalPath)));
            if (fileAlias is not null) aliases.Add(Path.Combine(aliasVideos, Path.GetFileName(fileAlias)));
        }
        if (aliases.Any(p => !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(p))) return false;
        paths = aliases;
        return true;
    }

    // null = unknown; empty = the original handle has no alternate leaf.
    internal static string? Read(SafeFileHandle handle)
    {
        const int FileAlternateNameInformation = 21;
        const uint StatusObjectNameNotFound = 0xC0000034;
        const int HeaderBytes = sizeof(uint);
        var buffer = new byte[32];
        var status = NtQueryInformationFile(handle, out var io, buffer, (uint)buffer.Length, FileAlternateNameInformation);
        if (status == StatusObjectNameNotFound) return "";
        if (status != 0 || io.Information < HeaderBytes || io.Information > (nuint)buffer.Length) return null;
        var bytes = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (bytes == 0 || (bytes & 1) != 0 || bytes > io.Information - HeaderBytes) return null;
        return Encoding.Unicode.GetString(buffer, HeaderBytes, (int)bytes);
    }

    internal static bool TryGetAliasPath(string originalPath, string? leaf, out string? alias)
    {
        alias = null;
        if (leaf is null) return false;
        if (leaf.Length == 0) return true;
        if (leaf.Length > 12 || leaf.Contains('\uFFFD') || leaf.Any(char.IsWhiteSpace) ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var parts = leaf.Split('.');
        if (parts.Length > 2 || parts[0].Length is < 1 or > 8 ||
            (parts.Length == 2 && parts[1].Length is < 1 or > 3)) return false;
        if (!string.Equals(Path.GetFileName(originalPath), leaf, StringComparison.OrdinalIgnoreCase))
            alias = Path.Combine(Path.GetDirectoryName(originalPath)!, leaf);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal nuint Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint NtQueryInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        [Out] byte[] information, uint length, int informationClass);
}
