using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV34
{
    internal const string MigrationName = "schema_v34_ordinary_window_target";
    internal const string Table = "ordinary_recording_window_targets";
    internal const string IndexName = "idx_ordinary_window_targets_key_created";
    internal const string Object0 = """
        CREATE TABLE ordinary_window_target_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_sequence INTEGER NOT NULL CHECK (legacy_sequence >= 0)
        )
        """;
    internal const string Object1 = """
        CREATE TABLE ordinary_recording_window_targets (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            source_type TEXT NOT NULL,
            capture_semantics TEXT NOT NULL,
            target_key TEXT COLLATE BINARY CHECK (target_key IS NULL OR
                (source_type = 'window' AND capture_semantics = 'window_surface' AND
                 substr(target_key,1,17) = 'target:v1:window:' AND length(target_key) = 81 AND
                 substr(target_key,-64) NOT GLOB '*[^0-9a-f]*')),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_history(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        )
        """;
    internal const string Object2 = """
        CREATE INDEX idx_ordinary_window_targets_key_created ON ordinary_recording_window_targets(target_key,created_at_utc DESC,recording_id COLLATE BINARY ASC)
        """;
    internal const string Object3 = """
        CREATE TRIGGER ordinary_window_target_initial AFTER INSERT ON ordinary_recording_history BEGIN
            INSERT INTO ordinary_recording_window_targets(recording_id,created_at_utc,source_type,capture_semantics,target_key)
            VALUES(NEW.recording_id,NEW.created_at_utc,NEW.source_type,NEW.capture_semantics,NULL);
        END
        """;
    internal const string Definition = Object0 + ";\n" +
        "INSERT INTO ordinary_window_target_boundary SELECT 1,COALESCE(MAX(history_sequence),0) FROM ordinary_recording_history;\n" +
        Object1 + ";\n" + Object2 + ";\n" + Object3 + ";\n";
    internal const string MigrationChecksum = "59a62efd7a33bdfa6fc1f521eccb4c7a96fd977020c258eae255d9a3a684d05b";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(34, MigrationName, Definition, MigrationChecksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed window target checksum differs.");
        var objects = new[] {
            (Type: "table", Name: "ordinary_window_target_boundary", Sql: Object0),
            (Type: "table", Name: Table, Sql: Object1),
            (Type: "index", Name: IndexName, Sql: Object2),
            (Type: "trigger", Name: "ordinary_window_target_initial", Sql: Object3) };
        foreach (var item in objects)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            command.Parameters.AddWithValue("$type", item.Type); command.Parameters.AddWithValue("$name", item.Name);
            if (command.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(item.Sql))
                throw new InvalidDataException("Window target schema is invalid.");
        }
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=34;";
        using (var reader = receipt.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != MigrationName || reader.GetString(1) != MigrationChecksum || reader.Read())
                throw new InvalidDataException("Window target migration receipt is invalid.");
        receipt.CommandText = "SELECT singleton,legacy_sequence FROM ordinary_window_target_boundary;";
        using var boundary = receipt.ExecuteReader();
        if (!boundary.Read() || boundary.GetValue(0) is not long singleton || singleton != 1 ||
            boundary.GetValue(1) is not long sequence || sequence < 0 || boundary.Read())
            throw new InvalidDataException("Window target legacy boundary is invalid.");
    }
}
