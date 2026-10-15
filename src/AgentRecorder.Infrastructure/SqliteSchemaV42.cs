using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV42
{
    internal const string MigrationName = "schema_v42_publication_identity_references";
    internal const string OrdinaryIndex = "idx_ordinary_publication_identity";
    internal const string FutureIndex = "idx_future_window_publication_identity";
    internal const string PlanIndex = "idx_plan_media_publication_identity";
    internal static readonly (string Name, string Table, string Owner, string Sql)[] Indexes =
    {
        (OrdinaryIndex, "ordinary_publication_anchors", "recording_id", "CREATE INDEX idx_ordinary_publication_identity ON ordinary_publication_anchors(volume_id,file_id,recording_id COLLATE BINARY)"),
        (FutureIndex, "future_window_publication_anchors", "authorization_id", "CREATE INDEX idx_future_window_publication_identity ON future_window_publication_anchors(volume_id,file_id,authorization_id COLLATE BINARY)"),
        (PlanIndex, "plan_media_publication_anchors", "run_id", "CREATE INDEX idx_plan_media_publication_identity ON plan_media_publication_anchors(volume_id,file_id,run_id COLLATE BINARY)")
    };
    internal static readonly string Definition = string.Join(";\n", Indexes.Select(i => i.Sql)) + ";";
    internal const string MigrationChecksum = "9f1312e9f0076f2a359062305206c79de69d265364e10c49f3cb34ff202dc469";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[]
    {
        new SqliteMigrationDefinition(42, MigrationName, Definition, MigrationChecksum)
    };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        // Only structures and receipts; no foreign business evidence or media.
        SqliteSchemaV30.Validate(connection, transaction);
        SqliteSchemaV40.Validate(connection, transaction);
        SqliteSchemaV41.Validate(connection, transaction);
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum)
            throw new InvalidDataException("Publication reference checksum differs.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var index in Indexes)
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name AND tbl_name=$table;";
            command.Parameters.AddWithValue("$name", index.Name); command.Parameters.AddWithValue("$table", index.Table);
            if (command.ExecuteScalar() is not string sql ||
                SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(index.Sql))
                throw new InvalidDataException("Publication reference index is invalid.");
            command.Parameters.Clear();
            command.CommandText = "PRAGMA index_xinfo(" + index.Name + ");";
            using var reader = command.ExecuteReader();
            var columns = new[] { "volume_id", "file_id", index.Owner }; var key = 0;
            while (reader.Read())
            {
                if (reader.GetInt64(5) == 0) continue;
                if (key >= columns.Length || reader.GetValue(2) is not string name || name != columns[key++] ||
                    reader.GetInt64(3) != 0 || reader.GetValue(4) is not string collation || collation != "BINARY")
                    throw new InvalidDataException("Publication reference index keys are invalid.");
            }
            if (key != columns.Length) throw new InvalidDataException("Publication reference index keys are missing.");
        }
        command.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=42;";
        using var receipt = command.ExecuteReader();
        if (!receipt.Read() || receipt.GetValue(0) is not string migration || migration != MigrationName ||
            receipt.GetValue(1) is not string checksum || checksum != MigrationChecksum || receipt.Read())
            throw new InvalidDataException("Publication reference migration receipt is invalid.");
    }
}
