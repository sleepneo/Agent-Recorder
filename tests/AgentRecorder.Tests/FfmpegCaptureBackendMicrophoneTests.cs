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
        Assert.Contains("aresample=async=1:first_pts=0", args);
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
    public void BuildArgs_MicrophoneWithDuration_AppliesDurationBeforeAudioInputAndAtOutput()
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

        var firstDuration = args.IndexOf("-t");
        var audioInput = args.IndexOf("audio=mic_1");
        var lastDuration = args.LastIndexOf("-t");
        var outputPath = args.IndexOf(cfg.OutputPath);

        Assert.True(firstDuration >= 0);
        Assert.True(audioInput >= 0);
        Assert.True(lastDuration >= 0);
        Assert.True(firstDuration < audioInput, "duration must limit the video input before the microphone input is opened");
        Assert.True(lastDuration < outputPath, "duration must also limit the final muxed output");
        Assert.Equal("45", args[firstDuration + 1]);
        Assert.Equal("45", args[lastDuration + 1]);
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

    private static void InvokeClassify(OutputMeta meta, string stderr, CaptureConfig? cfg)
    {
        var method = typeof(FfmpegCaptureBackend).GetMethod("ClassifyAudioOutcome",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { meta, stderr, cfg });
    }
}
