using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>V22 adds an independent ordinary-video history projection.</summary>
internal static class SqliteSchemaV22
{
    public const string MigrationName = "schema_v22_ordinary_recording_history";
    public const string MigrationChecksum = "16724c936346743a8706ebca471e8ce2a47fc01d7e7e7d4a7ec75bc5ca5b5deb";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[] { CreateMigration() });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("ordinary_recording_history", new[]
        {
            new SqliteColumnDefinition("recording_id", "TEXT", true, 1),
            new SqliteColumnDefinition("history_sequence", "INTEGER", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("source_type", "TEXT", true, 0),
            new SqliteColumnDefinition("capture_semantics", "TEXT", true, 0),
            new SqliteColumnDefinition("source_title", "TEXT", true, 0),
            new SqliteColumnDefinition("requested_duration_seconds", "INTEGER", false, 0),
            new SqliteColumnDefinition("audio_mode", "TEXT", true, 0),
            new SqliteColumnDefinition("microphone_device_id", "TEXT", false, 0),
            new SqliteColumnDefinition("microphone_device_name", "TEXT", false, 0),
            new SqliteColumnDefinition("system_audio_endpoint_id", "TEXT", false, 0),
            new SqliteColumnDefinition("system_audio_endpoint_name", "TEXT", false, 0),
            new SqliteColumnDefinition("output_path", "TEXT", true, 0),
            new SqliteColumnDefinition("nested_role", "TEXT", false, 0),
            new SqliteColumnDefinition("parent_recording_id", "TEXT", false, 0),
            new SqliteColumnDefinition("nested_session_id", "TEXT", false, 0),
            new SqliteColumnDefinition("profile_id", "TEXT", false, 0),
            new SqliteColumnDefinition("profile_version", "INTEGER", false, 0),
            new SqliteColumnDefinition("profile_digest", "TEXT", false, 0),
            new SqliteColumnDefinition("started_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("completed_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("output_size_bytes", "INTEGER", false, 0),
            new SqliteColumnDefinition("output_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_ordinary_recording_history_created", "ordinary_recording_history", false,
            new[] { "created_at_utc", "recording_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("ordinary_recording_history", new[] { "history_sequence" }),
    });
    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("recording_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(recording_id\\)\\)\\s+BETWEEN\\s+1\\s+AND\\s+128"),
        Check("status_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(status_code\\s+IN"),
        Check("source_type\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(source_type\\)\\s+BETWEEN\\s+1\\s+AND\\s+64"),
        Check("capture_semantics\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(capture_semantics\\)\\s+BETWEEN\\s+1\\s+AND\\s+64"),
        Check("source_title\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(source_title\\)\\s*<=\\s*512"),
        Check("requested_duration_seconds\\s+INTEGER.*CHECK\\s*\\(requested_duration_seconds\\s+IS\\s+NULL"),
        Check("profile_version\\s+INTEGER.*CHECK\\s*\\(profile_version\\s+IS\\s+NULL"),
        Check("CHECK\\s*\\(\\(profile_id\\s+IS\\s+NULL.*profile_digest\\s+IS\\s+NOT\\s+NULL\\)"),
        Check("updated_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(updated_at_utc\\s+>=\\s+created_at_utc"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.Empty<SqliteTriggerDefinition>();

    private const string SchemaDefinition = """
        CREATE TABLE ordinary_recording_history (
            recording_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK (length(trim(recording_id)) BETWEEN 1 AND 128),
            history_sequence INTEGER NOT NULL UNIQUE CHECK (history_sequence > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            status_code TEXT NOT NULL CHECK (status_code IN ('pending_confirmation','preparing','countdown','recording','finalizing','completed','failed','cancelled','rejected','expired','interrupted_unknown')),
            source_type TEXT NOT NULL CHECK (length(source_type) BETWEEN 1 AND 64),
            capture_semantics TEXT NOT NULL CHECK (length(capture_semantics) BETWEEN 1 AND 64),
            source_title TEXT NOT NULL CHECK (length(source_title) <= 512),
            requested_duration_seconds INTEGER NULL CHECK (requested_duration_seconds IS NULL OR requested_duration_seconds BETWEEN 1 AND 7200),
            audio_mode TEXT NOT NULL CHECK (audio_mode IN ('none','microphone','system_loopback')),
            microphone_device_id TEXT NULL,
            microphone_device_name TEXT NULL,
            system_audio_endpoint_id TEXT NULL,
            system_audio_endpoint_name TEXT NULL,
            output_path TEXT NOT NULL CHECK (length(output_path) BETWEEN 1 AND 4096),
            nested_role TEXT NULL CHECK (nested_role IS NULL OR nested_role IN ('outer','inner')),
            parent_recording_id TEXT NULL,
            nested_session_id TEXT NULL,
            profile_id TEXT NULL,
            profile_version INTEGER NULL CHECK (profile_version IS NULL OR profile_version > 0),
            profile_digest TEXT NULL,
            started_at_utc INTEGER NULL CHECK (started_at_utc IS NULL OR started_at_utc >= created_at_utc),
            completed_at_utc INTEGER NULL CHECK (completed_at_utc IS NULL OR completed_at_utc >= created_at_utc),
            reason_code TEXT NULL CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
            output_size_bytes INTEGER NULL CHECK (output_size_bytes IS NULL OR output_size_bytes >= 0),
            output_duration_ms INTEGER NULL CHECK (output_duration_ms IS NULL OR output_duration_ms >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            CHECK ((profile_id IS NULL AND profile_version IS NULL AND profile_digest IS NULL) OR
                   (profile_id IS NOT NULL AND profile_version IS NOT NULL AND profile_digest IS NOT NULL))
        );
        CREATE INDEX idx_ordinary_recording_history_created
            ON ordinary_recording_history(created_at_utc DESC, recording_id ASC);
        """;

    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v22 definition checksum is {checksum}.");
        return new SqliteMigrationDefinition(22, MigrationName, canonical, checksum);
    }

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("ordinary_recording_history", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));
}
