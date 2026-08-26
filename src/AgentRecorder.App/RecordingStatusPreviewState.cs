namespace AgentRecorder.App;

/// <summary>
/// Testable boundary for the Debug recording-status preview. The counts come
/// from the manager's real forms, not from a mock drawing surface.
/// </summary>
internal readonly record struct RecordingStatusPreviewWindowCounts(
    int IndicatorCount,
    int StopControlCount)
{
    public bool Matches(bool nested)
        => this == RecordingStatusPreviewState.Expected(nested);
}

internal static class RecordingStatusPreviewState
{
    public static RecordingStatusPreviewWindowCounts Expected(bool nested)
        => nested
            ? new RecordingStatusPreviewWindowCounts(2, 2)
            : new RecordingStatusPreviewWindowCounts(1, 1);

    public static RecordingStatusPreviewWindowCounts Capture(RecordingIndicatorManager manager)
        => new(manager.IndicatorsForTests.Count, manager.StopControlsForTests.Count);

    public static string Describe(
        bool nested,
        bool finalizing,
        bool motion,
        RecordingStatusPreviewWindowCounts counts)
    {
        var mode = nested ? "Nested: OUTER + INNER" : "Ordinary: single recording";
        var phase = finalizing ? "Finalizing (static neutral gray)" : "Recording (REC breathing when enabled)";
        var motionState = motion ? "motion preference: enabled" : "motion preference: disabled";
        return $"REAL PRODUCTION WINDOWS\r\n{mode}\r\n{phase}\r\n{motionState}\r\n" +
               $"manager forms: {counts.IndicatorCount} indicator + {counts.StopControlCount} stop control\r\n" +
               "The borders, labels, capsules, placement and collision behavior are owned by the real forms.";
    }
}
