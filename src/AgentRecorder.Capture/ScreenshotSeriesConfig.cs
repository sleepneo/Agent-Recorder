using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Canonical, immutable-ish configuration for the bounded screenshot-series
/// mode. Validation is owned by ConfigParser; this type only carries the
/// already validated values through the capture pipeline.
/// </summary>
public sealed class ScreenshotSeriesConfig
{
    public const string ModeName = "screenshot_series";
    public const int MinIntervalMs = 1_000;
    public const int MaxIntervalMs = 3_600_000;
    public const int MinCount = 1;
    public const int MaxFrameCount = 300;
    public const int MinDurationSeconds = 1;
    public const int MaxDurationSecondsLimit = 86_400;

    public int IntervalMs { get; init; }
    public int? MaxCount { get; init; }
    public int? MaxDurationSeconds { get; init; }
    public int PlannedFrameCount { get; init; }

    public string BoundKind => MaxCount.HasValue ? "max_count" : "max_duration_seconds";
    public int BoundValue => MaxCount ?? MaxDurationSeconds ?? 0;

    public static int CountForDuration(int durationSeconds, int intervalMs)
    {
        long numerator = (long)durationSeconds * 1000L + intervalMs - 1L;
        return checked((int)(numerator / intervalMs));
    }
}

/// <summary>
/// A single-frame request is deliberately separate from the continuous video
/// backend. Implementations must produce exactly one PNG at the supplied temp
/// path or return a stable failure result.
/// </summary>
public sealed record ScreenshotFrameRequest(
    CaptureConfig Config,
    string TempPath,
    TimeSpan Timeout,
    int FrameIndex,
    string BackendType = "",
    string CaptureSemantics = "",
    string SourceKind = "",
    string? TargetIdentity = null,
    string CoordinateSpace = "virtual_screen");

public sealed record ScreenshotFrameResult(
    bool Success,
    string ErrorCode,
    int Width,
    int Height,
    long SizeBytes,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int ExitCode = 0);

public interface IScreenshotFrameRunner
{
    Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken);
}
