using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies the split A/V boundary between diagnostic staging artifacts and
/// the public, approved final output path.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvSplitFailedOutputPathContractTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public AvSplitFailedOutputPathContractTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"avsplit-output-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir);
        DataDirResolver.SetOverride(_dataDir);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void Abort_OutputMetaUsesFinalPath_WhileFailedRetentionKeepsPartialVideo()
    {
        var validVideo = CreateValidVideo();
        var validAudio = CreateValidAudio();
        var cfg = CreateConfig();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var backend = new AvSplitCaptureBackend(
            new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            new FakeExternalProcessRunner(),
            new TempRetentionPolicy(_dataDir));

        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        var meta = backend.Abort(CaptureAbortReason.DisplayUnavailable);

        Assert.Equal(cfg.OutputPath, meta.OutputPath);
        Assert.Equal(0, meta.SizeBytes);
        Assert.False(meta.OutputFileExists);
        Assert.False(File.Exists(cfg.OutputPath));
        Assert.NotEqual(video.OutputPath, meta.OutputPath);

        var failedVideo = Path.Combine(backend.FailedArtifactsDirectory!, "video.mp4");
        Assert.True(File.Exists(failedVideo), "the partial video must remain available under failed retention");
    }

    [Fact]
    public void AudioHelperFailure_OutputMetaUsesFinalPathAndDoesNotRunMux()
    {
        var validVideo = CreateValidVideo();
        var validAudio = CreateValidAudio();
        var cfg = CreateConfig();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "audio_endpoint_inactive"
        });
        var video = new FakeVideoCaptureWorker();
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(
            new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            runner,
            new TempRetentionPolicy(_dataDir));

        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.NotNull(backend.LastMeta);
        var meta = backend.LastMeta!;

        Assert.Equal(cfg.OutputPath, meta.OutputPath);
        Assert.Equal(0, meta.SizeBytes);
        Assert.False(meta.OutputFileExists);
        Assert.Equal("audio_endpoint_inactive", meta.AudioHelperErrorCode);
        Assert.Equal(0, runner.RunCallCount);
        Assert.False(File.Exists(cfg.OutputPath));
    }

    [Fact]
    public void MuxFailure_OutputMetaUsesFinalPathAndRetainsSplitInputs()
    {
        var validVideo = CreateValidVideo();
        var validAudio = CreateValidAudio();
        var cfg = CreateSystemLoopbackConfig();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var runner = new FakeExternalProcessRunner(exitCode: 1, stderr: "mux-failed");
        var backend = new AvSplitCaptureBackend(
            new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            runner,
            new TempRetentionPolicy(_dataDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.NotNull(backend.LastMeta);
        var meta = backend.LastMeta!;

        Assert.Equal(cfg.OutputPath, meta.OutputPath);
        Assert.Equal(0, meta.SizeBytes);
        Assert.False(meta.OutputFileExists);
        Assert.Contains("mux-failed", meta.StderrLog ?? "");
        Assert.Equal(1, runner.RunCallCount);
        Assert.False(File.Exists(cfg.OutputPath));
    }

    [Fact]
    public void SuccessfulAvSplit_OutputMetaUsesPublishedFinalPathAndFinalFileSize()
    {
        var validVideo = CreateValidVideo();
        var validAudio = CreateValidAudio();
        var muxed = CreateValidMuxedVideo(validVideo, validAudio);
        var cfg = CreateConfig();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var backend = new AvSplitCaptureBackend(
            new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            new FakeExternalProcessRunner(outputFileToCopy: muxed),
            new TempRetentionPolicy(_dataDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.NotNull(backend.LastMeta);
        var meta = backend.LastMeta!;

        Assert.Equal(cfg.OutputPath, meta.OutputPath);
        Assert.True(meta.OutputFileExists,
            $"warnings={string.Join(" | ", meta.Warnings ?? Array.Empty<string>())}; stderr={meta.StderrLog}");
        Assert.True(meta.SizeBytes > 0);
        Assert.True(File.Exists(cfg.OutputPath));
        Assert.Equal(new FileInfo(cfg.OutputPath).Length, meta.SizeBytes);
    }

    [Fact]
    public void EngineVideoOutputContract_UsesFinalPathAndZeroFailedBytesAcrossAllJsonSurfaces()
    {
        var finalPath = Path.Combine(_dataDir, "approved-final.mp4");
        var tempPath = Path.Combine(_dataDir, "temp", "approved-final_video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllBytes(tempPath, Enumerable.Repeat((byte)0x4A, 4096).ToArray());

        var rec = new Recording
        {
            State = RecState.failed,
            SourceType = "region",
            BackendType = "ffmpeg-av-split",
            OutputPath = finalPath,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-2),
            CompletedAtUtc = DateTime.UtcNow,
            StopReason = "unexpected_exit",
            Error = "mux_failed",
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (0, 0, 320, 240),
                OutputPath = finalPath
            },
            LastMeta = new OutputMeta
            {
                OutputPath = tempPath,
                SizeBytes = new FileInfo(tempPath).Length,
                OutputFileExists = true,
                DurationSeconds = 2,
                Container = "mp4",
                Codec = "h264"
            }
        };
        rec.PublishFinalized();

        using var engine = new RecordingEngine(new AuditLogger());
        engine._recs[rec.Id] = rec;

        var jsonSurfaces = new[]
        {
            JsonSerializer.Serialize(engine.Stop(rec.Id, "user_requested")),
            JsonSerializer.Serialize(engine.GetStatus(rec.Id)),
            JsonSerializer.Serialize(engine.GetStatusWait(rec.Id, "recording", 0)),
            JsonSerializer.Serialize(engine.GetOutput(rec.Id))
        };

        foreach (var json in jsonSurfaces)
        {
            using var document = JsonDocument.Parse(json);
            var output = GetPropertyIgnoreCase(document.RootElement, "output");
            Assert.Equal(finalPath, GetPropertyIgnoreCase(output, "path").GetString());
            Assert.DoesNotContain(tempPath, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_video.mp4", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("muxing.partial", json, StringComparison.OrdinalIgnoreCase);

            var size = FindPropertyIgnoreCase(output, "size_bytes") ??
                       FindPropertyIgnoreCase(output, "bytes_written") ??
                       FindPropertyIgnoreCase(output, "byteswritten");
            Assert.NotNull(size);
            Assert.Equal(0, size!.Value.GetInt64());
        }
    }

    private CaptureConfig CreateConfig() => new()
    {
        SourceKind = "display",
        Microphone = true,
        MicDevice = "fake-mic",
        Fps = 30,
        Bounds = (0, 0, 320, 240),
        OutputPath = Path.Combine(_dataDir, $"final-{Guid.NewGuid():N}.mp4")
    };

    private CaptureConfig CreateSystemLoopbackConfig() => new()
    {
        SourceKind = "display",
        AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
        SystemLoopbackEndpoint = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}",
        Fps = 30,
        Bounds = (0, 0, 320, 240),
        OutputPath = Path.Combine(_dataDir, $"final-{Guid.NewGuid():N}.mp4")
    };

    private string CreateValidVideo()
    {
        var path = Path.Combine(_dataDir, $"fixture-video-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration=2:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 -t 2 \"{path}\"");
        return path;
    }

    private string CreateValidAudio()
    {
        var path = Path.Combine(_dataDir, $"fixture-audio-{Guid.NewGuid():N}.wav");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i sine=frequency=1000:duration=3 -acodec pcm_s16le -ar 44100 -ac 2 \"{path}\"");
        return path;
    }

    private string CreateValidMuxedVideo(string videoPath, string audioPath)
    {
        var path = Path.Combine(_dataDir, $"fixture-muxed-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -i \"{videoPath}\" -i \"{audioPath}\" -filter_complex \"[1:a]atrim=duration=2.0,asetpts=PTS-STARTPTS[a]\" -c:v copy -c:a aac -b:a 128k -map 0:v:0 -map \"[a]\" \"{path}\"");
        return path;
    }

    private static void RunFfmpeg(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed");
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException("ffmpeg generation timed out");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg generation failed: " + process.StandardError.ReadToEnd());
    }

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string name)
        => FindPropertyIgnoreCase(element, name) ?? throw new Xunit.Sdk.XunitException($"Missing JSON property '{name}'.");

    private static JsonElement? FindPropertyIgnoreCase(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;

                var nested = FindPropertyIgnoreCase(property.Value, name);
                if (nested.HasValue)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPropertyIgnoreCase(item, name);
                if (nested.HasValue)
                    return nested;
            }
        }

        return null;
    }
}
