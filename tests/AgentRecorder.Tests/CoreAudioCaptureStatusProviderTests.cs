using System;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for the dshow device id to CoreAudio endpoint id mapping and for
/// the high-level status provider with a fake native client. These tests do not
/// access real COM hardware; they exercise the mapping and failure-degradation
/// boundaries deterministically.
/// </summary>
public class CoreAudioCaptureStatusProviderTests
{
    [Fact]
    public void ExtractGuidFromDshowId_ValidWave_ReturnsGuid()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var guid = CoreAudioCaptureStatusProvider.ExtractGuidFromDshowId(dshowId);
        Assert.Equal("{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", guid);
    }

    [Fact]
    public void ToCoreAudioEndpointId_ValidWave_ReturnsCaptureEndpointId()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);
        Assert.Equal("{0.0.1.00000000}.{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", endpointId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\dsound_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}")]
    [InlineData(@"\wave_{not-a-guid}")]
    [InlineData(@"\wave_1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D")]
    public void ExtractGuidFromDshowId_InvalidInput_ReturnsNull(string? dshowId)
    {
        Assert.Null(CoreAudioCaptureStatusProvider.ExtractGuidFromDshowId(dshowId!));
    }

    [Fact]
    public void ExtractGuidFromDshowId_CaseInsensitivePrefix_Works()
    {
        var dshowId = @"\WAVE_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var guid = CoreAudioCaptureStatusProvider.ExtractGuidFromDshowId(dshowId);
        Assert.Equal("{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", guid);
    }

    [Theory]
    [InlineData(true, 0, true, "active")]
    [InlineData(false, 7, false, "active")]
    [InlineData(false, 100, true, "active")]
    [InlineData(true, 50, false, "inactive")]
    public async Task GetStatusAsync_FakeNativeClient_ReturnsExpectedState(
        bool muted, int volume, bool isDefault, string state)
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var defaultEndpointId = isDefault
            ? endpointId
            : "{0.0.1.00000000}.{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}";
        var fake = new FakeCoreAudioNativeClient(defaultEndpointId, new CoreAudioEndpointDetails(isDefault, state, muted, volume));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Equal(muted, status.IsMuted);
        Assert.Equal(volume, status.VolumePercent);
        Assert.Equal(isDefault, status.IsDefault);
        Assert.Equal(state, status.State);
    }

    [Fact]
    public async Task GetStatusAsync_DifferentEndpoint_IsDefaultFalse()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);
        var otherEndpointId = "{0.0.1.00000000}.{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}";

        var fake = new FakeCoreAudioNativeClient(otherEndpointId, new CoreAudioEndpointDetails(true, "active", false, 50));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.False(status.IsDefault);
    }

    [Fact]
    public async Task GetStatusAsync_NativeClientThrows_ReturnsUnknown()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var fake = new ThrowingCoreAudioNativeClient();
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Null(status.IsMuted);
        Assert.Null(status.VolumePercent);
        Assert.Null(status.IsDefault);
        Assert.Null(status.State);
    }

    [Fact]
    public async Task GetStatusAsync_GetDefaultFailsButDetailsSucceed_DetailsPreserved_IsDefaultUnknown()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";

        var fake = new FakeCoreAudioNativeClient(null, new CoreAudioEndpointDetails(null, "active", false, 33));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Null(status.IsDefault);
        Assert.Equal("active", status.State);
        Assert.False(status.IsMuted);
        Assert.Equal(33, status.VolumePercent);
    }

    [Fact]
    public async Task GetStatusAsync_DefaultKnownDetailsThrows_PreservesIsDefaultTrue()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var fake = new ThrowingDetailsCoreAudioNativeClient(endpointId);
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.True(status.IsDefault);
        Assert.Null(status.State);
        Assert.Null(status.IsMuted);
        Assert.Null(status.VolumePercent);
    }

    [Fact]
    public async Task GetStatusAsync_DefaultKnownDetailsThrows_PreservesIsDefaultFalse()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var otherEndpointId = "{0.0.1.00000000}.{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}";

        var fake = new ThrowingDetailsCoreAudioNativeClient(otherEndpointId);
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.False(status.IsDefault);
        Assert.Null(status.State);
        Assert.Null(status.IsMuted);
        Assert.Null(status.VolumePercent);
    }

    [Fact]
    public async Task GetStatusAsync_DefaultAndDetailsThrow_ReturnsAllUnknown()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";

        var fake = new ThrowingCoreAudioNativeClient();
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Null(status.IsDefault);
        Assert.Null(status.State);
        Assert.Null(status.IsMuted);
        Assert.Null(status.VolumePercent);
    }

    [Fact]
    public async Task GetStatusAsync_StateLookupFails_ReturnsNullState()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var fake = new FakeCoreAudioNativeClient(endpointId, new CoreAudioEndpointDetails(null, null, false, 33));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Null(status.State);
        Assert.False(status.IsMuted);
        Assert.Equal(33, status.VolumePercent);
    }

    [Fact]
    public async Task GetStatusAsync_MuteSuccessVolumeFailure_PreservesMute()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var fake = new FakeCoreAudioNativeClient(endpointId, new CoreAudioEndpointDetails(null, "active", true, null));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.True(status.IsMuted);
        Assert.Null(status.VolumePercent);
        Assert.Equal("active", status.State);
    }

    [Fact]
    public async Task GetStatusAsync_MuteFailureVolumeSuccess_PreservesVolume()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var fake = new FakeCoreAudioNativeClient(endpointId, new CoreAudioEndpointDetails(null, "active", null, 50));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Null(status.IsMuted);
        Assert.Equal(50, status.VolumePercent);
        Assert.Equal("active", status.State);
    }

    [Fact]
    public async Task GetStatusAsync_StateSuccessVolumeActivationFails_PreservesState()
    {
        var dshowId = @"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(dshowId);

        var fake = new FakeCoreAudioNativeClient(endpointId, new CoreAudioEndpointDetails(null, "active", null, null));
        var provider = new CoreAudioCaptureStatusProvider(fake);

        var status = await provider.GetStatusAsync(dshowId);

        Assert.Equal("active", status.State);
        Assert.Null(status.IsMuted);
        Assert.Null(status.VolumePercent);
    }

    private sealed class FakeCoreAudioNativeClient : ICoreAudioNativeClient
    {
        private readonly string? _defaultEndpointId;
        private readonly CoreAudioEndpointDetails _details;
        private readonly string _expectedEndpointId;

        public FakeCoreAudioNativeClient(string? defaultEndpointId, CoreAudioEndpointDetails details)
        {
            _defaultEndpointId = defaultEndpointId;
            _details = details;
            _expectedEndpointId = string.Empty;
        }

        public FakeCoreAudioNativeClient(string? defaultEndpointId, CoreAudioEndpointDetails details, string expectedEndpointId)
        {
            _defaultEndpointId = defaultEndpointId;
            _details = details;
            _expectedEndpointId = expectedEndpointId;
        }

        public string? GetDefaultCaptureEndpointId() => _defaultEndpointId;

        public CoreAudioEndpointDetails GetEndpointDetails(string endpointId)
        {
            if (!string.IsNullOrEmpty(_expectedEndpointId) && !string.Equals(endpointId, _expectedEndpointId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected endpoint id: {endpointId}");
            return _details;
        }
    }

    private sealed class ThrowingCoreAudioNativeClient : ICoreAudioNativeClient
    {
        public string? GetDefaultCaptureEndpointId() => throw new InvalidOperationException("COM failure");
        public CoreAudioEndpointDetails GetEndpointDetails(string endpointId) => throw new InvalidOperationException("COM failure");
    }

    private sealed class ThrowingDetailsCoreAudioNativeClient : ICoreAudioNativeClient
    {
        private readonly string? _defaultEndpointId;

        public ThrowingDetailsCoreAudioNativeClient(string? defaultEndpointId)
        {
            _defaultEndpointId = defaultEndpointId;
        }

        public string? GetDefaultCaptureEndpointId() => _defaultEndpointId;

        public CoreAudioEndpointDetails GetEndpointDetails(string endpointId)
            => throw new InvalidOperationException("details failure");
    }
}
