namespace AgentRecorder.Capture;

/// <summary>
/// Exposes the terminal summary of an audio helper session when the worker
/// implementation is backed by the audio-helper-v1 protocol. This is separated
/// from <see cref="IAudioCaptureWorker"/> so that test doubles can provide a
/// deterministic summary without running a real helper process.
/// </summary>
internal interface IAudioHelperSummaryProvider
{
    AudioHelperSessionSummary? GetTerminalSummary();
}
