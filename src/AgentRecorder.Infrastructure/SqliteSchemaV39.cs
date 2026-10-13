using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV39
{
    public const string MigrationName = "schema_v39_retention_required_once_setup_output";
    public const string MigrationChecksum = "0a6ebe640627b70076d32b50374783fbe3b8a0e3f9168ffe1f58618a39b81612";
    internal static readonly (string Index, string Table, string[] Columns)[] Paths = {
        ("idx_required_once_setup_pending_output_path", "required_once_setup_intents", new[] { "output_directory", "frozen_file_name" }),
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
        if (checksum != MigrationChecksum) throw new InvalidOperationException("The fixed schema v39 checksum differs.");
        return new(39, MigrationName, definition, checksum);
    }
}
