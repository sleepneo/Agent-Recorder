using System.Text.Json;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record BatchDeletionItem(int Ordinal,string RecordingId,string ChildId,string ChildKey,string Phase,
    string? Outcome,string? Reason,bool? Retired,DeletionRequestFacts? Child=null);
internal sealed record BatchDeletionFacts(string Id,string Sid,string Session,string Root,string Host,string KeyHash,string Digest,string BindingDigest,
    int Count,long Created,long Expires,string Phase,string? Reason,long Updated,IReadOnlyList<BatchDeletionItem> Items)
{
    internal RetentionDeletionPolicy? Policy { get; init; }
}

// The original deletion request I/O owner supplies FULL/WAL, three-second DB
// budget, exact commit classification and unknown-close retention for this table.
internal sealed class BatchDeletionStore(DeletionRequestStore io,string sid,string session,string root,TimeProvider clock)
{
    internal static string RequestDigest(IReadOnlyList<string> ids)=>RetentionDeletionCoordinator.Digest("ordered_per_item_local_deletion/v1\n"+JsonSerializer.Serialize(ids));
    internal static string RequestDigest(IReadOnlyList<string> ids, RetentionDeletionPolicy? policy) =>
        policy is null ? RequestDigest(ids) : RetentionDeletionCoordinator.Digest(
            "ordered_per_item_local_deletion/policy/v1\n" + RequestDigest(ids) + "\n" + policy.Digest);
    internal DeletionRequestIo<T> Run<T>(string stage,bool write,CancellationToken cancel,Func<SqliteConnection,SqliteTransaction,T?> action,long? started=null) where T:class
        =>io.Run("batch"+stage,write,started??clock.GetTimestamp(),cancel,action);
    private static string Bindings(string id, string sid, string session, string root, string host,
        IEnumerable<BatchDeletionItem> items, RetentionDeletionPolicy? policy)
    {
        var legacy = JsonSerializer.Serialize(new { id, sid, session, root, host,
            items = items.Select(i => new { i.Ordinal, i.RecordingId, i.ChildId, i.ChildKey }) });
        return RetentionDeletionCoordinator.Digest(policy is null ? legacy :
            "ordered_deletion_binding/policy/v1\n" + policy.Digest + "\n" + legacy);
    }

    internal BatchDeletionFacts Create(SqliteConnection c,SqliteTransaction t,string id,string key,IReadOnlyList<string> ids,
        RetentionDeletionPolicy? policy)
    {
        var digest = RequestDigest(ids, policy);
        var old=Read(c,t,key,true);if(old is not null){if(old.Digest!=digest)throw new DeletionRequestConflictException();return old;}
        var now=clock.GetUtcNow().UtcTicks;var items=ids.Select((recording,n)=>new BatchDeletionItem(n,recording,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"not_requested",null,null,null)).ToArray();
        if (policy?.CompletedBefore.UtcTicks > now) throw new InvalidDataException("Batch policy cutoff is in the future.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="INSERT INTO ordinary_deletion_batches(batch_id,owner_sid,owner_session,videos_root,host_id,key_digest,request_digest,binding_digest,item_count,created_ticks,expires_ticks,phase,stop_reason,updated_ticks) VALUES($id,$sid,$session,$root,$host,$key,$hash,$binding,$count,$now,$expires,'active',NULL,$now);";
        Add(cmd,("$id",id),("$sid",sid),("$session",session),("$root",root),("$host",DeletionLedger.HostId),("$key",key),("$hash",digest),("$binding",Bindings(id,sid,session,root,DeletionLedger.HostId,items,policy)),("$count",ids.Count),("$now",now),("$expires",checked(now+TimeSpan.FromMinutes(10).Ticks)));
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
        for(var n=0;n<ids.Count;n++)
        {
            cmd.Parameters.Clear();cmd.CommandText="INSERT INTO ordinary_deletion_batch_items(batch_id,ordinal,recording_id,child_request_id,child_key,phase) VALUES($id,$n,$recording,$child,$key,'not_requested');";
            Add(cmd,("$id",id),("$n",n),("$recording",ids[n]),("$child",items[n].ChildId),("$key",items[n].ChildKey));if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
        }
        RetentionDeletionPolicy.Store(c, t, "batch", id, policy);
        return Read(c,t,id,false)??throw new InvalidDataException();
    }
    internal BatchDeletionFacts Reserve(SqliteConnection c,SqliteTransaction t,BatchDeletionFacts expected,int ordinal)
    {
        var current=Read(c,t,expected.Id,false)??throw new InvalidDataException();Compare(expected,current);
        if(current.Phase!="active"||current.Items[ordinal].Phase!="not_requested"||current.Items.Take(ordinal).Any(i=>i.Outcome!="accepted"||i.Retired!=true)||clock.GetUtcNow().UtcTicks>=current.Expires)throw new InvalidDataException();
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE ordinary_deletion_batch_items SET phase='dispatch_reserved' WHERE batch_id=$id AND ordinal=$n AND phase='not_requested';";Add(cmd,("$id",expected.Id),("$n",ordinal));if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
        return Read(c,t,expected.Id,false)!;
    }
    internal BatchDeletionFacts FinishItem(SqliteConnection c,SqliteTransaction t,BatchDeletionFacts expected,int ordinal,bool accepted,string reason,bool retired)
    {
        var current=Read(c,t,expected.Id,false)??throw new InvalidDataException();Compare(expected,current);
        var item=current.Items[ordinal];
        if(item.Phase!="dispatch_reserved")throw new InvalidDataException();
        // A success must be backed by the exact original request+ledger in this
        // transaction, as well as the caller's actual live retirement evidence.
        if(accepted&&(!retired||!Accepted(item.Child)))throw new InvalidDataException();
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE ordinary_deletion_batch_items SET phase='finished',outcome=$outcome,reason=$reason,resources_retired=$retired WHERE batch_id=$id AND ordinal=$n AND phase='dispatch_reserved';";
        Add(cmd,("$outcome",accepted?"accepted":"non_success"),("$reason",reason),("$retired",retired?1:0),("$id",expected.Id),("$n",ordinal));if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
        return Read(c,t,expected.Id,false)!;
    }
    internal BatchDeletionFacts Finish(SqliteConnection c,SqliteTransaction t,BatchDeletionFacts expected,string reason)
    {
        var current=Read(c,t,expected.Id,false)??throw new InvalidDataException();Compare(expected,current);
        if(current.Phase!="active"||reason=="all_dispositions_accepted"&&current.Items.Any(i=>i.Outcome!="accepted"||i.Retired!=true))throw new InvalidDataException();
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE ordinary_deletion_batches SET phase='finished',stop_reason=$reason,updated_ticks=$now WHERE batch_id=$id AND phase='active';";
        Add(cmd,("$id",expected.Id),("$reason",reason),("$now",clock.GetUtcNow().UtcTicks));if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();return Read(c,t,expected.Id,false)!;
    }
    internal static bool Accepted(DeletionRequestFacts? f)=>f is {Row.Phase:"finished",Row.LocalState:"approved",Row.RootRetired:true,Row.LedgerRetired:true,Row.Preparation:"committed",Row.Settlement:"committed",
        Ledger.Progress:LocalDeletionProgress.DispositionAccepted,Ledger.OurResourcesRetired:true,Ledger.State:DeletionRecoveryState.Settled};

    internal static string? ChildStopReason(DeletionRequestFacts? child)
    {
        if (child?.Row.Phase != "finished") return null;
        return child.Row.PolicyReason ?? child.Ledger?.PolicyReason ?? (child.Row.LocalState switch
        {
            "rejected" => "local_confirmation_rejected",
            "expired" => "local_confirmation_expired",
            "cancelled" => "local_confirmation_cancelled",
            _ => null
        });
    }

    private static bool IsSpecificChildStop(string? reason) => reason is
        "within_keep_latest" or "completion_not_before_cutoff" or
        "local_confirmation_rejected" or "local_confirmation_expired" or "local_confirmation_cancelled";

    private static void Compare(BatchDeletionFacts a,BatchDeletionFacts b)
    {
        if(a with {Items=Array.Empty<BatchDeletionItem>()}!=b with {Items=Array.Empty<BatchDeletionItem>()}||
            !a.Items.Select(i=>i with {Child=null}).SequenceEqual(b.Items.Select(i=>i with {Child=null})))throw new InvalidDataException("Batch state changed.");
    }
    internal BatchDeletionFacts? Read(SqliteConnection c,SqliteTransaction t,string id,bool key)
    {
        var row = ReadBindings(c, t, id, key, sid, session, root);
        if (row is null) return null;
        var items = row.Items.ToArray();
        for (var n = 0; n < items.Length; n++)
        {
            var item = items[n];
            var child = io.Facts(c, t, item.ChildId, false);
            var keyHash = RetentionDeletionCoordinator.Digest(item.ChildKey);
            var byKey = io.Facts(c, t, keyHash, true);
            if (child is null && row.Policy is not null && HasPolicyChildLink(c, t, item.ChildId))
                throw new InvalidDataException("The linked child request is missing.");
            if (child?.Row != byKey?.Row || child is not null &&
                (child.Row.Id != item.ChildId || child.Row.KeyHash != keyHash || child.Row.RecordingId != item.RecordingId ||
                 child.Row.Host != row.Host || child.Row.Created < row.Created || child.Row.Policy != row.Policy ||
                 child.Row.PolicyBatchId != (row.Policy is null ? null : row.Id)) ||
                item.Phase == "not_requested" && child is not null)
                throw new InvalidDataException("Child differs from its batch binding.");
            if (item.Outcome == "accepted" && (item.Retired != true || !Accepted(child)))
                throw new InvalidDataException("Accepted child evidence is unavailable.");
            if ((RetentionHostKeepLatestProtection.IsHostReason(item.Reason) || row.Policy is not null && IsSpecificChildStop(item.Reason)) &&
                item.Reason != ChildStopReason(child))
                throw new InvalidDataException("Child stop reason differs from durable evidence.");
            items[n] = item with { Child = child };
        }

        if ((RetentionHostKeepLatestProtection.IsHostReason(row.Reason) || row.Policy is not null && IsSpecificChildStop(row.Reason)) &&
            row.Reason != items.FirstOrDefault(i => i.Outcome != "accepted")?.Reason)
            throw new InvalidDataException("Batch stop reason differs from durable evidence.");
        return row with { Items = Array.AsReadOnly(items) };
    }

    private static bool HasPolicyChildLink(SqliteConnection c, SqliteTransaction t, string id)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = "SELECT 1 FROM ordinary_deletion_batch_policy_children WHERE child_id=$child;";
        Add(command, ("$child", id));
        return command.ExecuteScalar() is not null;
    }

    internal static void BindPolicyChild(SqliteConnection c, SqliteTransaction t, DeletionRequestRow child, string batchId)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = "INSERT INTO ordinary_deletion_batch_policy_children(child_id,batch_id) VALUES($child,$batch);";
        Add(command, ("$child", child.Id), ("$batch", batchId));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException();
        if (ValidatePolicyChild(c, t, child, requireDispatch: true) != batchId)
            throw new InvalidDataException("Child reservation is unavailable.");
    }

    // Read only the frozen parent and reservation here. The full batch query
    // traverses child requests and ledgers, which in turn validate this backlink.
    internal static string? ValidatePolicyChild(SqliteConnection c, SqliteTransaction t, DeletionRequestRow child,
        bool requireDispatch)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = """
            SELECT (SELECT batch_id FROM ordinary_deletion_batch_items WHERE child_request_id=$child),
                   (SELECT batch_id FROM ordinary_deletion_batch_policy_children WHERE child_id=$child);
            """;
        Add(command, ("$child", child.Id));
        string? memberBatch;
        string? linkedBatch;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException();
            memberBatch = Optional(reader, 0);
            linkedBatch = Optional(reader, 1);
        }
        if (memberBatch is null && linkedBatch is null) return null;
        if (memberBatch is null || linkedBatch is not null && linkedBatch != memberBatch)
            throw new InvalidDataException("Batch child policy link differs from its reservation.");

        var parent = ReadBindings(c, t, memberBatch, false, child.Sid, child.Session, child.Root)
            ?? throw new InvalidDataException("Batch parent is missing.");
        if (parent.Policy is null && linkedBatch is null && child.Policy is null) return null;
        var item = parent.Items.SingleOrDefault(i => i.ChildId == child.Id);
        if (linkedBatch is null || child.Policy is null || item is null || parent.Policy != child.Policy || parent.Host != child.Host ||
            parent.Created > child.Created || item.RecordingId != child.RecordingId ||
            RetentionDeletionCoordinator.Digest(item.ChildKey) != child.KeyHash || item.Phase == "not_requested" ||
            requireDispatch && (parent.Phase != "active" || item.Phase != "dispatch_reserved"))
            throw new InvalidDataException("Batch child policy differs from the frozen parent.");
        return linkedBatch;
    }

    private static BatchDeletionFacts? ReadBindings(SqliteConnection c, SqliteTransaction t, string id, bool key,
        string sid, string session, string root)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        var names=new[]{"batch_id","owner_sid","owner_session","videos_root","host_id","key_digest","request_digest","binding_digest","item_count","created_ticks","expires_ticks","phase","stop_reason","updated_ticks"};
        cmd.CommandText="SELECT "+string.Join(',',names.Select(n=>n is "item_count" or "created_ticks" or "expires_ticks" or "updated_ticks"?n:SafeText(n)))+" FROM ordinary_deletion_batches "+(key?"INDEXED BY idx_deletion_batch_key ":"")+"WHERE "+(key?"key_digest":"batch_id")+" COLLATE BINARY=$id AND owner_sid COLLATE BINARY=$sid AND owner_session COLLATE BINARY=$session AND videos_root COLLATE BINARY=$root;";
        Add(cmd,("$id",id),("$sid",sid),("$session",session),("$root",root));BatchDeletionFacts row;
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read())return null;
            row=new(Text(r,0),Text(r,1),Text(r,2),Text(r,3),Text(r,4),Text(r,5),Text(r,6),Text(r,7),checked((int)Number(r,8)),Number(r,9),Number(r,10),Text(r,11),Optional(r,12),Number(r,13),Array.Empty<BatchDeletionItem>());
            if(r.Read())throw new InvalidDataException();
        }
        if(!DeletionLedger.Id(row.Id)||!DeletionLedger.Id(row.Host)||row.Sid!=sid||row.Session!=session||row.Root!=root||
            !UnifiedHistoryQueryService.ValidIdentity(sid)||!UnifiedHistoryQueryService.ValidIdentity(session)||root.Length is <4 or >1024||
            !DeletionRequestStore.Hash(row.KeyHash)||!DeletionRequestStore.Hash(row.Digest)||!DeletionRequestStore.Hash(row.BindingDigest)||row.Count is <1 or >5||row.Created<0||
            row.Expires-row.Created!=TimeSpan.FromMinutes(10).Ticks||row.Expires>DateTimeOffset.MaxValue.UtcTicks||row.Updated<row.Created||row.Updated>DateTimeOffset.MaxValue.UtcTicks||
            row.Phase is not("active" or "finished")||row.Phase=="active"&&row.Reason is not null||row.Phase=="finished"&&!ValidReason(row.Reason))throw new InvalidDataException();
        cmd.Parameters.Clear();cmd.CommandText="SELECT ordinal,"+string.Join(',',new[]{"recording_id","child_request_id","child_key","phase","outcome","reason"}.Select(SafeText))+",resources_retired FROM ordinary_deletion_batch_items WHERE batch_id COLLATE BINARY=$id ORDER BY ordinal LIMIT 6;";Add(cmd,("$id",row.Id));var items=new List<BatchDeletionItem>();
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var retired=r.IsDBNull(7)?(bool?)null:Number(r,7) switch {0=>false,1=>true,_=>throw new InvalidDataException()};
            var item=new BatchDeletionItem(checked((int)Number(r,0)),Text(r,1),Text(r,2),Text(r,3),Text(r,4),Optional(r,5),Optional(r,6),retired);
            if(item.Ordinal!=items.Count||!RetentionDeletionCoordinator.ValidRecordingId(item.RecordingId)||!DeletionLedger.Id(item.ChildId)||!DeletionLedger.Id(item.ChildKey)||
                item.Phase is not("not_requested" or "dispatch_reserved" or "finished")||item.Phase!="finished"&&(item.Outcome is not null||item.Reason is not null||item.Retired is not null)||
                item.Phase=="finished"&&(item.Outcome is not("accepted" or "non_success")||!ValidReason(item.Reason)||item.Retired is null))throw new InvalidDataException();
            items.Add(item);
        }
        var policy = RetentionDeletionPolicy.Read(c, t, "batch", row.Id, out _);
        if(items.Count!=row.Count||items.Select(i=>i.RecordingId).Distinct(StringComparer.Ordinal).Count()!=row.Count||items.Select(i=>i.ChildId).Distinct(StringComparer.Ordinal).Count()!=row.Count||items.Select(i=>i.ChildKey).Distinct(StringComparer.Ordinal).Count()!=row.Count||
            policy?.CompletedBefore.UtcTicks>row.Created||RequestDigest(items.Select(i=>i.RecordingId).ToArray(),policy)!=row.Digest)throw new InvalidDataException();
        if(Bindings(row.Id,sid,session,root,row.Host,items,policy)!=row.BindingDigest)throw new InvalidDataException();
        var stopped=false;
        for(var i=0;i<items.Count;i++)
        {
            var item=items[i];if(stopped&&item.Phase!="not_requested")throw new InvalidDataException();
            if(item.Outcome!="accepted")stopped=true;
        }
        if(row.Reason=="all_dispositions_accepted"&&items.Any(i=>i.Outcome!="accepted"))throw new InvalidDataException();
        return row with { Items = items.AsReadOnly(), Policy = policy };
    }
    private static bool ValidReason(string? s)=>s is {Length:>0 and <=128}&&s.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_');
    private static string SafeText(string n)=>$"CASE WHEN {n} IS NULL THEN NULL WHEN typeof({n})='text' AND instr({n},char(0))=0 AND length(CAST({n} AS BLOB))<=4096 THEN {n} ELSE zeroblob(0) END";
    private static string Text(SqliteDataReader r,int n)=>r.GetValue(n) is string v?v:throw new InvalidDataException();
    private static string? Optional(SqliteDataReader r,int n)=>r.IsDBNull(n)?null:Text(r,n);
    private static long Number(SqliteDataReader r,int n)=>r.GetValue(n) is long v?v:throw new InvalidDataException();
    private static void Add(SqliteCommand cmd,params (string Key,object Value)[] args){foreach(var x in args)cmd.Parameters.AddWithValue(x.Key,x.Value);}
}
