using System;
using System.Collections.Generic;
using System.IO;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

/// <summary>
/// Result of <see cref="TempRetentionPolicy.OnFailure"/>, reporting whether each
/// temporary media file was successfully moved into the controlled failed
/// directory for diagnosis.
/// </summary>
public sealed class TempRetentionResult
{
    public bool VideoMoved { get; init; }
    public string? VideoSourcePath { get; init; }
    public string? VideoTargetPath { get; init; }
    public bool AudioMoved { get; init; }
    public string? AudioSourcePath { get; init; }
    public string? AudioTargetPath { get; init; }

    /// <summary>Verified diagnostic directory acquired for this attempt, if any.</summary>
    public string? FailedDirectoryPath { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Decides how temporary capture files are cleaned up after finalization.
/// On success they are deleted immediately; on failure they are preserved
/// under &lt;data-dir&gt;/failed/&lt;recording-id&gt;/ for later diagnosis.
/// </summary>
public sealed class TempRetentionPolicy
{
    private readonly string _dataDir;
    internal Action<string, string>? BeforeDiagnosticRenameForTests { get; init; }
    internal Func<ulong, ulong>? DiagnosticTargetVolumeForTests { get; init; }

    public TempRetentionPolicy() : this(DataDirResolver.Resolve()) { }

    public TempRetentionPolicy(string dataDir)
    {
        _dataDir = dataDir;
    }

    /// <summary>
    /// Called when finalization succeeds. Deletes temporary files immediately.
    /// </summary>
    public void OnSuccess(string? tempVideo, string? tempAudio)
    {
        TryDelete(tempVideo);
        TryDelete(tempAudio);
    }

    /// <summary>
    /// Called when finalization fails. Moves any existing temp files to a
    /// structured failed directory for diagnostics. Returns a result that
    /// accurately reports each move so callers do not claim a file was
    /// preserved when it is still locked in the temp directory.
    /// </summary>
    public TempRetentionResult OnFailure(string recordingId, string? tempVideo, string? tempAudio) =>
        OnFailure(recordingId, tempVideo, tempAudio, null);

    internal TempRetentionResult OnFailure(string recordingId, string? tempVideo, string? tempAudio,
        Func<IReadOnlyList<string>, string>? diagnostics, ObservedFileIdentity? expectedVideoIdentity = null,
        ObservedFileIdentity? expectedAudioIdentity = null, Action? verifySourceDirectory = null)
    {
        var errors = new List<string>();
        string? failedDir = null, videoTarget = null, audioTarget = null;
        try
        {
            var api = WindowsFileMetadataApi.Instance;
            var dataDir = Path.TrimEndingDirectorySeparator(_dataDir);
            if (!api.Supported || !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(dataDir) ||
                api.DriveType(dataDir[..3]) is not (2 or 3 or 6) || !IsDirectoryName(recordingId))
                throw new IOException("retention_directory_invalid");

            using var chain = new DirectoryChain();
            var current = dataDir[..3];
            chain.Hold(current);
            foreach (var component in dataDir[3..].Split('\\'))
            {
                current = Path.Combine(current, component);
                chain.Hold(current);
            }
            var failedRoot = Path.Combine(dataDir, "failed");
            chain.Verify();
            Directory.CreateDirectory(failedRoot);
            chain.Hold(failedRoot);
            chain.Verify();
            // Use the original NT FILE_CREATE handle. An occupied leaf is never opened.
            using var directory = OwnedBundleDirectory.Create(Path.Combine(failedRoot, recordingId));

            void VerifyDirectory()
            {
                chain.Verify();
                if (!OwnedBundleDirectory.IsDirectoryAt(directory.Handle, directory.Path))
                    throw new IOException("retention_directory_changed");
                verifySourceDirectory?.Invoke();
            }

            videoTarget = MoveForDiagnostics(directory, tempVideo, "video.mp4", errors, VerifyDirectory, expectedVideoIdentity);
            audioTarget = MoveForDiagnostics(directory, tempAudio, "audio.wav", errors, VerifyDirectory, expectedAudioIdentity);
            if (diagnostics is not null)
            {
                try
                {
                    var json = diagnostics(errors);
                    VerifyDirectory();
                    using var file = new FileStream(Path.Combine(directory.Path, "diagnostics.json"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(file);
                    writer.Write(json);
                }
                catch (Exception) { errors.Add("retention_diagnostics_not_written"); }
            }
            VerifyDirectory();
            failedDir = directory.Path;
        }
        catch (Exception) { errors.Add("retention_directory_unavailable"); }

        return new TempRetentionResult
        {
            VideoMoved = videoTarget is not null,
            VideoSourcePath = tempVideo,
            VideoTargetPath = videoTarget,
            AudioMoved = audioTarget is not null,
            AudioSourcePath = tempAudio,
            AudioTargetPath = audioTarget,
            FailedDirectoryPath = failedDir,
            Errors = errors
        };
    }

    private static bool IsDirectoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Trim() != value ||
            value.EndsWith('.') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var stem = value.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem[..3] is "COM" or "LPT") && "123456789¹²³".Contains(stem[3]));
    }

    internal sealed class DirectoryChain : IDisposable
    {
        private readonly List<(string Path, SafeFileHandle Handle)> _directories = new();

        internal void Hold(string path, bool createIfMissing = false)
        {
            var api = WindowsFileMetadataApi.Instance;
            SafeFileHandle Open(out int error) => api.Open(@"\\?\" + path,
                OriginalFileScopeResources.Access, OriginalFileScopeResources.Sharing,
                WindowsReadOnlyFileMetadataProbe.OpenExisting, OriginalFileScopeResources.Flags, out error);
            var handle = Open(out var error);
            if (createIfMissing && handle.IsInvalid && error == 2)
            {
                handle.Dispose();
                // The caller holds the already-verified parent. Reopen the
                // new component without traversing a competing reparse leaf.
                Directory.CreateDirectory(path);
                handle = Open(out _);
            }
            try
            {
                if (!OwnedBundleDirectory.IsDirectoryAt(handle, path))
                    throw new IOException("retention_directory_unavailable");
                _directories.Add((path, handle));
            }
            catch { handle.Dispose(); throw; }
        }

        internal void Verify()
        {
            foreach (var (path, handle) in _directories)
                if (!OwnedBundleDirectory.IsDirectoryAt(handle, path))
                    throw new IOException("retention_directory_changed");
        }

        public void Dispose()
        {
            for (var i = _directories.Count - 1; i >= 0; i--)
                _directories[i].Handle.Dispose();
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private string? MoveForDiagnostics(OwnedBundleDirectory directory, string? sourcePath, string targetName,
        List<string> errors, Action verifyDirectory, ObservedFileIdentity? expectedIdentity)
    {
        if (string.IsNullOrEmpty(sourcePath))
            return null;

        const uint deleteAccess = 0x10000;
        const int notSameDevice = unchecked((int)0xC00000D4);
        var targetPath = Path.Combine(directory.Path, targetName);
        try
        {
            var api = WindowsFileMetadataApi.Instance;
            if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(sourcePath) ||
                api.DriveType(sourcePath[..3]) is not (2 or 3 or 6))
                throw new IOException("retention_source_unavailable");

            // DELETE permits our handle's rename. Withhold WRITE and DELETE sharing
            // so a different opener cannot modify or replace the observed source.
            using var source = api.Open(@"\\?\" + sourcePath, deleteAccess | WindowsReadOnlyFileMetadataProbe.ReadAttributes,
                WindowsReadOnlyFileMetadataProbe.ShareRead, WindowsReadOnlyFileMetadataProbe.OpenExisting,
                WindowsReadOnlyFileMetadataProbe.OpenFlags, out var error);
            if (source.IsInvalid && error is 2 or 3) return null;
            var observed = PublicationHandleObservation.Read(api, source, sourcePath, allowEmpty: true);
            if (observed?.Snapshot.Identity is not { } identity || (expectedIdentity is not null && identity != expectedIdentity))
                throw new IOException("retention_source_unavailable");

            BeforeDiagnosticRenameForTests?.Invoke(sourcePath, targetPath);
            verifyDirectory();
            if (PublicationHandleObservation.Read(api, source, sourcePath, allowEmpty: true) != observed ||
                !api.Identity(directory.Handle, out var targetIdentity, out _) || targetIdentity.VolumeSerialNumber == 0 ||
                (targetIdentity.IdLow == 0 && targetIdentity.IdHigh == 0))
                throw new IOException("retention_binding_unavailable");

            var targetVolume = DiagnosticTargetVolumeForTests?.Invoke(targetIdentity.VolumeSerialNumber) ?? targetIdentity.VolumeSerialNumber;
            var status = identity.VolumeSerialNumber == targetVolume
                ? directory.RenameOriginalHere(source, targetName) : notSameDevice;
            if (status == notSameDevice)
            {
                errors.Add($"retention_move_failed:{targetName}:cross_volume");
                return null;
            }
            if (status != 0)
                throw new IOException("retention_handle_rename_failed:" + status.ToString("x8"));
            return targetPath;
        }
        catch (Exception ex)
        {
            errors.Add($"retention_move_failed:{targetName}:{ex.GetType().Name}");
            // Leave the source in place and report that the move did not complete.
            return null;
        }
    }
}
