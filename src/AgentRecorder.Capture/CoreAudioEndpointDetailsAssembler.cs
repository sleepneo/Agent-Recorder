using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Internal test seam that assembles a <see cref="CoreAudioEndpointDetails"/>
/// from a raw endpoint state and an optional volume interface object.
/// It is intentionally free of COM creation/release logic; production callers
/// own the lifetime of the volume object, and tests can pass a fake object
/// to exercise every partial-result branch deterministically.
/// </summary>
internal static class CoreAudioEndpointDetailsAssembler
{
    internal static CoreAudioEndpointDetails Assemble(
        DeviceState? rawState,
        object? volume,
        Func<object, bool?> readMute,
        Func<object, int?> readVolumePercent)
    {
        string? stateName = rawState.HasValue
            ? rawState.Value == DeviceState.Active ? "active" : "inactive"
            : null;

        if (volume == null)
            return new CoreAudioEndpointDetails(null, stateName, null, null);

        bool? muted = null;
        try
        {
            muted = readMute(volume);
        }
        catch
        {
            muted = null;
        }

        int? percent = null;
        try
        {
            percent = readVolumePercent(volume);
        }
        catch
        {
            percent = null;
        }

        return new CoreAudioEndpointDetails(null, stateName, muted, percent);
    }
}
