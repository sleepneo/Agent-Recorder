using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

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
    public void Task411ExistingDiagnosticDirectoryAndSourceKeepTheirOriginalHashes()
    {
        var oldDirectory = Directory.CreateDirectory(Path.Combine(_dataDir, "failed", "rec_existing")).FullName;
        var oldVideo = Path.Combine(oldDirectory, "video.mp4");
        var oldJson = Path.Combine(oldDirectory, "diagnostics.json");
        var incoming = Path.Combine(_dataDir, "incoming.mp4");
        File.WriteAllText(oldVideo, "old video");
        File.WriteAllText(oldJson, "old diagnostic");
        File.WriteAllText(incoming, "new video");
        var oldVideoHash = Hash(oldVideo);
        var oldJsonHash = Hash(oldJson);
        var incomingHash = Hash(incoming);

        var result = new TempRetentionPolicy(_dataDir).OnFailure("rec_existing", incoming, null);

        Assert.Equal(oldVideoHash, Hash(oldVideo));
        Assert.Equal(oldJsonHash, Hash(oldJson));
        Assert.Equal(incomingHash, Hash(incoming));
        Assert.False(result.VideoMoved);
        Assert.Null(result.VideoTargetPath);
        Assert.Null(result.FailedDirectoryPath);
        Assert.NotEmpty(result.Errors);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Task411RSourceReplacementAfterIdentityCheckCannotBecomeDiagnosticMedia()
    {
        var source = Path.Combine(_dataDir, "owned.mp4");
        var backup = Path.Combine(_dataDir, "original-backup.mp4");
        var replacement = Path.Combine(_dataDir, "replacement.mp4");
        File.WriteAllText(source, "observed original");
        File.WriteAllText(replacement, "foreign replacement");
        var originalHash = Hash(source);
        var replacementHash = Hash(replacement);
        Exception? replacementError = null;
        var attempts = 0;
        var policy = new TempRetentionPolicy(_dataDir)
        {
            BeforeDiagnosticRenameForTests = (checkedSource, _) =>
            {
                Assert.Equal(source, checkedSource);
                attempts++;
                replacementError = Record.Exception(() =>
                {
                    File.Move(source, backup);
                    File.Move(replacement, source);
                });
            }
        };

        var result = policy.OnFailure("rec_checked", source, null);

        Assert.Equal(1, attempts);
        Assert.True(replacementError is IOException,
            $"Replacement was not blocked. Moved={result.VideoMoved}, target_has_replacement={result.VideoTargetPath is { } target && Hash(target) == replacementHash}.");
        Assert.False(File.Exists(backup));
        Assert.Equal(replacementHash, Hash(replacement));
        Assert.True(result.VideoMoved);
        Assert.Empty(result.Errors);
        Assert.Equal(originalHash, Hash(result.VideoTargetPath!));
        Assert.False(File.Exists(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Task411RSameVolumeRenamePreservesTheOriginalIdentity(bool empty)
    {
        var source = Path.Combine(_dataDir, "owned.mp4");
        File.WriteAllText(source, empty ? "" : "owned media");
        var original = WindowsReadOnlyFileMetadataProbe.Instance.Observe(source).Before!.Identity;

        var result = new TempRetentionPolicy(_dataDir).OnFailure("rec_identity", source, null);

        Assert.True(result.VideoMoved);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(source));
        Assert.Equal(original, WindowsReadOnlyFileMetadataProbe.Instance.Observe(result.VideoTargetPath!).Before!.Identity);
        Assert.Equal(empty ? "" : "owned media", File.ReadAllText(result.VideoTargetPath!));
    }

    [Fact]
    public void Task411ROccupiedTargetAfterSourceCheckIsNotReplaced()
    {
        var source = Path.Combine(_dataDir, "owned.mp4");
        File.WriteAllText(source, "owned media");
        var original = Hash(source);
        string? occupied = null;
        string? winner = null;
        var policy = new TempRetentionPolicy(_dataDir)
        {
            BeforeDiagnosticRenameForTests = (_, target) =>
            {
                occupied = target;
                File.WriteAllText(target, "target won");
                winner = Hash(target);
            }
        };

        var result = policy.OnFailure("rec_target", source, null);

        Assert.NotNull(occupied);
        Assert.NotNull(winner);
        Assert.False(result.VideoMoved);
        Assert.Null(result.VideoTargetPath);
        Assert.Contains(result.Errors, error => error.StartsWith("retention_move_failed:video.mp4:", StringComparison.Ordinal));
        Assert.Equal(winner, Hash(occupied));
        Assert.Equal(original, Hash(source));
    }

    [Fact]
    public void Task411RUnverifiableSourceAliasKeepsBothNames()
    {
        var source = Path.Combine(_dataDir, "owned.mp4");
        var alias = Path.Combine(_dataDir, "another-name.mp4");
        File.WriteAllText(source, "shared file");
        WindowsReadOnlyFileMetadataNativeTests.HardLink(alias, source);

        var result = new TempRetentionPolicy(_dataDir).OnFailure("rec_alias", source, null);

        Assert.False(result.VideoMoved);
        Assert.Null(result.VideoTargetPath);
        Assert.Contains(result.Errors, error => error.StartsWith("retention_move_failed:video.mp4:", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(result.FailedDirectoryPath!));
        Assert.Equal("shared file", File.ReadAllText(source));
        Assert.Equal(Hash(source), Hash(alias));
    }

    [Fact]
    public void Task411RCrossVolumeClassificationLeavesTheSourceInPlace()
    {
        var source = Path.Combine(_dataDir, "owned.mp4");
        File.WriteAllText(source, "owned media");
        var original = WindowsReadOnlyFileMetadataProbe.Instance.Observe(source).Before!.Identity;
        var volumeChecks = 0;
        var policy = new TempRetentionPolicy(_dataDir)
        {
            DiagnosticTargetVolumeForTests = observed =>
            {
                Assert.Equal(original!.VolumeSerialNumber, observed);
                volumeChecks++;
                return observed == 1 ? 2UL : 1UL;
            }
        };

        var result = policy.OnFailure("rec_cross_volume", source, null);

        Assert.Equal(1, volumeChecks);
        Assert.False(result.VideoMoved);
        Assert.Null(result.VideoTargetPath);
        Assert.Equal("retention_move_failed:video.mp4:cross_volume", Assert.Single(result.Errors));
        Assert.Empty(Directory.GetFileSystemEntries(result.FailedDirectoryPath!));
        Assert.Equal(original, WindowsReadOnlyFileMetadataProbe.Instance.Observe(source).Before!.Identity);
        Assert.Equal("owned media", File.ReadAllText(source));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("failed/rec_reserved")]
    public void Task411AnOccupiedFileDoesNotBecomeADiagnosticDirectory(string occupied)
    {
        var path = Path.Combine(_dataDir, occupied);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "foreign file");
        var source = Path.Combine(_dataDir, "incoming.mp4");
        File.WriteAllText(source, "incoming");

        var result = new TempRetentionPolicy(_dataDir).OnFailure("rec_reserved", source, null,
            _ => throw new InvalidOperationException("The old namespace must not receive JSON."));

        Assert.Equal("foreign file", File.ReadAllText(path));
        Assert.Equal("incoming", File.ReadAllText(source));
        Assert.False(result.VideoMoved);
        Assert.Null(result.FailedDirectoryPath);
        Assert.Equal("retention_directory_unavailable", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("nested\\escaped")]
    [InlineData("CON.txt")]
    [InlineData("rec_trailing.")]
    public void Task411RejectsNonLocalOrAmbiguousDiagnosticNames(string name)
    {
        var source = Path.Combine(_dataDir, "incoming.mp4");
        File.WriteAllText(source, "incoming");
        var result = new TempRetentionPolicy(_dataDir).OnFailure(name, source, null);
        Assert.Null(result.FailedDirectoryPath);
        Assert.False(result.VideoMoved);
        Assert.Equal("incoming", File.ReadAllText(source));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "failed")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("data")]
    [InlineData("source")]
    public void Task411RejectsRedirectedFailureRootDataAncestorOrSource(string location)
    {
        var target = Directory.CreateDirectory(Path.Combine(_dataDir, "target")).FullName;
        if (location == "data") Directory.CreateDirectory(Path.Combine(target, "host"));
        var link = Path.Combine(_dataDir, location == "failed" ? "failed" : "link");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"New-Item -ErrorAction Stop -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var source = Path.Combine(location == "source" ? link : _dataDir, "incoming.mp4");
            var physical = Path.Combine(location == "source" ? target : _dataDir, "incoming.mp4");
            File.WriteAllText(physical, "incoming");
            var policy = new TempRetentionPolicy(location == "data" ? Path.Combine(link, "host") : _dataDir);
            var result = policy.OnFailure("rec_redirected", source, null);
            Assert.False(result.VideoMoved);
            Assert.Equal("incoming", File.ReadAllText(physical));
            if (location == "source")
            {
                Assert.NotNull(result.FailedDirectoryPath);
                Assert.Empty(Directory.GetFileSystemEntries(result.FailedDirectoryPath));
                Assert.Contains(result.Errors, error => error.StartsWith("retention_move_failed:", StringComparison.Ordinal));
            }
            else
            {
                Assert.Null(result.FailedDirectoryPath);
                Assert.Empty(Directory.GetFileSystemEntries(location == "data" ? Path.Combine(target, "host") : target));
            }
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Task411JsonClaimAfterPartialMovePreservesTheWinnerAndBothMedia()
    {
        var video = Path.Combine(_dataDir, "incoming.mp4");
        var audio = Path.Combine(_dataDir, "incoming.wav");
        File.WriteAllText(video, "new video");
        File.WriteAllText(audio, "locked audio");
        var incomingHash = Hash(video);
        var audioHash = Hash(audio);
        var dir = Path.Combine(_dataDir, "failed", "rec_partial");
        var json = Path.Combine(dir, "diagnostics.json");
        string? claimedHash = null;
        using (var locked = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new TempRetentionPolicy(_dataDir).OnFailure("rec_partial", video, audio, errors =>
            {
                Assert.Contains(errors, error => error.StartsWith("retention_move_failed:audio.wav:", StringComparison.Ordinal));
                File.WriteAllText(json, "earlier JSON claim");
                claimedHash = Hash(json);
                return "{\"different\":true}";
            });
            Assert.Equal(dir, result.FailedDirectoryPath);
            Assert.True(result.VideoMoved);
            Assert.False(result.AudioMoved);
            Assert.Null(result.AudioTargetPath);
            Assert.Equal(incomingHash, Hash(result.VideoTargetPath!));
            Assert.Contains("retention_diagnostics_not_written", result.Errors);
        }
        Assert.NotNull(claimedHash);
        Assert.Equal(claimedHash, Hash(json));
        Assert.Equal(audioHash, Hash(audio));
        Assert.False(File.Exists(video));
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
        Assert.True(File.Exists(audio), "locked audio temp must remain in place");
        Assert.False(File.Exists(video), "video should have been moved out of temp");
    }
}
