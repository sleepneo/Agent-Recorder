using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV37
{
    internal const string MigrationName = "schema_v37_ordinary_bundle_attachment_hash";
    internal const string IndexName = "idx_ordinary_bundle_attachment_hash_ordinal";
    internal const string Object0 = """
        CREATE TABLE ordinary_bundle_attachment_hash_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_bundle_rowid INTEGER NOT NULL CHECK (legacy_bundle_rowid >= 0)
        )
        """;
    internal const string Object1 = """
        CREATE TABLE ordinary_bundle_attachment_hash (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            media_output_path TEXT NOT NULL CHECK (length(media_output_path) BETWEEN 1 AND 4096),
            operation_id TEXT CHECK (operation_id IS NULL OR (length(operation_id) = 32 AND operation_id NOT GLOB '*[^0-9a-f]*')),
            bundle_path TEXT CHECK (bundle_path IS NULL OR length(bundle_path) BETWEEN 1 AND 4096),
            evidence_version INTEGER CHECK (evidence_version IS NULL OR evidence_version = 1),
            item_count INTEGER NOT NULL CHECK ((evidence_version IS NULL AND item_count = 0) OR (evidence_version = 1 AND item_count = 5)),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_bundles(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        )
        """;
    internal const string Object2 = """
        CREATE TABLE ordinary_bundle_attachment_hash_items (
            recording_id TEXT NOT NULL COLLATE BINARY,
            ordinal INTEGER NOT NULL CHECK (ordinal BETWEEN 0 AND 4),
            name TEXT NOT NULL COLLATE BINARY CHECK (name IN ('metadata.json','thumbnail.jpg','first_frame.png','last_frame.png','marks.json')),
            output_path TEXT NOT NULL CHECK (length(output_path) BETWEEN 1 AND 4096),
            output_size_bytes INTEGER NOT NULL CHECK (typeof(output_size_bytes) = 'integer' AND output_size_bytes > 0),
            algorithm TEXT NOT NULL CHECK (algorithm = 'sha256'),
            hash_value TEXT NOT NULL CHECK (typeof(hash_value) = 'text' AND length(hash_value) = 64 AND hash_value NOT GLOB '*[^0-9a-f]*'),
            basis TEXT NOT NULL CHECK (basis = 'bundle_publication'),
            volume_id BLOB NOT NULL CHECK (typeof(volume_id) = 'blob' AND length(volume_id) = 8),
            file_id BLOB NOT NULL CHECK (typeof(file_id) = 'blob' AND length(file_id) = 16),
            PRIMARY KEY (recording_id,name),
            FOREIGN KEY (recording_id) REFERENCES ordinary_bundle_attachment_hash(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        )
        """;
    internal const string Object3 = """
        CREATE UNIQUE INDEX idx_ordinary_bundle_attachment_hash_ordinal ON ordinary_bundle_attachment_hash_items(recording_id COLLATE BINARY,ordinal ASC)
        """;
    internal const string Object4 = """
        CREATE TRIGGER ordinary_bundle_attachment_hash_initial AFTER INSERT ON ordinary_recording_bundles BEGIN
            INSERT INTO ordinary_bundle_attachment_hash(recording_id,media_output_path,operation_id,bundle_path,evidence_version,item_count)
            VALUES(NEW.recording_id,NEW.media_output_path,NEW.operation_id,NEW.bundle_path,NULL,0);
        END
        """;
    internal const string Definition = Object0 + ";\n" +
        "INSERT INTO ordinary_bundle_attachment_hash_boundary SELECT 1,COALESCE(MAX(rowid),0) FROM ordinary_recording_bundles;\n" +
        Object1 + ";\n" + Object2 + ";\n" + Object3 + ";\n" + Object4 + ";\n";
    internal const string MigrationChecksum = "eb651b35d8a16ffeff89e39cd3ad20092d84f71efd2ed51700628ca8236168b0";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(37, MigrationName, Definition, MigrationChecksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed Bundle attachment hash checksum differs.");
        var objects = new[] {
            (Type: "table", Name: "ordinary_bundle_attachment_hash_boundary", Sql: Object0),
            (Type: "table", Name: "ordinary_bundle_attachment_hash", Sql: Object1),
            (Type: "table", Name: "ordinary_bundle_attachment_hash_items", Sql: Object2),
            (Type: "index", Name: "idx_ordinary_bundle_attachment_hash_ordinal", Sql: Object3),
            (Type: "trigger", Name: "ordinary_bundle_attachment_hash_initial", Sql: Object4),
        };
        foreach (var item in objects)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", item.Type); command.Parameters.AddWithValue("$name", item.Name);
            if (command.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(item.Sql))
                throw new InvalidDataException("Bundle attachment hash schema is invalid.");
        }
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=37;";
        using (var reader = receipt.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != MigrationName || reader.GetString(1) != MigrationChecksum || reader.Read())
                throw new InvalidDataException("Bundle attachment hash migration receipt is invalid.");
        receipt.CommandText = "SELECT singleton,legacy_bundle_rowid FROM ordinary_bundle_attachment_hash_boundary;";
        using var boundary = receipt.ExecuteReader();
        if (!boundary.Read() || boundary.GetValue(0) is not long singleton || singleton != 1 ||
            boundary.GetValue(1) is not long legacyRowid || legacyRowid < 0 || boundary.Read())
            throw new InvalidDataException("Bundle attachment hash legacy boundary is invalid.");
    }
}
