namespace AgentRecorder.Core;

/// <summary>
/// The process-local linearization point shared by the standing backend-start
/// boundary and durable unattended safety controls. Callers must keep the
/// critical section synchronous: no await is permitted while it is held.
/// Physical recording teardown is intentionally outside this boundary.
/// </summary>
internal sealed class StandingLeaseStartSafetyInterlock
{
    private readonly object _sync = new();

    internal T Execute<T>(string operation, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            return action();
        }
    }

    internal void Execute(string operation, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            action();
        }
    }
}
