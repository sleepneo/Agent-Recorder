using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Provides fresh, read-only microphone status for a given FFmpeg dshow device id.
/// Implementations must not modify system volume or mute state and must treat
/// any COM/HRESULT/availability failure as "status unknown" (null values).
/// </summary>
public interface IMicrophoneStatusProvider
{
    /// <summary>
    /// Reads the current mute and volume state of the capture endpoint that
    /// corresponds to <paramref name="dshowDeviceId"/>. Returns null values
    /// when the state cannot be determined.
    /// </summary>
    Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default);
}
