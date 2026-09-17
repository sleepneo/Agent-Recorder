using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

/// <summary>
/// App-owned natural-wake loop. It is deliberately a small, cancellable
/// in-process scheduler: SQLite is queried briefly, the existing intent-bound
/// dispatcher performs all authorization, and no OS wake primitive is used.
/// </summary>
internal sealed class StandingLeaseNaturalWakeScheduler : IDisposable
{
    private readonly StandingLeaseNaturalWakeCandidateQuery _candidates;
    private readonly StandingLeasePreparedIntentNaturalWakeDispatcher _dispatcher;
    private readonly Func<string?> _currentUserSid;
    private readonly Func<string?> _sessionBinding;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<string?, string?, IReadOnlyList<StandingLeaseNaturalWakeCandidate>> _candidateSource;
    private readonly Func<string, CancellationToken, Task>? _dispatchForTest;
    private readonly Action<string, object> _audit;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sync = new();
    private Task? _loop;
    private int _started;
    private int _disposed;

    internal StandingLeaseNaturalWakeScheduler(
        SqliteOperationalStore store,
        StandingLeasePreparedIntentNaturalWakeDispatcher dispatcher,
        Func<string?> currentUserSid,
        Func<string?> sessionBinding,
        Func<DateTimeOffset?>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? waitForTest = null,
        TimeSpan? pollInterval = null,
        Action<string, object>? audit = null,
        Func<string?, string?, IReadOnlyList<StandingLeaseNaturalWakeCandidate>>? candidateSourceForTest = null,
        Func<string, CancellationToken, Task>? dispatchForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _candidates = new StandingLeaseNaturalWakeCandidateQuery(store);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _currentUserSid = currentUserSid ?? throw new ArgumentNullException(nameof(currentUserSid));
        _sessionBinding = sessionBinding ?? throw new ArgumentNullException(nameof(sessionBinding));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _wait = waitForTest ?? WaitWithSignalAsync;
        _candidateSource = candidateSourceForTest ?? _candidates.ListCandidates;
        _dispatchForTest = dispatchForTest;
        _pollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(1));
        if (_pollInterval <= TimeSpan.Zero || _pollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _audit = audit ?? new AuditLogger().Log;
    }

    internal bool IsStarted => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _disposed) == 0;

    internal Task? LoopTaskForTests => _loop;

    internal bool Start()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return true;

            try
            {
                _loop = Task.Run(() => RunAsync(_lifetimeCts.Token));
                return true;
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                return false;
            }
        }
    }

    internal void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try { _wakeSignal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = ReadUtcNow();
                var currentUserSid = _currentUserSid();
                var sessionBinding = _sessionBinding();
                var candidates = _candidateSource(currentUserSid, sessionBinding);
                var candidate = candidates.FirstOrDefault();
                if (candidate is null)
                {
                    await _wait(_pollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (now < candidate.WindowStartUtc)
                {
                    var untilWindow = candidate.WindowStartUtc - now;
                    await _wait(untilWindow < _pollInterval ? untilWindow : _pollInterval, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await DispatchSingleFlightAsync(candidate.IntentId, cancellationToken).ConfigureAwait(false);
                // Always yield after a dispatch, including a fail-closed
                // result, so a disabled/revoked chain cannot busy-loop.
                await _wait(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                SafeAudit("standing_lease.scheduler_failed", new { reason_code = "natural_wake_scheduler_tick_failed" });
                try
                {
                    await _wait(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task DispatchSingleFlightAsync(string intentId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Dispose may have raced with the gate acquisition.  A cancelled
            // loop must never begin a new production dispatch after shutdown
            // has been linearized.
            if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
                return;

            if (_dispatchForTest is not null)
            {
                await _dispatchForTest(intentId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var result = await _dispatcher.DispatchAsync(intentId, cancellationToken).ConfigureAwait(false);
                SafeAudit("standing_lease.scheduler_dispatch", new
                {
                    intent_id = intentId,
                    status = result.Status.ToString(),
                    reason_code = result.Reason,
                });
            }
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private DateTimeOffset ReadUtcNow()
    {
        var value = _utcNow();
        if (value is null || value.Value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("natural_wake_clock_invalid");
        return value.Value;
    }

    private async Task WaitWithSignalAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        // Each iteration owns a linked cancellation source for both waiters.
        // When one waiter wins, cancel and observe the other before returning;
        // otherwise a timer-win leaves a semaphore waiter behind that a later
        // Signal() can consume.  Awaiting WhenAll also makes every cancellation
        // exception observed instead of creating an unowned background task.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, waitCts.Token);
        var signalTask = _wakeSignal.WaitAsync(waitCts.Token);
        try
        {
            await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
        }
        finally
        {
            waitCts.Cancel();
            try
            {
                await Task.WhenAll(delayTask, signalTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal cleanup result for the losing
                // waiter and for scheduler shutdown.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SafeAudit(string eventName, object payload)
    {
        try { _audit(eventName, payload); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // _disposed is the shutdown linearization point: Signal() intentionally
        // becomes a no-op after it, so do not call Signal() after this write.
        try { _lifetimeCts.Cancel(); } catch { }
        var loop = _loop;
        var completed = loop is null;
        if (loop is not null)
        {
            try { completed = loop.Wait(TimeSpan.FromSeconds(2)); }
            catch { completed = loop.IsCompleted; }
        }

        // A dispatch implementation is allowed to be non-cancellable.  In
        // that case retain all synchronization objects so the still-running
        // loop can safely unwind later; disposing them here would turn a
        // bounded shutdown into an ObjectDisposedException race.
        if (completed)
        {
            _dispatchGate.Dispose();
            _wakeSignal.Dispose();
            _lifetimeCts.Dispose();
        }
    }
}
