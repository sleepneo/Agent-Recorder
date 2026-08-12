using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using static AgentRecorder.AudioHelper.NativeHfpInterop;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Minimal seam over an MMDevice so production code uses NAudio while tests
/// inject deterministic fakes. Each <see cref="CreateAudioClient"/> call must
/// return a fresh <see cref="IAudioClient"/>.
/// </summary>
internal interface IDevice : IDisposable
{
    DeviceState State { get; }
    DataFlow DataFlow { get; }
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
/// Optional event-driven AudioClient capability. Kept separate from the
/// capture seam so existing direct-profile fakes remain source compatible.
/// </summary>
internal interface IEventDrivenAudioClient
{
    void SetEventHandle(IntPtr eventHandle);
}

/// <summary>
/// Seam over an AudioCaptureClient COM object obtained via GetService. Each
/// instance wraps one IAudioCaptureClient interface and must be disposed.
/// </summary>
internal interface IAudioCaptureClient : IDisposable
{
    int GetNextPacketSize();
    IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);

    /// <summary>
    /// Position-aware overload. Existing microphone/HFP test seams can keep
    /// implementing the legacy overload; the default implementation marks the
    /// position evidence unavailable for callers that require loopback timing.
    /// </summary>
    IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags,
        out long devicePosition, out long qpcPosition)
    {
        devicePosition = -1;
        qpcPosition = 0;
        return GetBuffer(out framesAvailable, out flags);
    }

    void ReleaseBuffer(int framesRead);
}

/// <summary>
/// Seam over the render-side AudioClient used by the HFP prime path. The
/// production implementation activates through IMMDevice so the exact
/// MMDevice endpoint ID is never passed to ActivateAudioInterfaceAsync.
/// </summary>
internal interface IHfpRenderActivationClient : IDisposable
{
    void SetClientProperties(AudioClientProperties properties);
    WaveFormat MixFormat { get; }
    bool IsFormatSupported(AudioClientShareMode shareMode, WaveFormat format);
    int BufferSize { get; }
    int CurrentPadding { get; }
    void Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        WaveFormat format,
        Guid audioSessionGuid);
    void SetEventHandle(IntPtr eventHandle);
    IHfpRenderBuffer GetRenderBuffer();
    void Start();
    void Stop();
}

internal interface IHfpRenderBuffer : IDisposable
{
    IntPtr GetBuffer(int framesRequested);
    void ReleaseBuffer(int framesWritten, AudioClientBufferFlags flags);
}

internal interface IHfpRenderActivationFactory
{
    IHfpRenderActivationClient Activate(string endpointId);
}

internal interface IHfpRenderDeviceActivator
{
    IHfpRenderActivationClient Activate(string endpointId);
}

internal interface IHfpComApartment : IDisposable
{
    int ThreadId { get; }
}

internal interface IHfpComApartmentFactory
{
    IHfpComApartment Enter();
}

internal interface IHfpNativeRenderApi
{
    IHfpNativeRenderDevice GetDevice(string endpointId);
}

internal interface IHfpNativeRenderDevice : IDisposable
{
    IHfpRenderActivationClient Activate(Guid iid, uint clsContext);
}

internal interface IHfpNativeAudioClient2 : IDisposable
{
    int SetClientProperties(ref AudioClientProperties properties);
    int GetMixFormat(out IntPtr format);
    int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);
    int GetBufferSize(out int numBufferFrames);
    int GetCurrentPadding(out int numPaddingFrames);
    int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags,
        long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    int SetEventHandle(IntPtr eventHandle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object service);
    int Start();
    int Stop();
}

internal interface IHfpWaveFormatMemory
{
    IntPtr MarshalToPtr(WaveFormat format);
    WaveFormat MarshalFromPtr(IntPtr format);
    void Free(IntPtr pointer);
}

internal sealed class HfpRenderActivationException : Exception
{
    public HfpRenderActivationException(string stage, int hresult, string message, Exception innerException)
        : base(message, innerException)
    {
        Stage = stage;
        Hresult = hresult;
    }

    public string Stage { get; }
    public int Hresult { get; }
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

    public DataFlow DataFlow => _device.DataFlow;

    public IAudioClient CreateAudioClient() => new NAudioAudioClient(_device.AudioClient);

    public void Dispose() => _device.Dispose();
}

internal sealed class NAudioAudioClient : IAudioClient, IEventDrivenAudioClient
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

    public void SetEventHandle(IntPtr eventHandle) => _client.SetEventHandle(eventHandle);

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

    public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags,
        out long devicePosition, out long qpcPosition)
        => _client.GetBuffer(out framesAvailable, out flags, out devicePosition, out qpcPosition);

    public void ReleaseBuffer(int framesRead) => _client.ReleaseBuffer(framesRead);

    public void Dispose() => _client.Dispose();
}

internal sealed class NAudioHfpRenderActivationFactory : IHfpRenderActivationFactory
{
    private readonly IHfpRenderDeviceActivator _deviceActivator;

    public NAudioHfpRenderActivationFactory()
        : this(new NativeHfpRenderDeviceActivator())
    {
    }

    internal NAudioHfpRenderActivationFactory(IHfpRenderDeviceActivator deviceActivator)
        => _deviceActivator = deviceActivator ?? throw new ArgumentNullException(nameof(deviceActivator));

    public IHfpRenderActivationClient Activate(string endpointId)
        => _deviceActivator.Activate(endpointId);
}

internal sealed class NAudioHfpRenderActivationClient : IHfpRenderActivationClient
{
    private readonly IHfpNativeAudioClient2 _client;
    private readonly IHfpWaveFormatMemory _memory;
    private int _disposed;

    internal NAudioHfpRenderActivationClient(IAudioClient2Native client)
        : this(new NativeHfpAudioClient2(client), null)
    {
    }

    internal NAudioHfpRenderActivationClient(IHfpNativeAudioClient2 client,
        IHfpWaveFormatMemory? memory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _memory = memory ?? new NativeHfpWaveFormatMemory();
    }

    public void SetClientProperties(AudioClientProperties properties)
        => ThrowIfFailed(_client.SetClientProperties(ref properties), "IAudioClient2.SetClientProperties");

    public WaveFormat MixFormat
    {
        get
        {
            IntPtr format = IntPtr.Zero;
            try
            {
                ThrowIfFailed(_client.GetMixFormat(out format), "IAudioClient.GetMixFormat");
                return _memory.MarshalFromPtr(format);
            }
            finally
            {
                if (format != IntPtr.Zero)
                    _memory.Free(format);
            }
        }
    }

    public bool IsFormatSupported(AudioClientShareMode shareMode, WaveFormat format)
    {
        var formatPointer = _memory.MarshalToPtr(format);
        IntPtr closest = IntPtr.Zero;
        try
        {
            var hresult = _client.IsFormatSupported(shareMode, formatPointer, out closest);
            if (hresult == 0)
                return true;
            if (hresult == 1)
                return false;
            if (hresult == unchecked((int)0x88890008))
                return false;
            ThrowIfFailed(hresult, "IAudioClient.IsFormatSupported");
            return false;
        }
        finally
        {
            _memory.Free(formatPointer);
            if (closest != IntPtr.Zero)
                _memory.Free(closest);
        }
    }

    public int BufferSize
    {
        get
        {
            ThrowIfFailed(_client.GetBufferSize(out var frames), "IAudioClient.GetBufferSize");
            return frames;
        }
    }

    public int CurrentPadding
    {
        get
        {
            ThrowIfFailed(_client.GetCurrentPadding(out var frames), "IAudioClient.GetCurrentPadding");
            return frames;
        }
    }

    public void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration,
        long periodicity, WaveFormat format, Guid audioSessionGuid)
    {
        var formatPointer = _memory.MarshalToPtr(format);
        try
        {
            ThrowIfFailed(_client.Initialize(shareMode, streamFlags, bufferDuration, periodicity,
                formatPointer, IntPtr.Zero), "IAudioClient.Initialize");
        }
        finally
        {
            _memory.Free(formatPointer);
        }
    }

    public void SetEventHandle(IntPtr eventHandle)
        => ThrowIfFailed(_client.SetEventHandle(eventHandle), "IAudioClient.SetEventHandle");

    public IHfpRenderBuffer GetRenderBuffer()
    {
        var iid = NativeAudioGuids.AudioRenderClient;
        object? service = null;
        try
        {
            ThrowIfFailed(_client.GetService(ref iid, out service), "IAudioClient.GetService(IAudioRenderClient)");
            if (service is not IAudioRenderClientNative renderClient)
                throw new InvalidOperationException("IAudioClient.GetService did not return IAudioRenderClient");
            var result = new NativeHfpRenderBuffer(renderClient);
            service = null;
            return result;
        }
        finally
        {
            NativeCom.Release(service);
        }
    }

    public void Start() => ThrowIfFailed(_client.Start(), "IAudioClient.Start");
    public void Stop() => ThrowIfFailed(_client.Stop(), "IAudioClient.Stop");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _client.Dispose();
    }
}

internal sealed class NativeHfpWaveFormatMemory : IHfpWaveFormatMemory
{
    public IntPtr MarshalToPtr(WaveFormat format) => WaveFormat.MarshalToPtr(format);

    public WaveFormat MarshalFromPtr(IntPtr format) => WaveFormat.MarshalFromPtr(format);

    public void Free(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
            Marshal.FreeCoTaskMem(pointer);
    }
}

internal sealed class NativeHfpAudioClient2 : IHfpNativeAudioClient2
{
    private readonly IAudioClient2Native _client;
    private int _disposed;

    public NativeHfpAudioClient2(IAudioClient2Native client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public int SetClientProperties(ref AudioClientProperties properties)
        => _client.SetClientProperties(ref properties);

    public int GetMixFormat(out IntPtr format) => _client.GetMixFormat(out format);

    public int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch)
        => _client.IsFormatSupported(shareMode, format, out closestMatch);

    public int GetBufferSize(out int numBufferFrames) => _client.GetBufferSize(out numBufferFrames);

    public int GetCurrentPadding(out int numPaddingFrames) => _client.GetCurrentPadding(out numPaddingFrames);

    public int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags,
        long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid)
        => _client.Initialize(shareMode, streamFlags, bufferDuration, periodicity, format, audioSessionGuid);

    public int SetEventHandle(IntPtr eventHandle) => _client.SetEventHandle(eventHandle);

    public int GetService(ref Guid iid, out object service) => _client.GetService(ref iid, out service);

    public int Start() => _client.Start();

    public int Stop() => _client.Stop();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            NativeCom.Release(_client);
    }
}

internal sealed class NativeHfpRenderBuffer : IHfpRenderBuffer
{
    private readonly IAudioRenderClientNative _client;
    private int _disposed;

    internal NativeHfpRenderBuffer(IAudioRenderClientNative client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    public IntPtr GetBuffer(int framesRequested)
    {
        ThrowIfFailed(_client.GetBuffer(framesRequested, out var buffer), "IAudioRenderClient.GetBuffer");
        return buffer;
    }

    public void ReleaseBuffer(int framesWritten, AudioClientBufferFlags flags)
        => ThrowIfFailed(_client.ReleaseBuffer(framesWritten, flags), "IAudioRenderClient.ReleaseBuffer");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            NativeCom.Release(_client);
    }
}

internal sealed class NativeHfpRenderDeviceActivator : IHfpRenderDeviceActivator
{
    private const uint ClsctxAll = 23;
    private readonly IHfpNativeRenderApi _nativeApi;

    public NativeHfpRenderDeviceActivator()
        : this(new NativeHfpRenderApi())
    {
    }

    internal NativeHfpRenderDeviceActivator(IHfpNativeRenderApi nativeApi)
        => _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));

    public IHfpRenderActivationClient Activate(string endpointId)
    {
        IHfpNativeRenderDevice? device = null;
        try
        {
            try
            {
                device = _nativeApi.GetDevice(endpointId);
            }
            catch (HfpRenderActivationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HfpRenderActivationException(HfpFailureStages.RenderResolve,
                    HfpDuplexAudioInputFactory.Hresult(ex), "HFP render endpoint resolve failed", ex);
            }

            try
            {
                return device.Activate(NativeAudioGuids.AudioClient2, ClsctxAll);
            }
            catch (HfpRenderActivationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HfpRenderActivationException(HfpFailureStages.RenderActivation,
                    HfpDuplexAudioInputFactory.Hresult(ex), "HFP render IAudioClient2 activation failed", ex);
            }
        }
        finally
        {
            try { device?.Dispose(); } catch { }
        }
    }
}

internal sealed class NativeHfpRenderApi : IHfpNativeRenderApi
{
    private const uint ClsctxAll = 23;
    private static readonly Guid MmDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public IHfpNativeRenderDevice GetDevice(string endpointId)
    {
        IMMDeviceEnumeratorNative? enumerator = null;
        IMMDeviceNative? device = null;
        try
        {
            var hresult = NativeCom.CoCreateInstance(MmDeviceEnumeratorClassId, ClsctxAll, out enumerator);
            ThrowIfFailed(hresult, "CoCreateInstance(MMDeviceEnumerator)");
            ThrowIfFailed(enumerator.GetDevice(endpointId, out device), "IMMDeviceEnumerator.GetDevice");
            var result = new NativeHfpRenderDevice(device);
            device = null;
            return result;
        }
        finally
        {
            NativeCom.Release(device);
            NativeCom.Release(enumerator);
        }
    }
}

internal sealed class NativeHfpRenderDevice : IHfpNativeRenderDevice
{
    private readonly IMMDeviceNative _device;
    private int _disposed;

    public NativeHfpRenderDevice(IMMDeviceNative device)
        => _device = device ?? throw new ArgumentNullException(nameof(device));

    public IHfpRenderActivationClient Activate(Guid iid, uint clsContext)
    {
        object? activated = null;
        try
        {
            ThrowIfFailed(_device.Activate(ref iid, clsContext, IntPtr.Zero, out activated),
                "IMMDevice.Activate(IAudioClient2)");
            if (activated is not IAudioClient2Native client)
                throw new InvalidOperationException("IMMDevice.Activate did not return IAudioClient2");
            var nativeClient = new NativeHfpAudioClient2(client);
            var result = new NAudioHfpRenderActivationClient(nativeClient);
            activated = null;
            return result;
        }
        finally
        {
            NativeCom.Release(activated);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            NativeCom.Release(_device);
    }
}

internal static class NativeAudioGuids
{
    public static readonly Guid MmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid AudioClient2 = new("726778CD-F60A-4eda-82DE-E47610CD78AA");
    public static readonly Guid AudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
}

internal sealed class NativeHfpComApartmentFactory : IHfpComApartmentFactory
{
    public IHfpComApartment Enter() => ComApartmentScope.Enter();
}

internal sealed class ComApartmentScope : IHfpComApartment
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint CoInitMultithreaded = 0;
    private int _initialized;

    private ComApartmentScope(int threadId)
    {
        ThreadId = threadId;
    }

    public int ThreadId { get; }

    public static ComApartmentScope Enter()
    {
        var hresult = NativeCom.CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hresult == RpcEChangedMode)
            throw new COMException("COM apartment is already initialized with an incompatible model", hresult);
        if (hresult != SOk && hresult != SFalse)
            ThrowIfFailed(hresult, "CoInitializeEx");
        return new ComApartmentScope(Environment.CurrentManagedThreadId) { _initialized = 1 };
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ThreadId)
            throw new InvalidOperationException("COM apartment must be released on its owning thread");
        if (Interlocked.Exchange(ref _initialized, 0) != 0)
            NativeCom.CoUninitialize();
    }
}

internal static class NativeCom
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint clsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumeratorNative result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();

    internal static int CoCreateInstance(Guid clsid, uint clsContext, out IMMDeviceEnumeratorNative result)
    {
        var iid = NativeAudioGuids.MmDeviceEnumerator;
        return CoCreateInstance(ref clsid, IntPtr.Zero, clsContext, ref iid, out result);
    }

    internal static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}

internal static class NativeHfpInterop
{
    internal static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
            throw new COMException(operation, hresult);
    }
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorNative
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, [MarshalAs(UnmanagedType.Interface)] out object devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceNative device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string endpointId, out IMMDeviceNative device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceNative
{
    [PreserveSig] int Activate(ref Guid iid, uint clsContext, IntPtr activationParams,
        [MarshalAs(UnmanagedType.Interface)] out object activated);
    [PreserveSig] int OpenPropertyStore(int access, [MarshalAs(UnmanagedType.Interface)] out object properties);
    [PreserveSig] int GetId(out IntPtr endpointId);
    [PreserveSig] int GetState(out int state);
}

[ComImport]
[Guid("726778CD-F60A-4eda-82DE-E47610CD78AA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient2Native
{
    [PreserveSig] int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags,
        long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out int numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out int numPaddingFrames);
    [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object service);
    [PreserveSig] int IsOffloadCapable(AudioStreamCategory category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
    [PreserveSig] int GetBufferSizeLimits(AudioStreamCategory category, int eventDriven,
        out long minBufferDuration, out long maxBufferDuration);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClientNative
{
    [PreserveSig] int GetBuffer(int numFramesRequested, out IntPtr dataPointer);
    [PreserveSig] int ReleaseBuffer(int numFramesWritten, AudioClientBufferFlags flags);
}
