using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V13 persists the immutable result of one recurring environment recheck.
/// The migration is intentionally independent from the preceding recurring
/// migrations: old migration text and checksums are part of the database
/// protocol and must never be rewritten.
/// </summary>
internal static class SqliteSchemaV13
{
    public const string MigrationName = "schema_v13_recurring_occurrence_execution_specs";

    // Replaced with the fixed checksum of SchemaDefinition after the
    // canonical definition is added.  A literal is required so editing this
    // migration cannot silently rewrite its history.
    public const string MigrationChecksum = "ae54bbc7e8b873823cbba54e812f93dd50aaf954e329ef048104b029a8bd319f";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(13, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_occurrence_execution_specs", new[]
        {
            new SqliteColumnDefinition("occurrence_identity", "TEXT", true, 1),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 0),
            new SqliteColumnDefinition("schedule_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("time_zone_rules_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_id", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("profile_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("configuration_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("lease_authorization_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("local_approval_id", "TEXT", true, 0),
            new SqliteColumnDefinition("local_approval_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("evaluated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("stable_display_fingerprint", "TEXT", true, 0),
            new SqliteColumnDefinition("display_bounds_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("virtual_region_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("virtual_region_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("virtual_region_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("virtual_region_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("orientation_code", "TEXT", true, 0),
            new SqliteColumnDefinition("topology_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("backend_code", "TEXT", true, 0),
            new SqliteColumnDefinition("audio_mode_code", "TEXT", true, 0),
            new SqliteColumnDefinition("duration_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("countdown_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("normalized_output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("frozen_output_file_name", "TEXT", true, 0),
            new SqliteColumnDefinition("frozen_output_file_path", "TEXT", true, 0),
            new SqliteColumnDefinition("output_conflict_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("specification_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("specification_digest", "TEXT", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "ux_recurring_lease_local_approvals_exact_spec_parent",
            "recurring_lease_local_approvals",
            true,
            new[] { "approval_id", "lease_id", "plan_id", "configuration_digest", "authorization_digest", "approval_digest", "current_user_sid", "session_binding" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_occurrence_execution_specs", new[] { "occurrence_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_occurrence_execution_specs", new[]
        {
            new SqliteForeignKeyInfo(
                "recurring_occurrence_slots", "NO ACTION", "RESTRICT",
                new[] { "occurrence_identity", "plan_id", "occurrence_id" },
                new[] { "occurrence_identity", "plan_id", "occurrence_id" }),
            new SqliteForeignKeyInfo(
                "recurring_schedule_versions", "NO ACTION", "RESTRICT",
                new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest" },
                new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest" }),
            new SqliteForeignKeyInfo(
                "recurring_plan_profile_bindings", "NO ACTION", "RESTRICT",
                new[] { "plan_id", "profile_id", "profile_version", "profile_digest" },
                new[] { "plan_id", "profile_id", "profile_version", "profile_digest" }),
            new SqliteForeignKeyInfo(
                "recurring_consent_leases", "NO ACTION", "RESTRICT",
                new[] { "lease_id", "plan_id", "configuration_digest", "lease_authorization_digest" },
                new[] { "lease_id", "plan_id", "configuration_digest", "authorization_digest" }),
            new SqliteForeignKeyInfo(
                "recurring_lease_local_approvals", "NO ACTION", "RESTRICT",
                new[] { "local_approval_id", "lease_id", "plan_id", "configuration_digest", "lease_authorization_digest", "local_approval_digest", "approved_current_user_sid", "approved_session_binding" },
                new[] { "approval_id", "lease_id", "plan_id", "configuration_digest", "authorization_digest", "approval_digest", "current_user_sid", "session_binding" }),
        }, RequireCompatibleCollation: true),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("occurrence_identity\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-occurrence/v1:"),
        Check("occurrence_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(occurrence_id"),
        Check("plan_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(plan_id"),
        Check("lease_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(lease_id"),
        Check("schedule_revision\\s+INTEGER\\s+NOT NULL.*schedule_revision\\s*>\\s*0"),
        Check("schedule_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-schedule/v1:"),
        Check("time_zone_rules_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-timezone-rules/v1:"),
        Check("profile_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(profile_id"),
        Check("profile_version\\s+INTEGER\\s+NOT NULL.*profile_version\\s*>\\s*0"),
        Check("profile_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-fixed-region-profile/v1:"),
        Check("configuration_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-plan-configuration/v1:"),
        Check("lease_authorization_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-consent-lease-authorization/v1:"),
        Check("local_approval_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(local_approval_id"),
        Check("local_approval_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-lease-local-approval/v1:"),
        Check("scheduled_start_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(scheduled_start_utc\\s*>=\\s*0\\)"),
        Check("latest_start_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(latest_start_utc\\s*>=\\s*scheduled_start_utc\\)"),
        Check("planned_end_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(planned_end_utc\\s*>\\s*latest_start_utc\\)"),
        Check("evaluated_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(evaluated_at_utc\\s*>=\\s*scheduled_start_utc\\)"),
        Check("stable_display_fingerprint\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(stable_display_fingerprint"),
        Check("display_bounds_width\\s+INTEGER\\s+NOT NULL.*display_bounds_width\\s*>\\s*0"),
        Check("display_bounds_height\\s+INTEGER\\s+NOT NULL.*display_bounds_height\\s*>\\s*0"),
        Check("region_width\\s+INTEGER\\s+NOT NULL.*region_width\\s*>\\s*0"),
        Check("region_height\\s+INTEGER\\s+NOT NULL.*region_height\\s*>\\s*0"),
        Check("virtual_region_width\\s+INTEGER\\s+NOT NULL.*virtual_region_width\\s*>\\s*0"),
        Check("virtual_region_height\\s+INTEGER\\s+NOT NULL.*virtual_region_height\\s*>\\s*0"),
        Check("dpi_x\\s+INTEGER\\s+NOT NULL.*dpi_x\\s*>\\s*0"),
        Check("dpi_y\\s+INTEGER\\s+NOT NULL.*dpi_y\\s*>\\s*0"),
        Check("physical_width\\s+INTEGER\\s+NOT NULL.*physical_width\\s*>\\s*0"),
        Check("physical_height\\s+INTEGER\\s+NOT NULL.*physical_height\\s*>\\s*0"),
        Check("orientation_code\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*orientation_code\\s+IN\\s*\\('landscape'.*'portrait_flipped'\\)"),
        Check("topology_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(topology_digest\\)\\s*=\\s*64"),
        Check("backend_code\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*backend_code\\s*=\\s*'ffmpeg-region'"),
        Check("audio_mode_code\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*audio_mode_code\\s*=\\s*'none'"),
        Check("duration_ticks\\s+INTEGER\\s+NOT NULL.*duration_ticks\\s*>\\s*0"),
        Check("countdown_seconds\\s+INTEGER\\s+NOT NULL.*countdown_seconds\\s+BETWEEN\\s+0\\s+AND\\s+10"),
        Check("normalized_output_directory\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(normalized_output_directory"),
        Check("frozen_output_file_name\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*instr\\(frozen_output_file_name,\\s*char\\(47\\)\\)"),
        Check("frozen_output_file_path\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(frozen_output_file_path"),
        Check("output_conflict_policy_code\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*output_conflict_policy_code\\s*=\\s*'fail_if_exists'"),
        Check("approved_current_user_sid\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(approved_current_user_sid"),
        Check("approved_session_binding\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(approved_session_binding"),
        Check("specification_version\\s+INTEGER\\s+NOT NULL.*specification_version\\s*=\\s*1"),
        Check("specification_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-occurrence-execution-spec/v1:"),
        Check("region_x\\s*<=\\s*display_bounds_width\\s*-\\s*region_width"),
        Check("region_y\\s*<=\\s*display_bounds_height\\s*-\\s*region_height"),
        Check("virtual_region_x\\s*=\\s*display_bounds_x\\s*\\+\\s*region_x"),
        Check("virtual_region_y\\s*=\\s*display_bounds_y\\s*\\+\\s*region_y"),
        Check("frozen_output_file_path\\s*=\\s*normalized_output_directory\\s*\\|\\|\\s*frozen_output_file_name"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        ImmutableTrigger("trg_recurring_occurrence_execution_specs_immutable_update", "UPDATE"),
        ImmutableTrigger("trg_recurring_occurrence_execution_specs_immutable_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE UNIQUE INDEX ux_recurring_lease_local_approvals_exact_spec_parent
            ON recurring_lease_local_approvals(approval_id, lease_id, plan_id, configuration_digest, authorization_digest, approval_digest, current_user_sid, session_binding);

        CREATE TABLE recurring_occurrence_execution_specs (
            occurrence_identity TEXT NOT NULL COLLATE BINARY CHECK (length(occurrence_identity) = length('recurring-occurrence/v1:') + 64 AND substr(occurrence_identity, 1, length('recurring-occurrence/v1:')) = 'recurring-occurrence/v1:' AND substr(occurrence_identity, length('recurring-occurrence/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(occurrence_identity, length('recurring-occurrence/v1:') + 1) = lower(substr(occurrence_identity, length('recurring-occurrence/v1:') + 1))),
            occurrence_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND occurrence_id = trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND plan_id = trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND lease_id = trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            schedule_digest TEXT NOT NULL COLLATE BINARY CHECK (length(schedule_digest) = length('recurring-schedule/v1:') + 64 AND substr(schedule_digest, 1, length('recurring-schedule/v1:')) = 'recurring-schedule/v1:' AND substr(schedule_digest, length('recurring-schedule/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(schedule_digest, length('recurring-schedule/v1:') + 1) = lower(substr(schedule_digest, length('recurring-schedule/v1:') + 1))),
            time_zone_rules_digest TEXT NOT NULL COLLATE BINARY CHECK (length(time_zone_rules_digest) = length('recurring-timezone-rules/v1:') + 64 AND substr(time_zone_rules_digest, 1, length('recurring-timezone-rules/v1:')) = 'recurring-timezone-rules/v1:' AND substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1) = lower(substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1))),
            profile_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND profile_id = trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            profile_version INTEGER NOT NULL CHECK (profile_version > 0),
            profile_digest TEXT NOT NULL COLLATE BINARY CHECK (length(profile_digest) = length('recurring-fixed-region-profile/v1:') + 64 AND substr(profile_digest, 1, length('recurring-fixed-region-profile/v1:')) = 'recurring-fixed-region-profile/v1:' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) = lower(substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1))),
            configuration_digest TEXT NOT NULL COLLATE BINARY CHECK (length(configuration_digest) = length('recurring-plan-configuration/v1:') + 64 AND substr(configuration_digest, 1, length('recurring-plan-configuration/v1:')) = 'recurring-plan-configuration/v1:' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) = lower(substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1))),
            lease_authorization_digest TEXT NOT NULL COLLATE BINARY CHECK (length(lease_authorization_digest) = length('recurring-consent-lease-authorization/v1:') + 64 AND substr(lease_authorization_digest, 1, length('recurring-consent-lease-authorization/v1:')) = 'recurring-consent-lease-authorization/v1:' AND substr(lease_authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(lease_authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) = lower(substr(lease_authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1))),
            local_approval_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(local_approval_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND local_approval_id = trim(local_approval_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            local_approval_digest TEXT NOT NULL COLLATE BINARY CHECK (length(local_approval_digest) = length('recurring-lease-local-approval/v1:') + 64 AND substr(local_approval_digest, 1, length('recurring-lease-local-approval/v1:')) = 'recurring-lease-local-approval/v1:' AND substr(local_approval_digest, length('recurring-lease-local-approval/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(local_approval_digest, length('recurring-lease-local-approval/v1:') + 1) = lower(substr(local_approval_digest, length('recurring-lease-local-approval/v1:') + 1))),
            scheduled_start_utc INTEGER NOT NULL CHECK (scheduled_start_utc >= 0),
            latest_start_utc INTEGER NOT NULL CHECK (latest_start_utc >= scheduled_start_utc),
            planned_end_utc INTEGER NOT NULL CHECK (planned_end_utc > latest_start_utc),
            evaluated_at_utc INTEGER NOT NULL CHECK (evaluated_at_utc >= scheduled_start_utc),
            stable_display_fingerprint TEXT NOT NULL COLLATE BINARY CHECK (length(trim(stable_display_fingerprint, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND stable_display_fingerprint = trim(stable_display_fingerprint, char(9) || char(10) || char(11) || char(12) || char(13) || char(32)) AND length(stable_display_fingerprint) <= 256 AND instr(stable_display_fingerprint, char(47)) = 0 AND instr(stable_display_fingerprint, char(92)) = 0),
            display_bounds_x INTEGER NOT NULL CHECK (display_bounds_x BETWEEN -2147483648 AND 2147483647),
            display_bounds_y INTEGER NOT NULL CHECK (display_bounds_y BETWEEN -2147483648 AND 2147483647),
            display_bounds_width INTEGER NOT NULL CHECK (display_bounds_width > 0 AND display_bounds_width <= 2147483647),
            display_bounds_height INTEGER NOT NULL CHECK (display_bounds_height > 0 AND display_bounds_height <= 2147483647),
            region_x INTEGER NOT NULL CHECK (region_x >= 0 AND region_x <= 2147483647),
            region_y INTEGER NOT NULL CHECK (region_y >= 0 AND region_y <= 2147483647),
            region_width INTEGER NOT NULL CHECK (region_width > 0 AND region_width <= 2147483647),
            region_height INTEGER NOT NULL CHECK (region_height > 0 AND region_height <= 2147483647),
            virtual_region_x INTEGER NOT NULL CHECK (virtual_region_x BETWEEN -2147483648 AND 2147483647),
            virtual_region_y INTEGER NOT NULL CHECK (virtual_region_y BETWEEN -2147483648 AND 2147483647),
            virtual_region_width INTEGER NOT NULL CHECK (virtual_region_width > 0 AND virtual_region_width <= 2147483647),
            virtual_region_height INTEGER NOT NULL CHECK (virtual_region_height > 0 AND virtual_region_height <= 2147483647),
            dpi_x INTEGER NOT NULL CHECK (dpi_x > 0 AND dpi_x <= 2147483647),
            dpi_y INTEGER NOT NULL CHECK (dpi_y > 0 AND dpi_y <= 2147483647),
            physical_width INTEGER NOT NULL CHECK (physical_width > 0 AND physical_width <= 2147483647),
            physical_height INTEGER NOT NULL CHECK (physical_height > 0 AND physical_height <= 2147483647),
            orientation_code TEXT NOT NULL COLLATE BINARY CHECK (orientation_code IN ('landscape', 'portrait', 'landscape_flipped', 'portrait_flipped')),
            topology_digest TEXT NOT NULL COLLATE BINARY CHECK (length(topology_digest) = 64 AND topology_digest NOT GLOB '*[^0-9a-f]*' AND topology_digest = lower(topology_digest)),
            backend_code TEXT NOT NULL COLLATE BINARY CHECK (backend_code = 'ffmpeg-region'),
            audio_mode_code TEXT NOT NULL COLLATE BINARY CHECK (audio_mode_code = 'none'),
            duration_ticks INTEGER NOT NULL CHECK (duration_ticks > 0),
            countdown_seconds INTEGER NOT NULL CHECK (countdown_seconds BETWEEN 0 AND 10),
            normalized_output_directory TEXT NOT NULL COLLATE BINARY CHECK (length(trim(normalized_output_directory, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND normalized_output_directory = trim(normalized_output_directory, char(9) || char(10) || char(11) || char(12) || char(13) || char(32)) AND instr(normalized_output_directory, char(0)) = 0),
            frozen_output_file_name TEXT NOT NULL COLLATE BINARY CHECK (length(trim(frozen_output_file_name, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND frozen_output_file_name = trim(frozen_output_file_name, char(9) || char(10) || char(11) || char(12) || char(13) || char(32)) AND instr(frozen_output_file_name, char(47)) = 0 AND instr(frozen_output_file_name, char(92)) = 0),
            frozen_output_file_path TEXT NOT NULL COLLATE BINARY CHECK (length(trim(frozen_output_file_path, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND frozen_output_file_path = trim(frozen_output_file_path, char(9) || char(10) || char(11) || char(12) || char(13) || char(32)) AND instr(frozen_output_file_path, char(0)) = 0),
            output_conflict_policy_code TEXT NOT NULL COLLATE BINARY CHECK (output_conflict_policy_code = 'fail_if_exists'),
            approved_current_user_sid TEXT NOT NULL COLLATE BINARY CHECK (length(trim(approved_current_user_sid, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND approved_current_user_sid = trim(approved_current_user_sid, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            approved_session_binding TEXT NOT NULL COLLATE BINARY CHECK (length(trim(approved_session_binding, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND approved_session_binding = trim(approved_session_binding, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            specification_version INTEGER NOT NULL CHECK (specification_version = 1),
            specification_digest TEXT NOT NULL COLLATE BINARY CHECK (length(specification_digest) = length('recurring-occurrence-execution-spec/v1:') + 64 AND substr(specification_digest, 1, length('recurring-occurrence-execution-spec/v1:')) = 'recurring-occurrence-execution-spec/v1:' AND substr(specification_digest, length('recurring-occurrence-execution-spec/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(specification_digest, length('recurring-occurrence-execution-spec/v1:') + 1) = lower(substr(specification_digest, length('recurring-occurrence-execution-spec/v1:') + 1))),
            PRIMARY KEY (occurrence_identity),
            UNIQUE (occurrence_id),
            CHECK (region_x <= display_bounds_width - region_width),
            CHECK (region_y <= display_bounds_height - region_height),
            CHECK (virtual_region_x = display_bounds_x + region_x),
            CHECK (virtual_region_y = display_bounds_y + region_y),
            CHECK (frozen_output_file_path = normalized_output_directory || frozen_output_file_name),
            FOREIGN KEY (occurrence_identity, plan_id, occurrence_id)
                REFERENCES recurring_occurrence_slots(occurrence_identity, plan_id, occurrence_id)
                ON DELETE RESTRICT,
            FOREIGN KEY (plan_id, schedule_revision, schedule_digest, time_zone_rules_digest)
                REFERENCES recurring_schedule_versions(plan_id, schedule_revision, schedule_digest, time_zone_rules_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (plan_id, profile_id, profile_version, profile_digest)
                REFERENCES recurring_plan_profile_bindings(plan_id, profile_id, profile_version, profile_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (lease_id, plan_id, configuration_digest, lease_authorization_digest)
                REFERENCES recurring_consent_leases(lease_id, plan_id, configuration_digest, authorization_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (local_approval_id, lease_id, plan_id, configuration_digest, lease_authorization_digest, local_approval_digest, approved_current_user_sid, approved_session_binding)
                REFERENCES recurring_lease_local_approvals(approval_id, lease_id, plan_id, configuration_digest, authorization_digest, approval_digest, current_user_sid, session_binding)
                ON DELETE RESTRICT
        );

        CREATE TRIGGER trg_recurring_occurrence_execution_specs_immutable_update
            BEFORE UPDATE ON recurring_occurrence_execution_specs
            BEGIN
                SELECT RAISE(ABORT, 'recurring_occurrence_execution_specs_are_immutable');
            END;

        CREATE TRIGGER trg_recurring_occurrence_execution_specs_immutable_delete
            BEFORE DELETE ON recurring_occurrence_execution_specs
            BEGIN
                SELECT RAISE(ABORT, 'recurring_occurrence_execution_specs_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recurring_occurrence_execution_specs", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(
            name,
            "recurring_occurrence_execution_specs",
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{operation}\\s+ON\\s+recurring_occurrence_execution_specs\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_occurrence_execution_specs_are_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v13 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
