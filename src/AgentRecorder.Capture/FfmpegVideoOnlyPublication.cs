using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentRecorder.Infrastructure;

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
        // Atomic exclusive directory creation: never take ownership of an existing directory.
        if (!CreateDirectoryW(ExtendedDirectoryPath(directory), IntPtr.Zero))
            throw new ApiException(500, "ENCODER_ERROR", "Could not create owned video staging.");
        var run = new VideoOnlyRun(cfg, _output, directory, Path.Combine(directory, "capture_video.mp4"), id, publisher);
        _videoOnlyRun = run;
        try
        {
            File.WriteAllText(Path.Combine(directory, "owner.json"), JsonSerializer.Serialize(new { run.RecordingId, run.FinalPath }));
            using var reserved = new FileStream(run.SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch
        {
            CompleteFailedVideoOnlyStart(run);
            throw new ApiException(500, "ENCODER_ERROR", "Could not prepare owned video staging.");
        }
    }

    private static string ExtendedDirectoryPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    private void CompleteFailedVideoOnlyStart(VideoOnlyRun run)
    {
        var meta = FailedVideoOnly(run, "encoder_start_failed");
        run.Config.StorageSafety?.EndFfmpegVideoOnlyPublication();
        RetainVideoOnlySource(run, success: false);
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
        BeforeVideoOnlyCleanupForTests?.Invoke();
        run.Config.StorageSafety?.EndFfmpegVideoOnlyPublication();
        RetainVideoOnlySource(run, success);
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

    private static bool OwnsStaging(VideoOnlyRun run) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(run.Directory)),
            Path.GetDirectoryName(Path.GetFullPath(run.FinalPath)), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(run.Directory) == ".agent-recorder-" + run.RecordingId &&
        Path.GetFullPath(run.SourcePath) == Path.Combine(Path.GetFullPath(run.Directory), "capture_video.mp4");

    private static void RetainVideoOnlySource(VideoOnlyRun run, bool success)
    {
        if (!OwnsStaging(run)) return;
        try
        {
            var retention = new TempRetentionPolicy();
            if (success) retention.OnSuccess(run.SourcePath, null);
            else
            {
                var result = retention.OnFailure(run.RecordingId, run.SourcePath, null);
                if (result.FailedDirectoryPath is { } failed)
                    File.WriteAllText(Path.Combine(failed, "diagnostics.json"), JsonSerializer.Serialize(new { reason = "video_finalization_failed", run.FinalPath }));
            }
            if (File.Exists(run.SourcePath))
            {
                // Retention I/O failure preserves the exclusively owned directory
                // and durable owner marker for diagnosis, never sweeps user output.
                File.WriteAllText(Path.Combine(run.Directory, "diagnostics.json"), "{\"reason\":\"staged_diagnostics_retained\"}");
                return;
            }
            File.Delete(Path.Combine(run.Directory, "owner.json"));
            Directory.Delete(run.Directory, recursive: false);
        }
        catch { } // Keep owned diagnostics if cleanup is unavailable.
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
