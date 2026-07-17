using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// A single performance trace event. One JSON object per line in
/// <c>&lt;data-dir&gt;\perf\recording-traces.jsonl</c>.
/// </summary>
public sealed class PerformanceTraceEvent
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("trace_id")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("timestamp_utc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("elapsed_from_intent_ms")]
    public double ElapsedFromIntentMs { get; set; }

    [JsonPropertyName("recording_id")]
    public string? RecordingId { get; set; }

    [JsonPropertyName("confirmation_id")]
    public string? ConfirmationId { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; set; }

    [JsonPropertyName("backend")]
    public string? Backend { get; set; }

    [JsonPropertyName("client_hints")]
    public Dictionary<string, object?>? ClientHints { get; set; }

    /// <summary>
    /// Ensure-running startup association. Only present when the server
    /// successfully consumed a one-time ensure context for this trace.
    /// </summary>
    [JsonPropertyName("startup_kind")]
    public string? StartupKind { get; set; }

    [JsonPropertyName("ensure_elapsed_ms")]
    public long? EnsureElapsedMs { get; set; }

    [JsonPropertyName("service_startup_elapsed_ms")]
    public long? ServiceStartupElapsedMs { get; set; }

    [JsonPropertyName("ensure_context_status")]
    public string? EnsureContextStatus { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object?>? Data { get; set; }
}
