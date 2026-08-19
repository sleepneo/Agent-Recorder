using System;
using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class CoreAudioSystemAudioEndpointNativeClientTests
{
    [Fact]
    public void PropVariant_UsesArchitectureAwareWindowsSdkLayout()
    {
        var expectedPropVariantSize = IntPtr.Size == 8 ? 24 : 16;
        var expectedUnionSize = IntPtr.Size == 8 ? 16 : 8;

        Assert.Equal(expectedPropVariantSize, Marshal.SizeOf<PropVariant>());
        Assert.Equal(expectedUnionSize, Marshal.SizeOf<PropVariantUnion>());
        Assert.Equal(expectedUnionSize, Marshal.SizeOf<PropVariantBlob>());
        Assert.Equal(0, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.VariantType)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.PointerValue)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Value)).ToInt32());
    }

    [Fact]
    public void DetermineIsDefaultMultimedia_SameId_ReturnsTrue()
    {
        var result = CoreAudioSystemAudioEndpointNativeClient.DetermineIsDefaultMultimedia(
            "default-id", 0, defaultEndpointPresent: true, defaultIdHr: 0, "default-id");

        Assert.True(result);
    }

    [Fact]
    public void DetermineIsDefaultMultimedia_DifferentId_ReturnsFalse()
    {
        var result = CoreAudioSystemAudioEndpointNativeClient.DetermineIsDefaultMultimedia(
            "selected-id", 0, defaultEndpointPresent: true, defaultIdHr: 0, "default-id");

        Assert.False(result);
    }

    [Fact]
    public void DetermineIsDefaultMultimedia_NoDefaultEndpoint_ReturnsFalse()
    {
        var result = CoreAudioSystemAudioEndpointNativeClient.DetermineIsDefaultMultimedia(
            "selected-id",
            CoreAudioSystemAudioEndpointNativeClient.ErrorNotFound,
            defaultEndpointPresent: false,
            defaultIdHr: -1,
            defaultEndpointId: null);

        Assert.False(result);
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005), true, 0, "default-id")]
    [InlineData(0, false, -1, null)]
    [InlineData(0, true, 1, null)]
    [InlineData(0, true, 0, "")]
    public void DetermineIsDefaultMultimedia_UnknownOrMalformedDefault_FailsClosed(
        int defaultHr,
        bool defaultEndpointPresent,
        int defaultIdHr,
        string? defaultEndpointId)
    {
        var exception = Assert.Throws<SystemAudioEndpointEnumerationException>(() =>
            CoreAudioSystemAudioEndpointNativeClient.DetermineIsDefaultMultimedia(
                "selected-id",
                defaultHr,
                defaultEndpointPresent,
                defaultIdHr,
                defaultEndpointId));

        Assert.Equal("system_audio_default_endpoint_unavailable", exception.ErrorCode);
    }
}
