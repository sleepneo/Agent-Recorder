using AgentRecorder.Api;

namespace AgentRecorder.App;

// Lifetime belongs to the tray's existing hidden owner control, not an observation form.
internal sealed class ObservationHealthOwnerDispatcher
{
    private readonly Control _control;
    private readonly Func<ObservationHealthOwner> _capture;
    private readonly int _ownerThread=Environment.CurrentManagedThreadId;
    private readonly object _gate=new();
    private readonly HashSet<TaskCompletionSource<ObservationHealthOwner>> _pending=new();
    private bool _available;
    private int _generation;
    internal Action<Action>? PostForTests { get; set; }
    internal ObservationHealthOwnerDispatcher(Control control,Func<ObservationHealthOwner> capture)
    {
        _control=control;_capture=capture;_available=control.IsHandleCreated && !control.IsDisposed;
        control.HandleDestroyed+=(_,_)=>Invalidate();
        control.Disposed+=(_,_)=>Invalidate();
        control.HandleCreated+=(_,_)=>{lock(_gate) {_generation++;_available=!control.IsDisposed;}};
    }
    private void Invalidate()
    {
        lock(_gate)
        {
            _available=false;_generation++;
            foreach(var pending in _pending) pending.TrySetException(new InvalidOperationException("owner_unavailable"));
            _pending.Clear();
        }
    }
    internal Task<ObservationHealthOwner> PostAsync(CancellationToken cancellation)
    {
        var pending=new TaskCompletionSource<ObservationHealthOwner>(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;
        lock(_gate)
        {
            if(!_available) return Task.FromException<ObservationHealthOwner>(new InvalidOperationException("owner_unavailable"));
            generation=_generation;_pending.Add(pending);
        }
        void Complete()
        {
            try
            {
                lock(_gate)
                    if(!_available || generation!=_generation || Environment.CurrentManagedThreadId!=_ownerThread)
                    { pending.TrySetException(new InvalidOperationException("owner_unavailable"));return; }
                // A cancelled queued callback acknowledges completion only when drained.
                cancellation.ThrowIfCancellationRequested();
                pending.TrySetResult(_capture());
            }
            catch { pending.TrySetException(new InvalidOperationException("owner_unavailable")); }
            finally { lock(_gate) _pending.Remove(pending); }
        }
        try
        {
            if(Environment.CurrentManagedThreadId==_ownerThread) Complete();
            else if(PostForTests is { } post) post(Complete);
            else _control.BeginInvoke((Action)Complete);
        }
        catch {lock(_gate) _pending.Remove(pending);pending.TrySetException(new InvalidOperationException("owner_unavailable"));}
        return pending.Task;
    }
}
