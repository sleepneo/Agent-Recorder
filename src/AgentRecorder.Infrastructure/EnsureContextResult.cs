using System;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Result of consuming an ensure context. On success, carries the cold/warm
/// fields that can be associated with a performance trace. On failure, only
/// the status is set; no trusted cold/warm fields are produced.
/// </summary>
public sealed class EnsureContextResult
{
    public EnsureContextStatus Status { get; init; }

    /// <summary>
    /// Context ID that was consumed, or null if the ID was missing/invalid.
    /// </summary>
    public string? EnsureContextId { get; init; }

    /// <summary>
    /// <c>cold</c> or <c>warm</c>. Null when <see cref="Status"/> is not
    /// <see cref="EnsureContextStatus.Consumed"/>.
    /// </summary>
    public string? StartupKind { get; init; }

    /// <summary>
    /// Wall-clock time of the ensure-running handshake. Null when not consumed.
    /// </summary>
    public long? EnsureElapsedMs { get; init; }

    /// <summary>
    /// Service startup elapsed time recorded in ready.json. For warm starts this
    /// is the original cold-start value, not the warm handshake time. Null when
    /// not consumed.
    /// </summary>
    public long? ServiceStartupElapsedMs { get; init; }

    /// <summary>
    /// UTC creation time of the context file. Useful for diagnostics but not a
    /// trusted metric. Null when not consumed.
    /// </summary>
    public DateTime? CreatedAtUtc { get; init; }

    public static EnsureContextResult Consumed(EnsureContext ctx) => new()
    {
        Status = EnsureContextStatus.Consumed,
        EnsureContextId = ctx.EnsureContextId,
        StartupKind = ctx.StartupKind,
        EnsureElapsedMs = ctx.EnsureElapsedMs,
        ServiceStartupElapsedMs = ctx.ServiceStartupElapsedMs,
        CreatedAtUtc = ctx.CreatedAtUtc
    };

    public static EnsureContextResult Failed(EnsureContextStatus status, string? ensureContextId = null) => new()
    {
        Status = status,
        EnsureContextId = ensureContextId
    };
}
