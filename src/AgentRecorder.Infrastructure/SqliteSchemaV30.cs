using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV30
{
    internal const string Table = "ordinary_publication_anchors";
    internal const string MigrationName = "schema_v30_ordinary_publication_identity";
    internal const string Definition = """
        CREATE TABLE ordinary_publication_anchors (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            output_path TEXT NOT NULL CHECK (length(output_path) BETWEEN 4 AND 1024),
            output_size_bytes INTEGER NOT NULL CHECK (output_size_bytes > 0),
            observation_version INTEGER NOT NULL CHECK (observation_version = 1),
            source TEXT NOT NULL CHECK (source = 'staging_copy_handle/v1'),
            volume_id BLOB NOT NULL CHECK (typeof(volume_id) = 'blob' AND length(volume_id) = 8 AND volume_id != zeroblob(8)),
            file_id BLOB NOT NULL CHECK (typeof(file_id) = 'blob' AND length(file_id) = 16 AND file_id != zeroblob(16)),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_history(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        );
        """;
    internal const string MigrationChecksum = "c348068b6e9b8fbb620c5f59489b6ede68fce067b81f6f308f32c0a15673a980";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(30, MigrationName, SqliteSchemaV1.CanonicalizeDefinition(Definition), MigrationChecksum) };
    // Exact definition validates all columns, affinities, checks, PK and FK without
    // widening the legacy INTEGER/TEXT-only table descriptor or old migrations.
    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed publication migration checksum differs.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", Table);
        if (command.ExecuteScalar() is not string sql ||
            SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(Definition.TrimEnd().TrimEnd(';')))
            throw new InvalidDataException("Publication anchor schema is invalid.");
        command.Parameters.Clear();
        command.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=30;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != MigrationName || reader.GetString(1) != MigrationChecksum || reader.Read())
            throw new InvalidDataException("Publication anchor migration receipt is invalid.");
    }
}
