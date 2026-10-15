using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

public sealed record RetentionDeletionRequestResult(string DeletionRequestId,string RecordingId,string RequestState,
    string LocalConfirmationState,string PersistedRequestPhase,string QueryUrl,string? OperationId,string? DispositionProgress,
    string? ReasonCode,int? Win32Error,bool? FileActionPerformed,bool? OriginalResourcesRetired,bool? LedgerResourcesRetired,
    bool? OurResourcesRetired,string? PreparationCommit,string? LedgerSettlement,string? PersistedDispositionProgress,
    string RequestWriteCommit,bool? RequestResourcesRetired,string SuggestedAction);

internal sealed class DeletionRequestAdmission
{
    internal static readonly DeletionRequestAdmission Host=new();
    private object? _active;
    internal bool Acquire(object f)=>Interlocked.CompareExchange(ref _active,f,null) is null;
    internal void Release(object f)=>Interlocked.CompareExchange(ref _active,null,f);
}
internal sealed class DeletionQueryAdmission
{internal static readonly DeletionQueryAdmission Host=new();internal int Busy;}

/// <summary>One host-owned local deletion flow. HTTP lifetime never owns its Permit or resources.</summary>
public sealed class RetentionDeletionCoordinator:IDisposable
{
    private readonly object _gate=new();
    private readonly LocalRetentionApproval _authority;
    private readonly DeletionRequestStore _store;
    private readonly DeletionRequestAdmission _admission;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop=new();
    private Flight? _active;
    private Flight? _last;
    private QueryFlight? _query;
    private readonly DeletionQueryAdmission _queryAdmission;
    internal Func<CancellationToken,Task> DeadlineForTests {get;set;}=c=>Task.Delay(TimeSpan.FromSeconds(3),c);
    internal RetentionDeletionCoordinator(SqliteOperationalStore store,string sid,string session,string root,LocalRetentionApproval authority,
        TimeProvider? clock=null,DeletionLedgerDatabase? database=null,DeletionRequestAdmission? admission=null,DeletionQueryAdmission? queryAdmission=null)
    { _authority=authority;_clock=clock??TimeProvider.System;_store=new(store,sid,session,root,_clock,database);_admission=admission??DeletionRequestAdmission.Host;_queryAdmission=queryAdmission??DeletionQueryAdmission.Host; }
    public bool Supported=>!_stop.IsCancellationRequested;
    public static bool ValidRecordingId(string? id)=>id is {Length:>0 and <=200}&&!id.Any(char.IsControl)&&id.IndexOfAny(new[]{'/','\\','%',':','?','#'})<0&&UnifiedHistoryQueryService.ValidId(id);
    public static string NormalizeKey(string? key)
    {
        if(key is null||key.Length>128)throw new ArgumentException("Idempotency-Key is required and bounded.");var s=key.Trim();
        if(s.Length is <1 or >128||s.Any(c=>c<'!'||c>'~'||c is '/' or '\\' or ','))throw new ArgumentException("A single printable Idempotency-Key of 1-128 characters is required.");return s;
    }
    internal static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string RequestHash(string recordingId)=>Digest("single_ordinary_main_mp4/v1\n"+recordingId);
    public async Task<RetentionDeletionRequestResult> RequestAsync(string recordingId,string key,CancellationToken cancellation=default)
    {
        if(!ValidRecordingId(recordingId))throw new ArgumentException("Invalid recording id.");key=NormalizeKey(key);var hash=Digest(key);var requestHash=RequestHash(recordingId);
        if(!Supported)throw new DeletionRequestUnavailableException("host_stopping");
        // Join a creation already owned by this exact authority, without a second
        // SQL query/window and without tying its lifetime to the retry's socket.
        Flight? same;lock(_gate)same=_active?.Key==hash?_active:null;
        if(same is not null){if(same.Hash!=requestHash)throw new DeletionRequestConflictException();return await AwaitCreated(same,cancellation).ConfigureAwait(false);}
        Flight? flight=null;
        lock(_gate)
        {
            if(_stop.IsCancellationRequested)throw new DeletionRequestUnavailableException("host_stopping");
            if(_active is not null)
            {if(_active.Key==hash){if(_active.Hash!=requestHash)throw new DeletionRequestConflictException();flight=_active;}}
            else
            {
                flight=new(this,recordingId,hash,requestHash,_clock.GetTimestamp());
                if(_admission.Acquire(flight)){_active=flight;var owned=flight;flight.Work=Task.Run(()=>Run(owned));}else flight=null;
            }
        }
        if(flight is null)
        {
            var old=await QueryAsync(hash,true,cancellation).ConfigureAwait(false);
            if(old is null)throw new DeletionRequestUnavailableException("busy");if(old.Row.RequestHash!=requestHash)throw new DeletionRequestConflictException();return Project(old);
        }
        return await AwaitCreated(flight,cancellation).ConfigureAwait(false);
    }
    private async Task<RetentionDeletionRequestResult> AwaitCreated(Flight f,CancellationToken cancellation)
    {
        try{return await f.Created.Task.WaitAsync(TimeSpan.FromSeconds(3),cancellation).ConfigureAwait(false);}
        catch(DeletionRequestConflictException){throw;}
        catch{throw new DeletionRequestUnavailableException(cancellation.IsCancellationRequested?"cancelled":"request_pending_or_unknown");}
    }
    public async Task<RetentionDeletionRequestResult?> ReadAsync(string requestId,CancellationToken cancellation=default)
    {
        if(!DeletionLedger.Id(requestId))throw new ArgumentException("Invalid deletion request id.");
        if(!Supported)throw new DeletionRequestUnavailableException("host_stopping");
        var facts=await QueryAsync(requestId,false,cancellation).ConfigureAwait(false);return facts is null?null:Project(facts);
    }
    private async Task<DeletionRequestFacts?> QueryAsync(string id,bool key,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested)throw new DeletionRequestUnavailableException("cancelled");
        var started=_clock.GetTimestamp();var stop=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token,cancellation);QueryFlight q;
        lock(_gate)
        {
            if(_query is not null||Interlocked.CompareExchange(ref _queryAdmission.Busy,1,0)!=0){stop.Dispose();throw new DeletionRequestUnavailableException("query_busy");}
            q=new(stop);_query=q;
        }
        using var deadlineStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stop.Token);
        var work=Task.Run(()=>
        {
            DeletionRequestIo<DeletionRequestFacts> result;
            try{result=_store.Run("query",false,started,stop.Token,(c,t)=>_store.Facts(c,t,id,key));}
            catch{result=new(null,DeletionLedgerCommit.NotCommitted,false,false,null);}
            lock(_gate){q.Retired=result.Retired;if(result.Retired){if(ReferenceEquals(_query,q))_query=null;Volatile.Write(ref _queryAdmission.Busy,0);}}
            q.Done.TrySetResult(result.Retired);stop.Dispose();return result;
        });
        try
        {
            var deadline=DeadlineForTests(deadlineStop.Token);
            if(await Task.WhenAny(work,deadline).ConfigureAwait(false)!=work)
            {try{stop.Cancel();}catch(ObjectDisposedException){}throw new DeletionRequestUnavailableException(cancellation.IsCancellationRequested?"cancelled":"query_timeout");}
            var r=await work.ConfigureAwait(false);if(!r.Usable)throw new DeletionRequestUnavailableException("query_unavailable");return r.Value;
        }
        finally{deadlineStop.Cancel();}
    }
    private async Task Run(Flight f)
    {
        var retired=true;LocalDeletionResult? outcome=null;
        try
        {
            var lookup=_store.Run("lookup",false,f.Started,_stop.Token,(c,t)=>_store.Facts(c,t,f.Key,true));
            retired&=lookup.Retired;lock(_gate)f.RequestRetired=lookup.Retired;
            if(!lookup.Usable)throw new DeletionRequestUnavailableException("request_store_unknown");
            if(lookup.Value is not null)
            {if(lookup.Value.Row.RequestHash!=f.Hash)throw new DeletionRequestConflictException();lock(_gate){f.Row=lookup.Value.Row;f.LastCommit=DeletionLedgerCommit.Committed;}f.Created.TrySetResult(Project(lookup.Value));return;}
            lock(_gate){f.RequestRetired=null;f.LastCommit=DeletionLedgerCommit.Unknown;}
            var created=_store.Run("create",true,f.Started,_stop.Token,(c,t)=>_store.Create(c,t,f.Id,f.Recording,f.Key,f.Hash));
            retired&=created.Retired;lock(_gate){f.LastCommit=created.Commit;f.RequestRetired=created.Retired;}
            if(created.Error is DeletionRequestConflictException)throw created.Error;
            if(!created.Usable||created.Commit!=DeletionLedgerCommit.Committed||created.Value is null)throw new DeletionRequestUnavailableException("request_store_unknown");
            lock(_gate){f.Row=created.Value.Row;f.CreatedHere=created.Value.Created;}f.Created.TrySetResult(Project(new(f.Row,null)));
            if(!created.Value.Created)return; // Persisted replay never re-enters the UI.
            if(_stop.IsCancellationRequested)return;
            var session=_authority.Request(f.Recording,_stop.Token);lock(_gate)f.Session=session;
            var decision=await f.Session.Response.ConfigureAwait(false);
            if(decision.Permit is not null)
            {
                lock(_gate){f.RequestRetired=null;f.LastCommit=DeletionLedgerCommit.Unknown;}
                var reserve=_store.Run("reserve",true,_clock.GetTimestamp(),_stop.Token,
                    (c,t)=>_store.Update(c,t,f.Row,decision.Permit.ApprovalId,"approved",null,false));
                retired&=reserve.Retired;lock(_gate){f.LastCommit=reserve.Commit;f.RequestRetired=reserve.Retired;}
                if(!reserve.Usable||reserve.Commit!=DeletionLedgerCommit.Committed||reserve.Value is null)return;
                lock(_gate)f.Row=reserve.Value;
                // No clock is restarted and no persistent row is exchanged for
                // a Permit. RequestDeletion checks the same actual Session again.
                var operation=_authority.RequestDeletion(decision.Permit,f.Recording);lock(_gate)f.Operation=operation;
                outcome=await f.Operation.Retirement.ConfigureAwait(false);
                retired&=outcome.OurResourcesRetired==true;
            }
            else f.Session.Dispose();
            retired&=await f.Session.Retirement.ConfigureAwait(false);
            if(retired)
            {
                var local=LocalName(f.Session.Current.State);
                lock(_gate){f.RequestRetired=null;f.LastCommit=DeletionLedgerCommit.Unknown;}
                var finish=_store.Run("finish",true,_clock.GetTimestamp(),CancellationToken.None,
                    (c,t)=>_store.Update(c,t,f.Row,f.Row.ApprovalId,local,outcome,true));
                retired&=finish.Retired;lock(_gate){f.LastCommit=finish.Commit;f.RequestRetired=finish.Retired;
                if(finish.Commit==DeletionLedgerCommit.Committed&&finish.Value is not null)f.Row=finish.Value;}
            }
        }
        catch(Exception e){f.Created.TrySetException(e is DeletionRequestConflictException?e:new DeletionRequestUnavailableException("request_store_unknown"));}
        finally
        {
            if(f.Session is not null)
            {f.Session.Dispose();try{retired&=await f.Session.Retirement.ConfigureAwait(false);}catch{retired=false;}}
            f.Created.TrySetException(new DeletionRequestUnavailableException("request_store_unknown"));
            lock(_gate){f.Retired=retired;if(retired){if(f.Session is not null)_last=f;if(ReferenceEquals(_active,f))_active=null;_admission.Release(f);}}
            f.Done.TrySetResult(retired);
        }
    }
    private RetentionDeletionRequestResult Project(DeletionRequestFacts facts)
    {
        lock(_gate)
        {
        var row=facts.Row;var ledger=facts.Ledger;var live=_active?.Row?.Id==row.Id?_active:_last?.Row?.Id==row.Id?_last:null;
        if(live is not null&&(!live.CreatedHere||row.Host!=DeletionLedger.HostId))live=null;
        var actual=live?.Operation?.Current;var linked=ledger is not null;
        var progress=linked?(actual?.Progress??ledger!.Progress):null;
        string? native=progress switch{LocalDeletionProgress.NotAttempted=>"not_attempted",LocalDeletionProgress.DispositionAccepted=>"accepted",LocalDeletionProgress.DispositionRejected=>"rejected",LocalDeletionProgress.InFlight or LocalDeletionProgress.Indeterminate=>"unknown",_=>null};
        var state=row.Phase switch{"awaiting_confirmation"=>live is null?"confirmation_invalidated":live.Work?.IsCompleted==true?"unknown":"awaiting_local_confirmation","execution_reserved"=>live is null||live.Retired||live.Work?.IsCompleted==true?"unknown":"execution_reserved",_=>"finished"};
        if(live?.RequestRetired==false&&live.Work?.IsCompleted==true)state="unknown";
        var local=row.Phase=="awaiting_confirmation"&&live is null?"invalidated":live?.Session is null?row.LocalState:LocalName(live.Session.Current.State);
        var reason=linked?(actual?.Reason??ledger!.Reason)?.ToString().ToLowerInvariant():local;
        return new(row.Id,row.RecordingId,state,local,row.Phase,"/api/v1/retention/deletion-requests/"+row.Id,ledger?.Binding.OperationId,native,reason,
            linked?actual?.Win32Error??ledger!.Win32Error:null,native is null or "unknown"?null:native=="accepted",
            linked?actual?.Retirement?.Retired??row.RootRetired??ledger!.OurResourcesRetired:null,linked?actual?.LedgerResourcesRetired??row.LedgerRetired:null,
            live is null?null:live.Retired?true:actual?.OurResourcesRetired==false?false:null,
            linked?actual is null?row.Preparation??"committed":DeletionRequestStore.CommitName(actual.Preparation):null,
            linked?actual is null?ledger!.State==DeletionRecoveryState.Settled?"committed":row.Settlement??"not_committed":DeletionRequestStore.CommitName(actual.Settlement):null,
            ledger?.Progress switch{LocalDeletionProgress.NotAttempted=>"not_attempted",LocalDeletionProgress.DispositionAccepted=>"accepted",LocalDeletionProgress.DispositionRejected=>"rejected",LocalDeletionProgress.Indeterminate=>"unknown",_=>null},
            live is null?"committed":DeletionRequestStore.CommitName(live.LastCommit),live?.RequestRetired,
            state=="awaiting_local_confirmation"?"wait_for_local_confirmation_then_query":state=="execution_reserved"?"query_same_request":state is "unknown" or "confirmation_invalidated"?"query_same_request_do_not_replay_execution":"inspect_result_do_not_retry_unknown");
        }
    }
    private static string LocalName(LocalRetentionState s)=>s switch{LocalRetentionState.Pending=>"pending",LocalRetentionState.Approved or LocalRetentionState.Consumed=>"approved",LocalRetentionState.Rejected=>"rejected",LocalRetentionState.Expired=>"expired",LocalRetentionState.Cancelled=>"cancelled",LocalRetentionState.Busy=>"busy",_=>"unavailable"};
    public void Dispose(){_stop.Cancel();_authority.Dispose();}
    public async Task<bool> WaitForIdleAsync()
    {Flight? f;QueryFlight? q;lock(_gate){f=_active;q=_query;}var a=f is null||await f.Done.Task.ConfigureAwait(false);var b=q is null||await q.Done.Task.ConfigureAwait(false);return a&&b;}
    private sealed class Flight(RetentionDeletionCoordinator owner,string recording,string key,string hash,long started)
    {
        internal readonly string Id=Guid.NewGuid().ToString("N"),Recording=recording,Key=key,Hash=hash;
        internal readonly long Started=started;
        internal readonly TaskCompletionSource<RetentionDeletionRequestResult> Created=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DeletionRequestRow? Row;internal LocalRetentionApproval.Session? Session;internal LocalRetentionApproval.DeletionOperation? Operation;
        internal Task? Work;internal bool Retired,CreatedHere;internal bool? RequestRetired;internal DeletionLedgerCommit LastCommit;
        // Keep the actual authority rooted even when its provider retirement is unknown.
        private readonly RetentionDeletionCoordinator _owner=owner;
    }
    private sealed class QueryFlight(CancellationTokenSource stop)
    {internal readonly CancellationTokenSource Stop=stop;internal bool Retired;internal readonly TaskCompletionSource<bool> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);}
}
