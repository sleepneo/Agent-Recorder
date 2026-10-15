using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV43
{
    internal const string MigrationName = "schema_v43_ordinary_deletion_ledger";
    internal const string Table = "ordinary_deletion_operations";
    internal static readonly string[] BindingColumns = { "operation_id", "approval_id", "owner_sid", "owner_session", "host_id", "recording_id", "videos_root", "output_path", "volume_id", "file_id", "recorded_bytes", "completed_ticks", "cutoff_ticks", "semantics", "prepared_ticks" };
    internal static readonly (string Type,string Name,string Sql)[] Objects =
    {
        ("table", Table, """
CREATE TABLE ordinary_deletion_operations (
 operation_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*'),
 approval_id TEXT NOT NULL CHECK(length(approval_id)=32 AND approval_id NOT GLOB '*[^0-9a-f]*'),
 owner_sid TEXT NOT NULL CHECK(length(owner_sid) BETWEEN 1 AND 256 AND instr(owner_sid,char(0))=0),
 owner_session TEXT NOT NULL CHECK(length(owner_session) BETWEEN 1 AND 256 AND instr(owner_session,char(0))=0),
 host_id TEXT NOT NULL CHECK(length(host_id)=32 AND host_id NOT GLOB '*[^0-9a-f]*'),
 recording_id TEXT NOT NULL CHECK(length(recording_id) BETWEEN 1 AND 200 AND instr(recording_id,char(0))=0),
 videos_root TEXT NOT NULL CHECK(length(videos_root) BETWEEN 4 AND 1024 AND instr(videos_root,char(0))=0),
 output_path TEXT NOT NULL CHECK(length(output_path) BETWEEN 4 AND 1024 AND instr(output_path,char(0))=0),
 volume_id BLOB NOT NULL CHECK(length(volume_id)=8),
 file_id BLOB NOT NULL CHECK(length(file_id)=16),
 recorded_bytes INTEGER NOT NULL CHECK(recorded_bytes>0),
 completed_ticks INTEGER NOT NULL CHECK(completed_ticks BETWEEN 0 AND 3155378975999999999),
 cutoff_ticks INTEGER NOT NULL CHECK(cutoff_ticks>completed_ticks AND cutoff_ticks<=3155378975999999999),
 semantics TEXT NOT NULL CHECK(semantics='permanently_delete_one_ordinary_main_mp4/v1'),
 prepared_ticks INTEGER NOT NULL CHECK(prepared_ticks BETWEEN cutoff_ticks AND 3155378975999999999),
 phase TEXT NOT NULL CHECK(phase IN ('prepared_unknown','settled')),
 progress INTEGER NULL CHECK(progress BETWEEN 0 AND 4 AND progress<>1),
 reason INTEGER NULL CHECK(reason BETWEEN 0 AND 4),
 win32_error INTEGER NULL CHECK(win32_error BETWEEN 0 AND 2147483647),
 resources_retired INTEGER NULL CHECK(resources_retired IN (0,1)),
 primary_reason INTEGER NULL CHECK(primary_reason BETWEEN 0 AND 12),
 database_cleanup INTEGER NULL CHECK(database_cleanup IN (0,1)),
 native_cleanup INTEGER NULL CHECK(native_cleanup IN (0,1)),
 settled_ticks INTEGER NULL CHECK(settled_ticks BETWEEN prepared_ticks AND 3155378975999999999),
 CHECK((phase='prepared_unknown' AND progress IS NULL AND reason IS NULL AND win32_error IS NULL AND resources_retired IS NULL AND primary_reason IS NULL AND database_cleanup IS NULL AND native_cleanup IS NULL AND settled_ticks IS NULL)
 OR (phase='settled' AND progress IS NOT NULL AND reason IS NOT NULL AND resources_retired IS NOT NULL AND settled_ticks IS NOT NULL))
) STRICT
"""),
        ("index","idx_deletion_approval","CREATE UNIQUE INDEX idx_deletion_approval ON ordinary_deletion_operations(approval_id COLLATE BINARY)"),
        ("index","idx_deletion_owner","CREATE INDEX idx_deletion_owner ON ordinary_deletion_operations(owner_sid COLLATE BINARY,owner_session COLLATE BINARY,operation_id COLLATE BINARY)"),
        ("trigger","deletion_no_delete","CREATE TRIGGER deletion_no_delete BEFORE DELETE ON ordinary_deletion_operations BEGIN SELECT RAISE(ABORT,'deletion ledger is retained'); END"),
        ("trigger","deletion_transition", "CREATE TRIGGER deletion_transition BEFORE UPDATE ON ordinary_deletion_operations WHEN OLD.phase<>'prepared_unknown' OR NEW.phase<>'settled' OR " +
            string.Join(" OR ",BindingColumns.Select(c=>"NEW."+c+" IS NOT OLD."+c)) + " BEGIN SELECT RAISE(ABORT,'invalid deletion transition'); END")
    };
    internal static readonly string Definition = string.Join(";\n",Objects.Select(x=>x.Sql))+";";
    internal const string MigrationChecksum = "a0f295f3beaa868b48c3444dd3157be9cfa1016b71d8d420b2dfa3bb2a6127f8";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { new SqliteMigrationDefinition(43,MigrationName,Definition,MigrationChecksum) };
    internal static void Validate(SqliteConnection c, SqliteTransaction? t)
    {
        if(SqliteSchemaV1.ComputeChecksum(Definition)!=MigrationChecksum)throw new InvalidDataException("Deletion schema checksum differs.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        foreach(var obj in Objects)
        {
            cmd.CommandText="SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            cmd.Parameters.AddWithValue("$type",obj.Type);cmd.Parameters.AddWithValue("$name",obj.Name);
            if(cmd.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';'))!=SqliteSchemaV1.CanonicalizeDefinition(obj.Sql))throw new InvalidDataException("Deletion schema is invalid.");
            cmd.Parameters.Clear();
        }
        cmd.CommandText="SELECT name,definition_checksum FROM schema_migrations WHERE version=43;";
        using var rd=cmd.ExecuteReader();
        if(!rd.Read()||rd.GetValue(0) is not string name||name!=MigrationName||rd.GetValue(1) is not string checksum||checksum!=MigrationChecksum||rd.Read())throw new InvalidDataException("Deletion receipt is invalid.");
    }
}
