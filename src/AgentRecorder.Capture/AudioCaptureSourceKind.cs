namespace AgentRecorder.Capture;

/// <summary>
/// Strong-typed internal audio source kind for the capture layer.
/// This is distinct from AudioHelper's <c>AudioSourceKind</c> which is
/// internal to the helper process. The default <see cref="None"/> is
/// equivalent to no audio; <see cref="Microphone"/> preserves the legacy
/// Microphone=true behavior; <see cref="SystemLoopback"/> enables system
/// loopback capture through the exact render endpoint.
/// </summary>
public enum AudioCaptureSourceKind
{
    /// <summary>No audio capture requested. Default.</summary>
    None = 0,
    /// <summary>Microphone capture via the configured MicDevice/MicDeviceName.</summary>
    Microphone,
    /// <summary>System loopback capture via the exact render endpoint.</summary>
    SystemLoopback
}