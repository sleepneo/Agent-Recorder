using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V16 records immutable actual media output-path evidence for settled plan
/// runs. Historical settled runs are explicitly marked as having no path
/// evidence; no path is inferred during migration.
/// </summary>
internal static class SqliteSchemaV16
{
    public const string MigrationName = "schema_v16_recording_run_output_path_evidence";

    // Fixed after canonicalizing SchemaDefinition. This checksum is a durable
    // protocol value and must never be recomputed from a changed definition.
    public const string MigrationChecksum = "0ef3506800e87e322d0273bf1d43a8b66a591046b54b0b133a4b2932b9e913f7";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(16, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recording_run_output_evidence", new[]
        {
            new SqliteColumnDefinition("run_id", "TEXT", true, 1),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("evidence_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("output_path", "TEXT", false, 0),
            new SqliteColumnDefinition("recorded_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "idx_recording_run_output_evidence_occurrence",
            "recording_run_output_evidence",
            false,
            new[] { "occurrence_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recording_run_output_evidence", new[]
        {
            new SqliteForeignKeyInfo(
                "recording_runs",
                "NO ACTION",
                "RESTRICT",
                new[] { "run_id", "occurrence_id" },
                new[] { "id", "occurrence_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("run_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(run_id"),
        Check("occurrence_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(occurrence_id"),
        Check("evidence_kind_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(evidence_kind_code\\s+IN\\s*\\('verified_output_path',\\s*'legacy_path_unavailable'\\)\\)"),
        Check("output_path\\s+TEXT\\s+NULL\\s+COLLATE\\s+BINARY\\s+CHECK\\s*\\(output_path\\s+IS\\s+NULL\\s+OR\\s*\\(length\\(trim\\(output_path"),
        Check("recorded_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(recorded_at_utc\\s*>=\\s*0\\)"),
        Check("CHECK\\s*\\(\\(evidence_kind_code\\s*=\\s*'verified_output_path'.*evidence_kind_code\\s*=\\s*'legacy_path_unavailable'.*output_path\\s+IS\\s+NULL\\)\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        ImmutableTrigger("trg_recording_run_output_evidence_immutable_update", "UPDATE"),
        ImmutableTrigger("trg_recording_run_output_evidence_immutable_delete", "DELETE"),
        new SqliteTriggerDefinition(
            "trg_recording_run_output_evidence_settled_chain_insert",
            "recording_run_output_evidence",
            new[]
            {
                new Regex(@"BEFORE\s+INSERT\s+ON\s+recording_run_output_evidence", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(@"RAISE\s*\(\s*ABORT\s*,\s*'recording_run_output_evidence_requires_settled_chain'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(@"NEW\.evidence_kind_code\s*!=\s*'verified_output_path'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(@"r\.status_code\s*=\s*'settled'.*o\.status_code\s*=\s*'completed'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(@"lease_uses.*status_code\s*=\s*'settled'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(@"recurring_lease_uses.*status_code\s*=\s*'settled'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            }),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recording_run_output_evidence (
            run_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            occurrence_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            evidence_kind_code TEXT NOT NULL COLLATE BINARY CHECK (evidence_kind_code IN ('verified_output_path', 'legacy_path_unavailable')),
            output_path TEXT NULL COLLATE BINARY CHECK (output_path IS NULL OR (length(trim(output_path)) > 0 AND length(output_path) <= 32767 AND output_path = trim(output_path) AND instr(output_path, char(0)) = 0)),
            recorded_at_utc INTEGER NOT NULL CHECK (recorded_at_utc >= 0),
            PRIMARY KEY (run_id),
            CHECK ((evidence_kind_code = 'verified_output_path' AND output_path IS NOT NULL) OR (evidence_kind_code = 'legacy_path_unavailable' AND output_path IS NULL)),
            FOREIGN KEY (run_id, occurrence_id) REFERENCES recording_runs(id, occurrence_id) ON DELETE RESTRICT
        );

        INSERT INTO recording_run_output_evidence
            (run_id, occurrence_id, evidence_kind_code, output_path, recorded_at_utc)
        SELECT r.id, r.occurrence_id, 'legacy_path_unavailable', NULL, r.updated_at_utc
        FROM recording_runs AS r
        JOIN plan_occurrences AS o ON o.id = r.occurrence_id AND o.run_id = r.id
        WHERE r.status_code = 'settled' AND r.terminal_reason_code IS NULL
          AND o.status_code = 'completed' AND o.terminal_reason_code IS NULL;

        CREATE INDEX idx_recording_run_output_evidence_occurrence
            ON recording_run_output_evidence(occurrence_id);

        CREATE TRIGGER trg_recording_run_output_evidence_settled_chain_insert
            BEFORE INSERT ON recording_run_output_evidence
            BEGIN
                SELECT RAISE(ABORT, 'recording_run_output_evidence_requires_settled_chain')
                WHERE NEW.evidence_kind_code != 'verified_output_path'
                   OR NOT EXISTS (
                    SELECT 1
                    FROM recording_runs AS r
                    JOIN plan_occurrences AS o ON o.id = NEW.occurrence_id AND o.run_id = r.id
                    WHERE r.id = NEW.run_id AND r.occurrence_id = NEW.occurrence_id
                      AND r.status_code = 'settled' AND r.terminal_reason_code IS NULL
                      AND o.status_code = 'completed' AND o.terminal_reason_code IS NULL
                )
                   OR (
                       NOT EXISTS (SELECT 1 FROM lease_uses AS u WHERE u.run_id = NEW.run_id AND u.occurrence_id = NEW.occurrence_id AND u.status_code = 'settled')
                       AND NOT EXISTS (SELECT 1 FROM recurring_lease_uses AS ru WHERE ru.run_id = NEW.run_id AND ru.occurrence_id = NEW.occurrence_id AND ru.status_code = 'settled')
                   );
            END;

        CREATE TRIGGER trg_recording_run_output_evidence_immutable_update
            BEFORE UPDATE ON recording_run_output_evidence
            BEGIN
                SELECT RAISE(ABORT, 'recording_run_output_evidence_is_immutable');
            END;

        CREATE TRIGGER trg_recording_run_output_evidence_immutable_delete
            BEFORE DELETE ON recording_run_output_evidence
            BEGIN
                SELECT RAISE(ABORT, 'recording_run_output_evidence_is_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recording_run_output_evidence", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(
            name,
            "recording_run_output_evidence",
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+recording_run_output_evidence\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recording_run_output_evidence_is_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The fixed schema v16 definition checksum changed: {checksum}.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
