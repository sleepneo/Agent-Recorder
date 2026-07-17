namespace AgentRecorder.Infrastructure;

/// <summary>
/// Outcome of attempting to consume a one-time ensure context. Values are
/// limited to a stable enum and are safe to write to performance traces.
/// </summary>
public enum EnsureContextStatus
{
    Consumed,
    Missing,
    Invalid,
    Expired,
    InstanceMismatch,
    Reused,
    Unavailable
}
