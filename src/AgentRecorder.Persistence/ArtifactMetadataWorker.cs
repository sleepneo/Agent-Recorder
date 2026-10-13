namespace AgentRecorder.Persistence;

// Metadata endpoints share this actual worker, independently of content verification.
internal static class ArtifactMetadataWorker
{
    private static int _busy;
    internal static async Task<T?> ReadAsync<T>(Func<CancellationToken,T?> run, Func<string,T> snapshot,
        Func<CancellationToken,Task> deadlineFactory, Action? retired, CancellationToken cancellation) where T : class
    {
        if (cancellation.IsCancellationRequested) return snapshot("cancelled");
        if (Interlocked.CompareExchange(ref _busy,1,0)!=0) return snapshot("busy");
        var workCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var deadlineCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task deadline; Task<T?> work;
        try
        {
            deadline=deadlineFactory(deadlineCancellation.Token); // includes DB dispatch and disposal
            work=Task.Run(()=>
            {
                try { return run(workCancellation.Token); }
                finally
                {
                    try { workCancellation.Dispose(); }
                    finally { Volatile.Write(ref _busy,0); retired?.Invoke(); }
                }
            });
        }
        catch
        {
            workCancellation.Dispose(); Volatile.Write(ref _busy,0);
            return snapshot("failed");
        }
        try
        {
            if (await Task.WhenAny(work,deadline).ConfigureAwait(false)==work) return await work.ConfigureAwait(false);
            _=work.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
            try { workCancellation.Cancel(); } catch (ObjectDisposedException) { }
            return snapshot(cancellation.IsCancellationRequested?"cancelled":"timeout");
        }
        finally { deadlineCancellation.Cancel(); }
    }
}
