using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

// A missing modern marker is corruption, whereas a migration-era row may have
// no observation. Neither boundary nor marker is inferred from media on disk.
internal static class SqliteSchemaV40
{
    internal const string MigrationName = "schema_v40_future_window_publication_identity";
    internal const string IndexName = "idx_future_window_publication_authorization";
    internal const string Object0 = """
        CREATE TABLE future_window_publication_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_authorization_rowid INTEGER NOT NULL CHECK (legacy_authorization_rowid >= 0)
        )
        """;
    internal const string Object1 = """
        CREATE TABLE future_window_publication_anchors (
            authorization_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            run_id TEXT, output_path TEXT, output_size_bytes INTEGER,
            observation_version INTEGER, source TEXT, volume_id BLOB, file_id BLOB,
            CHECK ((run_id IS NULL AND output_path IS NULL AND output_size_bytes IS NULL) OR
                (run_id IS NOT NULL AND typeof(run_id) = 'text' AND length(run_id) BETWEEN 1 AND 128 AND
                output_path IS NOT NULL AND typeof(output_path) = 'text' AND length(output_path) BETWEEN 4 AND 4096 AND
                output_size_bytes IS NOT NULL AND typeof(output_size_bytes) = 'integer' AND output_size_bytes > 0)),
            CHECK ((observation_version IS NULL AND source IS NULL AND volume_id IS NULL AND file_id IS NULL) OR
                (run_id IS NOT NULL AND observation_version IS NOT NULL AND typeof(observation_version) = 'integer' AND observation_version = 1 AND
                source IS NOT NULL AND typeof(source) = 'text' AND source = 'staging_copy_handle/v1' AND
                volume_id IS NOT NULL AND typeof(volume_id) = 'blob' AND length(volume_id) = 8 AND volume_id != zeroblob(8) AND
                file_id IS NOT NULL AND typeof(file_id) = 'blob' AND length(file_id) = 16 AND file_id != zeroblob(16))),
            FOREIGN KEY (authorization_id) REFERENCES future_window_authorizations(authorization_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        )
        """;
    internal const string Object2 = """
        CREATE INDEX idx_future_window_publication_authorization ON future_window_publication_anchors(authorization_id COLLATE BINARY)
        """;
    internal const string Object3 = """
        CREATE TRIGGER future_window_publication_initial AFTER INSERT ON future_window_authorizations BEGIN
            INSERT INTO future_window_publication_anchors(authorization_id) VALUES(NEW.authorization_id);
        END
        """;
    internal const string Definition = Object0 + ";\n" +
        "INSERT INTO future_window_publication_boundary SELECT 1,COALESCE(MAX(rowid),0) FROM future_window_authorizations;\n" +
        Object1 + ";\n" + Object2 + ";\n" + Object3 + ";";
    internal const string MigrationChecksum = "5a795ee70fe61a00c9ff10cb81abbb9aa17de833aa9766c42bb1f0eaee5a92ea";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(40, MigrationName, Definition, MigrationChecksum) };

    internal static long Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Future publication checksum differs.");
        var objects = new[] {
            (Type: "table", Name: "future_window_publication_boundary", Sql: Object0),
            (Type: "table", Name: "future_window_publication_anchors", Sql: Object1),
            (Type: "index", Name: IndexName, Sql: Object2),
            (Type: "trigger", Name: "future_window_publication_initial", Sql: Object3),
        };
        foreach (var item in objects)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", item.Type); command.Parameters.AddWithValue("$name", item.Name);
            if (command.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(item.Sql))
                throw new InvalidDataException("Future publication schema is invalid.");
        }
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=40;";
        using (var reader = receipt.ExecuteReader())
            if (!reader.Read() || reader.GetValue(0) is not string name || name != MigrationName ||
                reader.GetValue(1) is not string checksum || checksum != MigrationChecksum || reader.Read())
                throw new InvalidDataException("Future publication migration receipt is invalid.");
        receipt.CommandText = "SELECT singleton,legacy_authorization_rowid FROM future_window_publication_boundary;";
        long legacy;
        using (var boundary = receipt.ExecuteReader())
        {
            if (!boundary.Read() || boundary.GetValue(0) is not long singleton || singleton != 1 ||
                boundary.GetValue(1) is not long value || value < 0 || boundary.Read())
                throw new InvalidDataException("Future publication legacy boundary is invalid.");
            legacy = value;
        }
        // MAX(rowid) is a single rowid-index edge lookup, not a table scan.
        receipt.CommandText = "SELECT COALESCE(MAX(rowid),0) FROM future_window_authorizations;";
        if (receipt.ExecuteScalar() is not long maximum || legacy > maximum)
            throw new InvalidDataException("Future publication legacy boundary is invalid.");
        return legacy;
    }
}
