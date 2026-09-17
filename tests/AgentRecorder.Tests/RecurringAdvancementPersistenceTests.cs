using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringAdvancementPersistenceTests
{
    [Fact]
    public void DailyAdvancementUsesLocalCursorAndSharesOneVersionedChain()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("daily");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));

        var first = database.Advancement.AdvanceOne(plan.Id, 1, "op-1", 0, Utc(2026, 1, 1, 11), Utc(2026, 1, 1, 11, 1));
        var afterFirst = database.Advancement.GetCursor(plan.Id, 1);
        var second = database.Advancement.AdvanceOne(plan.Id, 1, "op-2", 1, Utc(2026, 1, 1, 11), Utc(2026, 1, 1, 11, 2));
        var afterSecond = database.Advancement.GetCursor(plan.Id, 1);

        Assert.Equal("scheduled", first.ResultCode);
        Assert.Equal("scheduled", second.ResultCode);
        Assert.Equal(new DateOnly(2026, 1, 1), afterFirst.LastLocalDate);
        Assert.Equal(new DateOnly(2026, 1, 2), afterSecond.LastLocalDate);
        Assert.Equal(2L, afterSecond.Version);
        Assert.Equal(2L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void InitialBoundaryIsStrictAndFrozenAfterTheFirstRequest()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("strict");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));

        var first = database.Advancement.AdvanceOne(plan.Id, 1, "strict-1", 0, Utc(2026, 1, 1, 12), Utc(2026, 1, 1));
        var replay = database.Advancement.AdvanceOne(plan.Id, 1, "strict-1", 0, Utc(2026, 1, 1, 12), Utc(2026, 1, 2));

        Assert.Equal(first, replay);
        Assert.Equal(new DateOnly(2026, 1, 2), database.Advancement.GetCursor(plan.Id, 1).LastLocalDate);
        var conflict = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.AdvanceOne(
            plan.Id, 1, "strict-2", 1, Utc(2026, 1, 1, 11), Utc(2026, 1, 3)));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorRequestConflict, conflict.Code);
    }

    [Fact]
    public void WeeklyAdvancementJumpsMatchingLocalDatesWithoutWalkingTheRange()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("weekly");
        var schedule = RecurringPlanSchedule.CreateWeekly(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            new TimeOnly(12, 0),
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday },
            2,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));

        var first = database.Advancement.AdvanceOne(plan.Id, 1, "weekly-1", 0, Utc(2026, 1, 1), Utc(2026, 1, 1));
        var afterFirst = database.Advancement.GetCursor(plan.Id, 1);
        var second = database.Advancement.AdvanceOne(plan.Id, 1, "weekly-2", 1, Utc(2026, 1, 1), Utc(2026, 1, 2));

        Assert.Equal(new DateOnly(2026, 1, 5), afterFirst.LastLocalDate);
        Assert.Equal(new DateOnly(2026, 1, 7), database.Advancement.GetCursor(plan.Id, 1).LastLocalDate);
        Assert.Equal(0L, first.ExpectedCursorVersion);
        Assert.Equal(2L, second.ResultCursorVersion);
        Assert.Equal(2L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
    }

    [Fact]
    public void DstGapConsumesAnOrdinalAndOverlapUsesEarlierUtc()
    {
        var zoneId = GetDstZoneId();
        using (var gapDatabase = new TestDatabase())
        {
            var plan = gapDatabase.InsertEnabledPeriodicPlan("gap");
            var schedule = RecurringPlanSchedule.CreateDaily(
                zoneId,
                new DateOnly(2026, 3, 8),
                new DateOnly(2026, 3, 9),
                new TimeOnly(2, 30),
                2,
                TimeSpan.FromMinutes(1),
                TimeSpan.Zero);
            gapDatabase.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));

            var skipped = gapDatabase.Advancement.AdvanceOne(plan.Id, 1, "gap-1", 0, Utc(2026, 1, 1), Utc(2026, 1, 1));
            Assert.Equal("skipped", skipped.ResultCode);
            var afterSkipped = gapDatabase.Advancement.GetCursor(plan.Id, 1);
            Assert.Equal(new DateOnly(2026, 3, 8), afterSkipped.LastLocalDate);
            var scheduled = gapDatabase.Advancement.AdvanceOne(plan.Id, 1, "gap-2", 1, Utc(2026, 1, 1), Utc(2026, 1, 2));
            Assert.Equal("scheduled", scheduled.ResultCode);
            Assert.Equal(new DateOnly(2026, 3, 9), gapDatabase.Advancement.GetCursor(plan.Id, 1).LastLocalDate);
            Assert.Equal(2L, Scalar(gapDatabase.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
            Assert.Equal(1L, Scalar(gapDatabase.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        }

        using var overlapDatabase = new TestDatabase();
        var overlapPlan = overlapDatabase.InsertEnabledPeriodicPlan("overlap");
        var overlapSchedule = RecurringPlanSchedule.CreateDaily(
            zoneId,
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 1),
            new TimeOnly(1, 30),
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        overlapDatabase.Schedules.CreateOrGet(overlapPlan.Id, 1, overlapSchedule, Utc(2026, 1, 1));
        var overlap = overlapDatabase.Advancement.AdvanceOne(overlapPlan.Id, 1, "overlap-1", 0, Utc(2026, 1, 1), Utc(2026, 1, 1));
        var overlapSlot = overlapDatabase.Materializer.Get(overlap.OccurrenceIdentity!);
        Assert.Equal("scheduled", overlap.ResultCode);
        Assert.Equal("schedule_ambiguous_earlier_utc", overlapSlot.ResolutionCode);
    }

    [Fact]
    public void MaximumAndDateEndExhaustionArePersistedAndConsumeTheExpectedVersion()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("exhaust");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 1);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));

        var scheduled = database.Advancement.AdvanceOne(plan.Id, 1, "exhaust-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        Assert.Equal("scheduled", scheduled.ResultCode);
        var cursor = database.Advancement.GetCursor(plan.Id, 1);
        Assert.True(cursor.IsExhausted);
        Assert.Equal(1L, cursor.Version);

        var exhausted = database.Advancement.AdvanceOne(
            plan.Id, 1, "exhaust-2", 1, Utc(2025, 12, 31), Utc(2026, 1, 2));
        var exhaustedRetry = database.Advancement.AdvanceOne(
            plan.Id, 1, "exhaust-3", 1, Utc(2025, 12, 31), Utc(2026, 1, 3));
        Assert.Equal("exhausted", exhausted.ResultCode);
        Assert.Equal("exhausted", exhaustedRetry.ResultCode);
        Assert.Equal(1L, exhausted.ResultCursorVersion);
        Assert.Equal(1L, database.Advancement.GetCursor(plan.Id, 1).Version);
        Assert.Equal(3L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));

        using var endDatabase = new TestDatabase();
        var endPlan = endDatabase.InsertEnabledPeriodicPlan("date-end");
        endDatabase.Schedules.CreateOrGet(endPlan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 3), Utc(2026, 1, 1));
        var endResult = endDatabase.Advancement.AdvanceOne(endPlan.Id, 1, "date-end-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        Assert.Equal("scheduled", endResult.ResultCode);
        Assert.True(endDatabase.Advancement.GetCursor(endPlan.Id, 1).IsExhausted);
        Assert.Equal(1L, Scalar(endDatabase.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void SameOperationReplaysButACompetingExpectedVersionBecomesStale()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("idempotent");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var first = database.Advancement.AdvanceOne(plan.Id, 1, "same", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        var replay = database.Advancement.AdvanceOne(plan.Id, 1, "same", 0, Utc(2025, 12, 31), Utc(2026, 1, 2));

        Assert.Equal(first, replay);
        var conflict = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.AdvanceOne(
            plan.Id, 1, "same", 0, Utc(2025, 12, 31, 1), Utc(2026, 1, 2)));
        Assert.Equal(RecurringPersistenceReasonCodes.AdvancementIdempotencyConflict, conflict.Code);
        var stale = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.AdvanceOne(
            plan.Id, 1, "different", 0, Utc(2025, 12, 31), Utc(2026, 1, 2)));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorStale, stale.Code);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
    }

    [Fact]
    public async Task SameOperationConcurrentCallsCommitOneImmutableResultAndReplayIt()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("same-concurrent");
        database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        var firstStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var first = new SqliteRecurringAdvancementTransaction(firstStore);
        var second = new SqliteRecurringAdvancementTransaction(secondStore);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureOperation(() => first.AdvanceOne(plan.Id, 1, "same-race", 0, Utc(2025, 12, 31), Utc(2026, 1, 1)))),
            Task.Run(() => CaptureOperation(() => second.AdvanceOne(plan.Id, 1, "same-race", 0, Utc(2025, 12, 31), Utc(2026, 1, 1)))));

        Assert.All(results, result => Assert.True(result.Success, result.Code));
        Assert.Equal(results[0].Operation, results[1].Operation);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1L, database.Advancement.GetCursor(plan.Id, 1).Version);
    }

    [Fact]
    public void HistoricalOperationsRemainReadableAndReplayableAfterFurtherAdvancesAndExhaustion()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("historical");
        database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        var initialAfter = Utc(2025, 12, 31);
        var first = database.Advancement.AdvanceOne(plan.Id, 1, "historical-1", 0, initialAfter, Utc(2026, 1, 1));
        var second = database.Advancement.AdvanceOne(plan.Id, 1, "historical-2", 1, initialAfter, Utc(2026, 1, 2));
        var third = database.Advancement.AdvanceOne(plan.Id, 1, "historical-3", 2, initialAfter, Utc(2026, 1, 3));
        _ = second;
        _ = third;

        var afterThree = database.Advancement.GetCursor(plan.Id, 1);
        Assert.Equal(first, database.Advancement.GetOperation(first.OperationId));
        Assert.Equal(first, database.Advancement.AdvanceOne(plan.Id, 1, first.OperationId, 0, initialAfter, Utc(2026, 1, 10)));
        Assert.Equal(afterThree, database.Advancement.GetCursor(plan.Id, 1));

        var exhausted = database.Advancement.AdvanceOne(plan.Id, 1, "historical-exhausted", 3, initialAfter, Utc(2026, 1, 4));
        Assert.Equal("exhausted", exhausted.ResultCode);
        Assert.Equal(3L, exhausted.ResultCursorVersion);
        Assert.Equal(first, database.Advancement.AdvanceOne(plan.Id, 1, first.OperationId, 0, initialAfter, Utc(2026, 1, 11)));
        Assert.Equal(4L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(3L, database.Advancement.GetCursor(plan.Id, 1).Version);
    }

    [Fact]
    public void AdvancedAtCannotRegressAndFailedAttemptLeavesAllRowsUnchanged()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("timestamp");
        database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        var initialAfter = Utc(2025, 12, 31);
        database.Advancement.AdvanceOne(plan.Id, 1, "timestamp-1", 0, initialAfter, Utc(2026, 1, 2));
        var before = database.Advancement.GetCursor(plan.Id, 1);

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.AdvanceOne(
            plan.Id, 1, "timestamp-2", 1, initialAfter, Utc(2026, 1, 1)));

        Assert.Equal(RecurringPersistenceReasonCodes.AdvancedAtRegression, exception.Code);
        Assert.Equal(before, database.Advancement.GetCursor(plan.Id, 1));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public async Task ConcurrentDifferentOperationsWithOneExpectedVersionHaveOneWinner()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("concurrent");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var firstStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var first = new SqliteRecurringAdvancementTransaction(firstStore);
        var second = new SqliteRecurringAdvancementTransaction(secondStore);

        var results = await Task.WhenAll(
            Task.Run(() => Capture(() => first.AdvanceOne(plan.Id, 1, "race-a", 0, Utc(2025, 12, 31), Utc(2026, 1, 1)))),
            Task.Run(() => Capture(() => second.AdvanceOne(plan.Id, 1, "race-b", 0, Utc(2025, 12, 31), Utc(2026, 1, 1, 0, 1)))));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(1, results.Count(result => result.Code == RecurringPersistenceReasonCodes.CursorStale));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
    }

    [Fact]
    public void RestartReplaysTheOperationAndContinuesFromThePersistedLocalCursor()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("restart");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var first = database.Advancement.AdvanceOne(plan.Id, 1, "restart-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));

        var reopened = new SqliteOperationalStore(database.Store.DatabasePath);
        var afterRestart = new SqliteRecurringAdvancementTransaction(reopened);
        Assert.Equal(first, afterRestart.AdvanceOne(plan.Id, 1, "restart-1", 0, Utc(2025, 12, 31), Utc(2027, 1, 1)));
        var second = afterRestart.AdvanceOne(plan.Id, 1, "restart-2", 1, Utc(2025, 12, 31), Utc(2026, 1, 2));
        Assert.NotEqual(first.OccurrenceIdentity, second.OccurrenceIdentity);
        Assert.Equal(new DateOnly(2026, 1, 2), afterRestart.GetCursor(plan.Id, 1).LastLocalDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailureAtAnyAtomicBoundaryRollsBackOccurrenceCursorAndOperation(int pointValue)
    {
        var point = (RecurringAdvancementFailurePoint)pointValue;
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("rollback-" + point);
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var injected = new SqliteRecurringAdvancementTransaction(
            database.Store,
            current =>
            {
                if (current == point)
                {
                    throw new InvalidOperationException("injected failure");
                }
            });

        var exception = Assert.Throws<Phase3PersistenceException>(() => injected.AdvanceOne(
            plan.Id, 1, "rollback", 0, Utc(2025, 12, 31), Utc(2026, 1, 1)));
        Assert.Equal(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, exception.Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void InvalidPlansOldRevisionsAndTimeZoneDriftFailClosedWithoutCreatingCursor()
    {
        using (var missing = new TestDatabase())
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => missing.Advancement.AdvanceOne("missing", 1, "missing-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.PlanNotFound, exception.Code);
        }

        using (var draft = new TestDatabase())
        {
            var plan = draft.InsertDraftPeriodicPlan("draft");
            var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
            draft.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            var exception = Assert.Throws<Phase3PersistenceException>(() => draft.Advancement.AdvanceOne(plan.Id, 1, "draft-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.PlanNotEnabled, exception.Code);
            Assert.Equal(0L, Scalar(draft.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        }

        using (var paused = new TestDatabase())
        {
            var plan = paused.InsertEnabledPeriodicPlan("paused");
            paused.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2), Utc(2026, 1, 1));
            SetPlanStatus(paused, plan.Id, PlanDefinitionStatus.Paused, Utc(2026, 1, 1, 0, 2));
            var exception = Assert.Throws<Phase3PersistenceException>(() => paused.Advancement.AdvanceOne(plan.Id, 1, "paused-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.PlanNotEnabled, exception.Code);
            Assert.Equal(0L, Scalar(paused.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        }

        using (var cancelled = new TestDatabase())
        {
            var plan = cancelled.InsertEnabledPeriodicPlan("cancelled");
            cancelled.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2), Utc(2026, 1, 1));
            SetPlanStatus(cancelled, plan.Id, PlanDefinitionStatus.Cancelled, Utc(2026, 1, 1, 0, 2));
            var exception = Assert.Throws<Phase3PersistenceException>(() => cancelled.Advancement.AdvanceOne(plan.Id, 1, "cancelled-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.PlanNotEnabled, exception.Code);
            Assert.Equal(0L, Scalar(cancelled.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        }

        using (var oneShot = new TestDatabase())
        {
            var plan = oneShot.InsertEnabledOneShotPlan("one-shot");
            var exception = Assert.Throws<Phase3PersistenceException>(() => oneShot.Advancement.AdvanceOne(plan.Id, 1, "one-shot-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.OneShotPlanRejected, exception.Code);
            Assert.Equal(0L, Scalar(oneShot.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        }

        using (var revisions = new TestDatabase())
        {
            var plan = revisions.InsertEnabledPeriodicPlan("revisions");
            revisions.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2), Utc(2026, 1, 1));
            revisions.Schedules.CreateOrGet(plan.Id, 2, DailySchedule(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 2), 2), Utc(2026, 1, 1));
            var exception = Assert.Throws<Phase3PersistenceException>(() => revisions.Advancement.AdvanceOne(plan.Id, 1, "old-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
            Assert.Equal(RecurringPersistenceReasonCodes.ScheduleRevisionSuperseded, exception.Code);
        }

        using var drift = new TestDatabase();
        var driftPlan = drift.InsertEnabledPeriodicPlan("drift");
        var driftSchedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2);
        drift.Schedules.CreateOrGet(driftPlan.Id, 1, driftSchedule, Utc(2026, 1, 1));
        Execute(drift.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$digest", RecurringTimeZoneRulesDigest.Prefix + new string('f', 64)), ("$plan_id", driftPlan.Id));
        var driftException = Assert.Throws<Phase3PersistenceException>(() => drift.Advancement.AdvanceOne(driftPlan.Id, 1, "drift-op", 0, Utc(2026, 1, 1), Utc(2026, 1, 1)));
        Assert.Equal(RecurringPersistenceReasonCodes.TimeZoneRulesChanged, driftException.Code);
        Assert.Equal(0L, Scalar(drift.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
    }

    [Fact]
    public void CursorAndOperationRelationTamperingIsRejectedOnRead()
    {
        using (var cursorDatabase = new TestDatabase())
        {
            var plan = cursorDatabase.InsertEnabledPeriodicPlan("cursor-tamper");
            var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3);
            cursorDatabase.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            cursorDatabase.Advancement.AdvanceOne(plan.Id, 1, "cursor-op", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
            Execute(cursorDatabase.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_cursors SET last_local_date = '2026-01-03', last_schedule_ordinal = 3, is_exhausted = 1 WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$plan_id", plan.Id));
            var exception = Assert.Throws<Phase3PersistenceException>(() => cursorDatabase.Advancement.GetCursor(plan.Id, 1));
            Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, exception.Code);
            var advanceException = Assert.Throws<Phase3PersistenceException>(() => cursorDatabase.Advancement.AdvanceOne(
                plan.Id, 1, "cursor-tamper-next", 1, Utc(2025, 12, 31), Utc(2026, 1, 2)));
            Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, advanceException.Code);
        }

        using var operationDatabase = new TestDatabase();
        var operationPlan = operationDatabase.InsertEnabledPeriodicPlan("operation-tamper");
        var operationSchedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2);
        operationDatabase.Schedules.CreateOrGet(operationPlan.Id, 1, operationSchedule, Utc(2026, 1, 1));
        operationDatabase.Advancement.AdvanceOne(operationPlan.Id, 1, "operation-op", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        var alternateDigest = RecurringAdvancementRequestDigest.Compute(operationPlan.Id, 1, 0, Utc(2025, 12, 30));
        Execute(operationDatabase.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_advancement_operations SET request_digest = $digest WHERE operation_id = 'operation-op';", ("$digest", alternateDigest));
        var operationException = Assert.Throws<Phase3PersistenceException>(() => operationDatabase.Advancement.GetOperation("operation-op"));
        Assert.Equal(RecurringPersistenceReasonCodes.OperationRelationInvalid, operationException.Code);
        var replayException = Assert.Throws<Phase3PersistenceException>(() => operationDatabase.Advancement.AdvanceOne(
            operationPlan.Id, 1, "operation-op", 0, Utc(2025, 12, 31), Utc(2026, 1, 2)));
        Assert.Equal(RecurringPersistenceReasonCodes.AdvancementIdempotencyConflict, replayException.Code);
    }

    [Fact]
    public void CursorRejectsExhaustionBeforeTheScheduleTail()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("non-tail-exhausted");
        database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        database.Advancement.AdvanceOne(plan.Id, 1, "non-tail-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        Execute(database.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_cursors SET is_exhausted = 1 WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$plan_id", plan.Id));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.GetCursor(plan.Id, 1));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, exception.Code);
        var advanceException = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.AdvanceOne(
            plan.Id, 1, "non-tail-next", 1, Utc(2025, 12, 31), Utc(2026, 1, 2)));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, advanceException.Code);
    }

    [Fact]
    public void MissingOrForgedCurrentTransitionFailsClosed()
    {
        using (var missing = new TestDatabase())
        {
            var plan = missing.InsertEnabledPeriodicPlan("missing-transition");
            missing.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
            missing.Advancement.AdvanceOne(plan.Id, 1, "missing-transition-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
            Execute(missing.Store, "PRAGMA foreign_keys = OFF; DELETE FROM recurring_advancement_operations WHERE operation_id = 'missing-transition-1';");
            var exception = Assert.Throws<Phase3PersistenceException>(() => missing.Advancement.GetCursor(plan.Id, 1));
            Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, exception.Code);
        }

        using var forged = new TestDatabase();
        var forgedPlan = forged.InsertEnabledPeriodicPlan("forged-transition");
        forged.Schedules.CreateOrGet(forgedPlan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        var first = forged.Advancement.AdvanceOne(forgedPlan.Id, 1, "forged-transition-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        var second = forged.Advancement.AdvanceOne(forgedPlan.Id, 1, "forged-transition-2", 1, Utc(2025, 12, 31), Utc(2026, 1, 2));
        Execute(forged.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_advancement_operations SET occurrence_identity = $identity WHERE operation_id = $operationId;", ("$identity", first.OccurrenceIdentity!), ("$operationId", second.OperationId));

        var forgedException = Assert.Throws<Phase3PersistenceException>(() => forged.Advancement.GetCursor(forgedPlan.Id, 1));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, forgedException.Code);
    }

    [Fact]
    public void OperationWithResultVersionBeyondCursorIsRejectedEvenWhenItsRelationIsOtherwiseValid()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("future-operation");
        database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3), Utc(2026, 1, 1));
        var first = database.Advancement.AdvanceOne(plan.Id, 1, "future-operation-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        var digest = RecurringAdvancementRequestDigest.Compute(plan.Id, 1, 1, Utc(2025, 12, 31));
        Execute(database.Store, "PRAGMA foreign_keys = OFF; UPDATE recurring_advancement_operations SET expected_cursor_version = 1, request_digest = $digest, result_cursor_version = 2 WHERE operation_id = $operationId;", ("$digest", digest), ("$operationId", first.OperationId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Advancement.GetOperation(first.OperationId));
        Assert.Equal(RecurringPersistenceReasonCodes.CursorInvalid, exception.Code);
    }

    [Fact]
    public void LongDateRangeAdvancesWithoutMaterializingTheRange()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPeriodicPlan("long-range");
        var schedule = DailySchedule(new DateOnly(2000, 1, 1), new DateOnly(9999, 12, 31), 2);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(1999, 12, 31));

        var first = database.Advancement.AdvanceOne(plan.Id, 1, "long-1", 0, Utc(1999, 12, 31), Utc(2026, 1, 1));
        Assert.Equal("scheduled", first.ResultCode);
        Assert.Equal(new DateOnly(2000, 1, 1), database.Advancement.GetCursor(plan.Id, 1).LastLocalDate);
    }

    private static (bool Success, string? Code) Capture(Func<RecurringAdvancementOperationSnapshot> action)
    {
        try
        {
            _ = action();
            return (true, null);
        }
        catch (Phase3PersistenceException exception)
        {
            return (false, exception.Code);
        }
    }

    private static (bool Success, string? Code, RecurringAdvancementOperationSnapshot? Operation) CaptureOperation(Func<RecurringAdvancementOperationSnapshot> action)
    {
        try
        {
            return (true, null, action());
        }
        catch (Phase3PersistenceException exception)
        {
            return (false, exception.Code, null);
        }
    }

    private static RecurringPlanSchedule DailySchedule(DateOnly start, DateOnly end, int maximum) =>
        RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            start,
            end,
            new TimeOnly(12, 0),
            maximum,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5));

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

    private static void SetPlanStatus(TestDatabase database, string planId, PlanDefinitionStatus status, DateTimeOffset atUtc)
    {
        var plan = database.Plans.Get(planId);
        var expectedVersion = plan.Version;
        Assert.True(plan.TryTransition(status, atUtc).Succeeded);
        database.Plans.Update(plan, expectedVersion);
    }

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AgentRecorderAdvancement_" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Store = new SqliteOperationalStore(Path.Combine(root, "state", SqliteOperationalStore.DatabaseFileName));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            Plans = new SqlitePlanDefinitionRepository(Store);
            Schedules = new SqliteRecurringScheduleVersionRepository(Store);
            Materializer = new SqliteRecurringOccurrenceMaterializationTransaction(Store);
            Advancement = new SqliteRecurringAdvancementTransaction(Store);
        }

        public SqliteOperationalStore Store { get; }
        public SqlitePlanDefinitionRepository Plans { get; }
        public SqliteRecurringScheduleVersionRepository Schedules { get; }
        public SqliteRecurringOccurrenceMaterializationTransaction Materializer { get; }
        public SqliteRecurringAdvancementTransaction Advancement { get; }

        public PlanDefinition InsertEnabledPeriodicPlan(string id)
        {
            var plan = new PlanDefinition(id, isOneTime: false, Utc(2026, 1, 1));
            Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, Utc(2026, 1, 1, 0, 1)).Succeeded);
            Plans.Insert(plan);
            return plan;
        }

        public PlanDefinition InsertDraftPeriodicPlan(string id)
        {
            var plan = new PlanDefinition(id, isOneTime: false, Utc(2026, 1, 1));
            Plans.Insert(plan);
            return plan;
        }

        public PlanDefinition InsertEnabledOneShotPlan(string id)
        {
            var plan = new PlanDefinition(id, isOneTime: true, Utc(2026, 1, 1));
            Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, Utc(2026, 1, 1, 0, 1)).Succeeded);
            Plans.Insert(plan);
            return plan;
        }

        public void Dispose()
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
}
