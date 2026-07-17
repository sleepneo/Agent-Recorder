using System;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Serializable snapshot created by the CLI after a successful ensure-running
/// handshake. Stored under <c>&lt;data-dir&gt;\runtime\ensure-contexts</c> and
/// consumed by the server on the next authenticated recording request.
/// </summary>
public sealed class EnsureContext
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("ensure_context_id")]
    public string EnsureContextId { get; init; } = "";

    [JsonPropertyName("service_pid")]
    public int ServicePid { get; init; }

    [JsonPropertyName("service_started_at")]
    public string ServiceStartedAt { get; init; } = "";

    [JsonPropertyName("service_ready_at")]
    public string ServiceReadyAt { get; init; } = "";

    [JsonPropertyName("startup_kind")]
    public string StartupKind { get; init; } = "";

    [JsonPropertyName("ensure_elapsed_ms")]
    public long EnsureElapsedMs { get; init; }

    [JsonPropertyName("service_startup_elapsed_ms")]
    public long ServiceStartupElapsedMs { get; init; }

    [JsonPropertyName("created_at_utc")]
    public DateTime CreatedAtUtc { get; init; }
}
