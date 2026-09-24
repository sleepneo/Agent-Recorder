using AgentRecorder.Logging;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

/// <summary>
/// App-owned bounded natural-wake loop for persisted recurring occurrences.
/// Discovery is read-only; the existing dispatcher/coordinator owns every
/// reservation, authorization, recovery, backend and lifecycle transition.
/// </summary>
internal sealed class RecurringLeaseNaturalWakeScheduler : IDisposable
{
    internal const int DefaultCandidateLimit = 4;

    private readonly RecurringOccurrenceNaturalWakeCandidateQuery _candidates;
    private readonly RecurringOccurrenceNaturalWakeDispatcher _dispatcher;
    private readonly Func<bool> _hasActiveRecording;
    private readonly Func<string?> _currentUserSid;
    private readonly Func<string?> _sessionBinding;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<
        DateTimeOffset,
        string?,
        string?,
        int,
        RecurringOccurrenceNaturalWakeCandidatePage> _candidateSource;
    private readonly Func<
        RecurringOccurrenceNaturalWakeCandidate,
        CancellationToken,
        Task<RecurringOccurrenceNaturalWakeDispatchResult>>? _dispatchForTest;
    private readonly Func<DateTimeOffset, string, string, CancellationToken, Task<bool>>? _advanceBeforeQuery;
    private readonly Action<string, object> _audit;
    private readonly bool _probeCandidateSourceAtStart;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _disposeWait;
    private readonly int _candidateLimit;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sync = new();
    private Task? _loop;
    private Task? _resourceCleanupTask;
    private int _started;
    private int _healthy;
    private int _disposed;
    private int _resourcesDisposed;
    private int _resourceDisposeCount;

    internal RecurringLeaseNaturalWakeScheduler(
        SqliteOperationalStore store,
        RecurringOccurrenceNaturalWakeDispatcher dispatcher,
        Func<bool> hasActiveRecording,
        Func<string?> currentUserSid,
        Func<string?> sessionBinding,
        Func<DateTimeOffset?>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? waitForTest = null,
        TimeSpan? pollInterval = null,
        int candidateLimit = DefaultCandidateLimit,
        Action<string, object>? audit = null,
        Func<
            DateTimeOffset,
            string?,
            string?,
            int,
            RecurringOccurrenceNaturalWakeCandidatePage>? candidateSourceForTest = null,
        Func<
            RecurringOccurrenceNaturalWakeCandidate,
            CancellationToken,
            Task<RecurringOccurrenceNaturalWakeDispatchResult>>? dispatchForTest = null,
        TimeSpan? disposeWaitForTest = null,
        Func<DateTimeOffset, string, string, CancellationToken, Task<bool>>? advanceBeforeQuery = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _candidates = new RecurringOccurrenceNaturalWakeCandidateQuery(store);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hasActiveRecording = hasActiveRecording ?? throw new ArgumentNullException(nameof(hasActiveRecording));
        _currentUserSid = currentUserSid ?? throw new ArgumentNullException(nameof(currentUserSid));
        _sessionBinding = sessionBinding ?? throw new ArgumentNullException(nameof(sessionBinding));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _wait = waitForTest ?? WaitWithSignalAsync;
        _candidateSource = candidateSourceForTest ??
            ((observedAtUtc, currentUserSidValue, sessionBindingValue, limit) =>
                _candidates.ListReady(
                    observedAtUtc,
                    currentUserSidValue ?? "",
                    sessionBindingValue ?? "",
                    limit));
        _probeCandidateSourceAtStart = candidateSourceForTest is null;
        _dispatchForTest = dispatchForTest;
        _advanceBeforeQuery = advanceBeforeQuery;
        _audit = audit ?? new AuditLogger().Log;
        _pollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(1));
        if (_pollInterval <= TimeSpan.Zero || _pollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _disposeWait = disposeWaitForTest.GetValueOrDefault(TimeSpan.FromSeconds(2));
        if (_disposeWait <= TimeSpan.Zero || _disposeWait > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(disposeWaitForTest));
        if (candidateLimit is < 1 or > RecurringOccurrenceNaturalWakeCandidateLimits.MaxCandidateLimit)
            throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        _candidateLimit = candidateLimit;
    }

    internal bool IsStarted =>
        Volatile.Read(ref _started) != 0 && Volatile.Read(ref _disposed) == 0;

    internal bool IsHealthy => IsStarted && Volatile.Read(ref _healthy) != 0;

    internal Task? LoopTaskForTests => _loop;

    internal bool SynchronizationResourcesDisposedForTests =>
        Volatile.Read(ref _resourcesDisposed) != 0;

    internal int SynchronizationResourceDisposeCountForTests =>
        Volatile.Read(ref _resourceDisposeCount);

    internal Task? ResourceCleanupTaskForTests => _resourceCleanupTask;

    internal Task HoldDispatchGateForTestsAsync() => _dispatchGate.WaitAsync();

    internal void ReleaseDispatchGateForTests()
    {
        try { _dispatchGate.Release(); } catch (ObjectDisposedException) { }
    }

    internal bool Start()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return true;

            if (_probeCandidateSourceAtStart && !ProbeCandidateSource())
            {
                Volatile.Write(ref _started, 0);
                Volatile.Write(ref _healthy, 0);
                SafeAudit("recurring_lease.scheduler_start_failed", new
                {
                    reason_code = "recurring_natural_wake_startup_query_failed",
                });
                return false;
            }

            try
            {
                Volatile.Write(ref _healthy, 1);
                _loop = Task.Run(() => RunAsync(_lifetimeCts.Token));
                SafeAudit("recurring_lease.scheduler_started", new
                {
                    candidate_limit = _candidateLimit,
                    poll_interval_ms = (int)_pollInterval.TotalMilliseconds,
                });
                return true;
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                Volatile.Write(ref _healthy, 0);
                SafeAudit("recurring_lease.scheduler_start_failed", new
                {
                    reason_code = "recurring_natural_wake_scheduler_start_failed",
                });
                return false;
            }
        }
    }

    internal void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var observedAtUtc = ReadUtcNow();
                var currentUserSid = ReadCanonicalIdentity(_currentUserSid, "current_user_sid");
                var sessionBinding = ReadCanonicalIdentity(_sessionBinding, "session_binding");

                if (_advanceBeforeQuery is not null &&
                    !await _advanceBeforeQuery(
                        observedAtUtc, currentUserSid, sessionBinding, cancellationToken).ConfigureAwait(false))
                {
                    Volatile.Write(ref _healthy, 0);
                    await WaitAfterTickAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_hasActiveRecording())
                {
                    SafeAudit("recurring_lease.scheduler_busy_deferred", new
                    {
                        reason_code = "recording_conflict",
                    });
                    await WaitAfterTickAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var page = _candidateSource(
                    observedAtUtc,
                    currentUserSid,
                    sessionBinding,
                    _candidateLimit);
                Volatile.Write(ref _healthy, 1);
                var candidate = page.Candidates.FirstOrDefault();
                if (candidate is null)
                {
                    await WaitAfterTickAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await DispatchSingleFlightAsync(candidate, cancellationToken).ConfigureAwait(false);
                // Every dispatch result, including rejection/blocked/missed
                // results, takes the same bounded backoff. This prevents a
                // malformed or permanently blocked row from becoming a hot
                // retry loop.
                await WaitAfterTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                Volatile.Write(ref _healthy, 0);
                SafeAudit("recurring_lease.scheduler_query_failed", new
                {
                    reason_code = "recurring_natural_wake_tick_failed",
                });
                try
                {
                    await WaitAfterTickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private bool ProbeCandidateSource()
    {
        try
        {
            var observedAtUtc = ReadUtcNow();
            var currentUserSid = ReadCanonicalIdentity(_currentUserSid, "current_user_sid");
            var sessionBinding = ReadCanonicalIdentity(_sessionBinding, "session_binding");
            var page = _candidateSource(observedAtUtc, currentUserSid, sessionBinding, _candidateLimit);
            return page is not null && page.Candidates is not null &&
                page.Candidates.Count <= _candidateLimit &&
                page.Candidates.All(candidate => candidate is not null);
        }
        catch
        {
            return false;
        }
    }

    private async Task DispatchSingleFlightAsync(
        RecurringOccurrenceNaturalWakeCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Dispose is the shutdown linearization point. A query that
            // completed before Dispose must not begin dispatch afterward.
            if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
                return;

            RecurringOccurrenceNaturalWakeDispatchResult result;
            try
            {
                result = _dispatchForTest is not null
                    ? await _dispatchForTest(candidate, cancellationToken).ConfigureAwait(false)
                    : await _dispatcher.DispatchAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = RecurringOccurrenceNaturalWakeDispatchResult.Cancelled(
                    candidate,
                    "recurring_natural_wake_cancelled");
            }
            catch
            {
                result = RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "recurring_natural_wake_dispatch_failed");
            }

            // A valid Started result carries the lifecycle owner retained by
            // RecordingEngine. The scheduler deliberately does not dispose it.
            SafeAudit("recurring_lease.scheduler_dispatch", new
            {
                occurrence_identity = candidate.Slot.OccurrenceIdentity,
                result_status = result.Status.ToString(),
                reason_code = result.Reason,
                lifecycle_owner_transferred = result.Status == RecurringOccurrenceExecutionStatus.Started &&
                    result.LifecycleSession is not null,
            });
        }
        finally
        {
            try { _dispatchGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private DateTimeOffset ReadUtcNow()
    {
        var value = _utcNow();
        if (value is null || value.Value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("recurring_natural_wake_clock_invalid");
        return value.Value;
    }

    private static string ReadCanonicalIdentity(Func<string?> source, string name)
    {
        var value = source();
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException("recurring_natural_wake_" + name + "_invalid");
        }

        return value;
    }

    private Task WaitAfterTickAsync(CancellationToken cancellationToken) =>
        _wait(_pollInterval, cancellationToken);

    private async Task WaitWithSignalAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

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
            try { await Task.WhenAll(delayTask, signalTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
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

        try { _lifetimeCts.Cancel(); } catch { }
        Task? loop;
        lock (_sync)
            loop = _loop;

        var completed = loop is null;
        if (loop is not null)
        {
            try { completed = loop.Wait(_disposeWait); }
            catch { completed = loop.IsCompleted; }
        }

        if (completed)
        {
            DisposeSynchronizationResourcesOnce();
        }
        else if (loop is not null)
        {
            // A dispatch is allowed to outlive the bounded synchronous
            // shutdown window. Keep the primitives alive until the loop has
            // actually stopped, then observe the loop task and dispose them
            // exactly once. The continuation is retained so its completion is
            // observable by deterministic tests and cannot become an
            // unobserved fire-and-forget cleanup task.
            _resourceCleanupTask = loop.ContinueWith(
                completedLoop =>
                {
                    try { _ = completedLoop.Exception; } catch { }
                    DisposeSynchronizationResourcesOnce();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        SafeAudit("recurring_lease.scheduler_stopped", new
        {
            reason_code = "application_shutdown",
        });
    }

    private void DisposeSynchronizationResourcesOnce()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        try { _dispatchGate.Dispose(); } catch { }
        try { _wakeSignal.Dispose(); } catch { }
        try { _lifetimeCts.Dispose(); } catch { }
        Interlocked.Increment(ref _resourceDisposeCount);
    }
}
