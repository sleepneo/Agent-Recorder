namespace AgentRecorder.Infrastructure;

/// <summary>
/// Creates and consumes short-lived ensure-running context files under a fixed
/// data-dir directory. Implementations must be thread-safe and must never treat
/// a context ID as a file path.
/// </summary>
public interface IEnsureContextStore
{
    /// <summary>
    /// Directory where context files are stored.
    /// </summary>
    string ContextDirectory { get; }

    /// <summary>
    /// Atomically writes a new context file. Returns the validated context ID,
    /// or null if the write failed. Failures are diagnostic only and must not
    /// make the calling ensure-running operation fail.
    /// </summary>
    string? TryCreate(EnsureContext context);

    /// <summary>
    /// Validates and one-time consumes the context with the given ID. On success
    /// the context is removed so subsequent calls return <see cref="EnsureContextStatus.Reused"/>.
    /// </summary>
    EnsureContextResult TryConsume(string contextId);
}
