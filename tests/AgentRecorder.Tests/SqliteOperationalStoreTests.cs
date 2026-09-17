using Microsoft.Data.Sqlite;
using Xunit;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class SqliteOperationalStoreTests
{
    [Fact]
    public void DefaultInitializationUsesDataDirStateDatabasePath()
    {
        using var dataDir = new TemporaryDirectory();
        DataDirResolver.SetOverride(dataDir.Path);
        try
        {
            var store = new SqliteOperationalStore();
            var expected = Path.Combine(dataDir.Path, "state", "agent-recorder.db");

            Assert.Equal(Path.GetFullPath(expected), store.DatabasePath);
            Assert.False(Directory.Exists(Path.Combine(dataDir.Path, "state")));

            store.Initialize();

            Assert.True(File.Exists(expected));
            Assert.True(Directory.Exists(Path.Combine(dataDir.Path, "state")));
            Assert.Equal(new[] { "state" }, Directory.GetDirectories(dataDir.Path).Select(Path.GetFileName));
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void SchemaV13ContainsTheMigrationTableAndRecurringExecutionSpecificationPersistenceTables()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();

        using var connection = database.Store.OpenConnection();
        Assert.Equal(
            new[] { "authorized_capture_scopes", "consent_leases", "lease_uses", "plan_occurrences", "plans", "recording_runs", "recurring_advancement_operations", "recurring_consent_leases", "recurring_fixed_region_profile_versions", "recurring_lease_local_approvals", "recurring_lease_uses", "recurring_occurrence_execution_specs", "recurring_occurrence_slots", "recurring_plan_profile_bindings", "recurring_schedule_cursors", "recurring_schedule_versions", "schema_migrations", "setup_intents", "standing_lease_safety_operations", "unattended_safety_state" },
            GetTables(connection).OrderBy(name => name));

        Assert.Equal(new[] { "version", "name", "definition_checksum", "applied_at_utc" }, GetColumnsInDeclarationOrder(connection, "schema_migrations"));
        Assert.Equal(new[] { "id", "is_one_time", "status_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "plans"));
        Assert.Equal(new[] { "id", "plan_id", "status_code", "window_start_utc", "window_end_utc", "run_id", "terminal_reason_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "plan_occurrences"));
        Assert.Equal(new[] { "id", "occurrence_id", "status_code", "has_crossed_start_commit", "media_artifact_id", "bundle_id", "terminal_reason_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "recording_runs"));
        Assert.Equal(new[] { "id", "plan_id", "occurrence_id", "status_code", "valid_from_utc", "valid_until_utc", "max_uses", "max_duration_ms", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "consent_leases"));
        Assert.Equal(new[] { "id", "lease_id", "occurrence_id", "run_id", "status_code", "reserved_use_count", "reserved_duration_ms", "actual_settled_duration_ms", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "lease_uses"));
        Assert.Equal(new[] { "plan_id", "profile_id", "profile_version", "profile_digest", "bound_at_utc" }, GetColumnsInDeclarationOrder(connection, "recurring_plan_profile_bindings"));
        Assert.Equal(new[] { "scope_id", "plan_id", "occurrence_id", "lease_id", "authorization_version", "created_at_utc", "scope_digest", "target_type_code", "capture_semantics_code", "coordinate_space_code", "display_identity_status_code", "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height", "region_x", "region_y", "region_width", "region_height", "dpi_x", "dpi_y", "physical_width", "physical_height", "orientation_code", "backend_code", "audio_mode_code", "reserved_duration_ms", "countdown_seconds", "output_directory", "frozen_file_name", "output_conflict_policy_code", "wake_policy_code", "desktop_requirement_code", "current_user_sid", "session_binding", "topology_digest" }, GetColumnsInDeclarationOrder(connection, "authorized_capture_scopes"));
        Assert.Equal(new[] { "intent_id", "intent_kind_code", "idempotency_key", "request_digest", "current_user_sid", "session_binding", "status_code", "requested_at_utc", "expires_at_utc", "plan_id", "occurrence_id", "lease_id", "scope_id", "created_at_utc", "updated_at_utc", "version", "terminal_reason_code", "scheduled_start_utc", "latest_start_utc", "planned_end_utc", "maximum_duration_ms", "lease_valid_until_utc", "output_directory", "frozen_file_name" }, GetColumnsInDeclarationOrder(connection, "setup_intents"));
        Assert.Equal(new[] { "state_id", "unattended_mode_code", "mode_changed_at_utc", "unattended_enabled_at_utc", "stop_all_applied", "stop_all_operation_id", "stop_all_reason_code", "stop_all_requested_at_utc", "stop_all_applied_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "unattended_safety_state"));
        Assert.Equal(new[] { "operation_id", "operation_kind_code", "intent_id", "lease_id", "requested_at_utc", "reason_code", "result_code", "changed", "requires_active_run_stop", "completed_at_utc" }, GetColumnsInDeclarationOrder(connection, "standing_lease_safety_operations"));
        Assert.Equal(new[] { "plan_id", "schedule_revision", "schedule_digest", "schedule_kind_code", "time_zone_id", "time_zone_rules_digest", "local_start_date", "local_end_date", "local_wall_clock_seconds", "weekday_mask", "maximum_occurrences", "recording_duration_ticks", "latest_start_grace_ticks", "created_at_utc" }, GetColumnsInDeclarationOrder(connection, "recurring_schedule_versions"));
        Assert.Equal(new[] { "occurrence_identity", "plan_id", "schedule_revision", "schedule_digest", "local_date", "local_wall_clock_seconds", "time_zone_id", "slot_status_code", "scheduled_start_utc", "latest_start_utc", "planned_end_utc", "resolution_code", "terminal_reason_code", "occurrence_id", "created_at_utc" }, GetColumnsInDeclarationOrder(connection, "recurring_occurrence_slots"));
        Assert.Equal(new[] { "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest", "initial_after_utc", "last_local_date", "last_schedule_ordinal", "is_exhausted", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "recurring_schedule_cursors"));
        Assert.Equal(new[] { "operation_id", "plan_id", "schedule_revision", "request_digest", "expected_cursor_version", "result_code", "occurrence_identity", "result_cursor_version", "created_at_utc" }, GetColumnsInDeclarationOrder(connection, "recurring_advancement_operations"));
        Assert.Equal(new[] { "profile_id", "profile_version", "profile_digest", "created_at_utc", "target_type_code", "rebind_policy_code", "capture_semantics_code", "coordinate_space_code", "display_identity_status_code", "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height", "region_x", "region_y", "region_width", "region_height", "dpi_x", "dpi_y", "physical_width", "physical_height", "orientation_code", "topology_digest", "backend_code", "audio_mode_code", "duration_ms", "countdown_seconds", "output_directory", "filename_prefix", "filename_template", "output_conflict_policy_code", "wake_policy_code", "desktop_requirement_code" }, GetColumnsInDeclarationOrder(connection, "recurring_fixed_region_profile_versions"));
        Assert.Equal(new[] { "lease_id", "plan_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest", "profile_id", "profile_version", "profile_digest", "configuration_digest", "status_code", "valid_from_utc", "valid_until_utc", "authorized_plan_latest_end_utc", "per_run_duration_ticks", "max_uses", "max_cumulative_duration_ticks", "authorization_digest", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "recurring_consent_leases"));
        Assert.Equal(new[] { "approval_id", "lease_id", "plan_id", "configuration_digest", "authorization_digest", "current_user_sid", "session_binding", "approved_at_utc", "approval_kind_code", "approval_version", "approval_digest" }, GetColumnsInDeclarationOrder(connection, "recurring_lease_local_approvals"));
        Assert.Equal(new[] { "use_id", "lease_id", "plan_id", "occurrence_identity", "occurrence_id", "run_id", "status_code", "reserved_use_count", "reserved_duration_ticks", "actual_settled_duration_ticks", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "recurring_lease_uses"));
        Assert.Equal(new[] { "occurrence_identity", "occurrence_id", "plan_id", "lease_id", "schedule_revision", "schedule_digest", "time_zone_rules_digest", "profile_id", "profile_version", "profile_digest", "configuration_digest", "lease_authorization_digest", "local_approval_id", "local_approval_digest", "scheduled_start_utc", "latest_start_utc", "planned_end_utc", "evaluated_at_utc", "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height", "region_x", "region_y", "region_width", "region_height", "virtual_region_x", "virtual_region_y", "virtual_region_width", "virtual_region_height", "dpi_x", "dpi_y", "physical_width", "physical_height", "orientation_code", "topology_digest", "backend_code", "audio_mode_code", "duration_ticks", "countdown_seconds", "normalized_output_directory", "frozen_output_file_name", "frozen_output_file_path", "output_conflict_policy_code", "approved_current_user_sid", "approved_session_binding", "specification_version", "specification_digest" }, GetColumnsInDeclarationOrder(connection, "recurring_occurrence_execution_specs"));

        foreach (var table in new[] { "schema_migrations", "plans", "plan_occurrences", "recording_runs", "consent_leases", "lease_uses", "authorized_capture_scopes", "setup_intents", "unattended_safety_state", "standing_lease_safety_operations", "recurring_schedule_versions", "recurring_occurrence_slots", "recurring_schedule_cursors", "recurring_advancement_operations", "recurring_fixed_region_profile_versions", "recurring_consent_leases", "recurring_lease_local_approvals", "recurring_lease_uses", "recurring_occurrence_execution_specs" })
        {
            Assert.Equal(1, GetPrimaryKeyOrdinal(connection, table, table switch
            {
                "schema_migrations" => "version",
                "authorized_capture_scopes" => "scope_id",
                "setup_intents" => "intent_id",
                "unattended_safety_state" => "state_id",
                "standing_lease_safety_operations" => "operation_id",
                "recurring_occurrence_slots" => "occurrence_identity",
                "recurring_schedule_versions" => "plan_id",
                "recurring_schedule_cursors" => "plan_id",
                "recurring_advancement_operations" => "operation_id",
                "recurring_fixed_region_profile_versions" => "profile_id",
                "recurring_consent_leases" => "lease_id",
                "recurring_lease_local_approvals" => "approval_id",
                "recurring_lease_uses" => "use_id",
                "recurring_occurrence_execution_specs" => "occurrence_identity",
                _ => "id",
            }));
        }

        foreach (var table in SqliteSchemaCatalog.Tables)
        {
            Assert.Equal(
                table.Columns.Select(column => (column.Name, column.DeclaredType, column.NotNull, column.PrimaryKeyPosition)),
                GetColumnMetadata(connection, table.Name));
        }

        Assert.Contains(GetForeignKeys(connection, "plan_occurrences"), fk => fk.Table == "plans" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "plan_occurrences"), fk => fk.Table == "recording_runs" && fk.From == "run_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recording_runs"), fk => fk.Table == "plan_occurrences" && fk.From == "occurrence_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "consent_leases"), fk => fk.Table == "plans" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "consent_leases"), fk => fk.Table == "plan_occurrences" && fk.From == "occurrence_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "lease_uses"), fk => fk.Table == "consent_leases" && fk.From == "lease_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "lease_uses"), fk => fk.Table == "recording_runs" && fk.From == "run_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "authorized_capture_scopes"), fk => fk.Table == "plans" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "authorized_capture_scopes"), fk => fk.Table == "plan_occurrences" && fk.From == "occurrence_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "authorized_capture_scopes"), fk => fk.Table == "consent_leases" && fk.From == "lease_id" && fk.OnDelete == "RESTRICT");

        Assert.Contains("idx_plan_occurrences_plan_window", GetExplicitIndexes(connection, "plan_occurrences"));
        Assert.Contains("idx_plan_occurrences_status_window", GetExplicitIndexes(connection, "plan_occurrences"));
        Assert.Contains("idx_recording_runs_status_updated", GetExplicitIndexes(connection, "recording_runs"));
        Assert.Contains("idx_consent_leases_plan_status", GetExplicitIndexes(connection, "consent_leases"));
        Assert.Contains("idx_consent_leases_occurrence_status", GetExplicitIndexes(connection, "consent_leases"));
        Assert.Contains("idx_lease_uses_lease_status", GetExplicitIndexes(connection, "lease_uses"));
        Assert.Contains("idx_lease_uses_occurrence_status", GetExplicitIndexes(connection, "lease_uses"));
        Assert.Contains("idx_authorized_capture_scopes_plan", GetExplicitIndexes(connection, "authorized_capture_scopes"));
        Assert.Contains("idx_authorized_capture_scopes_occurrence", GetExplicitIndexes(connection, "authorized_capture_scopes"));
        Assert.Contains("idx_authorized_capture_scopes_lease", GetExplicitIndexes(connection, "authorized_capture_scopes"));
        Assert.Contains("idx_setup_intents_status_expires", GetExplicitIndexes(connection, "setup_intents"));
        Assert.Contains("idx_standing_lease_safety_operations_kind_time", GetExplicitIndexes(connection, "standing_lease_safety_operations"));
        Assert.Contains("idx_standing_lease_safety_operations_lease_time", GetExplicitIndexes(connection, "standing_lease_safety_operations"));
        Assert.Contains("idx_standing_lease_safety_operations_intent_time", GetExplicitIndexes(connection, "standing_lease_safety_operations"));
        Assert.Contains("idx_recurring_occurrence_slots_plan_status_date", GetExplicitIndexes(connection, "recurring_occurrence_slots"));
        Assert.Contains("idx_recurring_occurrence_slots_occurrence", GetExplicitIndexes(connection, "recurring_occurrence_slots"));
        Assert.Contains("idx_recurring_schedule_cursors_plan_exhausted_updated", GetExplicitIndexes(connection, "recurring_schedule_cursors"));
        Assert.Contains("idx_recurring_advancement_operations_cursor", GetExplicitIndexes(connection, "recurring_advancement_operations"));
        Assert.Contains("idx_recurring_advancement_operations_occurrence", GetExplicitIndexes(connection, "recurring_advancement_operations"));
        Assert.Contains("ux_recurring_advancement_operations_cursor_transition", GetExplicitIndexes(connection, "recurring_advancement_operations"));
        Assert.Contains("WHERE result_cursor_version = expected_cursor_version + 1", GetIndexSql(connection, "ux_recurring_advancement_operations_cursor_transition"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ux_recurring_schedule_versions_exact_lease_parent", GetExplicitIndexes(connection, "recurring_schedule_versions"));
        Assert.Contains("ux_recurring_plan_profile_bindings_exact_lease_parent", GetExplicitIndexes(connection, "recurring_plan_profile_bindings"));
        Assert.Contains("ux_recurring_occurrence_slots_exact_lease_parent", GetExplicitIndexes(connection, "recurring_occurrence_slots"));
        Assert.Contains("idx_recurring_consent_leases_plan_status_valid_until", GetExplicitIndexes(connection, "recurring_consent_leases"));
        Assert.Contains("idx_recurring_lease_uses_lease_status_created", GetExplicitIndexes(connection, "recurring_lease_uses"));
        Assert.Contains("ux_recurring_lease_local_approvals_exact_spec_parent", GetExplicitIndexes(connection, "recurring_lease_local_approvals"));
        Assert.Contains(
            "approval_id, lease_id, plan_id, configuration_digest, authorization_digest, approval_digest, current_user_sid, session_binding",
            GetIndexSql(connection, "ux_recurring_lease_local_approvals_exact_spec_parent"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE (occurrence_id)", GetCreateSql(connection, "recurring_occurrence_execution_specs"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GetForeignKeys(connection, "recurring_schedule_versions"), fk => fk.Table == "plans" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_slots"), fk => fk.Table == "recurring_schedule_versions" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_slots"), fk => fk.Table == "plan_occurrences" && fk.From == "occurrence_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_schedule_cursors"), fk => fk.Table == "recurring_schedule_versions" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_advancement_operations"), fk => fk.Table == "recurring_schedule_cursors" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_advancement_operations"), fk => fk.Table == "recurring_occurrence_slots" && fk.From == "occurrence_identity" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_consent_leases"), fk => fk.Table == "recurring_schedule_versions" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_consent_leases"), fk => fk.Table == "recurring_plan_profile_bindings" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_lease_uses"), fk => fk.Table == "recurring_consent_leases" && fk.From == "lease_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_lease_uses"), fk => fk.Table == "recurring_occurrence_slots" && fk.From == "occurrence_identity" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_lease_uses"), fk => fk.Table == "recording_runs" && fk.From == "run_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_execution_specs"), fk => fk.Table == "recurring_occurrence_slots" && fk.From == "occurrence_identity" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_execution_specs"), fk => fk.Table == "recurring_schedule_versions" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_execution_specs"), fk => fk.Table == "recurring_plan_profile_bindings" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_execution_specs"), fk => fk.Table == "recurring_consent_leases" && fk.From == "lease_id" && fk.OnDelete == "RESTRICT");
        Assert.Contains(GetForeignKeys(connection, "recurring_occurrence_execution_specs"), fk => fk.Table == "recurring_lease_local_approvals" && fk.From == "local_approval_id" && fk.OnDelete == "RESTRICT");

        var occurrenceSql = GetCreateSql(connection, "plan_occurrences");
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", occurrenceSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE (run_id)", occurrenceSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE (occurrence_id)", GetCreateSql(connection, "recording_runs"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V13ShapeTamperingFailsClosedOnReinitialize()
    {
        var mutations = new Action<SqliteConnection>[]
        {
            connection => Execute(connection, "DROP TABLE recurring_occurrence_execution_specs;"),
            connection => Execute(connection, "DROP INDEX ux_recurring_lease_local_approvals_exact_spec_parent;"),
            connection =>
            {
                Execute(connection, "DROP INDEX ux_recurring_lease_local_approvals_exact_spec_parent;");
                Execute(connection, "CREATE UNIQUE INDEX ux_recurring_lease_local_approvals_exact_spec_parent ON recurring_lease_local_approvals(approval_id);");
            },
            connection => Execute(connection, "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update;"),
            connection =>
            {
                Execute(connection, "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update;");
                Execute(connection, "CREATE TRIGGER trg_recurring_occurrence_execution_specs_immutable_update BEFORE INSERT ON recurring_occurrence_execution_specs BEGIN SELECT RAISE(ABORT, 'wrong_trigger'); END;");
            },
        };

        foreach (var mutate in mutations)
        {
            using var database = new TemporaryDatabase();
            database.Store.Initialize();
            using (var connection = OpenRawConnection(database.Store.DatabasePath))
            {
                mutate(connection);
            }

            var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }
    }

    [Fact]
    public void MigrationsV1ThroughV13AreRecordedOnceAndRepeatedInitializationIsANoOp()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var first = ReadMigration(database.Store);
        var firstBytes = File.ReadAllBytes(database.Store.DatabasePath);

        database.Store.Initialize();
        var second = ReadMigration(database.Store);

        Assert.Equal(first, second);
        Assert.Equal(13, second.Count);
        Assert.Equal((1, "schema_v1_operational_domain"), (second[0].Version, second[0].Name));
        Assert.Equal((2, "schema_v2_authorized_capture_scopes"), (second[1].Version, second[1].Name));
        Assert.Equal(SqliteSchemaV1.Migrations.Single().Checksum, second[0].Checksum);
        Assert.Equal(SqliteSchemaV2.Migrations.Single().Checksum, second[1].Checksum);
        Assert.Equal((3, "schema_v3_setup_intents"), (second[2].Version, second[2].Name));
        Assert.Equal(SqliteSchemaV3.Migrations.Single().Checksum, second[2].Checksum);
        Assert.Equal((4, "schema_v4_setup_intent_request_metadata"), (second[3].Version, second[3].Name));
        Assert.Equal(SqliteSchemaV4.Migrations.Single().Checksum, second[3].Checksum);
        Assert.Equal((5, "schema_v5_standing_lease_safety_controls"), (second[4].Version, second[4].Name));
        Assert.Equal(SqliteSchemaV5.Migrations.Single().Checksum, second[4].Checksum);
        Assert.Equal((6, "schema_v6_unattended_default_disabled"), (second[5].Version, second[5].Name));
        Assert.Equal(SqliteSchemaV6.Migrations.Single().Checksum, second[5].Checksum);
        Assert.Equal((7, "schema_v7_recurring_schedule_versions_and_occurrence_slots"), (second[6].Version, second[6].Name));
        Assert.Equal(SqliteSchemaV7.Migrations.Single().Checksum, second[6].Checksum);
        Assert.Equal((8, "schema_v8_recurring_advancement_cursors_and_operations"), (second[7].Version, second[7].Name));
        Assert.Equal(SqliteSchemaV8.Migrations.Single().Checksum, second[7].Checksum);
        Assert.Equal((9, "schema_v9_recurring_fixed_region_profile_versions"), (second[8].Version, second[8].Name));
        Assert.Equal("c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a", second[8].Checksum);
        Assert.Equal((10, "schema_v10_recurring_plan_profile_bindings"), (second[9].Version, second[9].Name));
        Assert.Equal(SqliteSchemaV10.Migrations.Single().Checksum, second[9].Checksum);
        Assert.Equal((11, "schema_v11_recurring_consent_leases_and_uses"), (second[10].Version, second[10].Name));
        Assert.Equal(SqliteSchemaV11.Migrations.Single().Checksum, second[10].Checksum);
        Assert.Equal((12, "schema_v12_recurring_lease_local_approvals"), (second[11].Version, second[11].Name));
        Assert.Equal("5c2e13129873818b6b89f9005031850ed5daf172b41d2ec9a7c8f5e80913835b", second[11].Checksum);
        Assert.Equal((13, "schema_v13_recurring_occurrence_execution_specs"), (second[12].Version, second[12].Name));
        Assert.Equal(SqliteSchemaV13.Migrations.Single().Checksum, second[12].Checksum);
        using (var modeConnection = database.Store.OpenConnection())
            Assert.Equal("disabled", ScalarString(modeConnection, "SELECT unattended_mode_code FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(firstBytes, File.ReadAllBytes(database.Store.DatabasePath));
    }

    [Fact]
    public void V6DefaultOffMigrationPreservesAnExplicitEnabledSafetyChoice()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled', mode_changed_at_utc = 1234, unattended_enabled_at_utc = 1234, version = 1 WHERE state_id = 'global';");
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal("enabled", ScalarString(verification, "SELECT unattended_mode_code FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(1234L, ScalarInt64(verification, "SELECT mode_changed_at_utc FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT version FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public void RealPreV7FixtureUpgradesAdditivelyToV8AndPreservesEarlierRowsAndChecksums()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            InsertValidExecutionChain(connection);
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM plans WHERE id = 'plan-1';"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM plan_occurrences WHERE id = 'occ-1';"));
        Assert.Equal(SqliteSchemaV1.Migrations.Single().Checksum, ReadMigration(database.Store)[0].Checksum);
        Assert.Equal(SqliteSchemaV6.Migrations.Single().Checksum, ReadMigration(database.Store)[5].Checksum);
        Assert.Equal(SqliteSchemaV7.Migrations.Single().Checksum, ReadMigration(database.Store)[6].Checksum);
        Assert.Equal(SqliteSchemaV8.Migrations.Single().Checksum, ReadMigration(database.Store)[7].Checksum);
        Assert.Equal("c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a", ReadMigration(database.Store)[8].Checksum);
    }

    [Fact]
    public void RealV7FixturePreservesRecurringPlansSchedulesAndSlotsBeforeFirstV8Advance()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        var scheduledPlanId = "fixture-periodic";
        var skippedPlanId = "fixture-skipped";
        var scheduledSchedule = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            new TimeOnly(12, 0),
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5));
        var skippedSchedule = RecurringPlanSchedule.CreateDaily(
            GetDstZoneId(),
            new DateOnly(2026, 3, 8),
            new DateOnly(2026, 3, 9),
            new TimeOnly(2, 30),
            2,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
                SqliteSchemaV7.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            InsertRawRecurringFixture(connection, scheduledPlanId, scheduledSchedule, Utc(2025, 12, 31), includeScheduledSlot: true);
            InsertRawRecurringFixture(connection, skippedPlanId, skippedSchedule, Utc(2026, 1, 1), includeScheduledSlot: false);
        }

        database.Store.Initialize();

        using (var verification = database.Store.OpenConnection())
        {
            Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(2L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
            Assert.Equal(2L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
            Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_occurrence_slots WHERE slot_status_code = 'scheduled';"));
            Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_occurrence_slots WHERE slot_status_code = 'skipped';"));
            Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM plans WHERE id = 'fixture-periodic' AND is_one_time = 0 AND status_code = 'enabled';"));
            Assert.Equal(scheduledSchedule.CanonicalDigest, ScalarString(verification, "SELECT schedule_digest FROM recurring_schedule_versions WHERE plan_id = 'fixture-periodic' AND schedule_revision = 1;"));
            Assert.Equal(RecurringTimeZoneRulesDigest.Compute(scheduledSchedule.TimeZoneInfo), ScalarString(verification, "SELECT time_zone_rules_digest FROM recurring_schedule_versions WHERE plan_id = 'fixture-periodic' AND schedule_revision = 1;"));
            Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_occurrence_slots WHERE plan_id = 'fixture-periodic' AND slot_status_code = 'scheduled' AND occurrence_id = occurrence_identity;"));
            Assert.Equal(Utc(2026, 1, 1, 12).UtcDateTime.Ticks, ScalarInt64(verification, "SELECT window_start_utc FROM plan_occurrences WHERE plan_id = 'fixture-periodic';"));
            Assert.Equal(Utc(2026, 1, 1, 12, 1, 5).UtcDateTime.Ticks, ScalarInt64(verification, "SELECT window_end_utc FROM plan_occurrences WHERE plan_id = 'fixture-periodic';"));
            Assert.Equal("skipped", ScalarString(verification, "SELECT slot_status_code FROM recurring_occurrence_slots WHERE plan_id = 'fixture-skipped';"));
            Assert.Equal("schedule_local_time_invalid", ScalarString(verification, "SELECT terminal_reason_code FROM recurring_occurrence_slots WHERE plan_id = 'fixture-skipped';"));
            Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
            Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
            Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM consent_leases;"));
            Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM lease_uses;"));
            Assert.Contains(GetForeignKeys(verification, "recurring_occurrence_slots"), fk => fk.Table == "recurring_schedule_versions" && fk.From == "plan_id" && fk.OnDelete == "RESTRICT");
            Assert.Contains(GetForeignKeys(verification, "recurring_occurrence_slots"), fk => fk.Table == "plan_occurrences" && fk.From == "occurrence_id" && fk.OnDelete == "RESTRICT");
        }

        var advancement = new SqliteRecurringAdvancementTransaction(database.Store);
        var first = advancement.AdvanceOne(
            scheduledPlanId,
            1,
            "fixture-first",
            0,
            Utc(2025, 12, 31),
            Utc(2026, 1, 1));
        Assert.Equal("scheduled", first.ResultCode);
        Assert.Equal(1L, advancement.GetCursor(scheduledPlanId, 1).Version);
        using (var verification = database.Store.OpenConnection())
        {
            Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        }
    }

    [Fact]
    public void FailedV7MigrationRollsBackItsNewTableAndHistory()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "CREATE TABLE recurring_occurrence_slots (id TEXT);");
        }

        Assert.Equal("sqlite_migration_failed", Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize()).Code);
        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal(6L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.False(TableExists(verification, "recurring_schedule_versions"));
        Assert.True(TableExists(verification, "recurring_occurrence_slots"));
    }

    [Fact]
    public void FailedV8MigrationRollsBackBothNewTablesAndHistory()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
                SqliteSchemaV7.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "CREATE TABLE recurring_schedule_cursors (id TEXT);");
        }

        Assert.Equal("sqlite_migration_failed", Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize()).Code);
        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal(7L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.True(TableExists(verification, "recurring_schedule_cursors"));
        Assert.False(TableExists(verification, "recurring_advancement_operations"));
    }

    [Fact]
    public void RealV8FixtureUpgradesAdditivelyToV9PreservingOneShotAndRecurringRows()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        var preservedTables = new[]
        {
            "plans", "plan_occurrences", "recording_runs", "consent_leases", "lease_uses",
            "authorized_capture_scopes", "recurring_schedule_versions", "recurring_occurrence_slots",
            "recurring_schedule_cursors", "recurring_advancement_operations",
        };
        Dictionary<string, List<string>> beforeRows;
        List<(int Version, string Name, string Checksum, long AppliedAtUtc)> beforeHistory;
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
                SqliteSchemaV7.Migrations.Single(),
                SqliteSchemaV8.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('fixture-one-shot', 1, 'completed', 1000, 2500, 2);");
            Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('fixture-recurring', 0, 'enabled', 1000, 1000, 1);");
            Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ('fixture-one-shot-occurrence', 'fixture-one-shot', 'completed', 1000, 10000000000, NULL, 'completed', 1000, 2500, 2);");
            Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ('fixture-one-shot-run', 'fixture-one-shot-occurrence', 'settled', 1, 'artifact-1', 'bundle-1', 'completed', 2000, 2500, 2);");
            Execute(connection, "UPDATE plan_occurrences SET run_id = 'fixture-one-shot-run' WHERE id = 'fixture-one-shot-occurrence';");
            Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('fixture-one-shot-lease', 'fixture-one-shot', 'fixture-one-shot-occurrence', 'exhausted', 1000, 10000000000, 1, 60000, 2500, 2);");
            Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('fixture-one-shot-use', 'fixture-one-shot-lease', 'fixture-one-shot-occurrence', 'fixture-one-shot-run', 'settled', 1, 60000, 60000, 2000, 2500, 2);");
            Execute(connection, "INSERT INTO authorized_capture_scopes(scope_id, plan_id, occurrence_id, lease_id, authorization_version, created_at_utc, scope_digest, target_type_code, capture_semantics_code, coordinate_space_code, display_identity_status_code, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, backend_code, audio_mode_code, reserved_duration_ms, countdown_seconds, output_directory, frozen_file_name, output_conflict_policy_code, wake_policy_code, desktop_requirement_code, current_user_sid, session_binding, topology_digest) VALUES ('fixture-one-shot-scope', 'fixture-one-shot', 'fixture-one-shot-occurrence', 'fixture-one-shot-lease', 1, 1500, 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 'fixed_region', 'desktop_region', 'physical_virtual_screen', 'resolved', 'fixture-display', -100, 50, 1920, 1080, 10, 20, 640, 480, 96, 144, 1920, 1080, 'landscape', 'ffmpeg-region', 'none', 60000, 3, 'C:\\task254\\fixture-output', 'fixture-one-shot.mp4', 'fail_if_exists', 'natural_wake_only', 'interactive_desktop_required', 'S-1-5-21-fixture', 'fixture-session', 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd');");
            Execute(connection, "INSERT INTO recurring_schedule_versions(plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('fixture-recurring', 1, 'recurring-schedule/v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'daily', 'UTC', 'recurring-timezone-rules/v1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 0, 1000);");
            Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ('fixture-recurring-occurrence', 'fixture-recurring', 'authorized', 864000000000, 900000000000, NULL, NULL, 1000, 1000, 0);");
            Execute(connection, "INSERT INTO recurring_occurrence_slots(occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ('recurring-occurrence/v1:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'fixture-recurring', 1, 'recurring-schedule/v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '2026-01-01', 43200, 'UTC', 'scheduled', 864000000000, 864000000000, 900000000000, 'schedule_exact', NULL, 'fixture-recurring-occurrence', 1000);");
            Execute(connection, "INSERT INTO recurring_schedule_cursors(plan_id, schedule_revision, schedule_digest, time_zone_rules_digest, initial_after_utc, last_local_date, last_schedule_ordinal, is_exhausted, created_at_utc, updated_at_utc, version) VALUES ('fixture-recurring', 1, 'recurring-schedule/v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'recurring-timezone-rules/v1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 1000, '2026-01-01', 1, 0, 1000, 1000, 1);");
            Execute(connection, "INSERT INTO recurring_advancement_operations(operation_id, plan_id, schedule_revision, request_digest, expected_cursor_version, result_code, occurrence_identity, result_cursor_version, created_at_utc) VALUES ('fixture-advancement-1', 'fixture-recurring', 1, 'recurring-advancement/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff', 0, 'scheduled', 'recurring-occurrence/v1:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 1, 1000);");

            beforeRows = preservedTables.ToDictionary(table => table, table => ReadCanonicalRows(connection, table));
            beforeHistory = ReadMigration(connection);
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_consent_leases;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_lease_uses;"));
        foreach (var table in preservedTables)
        {
            Assert.Equal(beforeRows[table], ReadCanonicalRows(verification, table));
        }

        var afterHistory = ReadMigration(database.Store);
        Assert.Equal(beforeHistory, afterHistory.Take(8));
        Assert.Equal("schema_v9_recurring_fixed_region_profile_versions", afterHistory[8].Name);
        Assert.Equal("c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a", afterHistory[8].Checksum);
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));

        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profileDirectory = Path.Combine(Path.GetTempPath(), "task254-fixture-profile");
        var profileV1Spec = new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion,
            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            "fixture-profile-display",
            new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96, 144, 1920, 1080, AuthorizedDisplayOrientation.Landscape,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
            TimeSpan.FromMinutes(2), 3, profileDirectory, "fixture-区域",
            AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);
        var profileV1 = RecurringFixedRegionProfileVersion.CreateVersion1(
            "fixture-profile",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            profileV1Spec);
        var persistedV1 = repository.CreateVersion1(profileV1);
        var profileV2Spec = new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion,
            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            "fixture-profile-display",
            new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96, 144, 1920, 1080, AuthorizedDisplayOrientation.Landscape,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
            TimeSpan.FromMinutes(3), 4, profileDirectory,
            "fixture-区域", AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly, AuthorizedDesktopRequirement.InteractiveDesktopRequired);
        var persistedV2 = repository.CreateNext(profileV1.Reference, profileV2Spec,
            new DateTimeOffset(2026, 9, 1, 10, 1, 0, TimeSpan.Zero));
        Assert.Equal(persistedV1.Reference, repository.Get(persistedV1.Reference).Reference);
        Assert.Equal(persistedV2.Reference, repository.GetLatest("fixture-profile").Reference);

        var restartedStore = new SqliteOperationalStore(database.Store.DatabasePath);
        restartedStore.Initialize();
        var restartedRepository = new SqliteRecurringFixedRegionProfileRepository(restartedStore);
        Assert.Equal(persistedV1.Reference, restartedRepository.Get("fixture-profile", 1).Reference);
        Assert.Equal(persistedV2.Reference, restartedRepository.Get(persistedV2.Reference).Reference);
        Assert.Equal(new[] { 2L, 1L }, restartedRepository.ListVersions("fixture-profile").Select(profile => profile.ProfileVersion));
    }

    [Fact]
    public void FailedV9MigrationRollsBackItsHistoryAndDoesNotPublishAProfileTable()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
                SqliteSchemaV7.Migrations.Single(),
                SqliteSchemaV8.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "CREATE TABLE recurring_fixed_region_profile_versions (blocker TEXT);");
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_migration_failed", exception.Code);

        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal(8L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations WHERE version = 9;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recurring_fixed_region_profile_versions';"));
    }

    [Fact]
    public async Task ConcurrentInitializationProducesOneCompleteMigrationHistory()
    {
        using var database = new TemporaryDatabase();
        var first = new SqliteOperationalStore(database.Store.DatabasePath);
        var second = new SqliteOperationalStore(database.Store.DatabasePath);

        await Task.WhenAll(Task.Run(first.Initialize), Task.Run(second.Initialize));

        using var connection = database.Store.OpenConnection();
        Assert.Equal(13, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(19, ScalarInt64(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name <> 'schema_migrations';"));
        Assert.Contains("lease_uses", GetTables(connection));
    }

    [Fact]
    public void V1DatabaseUpgradesToV3PreservingRowsWithoutImplicitSetupIntent()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            InsertValidExecutionChain(connection);
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM plans WHERE id = 'plan-1' AND is_one_time = 1;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM consent_leases WHERE id = 'lease-1' AND max_uses = 1;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
    }

    [Fact]
    public void V2DatabaseUpgradesToV3PreservingTheAuthorizationScopeTable()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            var migration = SqliteSchemaV2.Migrations.Single();
            Execute(connection, migration.Definition);
            Execute(
                connection,
                "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                ("$version", migration.Version),
                ("$name", migration.Name),
                ("$checksum", migration.Checksum),
                ("$appliedAtUtc", 2000L));
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal(13L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'authorized_capture_scopes';"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'setup_intents';"));
    }

    [Fact]
    public void MissingV2CheckConstraintFailsClosedDuringInitialization()
    {
        AssertV2SchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "backend_code TEXT NOT NULL CHECK (backend_code = 'ffmpeg-region'),",
            "backend_code TEXT NOT NULL,"));
    }

    [Fact]
    public void RewrittenV2ForeignKeyFailsClosedDuringInitialization()
    {
        AssertV2SchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "FOREIGN KEY (lease_id, occurrence_id) REFERENCES consent_leases(id, occurrence_id) ON DELETE RESTRICT",
            "FOREIGN KEY (lease_id, occurrence_id) REFERENCES consent_leases(id, occurrence_id) ON DELETE CASCADE"));
    }

    [Fact]
    public void RewrittenV2IndexFailsClosedDuringInitialization()
    {
        AssertV2SchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "ON authorized_capture_scopes(plan_id);",
            "ON authorized_capture_scopes(occurrence_id);"));
    }

    [Fact]
    public void ExtraV2ColumnFailsClosedDuringInitialization()
    {
        AssertV2SchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),",
            "legacy_column TEXT,\n            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),"));
    }

    [Fact]
    public void FailedV2MigrationRollsBackItsHistoryAndLeavesNoHalfCreatedScopeTable()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(connection, "CREATE TABLE authorized_capture_scopes (scope_id TEXT);");
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());

        Assert.Equal("sqlite_migration_failed", exception.Code);
        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'authorized_capture_scopes';"));
    }

    [Fact]
    public void NewConnectionsApplyForeignKeysWalBusyTimeoutAndSynchronousPolicy()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();

        using var connection = database.Store.OpenConnection();
        Assert.Equal(1, ScalarInt64(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;"), ignoreCase: true);
        Assert.Equal(SqliteOperationalStore.BusyTimeoutMilliseconds, ScalarInt64(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1, ScalarInt64(connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public void BusinessConnectionRejectsAnUninitializedDatabaseWithoutSwitchingToWal()
    {
        using var database = new TemporaryDatabase(createDatabase: false);

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.OpenConnection());

        Assert.Equal("sqlite_not_initialized", exception.Code);
        using var raw = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal("delete", ScalarString(raw, "PRAGMA journal_mode;"), ignoreCase: true);
    }

    [Fact]
    public void BusinessConnectionDoesNotRepairAJournalModePolicyMismatch()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using (var raw = OpenRawConnection(database.Store.DatabasePath))
        {
            Assert.Equal("delete", ScalarString(raw, "PRAGMA journal_mode = DELETE;"), ignoreCase: true);
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.OpenConnection());

        Assert.Equal("sqlite_journal_mode_mismatch", exception.Code);
        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.Equal("delete", ScalarString(verification, "PRAGMA journal_mode;"), ignoreCase: true);
    }

    [Fact]
    public void MigrationChecksumCanonicalizesLfAndCrlfDefinitions()
    {
        const string definition = "CREATE TABLE sample (id INTEGER);\nCREATE INDEX sample_id ON sample(id);\n";
        var crlf = definition.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(definition, SqliteSchemaV1.CanonicalizeDefinition(crlf));
        Assert.Equal(SqliteSchemaV1.ComputeChecksum(definition), SqliteSchemaV1.ComputeChecksum(crlf));
        var migration = SqliteSchemaV1.Migrations.Single();
        Assert.Equal(migration.Definition, SqliteSchemaV1.CanonicalizeDefinition(migration.Definition));
        Assert.Equal(migration.Checksum, SqliteSchemaV1.ComputeChecksum(migration.Definition));
    }

    [Fact]
    public void MigrationNamesAndChecksumsAreFixedIndependentContracts()
    {
        var expected = new[]
        {
            (1, "schema_v1_operational_domain", "8e01a478ab50d7d8e4a96e0419241e2cb7c13d1cfdb1e682092d1d7db47785ec"),
            (2, "schema_v2_authorized_capture_scopes", "bb2630d19c8f61f0334647a723228761347b2bd7259956464deaf81bb3157050"),
            (3, "schema_v3_setup_intents", "17ac809145a36207f628b63599bf52e898d32b08fe853f9614a0eac569314a76"),
            (4, "schema_v4_setup_intent_request_metadata", "ed52963c9a0c6f1f767a7631a19c281ea9f9d15f7a2c205eadf33d8cc90373cc"),
            (5, "schema_v5_standing_lease_safety_controls", "f140e79ad3d9928e78be8972a3de40f0af118ebd5cedfee03b24655f471db438"),
            (6, "schema_v6_unattended_default_disabled", "8ee413c6f9f9b2e90f78cd93a85ef3198e4c2242c5287ccd3b2a10de5771c0d5"),
            (7, "schema_v7_recurring_schedule_versions_and_occurrence_slots", "a72670309ef1060205dd5c6da82115a31755b6d016bb0d707b7da120be86b013"),
            (8, "schema_v8_recurring_advancement_cursors_and_operations", "79a65a6f87c07070f1c7d05530ee0621465882f0758c9890f8c25958e6fcd6b0"),
            (9, "schema_v9_recurring_fixed_region_profile_versions", "c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a"),
            (10, "schema_v10_recurring_plan_profile_bindings", "3ed33a8d8176dc56b35fa035f45a5bc2dd6ef95c70979065b73a399adee4e727"),
            (11, "schema_v11_recurring_consent_leases_and_uses", "846f8e7d6775648674ad327fad040b7cec73cc1948f837bf81ec1fc5b9e3e6be"),
            (12, "schema_v12_recurring_lease_local_approvals", "5c2e13129873818b6b89f9005031850ed5daf172b41d2ec9a7c8f5e80913835b"),
            (13, "schema_v13_recurring_occurrence_execution_specs", "ae54bbc7e8b873823cbba54e812f93dd50aaf954e329ef048104b029a8bd319f"),
        };

        var actual = SqliteSchemaCatalog.Migrations
            .Select(migration => (migration.Version, migration.Name, migration.Checksum))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WrongIndexTableFailsClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "ON plan_occurrences(plan_id, window_start_utc, window_end_utc);",
            "ON plans(id);"));
    }

    [Fact]
    public void WrongIndexColumnsFailClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "ON plan_occurrences(plan_id, window_start_utc, window_end_utc);",
            "ON plan_occurrences(window_start_utc, plan_id, window_end_utc);"));
    }

    [Fact]
    public void RewrittenV8TransitionIndexPredicateFailsClosedDuringInitialization()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(connection, "DROP INDEX ux_recurring_advancement_operations_cursor_transition;");
            Execute(connection, "CREATE UNIQUE INDEX ux_recurring_advancement_operations_cursor_transition ON recurring_advancement_operations(plan_id, schedule_revision, expected_cursor_version) WHERE result_cursor_version = expected_cursor_version;");
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    [Fact]
    public void RewrittenForeignKeyFailsClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT",
            "FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE CASCADE",
            expectedCount: 2));
    }

    [Fact]
    public void MissingForeignKeyFailsClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,\n",
            string.Empty,
            expectedCount: 2));
    }

    [Fact]
    public void MissingDeferredForeignKeyClauseFailsClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "DEFERRABLE INITIALLY DEFERRED",
            string.Empty,
            expectedCount: 2));
    }

    [Fact]
    public void MissingNotNullPrimaryKeyAndUniqueConstraintsFailClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),",
            "id TEXT CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),",
            expectedCount: 5));

        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "UNIQUE (run_id),\n",
            string.Empty));
    }

    [Fact]
    public void ExtraColumnFailsClosedDuringInitialization()
    {
        AssertSchemaTamperRejected(definition => ReplaceExactly(
            definition,
            "version INTEGER NOT NULL CHECK (version >= 0)\n);\n\nCREATE TABLE plan_occurrences",
            "version INTEGER NOT NULL CHECK (version >= 0),\nlegacy_column TEXT\n);\n\nCREATE TABLE plan_occurrences"));
    }

    [Fact]
    public void OrphanInsertedWithForeignKeysDisabledFailsClosedOnReinitialize()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using (var raw = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(raw, "PRAGMA foreign_keys = OFF;");
            Execute(raw, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('orphan', 'missing-plan', 'scheduled', 1000, 2000, 1000, 1000, 0);");
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());

        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    [Fact]
    public void ForeignKeysAndBasicChecksRejectInvalidRows()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var connection = database.Store.OpenConnection();
        InsertValidExecutionChain(connection);

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('orphan', 'missing-plan', 'scheduled', 1000, 2000, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('negative-version', 1, 'draft', 1000, 1000, -1);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('invalid-bool', 2, 'draft', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('inverted-window', 'plan-1', 'scheduled', 2000, 1000, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('zero-uses', 'plan-1', 'occ-1', 'pending', 1000, 2000, 0, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('zero-duration', 'plan-1', 'occ-1', 'pending', 1000, 2000, 1, 0, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('negative-count', 'lease-1', 'occ-1', 'run-1', 'available', -1, 0, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('negative-duration', 'lease-1', 'occ-1', 'run-1', 'available', 0, -1, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('negative-actual', 'lease-1', 'occ-1', 'run-1', 'available', 0, 0, -1, 1000, 1000, 0);"));
    }

    [Fact]
    public void SchemaChecksRejectEmptyWhitespaceAndNegativeUtcValues()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var connection = database.Store.OpenConnection();
        InsertValidExecutionChain(connection);

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('', 1, 'draft', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('   ', 1, 'draft', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('plan-empty-status', 1, '', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('plan-empty-status', 1, '   ', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('plan-negative-time', 1, 'draft', -1, 1000, 0);"));

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('', 'plan-1', 'scheduled', 1000, 2000, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('occ-empty-plan', '   ', 'scheduled', 1000, 2000, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('occ-empty-status', 'plan-1', '', 1000, 2000, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, run_id, created_at_utc, updated_at_utc, version) VALUES ('occ-empty-run', 'plan-1', 'scheduled', 1000, 2000, '   ', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ('occ-empty-reason', 'plan-1', 'scheduled', 1000, 2000, '\t', 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('occ-negative-time', 'plan-1', 'scheduled', -1, 2000, 1000, 1000, 0);"));

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, created_at_utc, updated_at_utc, version) VALUES ('run-empty-occ', '   ', 'created', 0, ' ', 1000, 1000, 0);"));
        InsertPlanAndOccurrence(connection, "plan-media", "occ-media");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, created_at_utc, updated_at_utc, version) VALUES ('run-empty-media', 'occ-media', 'created', 0, ' ', 1000, 1000, 0);"));
        InsertPlanAndOccurrence(connection, "plan-bundle", "occ-bundle");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, bundle_id, created_at_utc, updated_at_utc, version) VALUES ('run-empty-bundle', 'occ-bundle', 'created', 0, '\t', 1000, 1000, 0);"));
        InsertPlanAndOccurrence(connection, "plan-reason", "occ-reason");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ('run-empty-reason', 'occ-reason', 'created', 0, '   ', 1000, 1000, 0);"));
        InsertPlanAndOccurrence(connection, "plan-run-status", "occ-run-status");
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, created_at_utc, updated_at_utc, version) VALUES ('run-empty-status', 'occ-run-status', '', 0, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, created_at_utc, updated_at_utc, version) VALUES ('run-negative-time', 'occ-1', 'created', 0, -1, 1000, 0);"));

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('lease-empty-plan', '   ', 'occ-1', 'pending', 1000, 2000, 1, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('lease-empty-status', 'plan-1', 'occ-1', '   ', 1000, 2000, 1, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('lease-negative-time', 'plan-1', 'occ-1', 'pending', -1, 2000, 1, 1000, 1000, 0);"));

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('use-empty-lease', '   ', 'occ-1', 'run-1', 'available', 0, 0, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('use-empty-status', 'lease-1', 'occ-1', 'run-1', '   ', 0, 0, 1000, 1000, 0);"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('use-negative-time', 'lease-1', 'occ-1', 'run-1', 'available', 0, 0, -1, 1000, 0);"));

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES (2, 'migration-2', '0000000000000000000000000000000000000000000000000000000000000000', -1);"));
    }

    [Fact]
    public void V2ScopeChecksAndCompositeRelationsRejectInvalidRows()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var connection = database.Store.OpenConnection();
        InsertValidExecutionChain(connection);

        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-version", authorizationVersion: 2));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-digest", scopeDigest: "ABC"));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-region", regionWidth: 1911));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-duration", durationMilliseconds: 600001));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-backend", backendCode: "wgc"));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-file", frozenFileName: "nested/capture.mp4"));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-resolution", physicalWidth: 1919));
        Assert.Throws<SqliteException>(() => InsertRawAuthorizedScope(connection, "scope-relation", planId: "missing-plan"));
    }

    [Fact]
    public void ForeignKeyDeletesAreRestrictedRatherThanCascaded()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var connection = database.Store.OpenConnection();
        InsertValidExecutionChain(connection);
        Execute(connection, "INSERT INTO lease_uses(id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, created_at_utc, updated_at_utc, version) VALUES ('use-1', 'lease-1', 'occ-1', 'run-1', 'available', 0, 0, 1000, 1000, 0);");

        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM plans WHERE id = 'plan-1';"));
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM consent_leases WHERE id = 'lease-1';"));
        Assert.True(TableExists(connection, "plans"));
        Assert.Equal(1, ScalarInt64(connection, "SELECT COUNT(*) FROM lease_uses WHERE id = 'use-1';"));
    }

    [Fact]
    public void FailedMigrationRollsBackItsDdlAndDoesNotWriteHistory()
    {
        using var database = new TemporaryDatabase();
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(connection, """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
                    name TEXT NOT NULL UNIQUE CHECK (length(trim(name)) > 0),
                    definition_checksum TEXT NOT NULL CHECK (length(definition_checksum) = 64),
                    applied_at_utc INTEGER NOT NULL CHECK (applied_at_utc >= 0)
                );
                CREATE TABLE plan_occurrences (id TEXT NOT NULL PRIMARY KEY);
                """);
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_migration_failed", exception.Code);

        using var verification = OpenRawConnection(database.Store.DatabasePath);
        Assert.False(TableExists(verification, "plans"));
        Assert.Equal(0, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.True(TableExists(verification, "plan_occurrences"));
    }

    [Fact]
    public void ChecksumMismatchFailsClosed()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "UPDATE schema_migrations SET definition_checksum = $checksum WHERE version = 1;", ("$checksum", new string('0', 64)));
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_migration_checksum_mismatch", exception.Code);
    }

    [Fact]
    public void FutureSchemaVersionFailsClosed()
    {
        using var database = new TemporaryDatabase();
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(connection, """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
                    name TEXT NOT NULL UNIQUE CHECK (length(trim(name)) > 0),
                    definition_checksum TEXT NOT NULL CHECK (length(definition_checksum) = 64),
                    applied_at_utc INTEGER NOT NULL CHECK (applied_at_utc >= 0)
                );
                INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc)
                VALUES (99, 'future', '0000000000000000000000000000000000000000000000000000000000000000', 1000);
                """);
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_future_schema", exception.Code);
    }

    [Fact]
    public void CorruptDatabaseFailsClosedWithoutChangingOriginalBytes()
    {
        using var database = new TemporaryDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.Store.DatabasePath)!);
        var original = new byte[] { 0x41, 0x67, 0x65, 0x6E, 0x74, 0x00, 0xFF, 0x12, 0x34 };
        File.WriteAllBytes(database.Store.DatabasePath, original);

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());

        Assert.Equal("sqlite_corrupt", exception.Code);
        Assert.Equal(original, File.ReadAllBytes(database.Store.DatabasePath));
    }

    [Fact]
    public void InitializationCreatesNoMediaLogsReadyFilesOrRecordingArtifacts()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();

        var files = Directory.GetFiles(database.RootPath, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        Assert.DoesNotContain(files, name => name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name.Contains("ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Directory.GetDirectories(database.RootPath, "*", SearchOption.AllDirectories), path =>
            string.Equals(Path.GetFileName(path), "Videos", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(path), "logs", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSchemaTamperRejected(Func<string, string> mutate)
    {
        using var database = new TemporaryDatabase();
        var definition = SqliteSchemaV1.Migrations.Single().Definition;
        CreatePreloadedSchema(database.Store, mutate(definition));

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());

        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    private static void AssertV2SchemaTamperRejected(Func<string, string> mutate)
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            Execute(connection, mutate(SqliteSchemaV2.Migrations.Single().Definition));
            var migration = SqliteSchemaV2.Migrations.Single();
            Execute(
                connection,
                "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                ("$version", migration.Version),
                ("$name", migration.Name),
                ("$checksum", migration.Checksum),
                ("$appliedAtUtc", 2000L));
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());

        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    private static void CreatePreloadedSchema(SqliteOperationalStore store, string definition)
    {
        using var connection = OpenRawConnection(store.DatabasePath);
        Execute(connection, definition);
        var migration = SqliteSchemaV1.Migrations.Single();
        Execute(
            connection,
            "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
            ("$version", migration.Version),
            ("$name", migration.Name),
            ("$checksum", migration.Checksum),
            ("$appliedAtUtc", 1000L));
    }

    private static string ReplaceExactly(string source, string oldValue, string newValue, int expectedCount = 1)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(oldValue, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += oldValue.Length;
        }

        Assert.Equal(expectedCount, count);
        return source.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static SqliteConnection OpenRawConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static List<(string Table, string From, string To, string OnDelete)> GetForeignKeys(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({table});";
        using var reader = command.ExecuteReader();
        var result = new List<(string Table, string From, string To, string OnDelete)>();
        while (reader.Read())
        {
            result.Add((reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(6)));
        }

        return result;
    }

    private static HashSet<string> GetTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static List<string> GetColumnsInDeclarationOrder(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static List<(string Name, string DeclaredType, bool NotNull, int PrimaryKeyPosition)> GetColumnMetadata(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var result = new List<(string Name, string DeclaredType, bool NotNull, int PrimaryKeyPosition)>();
        while (reader.Read())
        {
            result.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, reader.GetInt32(5)));
        }

        return result;
    }

    private static int GetPrimaryKeyOrdinal(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return reader.GetInt32(5);
            }
        }

        return 0;
    }

    private static HashSet<string> GetExplicitIndexes(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table AND name NOT LIKE 'sqlite_autoindex%';";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static string GetCreateSql(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    private static string GetIndexSql(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $index;";
        command.Parameters.AddWithValue("$index", index);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    private static List<(int Version, string Name, string Checksum, long AppliedAtUtc)> ReadMigration(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        return ReadMigration(connection);
    }

    private static List<(int Version, string Name, string Checksum, long AppliedAtUtc)> ReadMigration(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, definition_checksum, applied_at_utc FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        var result = new List<(int Version, string Name, string Checksum, long AppliedAtUtc)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return result;
    }

    private static List<string> ReadCanonicalRows(SqliteConnection connection, string table)
    {
        var columns = GetColumnsInDeclarationOrder(connection, table);
        var quotedColumns = string.Join(", ", columns.Select(column => $"quote(\"{column}\")"));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {quotedColumns} FROM \"{table}\" ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetString(index))));
        }

        return rows;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection connection, string sql, bool ignoreCase = false)
    {
        var value = Convert.ToString(ExecuteScalar(connection, sql), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return ignoreCase ? value.ToLowerInvariant() : value;
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void InsertValidExecutionChain(SqliteConnection connection)
    {
        Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('plan-1', 1, 'draft', 1000, 1000, 0);");
        Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('occ-1', 'plan-1', 'scheduled', 1000, 2000, 1000, 1000, 0);");
        Execute(connection, "INSERT INTO recording_runs(id, occurrence_id, status_code, has_crossed_start_commit, created_at_utc, updated_at_utc, version) VALUES ('run-1', 'occ-1', 'created', 0, 1000, 1000, 0);");
        Execute(connection, "UPDATE plan_occurrences SET run_id = 'run-1' WHERE id = 'occ-1';");
        Execute(connection, "INSERT INTO consent_leases(id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version) VALUES ('lease-1', 'plan-1', 'occ-1', 'pending', 1000, 2000, 1, 1000, 1000, 0);");
    }

    private static void InsertRawRecurringFixture(
        SqliteConnection connection,
        string planId,
        RecurringPlanSchedule schedule,
        DateTimeOffset afterUtc,
        bool includeScheduledSlot)
    {
        var createdAtTicks = Utc(2026, 1, 1).UtcDateTime.Ticks;
        var scheduleDigest = schedule.CanonicalDigest;
        var rulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(planId, 1, schedule, afterUtc).Candidate
            ?? throw new InvalidOperationException("The v7 fixture schedule did not produce its required candidate.");

        Execute(
            connection,
            "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ($planId, 0, 'enabled', $createdAt, $updatedAt, 1);",
            ("$planId", planId), ("$createdAt", createdAtTicks), ("$updatedAt", createdAtTicks));
        Execute(
            connection,
            """
            INSERT INTO recurring_schedule_versions
                (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id,
                 time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds,
                 weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks,
                 created_at_utc)
            VALUES
                ($planId, 1, $scheduleDigest, $kind, $timeZoneId, $rulesDigest, $startDate, $endDate,
                 $wallClockSeconds, $weekdayMask, $maximumOccurrences, $durationTicks, $graceTicks, $createdAt);
            """,
            ("$planId", planId),
            ("$scheduleDigest", scheduleDigest),
            ("$kind", schedule.Kind == RecurringScheduleKind.Daily ? "daily" : "weekly"),
            ("$timeZoneId", schedule.TimeZoneId),
            ("$rulesDigest", rulesDigest),
            ("$startDate", schedule.LocalStartDate.ToString("yyyy-MM-dd")),
            ("$endDate", schedule.LocalEndDate.ToString("yyyy-MM-dd")),
            ("$wallClockSeconds", schedule.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond),
            ("$weekdayMask", schedule.IsWeekly ? schedule.WeeklyDays.Aggregate(0, (mask, day) => mask | (1 << (int)day)) : 0),
            ("$maximumOccurrences", schedule.MaximumOccurrences),
            ("$durationTicks", schedule.RecordingDuration.Ticks),
            ("$graceTicks", schedule.LatestStartGrace.Ticks),
            ("$createdAt", createdAtTicks));

        if (includeScheduledSlot)
        {
            Assert.NotNull(candidate.ScheduledStartUtc);
            Assert.NotNull(candidate.PlannedEndUtc);
            var occurrenceIdentity = candidate.Identity.Value;
            Execute(
                connection,
                "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ($occurrenceId, $planId, 'scheduled', $windowStart, $windowEnd, $createdAt, $createdAt, 0);",
                ("$occurrenceId", occurrenceIdentity),
                ("$planId", planId),
                ("$windowStart", candidate.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
                ("$windowEnd", candidate.PlannedEndUtc!.Value.UtcDateTime.Ticks),
                ("$createdAt", createdAtTicks));
        }

        Execute(
            connection,
            """
            INSERT INTO recurring_occurrence_slots
                (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date,
                 local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc,
                 latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code,
                 occurrence_id, created_at_utc)
            VALUES
                ($occurrenceIdentity, $planId, 1, $scheduleDigest, $localDate, $wallClockSeconds,
                 $timeZoneId, $status, $scheduledStart, $latestStart, $plannedEnd, $resolution,
                 $terminalReason, $occurrenceId, $createdAt);
            """,
            ("$occurrenceIdentity", candidate.Identity.Value),
            ("$planId", planId),
            ("$scheduleDigest", scheduleDigest),
            ("$localDate", candidate.LocalDate.ToString("yyyy-MM-dd")),
            ("$wallClockSeconds", schedule.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond),
            ("$timeZoneId", schedule.TimeZoneId),
            ("$status", candidate.IsValid ? "scheduled" : "skipped"),
            ("$scheduledStart", (object?)candidate.ScheduledStartUtc?.UtcDateTime.Ticks ?? DBNull.Value),
            ("$latestStart", (object?)candidate.LatestStartUtc?.UtcDateTime.Ticks ?? DBNull.Value),
            ("$plannedEnd", (object?)candidate.PlannedEndUtc?.UtcDateTime.Ticks ?? DBNull.Value),
            ("$resolution", candidate.ResolutionCode),
            ("$terminalReason", candidate.IsSkipped ? candidate.ReasonCode : DBNull.Value),
            ("$occurrenceId", candidate.IsValid ? candidate.Identity.Value : DBNull.Value),
            ("$createdAt", createdAtTicks));
    }

    private static string GetDstZoneId()
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return "Pacific Standard Time";
        }
        catch (TimeZoneNotFoundException)
        {
            Assert.Fail("The Windows DST test zone is unavailable.");
            return string.Empty;
        }
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static void InsertPlanAndOccurrence(SqliteConnection connection, string planId, string occurrenceId)
    {
        Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ($planId, 1, 'draft', 1000, 1000, 0);", ("$planId", planId));
        Execute(connection, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ($occurrenceId, $planId, 'scheduled', 1000, 2000, 1000, 1000, 0);", ("$occurrenceId", occurrenceId), ("$planId", planId));
    }

    private static void InsertRawAuthorizedScope(
        SqliteConnection connection,
        string scopeId,
        string planId = "plan-1",
        string occurrenceId = "occ-1",
        string leaseId = "lease-1",
        int authorizationVersion = 1,
        string scopeDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string backendCode = "ffmpeg-region",
        int regionWidth = 640,
        long durationMilliseconds = 1000,
        string frozenFileName = "capture.mp4",
        int physicalWidth = 1920) =>
        Execute(
            connection,
            "INSERT INTO authorized_capture_scopes (scope_id, plan_id, occurrence_id, lease_id, authorization_version, created_at_utc, scope_digest, target_type_code, capture_semantics_code, coordinate_space_code, display_identity_status_code, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, backend_code, audio_mode_code, reserved_duration_ms, countdown_seconds, output_directory, frozen_file_name, output_conflict_policy_code, wake_policy_code, desktop_requirement_code, current_user_sid, session_binding, topology_digest) VALUES ($scopeId, $planId, $occurrenceId, $leaseId, $authorizationVersion, 1500, $scopeDigest, 'fixed_region', 'desktop_region', 'physical_virtual_screen', 'resolved', 'display-fingerprint-1', 100, -50, 1920, 1080, 10, 200, $regionWidth, 480, 96, 96, $physicalWidth, 1080, 'landscape', $backendCode, 'none', $durationMilliseconds, 0, 'C:\\captures\\', $frozenFileName, 'fail_if_exists', 'natural_wake_only', 'interactive_desktop_required', 'S-1-5-21-1', 'session-1', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');",
            ("$scopeId", scopeId), ("$planId", planId), ("$occurrenceId", occurrenceId), ("$leaseId", leaseId),
            ("$authorizationVersion", authorizationVersion), ("$scopeDigest", scopeDigest), ("$regionWidth", regionWidth),
            ("$physicalWidth", physicalWidth), ("$backendCode", backendCode), ("$durationMilliseconds", durationMilliseconds),
            ("$frozenFileName", frozenFileName));

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public TemporaryDatabase(bool createDatabase = true)
        {
            Store = new SqliteOperationalStore(Path.Combine(_directory.Path, "state", "agent-recorder.db"));
            if (createDatabase)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            }
        }

        public SqliteOperationalStore Store { get; }

        public string RootPath => _directory.Path;

        public void Dispose() => _directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentRecorderSqliteTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
