using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

internal sealed record OriginalFileScopeClose(int DirectoryIndex, bool Leaf, OriginalFileCloseOutcome Outcome);
internal sealed record OriginalFileScopeRetirement(bool Retired, OriginalFileHoldReason? PrimaryReason,
    IReadOnlyList<OriginalFileScopeClose> Resources);
internal sealed record OriginalFileScopeResult(OriginalFileHoldReason Reason, OriginalFileHoldReason? PrimaryReason,
    OriginalFileScopeOwner? Owner, Task<OriginalFileScopeRetirement> Retirement);

// A narrow internal extension of the actual leaf lifecycle. No new host gate,
// independent acquisition timer, raw handles, or independently disposable leaf.
internal sealed class OriginalFileScopeHoldService
{
    private readonly OriginalFileHoldService _leaf;
    private readonly IOriginalFileHoldNative _native;
    internal OriginalFileScopeHoldService(string frozenVideosRoot, IOriginalFileHoldNative? native = null,
        OriginalFileHoldAdmission? admission = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
    {
        _native = native ?? WindowsOriginalFileHoldNative.Instance;
        _leaf = new(frozenVideosRoot, _native, admission: admission, clock: clock, deadline: deadline, dispatch: dispatch);
    }
    internal async Task<OriginalFileScopeResult> AcquireAsync(PublicationIdentityAnchor anchor, CancellationToken cancellation = default)
        => await AcquireCore(anchor, cancellation, null).ConfigureAwait(false);

    internal Task<OriginalFileScopeResult> AcquireBeforeAsync(PublicationIdentityAnchor anchor, long started, CancellationToken cancellation)
        => AcquireCore(anchor, cancellation, started);

    private async Task<OriginalFileScopeResult> AcquireCore(PublicationIdentityAnchor anchor, CancellationToken cancellation, long? started)
    {
        var resources = new OriginalFileScopeResources(_native);
        var result = await _leaf.AcquireScopeAsync(anchor, resources, cancellation, started).ConfigureAwait(false);
        var retirement = Retire(result.Retirement, resources);
        return new(result.Reason, result.PrimaryReason,
            result.Owner is { } leaf ? new OriginalFileScopeOwner(leaf, retirement) : null, retirement);
    }
    private static async Task<OriginalFileScopeRetirement> Retire(Task<OriginalFileHoldRetirement> leaf, OriginalFileScopeResources resources)
    {
        var retired = await leaf.ConfigureAwait(false);
        return new(retired.Retired, retired.PrimaryReason, resources.ClosedResources);
    }
}

internal sealed class OriginalFileScopeOwner : IDisposable
{
    private readonly OriginalFileHoldOwner _leaf;
    internal OriginalFileScopeOwner(OriginalFileHoldOwner leaf, Task<OriginalFileScopeRetirement> retirement)
    { _leaf = leaf; Retirement = retirement; }
    // Both chain and original leaf are observed on their still-held handles.
    // A failed observation supplies no prior snapshot and retains all resources.
    internal Task<OriginalFileHoldObservation> ObserveAsync() => _leaf.ObserveAsync();
    internal OriginalFileBorrowResult Borrow(PublicationIdentityAnchor anchor, Action check,
        Func<OriginalFileBorrowContext, OriginalFileBorrowContinuationResult> continuation)
        => _leaf.Borrow(anchor, check, continuation);
    internal Task<OriginalFileScopeRetirement> Retirement { get; }
    public void Dispose() => _leaf.Dispose();
}

internal sealed class OriginalFileScopeResources
{
    // FILE_LIST_DIRECTORY supplies directory read access for share admission.
    // Attribute-only access is exempt from share checks and cannot enforce the
    // ordinary rename/delete contract. We never enumerate/read directory data.
    internal const uint Access = 1 | WindowsReadOnlyFileMetadataProbe.ReadAttributes;
    internal const uint Sharing = 1 | 2; // compatible readers/writers; never FILE_SHARE_DELETE
    internal const uint Flags = 0x00200000 | 0x02000000; // OPEN_REPARSE_POINT | BACKUP_SEMANTICS
    private readonly IOriginalFileHoldNative _native;
    private readonly List<DirectoryResource> _directories = new();
    private ulong _volume;
    internal IReadOnlyList<OriginalFileScopeClose> ClosedResources { get; private set; } = Array.Empty<OriginalFileScopeClose>();
    private sealed class DirectoryResource(string path)
    {
        internal readonly string Path = path;
        internal SafeFileHandle? Handle;
        internal bool Unknown;
        internal ObservedFileIdentity? Identity;
    }
    internal OriginalFileScopeResources(IOriginalFileHoldNative native) => _native = native;
    internal OriginalFileHoldReason? Open(string[] ancestors, ObservedFileIdentity identity, Action stop)
    {
        _volume = identity.VolumeSerialNumber;
        foreach (var path in ancestors)
        {
            stop();
            var resource = new DirectoryResource(path); _directories.Add(resource);
            try
            {
                resource.Handle = _native.Open(@"\\?\" + path, Access, Sharing, WindowsReadOnlyFileMetadataProbe.OpenExisting, Flags, out var error);
                if (resource.Handle is null) { resource.Unknown = true; return OriginalFileHoldReason.OpenFailed; }
                if (resource.Handle.IsClosed) { resource.Unknown = true; return OriginalFileHoldReason.OpenFailed; }
                if (resource.Handle.IsInvalid) return error switch
                {
                    32 or 33 => OriginalFileHoldReason.SharingViolation,
                    5 => OriginalFileHoldReason.Inaccessible,
                    _ => OriginalFileHoldReason.AncestorUnavailable
                };
            }
            catch { resource.Unknown = true; throw; }
            stop();
            var failure = Sample(resource, stop, out var observed);
            if (failure is not null) return failure;
            resource.Identity = observed;
        }
        return null;
    }
    internal OriginalFileHoldReason? Verify(Action stop)
    {
        foreach (var resource in _directories)
        {
            stop();
            var failure = Sample(resource, stop, out var observed);
            if (failure is not null) return failure;
            if (resource.Identity is null || observed != resource.Identity) return OriginalFileHoldReason.IdentityMismatch;
        }
        return null;
    }
    private OriginalFileHoldReason? Sample(DirectoryResource resource, Action stop, out ObservedFileIdentity? identity)
    {
        identity = null;
        var handle = resource.Handle;
        if (resource.Unknown || handle is null || handle.IsInvalid || handle.IsClosed) return OriginalFileHoldReason.MetadataUnavailable;
        stop(); if (!_native.Basic(handle, out var basic, out _)) return OriginalFileHoldReason.MetadataUnavailable;
        stop(); if (!_native.Standard(handle, out var standard, out _)) return OriginalFileHoldReason.MetadataUnavailable;
        stop(); if (!_native.Identity(handle, out var id, out _)) return OriginalFileHoldReason.MetadataUnavailable;
        if (!WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) ||
            (basic.Attributes & (uint)FileAttributes.Directory) == 0 || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            standard.Directory != 1 || standard.DeletePending != 0 || standard.AllocationSize < 0 || standard.EndOfFile < 0)
            return OriginalFileHoldReason.AncestorUnsafe;
        if (id.VolumeSerialNumber == 0 || (id.IdLow == 0 && id.IdHigh == 0) || id.VolumeSerialNumber != _volume)
            return OriginalFileHoldReason.IdentityMismatch;
        stop(); var buffer = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var length = _native.FinalPath(handle, buffer, WindowsReadOnlyFileMetadataProbe.FinalPathCapacity, out _);
        if (length == 0 || length >= WindowsReadOnlyFileMetadataProbe.FinalPathCapacity || length != buffer.Length) return OriginalFileHoldReason.PathMismatch;
        var path = buffer.ToString(); if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        // A drive root is legal here only as the exact expected root handle.
        // This does not relax the lexical leaf/root admission predicate.
        var driveRoot = resource.Path.Length == 3;
        if (!(driveRoot ? path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
                : WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path)) ||
            !string.Equals(path, resource.Path, StringComparison.OrdinalIgnoreCase)) return OriginalFileHoldReason.PathMismatch;
        stop(); identity = new(id.VolumeSerialNumber, id.IdLow, id.IdHigh); return null;
    }
    internal OriginalFileCloseOutcome CloseAfterLeaf(OriginalFileCloseOutcome leaf, bool leafOpened)
    {
        var receipts = new List<OriginalFileScopeClose>();
        if (leafOpened) receipts.Add(new(-1, true, leaf));
        var aggregate = leaf;
        for (var i = _directories.Count - 1; i >= 0; i--)
        {
            var resource = _directories[i]; OriginalFileCloseOutcome outcome;
            if (resource.Unknown || resource.Handle is null || resource.Handle.IsClosed) outcome = OriginalFileCloseOutcome.Unknown;
            else if (resource.Handle.IsInvalid) { resource.Handle.Dispose(); outcome = OriginalFileCloseOutcome.NotNeeded; }
            else
            {
                try { outcome = _native.Close(resource.Handle); }
                catch { outcome = OriginalFileCloseOutcome.Unknown; }
                if (outcome is not (OriginalFileCloseOutcome.Closed or OriginalFileCloseOutcome.Failed)) outcome = OriginalFileCloseOutcome.Unknown;
            }
            receipts.Add(new(i, false, outcome));
            if (outcome == OriginalFileCloseOutcome.Unknown || aggregate == OriginalFileCloseOutcome.Unknown) aggregate = OriginalFileCloseOutcome.Unknown;
            else if (outcome == OriginalFileCloseOutcome.Failed || aggregate == OriginalFileCloseOutcome.Failed) aggregate = OriginalFileCloseOutcome.Failed;
            else if (outcome == OriginalFileCloseOutcome.Closed) aggregate = OriginalFileCloseOutcome.Closed;
        }
        ClosedResources = Array.AsReadOnly(receipts.ToArray());
        return aggregate;
    }
}
