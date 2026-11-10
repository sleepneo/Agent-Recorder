using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

public sealed partial class FfmpegCaptureBackend
{
    private VideoOnlyRun? _videoOnlyRun;
    internal Func<CaptureConfig, IReadOnlyList<string>>? VideoInputArgumentsForTests { get; set; }
    internal Action<ProcessStartInfo>? ConfigureProcessForTests { get; set; }
    internal Action<string>? BeforeStagingCreateForTests { get; set; }
    internal Action<string>? BeforeVideoOnlyFinalizeForTests { get; set; }
    internal IOutputProber? VideoOnlyProberForTests { get; set; }
    internal Action? BeforeVideoOnlyTerminalForTests { get; set; }
    internal Action? AfterVideoOnlyTerminalForTests { get; set; }
    internal Action? BeforeVideoOnlyCleanupForTests { get; set; }
    internal Action? WaitForVideoOnlyOwnerForTests { get; set; }
    internal Func<bool, IStagingToFinalPublisher> VideoOnlyPublisherFactory { get; set; } = replace =>
        new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles:
            replace ? WindowsPublicationFileOperations.Instance : WindowsPublicationFileOperations.NoReplace);
    internal void KillVideoEncoderForTests() => _proc!.Kill(entireProcessTree: true);
    internal string? VideoOnlyStagingPath => _videoOnlyRun?.SourcePath;
    internal Task<OutputMeta>? VideoOnlyCompletion => _videoOnlyRun?.Completion.Task;
    internal bool IsVideoOnlyOutputSettled => _videoOnlyRun is not { } run || Volatile.Read(ref run.OutputSettled) != 0;
    internal IReadOnlyList<string>? ExecutedArguments => _proc?.StartInfo.ArgumentList.ToArray();

    internal static bool UsesVideoOnlyPublication(CaptureConfig cfg) =>
        string.Equals(cfg.Mode, "video", StringComparison.OrdinalIgnoreCase) &&
        !cfg.Microphone && cfg.AudioSourceKind == AudioCaptureSourceKind.None &&
        !cfg.StorageIntermediatePublication;

    private sealed class VideoOnlyRun
    {
        internal VideoOnlyRun(CaptureConfig config, string finalPath, string directory, string sourcePath,
            string recordingId, IStagingToFinalPublisher publisher)
        {
            Config = config; FinalPath = finalPath; Directory = directory; SourcePath = sourcePath;
            RecordingId = recordingId; Publisher = publisher; Token = Cancellation.Token;
        }
        internal readonly CaptureConfig Config;
        internal readonly string FinalPath, Directory, SourcePath, RecordingId;
        internal readonly IStagingToFinalPublisher Publisher;
        internal OwnedBundleDirectory? OriginalDirectory;
        internal SafeFileHandle? OriginalMedia, OriginalMarker;
        internal string MarkerPath => Path.Combine(Directory, "owner.json");
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly CancellationToken Token;
        internal readonly FileCommitGate Gate = new();
        internal readonly TaskCompletionSource<OutputMeta> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StderrDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool OwnerClaimed;
        internal bool DisposeRequested;
        internal int OutputSettled;
        internal int ExitCode = -1;
        internal OutputMeta? Terminal;

        internal void ReleaseStaging()
        {
            OriginalMedia?.Dispose();
            OriginalMarker?.Dispose();
            OriginalDirectory?.Dispose();
        }
    }

    private void PrepareVideoOnlyRun(CaptureConfig cfg)
    {
        cfg.WritePaths?.ValidateOutputPath(cfg);
        cfg.StorageSafety?.ReserveFfmpegVideoOnlyPublication();
        cfg.StorageSafety?.EnsureAdmission();
        var parent = Path.GetDirectoryName(Path.GetFullPath(_output))!;
        var id = "ffmpeg-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(parent, ".agent-recorder-" + id);
        var publisher = VideoOnlyPublisherFactory(cfg.OutputConflictPolicy == "overwrite");
        BeforeStagingCreateForTests?.Invoke(directory);
        var run = new VideoOnlyRun(cfg, _output, directory, Path.Combine(directory, "capture_video.mp4"), id, publisher)
        {
            OriginalDirectory = CreateVideoOnlyStagingDirectory(directory)
        };
        _videoOnlyRun = run;
        try
        {
            using (var marker = new FileStream(run.MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                run.OriginalMarker = HoldVideoOnlyOriginal(marker.SafeFileHandle);
                JsonSerializer.Serialize(marker, new { run.RecordingId, run.FinalPath });
            }
            using var reserved = new FileStream(run.SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            run.OriginalMedia = HoldVideoOnlyOriginal(reserved.SafeFileHandle);
        }
        catch
        {
            CompleteFailedVideoOnlyStart(run);
            throw new ApiException(500, "ENCODER_ERROR", "Could not prepare owned video staging.");
        }
    }

    private static OwnedBundleDirectory? CreateVideoOnlyStagingDirectory(string path)
    {
        try { return OwnedBundleDirectory.Create(path); }
        catch (IOException)
        {
            // Preserve the existing exclusive creation path if it cannot return
            // a verifiable handle. This run then has no cleanup authority.
            if (!CreateDirectoryW(ExtendedDirectoryPath(path), IntPtr.Zero))
                throw new ApiException(500, "ENCODER_ERROR", "Could not create owned video staging.");
            return null;
        }
    }

    private static string ExtendedDirectoryPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    private const uint VideoOnlyShareAll = 7;
    private const uint VideoOnlyDeleteAccess = 0x10000;

    private static SafeFileHandle? HoldVideoOnlyOriginal(SafeFileHandle original)
        => ReopenVideoOnlyOriginal(original, WindowsReadOnlyFileMetadataProbe.ReadAttributes, VideoOnlyShareAll);

    private static SafeFileHandle? ReopenVideoOnlyOriginal(SafeFileHandle? original, uint access, uint share)
    {
        if (original is null || original.IsClosed || original.IsInvalid) return null;
        var reopened = ReOpenFile(original, access, share, 0);
        if (!reopened.IsInvalid) return reopened;
        reopened.Dispose();
        return null;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint sharing, uint flags);

    private void CompleteFailedVideoOnlyStart(VideoOnlyRun run)
    {
        var meta = FailedVideoOnly(run, "encoder_start_failed");
        try
        {
            run.Config.StorageSafety?.EndFfmpegVideoOnlyPublication();
            RetainVideoOnlySource(run, meta, success: false);
        }
        finally { run.ReleaseStaging(); }
        Volatile.Write(ref run.OutputSettled, 1);
        lock (_lock) run.OwnerClaimed = true;
        PublishVideoOnlyTerminal(run, meta, -1);
    }

    // Completion is the release signal. Select once, install all logical fields,
    // then signal; actual output cleanup has its separate OutputSettled boundary.
    private OutputMeta PublishVideoOnlyTerminal(VideoOnlyRun run, OutputMeta candidate, int exitCode)
    {
        BeforeVideoOnlyTerminalForTests?.Invoke();
        OutputMeta canonical;
        lock (_lock)
        {
            if (run.Terminal is null)
            {
                run.Terminal = candidate;
                run.ExitCode = exitCode;
                if (ReferenceEquals(_videoOnlyRun, run))
                {
                    _completionMeta = candidate;
                    _hasExited = true;
                }
                run.Completion.TrySetResult(candidate);
            }
            canonical = run.Terminal;
        }
        AfterVideoOnlyTerminalForTests?.Invoke();
        return canonical;
    }

    private static void CloseVideoOnlyAdmission(VideoOnlyRun run)
    {
        if (run.Completion.Task.IsCompleted) return;
        // Close linearizes with the original-handle rename; cancellation covers copy/probes.
        run.Gate.Close();
        try { run.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private OutputMeta ConvergeVideoOnly(VideoOnlyRun run, bool natural, int? naturalExitCode)
    {
        bool owner;
        lock (_lock)
        {
            owner = !run.OwnerClaimed;
            if (owner) { run.OwnerClaimed = true; if (ReferenceEquals(_videoOnlyRun, run)) _manualStopped = !natural; }
        }
        if (!owner)
        {
            try
            {
                WaitForVideoOnlyOwnerForTests?.Invoke();
                return run.Completion.Task.WaitAsync(AvFinalizer.DefaultMuxTimeout + TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                CloseVideoOnlyAdmission(run);
                return PublishVideoOnlyTerminal(run, FailedVideoOnly(run, "convergence_owner_timeout"), -1);
            }
        }

        var ownedProcess = _proc;
        OutputMeta candidate;
        bool success = false;
        try
        {
            bool forced = false;
            if (!natural && ownedProcess is { } process && !process.HasExited)
            {
                try
                {
                    process.StandardInput.Write('q'); process.StandardInput.Flush();
                    if (!process.WaitForExit(8000)) { forced = true; process.Kill(true); process.WaitForExit(2000); }
                }
                catch
                {
                    forced = true;
                    try { process.Kill(true); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                }
            }
            DrainStdoutReader(StdoutDrainTimeout);
            DrainTask(run.StderrDrained.Task, StdoutDrainTimeout);
            int encoderExitCode;
            try { encoderExitCode = naturalExitCode ?? (ownedProcess?.HasExited == true ? ownedProcess.ExitCode : -1); }
            catch { encoderExitCode = -1; }
            BeforeVideoOnlyFinalizeForTests?.Invoke(run.SourcePath);
            CaptureAbortReason? abort;
            lock (_lock) abort = _abortReason;
            if (forced || encoderExitCode != 0)
                candidate = FailedVideoOnly(run, "encoder_failed");
            else if (abort.HasValue || run.Cancellation.IsCancellationRequested)
                candidate = FailedVideoOnly(run, abort.HasValue ? CaptureAbortReasonCodes.ToCode(abort.Value) : "publication_cancelled");
            else
            {
                if (File.Exists(run.SourcePath)) run.Config.StorageSafety?.ReservePublicationCopy(new FileInfo(run.SourcePath).Length);
                var finalizer = new AvFinalizer(new ExternalProcessRunner(), AvFinalizer.DefaultMuxTimeout,
                    new ScopedPublisher(run.Publisher, run.Gate), VideoOnlyProberForTests ?? new FinalCaptureProber(run.Config, run.FinalPath));
                var result = finalizer.FinalizeAsync(run.SourcePath, "", run.FinalPath, null, AudioCaptureSourceKind.None,
                    applyContinuityCheck: false, audioStderr: null, videoAnchorAvailable: null, audioAnchorAvailable: null,
                    cancellationToken: run.Token, storageSafety: run.Config.StorageSafety).GetAwaiter().GetResult();
                candidate = result.Meta;
                success = result.ExitCode == 0 && !result.TimedOut && string.IsNullOrEmpty(result.Error) &&
                    candidate.OutputFileExists && candidate.SizeBytes > 0;
            }
            lock (_lock) abort = _abortReason;
            if (abort.HasValue) { candidate.StopReason = CaptureAbortReasonCodes.ToCode(abort.Value); success = false; }
        }
        catch
        {
            candidate = FailedVideoOnly(run, run.Config.StorageSafety?.FailureCode ?? "video_finalization_failed");
        }
        candidate.AudioSourceKind = "none";
        candidate.OutputPath = run.FinalPath;
        if (!success)
        {
            candidate.PublicationReceipt = null; candidate.OutputFileExists = false; candidate.SizeBytes = 0;
            candidate.QuantizedTailEvidence = null;
        }
        try
        {
            BeforeVideoOnlyCleanupForTests?.Invoke();
            run.Config.StorageSafety?.EndFfmpegVideoOnlyPublication();
            RetainVideoOnlySource(run, candidate, success);
        }
        finally { run.ReleaseStaging(); }
        StopMicrophoneMonitor();
        // A timed-out Dispose waiter cannot close resources still used by this owner.
        bool disposeRequested;
        lock (_lock)
        {
            // Pair the cleanup handoff with Dispose's request, so neither side misses it.
            disposeRequested = run.DisposeRequested;
            if (!disposeRequested) Volatile.Write(ref run.OutputSettled, 1);
        }
        if (disposeRequested)
        {
            run.Cancellation.Dispose();
            try { ownedProcess?.Dispose(); } catch { }
            Volatile.Write(ref run.OutputSettled, 1);
        }
        var canonical = PublishVideoOnlyTerminal(run, candidate, success ? 0 : -1);
        Action<int, OutputMeta>? callback;
        lock (_lock) callback = natural && ReferenceEquals(_videoOnlyRun, run) ? _onNaturalExit : null;
        if (callback is not null) { try { callback(run.ExitCode, canonical); } catch { } }
        return canonical;
    }

    private static OutputMeta FailedVideoOnly(VideoOnlyRun run, string reason) => new()
    {
        OutputPath = run.FinalPath, OutputFileExists = false, SizeBytes = 0,
        AudioStatus = "not_requested", AudioContinuityStatus = "not_checked", AudioSourceKind = "none",
        StopReason = reason, Warnings = new[] { reason }, StderrLog = reason
    };

    private static void RetainVideoOnlySource(VideoOnlyRun run, OutputMeta meta, bool success)
    {
        try
        {
            var api = WindowsFileMetadataApi.Instance;
            if (run.OriginalDirectory is not { } directory || !OwnedBundleDirectory.IsDirectoryAt(directory.Handle, run.Directory) ||
                !api.Identity(directory.Handle, out var directoryId, out _) || directoryId.VolumeSerialNumber == 0 ||
                (directoryId.IdLow == 0 && directoryId.IdHigh == 0))
                return;

            if (success)
            {
                if (!TryDeleteVideoOnlyOriginal(run.OriginalMedia, run.SourcePath)) return;
            }
            else
            {
                var observed = run.OriginalMedia is { } media
                    ? PublicationHandleObservation.Read(api, media, run.SourcePath, allowEmpty: true) : null;
                if (observed?.Snapshot.Identity is not { } expected) return;
                var result = new TempRetentionPolicy().OnFailure(run.RecordingId, run.SourcePath, null,
                    _ => JsonSerializer.Serialize(new { reason = "video_finalization_failed", run.FinalPath }), expected);
                if (result.Errors.Count > 0)
                    meta.Warnings = (meta.Warnings ?? Array.Empty<string>()).Concat(result.Errors).ToArray();
                if (!result.VideoMoved) return;
            }

            run.OriginalMedia?.Dispose();
            if (!OwnedBundleDirectory.IsDirectoryAt(directory.Handle, run.Directory) ||
                !TryDeleteVideoOnlyOriginal(run.OriginalMarker, run.MarkerPath))
                return;
            run.OriginalMarker?.Dispose();
            // The native disposition rejects a nonempty directory. Never enumerate
            // or delete any entry other than our bound media and marker.
            if (OwnedBundleDirectory.IsDirectoryAt(directory.Handle, run.Directory))
                directory.DeleteEmptyOriginal();
        }
        catch { } // Keep owned diagnostics if cleanup is unavailable.
    }

    private static bool TryDeleteVideoOnlyOriginal(SafeFileHandle? original, string path)
    {
        using var deletion = ReopenVideoOnlyOriginal(original, VideoOnlyDeleteAccess | WindowsReadOnlyFileMetadataProbe.ReadAttributes,
            WindowsReadOnlyFileMetadataProbe.ShareRead);
        return deletion is not null &&
            PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance, deletion, path, allowEmpty: true) is not null &&
            WindowsPublicationFileOperations.NoReplace.DeleteOriginal(deletion) == PublicationCleanupOutcome.MarkedForDeletion;
    }

    private sealed class FinalCaptureProber(CaptureConfig config, string finalPath) : IOutputProber
    {
        public OutputMeta Probe(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase)
            ? ProbeAuthorizedFixedRateCapture(path, config) : FfmpegCaptureBackend.Probe(path);
    }

    private sealed class ScopedPublisher(IStagingToFinalPublisher publisher, IFileCommitGate local) : IStagingToFinalPublisher
    {
        public Task<PublishResult> PublishAsync(string source, string final, CancellationToken cancellationToken = default, IFileCommitGate? commitGate = null)
            => publisher.PublishAsync(source, final, cancellationToken, new CombinedGate(local, commitGate));
        private sealed class CombinedGate(IFileCommitGate local, IFileCommitGate? storage) : IFileCommitGate, IPublicationCopyCapacityGate
        {
            public Task<IPublicationCopyReservation?> BeginCopyAsync(long bytes, CancellationToken token) => storage is IPublicationCopyCapacityGate capacity
                ? capacity.BeginCopyAsync(bytes, token) : Task.FromResult<IPublicationCopyReservation?>(null);
            public bool IsClosed => local.IsClosed || (storage?.IsClosed ?? false);
            public void Close() => local.Close();
            public bool TryCommit(Action move)
            {
                if (storage is null) return local.TryCommit(move);
                return storage.TryCommit(() => { if (!local.TryCommit(move)) throw new OperationCanceledException(); });
            }
        }
    }
}
