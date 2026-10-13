using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV38
{
    public const string MigrationName = "schema_v38_retention_pending_output_use";
    public const string MigrationChecksum = "0bdf2b53c23f373ca5a65833617ccb46c8ae62b7e714f10ea16ea28e234a903c";
    internal static readonly (string Index, string Table, string[] Columns)[] Paths = {
        ("idx_setup_intents_pending_output_path", "setup_intents", new[] { "output_directory", "frozen_file_name" }),
        ("idx_setup_intents_pending_output_template", "setup_intents", new[] { "recurring_output_directory" }),
        ("idx_recurring_profiles_pending_output_template", "recurring_fixed_region_profile_versions", new[] { "output_directory" }),
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
        if (checksum != MigrationChecksum) throw new InvalidOperationException("The fixed schema v38 checksum differs.");
        return new(38, MigrationName, definition, checksum);
    }
}
