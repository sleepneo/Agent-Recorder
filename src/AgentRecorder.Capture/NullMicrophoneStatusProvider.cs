using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// No-op status provider used in tests and headless environments where real
/// CoreAudio state is unavailable. Always returns unknown status.
/// </summary>
public sealed class NullMicrophoneStatusProvider : IMicrophoneStatusProvider
{
    public static readonly NullMicrophoneStatusProvider Instance = new();

    public Task<MicrophoneStatus> GetStatusAsync(string dshowDeviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(new MicrophoneStatus(null, null, null, null));
}
