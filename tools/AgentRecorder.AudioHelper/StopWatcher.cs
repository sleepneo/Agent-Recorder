namespace AgentRecorder.AudioHelper;

/// <summary>
/// Polls a control file for existence. The first time the file is observed,
/// the stop action is invoked exactly once and the polling thread exits.
/// </summary>
internal sealed class StopWatcher : IDisposable
{
    private readonly string _path;
    private readonly Action _onTriggered;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private int _triggered;

    public StopWatcher(string path, Action onTriggered)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioHelperStopWatcher" };
    }

    public bool Triggered => Interlocked.CompareExchange(ref _triggered, 0, 0) != 0;

    public void Start() => _thread.Start();

    private void Run()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (File.Exists(_path))
                {
                    Trigger();
                    return;
                }

                Thread.Sleep(50);
            }
        }
        catch
        {
            // The watcher must never crash the capture thread.
        }
    }

    private void Trigger()
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0)
            return;

        try
        {
            _onTriggered();
        }
        catch
        {
            // The callback must not propagate; the watcher has done its job.
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try
        {
            if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(1)))
            {
                try { _thread.Interrupt(); } catch { }
            }
        }
        catch { }
        _cts.Dispose();
    }
}
