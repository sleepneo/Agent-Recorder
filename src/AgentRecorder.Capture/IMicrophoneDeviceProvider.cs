using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Injectable provider that enumerates microphone input devices without opening
/// any capture stream. Implementations must be safe to call before user consent.
/// </summary>
public interface IMicrophoneDeviceProvider
{
    /// <summary>
    /// Returns the list of available microphone input devices.
    /// The call must not open an audio capture stream or hold a device handle.
    /// </summary>
    Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);
}
