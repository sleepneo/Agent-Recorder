using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV25
{
    public const string MigrationName = "schema_v25_ordinary_history_parent_reference";
    public const string MigrationChecksum = "67593a68f47a292736305488562badfe2656c3eef45d8c88c2298db9a4427a5a";
    internal const string IndexName = "idx_ordinary_recording_history_parent";
    private const string Definition = "CREATE INDEX idx_ordinary_recording_history_parent ON ordinary_recording_history(parent_recording_id COLLATE BINARY);";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = new[]
    {
        new SqliteIndexDefinition(IndexName, "ordinary_recording_history", false,
            new[] { "parent_recording_id" }, DefinitionPattern: new Regex(
                "^" + Regex.Escape(Definition.TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    };
    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(Definition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (checksum != MigrationChecksum) throw new InvalidOperationException($"The fixed schema v25 definition checksum is {checksum}.");
        return new(25, MigrationName, canonical, checksum);
    }
}
