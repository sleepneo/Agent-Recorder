using Microsoft.Data.Sqlite;
namespace AgentRecorder.Infrastructure;
internal static class SqliteSchemaV49
{
    internal const string Name="schema_v49_single_deletion_policy";
    internal static readonly (string Type,string Name,string Sql)[] Objects={
        ("table","ordinary_deletion_request_policies","CREATE TABLE ordinary_deletion_request_policies (id TEXT NOT NULL COLLATE BINARY PRIMARY KEY REFERENCES ordinary_deletion_requests(request_id), policy_cutoff INTEGER NOT NULL CHECK(policy_cutoff BETWEEN 0 AND 3155378975999999999), policy_keep_count INTEGER NOT NULL CHECK(policy_keep_count BETWEEN 1 AND 1000), policy_digest TEXT NOT NULL CHECK(length(policy_digest)=64 AND instr(policy_digest,char(0))=0 AND policy_digest NOT GLOB '*[^0-9a-f]*'), policy_reason TEXT NULL CHECK(policy_reason IN ('within_keep_latest','completion_not_before_cutoff'))) STRICT"),
        ("trigger","ordinary_deletion_request_policies_immutable","CREATE TRIGGER ordinary_deletion_request_policies_immutable BEFORE UPDATE ON ordinary_deletion_request_policies WHEN NEW.id IS NOT OLD.id OR NEW.policy_cutoff IS NOT OLD.policy_cutoff OR NEW.policy_keep_count IS NOT OLD.policy_keep_count OR NEW.policy_digest IS NOT OLD.policy_digest OR OLD.policy_reason IS NOT NULL OR NEW.policy_reason IS NULL BEGIN SELECT RAISE(ABORT,'immutable deletion policy'); END"),
        ("trigger","ordinary_deletion_request_policies_retained","CREATE TRIGGER ordinary_deletion_request_policies_retained BEFORE DELETE ON ordinary_deletion_request_policies BEGIN SELECT RAISE(ABORT,'deletion policy evidence retained'); END"),
        ("table","ordinary_deletion_operation_policies","CREATE TABLE ordinary_deletion_operation_policies (id TEXT NOT NULL COLLATE BINARY PRIMARY KEY REFERENCES ordinary_deletion_operations(operation_id), policy_cutoff INTEGER NOT NULL CHECK(policy_cutoff BETWEEN 0 AND 3155378975999999999), policy_keep_count INTEGER NOT NULL CHECK(policy_keep_count BETWEEN 1 AND 1000), policy_digest TEXT NOT NULL CHECK(length(policy_digest)=64 AND instr(policy_digest,char(0))=0 AND policy_digest NOT GLOB '*[^0-9a-f]*'), policy_reason TEXT NULL CHECK(policy_reason IN ('within_keep_latest','completion_not_before_cutoff'))) STRICT"),
        ("trigger","ordinary_deletion_operation_policies_immutable","CREATE TRIGGER ordinary_deletion_operation_policies_immutable BEFORE UPDATE ON ordinary_deletion_operation_policies WHEN NEW.id IS NOT OLD.id OR NEW.policy_cutoff IS NOT OLD.policy_cutoff OR NEW.policy_keep_count IS NOT OLD.policy_keep_count OR NEW.policy_digest IS NOT OLD.policy_digest OR OLD.policy_reason IS NOT NULL OR NEW.policy_reason IS NULL BEGIN SELECT RAISE(ABORT,'immutable deletion policy'); END"),
        ("trigger","ordinary_deletion_operation_policies_retained","CREATE TRIGGER ordinary_deletion_operation_policies_retained BEFORE DELETE ON ordinary_deletion_operation_policies BEGIN SELECT RAISE(ABORT,'deletion policy evidence retained'); END")
    };
    internal static readonly string Definition=string.Join(";\n",Objects.Select(x=>x.Sql))+";";
    internal const string Checksum="de8578f33db040cb37c1c9e3f7e073866f79b7b51eaa00226eb66cb03ea0f836";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations {get;}=new[]{new SqliteMigrationDefinition(49,Name,Definition,Checksum)};
    internal static void Validate(SqliteConnection c,SqliteTransaction? t)
    {
        if(SqliteSchemaV1.ComputeChecksum(Definition)!=Checksum)throw new InvalidDataException("Policy schema checksum differs.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        foreach(var o in Objects){cmd.CommandText="SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";cmd.Parameters.AddWithValue("$type",o.Type);cmd.Parameters.AddWithValue("$name",o.Name);
            if(cmd.ExecuteScalar() is not string sql||SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';'))!=SqliteSchemaV1.CanonicalizeDefinition(o.Sql))throw new InvalidDataException("Policy schema differs.");cmd.Parameters.Clear();}
        cmd.CommandText="SELECT name,definition_checksum FROM schema_migrations WHERE version=49;";using var r=cmd.ExecuteReader();
        if(!r.Read()||r.GetValue(0) is not string name||name!=Name||r.GetValue(1) is not string hash||hash!=Checksum||r.Read())throw new InvalidDataException("Policy receipt differs.");
    }
}
