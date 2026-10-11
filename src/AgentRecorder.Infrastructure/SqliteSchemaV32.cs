using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV32
{
    internal const string Table = "ordinary_recording_targets";
    internal const string IndexName = "idx_ordinary_recording_targets_key_created";
    internal const string MigrationName = "schema_v32_ordinary_screen_target";
    internal const string Definition = """
        CREATE TABLE ordinary_recording_target_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_sequence INTEGER NOT NULL CHECK (legacy_sequence >= 0)
        );
        INSERT INTO ordinary_recording_target_boundary SELECT 1, COALESCE(MAX(history_sequence),0) FROM ordinary_recording_history;
        CREATE TABLE ordinary_recording_targets (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            target_key TEXT COLLATE BINARY CHECK (target_key IS NULL OR
                ((substr(target_key,1,18) = 'target:v1:display:' AND length(target_key) = 82) OR
                 (substr(target_key,1,17) = 'target:v1:region:' AND length(target_key) = 81)) AND
                substr(target_key,-64) NOT GLOB '*[^0-9a-f]*'),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_history(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        );
        CREATE INDEX idx_ordinary_recording_targets_key_created ON ordinary_recording_targets(target_key,created_at_utc DESC,recording_id COLLATE BINARY ASC);
        CREATE TRIGGER ordinary_recording_target_initial AFTER INSERT ON ordinary_recording_history BEGIN
            INSERT INTO ordinary_recording_targets(recording_id,created_at_utc,target_key) VALUES(NEW.recording_id,NEW.created_at_utc,NULL);
        END;
        """;
    internal const string MigrationChecksum = "b9040e1b954fcfd34c29f058049a7147a0610b0d0b502843e8d93bd7c7507952";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(32, MigrationName, SqliteSchemaV1.CanonicalizeDefinition(Definition), MigrationChecksum) };

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed target checksum differs.");
        // Compare exact objects using constants; no extra connections or repair.
        var definition = SqliteSchemaV1.CanonicalizeDefinition(Definition);
        var tableStart = definition.IndexOf("CREATE TABLE ordinary_recording_targets", StringComparison.Ordinal);
        var indexStart = definition.IndexOf("CREATE INDEX", StringComparison.Ordinal);
        var triggerStart = definition.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);
        var objects = new[] {
            (Type: "table", Name: "ordinary_recording_target_boundary", Sql: definition[..definition.IndexOf(';')]),
            (Type: "table", Name: Table, Sql: definition[tableStart..(indexStart - 2)]),
            (Type: "index", Name: IndexName, Sql: definition[indexStart..(triggerStart - 2)]),
            (Type: "trigger", Name: "ordinary_recording_target_initial", Sql: definition[triggerStart..].TrimEnd(';')) };
        foreach (var item in objects)
        {
            using var actual = connection.CreateCommand(); actual.Transaction = transaction;
            actual.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            actual.Parameters.AddWithValue("$type", item.Type); actual.Parameters.AddWithValue("$name", item.Name);
            if (actual.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql) != item.Sql) throw new InvalidDataException("Target schema is invalid.");
        }
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=32;";
        using var receipts = receipt.ExecuteReader();
        if (!receipts.Read() || receipts.GetString(0) != MigrationName || receipts.GetString(1) != MigrationChecksum || receipts.Read())
            throw new InvalidDataException("Target migration receipt is invalid.");
        receipts.Dispose();
        receipt.CommandText = "SELECT singleton,legacy_sequence FROM ordinary_recording_target_boundary;";
        using var boundary = receipt.ExecuteReader();
        if (!boundary.Read() || boundary.GetInt64(0) != 1 || boundary.GetInt64(1) < 0 || boundary.Read())
            throw new InvalidDataException("Target legacy boundary is invalid.");
    }
}
