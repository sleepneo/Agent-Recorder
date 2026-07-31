using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace AgentRecorder.AudioHelper;

internal sealed class MediaCaptureNativeAudioRecorder : INativeAudioRecorder
{
    private const int RequestedSampleRate = 16000;
    private const int RequestedChannels = 1;
    private const int RequestedBitsPerSample = 16;

    private readonly Func<IMediaCaptureNativeSource> _sourceFactory;
    private readonly IMediaCaptureDeviceMapper _deviceMapper;
    private readonly object _lock = new();
    private readonly TaskCompletionSource<NativeAudioRecorderException> _failureSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IMediaCaptureNativeSource? _source;
    private bool _started;
    private int _disposed;
    private int _stopping;

    public MediaCaptureNativeAudioRecorder()
        : this(() => new WinRtMediaCaptureNativeSource(), new WinRtMediaCaptureDeviceMapper(ValidateEndpoint))
    {
    }

    internal MediaCaptureNativeAudioRecorder(
        Func<IMediaCaptureNativeSource> sourceFactory,
        Action<string>? endpointValidator = null)
        : this(sourceFactory, new WinRtMediaCaptureDeviceMapper(endpointValidator ?? ValidateEndpoint))
    {
    }

    internal MediaCaptureNativeAudioRecorder(
        Func<IMediaCaptureNativeSource> sourceFactory,
        IMediaCaptureDeviceMapper deviceMapper)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _deviceMapper = deviceMapper ?? throw new ArgumentNullException(nameof(deviceMapper));
    }

    public async Task InitializeAsync(NativeAudioRecorderRequest request, CancellationToken cancellationToken)
    {
        string mediaCaptureDeviceId;
        try
        {
            mediaCaptureDeviceId = await _deviceMapper
                .MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(request.EndpointId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NativeAudioRecorderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_native_device_enumeration_failed",
                "Failed to enumerate Windows audio capture devices.",
                ex.HResult,
                ex,
                "DeviceInformation.FindAllAsync");
        }

        var source = _sourceFactory();
        AttachSource(source);
        try
        {
            await source.InitializeAsync(mediaCaptureDeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeAudioRecorderException)
        {
            DetachAndReleaseFailedInitializeSource(source);
            throw;
        }
        catch (Exception ex)
        {
            DetachAndReleaseFailedInitializeSource(source);
            throw new NativeAudioRecorderException(
                "audio_native_initialize_failed",
                "MediaCapture.InitializeAsync failed for the requested endpoint.",
                ex.HResult,
                ex);
        }
    }

    public async Task<NativeAudioRecorderFormat> StartAsync(string partialPath, CancellationToken cancellationToken)
    {
        var source = GetSource();
        if (source == null)
            throw new InvalidOperationException("MediaCapture has not been initialized.");

        try
        {
            await source.StartRecordToStorageFileAsync(partialPath, cancellationToken).ConfigureAwait(false);
            lock (_lock) _started = true;
            return new NativeAudioRecorderFormat(RequestedSampleRate, RequestedChannels, RequestedBitsPerSample);
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_native_start_failed",
                "MediaCapture.StartRecordToStorageFileAsync failed for the requested endpoint.",
                ex.HResult,
                ex);
        }
    }

    public async Task WaitForRecordingFailureAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<NativeAudioRecorderException>)state!).TrySetCanceled();
        }, _failureSignal);

        var failure = await _failureSignal.Task.ConfigureAwait(false);
        throw failure;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var source = GetSource();
        if (source == null)
            return;

        lock (_lock)
        {
            if (!_started)
                return;
            _stopping = 1;
        }

        try
        {
            await source.StopRecordAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock) _started = false;
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_native_stop_failed",
                "MediaCapture.StopRecordAsync failed.",
                ex.HResult,
                ex);
        }
    }

    public Task<NativeAudioRecorderFinalized> FinalizeAsync(string partialPath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = new WaveFileReader(partialPath);
            var bytesWritten = new FileInfo(partialPath).Length;
            var durationMs = (long)Math.Round(reader.TotalTime.TotalMilliseconds);
            return Task.FromResult(new NativeAudioRecorderFinalized(
                reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels,
                reader.WaveFormat.BitsPerSample,
                bytesWritten,
                durationMs));
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_native_finalize_failed",
                "MediaCapture output WAV validation failed.",
                ex.HResult,
                ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var source = GetAndClearSource();
        if (source == null)
            return;

            DetachSource(source);
            try { source.Dispose(); } catch { throw; }
    }

    private void DetachAndReleaseFailedInitializeSource(IMediaCaptureNativeSource source)
    {
        DetachSource(source);
        lock (_lock)
        {
            if (ReferenceEquals(_source, source))
                _source = null;
        }

        try { source.Dispose(); } catch { }
    }

    private void AttachSource(IMediaCaptureNativeSource source)
    {
        lock (_lock)
        {
            _source = source;
        }

        source.Failed += OnMediaCaptureFailed;
        source.RecordLimitationExceeded += OnRecordLimitationExceeded;
    }

    private void DetachSource(IMediaCaptureNativeSource source)
    {
        try { source.Failed -= OnMediaCaptureFailed; } catch { }
        try { source.RecordLimitationExceeded -= OnRecordLimitationExceeded; } catch { }
    }

    private IMediaCaptureNativeSource? GetSource()
    {
        lock (_lock) return _source;
    }

    private IMediaCaptureNativeSource? GetAndClearSource()
    {
        lock (_lock)
        {
            var source = _source;
            _source = null;
            return source;
        }
    }

    private void OnMediaCaptureFailed(object? sender, NativeMediaCaptureFailureEventArgs args)
    {
        if (ShouldIgnoreNativeEvent())
            return;

        _failureSignal.TrySetResult(new NativeAudioRecorderException(
            "audio_native_recording_failed",
            args.Message,
            args.Hresult,
            sourceEvent: args.SourceEvent));
    }

    private void OnRecordLimitationExceeded(object? sender, EventArgs args)
    {
        if (ShouldIgnoreNativeEvent())
            return;

        _failureSignal.TrySetResult(new NativeAudioRecorderException(
            "audio_native_recording_failed",
            "MediaCapture.RecordLimitationExceeded was raised.",
            sourceEvent: "RecordLimitationExceeded"));
    }

    private bool ShouldIgnoreNativeEvent()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0 ||
            Interlocked.CompareExchange(ref _stopping, 0, 0) != 0)
            return true;

        lock (_lock) return !_started;
    }

    private static void ValidateEndpoint(string endpointId)
    {
        try
        {
            using var enumerator = new NAudioDeviceEnumerator();
            using var device = enumerator.GetDevice(endpointId);
            if (device.State == DeviceState.NotPresent)
                throw new NativeAudioRecorderException("audio_endpoint_not_found", "Endpoint not present.");
            if (device.State == DeviceState.Unplugged)
                throw new NativeAudioRecorderException("audio_endpoint_inactive", "Endpoint unplugged.");
            if (device.State == DeviceState.Disabled)
                throw new NativeAudioRecorderException("audio_endpoint_inactive", "Endpoint disabled.");
            if (device.State != DeviceState.Active)
                throw new NativeAudioRecorderException("audio_endpoint_inactive", $"Endpoint state is {device.State}.");
        }
        catch (NativeAudioRecorderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_endpoint_not_found",
                "Failed to resolve the requested CoreAudio endpoint.",
                ex.HResult,
                ex);
        }
    }
}

internal sealed record MediaCaptureDeviceInfo(string Id, bool IsEnabled, string Name);

internal interface IMediaCaptureDeviceEnumerator
{
    Task<IReadOnlyList<MediaCaptureDeviceInfo>> FindAudioCaptureDevicesAsync(CancellationToken cancellationToken);
}

internal interface IMediaCaptureDeviceMapper
{
    Task<string> MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(string coreAudioEndpointId, CancellationToken cancellationToken);
}

internal sealed class WinRtMediaCaptureDeviceMapper : IMediaCaptureDeviceMapper
{
    private readonly Action<string> _endpointValidator;
    private readonly IMediaCaptureDeviceEnumerator _deviceEnumerator;

    public WinRtMediaCaptureDeviceMapper(Action<string> endpointValidator)
        : this(endpointValidator, new WinRtMediaCaptureDeviceEnumerator())
    {
    }

    internal WinRtMediaCaptureDeviceMapper(
        Action<string> endpointValidator,
        IMediaCaptureDeviceEnumerator deviceEnumerator)
    {
        _endpointValidator = endpointValidator ?? throw new ArgumentNullException(nameof(endpointValidator));
        _deviceEnumerator = deviceEnumerator ?? throw new ArgumentNullException(nameof(deviceEnumerator));
    }

    public async Task<string> MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(
        string coreAudioEndpointId,
        CancellationToken cancellationToken)
    {
        _endpointValidator(coreAudioEndpointId);

        IReadOnlyList<MediaCaptureDeviceInfo> devices;
        try
        {
            devices = await _deviceEnumerator.FindAudioCaptureDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NativeAudioRecorderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NativeAudioRecorderException(
                "audio_native_device_enumeration_failed",
                "DeviceInformation.FindAllAsync failed while mapping the approved CoreAudio endpoint.",
                ex.HResult,
                ex,
                "DeviceInformation.FindAllAsync");
        }

        var matches = devices
            .Where(device => DeviceInformationIdContainsEndpoint(device.Id, coreAudioEndpointId))
            .ToList();

        if (matches.Count == 0)
        {
            throw new NativeAudioRecorderException(
                "audio_native_device_mapping_not_found",
                "No Windows MediaCapture audio device id matched the approved CoreAudio endpoint.",
                sourceEvent: "DeviceInformation.FindAllAsync");
        }

        if (matches.Count > 1)
        {
            throw new NativeAudioRecorderException(
                "audio_native_device_mapping_ambiguous",
                $"Multiple Windows MediaCapture audio device ids matched the approved CoreAudio endpoint: {matches.Count}.",
                sourceEvent: "DeviceInformation.FindAllAsync");
        }

        var match = matches[0];
        if (!match.IsEnabled)
        {
            throw new NativeAudioRecorderException(
                "audio_native_device_mapping_disabled",
                "The Windows MediaCapture audio device id matching the approved CoreAudio endpoint is disabled.",
                sourceEvent: "DeviceInformation.FindAllAsync");
        }

        return match.Id;
    }

    internal static bool DeviceInformationIdContainsEndpoint(string deviceInformationId, string coreAudioEndpointId)
    {
        if (string.IsNullOrWhiteSpace(deviceInformationId) || string.IsNullOrWhiteSpace(coreAudioEndpointId))
            return false;

        var parts = deviceInformationId.Split('#', StringSplitOptions.None);
        for (int i = 1; i < parts.Length; i++)
        {
            if (string.Equals(parts[i - 1], "MMDEVAPI", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i], coreAudioEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class WinRtMediaCaptureDeviceEnumerator : IMediaCaptureDeviceEnumerator
{
    public async Task<IReadOnlyList<MediaCaptureDeviceInfo>> FindAudioCaptureDevicesAsync(CancellationToken cancellationToken)
    {
        var selector = MediaDevice.GetAudioCaptureSelector();
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(device => new MediaCaptureDeviceInfo(device.Id, device.IsEnabled, device.Name ?? ""))
            .ToList();
    }
}

internal sealed class NativeMediaCaptureFailureEventArgs : EventArgs
{
    public NativeMediaCaptureFailureEventArgs(string sourceEvent, string message, int? hresult)
    {
        SourceEvent = sourceEvent;
        Message = message;
        Hresult = hresult;
    }

    public string SourceEvent { get; }
    public string Message { get; }
    public int? Hresult { get; }
}

internal interface IMediaCaptureNativeSource : IDisposable
{
    event EventHandler<NativeMediaCaptureFailureEventArgs>? Failed;
    event EventHandler? RecordLimitationExceeded;

    Task InitializeAsync(string endpointId, CancellationToken cancellationToken);
    Task StartRecordToStorageFileAsync(string partialPath, CancellationToken cancellationToken);
    Task StopRecordAsync(CancellationToken cancellationToken);
}

internal sealed class WinRtMediaCaptureNativeSource : IMediaCaptureNativeSource
{
    private readonly object _lock = new();
    private MediaCapture? _mediaCapture;
    private MediaCaptureFailedEventHandler? _failedHandler;
    private RecordLimitationExceededEventHandler? _recordLimitationExceededHandler;
    private int _disposed;

    public event EventHandler<NativeMediaCaptureFailureEventArgs>? Failed;
    public event EventHandler? RecordLimitationExceeded;

    public async Task InitializeAsync(string endpointId, CancellationToken cancellationToken)
    {
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            AudioDeviceId = endpointId,
            MediaCategory = MediaCategory.Speech,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        };

        var mediaCapture = new MediaCapture();
        lock (_lock)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            {
                mediaCapture.Dispose();
                throw new ObjectDisposedException(nameof(WinRtMediaCaptureNativeSource));
            }

            _mediaCapture = mediaCapture;
        }

        _failedHandler = (_, args) =>
        {
            Failed?.Invoke(this, new NativeMediaCaptureFailureEventArgs(
                "MediaCapture.Failed",
                args.Message ?? "",
                unchecked((int)args.Code)));
        };
        _recordLimitationExceededHandler = _ =>
        {
            RecordLimitationExceeded?.Invoke(this, EventArgs.Empty);
        };

        mediaCapture.Failed += _failedHandler;
        mediaCapture.RecordLimitationExceeded += _recordLimitationExceededHandler;
        try
        {
            await mediaCapture.InitializeAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task StartRecordToStorageFileAsync(string partialPath, CancellationToken cancellationToken)
    {
        if (_mediaCapture == null)
            throw new InvalidOperationException("MediaCapture has not been initialized.");

        var storageFile = await StorageFile.GetFileFromPathAsync(partialPath).AsTask(cancellationToken).ConfigureAwait(false);
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
        await _mediaCapture.StartRecordToStorageFileAsync(profile, storageFile).AsTask(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopRecordAsync(CancellationToken cancellationToken)
    {
        if (_mediaCapture == null)
            return;

        await _mediaCapture.StopRecordAsync().AsTask(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        MediaCapture? mediaCapture;
        lock (_lock)
        {
            mediaCapture = _mediaCapture;
            _mediaCapture = null;
        }

        if (mediaCapture == null)
            return;

        if (_failedHandler != null)
            mediaCapture.Failed -= _failedHandler;
        if (_recordLimitationExceededHandler != null)
            mediaCapture.RecordLimitationExceeded -= _recordLimitationExceededHandler;
        mediaCapture.Dispose();
    }
}
