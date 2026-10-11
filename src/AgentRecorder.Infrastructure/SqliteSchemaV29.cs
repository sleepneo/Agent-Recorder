using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV29
{
    public const string MigrationName = "schema_v29_retention_planned_output_path";
    public const string MigrationChecksum = "838d28e05e07cf315eb7f8c0708e7b59dc6d466dacd63d36878f4941d8f29300";
    internal static readonly (string Index, string Table, string[] Columns)[] Paths = {
        ("idx_authorized_capture_scopes_planned_output_path", "authorized_capture_scopes", new[] { "output_directory", "frozen_file_name" }),
        ("idx_recurring_occurrence_execution_specs_planned_output_path", "recurring_occurrence_execution_specs", new[] { "frozen_output_file_path" }),
        ("idx_required_once_authorized_specs_planned_output_path", "required_once_authorized_specs", new[] { "output_directory", "frozen_file_name" }),
        ("idx_future_window_authorizations_planned_output_path", "future_window_authorizations", new[] { "output_directory", "output_file_name" }),
    };
    private static string Definition(string index, string table, IEnumerable<string> columns) =>
        $"CREATE INDEX {index} ON {table}(" + string.Join(", ", columns.Select(c => c + " COLLATE " + SqliteOutputPathCollation.Name)) + ");";
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Paths.Select(p =>
        new SqliteIndexDefinition(p.Index, p.Table, false, p.Columns, DefinitionPattern: new Regex(
            "^" + Regex.Escape(Definition(p.Index, p.Table, p.Columns).TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToArray();
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    private static SqliteMigrationDefinition CreateMigration()
    {
        var definition = SqliteSchemaV1.CanonicalizeDefinition(string.Join("\n", Paths.Select(p => Definition(p.Index, p.Table, p.Columns))));
        var checksum = SqliteSchemaV1.ComputeChecksum(definition);
        if (checksum != MigrationChecksum) throw new InvalidOperationException("The fixed schema v29 checksum differs.");
        return new(29, MigrationName, definition, checksum);
    }
}
