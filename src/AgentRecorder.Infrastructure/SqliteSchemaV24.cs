using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV24
{
    public const string MigrationName = "schema_v24_ordinary_history_status_filter";
    public const string MigrationChecksum = "f3416cd534bef531da6ef4782f171e901243a437b0aa374922c5d61c0176c93e";
    internal const string IndexName = "idx_ordinary_recording_history_status_created";
    private const string Definition = "CREATE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(status_code, created_at_utc DESC, recording_id COLLATE BINARY ASC);";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = new[]
    {
        new SqliteIndexDefinition(IndexName, "ordinary_recording_history", false,
            new[] { "status_code", "created_at_utc", "recording_id" }, DefinitionPattern: new Regex(
                "^" + Regex.Escape(Definition.TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    };
    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(Definition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (checksum != MigrationChecksum) throw new InvalidOperationException($"The fixed schema v24 definition checksum is {checksum}.");
        return new(24, MigrationName, canonical, checksum);
    }
}
