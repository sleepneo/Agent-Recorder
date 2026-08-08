namespace AgentRecorder.Infrastructure;

/// <summary>
/// Optional tray-host capability for showing a localized recording failure
/// after the active recording UI has been torn down.
/// </summary>
public interface IRecordingFailureNotifier
{
    /// <summary>
    /// Requests one local notification for a terminal recording failure.
    /// Both values are stable, bounded identifiers; localized text is owned by
    /// the tray host and never crosses this boundary.
    /// </summary>
    void ShowRecordingFailure(string recordingId, string reasonCode);
}
