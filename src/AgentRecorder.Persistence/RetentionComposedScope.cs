using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal enum RetentionComposedReason
{ Checked, InvalidInput, Busy, Cancelled, HostStopped, TimedOut, PreparationFailed, DatabaseRejected, BorrowFailed, ContinuationFailed, Unavailable }
internal enum RetentionComposedContinuationResult { Completed }
internal sealed record RetentionComposedResult(RetentionComposedReason Reason, OrdinaryPreparationReason? Preparation,
    RetentionGuardReason? Database, OriginalFileBorrowResult? Borrow);
internal sealed record RetentionComposedRetirement(bool Retired, RetentionComposedReason Primary,
    OrdinaryPreparationRetirement? Preparation, RetentionGuardRetirement? Database, OriginalFileBorrowResult? Borrow,
    OrdinaryPreparationReason? PreparationReason = null, OriginalFileHoldReason? PreparationScopeReason = null,
    RetentionGuardReason? DatabaseReason = null);

internal sealed class RetentionComposedAdmission
{
    internal static readonly RetentionComposedAdmission Host = new();
    private RetentionComposedScope.Session? _active;
    internal bool Acquire(RetentionComposedScope.Session session) => Interlocked.CompareExchange(ref _active, session, null) is null;
    internal void Release(RetentionComposedScope.Session session) => Interlocked.CompareExchange(ref _active, null, session);
}

// Local approval belongs before this protocol. Pure Checked scopes cannot
// construct a consumed-attempt disposition capability.
internal sealed class RetentionComposedScope : IDisposable
{
    private readonly RecordingEngine _engine;
    private readonly OrdinaryRetentionPreparationService _preparation;
    private readonly RetentionReferenceGuard _guard;
    private readonly RetentionComposedAdmission _admission;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();

    internal RetentionComposedScope(RecordingEngine engine, SqliteOperationalStore store, SqliteOrdinaryRecordingHistoryStore writer,
        string sid, string binding, string frozenVideosRoot, IOriginalFileHoldNative? native = null,
        OriginalFileHoldAdmission? nativeAdmission = null, RetentionGuardAdmission? databaseAdmission = null,
        RetentionComposedAdmission? admission = null, RetentionGuardDatabase? database = null, TimeProvider? clock = null,
        Func<Action, Task>? preparationDispatch = null)
    {
        _engine = engine; _clock = clock ?? TimeProvider.System; _admission = admission ?? RetentionComposedAdmission.Host;
        _preparation = engine.CreateRetentionPreparationService(frozenVideosRoot, native, nativeAdmission, _clock, dispatch: preparationDispatch);
        _guard = new(store, writer, sid, binding, databaseAdmission, database, _clock);
    }
    internal Session Execute(string recordingId, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
        Func<ScopeContext, RetentionComposedContinuationResult> continuation, CancellationToken cancellation = default)
        => ExecuteBefore(recordingId,anchor,cutoff,continuation,null,cancellation);
    internal Session ExecuteBefore(string recordingId, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
        Func<ScopeContext, RetentionComposedContinuationResult> continuation,long? absoluteStarted,CancellationToken cancellation)
    {
        var started = absoluteStarted ?? _clock.GetTimestamp();
        var session = new Session(this, recordingId, anchor, cutoff, continuation, cancellation, started);
        if (!UnifiedHistoryQueryService.ValidId(recordingId) || recordingId.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            anchor is null || !anchor.Valid || cutoff.Offset != TimeSpan.Zero || cutoff > _clock.GetUtcNow() || continuation is null)
            session.Reject(RetentionComposedReason.InvalidInput);
        else if (_stop.IsCancellationRequested || engineStopping()) session.Reject(RetentionComposedReason.HostStopped);
        else if (_clock.GetElapsedTime(started) >= RetentionReferenceGuard.Budget) session.Reject(RetentionComposedReason.TimedOut);
        else if (!_admission.Acquire(session)) session.Reject(RetentionComposedReason.Busy);
        else session.Start();
        return session;
    }
    private bool engineStopping() => _engine.RetentionHostStopping;
    public void Dispose() => _stop.Cancel(); // Cancellation requests only; no native/SQL on this thread.

    internal sealed class ScopeContext
    {
        private readonly Session _session;
        private readonly RetentionReferenceGuard.ScopeContext _database;
        private readonly OriginalFileBorrowContext _borrow;
        private readonly int _thread;
        private int _active = 1;
        private ScopeContext(Session session, RetentionReferenceGuard.ScopeContext database, OriginalFileBorrowContext borrow)
        { _session = session; _database = database; _borrow = borrow; _thread = Environment.CurrentManagedThreadId; }
        internal string RecordingId => _session.Id;
        internal PublicationIdentityAnchor Anchor => _session.Anchor;
        internal DateTimeOffset Cutoff => _session.Cutoff;
        internal DateTimeOffset CompletedAtUtc => _database.CompletedAtUtc;
        internal bool OwnsBorrow(OriginalFileBorrowContext borrow) => ReferenceEquals(_borrow, borrow);
        internal void ValidateDeletion(DeletionLedger.Prepared proof) { RequireActive(); _database.ValidateDeletion(proof); RequireActive(); }
        internal void SubmitDisposition(OriginalFileBorrowContext.DispositionAuthorization authorization)
        { RequireActive(); authorization.Submit(_borrow); }
        internal void RequireActive()
        {
            if (Volatile.Read(ref _active) == 0 || Environment.CurrentManagedThreadId != _thread || _database.RecordingId != _session.Id)
                throw new InvalidOperationException("The composed retention scope has expired.");
            _session.Check(); _database.RequireActive(); _borrow.RequireActive(); _session.RequireRetention();
        }
        internal static OriginalFileBorrowContinuationResult Invoke(Session session,
            RetentionReferenceGuard.ScopeContext database, OriginalFileBorrowContext borrow)
        {
            var context = new ScopeContext(session, database, borrow);
            try
            {
                context.RequireActive();
                if (session.Continuation(context) != RetentionComposedContinuationResult.Completed)
                    throw new InvalidOperationException("Invalid synchronous continuation result.");
                context.RequireActive(); return OriginalFileBorrowContinuationResult.Completed;
            }
            finally { Volatile.Write(ref context._active, 0); }
        }
        public override string ToString() => "ComposedRetentionScope(current-only)";
    }

    internal sealed class Session : IDisposable
    {
        private readonly RetentionComposedScope _host;
        internal string Id { get; }
        private readonly PublicationIdentityAnchor _anchor;
        private readonly DateTimeOffset _cutoff;
        internal PublicationIdentityAnchor Anchor => _anchor;
        internal DateTimeOffset Cutoff => _cutoff;
        internal Func<ScopeContext, RetentionComposedContinuationResult> Continuation { get; }
        private readonly long _started;
        private readonly CancellationToken _cancel;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<RetentionComposedResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RetentionComposedRetirement> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed, _primary = -1;
        private ITimer? _timer;
        private CancellationTokenRegistration _registration, _hostRegistration;
        private Task? _worker;
        private Task<OrdinaryPreparationResult>? _preparationTask;
        private OrdinaryPreparationResult? _prepared;
        private OrdinaryPreparationRetirement? _preparationRetirement;
        private OrdinaryRetentionPreparationSession.BorrowLease? _lease;
        private RetentionReferenceGuard.Session? _database;
        private RetentionGuardResult? _databaseResult;
        private RetentionGuardRetirement? _databaseRetirement;
        private OriginalFileBorrowResult? _borrow;
        internal Task<RetentionComposedResult> Response => _response.Task;
        internal Task<RetentionComposedRetirement> Retirement => _retirement.Task;
        internal Session(RetentionComposedScope host, string id, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
            Func<ScopeContext, RetentionComposedContinuationResult> continuation, CancellationToken cancellation, long started)
        { _host = host; Id = id; _anchor = anchor; _cutoff = cutoff; Continuation = continuation; _cancel = cancellation; _started = started; }
        private TimeSpan Remaining => RetentionReferenceGuard.Budget - _host._clock.GetElapsedTime(_started);
        private RetentionComposedReason? Stopped()
            => _host._stop.IsCancellationRequested || _host.engineStopping() ? RetentionComposedReason.HostStopped :
                _cancel.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 ? RetentionComposedReason.Cancelled :
                Remaining <= TimeSpan.Zero ? RetentionComposedReason.TimedOut : null;
        internal void Check() { if (Stopped() is not null) throw new OperationCanceledException(); }
        internal void RequireRetention() => _lease!.RequireActive();
        private void Primary(RetentionComposedReason reason) => Interlocked.CompareExchange(ref _primary, (int)reason, -1);
        private RetentionComposedResult Snapshot(RetentionComposedReason reason)
            => new(reason, Volatile.Read(ref _prepared)?.Reason, Volatile.Read(ref _databaseResult)?.Reason, Volatile.Read(ref _borrow));
        internal void Reject(RetentionComposedReason reason)
        { _response.TrySetResult(Snapshot(reason)); _retirement.TrySetResult(new(true, reason, null, null, null)); }
        private void Freeze(RetentionComposedReason reason)
        {
            Primary(reason); _response.TrySetResult(Snapshot(reason));
            _stop.Cancel(); Volatile.Read(ref _database)?.Dispose(); Volatile.Read(ref _prepared)?.Owner?.Dispose();
        }
        public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Freeze(RetentionComposedReason.Cancelled); }
        internal void Start()
        {
            try
            {
                _registration = _cancel.Register(() => Freeze(RetentionComposedReason.Cancelled));
                _hostRegistration = _host._stop.Token.Register(() => Freeze(RetentionComposedReason.HostStopped));
                _timer = _host._clock.CreateTimer(_ => Freeze(RetentionComposedReason.TimedOut), null,
                    Remaining > TimeSpan.Zero ? Remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                var worker = Task.Run(Run); _worker = worker;
                _ = worker.ContinueWith(done =>
                {
                    var result = done.Status == TaskStatus.RanToCompletion ? done.Result :
                        new RetentionComposedRetirement(false, RetentionComposedReason.Unavailable, _preparationRetirement, _databaseRetirement, _borrow);
                    if (done.IsFaulted) { _ = done.Exception; _response.TrySetResult(Snapshot(RetentionComposedReason.Unavailable)); }
                    if (result.Retired) _host._admission.Release(this);
                    _retirement.TrySetResult(result);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch
            {
                if (_worker is not null) { Freeze(RetentionComposedReason.Unavailable); return; }
                _timer?.Dispose(); _registration.Dispose(); _hostRegistration.Dispose();
                _host._admission.Release(this); Reject(RetentionComposedReason.Unavailable);
            }
        }
        private RetentionGuardContinuationResult Use(RetentionReferenceGuard.ScopeContext database)
        {
            Check(); RequireRetention(); database.RequireActive();
            var borrowed = _lease!.Borrow(_anchor, () => { Check(); database.RequireActive(); },
                native => ScopeContext.Invoke(this, database, native));
            Volatile.Write(ref _borrow, borrowed);
            Primary(borrowed.Reason == OriginalFileBorrowReason.Borrowed ? RetentionComposedReason.Checked :
                borrowed.Reason == OriginalFileBorrowReason.CallbackFailed ? RetentionComposedReason.ContinuationFailed :
                borrowed.Reason == OriginalFileBorrowReason.Cancelled ? Stopped() ?? RetentionComposedReason.Cancelled : RetentionComposedReason.BorrowFailed);
            return RetentionGuardContinuationResult.Completed;
        }
        private async Task<RetentionComposedRetirement> Run()
        {
            var confirmed = false;
            try
            {
                Check();
                _preparationTask = _host._preparation.AcquireBeforeAsync(_anchor, _started, _stop.Token);
                var prepared = await _preparationTask.ConfigureAwait(false); Volatile.Write(ref _prepared, prepared);
                if (prepared.Owner is null) Primary(RetentionComposedReason.PreparationFailed);
                else
                {
                    Check(); _lease = prepared.Owner.RetainForDatabaseScope();
                    if (_lease is null) Primary(RetentionComposedReason.BorrowFailed);
                    else
                    {
                        Check();
                        var database = _host._guard.ExecuteBefore(Id, _anchor, _cutoff, Use, _started, _stop.Token);
                        Volatile.Write(ref _database, database);
                        if (Stopped() is not null) database.Dispose();
                        var result = await database.Response.ConfigureAwait(false); Volatile.Write(ref _databaseResult, result);
                        _databaseRetirement = await database.Retirement.ConfigureAwait(false); // Actual worker, never merely Response.
                        if (!_databaseRetirement.Retired) Primary(RetentionComposedReason.Unavailable);
                        else if (result.Reason != RetentionGuardReason.Checked) Primary(Stopped() ?? RetentionComposedReason.DatabaseRejected);
                    }
                }
            }
            catch (OperationCanceledException) { Primary(Stopped() ?? RetentionComposedReason.Cancelled); }
            catch { Primary(RetentionComposedReason.Unavailable); }
            finally
            {
                // Once dispatched, uncertain DB ownership retains the native
                // lease and booking, including after StopAll/shutdown requests.
                if (_database is not null && _databaseRetirement is null)
                {
                    try { _databaseRetirement = await _database.Retirement.ConfigureAwait(false); }
                    catch { }
                }
                var databaseClosed = _database is null || _databaseRetirement?.Retired == true;
                if (databaseClosed)
                {
                    Volatile.Read(ref _prepared)?.Owner?.Dispose();
                    _lease?.Dispose();
                    if (_prepared is not null)
                    {
                        try { _preparationRetirement = await _prepared.Retirement.ConfigureAwait(false); }
                        catch { }
                    }
                    confirmed = _preparationTask is null || _preparationRetirement?.Retired == true && _preparationRetirement.ReservationReleased;
                }
                try { _timer?.Dispose(); _registration.Dispose(); _hostRegistration.Dispose(); }
                catch { confirmed = false; }
                var reason = Stopped() ?? (!confirmed ? RetentionComposedReason.Unavailable :
                    _primary >= 0 ? (RetentionComposedReason)_primary : RetentionComposedReason.Unavailable);
                _response.TrySetResult(Snapshot(reason));
            }
            return new(confirmed, _primary >= 0 ? (RetentionComposedReason)_primary : RetentionComposedReason.Unavailable,
                _preparationRetirement, _databaseRetirement, _borrow, _prepared?.Reason, _prepared?.ScopeReason, _databaseResult?.Reason);
        }
    }
}
