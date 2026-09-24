using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V14 widens setup_intents to the recurring setup-intent contract and
/// changes idempotency uniqueness from kind-scoped to cross-kind. SQLite
/// cannot alter CHECK or UNIQUE definitions, so this migration rebuilds the
/// table and copies every standing row unchanged.
/// </summary>
internal static class SqliteSchemaV14
{
    public const string MigrationName = "schema_v14_recurring_setup_intents";
    public const string MigrationChecksum = "a328d669bede87bf3364fc499d5300de991163ba02f6270db01ed235a3d0f6ee";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(14, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("setup_intents", new[]
        {
            new SqliteColumnDefinition("intent_id", "TEXT", true, 1),
            new SqliteColumnDefinition("intent_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("idempotency_key", "TEXT", true, 0),
            new SqliteColumnDefinition("request_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("requested_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("expires_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", false, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", false, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", false, 0),
            new SqliteColumnDefinition("scope_id", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
            new SqliteColumnDefinition("terminal_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("maximum_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("lease_valid_until_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", false, 0),
            new SqliteColumnDefinition("frozen_file_name", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_schedule_kind_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_time_zone_id", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_time_zone_rules_digest", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_schedule_digest", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_local_start_date", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_local_end_date", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_local_wall_clock_seconds", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_weekday_mask", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_maximum_occurrences", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_recording_duration_ticks", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_latest_start_grace_ticks", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_max_uses", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_max_cumulative_duration_ticks", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_authorization_valid_until_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_target_type_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_audio_mode_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_backend_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_countdown_seconds", "INTEGER", false, 0),
            new SqliteColumnDefinition("recurring_output_directory", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_filename_prefix", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_output_conflict_policy_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_wake_policy_code", "TEXT", false, 0),
            new SqliteColumnDefinition("recurring_desktop_requirement_code", "TEXT", false, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_setup_intents_status_expires", "setup_intents", false, new[] { "status_code", "expires_at_utc" }),
        new SqliteIndexDefinition("idx_setup_intents_identity", "setup_intents", false, new[] { "current_user_sid", "session_binding", "idempotency_key" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("setup_intents", new[] { "idempotency_key", "current_user_sid", "session_binding" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("intent_kind_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(intent_kind_code\\s+IN\\s*\\('standing_once_fixed_region',\\s*'recurring_daily_weekly_fixed_region'\\)\\)"),
        Check("request_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(request_digest\\)\\s*=\\s*64"),
        Check("status_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(status_code\\s+IN"),
        Check("terminal_reason_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(\\(status_code\\s+IN"),
        Check("recurring_schedule_kind_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_schedule_kind_code\\s+IS\\s+NULL\\s+OR\\s+recurring_schedule_kind_code\\s+IN\\s*\\('daily',\\s*'weekly'\\)\\)"),
        Check("recurring_time_zone_id\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_time_zone_id\\s+IS\\s+NULL\\s+OR\\s+\\(length\\(trim\\(recurring_time_zone_id\\)\\)\\s*>\\s*0"),
        Check("recurring_time_zone_rules_digest\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_time_zone_rules_digest\\s+IS\\s+NULL\\s+OR\\s+\\(?length\\(recurring_time_zone_rules_digest\\)\\s*=\\s*92"),
        Check("recurring_schedule_digest\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_schedule_digest\\s+IS\\s+NULL\\s+OR\\s+\\(?length\\(recurring_schedule_digest\\)\\s*=\\s*86"),
        Check("recurring_local_start_date\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_local_start_date\\s+IS\\s+NULL\\s+OR\\s+length\\(recurring_local_start_date\\)\\s*=\\s*10"),
        Check("recurring_local_end_date\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_local_end_date\\s+IS\\s+NULL\\s+OR\\s+length\\(recurring_local_end_date\\)\\s*=\\s*10"),
        Check("recurring_local_wall_clock_seconds\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_local_wall_clock_seconds\\s+IS\\s+NULL\\s+OR\\s+recurring_local_wall_clock_seconds\\s+BETWEEN\\s+0\\s+AND\\s+86399\\)"),
        Check("recurring_weekday_mask\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_weekday_mask\\s+IS\\s+NULL\\s+OR\\s+recurring_weekday_mask\\s+BETWEEN\\s+0\\s+AND\\s+127\\)"),
        Check("recurring_maximum_occurrences\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_maximum_occurrences\\s+IS\\s+NULL\\s+OR\\s+recurring_maximum_occurrences\\s+BETWEEN\\s+1\\s+AND\\s+366\\)"),
        Check("recurring_recording_duration_ticks\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_recording_duration_ticks\\s+IS\\s+NULL\\s+OR\\s+recurring_recording_duration_ticks\\s+BETWEEN\\s+10000000\\s+AND\\s+6000000000\\)"),
        Check("recurring_latest_start_grace_ticks\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_latest_start_grace_ticks\\s+IS\\s+NULL\\s+OR\\s+recurring_latest_start_grace_ticks\\s+BETWEEN\\s+0\\s+AND\\s+3000000000\\)"),
        Check("recurring_max_uses\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_max_uses\\s+IS\\s+NULL\\s+OR\\s+recurring_max_uses\\s+BETWEEN\\s+1\\s+AND\\s+366\\)"),
        Check("recurring_max_cumulative_duration_ticks\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_max_cumulative_duration_ticks\\s+IS\\s+NULL\\s+OR\\s+recurring_max_cumulative_duration_ticks\\s+BETWEEN\\s+10000000\\s+AND\\s+2196000000000\\)"),
        Check("recurring_authorization_valid_until_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_authorization_valid_until_utc\\s+IS\\s+NULL\\s+OR\\s+recurring_authorization_valid_until_utc\\s+>=\\s+0\\)"),
        Check("recurring_target_type_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_target_type_code\\s+IS\\s+NULL\\s+OR\\s+recurring_target_type_code\\s*=\\s*'fixed_region'\\)"),
        Check("recurring_audio_mode_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_audio_mode_code\\s+IS\\s+NULL\\s+OR\\s+recurring_audio_mode_code\\s*=\\s*'none'\\)"),
        Check("recurring_backend_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_backend_code\\s+IS\\s+NULL\\s+OR\\s+recurring_backend_code\\s*=\\s*'ffmpeg-region'\\)"),
        Check("recurring_countdown_seconds\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(recurring_countdown_seconds\\s+IS\\s+NULL\\s+OR\\s+recurring_countdown_seconds\\s*=\\s*0\\)"),
        Check("recurring_output_directory\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_output_directory\\s+IS\\s+NULL\\s+OR\\s+length\\(trim\\(recurring_output_directory\\)\\)\\s*>\\s*0"),
        Check("recurring_filename_prefix\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_filename_prefix\\s+IS\\s+NULL\\s+OR\\s+\\(length\\(trim\\(recurring_filename_prefix\\)\\)\\s*>\\s*0"),
        Check("recurring_output_conflict_policy_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_output_conflict_policy_code\\s+IS\\s+NULL\\s+OR\\s+recurring_output_conflict_policy_code\\s*=\\s*'fail_if_exists'\\)"),
        Check("recurring_wake_policy_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_wake_policy_code\\s+IS\\s+NULL\\s+OR\\s+recurring_wake_policy_code\\s*=\\s*'natural_wake_only'\\)"),
        Check("recurring_desktop_requirement_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(recurring_desktop_requirement_code\\s+IS\\s+NULL\\s+OR\\s+recurring_desktop_requirement_code\\s*=\\s*'interactive_desktop_required'\\)"),
        Check("CHECK\\s*\\(\\(intent_kind_code\\s*=\\s*'standing_once_fixed_region'.*recurring_schedule_kind_code\\s+IS\\s+NULL.*recurring_desktop_requirement_code\\s+IS\\s+NULL.*\\)\\s+OR\\s+\\(intent_kind_code\\s*=\\s*'recurring_daily_weekly_fixed_region'.*recurring_schedule_kind_code\\s+IS\\s+NOT\\s+NULL.*recurring_desktop_requirement_code\\s+IS\\s+NOT\\s+NULL.*plan_id\\s+IS\\s+NULL.*scope_id\\s+IS\\s+NULL.*output_directory\\s+IS\\s+NULL.*\\)\\)"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE setup_intents_v14 (
            intent_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(intent_id)) > 0 AND length(intent_id) <= 128 AND instr(intent_id, char(47)) = 0 AND instr(intent_id, char(92)) = 0),
            intent_kind_code TEXT NOT NULL CHECK (intent_kind_code IN ('standing_once_fixed_region', 'recurring_daily_weekly_fixed_region')),
            idempotency_key TEXT NOT NULL CHECK (length(trim(idempotency_key)) > 0 AND length(idempotency_key) <= 128 AND instr(idempotency_key, char(47)) = 0 AND instr(idempotency_key, char(92)) = 0),
            request_digest TEXT NOT NULL CHECK (length(request_digest) = 64 AND request_digest = lower(request_digest) AND request_digest NOT GLOB '*[^0-9a-f]*'),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0 AND length(current_user_sid) <= 256),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0 AND length(session_binding) <= 256),
            status_code TEXT NOT NULL CHECK (status_code IN ('region_selection_pending', 'lease_approval_pending', 'activated', 'rejected', 'expired')),
            requested_at_utc INTEGER NOT NULL CHECK (requested_at_utc >= 0),
            expires_at_utc INTEGER NOT NULL CHECK (expires_at_utc > requested_at_utc),
            plan_id TEXT NULL CHECK (plan_id IS NULL OR (length(trim(plan_id)) > 0 AND length(plan_id) <= 128 AND instr(plan_id, char(47)) = 0 AND instr(plan_id, char(92)) = 0)),
            occurrence_id TEXT NULL CHECK (occurrence_id IS NULL OR (length(trim(occurrence_id)) > 0 AND length(occurrence_id) <= 128 AND instr(occurrence_id, char(47)) = 0 AND instr(occurrence_id, char(92)) = 0)),
            lease_id TEXT NULL CHECK (lease_id IS NULL OR (length(trim(lease_id)) > 0 AND length(lease_id) <= 128 AND instr(lease_id, char(47)) = 0 AND instr(lease_id, char(92)) = 0)),
            scope_id TEXT NULL CHECK (scope_id IS NULL OR (length(trim(scope_id)) > 0 AND length(scope_id) <= 128 AND instr(scope_id, char(47)) = 0 AND instr(scope_id, char(92)) = 0)),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            terminal_reason_code TEXT NULL CHECK ((status_code IN ('region_selection_pending', 'lease_approval_pending', 'activated') AND terminal_reason_code IS NULL) OR (status_code IN ('rejected', 'expired') AND terminal_reason_code IS NOT NULL AND length(trim(terminal_reason_code)) > 0)),
            scheduled_start_utc INTEGER NULL CHECK (scheduled_start_utc IS NULL OR scheduled_start_utc >= 0),
            latest_start_utc INTEGER NULL CHECK (latest_start_utc IS NULL OR latest_start_utc >= 0),
            planned_end_utc INTEGER NULL CHECK (planned_end_utc IS NULL OR planned_end_utc >= 0),
            maximum_duration_ms INTEGER NULL CHECK (maximum_duration_ms IS NULL OR (maximum_duration_ms > 0 AND maximum_duration_ms <= 600000)),
            lease_valid_until_utc INTEGER NULL CHECK (lease_valid_until_utc IS NULL OR lease_valid_until_utc >= 0),
            output_directory TEXT NULL CHECK (output_directory IS NULL OR length(trim(output_directory)) > 0),
            frozen_file_name TEXT NULL CHECK (frozen_file_name IS NULL OR (length(trim(frozen_file_name)) > 0 AND instr(frozen_file_name, char(47)) = 0 AND instr(frozen_file_name, char(92)) = 0)),
            recurring_schedule_kind_code TEXT NULL CHECK (recurring_schedule_kind_code IS NULL OR recurring_schedule_kind_code IN ('daily', 'weekly')),
            recurring_time_zone_id TEXT NULL CHECK (recurring_time_zone_id IS NULL OR (length(trim(recurring_time_zone_id)) > 0 AND length(recurring_time_zone_id) <= 128)),
            recurring_time_zone_rules_digest TEXT NULL CHECK (recurring_time_zone_rules_digest IS NULL OR (length(recurring_time_zone_rules_digest) = 92 AND substr(recurring_time_zone_rules_digest, 1, 28) = 'recurring-timezone-rules/v1:' AND substr(recurring_time_zone_rules_digest, 29) NOT GLOB '*[^0-9a-f]*')),
            recurring_schedule_digest TEXT NULL CHECK (recurring_schedule_digest IS NULL OR (length(recurring_schedule_digest) = 86 AND substr(recurring_schedule_digest, 1, 22) = 'recurring-schedule/v1:' AND substr(recurring_schedule_digest, 23) NOT GLOB '*[^0-9a-f]*')),
            recurring_local_start_date TEXT NULL CHECK (recurring_local_start_date IS NULL OR length(recurring_local_start_date) = 10),
            recurring_local_end_date TEXT NULL CHECK (recurring_local_end_date IS NULL OR length(recurring_local_end_date) = 10),
            recurring_local_wall_clock_seconds INTEGER NULL CHECK (recurring_local_wall_clock_seconds IS NULL OR recurring_local_wall_clock_seconds BETWEEN 0 AND 86399),
            recurring_weekday_mask INTEGER NULL CHECK (recurring_weekday_mask IS NULL OR recurring_weekday_mask BETWEEN 0 AND 127),
            recurring_maximum_occurrences INTEGER NULL CHECK (recurring_maximum_occurrences IS NULL OR recurring_maximum_occurrences BETWEEN 1 AND 366),
            recurring_recording_duration_ticks INTEGER NULL CHECK (recurring_recording_duration_ticks IS NULL OR recurring_recording_duration_ticks BETWEEN 10000000 AND 6000000000),
            recurring_latest_start_grace_ticks INTEGER NULL CHECK (recurring_latest_start_grace_ticks IS NULL OR recurring_latest_start_grace_ticks BETWEEN 0 AND 3000000000),
            recurring_max_uses INTEGER NULL CHECK (recurring_max_uses IS NULL OR recurring_max_uses BETWEEN 1 AND 366),
            recurring_max_cumulative_duration_ticks INTEGER NULL CHECK (recurring_max_cumulative_duration_ticks IS NULL OR recurring_max_cumulative_duration_ticks BETWEEN 10000000 AND 2196000000000),
            recurring_authorization_valid_until_utc INTEGER NULL CHECK (recurring_authorization_valid_until_utc IS NULL OR recurring_authorization_valid_until_utc >= 0),
            recurring_target_type_code TEXT NULL CHECK (recurring_target_type_code IS NULL OR recurring_target_type_code = 'fixed_region'),
            recurring_audio_mode_code TEXT NULL CHECK (recurring_audio_mode_code IS NULL OR recurring_audio_mode_code = 'none'),
            recurring_backend_code TEXT NULL CHECK (recurring_backend_code IS NULL OR recurring_backend_code = 'ffmpeg-region'),
            recurring_countdown_seconds INTEGER NULL CHECK (recurring_countdown_seconds IS NULL OR recurring_countdown_seconds = 0),
            recurring_output_directory TEXT NULL CHECK (recurring_output_directory IS NULL OR length(trim(recurring_output_directory)) > 0),
            recurring_filename_prefix TEXT NULL CHECK (recurring_filename_prefix IS NULL OR (length(trim(recurring_filename_prefix)) > 0 AND length(recurring_filename_prefix) <= 64 AND instr(recurring_filename_prefix, char(47)) = 0 AND instr(recurring_filename_prefix, char(92)) = 0)),
            recurring_output_conflict_policy_code TEXT NULL CHECK (recurring_output_conflict_policy_code IS NULL OR recurring_output_conflict_policy_code = 'fail_if_exists'),
            recurring_wake_policy_code TEXT NULL CHECK (recurring_wake_policy_code IS NULL OR recurring_wake_policy_code = 'natural_wake_only'),
            recurring_desktop_requirement_code TEXT NULL CHECK (recurring_desktop_requirement_code IS NULL OR recurring_desktop_requirement_code = 'interactive_desktop_required'),
            UNIQUE (idempotency_key, current_user_sid, session_binding),
            CHECK ((intent_kind_code = 'standing_once_fixed_region' AND recurring_schedule_kind_code IS NULL AND recurring_time_zone_id IS NULL AND recurring_time_zone_rules_digest IS NULL AND recurring_schedule_digest IS NULL AND recurring_local_start_date IS NULL AND recurring_local_end_date IS NULL AND recurring_local_wall_clock_seconds IS NULL AND recurring_weekday_mask IS NULL AND recurring_maximum_occurrences IS NULL AND recurring_recording_duration_ticks IS NULL AND recurring_latest_start_grace_ticks IS NULL AND recurring_max_uses IS NULL AND recurring_max_cumulative_duration_ticks IS NULL AND recurring_authorization_valid_until_utc IS NULL AND recurring_target_type_code IS NULL AND recurring_audio_mode_code IS NULL AND recurring_backend_code IS NULL AND recurring_countdown_seconds IS NULL AND recurring_output_directory IS NULL AND recurring_filename_prefix IS NULL AND recurring_output_conflict_policy_code IS NULL AND recurring_wake_policy_code IS NULL AND recurring_desktop_requirement_code IS NULL) OR (intent_kind_code = 'recurring_daily_weekly_fixed_region' AND recurring_schedule_kind_code IS NOT NULL AND recurring_time_zone_id IS NOT NULL AND recurring_time_zone_rules_digest IS NOT NULL AND recurring_schedule_digest IS NOT NULL AND recurring_local_start_date IS NOT NULL AND recurring_local_end_date IS NOT NULL AND recurring_local_wall_clock_seconds IS NOT NULL AND recurring_weekday_mask IS NOT NULL AND recurring_maximum_occurrences IS NOT NULL AND recurring_recording_duration_ticks IS NOT NULL AND recurring_latest_start_grace_ticks IS NOT NULL AND recurring_max_uses IS NOT NULL AND recurring_max_cumulative_duration_ticks IS NOT NULL AND recurring_authorization_valid_until_utc IS NOT NULL AND recurring_target_type_code IS NOT NULL AND recurring_audio_mode_code IS NOT NULL AND recurring_backend_code IS NOT NULL AND recurring_countdown_seconds IS NOT NULL AND recurring_output_directory IS NOT NULL AND recurring_filename_prefix IS NOT NULL AND recurring_output_conflict_policy_code IS NOT NULL AND recurring_wake_policy_code IS NOT NULL AND recurring_desktop_requirement_code IS NOT NULL AND plan_id IS NULL AND occurrence_id IS NULL AND lease_id IS NULL AND scope_id IS NULL AND scheduled_start_utc IS NULL AND latest_start_utc IS NULL AND planned_end_utc IS NULL AND maximum_duration_ms IS NULL AND output_directory IS NULL AND frozen_file_name IS NULL))
        );

        INSERT INTO setup_intents_v14 (
            intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code,
            requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc,
            version, terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms,
            lease_valid_until_utc, output_directory, frozen_file_name, recurring_schedule_kind_code, recurring_time_zone_id,
            recurring_time_zone_rules_digest, recurring_schedule_digest, recurring_local_start_date, recurring_local_end_date,
            recurring_local_wall_clock_seconds, recurring_weekday_mask, recurring_maximum_occurrences, recurring_recording_duration_ticks,
            recurring_latest_start_grace_ticks, recurring_max_uses, recurring_max_cumulative_duration_ticks,
            recurring_authorization_valid_until_utc, recurring_target_type_code, recurring_audio_mode_code, recurring_backend_code,
            recurring_countdown_seconds, recurring_output_directory, recurring_filename_prefix, recurring_output_conflict_policy_code,
            recurring_wake_policy_code, recurring_desktop_requirement_code)
        SELECT intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code,
            requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc,
            version, terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms,
            lease_valid_until_utc, output_directory, frozen_file_name, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
        FROM setup_intents;

        DROP TABLE setup_intents;
        ALTER TABLE setup_intents_v14 RENAME TO setup_intents;

        CREATE INDEX idx_setup_intents_status_expires
            ON setup_intents(status_code, expires_at_utc);
        CREATE INDEX idx_setup_intents_identity
            ON setup_intents(current_user_sid, session_binding, idempotency_key);
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("setup_intents", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException("The fixed schema v14 definition checksum changed.");
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
