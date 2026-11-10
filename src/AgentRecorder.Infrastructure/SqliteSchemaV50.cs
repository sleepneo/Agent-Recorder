using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV50
{
    internal const string Name = "schema_v50_batch_deletion_policy";
    private static readonly (string Type, string Name, string Sql)[] Objects =
    {
        ("table", "ordinary_deletion_batch_policies", "CREATE TABLE ordinary_deletion_batch_policies (\n id TEXT NOT NULL COLLATE BINARY PRIMARY KEY REFERENCES ordinary_deletion_batches(batch_id),\n policy_cutoff INTEGER NOT NULL CHECK(policy_cutoff BETWEEN 0 AND 3155378975999999999),\n policy_keep_count INTEGER NOT NULL CHECK(policy_keep_count BETWEEN 1 AND 1000),\n policy_digest TEXT NOT NULL CHECK(length(policy_digest)=64 AND instr(policy_digest,char(0))=0 AND policy_digest NOT GLOB '*[^0-9a-f]*')\n) STRICT"),
        ("table", "ordinary_deletion_batch_policy_children", "CREATE TABLE ordinary_deletion_batch_policy_children (\n child_id TEXT NOT NULL COLLATE BINARY PRIMARY KEY REFERENCES ordinary_deletion_requests(request_id),\n batch_id TEXT NOT NULL COLLATE BINARY REFERENCES ordinary_deletion_batch_policies(id)\n) STRICT"),
        ("trigger", "ordinary_deletion_batch_policies_immutable", "CREATE TRIGGER ordinary_deletion_batch_policies_immutable BEFORE UPDATE ON ordinary_deletion_batch_policies BEGIN SELECT RAISE(ABORT,'immutable batch deletion policy evidence'); END"),
        ("trigger", "ordinary_deletion_batch_policies_retained", "CREATE TRIGGER ordinary_deletion_batch_policies_retained BEFORE DELETE ON ordinary_deletion_batch_policies BEGIN SELECT RAISE(ABORT,'batch deletion policy evidence retained'); END"),
        ("trigger", "ordinary_deletion_batch_policy_children_immutable", "CREATE TRIGGER ordinary_deletion_batch_policy_children_immutable BEFORE UPDATE ON ordinary_deletion_batch_policy_children BEGIN SELECT RAISE(ABORT,'immutable batch deletion policy evidence'); END"),
        ("trigger", "ordinary_deletion_batch_policy_children_retained", "CREATE TRIGGER ordinary_deletion_batch_policy_children_retained BEFORE DELETE ON ordinary_deletion_batch_policy_children BEGIN SELECT RAISE(ABORT,'batch deletion policy evidence retained'); END"),
    };

    internal static readonly string Definition = string.Join(";\n", Objects.Select(o => o.Sql)) + ";";
    internal const string Checksum = "1a28f27a0ae44d847a751f2ef9954c17d3b4fe4a1e258c8f264cf2a5ef6fda25";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } =
        new[] { new SqliteMigrationDefinition(50, Name, Definition, Checksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != Checksum)
            throw new InvalidDataException("Batch policy schema checksum differs.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var expected in Objects)
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", expected.Type);
            command.Parameters.AddWithValue("$name", expected.Name);
            if (command.ExecuteScalar() is not string sql ||
                SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != expected.Sql)
                throw new InvalidDataException("Batch policy schema differs.");
            command.Parameters.Clear();
        }

        command.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=50;";
        using var receipt = command.ExecuteReader();
        if (!receipt.Read() || receipt.GetValue(0) is not string name || name != Name ||
            receipt.GetValue(1) is not string checksum || checksum != Checksum || receipt.Read())
            throw new InvalidDataException("Batch policy receipt differs.");
    }
}
