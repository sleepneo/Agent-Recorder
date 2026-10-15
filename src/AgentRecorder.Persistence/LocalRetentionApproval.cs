using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal enum LocalRetentionState { Pending, Rejected, Expired, Approved, Consumed, Cancelled, Unavailable, Busy, ExecutionRejected, Unknown }
internal enum LocalRetentionUiChoice { Approve, Reject, Closed, Escape, Unavailable }
internal enum LocalRetentionAttemptResult { Completed }
internal sealed record LocalRetentionResult(LocalRetentionState State, LocalRetentionApproval.Permit? Permit = null,
    RetentionComposedResult? Execution = null, bool FileActionPerformed = false, bool ApprovalConsumed = false,
    RetentionComposedRetirement? ExecutionRetirement = null);
internal sealed record LocalRetentionDecision(LocalRetentionIntent Intent, LocalRetentionUiChoice Choice);
internal sealed record LocalRetentionUiSession(Task<LocalRetentionDecision> Response, Task<bool> Retirement);
internal interface ILocalRetentionApprovalUi
{
    LocalRetentionUiSession Begin(LocalRetentionIntent intent, CancellationToken cancellation);
}

internal sealed class LocalRetentionIntent
{
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    internal LocalRetentionFacts Facts { get; }
    internal DateTimeOffset CreatedAtUtc { get; }
    internal long Started { get; }
    private readonly TimeProvider _clock;
    private LocalRetentionIntent(LocalRetentionFacts facts, TimeProvider clock)
    { Facts = facts; _clock = clock; CreatedAtUtc = clock.GetUtcNow(); Started = clock.GetTimestamp(); }
    internal bool Expired => !LocalRetentionApproval.Within(_clock, CreatedAtUtc, Started);
    internal static LocalRetentionIntent ForConfirmedFacts(LocalRetentionFacts facts, TimeProvider clock) => new(facts, clock);
}

internal sealed class LocalRetentionAdmission
{
    internal static readonly LocalRetentionAdmission Host = new();
    private LocalRetentionApproval.Session? _active;
    internal bool Acquire(LocalRetentionApproval.Session session) => Interlocked.CompareExchange(ref _active, session, null) is null;
    internal void Release(LocalRetentionApproval.Session session) => Interlocked.CompareExchange(ref _active, null, session);
}

// One fixed trusted host. No API/menu hookup. The old Execute remains a no-file-
// action protocol; RequestDeletion is a separate consumed-attempt operation.
internal sealed partial class LocalRetentionApproval : IDisposable
{
    internal const string Semantics = "permanently_delete_one_ordinary_main_mp4/v1";
    private readonly object _sync = new();
    private readonly RecordingEngine _engine;
    private readonly SqliteOperationalStore _store;
    private readonly SqliteOrdinaryRecordingHistoryStore _writer;
    private readonly string _sid, _binding, _root;
    private readonly Guid _lifetime = Guid.NewGuid();
    private readonly TimeProvider _clock;
    private readonly ILocalRetentionApprovalUi _ui;
    private readonly RetentionComposedScope _composition;
    private readonly LocalRetentionAdmission _admission;
    private readonly LocalRetentionFactsReader _reader;
    private readonly DeletionLedger _ledger;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenRegistration _hostRegistration;
    private Session? _active;
    private bool _stopped;

    internal LocalRetentionApproval(RecordingEngine engine, SqliteOperationalStore store, SqliteOrdinaryRecordingHistoryStore writer,
        string sid, string binding, string frozenVideosRoot, ILocalRetentionApprovalUi ui,
        TimeProvider? clock = null, LocalRetentionAdmission? admission = null,
        IOriginalFileHoldNative? native = null, OriginalFileHoldAdmission? nativeAdmission = null,
        RetentionGuardAdmission? databaseAdmission = null, RetentionComposedAdmission? composedAdmission = null,
        RetentionGuardDatabase? database = null, CancellationToken hostStopping = default,
        Func<string, bool>? factsExistence = null, DeletionLedgerDatabase? ledgerDatabase = null)
    {
        _engine = engine; _store = store; _writer = writer; _sid = sid; _binding = binding; _root = frozenVideosRoot;
        _clock = clock ?? TimeProvider.System; _ui = ui; _admission = admission ?? LocalRetentionAdmission.Host;
        _reader = new(store, writer, sid, binding, frozenVideosRoot, _clock, factsExistence);
        _ledger = new(store, sid, binding, frozenVideosRoot, _clock, ledgerDatabase);
        _composition = new(engine, store, writer, sid, binding, frozenVideosRoot, native, nativeAdmission, databaseAdmission, composedAdmission, database, _clock);
        _hostRegistration = hostStopping.Register(Dispose);
    }
    internal static bool Within(TimeProvider clock, DateTimeOffset utc, long started)
    {
        var now = clock.GetUtcNow(); var elapsed = clock.GetElapsedTime(started);
        return now >= utc && now < utc.AddSeconds(60) && elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(60);
    }
    internal Session Request(string recordingId, CancellationToken cancellation = default)
    {
        var session = new Session(this, recordingId, cancellation);
        lock (_sync)
        {
            if (_stopped || _engine.RetentionHostStopping) session.Reject(LocalRetentionState.Cancelled);
            else if (!UnifiedHistoryQueryService.ValidIdentity(_sid) || !UnifiedHistoryQueryService.ValidIdentity(_binding) ||
                !UnifiedHistoryQueryService.ValidId(recordingId) || recordingId.IndexOfAny(new[] { '/', '\\' }) >= 0) session.Reject(LocalRetentionState.Unavailable);
            else if (_active is not null || !_admission.Acquire(session)) session.Reject(LocalRetentionState.Busy);
            else _active = session;
        }
        if (ReferenceEquals(Volatile.Read(ref _active), session)) session.Start();
        return session;
    }
    internal Task<LocalRetentionResult> Execute(Permit? permit, string recordingId,
        Func<AttemptContext, LocalRetentionAttemptResult> continuation)
    {
        lock (_sync)
        {
            var session = _active;
            if (session?.State == LocalRetentionState.Approved && session.ApprovedPermit?.Valid(_clock) == false)
                session.MarkExpired();
            if (permit is null || session is null || continuation is null || !ReferenceEquals(session.ApprovedPermit, permit) ||
                !permit.Matches(this, session) || recordingId != session.Id || session.Executing || session.State != LocalRetentionState.Approved ||
                session.Stopping || _stopped || _engine.RetentionHostStopping || !permit.Valid(_clock))
                return Task.FromResult(new LocalRetentionResult(LocalRetentionState.ExecutionRejected));
            session.Executing = true; // Own one attempt lifecycle before dispatch; consumption is later inside Borrow.
            return session.Execute(permit, continuation);
        }
    }
    public void Dispose()
    {
        Session? active; lock (_sync) { _stopped = true; active = _active; }
        _stop.Cancel(); active?.Dispose(); // Requests only, never infer UI/native/DB retirement.
        _hostRegistration.Unregister();
    }

    internal sealed class Permit
    {
        private readonly LocalRetentionApproval _host;
        private readonly Session _session;
        private readonly LocalRetentionIntent _intent;
        private readonly LocalRetentionFacts _facts;
        private readonly Guid _lifetime;
        private readonly object _engine, _store, _writer;
        private readonly string _sid, _binding, _root, _semantics = Semantics;
        private readonly DateTimeOffset _approvedAt;
        private readonly long _started;
        internal string ApprovalId { get; } = Guid.NewGuid().ToString("N");
        private Permit(Session session, LocalRetentionIntent intent, DateTimeOffset approvedAt, long started)
        {
            _session = session; _host = session.Host; _intent = intent; _facts = intent.Facts;
            _lifetime = _host._lifetime; _engine = _host._engine; _store = _host._store; _writer = _host._writer;
            _sid = _host._sid; _binding = _host._binding; _root = _host._root; _approvedAt = approvedAt; _started = started;
        }
        internal bool Matches(LocalRetentionApproval host, Session session) => ReferenceEquals(host, _host) && ReferenceEquals(session, _session) &&
            ReferenceEquals(_intent, session.Intent) && _facts == session.Facts && _lifetime == host._lifetime &&
            ReferenceEquals(_engine, host._engine) && ReferenceEquals(_store, host._store) && ReferenceEquals(_writer, host._writer) &&
            _sid == host._sid && _binding == host._binding && _root == host._root && _semantics == Semantics;
        internal bool Valid(TimeProvider clock) => Within(clock, _approvedAt, _started);
        internal static Permit? CompleteLocalDecision(Session session, LocalRetentionDecision decision, DateTimeOffset utc, long stamp)
        {
            lock (session.Host._sync)
            {
                if (!ReferenceEquals(session.RetiredDecision, decision) || !ReferenceEquals(session.Intent, decision.Intent) || decision.Choice != LocalRetentionUiChoice.Approve ||
                    session.Intent!.Expired || session.Stopping || session.Host._stopped || session.Host._engine.RetentionHostStopping ||
                    session.State != LocalRetentionState.Pending || !Within(session.Host._clock, utc, stamp)) return null;
                return new(session, decision.Intent, utc, stamp);
            }
        }
    }

    internal sealed class AttemptContext
    {
        private readonly Session _session;
        private readonly RetentionComposedScope.ScopeContext _scope;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private int _active = 1;
        private AttemptContext(Session session, RetentionComposedScope.ScopeContext scope) { _session = session; _scope = scope; }
        internal void RequireActive()
        {
            if (_active == 0 || _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The local attempt has expired.");
            _session.OperationStop.Token.ThrowIfCancellationRequested(); _scope.RequireActive();
        }
        internal static RetentionComposedContinuationResult Invoke(Session session, RetentionComposedScope.ScopeContext scope,
            Func<AttemptContext, LocalRetentionAttemptResult> continuation)
        {
            var context = new AttemptContext(session, scope);
            try { context.RequireActive(); if (continuation(context) != LocalRetentionAttemptResult.Completed) throw new InvalidOperationException(); context.RequireActive(); return RetentionComposedContinuationResult.Completed; }
            finally { Volatile.Write(ref context._active, 0); }
        }
    }

    internal sealed class Session : IDisposable
    {
        internal LocalRetentionApproval Host { get; }
        internal string Id { get; }
        internal LocalRetentionFacts? Facts { get; private set; }
        internal LocalRetentionIntent? Intent { get; private set; }
        internal Permit? ApprovedPermit { get; private set; }
        internal LocalRetentionDecision? RetiredDecision { get; private set; }
        internal LocalRetentionState State = LocalRetentionState.Pending;
        internal bool Executing;
        private bool _cancelled, _consumed;
        private readonly CancellationToken _cancel;
        internal bool Stopping => _cancelled || _cancel.IsCancellationRequested || Host._stop.IsCancellationRequested;
        internal readonly CancellationTokenSource OperationStop = new();
        private readonly TaskCompletionSource<LocalRetentionResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private ITimer? _timer;
        private Task<LocalRetentionFacts?>? _query;
        private LocalRetentionUiSession? _ui;
        private LocalRetentionDecision? _receivedDecision;
        private bool _uiStarted;
        private RetentionComposedScope.Session? _execution;
        private Task<LocalRetentionResult>? _attempt;
        private DeletionOperation? _deletion;
        private bool _retired;
        internal Task<LocalRetentionResult> Response => _response.Task;
        internal Task<bool> Retirement => _retirement.Task;
        internal LocalRetentionResult Current
        {
            get { lock (Host._sync) { if (State == LocalRetentionState.Approved && ApprovedPermit?.Valid(Host._clock) == false) MarkExpired();
                return new(State, State == LocalRetentionState.Approved ? ApprovedPermit : null, ApprovalConsumed: _consumed); } }
        }
        internal Session(LocalRetentionApproval host, string id, CancellationToken cancellation) { Host = host; Id = id; _cancel = cancellation; }
        internal void Reject(LocalRetentionState state) { State = state; _response.TrySetResult(new(state)); _retirement.TrySetResult(true); }
        internal void Start()
        {
            _registration = _cancel.Register(Dispose);
            var worker = Task.Run(Run);
            _ = worker.ContinueWith(done =>
            {
                var retired = done.Status == TaskStatus.RanToCompletion && _retired;
                if (done.IsFaulted) { _ = done.Exception; _response.TrySetResult(new(LocalRetentionState.Unavailable)); }
                lock (Host._sync) { if (retired) { if (ReferenceEquals(Host._active, this)) Host._active = null; Host._admission.Release(this); } }
                _retirement.TrySetResult(retired);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private void Expire()
        {
            lock (Host._sync)
            {
                if (_consumed || State is not (LocalRetentionState.Pending or LocalRetentionState.Approved)) return;
                State = LocalRetentionState.Expired; _cancelled = true; _response.TrySetResult(new(State)); _finish.TrySetResult();
            }
            OperationStop.Cancel();
        }
        internal void MarkExpired()
        {
            // Caller owns Host._sync. No IO or cancellation callbacks in lock.
            State = LocalRetentionState.Expired; _cancelled = true; _finish.TrySetResult();
            _response.TrySetResult(new(State));
        }
        public void Dispose()
        {
            lock (Host._sync)
            {
                _cancelled = true; if (!_consumed) State = LocalRetentionState.Cancelled;
                _response.TrySetResult(new(State)); _finish.TrySetResult();
            }
            OperationStop.Cancel(); _execution?.Dispose();
        }
        private void Arm(TimeSpan? remaining = null) { _timer?.Dispose(); var due = remaining ?? TimeSpan.FromSeconds(60); _timer = Host._clock.CreateTimer(_ => Expire(), null, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
        private async Task Run()
        {
            var retired = true;
            try
            {
                OperationStop.Token.ThrowIfCancellationRequested();
                _query = Task.Run(() => Host._reader.Read(Id, OperationStop.Token));
                Facts = await _query.WaitAsync(TimeSpan.FromSeconds(3), OperationStop.Token).ConfigureAwait(false);
                if (Facts is null || Stopping || Host._engine.RetentionHostStopping) { _response.TrySetResult(new(LocalRetentionState.Unavailable)); return; }
                Intent = LocalRetentionIntent.ForConfirmedFacts(Facts, Host._clock); Arm();
                _uiStarted = true; _ui = Host._ui.Begin(Intent, OperationStop.Token);
                var decision = await _ui.Response.WaitAsync(OperationStop.Token).ConfigureAwait(false);
                var approvedUtc = Host._clock.GetUtcNow(); var approvedStamp = Host._clock.GetTimestamp();
                Volatile.Write(ref _receivedDecision, decision);
                // A response never substitutes for actual UI/STA retirement.
                retired = await _ui.Retirement.ConfigureAwait(false);
                if (!retired) { _response.TrySetResult(new(LocalRetentionState.Unavailable)); return; }
                RetiredDecision = decision;
                lock (Host._sync)
                {
                    ApprovedPermit = Permit.CompleteLocalDecision(this, decision, approvedUtc, approvedStamp);
                    if (ApprovedPermit is null)
                    {
                        if (State == LocalRetentionState.Pending) State = Intent.Expired ? LocalRetentionState.Expired :
                            decision.Choice == LocalRetentionUiChoice.Unavailable || !ReferenceEquals(Intent, decision.Intent) ? LocalRetentionState.Unavailable : LocalRetentionState.Rejected;
                        _response.TrySetResult(new(State)); return;
                    }
                    State = LocalRetentionState.Approved; Arm(TimeSpan.FromSeconds(60) - Host._clock.GetElapsedTime(approvedStamp)); _response.TrySetResult(new(State, ApprovedPermit));
                }
                await _finish.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { _response.TrySetResult(new(State == LocalRetentionState.Expired ? State : LocalRetentionState.Cancelled)); }
            catch { _response.TrySetResult(new(LocalRetentionState.Unavailable)); }
            finally
            {
                // Late query/UI/execution still owns this admission. Unknown
                // retirement keeps this Session rooted by the host/admission.
                if (_query is not null) { try { await _query.ConfigureAwait(false); } catch { } }
                retired &= Host._reader.Retired;
                if (_ui is not null) { try { retired &= await _ui.Retirement.ConfigureAwait(false); } catch { retired = false; } }
                else if (_uiStarted) retired = false;
                if (_attempt is not null) { try { await _attempt.ConfigureAwait(false); } catch { retired = false; } }
                if (_execution is not null) { try { retired &= (await _execution.Retirement.ConfigureAwait(false)).Retired; } catch { retired = false; } }
                if (_deletion is not null) { try { retired &= (await _deletion.Retirement.ConfigureAwait(false)).OurResourcesRetired == true; } catch { retired = false; } }
                retired &= _deletion?.LedgerResourcesRetired != false;
                try { _timer?.Dispose(); _registration.Dispose(); } catch { retired = false; }
                lock (Host._sync) { if (State == LocalRetentionState.Pending) State = LocalRetentionState.Unavailable; }
                _retired = retired;
            }
        }
        internal Task<LocalRetentionResult> Execute(Permit permit, Func<AttemptContext, LocalRetentionAttemptResult> continuation)
            => _attempt = ExecuteCore(permit, continuation);
        // Establish Task357's common start before the first await, rather than
        // adding an unbudgeted thread-pool hop before its dispatch.
        private async Task<LocalRetentionResult> ExecuteCore(Permit permit, Func<AttemptContext, LocalRetentionAttemptResult>? continuation,
            DeletionOperation? deletion = null)
            {
                try
                {
                    _execution = Host._composition.ExecuteBefore(Id, Facts!.Anchor, Facts.Cutoff, scope =>
                    {
                        scope.RequireActive();
                        deletion?.ValidatePrepared(scope);
                        lock (Host._sync)
                        {
                            if (Stopping || Host._stopped || Host._engine.RetentionHostStopping || _consumed ||
                                State != LocalRetentionState.Approved || !permit.Matches(Host, this) || !permit.Valid(Host._clock) ||
                                scope.RecordingId != Facts.RecordingId || scope.Anchor != Facts.Anchor || scope.Cutoff != Facts.Cutoff || scope.CompletedAtUtc != Facts.CompletedAtUtc)
                                throw new OperationCanceledException();
                            _consumed = true; State = LocalRetentionState.Consumed; // Linearization point.
                        }
                        return deletion is null ? AttemptContext.Invoke(this, scope, continuation!) : deletion.Invoke(scope);
                    }, deletion?.Started, OperationStop.Token);
                    deletion?.Attach(_execution);
                    var response = await _execution.Response.ConfigureAwait(false);
                    var retirement = await _execution.Retirement.ConfigureAwait(false);
                    return new(retirement.Retired ? _consumed ? LocalRetentionState.Consumed : LocalRetentionState.ExecutionRejected : LocalRetentionState.Unknown,
                        Execution: response, ApprovalConsumed: _consumed, ExecutionRetirement: retirement);
                }
                catch { return new(LocalRetentionState.Unknown, ApprovalConsumed: _consumed); }
                finally { _finish.TrySetResult(); }
            }
        internal void StartDeletion(Permit permit, DeletionOperation deletion)
        {
            _deletion = deletion;
            _attempt = deletion.PrepareAndExecute(() => ExecuteCore(permit, null, deletion));
            deletion.Settle(_attempt);
        }
        internal void FinishDeletionAttempt() => _finish.TrySetResult();
    }
}
