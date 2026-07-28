namespace AgentRecorder.Capture;

/// <summary>
/// Factory for creating the audio and video workers used by the split A/V backend.
/// Production uses real FFmpeg workers; tests inject lightweight fakes.
/// </summary>
public interface IAvWorkerFactory
{
    IAudioCaptureWorker CreateAudioWorker();
    IVideoCaptureWorker CreateVideoWorker();
}
