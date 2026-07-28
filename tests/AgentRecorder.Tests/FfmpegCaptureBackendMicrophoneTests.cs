using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class FfmpegCaptureBackendMicrophoneTests
{
    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_WithMicrophone_AddsDshowInputAndAacEncoding(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60,
            Microphone = true,
            MicDevice = "mic_1"
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        Assert.Contains("-f", args);
        Assert.Contains("dshow", args);
        Assert.Contains("audio=mic_1", args);
        Assert.Contains("-c:a", args);
        Assert.Contains("aac", args);
        Assert.Contains("-b:a", args);
        Assert.Contains("128k", args);
        Assert.Contains("-af", args);
        var af = args[args.IndexOf("-af") + 1];
        Assert.Contains("aresample=async=1:first_pts=0", af);
        Assert.Contains("silencedetect", af);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_WithoutMicrophone_DoesNotAddAudioInputOrCodec(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60,
            Microphone = false
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        Assert.DoesNotContain("dshow", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("aac", args);
        Assert.DoesNotContain("-af", args);
    }

    [Fact]
    public void BuildArgs_MicrophoneWithSpecialCharacters_KeepsDeviceIdAsSingleArgument()
    {
        var deviceId = "Mic with spaces \"and quotes\" \\path";
        var cfg = new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            Microphone = true,
            MicDevice = deviceId
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        var audioInput = args.FirstOrDefault(a => a.StartsWith("audio="));
        Assert.Equal($"audio={deviceId}", audioInput);
    }

    [Fact]
    public void BuildArgs_MicrophoneWithDuration_AppliesDurationToVideoInputAndOutput()
    {
        var cfg = new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 45,
            Microphone = true,
            MicDevice = "mic_1"
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        var durations = args.Select((a, i) => (arg: a, index: i))
            .Where(x => x.arg == "-t")
            .Select(x => x.index)
            .ToList();

        var audioInput = args.IndexOf("audio=mic_1");
        var videoInput = args.IndexOf("desktop");
        var outputPath = args.IndexOf(cfg.OutputPath);

        Assert.Equal(2, durations.Count);
        Assert.True(audioInput >= 0);
        Assert.True(videoInput >= 0);
        Assert.True(outputPath >= 0);

        // The first -t must be on the video input (after audio input is opened,
        // so the microphone is not constrained before it has a chance to warm up).
        Assert.True(durations[0] > audioInput, "duration must not appear before the audio input");
        Assert.True(durations[0] < videoInput, "duration must limit the video input before it begins capturing");
        Assert.True(durations[1] < outputPath, "duration must also limit the final muxed output");
        Assert.Equal("45", args[durations[0] + 1]);
        Assert.Equal("45", args[durations[1] + 1]);
    }

    [Fact]
    public void RenderCommandArgs_SpecialCharacters_QuotesForDisplayOnly()
    {
        var args = new List<string>
        {
            "-i",
            "audio=Mic with spaces \"and quotes\" \\path",
            "-af",
            "aresample=async=1:first_pts=0"
        };

        var rendered = FfmpegCaptureBackend.RenderCommandArgs(args);

        Assert.Contains("\"audio=Mic with spaces \\\"and quotes\\\" \\\\path\"", rendered);
        Assert.DoesNotContain("-i audio=Mic with spaces", rendered);
    }

    [Fact]
    public void ClassifyAudioOutcome_NoMicrophoneConfig_SetsNotRequested()
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, "any stderr", cfg: null);

        Assert.Equal("not_requested", meta.AudioStatus);
    }

    [Fact]
    public void ClassifyAudioOutcome_MicrophoneDisabled_SetsNotRequested()
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, "could not open audio device", new CaptureConfig { Microphone = false });

        Assert.Equal("not_requested", meta.AudioStatus);
    }

    [Fact]
    public void ClassifyAudioOutcome_EmptyStderr_WithAacStream_SetsRecorded()
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac" };
        InvokeClassify(meta, "", new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.Empty(meta.Warnings);
    }

    [Theory]
    [InlineData("could not open audio device")]
    [InlineData("audio device not found")]
    [InlineData("cannot open audio device")]
    [InlineData("I/O error while opening audio=mic_1")]
    public void ClassifyAudioOutcome_OpenFailurePatterns_SetsStartFailed(string stderr)
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("start_failed", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_start_failed"));
    }

    [Theory]
    [InlineData("error reading input")]
    [InlineData("I/O error dshow")]
    public void ClassifyAudioOutcome_RuntimeLossPatterns_WithAacStream_SetsLost(string stderr)
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac" };
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("lost", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_lost"));
    }

    [Theory]
    [InlineData("error reading input")]
    [InlineData("I/O error dshow")]
    public void ClassifyAudioOutcome_RuntimeLossPatterns_WithoutAudioStream_SetsMissingAudioTrack(string stderr)
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("missing_audio_track", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Fact]
    public void ClassifyAudioOutcome_RuntimeLossPatterns_NonAacStream_SetsMissingAudioTrack()
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "mp3" };
        InvokeClassify(meta, "error reading input", new CaptureConfig { Microphone = true });

        Assert.Equal("missing_audio_track", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Fact]
    public void ClassifyAudioOutcome_BufferUnderrunWithoutOtherLossEvidence_TreatedAsRecordedWithWarning()
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac" };
        InvokeClassify(meta, "buffer underrun detected", new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_buffer_underrun"));
    }

    [Fact]
    public void ClassifyAudioOutcome_BufferUnderrunWithoutAudioStream_SetsMissingAudioTrack()
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, "buffer underrun detected", new CaptureConfig { Microphone = true });

        Assert.Equal("missing_audio_track", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Theory]
    [InlineData("some unrelated progress text")]
    [InlineData("")]
    public void ClassifyAudioOutcome_NoAudioStreamEvidence_SetsMissingAudioTrack(string stderr)
    {
        var meta = new OutputMeta();
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("missing_audio_track", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Fact]
    public void ClassifyAudioOutcome_AacStreamPresent_SetsRecorded()
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac" };
        InvokeClassify(meta, "some unrelated progress text", new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.Empty(meta.Warnings);
    }

    [Fact]
    public void ClassifyAudioOutcome_NonAacAudioStream_SetsMissingAudioTrack()
    {
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "mp3" };
        InvokeClassify(meta, "", new CaptureConfig { Microphone = true });

        Assert.Equal("missing_audio_track", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_WithMicrophone_DshowInputBeforeVideoInput(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60,
            Microphone = true,
            MicDevice = "mic_1"
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        var audioInput = args.IndexOf("audio=mic_1");
        var videoInput = args.IndexOf("desktop");

        Assert.True(audioInput >= 0, "dshow audio input must be present");
        Assert.True(videoInput >= 0, "gdigrab video input must be present");
        Assert.True(audioInput < videoInput, "dshow audio input must be opened before gdigrab video input");
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_WithMicrophone_UsesExplicitMapForAudioAndVideo(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60,
            Microphone = true,
            MicDevice = "mic_1"
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        var mapAudio = args.IndexOf("0:a:0");
        var mapVideo = args.IndexOf("1:v:0");

        Assert.True(mapAudio >= 0, "must explicitly map audio stream from input 0");
        Assert.True(mapVideo >= 0, "must explicitly map video stream from input 1");
        Assert.True(mapAudio < mapVideo, "audio map must precede video map");
    }

    [Theory]
    [InlineData("display")]
    [InlineData("window")]
    [InlineData("region")]
    public void BuildArgs_WithoutMicrophone_NoExplicitMapAndVideoFirst(string sourceKind)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 1920, 1080),
            Fps = 30,
            OutputPath = "C:\\temp\\out.mp4",
            DurationSeconds = 60,
            Microphone = false
        };

        var args = FfmpegCaptureBackend.BuildArgs(cfg);

        Assert.DoesNotContain("0:a:0", args);
        Assert.DoesNotContain("1:v:0", args);
        Assert.DoesNotContain("-map", args);

        var videoInput = args.IndexOf("desktop");
        Assert.True(videoInput >= 0, "gdigrab video input must be present");
        Assert.DoesNotContain("dshow", args);
    }

    [Fact]
    public void ClassifyAudioOutcome_InternalLongSilence_WithAacStream_AddsInterruptionWarning()
    {
        var stderr =
            "[silencedetect @ 000001] silence_start: 2.0\n" +
            "[silencedetect @ 000001] silence_end: 6.5 | silence_duration: 4.5\n";

        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac", DurationSeconds = 10 };
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_signal_interruption_suspected"));
    }

    [Fact]
    public void ClassifyAudioOutcome_InitialAndTrailingSilence_DoesNotAddInterruptionWarning()
    {
        var stderr =
            "[silencedetect @ 000001] silence_start: 0.0\n" +
            "[silencedetect @ 000001] silence_end: 4.0 | silence_duration: 4.0\n" +
            "[silencedetect @ 000001] silence_start: 8.0\n" +
            "[silencedetect @ 000001] silence_end: 10.0 | silence_duration: 2.0\n";

        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac", DurationSeconds = 10 };
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.DoesNotContain(meta.Warnings, w => w.Contains("microphone_signal_interruption_suspected"));
    }

    [Fact]
    public void ClassifyAudioOutcome_RuntimeAudioLost_WithAacStream_SetsLostAndAudioLostAtMs()
    {
        var lostAtMs = 1234567890L;
        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac" };
        InvokeClassify(meta, "clean stderr", new CaptureConfig { Microphone = true }, lostAtMs);

        Assert.Equal("lost", meta.AudioStatus);
        Assert.Equal(lostAtMs, meta.AudioLostAtMs);
        Assert.Contains(meta.Warnings, w => w.Contains("microphone_lost"));
    }

    [Fact]
    public void ClassifyAudioOutcome_InternalShortSilence_DoesNotAddInterruptionWarning()
    {
        var stderr =
            "[silencedetect @ 000001] silence_start: 2.0\n" +
            "[silencedetect @ 000001] silence_end: 4.5 | silence_duration: 2.5\n";

        var meta = new OutputMeta { HasAudioStream = true, AudioCodec = "aac", DurationSeconds = 10 };
        InvokeClassify(meta, stderr, new CaptureConfig { Microphone = true });

        Assert.Equal("recorded", meta.AudioStatus);
        Assert.DoesNotContain(meta.Warnings, w => w.Contains("microphone_signal_interruption_suspected"));
    }

    private static void InvokeClassify(OutputMeta meta, string stderr, CaptureConfig? cfg, long runtimeAudioLostAtMs = 0)
    {
        var method = typeof(FfmpegCaptureBackend).GetMethod("ClassifyAudioOutcome",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { meta, stderr, cfg, runtimeAudioLostAtMs });
    }
}
