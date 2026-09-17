using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV11
{
    public const string MigrationName = "schema_v11_recurring_consent_leases_and_uses";

    // Filled from the canonical definition after the first compile.  Keeping
    // this as a fixed literal is intentional: the migration history is an
    // immutable protocol, not a checksum calculated from the current source.
    public const string MigrationChecksum = "846f8e7d6775648674ad327fad040b7cec73cc1948f837bf81ec1fc5b9e3e6be";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(11, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_consent_leases", new[]
        {
            new SqliteColumnDefinition("lease_id", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 0),
            new SqliteColumnDefinition("schedule_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("time_zone_rules_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_id", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("profile_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("configuration_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("valid_from_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("valid_until_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("authorized_plan_latest_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("per_run_duration_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("max_uses", "INTEGER", true, 0),
            new SqliteColumnDefinition("max_cumulative_duration_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("authorization_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("recurring_lease_uses", new[]
        {
            new SqliteColumnDefinition("use_id", "TEXT", true, 1),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_identity", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("run_id", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reserved_use_count", "INTEGER", true, 0),
            new SqliteColumnDefinition("reserved_duration_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("actual_settled_duration_ticks", "INTEGER", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "ux_recurring_schedule_versions_exact_lease_parent",
            "recurring_schedule_versions",
            true,
            new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest" }),
        new SqliteIndexDefinition(
            "ux_recurring_plan_profile_bindings_exact_lease_parent",
            "recurring_plan_profile_bindings",
            true,
            new[] { "plan_id", "profile_id", "profile_version", "profile_digest" }),
        new SqliteIndexDefinition(
            "ux_recurring_occurrence_slots_exact_lease_parent",
            "recurring_occurrence_slots",
            true,
            new[] { "occurrence_identity", "plan_id", "occurrence_id" }),
        new SqliteIndexDefinition(
            "idx_recurring_consent_leases_plan_status_valid_until",
            "recurring_consent_leases",
            false,
            new[] { "plan_id", "status_code", "valid_until_utc", "lease_id" }),
        new SqliteIndexDefinition(
            "idx_recurring_lease_uses_lease_status_created",
            "recurring_lease_uses",
            false,
            new[] { "lease_id", "status_code", "created_at_utc", "use_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_consent_leases", new[] { "lease_id", "plan_id" }),
        new SqliteUniqueConstraintDefinition("recurring_lease_uses", new[] { "occurrence_identity" }),
        new SqliteUniqueConstraintDefinition("recurring_lease_uses", new[] { "run_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_consent_leases", new[]
        {
            new SqliteForeignKeyInfo(
                "recurring_schedule_versions", "NO ACTION", "RESTRICT",
                new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest" },
                new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest" }),
            new SqliteForeignKeyInfo(
                "recurring_plan_profile_bindings", "NO ACTION", "RESTRICT",
                new[] { "plan_id", "profile_id", "profile_version", "profile_digest" },
                new[] { "plan_id", "profile_id", "profile_version", "profile_digest" }),
        }, RequireCompatibleCollation: true),
        new SqliteForeignKeyDefinition("recurring_lease_uses", new[]
        {
            new SqliteForeignKeyInfo(
                "recurring_consent_leases", "NO ACTION", "RESTRICT",
                new[] { "lease_id", "plan_id" }, new[] { "lease_id", "plan_id" }),
            new SqliteForeignKeyInfo(
                "recurring_occurrence_slots", "NO ACTION", "RESTRICT",
                new[] { "occurrence_identity", "plan_id", "occurrence_id" },
                new[] { "occurrence_identity", "plan_id", "occurrence_id" }),
            new SqliteForeignKeyInfo(
                "recording_runs", "NO ACTION", "RESTRICT",
                new[] { "run_id", "occurrence_id" }, new[] { "id", "occurrence_id" }),
        }, RequireCompatibleCollation: true),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        CheckLease("lease_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(lease_id"),
        CheckLease("schedule_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-schedule/v1:"),
        CheckLease("time_zone_rules_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-timezone-rules/v1:"),
        CheckLease("profile_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-fixed-region-profile/v1:"),
        CheckLease("configuration_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-plan-configuration/v1:"),
        CheckLease("authorization_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-consent-lease-authorization/v1:"),
        CheckLease("status_code\\s+TEXT\\s+NOT NULL.*status_code\\s+IN\\s*\\('pending'.*'exhausted'\\)"),
        CheckLease("created_at_utc\\s+INTEGER\\s+NOT NULL.*created_at_utc\\s*<\\s*valid_until_utc"),
        CheckLease("valid_from_utc\\s+INTEGER\\s+NOT NULL.*valid_from_utc\\s*<\\s*authorized_plan_latest_end_utc.*authorized_plan_latest_end_utc\\s*<\\s*valid_until_utc"),
        CheckLease("per_run_duration_ticks\\s+INTEGER\\s+NOT NULL.*per_run_duration_ticks\\s*>\\s*0"),
        CheckLease("max_uses\\s+INTEGER\\s+NOT NULL.*max_uses\\s*>\\s*0"),
        CheckLease("max_cumulative_duration_ticks\\s+INTEGER\\s+NOT NULL.*max_cumulative_duration_ticks\\s*>\\s*=\\s*per_run_duration_ticks"),
        CheckLease("updated_at_utc\\s+INTEGER\\s+NOT NULL.*updated_at_utc\\s*>\\s*=\\s*created_at_utc.*version\\s+INTEGER\\s+NOT NULL.*version\\s*>\\s*=\\s*0"),
        CheckUse("use_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(use_id"),
        CheckUse("status_code\\s+TEXT\\s+NOT NULL.*status_code\\s+IN\\s*\\('available'.*'started_unknown'\\)"),
        CheckUse("reserved_use_count\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(reserved_use_count\\s+BETWEEN\\s+0\\s+AND\\s+1\\)"),
        CheckUse("actual_settled_duration_ticks\\s+INTEGER\\s+NULL.*actual_settled_duration_ticks\\s+IS NULL\\s+OR\\s+actual_settled_duration_ticks\\s*>\\s*=\\s*0"),
        CheckUse("created_at_utc\\s+INTEGER\\s+NOT NULL.*updated_at_utc\\s+INTEGER\\s+NOT NULL.*updated_at_utc\\s*>\\s*=\\s*created_at_utc.*version\\s+INTEGER\\s+NOT NULL.*version\\s*>\\s*=\\s*0"),
        CheckUse("status_code\\s*=\\s*'available'.*reserved_use_count\\s*=\\s*0.*reserved_duration_ticks\\s*=\\s*0.*actual_settled_duration_ticks\\s+IS NULL"),
        CheckUse("status_code\\s+IN\\s*\\('reserved'.*'started_unknown'\\).*reserved_use_count\\s*=\\s*1.*reserved_duration_ticks\\s*>\\s*0.*actual_settled_duration_ticks\\s+IS NULL"),
        CheckUse("status_code\\s*=\\s*'settled'.*reserved_use_count\\s*=\\s*1.*reserved_duration_ticks\\s*>\\s*0.*actual_settled_duration_ticks\\s+IS NOT NULL.*actual_settled_duration_ticks\\s*<=\\s*reserved_duration_ticks"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        Trigger(
            "trg_recurring_consent_leases_pending_insert",
            "INSERT",
            "recurring_consent_leases",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_pending_insert_only'",
            "WHERE\\s+NEW\\.status_code\\s*<>\\s*'pending'",
            "NEW\\.version\\s*<>\\s*0",
            "NEW\\.updated_at_utc\\s*<>\\s*NEW\\.created_at_utc"),
        Trigger(
            "trg_recurring_consent_leases_lifecycle_update",
            "UPDATE",
            "recurring_consent_leases",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_identity_immutable'",
            "NEW\\.lease_id\\s+IS\\s+NOT\\s+OLD\\.lease_id.*NEW\\.authorization_digest\\s+IS\\s+NOT\\s+OLD\\.authorization_digest.*NEW\\.created_at_utc\\s+IS\\s+NOT\\s+OLD\\.created_at_utc",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_same_state_update_rejected'",
            "NEW\\.status_code\\s*=\\s*OLD\\.status_code",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_invalid_transition'",
            "OLD\\.status_code\\s*=\\s*'pending'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('active',\\s*'rejected',\\s*'revoked',\\s*'expired'\\)",
            "OLD\\.status_code\\s*=\\s*'active'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('revoked',\\s*'expired',\\s*'exhausted'\\)",
            "OLD\\.status_code\\s*=\\s*'exhausted'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'revoked'",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_version_or_time_not_monotonic'",
            "NEW\\.version\\s*<>\\s*OLD\\.version\\s*\\+\\s*1\\s+OR\\s+NEW\\.updated_at_utc\\s*<\\s*OLD\\.updated_at_utc",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_active_after_validity'",
            "NEW\\.status_code\\s*=\\s*'active'\\s+AND\\s+NEW\\.updated_at_utc\\s*>=\\s*NEW\\.valid_until_utc",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_consent_leases_expiration_before_validity'",
            "NEW\\.status_code\\s*=\\s*'expired'\\s+AND\\s+NEW\\.updated_at_utc\\s*<\\s*NEW\\.valid_until_utc"),
        ImmutableTrigger(
            "trg_recurring_consent_leases_immutable_delete",
            "recurring_consent_leases",
            "recurring_consent_leases_are_immutable"),
        Trigger(
            "trg_recurring_lease_uses_lifecycle_update",
            "UPDATE",
            "recurring_lease_uses",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_identity_immutable'",
            "NEW\\.use_id\\s+IS\\s+NOT\\s+OLD\\.use_id.*NEW\\.lease_id\\s+IS\\s+NOT\\s+OLD\\.lease_id.*NEW\\.run_id\\s+IS\\s+NOT\\s+OLD\\.run_id.*NEW\\.created_at_utc\\s+IS\\s+NOT\\s+OLD\\.created_at_utc",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_same_state_update_rejected'",
            "NEW\\.status_code\\s*=\\s*OLD\\.status_code",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_invalid_transition'",
            "OLD\\.status_code\\s*=\\s*'available'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'reserved'",
            "OLD\\.status_code\\s*=\\s*'reserved'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('available',\\s*'start_committed'\\)",
            "OLD\\.status_code\\s*=\\s*'start_committed'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('consumed',\\s*'started_unknown'\\)",
            "OLD\\.status_code\\s*=\\s*'consumed'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('settled',\\s*'started_unknown'\\)",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_version_or_time_not_monotonic'",
            "NEW\\.version\\s*<>\\s*OLD\\.version\\s*\\+\\s*1\\s+OR\\s+NEW\\.updated_at_utc\\s*<\\s*OLD\\.updated_at_utc",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_reserved_to_available_requires_release'",
            "OLD\\.status_code\\s*=\\s*'reserved'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'available'.*NEW\\.reserved_use_count\\s*<>\\s*0.*NEW\\.reserved_duration_ticks\\s*<>\\s*0.*NEW\\.actual_settled_duration_ticks\\s+IS\\s+NOT\\s+NULL",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_reservation_evidence_immutable'",
            "OLD\\.status_code\\s*=\\s*'reserved'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'start_committed'.*OLD\\.status_code\\s*=\\s*'start_committed'\\s+AND\\s+NEW\\.status_code\\s+IN\\s*\\('consumed',\\s*'started_unknown'\\).*OLD\\.status_code\\s*=\\s*'consumed'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'started_unknown'",
            "NEW\\.reserved_use_count\\s*<>\\s*OLD\\.reserved_use_count",
            "NEW\\.reserved_duration_ticks\\s*<>\\s*OLD\\.reserved_duration_ticks",
            "NEW\\.actual_settled_duration_ticks\\s+IS\\s+NOT\\s+NULL",
            "SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_lease_uses_settlement_evidence_invalid'",
            "OLD\\.status_code\\s*=\\s*'consumed'\\s+AND\\s+NEW\\.status_code\\s*=\\s*'settled'.*NEW\\.actual_settled_duration_ticks\\s+IS\\s+NULL.*NEW\\.actual_settled_duration_ticks\\s*>\\s*OLD\\.reserved_duration_ticks"),
        ImmutableTrigger(
            "trg_recurring_lease_uses_immutable_delete",
            "recurring_lease_uses",
            "recurring_lease_uses_are_immutable"),
    });

    private const string SchemaDefinition = """
        CREATE UNIQUE INDEX ux_recurring_schedule_versions_exact_lease_parent
            ON recurring_schedule_versions(plan_id, schedule_revision, schedule_digest, time_zone_rules_digest);

        CREATE UNIQUE INDEX ux_recurring_plan_profile_bindings_exact_lease_parent
            ON recurring_plan_profile_bindings(plan_id, profile_id, profile_version, profile_digest);

        CREATE UNIQUE INDEX ux_recurring_occurrence_slots_exact_lease_parent
            ON recurring_occurrence_slots(occurrence_identity, plan_id, occurrence_id);

        CREATE TABLE recurring_consent_leases (
            lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND lease_id = trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND plan_id = trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            schedule_digest TEXT NOT NULL COLLATE BINARY CHECK (length(schedule_digest) = length('recurring-schedule/v1:') + 64 AND substr(schedule_digest, 1, length('recurring-schedule/v1:')) = 'recurring-schedule/v1:' AND substr(schedule_digest, length('recurring-schedule/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(schedule_digest, length('recurring-schedule/v1:') + 1) = lower(substr(schedule_digest, length('recurring-schedule/v1:') + 1))),
            time_zone_rules_digest TEXT NOT NULL COLLATE BINARY CHECK (length(time_zone_rules_digest) = length('recurring-timezone-rules/v1:') + 64 AND substr(time_zone_rules_digest, 1, length('recurring-timezone-rules/v1:')) = 'recurring-timezone-rules/v1:' AND substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1) = lower(substr(time_zone_rules_digest, length('recurring-timezone-rules/v1:') + 1))),
            profile_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND profile_id = trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            profile_version INTEGER NOT NULL CHECK (profile_version > 0),
            profile_digest TEXT NOT NULL COLLATE BINARY CHECK (length(profile_digest) = length('recurring-fixed-region-profile/v1:') + 64 AND substr(profile_digest, 1, length('recurring-fixed-region-profile/v1:')) = 'recurring-fixed-region-profile/v1:' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) = lower(substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1))),
            configuration_digest TEXT NOT NULL COLLATE BINARY CHECK (length(configuration_digest) = length('recurring-plan-configuration/v1:') + 64 AND substr(configuration_digest, 1, length('recurring-plan-configuration/v1:')) = 'recurring-plan-configuration/v1:' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) = lower(substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1))),
            status_code TEXT NOT NULL COLLATE BINARY CHECK (status_code IN ('pending', 'active', 'rejected', 'revoked', 'expired', 'exhausted')),
            valid_from_utc INTEGER NOT NULL CHECK (valid_from_utc >= 0),
            valid_until_utc INTEGER NOT NULL CHECK (valid_until_utc >= 0),
            authorized_plan_latest_end_utc INTEGER NOT NULL CHECK (authorized_plan_latest_end_utc >= 0),
            per_run_duration_ticks INTEGER NOT NULL CHECK (per_run_duration_ticks > 0),
            max_uses INTEGER NOT NULL CHECK (max_uses > 0),
            max_cumulative_duration_ticks INTEGER NOT NULL CHECK (max_cumulative_duration_ticks > 0 AND max_cumulative_duration_ticks >= per_run_duration_ticks),
            authorization_digest TEXT NOT NULL COLLATE BINARY CHECK (length(authorization_digest) = length('recurring-consent-lease-authorization/v1:') + 64 AND substr(authorization_digest, 1, length('recurring-consent-lease-authorization/v1:')) = 'recurring-consent-lease-authorization/v1:' AND substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) = lower(substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1))),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0 AND created_at_utc < valid_until_utc),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            PRIMARY KEY (lease_id),
            UNIQUE (lease_id, plan_id),
            FOREIGN KEY (plan_id, schedule_revision, schedule_digest, time_zone_rules_digest)
                REFERENCES recurring_schedule_versions(plan_id, schedule_revision, schedule_digest, time_zone_rules_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (plan_id, profile_id, profile_version, profile_digest)
                REFERENCES recurring_plan_profile_bindings(plan_id, profile_id, profile_version, profile_digest)
                ON DELETE RESTRICT,
            CHECK (valid_from_utc < authorized_plan_latest_end_utc AND authorized_plan_latest_end_utc < valid_until_utc)
        );

        CREATE INDEX idx_recurring_consent_leases_plan_status_valid_until
            ON recurring_consent_leases(plan_id, status_code, valid_until_utc, lease_id);

        CREATE TRIGGER trg_recurring_consent_leases_pending_insert
            BEFORE INSERT ON recurring_consent_leases
            BEGIN
                SELECT RAISE(ABORT, 'recurring_consent_leases_pending_insert_only')
                WHERE NEW.status_code <> 'pending' OR NEW.version <> 0 OR NEW.updated_at_utc <> NEW.created_at_utc;
            END;

        CREATE TRIGGER trg_recurring_consent_leases_lifecycle_update
            BEFORE UPDATE ON recurring_consent_leases
            BEGIN
                SELECT RAISE(ABORT, 'recurring_consent_leases_identity_immutable')
                WHERE NEW.lease_id IS NOT OLD.lease_id OR NEW.plan_id IS NOT OLD.plan_id
                   OR NEW.schedule_revision IS NOT OLD.schedule_revision OR NEW.schedule_digest IS NOT OLD.schedule_digest
                   OR NEW.time_zone_rules_digest IS NOT OLD.time_zone_rules_digest OR NEW.profile_id IS NOT OLD.profile_id
                   OR NEW.profile_version IS NOT OLD.profile_version OR NEW.profile_digest IS NOT OLD.profile_digest
                   OR NEW.configuration_digest IS NOT OLD.configuration_digest OR NEW.valid_from_utc IS NOT OLD.valid_from_utc
                   OR NEW.valid_until_utc IS NOT OLD.valid_until_utc OR NEW.authorized_plan_latest_end_utc IS NOT OLD.authorized_plan_latest_end_utc
                   OR NEW.per_run_duration_ticks IS NOT OLD.per_run_duration_ticks OR NEW.max_uses IS NOT OLD.max_uses
                   OR NEW.max_cumulative_duration_ticks IS NOT OLD.max_cumulative_duration_ticks
                   OR NEW.authorization_digest IS NOT OLD.authorization_digest OR NEW.created_at_utc IS NOT OLD.created_at_utc;
                SELECT RAISE(ABORT, 'recurring_consent_leases_same_state_update_rejected') WHERE NEW.status_code = OLD.status_code;
                SELECT RAISE(ABORT, 'recurring_consent_leases_invalid_transition')
                WHERE NOT ((OLD.status_code = 'pending' AND NEW.status_code IN ('active', 'rejected', 'revoked', 'expired'))
                    OR (OLD.status_code = 'active' AND NEW.status_code IN ('revoked', 'expired', 'exhausted'))
                    OR (OLD.status_code = 'exhausted' AND NEW.status_code = 'revoked'));
                SELECT RAISE(ABORT, 'recurring_consent_leases_version_or_time_not_monotonic')
                WHERE NEW.version <> OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
                SELECT RAISE(ABORT, 'recurring_consent_leases_active_after_validity')
                WHERE NEW.status_code = 'active' AND NEW.updated_at_utc >= NEW.valid_until_utc;
                SELECT RAISE(ABORT, 'recurring_consent_leases_expiration_before_validity')
                WHERE NEW.status_code = 'expired' AND NEW.updated_at_utc < NEW.valid_until_utc;
            END;

        CREATE TRIGGER trg_recurring_consent_leases_immutable_delete
            BEFORE DELETE ON recurring_consent_leases
            BEGIN
                SELECT RAISE(ABORT, 'recurring_consent_leases_are_immutable');
            END;

        CREATE TABLE recurring_lease_uses (
            use_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(use_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND use_id = trim(use_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND lease_id = trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND plan_id = trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            occurrence_identity TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_identity, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND occurrence_identity = trim(occurrence_identity, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            occurrence_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND occurrence_id = trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            run_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND run_id = trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            status_code TEXT NOT NULL COLLATE BINARY CHECK (status_code IN ('available', 'reserved', 'start_committed', 'consumed', 'settled', 'started_unknown')),
            reserved_use_count INTEGER NOT NULL CHECK (reserved_use_count BETWEEN 0 AND 1),
            reserved_duration_ticks INTEGER NOT NULL CHECK (reserved_duration_ticks >= 0),
            actual_settled_duration_ticks INTEGER NULL CHECK (actual_settled_duration_ticks IS NULL OR actual_settled_duration_ticks >= 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            PRIMARY KEY (use_id),
            UNIQUE (occurrence_identity),
            UNIQUE (run_id),
            FOREIGN KEY (lease_id, plan_id) REFERENCES recurring_consent_leases(lease_id, plan_id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_identity, plan_id, occurrence_id)
                REFERENCES recurring_occurrence_slots(occurrence_identity, plan_id, occurrence_id)
                ON DELETE RESTRICT,
            FOREIGN KEY (run_id, occurrence_id) REFERENCES recording_runs(id, occurrence_id) ON DELETE RESTRICT,
            CHECK ((status_code = 'available' AND reserved_use_count = 0 AND reserved_duration_ticks = 0 AND actual_settled_duration_ticks IS NULL)
                OR (status_code IN ('reserved', 'start_committed', 'consumed', 'started_unknown')
                    AND reserved_use_count = 1 AND reserved_duration_ticks > 0 AND actual_settled_duration_ticks IS NULL)
                OR (status_code = 'settled' AND reserved_use_count = 1 AND reserved_duration_ticks > 0
                    AND actual_settled_duration_ticks IS NOT NULL AND actual_settled_duration_ticks <= reserved_duration_ticks))
        );

        CREATE INDEX idx_recurring_lease_uses_lease_status_created
            ON recurring_lease_uses(lease_id, status_code, created_at_utc, use_id);

        CREATE TRIGGER trg_recurring_lease_uses_lifecycle_update
            BEFORE UPDATE ON recurring_lease_uses
            BEGIN
                SELECT RAISE(ABORT, 'recurring_lease_uses_identity_immutable')
                WHERE NEW.use_id IS NOT OLD.use_id OR NEW.lease_id IS NOT OLD.lease_id OR NEW.plan_id IS NOT OLD.plan_id
                   OR NEW.occurrence_identity IS NOT OLD.occurrence_identity OR NEW.occurrence_id IS NOT OLD.occurrence_id
                   OR NEW.run_id IS NOT OLD.run_id OR NEW.created_at_utc IS NOT OLD.created_at_utc;
                SELECT RAISE(ABORT, 'recurring_lease_uses_same_state_update_rejected') WHERE NEW.status_code = OLD.status_code;
                SELECT RAISE(ABORT, 'recurring_lease_uses_invalid_transition')
                WHERE NOT ((OLD.status_code = 'available' AND NEW.status_code = 'reserved')
                    OR (OLD.status_code = 'reserved' AND NEW.status_code IN ('available', 'start_committed'))
                    OR (OLD.status_code = 'start_committed' AND NEW.status_code IN ('consumed', 'started_unknown'))
                    OR (OLD.status_code = 'consumed' AND NEW.status_code IN ('settled', 'started_unknown')));
                SELECT RAISE(ABORT, 'recurring_lease_uses_version_or_time_not_monotonic')
                WHERE NEW.version <> OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
                SELECT RAISE(ABORT, 'recurring_lease_uses_reserved_to_available_requires_release')
                WHERE OLD.status_code = 'reserved' AND NEW.status_code = 'available'
                  AND (NEW.reserved_use_count <> 0 OR NEW.reserved_duration_ticks <> 0 OR NEW.actual_settled_duration_ticks IS NOT NULL);
                SELECT RAISE(ABORT, 'recurring_lease_uses_reservation_evidence_immutable')
                WHERE ((OLD.status_code = 'reserved' AND NEW.status_code = 'start_committed')
                    OR (OLD.status_code = 'start_committed' AND NEW.status_code IN ('consumed', 'started_unknown'))
                    OR (OLD.status_code = 'consumed' AND NEW.status_code = 'started_unknown'))
                  AND (NEW.reserved_use_count <> OLD.reserved_use_count
                    OR NEW.reserved_duration_ticks <> OLD.reserved_duration_ticks
                    OR NEW.actual_settled_duration_ticks IS NOT NULL);
                SELECT RAISE(ABORT, 'recurring_lease_uses_settlement_evidence_invalid')
                WHERE OLD.status_code = 'consumed' AND NEW.status_code = 'settled'
                  AND (NEW.reserved_use_count <> OLD.reserved_use_count
                    OR NEW.reserved_duration_ticks <> OLD.reserved_duration_ticks
                    OR NEW.actual_settled_duration_ticks IS NULL
                    OR NEW.actual_settled_duration_ticks > OLD.reserved_duration_ticks);
            END;

        CREATE TRIGGER trg_recurring_lease_uses_immutable_delete
            BEFORE DELETE ON recurring_lease_uses
            BEGIN
                SELECT RAISE(ABORT, 'recurring_lease_uses_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition CheckLease(string pattern) =>
        new("recurring_consent_leases", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteCheckConstraintDefinition CheckUse(string pattern) =>
        new("recurring_lease_uses", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition Trigger(string name, string operation, string tableName, params string[] requiredPatterns) =>
        new(
            name,
            tableName,
            requiredPatterns
                .Prepend($"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{operation}\\s+ON\\s+{Regex.Escape(tableName)}\\s+BEGIN\\b")
                .Append("END\\s*;?$")
                .Select(TriggerPattern)
                .ToArray());

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string tableName, string message) =>
        Trigger(
            name,
            "DELETE",
            tableName,
            $"SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'{Regex.Escape(message)}'\\s*\\)");

    private static Regex TriggerPattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v11 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
