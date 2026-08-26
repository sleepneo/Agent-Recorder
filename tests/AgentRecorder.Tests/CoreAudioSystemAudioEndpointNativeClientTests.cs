using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class CoreAudioSystemAudioEndpointNativeClientTests
{
    [Fact]
    public void NativeCollectionAbi_UsesOfficialImmDeviceCollectionGuid()
    {
        var guid = typeof(IMMDeviceCollectionSystemAudio).GUID;
        Assert.Equal(new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), guid);

        var enumMethod = typeof(IMMDeviceEnumeratorSystemAudio).GetMethod("EnumAudioEndpoints");
        Assert.NotNull(enumMethod);
        var collectionParameter = enumMethod!.GetParameters()[2];
        Assert.Equal(typeof(IMMDeviceCollectionSystemAudio).MakeByRefType(), collectionParameter.ParameterType);
    }

    [Fact]
    public async Task Provider_RenderEnumeration_PrioritizesDefaultThenStableNameAndId()
    {
        var native = new FakeNativeClient(new[]
        {
            new SystemAudioEndpointInfo("z", "Same", "render", "active", false),
            new SystemAudioEndpointInfo("b", "Beta", "render", "active", false),
            new SystemAudioEndpointInfo("a", "Same", "render", "active", true)
        });
        var provider = new CoreAudioSystemAudioEndpointProvider(native);

        var result = await provider.GetRenderEndpointsAsync();

        Assert.Equal(new[] { "a", "b", "z" }, result.Select(e => e.Id));
        Assert.True(result[0].IsDefaultMultimedia);
        Assert.All(result, e => Assert.Equal("render", e.Direction));
    }

    [Fact]
    public async Task Provider_CancellationIsObservedBeforeNativeEnumeration()
    {
        var native = new FakeNativeClient(Array.Empty<SystemAudioEndpointInfo>());
        var provider = new CoreAudioSystemAudioEndpointProvider(native);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetRenderEndpointsAsync(cts.Token));
        Assert.Equal(0, native.RenderEnumerationCount);
    }

    [Fact]
    public async Task Provider_NativeFailureIsStructuredAndDoesNotExposeRawException()
    {
        var native = new FakeNativeClient(Array.Empty<SystemAudioEndpointInfo>(),
            new InvalidOperationException("secret COM path"));
        var provider = new CoreAudioSystemAudioEndpointProvider(native);

        var exception = await Assert.ThrowsAsync<SystemAudioEndpointEnumerationException>(() =>
            provider.GetRenderEndpointsAsync());

        Assert.Equal("system_audio_endpoint_enumeration_unavailable", exception.ErrorCode);
        Assert.DoesNotContain("secret COM path", exception.Message);
    }

    [Fact]
    public async Task Provider_RenderEnumerationCanBeBoundedByCallerTimeout()
    {
        var native = new FakeNativeClient(Array.Empty<SystemAudioEndpointInfo>(), delay: 250);
        var provider = new CoreAudioSystemAudioEndpointProvider(native);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            provider.GetRenderEndpointsAsync().WaitAsync(TimeSpan.FromMilliseconds(20)));
    }

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

    private sealed class FakeNativeClient : ISystemAudioEndpointNativeClient
    {
        private readonly IReadOnlyList<SystemAudioEndpointInfo> _endpoints;
        private readonly Exception? _exception;
        private readonly int _delay;

        public FakeNativeClient(IReadOnlyList<SystemAudioEndpointInfo> endpoints,
            Exception? exception = null, int delay = 0)
        {
            _endpoints = endpoints;
            _exception = exception;
            _delay = delay;
        }

        public int RenderEnumerationCount { get; private set; }

        public IReadOnlyList<SystemAudioEndpointInfo> GetRenderEndpoints(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderEnumerationCount++;
            if (_delay > 0)
                Thread.Sleep(_delay);
            if (_exception != null)
                throw _exception;
            return _endpoints;
        }

        public SystemAudioEndpointInfo? GetDefaultMultimediaRenderEndpoint(CancellationToken cancellationToken = default)
            => _endpoints.FirstOrDefault(e => e.IsDefaultMultimedia);

        public SystemAudioEndpointInfo? GetEndpoint(string endpointId, CancellationToken cancellationToken = default)
            => _endpoints.FirstOrDefault(e => e.Id == endpointId);
    }
}
