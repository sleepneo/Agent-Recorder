using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringFixedRegionProfilePersistenceTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private const string ProfileDigestPrefix = "recurring-fixed-region-profile/v1:";
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] ProfileColumns =
    {
        "profile_id", "profile_version", "profile_digest", "created_at_utc",
        "target_type_code", "rebind_policy_code", "capture_semantics_code", "coordinate_space_code", "display_identity_status_code",
        "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height",
        "region_x", "region_y", "region_width", "region_height", "dpi_x", "dpi_y", "physical_width", "physical_height",
        "orientation_code", "topology_digest", "backend_code", "audio_mode_code", "duration_ms", "countdown_seconds",
        "output_directory", "filename_prefix", "filename_template", "output_conflict_policy_code", "wake_policy_code", "desktop_requirement_code",
    };

    private static readonly string[] BusinessTables =
    {
        "plans", "plan_occurrences", "recording_runs", "consent_leases", "lease_uses", "authorized_capture_scopes",
        "setup_intents", "unattended_safety_state", "standing_lease_safety_operations", "recurring_schedule_versions",
        "recurring_occurrence_slots", "recurring_schedule_cursors", "recurring_advancement_operations",
        "recurring_occurrence_execution_specs",
    };

    [Fact]
    public void FreshSchemaV13HasAnEmptyImmutableProfileTableAndFixedMigrationEvidence()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();

        Assert.Equal(13L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("schema_v9_recurring_fixed_region_profile_versions", ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a", ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT definition_checksum FROM schema_migrations WHERE version = 9;"));
        Assert.Equal("schema_v10_recurring_plan_profile_bindings", ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("3ed33a8d8176dc56b35fa035f45a5bc2dd6ef95c70979065b73a399adee4e727", ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT definition_checksum FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("schema_v13_recurring_occurrence_execution_specs", ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT name FROM schema_migrations WHERE version = 13;"));
        Assert.Equal(SqliteSchemaV13.MigrationChecksum, ScalarString(OpenRaw(database.Store.DatabasePath), "SELECT definition_checksum FROM schema_migrations WHERE version = 13;"));
        Assert.Equal(0L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(1L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_recurring_fixed_region_profile_versions_profile_version_desc';"));
        Assert.Equal(2L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'recurring_fixed_region_profile_versions';"));
    }

    [Fact]
    public void CrudReadsAlwaysRehydrateAndListIsStableDescending()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var first = CreateProfile("crud-profile");

        var persistedFirst = repository.CreateVersion1(first);
        var replayedFirst = repository.CreateVersion1(first);
        var second = repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3)), CreatedAt.AddMinutes(1));

        Assert.Equal(first.Reference, persistedFirst.Reference);
        Assert.Equal(first.Reference, replayedFirst.Reference);
        Assert.Equal(second.Reference, repository.Get(second.Reference).Reference);
        Assert.Equal(second.Reference, repository.GetLatest(first.ProfileId).Reference);
        Assert.Equal(new[] { 2L, 1L }, repository.ListVersions(first.ProfileId).Select(profile => profile.ProfileVersion));
        Assert.Equal(new[] { 1L }, repository.ListVersions(first.ProfileId, beforeVersionExclusive: 2).Select(profile => profile.ProfileVersion));
        Assert.Equal(2L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Fact]
    public void VersionOneRoundTripsEveryFieldAndRestartPreservesDigestLatestPaginationAndReplay()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "agent-recorder-task254-roundtrip", Guid.NewGuid().ToString("N"), "..", "captures");
        var versionOne = RecurringFixedRegionProfileVersion.CreateVersion1("roundtrip-profile", CreatedAt, CreateSpec(outputDirectory: outputDirectory, filenamePrefix: "中文 prefix"));
        var persistedOne = repository.CreateVersion1(versionOne);
        AssertProfileEqual(versionOne, repository.Get(persistedOne.Reference));
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agent-recorder-task254-roundtrip", "captures")) + Path.DirectorySeparatorChar, persistedOne.OutputDirectory);
        Assert.False(Directory.Exists(persistedOne.OutputDirectory));

        var versionTwoSpec = CreateSpec(duration: TimeSpan.FromMinutes(3), outputDirectory: persistedOne.OutputDirectory, filenamePrefix: persistedOne.FilenamePrefix, countdownSeconds: 4);
        var persistedTwo = repository.CreateNext(persistedOne.Reference, versionTwoSpec, CreatedAt.AddMinutes(1));
        Assert.Equal(persistedTwo.Reference, repository.CreateNext(persistedOne.Reference, versionTwoSpec, CreatedAt.AddMinutes(1)).Reference);
        Assert.Equal(new[] { 2L, 1L }, repository.ListVersions("roundtrip-profile").Select(profile => profile.ProfileVersion));

        var restartedStore = new SqliteOperationalStore(database.Store.DatabasePath);
        restartedStore.Initialize();
        var restarted = new SqliteRecurringFixedRegionProfileRepository(restartedStore);
        AssertProfileEqual(persistedOne, restarted.Get("roundtrip-profile", 1));
        AssertProfileEqual(persistedTwo, restarted.Get(persistedTwo.Reference));
        AssertProfileEqual(persistedTwo, restarted.GetLatest("roundtrip-profile"));
        Assert.Equal(new[] { 2L }, restarted.ListVersions("roundtrip-profile", beforeVersionExclusive: 3, limit: 1).Select(profile => profile.ProfileVersion));
        Assert.Equal(new[] { 1L }, restarted.ListVersions("roundtrip-profile", beforeVersionExclusive: 2, limit: 100).Select(profile => profile.ProfileVersion));
        Assert.False(Directory.Exists(persistedOne.OutputDirectory));
    }

    [Fact]
    public void VersionOneExactReplayDifferentImmutableContentAndVersionTwoEntryAreStable()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var versionOne = CreateProfile("version-one-entry");

        Assert.Equal(versionOne.Reference, repository.CreateVersion1(versionOne).Reference);
        Assert.Equal(versionOne.Reference, repository.CreateVersion1(versionOne).Reference);
        Assert.Equal(
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
            Assert.Throws<Phase3PersistenceException>(() => repository.CreateVersion1(CreateProfileWithSpec("version-one-entry", CreateSpec(duration: TimeSpan.FromMinutes(3))))).Code);

        var versionTwo = RecurringFixedRegionProfileVersion.CreateVersionForTests("version-two-entry", 2, CreatedAt, CreateSpec());
        Assert.Equal(
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
            Assert.Throws<Phase3PersistenceException>(() => repository.CreateVersion1(versionTwo)).Code);
        Assert.Equal(new[] { 1L }, repository.ListVersions("version-one-entry").Select(profile => profile.ProfileVersion));
    }

    [Fact]
    public void ProfileReferencesCoverNotFoundMismatchStaleFutureDefaultAndIdentityErrors()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var first = CreateProfile("reference-matrix");
        repository.CreateVersion1(first);
        var second = repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3)), CreatedAt.AddMinutes(1));
        var third = repository.CreateNext(second.Reference, CreateSpec(duration: TimeSpan.FromMinutes(5)), CreatedAt.AddMinutes(2));

        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound, () => repository.Get("missing", 1));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound, () => repository.GetLatest("missing"));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound, () => repository.Get(new ProfileRef("missing", 1, first.ProfileDigest)));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.Get(new ProfileRef(first.ProfileId, 1, ProfileDigestPrefix + new string('0', 64))));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.Get(first.ProfileId, 0));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.CreateNext(default, CreateSpec(), CreatedAt));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.CreateNext(new ProfileRef(first.ProfileId, 1, ProfileDigestPrefix + new string('0', 64)), CreateSpec(), CreatedAt));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionStale, () => repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(4)), CreatedAt.AddMinutes(3)));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound, () => repository.CreateNext(new ProfileRef(first.ProfileId, 4, ProfileDigestPrefix + new string('1', 64)), CreateSpec(), CreatedAt.AddMinutes(3)));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound, () => repository.CreateNext(new ProfileRef("other-profile", 1, first.ProfileDigest), CreateSpec(), CreatedAt.AddMinutes(3)));
        Assert.Equal(third.Reference, repository.GetLatest(first.ProfileId).Reference);
    }

    [Fact]
    public void ListVersionsSupportsStableDescendingPaginationAndPageLimits()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var first = CreateProfile("pagination-profile");
        repository.CreateVersion1(first);
        var second = repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3)), CreatedAt.AddMinutes(1));
        var third = repository.CreateNext(second.Reference, CreateSpec(duration: TimeSpan.FromMinutes(4)), CreatedAt.AddMinutes(2));

        Assert.Equal(new[] { 3L }, repository.ListVersions(first.ProfileId, limit: 1).Select(profile => profile.ProfileVersion));
        Assert.Equal(new[] { 2L }, repository.ListVersions(first.ProfileId, beforeVersionExclusive: 3, limit: 1).Select(profile => profile.ProfileVersion));
        Assert.Equal(new[] { 1L }, repository.ListVersions(first.ProfileId, beforeVersionExclusive: 2, limit: 1).Select(profile => profile.ProfileVersion));
        Assert.Equal(new[] { 3L, 2L, 1L }, repository.ListVersions(first.ProfileId, limit: 100).Select(profile => profile.ProfileVersion));
        Assert.Equal(third.Reference, repository.GetLatest(first.ProfileId).Reference);
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileListLimitInvalid, () => repository.ListVersions(first.ProfileId, limit: 0));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileListLimitInvalid, () => repository.ListVersions(first.ProfileId, limit: 101));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.ListVersions(first.ProfileId, beforeVersionExclusive: 0));
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, () => repository.ListVersions(first.ProfileId, beforeVersionExclusive: -1));
    }

    [Fact]
    public void DefaultReferencesAndInvalidListLimitsAreRejectedWithStableCodes()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var exception = Assert.Throws<Phase3PersistenceException>(() => repository.CreateNext(default, CreateSpec(), CreatedAt));
        Assert.Equal(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch, exception.Code);

        Assert.Equal(
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileListLimitInvalid,
            Assert.Throws<Phase3PersistenceException>(() => repository.ListVersions("profile", limit: 0)).Code);
        Assert.Equal(
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileListLimitInvalid,
            Assert.Throws<Phase3PersistenceException>(() => repository.ListVersions("profile", limit: 101)).Code);
    }

    [Fact]
    public void DifferentNextRequestConflictsAndDoesNotCreateARevisionHole()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var first = CreateProfile("conflict-profile");
        repository.CreateVersion1(first);
        var winner = repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3)), CreatedAt.AddMinutes(1));

        var exception = Assert.Throws<Phase3PersistenceException>(() =>
            repository.CreateNext(first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(4)), CreatedAt.AddMinutes(1)));

        Assert.Equal(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict, exception.Code);
        Assert.Equal(2L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT MAX(profile_version) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(winner.ProfileDigest, repository.GetLatest(first.ProfileId).ProfileDigest);
    }

    [Fact]
    public async Task ConcurrentIdenticalNextRequestsConvergeOnOneVersionWithoutV3()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var firstRepository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var secondRepository = new SqliteRecurringFixedRegionProfileRepository(new SqliteOperationalStore(database.Store.DatabasePath));
        var first = CreateProfile("concurrent-profile");
        firstRepository.CreateVersion1(first);
        var specification = CreateSpec(duration: TimeSpan.FromMinutes(3));

        var results = await Task.WhenAll(
            Task.Run(() => firstRepository.CreateNext(first.Reference, specification, CreatedAt.AddMinutes(1))),
            Task.Run(() => secondRepository.CreateNext(first.Reference, specification, CreatedAt.AddMinutes(1))));

        Assert.Equal(results[0].Reference, results[1].Reference);
        Assert.Equal(new[] { 2L, 1L }, firstRepository.ListVersions(first.ProfileId).Select(profile => profile.ProfileVersion));
        Assert.Equal(2L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Fact]
    public async Task ConcurrentDifferentNextRequestsHaveOneWinnerAndNoRevisionHole()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var firstStore = database.Store;
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var firstRepository = new SqliteRecurringFixedRegionProfileRepository(firstStore);
        var secondRepository = new SqliteRecurringFixedRegionProfileRepository(secondStore);
        var first = CreateProfile("different-concurrency-profile");
        firstRepository.CreateVersion1(first);

        var attempts = await Task.WhenAll(
            Task.Run(() => TryCreateNext(firstRepository, first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3)), CreatedAt.AddMinutes(1))),
            Task.Run(() => TryCreateNext(secondRepository, first.Reference, CreateSpec(duration: TimeSpan.FromMinutes(4)), CreatedAt.AddMinutes(1))));

        Assert.Single(attempts.Where(attempt => attempt.Profile is not null));
        var loser = Assert.Single(attempts.Where(attempt => attempt.Exception is not null));
        Assert.Contains(loser.Exception!.Code, new[]
        {
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
            RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionStale,
        });
        var winner = attempts.Single(attempt => attempt.Profile is not null).Profile!;
        Assert.Equal(2L, winner.ProfileVersion);
        Assert.Equal(new[] { 2L, 1L }, firstRepository.ListVersions(first.ProfileId).Select(profile => profile.ProfileVersion));
        Assert.Equal(winner.Reference, firstRepository.GetLatest(first.ProfileId).Reference);
        Assert.Equal(winner.Reference, secondRepository.GetLatest(first.ProfileId).Reference);
        Assert.Equal(2L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Fact]
    public void RawChecksAndImmutableTriggersRejectInvalidWrites()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = CreateProfile("raw-check-profile");
        repository.CreateVersion1(profile);

        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE recurring_fixed_region_profile_versions SET countdown_seconds = 4 WHERE profile_id = $id;", ("$id", profile.ProfileId)));
            Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM recurring_fixed_region_profile_versions WHERE profile_id = $id;", ("$id", profile.ProfileId)));
            Assert.Throws<SqliteException>(() => Execute(connection, $"INSERT INTO recurring_fixed_region_profile_versions SELECT 'invalid-check', profile_version, '{ProfileDigestPrefix}{new string('f', 64)}', created_at_utc, target_type_code, rebind_policy_code, capture_semantics_code, coordinate_space_code, display_identity_status_code, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, topology_digest, backend_code, audio_mode_code, duration_ms, 11, output_directory, filename_prefix, filename_template, output_conflict_policy_code, wake_policy_code, desktop_requirement_code FROM recurring_fixed_region_profile_versions WHERE profile_id = $id;", ("$id", profile.ProfileId)));
        }

        Assert.Equal(profile.Reference, repository.Get(profile.Reference).Reference);
    }

    [Theory]
    [InlineData("target_type_code", "'window'")]
    [InlineData("rebind_policy_code", "'allow_rebind'")]
    [InlineData("profile_digest", "'bad-digest'")]
    [InlineData("topology_digest", "upper(topology_digest)")]
    [InlineData("display_bounds_width", "2147483648")]
    [InlineData("region_x", "-1")]
    [InlineData("region_width", "1921")]
    [InlineData("duration_ms", "0")]
    [InlineData("duration_ms", "600001")]
    [InlineData("countdown_seconds", "-1")]
    [InlineData("countdown_seconds", "11")]
    [InlineData("filename_template", "'tampered.mp4'")]
    [InlineData("output_directory", "''")]
    public void RawInsertConstraintMatrixRejectsInvalidProfileShape(string column, string expression)
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = CreateProfile("raw-matrix-profile");
        repository.CreateVersion1(profile);
        using var connection = OpenRaw(database.Store.DatabasePath);
        var columns = ProfileColumns;
        var values = columns.Select(current => current switch
        {
            "profile_id" => "'raw-matrix-" + column + "'",
            "profile_digest" when column != "profile_digest" => $"'{ProfileDigestPrefix}{new string('e', 64)}'",
            _ when current == column => expression,
            _ => current,
        });
        var sql = $"INSERT INTO recurring_fixed_region_profile_versions ({string.Join(", ", columns)}) SELECT {string.Join(", ", values)} FROM recurring_fixed_region_profile_versions WHERE profile_id = $id;";
        Assert.Throws<SqliteException>(() => Execute(connection, sql, ("$id", profile.ProfileId)));
        Assert.Equal(profile.Reference, repository.Get(profile.Reference).Reference);
    }

    [Fact]
    public void RehydrateRejectsPersistedDigestTamperingAfterDirectTriggerBypass()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = CreateProfile("tamper-profile");
        repository.CreateVersion1(profile);

        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update;");
            Execute(connection, $"UPDATE recurring_fixed_region_profile_versions SET profile_digest = '{ProfileDigestPrefix}{new string('0', 64)}' WHERE profile_id = $id;", ("$id", profile.ProfileId));
            Execute(connection, "CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update BEFORE UPDATE ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => repository.Get(profile.ProfileId, 1));
        Assert.Equal(RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid, exception.Code);
    }

    [Theory]
    [InlineData("profile_digest", "'recurring-fixed-region-profile/v1:0000000000000000000000000000000000000000000000000000000000000000'")]
    [InlineData("filename_template", "'tampered.mp4'")]
    [InlineData("target_type_code", "'window'")]
    [InlineData("region_x", "-1")]
    [InlineData("duration_ms", "0")]
    [InlineData("output_directory", "'relative-output'")]
    [InlineData("topology_digest", "'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'")]
    public void RehydrateRejectsEachIndependentTriggerBypassedTamper(string column, string value)
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = CreateProfile("tamper-matrix-" + column);
        repository.CreateVersion1(profile);
        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update;");
            Execute(connection, "PRAGMA ignore_check_constraints = ON;");
            Execute(connection, $"UPDATE recurring_fixed_region_profile_versions SET {column} = {value} WHERE profile_id = $id;", ("$id", profile.ProfileId));
            Execute(connection, "CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update BEFORE UPDATE ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => repository.Get(profile.ProfileId, 1));
        Assert.Equal(RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid, exception.Code);
    }

    [Fact]
    public void InsertFailureRollsBackWithoutLeavingAPartialProfile()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "CREATE TRIGGER task254_fail_profile_insert BEFORE INSERT ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'task254 injected failure'); END;");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => repository.CreateVersion1(CreateProfile("rollback-profile")));
        Assert.Equal(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed, exception.Code);
        Assert.Equal(0L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));

        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "DROP TRIGGER task254_fail_profile_insert;");
        }

        var retried = repository.CreateVersion1(CreateProfile("rollback-profile"));
        Assert.Equal(1L, retried.ProfileVersion);
        Assert.Equal(1L, Scalar(OpenRaw(database.Store.DatabasePath), "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Fact]
    public void ProfileOperationsDoNotTouchBusinessTablesForeignKeysOrTheFilesystem()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var before = SnapshotBusinessTables(database.Store.DatabasePath);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "agent-recorder-task254-side-effect", Guid.NewGuid().ToString("N"));
        var repository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var first = CreateProfileWithSpec("side-effect-profile", CreateSpec(outputDirectory: outputDirectory));
        var persisted = repository.CreateVersion1(first);
        _ = repository.CreateVersion1(first);
        var second = repository.CreateNext(persisted.Reference, CreateSpec(duration: TimeSpan.FromMinutes(3), outputDirectory: outputDirectory), CreatedAt.AddMinutes(1));
        _ = repository.Get(persisted.Reference);
        _ = repository.GetLatest(first.ProfileId);
        _ = repository.ListVersions(first.ProfileId, limit: 100);
        AssertCode(RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict, () => repository.CreateNext(persisted.Reference, CreateSpec(duration: TimeSpan.FromMinutes(4), outputDirectory: outputDirectory), CreatedAt.AddMinutes(1)));
        Assert.Equal(2L, second.ProfileVersion);
        AssertSnapshotsEqual(before, SnapshotBusinessTables(database.Store.DatabasePath));
        Assert.False(Directory.Exists(outputDirectory));

        using var connection = OpenRaw(database.Store.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('recurring_fixed_region_profile_versions');";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RemovingOrWeakeningRequiredTriggerFailsClosedDuringInitialization()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_delete;");
        }

        var missing = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_corrupt", missing.Code);

        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_delete AFTER DELETE ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        }

        var weak = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_corrupt", weak.Code);
    }

    [Fact]
    public void MissingAfterWrongTargetWrongOperationAndWrongRaiseTriggersFailClosed()
    {
        AssertTriggerShapeRejected("DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update;");
        AssertTriggerShapeRejected("DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update AFTER UPDATE ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        AssertTriggerShapeRejected("DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update BEFORE UPDATE ON plans BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        AssertTriggerShapeRejected("DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update BEFORE INSERT ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable'); END;");
        AssertTriggerShapeRejected("DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update BEFORE UPDATE ON recurring_fixed_region_profile_versions BEGIN SELECT RAISE(ABORT, 'wrong_message'); END;");
    }

    private static RecurringFixedRegionProfileVersion CreateProfile(string profileId) =>
        CreateProfileWithSpec(profileId, CreateSpec());

    private static RecurringFixedRegionProfileVersion CreateProfileWithSpec(string profileId, RecurringFixedRegionProfileSpecification specification) =>
        RecurringFixedRegionProfileVersion.CreateVersion1(profileId, CreatedAt, specification);

    private static RecurringFixedRegionProfileSpecification CreateSpec(
        TimeSpan? duration = null,
        int countdownSeconds = 3,
        string? outputDirectory = null,
        string? filenamePrefix = null,
        string? topologyDigest = null) =>
        new(
            AuthorizedScopeTargetType.FixedRegion,
            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            "DISPLAY-FP-1",
            new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            144,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            topologyDigest ?? TopologyDigest,
            AuthorizedCaptureBackend.FfmpegRegion,
            AuthorizedAudioMode.None,
            duration ?? TimeSpan.FromMinutes(2),
            countdownSeconds,
            outputDirectory ?? Path.Combine(Path.GetTempPath(), "task254-output"),
            filenamePrefix ?? "demo-区域",
            AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    private static void AssertProfileEqual(RecurringFixedRegionProfileVersion expected, RecurringFixedRegionProfileVersion actual)
    {
        Assert.Equal(expected.ProfileId, actual.ProfileId);
        Assert.Equal(expected.ProfileVersion, actual.ProfileVersion);
        Assert.Equal(expected.ProfileDigest, actual.ProfileDigest);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.TargetType, actual.TargetType);
        Assert.Equal(expected.RebindPolicy, actual.RebindPolicy);
        Assert.Equal(expected.CaptureSemantics, actual.CaptureSemantics);
        Assert.Equal(expected.CoordinateSpace, actual.CoordinateSpace);
        Assert.Equal(expected.DisplayIdentityStatus, actual.DisplayIdentityStatus);
        Assert.Equal(expected.StableDisplayFingerprint, actual.StableDisplayFingerprint);
        Assert.Equal(expected.DisplayBounds, actual.DisplayBounds);
        Assert.Equal(expected.RegionWithinDisplay, actual.RegionWithinDisplay);
        Assert.Equal(expected.DpiX, actual.DpiX);
        Assert.Equal(expected.DpiY, actual.DpiY);
        Assert.Equal(expected.PhysicalWidth, actual.PhysicalWidth);
        Assert.Equal(expected.PhysicalHeight, actual.PhysicalHeight);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.TopologyDigest, actual.TopologyDigest);
        Assert.Equal(expected.Backend, actual.Backend);
        Assert.Equal(expected.AudioMode, actual.AudioMode);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.CountdownSeconds, actual.CountdownSeconds);
        Assert.Equal(expected.OutputDirectory, actual.OutputDirectory);
        Assert.Equal(expected.FilenamePrefix, actual.FilenamePrefix);
        Assert.Equal(expected.FilenameTemplate, actual.FilenameTemplate);
        Assert.Equal(expected.OutputConflictPolicy, actual.OutputConflictPolicy);
        Assert.Equal(expected.WakePolicy, actual.WakePolicy);
        Assert.Equal(expected.DesktopRequirement, actual.DesktopRequirement);
    }

    private static void AssertCode(string expectedCode, Action action)
    {
        var exception = Assert.Throws<Phase3PersistenceException>(action);
        Assert.Equal(expectedCode, exception.Code);
    }

    private static (RecurringFixedRegionProfileVersion? Profile, Phase3PersistenceException? Exception) TryCreateNext(
        SqliteRecurringFixedRegionProfileRepository repository,
        ProfileRef expectedCurrentRef,
        RecurringFixedRegionProfileSpecification specification,
        DateTimeOffset createdAtUtc)
    {
        try
        {
            return (repository.CreateNext(expectedCurrentRef, specification, createdAtUtc), null);
        }
        catch (Phase3PersistenceException exception)
        {
            return (null, exception);
        }
    }

    private static Dictionary<string, List<string>> SnapshotBusinessTables(string databasePath)
    {
        using var connection = OpenRaw(databasePath);
        return BusinessTables.ToDictionary(table => table, table => ReadCanonicalRows(connection, table));
    }

    private static List<string> ReadCanonicalRows(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        var projection = string.Join(" || '|' || ", columns.Select(column => $"quote(\"{column}\")"));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {projection} FROM \"{table}\" ORDER BY rowid;";
        using var rows = command.ExecuteReader();
        var result = new List<string>();
        while (rows.Read())
        {
            result.Add(rows.GetString(0));
        }

        return result;
    }

    private static void AssertSnapshotsEqual(Dictionary<string, List<string>> expected, Dictionary<string, List<string>> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var table in expected.Keys)
        {
            Assert.Equal(expected[table], actual[table]);
        }
    }

    private static void AssertTriggerShapeRejected(string tamperSql)
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, tamperSql);
        }

        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using (connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using (connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "agent-recorder-task254-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(root);
            Store = new SqliteOperationalStore(Path.Combine(root, "state.db"));
        }

        public SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
