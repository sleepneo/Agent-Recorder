using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

public sealed record RetentionBatchDeletionItemResult(int Position,string RecordingId,string State,string ReasonCode,
    string? DeletionRequestId,bool? ResourcesRetired,RetentionDeletionRequestResult? Request);
public sealed record RetentionBatchDeletionRequestResult(string BatchDeletionRequestId,string BatchState,string QueryUrl,
    DateTimeOffset CreatedAtUtc,DateTimeOffset ExpiresAtUtc,string? StopReason,bool? BatchResourcesRetired,bool NonAtomic,bool PerItemLocalConfirmation,
    bool RollbackSupported,string SuccessSemantics,int DispositionAcceptedCount,int NotRequestedCount,
    IReadOnlyList<RetentionBatchDeletionItemResult> Items,string SuggestedAction)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RetentionDeletionPolicy? Policy { get; init; }
}

/// <summary>At most five original single-item flows. The parent is not a Permit.</summary>
public sealed class RetentionBatchDeletionCoordinator:IDisposable
{
    private readonly object _gate=new();
    private readonly RetentionDeletionCoordinator _child;
    private readonly DeletionRequestStore _io;
    private readonly BatchDeletionStore _store;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop=new();
    private readonly DeletionQueryAdmission _queries;
    private Flight? _active;
    private Flight? _last;
    private Query? _query;
    internal Func<CancellationToken,Task> DeadlineForTests {get;set;}=c=>Task.Delay(TimeSpan.FromSeconds(3),c);
    internal Action<string,int>? CheckpointForTests {get;set;}
    internal RetentionBatchDeletionCoordinator(SqliteOperationalStore store,string sid,string session,string root,
        RetentionDeletionCoordinator child,TimeProvider? clock=null,DeletionLedgerDatabase? database=null,DeletionQueryAdmission? queries=null)
    {
        _clock=clock??TimeProvider.System;_child=child;_queries=queries??DeletionQueryAdmission.Host;
        _io=new(store,sid,session,root,_clock,database);_store=new(_io,sid,session,root,_clock);
    }
    public bool Supported=>!_stop.IsCancellationRequested&&_child.Supported;
    public string? HostShutdownBusyReason
    {
        get
        {
            lock(_gate){if(_active is not null)return "batch_deletion_in_flight";if(_query is not null)return "batch_deletion_query_pending";}
            if(_child.Admission.Active is not null)return _child.HostShutdownBusyReason??"deletion_admission_pending";
            return Volatile.Read(ref _queries.Busy)!=0?_child.HostShutdownBusyReason??"deletion_query_pending":null;
        }
    }
    public static void ValidateIds(IReadOnlyList<string> ids)
    {if(ids is null||ids.Count is <1 or >5||ids.Any(id=>!RetentionDeletionCoordinator.ValidRecordingId(id))||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count)throw new ArgumentException("Supply 1-5 distinct ordinary recording IDs.");}
    public Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids, string key,
        CancellationToken cancellation = default) => RequestAsync(ids, key, cancellation, null);

    public async Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids, string key,
        CancellationToken cancellation, RetentionDeletionPolicy? policy)
    {
        if (policy?.CompletedBefore > _clock.GetUtcNow()) throw new ArgumentException("Future cutoff.");
        var frozen=ids?.ToArray()!;ValidateIds(frozen);key=RetentionDeletionCoordinator.NormalizeKey(key);var digest=BatchDeletionStore.RequestDigest(frozen,policy);var keyHash=RetentionDeletionCoordinator.Digest(key);
        if(!Supported)throw new DeletionRequestUnavailableException("host_stopping");Flight? f;
        lock(_gate)
        {
            if(!Supported)throw new DeletionRequestUnavailableException("host_stopping");
            f=_active?.Key==keyHash?_active:null;
            if(f is not null&&f.Digest!=digest)throw new DeletionRequestConflictException();
            if(f is null&&_active is null)
            {
                var candidate=new Flight(this,keyHash,digest,frozen,policy,_clock.GetTimestamp(),_stop.Token);
                if(_child.Admission.Acquire(candidate)){_active=f=candidate;candidate.Work=Task.Run(()=>Run(candidate));}
                else candidate.Cancel.Dispose();
            }
        }
        if(f is not null)
        {
            try{return await f.Created.Task.WaitAsync(TimeSpan.FromSeconds(3),cancellation).ConfigureAwait(false);}
            catch(DeletionRequestConflictException){throw;}
            catch{throw new DeletionRequestUnavailableException(cancellation.IsCancellationRequested?"cancelled":"batch_creation_pending_or_unknown");}
        }
        var old=await QueryAsync(keyHash,true,cancellation).ConfigureAwait(false);
        if(old is null)throw new DeletionRequestUnavailableException("busy");if(old.Digest!=digest)throw new DeletionRequestConflictException();return Project(old);
    }
    public async Task<RetentionBatchDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default)
    {
        if(!DeletionLedger.Id(id))throw new ArgumentException();if(!Supported)throw new DeletionRequestUnavailableException("host_stopping");
        var facts=await QueryAsync(id,false,cancellation).ConfigureAwait(false);return facts is null?null:Project(facts);
    }
    private async Task<BatchDeletionFacts?> QueryAsync(string id,bool key,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stop.Token);Query q;
        lock(_gate)
        {
            if(_query is not null||Interlocked.CompareExchange(ref _queries.Busy,1,0)!=0){stop.Dispose();throw new DeletionRequestUnavailableException("query_busy");}
            _query=q=new(this,stop);Volatile.Write(ref _queries.Owner,q);
        }
        using var deadlineStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stop.Token);
        var queryStarted=_clock.GetTimestamp();
        var work=Task.Run(()=>
        {
            DeletionRequestIo<BatchDeletionFacts> value;
            try{value=_store.Run("query",false,stop.Token,(c,t)=>_store.Read(c,t,id,key),queryStarted);}catch{value=new(null,DeletionLedgerCommit.NotCommitted,false,false,null);}
            lock(_gate)if(value.Retired){if(ReferenceEquals(_query,q))_query=null;Volatile.Write(ref _queries.Owner,null);Volatile.Write(ref _queries.Busy,0);}
            q.Done.TrySetResult(value.Retired);stop.Dispose();return value;
        });
        try
        {
            if(await Task.WhenAny(work,DeadlineForTests(deadlineStop.Token)).ConfigureAwait(false)!=work)
            {try{stop.Cancel();}catch(ObjectDisposedException){}throw new DeletionRequestUnavailableException(cancellation.IsCancellationRequested?"cancelled":"query_timeout");}
            var result=await work.ConfigureAwait(false);if(!result.Usable)throw new DeletionRequestUnavailableException("query_unavailable");return result.Value;
        }
        finally{deadlineStop.Cancel();}
    }
    private BatchDeletionFacts Use(Flight f,DeletionRequestIo<BatchDeletionFacts> result,bool write)
    {
        f.Retired&=result.Retired;
        if(result.Error is DeletionRequestConflictException)throw result.Error;
        if(!result.Usable||write&&result.Commit!=DeletionLedgerCommit.Committed||result.Value is null)throw new DeletionRequestUnavailableException("batch_store_unknown");
        return result.Value;
    }
    private async Task Run(Flight f)
    {
        try
        {
            // A replay gets its original row and never a new child, UI or Permit.
            var old=UseLookup(f,_store.Run("lookup",false,f.Cancel.Token,(c,t)=>_store.Read(c,t,f.Key,true),f.Started));
            if(old is not null){if(old.Digest!=f.Digest)throw new DeletionRequestConflictException();f.Created.TrySetResult(Project(old));return;}
            f.Row=Use(f,_store.Run("create",true,f.Cancel.Token,(c,t)=>_store.Create(c,t,f.Id,f.Key,f.Ids,f.Policy),f.Started),true);
            f.Created.TrySetResult(Project(f.Row));
            if(f.Row.Id!=f.Id)return;
            // A backwards wall-clock jump cannot extend the ten-minute lifetime.
            var remaining=TimeSpan.FromMinutes(10)-_clock.GetElapsedTime(f.Started);
            var due=TimeSpan.FromTicks(Math.Max(0,Math.Min(remaining.Ticks,f.Row.Expires-_clock.GetUtcNow().UtcTicks)));
            using var expiry=_clock.CreateTimer(_=>{f.Expired=true;try{f.Cancel.Cancel();}catch(ObjectDisposedException){}},null,due,Timeout.InfiniteTimeSpan);
            CheckpointForTests?.Invoke("created",-1);
            var reason="all_dispositions_accepted";
            for(var n=0;n<f.Ids.Length;n++)
            {
                if(_clock.GetElapsedTime(f.Started)>=TimeSpan.FromMinutes(10)||_clock.GetUtcNow().UtcTicks>=f.Row.Expires)
                {f.Expired=true;f.Cancel.Cancel();}
                if(f.Cancel.IsCancellationRequested){reason=f.Expired?"batch_lifetime_expired":"host_stopping";break;}
                f.Row=Use(f,_store.Run("reserve",true,f.Cancel.Token,(c,t)=>_store.Reserve(c,t,f.Row,n)),true);
                CheckpointForTests?.Invoke("reserved",n);
                var item=f.Row.Items[n];
                f.Child = _child.StartBatchChild(item.RecordingId, item.ChildKey, item.ChildId, f, f.Cancel.Token,
                    f.Row.Policy, f.Row.Policy is null ? null : f.Row.Id);
                var createdOk=true;
                try
                {
                    var created=await f.Child.Created.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    if(created.DeletionRequestId!=item.ChildId||created.RecordingId!=item.RecordingId||
                        created.Policy!=f.Row.Policy)createdOk=false;
                }
                catch{createdOk=false;}
                // Public completion and timeout do not grant this next step.
                // Retain the admission while the exact original child retires.
                var completion=await f.Child.Retirement.ConfigureAwait(false);var retired=completion.Retired;f.Retired&=retired;f.Child=null;
                CheckpointForTests?.Invoke("child_retired",n);
                if(!retired){reason="child_retirement_unknown";break;}
                var facts=Use(f,_store.Run("observe",false,CancellationToken.None,(c,t)=>_store.Read(c,t,f.Row.Id,false)),false);
                var child=facts.Items[n].Child;
                var accepted=createdOk&&completion.CompletedSuccessfully&&BatchDeletionStore.Accepted(child);
                var policyStop = BatchDeletionStore.ChildStopReason(child);
                if (facts.Policy is null && !RetentionHostKeepLatestProtection.IsHostReason(policyStop)) policyStop = null;
                reason = accepted ? "all_dispositions_accepted" :
                    f.Expired ? "batch_lifetime_expired" :
                    f.Cancel.IsCancellationRequested ? "host_stopping" :
                    policyStop ?? (completion.LocalDecisionState == "busy" ? "child_busy" : "child_non_success_or_unknown");
                f.Row=Use(f,_store.Run("itemfinish",true,CancellationToken.None,(c,t)=>_store.FinishItem(c,t,f.Row,n,accepted,accepted?"disposition_accepted":reason,true)),true);
                CheckpointForTests?.Invoke("item_committed",n);
                if(!accepted)break;
            }
            f.Row=Use(f,_store.Run("finish",true,CancellationToken.None,(c,t)=>_store.Finish(c,t,f.Row,reason)),true);
            CheckpointForTests?.Invoke("finished",-1);
        }
        catch(Exception e){f.Created.TrySetException(e is DeletionRequestConflictException?e:new DeletionRequestUnavailableException("batch_store_unknown"));}
        finally
        {
            // An injected exception between dispatch and wait must still root
            // and await the actual child; a failed close stays busy indefinitely.
            if(f.Child is not null){try{f.Retired&=(await f.Child.Retirement.ConfigureAwait(false)).Retired;}catch{f.Retired=false;}}
            f.Created.TrySetException(new DeletionRequestUnavailableException("batch_store_unknown"));
            lock(_gate){_last=f;if(f.Retired){if(ReferenceEquals(_active,f))_active=null;_child.Admission.Release(f);}}
            f.Cancel.Dispose();f.Done.TrySetResult(f.Retired);
        }
    }
    private BatchDeletionFacts? UseLookup(Flight f,DeletionRequestIo<BatchDeletionFacts> r)
    {f.Retired&=r.Retired;if(!r.Usable)throw new DeletionRequestUnavailableException("batch_store_unknown");return r.Value;}
    private RetentionBatchDeletionRequestResult Project(BatchDeletionFacts row)
    {
        lock(_gate)
        {
            var live=_active?.Id==row.Id&&row.Host==DeletionLedger.HostId?_active:null;
            var retired=live is {Retired:false}?false:_last?.Id==row.Id&&_last.Retired?true:(bool?)null;
            var state=row.Phase=="finished"?row.Reason=="all_dispositions_accepted"?"dispositions_accepted":"stopped":live is null?"interrupted":live.Work?.IsCompleted==true?"unknown":live.Cancel.IsCancellationRequested?"stopping":"active";
            if(live is {Retired:false})state="unknown";
            var reason=row.Reason??(state=="interrupted"?"host_lifetime_interrupted":state=="unknown"?"batch_resources_or_store_unknown":live?.Expired==true?"batch_lifetime_expired":null);
            var items=row.Items.Select(i=>new RetentionBatchDeletionItemResult(i.Ordinal+1,i.RecordingId,
                i.Phase=="not_requested"?"not_requested":i.Phase=="dispatch_reserved"?"requested_or_unknown":i.Outcome=="accepted"?"disposition_accepted":"non_success",
                i.Reason??(i.Phase=="not_requested"?reason??"awaiting_previous_item":state=="interrupted"?"interrupted_no_execution_recovery":"query_original_child"),
                i.Phase=="not_requested"?null:i.ChildId,i.Retired,i.Child is null?null:_child.Project(i.Child))).ToArray();
            return new(row.Id,state,"/api/v1/retention/batch-deletion-requests/"+row.Id,new(row.Created,TimeSpan.Zero),new(row.Expires,TimeSpan.Zero),reason,retired,true,true,false,
                "os_disposition_request_accepted_not_physical_space_reclaimed",items.Count(i=>i.Request?.DispositionProgress=="accepted"),items.Count(i=>i.State=="not_requested"),Array.AsReadOnly(items),
                state=="active"?"wait_for_each_local_confirmation_then_query":
                reason==RetentionHostKeepLatestProtection.InvalidConfigurationReason?"fix_host_keep_latest_configuration_and_restart":"inspect_partial_results_do_not_resume_or_retry_unknown") { Policy = row.Policy };
        }
    }
    public void Dispose()=>_stop.Cancel();
    public async Task<bool> WaitForIdleAsync()
    {
        Flight? f;Query? q;lock(_gate){f=_active;q=_query;}
        f??=_child.Admission.Active as Flight;q??=Volatile.Read(ref _queries.Owner) as Query;
        var a=f is not null?await f.Done.Task.ConfigureAwait(false):_child.Admission.Active is null||_child.OwnsRequest&&await _child.WaitForIdleAsync().ConfigureAwait(false);
        var b=q is not null?await q.Done.Task.ConfigureAwait(false):Volatile.Read(ref _queries.Busy)==0||_child.OwnsQuery&&await _child.WaitForIdleAsync().ConfigureAwait(false);
        return a&&b;
    }
    private sealed class Flight(RetentionBatchDeletionCoordinator owner,string key,string digest,string[] ids,
        RetentionDeletionPolicy? policy,long started,CancellationToken stop)
    {
        private readonly RetentionBatchDeletionCoordinator _owner=owner; // Root unknown DB/native ownership across gateway recreation.
        internal readonly string Id=Guid.NewGuid().ToString("N"),Key=key,Digest=digest;
        internal readonly long Started=started;
        internal readonly RetentionDeletionPolicy? Policy = policy;
        internal readonly string[] Ids=ids;internal readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(stop);
        internal readonly TaskCompletionSource<RetentionBatchDeletionRequestResult> Created=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Work;internal BatchDeletionFacts? Row;internal RetentionDeletionCoordinator.BatchChild? Child;internal bool Retired=true;internal volatile bool Expired;
    }
    private sealed class Query(RetentionBatchDeletionCoordinator owner,CancellationTokenSource stop)
    {private readonly RetentionBatchDeletionCoordinator _owner=owner;internal readonly CancellationTokenSource Stop=stop;internal readonly TaskCompletionSource<bool> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);}
}
