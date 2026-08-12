using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Opens the exact approved render endpoint in WASAPI shared-mode loopback.
/// This factory intentionally has no microphone format candidates, conversion
/// flags, HFP pairing, or render prime behavior: the endpoint's shared mix
/// format is the capture format.
/// </summary>
internal static class WasapiLoopbackAudioInput
{
    private const long ReftimesPerMillisec = 10000L;
    private const int DefaultBufferMilliseconds = 100;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan TotalRetryBudget = TimeSpan.FromSeconds(5);
    private static readonly AudioClientStreamFlags StreamFlags = AudioClientStreamFlags.Loopback;

    public static AudioInputOpenResult Open(string endpointId, TimeSpan totalBudget)
    {
        using var enumerator = new NAudioDeviceEnumerator();
        return Open(endpointId, enumerator, SystemClock.Instance, totalBudget);
    }

    internal static AudioInputOpenResult Open(
        string endpointId,
        IDeviceEnumerator enumerator,
        ISystemClock clock,
        TimeSpan? totalBudget = null)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return AudioInputOpenResult.Failure(
                "audio_endpoint_not_found",
                "Endpoint id is empty",
                "LoopbackEndpointResolve",
                captureStrategy: "wasapi-loopback");
        }

        var budget = totalBudget ?? TotalRetryBudget;
        var stopwatch = clock.StartStopwatch();
        AudioInputOpenResult? lastFailure = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            var result = TryOpenOnce(endpointId, enumerator);
            if (result.Input != null)
                return result;

            lastFailure = result;
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

        return lastFailure ?? AudioInputOpenResult.Failure(
            "audio_helper_runtime_failure",
            "Loopback open budget was exhausted before an attempt could begin",
            "LoopbackOpenBudget",
            captureStrategy: "wasapi-loopback");
    }

    internal static AudioInputOpenResult TryOpenOnce(string endpointId, IDeviceEnumerator enumerator)
    {
        IDevice? device = null;
        IAudioClient? probeClient = null;
        IAudioClient? audioClientToDispose = null;
        IAudioCaptureClient? captureClientToDispose = null;

        try
        {
            try
            {
                device = enumerator.GetDevice(endpointId);
            }
            catch (Exception ex)
            {
                return Failure("audio_endpoint_not_found", "LoopbackEndpointResolve", ex, endpointId);
            }

            var state = device.State;
            if (state == DeviceState.NotPresent)
                return Failure("audio_endpoint_not_found", "LoopbackEndpointState", null, endpointId, "Endpoint not present");
            if (state != DeviceState.Active)
                return Failure("audio_endpoint_inactive", "LoopbackEndpointState", null, endpointId,
                    $"Endpoint state is {state}");

            if (device.DataFlow != DataFlow.Render)
            {
                return AudioInputOpenResult.Failure(
                    "audio_loopback_endpoint_wrong_flow",
                    $"Endpoint data flow is {device.DataFlow}, expected Render. EndpointId={endpointId}",
                    "LoopbackEndpointDataFlow",
                    captureStrategy: "wasapi-loopback");
            }

            WaveFormat mixFormat;
            try
            {
                probeClient = device.CreateAudioClient();
                mixFormat = probeClient.MixFormat;
            }
            catch (Exception ex)
            {
                return Failure("audio_endpoint_unavailable", "LoopbackMixFormat", ex, endpointId);
            }
            finally
            {
                try { probeClient?.Dispose(); } catch { }
                probeClient = null;
            }

            try
            {
                audioClientToDispose = device.CreateAudioClient();
                audioClientToDispose.Initialize(
                    AudioClientShareMode.Shared,
                    StreamFlags,
                    ReftimesPerMillisec * DefaultBufferMilliseconds,
                    0,
                    mixFormat,
                    Guid.Empty);

                captureClientToDispose = audioClientToDispose.GetAudioCaptureClient();
                var input = new AudioClientAudioInput(
                    device,
                    audioClientToDispose,
                    captureClientToDispose,
                    mixFormat,
                    DefaultBufferMilliseconds,
                    sourceKind: AudioSourceKind.SystemLoopback);

                audioClientToDispose = null;
                captureClientToDispose = null;
                device = null;
                return AudioInputOpenResult.Success(input, "not_applicable", "wasapi-loopback");
            }
            catch (Exception ex)
            {
                var code = ClassifyInitializeFailure(ex);
                return Failure(code, "LoopbackInitialize", ex, endpointId,
                    $"ShareMode=Shared StreamFlags={StreamFlags} MixFormat={FormatSummary(mixFormat)}");
            }
        }
        finally
        {
            try { captureClientToDispose?.Dispose(); } catch { }
            try { audioClientToDispose?.Dispose(); } catch { }
            try { probeClient?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        }
    }

    private static AudioInputOpenResult Failure(
        string errorCode,
        string stage,
        Exception? exception,
        string endpointId,
        string? detail = null)
    {
        var hresult = exception == null ? (int?)null : HresultFrom(exception);
        var reason = exception == null
            ? $"{detail ?? "Loopback endpoint open failed"}; EndpointId={endpointId}"
            : $"{stage} failed ({exception.GetType().Name}, HRESULT={FormatHresult(hresult!.Value)}): {exception.Message}; EndpointId={endpointId}";
        if (!string.IsNullOrEmpty(detail))
            reason += "; " + detail;

        return AudioInputOpenResult.Failure(
            errorCode,
            reason,
            stage,
            hresult,
            captureStrategy: "wasapi-loopback");
    }

    private static string ClassifyInitializeFailure(Exception ex)
    {
        var hresult = HresultFrom(ex);
        if (hresult == unchecked((int)0x80070057) ||
            ex.Message.Contains("expected range", StringComparison.OrdinalIgnoreCase))
            return "audio_format_negotiation_failure";

        if (ex.Message.Contains("format", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
            return "audio_format_unsupported";

        if (ex.Message.Contains("activate", StringComparison.OrdinalIgnoreCase) ||
            hresult == unchecked((int)0x80070490))
            return "audio_endpoint_unavailable";

        return "audio_capture_start_failed";
    }

    private static int HresultFrom(Exception ex)
        => ex is COMException comException ? comException.HResult : ex.HResult;

    private static string FormatHresult(int hresult)
        => $"0x{hresult.ToString("X8", CultureInfo.InvariantCulture)}";

    private static string FormatSummary(WaveFormat format)
        => $"{format.Encoding}/{format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit/block={format.BlockAlign}";
}
