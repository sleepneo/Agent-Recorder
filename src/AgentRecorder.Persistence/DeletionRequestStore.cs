using System.Collections.Concurrent;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public sealed class DeletionRequestUnavailableException(string reason="unavailable"):Exception("Deletion requests are temporarily unavailable.")
{ public string Reason {get;}=reason; }
public sealed class DeletionRequestConflictException():Exception("The idempotency key belongs to a different deletion request.");
internal sealed record DeletionRequestRow(string Id,string Sid,string Session,string Root,string Host,string KeyHash,string RequestHash,
    string RecordingId,long Created,string Phase,string LocalState,string? ApprovalId,bool? RootRetired,bool? LedgerRetired,string? Preparation,string? Settlement,long Updated);
internal sealed record DeletionRequestFacts(DeletionRequestRow Row,DeletionLedgerEntry? Ledger);
internal sealed record DeletionRequestCreation(DeletionRequestRow Row,bool Created);
internal sealed record DeletionRequestIo<T>(T? Value,DeletionLedgerCommit Commit,bool Retired,bool Usable,Exception? Error) where T:class;

// All SQL/provider objects belong to one actual worker. A late/unknown close is
// retained; its caller must keep the corresponding admission even after timeout.
internal sealed class DeletionRequestStore(SqliteOperationalStore store,string sid,string session,string root,
    TimeProvider clock,DeletionLedgerDatabase? database=null)
{
    private readonly DeletionLedgerDatabase _io=database??new();
    private readonly ConcurrentBag<object> _unknown=new();
    internal DeletionRequestIo<T> Run<T>(string stage,bool write,long started,CancellationToken cancel,
        Func<SqliteConnection,SqliteTransaction,T?> run) where T:class
    {
        SqliteConnection? c=null;SqliteTransaction? t=null;var commit=DeletionLedgerCommit.NotCommitted;var retired=true;var usable=false;T? value=null;Exception? error=null;
        void Check(){cancel.ThrowIfCancellationRequested();if(clock.GetElapsedTime(started)>=TimeSpan.FromSeconds(3))throw new TimeoutException();}
        void Step(string name){_io.Step(stage+"_"+name,c!,t);Check();}
        try
        {
            Check();c=write?store.CreateRetentionGuardConnection():new(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Mode=SqliteOpenMode.ReadOnly,Cache=SqliteCacheMode.Private,Pooling=false,DefaultTimeout=1}.ToString());
            Step("before_open");_io.Open(c);Step("after_open");SqliteOperationalStore.ConfigureRetentionGuardConnection(c);
            if(write)
            {
                using var policy=c.CreateCommand();policy.CommandText="PRAGMA synchronous=FULL;";policy.ExecuteNonQuery();
                foreach(var pair in new[]{("PRAGMA synchronous;",(object)2L),("PRAGMA foreign_keys;",1L),("PRAGMA journal_mode;","wal")})
                {policy.CommandText=pair.Item1;if(!Equals(policy.ExecuteScalar(),pair.Item2))throw new InvalidDataException();}
            }
            Step("before_begin");t=write?_io.Begin(c):c.BeginTransaction(deferred:true);Step("after_begin");
            SqliteOperationalStore.ValidateRetentionGuardStore(c,t);Check();Step("before_sql");value=run(c,t);Step("after_sql");
            if(write){Step("before_commit");commit=DeletionLedgerCommit.Unknown;_io.Commit(t);commit=DeletionLedgerCommit.Committed;Step("after_commit");}
            usable=true;
        }
        catch(Exception e){error=e;}
        finally
        {
            if(t is not null){try{_io.DisposeTransaction(t);if(t.Connection is not null)retired=false;}catch{retired=false;}}
            if(c is not null)
            {
                try{_io.Step(stage+"_before_close",c,t);_io.Close(c);if(c.State!=System.Data.ConnectionState.Closed||c.Handle is not null)retired=false;_io.Step(stage+"_after_close",c,t);}catch{retired=false;}
                try{_io.DisposeConnection(c);}catch{retired=false;}
            }
            if(!retired){if(c is not null)_unknown.Add(c);if(t is not null)_unknown.Add(t);}
            try{Check();}catch{usable=false;}
        }
        return new(value,commit,retired,usable&&error is null&&retired,error);
    }
    internal DeletionRequestCreation Create(SqliteConnection c,SqliteTransaction t,string id,string recordingId,string keyHash,string requestHash)
    {
        var old=Read(c,t,keyHash,true);
        if(old is not null){if(old.RequestHash!=requestHash)throw new DeletionRequestConflictException();return new(old,false);}
        var now=clock.GetUtcNow().UtcTicks;
        var row=new DeletionRequestRow(id,sid,session,root,DeletionLedger.HostId,keyHash,requestHash,recordingId,now,"awaiting_confirmation","pending",null,null,null,null,null,now);
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="INSERT INTO ordinary_deletion_requests(request_id,owner_sid,owner_session,videos_root,host_id,key_digest,request_digest,recording_id,created_ticks,phase,local_state,updated_ticks) VALUES($id,$sid,$session,$root,$host,$key,$hash,$recording,$at,'awaiting_confirmation','pending',$at);";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$sid",sid);cmd.Parameters.AddWithValue("$session",session);cmd.Parameters.AddWithValue("$root",root);cmd.Parameters.AddWithValue("$host",row.Host);
        cmd.Parameters.AddWithValue("$key",keyHash);cmd.Parameters.AddWithValue("$hash",requestHash);cmd.Parameters.AddWithValue("$recording",recordingId);cmd.Parameters.AddWithValue("$at",now);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();return new(row,true);
    }
    internal DeletionRequestRow Update(SqliteConnection c,SqliteTransaction t,DeletionRequestRow row,string? approval,string local,LocalDeletionResult? result,bool finish)
    {
        var actual=Read(c,t,row.Id,false);if(actual!=row)throw new InvalidDataException();
        var next=row with {Phase=finish?"finished":"execution_reserved",LocalState=local,ApprovalId=approval,
            RootRetired=result?.Retirement?.Retired,LedgerRetired=result?.LedgerResourcesRetired,
            Preparation=result is null?null:CommitName(result.Preparation),Settlement=result is null?null:CommitName(result.Settlement),Updated=clock.GetUtcNow().UtcTicks};
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="UPDATE ordinary_deletion_requests SET phase=$phase,local_state=$local,approval_id=$approval,root_retired=$root_retired,ledger_retired=$ledger_retired,preparation=$preparation,settlement=$settlement,updated_ticks=$updated WHERE request_id COLLATE BINARY=$id AND phase=$old;";
        cmd.Parameters.AddWithValue("$phase",next.Phase);cmd.Parameters.AddWithValue("$local",local);cmd.Parameters.AddWithValue("$approval",(object?)approval??DBNull.Value);
        cmd.Parameters.AddWithValue("$root_retired",next.RootRetired is null?DBNull.Value:next.RootRetired==true?1:0);cmd.Parameters.AddWithValue("$ledger_retired",next.LedgerRetired is null?DBNull.Value:next.LedgerRetired==true?1:0);
        cmd.Parameters.AddWithValue("$preparation",(object?)next.Preparation??DBNull.Value);cmd.Parameters.AddWithValue("$settlement",(object?)next.Settlement??DBNull.Value);cmd.Parameters.AddWithValue("$updated",next.Updated);
        cmd.Parameters.AddWithValue("$id",row.Id);cmd.Parameters.AddWithValue("$old",row.Phase);if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();return next;
    }
    internal DeletionRequestFacts? Facts(SqliteConnection c,SqliteTransaction t,string id,bool byKey)
    {
        var row=Read(c,t,id,byKey);if(row is null)return null;DeletionLedgerEntry? ledger=null;
        if(row.ApprovalId is not null)
        {
            using var cmd=c.CreateCommand();cmd.Transaction=t;
            cmd.CommandText="SELECT CASE WHEN typeof(operation_id)='text' AND length(CAST(operation_id AS BLOB))=32 AND instr(operation_id,char(0))=0 THEN operation_id END FROM ordinary_deletion_operations INDEXED BY idx_deletion_approval WHERE approval_id COLLATE BINARY=$approval;";
            cmd.Parameters.AddWithValue("$approval",row.ApprovalId);string? op=null;using(var r=cmd.ExecuteReader()){if(r.Read()){if(r.GetValue(0) is not string v||!DeletionLedger.Id(v))throw new InvalidDataException();op=v;if(r.Read())throw new InvalidDataException();}}
            if(op is not null)
            {
                ledger=DeletionLedger.ReadRow(c,t,op);var b=ledger?.Binding??throw new InvalidDataException();
                if(b.ApprovalId!=row.ApprovalId||b.Sid!=sid||b.Session!=session||b.Root!=root||b.RecordingId!=row.RecordingId||b.HostId!=row.Host||b.PreparedAt.UtcTicks<row.Created)throw new InvalidDataException();
            }
        }
        return new(row,ledger);
    }
    private DeletionRequestRow? Read(SqliteConnection c,SqliteTransaction t,string id,bool key)
    {
        var names=new[]{"request_id","owner_sid","owner_session","videos_root","host_id","key_digest","request_digest","recording_id","created_ticks","phase","local_state","approval_id","root_retired","ledger_retired","preparation","settlement","updated_ticks"};
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="SELECT "+string.Join(',',names.Select(n=>n is "created_ticks" or "updated_ticks" or "root_retired" or "ledger_retired"?n:$"CASE WHEN {n} IS NULL THEN NULL WHEN typeof({n})='text' AND instr({n},char(0))=0 AND length(CAST({n} AS BLOB))<=4096 THEN {n} ELSE zeroblob(0) END"))+" FROM ordinary_deletion_requests "+(key?"INDEXED BY idx_deletion_request_key ":"")+"WHERE "+(key?"key_digest":"request_id")+" COLLATE BINARY=$id AND owner_sid COLLATE BINARY=$sid AND owner_session COLLATE BINARY=$session AND videos_root COLLATE BINARY=$root;";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$sid",sid);cmd.Parameters.AddWithValue("$session",session);cmd.Parameters.AddWithValue("$root",root);
        using var r=cmd.ExecuteReader();if(!r.Read())return null;
        string Text(int i)=>r.GetValue(i) is string s?s:throw new InvalidDataException();
        long Number(int i)=>r.GetValue(i) is long v?v:throw new InvalidDataException();
        string? Optional(int i)=>r.IsDBNull(i)?null:Text(i);
        bool? Flag(int i)=>r.IsDBNull(i)?null:Number(i) switch{0=>false,1=>true,_=>throw new InvalidDataException()};
        var row=new DeletionRequestRow(Text(0),Text(1),Text(2),Text(3),Text(4),Text(5),Text(6),Text(7),Number(8),Text(9),Text(10),Optional(11),Flag(12),Flag(13),Optional(14),Optional(15),Number(16));
        if(r.Read()||!DeletionLedger.Id(row.Id)||!DeletionLedger.Id(row.Host)||!Hash(row.KeyHash)||!Hash(row.RequestHash)||
            !RetentionDeletionCoordinator.ValidRecordingId(row.RecordingId)||row.RequestHash!=RetentionDeletionCoordinator.RequestHash(row.RecordingId)||
            row.Sid!=sid||row.Session!=session||row.Root!=root||sid.Length>256||session.Length>256||root.Length>1024||
            row.Created<0||row.Updated<row.Created||row.Updated>DateTimeOffset.MaxValue.UtcTicks||row.ApprovalId is not null&&!DeletionLedger.Id(row.ApprovalId)||
            row.Phase is not("awaiting_confirmation" or "execution_reserved" or "finished")||row.LocalState is not("pending" or "approved" or "rejected" or "expired" or "cancelled" or "unavailable" or "busy")||
            row.Preparation is not(null or "not_committed" or "committed" or "unknown")||row.Settlement is not(null or "not_committed" or "committed" or "unknown"))throw new InvalidDataException();
        if(row.Phase=="awaiting_confirmation"&&(row.LocalState!="pending"||row.ApprovalId is not null)||row.Phase=="execution_reserved"&&(row.LocalState!="approved"||row.ApprovalId is null)||row.Phase=="finished"&&row.LocalState=="pending")throw new InvalidDataException();
        if((row.Phase!="finished"||row.ApprovalId is null)&&(row.RootRetired is not null||row.LedgerRetired is not null||row.Preparation is not null||row.Settlement is not null))throw new InvalidDataException();
        return row;
    }
    internal static bool Hash(string? s)=>s is {Length:64}&&s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string CommitName(DeletionLedgerCommit c)=>c switch{DeletionLedgerCommit.Committed=>"committed",DeletionLedgerCommit.Unknown=>"unknown",_=>"not_committed"};
}
