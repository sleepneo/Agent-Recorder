using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Read-only microphone status provider backed by Windows CoreAudio.
/// Maps an FFmpeg dshow device id (wave_&#123;GUID&#125;) to the corresponding
/// CoreAudio capture endpoint id (&#123;0.0.1.00000000&#125;.&#123;GUID&#125;) and reads the
/// endpoint state, default-ness, mute state and master volume scalar.
/// All COM/HRESULT/availability failures are swallowed and reported as
/// <c>null</c> values so callers never treat "unknown" as "muted".
/// </summary>
public sealed class CoreAudioCaptureStatusProvider : IMicrophoneStatusProvider
{
    private readonly ICoreAudioNativeClient _nativeClient;

    public CoreAudioCaptureStatusProvider(ICoreAudioNativeClient? nativeClient = null)
    {
        _nativeClient = nativeClient ?? new CoreAudioNativeClient();
    }

    public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var guid = ExtractGuidFromDshowId(dshowDeviceId);
            if (guid == null)
                return new MicrophoneStatus(null, null, null, null);

            var endpointId = $"{{0.0.1.00000000}}.{guid}";
            return ReadStatusCore(endpointId);
        }, cancellationToken);
    }

    private MicrophoneStatus ReadStatusCore(string endpointId)
    {
        // Default-endpoint lookup failure must not wipe successfully-read state,
        // mute or volume. It only means we cannot answer "is this the default?".
        string? defaultEndpointId = null;
        try
        {
            defaultEndpointId = _nativeClient.GetDefaultCaptureEndpointId();
        }
        catch
        {
            defaultEndpointId = null;
        }

        bool? isDefault = defaultEndpointId == null
            ? null
            : string.Equals(defaultEndpointId, endpointId, StringComparison.OrdinalIgnoreCase);

        CoreAudioEndpointDetails details;
        try
        {
            details = _nativeClient.GetEndpointDetails(endpointId);
        }
        catch
        {
            // Details failure must not discard the IsDefault we already know.
            return new MicrophoneStatus(null, null, isDefault, null);
        }

        return new MicrophoneStatus(
            details.IsMuted,
            details.VolumePercent,
            isDefault,
            details.State);
    }

    /// <summary>
    /// Extracts the endpoint GUID from an FFmpeg dshow alternative name.
    /// Returns <c>null</c> when the id does not contain a recognizable
    /// <c>wave_&#123;GUID&#125;</c> segment.
    /// </summary>
    internal static string? ExtractGuidFromDshowId(string dshowDeviceId)
    {
        if (string.IsNullOrWhiteSpace(dshowDeviceId))
            return null;

        const string prefix = "\\wave_";
        int index = dshowDeviceId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        int guidStart = index + prefix.Length;
        int guidEnd = dshowDeviceId.IndexOf('}', guidStart);
        if (guidEnd < 0)
            return null;

        guidEnd++; // include closing brace
        if (guidEnd <= guidStart || guidEnd > dshowDeviceId.Length)
            return null;

        var candidate = dshowDeviceId.Substring(guidStart, guidEnd - guidStart);
        return Guid.TryParse(candidate, out _) ? candidate : null;
    }

    /// <summary>
    /// Maps a dshow device id to the CoreAudio capture endpoint id format.
    /// Returns an empty string when the input cannot be mapped.
    /// </summary>
    internal static string ToCoreAudioEndpointId(string dshowDeviceId)
    {
        var guid = ExtractGuidFromDshowId(dshowDeviceId);
        return guid == null ? string.Empty : $"{{0.0.1.00000000}}.{guid}";
    }
}
