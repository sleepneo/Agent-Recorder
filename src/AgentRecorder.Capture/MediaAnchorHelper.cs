using System;
using System.Diagnostics;

namespace AgentRecorder.Capture;

/// <summary>
/// Pure helper for converting monotonic Stopwatch timestamps into media-time
/// estimates. Keeps the conversion logic in one place so it can be unit-tested
/// without launching FFmpeg.
/// </summary>
internal static class MediaAnchorHelper
{
    /// <summary>
    /// Estimates the media-time zero of a stream from the moment a progress
    /// notification arrived and the stream duration reported in that progress.
    /// </summary>
    public static long EstimateMediaStartAnchor(long observedStopwatchTicks, long outTimeUs)
    {
        var outTimeSeconds = outTimeUs / 1_000_000.0;
        var outTimeTicks = (long)(outTimeSeconds * Stopwatch.Frequency);
        return observedStopwatchTicks - outTimeTicks;
    }

    /// <summary>
    /// Estimates the media-time zero only when the progress contains a credible
    /// positive <paramref name="outTimeUs"/>. Returns false for missing, zero,
    /// or negative values so callers can keep the anchor unset instead of
    /// silently treating the observation time as the media zero.
    /// </summary>
    public static bool TryEstimateMediaStartAnchor(long observedStopwatchTicks, long? outTimeUs, out long anchorTicks)
    {
        if (!outTimeUs.HasValue || outTimeUs.Value <= 0)
        {
            anchorTicks = 0;
            return false;
        }

        anchorTicks = EstimateMediaStartAnchor(observedStopwatchTicks, outTimeUs.Value);
        return true;
    }

    /// <summary>
    /// Converts Stopwatch ticks into a <see cref="TimeSpan"/> using the current
    /// machine's Stopwatch frequency. Do not use <see cref="TimeSpan.FromTicks"/>
    /// because Stopwatch ticks and TimeSpan ticks have independent scales.
    /// </summary>
    public static TimeSpan ToTimeSpan(long stopwatchTicks)
    {
        return TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency);
    }
}
