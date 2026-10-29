namespace AgentRecorder.AudioHelper;

// Input-local metadata only. Empty polls are evidence of non-delivery, never evidence of silence.
internal sealed class LoopbackIdleEvidence
{
    private readonly object _gate = new();
    private readonly List<Window> _windows = new();
    private Window? _active, _following, _pinned;
    private bool _faultPinned;
    private long[] _last = new long[] { -1, -1, -1, -1, -1 };
    private long _total;
    private int _lastFrames;
    internal LoopbackIdleTicket? RecordPacket(long sequence,long getStart,long releaseEnd,long device,long qpc,int flags,long frequency,int frames)
    {
        lock (_gate)
        {
            var w=_active;
            var ticket=w is null ? null : new LoopbackIdleTicket(this,w.Start,w.LastEmpty,getStart,w.Polls,
                Math.Max(w.MaxPollGap,getStart-w.LastEmpty),_last[0],_last[1],_last[2],_lastFrames,_last[3],(int)_last[4]);
            Packet(sequence,getStart,releaseEnd,device,qpc,flags,frequency);
            _lastFrames=frames;
            return ticket;
        }
    }
    internal Context? Capture(long inputSequence)
    {
        lock (_gate)
        {
            // Exact membership only. Prefer the latest resume containing this native packet.
            var w = _windows.Concat(_following is null ? Array.Empty<Window>() : new[] { _following })
                .Where(w => w.Packets.Any(p => p[0] == inputSequence))
                .OrderByDescending(w => w.Packets[0][0]).FirstOrDefault();
            return w is null ? null : new Context(this, w);
        }
    }
    internal void Pin(Context context)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(context.Owner, this)) throw new InvalidOperationException("Foreign idle context");
            _pinned = context.Window;
            _faultPinned = true;
            if (!_windows.Contains(_pinned)) _windows.Add(_pinned);
            while (_windows.Count > 4) _windows.RemoveAt(ReferenceEquals(_windows[0], _pinned) ? 1 : 0);
            _windows.Sort((a,b) => a.Start.CompareTo(b.Start));
        }
    }
    internal sealed record Context(LoopbackIdleEvidence Owner, Window Window)
    { internal long Start => Window.Start; }
    internal void Empty(long ticks)
    {
        lock (_gate)
        {
            if (_active is null)
            {
                _active = new Window(ticks, (long[])_last.Clone());
                _windows.Add(_active); _total++;
                if (_windows.Count > 4)
                    _windows.RemoveAt(ReferenceEquals(_windows[0], _pinned) ? 1 : 0);
            }
            _active.MaxPollGap=Math.Max(_active.MaxPollGap,ticks-_active.LastEmpty);
            _active.LastEmpty = ticks; _active.Polls++;
        }
    }
    internal void Packet(long sequence, long getStart, long releaseEnd, long device, long qpc, int flags, long frequency)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                _active.End = getStart; _active.Reason = 1;
                bool longWindow = getStart - _active.Start >= frequency / 10;
                if (_pinned is null && longWindow) _pinned = _active;
                _active.Packets.Add(new[] { sequence, getStart, releaseEnd, device, qpc, (long)flags });
                if (longWindow || _following is null) _following = _active;
                _active = null;
            }
            if (_following is not null && _following.Packets.Count < 4 &&
                (_following.Packets.Count == 0 || _following.Packets[^1][0] != sequence))
                _following.Packets.Add(new[] { sequence, getStart, releaseEnd, device, qpc, (long)flags });
            if (_faultPinned && _pinned is not null && _pinned.Packets.Count < 4 &&
                (_pinned.Packets.Count == 0 || _pinned.Packets[^1][0] != sequence))
                _pinned.Packets.Add(new[] { sequence, getStart, releaseEnd, device, qpc, (long)flags });
            _last = new[] { sequence, releaseEnd, device, qpc, (long)flags };
        }
    }
    internal void End(long ticks)
    {
        lock (_gate)
        {
            if (_active is not null) { _active.End = ticks; _active.Reason = 2; _active = null; }
        }
    }
    internal object Snapshot()
    {
        lock (_gate) return new
        {
            total_windows = _total, omitted_windows = _total - _windows.Count,
            // Times are Stopwatch ticks. End reason: 0 still open, 1 packet, 2 input exit (not silence).
            windows = _windows.Select(w => new { start_ticks = w.Start, last_empty_ticks = w.LastEmpty,
                end_ticks = w.End, empty_polls = w.Polls, end_reason = w.Reason,
                previous = (long[])w.Previous.Clone(), following = w.Packets.Select(p => (long[])p.Clone()).ToArray() }).ToArray()
        };
    }
    internal sealed class Window(long start, long[] previous)
    {
        internal long Start = start, LastEmpty = start, End = -1, Polls, MaxPollGap;
        internal int Reason;
        internal readonly long[] Previous = previous;
        internal readonly List<long[]> Packets = new(4);
    }
}
