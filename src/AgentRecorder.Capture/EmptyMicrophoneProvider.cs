using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Default microphone provider that returns no devices. Used as a safe fallback
/// when no production provider is injected.
/// </summary>
public sealed class EmptyMicrophoneProvider : IMicrophoneDeviceProvider
{
    public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(new List<MicrophoneDeviceInfo>());
}
