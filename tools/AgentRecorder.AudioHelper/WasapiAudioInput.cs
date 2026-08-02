using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Abstraction over a Windows audio capture device. Production implementation
/// uses the CoreAudio AudioClient directly; fake implementations are used for
/// deterministic unit tests.
/// </summary>
internal interface IAudioInput : IDisposable
{
    WaveFormat? Format { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>
    /// Number of delivered packets that carried the WASAPI DataDiscontinuity
    /// flag. Objective stream-health evidence used for diagnostics; 0 when the
    /// input does not surface the flag.
    /// </summary>
    long DiscontinuityCount { get; }

    StartRecordingResult StartRecording();
    void StopRecording();
}

/// <summary>
/// Opens a WASAPI capture endpoint for the approved device only. Handles
/// bounded retry and format negotiation, but never silently switches to a
/// different endpoint.
///
/// The retry loop covers the full single-attempt lifecycle: enumerate the
/// approved endpoint, negotiate a shared-mode format, create the capture
/// client, and hand ownership to an <see cref="AudioClientAudioInput"/>.
/// The returned input is <em>ready</em> but not yet started;
/// <see cref="AudioClientAudioInput.StartRecording"/> performs the actual
/// AudioClient.Start and starts the capture thread. A failure there is
/// handled by the caller (see <see cref="CaptureSession"/>) so that the
/// formal start phase is also covered by the overall retry policy.
/// </summary>
internal static class WasapiAudioInput
{
    private const long ReftimesPerSec = 10000000L;
    private const long ReftimesPerMillisec = 10000L;
    private const int DefaultBufferMilliseconds = 100;
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan TotalRetryBudget = TimeSpan.FromSeconds(5);
    private static readonly AudioClientStreamFlags StreamFlags =
        AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

    /// <summary>
    /// Opens the requested CoreAudio endpoint. Retries a bounded number of
    /// times for transient post-reconnection states, but only for the exact
    /// endpoint id that was approved by the user.
    /// </summary>
    public static (IAudioInput? Input, string? ErrorCode, string? Reason) Open(string endpointId)
        => Open(endpointId, TotalRetryBudget);

    /// <summary>
    /// Production overload that lets the caller bound the total monotonic
    /// budget for open attempts. The caller (e.g. <see cref="CaptureSession"/>)
    /// owns the outer deadline; this method does not exceed the supplied budget.
    /// </summary>
    public static (IAudioInput? Input, string? ErrorCode, string? Reason) Open(string endpointId, TimeSpan totalBudget)
    {
        using var enumerator = new NAudioDeviceEnumerator();
        return Open(endpointId, enumerator, SystemClock.Instance, TryOpenOnce, totalBudget);
    }

    /// <summary>
    /// Opens the exact endpoint with the classic HFP capture profile. This is
    /// deliberately separate from <see cref="Open(string, TimeSpan)"/> so the
    /// ordinary direct profile keeps its format candidates and conversion flags.
    /// </summary>
    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) OpenClassic(
        string endpointId, TimeSpan totalBudget)
    {
        using var enumerator = new NAudioDeviceEnumerator();
        return Open(endpointId, enumerator, SystemClock.Instance, TryOpenOnceClassic, totalBudget);
    }

    /// <summary>
    /// Test seam: the same retry policy as <see cref="Open(string)"/> but the
    /// single-attempt opener, clock and enumerator can be substituted for
    /// deterministic unit tests.
    /// </summary>
    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) Open(
        string endpointId,
        IDeviceEnumerator enumerator,
        ISystemClock clock,
        Func<string, IDeviceEnumerator, (IAudioInput? Input, string? ErrorCode, string? Reason)> tryOpenOnce,
        TimeSpan? totalBudget = null)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return (null, "audio_endpoint_not_found", "Endpoint id is empty");

        var budget = totalBudget ?? TotalRetryBudget;
        var stopwatch = clock.StartStopwatch();
        string? lastReason = null;
        string? lastCode = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Check the monotonic deadline before beginning any endpoint/open/
            // initialize attempt. If the budget is already exhausted we must not
            // start another synchronous COM call.
            var remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var result = tryOpenOnce(endpointId, enumerator);
            if (result.Input != null)
                return result;

            lastReason = result.Reason;
            lastCode = result.ErrorCode;

            bool transient = result.ErrorCode is "audio_endpoint_unavailable"
                                              or "audio_format_negotiation_failure"
                                              or "audio_capture_start_failed";

            if (!transient || attempt >= MaxAttempts)
                break;

            remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
            if (delay > remaining)
                delay = remaining;

            clock.Sleep(delay);
        }

        return (null, lastCode ?? "audio_helper_runtime_failure", lastReason ?? "Failed to open audio endpoint");
    }

    /// <summary>
    /// Single attempt to open the endpoint. The enumerator is reused across
    /// retries within one <see cref="Open"/> call, but the
    /// <see cref="IDevice"/>, <see cref="IAudioClient"/> and
    /// <see cref="IAudioCaptureClient"/> are recreated on every attempt.
    /// On success, the returned <see cref="AudioClientAudioInput"/> owns
    /// the device, client and capture client. On failure, all COM objects
    /// created during the attempt are released before returning.
    /// </summary>
    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpenOnce(
        string endpointId,
        IDeviceEnumerator enumerator)
    {
        IDevice? device = null;

        try
        {
            try
            {
                device = enumerator.GetDevice(endpointId);
            }
            catch (Exception ex)
            {
                device?.Dispose();
                return (null, "audio_endpoint_not_found", $"Failed to resolve endpoint: {ex.GetType().Name}: {ex.Message}");
            }

            var endpointState = device.State;
            switch (endpointState)
            {
                case DeviceState.NotPresent:
                    device.Dispose();
                    return (null, "audio_endpoint_not_found", "Endpoint not present");
                case DeviceState.Unplugged:
                    device.Dispose();
                    return (null, "audio_endpoint_inactive", "Endpoint unplugged");
                case DeviceState.Disabled:
                    device.Dispose();
                    return (null, "audio_endpoint_inactive", "Endpoint disabled");
            }

            if (endpointState != DeviceState.Active)
            {
                device.Dispose();
                return (null, "audio_endpoint_inactive", $"Endpoint state is {endpointState}");
            }

            return TryInitializeCapture(device, endpointId, endpointState);
        }
        catch (Exception ex)
        {
            device?.Dispose();
            var (code, reason) = ClassifyFailure(ex);
            return (null, code, reason);
        }
    }

    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) TryOpenOnceClassic(
        string endpointId,
        IDeviceEnumerator enumerator)
    {
        IDevice? device = null;
        try
        {
            device = enumerator.GetDevice(endpointId);
            var endpointState = device.State;
            if (endpointState != DeviceState.Active)
            {
                var code = endpointState == DeviceState.NotPresent
                    ? "audio_endpoint_not_found"
                    : "audio_endpoint_inactive";
                device.Dispose();
                return (null, code, $"Endpoint state is {endpointState}");
            }

            return TryInitializeClassicCapture(device, endpointId, endpointState);
        }
        catch (Exception ex)
        {
            device?.Dispose();
            var (code, reason) = ClassifyFailure(ex);
            return (null, code, reason);
        }
    }

    /// <summary>
    /// HFP classic profile: exact endpoint mix format, shared mode, event
    /// callback, zero buffer duration/periodicity, and no auto conversion.
    /// </summary>
    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) TryInitializeClassicCapture(
        IDevice device,
        string endpointId,
        DeviceState endpointState)
    {
        IAudioClient? audioClientToDispose = null;
        IAudioCaptureClient? captureClientToDispose = null;
        IAudioClient? probeClient = null;
        try
        {
            WaveFormat mixFormat;
            try
            {
                probeClient = device.CreateAudioClient();
                mixFormat = probeClient.MixFormat;
            }
            finally
            {
                try { probeClient?.Dispose(); } finally { probeClient = null; }
            }

            var audioClient = device.CreateAudioClient();
            audioClientToDispose = audioClient;
            if (audioClient is not IEventDrivenAudioClient)
                throw new InvalidOperationException("HFP classic capture requires an event-capable AudioClient");

            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback,
                0,
                0,
                mixFormat,
                Guid.Empty);

            var captureClient = audioClient.GetAudioCaptureClient();
            captureClientToDispose = captureClient;
            var input = new AudioClientAudioInput(
                device, audioClient, captureClient, mixFormat, DefaultBufferMilliseconds,
                eventDriven: true);
            audioClientToDispose = null;
            captureClientToDispose = null;
            return (input, null, null);
        }
        catch (Exception ex)
        {
            captureClientToDispose?.Dispose();
            audioClientToDispose?.Dispose();
            device.Dispose();
            var (code, reason) = ClassifyFailure(ex);
            return (null, code, reason + $"\nEndpointId={endpointId}\nEndpointState={endpointState}\nCaptureProfile=hfp-classic\nStreamFlags={AudioClientStreamFlags.EventCallback} BufferDuration=0 Periodicity=0");
        }
    }

    /// <summary>
    /// Attempts to create an AudioClient for the active device and initialize it
    /// with a supported shared-mode format. Each failed candidate creates and
    /// disposes its own AudioClient so no partially-initialized client is reused.
    /// The AudioClient is <em>not</em> started here; starting is the responsibility
    /// of <see cref="AudioClientAudioInput.StartRecording"/> so that a start failure
    /// can be retried by the caller.
    /// </summary>
    internal static (IAudioInput? Input, string? ErrorCode, string? Reason) TryInitializeCapture(
        IDevice device,
        string endpointId,
        DeviceState endpointState)
    {
        WaveFormat? mixFormat = null;
        string? mixFormatStage = null;

        // First read the device's mix format so candidates can be derived from it.
        IAudioClient? probeClient = null;
        try
        {
            probeClient = device.CreateAudioClient();
            mixFormat = probeClient.MixFormat;
            mixFormatStage = "MixFormat";
        }
        catch (Exception ex)
        {
            probeClient?.Dispose();
            device.Dispose();
            var probeFailedCandidates = new List<CandidateDiagnostic>();
            if (mixFormat != null)
                probeFailedCandidates.Add(new CandidateDiagnostic(-1, mixFormat, "mix-raw", "MixFormat", HresultFrom(ex), ex.Message));
            var diag = BuildDiagnosticSummary(endpointId, endpointState, mixFormat, mixFormatStage, probeFailedCandidates, ex, "MixFormat", HresultFrom(ex));
            var (errorCode, errorReason) = ClassifyFailure(ex);
            return (null, errorCode, errorReason + "\n" + diag);
        }

        probeClient.Dispose();
        probeClient = null;

        var candidates = BuildFormatCandidates(mixFormat);
        long bufferDuration = ReftimesPerMillisec * DefaultBufferMilliseconds;
        var failedCandidates = new List<CandidateDiagnostic>();

        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var (candidate, source) = candidates[candidateIndex];
            IAudioClient? audioClientToDispose = null;
            IAudioCaptureClient? captureClientToDispose = null;
            try
            {
                var audioClient = device.CreateAudioClient();
                audioClientToDispose = audioClient;
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    StreamFlags,
                    bufferDuration,
                    0,
                    candidate,
                    Guid.Empty);

                var captureClient = audioClient.GetAudioCaptureClient();
                captureClientToDispose = captureClient;

                var input = new AudioClientAudioInput(device, audioClient, captureClient, candidate, DefaultBufferMilliseconds);

                // Ownership has been transferred to the input. Null the dispose
                // handles so the catch block does not release the now-owned objects.
                audioClientToDispose = null;
                captureClientToDispose = null;
                return (input, null, null);
            }
            catch (Exception ex)
            {
                var stage = ex is AudioCaptureStartException ? "StartRecording" : "Initialize";
                var hresult = HresultFrom(ex);
                failedCandidates.Add(new CandidateDiagnostic(candidateIndex, candidate, source, stage, hresult, ex.Message));

                captureClientToDispose?.Dispose();
                audioClientToDispose?.Dispose();

                if (candidateIndex == candidates.Count - 1)
                {
                    device.Dispose();
                    var (code, reason) = ClassifyFailure(ex);
                    var diag = BuildDiagnosticSummary(endpointId, endpointState, mixFormat, mixFormatStage, failedCandidates, ex, stage, hresult);
                    return (null, code, reason + "\n" + diag);
                }
            }
        }

        device?.Dispose();
        var finalEx = new InvalidOperationException("No format candidate succeeded");
        var finalDiag = BuildDiagnosticSummary(
            endpointId,
            endpointState,
            mixFormat,
            mixFormatStage,
            failedCandidates,
            finalEx,
            "Initialize",
            HresultFrom(finalEx));
        var (finalCode, finalReason) = ClassifyFailure(finalEx);
        return (null, finalCode, finalReason + "\n" + finalDiag);
    }

    private static List<(WaveFormat Format, string Source)> BuildFormatCandidates(WaveFormat mixFormat)
    {
        var candidates = new List<(WaveFormat, string)>();

        void AddCandidate(WaveFormat? format, string source)
        {
            if (format == null) return;
            foreach (var (existing, _) in candidates)
            {
                if (existing.SampleRate == format.SampleRate &&
                    existing.Channels == format.Channels &&
                    existing.BitsPerSample == format.BitsPerSample &&
                    existing.Encoding == format.Encoding)
                {
                    return;
                }
            }
            candidates.Add((format, source));
        }

        // 1. Standard 16-bit PCM at the mix sample rate / channel count. The
        //    WASAPI engine will convert if necessary (AutoConvertPcm).
        AddCandidate(new WaveFormat(mixFormat.SampleRate, 16, mixFormat.Channels), "mix-pcm16");

        // 2. The mix format expressed as a standard WAVEFORMATEX/IEEE float.
        AddCandidate(mixFormat.AsStandardWaveFormat(), "mix-standard");

        // 3. The raw mix format (often WAVEFORMATEXTENSIBLE).
        AddCandidate(mixFormat, "mix-raw");

        // 4/5. Common Bluetooth/HFP microphone formats.
        AddCandidate(new WaveFormat(16000, 16, 1), "bluetooth-16k-mono");
        AddCandidate(new WaveFormat(8000, 16, 1), "bluetooth-8k-mono");

        return candidates;
    }

    private sealed class CandidateDiagnostic
    {
        public int Index { get; }
        public WaveFormat Format { get; }
        public string Source { get; }
        public string Stage { get; }
        public int Hresult { get; }
        public string Message { get; }

        public CandidateDiagnostic(int index, WaveFormat format, string source, string stage, int hresult, string message)
        {
            Index = index;
            Format = format;
            Source = source;
            Stage = stage;
            Hresult = hresult;
            Message = message;
        }
    }

    private static (string Code, string Reason) ClassifyFailure(Exception? ex)
    {
        if (ex == null)
        {
            return ("audio_helper_runtime_failure", "Unknown audio initialization failure");
        }

        var hresult = HresultFrom(ex);
        var typeName = ex.GetType().Name;
        var message = ex.Message ?? "";

        if (message.Contains("Value does not fall within the expected range", StringComparison.OrdinalIgnoreCase) ||
            hresult == unchecked((int)0x80070057))
        {
            return ("audio_format_negotiation_failure",
                $"WASAPI format negotiation failed ({typeName}, HRESULT={FormatHresult(hresult)}): {message}");
        }

        if (message.Contains("format", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Format", StringComparison.OrdinalIgnoreCase))
        {
            return ("audio_format_unsupported",
                $"Audio format unsupported ({typeName}, HRESULT={FormatHresult(hresult)}): {message}");
        }

        if (message.Contains("enumerator", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("activate", StringComparison.OrdinalIgnoreCase) ||
            hresult == unchecked((int)0x80070490)) // ERROR_NOT_FOUND
        {
            return ("audio_endpoint_unavailable",
                $"Endpoint active but audio client activation failed ({typeName}, HRESULT={FormatHresult(hresult)}): {message}");
        }

        return ("audio_capture_start_failed",
            $"StartRecording failed ({typeName}, HRESULT={FormatHresult(hresult)}): {message}");
    }

    private static int HresultFrom(Exception ex)
    {
        if (ex is COMException comEx)
            return comEx.HResult;
        if (ex is AudioCaptureRuntimeException runtimeEx)
            return runtimeEx.Hresult;
        try { return ex.HResult; }
        catch { return 0; }
    }

    private static string FormatHresult(int hresult)
    {
        if (hresult == 0)
            return "0x00000000";
        return $"0x{hresult:X8}";
    }

    private static string BuildDiagnosticSummary(
        string endpointId,
        DeviceState endpointState,
        WaveFormat? mixFormat,
        string? mixFormatStage,
        List<CandidateDiagnostic> failedCandidates,
        Exception ex,
        string stage,
        int hresult)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"EndpointId={endpointId}");
        sb.AppendLine($"EndpointState={endpointState}");
        if (mixFormat != null)
        {
            sb.AppendLine($"MixFormatStage={mixFormatStage ?? "unknown"}");
            AppendFormat(sb, "MixFormat", mixFormat, "mix-raw");
        }

        foreach (var candidate in failedCandidates)
        {
            sb.AppendLine($"CandidateIndex={candidate.Index} Source={candidate.Source} Stage={candidate.Stage} HRESULT={FormatHresult(candidate.Hresult)} Message={candidate.Message}");
            AppendFormat(sb, "Candidate", candidate.Format, candidate.Source);
        }

        sb.AppendLine($"ShareMode=Shared StreamFlags={StreamFlags} BufferDuration={ReftimesPerMillisec * DefaultBufferMilliseconds} Periodicity=0");
        sb.AppendLine($"FailureStage={stage}");
        sb.AppendLine($"ExceptionType={ex.GetType().Name}");
        sb.AppendLine($"HRESULT={FormatHresult(hresult)}");
        sb.Append($"Message={ex.Message}");
        return sb.ToString();
    }

    private static void AppendFormat(StringBuilder sb, string label, WaveFormat format, string source)
    {
        sb.AppendLine($"{label}.Source={source} Tag={(int)format.Encoding} SampleRate={format.SampleRate} Channels={format.Channels} BitsPerSample={format.BitsPerSample} BlockAlign={format.BlockAlign} AverageBytesPerSecond={format.AverageBytesPerSecond} ExtraSize={format.ExtraSize}");
        if (format is WaveFormatExtensible ext)
        {
            var (validBits, channelMask) = GetExtensibleDetails(ext);
            sb.AppendLine($"{label}.ValidBitsPerSample={validBits} ChannelMask=0x{channelMask:X8} Subformat={ext.SubFormat}");
        }
    }

    private static (int ValidBits, int ChannelMask) GetExtensibleDetails(WaveFormatExtensible ext)
    {
        var type = typeof(WaveFormatExtensible);
        var validBitsField = type.GetField("wValidBitsPerSample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var channelMaskField = type.GetField("dwChannelMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        int validBits = validBitsField?.GetValue(ext) is short v ? v : 0;
        int channelMask = channelMaskField?.GetValue(ext) is int m ? m : 0;
        return (validBits, channelMask);
    }
}

/// <summary>
/// Abstraction over the system clock so retry delays can be virtualized in
/// unit tests.
/// </summary>
internal interface ISystemClock
{
    IStopwatch StartStopwatch();
    void Sleep(TimeSpan delay);
}

internal interface IStopwatch
{
    TimeSpan Elapsed { get; }
}

internal sealed class SystemClock : ISystemClock
{
    public static readonly ISystemClock Instance = new SystemClock();

    private SystemClock() { }

    public IStopwatch StartStopwatch() => new StopwatchAdapter();

    public void Sleep(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            Thread.Sleep(delay);
    }

    private sealed class StopwatchAdapter : IStopwatch
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public TimeSpan Elapsed => _sw.Elapsed;
    }
}
