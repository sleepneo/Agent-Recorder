namespace AgentRecorder.AudioHelper;

/// <summary>
/// Result of <see cref="AudioClientAudioInput.StartRecording"/>.
/// </summary>
internal enum StartRecordingResult
{
    /// <summary>The capture thread was started and the input is capturing.</summary>
    Started,

    /// <summary>The operation was cancelled by Stop/Dispose before the capture thread started.</summary>
    Cancelled,

    /// <summary>The input was disposed while starting; no capture thread was started.</summary>
    Disposed
}
