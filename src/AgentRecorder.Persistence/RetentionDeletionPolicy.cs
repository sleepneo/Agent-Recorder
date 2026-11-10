using System.Globalization;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
namespace AgentRecorder.Persistence;
public sealed record RetentionDeletionPolicy
{
    public DateTimeOffset CompletedBefore {get;}
    public int KeepLatestCount {get;}
    public string Digest => RetentionDeletionCoordinator.Digest("single_delete_policy/v1\n"+CompletedBefore.UtcTicks.ToString(CultureInfo.InvariantCulture)+"\n"+KeepLatestCount.ToString(CultureInfo.InvariantCulture));
    public RetentionDeletionPolicy(DateTimeOffset completedBefore,int keepLatestCount)
    { if(completedBefore.Offset!=TimeSpan.Zero || keepLatestCount is <1 or >1000)throw new ArgumentException();CompletedBefore=completedBefore;KeepLatestCount=keepLatestCount; }
    private static string Table(string kind) => kind switch
    {
        "request" => "ordinary_deletion_request_policies",
        "operation" => "ordinary_deletion_operation_policies",
        "batch" => "ordinary_deletion_batch_policies",
        _ => throw new InvalidDataException()
    };

    internal static RetentionDeletionPolicy? Read(SqliteConnection c,SqliteTransaction t,string kind,string id,out string? reason)
    {
        var hostReason = kind == "batch" ? null : ReadHostReason(c, t, kind, id);
        var reasonColumn = kind == "batch" ? "NULL" : "policy_reason";
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT policy_cutoff,policy_keep_count,policy_digest,"+reasonColumn+" FROM "+Table(kind)+" WHERE id COLLATE BINARY=$id;";cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader();if(!r.Read()){reason=hostReason;return null;}
        reason=r.IsDBNull(3)?null:r.GetValue(3) is string s && s is "within_keep_latest" or "completion_not_before_cutoff"?s:throw new InvalidDataException();
        if (reason is not null && hostReason is not null) throw new InvalidDataException("Deletion rejection receipts disagree.");
        reason ??= hostReason;
        if(r.IsDBNull(0)&&r.IsDBNull(1)&&r.IsDBNull(2)){if(reason is not null)throw new InvalidDataException();return null;}
        if(r.GetValue(0) is not long ticks || r.GetValue(1) is not long n || n is <1 or >1000 || r.GetValue(2) is not string digest)throw new InvalidDataException();
        var policy=new RetentionDeletionPolicy(new(ticks,TimeSpan.Zero),(int)n);if(policy.Digest!=digest)throw new InvalidDataException();return policy;
    }
    internal static void Store(SqliteConnection c,SqliteTransaction t,string kind,string id,RetentionDeletionPolicy? policy)
    {
        if(policy is null)return;
        var table=Table(kind);
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="INSERT INTO "+table+"(id,policy_cutoff,policy_keep_count,policy_digest) VALUES($id,$cutoff,$n,$digest);";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$cutoff",policy.CompletedBefore.UtcTicks);cmd.Parameters.AddWithValue("$n",policy.KeepLatestCount);cmd.Parameters.AddWithValue("$digest",policy.Digest);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
    }
    internal static void StoreReason(SqliteConnection c,SqliteTransaction t,string kind,string id,string? reason)
    {
        if(reason is null)return;
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText = RetentionHostKeepLatestProtection.IsHostReason(reason)
            ? $"INSERT INTO {SqliteSchemaV51.Table}({HostReasonKey(kind)},reason) VALUES($id,$reason);"
            : $"UPDATE {Table(kind)} SET policy_reason=$reason WHERE id=$id AND policy_reason IS NULL;";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$reason",reason);if(cmd.ExecuteNonQuery()!=1)throw new InvalidDataException();
    }

    private static string? ReadHostReason(SqliteConnection c, SqliteTransaction t, string kind, string id)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = $"SELECT reason FROM {SqliteSchemaV51.Table} WHERE {HostReasonKey(kind)}=$id;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        if (value is null) return null;
        if (value is string reason && RetentionHostKeepLatestProtection.IsHostReason(reason)) return reason;
        throw new InvalidDataException("Invalid host deletion rejection receipt.");
    }

    private static string HostReasonKey(string kind) => kind switch
    {
        "request" => "request_id",
        "operation" => "operation_id",
        _ => throw new InvalidDataException()
    };
}
internal sealed class RetentionPolicyProtectedException(string reason):Exception("The deletion policy protects this recording.") {internal string Reason {get;}=reason;}
