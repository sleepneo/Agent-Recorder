using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>V20 widens only the future-window one-shot duration constraint.</summary>
internal static class SqliteSchemaV20
{
    public const string MigrationName = "schema_v20_future_window_30_minute_duration";
    public const string MigrationChecksum = "f614b995ad6e3aa5504fd1da5b80076e60fe1b70cc87208488ad7dc7a06cc43e";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(),
    });

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(
        SqliteSchemaV19.CheckConstraints
            .Where(check => !check.Pattern.ToString().Contains("maximum_duration_seconds", StringComparison.Ordinal))
            .Append(new SqliteCheckConstraintDefinition(
                "future_window_authorizations",
                new Regex(
                    "maximum_duration_seconds\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(maximum_duration_seconds\\s+BETWEEN\\s+1\\s+AND\\s+1800",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)))
            .ToArray());

    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(BuildDefinition());
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v20 definition checksum is {checksum}.");
        return new SqliteMigrationDefinition(20, MigrationName, canonicalDefinition, MigrationChecksum);
    }

    private static string BuildDefinition()
    {
        var v19 = SqliteSchemaV19.Migrations.Single().Definition;
        const string tablePrefix = "CREATE TABLE future_window_authorizations (";
        var tableStart = v19.IndexOf(tablePrefix, StringComparison.OrdinalIgnoreCase);
        if (tableStart < 0)
            throw new InvalidOperationException("The frozen v19 authorization table definition could not be located.");
        var indexStart = v19.IndexOf("CREATE INDEX idx_future_window_authorizations_status", tableStart, StringComparison.OrdinalIgnoreCase);
        if (indexStart < 0)
            throw new InvalidOperationException("The frozen v19 status index could not be located.");
        var tableEnd = v19.LastIndexOf(';', indexStart);
        if (tableEnd < tableStart)
            throw new InvalidOperationException("The frozen v19 authorization table statement could not be located.");

        var tableDefinition = v19[tableStart..(tableEnd + 1)];
        tableDefinition = tableDefinition.Replace(
            tablePrefix,
            "CREATE TABLE future_window_authorizations_v20 (",
            StringComparison.OrdinalIgnoreCase);
        const string oldMaximumCheck = "maximum_duration_seconds BETWEEN 1 AND 600";
        if (tableDefinition.Split(oldMaximumCheck, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("The frozen v19 authorization duration check changed unexpectedly.");
        tableDefinition = tableDefinition.Replace(oldMaximumCheck,
            "maximum_duration_seconds BETWEEN 1 AND 1800", StringComparison.Ordinal);

        var indexEnd = v19.IndexOf(';', indexStart);
        var triggerStart = v19.IndexOf("CREATE TRIGGER trg_future_window_authorization_transition", indexEnd, StringComparison.OrdinalIgnoreCase);
        if (indexStart < 0 || indexEnd < 0 || triggerStart < 0)
            throw new InvalidOperationException("The frozen v19 authorization indexes and triggers could not be located.");
        var indexDefinition = v19[indexStart..(indexEnd + 1)];
        var triggerDefinitions = v19[triggerStart..].Trim();

        var columns = string.Join(", ", SqliteSchemaV19.Tables.Single().Columns.Select(column => column.Name));
        return $"""
            CREATE TEMP TABLE task301_v20_legacy_guard (valid INTEGER NOT NULL CHECK (valid = 1));
            INSERT INTO task301_v20_legacy_guard(valid)
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM future_window_authorizations
                WHERE maximum_duration_seconds NOT BETWEEN 1 AND 600
                   OR validity_seconds NOT BETWEEN 1 AND 3600
                   OR maximum_duration_seconds > validity_seconds
            ) THEN 0 ELSE 1 END;
            DROP TABLE task301_v20_legacy_guard;
            {tableDefinition}
            INSERT INTO future_window_authorizations_v20 ({columns})
            SELECT {columns} FROM future_window_authorizations;
            DROP TRIGGER trg_future_window_authorization_transition;
            DROP TRIGGER trg_future_window_authorization_no_delete;
            DROP INDEX idx_future_window_authorizations_status;
            DROP TABLE future_window_authorizations;
            ALTER TABLE future_window_authorizations_v20 RENAME TO future_window_authorizations;
            {indexDefinition}
            {triggerDefinitions}
            """;
    }
}
