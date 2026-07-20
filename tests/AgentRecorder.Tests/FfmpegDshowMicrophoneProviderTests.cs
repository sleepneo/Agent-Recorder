using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the FFmpeg dshow microphone device parser and provider.
/// These tests do not access real hardware; they feed captured/constructed
/// FFmpeg stderr samples through fakes.
/// </summary>
public class FfmpegDshowMicrophoneProviderTests
{
    private static readonly string EnglishSample =
        "ffmpeg version git-2019-10-22 something\r\n" +
        "[dshow @ 000001] DirectShow audio devices\r\n" +
        "[dshow @ 000001]  \"Microphone (Realtek(R) Audio)\"\r\n" +
        "[dshow @ 000001]    Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n" +
        "[dshow @ 000001]  \"Line (Voicemod Virtual Audio Device)\"\r\n" +
        "[dshow @ 000001]    Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n" +
        "[dshow @ 000001] DirectShow video devices\r\n" +
        "dummy: Immediate exit requested\r\n";

    private static readonly string ChineseSample =
        "[dshow @ 000001] DirectShow audio devices\r\n" +
        "[dshow @ 000001]  \"麦克风 (Realtek 音频)\"\r\n" +
        "[dshow @ 000001]    Alternative name \"@device_cm_{XXX}\\wave_{YYY}\"\r\n";

    private static readonly string TaggedChineseSample =
        "[in#0 @ 000001] \"耳机 (AirPods Pro)\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n";

    private static readonly string TaggedMultipleSample =
        "[in#0 @ 000001] \"Microphone (Realtek(R) Audio)\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"id_1\"\r\n" +
        "[in#0 @ 000001] \"Line (Voicemod Virtual Audio Device)\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"id_2\"\r\n";

    private static readonly string TaggedVideoAudioSample =
        "[in#0 @ 000001] \"HD Webcam\" (video)\r\n" +
        "[in#0 @ 000001]   Alternative name \"video_id_1\"\r\n" +
        "[in#0 @ 000001] \"Microphone (Realtek(R) Audio)\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"audio_id_1\"\r\n";

    private static readonly string TaggedSameNameVideoAudioSample =
        "[in#0 @ 000001] \"Camera Plus\" (video)\r\n" +
        "[in#0 @ 000001]   Alternative name \"video_camera_plus\"\r\n" +
        "[in#0 @ 000001] \"Camera Plus\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"audio_camera_plus\"\r\n";

    private static readonly string TaggedNoAlternativeSample =
        "[in#0 @ 000001] \"Microphone (Realtek(R) Audio)\" (audio)\r\n" +
        "[in#0 @ 000001] Could not enumerate audio only devices (or none found).\r\n";

    private static readonly string TaggedOrphanAlternativeSample =
        "[in#0 @ 000001]   Alternative name \"orphan_id\"\r\n";

    private static readonly string TaggedInterruptedCandidateSample =
        "[in#0 @ 000001] \"Old Mic\" (audio)\r\n" +
        "[in#0 @ 000001] \"New Mic\" (audio)\r\n" +
        "[in#0 @ 000001]   Alternative name \"new_id\"\r\n";

    private static readonly string TaggedNoDevicesMarkerSample =
        "[in#0 @ 000001] Could not enumerate video devices (or none found).\r\n" +
        "[in#0 @ 000001] Could not enumerate audio only devices (or none found).\r\n" +
        "Error opening input file dummy.\r\n";

    private static readonly string OnlyDummyErrorSample =
        "ffmpeg version 8.1.1\r\n" +
        "Error opening input file dummy.\r\n";

    private static readonly string GarbageStderrSample =
        "ffmpeg version 8.1.1\r\n" +
        "some random \"quoted\" text\r\n" +
        "another line\r\n";

    [Fact]
    public void Parse_EnglishSample_ExtractsDevicesWithAlternativeNames()
    {
        var result = DshowAudioDeviceParser.Parse(EnglishSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", result.Devices[0].Id);
        Assert.Equal("Microphone (Realtek(R) Audio)", result.Devices[0].Name);
        Assert.Null(result.Devices[0].IsDefault);
        Assert.Null(result.Devices[0].State);
        Assert.Equal("@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D}", result.Devices[1].Id);
        Assert.Equal("Line (Voicemod Virtual Audio Device)", result.Devices[1].Name);
        Assert.Null(result.Devices[1].IsDefault);
        Assert.Null(result.Devices[1].State);
    }

    [Fact]
    public void Parse_ChineseSample_PreservesUnicodeNames()
    {
        var result = DshowAudioDeviceParser.Parse(ChineseSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("@device_cm_{XXX}\\wave_{YYY}", device.Id);
        Assert.Equal("麦克风 (Realtek 音频)", device.Name);
    }

    [Fact]
    public void Parse_EmptyStderr_ReturnsUnrecognized()
    {
        Assert.Equal(DshowParseConclusion.Unrecognized, DshowAudioDeviceParser.Parse("").Conclusion);
        Assert.Equal(DshowParseConclusion.Unrecognized, DshowAudioDeviceParser.Parse("   ").Conclusion);
        Assert.Equal(DshowParseConclusion.Unrecognized, DshowAudioDeviceParser.Parse("\r\n").Conclusion);
    }

    [Fact]
    public void Parse_NoAudioSection_ReturnsUnrecognized()
    {
        var stderr = "ffmpeg version\r\n[dshow] DirectShow video devices\r\n\"HD Webcam\"\r\n";
        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicEmptyAudioSection_ReturnsNoDevices()
    {
        var stderr =
            "[dshow @ 000001] DirectShow audio devices\r\n" +
            "[dshow @ 000001] DirectShow video devices\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedNoDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_DuplicateDisplayNames_DeduplicatesNamesWithoutChangingIds()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Microphone\"\r\n" +
            "[dshow]    Alternative name \"id_1\"\r\n" +
            "[dshow]  \"Microphone\"\r\n" +
            "[dshow]    Alternative name \"id_2\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("id_1", result.Devices[0].Id);
        Assert.Equal("Microphone", result.Devices[0].Name);
        Assert.Equal("id_2", result.Devices[1].Id);
        Assert.Equal("Microphone (2)", result.Devices[1].Name);
    }

    [Fact]
    public void Parse_DuplicateIds_KeepFirstOccurrence()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Mic A\"\r\n" +
            "[dshow]    Alternative name \"shared_id\"\r\n" +
            "[dshow]  \"Mic B\"\r\n" +
            "[dshow]    Alternative name \"shared_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        var device = Assert.Single(result.Devices);
        Assert.Equal("shared_id", device.Id);
        Assert.Equal("Mic A", device.Name);
    }

    [Fact]
    public void Parse_BadLinesAndJunk_AreIgnored()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    some random log\r\n" +
            "[dshow]    Alternative name \"good_id\"\r\n" +
            "[dshow]  no quotes here\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("good_id", device.Id);
        Assert.Equal("Good Mic", device.Name);
    }

    [Fact]
    public void Parse_LongStderrWithTrailingGarbage_ExtractsAudioSectionOnly()
    {
        var header = string.Join("\r\n", Enumerable.Repeat("[info] unrelated preamble line", 500));
        var footer = string.Join("\r\n", Enumerable.Repeat("[info] unrelated footer line", 500));
        var stderr = $"{header}\r\n{EnglishSample}\r\n{footer}";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        Assert.Equal(2, result.Devices.Count);
    }

    [Fact]
    public void Parse_TaggedChineseSample_ExtractsAudioDevice()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedChineseSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("耳机 (AirPods Pro)", device.Name);
        Assert.Equal("@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", device.Id);
    }

    [Fact]
    public void Parse_TaggedMultipleSample_PreservesOrderAndIds()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedMultipleSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("id_1", result.Devices[0].Id);
        Assert.Equal("Microphone (Realtek(R) Audio)", result.Devices[0].Name);
        Assert.Equal("id_2", result.Devices[1].Id);
        Assert.Equal("Line (Voicemod Virtual Audio Device)", result.Devices[1].Name);
    }

    [Fact]
    public void Parse_TaggedVideoAudioInterleaved_ReturnsOnlyAudio()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedVideoAudioSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("audio_id_1", device.Id);
        Assert.Equal("Microphone (Realtek(R) Audio)", device.Name);
    }

    [Fact]
    public void Parse_TaggedSameNameVideoAudio_DoesNotMismatch()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedSameNameVideoAudioSample);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("audio_camera_plus", device.Id);
        Assert.Equal("Camera Plus", device.Name);
    }

    [Fact]
    public void Parse_TaggedAudioWithoutAlternative_ReturnsUnrecognized()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedNoAlternativeSample);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedOrphanAlternative_ReturnsUnrecognized()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedOrphanAlternativeSample);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedInterruptedCandidate_IsUnrecognized()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedInterruptedCandidateSample);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedNoDevicesMarker_ReturnsNoDevices()
    {
        var result = DshowAudioDeviceParser.Parse(TaggedNoDevicesMarkerSample);

        Assert.Equal(DshowParseConclusion.RecognizedNoDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_OnlyDummyError_ReturnsUnrecognized()
    {
        var result = DshowAudioDeviceParser.Parse(OnlyDummyErrorSample);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_GarbageQuotedStderr_ReturnsUnrecognized()
    {
        var result = DshowAudioDeviceParser.Parse(GarbageStderrSample);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_QuotedString_WithBackslash_PreservesBackslash()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Mic\"\r\n" +
            "[dshow]    Alternative name \"@device_cm_{X}\\wave_{Y}\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal("@device_cm_{X}\\wave_{Y}", Assert.Single(result.Devices).Id);
    }

    [Fact]
    public void Parse_QuotedString_WithEscapedQuote_DecodesQuote()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Mic\\\"Pro\"\r\n" +
            "[dshow]    Alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("id_1", device.Id);
        Assert.Equal("Mic\"Pro", device.Name);
    }

    [Fact]
    public void Parse_TaggedQuotedString_WithEscapedQuote_DecodesQuote()
    {
        var stderr =
            "[in#0 @ 000001] \"Mic\\\"Pro\" (audio)\r\n" +
            "[in#0 @ 000001]   Alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("id_1", device.Id);
        Assert.Equal("Mic\"Pro", device.Name);
    }

    [Fact]
    public void Parse_QuotedString_Unterminated_ReturnsUnrecognized()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Mic\\\"Pro\r\n" +
            "[dshow]    Alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_QuotedString_DoubleBackslash_DecodesSingleBackslash()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Mic\\\\Pro\"\r\n" +
            "[dshow]    Alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("id_1", device.Id);
        Assert.Equal("Mic\\Pro", device.Name);
    }

    [Fact]
    public void Parse_ClassicInterruptedFriendly_IsUnrecognized()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Old Mic\"\r\n" +
            "[dshow]  \"New Mic\"\r\n" +
            "[dshow]    Alternative name \"new_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicFriendlyWithoutAlternative_IsUnrecognized()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Lonely Mic\"\r\n" +
            "[dshow] DirectShow video devices\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicFriendlyOrphanAlternative_IsUnrecognized()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]    Alternative name \"orphan_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAudioInterruptedByVideo_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 000001] \"Old Mic\" (audio)\r\n" +
            "[in#0 @ 000001] \"Webcam\" (video)\r\n" +
            "[in#0 @ 000001]   Alternative name \"video_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ValidDeviceFollowedByIncomplete_IsUnrecognized()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    Alternative name \"good_id\"\r\n" +
            "[dshow]  \"Broken Mic\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_DevicesWithNoAudioMarker_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 000001] \"Mic\" (audio)\r\n" +
            "[in#0 @ 000001]   Alternative name \"id_1\"\r\n" +
            "[in#0 @ 000001] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedLine_NonLoggerPrefix_IsUnrecognized()
    {
        var stderr =
            "warning: \"fake-device\" (audio)\r\n" +
            "[other @ 000001] \"fake-device\" (audio)\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedLine_ExtraTextAroundName_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 000001] prefix \"fake-device\" (audio)\r\n" +
            "[in#0 @ 000001] \"fake-device\" (audio) suffix\r\n" +
            "[in#0 @ 000001] \"unterminated (audio)\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAlternative_FromDifferentInputKey_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 000001] \"Mic\" (audio)\r\n" +
            "[in#1 @ 000001]   Alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAlternative_MalformedPrefix_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 000001] \"Mic\" (audio)\r\n" +
            "[in#0 @ 000001]   alternative name \"id_1\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedRealWorld8x_Fixture_Passes()
    {
        // Simulates actual FFmpeg 8.x tagged listing observed on a host with a microphone.
        var stderr =
            "[in#0 @ 000001] \"耳机 (AirPods Pro)\" (audio)\r\n" +
            "[in#0 @ 000001]   Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n" +
            "[in#0 @ 000001] \"Line (Voicemod Virtual Audio Device)\" (audio)\r\n" +
            "[in#0 @ 000001]   Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        Assert.Equal(2, result.Devices.Count);
    }

    [Fact]
    public async Task Provider_ParsesStderrReturnedByRunner()
    {
        var runner = new FakeRunner(EnglishSample, timedOut: false, exitCode: 1);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var devices = await provider.GetDevicesAsync();

        Assert.Equal(2, devices.Count);
        Assert.True(runner.WasCalled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Provider_RecognizedCompleteOutput_AcceptsListingExitCodes(int exitCode)
    {
        var runner = new FakeRunner(EnglishSample, timedOut: false, exitCode: exitCode);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var devices = await provider.GetDevicesAsync();

        Assert.Equal(2, devices.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Provider_UnrecognizedOutput_ThrowsUnavailableRegardlessOfExitCode(int exitCode)
    {
        var runner = new FakeRunner(GarbageStderrSample, timedOut: false, exitCode: exitCode);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<MicrophoneEnumerationException>(() => provider.GetDevicesAsync());
        Assert.Equal("device_enumeration_unavailable", ex.ErrorCode);
    }

    [Fact]
    public async Task Provider_NoDevicesMarker_ReturnsEmptyList()
    {
        var runner = new FakeRunner(TaggedNoDevicesMarkerSample, timedOut: false, exitCode: 0);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var devices = await provider.GetDevicesAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task Provider_Timeout_ThrowsEnumerationException()
    {
        var runner = new FakeRunner("", timedOut: true, exitCode: -1);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<MicrophoneEnumerationException>(() => provider.GetDevicesAsync());
        Assert.Equal("device_enumeration_timeout", ex.ErrorCode);
    }

    [Fact]
    public async Task Provider_RunnerThrows_ThrowsEnumerationException()
    {
        var runner = new ThrowingRunner();
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<MicrophoneEnumerationException>(() => provider.GetDevicesAsync());
        Assert.Equal("device_enumeration_unavailable", ex.ErrorCode);
    }

    [Fact]
    public async Task Provider_Cancellation_PropagatesOperationCanceled()
    {
        var runner = new FakeRunner("", timedOut: true, exitCode: -1);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetDevicesAsync(cts.Token));
    }

    [Fact]
    public async Task Provider_RequestsUtf8Encoding_ForChineseDeviceNames()
    {
        var runner = new EncodingCapturingRunner(ChineseSample, exitCode: 1);
        var provider = new FfmpegDshowMicrophoneProvider(runner, () => "ffmpeg.exe", TimeSpan.FromSeconds(1));

        var devices = await provider.GetDevicesAsync();

        Assert.Single(devices);
        Assert.Equal("麦克风 (Realtek 音频)", devices[0].Name);
        Assert.NotNull(runner.CapturedEncoding);
        Assert.Equal(Encoding.UTF8, runner.CapturedEncoding);
    }

    [Fact]
    public void Parse_AirPodsStyleChineseName_PreservesUnicodeAndParentheses()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"耳机 (AirPods Pro)\"\r\n" +
            "[dshow]    Alternative name \"@device_cm_{XXX}\\wave_{YYY}\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        var device = Assert.Single(result.Devices);
        Assert.Equal("@device_cm_{XXX}\\wave_{YYY}", device.Id);
        Assert.Equal("耳机 (AirPods Pro)", device.Name);
    }

    [Fact]
    public void Parse_MalformedLogger_ReturnsUnrecognized()
    {
        var stderr =
            "[in#garbage] \"Fake\" (audio)\r\n" +
            "[in#garbage] Alternative name \"fake_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ValidThenMalformedCandidate_ReturnsUnrecognized()
    {
        var stderr =
            "[in#0 @ 1] \"Good\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"good_id\"\r\n" +
            "[in#0 @ 1] prefix \"Bad\" (audio)\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_AlternativeTrailingText_ReturnsUnrecognized()
    {
        var stderr =
            "[in#0 @ 1] \"Good\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"good_id\" trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicSpoofInsideSection_NotRecognizedWithDevices()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "warning: \"Fake\"\r\n" +
            "warning: Alternative name \"fake_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.NotEqual(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Theory]
    [InlineData("[in#] \"Fake\" (audio)")]
    [InlineData("[in#x] \"Fake\" (audio)")]
    [InlineData("[in#0] \"Fake\" (audio)")]
    [InlineData("[in#0 @ ] \"Fake\" (audio)")]
    public void Parse_InputLoggerPrefix_MissingParts_Rejected(string line)
    {
        var stderr = line + "\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Theory]
    [InlineData("[in#0 @ identity] \"Device\" (audio)\r\n[in#0 @ identity]   Alternative name \"id_1\"\r\n")]
    [InlineData("[in#12 @ identity] \"Device\" (audio)\r\n[in#12 @ identity]   Alternative name \"id_1\"\r\n")]
    public void Parse_InputLoggerPrefix_Valid_Accepted(string stderr)
    {
        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Device", device.Name);
        Assert.Equal("id_1", device.Id);
    }

    [Fact]
    public void Parse_DshowGarbagePrefix_CannotOpenSection()
    {
        var stderr =
            "[dshow-garbage] DirectShow audio devices\r\n" +
            "[dshow-garbage]  \"Fake Mic\"\r\n" +
            "[dshow-garbage]    Alternative name \"fake_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAlternative_TrailingText_Rejected()
    {
        var stderr =
            "[in#0 @ 1] \"Good\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"good_id\" trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAlternative_NoWhitespace_Rejected()
    {
        var stderr =
            "[in#0 @ 1] \"Good\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name\"good_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicFriendly_TrailingText_Rejected()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\" trailing-junk\r\n" +
            "[dshow]    Alternative name \"good_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicAlternative_TrailingText_Rejected()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    Alternative name \"good_id\" trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicAlternative_NoWhitespace_Rejected()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    Alternative name\"good_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_NoDevicesMarker_InUnrelatedWarning_NotRecognized()
    {
        var stderr =
            "warning: Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicHeader_InUnrelatedWarning_CannotOpenSection()
    {
        var stderr =
            "warning: DirectShow audio devices\r\n" +
            "warning: \"Fake Mic\"\r\n" +
            "warning: Alternative name \"fake_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ValidTaggedThenMalformedCandidate_FailClosed()
    {
        var stderr =
            "[in#0 @ 1] \"Good\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"good_id\"\r\n" +
            "[in#0 @ 1] \"Bad\" (audio) trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ValidClassicThenMalformedCandidate_FailClosed()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    Alternative name \"good_id\"\r\n" +
            "[dshow]  \"Bad Mic\" (audio) trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ValidDeviceThenUnrelatedWarning_DeviceRetained()
    {
        var stderr =
            "[dshow] DirectShow audio devices\r\n" +
            "[dshow]  \"Good Mic\"\r\n" +
            "[dshow]    Alternative name \"good_id\"\r\n" +
            "warning: \"something\" (audio)\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Good Mic", device.Name);
        Assert.Equal("good_id", device.Id);
    }

    // ------------------------------------------------------------------
    // Task 174C: classic no-devices marker boundaries
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_ClassicNoDevicesMarker_WithoutAudioHeader_IsUnrecognized()
    {
        var stderr =
            "[dshow @ 1] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicNoDevicesMarker_WithAudioHeader_ReturnsNoDevices()
    {
        var stderr =
            "[dshow @ 1] DirectShow audio devices\r\n" +
            "[dshow @ 1] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedNoDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicNoDevicesMarker_InVideoSection_IsIgnored()
    {
        var stderr =
            "[dshow @ 1] DirectShow audio devices\r\n" +
            "[dshow @ 1] DirectShow video devices\r\n" +
            "[dshow @ 1] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedNoDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_ClassicDeviceAndNoDevicesMarker_IsUnrecognized()
    {
        var stderr =
            "[dshow @ 1] DirectShow audio devices\r\n" +
            "[dshow @ 1]  \"Good Mic\"\r\n" +
            "[dshow @ 1]    Alternative name \"good_id\"\r\n" +
            "[dshow @ 1] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_InputLoggerNoDevicesMarker_ReturnsNoDevices()
    {
        var stderr =
            "[in#0 @ 1] Could not enumerate audio only devices (or none found).\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedNoDevices, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    // ------------------------------------------------------------------
    // Task 174C: tagged video ordering
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_TaggedVideoFirstAudioLast_ReturnsOnlyAudio()
    {
        var stderr =
            "[in#0 @ 1] \"Camera\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_id\"\r\n" +
            "[in#0 @ 1] \"Good Mic\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"audio_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Good Mic", device.Name);
        Assert.Equal("audio_id", device.Id);
    }

    [Fact]
    public void Parse_TaggedAudioFirstVideoLast_ReturnsOnlyAudio()
    {
        var stderr =
            "[in#0 @ 1] \"Good Mic\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"audio_id\"\r\n" +
            "[in#0 @ 1] \"Camera\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Good Mic", device.Name);
        Assert.Equal("audio_id", device.Id);
    }

    [Fact]
    public void Parse_TaggedVideoAudioVideoInterleaved_ReturnsOnlyAudio()
    {
        var stderr =
            "[in#0 @ 1] \"Camera\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_id_1\"\r\n" +
            "[in#0 @ 1] \"Good Mic\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"audio_id\"\r\n" +
            "[in#0 @ 1] \"Another Camera\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_id_2\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Good Mic", device.Name);
        Assert.Equal("audio_id", device.Id);
    }

    [Fact]
    public void Parse_TaggedAudioVideoSameNameDifferentId_ReturnsOnlyAudioId()
    {
        var stderr =
            "[in#0 @ 1] \"Camera Plus\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_camera_plus\"\r\n" +
            "[in#0 @ 1] \"Camera Plus\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"audio_camera_plus\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.RecognizedWithDevices, result.Conclusion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("Camera Plus", device.Name);
        Assert.Equal("audio_camera_plus", device.Id);
    }

    [Fact]
    public void Parse_TaggedVideoAlternative_DifferentInputKey_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 1] \"Camera\" (video)\r\n" +
            "[in#1 @ 2]   Alternative name \"video_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedVideoAlternative_TrailingJunk_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 1] \"Camera\" (video)\r\n" +
            "[in#0 @ 1]   Alternative name \"video_id\" trailing-junk\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Parse_TaggedAudioComplete_ThenOrphanAlternative_IsUnrecognized()
    {
        var stderr =
            "[in#0 @ 1] \"Good Mic\" (audio)\r\n" +
            "[in#0 @ 1]   Alternative name \"audio_id\"\r\n" +
            "[in#0 @ 1]   Alternative name \"orphan_id\"\r\n";

        var result = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(DshowParseConclusion.Unrecognized, result.Conclusion);
        Assert.Empty(result.Devices);
    }

    private sealed class EncodingCapturingRunner : IExternalProcessRunner
    {
        private readonly string _stderr;
        private readonly int _exitCode;

        public EncodingCapturingRunner(string stderr, int exitCode)
        {
            _stderr = stderr;
            _exitCode = exitCode;
        }

        public Encoding? CapturedEncoding { get; private set; }

        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, TimeSpan timeout, bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
        {
            CapturedEncoding = stderrEncoding;
            return Task.FromResult(new ExternalProcessResult(_exitCode, false, _stderr));
        }
    }

    private sealed class FakeRunner : IExternalProcessRunner
    {
        private readonly string _stderr;
        private readonly bool _timedOut;
        private readonly int _exitCode;

        public FakeRunner(string stderr, bool timedOut, int exitCode)
        {
            _stderr = stderr;
            _timedOut = timedOut;
            _exitCode = exitCode;
        }

        public bool WasCalled { get; private set; }

        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, TimeSpan timeout, bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ExternalProcessResult(_exitCode, _timedOut, _stderr));
        }
    }

    private sealed class ThrowingRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, TimeSpan timeout, bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated runner failure");
    }
}
