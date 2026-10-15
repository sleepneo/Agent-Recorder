using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal enum LocalDeletionProgress { NotAttempted, InFlight, DispositionAccepted, DispositionRejected, Indeterminate }
internal enum LocalDeletionReason { None, AuthorizationRejected, ScopeRejected, NativeRejected, NativeUnknown }
internal sealed record LocalDeletionResult(string? ApprovalId, string AttemptId, LocalDeletionProgress Progress,
    LocalDeletionReason Reason, int? Win32Error, bool ApprovalConsumed, bool? OurResourcesRetired,
    RetentionComposedResult? Execution, RetentionComposedRetirement? Retirement,
    DeletionLedgerCommit Preparation = DeletionLedgerCommit.NotCommitted, DeletionLedgerCommit Settlement = DeletionLedgerCommit.NotCommitted,
    bool? LedgerResourcesRetired = null)
{
    // TRUE means an accepted disposition request, never proof of physical
    // deletion/free space. NULL is essential for a call still in flight.
    internal bool? FileActionPerformed => Progress switch
    {
        LocalDeletionProgress.DispositionAccepted => true,
        LocalDeletionProgress.Indeterminate or LocalDeletionProgress.InFlight => null,
        _ => false
    };
}

internal sealed partial class LocalRetentionApproval
{
    // Host-frozen internal history access; these readers never issue a Permit,
    // reclaim an admission or resume an operation.
    internal DeletionLedgerEntry? ReadDeletionOperation(string operationId) => _ledger.Read(operationId);
    internal IReadOnlyList<DeletionLedgerEntry> ReadDeletionOperations(string? after=null,int limit=100) => _ledger.Page(after,limit);
    // Deliberately separate from the old no-file-action Execute protocol. No
    // caller-supplied callback, handle, path or native adapter at this entry.
    internal DeletionOperation RequestDeletion(Permit? permit, string recordingId)
    {
        lock (_sync)
        {
            var session = _active;
            if (session?.State == LocalRetentionState.Approved && session.ApprovedPermit?.Valid(_clock) == false)
                session.MarkExpired();
            if (permit is null || session is null || !ReferenceEquals(session.ApprovedPermit, permit) ||
                !permit.Matches(this, session) || recordingId != session.Id || session.Executing || session.State != LocalRetentionState.Approved ||
                session.Stopping || _stopped || _engine.RetentionHostStopping || !permit.Valid(_clock))
                return DeletionOperation.Rejected();
            session.Executing = true;
            var operation = DeletionOperation.ForAttempt(session, permit);
            session.StartDeletion(permit, operation);
            return operation;
        }
    }

    internal sealed class DeletionOperation
    {
        private readonly Session? _session;
        private readonly Permit? _permit;
        private readonly string _attemptId = Guid.NewGuid().ToString("N");
        private LocalDeletionProgress _progress;
        private LocalDeletionReason _reason;
        private int? _error;
        private readonly long _started;
        internal long Started => _started;
        private readonly DeletionLedgerBinding? _binding;
        private DeletionLedger.Prepared? _prepared;
        private RetentionComposedScope.ScopeContext? _checkedScope;
        private DeletionLedgerCommit _preparationCommit, _settlementCommit;
        private bool _ledgerWorking;
        internal bool LedgerResourcesRetired { get; private set; } = true;
        private ITimer? _timer;
        private CancellationTokenRegistration _cancelRegistration;
        private readonly TaskCompletionSource<LocalDeletionResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<LocalDeletionResult> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DeletionOperation(Session? session, Permit? permit)
        {
            _session = session; _permit = permit;
            if(session is not null)
            {
                _started=session.Host._clock.GetTimestamp();var facts=session.Facts!;
                _binding=new(_attemptId,permit!.ApprovalId,session.Host._sid,session.Host._binding,DeletionLedger.HostId,
                    facts.RecordingId,session.Host._root,facts.Anchor,facts.CompletedAtUtc,facts.Cutoff,Semantics,session.Host._clock.GetUtcNow().ToUniversalTime());
            }
        }
        internal Task<LocalDeletionResult> Response => _response.Task;
        internal Task<LocalDeletionResult> Retirement => _retirement.Task;
        // A fresh observation for the host coordinator; never mutates the
        // already frozen Response or creates/recovers a disposition capability.
        internal LocalDeletionResult Current => _retirement.Task.IsCompletedSuccessfully?_retirement.Task.Result:Snapshot(null);
        internal static DeletionOperation Rejected()
        {
            var op = new DeletionOperation(null, null) { _reason = LocalDeletionReason.AuthorizationRejected };
            var result = op.Snapshot(null, true); op._response.SetResult(result); op._retirement.SetResult(result); return op;
        }
        internal static DeletionOperation ForAttempt(Session session, Permit permit) => new(session, permit);
        private LocalDeletionResult Snapshot(RetentionComposedResult? execution, bool? retired = null, RetentionComposedRetirement? retirement = null)
        {
            if (_session is null) return new(null, _attemptId, _progress, _reason, _error, false, retired, execution, retirement);
            lock (_session.Host._sync)
                return new(_permit!.ApprovalId, _attemptId,
                    _progress == LocalDeletionProgress.InFlight ? LocalDeletionProgress.Indeterminate : _progress,
                    _reason == LocalDeletionReason.None && _progress == LocalDeletionProgress.NotAttempted ? LocalDeletionReason.ScopeRejected : _reason,
                    _error, _session.Current.ApprovalConsumed, retired, execution, retirement,_preparationCommit,_settlementCommit,_ledgerWorking?null:LedgerResourcesRetired);
        }
        internal void Attach(RetentionComposedScope.Session execution) => _ = Freeze(execution);
        private async Task Freeze(RetentionComposedScope.Session execution)
        {
            try { _response.TrySetResult(Snapshot(await execution.Response.ConfigureAwait(false))); }
            catch { _response.TrySetResult(Snapshot(null)); }
        }
        internal void Settle(Task<LocalRetentionResult> attempt) => _ = SettleCore(attempt);
        private async Task SettleCore(Task<LocalRetentionResult> attempt)
        {
            LocalDeletionResult final;
            try
            {
                var r = await attempt.ConfigureAwait(false);
                var resources=r.ExecutionRetirement?.Retired ?? r.State==LocalRetentionState.ExecutionRejected;
                final = Snapshot(r.Execution, resources && LedgerResourcesRetired, r.ExecutionRetirement);
                if(resources&&LedgerResourcesRetired&&_preparationCommit==DeletionLedgerCommit.Committed)
                {
                    lock(_session!.Host._sync){_settlementCommit=DeletionLedgerCommit.Unknown;_ledgerWorking=true;}
                    var work=_session!.Host._ledger.Settle(_binding!,final);
                    try { await work.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                    catch { _response.TrySetResult(Snapshot(r.Execution,resources,r.ExecutionRetirement)); }
                    var settled=await work.ConfigureAwait(false);
                    lock(_session.Host._sync){_settlementCommit=settled.Commit;LedgerResourcesRetired&=settled.ResourcesRetired;_ledgerWorking=false;}
                    final=Snapshot(r.Execution,resources&&LedgerResourcesRetired,r.ExecutionRetirement);
                }
            }
            catch { final = Snapshot(null, false); }
            try{_timer?.Dispose();_cancelRegistration.Dispose();}catch{LedgerResourcesRetired=false;final=Snapshot(final.Execution,false,final.Retirement);}
            if(LedgerResourcesRetired&&final.OurResourcesRetired==true)_session?.Host._ledger.Retire(_attemptId,this);
            _response.TrySetResult(final); _retirement.TrySetResult(final);
        }
        internal async Task<LocalRetentionResult> PrepareAndExecute(Func<Task<LocalRetentionResult>> execute)
        {
            var s=_session!;s.Host._ledger.Register(_attemptId,this);
            lock(s.Host._sync){_preparationCommit=DeletionLedgerCommit.Unknown;_ledgerWorking=true;}
            _timer=s.Host._clock.CreateTimer(_=>_response.TrySetResult(Snapshot(null)),null,TimeSpan.FromSeconds(3),Timeout.InfiniteTimeSpan);
            _cancelRegistration=s.OperationStop.Token.Register(()=>_response.TrySetResult(Snapshot(null)));
            var work=DeletionLedger.Prepared.Run(s.Host._ledger,_binding!,this,_started,s.OperationStop.Token);
            try
            {
                try{await work.WaitAsync(TimeSpan.FromSeconds(3),s.OperationStop.Token).ConfigureAwait(false);}
                catch{_response.TrySetResult(Snapshot(null));}
                var p=await work.ConfigureAwait(false);
                lock(s.Host._sync){_prepared=p.Proof;_preparationCommit=p.Result.Commit;LedgerResourcesRetired=p.Result.ResourcesRetired;_ledgerWorking=false;}
                if(_prepared is null||s.Stopping||s.Host._clock.GetElapsedTime(_started)>=TimeSpan.FromSeconds(3))return new(LocalRetentionState.ExecutionRejected);
                return await execute().ConfigureAwait(false);
            }
            finally { s.FinishDeletionAttempt(); }
        }
        internal void ValidatePrepared(RetentionComposedScope.ScopeContext scope)
        {
            if(_prepared is null||!ReferenceEquals(_prepared.Attempt,this)||_prepared.Binding!=_binding)throw new InvalidOperationException();
            scope.ValidateDeletion(_prepared);_checkedScope=scope;
        }
        internal RetentionComposedContinuationResult Invoke(RetentionComposedScope.ScopeContext scope)
        {
            scope.RequireActive();
            var authorization = Authorization.ForConsumedAttempt(this, scope);
            scope.SubmitDisposition(authorization);
            scope.RequireActive();
            return RetentionComposedContinuationResult.Completed;
        }

        // The sole concrete disposition capability. Its private construction
        // binds this consumed attempt, exact root scope and original borrow.
        // No IssueForTests, bool/JSON/proof constructor or generic callback exit.
        private sealed class Authorization : OriginalFileBorrowContext.DispositionAuthorization
        {
            private readonly DeletionOperation _operation;
            private readonly RetentionComposedScope.ScopeContext _scope;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private Authorization(DeletionOperation operation, RetentionComposedScope.ScopeContext scope)
            { _operation = operation; _scope = scope; }
            internal static Authorization ForConsumedAttempt(DeletionOperation operation, RetentionComposedScope.ScopeContext scope)
                => new(operation, scope);
            internal override bool TryBegin(OriginalFileBorrowContext current)
            {
                var op = _operation; var session = op._session!; var host = session.Host;
                _scope.RequireActive(); current.RequireActive();
                lock (host._sync)
                {
                    // State-only linearization against Dispose/host stopping;
                    // these RequireActive checks do no IO or provider calls.
                    _scope.RequireActive();
                    if (_thread != Environment.CurrentManagedThreadId || !_scope.OwnsBorrow(current) ||
                        !ReferenceEquals(op._checkedScope,_scope) || op._prepared is null || !ReferenceEquals(op._prepared.Attempt,op) ||
                        op._progress != LocalDeletionProgress.NotAttempted || !session.Executing ||
                        session.State != LocalRetentionState.Consumed || !session.Current.ApprovalConsumed ||
                        session.Stopping || host._stopped || host._engine.RetentionHostStopping ||
                        !ReferenceEquals(session.ApprovedPermit, op._permit) || !op._permit!.Matches(host, session) || !op._permit.Valid(host._clock) ||
                        session.Facts is not { } facts || _scope.RecordingId != facts.RecordingId ||
                        _scope.Anchor != facts.Anchor || _scope.Cutoff != facts.Cutoff || _scope.CompletedAtUtc != facts.CompletedAtUtc)
                        return false;
                    op._progress = LocalDeletionProgress.InFlight; // Irreversible submission starts here, not at consumption.
                    return true;
                }
            }
            internal override void Complete(OriginalFileDispositionResult result)
            {
                var op = _operation;
                lock (op._session!.Host._sync)
                {
                    if (op._progress != LocalDeletionProgress.InFlight) return;
                    op._progress = result.Outcome switch
                    {
                        OriginalFileDispositionOutcome.Accepted => LocalDeletionProgress.DispositionAccepted,
                        OriginalFileDispositionOutcome.Rejected => LocalDeletionProgress.DispositionRejected,
                        _ => LocalDeletionProgress.Indeterminate
                    };
                    op._reason = result.Outcome switch
                    {
                        OriginalFileDispositionOutcome.Accepted => LocalDeletionReason.None,
                        OriginalFileDispositionOutcome.Rejected => LocalDeletionReason.NativeRejected,
                        _ => LocalDeletionReason.NativeUnknown
                    };
                    op._error = result.Win32Error;
                }
            }
        }
    }
}
