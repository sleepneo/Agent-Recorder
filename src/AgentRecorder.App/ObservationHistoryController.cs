using AgentRecorder.Api;
using AgentRecorder.Core;

namespace AgentRecorder.App;

internal enum ObservationHistoryStatus { Initial, Loading, Ready, Empty, InvalidCursor, Unavailable, Closed }
internal sealed record ObservationHistoryView(ObservationHistoryStatus Status,
    IReadOnlyList<UnifiedHistoryEntry> Items, string? NextCursor, UnifiedHistoryFilters Filters, long Revision);

/// <summary>One bounded request at a time. Superseding a request discards its result,
/// not its SQLite work. Only the latest pending request is retained.</summary>
internal sealed class ObservationHistoryController : IDisposable
{
    private readonly object _gate = new();
    private readonly IUnifiedHistoryGateway _gateway;
    private readonly Action<Action> _dispatch;
    private ObservationHistoryView _view = new(ObservationHistoryStatus.Initial, Array.Empty<UnifiedHistoryEntry>(), null, new(), 0);
    private Request? _pending;
    private bool _running, _closed;
    private Task _idle = Task.CompletedTask;
    internal event Action? Changed;
    internal ObservationHistoryController(IUnifiedHistoryGateway gateway, Action<Action> dispatch)
    { _gateway = gateway; _dispatch = dispatch; }
    internal ObservationHistoryView View { get { lock (_gate) return _view; } }
    internal Task WhenIdle { get { lock (_gate) return _idle; } }
    internal void Reload(UnifiedHistoryFilters filters)
    {
        lock (_gate) { if (_closed) return; Queue(filters, null, clear: true); }
        Notify();
    }
    internal void NextPage()
    {
        lock (_gate)
        {
            if (_closed || _view.Status != ObservationHistoryStatus.Ready || _view.NextCursor is null) return;
            Queue(_view.Filters, _view.NextCursor, clear: false);
        }
        Notify();
    }
    internal void LanguageChanged()
    {
        lock (_gate)
        {
            if (_closed) return;
            // Translation does not alter the query. Keep an in-flight request's
            // revision so its facts publish once; the window invalidates its
            // own stale action messages through the selection generation.
            if (_view.Status != ObservationHistoryStatus.Loading)
                _view = _view with { Revision = _view.Revision + 1 };
        }
        Notify();
    }
    private void Queue(UnifiedHistoryFilters filters, string? cursor, bool clear)
    {
        _view = new(ObservationHistoryStatus.Loading, clear ? Array.Empty<UnifiedHistoryEntry>() : _view.Items,
            null, filters, _view.Revision + 1);
        _pending = new(filters, cursor, _view.Revision);
        if (!_running) { _running = true; _idle = Task.Run(ProcessPending); }
    }
    private void ProcessPending()
    {
        while (true)
        {
            Request request;
            lock (_gate)
            {
                if (_closed || _pending is null) { _running = false; return; }
                request = _pending; _pending = null;
            }
            UnifiedHistoryPage? page = null;
            var status = ObservationHistoryStatus.Unavailable;
            try
            {
                page = _gateway.GetPage(20, request.Filters, request.Cursor);
                if (page is null || page.Items is null || page.Items.Count > 20 || page.Filters != request.Filters)
                    throw new InvalidDataException("Invalid history page.");
                // Copy the list; the shared immutable entry contracts are not changed.
                page = page with { Items = Array.AsReadOnly(page.Items.ToArray()) };
                status = page.Items.Count == 0 ? ObservationHistoryStatus.Empty : ObservationHistoryStatus.Ready;
            }
            catch (UnifiedHistoryArgumentException) { page = null; status = ObservationHistoryStatus.InvalidCursor; }
            catch (Exception) { page = null; /* The UI exposes a readable code, never raw storage exceptions. */ }
            bool publish;
            lock (_gate)
            {
                publish = !_closed && request.Revision == _view.Revision;
                if (publish) _view = new(status, page?.Items ?? Array.Empty<UnifiedHistoryEntry>(),
                    status == ObservationHistoryStatus.Ready ? page!.NextCursor : null, request.Filters, request.Revision);
            }
            if (publish) Notify();
        }
    }
    private void Notify()
    {
        try { _dispatch(() => { lock (_gate) { if (_closed) return; } Changed?.Invoke(); }); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // The owner can lose its handle during shutdown.
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true; _pending = null; Changed = null;
            _view = new(ObservationHistoryStatus.Closed, Array.Empty<UnifiedHistoryEntry>(), null, _view.Filters, _view.Revision + 1);
        }
    }
    private sealed record Request(UnifiedHistoryFilters Filters, string? Cursor, long Revision);
}
