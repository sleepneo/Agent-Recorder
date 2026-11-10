using System.Runtime.InteropServices;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// The A/V owner alone releases this scope. Handles are acquired at creation
// or directly from the WGC publisher, never by adopting a terminal pathname.
internal sealed class AvSplitStaging : IDisposable
{
    private const uint ShareAll = 7;
    private const uint DeleteAccess = 0x10000;
    private const uint ReadData = 0x80000000;
    private static readonly IWindowsFileMetadataApi Api = WindowsFileMetadataApi.Instance;
    private readonly object _gate = new();
    private readonly TempRetentionPolicy.DirectoryChain _ancestors = new();
    private readonly OwnedBundleDirectory _directory;
    private readonly ulong _volume;
    private bool _closed;

    internal string Root { get; }
    internal string DirectoryPath => _directory.Path;
    internal Input Video { get; }
    internal Input Audio { get; }

    internal AvSplitStaging(string root, string nonce, string stem, Action<string>? beforeCreate)
    {
        Root = Path.GetFullPath(root);
        if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(Root) || Api.DriveType(Root[..3]) is not (2 or 3 or 6))
            throw new IOException("av_staging_root_unavailable");
        if (stem.Length == 0 || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("av_staging_stem_invalid");
        var current = Root[..3];
        try
        {
            _ancestors.Hold(current);
            foreach (var component in Root[3..].Split('\\'))
            {
                current = Path.Combine(current, component);
                _ancestors.Hold(current, createIfMissing: true);
            }
            var directory = Path.Combine(Root, ".agent-recorder-av-" + nonce);
            beforeCreate?.Invoke(directory);
            _directory = OwnedBundleDirectory.Create(directory);
            if (!Api.Identity(_directory.Handle, out var id, out _) || id.VolumeSerialNumber == 0 ||
                (id.IdLow == 0 && id.IdHigh == 0))
            {
                _directory.Dispose();
                throw new IOException("av_staging_directory_identity_unavailable");
            }
            _volume = id.VolumeSerialNumber;
        }
        catch
        {
            _ancestors.Dispose();
            throw;
        }
        Video = new Input(this, Path.Combine(DirectoryPath, stem + "_video.mp4"));
        Audio = new Input(this, Path.Combine(DirectoryPath, stem + "_audio.wav"));
    }

    internal bool MatchesVideo(string path, string? root)
    {
        lock (_gate)
            return !_closed && string.Equals(root, Root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, Video.Path, StringComparison.OrdinalIgnoreCase) && DirectoryIsBound();
    }

    internal void VerifyDirectory()
    {
        lock (_gate)
            if (!DirectoryIsBound()) throw new IOException("av_staging_directory_binding_changed");
    }

    private bool DirectoryIsBound()
    {
        if (_closed) return false;
        try
        {
            _ancestors.Verify();
            return OwnedBundleDirectory.IsDirectoryAt(_directory.Handle, DirectoryPath) &&
                Api.Identity(_directory.Handle, out var id, out _) && id.VolumeSerialNumber == _volume;
        }
        catch { return false; }
    }

    internal void CommitVideo(SafeFileHandle copy, string copyPath, string targetPath)
    {
        lock (_gate)
        {
            if (!MatchesVideo(targetPath, Root) || Video.Original is not null ||
                !string.Equals(Path.GetDirectoryName(copyPath), DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
                Observe(copy, copyPath) is null)
                throw new IOException("av_staging_video_handoff_invalid");

            var hold = Reopen(copy, WindowsReadOnlyFileMetadataProbe.ReadAttributes, ShareAll);
            try
            {
                if (hold.IsInvalid) throw new IOException("av_staging_video_handoff_unavailable");
                // Rename relative to the retained directory, without replacing
                // an entry that appeared while the publisher copied the source.
                if (_directory.RenameOriginalHere(copy, Path.GetFileName(targetPath)) != 0)
                    throw new IOException("av_staging_video_target_unavailable");
                // The rename committed. If the handoff cannot be proven,
                // let the WGC caller reject the intermediate without asking
                // the publisher to delete a potentially displaced copy.
                if (Observe(hold, targetPath) is not null)
                    Video.Original = hold;
            }
            finally
            {
                if (!ReferenceEquals(Video.Original, hold)) hold.Dispose();
            }
        }
    }

    private PublicationHandleObservation? Observe(SafeFileHandle? handle, string path, bool allowEmpty = false)
    {
        var observation = handle is null ? null : PublicationHandleObservation.Read(Api, handle, path, allowEmpty);
        return observation?.Snapshot.Identity?.VolumeSerialNumber == _volume ? observation : null;
    }

    internal void Finish(bool writersRetired)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                Video.CloseHandles();
                Audio.CloseHandles();
                // Native empty-directory disposition never visits other entries.
                if (writersRetired && DirectoryIsBound()) _directory.DeleteEmptyOriginal();
            }
            catch { } // Keep staging if disposition is unavailable.
            finally { Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Video.CloseHandles();
            Audio.CloseHandles();
            _directory.Dispose();
            _ancestors.Dispose();
        }
    }

    internal sealed class Input(AvSplitStaging owner, string path)
    {
        internal string Path { get; } = path;
        internal SafeFileHandle? Original;
        private SafeFileHandle? _writerPin;

        internal void RequireAbsent()
        {
            lock (owner._gate)
            {
                owner.VerifyDirectory();
                using var entry = Api.Open(@"\\?\" + Path, WindowsReadOnlyFileMetadataProbe.ReadAttributes, ShareAll,
                    WindowsReadOnlyFileMetadataProbe.OpenExisting, OriginalFileScopeResources.Flags, out var error);
                if (!entry.IsInvalid || error != 2) throw new IOException("av_staging_target_occupied");
            }
        }

        internal void PrepareForDirectWriter(bool pinDuringCapture)
        {
            lock (owner._gate)
            {
                owner.VerifyDirectory();
                var created = Api.Open(@"\\?\" + Path,
                    WindowsReadOnlyFileMetadataProbe.ReadAttributes | (pinDuringCapture ? ReadData : 0),
                    pinDuringCapture ? 3u : ShareAll, 1, 0x00200080, out _);
                using var release = pinDuringCapture ? null : created;
                if (pinDuringCapture) _writerPin = created;
                if (created.IsInvalid) throw new IOException("av_staging_create_failed");
                Original = Reopen(created, WindowsReadOnlyFileMetadataProbe.ReadAttributes, ShareAll);
                // Deny ordinary replacement during launch and capture, while
                // allowing the existing external writer to open for writing.
                if (owner.Observe(Original, Path, allowEmpty: true) is null)
                    throw new IOException("av_staging_original_unavailable");
            }
        }

        internal void ReleaseWriterPin()
        {
            lock (owner._gate)
            {
                _writerPin?.Dispose();
                _writerPin = null;
            }
        }

        internal ObservedFileIdentity? Identity()
        {
            lock (owner._gate)
                return owner.DirectoryIsBound() ? owner.Observe(Original, Path, allowEmpty: true)?.Snapshot.Identity : null;
        }

        internal SafeFileHandle? PinForRead()
        {
            lock (owner._gate)
                return OpenVerified(ReadData, WindowsReadOnlyFileMetadataProbe.ShareRead, allowEmpty: false);
        }

        internal void DeleteOriginal()
        {
            lock (owner._gate)
            {
                using var deletion = OpenVerified(DeleteAccess, WindowsReadOnlyFileMetadataProbe.ShareRead, allowEmpty: true);
                if (deletion is not null) WindowsPublicationFileOperations.NoReplace.DeleteOriginal(deletion);
            }
        }

        private SafeFileHandle? OpenVerified(uint access, uint share, bool allowEmpty)
        {
            if (!owner.DirectoryIsBound() || Original is null || Original.IsClosed || Original.IsInvalid) return null;
            var handle = Reopen(Original, access | WindowsReadOnlyFileMetadataProbe.ReadAttributes, share);
            if (owner.Observe(handle, Path, allowEmpty) is not null) return handle;
            handle.Dispose();
            return null;
        }

        internal void CloseHandles()
        {
            _writerPin?.Dispose();
            Original?.Dispose();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "ReOpenFile", SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle Reopen(SafeFileHandle original, uint access, uint sharing, uint flags = 0);
}
