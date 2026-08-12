using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

internal static class HfpFailureStages
{
    public const string PairDiscovery = "HfpPairDiscovery";
    public const string PairValidation = "HfpPairValidation";
    public const string RenderResolve = "HfpRenderResolve";
    public const string RenderActivation = "HfpRenderActivation";
    public const string RenderSetClientProperties = "HfpRenderSetClientProperties";
    public const string RenderPrime = "HfpRenderPrime";
    public const string RenderStop = "HfpRenderStop";
    public const string CaptureOpen = "HfpCaptureOpen";
    public const string CaptureStart = "HfpCaptureStart";
    public const string RenderRuntimePump = "HfpRenderRuntimePump";
    public const string CaptureStop = "HfpCaptureStop";
}

internal sealed class AudioInputOpenResult
{
    private AudioInputOpenResult(IAudioInput? input, string? errorCode, string? reason, int? hresult,
        string failureStage, string pairEvidence, string captureStrategy)
    {
        Input = input;
        ErrorCode = errorCode;
        Reason = reason ?? "";
        Hresult = hresult;
        FailureStage = failureStage;
        PairEvidence = pairEvidence;
        CaptureStrategy = captureStrategy;
    }

    public IAudioInput? Input { get; }
    public string? ErrorCode { get; }
    public string Reason { get; }
    public int? Hresult { get; }
    public string FailureStage { get; }
    public string PairEvidence { get; }
    public string CaptureStrategy { get; }

    public static AudioInputOpenResult Success(
        IAudioInput input,
        string pairEvidence = "unverified",
        string captureStrategy = "hfp-duplex-prime-classic")
        => new(input, null, "", null, "", pairEvidence, captureStrategy);

    public static AudioInputOpenResult Failure(string errorCode, string reason, string failureStage,
        int? hresult = null, string pairEvidence = "unverified", string captureStrategy = "hfp-duplex-prime-classic")
        => new(null, errorCode, reason, hresult, failureStage, pairEvidence, captureStrategy);

    public static AudioInputOpenResult FromTuple(
        (IAudioInput? Input, string? ErrorCode, string? Reason) result,
        string failureStage = HfpFailureStages.CaptureOpen)
        => result.Input != null
            ? Success(result.Input)
            : Failure(result.ErrorCode ?? "audio_endpoint_not_found", result.Reason ?? "unknown", failureStage);

    public AudioInputOpenResult WithPairEvidence(string pairEvidence)
        => Input != null
            ? Success(Input, pairEvidence)
            : Failure(ErrorCode ?? "audio_helper_runtime_failure", Reason, FailureStage, Hresult,
                pairEvidence, CaptureStrategy);
}

internal sealed class HfpRenderPrimeResult
{
    private HfpRenderPrimeResult(IHfpRenderSession? session, string? errorCode, string? reason,
        int? hresult, string failureStage)
    {
        Session = session;
        ErrorCode = errorCode;
        Reason = reason ?? "";
        Hresult = hresult;
        FailureStage = failureStage;
    }

    public IHfpRenderSession? Session { get; }
    public string? ErrorCode { get; }
    public string Reason { get; }
    public int? Hresult { get; }
    public string FailureStage { get; }

    public static HfpRenderPrimeResult Success(IHfpRenderSession session)
        => new(session, null, "", null, "");

    public static HfpRenderPrimeResult Failure(string errorCode, string reason, string failureStage,
        int? hresult = null)
        => new(null, errorCode, reason, hresult, failureStage);
}

internal interface IHfpDuplexInputFactory
{
    AudioInputOpenResult Open(string captureEndpointId, string renderEndpointId, TimeSpan budget);
}

internal interface IHfpAudioInputMetadata
{
    string CaptureStrategy { get; }
    string PairEvidence { get; }
    long RenderPrimeReadyMs { get; }
}

internal interface IHfpRenderSession : IDisposable
{
    long ReadyMs { get; }
    HfpRenderFailure? RuntimeFailure { get; }
}

internal interface IHfpRenderPrimeFactory
{
    HfpRenderPrimeResult Prime(string renderEndpointId, TimeSpan budget);
}

internal interface IHfpEndpoint : IDisposable
{
    string EndpointId { get; }
    DataFlow DataFlow { get; }
    DeviceState State { get; }
    WaveFormat MixFormat { get; }
    bool TryGetContainerId(out Guid containerId, out string failure);
    bool TryGetTransportClassification(out HfpTransportClassification classification, out string failure);
}

internal interface IHfpEndpointEnumerator : IDisposable
{
    IHfpEndpoint GetDevice(string endpointId);
    IReadOnlyList<IHfpEndpoint> EnumerateRenderEndpoints();
}

internal sealed class HfpEndpointPairValidator
{
    public (bool Ok, string Reason, string PairEvidence) Validate(
        string captureId, string renderId, IHfpEndpointEnumerator enumerator)
    {
        IHfpEndpoint? capture = null;
        IHfpEndpoint? render = null;
        try
        {
            try { capture = enumerator.GetDevice(captureId); }
            catch (Exception ex)
            {
                return (false, HfpDuplexAudioInputFactory.FormatFailure("HFP capture endpoint resolve", ex), "unverified");
            }

            try { render = enumerator.GetDevice(renderId); }
            catch (Exception ex)
            {
                return (false, HfpDuplexAudioInputFactory.FormatFailure("HFP render endpoint resolve", ex), "unverified");
            }

            if (capture.DataFlow != DataFlow.Capture)
                return (false, "HFP capture endpoint has the wrong data flow", "unverified");
            if (render.DataFlow != DataFlow.Render)
                return (false, "HFP render endpoint has the wrong data flow", "unverified");
            if (capture.State != DeviceState.Active || render.State != DeviceState.Active)
                return (false, "HFP capture/render endpoint is inactive", "unverified");
            if (!capture.TryGetContainerId(out var captureContainer, out var captureFailure))
                return (false, captureFailure, "unverified");
            if (!render.TryGetContainerId(out var renderContainer, out var renderFailure))
                return (false, renderFailure, "unverified");
            if (captureContainer != renderContainer)
                return (false, "HFP capture/render endpoints do not share a ContainerId", "unverified");
            return (true, "", "same_container_id");
        }
        finally
        {
            try { capture?.Dispose(); } catch { }
            try { render?.Dispose(); } catch { }
        }
    }
}

internal sealed class HfpDuplexAudioInputFactory : IHfpDuplexInputFactory
{
    private readonly Func<IHfpEndpointEnumerator> _endpointEnumeratorFactory;
    private readonly IHfpRenderPrimeFactory _renderPrimeFactory;
    private readonly Func<string, TimeSpan, AudioInputOpenResult> _captureFactory;

    public HfpDuplexAudioInputFactory()
        : this(
            () => new NAudioHfpEndpointEnumerator(),
            new NAudioHfpRenderPrimeFactory(),
            (endpointId, budget) => AudioInputOpenResult.FromTuple(WasapiAudioInput.OpenClassic(endpointId, budget)))
    {
    }

    internal HfpDuplexAudioInputFactory(
        Func<IHfpEndpointEnumerator> endpointEnumeratorFactory,
        IHfpRenderPrimeFactory renderPrimeFactory,
        Func<string, TimeSpan, AudioInputOpenResult> captureFactory)
    {
        _endpointEnumeratorFactory = endpointEnumeratorFactory;
        _renderPrimeFactory = renderPrimeFactory;
        _captureFactory = captureFactory;
    }

    public AudioInputOpenResult Open(string captureEndpointId, string renderEndpointId, TimeSpan budget)
    {
        if (string.IsNullOrWhiteSpace(captureEndpointId) || string.IsNullOrWhiteSpace(renderEndpointId) ||
            string.Equals(captureEndpointId, renderEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            return AudioInputOpenResult.Failure("audio_hfp_pair_invalid",
                "HFP capture/render endpoint pair is invalid", HfpFailureStages.PairValidation);
        }

        var stopwatch = Stopwatch.StartNew();
        IHfpEndpointEnumerator? endpointEnumerator = null;
        try
        {
            endpointEnumerator = _endpointEnumeratorFactory();
            var pair = new HfpEndpointPairValidator().Validate(captureEndpointId, renderEndpointId, endpointEnumerator);
            if (!pair.Ok)
            {
                return AudioInputOpenResult.Failure("audio_hfp_pair_invalid", pair.Reason,
                    HfpFailureStages.PairValidation, pairEvidence: pair.PairEvidence);
            }

            var remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return AudioInputOpenResult.Failure("audio_hfp_render_prime_failed",
                    "HFP render prime budget exhausted", HfpFailureStages.RenderPrime,
                    pairEvidence: pair.PairEvidence);
            }

            HfpRenderPrimeResult prime;
            try
            {
                prime = _renderPrimeFactory.Prime(renderEndpointId, remaining);
            }
            catch (Exception ex)
            {
                return AudioInputOpenResult.Failure("audio_hfp_render_prime_failed",
                    FormatFailure("HFP render prime", ex), HfpFailureStages.RenderPrime,
                    Hresult(ex), pair.PairEvidence);
            }

            if (prime.Session == null)
            {
                return AudioInputOpenResult.Failure(prime.ErrorCode ?? "audio_hfp_render_prime_failed",
                    prime.Reason, prime.FailureStage.Length == 0 ? HfpFailureStages.RenderPrime : prime.FailureStage,
                    prime.Hresult, pair.PairEvidence);
            }

            var render = prime.Session;
            try
            {
                var renderFailure = render.RuntimeFailure;
                if (renderFailure != null)
                    return RuntimeFailure(renderFailure, pair.PairEvidence, render);

                remaining = budget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    render.Dispose();
                    return AudioInputOpenResult.Failure("audio_capture_start_failed",
                        "HFP capture open budget exhausted", HfpFailureStages.CaptureOpen,
                        pairEvidence: pair.PairEvidence);
                }

                AudioInputOpenResult capture;
                try
                {
                    capture = _captureFactory(captureEndpointId, remaining).WithPairEvidence(pair.PairEvidence);
                }
                catch (Exception ex)
                {
                    render.Dispose();
                    return AudioInputOpenResult.Failure("audio_capture_start_failed",
                        FormatFailure("HFP capture open", ex), HfpFailureStages.CaptureOpen,
                        Hresult(ex), pair.PairEvidence);
                }

                if (capture.Input == null)
                {
                    render.Dispose();
                    return capture;
                }

                renderFailure = render.RuntimeFailure;
                if (renderFailure != null)
                {
                    try { capture.Input.Dispose(); } catch { }
                    return RuntimeFailure(renderFailure, pair.PairEvidence, render);
                }

                try
                {
                    return AudioInputOpenResult.Success(
                        new HfpDuplexAudioInput(capture.Input, render, pair.PairEvidence, render.ReadyMs),
                        pair.PairEvidence);
                }
                catch (Exception ex)
                {
                    try { capture.Input.Dispose(); } catch { }
                    render.Dispose();
                    return AudioInputOpenResult.Failure("audio_capture_start_failed",
                        FormatFailure("HFP capture open", ex), HfpFailureStages.CaptureOpen,
                        Hresult(ex), pair.PairEvidence);
                }
            }
            catch
            {
                try { render.Dispose(); } catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            return AudioInputOpenResult.Failure("audio_hfp_pair_invalid",
                FormatFailure("HFP pair validation", ex), HfpFailureStages.PairValidation);
        }
        finally
        {
            try { endpointEnumerator?.Dispose(); } catch { }
        }
    }

    private static AudioInputOpenResult RuntimeFailure(HfpRenderFailure failure, string pairEvidence, IHfpRenderSession render)
    {
        try { render.Dispose(); } catch { }
        return AudioInputOpenResult.Failure(failure.ErrorCode, failure.Reason, failure.Stage,
            failure.Hresult, pairEvidence);
    }

    internal static string FormatFailure(string stage, Exception ex)
        => $"{stage} failed (HRESULT={FormatHresult(Hresult(ex))}): {SanitizeReason(ex.Message)}";

    internal static string SanitizeReason(string reason)
    {
        var clean = new string((reason ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 512 ? clean : clean[..512];
    }

    internal static int Hresult(Exception ex)
        => ex is COMException com ? com.HResult : ex.HResult;

    internal static string FormatHresult(int hresult) => $"0x{hresult:X8}";

    internal static string? FormatHresult(int? hresult)
        => hresult.HasValue ? FormatHresult(hresult.Value) : null;
}

internal sealed class NAudioHfpEndpointEnumerator : IHfpEndpointEnumerator
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IHfpEndpoint GetDevice(string endpointId)
    {
        MMDevice? device = null;
        try
        {
            device = _enumerator.GetDevice(endpointId);
            var endpoint = new NAudioHfpEndpoint(device);
            device = null;
            return endpoint;
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            throw;
        }
    }

    public IReadOnlyList<IHfpEndpoint> EnumerateRenderEndpoints()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return HfpEndpointCollectionBuilder.Build(
            devices.Count,
            index => devices[index],
            device => new NAudioHfpEndpoint(device),
            device => device.Dispose(),
            endpoint => endpoint.Dispose());
    }

    public void Dispose() => _enumerator.Dispose();
}

/// <summary>
/// Transfers ownership from each raw endpoint returned by an enumerator to its
/// wrapper. The transfer is explicit so a failure at any index can release
/// both the current raw object and all wrappers already placed in the list.
/// </summary>
internal static class HfpEndpointCollectionBuilder
{
    internal static IReadOnlyList<TEndpoint> Build<TSource, TEndpoint>(
        int count,
        Func<int, TSource> getSource,
        Func<TSource, TEndpoint> wrap,
        Action<TSource> disposeSource,
        Action<TEndpoint> disposeEndpoint)
        where TSource : class
        where TEndpoint : class
    {
        var endpoints = new List<TEndpoint>(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                TSource? source = null;
                TEndpoint? endpoint = null;
                try
                {
                    source = getSource(index);
                    endpoint = wrap(source);
                    source = null;
                    endpoints.Add(endpoint);
                    endpoint = null;
                }
                catch
                {
                    try { if (endpoint != null) disposeEndpoint(endpoint); } catch { }
                    try { if (source != null) disposeSource(source); } catch { }
                    throw;
                }
            }

            return endpoints;
        }
        catch
        {
            foreach (var endpoint in endpoints)
            {
                try { disposeEndpoint(endpoint); } catch { }
            }
            throw;
        }
    }
}

internal sealed class NAudioHfpEndpoint : IHfpEndpoint
{
    private static readonly PropertyKey ContainerIdKey = new(
        new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey FormFactorKey = new(
        new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey EnumeratorNameKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private readonly MMDevice _device;

    public NAudioHfpEndpoint(MMDevice device) => _device = device ?? throw new ArgumentNullException(nameof(device));
    public string EndpointId => _device.ID;
    public DataFlow DataFlow => _device.DataFlow;
    public DeviceState State => _device.State;
    public WaveFormat MixFormat
    {
        get
        {
            using var client = _device.AudioClient;
            return client.MixFormat;
        }
    }
    public bool TryGetContainerId(out Guid containerId, out string failure)
    {
        containerId = Guid.Empty;
        try
        {
            if (!_device.Properties.TryGetValue<Guid>(ContainerIdKey, out containerId) || containerId == Guid.Empty)
            {
                failure = "HFP endpoint ContainerId is missing or empty";
                return false;
            }
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = HfpDuplexAudioInputFactory.FormatFailure("HFP endpoint ContainerId query", ex);
            return false;
        }
    }

    public bool TryGetTransportClassification(out HfpTransportClassification classification, out string failure)
    {
        classification = HfpTransportClassification.Unknown;
        try
        {
            var enumeratorName = ReadProperty(EnumeratorNameKey) as string;
            if (string.Equals(enumeratorName, "BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(enumeratorName, "BTHHFENUM", StringComparison.OrdinalIgnoreCase))
            {
                classification = HfpTransportClassification.HfpCandidate;
                failure = "";
                return true;
            }

            if (IsExplicitNonBluetoothEnumerator(enumeratorName))
            {
                classification = HfpTransportClassification.NotHfp;
                failure = "";
                return true;
            }

            classification = ClassifyFormFactor(ReadProperty(FormFactorKey));
            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            failure = HfpDuplexAudioInputFactory.FormatFailure("HFP transport property query", ex);
            return false;
        }
    }

    private object? ReadProperty(PropertyKey key)
    {
        if (!_device.Properties.Contains(key))
            return null;
        return _device.Properties[key].Value;
    }

    private static bool IsExplicitNonBluetoothEnumerator(string? value)
        => string.Equals(value, "USB", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "HDAUDIO", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "SWD", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "ROOT", StringComparison.OrdinalIgnoreCase);

    private static HfpTransportClassification ClassifyFormFactor(object? value)
    {
        var formFactor = value switch
        {
            uint unsignedValue => unsignedValue,
            int signedValue when signedValue >= 0 => (uint)signedValue,
            _ => uint.MaxValue
        };

        return formFactor switch
        {
            1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 => HfpTransportClassification.NotHfp,
            _ => HfpTransportClassification.Unknown
        };
    }

    public void Dispose() => _device.Dispose();
}

internal sealed class HfpDuplexAudioInput : IAudioInput, IHfpAudioInputMetadata
{
    private readonly IAudioInput _capture;
    private readonly IHfpRenderSession _render;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _monitorStop = new(false);
    private Thread? _renderFailureMonitor;
    private HfpRenderFailure? _latchedFailure;
    private int _state;
    private int _terminalRaised;
    private int _disposed;
    private int _cleanupOwner;
    private int _resourcesReleased;

    private enum State
    {
        Created,
        Starting,
        Capturing,
        Stopped,
        Disposed
    }

    public HfpDuplexAudioInput(IAudioInput capture, IHfpRenderSession render, string pairEvidence, long renderPrimeReadyMs)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        PairEvidence = pairEvidence;
        RenderPrimeReadyMs = renderPrimeReadyMs;
        _capture.DataAvailable += ForwardDataAvailable;
        _capture.RecordingStopped += ForwardRecordingStopped;
    }

    public WaveFormat? Format => _capture.Format;
    public AudioSourceKind SourceKind => AudioSourceKind.Microphone;
    public long DiscontinuityCount => _capture.DiscontinuityCount;
    public string CaptureStrategy => "hfp-duplex-prime-classic";
    public string PairEvidence { get; }
    public long RenderPrimeReadyMs { get; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public StartRecordingResult StartRecording()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _state == (int)State.Disposed)
                return StartRecordingResult.Disposed;
            if (_state != (int)State.Created)
                throw new InvalidOperationException("Recording has already been started");

            var failure = CurrentRenderFailure();
            if (failure != null)
                throw CreateStartException(failure);

            _state = (int)State.Starting;
            try
            {
                _renderFailureMonitor = new Thread(RenderFailureLoop)
                {
                    IsBackground = true,
                    Name = "HfpRenderFailureMonitor"
                };
                _renderFailureMonitor.Start();
            }
            catch
            {
                _state = (int)State.Stopped;
                _monitorStop.Set();
                throw;
            }
        }

        StartRecordingResult result;
        try
        {
            result = _capture.StartRecording();
        }
        catch (Exception ex)
        {
            lock (_gate) _state = (int)State.Stopped;
            _monitorStop.Set();
            throw new AudioCaptureStartException(
                "HFP capture Start failed: " + HfpDuplexAudioInputFactory.SanitizeReason(ex.Message),
                ex, HfpDuplexAudioInputFactory.Hresult(ex), "audio_capture_start_failed", HfpFailureStages.CaptureStart);
        }

        if (result != StartRecordingResult.Started)
        {
            lock (_gate) _state = (int)State.Stopped;
            _monitorStop.Set();
            return result;
        }

        var renderFailure = CurrentRenderFailure();
        if (renderFailure != null)
        {
            HandleRenderFailure(renderFailure);
            throw CreateStartException(renderFailure);
        }

        lock (_gate)
        {
            if (_state == (int)State.Starting)
                _state = (int)State.Capturing;
        }
        return result;
    }

    public void StopRecording() => _capture.StopRecording();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate) _state = (int)State.Disposed;
        _monitorStop.Set();

        var monitor = Volatile.Read(ref _renderFailureMonitor);
        if (monitor != null && monitor != Thread.CurrentThread)
        {
            if (monitor.IsAlive && !monitor.Join(TimeSpan.FromSeconds(5)))
                return;
        }

        if (Interlocked.CompareExchange(ref _cleanupOwner, 1, 0) == 0)
            CleanupResources();
    }

    private void RenderFailureLoop()
    {
        try
        {
            while (!_monitorStop.Wait(50))
            {
                var failure = CurrentRenderFailure();
                if (failure == null)
                    continue;
                HandleRenderFailure(failure);
                return;
            }
        }
        catch (Exception ex)
        {
            var failure = new HfpRenderFailure(
                HfpDuplexAudioInputFactory.FormatFailure("HFP render runtime monitor", ex),
                HfpDuplexAudioInputFactory.Hresult(ex), ex, HfpFailureStages.RenderRuntimePump);
            HandleRenderFailure(failure);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0 && Interlocked.CompareExchange(ref _cleanupOwner, 2, 0) == 0)
                CleanupResources();
        }
    }

    private void HandleRenderFailure(HfpRenderFailure failure)
    {
        if (Interlocked.CompareExchange(ref _latchedFailure, failure, null) != null)
            failure = Volatile.Read(ref _latchedFailure)!;

        _monitorStop.Set();
        var runtime = CreateRuntimeException(failure);
        try
        {
            _capture.StopRecording();
        }
        catch (Exception stopException)
        {
            runtime.TryAttachSecondaryFailure(HfpFailureStages.CaptureStop, stopException);
        }

        PublishTerminal(runtime);
    }

    private void ForwardDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_gate)
        {
            var failure = CurrentRenderFailure();
            if (failure != null)
            {
                HandleRenderFailure(failure);
                return;
            }
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _terminalRaised) != 0)
                return;
            DataAvailable?.Invoke(this, args);
        }
    }

    private void ForwardRecordingStopped(object? sender, StoppedEventArgs args)
    {
        var failure = Volatile.Read(ref _latchedFailure) ?? CurrentRenderFailure();
        PublishTerminal(failure == null ? args.Exception : CreateRuntimeException(failure));
    }

    private void PublishTerminal(Exception? exception)
    {
        if (Interlocked.Exchange(ref _terminalRaised, 1) != 0)
            return;
        _monitorStop.Set();
        lock (_gate)
        {
            if (_state != (int)State.Disposed)
                _state = (int)State.Stopped;
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));
    }

    private HfpRenderFailure? CurrentRenderFailure()
        => Volatile.Read(ref _latchedFailure) ?? _render.RuntimeFailure;

    private static AudioCaptureStartException CreateStartException(HfpRenderFailure failure)
    {
        var inner = failure.Exception ?? new InvalidOperationException(failure.Reason);
        return new AudioCaptureStartException(failure.Reason, inner, failure.Hresult,
            failure.ErrorCode, failure.Stage);
    }

    private static AudioCaptureRuntimeException CreateRuntimeException(HfpRenderFailure failure)
    {
        var inner = failure.Exception ?? new InvalidOperationException(failure.Reason);
        return new AudioCaptureRuntimeException(failure.Stage, failure.Reason, inner, failure.Hresult, failure.ErrorCode);
    }

    private void CleanupResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
            return;
        _capture.DataAvailable -= ForwardDataAvailable;
        _capture.RecordingStopped -= ForwardRecordingStopped;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        try { _render.Dispose(); } catch { }
        try { _monitorStop.Dispose(); } catch { }
    }
}

internal sealed class HfpRenderFailure
{
    public HfpRenderFailure(string reason, int hresult, Exception? exception = null,
        string stage = HfpFailureStages.RenderRuntimePump, string errorCode = "audio_hfp_render_runtime_failed")
    {
        Reason = HfpDuplexAudioInputFactory.SanitizeReason(reason);
        Hresult = hresult;
        Exception = exception;
        Stage = stage;
        ErrorCode = errorCode;
    }

    public string Reason { get; }
    public int Hresult { get; }
    public Exception? Exception { get; }
    public string Stage { get; }
    public string ErrorCode { get; }
}

internal sealed class NAudioHfpRenderPrimeFactory : IHfpRenderPrimeFactory
{
    private readonly IHfpRenderActivationFactory _activationFactory;
    private readonly IHfpComApartmentFactory _apartmentFactory;

    public NAudioHfpRenderPrimeFactory()
        : this(new NAudioHfpRenderActivationFactory(), new NativeHfpComApartmentFactory()) { }

    internal NAudioHfpRenderPrimeFactory(IHfpRenderActivationFactory activationFactory,
        IHfpComApartmentFactory? apartmentFactory = null)
    {
        _activationFactory = activationFactory;
        _apartmentFactory = apartmentFactory ?? new NativeHfpComApartmentFactory();
    }

    public HfpRenderPrimeResult Prime(string renderEndpointId, TimeSpan budget)
        => HfpRenderPrime.CreateAndPrime(renderEndpointId, budget, _activationFactory, _apartmentFactory);
}

internal sealed class HfpRenderPrime : IHfpRenderSession
{
    private readonly HfpRenderOwner _owner;

    private HfpRenderPrime(HfpRenderOwner owner)
        => _owner = owner;

    public long ReadyMs => _owner.ReadyMs;
    public HfpRenderFailure? RuntimeFailure => _owner.RuntimeFailure;

    internal static HfpRenderPrimeResult CreateAndPrime(string renderEndpointId, TimeSpan budget,
        IHfpRenderActivationFactory activationFactory, IHfpComApartmentFactory? apartmentFactory = null)
    {
        var owner = new HfpRenderOwner(renderEndpointId, budget, activationFactory,
            apartmentFactory ?? new NativeHfpComApartmentFactory());
        owner.Start();

        if (!owner.WaitForStartup(budget))
        {
            owner.Dispose();
            return HfpRenderPrimeResult.Failure("audio_hfp_render_prime_failed",
                "HFP render prime timed out", HfpFailureStages.RenderPrime);
        }

        var failure = owner.StartupFailure;
        if (failure != null)
        {
            owner.Dispose();
            return HfpRenderPrimeResult.Failure(failure.ErrorCode, failure.Reason,
                failure.Stage, failure.Hresult);
        }

        return HfpRenderPrimeResult.Success(new HfpRenderPrime(owner));
    }

    public void Dispose() => _owner.Dispose();
}

internal sealed class HfpRenderOwnerFailure
{
    public HfpRenderOwnerFailure(string errorCode, string reason, string stage, int? hresult)
    {
        ErrorCode = errorCode;
        Reason = reason;
        Stage = stage;
        Hresult = hresult;
    }

    public string ErrorCode { get; }
    public string Reason { get; }
    public string Stage { get; }
    public int? Hresult { get; }
}

internal sealed class HfpRenderOwner : IDisposable
{
    private const int FirstRefillTimeoutMs = 5000;
    private const int DisposeJoinTimeoutMs = 5000;
    private readonly string _renderEndpointId;
    private readonly TimeSpan _budget;
    private readonly IHfpRenderActivationFactory _activationFactory;
    private readonly IHfpComApartmentFactory _apartmentFactory;
    private readonly ManualResetEventSlim _startup = new(false);
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly Thread _thread;
    private HfpRenderOwnerFailure? _startupFailure;
    private HfpRenderFailure? _runtimeFailure;
    private long _readyMs;
    private int _startupSucceeded;
    private int _disposed;
    private int _signalsDisposed;

    internal HfpRenderOwner(string renderEndpointId, TimeSpan budget,
        IHfpRenderActivationFactory activationFactory, IHfpComApartmentFactory apartmentFactory)
    {
        _renderEndpointId = renderEndpointId;
        _budget = budget;
        _activationFactory = activationFactory;
        _apartmentFactory = apartmentFactory;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "HfpRenderOwner"
        };
    }

    public long ReadyMs => Volatile.Read(ref _readyMs);
    public HfpRenderFailure? RuntimeFailure => Volatile.Read(ref _runtimeFailure);
    public HfpRenderOwnerFailure? StartupFailure => Volatile.Read(ref _startupFailure);
    internal int OwnerThreadId => _thread.ManagedThreadId;
    internal bool IsAlive => _thread.IsAlive;

    public void Start() => _thread.Start();

    public bool WaitForStartup(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return false;
        return _startup.Wait(timeout);
    }

    private void Run()
    {
        IHfpComApartment? apartment = null;
        IHfpRenderActivationClient? client = null;
        IHfpRenderBuffer? renderClient = null;
        AutoResetEvent? audioEvent = null;
        var initialized = false;
        var startupSignaled = false;
        var stage = HfpFailureStages.RenderActivation;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            apartment = _apartmentFactory.Enter();
            client = _activationFactory.Activate(_renderEndpointId);

            stage = HfpFailureStages.RenderSetClientProperties;
            client.SetClientProperties(new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                bIsOffload = 0,
                eCategory = AudioStreamCategory.Communications,
                Options = AudioClientStreamOptions.None
            });

            stage = HfpFailureStages.RenderPrime;
            var format = client.MixFormat;
            if (!client.IsFormatSupported(AudioClientShareMode.Shared, format))
                throw new InvalidOperationException("Render mix format is not supported in shared mode");

            audioEvent = new AutoResetEvent(false);
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                0, 0, format, Guid.Empty);
            initialized = true;
            client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle());
            renderClient = client.GetRenderBuffer();
            var frames = client.BufferSize;

            renderClient.GetBuffer(frames);
            renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
            client.Start();

            if (!WaitForFirstRefill(client, renderClient, audioEvent, _budget - stopwatch.Elapsed))
            {
                SignalStartupFailure(new HfpRenderOwnerFailure("audio_hfp_render_prime_failed",
                    "HFP render first refill timed out", HfpFailureStages.RenderPrime, null));
                startupSignaled = true;
                return;
            }

            Volatile.Write(ref _readyMs, (long)stopwatch.Elapsed.TotalMilliseconds);
            Volatile.Write(ref _startupSucceeded, 1);
            _startup.Set();
            startupSignaled = true;
            PumpLoop(client, renderClient, audioEvent);
        }
        catch (HfpRenderActivationException ex)
        {
            if (!startupSignaled)
            {
                SignalStartupFailure(new HfpRenderOwnerFailure("audio_hfp_render_prime_failed",
                    HfpDuplexAudioInputFactory.FormatFailure("HFP render activation", ex),
                    ex.Stage, ex.Hresult));
                startupSignaled = true;
            }
            else
            {
                SetRuntimeFailure(ex, ex.Stage);
            }
        }
        catch (Exception ex)
        {
            if (!startupSignaled)
            {
                var label = stage == HfpFailureStages.RenderSetClientProperties
                    ? "HFP render SetClientProperties"
                    : stage == HfpFailureStages.RenderActivation
                        ? "HFP render activation"
                        : "HFP render prime";
                SignalStartupFailure(new HfpRenderOwnerFailure("audio_hfp_render_prime_failed",
                    HfpDuplexAudioInputFactory.FormatFailure(label, ex), stage,
                    HfpDuplexAudioInputFactory.Hresult(ex)));
                startupSignaled = true;
            }
            else
            {
                SetRuntimeFailure(ex, HfpFailureStages.RenderRuntimePump);
            }
        }
        finally
        {
            try
            {
                if (initialized)
                    client?.Stop();
            }
            catch (Exception ex)
            {
                SetRuntimeFailure(ex, HfpFailureStages.RenderStop);
            }

            try { renderClient?.Dispose(); }
            catch (Exception ex) { SetRuntimeFailure(ex, HfpFailureStages.RenderStop); }
            try { client?.Dispose(); }
            catch (Exception ex) { SetRuntimeFailure(ex, HfpFailureStages.RenderStop); }
            try { audioEvent?.Dispose(); }
            catch (Exception ex) { SetRuntimeFailure(ex, HfpFailureStages.RenderStop); }
            try { apartment?.Dispose(); }
            catch (Exception ex) { SetRuntimeFailure(ex, HfpFailureStages.RenderStop); }

            if (!startupSignaled)
                SignalStartupFailure(new HfpRenderOwnerFailure("audio_hfp_render_prime_failed",
                    "HFP render owner stopped before startup completed", HfpFailureStages.RenderPrime, null));

            if (Volatile.Read(ref _disposed) != 0)
                DisposeSignals();
        }
    }

    private bool WaitForFirstRefill(IHfpRenderActivationClient client, IHfpRenderBuffer renderClient,
        AutoResetEvent audioEvent, TimeSpan remaining)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0,
            Math.Min(remaining.TotalMilliseconds, FirstRefillTimeoutMs)) * Stopwatch.Frequency / 1000.0);
        while (!_stopRequested.IsSet && Stopwatch.GetTimestamp() < deadline)
        {
            if (PumpOnce(client, renderClient))
                return true;
            var waitMs = Math.Min(100, Math.Max(1,
                (int)Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline).TotalMilliseconds));
            audioEvent.WaitOne(waitMs);
        }
        return false;
    }

    private void PumpLoop(IHfpRenderActivationClient client, IHfpRenderBuffer renderClient,
        AutoResetEvent audioEvent)
    {
        var handles = new WaitHandle[] { audioEvent, _stopRequested.WaitHandle };
        while (!_stopRequested.IsSet)
        {
            if (WaitHandle.WaitAny(handles, 100) == 1)
                break;
            PumpOnce(client, renderClient);
        }
    }

    private static bool PumpOnce(IHfpRenderActivationClient client, IHfpRenderBuffer renderClient)
    {
        var available = client.BufferSize - client.CurrentPadding;
        if (available <= 0)
            return false;
        renderClient.GetBuffer(available);
        renderClient.ReleaseBuffer(available, AudioClientBufferFlags.Silent);
        return true;
    }

    private void SignalStartupFailure(HfpRenderOwnerFailure failure)
    {
        if (Interlocked.CompareExchange(ref _startupFailure, failure, null) == null)
            _startup.Set();
    }

    private void SetRuntimeFailure(Exception ex, string stage)
    {
        Interlocked.CompareExchange(ref _runtimeFailure, new HfpRenderFailure(
            HfpDuplexAudioInputFactory.FormatFailure(stage, ex),
            HfpDuplexAudioInputFactory.Hresult(ex), ex, stage), null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopRequested.Set();
        if (_thread != Thread.CurrentThread && _thread.IsAlive)
            _thread.Join(DisposeJoinTimeoutMs);
        if (!_thread.IsAlive)
            DisposeSignals();
    }

    private void DisposeSignals()
    {
        if (Interlocked.Exchange(ref _signalsDisposed, 1) != 0)
            return;
        _startup.Dispose();
        _stopRequested.Dispose();
    }
}
