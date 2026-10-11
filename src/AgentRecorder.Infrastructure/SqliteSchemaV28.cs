using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV28
{
    public const string MigrationName = "schema_v28_retention_shared_output_path";
    public const string MigrationChecksum = "4fcbe4b92a92634c8c418f512a005079cde42ea57678136f50ba517f93a2d24a";
    internal static readonly (string Index, string Table)[] Paths = {
        ("idx_ordinary_recording_history_shared_output_path", "ordinary_recording_history"),
        ("idx_recording_run_output_evidence_shared_output_path", "recording_run_output_evidence"),
        ("idx_required_once_execution_authorizations_shared_output_path", "required_once_execution_authorizations"),
        ("idx_future_window_authorizations_shared_output_path", "future_window_authorizations"),
    };
    private static string Definition(string index, string table) =>
        $"CREATE INDEX {index} ON {table}(output_path COLLATE {SqliteOutputPathCollation.Name});";
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Paths.Select(p =>
        new SqliteIndexDefinition(p.Index, p.Table, false, new[] { "output_path" }, DefinitionPattern: new Regex(
            "^" + Regex.Escape(Definition(p.Index,p.Table).TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToArray();
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    private static SqliteMigrationDefinition CreateMigration()
    {
        var definition = SqliteSchemaV1.CanonicalizeDefinition(string.Join("\n", Paths.Select(p => Definition(p.Index,p.Table))));
        var checksum = SqliteSchemaV1.ComputeChecksum(definition);
        if (checksum != MigrationChecksum) throw new InvalidOperationException("The fixed schema v28 checksum differs.");
        return new(28, MigrationName, definition, checksum);
    }
}
