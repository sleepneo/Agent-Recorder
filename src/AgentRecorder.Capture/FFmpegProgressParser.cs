using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgentRecorder.Capture;

/// <summary>
/// Parses FFmpeg -progress output into complete groups terminated by a
/// progress= line. The parser publishes every complete group; callers that
/// need exactly-once first-frame notifications must enforce that at their
/// own lifecycle boundary.
/// </summary>
public sealed class FFmpegProgressParser
{
    private readonly Dictionary<string, string> _currentGroup = new();
    private readonly object _lock = new();
    private bool _hasProgressKey;
    /// <summary>
    /// Raised for completed progress groups. Each published group contains at
    /// least a progress= key and any parsed numeric fields.
    /// </summary>
    public event Action<FFmpegProgressGroup>? GroupCompleted;

    /// <summary>
    /// Feeds a single line of -progress output. Empty lines and unknown fields
    /// are ignored. Parsing errors are swallowed.
    /// </summary>
    public void FeedLine(string? line)
    {
        if (line == null)
        {
            Flush();
            return;
        }

        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Flush();
            return;
        }

        var eq = trimmed.IndexOf('=');
        if (eq < 0)
            return;

        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();

        // A progress= line terminates the current group. The progress value
        // itself belongs to the group being completed, so add it before flushing.
        if (string.Equals(key, "progress", StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                _currentGroup[key] = value;
                _hasProgressKey = true;
            }

            Flush();
        }
        else
        {
            lock (_lock)
            {
                _currentGroup[key] = value;
            }
        }
    }

    /// <summary>
    /// Feeds raw text, splitting on line boundaries.
    /// </summary>
    public void FeedText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
            FeedLine(line);

        // FFmpeg may omit the trailing newline; flush any final group.
        Flush();
    }

    /// <summary>
    /// Completes the current group if it has a progress= key.
    /// </summary>
    public void Flush()
    {
        Dictionary<string, string> group;
        bool hasProgress;
        lock (_lock)
        {
            hasProgress = _hasProgressKey;
            if (!hasProgress || _currentGroup.Count == 0)
            {
                _currentGroup.Clear();
                _hasProgressKey = false;
                return;
            }

            group = new Dictionary<string, string>(_currentGroup, StringComparer.OrdinalIgnoreCase);
            _currentGroup.Clear();
            _hasProgressKey = false;
        }

        try
        {
            var parsed = new FFmpegProgressGroup(group);

            GroupCompleted?.Invoke(parsed);
        }
        catch
        {
            // Parser observers must not affect the recording process.
        }
    }
}

/// <summary>
/// A completed FFmpeg progress group. Only the numeric fields required for
/// first-frame evidence are exposed; unknown fields are ignored.
/// </summary>
public sealed class FFmpegProgressGroup
{
    private readonly Dictionary<string, string> _values;

    internal FFmpegProgressGroup(Dictionary<string, string> values)
    {
        _values = values;
    }

    /// <summary>progress=continue or progress=end.</summary>
    public string Progress => _values.TryGetValue("progress", out var v) ? v : "";

    /// <summary>frame=N, parsed as non-negative long.</summary>
    public long Frame => ParseLong("frame");

    /// <summary>total_size=N, parsed as non-negative long.</summary>
    public long TotalSize => ParseLong("total_size");

    /// <summary>out_time_us=N, parsed as non-negative long if present.</summary>
    public long? OutTimeUs => ParseNullableLong("out_time_us");

    /// <summary>
    /// True when this group represents a normal progress report
    /// (continue or end) with at least one frame and positive output bytes.
    /// </summary>
    public bool HasFirstFrameEvidence =>
        IsCompletedProgress && Frame >= 1 && TotalSize > 0;

    private bool IsCompletedProgress =>
        string.Equals(Progress, "continue", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Progress, "end", StringComparison.OrdinalIgnoreCase);

    private long ParseLong(string key)
    {
        if (_values.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
            v >= 0)
        {
            return v;
        }
        return 0;
    }

    private long? ParseNullableLong(string key)
    {
        if (_values.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
            v >= 0)
        {
            return v;
        }
        return null;
    }
}
