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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
                audioSourceKind: AudioCaptureSourceKind.Microphone,
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
                audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.None,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
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
            audioSourceKind: AudioCaptureSourceKind.Microphone,
            applyContinuityCheck: false);

        Assert.True(result.TimedOut);
        Assert.True(runner.KillInvoked);
    }

    [Fact]
    public void AvFinalizer_SystemLoopback_RealFfmpeg_Mux_ProducesValidOrderedStreams()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, $"sysloop-video-{Guid.NewGuid():N}.mp4");
        var audioPath = Path.Combine(_tmpDir, $"sysloop-audio-{Guid.NewGuid():N}.wav");
        var outputPath = Path.Combine(_tmpDir, $"sysloop-out-{Guid.NewGuid():N}.mp4");

        // Synthetic inputs: 2s H.264 video, 3s PCM WAV (1.0s pre-roll + 2.0s video).
        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath), "final output must exist");

        // Production probe: actual stream order and final timeline.
        var meta = result.Meta;
        Assert.Equal("system_loopback_recorded", meta.AudioStatus);
        Assert.True(meta.ProbeStreams.Length >= 2, $"expected >= 2 streams, got {meta.ProbeStreams.Length}");
        var videoStream = meta.ProbeStreams.First(s => s.CodecType == "video");
        var audioStream = meta.ProbeStreams.First(s => s.CodecType == "audio");
        Assert.Equal(0, videoStream.Index);
        Assert.Equal(1, audioStream.Index);
        Assert.Equal("h264", videoStream.CodecName, ignoreCase: true);
        Assert.Equal("aac", audioStream.CodecName, ignoreCase: true);
        Assert.True(videoStream.StartTimeSeconds.HasValue, "video start time must be present");
        Assert.True(audioStream.StartTimeSeconds.HasValue, "audio start time must be present");
        Assert.True(videoStream.DurationSeconds > 0, "video duration must be positive");
        Assert.True(audioStream.DurationSeconds > 0, "audio duration must be positive");

        // Independent test-side ffprobe parses its OWN stream order and A/V
        // timeline. This evidence must not reuse the production probe object.
        var streams = GetStreams(outputPath);
        Assert.Equal(2, streams.Count);
        var probeStream0 = streams[0]!;
        var probeStream1 = streams[1]!;
        Assert.Equal(0, probeStream0["index"]?.GetValue<int>());
        Assert.Equal(1, probeStream1["index"]?.GetValue<int>());
        Assert.Equal("video", probeStream0["codec_type"]?.GetValue<string>());
        Assert.Equal("h264", probeStream0["codec_name"]?.GetValue<string>());
        Assert.Equal("audio", probeStream1["codec_type"]?.GetValue<string>());
        Assert.Equal("aac", probeStream1["codec_name"]?.GetValue<string>());

        // start_time and duration must be present, finite and (for duration) > 0.
        var tvStart = ParseStreamDouble(probeStream0, "start_time");
        var tvDuration = ParseStreamDouble(probeStream0, "duration");
        var taStart = ParseStreamDouble(probeStream1, "start_time");
        var taDuration = ParseStreamDouble(probeStream1, "duration");
        Assert.NotNull(tvStart);
        Assert.True(double.IsFinite(tvStart!.Value), "independent video start_time must be finite");
        Assert.NotNull(tvDuration);
        Assert.True(double.IsFinite(tvDuration!.Value) && tvDuration!.Value > 0, "independent video duration must be finite and positive");
        Assert.NotNull(taStart);
        Assert.True(double.IsFinite(taStart!.Value), "independent audio start_time must be finite");
        Assert.NotNull(taDuration);
        Assert.True(double.IsFinite(taDuration!.Value) && taDuration!.Value > 0, "independent audio duration must be finite and positive");

        // Independent A/V boundary coverage within the 0.250s contract, in both
        // directions (audio may neither start late/early nor end short/long).
        var indVEnd = tvStart!.Value + tvDuration!.Value;
        var indAEnd = taStart!.Value + taDuration!.Value;
        Assert.True(taStart.Value - tvStart.Value <= 0.25 + 1e-6,
            $"independent: audio start {taStart.Value:F3}s later than video start {tvStart.Value:F3}s by >0.25s");
        Assert.True(tvStart.Value - taStart.Value <= 0.25 + 1e-6,
            $"independent: audio start {taStart.Value:F3}s earlier than video start {tvStart.Value:F3}s by >0.25s");
        Assert.True(indVEnd - indAEnd <= 0.25 + 1e-6,
            $"independent: audio end {indAEnd:F3}s shorter than video end {indVEnd:F3}s by >0.25s");
        Assert.True(indAEnd - indVEnd <= 0.25 + 1e-6,
            $"independent: audio end {indAEnd:F3}s longer than video end {indVEnd:F3}s by >0.25s");
    }

    [Fact]
    public async Task AvFinalizer_SystemLoopback_PureVideoFalseSuccess_Rejected()
    {
        SkipIfNoFfmpeg();

        var videoPath = Path.Combine(_tmpDir, $"pv-video-{Guid.NewGuid():N}.mp4");
        var audioPath = Path.Combine(_tmpDir, $"pv-audio-{Guid.NewGuid():N}.wav");
        var outputPath = Path.Combine(_tmpDir, $"pv-out-{Guid.NewGuid():N}.mp4");

        GenerateTestVideo(videoPath, durationSeconds: 2);
        GenerateTestAudio(audioPath, durationSeconds: 3);

        // FFmpeg exits 0 but the runner only places a pure-video file at the mux
        // temp path. Post-mux validation must fail closed: the final path is
        // never created and the output is never reported as recorded.
        var runner = new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureVideo(), exitCode: 0);
        var finalizer = new AvFinalizer(runner, TimeSpan.FromMinutes(2));

        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.NotNull(result.Error);
        Assert.Contains("output_missing_audio_stream", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath), "final output must not be published on false success");
        Assert.NotEqual("system_loopback_recorded", result.Meta.AudioStatus);
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "temp mux partial must be cleaned up");
    }

    // ============================================================
    // Section 3: system-loopback failure semantics — no microphone wording.
    // Every failure object produced by the production finalizer for a
    // system-loopback source must be free of microphone semantics.
    // ============================================================

    private static void AssertNoMicrophoneWording(AvFinalizer.Result result)
    {
        Assert.DoesNotContain("microphone", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("microphone", result.Stderr ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("microphone", result.Meta.AudioStatus ?? "", StringComparison.OrdinalIgnoreCase);
        foreach (var warning in result.Meta.Warnings)
            Assert.DoesNotContain("microphone", warning, StringComparison.OrdinalIgnoreCase);
    }

    // Failed() stores the human-readable message in Result.Error and embeds the
    // stable error code as the "{code}:" prefix of a warning entry. Assert on
    // the code via the warning prefix.
    private static void AssertErrorCode(AvFinalizer.Result result, string expectedCode)
    {
        Assert.NotNull(result.Error);
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith(expectedCode + ":", StringComparison.Ordinal));
    }

    private (string VideoPath, string AudioPath) GenerateSysloopInputs(double videoSeconds = 2, double audioSeconds = 3)
    {
        var videoPath = Path.Combine(_tmpDir, $"sys-in-{Guid.NewGuid():N}.mp4");
        var audioPath = Path.Combine(_tmpDir, $"sys-in-{Guid.NewGuid():N}.wav");
        GenerateTestVideo(videoPath, videoSeconds);
        GenerateTestAudio(audioPath, audioSeconds);
        return (videoPath, audioPath);
    }

    [Fact]
    public void SystemLoopback_MissingWav_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, _) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-missing-wav-{Guid.NewGuid():N}.mp4");

        var result = AvFinalizer.Finalize(
            videoPath,
            Path.Combine(_tmpDir, $"does-not-exist-{Guid.NewGuid():N}.wav"),
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "missing_audio_track");
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith("system_audio_missing_track", StringComparison.Ordinal));
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public void SystemLoopback_MissingAudioStream_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-missing-stream-{Guid.NewGuid():N}.mp4");
        // The audio path exists but contains no audio stream.
        File.WriteAllText(audioPath, "this is not audio");

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "missing_audio_track");
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith("system_audio_missing_track", StringComparison.Ordinal));
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public void SystemLoopback_AudioOpenFailure_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-open-fail-{Guid.NewGuid():N}.mp4");

        var result = AvFinalizer.Finalize(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false,
            audioStderr: "could not open audio device");

        AssertErrorCode(result, "start_failed");
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith("system_audio_start_failed", StringComparison.Ordinal));
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_VideoAnchorMissing_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-v-anchor-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false,
            videoAnchorAvailable: false);

        AssertErrorCode(result, "video_anchor_missing");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_AudioAnchorMissing_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-a-anchor-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false,
            audioAnchorAvailable: false);

        AssertErrorCode(result, "audio_anchor_missing");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_PreRollInvalid_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-preroll-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.Zero,
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "audio_preroll_invalid");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_TimelineTooShort_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs(videoSeconds: 2, audioSeconds: 1);
        var outputPath = Path.Combine(_tmpDir, $"sys-short-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "audio_timeline_too_short");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_MuxNonZero_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-mux-fail-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(exitCode: 1), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "mux_failed");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_MuxTimeout_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-mux-timeout-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(simulateTimeout: true), TimeSpan.FromMilliseconds(100));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.True(result.TimedOut);
        AssertErrorCode(result, "mux_timeout");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_PostMuxValidationFailure_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-validate-fail-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureVideo(), exitCode: 0), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.NotNull(result.Error);
        Assert.Contains("output_missing_audio_stream", result.Error, StringComparison.OrdinalIgnoreCase);
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    [Fact]
    public async Task SystemLoopback_AtomicPublishFailure_NoMicrophoneWording()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"sys-publish-fail-{Guid.NewGuid():N}.mp4");

        var finalizer = new AvFinalizer(
            new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureMuxedOutput(), exitCode: 0),
            TimeSpan.FromMinutes(2),
            new FailingPublisher());
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "atomic_publish_failed");
        AssertNoMicrophoneWording(result);
        Assert.False(File.Exists(outputPath), "final output must not be published on publish failure");
    }

    // ============================================================
    // P0-二: mux execution must be inside the full-path cleanup try/finally.
    // Every outcome — runner cancellation surfaced as OperationCanceledException,
    // runner throwing an unexpected exception, mux non-zero, post-mux validation
    // failure and publish failure — must leave no <output>.muxing.partial.mp4
    // behind and must never delete, truncate or replace a pre-existing final.
    // ============================================================

    private static void WriteExistingFinal(string path)
    {
        File.WriteAllText(path, "PRE-EXISTING-FINAL");
        Assert.True(File.Exists(path), "pre-existing final must exist before the test run");
    }

    private static void AssertExistingFinalUnchanged(string path)
    {
        Assert.True(File.Exists(path), "pre-existing final must still exist after the failed run");
        Assert.Equal("PRE-EXISTING-FINAL", File.ReadAllText(path));
    }

    [Fact]
    public async Task AvFinalizer_MuxRunnerCancellationAfterWrite_CleansPartial_KeepsExistingFinal()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"cancel-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        // The runner writes the mux partial then surfaces the caller's
        // cancellation (what the production ExternalProcessRunner throws on a
        // cancelled wait).
        var runner = new FakeExternalProcessRunner(
            outputFileToCopy: GenerateFixtureMuxedOutput(),
            throwCancellationAfterWrite: true);
        var finalizer = new AvFinalizer(runner, TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<OperationCanceledException>(() => finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false));

        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned after cancellation");
        AssertExistingFinalUnchanged(outputPath);
    }

    [Fact]
    public async Task AvFinalizer_MuxRunnerExceptionAfterWrite_CleansPartial_KeepsExistingFinal()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"exc-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        // The runner writes the mux partial then throws an unexpected exception.
        var runner = new FakeExternalProcessRunner(
            outputFileToCopy: GenerateFixtureMuxedOutput(),
            throwExceptionAfterWrite: true);
        var finalizer = new AvFinalizer(runner, TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false));

        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned after runner exception");
        AssertExistingFinalUnchanged(outputPath);
    }

    [Fact]
    public async Task AvFinalizer_MuxNonZero_PreservesExistingFinal()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"muxnz-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        var finalizer = new AvFinalizer(new FakeExternalProcessRunner(exitCode: 1), TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "mux_failed");
        AssertExistingFinalUnchanged(outputPath);
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned after mux failure");
    }

    [Fact]
    public async Task AvFinalizer_PostMuxValidationFailure_PreservesExistingFinal()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"valfail-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        // FFmpeg exits 0 but the mux temp only holds a pure-video file (missing
        // audio). Production probe + validation fails closed.
        var finalizer = new AvFinalizer(
            new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureVideo(), exitCode: 0),
            TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.NotNull(result.Error);
        Assert.Contains("output_missing_audio_stream", result.Error, StringComparison.OrdinalIgnoreCase);
        AssertExistingFinalUnchanged(outputPath);
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned on validation failure");
    }

    [Fact]
    public async Task AvFinalizer_AtomicPublishFailure_PreservesExistingFinal()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"publishfail-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        var finalizer = new AvFinalizer(
            new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureMuxedOutput(), exitCode: 0),
            TimeSpan.FromMinutes(2),
            new FailingPublisher());
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        AssertErrorCode(result, "atomic_publish_failed");
        AssertExistingFinalUnchanged(outputPath);
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned on publish failure");
    }

    [Fact]
    public async Task AvFinalizer_Success_ReplacesExistingFinal_NoTempLeftovers()
    {
        SkipIfNoFfmpeg();
        var (videoPath, audioPath) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"success-{Guid.NewGuid():N}.mp4");
        WriteExistingFinal(outputPath);

        var finalizer = new AvFinalizer(
            new FakeExternalProcessRunner(outputFileToCopy: GenerateFixtureMuxedOutput(), exitCode: 0),
            TimeSpan.FromMinutes(2));
        var result = await finalizer.FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath), "final output must exist after success");
        Assert.NotEqual("PRE-EXISTING-FINAL", File.ReadAllText(outputPath));
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "no mux partial may remain after success");
        var leftoverTmp = Directory.GetFiles(Path.GetDirectoryName(outputPath)!, $"{Path.GetFileName(outputPath)}.publish-tmp-*");
        Assert.Empty(leftoverTmp);
    }

    // ============================================================
    // P0-三: structured post-mux probe must fail closed on ambiguous or
    // non-finite evidence. Each scenario injects a failing OutputMeta through
    // the production IOutputProber seam and asserts the finalizer fails closed,
    // preserves a pre-existing final, and cleans the mux partial.
    // ============================================================

    private sealed class FakeOutputProber : IOutputProber
    {
        private static readonly OutputMeta ValidVideoMeta = new() { DurationSeconds = 2.0 };
        private static readonly OutputMeta ValidAudioMeta = new()
        {
            DurationSeconds = 3.0,
            HasAudioStream = true,
            AudioCodec = "aac",
            AudioStatus = "recorded"
        };

        private readonly OutputMeta _postMuxMeta;

        public FakeOutputProber(OutputMeta postMuxMeta)
        {
            _postMuxMeta = postMuxMeta;
        }

        public OutputMeta Probe(string path)
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return ValidAudioMeta;
            if (path.EndsWith(".muxing.partial.mp4", StringComparison.Ordinal))
                return _postMuxMeta;
            return ValidVideoMeta;
        }
    }

    private AvFinalizer CreateFinalizerInjectingProbeMeta(OutputMeta postMuxMeta)
        => new AvFinalizer(
            new FakeExternalProcessRunner(exitCode: 0),
            TimeSpan.FromMinutes(2),
            StagingToFinalPublisher.Instance,
            new FakeOutputProber(postMuxMeta));

    private static ProbeStreamInfo VideoStream(
        int index = 0,
        double? start = 0.0,
        double? duration = 2.0,
        string? codec = "h264")
        => new ProbeStreamInfo { Index = index, CodecType = "video", CodecName = codec, StartTimeSeconds = start, DurationSeconds = duration };

    private static ProbeStreamInfo AudioStream(
        int index = 1,
        double? start = 0.05,
        double? duration = 1.95,
        string? codec = "aac")
        => new ProbeStreamInfo { Index = index, CodecType = "audio", CodecName = codec, StartTimeSeconds = start, DurationSeconds = duration };

    private static OutputMeta PostMuxMeta(params ProbeStreamInfo[] streams)
        => new OutputMeta
        {
            DurationSeconds = 2.0,
            HasAudioStream = true,
            AudioCodec = "aac",
            ProbeStreams = streams
        };

    private async Task<AvFinalizer.Result> RunProbeValidation(
        string videoPath,
        string audioPath,
        string outputPath,
        OutputMeta postMuxMeta)
        => await CreateFinalizerInjectingProbeMeta(postMuxMeta).FinalizeAsync(
            videoPath,
            audioPath,
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            applyContinuityCheck: false);

    private (string VideoPath, string AudioPath, string OutputPath) BuildProbeInputs(string tag)
    {
        var dir = _tmpDir;
        var videoPath = Path.Combine(dir, $"probe-v-{tag}-{Guid.NewGuid():N}.mp4");
        var audioPath = Path.Combine(dir, $"probe-a-{tag}-{Guid.NewGuid():N}.wav");
        var outputPath = Path.Combine(dir, $"probe-out-{tag}-{Guid.NewGuid():N}.mp4");
        // Probe evidence is injected; only the files' existence is checked.
        File.WriteAllBytes(videoPath, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(audioPath, new byte[] { 1, 2, 3, 4 });
        return (videoPath, audioPath, outputPath);
    }

    private async Task AssertProbeFailsClosed(string tag, OutputMeta postMuxMeta, string expectedCodePart, bool preserveFinal = true)
    {
        var (videoPath, audioPath, outputPath) = BuildProbeInputs(tag);
        if (preserveFinal) WriteExistingFinal(outputPath);

        var result = await RunProbeValidation(videoPath, audioPath, outputPath, postMuxMeta);

        Assert.NotNull(result.Error);
        Assert.Contains(expectedCodePart, result.Error, StringComparison.OrdinalIgnoreCase);
        if (preserveFinal)
            AssertExistingFinalUnchanged(outputPath);
        Assert.False(File.Exists(outputPath + ".muxing.partial.mp4"), "mux partial must be cleaned");
    }

    [Fact]
    public async Task AvFinalizer_Probe_ExtraAudioStream_FailsClosed()
        => await AssertProbeFailsClosed("extra-audio",
            PostMuxMeta(VideoStream(), AudioStream(), AudioStream(index: 2)),
            "output_audio_stream_ambiguous");

    [Fact]
    public async Task AvFinalizer_Probe_ExtraVideoStream_FailsClosed()
        => await AssertProbeFailsClosed("extra-video",
            PostMuxMeta(VideoStream(), VideoStream(index: 2), AudioStream()),
            "output_video_stream_ambiguous");

    [Fact]
    public async Task AvFinalizer_Probe_ExtraDataStream_FailsClosed()
        => await AssertProbeFailsClosed("extra-data",
            PostMuxMeta(VideoStream(), AudioStream(), new ProbeStreamInfo { Index = 2, CodecType = "data" }),
            "output_unexpected_extra_streams");

    [Fact]
    public async Task AvFinalizer_Probe_DuplicateStreamIndex_FailsClosed()
        => await AssertProbeFailsClosed("dup-index",
            PostMuxMeta(VideoStream(index: 0), AudioStream(index: 0)),
            "output_duplicate_stream_index");

    [Fact]
    public async Task AvFinalizer_Probe_WrongVideoIndex_FailsClosed()
        => await AssertProbeFailsClosed("wrong-vindex",
            PostMuxMeta(VideoStream(index: 5), AudioStream()),
            "output_video_stream_index");

    [Fact]
    public async Task AvFinalizer_Probe_WrongAudioIndex_FailsClosed()
        => await AssertProbeFailsClosed("wrong-aindex",
            PostMuxMeta(VideoStream(), AudioStream(index: 9)),
            "output_audio_stream_index");

    [Fact]
    public async Task AvFinalizer_Probe_MissingVideoStart_FailsClosed()
        => await AssertProbeFailsClosed("missing-vstart",
            PostMuxMeta(VideoStream(start: null), AudioStream()),
            "output_video_start_time_invalid");

    [Fact]
    public async Task AvFinalizer_Probe_MissingVideoDuration_FailsClosed()
        => await AssertProbeFailsClosed("missing-vdur",
            PostMuxMeta(VideoStream(duration: null), AudioStream()),
            "output_video_duration_invalid");

    [Fact]
    public async Task AvFinalizer_Probe_NanVideoStart_FailsClosed()
        => await AssertProbeFailsClosed("nan-vstart",
            PostMuxMeta(VideoStream(start: double.NaN), AudioStream()),
            "output_video_start_time_invalid");

    [Fact]
    public async Task AvFinalizer_Probe_InfinityAudioDuration_FailsClosed()
        => await AssertProbeFailsClosed("inf-adur",
            PostMuxMeta(VideoStream(), AudioStream(duration: double.PositiveInfinity)),
            "output_audio_duration_invalid");

    [Fact]
    public async Task AvFinalizer_Probe_NonFiniteFormatDuration_FailsClosed()
        => await AssertProbeFailsClosed("nan-format",
            new OutputMeta
            {
                DurationSeconds = double.PositiveInfinity,
                HasAudioStream = true,
                ProbeStreams = new[] { VideoStream(), AudioStream() }
            },
            "output_duration_non_finite");

    [Fact]
    public async Task AvFinalizer_Probe_AudioStartTooLate_FailsClosed()
        => await AssertProbeFailsClosed("start-late",
            PostMuxMeta(VideoStream(), AudioStream(start: 0.5, duration: 1.5)),
            "output_audio_start_late");

    [Fact]
    public async Task AvFinalizer_Probe_AudioEndTooShort_FailsClosed()
        => await AssertProbeFailsClosed("end-short",
            PostMuxMeta(VideoStream(), AudioStream(start: 0.0, duration: 1.5)),
            "output_audio_end_short");

    [Fact]
    public void Microphone_Regression_MissingTrack_KeepsMicrophoneKey()
    {
        SkipIfNoFfmpeg();
        var (videoPath, _) = GenerateSysloopInputs();
        var outputPath = Path.Combine(_tmpDir, $"mic-missing-{Guid.NewGuid():N}.mp4");

        var result = AvFinalizer.Finalize(
            videoPath,
            Path.Combine(_tmpDir, $"does-not-exist-{Guid.NewGuid():N}.wav"),
            outputPath,
            audioPreRoll: TimeSpan.FromSeconds(1.0),
            audioSourceKind: AudioCaptureSourceKind.Microphone,
            applyContinuityCheck: false);

        // The microphone source must keep its own source-specific key.
        AssertErrorCode(result, "missing_audio_track");
        Assert.Contains(result.Meta.Warnings, w => w.StartsWith("microphone_missing_track", StringComparison.Ordinal));
        Assert.False(File.Exists(outputPath), "final output must not be published");
    }

    private sealed class FailingPublisher : IStagingToFinalPublisher
    {
        public Task<PublishResult> PublishAsync(
            string stagingPath,
            string finalPath,
            CancellationToken cancellationToken = default,
            IFileCommitGate? commitGate = null)
        {
            return Task.FromResult(new PublishResult
            {
                Success = false,
                FailureCategory = "simulated_publish_failure"
            });
        }
    }

    [Fact]
    public void AvFinalizer_Success_DeletesTempFiles()
    {
        var muxedFixture = GenerateFixtureMuxedOutput();

        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: muxedFixture);
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

        File.Copy(muxedFixture, video.OutputPath!, overwrite: true);
        File.Copy(muxedFixture, audio.OutputPath!, overwrite: true);

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

    private string GenerateFixtureMuxedOutput()
    {
        var path = Path.Combine(_tmpDir, $"fixture-muxed-{Guid.NewGuid():N}.mp4");
        var videoPath = Path.Combine(_tmpDir, $"fixture-muxed-video-{Guid.NewGuid():N}.mp4");
        var audioPath = Path.Combine(_tmpDir, $"fixture-muxed-audio-{Guid.NewGuid():N}.wav");
        try
        {
            GenerateTestVideo(videoPath, 2);
            GenerateTestAudio(audioPath, 3);
            // Mux H.264 video + PCM WAV → AAC into a single MP4
            RunFfmpeg($"-y -nostats -loglevel error -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -b:a 128k -map 0:v:0 -map 1:a:0 -shortest \"{path}\"");
        }
        finally
        {
            TryDeleteFile(videoPath);
            TryDeleteFile(audioPath);
        }
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
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

    /// <summary>
    /// Parses a numeric ffprobe stream field (e.g. start_time, duration) as a
    /// double using invariant culture. Returns null when the field is absent or
    /// not parseable, mirroring the production probe's nullable semantics.
    /// </summary>
    private static double? ParseStreamDouble(JsonNode? node, string field)
    {
        var raw = node?[field]?.GetValue<string>();
        if (raw == null)
            return null;
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
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
