using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class Phase3RepositoryTests
{
    [Fact]
    public void InsertsAndReadsAllFiveAggregatesWithoutChangingTheirSnapshot()
    {
        using var database = new TemporaryDatabase();
        var chain = SeedExecutionChain(database);

        var plan = database.Plans.Get(chain.Plan.Id);
        var occurrence = database.Occurrences.Get(chain.Occurrence.Id);
        var run = database.Runs.Get(chain.Run.Id);
        var lease = database.Leases.Get(chain.Lease.Id);
        var use = database.Uses.Get(chain.Use.Id);

        Assert.Equal(chain.Plan.Id, plan.Id);
        Assert.Equal(chain.Plan.IsOneTime, plan.IsOneTime);
        Assert.Equal(chain.Plan.Status, plan.Status);
        Assert.Equal(chain.Plan.CreatedAtUtc, plan.CreatedAtUtc);
        Assert.Equal(chain.Plan.UpdatedAtUtc, plan.UpdatedAtUtc);
        Assert.Equal(chain.Plan.Version, plan.Version);

        Assert.Equal(chain.Occurrence.Id, occurrence.Id);
        Assert.Equal(chain.Occurrence.PlanId, occurrence.PlanId);
        Assert.Equal(chain.Occurrence.Status, occurrence.Status);
        Assert.Equal(chain.Occurrence.WindowStartUtc, occurrence.WindowStartUtc);
        Assert.Equal(chain.Occurrence.WindowEndUtc, occurrence.WindowEndUtc);
        Assert.Equal(chain.Occurrence.RunId, occurrence.RunId);
        Assert.Equal(chain.Occurrence.TerminalReasonCode, occurrence.TerminalReasonCode);
        Assert.Equal(chain.Occurrence.Version, occurrence.Version);

        Assert.Equal(chain.Run.Id, run.Id);
        Assert.Equal(chain.Run.OccurrenceId, run.OccurrenceId);
        Assert.Equal(chain.Run.Status, run.Status);
        Assert.Equal(chain.Run.HasCrossedStartCommit, run.HasCrossedStartCommit);
        Assert.Equal(chain.Run.MediaArtifactId, run.MediaArtifactId);
        Assert.Equal(chain.Run.BundleId, run.BundleId);
        Assert.Equal(chain.Run.TerminalReasonCode, run.TerminalReasonCode);
        Assert.Equal(chain.Run.Version, run.Version);

        Assert.Equal(chain.Lease.Id, lease.Id);
        Assert.Equal(chain.Lease.PlanId, lease.PlanId);
        Assert.Equal(chain.Lease.OccurrenceId, lease.OccurrenceId);
        Assert.Equal(chain.Lease.Status, lease.Status);
        Assert.Equal(chain.Lease.ValidFromUtc, lease.ValidFromUtc);
        Assert.Equal(chain.Lease.ValidUntilUtc, lease.ValidUntilUtc);
        Assert.Equal(chain.Lease.MaxUses, lease.MaxUses);
        Assert.Equal(chain.Lease.MaxDuration, lease.MaxDuration);
        Assert.Equal(chain.Lease.Version, lease.Version);

        Assert.Equal(chain.Use.Id, use.Id);
        Assert.Equal(chain.Use.LeaseId, use.LeaseId);
        Assert.Equal(chain.Use.OccurrenceId, use.OccurrenceId);
        Assert.Equal(chain.Use.RunId, use.RunId);
        Assert.Equal(chain.Use.Status, use.Status);
        Assert.Equal(chain.Use.ReservedUseCount, use.ReservedUseCount);
        Assert.Equal(chain.Use.ReservedDuration, use.ReservedDuration);
        Assert.Equal(chain.Use.ActualSettledDuration, use.ActualSettledDuration);
        Assert.Equal(chain.Use.Version, use.Version);
    }

    [Fact]
    public void PersistenceIsInvariantAcrossCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var cultureName in new[] { "en-US", "zh-CN", "ar-SA" })
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                using var database = new TemporaryDatabase();
                var chain = SeedExecutionChain(database);

                Assert.Equal(chain.Plan.Status, database.Plans.Get(chain.Plan.Id).Status);
                Assert.Equal(chain.Occurrence.Status, database.Occurrences.Get(chain.Occurrence.Id).Status);
                Assert.Equal(chain.Run.Status, database.Runs.Get(chain.Run.Id).Status);
                Assert.Equal(chain.Lease.MaxDuration, database.Leases.Get(chain.Lease.Id).MaxDuration);
                Assert.Equal(chain.Use.ReservedDuration, database.Uses.Get(chain.Use.Id).ReservedDuration);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void MissingAndDuplicateRowsHaveStableErrors()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-1", true, At(0));

        var missing = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get("missing"));
        Assert.Equal("not_found", missing.Code);

        database.Plans.Insert(plan);
        var duplicate = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Insert(plan));
        Assert.Equal("already_exists", duplicate.Code);

        var invalidId = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get("  "));
        Assert.Equal("invalid_argument", invalidId.Code);
    }

    [Fact]
    public void OneLegalTransitionPerAggregateUsesCoreStateAndOptimisticUpdate()
    {
        using var database = new TemporaryDatabase();
        var chain = SeedExecutionChain(database);

        var plan = database.Plans.Get(chain.Plan.Id);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, At(10)).Succeeded);
        database.Plans.Update(plan, expectedVersion: 0);
        Assert.Equal((PlanDefinitionStatus.Enabled, 1L), (database.Plans.Get(plan.Id).Status, database.Plans.Get(plan.Id).Version));

        var occurrence = database.Occurrences.Get(chain.Occurrence.Id);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Completed, At(11)).Succeeded);
        database.Occurrences.Update(occurrence, expectedVersion: 1);
        Assert.Equal((PlanOccurrenceStatus.Completed, 2L), (database.Occurrences.Get(occurrence.Id).Status, database.Occurrences.Get(occurrence.Id).Version));

        var run = database.Runs.Get(chain.Run.Id);
        Assert.True(run.TryTransition(RecordingRunStatus.Preparing, At(3)).Succeeded);
        database.Runs.Update(run, expectedVersion: 0);
        Assert.Equal((RecordingRunStatus.Preparing, 1L), (database.Runs.Get(run.Id).Status, database.Runs.Get(run.Id).Version));

        var lease = database.Leases.Get(chain.Lease.Id);
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, At(4)).Succeeded);
        database.Leases.Update(lease, expectedVersion: 0);
        Assert.Equal((ConsentLeaseStatus.Active, 1L), (database.Leases.Get(lease.Id).Status, database.Leases.Get(lease.Id).Version));

        var use = database.Uses.Get(chain.Use.Id);
        Assert.True(use.TryReserve(1, TimeSpan.FromSeconds(1), At(5)).Succeeded);
        database.Uses.Update(use, expectedVersion: 0);
        var savedUse = database.Uses.Get(use.Id);
        Assert.Equal(LeaseUseStatus.Reserved, savedUse.Status);
        Assert.Equal(1, savedUse.ReservedUseCount);
        Assert.Equal(TimeSpan.FromSeconds(1), savedUse.ReservedDuration);
        Assert.Equal(1L, savedUse.Version);
    }

    [Fact]
    public async Task TwoRepositoryInstancesCompetingOnOneVersionHaveOneWinner()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-race", true, At(0));
        database.Plans.Insert(plan);
        var first = database.Plans.Get(plan.Id);
        var second = database.Plans.Get(plan.Id);
        Assert.True(first.TryTransition(PlanDefinitionStatus.Enabled, At(1)).Succeeded);
        Assert.True(second.TryTransition(PlanDefinitionStatus.Enabled, At(2)).Succeeded);
        var firstRepository = new SqlitePlanDefinitionRepository(database.Store);
        var secondRepository = new SqlitePlanDefinitionRepository(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureCode(() => firstRepository.Update(first, 0))),
            Task.Run(() => CaptureCode(() => secondRepository.Update(second, 0))));

        Assert.Equal(1, results.Count(code => code is null));
        Assert.Equal(1, results.Count(code => code == "concurrency_conflict"));
        Assert.Equal(PlanDefinitionStatus.Enabled, database.Plans.Get(plan.Id).Status);
        Assert.Equal(1L, database.Plans.Get(plan.Id).Version);
    }

    [Fact]
    public void InvalidExpectedVersionsAreRejectedBeforeAnySqlUpdate()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-version", true, At(0));
        database.Plans.Insert(plan);

        foreach (var (snapshotVersion, expectedVersion) in new[] { (0L, 0L), (1L, 1L), (2L, 0L), (1L, -1L) })
        {
            var snapshot = PlanDefinition.Rehydrate(plan.Id, true, PlanDefinitionStatus.Draft, At(0), At(0), snapshotVersion);
            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Update(snapshot, expectedVersion));
            Assert.Equal("invalid_version", exception.Code);
        }

        Assert.Equal(0L, database.Plans.Get(plan.Id).Version);
    }

    [Fact]
    public void ImmutableFieldsArePredicatesAndAreNeverSilentlyRewritten()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-immutable", true, At(0));
        database.Plans.Insert(plan);
        RawExecute(database.Store.DatabasePath, "UPDATE plans SET is_one_time = 0 WHERE id = $id;", ("$id", plan.Id));
        var immutableSnapshot = PlanDefinition.Rehydrate(plan.Id, true, PlanDefinitionStatus.Draft, At(0), At(0), 1);

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Update(immutableSnapshot, 0));

        Assert.Equal("immutable_mismatch", exception.Code);
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT is_one_time FROM plans WHERE id = $id;", ("$id", plan.Id)));
    }

    [Fact]
    public void UnknownStatusAndMalformedScalarValuesFailClosed()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-invalid", true, At(0));
        database.Plans.Insert(plan);

        RawExecute(database.Store.DatabasePath, "UPDATE plans SET status_code = $status WHERE id = $id;", ("$status", "status; DROP TABLE plans;--"), ("$id", plan.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id)).Code);

        RawExecute(database.Store.DatabasePath, "UPDATE plans SET status_code = 'draft' WHERE id = $id;", ("$id", plan.Id));
        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE plans SET is_one_time = 2 WHERE id = $id;", ("$id", plan.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id)).Code);

        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE plans SET is_one_time = 1, created_at_utc = -1 WHERE id = $id;", ("$id", plan.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id)).Code);
    }

    [Fact]
    public void StateFieldContradictionsAndReservationEvidenceFailClosed()
    {
        using var database = new TemporaryDatabase();
        var chain = SeedExecutionChain(database);

        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE recording_runs SET status_code = 'started_unknown', has_crossed_start_commit = 0, terminal_reason_code = NULL WHERE id = $id;", ("$id", chain.Run.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Runs.Get(chain.Run.Id)).Code);

        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE recording_runs SET status_code = 'created', has_crossed_start_commit = 0, terminal_reason_code = NULL WHERE id = $id;", ("$id", chain.Run.Id));
        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE lease_uses SET status_code = 'reserved', reserved_use_count = 0, reserved_duration_ms = 0, actual_settled_duration_ms = NULL WHERE id = $id;", ("$id", chain.Use.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Uses.Get(chain.Use.Id)).Code);
    }

    [Fact]
    public void ScalarStorageTypesAndForeignKeyRelationsAreCheckedOnLoad()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-types", true, At(0));
        database.Plans.Insert(plan);

        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE plans SET is_one_time = 'not-a-bool' WHERE id = $id;", ("$id", plan.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id)).Code);

        RawExecuteWithChecksDisabled(database.Store.DatabasePath, "UPDATE plans SET is_one_time = 1, created_at_utc = 'not-ticks' WHERE id = $id;", ("$id", plan.Id));
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id)).Code);

        RawExecuteWithForeignKeysDisabled(database.Store.DatabasePath, "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ('orphan-load', 'missing-plan', 'scheduled', 0, 1000, 0, 0, 0);");
        Assert.Equal("persisted_snapshot_invalid", Assert.Throws<Phase3PersistenceException>(() => database.Occurrences.Get("orphan-load")).Code);
    }

    [Fact]
    public void SubMillisecondDurationIsRejectedBeforeInsertAndLeavesNoRow()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-duration", true, At(0));
        database.Plans.Insert(plan);
        var occurrence = PlanOccurrence.CreateFor(plan, "occ-duration", At(0), At(60), At(0));
        database.Occurrences.Insert(occurrence);
        var lease = new ConsentLease("lease-duration", plan.Id, occurrence.Id, At(0), At(60), 1, TimeSpan.FromTicks(1));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Leases.Insert(lease));

        Assert.Equal("duration_not_representable", exception.Code);
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM consent_leases;"));
    }

    [Fact]
    public void MaximumUtcTicksIntegerDurationsAndNullableDurationsRoundTripExactly()
    {
        using var database = new TemporaryDatabase();
        var plan = new PlanDefinition("plan-max", true, DateTimeOffset.MaxValue);
        database.Plans.Insert(plan);
        var savedPlan = database.Plans.Get(plan.Id);
        Assert.Equal(DateTimeOffset.MaxValue, savedPlan.CreatedAtUtc);

        var occurrence = PlanOccurrence.CreateFor(plan, "occ-max", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue);
        database.Occurrences.Insert(occurrence);
        var lease = new ConsentLease("lease-max", plan.Id, occurrence.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, int.MaxValue, TimeSpan.FromTicks((TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond));
        database.Leases.Insert(lease);
        Assert.Equal(lease.MaxDuration, database.Leases.Get(lease.Id).MaxDuration);

        var run = new RecordingRun("run-max", occurrence.Id, DateTimeOffset.MinValue);
        database.Runs.Insert(run);
        var use = new LeaseUse("use-max", lease.Id, occurrence.Id, run.Id, DateTimeOffset.MinValue);
        database.Uses.Insert(use);
        var savedUse = database.Uses.Get(use.Id);
        Assert.Null(savedUse.ActualSettledDuration);
        Assert.Equal(TimeSpan.Zero, savedUse.ReservedDuration);
    }

    [Fact]
    public void SettledActualDurationAndReservationEvidenceRoundTrip()
    {
        using var database = new TemporaryDatabase();
        var chain = SeedExecutionChain(database);
        var use = database.Uses.Get(chain.Use.Id);
        Assert.True(use.TryReserve(1, TimeSpan.FromSeconds(30), At(3)).Succeeded);
        database.Uses.Update(use, 0);
        Assert.True(use.TryTransition(LeaseUseStatus.StartCommitted, At(4)).Succeeded);
        database.Uses.Update(use, 1);
        Assert.True(use.TryTransition(LeaseUseStatus.Consumed, At(5)).Succeeded);
        database.Uses.Update(use, 2);
        Assert.True(use.TrySettle(TimeSpan.Zero, At(6)).Succeeded);
        database.Uses.Update(use, 3);

        var saved = database.Uses.Get(use.Id);
        Assert.Equal(LeaseUseStatus.Settled, saved.Status);
        Assert.Equal(1, saved.ReservedUseCount);
        Assert.Equal(TimeSpan.FromSeconds(30), saved.ReservedDuration);
        Assert.Equal(TimeSpan.Zero, saved.ActualSettledDuration);
        Assert.Equal(4L, saved.Version);
    }

    [Fact]
    public void ForeignKeyConstraintIsDistinctFromInvalidSnapshotAndVersionConflict()
    {
        using var database = new TemporaryDatabase();
        var orphan = new PlanOccurrence("orphan", "missing-plan", At(0), At(1), At(0));
        var foreignKey = Assert.Throws<Phase3PersistenceException>(() => database.Occurrences.Insert(orphan));
        Assert.Equal("constraint_violation", foreignKey.Code);

        var plan = new PlanDefinition("plan-errors", true, At(0));
        database.Plans.Insert(plan);
        var changed = database.Plans.Get(plan.Id);
        Assert.True(changed.TryTransition(PlanDefinitionStatus.Enabled, At(1)).Succeeded);
        database.Plans.Update(changed, 0);
        var stale = PlanDefinition.Rehydrate(plan.Id, true, PlanDefinitionStatus.Enabled, At(0), At(1), 1);
        var conflict = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Update(stale, 0));
        Assert.Equal("concurrency_conflict", conflict.Code);

        RawExecute(database.Store.DatabasePath, "UPDATE plans SET status_code = 'not-a-state' WHERE id = $id;", ("$id", plan.Id));
        var invalid = Assert.Throws<Phase3PersistenceException>(() => database.Plans.Get(plan.Id));
        Assert.Equal("persisted_snapshot_invalid", invalid.Code);
    }

    [Fact]
    public void InjectionStyleValuesRemainValuesAndDoNotChangeSchema()
    {
        using var database = new TemporaryDatabase();
        var planId = "plan' OR 1=1; DROP TABLE plans;--";
        var plan = new PlanDefinition(planId, true, At(0));
        database.Plans.Insert(plan);
        Assert.Equal(planId, database.Plans.Get(planId).Id);

        var occurrence = PlanOccurrence.CreateFor(plan, "occ-injection", At(0), At(60), At(0));
        occurrence = PlanOccurrence.Rehydrate(occurrence.Id, occurrence.PlanId, occurrence.WindowStartUtc, occurrence.WindowEndUtc, occurrence.CreatedAtUtc, PlanOccurrenceStatus.Blocked, null, "reason'); DROP TABLE plan_occurrences;--", At(0), 0);
        database.Occurrences.Insert(occurrence);
        Assert.Equal(occurrence.TerminalReasonCode, database.Occurrences.Get(occurrence.Id).TerminalReasonCode);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void RepositoryOnlyCreatesOperationalRowsAndNoCaptureArtifacts()
    {
        using var database = new TemporaryDatabase();
        SeedExecutionChain(database);

        var files = Directory.GetFiles(database.RootPath, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, file => file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.Contains("proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.Contains("capture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryPlanDefinitionStateCanBeRehydratedThroughRepository()
    {
        using var database = new TemporaryDatabase();
        foreach (var status in Enum.GetValues<PlanDefinitionStatus>())
        {
            var plan = PlanDefinition.Rehydrate("plan-" + status, true, status, At(0), At(0), 0);
            database.Plans.Insert(plan);
            Assert.Equal(status, database.Plans.Get(plan.Id).Status);
        }
    }

    [Fact]
    public void EveryNonRunCreatedOccurrenceStateCanBeRehydratedThroughRepository()
    {
        using var database = new TemporaryDatabase();
        var statuses = new[]
        {
            PlanOccurrenceStatus.Scheduled,
            PlanOccurrenceStatus.Due,
            PlanOccurrenceStatus.Rechecking,
            PlanOccurrenceStatus.PendingLeaseApproval,
            PlanOccurrenceStatus.Authorized,
            PlanOccurrenceStatus.PendingConfirmation,
            PlanOccurrenceStatus.Missed,
            PlanOccurrenceStatus.Blocked,
            PlanOccurrenceStatus.Cancelled,
            PlanOccurrenceStatus.Expired,
        };
        foreach (var status in statuses)
        {
            var plan = new PlanDefinition("plan-occ-" + status, true, At(0));
            database.Plans.Insert(plan);
            var reason = status is PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired ? "reason" : null;
            var occurrence = PlanOccurrence.Rehydrate("occ-" + status, plan.Id, At(0), At(60), At(0), status, null, reason, At(0), 0);
            database.Occurrences.Insert(occurrence);
            Assert.Equal(status, database.Occurrences.Get(occurrence.Id).Status);
        }
    }

    [Fact]
    public void RunCreatedAndCompletedOccurrenceStatesCanBeRehydratedAfterRelationExists()
    {
        using var database = new TemporaryDatabase();
        var chain = SeedExecutionChain(database);
        var runCreated = database.Occurrences.Get(chain.Occurrence.Id);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, runCreated.Status);
        Assert.Equal(chain.Run.Id, runCreated.RunId);
        Assert.True(runCreated.TryTransition(PlanOccurrenceStatus.Completed, At(4)).Succeeded);
        database.Occurrences.Update(runCreated, 1);
        Assert.Equal(PlanOccurrenceStatus.Completed, database.Occurrences.Get(runCreated.Id).Status);
    }

    [Fact]
    public void EveryRecordingRunStateCanBeRehydratedThroughRepository()
    {
        using var database = new TemporaryDatabase();
        foreach (var status in Enum.GetValues<RecordingRunStatus>())
        {
            var plan = new PlanDefinition("plan-run-" + status, true, At(0));
            database.Plans.Insert(plan);
            var occurrence = new PlanOccurrence("occ-run-" + status, plan.Id, At(0), At(60), At(0));
            database.Occurrences.Insert(occurrence);
            var requiresCommit = status is RecordingRunStatus.StartCommitted or RecordingRunStatus.Recording or RecordingRunStatus.StartedUnknown or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady or RecordingRunStatus.Settled or RecordingRunStatus.SessionInterrupted;
            var reason = status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed ? "reason" : null;
            var run = RecordingRun.Rehydrate("run-" + status, occurrence.Id, At(0), status, requiresCommit, null, null, reason, At(0), 0);
            database.Runs.Insert(run);
            Assert.Equal(status, database.Runs.Get(run.Id).Status);
        }
    }

    [Fact]
    public void EveryConsentLeaseStateCanBeRehydratedThroughRepository()
    {
        using var database = new TemporaryDatabase();
        foreach (var status in Enum.GetValues<ConsentLeaseStatus>())
        {
            var plan = new PlanDefinition("plan-lease-" + status, true, At(0));
            database.Plans.Insert(plan);
            var occurrence = new PlanOccurrence("occ-lease-" + status, plan.Id, At(0), At(60), At(0));
            database.Occurrences.Insert(occurrence);
            var lease = ConsentLease.Rehydrate("lease-" + status, plan.Id, occurrence.Id, At(0), At(60), 1, TimeSpan.FromSeconds(1), status, At(0), 0);
            database.Leases.Insert(lease);
            Assert.Equal(status, database.Leases.Get(lease.Id).Status);
        }
    }

    [Fact]
    public void EveryLeaseUseStateCanBeRehydratedWithRequiredEvidence()
    {
        using var database = new TemporaryDatabase();
        foreach (var status in Enum.GetValues<LeaseUseStatus>())
        {
            var plan = new PlanDefinition("plan-use-" + status, true, At(0));
            database.Plans.Insert(plan);
            var occurrence = new PlanOccurrence("occ-use-" + status, plan.Id, At(0), At(60), At(0));
            database.Occurrences.Insert(occurrence);
            var run = new RecordingRun("run-use-" + status, occurrence.Id, At(0));
            database.Runs.Insert(run);
            var lease = new ConsentLease("lease-use-" + status, plan.Id, occurrence.Id, At(0), At(60), 1, TimeSpan.FromSeconds(1));
            database.Leases.Insert(lease);
            var hasEvidence = status is not LeaseUseStatus.Available;
            var use = LeaseUse.Rehydrate("use-" + status, lease.Id, occurrence.Id, run.Id, At(0), status, hasEvidence ? 1 : 0, hasEvidence ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, status == LeaseUseStatus.Settled ? TimeSpan.Zero : null, At(0), 0);
            database.Uses.Insert(use);
            var saved = database.Uses.Get(use.Id);
            Assert.Equal(status, saved.Status);
            Assert.Equal(use.ReservedUseCount, saved.ReservedUseCount);
            Assert.Equal(use.ReservedDuration, saved.ReservedDuration);
            Assert.Equal(use.ActualSettledDuration, saved.ActualSettledDuration);
        }
    }

    private static string? CaptureCode(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Phase3PersistenceException exception)
        {
            return exception.Code;
        }
    }

    private static ExecutionChain SeedExecutionChain(TemporaryDatabase database)
    {
        var plan = new PlanDefinition("plan-1", true, At(0));
        database.Plans.Insert(plan);
        var occurrence = PlanOccurrence.Rehydrate("occ-1", plan.Id, At(0), At(60), At(0), PlanOccurrenceStatus.Authorized, null, null, At(0), 0);
        database.Occurrences.Insert(occurrence);
        Assert.True(occurrence.TryCreateRun("run-1", At(1)).Succeeded);
        var run = RecordingRun.CreateFor(occurrence, occurrence.RunId!, At(1));
        database.Runs.Insert(run);
        database.Occurrences.Update(occurrence, 0);
        var lease = ConsentLease.CreateFor(plan, occurrence, At(0), At(120), 1, TimeSpan.FromSeconds(30), "lease-1");
        database.Leases.Insert(lease);
        var use = LeaseUse.CreateFor(lease, occurrence, run, "use-1", At(2));
        database.Uses.Insert(use);
        return new ExecutionChain(plan, occurrence, run, lease, use);
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static void RawExecute(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        Execute(connection, sql, parameters);
    }

    private static void RawExecuteWithChecksDisabled(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        Execute(connection, "PRAGMA ignore_check_constraints = ON;");
        Execute(connection, sql, parameters);
    }

    private static void RawExecuteWithForeignKeysDisabled(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        Execute(connection, "PRAGMA foreign_keys = OFF;");
        Execute(connection, sql, parameters);
    }

    private static long RawScalar(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
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
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private sealed record ExecutionChain(PlanDefinition Plan, PlanOccurrence Occurrence, RecordingRun Run, ConsentLease Lease, LeaseUse Use);

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly TemporaryDirectory directory = new();

        public TemporaryDatabase()
        {
            Store = new SqliteOperationalStore(Path.Combine(directory.Path, "state", "agent-recorder.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            Plans = new SqlitePlanDefinitionRepository(Store);
            Occurrences = new SqlitePlanOccurrenceRepository(Store);
            Runs = new SqliteRecordingRunRepository(Store);
            Leases = new SqliteConsentLeaseRepository(Store);
            Uses = new SqliteLeaseUseRepository(Store);
        }

        public SqliteOperationalStore Store { get; }

        public SqlitePlanDefinitionRepository Plans { get; }

        public SqlitePlanOccurrenceRepository Occurrences { get; }

        public SqliteRecordingRunRepository Runs { get; }

        public SqliteConsentLeaseRepository Leases { get; }

        public SqliteLeaseUseRepository Uses { get; }

        public string RootPath => directory.Path;

        public void Dispose() => directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentRecorderPhase3Repository_" + Guid.NewGuid().ToString("N"));
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
