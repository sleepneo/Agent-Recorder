using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

// Result receipts only. The host setting and latest set are never stored.
internal static class SqliteSchemaV51
{
    internal const string Name = "schema_v51_deletion_host_rejections";
    internal const string Table = "ordinary_deletion_host_rejections";
    private static readonly (string Type, string Name, string Sql)[] Objects =
    {
        ("table", Table, "CREATE TABLE ordinary_deletion_host_rejections (request_id TEXT NULL COLLATE BINARY UNIQUE REFERENCES ordinary_deletion_requests(request_id), operation_id TEXT NULL COLLATE BINARY UNIQUE REFERENCES ordinary_deletion_operations(operation_id), reason TEXT NOT NULL CHECK(reason IN ('within_host_keep_latest','host_keep_latest_configuration_invalid','host_keep_latest_unavailable')), CHECK((request_id IS NULL) <> (operation_id IS NULL))) STRICT"),
        ("trigger", "ordinary_deletion_host_rejections_immutable", "CREATE TRIGGER ordinary_deletion_host_rejections_immutable BEFORE UPDATE ON ordinary_deletion_host_rejections BEGIN SELECT RAISE(ABORT,'immutable host deletion rejection'); END"),
        ("trigger", "ordinary_deletion_host_rejections_retained", "CREATE TRIGGER ordinary_deletion_host_rejections_retained BEFORE DELETE ON ordinary_deletion_host_rejections BEGIN SELECT RAISE(ABORT,'host deletion rejection retained'); END"),
    };

    internal static readonly string Definition = string.Join(";\n", Objects.Select(o => o.Sql)) + ";";
    internal const string Checksum = "ef145cbb38a63c654d31f80f609f545992e53113ca18e6b3649568c9efb655c6";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } =
        new[] { new SqliteMigrationDefinition(51, Name, Definition, Checksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != Checksum)
            throw new InvalidDataException("Host deletion rejection schema checksum differs.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var expected in Objects)
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", expected.Type);
            command.Parameters.AddWithValue("$name", expected.Name);
            if (command.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != expected.Sql)
                throw new InvalidDataException("Host deletion rejection schema differs.");
            command.Parameters.Clear();
        }
        command.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=51;";
        using var receipt = command.ExecuteReader();
        if (!receipt.Read() || receipt.GetValue(0) is not string name || name != Name ||
            receipt.GetValue(1) is not string checksum || checksum != Checksum || receipt.Read())
            throw new InvalidDataException("Host deletion rejection receipt differs.");
    }
}
