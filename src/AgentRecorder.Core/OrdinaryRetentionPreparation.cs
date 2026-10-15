using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

internal enum OrdinaryPreparationReason { Prepared, InvalidInput, Busy, RuntimeInUse, Unavailable, ShuttingDown, Cancelled, TimedOut, ScopeUnavailable }
internal sealed record OrdinaryPreparationRetirement(bool Retired, bool ReservationReleased,
    OrdinaryPreparationReason? PrimaryReason, OriginalFileScopeRetirement? Scope);
internal sealed record OrdinaryPreparationResult(OrdinaryPreparationReason Reason, OriginalFileHoldReason? ScopeReason,
    OrdinaryRetentionPreparationOwner? Owner, Task<OrdinaryPreparationRetirement> Retirement);
internal enum OrdinaryPreparationReleaseAttempt { Released, LockUnavailable }

public sealed partial class RecordingEngine
{
    // A separate unforgeable session identity, never a synthetic Recording/ID.
    // Published under the original output lock; the host strongly roots it until
    // physical retirement AND exact logical release have both been confirmed.
    private OrdinaryRetentionPreparationSession? _ordinaryRetentionPreparation;
    internal OrdinaryRetentionPreparationService CreateRetentionPreparationService(string frozenVideosRoot,
        IOriginalFileHoldNative? native = null, OriginalFileHoldAdmission? admission = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
        => new(this, frozenVideosRoot, native, admission, clock, deadline, dispatch);

    internal bool RetentionHostStopping => Volatile.Read(ref _disposeStarted) != 0;
    internal OrdinaryPreparationReason TryReserveOrdinaryRetention(OrdinaryRetentionPreparationSession session)
    {
        if (!Monitor.TryEnter(_ordinaryOutputLock, OrdinaryRuntimeOutputSnapshot.LockTimeoutMilliseconds)) return OrdinaryPreparationReason.Unavailable;
        try
        {
            if (RetentionHostStopping) return OrdinaryPreparationReason.ShuttingDown;
            if (session.Stopped) return OrdinaryPreparationReason.Cancelled;
            if (_ordinaryRetentionPreparation is not null) return OrdinaryPreparationReason.Busy;
            if (_ordinaryOutputs.Count > OrdinaryRuntimeOutputSnapshot.MaxClaims) return OrdinaryPreparationReason.Unavailable;
            // Validate every immutable registration; never inspect Recording,
            // Completion/backend/PID or retire terminal owners on this path.
            var matching = false;
            foreach (var owner in _ordinaryOutputs.Values)
            {
                if (owner is null || owner.Recording is null || !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(owner.Path) ||
                    !string.Equals(Path.GetFullPath(owner.Path), owner.Path, StringComparison.OrdinalIgnoreCase)) return OrdinaryPreparationReason.Unavailable;
                matching |= string.Equals(owner.Path, session.Path, StringComparison.OrdinalIgnoreCase);
            }
            if (matching) return OrdinaryPreparationReason.RuntimeInUse;
            Volatile.Write(ref _ordinaryRetentionPreparation, session); // linearization point
            // Close the publish-vs-shutdown race without taking any other lock.
            if (RetentionHostStopping || session.Stopped)
            { Volatile.Write(ref _ordinaryRetentionPreparation, null); return RetentionHostStopping ? OrdinaryPreparationReason.ShuttingDown : OrdinaryPreparationReason.Cancelled; }
            return OrdinaryPreparationReason.Prepared;
        }
        catch { return OrdinaryPreparationReason.Unavailable; }
        finally { Monitor.Exit(_ordinaryOutputLock); }
    }
    internal bool TryReleaseOrdinaryRetention(OrdinaryRetentionPreparationSession session)
    {
        if (!Monitor.TryEnter(_ordinaryOutputLock, OrdinaryRuntimeOutputSnapshot.LockTimeoutMilliseconds)) return false;
        try
        {
            if (!ReferenceEquals(_ordinaryRetentionPreparation, session)) return false;
            Volatile.Write(ref _ordinaryRetentionPreparation, null); return true;
        }
        finally { Monitor.Exit(_ordinaryOutputLock); }
    }
    private void RequestOrdinaryRetentionExit() => Volatile.Read(ref _ordinaryRetentionPreparation)?.RequestDispose();
}

internal sealed class OrdinaryRetentionPreparationService
{
    private readonly RecordingEngine _engine;
    private readonly string _root;
    private readonly OriginalFileScopeHoldService _scope;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _deadline;
    private readonly Func<Action, Task> _dispatch;
    internal OrdinaryRetentionPreparationService(RecordingEngine engine, string root, IOriginalFileHoldNative? native,
        OriginalFileHoldAdmission? admission, TimeProvider? clock, Func<TimeSpan, CancellationToken, Task>? deadline, Func<Action, Task>? dispatch)
    {
        _engine = engine; _root = root; _clock = clock ?? TimeProvider.System;
        _scope = new(root, native, admission, _clock);
        _deadline = deadline ?? ((duration, token) => Task.Delay(duration, _clock, token)); _dispatch = dispatch ?? (action => Task.Run(action));
    }
    internal Task<OrdinaryPreparationResult> AcquireAsync(PublicationIdentityAnchor anchor, CancellationToken cancellation = default)
        => AcquireCore(anchor, cancellation, null);
    internal Task<OrdinaryPreparationResult> AcquireBeforeAsync(PublicationIdentityAnchor anchor, long started, CancellationToken cancellation)
        => AcquireCore(anchor, cancellation, started);
    private Task<OrdinaryPreparationResult> AcquireCore(PublicationIdentityAnchor anchor, CancellationToken cancellation, long? absoluteStarted)
    {
        var started = absoluteStarted ?? _clock.GetTimestamp();
        if (!OriginalFileHoldService.ValidScopeInput(_root, anchor)) return Rejected(OrdinaryPreparationReason.InvalidInput);
        if (cancellation.IsCancellationRequested) return Rejected(OrdinaryPreparationReason.Cancelled);
        if (_engine.RetentionHostStopping) return Rejected(OrdinaryPreparationReason.ShuttingDown);
        if (absoluteStarted.HasValue && _clock.GetElapsedTime(started) >= OriginalFileHoldService.AcquisitionBudget) return Rejected(OrdinaryPreparationReason.TimedOut);
        return new OrdinaryRetentionPreparationSession(_engine, _scope, anchor, _clock, started, _deadline, _dispatch, cancellation, absoluteStarted.HasValue).Start();
    }
    private static Task<OrdinaryPreparationResult> Rejected(OrdinaryPreparationReason reason)
        => Task.FromResult(new OrdinaryPreparationResult(reason, null, null,
            Task.FromResult(new OrdinaryPreparationRetirement(true, true, reason, null))));
}

internal sealed class OrdinaryRetentionPreparationSession
{
    private readonly RecordingEngine _engine;
    private readonly OriginalFileScopeHoldService _scope;
    private readonly PublicationIdentityAnchor _anchor;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly Func<TimeSpan, CancellationToken, Task> _deadline;
    private readonly Func<Action, Task> _dispatch;
    private readonly CancellationToken _cancellation;
    private readonly CancellationTokenSource _stopScope = new();
    private readonly object _sync = new();
    private readonly TaskCompletionSource<OrdinaryPreparationResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<OrdinaryPreparationRetirement> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<OrdinaryPreparationReleaseAttempt> _releaseAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OriginalFileScopeOwner? _owner;
    private int _stopped;
    private bool _published, _reserved, _scopeStarted;
    private OrdinaryPreparationReason? _primary;
    private readonly bool _absoluteDeadline;
    private BorrowLease? _retainer;
    internal string Path => _anchor.Path;
    internal bool Stopped => Volatile.Read(ref _stopped) != 0;
    internal Task<OrdinaryPreparationRetirement> Retirement => _retirement.Task;
    internal Task<OrdinaryPreparationReleaseAttempt> ReleaseAttempt => _releaseAttempt.Task;
    internal OrdinaryRetentionPreparationSession(RecordingEngine engine, OriginalFileScopeHoldService scope, PublicationIdentityAnchor anchor,
        TimeProvider clock, long started, Func<TimeSpan, CancellationToken, Task> deadline, Func<Action, Task> dispatch, CancellationToken cancellation, bool absoluteDeadline = false)
    { _engine = engine; _scope = scope; _anchor = anchor; _clock = clock; _started = started; _deadline = deadline; _dispatch = dispatch; _cancellation = cancellation; _absoluteDeadline = absoluteDeadline; }

    internal async Task<OrdinaryPreparationResult> Start()
    {
        using var timerStop = new CancellationTokenSource();
        using var registration = _cancellation.Register(() => Freeze(OrdinaryPreparationReason.Cancelled));
        try
        {
            var budget = _absoluteDeadline ? OriginalFileHoldService.AcquisitionBudget - _clock.GetElapsedTime(_started) : OriginalFileHoldService.AcquisitionBudget;
            var deadline = _deadline(budget > TimeSpan.Zero ? budget : TimeSpan.Zero, timerStop.Token); _ = Watch(deadline, timerStop.Token);
            Task worker;
            try { worker = _dispatch(Reserve); } catch { worker = Task.FromException(new InvalidOperationException()); }
            _ = Run(worker);
        }
        catch { Freeze(OrdinaryPreparationReason.Unavailable); Complete(true, null); }
        try { return await _result.Task.ConfigureAwait(false); }
        finally { timerStop.Cancel(); }
    }
    private async Task Watch(Task deadline, CancellationToken stopped)
    {
        try { await deadline.ConfigureAwait(false); if (!stopped.IsCancellationRequested) Freeze(OrdinaryPreparationReason.TimedOut); }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch { if (!stopped.IsCancellationRequested) Freeze(OrdinaryPreparationReason.Unavailable); }
    }
    private void Freeze(OrdinaryPreparationReason reason, OriginalFileHoldReason? scopeReason = null)
    {
        lock (_sync)
        {
            if (_published || _result.Task.IsCompleted) return;
            _primary ??= reason; Interlocked.Exchange(ref _stopped, 1);
            _result.TrySetResult(new(reason, scopeReason, null, Retirement));
        }
        _stopScope.Cancel();
        Volatile.Read(ref _owner)?.Dispose();
    }
    private void Reserve()
    {
        if (Stopped || _cancellation.IsCancellationRequested) { SetPrimary(OrdinaryPreparationReason.Cancelled); return; }
        if (_clock.GetElapsedTime(_started) >= OriginalFileHoldService.AcquisitionBudget) { Freeze(OrdinaryPreparationReason.TimedOut); return; }
        var reason = _engine.TryReserveOrdinaryRetention(this);
        _reserved = reason == OrdinaryPreparationReason.Prepared;
        if (!_reserved) SetPrimary(reason);
    }
    private void SetPrimary(OrdinaryPreparationReason reason) { lock (_sync) _primary ??= reason; }
    private async Task Run(Task reserveWorker)
    {
        OriginalFileScopeResult? scope = null;
        OriginalFileScopeRetirement? finalScope = null;
        try
        {
            await reserveWorker.ConfigureAwait(false); // actual lock worker has exited
            if (_reserved && !Stopped)
            {
                _scopeStarted = true;
                scope = await _scope.AcquireBeforeAsync(_anchor, _started, _stopScope.Token).ConfigureAwait(false);
                Volatile.Write(ref _owner, scope.Owner);
                if (Stopped) _owner?.Dispose();
                // The same owned dispatcher represents actual handoff work as
                // well as lock admission. A delayed handoff still consumes the
                // original absolute budget; abandonment closes an arrived Scope
                // immediately and retains the booking until this worker exits.
                await _dispatch(() =>
                {
                    lock (_sync)
                    {
                        if (_owner is not null && !Stopped && !_cancellation.IsCancellationRequested && !_engine.RetentionHostStopping &&
                            _clock.GetElapsedTime(_started) < OriginalFileHoldService.AcquisitionBudget)
                        {
                            _published = true;
                            _result.TrySetResult(new(OrdinaryPreparationReason.Prepared, null, new OrdinaryRetentionPreparationOwner(this), Retirement));
                        }
                        else _primary ??= _engine.RetentionHostStopping ? OrdinaryPreparationReason.ShuttingDown :
                            Stopped || _cancellation.IsCancellationRequested ? OrdinaryPreparationReason.Cancelled :
                            _clock.GetElapsedTime(_started) >= OriginalFileHoldService.AcquisitionBudget ? OrdinaryPreparationReason.TimedOut :
                            scope.Reason switch { OriginalFileHoldReason.TimedOut => OrdinaryPreparationReason.TimedOut,
                                OriginalFileHoldReason.Cancelled => OrdinaryPreparationReason.Cancelled, _ => OrdinaryPreparationReason.ScopeUnavailable };
                    }
                }).ConfigureAwait(false);
                if (!_published) { Freeze(_primary!.Value, scope.Reason); RequestDispose(); }
                // A successful response/Dispose is not physical retirement.
                var retired = await scope.Retirement.ConfigureAwait(false);
                if (!retired.Retired) { Complete(false, retired, scope.Reason); return; }
                await ReleaseReservation().ConfigureAwait(false); Complete(true, retired, scope.Reason); return;
            }
        }
        catch { SetPrimary(OrdinaryPreparationReason.Unavailable); RequestDispose(); }
        // If Scope returned an owner before a publication/clock failure, do not
        // lose its resources. Unknown/faulted scope tasks retain the reservation.
        if (scope is not null)
        {
            try { finalScope = await scope.Retirement.ConfigureAwait(false); if (!finalScope.Retired) { Complete(false, finalScope); return; } }
            catch { Complete(false, null); return; }
        }
        else if (_reserved && _scopeStarted) { Complete(false, null); return; }
        await ReleaseReservation().ConfigureAwait(false); Complete(true, finalScope);
    }
    private async Task ReleaseReservation()
    {
        if (!_reserved) { _releaseAttempt.TrySetResult(OrdinaryPreparationReleaseAttempt.Released); return; }
        // One rooted cleanup worker, bounded 50ms attempts and backoff. No new
        // acquisition is queued and no lock is held over delay/native/await.
        while (!_engine.TryReleaseOrdinaryRetention(this))
        {
            _releaseAttempt.TrySetResult(OrdinaryPreparationReleaseAttempt.LockUnavailable);
            await Task.Delay(50).ConfigureAwait(false);
        }
        _reserved = false; _releaseAttempt.TrySetResult(OrdinaryPreparationReleaseAttempt.Released);
    }
    private void Complete(bool physicallyRetired, OriginalFileScopeRetirement? scope, OriginalFileHoldReason? scopeReason = null)
    {
        var retired = physicallyRetired && !_reserved;
        _retirement.TrySetResult(new(retired, !_reserved, _primary, scope));
        _result.TrySetResult(new(_primary ?? OrdinaryPreparationReason.Unavailable, scopeReason, null, Retirement));
    }
    internal Task<OriginalFileHoldObservation> ObserveAsync()
        => Stopped || _owner is null ? Task.FromResult(new OriginalFileHoldObservation(OriginalFileHoldReason.Disposed)) : _owner.ObserveAsync();
    internal void RequestDispose()
    {
        bool retained;
        lock (_sync)
        {
            Interlocked.Exchange(ref _stopped, 1);
            if (!_published) _primary ??= _engine.RetentionHostStopping ? OrdinaryPreparationReason.ShuttingDown : OrdinaryPreparationReason.Cancelled;
            retained = _retainer is not null;
        }
        if (retained) return; // DB worker still owns the whole physical lifetime.
        _stopScope.Cancel();
        Volatile.Read(ref _owner)?.Dispose();
    }

    // One in-process lifetime retention, acquired before DB dispatch and released
    // only after that exact DB worker reports confirmed retirement. It is not a
    // file handle or operation permit; cancellation still invalidates all use.
    internal sealed class BorrowLease : IDisposable
    {
        private readonly OrdinaryRetentionPreparationSession _session;
        private BorrowLease(OrdinaryRetentionPreparationSession session) => _session = session;
        internal static BorrowLease? TryRetain(OrdinaryRetentionPreparationSession session)
        {
            lock (session._sync)
            {
                if (!session._published || session.Stopped || session._engine.RetentionHostStopping ||
                    session._owner is null || session._retainer is not null || session._cancellation.IsCancellationRequested ||
                    session._clock.GetElapsedTime(session._started) >= OriginalFileHoldService.AcquisitionBudget) return null;
                var lease = new BorrowLease(session); session._retainer = lease; return lease;
            }
        }
        internal void RequireActive()
        {
            if (!ReferenceEquals(Volatile.Read(ref _session._retainer), this) || _session.Stopped ||
                _session._engine.RetentionHostStopping || _session._cancellation.IsCancellationRequested ||
                _session._clock.GetElapsedTime(_session._started) >= OriginalFileHoldService.AcquisitionBudget)
                throw new OperationCanceledException();
        }
        internal OriginalFileBorrowResult Borrow(PublicationIdentityAnchor anchor, Action check,
            Func<OriginalFileBorrowContext, OriginalFileBorrowContinuationResult> continuation)
        {
            RequireActive();
            if (anchor != _session._anchor) return new(OriginalFileBorrowReason.InvalidInput);
            var owner = Volatile.Read(ref _session._owner);
            return owner is null ? new(OriginalFileBorrowReason.Disposed) : owner.Borrow(anchor, () => { RequireActive(); check(); }, continuation);
        }
        public void Dispose()
        {
            bool close;
            lock (_session._sync)
            {
                if (!ReferenceEquals(_session._retainer, this)) return;
                _session._retainer = null; close = _session.Stopped;
            }
            if (close) _session.RequestDispose();
        }
    }
    internal BorrowLease? RetainForDatabaseScope() => BorrowLease.TryRetain(this);
}

internal sealed class OrdinaryRetentionPreparationOwner : IDisposable
{
    private readonly OrdinaryRetentionPreparationSession _session;
    internal OrdinaryRetentionPreparationOwner(OrdinaryRetentionPreparationSession session) => _session = session;
    internal Task<OriginalFileHoldObservation> ObserveAsync() => _session.ObserveAsync();
    internal Task<OrdinaryPreparationRetirement> Retirement => _session.Retirement;
    internal Task<OrdinaryPreparationReleaseAttempt> ReleaseAttempt => _session.ReleaseAttempt;
    internal OrdinaryRetentionPreparationSession.BorrowLease? RetainForDatabaseScope() => _session.RetainForDatabaseScope();
    public void Dispose() => _session.RequestDispose();
}
