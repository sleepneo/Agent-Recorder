using Microsoft.Data.Sqlite;
using Xunit;
using AgentRecorder.Infrastructure;

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
    public void SchemaV2ContainsTheMigrationTableAndSixOperationalTables()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();

        using var connection = database.Store.OpenConnection();
        Assert.Equal(
            new[] { "authorized_capture_scopes", "consent_leases", "lease_uses", "plan_occurrences", "plans", "recording_runs", "schema_migrations" },
            GetTables(connection).OrderBy(name => name));

        Assert.Equal(new[] { "version", "name", "definition_checksum", "applied_at_utc" }, GetColumnsInDeclarationOrder(connection, "schema_migrations"));
        Assert.Equal(new[] { "id", "is_one_time", "status_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "plans"));
        Assert.Equal(new[] { "id", "plan_id", "status_code", "window_start_utc", "window_end_utc", "run_id", "terminal_reason_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "plan_occurrences"));
        Assert.Equal(new[] { "id", "occurrence_id", "status_code", "has_crossed_start_commit", "media_artifact_id", "bundle_id", "terminal_reason_code", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "recording_runs"));
        Assert.Equal(new[] { "id", "plan_id", "occurrence_id", "status_code", "valid_from_utc", "valid_until_utc", "max_uses", "max_duration_ms", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "consent_leases"));
        Assert.Equal(new[] { "id", "lease_id", "occurrence_id", "run_id", "status_code", "reserved_use_count", "reserved_duration_ms", "actual_settled_duration_ms", "created_at_utc", "updated_at_utc", "version" }, GetColumnsInDeclarationOrder(connection, "lease_uses"));
        Assert.Equal(new[] { "scope_id", "plan_id", "occurrence_id", "lease_id", "authorization_version", "created_at_utc", "scope_digest", "target_type_code", "capture_semantics_code", "coordinate_space_code", "display_identity_status_code", "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height", "region_x", "region_y", "region_width", "region_height", "dpi_x", "dpi_y", "physical_width", "physical_height", "orientation_code", "backend_code", "audio_mode_code", "reserved_duration_ms", "countdown_seconds", "output_directory", "frozen_file_name", "output_conflict_policy_code", "wake_policy_code", "desktop_requirement_code", "current_user_sid", "session_binding", "topology_digest" }, GetColumnsInDeclarationOrder(connection, "authorized_capture_scopes"));

        foreach (var table in new[] { "schema_migrations", "plans", "plan_occurrences", "recording_runs", "consent_leases", "lease_uses", "authorized_capture_scopes" })
        {
            Assert.Equal(1, GetPrimaryKeyOrdinal(connection, table, table switch
            {
                "schema_migrations" => "version",
                "authorized_capture_scopes" => "scope_id",
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

        var occurrenceSql = GetCreateSql(connection, "plan_occurrences");
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", occurrenceSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE (run_id)", occurrenceSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE (occurrence_id)", GetCreateSql(connection, "recording_runs"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationsV1AndV2AreRecordedOnceAndRepeatedInitializationIsANoOp()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var first = ReadMigration(database.Store);
        var firstBytes = File.ReadAllBytes(database.Store.DatabasePath);

        database.Store.Initialize();
        var second = ReadMigration(database.Store);

        Assert.Equal(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal((1, "schema_v1_operational_domain"), (second[0].Version, second[0].Name));
        Assert.Equal((2, "schema_v2_authorized_capture_scopes"), (second[1].Version, second[1].Name));
        Assert.Equal(SqliteSchemaV1.Migrations.Single().Checksum, second[0].Checksum);
        Assert.Equal(SqliteSchemaV2.Migrations.Single().Checksum, second[1].Checksum);
        Assert.Equal(firstBytes, File.ReadAllBytes(database.Store.DatabasePath));
    }

    [Fact]
    public async Task ConcurrentInitializationProducesOneCompleteMigrationHistory()
    {
        using var database = new TemporaryDatabase();
        var first = new SqliteOperationalStore(database.Store.DatabasePath);
        var second = new SqliteOperationalStore(database.Store.DatabasePath);

        await Task.WhenAll(Task.Run(first.Initialize), Task.Run(second.Initialize));

        using var connection = database.Store.OpenConnection();
        Assert.Equal(2, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(6, ScalarInt64(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name <> 'schema_migrations';"));
        Assert.Contains("lease_uses", GetTables(connection));
    }

    [Fact]
    public void V1DatabaseUpgradesToV2PreservingRowsWithoutImplicitAuthorizationScope()
    {
        using var database = new TemporaryDatabase();
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRawConnection(database.Store.DatabasePath))
        {
            InsertValidExecutionChain(connection);
        }

        database.Store.Initialize();

        using var verification = database.Store.OpenConnection();
        Assert.Equal(2L, ScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM plans WHERE id = 'plan-1' AND is_one_time = 1;"));
        Assert.Equal(1L, ScalarInt64(verification, "SELECT COUNT(*) FROM consent_leases WHERE id = 'lease-1' AND max_uses = 1;"));
        Assert.Equal(0L, ScalarInt64(verification, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
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

    private static List<(int Version, string Name, string Checksum, long AppliedAtUtc)> ReadMigration(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
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
