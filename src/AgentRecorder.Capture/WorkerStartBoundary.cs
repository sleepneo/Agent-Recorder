using System.ComponentModel;
using System.Diagnostics;

namespace AgentRecorder.Capture;

/// <summary>
/// Start/cleanup evidence only. Never stops a process or substitutes recording HasExited.
/// A missing native executable is an explicit no-child fact, not a missing-task inference.
/// </summary>
internal sealed class WorkerStartBoundary
{
    private enum ResourceState { NoResources, Launching, Started, Unknown }
    private readonly object _gate = new();
    private ResourceState _state;
    private long _generation;
    private bool _starting, _closed, _cleanupRunning, _cleanupComplete, _previousUnknown;
    private Func<bool>? _cleanup;
    private readonly TaskCompletionSource _noResourcesRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // A cheap published fact; readers never acquire the start/cleanup gate.
    internal Task NoResourcesRetired => _noResourcesRetired.Task;
    internal static bool NoAssociatedProcess(Process process)
    {
        try { _ = process.Id; return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return true; }
        catch { return false; }
    }
    internal bool KnownNoResources
    { get { lock (_gate) return !_starting && !_previousUnknown && _state == ResourceState.NoResources; } }

    internal void Start(Action<Attempt> start, Func<bool> previousRetired)
    {
        Attempt attempt;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_starting) throw new InvalidOperationException("Worker start is already in progress.");
            // Legacy reuse cannot erase an unretired/unknown preceding generation.
            if (_generation != 0 && _state != ResourceState.NoResources && !previousRetired()) _previousUnknown = true;
            _starting = true;
            _state = ResourceState.NoResources;
            _cleanupComplete = false;
            attempt = new(this, ++_generation);
        }
        try { start(attempt); }
        finally
        {
            bool clean;
            lock (_gate)
            {
                _starting = false;
                if (_state == ResourceState.Launching) _state = ResourceState.Unknown;
                clean = _closed;
            }
            if (clean) RunCleanup();
        }
    }

    internal bool ObserveRetired(Func<bool> startedRetired)
    {
        lock (_gate)
        {
            if (_starting || _cleanupRunning || _previousUnknown) return false;
            if (_state == ResourceState.NoResources) return _closed && _cleanupComplete;
            return startedRetired();
        }
    }

    internal void Close(Func<bool> cleanup)
    {
        lock (_gate) { _closed = true; _cleanup ??= cleanup; if (_starting) return; }
        RunCleanup();
    }

    // Used only by an owner that has already concluded a failed recording.
    internal void CloseIfNoResources(Func<bool> cleanup)
    {
        lock (_gate)
        {
            if (_starting || _previousUnknown || _state != ResourceState.NoResources) return;
            _closed = true; _cleanup ??= cleanup;
        }
        RunCleanup();
    }

    private void RunCleanup()
    {
        Func<bool>? cleanup;
        lock (_gate)
        {
            if (_starting || _cleanupRunning || _cleanupComplete || _cleanup is null) return;
            _cleanupRunning = true; cleanup = _cleanup;
        }
        bool complete = false;
        try { complete = cleanup(); } catch { }
        finally { lock (_gate) {
            _cleanupRunning = false; _cleanupComplete = complete;
            if (_closed && complete && !_starting && !_previousUnknown && _state == ResourceState.NoResources)
                _noResourcesRetired.TrySetResult();
        } }
    }

    internal sealed class Attempt(WorkerStartBoundary owner, long generation)
    {
        internal bool Launch(Func<bool> launch, bool nativeProcessContract, Func<bool>? noAssociatedProcess = null)
        {
            lock (owner._gate)
            {
                if (generation != owner._generation) throw new InvalidOperationException("Obsolete start generation.");
                ObjectDisposedException.ThrowIf(owner._closed, owner);
                owner._state = ResourceState.Launching;
            }
            try
            {
                bool started = launch();
                lock (owner._gate)
                    if (generation == owner._generation) owner._state = started ? ResourceState.Started : ResourceState.Unknown;
                return started;
            }
            catch (Win32Exception error) when (nativeProcessContract && error.NativeErrorCode is 2 or 3)
            {
                // These errors come from the real CreateProcess contract, before child creation.
                // Injected process implementations cannot make this claim merely by throwing one.
                bool absent = false;
                try { absent = noAssociatedProcess?.Invoke() == true; } catch { }
                if (absent)
                    lock (owner._gate)
                        if (generation == owner._generation) owner._state = ResourceState.NoResources;
                throw;
            }
        }
    }
}
