using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>Fail-closed binding for the one render endpoint approved in a future-window scope.</summary>
internal static class FutureWindowAudioEndpointPolicy
{
    internal static SystemAudioEndpointInfo? Validate(
        string? expectedEndpointId,
        string? expectedEndpointName,
        SystemAudioEndpointInfo? current,
        out string? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(expectedEndpointId)) return null;
        if (current is null)
        {
            failure = "future_window_audio_endpoint_unavailable";
            return null;
        }
        if (!string.Equals(current.Id, expectedEndpointId, StringComparison.Ordinal) ||
            (expectedEndpointName is not null &&
             !string.Equals(current.Name, expectedEndpointName, StringComparison.Ordinal)) ||
            !string.Equals(current.Direction, "render", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.State, "active", StringComparison.OrdinalIgnoreCase))
        {
            failure = "future_window_audio_endpoint_changed";
            return null;
        }
        return current;
    }
}
