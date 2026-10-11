using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV31
{
    internal const string Table = "ordinary_recording_bundles";
    internal const string MigrationName = "schema_v31_ordinary_bundle_history";
    internal const string Definition = """
        CREATE TABLE ordinary_recording_bundles (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            media_output_path TEXT NOT NULL CHECK (length(media_output_path) BETWEEN 1 AND 4096),
            operation_id TEXT CHECK (operation_id IS NULL OR (length(operation_id) = 32 AND operation_id NOT GLOB '*[^0-9a-f]*')),
            status_code TEXT NOT NULL CHECK (status_code IN ('generating','ready','failed','not_applicable','interrupted_unknown')),
            bundle_path TEXT CHECK (bundle_path IS NULL OR length(bundle_path) BETWEEN 1 AND 4096),
            error_code TEXT CHECK (error_code IS NULL OR error_code IN ('bundle_already_exists','bundle_hash_failed','bundle_frame_extract_failed','bundle_frame_output_invalid','bundle_metadata_write_failed','bundle_marks_write_failed','bundle_publish_failed','bundle_generation_failed','bundle_interrupted_unknown')),
            CHECK ((status_code = 'not_applicable' AND operation_id IS NULL AND bundle_path IS NULL AND error_code IS NULL) OR
                   (status_code IN ('generating','ready') AND operation_id IS NOT NULL AND bundle_path IS NOT NULL AND error_code IS NULL) OR
                   (status_code = 'failed' AND operation_id IS NOT NULL AND bundle_path IS NOT NULL AND error_code IS NOT NULL AND error_code != 'bundle_interrupted_unknown') OR
                   (status_code = 'interrupted_unknown' AND operation_id IS NULL AND bundle_path IS NOT NULL AND error_code = 'bundle_interrupted_unknown')),
            FOREIGN KEY (recording_id) REFERENCES ordinary_recording_history(recording_id) ON UPDATE NO ACTION ON DELETE NO ACTION
        );
        """;
    internal const string MigrationChecksum = "e3105eefad82884651c430b5a9412e71e9f4094231f94de01293538210604146";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(31, MigrationName, SqliteSchemaV1.CanonicalizeDefinition(Definition), MigrationChecksum) };
    // Exact definition validates all columns, affinities, checks, PK and FK without
    // widening the legacy INTEGER/TEXT-only table descriptor or old migrations.
    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Fixed bundle migration checksum differs.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", Table);
        if (command.ExecuteScalar() is not string sql ||
            SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(Definition.TrimEnd().TrimEnd(';')))
            throw new InvalidDataException("Bundle history schema is invalid.");
        command.Parameters.Clear();
        command.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=31;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != MigrationName || reader.GetString(1) != MigrationChecksum || reader.Read())
            throw new InvalidDataException("Bundle history migration receipt is invalid.");
    }
}
