using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static AgentRecorder.Capture.WindowsDirectoryMetadata;

namespace AgentRecorder.Capture;

internal sealed record AvStagingInventorySample(
    [property: JsonRequired] int ObservedDirectories,
    [property: JsonRequired] int VerifiedFiles,
    [property: JsonRequired] long LogicalBytes,
    [property: JsonRequired] int SkippedEntries,
    [property: JsonRequired] int VisitedEntries,
    [property: JsonRequired] DateTimeOffset SampledAtUtc,
    [property: JsonRequired] string[] ReasonCodes);

internal sealed record AvStagingInventoryObservation(
    [property: JsonRequired] AvStagingInventorySample? Sample,
    [property: JsonRequired] string? FailureReason);

// Only the isolated probe calls the scanner in production. Its paths and limits
// come from the host, and the reply carries only aggregate metadata.
internal static partial class WindowsAvStagingInventory
{
    internal const string ProbeArgument = "--internal-av-staging-inventory";
    internal const int EntryLimit = 512;
    internal const int AncestorLimit = 32;
    internal const int ScanMilliseconds = 1250;
    private const int RequestLimit = 8192;
    private const string DirectoryPrefix = ".agent-recorder-av-";
    private static readonly WindowsFileMetadataApi Api = WindowsFileMetadataApi.Instance;

    internal static int Run()
    {
        try
        {
            var input = new char[RequestLimit];
            var length = Console.In.ReadBlock(input, 0, input.Length);
            if (length == 0 || length == input.Length) return 64;
            var path = JsonSerializer.Deserialize<string>(new string(input, 0, length));
            if (path is null) return 64;
            Console.Write(JsonSerializer.Serialize(Observe(path)));
            return 0;
        }
        catch { return 74; }
    }

    internal static AvStagingInventoryObservation ReadResponse(string response)
    {
        using var document = JsonDocument.Parse(response);
        var envelope = document.RootElement;
        if (!HasDistinctProperties(envelope, 2) || !envelope.TryGetProperty("Sample", out var rawSample) ||
            rawSample.ValueKind != JsonValueKind.Null && !HasDistinctProperties(rawSample, 7))
            throw new IOException("av_staging_inventory_response_invalid");
        var value = envelope.Deserialize<AvStagingInventoryObservation>();
        if (value is { Sample: null, FailureReason: "root_unavailable" or "root_changed" or "scan_deadline" }) return value;
        if (value is { FailureReason: null, Sample: { } sample } && sample.VisitedEntries is >= 0 and <= EntryLimit &&
            sample.ObservedDirectories >= 0 && sample.VerifiedFiles >= 0 && sample.LogicalBytes >= 0 &&
            sample.SkippedEntries >= 0 && sample.SampledAtUtc != default && sample.SampledAtUtc.Offset == TimeSpan.Zero &&
            (long)sample.ObservedDirectories + sample.VerifiedFiles + sample.SkippedEntries <= sample.VisitedEntries &&
            (sample.VerifiedFiles != 0 || sample.LogicalBytes == 0) && (sample.ObservedDirectories != 0 || sample.VerifiedFiles == 0) &&
            sample.ReasonCodes is { Length: <= 8 } reasons && (sample.SkippedEntries == 0 || reasons.Length != 0) &&
            reasons.All(reason => reason is "entry_reparse" or "entry_unavailable" or "entry_changed" or
                "multiple_links" or "entry_limit" or "scan_deadline" or "enumeration_failed" or "logical_bytes_overflow") &&
            reasons.Distinct(StringComparer.Ordinal).Count() == reasons.Length)
            return value;
        throw new IOException("av_staging_inventory_response_invalid");
    }

    private static bool HasDistinctProperties(JsonElement value, int count) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == count && value.EnumerateObject().Select(property => property.Name)
            .Distinct(StringComparer.Ordinal).Count() == count;

    internal static AvStagingInventoryObservation Observe(string root, Action<string>? beforeBindingForTests = null)
    {
        var scan = new Scan();
        if (!Api.Supported || !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(root) ||
            !Path.GetFileName(root).Equals("temp", StringComparison.OrdinalIgnoreCase) || Api.DriveType(root[..3]) is not (2 or 3 or 6))
            return new(null, "root_unavailable");
        var components = root[3..].Split('\\');
        if (components.Length + 1 > AncestorLimit) return new(null, "root_unavailable");

        var chain = new List<BoundDirectory>();
        try
        {
            using var drive = OpenDrive(root[..3]);
            if (ReadMetadata(drive, root[..3], directory: true, null, out var driveIdentity, out _) is not null)
                return new(null, "root_unavailable");
            chain.Add(new(root[..3], "", drive, driveIdentity!));
            foreach (var name in components)
            {
                if (scan.DeadlineReached) return new(null, "scan_deadline");
                var parent = chain[^1];
                var path = Path.Combine(parent.Path, name);
                var handle = OpenRelative(parent.Handle, name, directory: true, out var status);
                if (status == ObjectNameNotFound)
                {
                    handle.Dispose();
                    if (!VerifyChain(chain)) return new(null, "root_changed");
                    using var recheck = OpenRelative(parent.Handle, name, directory: true, out var recheckStatus);
                    return recheckStatus == ObjectNameNotFound ? scan.Observation() : new(null, "root_changed");
                }
                if (ReadMetadata(handle, path, directory: true, null, out var identity, out _) is not null ||
                    identity!.VolumeSerialNumber != driveIdentity!.VolumeSerialNumber)
                {
                    handle.Dispose();
                    return new(null, "root_unavailable");
                }
                chain.Add(new(path, name, handle, identity));
            }

            var boundRoot = chain[^1];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Entries(boundRoot.Handle, scan))
            {
                if (!IsCandidateName(entry.Name)) continue;
                if (!names.Add(entry.Name)) { scan.Skip("entry_changed"); continue; }
                ScanCandidate(boundRoot, entry, scan, beforeBindingForTests);
            }
            return VerifyChain(chain) ? scan.Observation() : new(null, "root_changed");
        }
        catch (Exception) { return new(null, "root_unavailable"); }
        finally
        {
            // The drive handle has its own using declaration.
            for (int i = chain.Count - 1; i > 0; i--) chain[i].Handle.Dispose();
        }
    }

    private static void ScanCandidate(BoundDirectory root, DirectoryEntry entry, Scan scan, Action<string>? beforeBinding)
    {
        var path = Path.Combine(root.Path, entry.Name);
        beforeBinding?.Invoke(path);
        using var handle = BindEntry(root, entry, path, directory: true, scan, out var identity);
        if (handle is null) return;
        if (scan.Directories.Contains(identity!)) { scan.Skip("entry_changed"); return; }

        var candidate = new BoundDirectory(path, entry.Name, handle, identity!);
        var files = new Dictionary<ObservedFileIdentity, long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in Entries(handle, scan))
        {
            if (!IsWriterLeaf(leaf.Name)) continue;
            if (!names.Add(leaf.Name)) { scan.Skip("entry_changed"); continue; }
            var leafPath = Path.Combine(path, leaf.Name);
            beforeBinding?.Invoke(leafPath);
            using var file = BindEntry(candidate, leaf, leafPath, directory: false, scan, out var leafIdentity);
            if (file is null) continue;
            using var recheck = OpenRelative(handle, leaf.Name, directory: false, out var status);
            var failure = ReadMetadata(recheck, leafPath, directory: false, leafIdentity, out _, out var length);
            if (failure is not null) { scan.Skip(OpenFailure(status, failure)); continue; }
            if (scan.Files.Contains(leafIdentity!) || !files.TryAdd(leafIdentity!, length)) scan.Skip("entry_changed");
        }

        if (!Rebind(root.Handle, candidate)) { scan.Skip("entry_changed", 1 + files.Count); return; }
        scan.Directories.Add(identity!);
        foreach (var file in files)
        {
            if (file.Value > long.MaxValue - scan.LogicalBytes) { scan.Skip("logical_bytes_overflow"); continue; }
            scan.LogicalBytes += file.Value;
            scan.Files.Add(file.Key);
        }
    }

    private static SafeFileHandle? BindEntry(BoundDirectory parent, DirectoryEntry entry, string path, bool directory,
        Scan scan, out ObservedFileIdentity? identity)
    {
        identity = null;
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { scan.Skip("entry_reparse"); return null; }
        if (((entry.Attributes & FileAttributes.Directory) != 0) != directory ||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path) || (entry.IdLow == 0 && entry.IdHigh == 0))
        {
            scan.Skip("entry_unavailable");
            return null;
        }
        var expected = new ObservedFileIdentity(parent.Identity.VolumeSerialNumber, entry.IdLow, entry.IdHigh);
        var handle = OpenRelative(parent.Handle, entry.Name, directory, out var status);
        var reason = ReadMetadata(handle, path, directory, expected, out identity, out _);
        if (reason is null) return handle;
        handle.Dispose();
        scan.Skip(OpenFailure(status, reason));
        return null;
    }

    private static string OpenFailure(int status, string reason) => status switch
    {
        ObjectNameNotFound => "entry_changed",
        ReparseEncountered => "entry_reparse",
        _ => reason
    };

    private static bool VerifyChain(List<BoundDirectory> chain)
    {
        for (int i = 0; i < chain.Count; i++)
            if (!Rebind(i == 0 ? null : chain[i - 1].Handle, chain[i])) return false;
        return true;
    }

    private static bool Rebind(SafeFileHandle? parent, BoundDirectory directory)
    {
        using var current = parent is null ? OpenDrive(directory.Path) : OpenRelative(parent, directory.Name, directory: true, out _);
        return ReadMetadata(directory.Handle, directory.Path, directory: true, directory.Identity, out _, out _) is null &&
            ReadMetadata(current, directory.Path, directory: true, directory.Identity, out _, out _) is null;
    }

    private static string? ReadMetadata(SafeFileHandle handle, string path, bool directory,
        ObservedFileIdentity? expected, out ObservedFileIdentity? identity, out long length)
    {
        identity = null;
        length = 0;
        if (handle.IsInvalid || handle.IsClosed || !Api.Basic(handle, out var basic, out _) ||
            !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes)) return "entry_unavailable";
        var attributes = (FileAttributes)basic.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0) return "entry_reparse";
        if (((attributes & FileAttributes.Directory) != 0) != directory) return "entry_changed";
        if (!Api.Standard(handle, out var standard, out _) || !Api.Identity(handle, out var id, out _) ||
            id.VolumeSerialNumber == 0 || (id.IdLow == 0 && id.IdHigh == 0) || standard.EndOfFile < 0 ||
            standard.AllocationSize < 0 || standard.NumberOfLinks == 0) return "entry_unavailable";
        if (standard.DeletePending != 0 || standard.Directory != (directory ? 1 : 0)) return "entry_changed";
        if (standard.NumberOfLinks != 1) return "multiple_links";
        identity = new(id.VolumeSerialNumber, id.IdLow, id.IdHigh);
        if (expected is not null && expected != identity) return "entry_changed";
        var buffer = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var count = Api.FinalPath(handle, buffer, buffer.Capacity, out _);
        if (count == 0 || count >= buffer.Capacity || count != buffer.Length) return "entry_unavailable";
        if (!string.Equals(buffer.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) return "entry_changed";
        length = standard.EndOfFile;
        return null;
    }

    private static bool IsCandidateName(string name) => name.Length == DirectoryPrefix.Length + 32 &&
        name.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.AsSpan(DirectoryPrefix.Length), "N", out _);

    private static bool IsWriterLeaf(string name) => name.Length > "_video.mp4".Length &&
        (name.EndsWith("_video.mp4", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_audio.wav", StringComparison.OrdinalIgnoreCase));

    private sealed record BoundDirectory(string Path, string Name, SafeFileHandle Handle, ObservedFileIdentity Identity);

    private sealed class Scan
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        internal readonly HashSet<string> Reasons = new(StringComparer.Ordinal);
        internal readonly HashSet<ObservedFileIdentity> Directories = new();
        internal readonly HashSet<ObservedFileIdentity> Files = new();
        internal int Visited, Skipped;
        internal long LogicalBytes;
        internal bool DeadlineReached => Stopwatch.GetElapsedTime(_started).TotalMilliseconds >= ScanMilliseconds;

        internal void Skip(string reason, int count = 1)
        {
            Reasons.Add(reason);
            Skipped += count;
        }

        internal AvStagingInventoryObservation Observation() => new(new(Directories.Count, Files.Count, LogicalBytes,
            Skipped, Visited, DateTimeOffset.UtcNow, Reasons.Order(StringComparer.Ordinal).ToArray()), null);
    }
}
