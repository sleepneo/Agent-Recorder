using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV26
{
    public const string MigrationName = "schema_v26_ordinary_history_profile_filter";
    public const string MigrationChecksum = "8eaf8a5fd54d4a826367d13089f641be8fa7f24ef686b902647085766c99af9d";
    internal const string IdIndexName = "idx_ordinary_recording_history_profile_created";
    internal const string VersionIndexName = "idx_ordinary_recording_history_profile_version_created";
    private const string IdDefinition = "CREATE INDEX idx_ordinary_recording_history_profile_created ON ordinary_recording_history(profile_id COLLATE BINARY, created_at_utc DESC, recording_id COLLATE BINARY ASC);";
    private const string VersionDefinition = "CREATE INDEX idx_ordinary_recording_history_profile_version_created ON ordinary_recording_history(profile_id COLLATE BINARY, profile_version, created_at_utc DESC, recording_id COLLATE BINARY ASC);";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = new[]
    {
        Index(IdIndexName, new[] { "profile_id", "created_at_utc", "recording_id" }, IdDefinition),
        Index(VersionIndexName, new[] { "profile_id", "profile_version", "created_at_utc", "recording_id" }, VersionDefinition)
    };
    private static SqliteIndexDefinition Index(string name, string[] columns, string definition) =>
        new(name, "ordinary_recording_history", false, columns, DefinitionPattern: new Regex(
            "^" + Regex.Escape(definition.TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(IdDefinition + "\n" + VersionDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (checksum != MigrationChecksum) throw new InvalidOperationException($"The fixed schema v26 definition checksum is {checksum}.");
        return new(26, MigrationName, canonical, checksum);
    }
}
