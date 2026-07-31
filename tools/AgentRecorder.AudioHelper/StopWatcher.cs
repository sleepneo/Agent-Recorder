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
    private readonly ManualResetEventSlim _pollingExited = new(false);
    private int _triggered;
    private int _startCount;

    public StopWatcher(string path, Action onTriggered)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioHelperStopWatcher" };
    }

    public bool Triggered => Interlocked.CompareExchange(ref _triggered, 0, 0) != 0;

    /// <summary>
    /// Number of times the polling thread was actually started. A watcher that
    /// was never started has a count of 0; the normal path starts it exactly
    /// once. This is the stable contract for proving the watcher did or did
    /// not start, unlike <see cref="Triggered"/> which only reports whether a
    /// stop file was observed.
    /// </summary>
    public int StartCount => Interlocked.CompareExchange(ref _startCount, 0, 0);

    /// <summary>True once <see cref="Start"/> has launched the polling thread.</summary>
    public bool Started => StartCount != 0;

    /// <summary>
    /// True once the polling loop has fully exited. Only meaningful after
    /// <see cref="Start"/>; a watcher that was never started has no polling
    /// thread at all.
    /// </summary>
    public bool PollingExited => _pollingExited.IsSet;

    /// <summary>
    /// Starts the polling thread. Idempotent: only the first call starts the
    /// thread, subsequent calls are no-ops so a duplicate start can never
    /// surface an opaque ThreadStateException from Thread.Start().
    /// </summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _startCount, 1, 0) != 0)
            return;
        _thread.Start();
    }

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
        finally
        {
            try { _pollingExited.Set(); } catch (ObjectDisposedException) { }
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
        // _pollingExited is intentionally not disposed here: PollingExited is
        // part of the post-disposal observability contract used to prove no
        // background polling thread was left behind.
    }
}
