using System;
using System.IO;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that <see cref="TempRetentionPolicy.Cleanup"/> only removes
/// expired failed-recording directories under &lt;data-dir&gt;/failed/ and never
/// touches anything outside that scope.
/// </summary>
public sealed class TempRetentionPolicyTests : IDisposable
{
    private readonly string _dataDir;

    public TempRetentionPolicyTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"retention-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void Cleanup_ExpiredFailedDirectory_IsDeleted()
    {
        var failedDir = Path.Combine(_dataDir, "failed", "rec_expired");
        Directory.CreateDirectory(failedDir);
        var filePath = Path.Combine(failedDir, "video.mp4");
        File.WriteAllText(filePath, "diagnostic");
        Directory.SetLastWriteTimeUtc(failedDir, DateTime.UtcNow.AddHours(-25));

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup();

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(failedDir));
    }

    [Fact]
    public void Cleanup_RecentFailedDirectory_IsPreserved()
    {
        var failedDir = Path.Combine(_dataDir, "failed", "rec_recent");
        Directory.CreateDirectory(failedDir);
        var filePath = Path.Combine(failedDir, "audio.wav");
        File.WriteAllText(filePath, "diagnostic");
        Directory.SetLastWriteTimeUtc(failedDir, DateTime.UtcNow.AddHours(-1));

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup();

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(failedDir));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Cleanup_FilesOutsideFailedScope_AreNotTouched()
    {
        var outsideFile = Path.Combine(_dataDir, "outside.txt");
        var outsideDir = Path.Combine(_dataDir, "temp");
        File.WriteAllText(outsideFile, "keep");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "temp.mp4"), "keep");

        var failedDir = Path.Combine(_dataDir, "failed", "rec_expired");
        Directory.CreateDirectory(failedDir);
        File.WriteAllText(Path.Combine(failedDir, "video.mp4"), "diagnostic");
        Directory.SetLastWriteTimeUtc(failedDir, DateTime.UtcNow.AddHours(-25));

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup();

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(outsideFile), "file outside failed/ must not be touched");
        Assert.True(Directory.Exists(outsideDir), "directory outside failed/ must not be touched");
        Assert.True(File.Exists(Path.Combine(outsideDir, "temp.mp4")));
    }

    [Fact]
    public void Cleanup_CustomMaxAge_DeletesOnlyOlderThanThreshold()
    {
        var oldDir = Path.Combine(_dataDir, "failed", "rec_old");
        var newDir = Path.Combine(_dataDir, "failed", "rec_new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(oldDir, "video.mp4"), "old");
        File.WriteAllText(Path.Combine(newDir, "video.mp4"), "new");
        Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddMinutes(-10));
        Directory.SetLastWriteTimeUtc(newDir, DateTime.UtcNow.AddMinutes(-2));

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup(maxAge: TimeSpan.FromMinutes(5));

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void Cleanup_NoFailedDirectory_ReturnsZero()
    {
        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup();
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void OnFailure_LockedFile_IsReportedAsNotMoved()
    {
        var tempDir = Path.Combine(_dataDir, "temp");
        Directory.CreateDirectory(tempDir);
        var video = Path.Combine(tempDir, "rec_video.mp4");
        var audio = Path.Combine(tempDir, "rec_audio.wav");
        File.WriteAllText(video, "video");
        File.WriteAllText(audio, "audio");

        // Keep the audio file locked so File.Move fails.
        using var lockStream = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None);

        var policy = new TempRetentionPolicy(_dataDir);
        var result = policy.OnFailure("rec_locked", video, audio);

        Assert.True(result.VideoMoved);
        Assert.False(result.AudioMoved);
        Assert.NotNull(result.VideoTargetPath);
        Assert.Null(result.AudioTargetPath);
        Assert.Contains(result.Errors, e => e.Contains("audio.wav", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(audio), "locked audio temp must remain in place for later cleanup");
        Assert.False(File.Exists(video), "video should have been moved out of temp");
    }

    [Fact]
    public void CleanupTempOrphans_DeletesOldOwnedFilesOnly()
    {
        var tempDir = Path.Combine(_dataDir, "temp");
        var outsideDir = Path.Combine(_dataDir, "outside");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(outsideDir);

        var oldVideo = Path.Combine(tempDir, "rec_old_video.mp4");
        var oldAudio = Path.Combine(tempDir, "rec_old_audio.wav");
        var freshVideo = Path.Combine(tempDir, "rec_fresh_video.mp4");
        var otherFile = Path.Combine(tempDir, "other.txt");
        var outsideFile = Path.Combine(outsideDir, "rec_old_video.mp4");

        File.WriteAllText(oldVideo, "old video");
        File.WriteAllText(oldAudio, "old audio");
        File.WriteAllText(freshVideo, "fresh video");
        File.WriteAllText(otherFile, "keep");
        File.WriteAllText(outsideFile, "keep outside");

        var oldTime = DateTime.UtcNow.AddHours(-25);
        File.SetLastWriteTimeUtc(oldVideo, oldTime);
        File.SetLastWriteTimeUtc(oldAudio, oldTime);

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.CleanupTempOrphans();

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(oldVideo));
        Assert.False(File.Exists(oldAudio));
        Assert.True(File.Exists(freshVideo), "fresh temp must be preserved");
        Assert.True(File.Exists(otherFile), "non-owned files must be preserved");
        Assert.True(File.Exists(outsideFile), "files outside temp must not be touched");
    }

    [Fact]
    public void Cleanup_CombinesFailedAndTempOrphans()
    {
        var failedDir = Path.Combine(_dataDir, "failed", "rec_old");
        Directory.CreateDirectory(failedDir);
        File.WriteAllText(Path.Combine(failedDir, "video.mp4"), "old failed");
        Directory.SetLastWriteTimeUtc(failedDir, DateTime.UtcNow.AddHours(-25));

        var tempDir = Path.Combine(_dataDir, "temp");
        Directory.CreateDirectory(tempDir);
        var orphan = Path.Combine(tempDir, "orphan_audio.wav");
        File.WriteAllText(orphan, "orphan");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-25));

        var policy = new TempRetentionPolicy(_dataDir);
        int deleted = policy.Cleanup();

        Assert.Equal(2, deleted);
        Assert.False(Directory.Exists(failedDir));
        Assert.False(File.Exists(orphan));
    }
}
