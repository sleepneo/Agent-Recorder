using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Statistical summary of recent recording-performance traces, exposed through
/// <c>/api/v1/capabilities</c> as <c>perf_summary</c>. All numeric latencies
/// are in milliseconds.
/// </summary>
public sealed class PerformanceSummary
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// One of: <c>available</c>, <c>no_data</c>, <c>degraded</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = PerformanceSummaryStatus.NoData;

    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; }

    [JsonPropertyName("window")]
    public PerformanceSummaryWindow Window { get; set; } = new();

    [JsonPropertyName("quality")]
    public PerformanceSummaryQuality Quality { get; set; } = new();

    /// <summary>
    /// Always contains both <c>cold</c> and <c>warm</c> keys, even when one
    /// group has no qualifying traces.
    /// </summary>
    [JsonPropertyName("groups")]
    public Dictionary<string, PerformanceSummaryGroup> Groups { get; set; } = new()
    {
        [PerformanceSummaryGroups.Cold] = new PerformanceSummaryGroup(),
        [PerformanceSummaryGroups.Warm] = new PerformanceSummaryGroup()
    };

    /// <summary>
    /// Returns a stable no-data summary. Used when files are missing, empty,
    /// or no qualifying cold/warm trace exists.
    /// </summary>
    public static PerformanceSummary NoData(DateTime generatedAt, int maxTracesPerGroup,
        PerformanceSummaryQuality? quality = null) => new()
    {
        SchemaVersion = 1,
        Status = PerformanceSummaryStatus.NoData,
        GeneratedAt = generatedAt,
        Window = new PerformanceSummaryWindow { MaxTracesPerGroup = maxTracesPerGroup },
        Quality = quality ?? new PerformanceSummaryQuality(),
        Groups = new Dictionary<string, PerformanceSummaryGroup>
        {
            [PerformanceSummaryGroups.Cold] = new PerformanceSummaryGroup(),
            [PerformanceSummaryGroups.Warm] = new PerformanceSummaryGroup()
        }
    };
}

public static class PerformanceSummaryStatus
{
    public const string Available = "available";
    public const string NoData = "no_data";
    public const string Degraded = "degraded";
}

public static class PerformanceSummaryGroups
{
    public const string Cold = "cold";
    public const string Warm = "warm";
}

public static class PerformanceSummaryQualityLabels
{
    public const string Preliminary = "preliminary";
    public const string Representative = "representative";
}

public sealed class PerformanceSummaryWindow
{
    [JsonPropertyName("max_traces_per_group")]
    public int MaxTracesPerGroup { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "local_rolling_jsonl";
}

public sealed class PerformanceSummaryQuality
{
    [JsonPropertyName("malformed_line_count")]
    public int MalformedLineCount { get; set; }

    [JsonPropertyName("unsupported_schema_count")]
    public int UnsupportedSchemaCount { get; set; }

    [JsonPropertyName("discarded_sample_count")]
    public int DiscardedSampleCount { get; set; }

    [JsonPropertyName("unclassified_trace_count")]
    public int UnclassifiedTraceCount { get; set; }

    /// <summary>
    /// Stable machine-readable reason when <see cref="PerformanceSummary.Status"/>
    /// is <c>degraded</c>. Never contains file paths, exception text, IDs, or
    /// user content.
    /// </summary>
    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }
}

public sealed class PerformanceSummaryGroup
{
    [JsonPropertyName("trace_count")]
    public int TraceCount { get; set; }

    /// <summary>
    /// One of <c>preliminary</c> or <c>representative</c>. This is a data-quality
    /// label, not a performance SLO.
    /// </summary>
    [JsonPropertyName("quality")]
    public string Quality { get; set; } = PerformanceSummaryQualityLabels.Preliminary;

    [JsonPropertyName("metrics")]
    public Dictionary<string, PerformanceSummaryMetric> Metrics { get; set; } = new();
}

public sealed class PerformanceSummaryMetric
{
    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("p50")]
    public double P50 { get; set; }

    [JsonPropertyName("p95")]
    public double P95 { get; set; }
}
