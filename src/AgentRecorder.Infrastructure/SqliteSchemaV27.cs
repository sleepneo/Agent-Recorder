using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV27
{
    public const string MigrationName = "schema_v27_history_source_type_plan_filter";
    public const string MigrationChecksum = "28a44c823d64070e21d0e9b36115f323cf0516e4569cb5a9c2ae6b8545225fcb";
    internal const string TypeIndexName = "idx_ordinary_recording_history_type_created";
    internal const string PlanIndexName = "idx_plan_history_identity_plan_created";
    private const string TypeDefinition = "CREATE INDEX idx_ordinary_recording_history_type_created ON ordinary_recording_history(source_type COLLATE BINARY, created_at_utc DESC, recording_id COLLATE BINARY ASC);";
    private const string PlanDefinition = "CREATE INDEX idx_plan_history_identity_plan_created ON plan_history_locator(current_user_sid, session_binding, plan_id COLLATE BINARY, created_at_utc DESC, occurrence_id COLLATE BINARY ASC);";
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = new[]
    {
        Index(TypeIndexName, "ordinary_recording_history", new[] { "source_type", "created_at_utc", "recording_id" }, TypeDefinition),
        Index(PlanIndexName, "plan_history_locator", new[] { "current_user_sid", "session_binding", "plan_id", "created_at_utc", "occurrence_id" }, PlanDefinition)
    };
    private static SqliteIndexDefinition Index(string name, string table, string[] columns, string definition) =>
        new(name, table, false, columns, DefinitionPattern: new Regex(
            "^" + Regex.Escape(definition.TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(TypeDefinition + "\n" + PlanDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (checksum != MigrationChecksum) throw new InvalidOperationException($"The fixed schema v27 definition checksum is {checksum}.");
        return new(27, MigrationName, canonical, checksum);
    }
}
