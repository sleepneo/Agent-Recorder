using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV44
{
    internal const string Name="schema_v44_single_deletion_requests";
    internal const string Table="ordinary_deletion_requests";
    internal static readonly string[] Immutable={"request_id","owner_sid","owner_session","videos_root","host_id","key_digest","request_digest","recording_id","created_ticks"};
    internal static readonly (string Type,string Name,string Sql)[] Objects={
        ("table",Table,"""
CREATE TABLE ordinary_deletion_requests (
 request_id TEXT PRIMARY KEY NOT NULL COLLATE BINARY CHECK(length(request_id)=32 AND instr(request_id,char(0))=0 AND request_id NOT GLOB '*[^0-9a-f]*'),
 owner_sid TEXT NOT NULL CHECK(length(owner_sid) BETWEEN 1 AND 256 AND instr(owner_sid,char(0))=0),
 owner_session TEXT NOT NULL CHECK(length(owner_session) BETWEEN 1 AND 256 AND instr(owner_session,char(0))=0),
 videos_root TEXT NOT NULL CHECK(length(videos_root) BETWEEN 4 AND 1024 AND instr(videos_root,char(0))=0),
 host_id TEXT NOT NULL CHECK(length(host_id)=32 AND instr(host_id,char(0))=0 AND host_id NOT GLOB '*[^0-9a-f]*'),
 key_digest TEXT NOT NULL CHECK(length(key_digest)=64 AND instr(key_digest,char(0))=0 AND key_digest NOT GLOB '*[^0-9a-f]*'),
 request_digest TEXT NOT NULL CHECK(length(request_digest)=64 AND instr(request_digest,char(0))=0 AND request_digest NOT GLOB '*[^0-9a-f]*'),
 recording_id TEXT NOT NULL CHECK(length(recording_id) BETWEEN 1 AND 200 AND instr(recording_id,char(0))=0),
 created_ticks INTEGER NOT NULL CHECK(created_ticks BETWEEN 0 AND 3155378975999999999),
 phase TEXT NOT NULL CHECK(phase IN ('awaiting_confirmation','execution_reserved','finished')),
 local_state TEXT NOT NULL CHECK(local_state IN ('pending','approved','rejected','expired','cancelled','unavailable','busy')),
 approval_id TEXT NULL CHECK(length(approval_id)=32 AND instr(approval_id,char(0))=0 AND approval_id NOT GLOB '*[^0-9a-f]*'),
 root_retired INTEGER NULL CHECK(root_retired IN (0,1)),
 ledger_retired INTEGER NULL CHECK(ledger_retired IN (0,1)),
 preparation TEXT NULL CHECK(preparation IN ('not_committed','committed','unknown')),
 settlement TEXT NULL CHECK(settlement IN ('not_committed','committed','unknown')),
 updated_ticks INTEGER NOT NULL CHECK(updated_ticks BETWEEN created_ticks AND 3155378975999999999),
 CHECK((phase='awaiting_confirmation' AND local_state='pending' AND approval_id IS NULL AND root_retired IS NULL AND ledger_retired IS NULL AND preparation IS NULL AND settlement IS NULL)
 OR (phase='execution_reserved' AND local_state='approved' AND approval_id IS NOT NULL AND root_retired IS NULL AND ledger_retired IS NULL AND preparation IS NULL AND settlement IS NULL)
 OR (phase='finished' AND local_state<>'pending' AND ((approval_id IS NULL AND root_retired IS NULL AND ledger_retired IS NULL AND preparation IS NULL AND settlement IS NULL) OR approval_id IS NOT NULL)))
) STRICT
"""),
        ("index","idx_deletion_request_key","CREATE UNIQUE INDEX idx_deletion_request_key ON ordinary_deletion_requests(owner_sid COLLATE BINARY,owner_session COLLATE BINARY,key_digest COLLATE BINARY)"),
        ("index","idx_deletion_request_approval","CREATE UNIQUE INDEX idx_deletion_request_approval ON ordinary_deletion_requests(approval_id COLLATE BINARY) WHERE approval_id IS NOT NULL"),
        ("trigger","deletion_request_no_delete","CREATE TRIGGER deletion_request_no_delete BEFORE DELETE ON ordinary_deletion_requests BEGIN SELECT RAISE(ABORT,'deletion request is retained'); END"),
        ("trigger","deletion_request_transition","CREATE TRIGGER deletion_request_transition BEFORE UPDATE ON ordinary_deletion_requests WHEN "+
            string.Join(" OR ",Immutable.Select(c=>"NEW."+c+" IS NOT OLD."+c))+" OR NOT ((OLD.phase='awaiting_confirmation' AND NEW.phase IN ('execution_reserved','finished')) OR (OLD.phase='execution_reserved' AND NEW.phase='finished' AND NEW.approval_id IS OLD.approval_id)) BEGIN SELECT RAISE(ABORT,'invalid deletion request transition'); END")
    };
    internal static readonly string Definition=string.Join(";\n",Objects.Select(x=>x.Sql))+";";
    internal const string Checksum="52d3924f667692d4f9fae4bc298f4cc03a51b6a966dd2330d6a3ff601e6739f5";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations {get;}=new[]{new SqliteMigrationDefinition(44,Name,Definition,Checksum)};
    internal static void Validate(SqliteConnection c,SqliteTransaction? t)
    {
        if(SqliteSchemaV1.ComputeChecksum(Definition)!=Checksum)throw new InvalidDataException("Deletion request checksum differs.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        foreach(var o in Objects){cmd.CommandText="SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";cmd.Parameters.AddWithValue("$type",o.Type);cmd.Parameters.AddWithValue("$name",o.Name);
            if(cmd.ExecuteScalar() is not string sql||SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';'))!=SqliteSchemaV1.CanonicalizeDefinition(o.Sql))throw new InvalidDataException("Deletion request schema differs.");cmd.Parameters.Clear();}
        cmd.CommandText="SELECT name,definition_checksum FROM schema_migrations WHERE version=44;";using var r=cmd.ExecuteReader();
        if(!r.Read()||r.GetValue(0) is not string name||name!=Name||r.GetValue(1) is not string checksum||checksum!=Checksum||r.Read())throw new InvalidDataException("Deletion request receipt differs.");
    }
}
