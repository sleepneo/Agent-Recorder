namespace AgentRecorder.Capture;

/// <summary>
/// Optional capability for backends that want to receive an
/// <see cref="IMicrophoneStatusProvider"/> so they can supervise the capture
/// endpoint while a recording is active. Implementations must treat a null
/// provider as "no monitoring available" and must not fail.
/// </summary>
public interface IMicrophoneStatusConsumer
{
    IMicrophoneStatusProvider MicrophoneStatusProvider { set; }
}
