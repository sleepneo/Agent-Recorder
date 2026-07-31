using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Minimal seam over an MMDevice so production code uses NAudio while tests
/// inject deterministic fakes. Each <see cref="CreateAudioClient"/> call must
/// return a fresh <see cref="IAudioClient"/>.
/// </summary>
internal interface IDevice : IDisposable
{
    DeviceState State { get; }
    IAudioClient CreateAudioClient();
}

/// <summary>
/// Factory for creating device enumerators. Disposed once per open attempt.
/// </summary>
internal interface IDeviceEnumerator : IDisposable
{
    IDevice GetDevice(string endpointId);
}

/// <summary>
/// Seam over an AudioClient COM object. Disposing the client releases the
/// underlying COM interface.
/// </summary>
internal interface IAudioClient : IDisposable
{
    WaveFormat MixFormat { get; }
    int BufferSize { get; }

    void Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        WaveFormat format,
        Guid audioSessionGuid);

    void Start();
    void Stop();

    /// <summary>
    /// Obtains the IAudioCaptureClient service from this AudioClient. The
    /// caller owns the returned instance and must dispose it.
    /// </summary>
    IAudioCaptureClient GetAudioCaptureClient();
}

/// <summary>
/// Seam over an AudioCaptureClient COM object obtained via GetService. Each
/// instance wraps one IAudioCaptureClient interface and must be disposed.
/// </summary>
internal interface IAudioCaptureClient : IDisposable
{
    int GetNextPacketSize();
    IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);
    void ReleaseBuffer(int framesRead);
}

// ---------------------------------------------------------------------------
// NAudio adapters
// ---------------------------------------------------------------------------

internal sealed class NAudioDeviceEnumerator : IDeviceEnumerator
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IDevice GetDevice(string endpointId) => new NAudioDevice(_enumerator.GetDevice(endpointId));

    public void Dispose() => _enumerator.Dispose();
}

internal sealed class NAudioDevice : IDevice
{
    private readonly MMDevice _device;

    public NAudioDevice(MMDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public DeviceState State => _device.State;

    public IAudioClient CreateAudioClient() => new NAudioAudioClient(_device.AudioClient);

    public void Dispose() => _device.Dispose();
}

internal sealed class NAudioAudioClient : IAudioClient
{
    private readonly AudioClient _client;

    public NAudioAudioClient(AudioClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public WaveFormat MixFormat => _client.MixFormat;

    public int BufferSize => _client.BufferSize;

    public void Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        WaveFormat format,
        Guid audioSessionGuid)
    {
        _client.Initialize(shareMode, streamFlags, bufferDuration, periodicity, format, audioSessionGuid);
    }

    public void Start() => _client.Start();

    public void Stop() => _client.Stop();

    public IAudioCaptureClient GetAudioCaptureClient()
        => new NAudioAudioCaptureClient(_client.AudioCaptureClient);

    public void Dispose() => _client.Dispose();
}

internal sealed class NAudioAudioCaptureClient : IAudioCaptureClient
{
    private readonly AudioCaptureClient _client;

    public NAudioAudioCaptureClient(AudioCaptureClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public int GetNextPacketSize() => _client.GetNextPacketSize();

    public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags)
        => _client.GetBuffer(out framesAvailable, out flags);

    public void ReleaseBuffer(int framesRead) => _client.ReleaseBuffer(framesRead);

    public void Dispose() => _client.Dispose();
}
