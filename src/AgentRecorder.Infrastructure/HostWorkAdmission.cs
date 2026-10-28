namespace AgentRecorder.Infrastructure;

/// <summary>One process owner's work/idle-exit arbitration. No callbacks are awaited under its lock.</summary>
public sealed class HostWorkAdmission
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    private bool _closed;
    public bool IsClosed { get { lock (_gate) return _closed; } }

    public IDisposable? TryEnter(string reason)
    {
        lock (_gate)
        {
            if (_closed) return null;
            _active[reason] = _active.GetValueOrDefault(reason) + 1;
            return new Lease(this, reason);
        }
    }

    // reserveIdle must make the engine's check-and-close atomic with engine starts.
    public string? TryClose(Func<string?> reserveIdle)
    {
        lock (_gate)
        {
            if (_closed) return null;
            if (_active.Count != 0) return _active.Keys.OrderBy(x => x, StringComparer.Ordinal).First();
            var reason = reserveIdle();
            if (reason is not null) return reason;
            _closed = true;
            return null;
        }
    }

    private sealed class Lease(HostWorkAdmission owner, string reason) : IDisposable
    {
        private HostWorkAdmission? _owner = owner;
        public void Dispose()
        {
            var target = Interlocked.Exchange(ref _owner, null);
            if (target is null) return;
            lock (target._gate)
            {
                if (--target._active[reason] == 0) target._active.Remove(reason);
            }
        }
    }
}
