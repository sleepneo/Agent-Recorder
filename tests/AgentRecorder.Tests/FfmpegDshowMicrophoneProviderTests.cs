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

    [Fact]
    public void Parse_EnglishSample_ExtractsDevicesWithAlternativeNames()
    {
        var devices = DshowAudioDeviceParser.Parse(EnglishSample);

        Assert.Equal(2, devices.Count);
        Assert.Equal("@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", devices[0].Id);
        Assert.Equal("Microphone (Realtek(R) Audio)", devices[0].Name);
        Assert.Null(devices[0].IsDefault);
        Assert.Null(devices[0].State);
        Assert.Equal("@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D}", devices[1].Id);
        Assert.Equal("Line (Voicemod Virtual Audio Device)", devices[1].Name);
        Assert.Null(devices[1].IsDefault);
        Assert.Null(devices[1].State);
    }

    [Fact]
    public void Parse_ChineseSample_PreservesUnicodeNames()
    {
        var devices = DshowAudioDeviceParser.Parse(ChineseSample);

        var device = Assert.Single(devices);
        Assert.Equal("@device_cm_{XXX}\\wave_{YYY}", device.Id);
        Assert.Equal("麦克风 (Realtek 音频)", device.Name);
    }

    [Fact]
    public void Parse_EmptyStderr_ReturnsEmptyList()
    {
        Assert.Empty(DshowAudioDeviceParser.Parse(""));
        Assert.Empty(DshowAudioDeviceParser.Parse("   "));
        Assert.Empty(DshowAudioDeviceParser.Parse("\r\n"));
    }

    [Fact]
    public void Parse_NoAudioSection_ReturnsEmptyList()
    {
        var stderr = "ffmpeg version\r\n[dshow] DirectShow video devices\r\n\"HD Webcam\"\r\n";
        Assert.Empty(DshowAudioDeviceParser.Parse(stderr));
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

        var devices = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(2, devices.Count);
        Assert.Equal("id_1", devices[0].Id);
        Assert.Equal("Microphone", devices[0].Name);
        Assert.Equal("id_2", devices[1].Id);
        Assert.Equal("Microphone (2)", devices[1].Name);
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

        var devices = DshowAudioDeviceParser.Parse(stderr);

        var device = Assert.Single(devices);
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
            "[dshow]  no quotes here\r\n" +
            "[dshow]  \"Orphan name without alternative\"\r\n";

        var devices = DshowAudioDeviceParser.Parse(stderr);

        var device = Assert.Single(devices);
        Assert.Equal("good_id", device.Id);
        Assert.Equal("Good Mic", device.Name);
    }

    [Fact]
    public void Parse_LongStderrWithTrailingGarbage_ExtractsAudioSectionOnly()
    {
        var header = string.Join("\r\n", Enumerable.Repeat("[info] unrelated preamble line", 500));
        var footer = string.Join("\r\n", Enumerable.Repeat("[info] unrelated footer line", 500));
        var stderr = $"{header}\r\n{EnglishSample}\r\n{footer}";

        var devices = DshowAudioDeviceParser.Parse(stderr);

        Assert.Equal(2, devices.Count);
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

        var devices = DshowAudioDeviceParser.Parse(stderr);

        var device = Assert.Single(devices);
        Assert.Equal("@device_cm_{XXX}\\wave_{YYY}", device.Id);
        Assert.Equal("耳机 (AirPods Pro)", device.Name);
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
