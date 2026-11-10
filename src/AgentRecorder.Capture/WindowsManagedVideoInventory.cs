using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static AgentRecorder.Capture.WindowsDirectoryMetadata;

namespace AgentRecorder.Capture;

internal sealed record ManagedVideoDirectoryEntry(string Name, string ShortName, ObservedFileIdentity? ListedIdentity,
    bool FileCandidate, long? VerifiedLength, string? Reason);

internal sealed record ManagedVideoDirectoryObservation(bool Absent, ManagedVideoDirectoryEntry[] Entries,
    string? FailureReason = null, string?[]? AlternateComponents = null);

internal static class WindowsManagedVideoInventory
{
    internal const int EntryLimit = 512;
    internal const int AncestorLimit = 32;
    internal const int ScanMilliseconds = 1250;
    private const uint ShareRead = WindowsReadOnlyFileMetadataProbe.ShareRead;
    private static readonly WindowsFileMetadataApi Api = WindowsFileMetadataApi.Instance;

    private sealed record DirectoryPin(string Path, string Name, SafeFileHandle Handle, ObservedFileIdentity Identity);
    private sealed record FilePin(string Path, SafeFileHandle Handle, ObservedFileIdentity Identity, DirectoryEntry Entry);
    private sealed class ScanRejected(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    internal static ManagedVideoDirectoryObservation Observe(string root, Action check, Action? afterListingForTests = null)
    {
        check();
        if (!Api.Supported || !OriginalFileHoldService.ValidScopePath(root, Path.Combine(root, "a.mp4")) ||
            Api.DriveType(root[..3]) is not (2 or 3 or 6))
            return new(false, [], "root_unavailable");

        var parts = root[3..].Split('\\');
        if (parts.Length + 1 > AncestorLimit) return new(false, [], "root_unavailable");
        var chain = new List<DirectoryPin>();
        var pins = new List<FilePin>();
        try
        {
            BindDirectory(root[..3], "", OpenDrive(root[..3], ShareRead), null, chain);
            foreach (var name in parts)
            {
                check();
                var parent = chain[^1];
                var path = Path.Combine(parent.Path, name);
                var handle = OpenRelative(parent.Handle, name, directory: true, out var status, sharing: ShareRead);
                if (status == ObjectNameNotFound)
                {
                    handle.Dispose();
                    var stamp = ReadDirectoryStamp(parent.Handle);
                    VerifyChain(chain, check);
                    using var recheck = OpenRelative(parent.Handle, name, directory: true, out var again, sharing: ShareRead);
                    if (again != ObjectNameNotFound || stamp != ReadDirectoryStamp(parent.Handle))
                        throw new ScanRejected("root_changed");
                    return new(true, [], AlternateComponents: ReadAliases(chain, parts.Length, check));
                }
                BindDirectory(path, name, handle, parent.Identity.VolumeSerialNumber, chain);
            }

            var videos = chain[^1];
            var initialStamp = ReadDirectoryStamp(videos.Handle);
            var listing = Enumerate(videos.Handle, check);
            afterListingForTests?.Invoke();
            var entries = new List<ManagedVideoDirectoryEntry>(listing.Count);
            foreach (var entry in listing.Values)
            {
                check();
                entries.Add(ObserveEntry(videos, entry, pins));
            }

            var finalListing = Enumerate(videos.Handle, check);
            if (listing.Count != finalListing.Count ||
                listing.Any(item => !finalListing.TryGetValue(item.Key, out var final) || item.Value != final))
                throw new ScanRejected("root_changed");
            foreach (var pin in pins)
            {
                check();
                if (Inspect(pin.Handle, pin.Path, false, out var identity, out var basic, out var standard) is not null ||
                    identity != pin.Identity || !SameFile(pin.Entry, basic, standard))
                    throw new ScanRejected("root_changed");
            }
            VerifyChain(chain, check);
            if (initialStamp != ReadDirectoryStamp(videos.Handle)) throw new ScanRejected("root_changed");
            return new(false, entries.ToArray(), AlternateComponents: ReadAliases(chain, parts.Length, check));
        }
        catch (ScanRejected ex) { return new(false, [], ex.Reason); }
        finally
        {
            for (int i = pins.Count - 1; i >= 0; i--) pins[i].Handle.Dispose();
            for (int i = chain.Count - 1; i >= 0; i--) chain[i].Handle.Dispose();
        }
    }

    private static void BindDirectory(string path, string name, SafeFileHandle handle, ulong? volume, List<DirectoryPin> chain)
    {
        bool retained = false;
        try
        {
            if (Inspect(handle, path, true, out var identity, out _, out _) is not null ||
                volume is { } expected && identity!.VolumeSerialNumber != expected)
                throw new ScanRejected("root_unavailable");
            chain.Add(new(path, name, handle, identity!));
            retained = true;
        }
        finally { if (!retained) handle.Dispose(); }
    }

    private static void VerifyChain(List<DirectoryPin> chain, Action check)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            check();
            var pin = chain[i];
            using var current = i == 0 ? OpenDrive(pin.Path, ShareRead) :
                OpenRelative(chain[i - 1].Handle, pin.Name, directory: true, out _, sharing: ShareRead);
            if (Inspect(pin.Handle, pin.Path, true, out var original, out _, out _) is not null || original != pin.Identity ||
                Inspect(current, pin.Path, true, out var rebound, out _, out _) is not null || rebound != pin.Identity)
                throw new ScanRejected("root_changed");
        }
    }

    private static string?[] ReadAliases(List<DirectoryPin> chain, int components, Action check)
    {
        var result = new string?[components];
        for (int i = 1; i < chain.Count; i++)
        {
            check();
            var pin = chain[i];
            if (WindowsShortFileName.TryGetAliasPath(pin.Path, Api.ReadShortLeaf(pin.Handle), out var alias) && alias is not null)
                result[i - 1] = Path.GetFileName(alias);
        }
        return result;
    }

    private static (long LastWrite, long ChangeTime) ReadDirectoryStamp(SafeFileHandle handle)
    {
        if (!Api.Basic(handle, out var basic, out _)) throw new ScanRejected("root_unavailable");
        return (basic.LastWriteTime, basic.ChangeTime);
    }

    private static Dictionary<string, DirectoryEntry> Enumerate(SafeFileHandle handle, Action check)
    {
        var result = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
        var buffer = new byte[1024];
        var visited = 0;
        while (true)
        {
            check();
            bool first = visited == 0;
            var status = ReadNext(handle, buffer, out var length, includeAliases: true, restart: first);
            if (status == NoMoreFiles || first && status == NoSuchFile) return result;
            if (status != 0 || !TryReadEntry(buffer, length, out var entry, includeAliases: true) ||
                !NameIsSingleComponent(entry.Name) || entry.ShortName.Length != 0 && !NameIsSingleComponent(entry.ShortName))
                throw new ScanRejected("enumeration_failed");
            if (visited++ == EntryLimit) throw new ScanRejected("scan_limit");
            if (entry.Name is "." or "..") continue;
            if (!result.TryAdd(entry.Name, entry)) throw new ScanRejected("root_changed");
        }
    }

    private static bool NameIsSingleComponent(string name) =>
        name.Length > 0 && !name.Contains('\\') && !name.Contains('/') && !name.Contains('\0');

    private static ManagedVideoDirectoryEntry ObserveEntry(DirectoryPin videos, DirectoryEntry entry, List<FilePin> pins)
    {
        var listed = (entry.IdLow != 0 || entry.IdHigh != 0) && (entry.Attributes & FileAttributes.Directory) == 0
            ? new ObservedFileIdentity(videos.Identity.VolumeSerialNumber, entry.IdLow, entry.IdHigh) : null;
        bool reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        bool candidate = entry.Name.Length > 4 && entry.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
            (reparse || (entry.Attributes & FileAttributes.Directory) == 0);
        ManagedVideoDirectoryEntry Result(long? length = null, string? reason = null) =>
            new(entry.Name, entry.ShortName, listed, candidate, length, reason);

        if (!candidate) return Result();
        if (reparse) return Result(reason: "entry_reparse");
        var path = Path.Combine(videos.Path, entry.Name);
        if (listed is null || !OriginalFileHoldService.ValidScopePath(videos.Path, path))
            return Result(reason: "entry_unverified");

        // FILE_READ_DATA participates in share enforcement. The handle is used
        // exclusively for metadata calls and is kept through the second pass.
        var handle = OpenRelative(videos.Handle, entry.Name, directory: false, out var status, sharing: ShareRead, pinFile: true);
        bool retained = false;
        try
        {
            var failure = Inspect(handle, path, false, out var identity, out var basic, out var standard);
            if (failure is null && identity == listed && SameFile(entry, basic, standard))
            {
                pins.Add(new(path, handle, identity!, entry));
                retained = true;
                return Result(standard.EndOfFile);
            }
            if (status == ObjectNameNotFound || failure is null) throw new ScanRejected("root_changed");
            return Result(reason: status == ReparseEncountered ? "entry_reparse" : failure);
        }
        finally { if (!retained) handle.Dispose(); }
    }

    private static bool SameFile(DirectoryEntry entry, FileBasicInformation basic, FileStandardInformation standard) =>
        entry.Length == standard.EndOfFile && entry.LastWrite == basic.LastWriteTime &&
        entry.ChangeTime == basic.ChangeTime && entry.Attributes == (FileAttributes)basic.Attributes;

    private static string? Inspect(SafeFileHandle handle, string expectedPath, bool directory,
        out ObservedFileIdentity? identity, out FileBasicInformation basic, out FileStandardInformation standard)
    {
        identity = null;
        basic = default;
        standard = default;
        if (handle.IsInvalid || handle.IsClosed || !Api.Basic(handle, out basic, out _) ||
            !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes)) return "entry_unverified";
        var attrs = (FileAttributes)basic.Attributes;
        if ((attrs & FileAttributes.ReparsePoint) != 0) return "entry_reparse";
        if (((attrs & FileAttributes.Directory) != 0) != directory ||
            !Api.Standard(handle, out standard, out _) || standard.DeletePending != 0 ||
            standard.Directory != (directory ? 1 : 0) || standard.EndOfFile < 0 || standard.NumberOfLinks == 0 ||
            !Api.Identity(handle, out var id, out _) || id.VolumeSerialNumber == 0 || (id.IdLow == 0 && id.IdHigh == 0))
            return "entry_unverified";
        if (standard.NumberOfLinks != 1) return "multiple_links";

        var path = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var length = Api.FinalPath(handle, path, path.Capacity, out _);
        if (length == 0 || length >= path.Capacity || length != path.Length ||
            !string.Equals(path.ToString(), @"\\?\" + expectedPath, StringComparison.OrdinalIgnoreCase))
            return "entry_unverified";
        identity = new(id.VolumeSerialNumber, id.IdLow, id.IdHigh);
        return null;
    }
}
