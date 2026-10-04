namespace AgentRecorder.Capture;

/// <summary>
/// Bounded duration contract for individually confirmed strict window-surface
/// recordings. This intentionally does not change the shared WGC limit used by
/// display, region, or legacy window paths.
/// </summary>
public static class WindowSurfaceDurationPolicy
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 1800;
    public const int MillisecondsPerSecond = 1000;
    public const int MinMilliseconds = MinSeconds * MillisecondsPerSecond;
    public const int MaxMilliseconds = MaxSeconds * MillisecondsPerSecond;

    public static bool IsEligibleSeconds(int? durationSeconds) =>
        durationSeconds.HasValue &&
        durationSeconds.Value >= MinSeconds &&
        durationSeconds.Value <= MaxSeconds;

    public static bool IsEligibleMilliseconds(int durationMs) =>
        durationMs >= MinMilliseconds && durationMs <= MaxMilliseconds;

    public static int ToMilliseconds(int durationSeconds)
    {
        if (!IsEligibleSeconds(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds),
                $"Strict window-surface duration must be between {MinSeconds} and {MaxSeconds} seconds.");

        return checked(durationSeconds * MillisecondsPerSecond);
    }
}
