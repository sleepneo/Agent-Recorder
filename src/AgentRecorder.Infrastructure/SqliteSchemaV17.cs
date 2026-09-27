using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V17 stores the non-Lease, locally approved setup path for a one-time plan.
/// The immutable specification is intentionally separate from Lease-backed
/// authorized_capture_scopes and is not a capture proof.
/// </summary>
internal static class SqliteSchemaV17
{
    public const string MigrationName = "schema_v17_required_once_plan_setup";
    public const string MigrationChecksum = "eee1f5d5505d6b954e6716cccdc0b2000490263b6363233d5e640e2d78981949";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(17, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("required_once_setup_intents", new[]
        {
            new SqliteColumnDefinition("setup_intent_id", "TEXT", true, 1),
            new SqliteColumnDefinition("idempotency_key", "TEXT", true, 0),
            new SqliteColumnDefinition("request_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("frozen_file_name", "TEXT", true, 0),
            new SqliteColumnDefinition("expires_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("stable_display_fingerprint", "TEXT", false, 0),
            new SqliteColumnDefinition("display_bounds_x", "INTEGER", false, 0),
            new SqliteColumnDefinition("display_bounds_y", "INTEGER", false, 0),
            new SqliteColumnDefinition("display_bounds_width", "INTEGER", false, 0),
            new SqliteColumnDefinition("display_bounds_height", "INTEGER", false, 0),
            new SqliteColumnDefinition("region_x", "INTEGER", false, 0),
            new SqliteColumnDefinition("region_y", "INTEGER", false, 0),
            new SqliteColumnDefinition("region_width", "INTEGER", false, 0),
            new SqliteColumnDefinition("region_height", "INTEGER", false, 0),
            new SqliteColumnDefinition("dpi_x", "INTEGER", false, 0),
            new SqliteColumnDefinition("dpi_y", "INTEGER", false, 0),
            new SqliteColumnDefinition("physical_width", "INTEGER", false, 0),
            new SqliteColumnDefinition("physical_height", "INTEGER", false, 0),
            new SqliteColumnDefinition("orientation_code", "TEXT", false, 0),
            new SqliteColumnDefinition("topology_digest", "TEXT", false, 0),
            new SqliteColumnDefinition("selected_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", false, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("required_once_authorized_specs", new[]
        {
            new SqliteColumnDefinition("plan_id", "TEXT", true, 1),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("setup_intent_id", "TEXT", true, 0),
            new SqliteColumnDefinition("creation_approval_id", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("frozen_file_name", "TEXT", true, 0),
            new SqliteColumnDefinition("stable_display_fingerprint", "TEXT", true, 0),
            new SqliteColumnDefinition("display_bounds_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("orientation_code", "TEXT", true, 0),
            new SqliteColumnDefinition("topology_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("backend_code", "TEXT", true, 0),
            new SqliteColumnDefinition("audio_mode_code", "TEXT", true, 0),
            new SqliteColumnDefinition("countdown_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("wake_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("desktop_requirement_code", "TEXT", true, 0),
            new SqliteColumnDefinition("output_conflict_policy_code", "TEXT", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_required_once_setup_status_expiry", "required_once_setup_intents", false,
            new[] { "status_code", "expires_at_utc" }),
        new SqliteIndexDefinition("idx_required_once_setup_plan", "required_once_setup_intents", false,
            new[] { "plan_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("required_once_setup_intents", new[] { "current_user_sid", "session_binding", "idempotency_key" }),
        new SqliteUniqueConstraintDefinition("required_once_authorized_specs", new[] { "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("required_once_authorized_specs", new[] { "setup_intent_id" }),
        new SqliteUniqueConstraintDefinition("required_once_authorized_specs", new[] { "creation_approval_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("required_once_setup_intents", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
        }),
        new SqliteForeignKeyDefinition("required_once_authorized_specs", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
            new SqliteForeignKeyInfo("required_once_setup_intents", "NO ACTION", "RESTRICT", new[] { "setup_intent_id" }, new[] { "setup_intent_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("setup_intent_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(setup_intent_id"),
        Check("request_digest\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(request_digest\\)\\s*=\\s*64"),
        Check("status_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(status_code\\s+IN\\s*\\('region_selection_pending',\\s*'creation_approval_pending',\\s*'scheduled',\\s*'rejected',\\s*'expired'\\)\\)"),
        Check("duration_ms\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(duration_ms\\s*>\\s*0\\s+AND\\s+duration_ms\\s*<=\\s*600000\\)"),
        Check("expires_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(expires_at_utc\\s*>\\s*0"),
        Check("CHECK\\s*\\(\\(status_code\\s*=\\s*'region_selection_pending'.*status_code\\s*=\\s*'creation_approval_pending'.*status_code\\s*=\\s*'scheduled'.*status_code\\s+IN\\s*\\('rejected',\\s*'expired'\\).*\\)"),
        Check("approved_current_user_sid\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(approved_current_user_sid", "required_once_authorized_specs"),
        Check("backend_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(backend_code\\s*=\\s*'ffmpeg-region'\\)", "required_once_authorized_specs"),
        Check("audio_mode_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(audio_mode_code\\s*=\\s*'none'\\)", "required_once_authorized_specs"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTriggerDefinition("trg_required_once_setup_state_transition", "required_once_setup_intents", new[]
        {
            new Regex("BEFORE\\s+UPDATE\\s+ON\\s+required_once_setup_intents", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_setup_immutable_request", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_setup_invalid_transition", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_setup_selection_immutable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("required_once_setup_version_invalid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        }),
        ImmutableTrigger("trg_required_once_authorized_specs_immutable_update", "UPDATE", "required_once_authorized_specs"),
        ImmutableTrigger("trg_required_once_authorized_specs_immutable_delete", "DELETE", "required_once_authorized_specs"),
        new SqliteTriggerDefinition("trg_required_once_spec_approval_chain", "required_once_authorized_specs", new[]
        {
            new Regex("BEFORE\\s+INSERT\\s+ON\\s+required_once_authorized_specs", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("creation_approval_pending", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("NEW\\.approved_current_user_sid\\s*=\\s*i\\.current_user_sid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("NEW\\.approved_session_binding\\s*=\\s*i\\.session_binding", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        }),
    });

    private const string SchemaDefinition = """
        CREATE TABLE required_once_setup_intents (
            setup_intent_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(setup_intent_id)) > 0),
            idempotency_key TEXT NOT NULL CHECK (length(trim(idempotency_key)) > 0 AND length(idempotency_key) <= 128),
            request_digest TEXT NOT NULL CHECK (length(request_digest) = 64 AND request_digest = lower(request_digest) AND request_digest NOT GLOB '*[^0-9a-f]*'),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),
            status_code TEXT NOT NULL CHECK (status_code IN ('region_selection_pending', 'creation_approval_pending', 'scheduled', 'rejected', 'expired')),
            reason_code TEXT NULL CHECK (reason_code IS NULL OR (length(trim(reason_code)) > 0 AND reason_code = trim(reason_code))),
            scheduled_start_utc INTEGER NOT NULL CHECK (scheduled_start_utc > 0),
            latest_start_utc INTEGER NOT NULL CHECK (latest_start_utc > scheduled_start_utc),
            planned_end_utc INTEGER NOT NULL CHECK (planned_end_utc >= latest_start_utc),
            duration_ms INTEGER NOT NULL CHECK (duration_ms > 0 AND duration_ms <= 600000),
            output_directory TEXT NOT NULL CHECK (length(trim(output_directory)) > 0 AND output_directory = trim(output_directory)),
            frozen_file_name TEXT NOT NULL CHECK (length(trim(frozen_file_name)) > 0 AND frozen_file_name = trim(frozen_file_name) AND instr(frozen_file_name, char(47)) = 0 AND instr(frozen_file_name, char(92)) = 0),
            expires_at_utc INTEGER NOT NULL CHECK (expires_at_utc > 0),
            stable_display_fingerprint TEXT NULL CHECK (stable_display_fingerprint IS NULL OR length(trim(stable_display_fingerprint)) > 0),
            display_bounds_x INTEGER NULL, display_bounds_y INTEGER NULL,
            display_bounds_width INTEGER NULL CHECK (display_bounds_width IS NULL OR display_bounds_width > 0),
            display_bounds_height INTEGER NULL CHECK (display_bounds_height IS NULL OR display_bounds_height > 0),
            region_x INTEGER NULL CHECK (region_x IS NULL OR region_x >= 0),
            region_y INTEGER NULL CHECK (region_y IS NULL OR region_y >= 0),
            region_width INTEGER NULL CHECK (region_width IS NULL OR region_width > 0),
            region_height INTEGER NULL CHECK (region_height IS NULL OR region_height > 0),
            dpi_x INTEGER NULL CHECK (dpi_x IS NULL OR dpi_x > 0), dpi_y INTEGER NULL CHECK (dpi_y IS NULL OR dpi_y > 0),
            physical_width INTEGER NULL CHECK (physical_width IS NULL OR physical_width > 0),
            physical_height INTEGER NULL CHECK (physical_height IS NULL OR physical_height > 0),
            orientation_code TEXT NULL CHECK (orientation_code IS NULL OR orientation_code IN ('landscape', 'portrait', 'landscape_flipped', 'portrait_flipped')),
            topology_digest TEXT NULL CHECK (topology_digest IS NULL OR (length(topology_digest) = 64 AND topology_digest = lower(topology_digest) AND topology_digest NOT GLOB '*[^0-9a-f]*')),
            selected_at_utc INTEGER NULL CHECK (selected_at_utc IS NULL OR selected_at_utc > 0),
            plan_id TEXT NULL CHECK (plan_id IS NULL OR length(trim(plan_id)) > 0),
            occurrence_id TEXT NULL CHECK (occurrence_id IS NULL OR length(trim(occurrence_id)) > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc > 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (current_user_sid, session_binding, idempotency_key),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id) ON DELETE RESTRICT,
            CHECK (planned_end_utc - scheduled_start_utc >= duration_ms * 10000),
            CHECK ((status_code = 'region_selection_pending' AND stable_display_fingerprint IS NULL AND display_bounds_x IS NULL AND display_bounds_y IS NULL AND display_bounds_width IS NULL AND display_bounds_height IS NULL AND region_x IS NULL AND region_y IS NULL AND region_width IS NULL AND region_height IS NULL AND dpi_x IS NULL AND dpi_y IS NULL AND physical_width IS NULL AND physical_height IS NULL AND orientation_code IS NULL AND topology_digest IS NULL AND selected_at_utc IS NULL AND plan_id IS NULL AND occurrence_id IS NULL AND reason_code IS NULL) OR
                   (status_code = 'creation_approval_pending' AND stable_display_fingerprint IS NOT NULL AND display_bounds_x IS NOT NULL AND display_bounds_y IS NOT NULL AND display_bounds_width IS NOT NULL AND display_bounds_height IS NOT NULL AND region_x IS NOT NULL AND region_y IS NOT NULL AND region_width IS NOT NULL AND region_height IS NOT NULL AND dpi_x IS NOT NULL AND dpi_y IS NOT NULL AND physical_width IS NOT NULL AND physical_height IS NOT NULL AND orientation_code IS NOT NULL AND topology_digest IS NOT NULL AND selected_at_utc IS NOT NULL AND plan_id IS NULL AND occurrence_id IS NULL AND reason_code IS NULL) OR
                   (status_code = 'scheduled' AND stable_display_fingerprint IS NOT NULL AND display_bounds_x IS NOT NULL AND display_bounds_y IS NOT NULL AND display_bounds_width IS NOT NULL AND display_bounds_height IS NOT NULL AND region_x IS NOT NULL AND region_y IS NOT NULL AND region_width IS NOT NULL AND region_height IS NOT NULL AND dpi_x IS NOT NULL AND dpi_y IS NOT NULL AND physical_width IS NOT NULL AND physical_height IS NOT NULL AND orientation_code IS NOT NULL AND topology_digest IS NOT NULL AND selected_at_utc IS NOT NULL AND plan_id IS NOT NULL AND occurrence_id IS NOT NULL AND reason_code IS NULL) OR
                   (status_code IN ('rejected', 'expired') AND reason_code IS NOT NULL AND plan_id IS NULL AND occurrence_id IS NULL)),
            CHECK ((display_bounds_width IS NULL AND display_bounds_height IS NULL AND region_x IS NULL AND region_y IS NULL AND region_width IS NULL AND region_height IS NULL) OR
                   (region_x + region_width <= display_bounds_width AND region_y + region_height <= display_bounds_height AND physical_width = display_bounds_width AND physical_height = display_bounds_height))
        );

        CREATE TABLE required_once_authorized_specs (
            plan_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(plan_id)) > 0),
            occurrence_id TEXT NOT NULL UNIQUE CHECK (length(trim(occurrence_id)) > 0),
            setup_intent_id TEXT NOT NULL UNIQUE CHECK (length(trim(setup_intent_id)) > 0),
            creation_approval_id TEXT NOT NULL UNIQUE CHECK (length(trim(creation_approval_id)) > 0),
            approved_current_user_sid TEXT NOT NULL CHECK (length(trim(approved_current_user_sid)) > 0),
            approved_session_binding TEXT NOT NULL CHECK (length(trim(approved_session_binding)) > 0),
            approved_at_utc INTEGER NOT NULL CHECK (approved_at_utc > 0),
            scheduled_start_utc INTEGER NOT NULL CHECK (scheduled_start_utc > 0),
            latest_start_utc INTEGER NOT NULL CHECK (latest_start_utc > scheduled_start_utc),
            planned_end_utc INTEGER NOT NULL CHECK (planned_end_utc >= latest_start_utc),
            duration_ms INTEGER NOT NULL CHECK (duration_ms > 0 AND duration_ms <= 600000),
            output_directory TEXT NOT NULL CHECK (length(trim(output_directory)) > 0 AND output_directory = trim(output_directory)),
            frozen_file_name TEXT NOT NULL CHECK (length(trim(frozen_file_name)) > 0 AND instr(frozen_file_name, char(47)) = 0 AND instr(frozen_file_name, char(92)) = 0),
            stable_display_fingerprint TEXT NOT NULL CHECK (length(trim(stable_display_fingerprint)) > 0),
            display_bounds_x INTEGER NOT NULL, display_bounds_y INTEGER NOT NULL,
            display_bounds_width INTEGER NOT NULL CHECK (display_bounds_width > 0), display_bounds_height INTEGER NOT NULL CHECK (display_bounds_height > 0),
            region_x INTEGER NOT NULL CHECK (region_x >= 0), region_y INTEGER NOT NULL CHECK (region_y >= 0),
            region_width INTEGER NOT NULL CHECK (region_width > 0), region_height INTEGER NOT NULL CHECK (region_height > 0),
            dpi_x INTEGER NOT NULL CHECK (dpi_x > 0), dpi_y INTEGER NOT NULL CHECK (dpi_y > 0),
            physical_width INTEGER NOT NULL CHECK (physical_width = display_bounds_width),
            physical_height INTEGER NOT NULL CHECK (physical_height = display_bounds_height),
            orientation_code TEXT NOT NULL CHECK (orientation_code IN ('landscape', 'portrait', 'landscape_flipped', 'portrait_flipped')),
            topology_digest TEXT NOT NULL CHECK (length(topology_digest) = 64 AND topology_digest = lower(topology_digest) AND topology_digest NOT GLOB '*[^0-9a-f]*'),
            backend_code TEXT NOT NULL CHECK (backend_code = 'ffmpeg-region'),
            audio_mode_code TEXT NOT NULL CHECK (audio_mode_code = 'none'),
            countdown_seconds INTEGER NOT NULL CHECK (countdown_seconds = 0),
            wake_policy_code TEXT NOT NULL CHECK (wake_policy_code = 'natural_wake_only'),
            desktop_requirement_code TEXT NOT NULL CHECK (desktop_requirement_code = 'interactive_desktop_required'),
            output_conflict_policy_code TEXT NOT NULL CHECK (output_conflict_policy_code = 'fail_if_exists'),
            UNIQUE (plan_id, occurrence_id),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id) ON DELETE RESTRICT,
            FOREIGN KEY (setup_intent_id) REFERENCES required_once_setup_intents(setup_intent_id) ON DELETE RESTRICT,
            CHECK (region_x + region_width <= display_bounds_width AND region_y + region_height <= display_bounds_height),
            CHECK (planned_end_utc - scheduled_start_utc >= duration_ms * 10000)
        );

        CREATE INDEX idx_required_once_setup_status_expiry ON required_once_setup_intents(status_code, expires_at_utc);
        CREATE INDEX idx_required_once_setup_plan ON required_once_setup_intents(plan_id);

        CREATE TRIGGER trg_required_once_setup_state_transition
            BEFORE UPDATE ON required_once_setup_intents
            BEGIN
                SELECT RAISE(ABORT, 'required_once_setup_immutable_request')
                WHERE NEW.setup_intent_id != OLD.setup_intent_id OR NEW.idempotency_key != OLD.idempotency_key OR NEW.request_digest != OLD.request_digest OR NEW.current_user_sid != OLD.current_user_sid OR NEW.session_binding != OLD.session_binding OR NEW.scheduled_start_utc != OLD.scheduled_start_utc OR NEW.latest_start_utc != OLD.latest_start_utc OR NEW.planned_end_utc != OLD.planned_end_utc OR NEW.duration_ms != OLD.duration_ms OR NEW.output_directory != OLD.output_directory OR NEW.frozen_file_name != OLD.frozen_file_name OR NEW.expires_at_utc != OLD.expires_at_utc OR NEW.created_at_utc != OLD.created_at_utc;
                SELECT RAISE(ABORT, 'required_once_setup_invalid_transition')
                WHERE NOT ((OLD.status_code = 'region_selection_pending' AND NEW.status_code IN ('creation_approval_pending', 'rejected', 'expired')) OR
                           (OLD.status_code = 'creation_approval_pending' AND NEW.status_code IN ('scheduled', 'rejected', 'expired')));
                SELECT RAISE(ABORT, 'required_once_setup_selection_immutable')
                WHERE OLD.status_code != 'region_selection_pending' AND (NEW.stable_display_fingerprint IS NOT OLD.stable_display_fingerprint OR NEW.display_bounds_x IS NOT OLD.display_bounds_x OR NEW.display_bounds_y IS NOT OLD.display_bounds_y OR NEW.display_bounds_width IS NOT OLD.display_bounds_width OR NEW.display_bounds_height IS NOT OLD.display_bounds_height OR NEW.region_x IS NOT OLD.region_x OR NEW.region_y IS NOT OLD.region_y OR NEW.region_width IS NOT OLD.region_width OR NEW.region_height IS NOT OLD.region_height OR NEW.dpi_x IS NOT OLD.dpi_x OR NEW.dpi_y IS NOT OLD.dpi_y OR NEW.physical_width IS NOT OLD.physical_width OR NEW.physical_height IS NOT OLD.physical_height OR NEW.orientation_code IS NOT OLD.orientation_code OR NEW.topology_digest IS NOT OLD.topology_digest OR NEW.selected_at_utc IS NOT OLD.selected_at_utc);
                SELECT RAISE(ABORT, 'required_once_setup_version_invalid') WHERE NEW.version != OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
            END;

        CREATE TRIGGER trg_required_once_spec_approval_chain
            BEFORE INSERT ON required_once_authorized_specs
            BEGIN
                SELECT RAISE(ABORT, 'required_once_spec_approval_chain_invalid')
                WHERE NOT EXISTS (
                    SELECT 1 FROM required_once_setup_intents AS i
                    JOIN plans AS p ON p.id = NEW.plan_id AND p.is_one_time = 1 AND p.status_code = 'enabled'
                    JOIN plan_occurrences AS o ON o.id = NEW.occurrence_id AND o.plan_id = p.id AND o.status_code = 'scheduled' AND o.run_id IS NULL
                    WHERE i.setup_intent_id = NEW.setup_intent_id AND i.status_code = 'creation_approval_pending'
                      AND i.plan_id IS NULL AND i.occurrence_id IS NULL
                      AND NEW.approved_current_user_sid = i.current_user_sid
                      AND NEW.approved_session_binding = i.session_binding
                      AND NEW.scheduled_start_utc = i.scheduled_start_utc AND NEW.latest_start_utc = i.latest_start_utc
                      AND NEW.planned_end_utc = i.planned_end_utc AND NEW.duration_ms = i.duration_ms
                      AND NEW.output_directory = i.output_directory AND NEW.frozen_file_name = i.frozen_file_name
                      AND NEW.stable_display_fingerprint = i.stable_display_fingerprint
                      AND NEW.display_bounds_x = i.display_bounds_x AND NEW.display_bounds_y = i.display_bounds_y
                      AND NEW.display_bounds_width = i.display_bounds_width AND NEW.display_bounds_height = i.display_bounds_height
                      AND NEW.region_x = i.region_x AND NEW.region_y = i.region_y
                      AND NEW.region_width = i.region_width AND NEW.region_height = i.region_height
                      AND NEW.dpi_x = i.dpi_x AND NEW.dpi_y = i.dpi_y
                      AND NEW.physical_width = i.physical_width AND NEW.physical_height = i.physical_height
                      AND NEW.orientation_code = i.orientation_code AND NEW.topology_digest = i.topology_digest
                );
            END;

        CREATE TRIGGER trg_required_once_authorized_specs_immutable_update
            BEFORE UPDATE ON required_once_authorized_specs
            BEGIN SELECT RAISE(ABORT, 'required_once_authorized_spec_is_immutable'); END;
        CREATE TRIGGER trg_required_once_authorized_specs_immutable_delete
            BEFORE DELETE ON required_once_authorized_specs
            BEGIN SELECT RAISE(ABORT, 'required_once_authorized_spec_is_immutable'); END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern, string table = "required_once_setup_intents") =>
        new(table, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation, string table) =>
        new(name, table, Array.Empty<Regex>(), new Regex(
            $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+{Regex.Escape(table)}\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'required_once_authorized_spec_is_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v17 definition checksum changed: {checksum}.");
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
