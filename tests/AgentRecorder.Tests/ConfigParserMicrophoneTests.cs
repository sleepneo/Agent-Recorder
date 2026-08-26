using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for microphone device resolution in <see cref="ConfigParser"/>.
/// These tests exercise the request-parsing path without starting a real recording.
/// </summary>
[Collection("NonParallel-AgentRecorderEnvVar")]
public class ConfigParserMicrophoneTests : IDisposable
{
    private const string TestModeVar = "AGENT_RECORDER_TEST_MODE";

    public ConfigParserMicrophoneTests()
    {
        Environment.SetEnvironmentVariable(TestModeVar, "1", EnvironmentVariableTarget.Process);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestModeVar, null, EnvironmentVariableTarget.Process);
    }

    private static JsonNode Cfg(string audioJson)
    {
        var audioPart = string.IsNullOrEmpty(audioJson) ? "" : "," + audioJson;
        return JsonNode.Parse(
            $"{{\"source\":{{\"type\":\"display\",\"display_id\":\"display_1\"}}{audioPart},\"stop_condition\":{{\"type\":\"duration\",\"seconds\":60}}}}")!;
    }

    [Fact]
    public void Build_MicrophoneExplicitDeviceId_ResolvesDevice()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_a", "Alpha Mic", false, "active"),
            new MicrophoneDeviceInfo("mic_b", "Beta Mic", true, "active"));

        var rec = ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true,\"device_id\":\"mic_a\"}}"), "test", out var summary, provider);

        Assert.True(rec.Microphone);
        Assert.Equal("mic_a", rec.MicrophoneDeviceId);
        Assert.Equal("Alpha Mic", rec.MicrophoneDeviceName);
        Assert.Equal("mic_a", rec.Config.MicDevice);
        Assert.Contains("Alpha Mic", GetSummaryAudio(summary));
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_SingleActiveDevice_AutoSelects()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Solo Mic", true, "active"));

        var rec = ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider);

        Assert.True(rec.Microphone);
        Assert.Equal("mic_1", rec.MicrophoneDeviceId);
        Assert.Equal("Solo Mic", rec.MicrophoneDeviceName);
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_MultipleActiveNoDefault_ReturnsDeviceRequired()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic One", false, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic Two", false, "active"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(400, ex.Status);
        Assert.Equal("AUDIO_DEVICE_REQUIRED", ex.Code);
        Assert.Equal("list_audio_devices", GetSuggestedAction(ex.Details));
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_SingleReliableDefault_AutoSelectsDefault()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic One", true, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic Two", false, "active"));

        var rec = ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider);

        Assert.True(rec.Microphone);
        Assert.Equal("mic_1", rec.MicrophoneDeviceId);
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_NoReliableDefaultMultipleActive_ReturnsDeviceRequired()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic One", false, "active"),
            new MicrophoneDeviceInfo("mic_2", "Mic Two", false, "active"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(400, ex.Status);
        Assert.Equal("AUDIO_DEVICE_REQUIRED", ex.Code);
    }

    [Fact]
    public void Build_MicrophoneNoActiveDevices_ReturnsDeviceNotAvailable()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Disabled Mic", false, "inactive"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(503, ex.Status);
        Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public void Build_MicrophoneUnknownDeviceId_ReturnsDeviceNotFound()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Known Mic", true, "active"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true,\"device_id\":\"missing\"}}"), "test", out _, provider));

        Assert.Equal(404, ex.Status);
        Assert.Equal("AUDIO_DEVICE_NOT_FOUND", ex.Code);
        Assert.Equal("list_audio_devices", GetSuggestedAction(ex.Details));
    }

    [Fact]
    public void Build_MicrophoneEnumerationFails_ReturnsServiceUnavailable()
    {
        var provider = new FailingProvider(new MicrophoneEnumerationException("device_enumeration_unavailable", "boom"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(503, ex.Status);
        Assert.Equal("device_enumeration_unavailable", ex.Code);
        Assert.Equal("retry_or_check_audio_devices", GetSuggestedAction(ex.Details));
    }

    [Theory]
    [InlineData("\"audio\":{\"microphone\":{\"enabled\":false}}")]
    [InlineData("")]
    public void Build_MicrophoneDisabledOrAbsent_DoesNotRequestMicrophone(string audioJson)
    {
        var rec = ConfigParser.Build(Cfg(audioJson), "test", out _);

        Assert.False(rec.Microphone);
        Assert.Null(rec.MicrophoneDeviceId);
        Assert.False(rec.Config.Microphone);
    }

    [Fact]
    public void Build_SystemAudioEnabled_IsNotBlockedByAnExperimentGate()
    {
        var endpoint = new SystemAudioEndpointInfo("render_1", "Speakers", "render", "active", true);
        var provider = new FakeSystemAudioProvider(endpoint);
        var rec = ConfigParser.Build(
            Cfg("\"audio\":{\"system_audio\":{\"enabled\":true}}"),
            "test",
            out _,
            systemAudioEndpointProvider: provider);

        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, rec.AudioSourceKind);
        Assert.Equal("render_1", rec.SystemAudioEndpointId);
    }

    [Fact]
    public void Build_MicrophoneEnabled_SummaryDoesNotLeakDeviceNameIntoAudit()
    {
        // The summary is shown in the confirmation UI, but the device display name
        // must not appear in the audit log. This test only asserts the summary shape;
        // audit-log leakage is covered by ConsentInvariantTests.
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Secret Mic Name", true, "active"));

        ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out var summary, provider);

        Assert.Equal("Secret Mic Name", summary.AudioDevice);
        Assert.Equal("microphone", summary.AudioSourceKind);
    }

    [Fact]
    public void ResolveAudioIntent_MutedDevice_ThrowsAudioDeviceMuted()
    {
        var provider = new FakeProvider(new MicrophoneDeviceInfo("mic_1", "Muted Mic", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(true, 0));

        var ex = Assert.Throws<ApiException>(() =>
            ConfigParser.ResolveAudioIntent(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), provider, statusProvider));

        Assert.Equal(409, ex.Status);
        Assert.Equal("AUDIO_DEVICE_MUTED", ex.Code);
        Assert.Equal("unmute_microphone_in_windows_settings", GetSuggestedAction(ex.Details));
        Assert.Equal("mic_1", GetDeviceId(ex.Details));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void ResolveAudioIntent_UnknownMuteState_DoesNotBlock(bool? isMuted, int? volumePercent)
    {
        var provider = new FakeProvider(new MicrophoneDeviceInfo("mic_1", "Mic", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(isMuted, volumePercent));

        var device = ConfigParser.ResolveAudioIntent(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), provider, statusProvider);

        Assert.NotNull(device);
        Assert.Equal(isMuted, device.IsMuted);
        Assert.Equal(volumePercent, device.VolumePercent);
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_SingleUnknownState_AutoSelects()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Solo Mic", IsDefault: null, State: null));

        var rec = ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider);

        Assert.True(rec.Microphone);
        Assert.Equal("mic_1", rec.MicrophoneDeviceId);
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_MultipleUnknownStateNoDefault_ReturnsDeviceRequired()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic One", IsDefault: null, State: null),
            new MicrophoneDeviceInfo("mic_2", "Mic Two", IsDefault: null, State: null));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(400, ex.Status);
        Assert.Equal("AUDIO_DEVICE_REQUIRED", ex.Code);
    }

    [Fact]
    public void Build_MicrophoneOmittedDeviceId_AllExplicitlyInactive_ReturnsDeviceNotAvailable()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Inactive One", IsDefault: false, State: "inactive"),
            new MicrophoneDeviceInfo("mic_2", "Inactive Two", IsDefault: false, State: "inactive"));

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true}}"), "test", out _, provider));

        Assert.Equal(503, ex.Status);
        Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public void Build_MicrophoneExplicitDeviceId_UnknownState_DoesNotBlock()
    {
        var provider = new FakeProvider(
            new MicrophoneDeviceInfo("mic_1", "Mic", IsDefault: null, State: null));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(false, 50, null, null));

        var rec = ConfigParser.Build(Cfg("\"audio\":{\"microphone\":{\"enabled\":true,\"device_id\":\"mic_1\"}}"), "test", out _, provider, statusProvider);

        Assert.True(rec.Microphone);
        Assert.Equal("mic_1", rec.MicrophoneDeviceId);
    }

    [Fact]
    public void ResolveAudioIntent_StatusProviderFreshCall_PerDevice()
    {
        var provider = new FakeProvider(new MicrophoneDeviceInfo("mic_1", "Mic", true, "active"));
        var statusProvider = new FakeStatusProvider(new MicrophoneStatus(false, 50));

        ConfigParser.ResolveAudioIntent(Cfg("\"audio\":{\"microphone\":{\"enabled\":true,\"device_id\":\"mic_1\"}}"), provider, statusProvider);

        Assert.Equal(1, statusProvider.CallCount);
        Assert.Equal("mic_1", statusProvider.LastDeviceId);
    }

    private static string? GetDeviceId(object? details)
    {
        if (details == null) return null;
        var json = JsonSerializer.Serialize(details);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("device_id", out var p) ? p.GetString() : null;
    }

    private static string GetSummaryAudio(RecordingRequestSummary summary) => summary.Audio;

    private static string? GetSuggestedAction(object? details)
    {
        if (details == null) return null;
        var json = JsonSerializer.Serialize(details);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("suggested_action", out var p) ? p.GetString() : null;
    }

    private static string? GetCapability(object? details)
    {
        if (details == null) return null;
        var json = JsonSerializer.Serialize(details);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("capability", out var p)) return null;
        return p.GetString();
    }

    private sealed class FakeProvider : IMicrophoneDeviceProvider
    {
        private readonly MicrophoneDeviceInfo[] _devices;
        public FakeProvider(params MicrophoneDeviceInfo[] devices) => _devices = devices;
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(_devices.ToList());
    }

    private sealed class FakeSystemAudioProvider : ISystemAudioEndpointProvider
    {
        private readonly SystemAudioEndpointInfo _endpoint;
        public FakeSystemAudioProvider(SystemAudioEndpointInfo endpoint) => _endpoint = endpoint;

        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { _endpoint });

        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemAudioEndpointInfo?>(_endpoint);

        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<SystemAudioEndpointInfo?>(endpointId == _endpoint.Id ? _endpoint : null);
    }

    private sealed class FailingProvider : IMicrophoneDeviceProvider
    {
        private readonly Exception _exception;
        public FailingProvider(Exception exception) => _exception = exception;
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<MicrophoneDeviceInfo>>(_exception);
    }

    private sealed class FakeStatusProvider : IMicrophoneStatusProvider
    {
        private readonly MicrophoneStatus _status;
        public FakeStatusProvider(MicrophoneStatus status) => _status = status;
        public int CallCount { get; private set; }
        public string? LastDeviceId { get; private set; }

        public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastDeviceId = dshowDeviceId;
            return Task.FromResult(_status);
        }
    }
}
