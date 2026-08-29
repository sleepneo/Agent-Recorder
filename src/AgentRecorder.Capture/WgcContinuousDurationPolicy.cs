namespace AgentRecorder.Capture;

/// <summary>
/// Shared managed duration contract for the controlled WGC continuous path.
/// The native helper mirrors the same millisecond bounds in its CLI policy.
/// </summary>
internal static class WgcContinuousDurationPolicy
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 60;
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
                $"WGC continuous duration must be between {MinSeconds} and {MaxSeconds} seconds.");

        return checked(durationSeconds * MillisecondsPerSecond);
    }
}
