namespace AgentRecorder.Capture;

/// <summary>
/// Factory for creating the audio and video workers used by the split A/V backend.
/// Production uses real FFmpeg workers; tests inject lightweight fakes.
/// </summary>
public interface IAvWorkerFactory
{
    /// <summary>
    /// Legacy microphone-oriented seam. Existing fakes and callers retain the
    /// historical environment-driven behavior through this overload.
    /// </summary>
    IAudioCaptureWorker CreateAudioWorker();

    /// <summary>
    /// Creates an audio worker for the exact requested source. System loopback
    /// must never inherit the microphone backend preference.
    /// </summary>
    IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind sourceKind)
        => CreateAudioWorker();

    IVideoCaptureWorker CreateVideoWorker();
}
