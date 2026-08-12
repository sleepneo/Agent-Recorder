using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class WasapiLoopbackAudioInputTests
{
    [Fact]
    public void TryOpenOnce_UsesExactActiveRenderMixFormatAndLoopbackOnly()
    {
        var endpointId = "render-approved";
        var format = new WaveFormat(48000, 32, 2);
        var probe = new FakeAudioClient(format);
        var capture = new FakeAudioCaptureClient();
        var initialized = new FakeAudioClient(format) { CaptureClient = capture };
        var device = new FakeDevice(DataFlow.Render, DeviceState.Active, probe, initialized);
        var enumerator = new FakeEnumerator(endpointId, device);

        var result = WasapiLoopbackAudioInput.TryOpenOnce(endpointId, enumerator);

        Assert.NotNull(result.Input);
        Assert.Equal(endpointId, enumerator.LastEndpointId);
        Assert.Equal(AudioSourceKind.SystemLoopback, result.Input!.SourceKind);
        Assert.Equal(AudioClientShareMode.Shared, initialized.ShareMode);
        Assert.Equal(AudioClientStreamFlags.Loopback, initialized.StreamFlags);
        Assert.Equal(format.SampleRate, initialized.Format!.SampleRate);
        Assert.Equal(format.Channels, initialized.Format.Channels);
        Assert.Equal(format.BitsPerSample, initialized.Format.BitsPerSample);
        Assert.Equal(1, probe.DisposeCount);

        result.Input.Dispose();

        Assert.Equal(1, probe.DisposeCount);
        Assert.Equal(1, initialized.DisposeCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public void TryOpenOnce_WrongFlowReturnsStableErrorAndDoesNotOpenAudioClient()
    {
        var device = new FakeDevice(
            DataFlow.Capture,
            DeviceState.Active,
            new FakeAudioClient(new WaveFormat(48000, 32, 2)));
        var enumerator = new FakeEnumerator("approved", device);

        var result = WasapiLoopbackAudioInput.TryOpenOnce("approved", enumerator);

        Assert.Null(result.Input);
        Assert.Equal("audio_loopback_endpoint_wrong_flow", result.ErrorCode);
        Assert.Equal("LoopbackEndpointDataFlow", result.FailureStage);
        Assert.Contains("expected Render", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, device.CreateAudioClientCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Theory]
    [InlineData(DeviceState.NotPresent, "audio_endpoint_not_found")]
    [InlineData(DeviceState.Disabled, "audio_endpoint_inactive")]
    [InlineData(DeviceState.Unplugged, "audio_endpoint_inactive")]
    public void TryOpenOnce_InactiveOrMissingEndpointReturnsStableCode(DeviceState state, string expectedCode)
    {
        var device = new FakeDevice(DataFlow.Render, state,
            new FakeAudioClient(new WaveFormat(48000, 32, 2)));
        var result = WasapiLoopbackAudioInput.TryOpenOnce("approved", new FakeEnumerator("approved", device));

        Assert.Null(result.Input);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(0, device.CreateAudioClientCount);
    }

    [Fact]
    public void TryOpenOnce_InitializeFailurePreservesStageAndHresultAndReleasesAllObjects()
    {
        var probe = new FakeAudioClient(new WaveFormat(48000, 32, 2));
        var initialized = new FakeAudioClient(probe.MixFormat)
        {
            InitializeException = new COMException("loopback initialize failed", unchecked((int)0x80070057))
        };
        var device = new FakeDevice(DataFlow.Render, DeviceState.Active, probe, initialized);

        var result = WasapiLoopbackAudioInput.TryOpenOnce("approved", new FakeEnumerator("approved", device));

        Assert.Null(result.Input);
        Assert.Equal("audio_format_negotiation_failure", result.ErrorCode);
        Assert.Equal("LoopbackInitialize", result.FailureStage);
        Assert.Contains("HRESULT=0x80070057", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, probe.DisposeCount);
        Assert.Equal(1, initialized.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
    }

    private sealed class FakeEnumerator : IDeviceEnumerator
    {
        private readonly string _endpointId;
        private readonly IDevice _device;

        public FakeEnumerator(string endpointId, IDevice device)
        {
            _endpointId = endpointId;
            _device = device;
        }

        public string? LastEndpointId { get; private set; }

        public IDevice GetDevice(string endpointId)
        {
            LastEndpointId = endpointId;
            if (endpointId != _endpointId)
                throw new COMException("endpoint not found", unchecked((int)0x80070490));
            return _device;
        }

        public void Dispose() { }
    }

    private sealed class FakeDevice : IDevice
    {
        private readonly Queue<IAudioClient> _clients;

        public FakeDevice(DataFlow dataFlow, DeviceState state, params IAudioClient[] clients)
        {
            DataFlow = dataFlow;
            State = state;
            _clients = new Queue<IAudioClient>(clients);
        }

        public DeviceState State { get; }
        public DataFlow DataFlow { get; }
        public int DisposeCount { get; private set; }
        public int CreateAudioClientCount { get; private set; }

        public IAudioClient CreateAudioClient()
        {
            CreateAudioClientCount++;
            return _clients.Dequeue();
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeAudioClient : IAudioClient
    {
        public FakeAudioClient(WaveFormat format) => MixFormat = format;

        public WaveFormat MixFormat { get; }
        public int BufferSize => 4800;
        public AudioClientShareMode ShareMode { get; private set; }
        public AudioClientStreamFlags StreamFlags { get; private set; }
        public WaveFormat? Format { get; private set; }
        public Exception? InitializeException { get; set; }
        public IAudioCaptureClient? CaptureClient { get; set; }
        public int DisposeCount { get; private set; }

        public void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags,
            long bufferDuration, long periodicity, WaveFormat format, Guid audioSessionGuid)
        {
            if (InitializeException != null)
                throw InitializeException;
            ShareMode = shareMode;
            StreamFlags = streamFlags;
            Format = format;
        }

        public void Start() { }
        public void Stop() { }
        public IAudioCaptureClient GetAudioCaptureClient()
            => CaptureClient ?? new FakeAudioCaptureClient();
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeAudioCaptureClient : IAudioCaptureClient
    {
        public int DisposeCount { get; private set; }
        public int GetNextPacketSize() => 0;
        public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags)
        {
            framesAvailable = 0;
            flags = AudioClientBufferFlags.None;
            return IntPtr.Zero;
        }
        public void ReleaseBuffer(int framesRead) { }
        public void Dispose() => DisposeCount++;
    }
}
