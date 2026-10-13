using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV36
{
    internal const string MigrationName = "schema_v36_ordinary_media_hash";
    internal const string IndexName = "idx_ordinary_media_hash_recording";
    internal const string Object0 = """
        CREATE TABLE ordinary_media_hash_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_bundle_rowid INTEGER NOT NULL CHECK (legacy_bundle_rowid >= 0)
        )
        """;
    internal const string Object1 = """
        CREATE TABLE ordinary_media_hash (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            media_output_path TEXT NOT NULL CHECK (length(media_output_path) BETWEEN 1 AND 4096),
            operation_id TEXT CHECK (operation_id IS NULL OR (length(operation_id) = 32 AND operation_id NOT GLOB '*[^0-9a-f]*')),
            bundle_path TEXT CHECK (bundle_path IS NULL OR length(bundle_path) BETWEEN 1 AND 4096),
            evidence_version INTEGER CHECK (evidence_version IS NULL OR evidence_version = 1),
            algorithm TEXT, hash_value TEXT, basis TEXT, size_bytes INTEGER, volume_id BLOB, file_id BLOB,
            CHECK ((evidence_version IS NULL AND algorithm IS NULL AND hash_value IS NULL AND basis IS NULL AND size_bytes IS NULL AND volume_id IS NULL AND file_id IS NULL) OR
                (evidence_version = 1 AND algorithm IS NOT NULL AND algorithm = 'sha256' AND
                hash_value IS NOT NULL AND typeof(hash_value) = 'text' AND length(hash_value) = 64 AND hash_value NOT GLOB '*[^0-9a-f]*' AND
                basis IS NOT NULL AND basis = 'bundle_generation' AND size_bytes IS NOT NULL AND typeof(size_bytes) = 'integer' AND size_bytes > 0 AND
                volume_id IS NOT NULL AND typeof(volume_id) = 'blob' AND length(volume_id) = 8 AND
                file_id IS NOT NULL AND typeof(file_id) = 'blob' AND length(file_id) = 16)),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_bundles(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        )
        """;
    internal const string Object2 = """
        CREATE INDEX idx_ordinary_media_hash_recording ON ordinary_media_hash(recording_id COLLATE BINARY)
        """;
    internal const string Object3 = """
        CREATE TRIGGER ordinary_media_hash_initial AFTER INSERT ON ordinary_recording_bundles BEGIN
            INSERT INTO ordinary_media_hash(recording_id,media_output_path,operation_id,bundle_path)
            VALUES(NEW.recording_id,NEW.media_output_path,NEW.operation_id,NEW.bundle_path);
        END
        """;
    internal const string Definition = Object0 + ";\n" +
        "INSERT INTO ordinary_media_hash_boundary SELECT 1,COALESCE(MAX(rowid),0) FROM ordinary_recording_bundles;\n" +
        Object1 + ";\n" + Object2 + ";\n" + Object3 + ";\n";
    internal const string MigrationChecksum = "54ad4762e069f370fe6014ea23c7d6047799bce14437028f0c292ebce090469d";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(36, MigrationName, Definition, MigrationChecksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed Media hash checksum differs.");
        var objects = new[] {
            (Type: "table", Name: "ordinary_media_hash_boundary", Sql: Object0),
            (Type: "table", Name: "ordinary_media_hash", Sql: Object1),
            (Type: "index", Name: "idx_ordinary_media_hash_recording", Sql: Object2),
            (Type: "trigger", Name: "ordinary_media_hash_initial", Sql: Object3),
        };
        foreach (var item in objects)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", item.Type); command.Parameters.AddWithValue("$name", item.Name);
            if (command.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(item.Sql))
                throw new InvalidDataException("Media hash schema is invalid.");
        }
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=36;";
        using (var reader = receipt.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != MigrationName || reader.GetString(1) != MigrationChecksum || reader.Read())
                throw new InvalidDataException("Media hash migration receipt is invalid.");
        receipt.CommandText = "SELECT singleton,legacy_bundle_rowid FROM ordinary_media_hash_boundary;";
        using var boundary = receipt.ExecuteReader();
        if (!boundary.Read() || boundary.GetValue(0) is not long singleton || singleton != 1 ||
            boundary.GetValue(1) is not long legacyRowid || legacyRowid < 0 || boundary.Read())
            throw new InvalidDataException("Media hash legacy boundary is invalid.");
    }
}
