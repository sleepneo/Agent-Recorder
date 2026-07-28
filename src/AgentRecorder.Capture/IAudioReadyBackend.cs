namespace AgentRecorder.Capture;

/// <summary>
/// Optional capability for backends that can signal when an audio input is ready
/// before video capture begins. Used to drive the preparing/countdown lifecycle.
/// </summary>
public interface IAudioReadyBackend
{
    /// <summary>
    /// Raised once when the audio input has produced credible samples and is
    /// ready for video capture to begin.
    /// </summary>
    event Action? AudioReady;

    /// <summary>
    /// True when audio is already ready at the time of inspection. Used to
    /// avoid missing a race where AudioReady fired before the subscription.
    /// </summary>
    bool IsAudioReady { get; }

    /// <summary>
    /// Starts video capture after the audio-ready / countdown phase.
    /// </summary>
    void StartVideo();
}
