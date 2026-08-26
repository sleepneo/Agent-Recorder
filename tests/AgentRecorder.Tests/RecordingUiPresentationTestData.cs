using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

internal static class RecordingUiPresentationTestData
{
    public static RecordingUiPresentation FromRecording(
        Recording recording,
        RecordingUiState state = RecordingUiState.Recording,
        int? countdownRemainingSeconds = null,
        int? seriesCapturedFrameCount = null,
        int? seriesPlannedFrameCount = null,
        DateTime? seriesNextCaptureDueAtUtc = null)
    {
        lock (recording)
        {
            var bounds = recording.Config.Bounds;
            return new RecordingUiPresentation
            {
                RecordingId = recording.Id,
                State = state,
                SourceType = recording.SourceType,
                CaptureBounds = new RecordingUiBounds(bounds.x, bounds.y, bounds.w, bounds.h),
                DurationSeconds = recording.DurationSeconds,
                StartedAtUtc = recording.StartedAtUtc,
                IsScreenshotSeries = recording.IsScreenshotSeries,
                SeriesCapturedFrameCount = seriesCapturedFrameCount,
                SeriesPlannedFrameCount = seriesPlannedFrameCount ?? recording.ScreenshotSeries?.PlannedFrameCount,
                SeriesNextCaptureDueAtUtc = seriesNextCaptureDueAtUtc,
                CountdownRemainingSeconds = countdownRemainingSeconds,
                NestedRole = recording.NestedRole,
                ParentRecordingId = recording.ParentRecordingId,
                NestedSessionId = recording.NestedSessionId
            };
        }
    }
}
