using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentRecorder.Capture;

/// <summary>
/// Parses FFmpeg silencedetect stderr markers into contiguous silence intervals
/// and classifies them as initial, trailing, or internal.
/// </summary>
public static class SilenceIntervalParser
{
    // Example: [silencedetect @ 000001f8b6f9ee00] silence_start: 0.5
    private static readonly Regex SilenceStartRegex = new(
        @"silencedetect\s+@\s+\S+\]\s*silence_start:\s*(?<start>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Example: [silencedetect @ 000001f8b6f9ee00] silence_end: 3.5 | silence_duration: 3
    private static readonly Regex SilenceEndRegex = new(
        @"silencedetect\s+@\s+\S+\]\s*silence_end:\s*(?<end>[0-9]+(?:\.[0-9]+)?)\s*\|\s*silence_duration:\s*(?<dur>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the stderr log and returns the list of silence intervals in
    /// chronological order. Unmatched start/end pairs are ignored.
    /// </summary>
    public static IReadOnlyList<SilenceInterval> Parse(string stderr)
    {
        var intervals = new List<SilenceInterval>();
        if (string.IsNullOrWhiteSpace(stderr))
            return intervals;

        double? pendingStart = null;
        foreach (var rawLine in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var startMatch = SilenceStartRegex.Match(line);
            if (startMatch.Success)
            {
                pendingStart = ParseDouble(startMatch.Groups["start"].Value);
                continue;
            }

            var endMatch = SilenceEndRegex.Match(line);
            if (endMatch.Success && pendingStart.HasValue)
            {
                var end = ParseDouble(endMatch.Groups["end"].Value);
                var duration = ParseDouble(endMatch.Groups["dur"].Value);
                intervals.Add(new SilenceInterval(pendingStart.Value, end, duration));
                pendingStart = null;
            }
        }

        return intervals;
    }

    /// <summary>
    /// Classifies parsed intervals. Initial silence starts at or near 0;
    /// trailing silence reaches or exceeds the reported total duration;
    /// internal silence is any remaining interval whose duration is at least
    /// the supplied threshold.
    /// </summary>
    public static SilenceClassification Classify(
        IReadOnlyList<SilenceInterval> intervals,
        double totalDurationSeconds,
        double internalThresholdSeconds)
    {
        const double edgeTolerance = 0.1;

        var initial = new List<SilenceInterval>();
        var trailing = new List<SilenceInterval>();
        var internalSilence = new List<SilenceInterval>();

        foreach (var interval in intervals)
        {
            bool isInitial = interval.Start <= edgeTolerance;
            bool isTrailing = totalDurationSeconds > 0 &&
                              (interval.End >= totalDurationSeconds - edgeTolerance ||
                               interval.End >= totalDurationSeconds);

            if (isInitial)
                initial.Add(interval);
            else if (isTrailing)
                trailing.Add(interval);
            else if (interval.Duration >= internalThresholdSeconds)
                internalSilence.Add(interval);
        }

        return new SilenceClassification(initial, trailing, internalSilence);
    }

    /// <summary>
    /// Convenience helper that parses and classifies in one call.
    /// </summary>
    public static SilenceClassification ParseAndClassify(
        string stderr,
        double totalDurationSeconds,
        double internalThresholdSeconds)
    {
        var intervals = Parse(stderr);
        return Classify(intervals, totalDurationSeconds, internalThresholdSeconds);
    }

    private static double ParseDouble(string value)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        return 0;
    }
}

/// <summary>
/// A contiguous silence interval reported by silencedetect.
/// </summary>
public sealed record SilenceInterval(double Start, double End, double Duration)
{
    public double Start { get; } = Start;
    public double End { get; } = End;
    public double Duration { get; } = Duration;
}

/// <summary>
/// Classification result for silence intervals.
/// </summary>
public sealed record SilenceClassification(
    IReadOnlyList<SilenceInterval> Initial,
    IReadOnlyList<SilenceInterval> Trailing,
    IReadOnlyList<SilenceInterval> Internal)
{
    public bool HasInternalSilence => Internal.Count > 0;
    public double LongestInternalSeconds => Internal.Count > 0 ? Internal.Max(i => i.Duration) : 0;
}
