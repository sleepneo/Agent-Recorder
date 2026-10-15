using System.Buffers.Binary;
using System.Collections.Concurrent;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum DeletionLedgerCommit { NotCommitted, Committed, Unknown }
internal sealed record DeletionLedgerWrite(DeletionLedgerCommit Commit, bool ResourcesRetired, bool Usable);
internal sealed record DeletionLedgerBinding(string OperationId,string ApprovalId,string Sid,string Session,string HostId,
    string RecordingId,string Root,PublicationIdentityAnchor Anchor,DateTimeOffset Completed,DateTimeOffset Cutoff,string Semantics,DateTimeOffset PreparedAt);
internal enum DeletionRecoveryState { PendingLive, UnsettledUnknown, OtherHostUnknown, Settled }
internal sealed record DeletionLedgerEntry(DeletionLedgerBinding Binding, DeletionRecoveryState State, LocalDeletionProgress? Progress,
    LocalDeletionReason? Reason,int? Win32Error,bool? OurResourcesRetired,int? Primary,int? DatabaseCleanup,int? NativeCleanup);

// Only connection-local FULL is changed. No pooling, creation, migration or
// provider objects on cancellation/timer threads. Fault hooks are private-host seams.
internal class DeletionLedgerDatabase
{
    internal virtual void Step(string stage,SqliteConnection c,SqliteTransaction? t) { }
    internal virtual void Open(SqliteConnection c)=>c.Open();
    internal virtual SqliteTransaction Begin(SqliteConnection c)=>c.BeginTransaction(System.Data.IsolationLevel.Serializable,deferred:false);
    internal virtual void Commit(SqliteTransaction t)=>t.Commit();
    internal virtual void Close(SqliteConnection c)=>c.Close();
    internal virtual void DisposeTransaction(SqliteTransaction t)=>t.Dispose();
    internal virtual void DisposeConnection(SqliteConnection c)=>c.Dispose();
}

internal sealed class DeletionLedger(SqliteOperationalStore store,string sid,string session,string root,
    TimeProvider clock,DeletionLedgerDatabase? database=null)
{
    internal static readonly string HostId=Guid.NewGuid().ToString("N"); // Process incarnation, never a PID/time liveness guess.
    private static readonly ConcurrentDictionary<(string Db,string Op),object> Active=new();
    private readonly DeletionLedgerDatabase _io=database??new();
    // Unknown provider ownership is strongly retained by the local attempt/host.
    private readonly ConcurrentBag<object> _unknown=new();
    private static string DatabaseKey(string path)=>Path.GetFullPath(path).ToUpperInvariant();
    internal void Register(string op,object attempt) { if(!Active.TryAdd((DatabaseKey(store.DatabasePath),op),attempt))throw new InvalidOperationException(); }
    internal void Retire(string op,object attempt)
    { if(Active.TryGetValue((DatabaseKey(store.DatabasePath),op),out var current)&&ReferenceEquals(current,attempt))Active.TryRemove((DatabaseKey(store.DatabasePath),op),out _); }
    internal sealed class Prepared
    {
        internal DeletionLedgerBinding Binding { get; }
        internal object Attempt { get; }
        private Prepared(DeletionLedgerBinding binding,object attempt){Binding=binding;Attempt=attempt;}
        internal static async Task<(Prepared? Proof,DeletionLedgerWrite Result)> Run(DeletionLedger ledger,DeletionLedgerBinding binding,object attempt,long started,CancellationToken cancel)
        {
            var result=await ledger.Prepare(binding,started,cancel).ConfigureAwait(false);
            return (result.Usable&&result.Commit==DeletionLedgerCommit.Committed&&result.ResourcesRetired?new(binding,attempt):null,result);
        }
    }
    private Task<DeletionLedgerWrite> Prepare(DeletionLedgerBinding b,long started,CancellationToken cancel)
        => Task.Run(()=>Write(b,started,cancel,null));
    internal Task<DeletionLedgerWrite> Settle(DeletionLedgerBinding b,LocalDeletionResult result)
    {var started=clock.GetTimestamp();return Task.Run(()=>Write(b,started,CancellationToken.None,result));}
    private DeletionLedgerWrite Write(DeletionLedgerBinding b,long started,CancellationToken cancel,LocalDeletionResult? result)
    {
        SqliteConnection? c=null;SqliteTransaction? t=null;var retired=true;var commit=DeletionLedgerCommit.NotCommitted;var usable=false;
        var prefix=result is null?"prepare_":"settle_";
        void Check(){cancel.ThrowIfCancellationRequested();if(clock.GetElapsedTime(started)>=TimeSpan.FromSeconds(3))throw new TimeoutException();}
        void Step(string name){_io.Step(prefix+name,c!,t);Check();}
        try
        {
            Check();ValidateBinding(b);if(b.Sid!=sid||b.Session!=session||b.Root!=root||b.HostId!=HostId)throw new InvalidDataException();
            c=store.CreateRetentionGuardConnection();Step("before_open");_io.Open(c);Step("after_open");
            SqliteOperationalStore.ConfigureRetentionGuardConnection(c);
            using(var cmd=c.CreateCommand())
            {
                cmd.CommandText="PRAGMA synchronous=FULL;";cmd.ExecuteNonQuery();
                foreach(var item in new[]{("PRAGMA synchronous;",(object)2L),("PRAGMA foreign_keys;",1L),("PRAGMA journal_mode;","wal")})
                {cmd.CommandText=item.Item1;if(!Equals(cmd.ExecuteScalar(),item.Item2))throw new InvalidDataException("Deletion durability policy differs.");}
            }
            Step("before_begin");t=_io.Begin(c);Step("after_begin");SqliteOperationalStore.ValidateRetentionGuardStore(c,t);Check();
            using var command=c.CreateCommand();command.Transaction=t;Bind(command,b);
            if(result is null)
            {
                Step("before_insert");command.CommandText="INSERT INTO ordinary_deletion_operations("+string.Join(',',SqliteSchemaV43.BindingColumns)+",phase) VALUES("+string.Join(',',SqliteSchemaV43.BindingColumns.Select(x=>"$"+x))+",'prepared_unknown');";
                if(command.ExecuteNonQuery()!=1)throw new InvalidDataException();Step("after_insert");
            }
            else
            {
                var entry=ReadRow(c,t,b.OperationId);if(entry is null||entry.Binding!=b||entry.State==DeletionRecoveryState.Settled)throw new InvalidDataException("Deletion binding changed.");
                command.CommandText="UPDATE ordinary_deletion_operations SET phase='settled',progress=$progress,reason=$reason,win32_error=$error,resources_retired=$retired,primary_reason=$primary,database_cleanup=$database,native_cleanup=$native,settled_ticks=$settled WHERE operation_id COLLATE BINARY=$operation_id AND phase='prepared_unknown';";
                command.Parameters.AddWithValue("$progress",(int)result.Progress);command.Parameters.AddWithValue("$reason",(int)result.Reason);
                command.Parameters.AddWithValue("$error",(object?)result.Win32Error??DBNull.Value);command.Parameters.AddWithValue("$retired",result.OurResourcesRetired==true?1:0);
                command.Parameters.AddWithValue("$primary",result.Retirement is null?DBNull.Value:(int)result.Retirement.Primary);
                command.Parameters.AddWithValue("$database",result.Retirement?.Database is null?DBNull.Value:(int)result.Retirement.Database.Cleanup);
                command.Parameters.AddWithValue("$native",result.Retirement?.Preparation?.Scope is null?DBNull.Value:result.Retirement.Preparation.Scope.Retired?0:1);
                command.Parameters.AddWithValue("$settled",clock.GetUtcNow().UtcTicks);Step("before_update");if(command.ExecuteNonQuery()!=1)throw new InvalidDataException();Step("after_update");
            }
            Step("before_commit");commit=DeletionLedgerCommit.Unknown;_io.Commit(t);commit=DeletionLedgerCommit.Committed;Step("after_commit");usable=true;
        }
        catch { usable=false; }
        finally
        {
            if(t is not null){try{_io.DisposeTransaction(t);if(t.Connection is not null)retired=false;}catch{retired=false;}}
            if(c is not null)
            {
                try{_io.Step(prefix+"before_close",c,t);_io.Close(c);if(c.State!=System.Data.ConnectionState.Closed||c.Handle is not null)retired=false;_io.Step(prefix+"after_close",c,t);}catch{retired=false;}
                try{_io.DisposeConnection(c);}catch{retired=false;}
            }
            if(!retired){if(c is not null)_unknown.Add(c);if(t is not null)_unknown.Add(t);}
            try{Check();}catch{usable=false;}
        }
        return new(commit,retired,usable&&retired);
    }
    internal static bool Id(string? value)=>value is {Length:32}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateBinding(DeletionLedgerBinding b)
    {
        if(!Id(b.OperationId)||!Id(b.ApprovalId)||!Id(b.HostId)||!UnifiedHistoryQueryService.ValidIdentity(b.Sid)||b.Sid.Length>256||
            !UnifiedHistoryQueryService.ValidIdentity(b.Session)||b.Session.Length>256||!UnifiedHistoryQueryService.ValidId(b.RecordingId)||b.RecordingId.Length>200||
            b.RecordingId.IndexOfAny(new[]{'/','\\'})>=0||!OriginalFileHoldService.ValidScopeInput(b.Root,b.Anchor)||b.Semantics!=LocalRetentionApproval.Semantics||
            b.Completed.Offset!=TimeSpan.Zero||b.Cutoff.Offset!=TimeSpan.Zero||b.PreparedAt.Offset!=TimeSpan.Zero||b.Completed>=b.Cutoff||b.Cutoff>b.PreparedAt)throw new InvalidDataException("Invalid deletion binding.");
    }
    private static void Bind(SqliteCommand c,DeletionLedgerBinding b)
    {
        var volume=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(volume,b.Anchor.Identity.VolumeSerialNumber);
        var file=new byte[16];BinaryPrimitives.WriteUInt64LittleEndian(file,b.Anchor.Identity.IdLow);BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8),b.Anchor.Identity.IdHigh);
        object[] values={b.OperationId,b.ApprovalId,b.Sid,b.Session,b.HostId,b.RecordingId,b.Root,b.Anchor.Path,volume,file,b.Anchor.Length,b.Completed.UtcTicks,b.Cutoff.UtcTicks,b.Semantics,b.PreparedAt.UtcTicks};
        for(var i=0;i<values.Length;i++){var p=c.Parameters.AddWithValue("$"+SqliteSchemaV43.BindingColumns[i],values[i]);if(values[i] is byte[])p.SqliteType=SqliteType.Blob;}
    }
    internal static void ValidatePrepared(SqliteConnection c,SqliteTransaction t,Prepared proof)
    {
        var row=ReadRow(c,t,proof.Binding.OperationId);
        if(row is null||row.Binding!=proof.Binding||row.State==DeletionRecoveryState.Settled)throw new InvalidDataException("Prepared deletion differs.");
    }
    internal static DeletionLedgerEntry? ReadRow(SqliteConnection c,SqliteTransaction t,string op)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        // Bound text projection validates full bytes/NUL before GetString; no truncation/coercion.
        var names=SqliteSchemaV43.BindingColumns.Concat(new[]{"phase","progress","reason","win32_error","resources_retired","primary_reason","database_cleanup","native_cleanup","settled_ticks"}).ToArray();
        cmd.CommandText="SELECT "+string.Join(',',names.Select(n=>n is "volume_id" or "file_id"?$"CASE WHEN typeof({n})='blob' AND length({n})={(n=="volume_id"?8:16)} THEN {n} END":n is "operation_id" or "approval_id" or "owner_sid" or "owner_session" or "host_id" or "recording_id" or "videos_root" or "output_path" or "semantics" or "phase"?$"CASE WHEN typeof({n})='text' AND instr({n},char(0))=0 AND length(CAST({n} AS BLOB))<=4096 THEN {n} END":n))+" FROM ordinary_deletion_operations WHERE operation_id COLLATE BINARY=$op COLLATE BINARY;";
        cmd.Parameters.AddWithValue("$op",op);using var rd=cmd.ExecuteReader();if(!rd.Read())return null;
        string Text(int i)=>rd.GetValue(i) is string v?v:throw new InvalidDataException();
        long Number(int i)=>rd.GetValue(i) is long v?v:throw new InvalidDataException();
        int? Optional(int i,int min,int max){if(rd.IsDBNull(i))return null;var v=Number(i);return v>=min&&v<=max?(int)v:throw new InvalidDataException();}
        if(rd.GetValue(8) is not byte[] vol||vol.Length!=8||rd.GetValue(9) is not byte[] file||file.Length!=16)throw new InvalidDataException();
        var b=new DeletionLedgerBinding(Text(0),Text(1),Text(2),Text(3),Text(4),Text(5),Text(6),new(Text(7),Number(10),new(BinaryPrimitives.ReadUInt64LittleEndian(vol),BinaryPrimitives.ReadUInt64LittleEndian(file),BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8)))),new(Number(11),TimeSpan.Zero),new(Number(12),TimeSpan.Zero),Text(13),new(Number(14),TimeSpan.Zero));
        ValidateBinding(b);if(b.OperationId!=op)throw new InvalidDataException();var phase=Text(15);var progress=Optional(16,0,4);if(progress==1)throw new InvalidDataException();
        var reason=Optional(17,0,4);var error=Optional(18,0,int.MaxValue);var retired=Optional(19,0,1);var primary=Optional(20,0,12);var db=Optional(21,0,1);var native=Optional(22,0,1);
        if(phase=="prepared_unknown") {if(Enumerable.Range(16,8).Any(i=>!rd.IsDBNull(i)))throw new InvalidDataException();}
        else if(phase!="settled"||progress is null||reason is null||retired is null||rd.IsDBNull(23)||Number(23)<b.PreparedAt.UtcTicks||Number(23)>DateTimeOffset.MaxValue.UtcTicks)throw new InvalidDataException();
        if(primary is not null&&!Enum.IsDefined(typeof(RetentionComposedReason),primary.Value))throw new InvalidDataException();
        if(phase=="settled"&&(progress==2&&(reason!=0||error is not null)||progress==3&&(reason!=3||error is null)||progress==4&&reason!=4||progress==0&&reason is not(1 or 2)))throw new InvalidDataException();
        if(rd.Read())throw new InvalidDataException();
        var state=phase=="settled"?DeletionRecoveryState.Settled:b.HostId!=HostId?DeletionRecoveryState.OtherHostUnknown:
            Active.ContainsKey((DatabaseKey(c.DataSource),op))?DeletionRecoveryState.PendingLive:DeletionRecoveryState.UnsettledUnknown;
        return new(b,state,progress is null?null:(LocalDeletionProgress)progress,reason is null?null:(LocalDeletionReason)reason,error,retired is null?null:retired==1,primary,db,native);
    }
    internal DeletionLedgerEntry? Read(string operationId)=>ReadPage(operationId,null,1).SingleOrDefault();
    internal IReadOnlyList<DeletionLedgerEntry> Page(string? after=null,int limit=100)=>ReadPage(null,after,limit);
    private IReadOnlyList<DeletionLedgerEntry> ReadPage(string? exact,string? after,int limit)
    {
        var started=clock.GetTimestamp();void Check(){if(clock.GetElapsedTime(started)>=TimeSpan.FromSeconds(3))throw new TimeoutException();}
        if(limit is <1 or >100||exact is not null&&!Id(exact)||after is not null&&!Id(after))throw new ArgumentException();
        if(!UnifiedHistoryQueryService.ValidIdentity(sid)||!UnifiedHistoryQueryService.ValidIdentity(session))throw new InvalidDataException();
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Mode=SqliteOpenMode.ReadOnly,Cache=SqliteCacheMode.Private,Pooling=false,DefaultTimeout=1}.ToString());
        c.Open();Check();SqliteOperationalStore.ConfigureRetentionGuardConnection(c);using var t=c.BeginTransaction(deferred:true);SqliteOperationalStore.ValidateRetentionGuardStore(c,t);Check();
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT operation_id FROM ordinary_deletion_operations INDEXED BY idx_deletion_owner WHERE owner_sid COLLATE BINARY=$sid AND owner_session COLLATE BINARY=$session AND "+(exact is null?"operation_id COLLATE BINARY>$after":"operation_id COLLATE BINARY=$after")+" ORDER BY operation_id COLLATE BINARY LIMIT $limit;";
        cmd.Parameters.AddWithValue("$sid",sid);cmd.Parameters.AddWithValue("$session",session);cmd.Parameters.AddWithValue("$after",exact??after??"");cmd.Parameters.AddWithValue("$limit",limit);
        var ids=new List<string>();using(var rd=cmd.ExecuteReader())while(rd.Read()){if(rd.GetValue(0) is not string id||!Id(id))throw new InvalidDataException();ids.Add(id);}
        var rows=ids.Select(id=>{Check();var row=ReadRow(c,t,id)??throw new InvalidDataException();Check();return row;}).ToArray();
        if(rows.Any(x=>x.Binding.Sid!=sid||x.Binding.Session!=session||x.Binding.Root!=root))throw new InvalidDataException();return rows;
    }
}
