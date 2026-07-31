using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentRecorder.Infrastructure;

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

    /// <summary>Directory the failed-recording artifacts were moved into.</summary>
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
    public TempRetentionResult OnFailure(string recordingId, string? tempVideo, string? tempAudio)
    {
        var failedDir = Path.Combine(_dataDir, "failed", recordingId);
        Directory.CreateDirectory(failedDir);

        var errors = new List<string>();
        var videoMoved = MoveForDiagnostics(failedDir, tempVideo, "video.mp4", errors, out var videoTarget);
        var audioMoved = MoveForDiagnostics(failedDir, tempAudio, "audio.wav", errors, out var audioTarget);

        return new TempRetentionResult
        {
            VideoMoved = videoMoved,
            VideoSourcePath = tempVideo,
            VideoTargetPath = videoTarget,
            AudioMoved = audioMoved,
            AudioSourcePath = tempAudio,
            AudioTargetPath = audioTarget,
            FailedDirectoryPath = failedDir,
            Errors = errors
        };
    }

    /// <summary>
    /// Deletes failed-recording directories and orphaned temp media files older
    /// than <paramref name="maxAge"/>. Defaults to 24 hours. Does not block on
    /// individual deletion failures and never deletes outside &lt;data-dir&gt;.
    /// </summary>
    public int Cleanup(TimeSpan? maxAge = null)
    {
        return CleanupFailed(maxAge) + CleanupTempOrphans(maxAge);
    }

    /// <summary>
    /// Deletes failed-recording directories older than <paramref name="maxAge"/>.
    /// </summary>
    public int CleanupFailed(TimeSpan? maxAge = null)
    {
        var age = maxAge ?? TimeSpan.FromHours(24);
        var failedDir = Path.Combine(_dataDir, "failed");
        if (!Directory.Exists(failedDir))
            return 0;

        int deleted = 0;
        var now = DateTime.UtcNow;
        foreach (var dir in Directory.GetDirectories(failedDir))
        {
            try
            {
                var lastWrite = Directory.GetLastWriteTimeUtc(dir);
                if (now - lastWrite > age)
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
            }
            catch { }
        }
        return deleted;
    }

    /// <summary>
    /// Deletes orphaned temporary media files in &lt;data-dir&gt;/temp that match the
    /// application's naming pattern and are older than <paramref name="maxAge"/>.
    /// Only touches files owned by this application: *_video.mp4 and *_audio.wav.
    /// </summary>
    public int CleanupTempOrphans(TimeSpan? maxAge = null)
    {
        var age = maxAge ?? TimeSpan.FromHours(24);
        var tempDir = Path.Combine(_dataDir, "temp");
        if (!Directory.Exists(tempDir))
            return 0;

        int deleted = 0;
        var now = DateTime.UtcNow;
        foreach (var file in Directory.GetFiles(tempDir))
        {
            try
            {
                var name = Path.GetFileName(file);
                if (!IsOwnedTempMediaFile(name))
                    continue;

                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (now - lastWrite > age)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch { }
        }
        return deleted;
    }

    private static bool IsOwnedTempMediaFile(string fileName)
    {
        return (fileName.EndsWith("_video.mp4", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".", StringComparison.Ordinal))
            || (fileName.EndsWith("_audio.wav", StringComparison.OrdinalIgnoreCase) && !fileName.StartsWith(".", StringComparison.Ordinal));
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

    private static bool MoveForDiagnostics(string failedDir, string? sourcePath, string targetName, List<string> errors, out string? targetPath)
    {
        targetPath = null;
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return false;

        targetPath = Path.Combine(failedDir, targetName);
        try
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"retention_move_failed:{targetName}:{ex.Message}");
            // Best-effort: leave the source in place so it can be cleaned later
            // by the orphan temp cleanup. Do not claim success.
            targetPath = null;
            return false;
        }
    }
}
