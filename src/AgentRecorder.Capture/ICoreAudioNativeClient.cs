namespace AgentRecorder.Capture;

/// <summary>
/// Read-only boundary around the Windows CoreAudio COM calls used to inspect
/// a capture endpoint. Implementations must not modify mute or volume, and
/// must report any COM/HRESULT/availability failure as <c>null</c> values.
/// </summary>
public interface ICoreAudioNativeClient
{
    /// <summary>
    /// Reads the current default multimedia capture endpoint id, if available.
    /// </summary>
    string? GetDefaultCaptureEndpointId();

    /// <summary>
    /// Reads state, mute and volume for the endpoint identified by
    /// <paramref name="endpointId"/>. All values are null when the endpoint
    /// cannot be inspected.
    /// </summary>
    CoreAudioEndpointDetails GetEndpointDetails(string endpointId);
}

/// <summary>
/// Fresh read-only details for a single CoreAudio capture endpoint.
/// </summary>
/// <param name="IsDefault">True when this endpoint is the current eCapture+eMultimedia default.</param>
/// <param name="State">"active" when the endpoint is in the ACTIVE state; otherwise "inactive".</param>
/// <param name="IsMuted">True when the endpoint is software-muted.</param>
/// <param name="VolumePercent">Master volume scalar rounded to 0..100.</param>
public sealed record CoreAudioEndpointDetails(
    bool? IsDefault,
    string? State,
    bool? IsMuted,
    int? VolumePercent);
