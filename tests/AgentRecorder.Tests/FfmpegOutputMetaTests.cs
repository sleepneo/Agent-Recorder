using System;
using System.Diagnostics;
using System.IO;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FfmpegOutputMetaTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"ffmpeg-output-meta-{Guid.NewGuid():N}");

    public FfmpegOutputMetaTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Probe_ExistingValidOutput_SetsConsistentFileMetadata()
    {
        SkipIfNoFfmpeg();
        var path = Path.Combine(_tempDir, "valid.mp4");
        GenerateVideo(path);

        var meta = FfmpegCaptureBackend.Probe(path);

        Assert.True(meta.OutputFileExists);
        Assert.Equal(path, meta.OutputPath);
        Assert.Equal(new FileInfo(path).Length, meta.SizeBytes);
        Assert.True(meta.DurationSeconds > 0);
    }

    [Fact]
    public void Probe_MissingOutput_SetsExistsFalse()
    {
        var path = Path.Combine(_tempDir, "missing.mp4");

        var meta = FfmpegCaptureBackend.Probe(path);

        Assert.False(meta.OutputFileExists);
        Assert.Equal(path, meta.OutputPath);
        Assert.Equal(0, meta.SizeBytes);
    }

    [Fact]
    public void Probe_InvalidExistingFile_SetsExistsFalseWhenProbeFails()
    {
        SkipIfNoFfmpeg();
        var path = Path.Combine(_tempDir, "invalid.mp4");
        File.WriteAllText(path, "not an MP4");

        var meta = FfmpegCaptureBackend.Probe(path);

        Assert.False(meta.OutputFileExists);
        Assert.Equal(path, meta.OutputPath);
    }

    [Fact]
    public void Finalizer_SuccessfulPublish_ReportsFinalPathSizeAndExistence()
    {
        SkipIfNoFfmpeg();
        var videoPath = Path.Combine(_tempDir, "video.mp4");
        var outputPath = Path.Combine(_tempDir, "published.mp4");
        GenerateVideo(videoPath);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath: "",
            outputPath,
            audioPreRoll: null,
            audioSourceKind: AudioCaptureSourceKind.None,
            applyContinuityCheck: false);

        Assert.Null(result.Error);
        Assert.True(result.Meta.OutputFileExists);
        Assert.Equal(outputPath, result.Meta.OutputPath);
        Assert.Equal(new FileInfo(outputPath).Length, result.Meta.SizeBytes);
    }

    [Fact]
    public void Finalizer_FailureWithOldFinal_DoesNotClaimCurrentOutputSuccess()
    {
        SkipIfNoFfmpeg();
        var videoPath = Path.Combine(_tempDir, "video-failure.mp4");
        var outputPath = Path.Combine(_tempDir, "old-final.mp4");
        GenerateVideo(videoPath);
        File.WriteAllText(outputPath, "PRE-EXISTING-FINAL");

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath: Path.Combine(_tempDir, "missing.wav"),
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.NotNull(result.Error);
        Assert.False(result.Meta.OutputFileExists);
        Assert.Null(result.Meta.OutputPath);
        Assert.Equal("PRE-EXISTING-FINAL", File.ReadAllText(outputPath));
    }

    private static void SkipIfNoFfmpeg()
    {
        Assert.True(File.Exists(FfmpegLocator.FfmpegPath), "Bundled FFmpeg not available.");
    }

    private static void GenerateVideo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-y", "-nostats", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=10",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", path
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg failed to start.");
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "FFmpeg fixture generation timed out.");
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(path), stderr);
    }
}
