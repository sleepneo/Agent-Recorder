using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the audio/video split capture backend and finalizer introduced in
/// Task 180. Verifies backend selection, interface contracts, audio/video mux
/// stream ordering, duration trimming, audio continuity diagnosis, and
/// production finalization paths.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvSplitAndFinalizationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string? _originalDataDir;

    public AvSplitAndFinalizationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"avsplit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _tmpDir);
        DataDirResolver.SetOverride(_tmpDir);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void Select_WithMicrophone_UsesAvSplitBackend(string sourceKind)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var cfg = new CaptureConfig { SourceKind = sourceKind, Microphone = true, MicDevice = "mic" };

        var (backend, type) = CaptureBackendSelector.Select(cfg);

        Assert.IsType<AvSplitCaptureBackend>(backend);
        Assert.Contains("av-split", type);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void Select_WithoutMicrophone_DoesNotUseAvSplitBackend(string sourceKind)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var cfg = new CaptureConfig { SourceKind = sourceKind, Microphone = false };

        var (backend, type) = CaptureBackendSelector.Select(cfg);

        Assert.IsNotType<AvSplitCaptureBackend>(backend);
        Assert.DoesNotContain("av-split", type);
    }

    [Fact]
    public void AvSplitBackend_ImplementsExpectedLifecycleInterfaces()
    {
        ICaptureBackend backend = new AvSplitCaptureBackend();

        Assert.IsAssignableFrom<IAudioReadyBackend>(backend);
        Assert.IsAssignableFrom<IFirstFrameObservableCaptureBackend>(backend);
        Assert.IsAssignableFrom<ICaptureEndedObservableBackend>(backend);
    }

    [Fact]
    public void AvFinalizer_Mux_Streams_VideoIndex0_AudioIndex1()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "output.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));

        AssertDuration(outputPath, 2.0, tolerance: 0.10);

        var streams = GetStreams(outputPath).OrderBy(s => (int?)s!["index"] ?? int.MaxValue).ToList();
        Assert.True(streams.Count >= 2, "expected at least two streams");

        var videoCodecType = streams[0]!["codec_type"]?.GetValue<string>();
        var videoCodecName = streams[0]!["codec_name"]?.GetValue<string>();
        var audioCodecType = streams[1]!["codec_type"]?.GetValue<string>();
        var audioCodecName = streams[1]!["codec_name"]?.GetValue<string>();

        Assert.NotNull(videoCodecType);
        Assert.NotNull(videoCodecName);
        Assert.NotNull(audioCodecType);
        Assert.NotNull(audioCodecName);

        Assert.Equal("video", videoCodecType);
        Assert.Equal("h264", videoCodecName);
        Assert.Equal("audio", audioCodecType);
        Assert.Equal("aac", audioCodecName);
    }

    [Fact]
    public void AvFinalizer_TrimsAudioLongerThanVideo_ToVideoDuration()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "trimmed.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 5);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        AssertDuration(outputPath, 2.0, tolerance: 0.10);
    }

    [Fact]
    public void AvFinalizer_AudioPreRoll_PositivePreRoll_SkipsLeadingAudio()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "delayed.mp4");

        GenerateTwoToneVideo(videoPath);
        GenerateDelayedAudio(audioPath);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(0.5),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));

        AssertDuration(outputPath, 2.0, tolerance: 0.10);

        var ppmPath = Path.Combine(_tmpDir, "firstframe.ppm");
        ExtractFirstFrame(outputPath, ppmPath);
        var (r, g, b) = ReadPpmFirstPixel(ppmPath);
        Assert.True(r < 30 && g < 30 && b < 30, "first frame should be black");

        var maxVolume = GetAudioMaxVolume(outputPath, durationSeconds: 0.1);
        Assert.True(maxVolume > -30.0, $"first 0.1s of audio should not be silent, got {maxVolume:F1} dB");

        var startTime = GetFormatStartTime(outputPath);
        Assert.True(startTime >= -0.05 && startTime <= 0.05, $"container start_time should be near 0, got {startTime:F3}");
    }

    [Fact]
    public void AvFinalizer_AudioCoverageEnough_AllowsMux()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "coverage-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "coverage-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "coverage-ok.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(500),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Error);
        Assert.Equal("available", result.Meta.VideoAnchorStatus);
        Assert.Equal("available", result.Meta.AudioAnchorStatus);
        Assert.True(result.Meta.AudioCoverageDeltaSeconds >= 0);
    }

    [Fact]
    public void AvFinalizer_AudioCoverageSlightlyShortWithinTolerance_AllowsMux()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "coverage-tolerance-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "coverage-tolerance-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "coverage-tolerance-ok.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 2);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(100),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Error);
        Assert.True(result.Meta.AudioCoverageDeltaSeconds < 0);
        Assert.True(result.Meta.AudioCoverageDeltaSeconds >= -AvFinalizer.AudioCoverageToleranceSeconds);
    }

    [Fact]
    public void AvFinalizer_AudioCoverageClearlyTooShort_ReturnsStableDiagnostic()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "coverage-short-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "coverage-short-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "coverage-short-fail.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 1);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(500),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal("Audio duration (1.000s) does not cover pre-roll plus video (2.500s, tolerance 0.250s).", result.Error);
        Assert.Contains(result.Meta.Warnings, w => w.Contains("audio_timeline_too_short"));
        Assert.Equal("available", result.Meta.VideoAnchorStatus);
        Assert.Equal("available", result.Meta.AudioAnchorStatus);
        Assert.True(result.Meta.AudioCoverageDeltaSeconds < -AvFinalizer.AudioCoverageToleranceSeconds);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task AvFinalizer_LaunchAnchorAcceptanceDurations_AllowsMux()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "task188-acceptance-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "task188-acceptance-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "task188-acceptance-ok.mp4");
        const double launchDerivedPreRollSeconds = 3.100;

        GenerateTestVideo(videoPath, durationSeconds: 15.034);
        GenerateTestAudio(audioPath, durationSeconds: 18.360);

        var result = await new AvFinalizer(new ExternalProcessRunner())
            .FinalizeAsync(
                videoPath,
                audioPath,
                outputPath,
                audioPreRoll: TimeSpan.FromSeconds(launchDerivedPreRollSeconds),
                microphoneRequested: true,
                applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Error);
        Assert.True(File.Exists(outputPath));
        Assert.DoesNotContain(result.Meta.Warnings ?? Array.Empty<string>(), w => w.Contains("audio_timeline_too_short"));
        Assert.InRange(result.Meta.TempVideoDurationSeconds!.Value, 15.00, 15.10);
        Assert.InRange(result.Meta.TempAudioDurationSeconds!.Value, 18.30, 18.40);
        Assert.Equal(
            launchDerivedPreRollSeconds + result.Meta.TempVideoDurationSeconds.Value,
            result.Meta.RequiredAudioCoverageSeconds!.Value,
            3);
        Assert.Equal(
            result.Meta.TempAudioDurationSeconds.Value - result.Meta.RequiredAudioCoverageSeconds.Value,
            result.Meta.AudioCoverageDeltaSeconds!.Value,
            3);
        Assert.InRange(result.Meta.AudioCoverageDeltaSeconds.Value, -AvFinalizer.AudioCoverageToleranceSeconds, 0.30);
    }

    [Fact]
    public async Task AvFinalizer_LaunchAnchorAcceptanceShortAudio_StillFailsCoverage()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "task188-short-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "task188-short-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "task188-short-fail.mp4");
        const double launchDerivedPreRollSeconds = 3.100;

        GenerateTestVideo(videoPath, durationSeconds: 15.034);
        GenerateTestAudio(audioPath, durationSeconds: 17.700);

        var result = await new AvFinalizer(new ExternalProcessRunner())
            .FinalizeAsync(
                videoPath,
                audioPath,
                outputPath,
                audioPreRoll: TimeSpan.FromSeconds(launchDerivedPreRollSeconds),
                microphoneRequested: true,
                applyContinuityCheck: false);

        Assert.Contains(result.Meta.Warnings ?? Array.Empty<string>(), w => w.Contains("audio_timeline_too_short"));
        Assert.NotNull(result.Error);
        Assert.Contains("does not cover pre-roll plus video", result.Error);
        Assert.True(result.Meta.RequiredAudioCoverageSeconds > 18.0);
        Assert.True(result.Meta.AudioCoverageDeltaSeconds < -AvFinalizer.AudioCoverageToleranceSeconds);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task AvFinalizer_MissingVideoAnchor_ReturnsStableDiagnostic()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "missing-video-anchor-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "missing-video-anchor-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "missing-video-anchor-fail.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(outputFileToCopy: videoPath));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(500),
            microphoneRequested: true,
            applyContinuityCheck: false,
            videoAnchorAvailable: false,
            audioAnchorAvailable: true);

        Assert.Contains(result.Meta.Warnings, w => w.Contains("video_anchor_missing"));
        Assert.Equal("missing", result.Meta.VideoAnchorStatus);
        Assert.Equal("available", result.Meta.AudioAnchorStatus);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task AvFinalizer_MissingAudioAnchor_ReturnsStableDiagnostic()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "missing-audio-anchor-video.mp4");
        var audioPath = Path.Combine(_tmpDir, "missing-audio-anchor-audio.wav");
        var outputPath = Path.Combine(_tmpDir, "missing-audio-anchor-fail.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(outputFileToCopy: videoPath));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(500),
            microphoneRequested: true,
            applyContinuityCheck: false,
            videoAnchorAvailable: true,
            audioAnchorAvailable: false);

        Assert.Contains(result.Meta.Warnings, w => w.Contains("audio_anchor_missing"));
        Assert.Equal("available", result.Meta.VideoAnchorStatus);
        Assert.Equal("missing", result.Meta.AudioAnchorStatus);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void AvFinalizer_InternalLongSilence_AddsInterruptionWarningAndDegraded()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio_gap.wav");
        var outputPath = Path.Combine(_tmpDir, "degraded.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 6);
        GenerateTestAudioWithGap(audioPath, durationSeconds: 6, gapStart: 2, gapDuration: 3.5);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("degraded", result.Meta.AudioContinuityStatus);
        Assert.Contains(result.Meta.Warnings, w => w.Contains("microphone_signal_interruption_suspected"));
    }

    [Fact]
    public void AvFinalizer_InitialAndTrailingSilence_DoesNotDegrade()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio_edges.wav");
        var outputPath = Path.Combine(_tmpDir, "continuous.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 5);
        GenerateTestAudioWithEdgeSilence(audioPath, durationSeconds: 5, leadingSilence: 1.5, trailingSilence: 1.5);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("continuous", result.Meta.AudioContinuityStatus);
        Assert.DoesNotContain(result.Meta.Warnings, w => w.Contains("microphone_signal_interruption_suspected"));
    }

    [Fact]
    public void AvFinalizer_NoMicrophoneRequest_SetsNotRequestedContinuity()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var outputPath = Path.Combine(_tmpDir, "noaudio.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 1);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath: "",
            outputPath,
            audioPreRoll: null,
            microphoneRequested: false,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("not_requested", result.Meta.AudioStatus);
        Assert.Equal("not_checked", result.Meta.AudioContinuityStatus);
    }

    [Fact]
    public void AvFinalizer_CleanAudio_ClassifiesRecorded()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "recorded.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("recorded", result.Meta.AudioStatus);
    }

    [Fact]
    public void AvFinalizer_MissingAudioTrack_ClassifiesMissingTrack()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var outputPath = Path.Combine(_tmpDir, "missing.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath: Path.Combine(_tmpDir, "nonexistent.wav"),
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("missing_audio_track", result.Meta.AudioStatus);
    }

    [Fact]
    public void AvFinalizer_OpenFailedAudioStderr_ClassifiesStartFailed()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "startfailed.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false,
            audioStderr: "could not open audio device");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("start_failed", result.Meta.AudioStatus);
    }

    [Fact]
    public void AvFinalizer_AudioStderr_ClassifiesLost()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "lost.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false,
            audioStderr: "error reading input");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("lost", result.Meta.AudioStatus);
        Assert.Contains(result.Meta.Warnings, w => w.Contains("microphone_lost"));
    }

    [Fact]
    public void AudioCaptureWorker_BuildArgs_UsesTimestampCompensationOnceBeforePcmOutput()
    {
        var args = AudioCaptureWorker.BuildArgs(
            new CaptureConfig
            {
                SourceKind = "display",
                Microphone = true,
                MicDevice = "fake-mic"
            },
            Path.Combine(_tmpDir, "audio.wav"));

        var filterIndexes = args.Select((arg, index) => (arg, index))
            .Where(x => x.arg == AudioCaptureWorker.TimestampCompensationFilter)
            .Select(x => x.index)
            .ToList();

        Assert.Single(filterIndexes);
        var filterOptionIndex = args.IndexOf("-af");
        Assert.True(filterOptionIndex >= 0);
        Assert.Equal(filterOptionIndex + 1, filterIndexes[0]);
        Assert.True(filterOptionIndex > args.IndexOf($"audio=fake-mic"));
        Assert.DoesNotContain("apad", args);
        Assert.DoesNotContain("atempo", args);
    }

    [Fact]
    public async Task AvFinalizer_MuxTimeout_KillsProcessTree()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, "video.mp4");
        var audioPath = Path.Combine(_tmpDir, "audio.wav");
        var outputPath = Path.Combine(_tmpDir, "timeout.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 1);
        GenerateTestAudio(audioPath, durationSeconds: 2);

        var runner = new FakeExternalProcessRunner(simulateTimeout: true);
        var finalizer = new AvFinalizer(runner, TimeSpan.FromMilliseconds(100));

        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromMilliseconds(1),
            microphoneRequested: true,
            applyContinuityCheck: false);

        Assert.True(result.TimedOut);
        Assert.True(runner.KillInvoked);
    }

    [Fact]
    public void AvFinalizer_Success_DeletesTempFiles()
    {
        var validVideo = GenerateFixtureVideo();
        var validAudio = GenerateFixtureAudio();

        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tmpDir));

        var cfg = new CaptureConfig
        {
            SourceKind = "display",
            Microphone = true,
            MicDevice = "fake-mic",
            Fps = 30,
            Bounds = (0, 0, 320, 240),
            OutputPath = Path.Combine(_tmpDir, $"final-{Guid.NewGuid():N}.mp4")
        };

        backend.Start(cfg);
        backend.StartVideo();

        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(10)));

        Assert.False(File.Exists(video.OutputPath), "temp video should be deleted on success");
        Assert.False(File.Exists(audio.OutputPath), "temp audio should be deleted on success");
        Assert.True(File.Exists(cfg.OutputPath), "final output should exist");
    }

    [Fact]
    public void AvFinalizer_Failure_PreservesTempFiles()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureVideo());
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tmpDir));

        var cfg = new CaptureConfig
        {
            SourceKind = "display",
            Microphone = true,
            MicDevice = "fake-mic",
            Fps = 30,
            Bounds = (0, 0, 320, 240),
            OutputPath = Path.Combine(_tmpDir, $"final-{Guid.NewGuid():N}.mp4")
        };

        backend.Start(cfg);
        backend.StartVideo();

        File.WriteAllText(video.OutputPath!, "not a video");
        File.WriteAllText(audio.OutputPath!, "not audio");

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(10)));

        var recordingId = Path.GetFileNameWithoutExtension(cfg.OutputPath);
        var failedDir = Path.Combine(_tmpDir, "failed", recordingId);
        Assert.True(Directory.Exists(failedDir), "failed diagnostics directory should exist");
        Assert.True(File.Exists(Path.Combine(failedDir, "video.mp4")) || File.Exists(video.OutputPath),
            "temp video should be preserved");
        Assert.True(File.Exists(Path.Combine(failedDir, "audio.wav")) || File.Exists(audio.OutputPath),
            "temp audio should be preserved");
    }

    private void SkipIfNoFfmpeg()
    {
        Assert.True(File.Exists(FfmpegLocator.FfmpegPath), "Bundled FFmpeg not available.");
    }

    private static void GenerateTestVideo(string path, double durationSeconds)
    {
        var duration = durationSeconds.ToString(CultureInfo.InvariantCulture);
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration={duration}:size=320x240:rate=30 -pix_fmt yuv420p -c:v libx264 -t {duration} \"{path}\"");
    }

    private static void GenerateTestAudio(string path, double durationSeconds)
    {
        var duration = durationSeconds.ToString(CultureInfo.InvariantCulture);
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i sine=frequency=1000:duration={duration} -acodec pcm_s16le -ar 44100 -ac 2 -t {duration} \"{path}\"");
    }

    private static void GenerateTestAudioWithGap(string path, int durationSeconds, double gapStart, double gapDuration)
    {
        var filter = $"sine=frequency=1000:duration={durationSeconds},volume=enable='between(t,{gapStart.ToString(CultureInfo.InvariantCulture)},{(gapStart + gapDuration).ToString(CultureInfo.InvariantCulture)})':volume=0";
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i \"{filter}\" -acodec pcm_s16le -ar 44100 -ac 2 -t {durationSeconds} \"{path}\"");
    }

    private static void GenerateTestAudioWithEdgeSilence(string path, int durationSeconds, double leadingSilence, double trailingSilence)
    {
        var toneDuration = durationSeconds - leadingSilence - trailingSilence;
        var filter = $"sine=frequency=1000:duration={durationSeconds},volume=enable='between(t,{leadingSilence.ToString(CultureInfo.InvariantCulture)},{(leadingSilence + toneDuration).ToString(CultureInfo.InvariantCulture)})':volume=0";
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i \"{filter}\" -acodec pcm_s16le -ar 44100 -ac 2 -t {durationSeconds} \"{path}\"");
    }

    private static void GenerateTwoToneVideo(string path)
    {
        // 0.5s black followed by 1.5s white, total 2.0s.
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i color=c=black:s=320x240:d=0.5 -f lavfi -i color=c=white:s=320x240:d=1.5 -filter_complex \"[0:v][1:v]concat=n=2:v=1:a=0\" -pix_fmt yuv420p -c:v libx264 -t 2 \"{path}\"");
    }

    private static void GenerateDelayedAudio(string path)
    {
        // 0.5s silence followed by 2.5s tone, total 3.0s.
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i aevalsrc=0:d=0.5 -f lavfi -i sine=frequency=1000:duration=2.5 -filter_complex \"[0:a][1:a]concat=n=2:v=0:a=1\" -acodec pcm_s16le -ar 44100 -ac 2 -t 3 \"{path}\"");
    }

    private string GenerateFixtureVideo()
    {
        var path = Path.Combine(_tmpDir, $"fixture-{Guid.NewGuid():N}.mp4");
        GenerateTestVideo(path, 2);
        return path;
    }

    private string GenerateFixtureAudio()
    {
        var path = Path.Combine(_tmpDir, $"fixture-{Guid.NewGuid():N}.wav");
        GenerateTestAudio(path, 2);
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
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed");
        proc.BeginOutputReadLine();
        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(true); } catch { }
            throw new InvalidOperationException("ffmpeg generation timed out");
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg generation failed: " + proc.StandardError.ReadToEnd());
    }

    private static JsonArray GetStreams(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfprobePath,
            Arguments = $"-v error -print_format json -show_streams \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe failed");
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        var root = JsonNode.Parse(json);
        return root?["streams"]?.AsArray() ?? new JsonArray();
    }

    private static void AssertDuration(string path, double expected, double tolerance)
    {
        var meta = FfmpegCaptureBackend.Probe(path);
        Assert.True(Math.Abs(meta.DurationSeconds - expected) <= tolerance,
            $"expected duration {expected:F2}s +/- {tolerance:F2}s, got {meta.DurationSeconds:F2}s");
    }

    private static void ExtractFirstFrame(string videoPath, string ppmPath)
    {
        RunFfmpeg($"-y -nostats -loglevel error -i \"{videoPath}\" -ss 00:00:00 -vframes 1 \"{ppmPath}\"");
    }

    private static (byte r, byte g, byte b) ReadPpmFirstPixel(string ppmPath)
    {
        var bytes = File.ReadAllBytes(ppmPath);
        // Skip PPM header (P6\nwidth height\n255\n).
        int i = 0;
        while (i < bytes.Length && bytes[i] != '\n') i++; // P6
        i++;
        while (i < bytes.Length && bytes[i] != '\n') i++; // width height
        i++;
        while (i < bytes.Length && bytes[i] != '\n') i++; // 255
        i++;
        return (bytes[i], bytes[i + 1], bytes[i + 2]);
    }

    private static double GetAudioMaxVolume(string path, double durationSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            Arguments = $"-y -nostats -loglevel info -i \"{path}\" -t {durationSeconds.ToString(CultureInfo.InvariantCulture)} -af volumedetect -f null -",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg volumedetect failed");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);

        foreach (var line in stderr.Split('\n'))
        {
            const string prefix = "max_volume:";
            var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var value = line.Substring(idx + prefix.Length).Trim();
            var num = value.Split(' ')[0];
            if (double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var db))
                return db;
        }
        return double.MinValue;
    }

    private static double GetFormatStartTime(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfprobePath,
            Arguments = $"-v error -print_format json -show_format \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe failed");
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        var root = JsonNode.Parse(json);
        var start = root?["format"]?["start_time"]?.GetValue<string>();
        return double.TryParse(start, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
