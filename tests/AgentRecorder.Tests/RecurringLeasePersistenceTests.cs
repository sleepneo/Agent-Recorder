using System.Globalization;
using System.Text.RegularExpressions;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringConsentLeasePersistenceTests
{
    [Fact]
    public void PendingLeaseRoundTripsExactTicksDigestsAndUsesBoundedBinaryPaging()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 10);
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        var created = fixture.CreatedAt.AddHours(-2);
        var lease = fixture.CreateLease("lease-b", created, fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var other = fixture.CreateLease("lease-a", created, fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));

        repository.InsertPending(lease);
        repository.InsertPending(other);

        var roundTrip = repository.Get(lease.LeaseId);
        Assert.Equal(lease.AuthorizationDigest, roundTrip.AuthorizationDigest);
        Assert.Equal(lease.ConfigurationRef.ConfigurationDigest, roundTrip.ConfigurationRef.ConfigurationDigest);
        Assert.Equal(lease.ConfigurationRef.ProfileRef, roundTrip.ConfigurationRef.ProfileRef);
        Assert.Equal(lease.CreatedAtUtc.UtcDateTime.Ticks, roundTrip.CreatedAtUtc.UtcDateTime.Ticks);
        Assert.Equal(lease.ValidFromUtc.UtcDateTime.Ticks, roundTrip.ValidFromUtc.UtcDateTime.Ticks);
        Assert.Equal(lease.PerRunDuration.Ticks, roundTrip.PerRunDuration.Ticks);
        Assert.Equal(lease.MaxCumulativeDuration.Ticks, roundTrip.MaxCumulativeDuration.Ticks);
        Assert.Equal(new[] { "lease-a" }, repository.ListByPlan(fixture.PlanId, limit: 1).Select(item => item.LeaseId));
        Assert.Equal(new[] { "lease-b" }, repository.ListByPlan(fixture.PlanId, afterLeaseIdExclusive: "lease-a", limit: 1).Select(item => item.LeaseId));
        Assert.Null(repository.TryGet("missing-lease"));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.NotFound, () => repository.Get("missing-lease"));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.AlreadyExists, () => repository.InsertPending(lease));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.ListLimitInvalid, () => repository.ListByPlan(fixture.PlanId, limit: 0));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.ListLimitInvalid, () => repository.ListByPlan(fixture.PlanId, limit: 1001));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.ListCursorInvalid, () => repository.ListByPlan(fixture.PlanId, afterLeaseIdExclusive: " "));
    }

    [Fact]
    public void RestartAndTamperedCanonicalFieldsFailClosed()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        var lease = fixture.CreateLease("restart-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        repository.InsertPending(lease);

        var restarted = new SqliteRecurringConsentLeaseRepository(new SqliteOperationalStore(fixture.Store.DatabasePath));
        Assert.Equal(lease.AuthorizationDigest, restarted.Get(lease.LeaseId).AuthorizationDigest);

        fixture.TamperLeaseAuthorizationDigest(lease.LeaseId, lease.AuthorizationDigest[..^1] + (lease.AuthorizationDigest[^1] == '0' ? '1' : '0'));
        AssertCode(RecurringConsentLeasePersistenceReasonCodes.PersistedDataInvalid, () => restarted.Get(lease.LeaseId));
    }

    [Fact]
    public void LeaseSqlLifecycleRejectsGenericAndIdentityBypassButKeepsExhaustedEdgeDatabaseOnly()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        var lease = fixture.CreateLease("lifecycle-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        repository.InsertPending(lease);

        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE recurring_consent_leases SET status_code = 'active', version = 0 WHERE lease_id = $id;", ("$id", lease.LeaseId)));
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE recurring_consent_leases SET lease_id = 'other' WHERE lease_id = $id;", ("$id", lease.LeaseId)));

        fixture.Execute("UPDATE recurring_consent_leases SET status_code = 'active', version = 1, updated_at_utc = $updated WHERE lease_id = $id;",
            ("$updated", fixture.CreatedAt.AddMinutes(-20).UtcDateTime.Ticks), ("$id", lease.LeaseId));
        fixture.Execute("UPDATE recurring_consent_leases SET status_code = 'exhausted', version = 2, updated_at_utc = $updated WHERE lease_id = $id;",
            ("$updated", fixture.CreatedAt.AddMinutes(-10).UtcDateTime.Ticks), ("$id", lease.LeaseId));
        Assert.Equal("exhausted", fixture.Scalar("SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", lease.LeaseId)));
    }

    [Fact]
    public void PublicContractsDoNotExposeRecurringLifecycleOrUseWriters()
    {
        Assert.DoesNotContain(typeof(IRecurringConsentLeaseRepository).GetMethods(), method => method.Name is "Update" or "Delete" or "MarkExhausted");
        Assert.DoesNotContain(typeof(IRecurringLeaseUseAccountingReader).GetMethods(), method => method.Name is "Insert" or "Update" or "Delete");
        Assert.DoesNotContain(typeof(SqliteRecurringConsentLeaseRepository).GetMethods(), method => method.Name is "Update" or "Delete" or "MarkExhausted");
        Assert.DoesNotContain(typeof(SqliteRecurringLeaseUseAccountingReader).GetMethods(), method => method.Name is "Insert" or "Update" or "Delete");
    }

    [Fact]
    public void V11CriticalIndexAndTriggerDriftFailClosedOnRestart()
    {
        using (var indexFixture = RecurringLeaseFixture.Create())
        {
            indexFixture.Execute("DROP INDEX ux_recurring_schedule_versions_exact_lease_parent;");
            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(indexFixture.Store.DatabasePath).Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }

        using (var triggerFixture = RecurringLeaseFixture.Create())
        {
            triggerFixture.Execute("DROP TRIGGER trg_recurring_consent_leases_pending_insert;");
            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(triggerFixture.Store.DatabasePath).Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }
    }

    [Fact]
    public void V11TriggerSemanticDriftFailsClosedForSameNameAndMissingReservationClause()
    {
        using (var leaseFixture = RecurringLeaseFixture.Create())
        {
            leaseFixture.ReplaceTriggerDefinition(
                "trg_recurring_consent_leases_lifecycle_update",
                "CREATE TRIGGER trg_recurring_consent_leases_lifecycle_update BEFORE UPDATE ON recurring_consent_leases BEGIN SELECT 1; END;");
            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(leaseFixture.Store.DatabasePath).Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }

        using (var useFixture = RecurringLeaseFixture.Create())
        {
            useFixture.ReplaceTriggerDefinition(
                "trg_recurring_lease_uses_lifecycle_update",
                "CREATE TRIGGER trg_recurring_lease_uses_lifecycle_update BEFORE UPDATE ON recurring_lease_uses BEGIN SELECT 1; END;");
            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(useFixture.Store.DatabasePath).Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }

        using (var preservationFixture = RecurringLeaseFixture.Create())
        {
            preservationFixture.RemoveTriggerStatement(
                "trg_recurring_lease_uses_lifecycle_update",
                "recurring_lease_uses_reservation_evidence_immutable");
            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(preservationFixture.Store.DatabasePath).Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }
    }

    [Fact]
    public void V11DdlFailureRollsBackNewIndexesTablesAndMigrationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-recorder-task259-v11-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.db");
        try
        {
            using (var connection = OpenRaw(path))
            {
                foreach (var migration in SqliteSchemaCatalog.Migrations.Take(10))
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

                Execute(connection, "CREATE TABLE recurring_consent_leases (blocker TEXT);");
            }

            var exception = Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(path).Initialize());
            Assert.Equal("sqlite_migration_failed", exception.Code);

            using var verification = OpenRaw(path);
            Assert.Equal(10L, Scalar(verification, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM schema_migrations WHERE version = 11;"));
            Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recurring_consent_leases';"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_recurring_schedule_versions_exact_lease_parent';"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
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
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<Phase3PersistenceException>(action);
        Assert.Equal(expected, exception.Code);
    }
}

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseUseAccountingPersistenceTests
{
    [Fact]
    public void SixAccountingStatesAreReadInStableOrderAndRestartedProjectionIsExact()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 10);
        var lease = fixture.CreateLease("accounting-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);

        var slots = fixture.CreateSlots(6);
        var statuses = new[] { "available", "reserved", "start_committed", "consumed", "settled", "started_unknown" };
        for (var index = 0; index < statuses.Length; index++)
        {
            var slot = slots[index];
            var createdAt = fixture.CreatedAt.AddMinutes(10 - index);
            fixture.InsertAccountingRow(
                $"use-{index}", lease, slot, $"run-{index}", statuses[index], createdAt,
                statuses[index] == "available" ? 0 : 1,
                statuses[index] == "available" ? TimeSpan.Zero : lease.PerRunDuration,
                statuses[index] == "settled" ? TimeSpan.FromTicks(lease.PerRunDuration.Ticks - 1) : null);
        }

        var reader = new SqliteRecurringLeaseUseAccountingReader(fixture.Store);
        var entries = reader.ListByLease(lease.LeaseId);
        Assert.Equal(new[] { "use-5", "use-4", "use-3", "use-2", "use-1", "use-0" }, entries.Select(entry => entry.UseId));
        Assert.Equal(lease.PerRunDuration, entries.Single(entry => entry.Status == LeaseUseStatus.Settled).ReservedDuration);
        Assert.NotNull(reader.TryGetByOccurrence(slots[3].OccurrenceIdentity));

        var restarted = new SqliteRecurringLeaseUseAccountingReader(new SqliteOperationalStore(fixture.Store.DatabasePath));
        Assert.Equal(entries.Select(entry => entry.UseId), restarted.ListByLease(lease.LeaseId).Select(entry => entry.UseId));
        Assert.Null(restarted.TryGetByOccurrence("missing-occurrence"));
    }

    [Fact]
    public void AccountingReaderRejectsOtherLeaseDurationAndSettledTampering()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 3);
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        var first = fixture.CreateLease("accounting-first", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var second = fixture.CreateLease("accounting-second", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        repository.InsertPending(first);
        repository.InsertPending(second);
        var slots = fixture.CreateSlots(3);
        fixture.InsertAccountingRow("bad-other", second, slots[0], "run-0", "reserved", fixture.CreatedAt, 1, second.PerRunDuration, null);
        fixture.TamperAccountingDuration("bad-other", second.PerRunDuration.Ticks + 1);
        AssertCode(RecurringLeaseUseAccountingPersistenceReasonCodes.PersistedDataInvalid,
            () => new SqliteRecurringLeaseUseAccountingReader(fixture.Store).ListByLease(second.LeaseId));
    }

    [Fact]
    public void DirectAccountingMatrixAndImmutableDeleteAreDatabaseRejected()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 2);
        var lease = fixture.CreateLease("matrix-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var slot = fixture.CreateSlots(1)[0];
        fixture.InsertAccountingRow("matrix-use", lease, slot, "run-0", "available", fixture.CreatedAt, 0, TimeSpan.Zero, null);
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE recurring_lease_uses SET status_code = 'settled', reserved_use_count = 0 WHERE use_id = 'matrix-use';"));
        Assert.Throws<SqliteException>(() => fixture.Execute("DELETE FROM recurring_lease_uses WHERE use_id = 'matrix-use';"));
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE recurring_lease_uses SET occurrence_id = 'other' WHERE use_id = 'matrix-use';"));
    }

    [Fact]
    public void UseLifecyclePreservesReservationEvidenceAndReleaseIsAtomic()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 2);
        var lease = fixture.CreateLease("reservation-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var slots = fixture.CreateSlots(2);
        var slot = slots[0];
        fixture.InsertAccountingRow("reservation-use", lease, slot, "run-0", "available", fixture.CreatedAt, 0, TimeSpan.Zero, null);

        fixture.UpdateAccountingRow("reservation-use", "reserved", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(1), 1);
        var reserved = fixture.ReadAccountingRow("reservation-use");
        Assert.Equal("reserved", reserved.StatusCode);

        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "reservation-use", "start_committed", 1, lease.PerRunDuration.Ticks + 1, null, fixture.CreatedAt.AddMinutes(2), 2));
        Assert.Equal(reserved, fixture.ReadAccountingRow("reservation-use"));

        fixture.UpdateAccountingRow("reservation-use", "start_committed", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(2), 2);
        var committed = fixture.ReadAccountingRow("reservation-use");
        Assert.Equal("start_committed", committed.StatusCode);

        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "reservation-use", "consumed", 0, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(3), 3));
        Assert.Equal(committed, fixture.ReadAccountingRow("reservation-use"));

        fixture.UpdateAccountingRow("reservation-use", "consumed", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(3), 3);
        var consumed = fixture.ReadAccountingRow("reservation-use");
        Assert.Equal("consumed", consumed.StatusCode);

        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "reservation-use", "started_unknown", 1, lease.PerRunDuration.Ticks + 1, null, fixture.CreatedAt.AddMinutes(4), 4));
        Assert.Equal(consumed, fixture.ReadAccountingRow("reservation-use"));

        fixture.UpdateAccountingRow("reservation-use", "started_unknown", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(4), 4);
        Assert.Equal("started_unknown", fixture.ReadAccountingRow("reservation-use").StatusCode);

        fixture.InsertAccountingRow("release-use", lease, slots[1], "run-1", "available", fixture.CreatedAt, 0, TimeSpan.Zero, null);
        fixture.UpdateAccountingRow("release-use", "reserved", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(1), 1);
        var releaseBefore = fixture.ReadAccountingRow("release-use");

        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "release-use", "available", 1, lease.PerRunDuration.Ticks, null, fixture.CreatedAt.AddMinutes(2), 2));
        Assert.Equal(releaseBefore, fixture.ReadAccountingRow("release-use"));

        fixture.UpdateAccountingRow("release-use", "available", 0, 0, null, fixture.CreatedAt.AddMinutes(2), 2);
        var released = fixture.ReadAccountingRow("release-use");
        Assert.Equal("available", released.StatusCode);
        Assert.Equal(0, released.ReservedUseCount);
        Assert.Equal(0, released.ReservedDurationTicks);
        Assert.Null(released.ActualSettledDurationTicks);
    }

    [Fact]
    public void ConsumedSettlementPreservesReservationEvidenceAndAcceptsOnlyLegalActualDuration()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 3);
        var lease = fixture.CreateLease("settlement-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var slots = fixture.CreateSlots(3);
        fixture.InsertAccountingRow("settlement-use", lease, slots[0], "run-0", "consumed", fixture.CreatedAt, 1, lease.PerRunDuration, null);

        var consumed = fixture.ReadAccountingRow("settlement-use");
        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "settlement-use", "settled", 1, lease.PerRunDuration.Ticks + 1, lease.PerRunDuration.Ticks - 1, fixture.CreatedAt.AddMinutes(1), 1));
        Assert.Equal(consumed, fixture.ReadAccountingRow("settlement-use"));

        Assert.Throws<SqliteException>(() => fixture.UpdateAccountingRow(
            "settlement-use", "settled", 0, lease.PerRunDuration.Ticks, lease.PerRunDuration.Ticks - 1, fixture.CreatedAt.AddMinutes(1), 1));
        Assert.Equal(consumed, fixture.ReadAccountingRow("settlement-use"));

        fixture.UpdateAccountingRow(
            "settlement-use", "settled", 1, lease.PerRunDuration.Ticks, lease.PerRunDuration.Ticks - 1, fixture.CreatedAt.AddMinutes(1), 1);
        var settled = fixture.ReadAccountingRow("settlement-use");
        Assert.Equal("settled", settled.StatusCode);
        Assert.Equal(consumed.ReservedUseCount, settled.ReservedUseCount);
        Assert.Equal(consumed.ReservedDurationTicks, settled.ReservedDurationTicks);
        Assert.Equal(lease.PerRunDuration.Ticks - 1, settled.ActualSettledDurationTicks);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<Phase3PersistenceException>(action);
        Assert.Equal(expected, exception.Code);
    }
}

internal sealed record RecurringLeaseUseRow(
    string StatusCode,
    int ReservedUseCount,
    long ReservedDurationTicks,
    long? ActualSettledDurationTicks,
    long UpdatedAtUtc,
    int Version);

internal sealed class RecurringLeaseFixture : IDisposable
{
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private RecurringLeaseFixture(string rootPath, SqliteOperationalStore store, RecurringPlanDraftSetupSnapshot setup, RecurringPlanSchedule schedule, DateTimeOffset createdAt)
    {
        RootPath = rootPath;
        Store = store;
        Setup = setup;
        Schedule = schedule;
        CreatedAt = createdAt;
    }

    public string RootPath { get; }
    public SqliteOperationalStore Store { get; }
    public RecurringPlanDraftSetupSnapshot Setup { get; }
    public RecurringPlanSchedule Schedule { get; }
    public DateTimeOffset CreatedAt { get; }
    public string PlanId => Setup.Plan.Id;

    public static RecurringLeaseFixture Create(
        int maxOccurrences = 10,
        TimeSpan latestStartGrace = default,
        int countdownSeconds = 3)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-recorder-task259-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteOperationalStore(Path.Combine(root, "state.db"));
        store.Initialize();
        var createdAt = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(store);
        var profile = profiles.CreateVersion1(CreateProfile("task259-profile", createdAt, root, countdownSeconds));
        var schedule = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 10).AddDays(maxOccurrences + 1),
            new TimeOnly(10, 0),
            maxOccurrences,
            TimeSpan.FromMinutes(2),
            latestStartGrace);
        var setup = new SqliteRecurringPlanDraftSetupTransaction(store).CreateOrGet("task259-plan", schedule, profile.Reference, createdAt);
        return new RecurringLeaseFixture(root, store, setup, schedule, createdAt);
    }

    public RecurringConsentLease CreateLease(string leaseId, DateTimeOffset createdAt, DateTimeOffset validFrom, DateTimeOffset validUntil)
    {
        return RecurringConsentLease.CreatePending(
            leaseId,
            Setup.ConfigurationRef,
            validFrom,
            validUntil,
            CreatedAt.AddMinutes(2),
            TimeSpan.FromMinutes(2),
            10,
            TimeSpan.FromMinutes(20),
            createdAt);
    }

    public IReadOnlyList<RecurringOccurrenceSlotSnapshot> CreateSlots(int count)
    {
        var calculator = new RecurringOccurrenceCalculator();
        var materializer = new SqliteRecurringOccurrenceMaterializationTransaction(Store);
        var runs = new SqliteRecordingRunRepository(Store);
        var slots = new List<RecurringOccurrenceSlotSnapshot>();
        DateTimeOffset? after = null;
        for (var index = 0; index < count; index++)
        {
            var calculation = calculator.CalculateNext(PlanId, 1, Schedule, after);
            var candidate = calculation.Candidate ?? throw new InvalidOperationException("Fixture could not calculate a recurring candidate.");
            var slot = materializer.Materialize(candidate, CreatedAt.AddMinutes(index + 1));
            slots.Add(slot);
            runs.Insert(new RecordingRun($"run-{index}", slot.OccurrenceId!, CreatedAt.AddMinutes(index + 1)));
            after = candidate.PlannedEndUtc;
        }

        return slots;
    }

    public void InsertAccountingRow(
        string useId,
        RecurringConsentLease lease,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string statusCode,
        DateTimeOffset createdAt,
        int reservedUseCount,
        TimeSpan reservedDuration,
        TimeSpan? actualDuration)
    {
        Execute("""
            INSERT INTO recurring_lease_uses(
                use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code,
                reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks,
                created_at_utc, updated_at_utc, version)
            VALUES ($useId, $leaseId, $planId, $occurrenceIdentity, $occurrenceId, $runId, $statusCode,
                    $reservedUseCount, $reservedDurationTicks, $actualDurationTicks,
                    $createdAtUtc, $createdAtUtc, 0);
            """,
            ("$useId", useId),
            ("$leaseId", lease.LeaseId),
            ("$planId", PlanId),
            ("$occurrenceIdentity", slot.OccurrenceIdentity),
            ("$occurrenceId", slot.OccurrenceId!),
            ("$runId", runId),
            ("$statusCode", statusCode),
            ("$reservedUseCount", reservedUseCount),
            ("$reservedDurationTicks", reservedDuration.Ticks),
            ("$actualDurationTicks", actualDuration?.Ticks),
            ("$createdAtUtc", createdAt.UtcDateTime.Ticks));
    }

    public RecurringLeaseUseRow ReadAccountingRow(string useId)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, updated_at_utc, version FROM recurring_lease_uses WHERE use_id = $useId;";
        command.Parameters.AddWithValue("$useId", useId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"The accounting row {useId} was not found.");
        }

        return new RecurringLeaseUseRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5));
    }

    public void UpdateAccountingRow(
        string useId,
        string statusCode,
        int reservedUseCount,
        long reservedDurationTicks,
        long? actualSettledDurationTicks,
        DateTimeOffset updatedAt,
        int version)
    {
        Execute(
            "UPDATE recurring_lease_uses SET status_code = $statusCode, reserved_use_count = $reservedUseCount, reserved_duration_ticks = $reservedDurationTicks, actual_settled_duration_ticks = $actualSettledDurationTicks, updated_at_utc = $updatedAtUtc, version = $version WHERE use_id = $useId;",
            ("$useId", useId),
            ("$statusCode", statusCode),
            ("$reservedUseCount", reservedUseCount),
            ("$reservedDurationTicks", reservedDurationTicks),
            ("$actualSettledDurationTicks", actualSettledDurationTicks),
            ("$updatedAtUtc", updatedAt.UtcDateTime.Ticks),
            ("$version", version));
    }

    public void ReplaceTriggerDefinition(string triggerName, string replacementDefinition)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TRIGGER {triggerName}; {replacementDefinition}";
        command.ExecuteNonQuery();
    }

    public void RemoveTriggerStatement(string triggerName, string statementMarker)
    {
        using var connection = OpenRaw();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = $name;";
        read.Parameters.AddWithValue("$name", triggerName);
        var triggerSql = read.ExecuteScalar() as string ?? throw new InvalidOperationException($"The trigger {triggerName} was not found.");
        var markerPattern = $"SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'{Regex.Escape(statementMarker)}'.*?;\\s*";
        var weakenedSql = Regex.Replace(triggerSql, markerPattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.NotEqual(triggerSql, weakenedSql);
        using var replace = connection.CreateCommand();
        replace.CommandText = $"DROP TRIGGER {triggerName}; {weakenedSql}";
        replace.ExecuteNonQuery();
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteScalar();
    }

    public void TamperLeaseAuthorizationDigest(string leaseId, string digest)
    {
        using var connection = OpenRaw();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_recurring_consent_leases_lifecycle_update';";
        var triggerSql = read.ExecuteScalar() as string ?? throw new InvalidOperationException("The lifecycle trigger was not found.");
        using var tamper = connection.CreateCommand();
        tamper.CommandText = "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET authorization_digest = $digest WHERE lease_id = $leaseId;";
        tamper.Parameters.AddWithValue("$digest", digest);
        tamper.Parameters.AddWithValue("$leaseId", leaseId);
        tamper.ExecuteNonQuery();
        using var restore = connection.CreateCommand();
        restore.CommandText = triggerSql;
        restore.ExecuteNonQuery();
    }

    public void TamperAccountingDuration(string useId, long durationTicks)
    {
        using var connection = OpenRaw();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_recurring_lease_uses_lifecycle_update';";
        var triggerSql = read.ExecuteScalar() as string ?? throw new InvalidOperationException("The accounting lifecycle trigger was not found.");
        using var tamper = connection.CreateCommand();
        tamper.CommandText = "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET reserved_duration_ticks = $duration WHERE use_id = $useId;";
        tamper.Parameters.AddWithValue("$duration", durationTicks);
        tamper.Parameters.AddWithValue("$useId", useId);
        tamper.ExecuteNonQuery();
        using var restore = connection.CreateCommand();
        restore.CommandText = triggerSql;
        restore.ExecuteNonQuery();
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Store.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
        }
    }

    private static RecurringFixedRegionProfileVersion CreateProfile(
        string id,
        DateTimeOffset createdAt,
        string root,
        int countdownSeconds = 3) =>
        RecurringFixedRegionProfileVersion.CreateVersion1(
            id,
            createdAt,
            new RecurringFixedRegionProfileSpecification(
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
                TopologyDigest,
                AuthorizedCaptureBackend.FfmpegRegion,
                AuthorizedAudioMode.None,
                TimeSpan.FromMinutes(2),
                countdownSeconds,
                Path.Combine(root, "output"),
                "demo",
                AuthorizedOutputConflictPolicy.FailIfExists,
                AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired));
}
