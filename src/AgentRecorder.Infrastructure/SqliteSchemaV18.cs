using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V18 stores the one-time, interactive execution-confirmation and lifecycle
/// receipt separately from every Lease-backed authorization table.
/// </summary>
internal static class SqliteSchemaV18
{
    public const string MigrationName = "schema_v18_required_once_execution";
    public const string MigrationChecksum = "b4cca7036bca5f723928c85b283137b34174b1fd60d9ac924295699d4a8fcf2e";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(18, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("required_once_execution_authorizations", new[]
        {
            new SqliteColumnDefinition("execution_id", "TEXT", true, 1),
            new SqliteColumnDefinition("setup_intent_id", "TEXT", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("run_id", "TEXT", false, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("specification_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("creation_approval_id", "TEXT", true, 0),
            new SqliteColumnDefinition("execution_approval_id", "TEXT", false, 0),
            new SqliteColumnDefinition("proof_id", "TEXT", false, 0),
            new SqliteColumnDefinition("proof_nonce", "TEXT", false, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("approved_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("committed_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("output_path", "TEXT", false, 0),
            new SqliteColumnDefinition("output_size_bytes", "INTEGER", false, 0),
            new SqliteColumnDefinition("actual_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_required_once_execution_status", "required_once_execution_authorizations", false,
            new[] { "status_code", "updated_at_utc" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("required_once_execution_authorizations", new[] { "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("required_once_execution_authorizations", new[] { "run_id" }),
        new SqliteUniqueConstraintDefinition("required_once_execution_authorizations", new[] { "execution_approval_id" }),
        new SqliteUniqueConstraintDefinition("required_once_execution_authorizations", new[] { "proof_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("required_once_execution_authorizations", new[]
        {
            new SqliteForeignKeyInfo("required_once_setup_intents", "NO ACTION", "RESTRICT", new[] { "setup_intent_id" }, new[] { "setup_intent_id" }),
            new SqliteForeignKeyInfo("required_once_authorized_specs", "NO ACTION", "RESTRICT", new[] { "plan_id", "occurrence_id" }, new[] { "plan_id", "occurrence_id" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
            new SqliteForeignKeyInfo("recording_runs", "NO ACTION", "RESTRICT", new[] { "run_id", "occurrence_id" }, new[] { "id", "occurrence_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("specification_digest\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(specification_digest\\)\\s*=\\s*64"),
        Check("status_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(status_code\\s+IN"),
        Check("duration_ms\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(duration_ms\\s*>\\s*0"),
        Check("proof_nonce\\s+TEXT\\s+NULL.*CHECK\\s*\\(proof_nonce\\s+IS\\s+NULL"),
        Check("CHECK\\s*\\(\\s*\\(status_code\\s*=\\s*'pending_confirmation'.*status_code\\s*=\\s*'settled'.*\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTriggerDefinition("trg_required_once_execution_insert_chain", "required_once_execution_authorizations", new[]
        {
            new Regex("BEFORE\\s+INSERT\\s+ON\\s+required_once_execution_authorizations", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_execution_insert_chain_invalid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("pending_confirmation", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_authorized_specs", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        }),
        new SqliteTriggerDefinition("trg_required_once_execution_transition", "required_once_execution_authorizations", new[]
        {
            new Regex("BEFORE\\s+UPDATE\\s+ON\\s+required_once_execution_authorizations", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_execution_immutable_binding", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_execution_invalid_transition", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_execution_version_invalid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        }),
        ImmutableTrigger("trg_required_once_execution_no_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE required_once_execution_authorizations (
            execution_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(execution_id)) > 0),
            setup_intent_id TEXT NOT NULL UNIQUE CHECK (length(trim(setup_intent_id)) > 0),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            occurrence_id TEXT NOT NULL UNIQUE CHECK (length(trim(occurrence_id)) > 0),
            run_id TEXT NULL UNIQUE CHECK (run_id IS NULL OR length(trim(run_id)) > 0),
            status_code TEXT NOT NULL CHECK (status_code IN ('pending_confirmation', 'rejected', 'expired', 'blocked', 'start_committed', 'recording', 'finalizing', 'settled', 'started_unknown', 'session_interrupted', 'failed')),
            reason_code TEXT NULL CHECK (reason_code IS NULL OR (length(trim(reason_code)) > 0 AND reason_code = trim(reason_code))),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),
            specification_digest TEXT NOT NULL CHECK (length(specification_digest) = 64 AND specification_digest = lower(specification_digest) AND specification_digest NOT GLOB '*[^0-9a-f]*'),
            creation_approval_id TEXT NOT NULL CHECK (length(trim(creation_approval_id)) > 0),
            execution_approval_id TEXT NULL UNIQUE CHECK (execution_approval_id IS NULL OR length(trim(execution_approval_id)) > 0),
            proof_id TEXT NULL UNIQUE CHECK (proof_id IS NULL OR length(trim(proof_id)) > 0),
            proof_nonce TEXT NULL CHECK (proof_nonce IS NULL OR (length(proof_nonce) = 32 AND proof_nonce = lower(proof_nonce) AND proof_nonce NOT GLOB '*[^0-9a-f]*')),
            scheduled_start_utc INTEGER NOT NULL CHECK (scheduled_start_utc > 0),
            latest_start_utc INTEGER NOT NULL CHECK (latest_start_utc > scheduled_start_utc),
            planned_end_utc INTEGER NOT NULL CHECK (planned_end_utc >= latest_start_utc),
            duration_ms INTEGER NOT NULL CHECK (duration_ms > 0 AND duration_ms <= 600000),
            approved_at_utc INTEGER NULL CHECK (approved_at_utc IS NULL OR approved_at_utc > 0),
            committed_at_utc INTEGER NULL CHECK (committed_at_utc IS NULL OR committed_at_utc > 0),
            output_path TEXT NULL CHECK (output_path IS NULL OR (length(trim(output_path)) > 0 AND output_path = trim(output_path))),
            output_size_bytes INTEGER NULL CHECK (output_size_bytes IS NULL OR output_size_bytes > 0),
            actual_duration_ms INTEGER NULL CHECK (actual_duration_ms IS NULL OR actual_duration_ms >= 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc > 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (plan_id, occurrence_id),
            FOREIGN KEY (setup_intent_id) REFERENCES required_once_setup_intents(setup_intent_id) ON DELETE RESTRICT,
            FOREIGN KEY (plan_id, occurrence_id) REFERENCES required_once_authorized_specs(plan_id, occurrence_id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id) ON DELETE RESTRICT,
            FOREIGN KEY (run_id, occurrence_id) REFERENCES recording_runs(id, occurrence_id) ON DELETE RESTRICT,
            CHECK (planned_end_utc - scheduled_start_utc >= duration_ms * 10000),
            CHECK (
                (status_code = 'pending_confirmation' AND reason_code IS NULL AND run_id IS NULL AND execution_approval_id IS NULL AND proof_id IS NULL AND proof_nonce IS NULL AND approved_at_utc IS NULL AND committed_at_utc IS NULL AND output_path IS NULL AND output_size_bytes IS NULL AND actual_duration_ms IS NULL) OR
                (status_code IN ('rejected', 'expired', 'blocked') AND reason_code IS NOT NULL AND run_id IS NULL AND proof_id IS NULL AND proof_nonce IS NULL AND committed_at_utc IS NULL AND output_path IS NULL AND output_size_bytes IS NULL AND actual_duration_ms IS NULL) OR
                (status_code IN ('start_committed', 'recording', 'finalizing') AND reason_code IS NULL AND run_id IS NOT NULL AND execution_approval_id IS NOT NULL AND proof_id IS NOT NULL AND proof_nonce IS NOT NULL AND approved_at_utc IS NOT NULL AND committed_at_utc IS NOT NULL AND output_path IS NULL AND output_size_bytes IS NULL AND actual_duration_ms IS NULL) OR
                (status_code = 'settled' AND reason_code IS NULL AND run_id IS NOT NULL AND execution_approval_id IS NOT NULL AND proof_id IS NOT NULL AND proof_nonce IS NOT NULL AND approved_at_utc IS NOT NULL AND committed_at_utc IS NOT NULL AND output_path IS NOT NULL AND output_size_bytes IS NOT NULL AND actual_duration_ms IS NOT NULL) OR
                (status_code IN ('started_unknown', 'session_interrupted', 'failed') AND reason_code IS NOT NULL AND run_id IS NOT NULL AND execution_approval_id IS NOT NULL AND proof_id IS NOT NULL AND proof_nonce IS NOT NULL AND approved_at_utc IS NOT NULL AND committed_at_utc IS NOT NULL AND output_path IS NULL AND output_size_bytes IS NULL AND actual_duration_ms IS NULL)
            )
        );

        CREATE INDEX idx_required_once_execution_status ON required_once_execution_authorizations(status_code, updated_at_utc);

        CREATE TRIGGER trg_required_once_execution_insert_chain
            BEFORE INSERT ON required_once_execution_authorizations
            BEGIN
                SELECT RAISE(ABORT, 'required_once_execution_insert_chain_invalid')
                WHERE NOT EXISTS (
                    SELECT 1 FROM required_once_setup_intents AS i
                    JOIN required_once_authorized_specs AS s ON s.setup_intent_id = i.setup_intent_id AND s.plan_id = NEW.plan_id AND s.occurrence_id = NEW.occurrence_id
                    JOIN plans AS p ON p.id = NEW.plan_id AND p.is_one_time = 1 AND p.status_code = 'enabled'
                    JOIN plan_occurrences AS o ON o.id = NEW.occurrence_id AND o.plan_id = p.id
                    WHERE i.setup_intent_id = NEW.setup_intent_id AND i.status_code = 'scheduled'
                      AND o.status_code = 'pending_confirmation' AND o.run_id IS NULL
                      AND NEW.status_code = 'pending_confirmation' AND NEW.reason_code IS NULL
                      AND NEW.current_user_sid = i.current_user_sid AND NEW.session_binding = i.session_binding
                      AND NEW.current_user_sid = s.approved_current_user_sid AND NEW.session_binding = s.approved_session_binding
                      AND NEW.creation_approval_id = s.creation_approval_id
                      AND NEW.scheduled_start_utc = s.scheduled_start_utc AND NEW.latest_start_utc = s.latest_start_utc
                      AND NEW.planned_end_utc = s.planned_end_utc AND NEW.duration_ms = s.duration_ms
                      AND NEW.run_id IS NULL AND NEW.execution_approval_id IS NULL AND NEW.proof_id IS NULL
                      AND NEW.proof_nonce IS NULL AND NEW.approved_at_utc IS NULL AND NEW.committed_at_utc IS NULL
                );
            END;

        CREATE TRIGGER trg_required_once_execution_transition
            BEFORE UPDATE ON required_once_execution_authorizations
            BEGIN
                SELECT RAISE(ABORT, 'required_once_execution_immutable_binding')
                WHERE NEW.execution_id != OLD.execution_id OR NEW.setup_intent_id != OLD.setup_intent_id OR
                      NEW.plan_id != OLD.plan_id OR NEW.occurrence_id != OLD.occurrence_id OR
                      NEW.current_user_sid != OLD.current_user_sid OR NEW.session_binding != OLD.session_binding OR
                      NEW.specification_digest != OLD.specification_digest OR NEW.creation_approval_id != OLD.creation_approval_id OR
                      NEW.scheduled_start_utc != OLD.scheduled_start_utc OR NEW.latest_start_utc != OLD.latest_start_utc OR
                      NEW.planned_end_utc != OLD.planned_end_utc OR NEW.duration_ms != OLD.duration_ms OR
                      NEW.created_at_utc != OLD.created_at_utc;
                SELECT RAISE(ABORT, 'required_once_execution_invalid_transition')
                WHERE NOT ((OLD.status_code = 'pending_confirmation' AND NEW.status_code IN ('rejected', 'expired', 'blocked', 'start_committed')) OR
                           (OLD.status_code = 'start_committed' AND NEW.status_code IN ('recording', 'finalizing', 'started_unknown', 'session_interrupted', 'failed')) OR
                           (OLD.status_code = 'recording' AND NEW.status_code IN ('finalizing', 'started_unknown', 'session_interrupted', 'failed')) OR
                           (OLD.status_code = 'finalizing' AND NEW.status_code IN ('settled', 'session_interrupted', 'failed')));
                SELECT RAISE(ABORT, 'required_once_execution_version_invalid')
                WHERE NEW.version != OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
            END;

        CREATE TRIGGER trg_required_once_execution_no_delete
            BEFORE DELETE ON required_once_execution_authorizations
            BEGIN SELECT RAISE(ABORT, 'required_once_execution_is_immutable'); END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("required_once_execution_authorizations", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(name, "required_once_execution_authorizations", Array.Empty<Regex>(), new Regex(
            $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+required_once_execution_authorizations\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'required_once_execution_is_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v18 definition checksum changed: {checksum}.");
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
