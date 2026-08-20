using System;

namespace AgentRecorder.App;

/// <summary>
/// Capture-safe presentation seam for local Chapter Marks feedback. The domain
/// operation is complete before this presenter is called, so presentation failures
/// cannot roll back or duplicate a mark.
/// </summary>
internal interface IChapterMarkFeedbackPresenter
{
    void Show(string text, TimeSpan duration, string? preferredRecordingId = null);
}

/// <summary>
/// Presents the transient status inside the existing recording indicator layer.
/// This deliberately does not use NotifyIcon balloons or shell notifications.
/// </summary>
internal sealed class RecordingIndicatorFeedbackPresenter : IChapterMarkFeedbackPresenter
{
    private readonly RecordingIndicatorManager _indicatorManager;

    public RecordingIndicatorFeedbackPresenter(RecordingIndicatorManager indicatorManager)
    {
        _indicatorManager = indicatorManager;
    }

    public void Show(string text, TimeSpan duration, string? preferredRecordingId = null) =>
        _indicatorManager.ShowChapterMarkFeedback(text, duration, preferredRecordingId);
}
