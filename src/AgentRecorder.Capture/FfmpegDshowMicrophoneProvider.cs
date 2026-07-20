using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Enumerates microphone devices using FFmpeg's dshow device list.
/// Runs <c>ffmpeg -list_devices true -f dshow -i dummy</c> with a bounded timeout,
/// captures a limited stderr excerpt, and parses the "DirectShow audio devices" section.
/// </summary>
public sealed class FfmpegDshowMicrophoneProvider : IMicrophoneDeviceProvider
{
    private readonly IExternalProcessRunner _runner;
    private readonly Func<string> _ffmpegPathProvider;
    private readonly TimeSpan _timeout;

    public FfmpegDshowMicrophoneProvider(
        IExternalProcessRunner? runner = null,
        Func<string>? ffmpegPathProvider = null,
        TimeSpan? timeout = null)
    {
        _runner = runner ?? new ExternalProcessRunner();
        _ffmpegPathProvider = ffmpegPathProvider ?? (() => FfmpegLocator.FfmpegPath);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var ffmpegPath = _ffmpegPathProvider();
        var args = new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" };

        ExternalProcessResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            result = await _runner.RunAsync(ffmpegPath, args, _timeout, captureStderr: true,
                stderrEncoding: Encoding.UTF8, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MicrophoneEnumerationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MicrophoneEnumerationException("device_enumeration_unavailable",
                "Microphone device enumeration failed.");
        }

        // Honor external cancellation even if the runner completed or timed out.
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        // FFmpeg returns exit code 1 for -list_devices because no output file is specified.
        // A timeout or a non-listing failure is reported differently.
        if (result.TimedOut)
            throw new MicrophoneEnumerationException("device_enumeration_timeout", "Microphone device enumeration timed out.");

        // Unexpected exit codes mean the listing command did not complete in the
        // normal "no output file" way; treat as unavailable rather than no_devices.
        if (result.ExitCode != 1)
            throw new MicrophoneEnumerationException("device_enumeration_unavailable", "Microphone device enumeration failed.");

        // Only trust the output when the well-known section header is present.
        // Missing section indicates truncation or an output format we cannot classify.
        if (string.IsNullOrWhiteSpace(result.Stderr) ||
            !result.Stderr.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            throw new MicrophoneEnumerationException("device_enumeration_unavailable", "Microphone device enumeration failed.");

        return DshowAudioDeviceParser.Parse(result.Stderr);
    }
}

/// <summary>
/// Thrown when microphone device enumeration fails in a way that should be surfaced
/// to the API as a stable error code.
/// </summary>
public sealed class MicrophoneEnumerationException : Exception
{
    public string ErrorCode { get; }

    public MicrophoneEnumerationException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Parses FFmpeg dshow stderr to extract audio input devices.
/// </summary>
internal static class DshowAudioDeviceParser
{
    public static IReadOnlyList<MicrophoneDeviceInfo> Parse(string stderr)
    {
        var devices = new List<MicrophoneDeviceInfo>();
        if (string.IsNullOrWhiteSpace(stderr))
            return devices;

        var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool inAudioSection = false;
        string? pendingFriendlyName = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (!inAudioSection)
            {
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
                {
                    inAudioSection = true;
                    pendingFriendlyName = null;
                }
                continue;
            }

            // End of audio section when the next major section header appears.
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("dummy", StringComparison.OrdinalIgnoreCase) && line.Contains("Input #", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Alternative name line follows the friendly name line.
            if (pendingFriendlyName != null && line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                var alternative = ExtractQuoted(line);
                if (alternative != null)
                {
                    // FFmpeg dshow cannot reliably report the Windows CoreAudio default
                    // endpoint or device state. Report them as unknown so callers do not
                    // treat "unverified" as "active" or "not default".
                    devices.Add(new MicrophoneDeviceInfo(alternative, pendingFriendlyName, null, null));
                    pendingFriendlyName = null;
                }
                continue;
            }

            // Friendly name lines contain a quoted name and are not alternative-name lines.
            var friendly = ExtractQuoted(line);
            if (friendly != null)
            {
                pendingFriendlyName = friendly;
                continue;
            }
        }

        return Deduplicate(devices);
    }

    private static string? ExtractQuoted(string line)
    {
        int first = line.IndexOf('"');
        if (first < 0) return null;
        int last = line.IndexOf('"', first + 1);
        if (last <= first) return null;
        return line.Substring(first + 1, last - first - 1);
    }

    private static IReadOnlyList<MicrophoneDeviceInfo> Deduplicate(List<MicrophoneDeviceInfo> devices)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<MicrophoneDeviceInfo>();

        foreach (var d in devices)
        {
            // IDs are FFmpeg dshow alternative names and must remain stable so
            // subsequent recording requests can open the same device. They are
            // expected to be unique; if not, keep the first occurrence defensively.
            if (!seenIds.Add(d.Id))
                continue;

            // Duplicate display names are common (e.g. "Microphone"). Disambiguate
            // the user-visible name without changing the underlying device ID.
            var originalName = d.Name;
            seenNames.TryGetValue(originalName, out var count);
            count++;
            seenNames[originalName] = count;
            var name = count == 1 ? originalName : $"{originalName} ({count})";

            result.Add(d with { Name = name });
        }

        return result;
    }
}
