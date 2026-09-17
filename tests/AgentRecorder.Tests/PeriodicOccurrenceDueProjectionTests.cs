using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PeriodicOccurrenceDueProjectionTests
{
    [Fact]
    public void CandidateQueryIsBoundedOrderedAndUsesStrictStartAndOverdueSemantics()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("candidate");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), 3, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var first = database.MaterializeNext(plan.Id, schedule, null);
        var second = database.MaterializeNext(plan.Id, schedule, first.ScheduledStartUtc);
        _ = second;

        Assert.Empty(database.Candidates.ListReady(Utc(2026, 1, 1, 11, 59, 59), 10));
        var ready = database.Candidates.ListReady(Utc(2026, 1, 2, 23), 10);
        Assert.Equal(2, ready.Count);
        Assert.Equal(first.OccurrenceIdentity, ready[0].OccurrenceIdentity);
        Assert.Equal(second.OccurrenceIdentity, ready[1].OccurrenceIdentity);
        Assert.Equal(first.ScheduledStartUtc, ready[0].ScheduledStartUtc);
        Assert.Equal(first.PlannedEndUtc, ready[0].PlannedEndUtc);
        Assert.All(ready, candidate => Assert.True(candidate.ScheduledStartUtc <= Utc(2026, 1, 2, 23)));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void ProjectionUsesInclusiveLatestStartAndDoesNotUseLocalNow()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("inclusive");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.FromMinutes(2));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);

        var before = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value.AddTicks(-1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.BeforeWindow, before.ResultCode);
        Assert.False(before.Changed);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, database.Occurrences.Get(slot.OccurrenceIdentity).Status);

        var due = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc!.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
        Assert.True(due.Changed);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, due.PreviousStatus);
        Assert.Equal(PlanOccurrenceStatus.Due, due.CurrentStatus);
        Assert.Equal(1L, due.OccurrenceVersion);

        var replay = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc.Value.AddHours(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyDue, replay.ResultCode);
        Assert.False(replay.Changed);
        Assert.Equal(1L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
    }

    [Fact]
    public void LateProjectionMissesWithStableReasonAndTerminalReplayDoesNotRewrite()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("missed");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);

        var missed = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc!.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Missed, missed.ResultCode);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, missed.PreviousStatus);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.MissedScheduleWindow, missed.TerminalReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Missed, missed.CurrentStatus);
        Assert.Equal(1L, missed.OccurrenceVersion);

        var replay = database.Projection.Project(slot.OccurrenceIdentity, 0, Utc(2026, 1, 2));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyTerminal, replay.ResultCode);
        Assert.False(replay.Changed);
        Assert.Equal(1L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
    }

    [Fact]
    public void ZeroGraceUsesExactStartAsTheOnlyDueInstant()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("zero-grace");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);

        var before = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value.AddTicks(-1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.BeforeWindow, before.ResultCode);
        var exact = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, exact.ResultCode);
    }

    [Fact]
    public void PausedPlanDoesNotMutateAndReenabledPlanCanBecomeDue()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("paused");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        database.SetPlanStatus(plan.Id, PlanDefinitionStatus.Paused, Utc(2026, 1, 1, 11));

        var paused = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.PlanPaused, paused.ResultCode);
        Assert.False(paused.Changed);
        Assert.Equal(0L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);

        database.SetPlanStatus(plan.Id, PlanDefinitionStatus.Enabled, Utc(2026, 1, 1, 12));
        var due = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
    }

    [Fact]
    public void PausedOccurrenceThatMissesWhilePausedBecomesMissedAfterReenable()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("paused-late");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        database.SetPlanStatus(plan.Id, PlanDefinitionStatus.Paused, Utc(2026, 1, 1, 11));

        var paused = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc!.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.PlanPaused, paused.ResultCode);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, database.Occurrences.Get(slot.OccurrenceIdentity).Status);

        database.SetPlanStatus(plan.Id, PlanDefinitionStatus.Enabled, Utc(2026, 1, 1, 13));
        var missed = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Missed, missed.ResultCode);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.MissedScheduleWindow, missed.TerminalReasonCode);
    }

    [Fact]
    public void WeeklyScheduleProjectsDueAndMissedUsingItsPersistedUtcWindows()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("weekly");
        var schedule = RecurringPlanSchedule.CreateWeekly(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 1, 5),
            new DateOnly(2026, 1, 12),
            new TimeOnly(12, 0),
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday },
            2,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(5));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var first = database.MaterializeNext(plan.Id, schedule, null);
        var second = database.MaterializeNext(plan.Id, schedule, first.ScheduledStartUtc);

        var before = database.Projection.Project(first.OccurrenceIdentity, 0, first.ScheduledStartUtc!.Value.AddTicks(-1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.BeforeWindow, before.ResultCode);
        var due = database.Projection.Project(first.OccurrenceIdentity, 0, first.ScheduledStartUtc.Value.AddSeconds(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
        var missed = database.Projection.Project(second.OccurrenceIdentity, 0, second.LatestStartUtc!.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Missed, missed.ResultCode);
    }

    [Fact]
    public void DstOverlapUsesEarlierUtcResolutionAndGapIsSkippedFromCandidates()
    {
        var fixture = FindDstFixture();
        using (var overlap = new TestDatabase())
        {
            var plan = overlap.InsertEnabledPlan("dst-overlap");
            var schedule = RecurringPlanSchedule.CreateDaily(
                fixture.Zone.Id,
                fixture.OverlapDate,
                fixture.OverlapDate,
                fixture.OverlapTime,
                1,
                TimeSpan.FromMinutes(1),
                TimeSpan.Zero);
            overlap.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            var slot = overlap.MaterializeNext(plan.Id, schedule, null);
            Assert.Equal("schedule_ambiguous_earlier_utc", slot.ResolutionCode);
            var projection = overlap.Projection.Project(slot.OccurrenceIdentity, 0, slot.LatestStartUtc!.Value);
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, projection.ResultCode);
        }

        using var gap = new TestDatabase();
        var gapPlan = gap.InsertEnabledPlan("dst-gap");
        var gapSchedule = RecurringPlanSchedule.CreateDaily(
            fixture.Zone.Id,
            fixture.GapDate,
            fixture.GapDate,
            fixture.GapTime,
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        gap.Schedules.CreateOrGet(gapPlan.Id, 1, gapSchedule, Utc(2026, 1, 1));
        var skipped = gap.MaterializeNext(gapPlan.Id, gapSchedule, null);
        Assert.True(skipped.IsSkipped);
        Assert.Empty(gap.Candidates.ListReady(Utc(2026, 12, 31), 10));
        var exception = Assert.Throws<Phase3PersistenceException>(() => gap.Projection.Project(skipped.OccurrenceIdentity, 0, Utc(2026, 12, 31)));
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, exception.Code);
        Assert.Equal(0L, Scalar(gap.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void ReopeningStorePreservesProjectionIdempotencyAndMissedDoesNotChangeNextCycle()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("restart");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var firstAdvance = database.Advancement.AdvanceOne(plan.Id, 1, "restart-op-1", 0, Utc(2025, 12, 31), Utc(2026, 1, 1));
        var firstSlot = database.Materializer.Get(firstAdvance.OccurrenceIdentity!);
        var planBeforeMiss = database.Plans.Get(plan.Id);
        var cursorBeforeMiss = database.Advancement.GetCursor(plan.Id, 1);
        var operationBeforeMiss = database.Advancement.GetOperation(firstAdvance.OperationId);

        var restartedStore = new SqliteOperationalStore(database.Store.DatabasePath);
        restartedStore.Initialize();
        var restartedProjection = new SqlitePeriodicOccurrenceDueProjectionTransaction(restartedStore);
        var missed = restartedProjection.Project(firstSlot.OccurrenceIdentity, 0, firstSlot.LatestStartUtc!.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Missed, missed.ResultCode);
        Assert.Equal(PlanOccurrenceStatus.Missed, new SqlitePlanOccurrenceRepository(restartedStore).Get(firstSlot.OccurrenceIdentity).Status);

        var planAfterMiss = new SqlitePlanDefinitionRepository(restartedStore).Get(plan.Id);
        var cursorAfterMiss = new SqliteRecurringAdvancementTransaction(restartedStore).GetCursor(plan.Id, 1);
        var operationAfterMiss = new SqliteRecurringAdvancementTransaction(restartedStore).GetOperation(firstAdvance.OperationId);
        Assert.Equal((planBeforeMiss.Status, planBeforeMiss.Version, planBeforeMiss.UpdatedAtUtc), (planAfterMiss.Status, planAfterMiss.Version, planAfterMiss.UpdatedAtUtc));
        Assert.Equal(cursorBeforeMiss, cursorAfterMiss);
        Assert.Equal(operationBeforeMiss, operationAfterMiss);

        var reopenedAgain = new SqliteOperationalStore(database.Store.DatabasePath);
        reopenedAgain.Initialize();
        var replay = new SqlitePeriodicOccurrenceDueProjectionTransaction(reopenedAgain).Project(firstSlot.OccurrenceIdentity, 0, Utc(2026, 1, 3));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyTerminal, replay.ResultCode);
        Assert.Equal(1L, new SqlitePlanOccurrenceRepository(reopenedAgain).Get(firstSlot.OccurrenceIdentity).Version);

        var nextAdvance = new SqliteRecurringAdvancementTransaction(reopenedAgain).AdvanceOne(
            plan.Id,
            1,
            "restart-op-2",
            cursorAfterMiss.Version,
            Utc(2025, 12, 31),
            Utc(2026, 1, 2));
        var nextSlot = new SqliteRecurringOccurrenceMaterializationTransaction(reopenedAgain).Get(nextAdvance.OccurrenceIdentity!);
        var nextDue = new SqlitePeriodicOccurrenceDueProjectionTransaction(reopenedAgain).Project(nextSlot.OccurrenceIdentity, 0, nextSlot.ScheduledStartUtc!.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, nextDue.ResultCode);
        Assert.Equal(PlanOccurrenceStatus.Due, new SqlitePlanOccurrenceRepository(reopenedAgain).Get(nextSlot.OccurrenceIdentity).Status);
    }

    [Fact]
    public void CandidateLimitExcludesPausedRowsAndStillReturnsCancelledAndSupersededRows()
    {
        using var database = new TestDatabase();
        var enabledA = database.InsertEnabledPlan("enabled-a");
        var enabledB = database.InsertEnabledPlan("enabled-b");
        var pausedA = database.InsertEnabledPlan("paused-a");
        var pausedB = database.InsertEnabledPlan("paused-b");
        var draft = database.InsertDraftPlan("draft");
        var sameDay = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        foreach (var planId in new[] { enabledA.Id, enabledB.Id, pausedA.Id, pausedB.Id, draft.Id })
        {
            database.Schedules.CreateOrGet(planId, 1, sameDay, Utc(2026, 1, 1));
        }
        var enabledASlot = database.MaterializeNext(enabledA.Id, sameDay, null);
        var enabledBSlot = database.MaterializeNext(enabledB.Id, sameDay, null);
        var pausedASlot = database.MaterializeNext(pausedA.Id, sameDay, null);
        var pausedBSlot = database.MaterializeNext(pausedB.Id, sameDay, null);
        var draftSlot = database.MaterializeNext(draft.Id, sameDay, null);
        _ = (pausedASlot, pausedBSlot, draftSlot);
        database.SetPlanStatus(pausedA.Id, PlanDefinitionStatus.Paused, Utc(2026, 1, 1, 11));
        database.SetPlanStatus(pausedB.Id, PlanDefinitionStatus.Paused, Utc(2026, 1, 1, 11));

        var cancelled = database.InsertEnabledPlan("cancelled-candidate");
        database.Schedules.CreateOrGet(cancelled.Id, 1, sameDay, Utc(2026, 1, 1));
        var cancelledSlot = database.MaterializeNext(cancelled.Id, sameDay, null);
        database.SetPlanStatus(cancelled.Id, PlanDefinitionStatus.Cancelled, Utc(2026, 1, 1, 11));

        var superseded = database.InsertEnabledPlan("superseded-candidate");
        var oldSchedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        var newSchedule = DailySchedule(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(superseded.Id, 1, oldSchedule, Utc(2026, 1, 1));
        var oldSlot = database.MaterializeNext(superseded.Id, oldSchedule, null);
        database.Schedules.CreateOrGet(superseded.Id, 2, newSchedule, Utc(2026, 1, 2));

        var first = database.Candidates.ListReady(Utc(2026, 1, 2), 2);
        var second = database.Candidates.ListReady(Utc(2026, 1, 2), 2);
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(candidate => candidate.OccurrenceIdentity), second.Select(candidate => candidate.OccurrenceIdentity));
        Assert.Equal(new[] { cancelledSlot.OccurrenceIdentity, enabledASlot.OccurrenceIdentity }, first.Select(candidate => candidate.OccurrenceIdentity));

        var all = database.Candidates.ListReady(Utc(2026, 1, 2), 100).ToDictionary(candidate => candidate.OccurrenceIdentity);
        Assert.Contains(cancelledSlot.OccurrenceIdentity, all.Keys);
        Assert.Contains(oldSlot.OccurrenceIdentity, all.Keys);
        Assert.DoesNotContain(pausedASlot.OccurrenceIdentity, all.Keys);
        Assert.DoesNotContain(pausedBSlot.OccurrenceIdentity, all.Keys);
        Assert.DoesNotContain(draftSlot.OccurrenceIdentity, all.Keys);
        Assert.All(new[] { cancelledSlot.OccurrenceIdentity, oldSlot.OccurrenceIdentity }, identity => Assert.True(all.ContainsKey(identity)));
        var cancelledResult = database.Projection.Project(cancelledSlot.OccurrenceIdentity, 0, Utc(2026, 1, 2));
        var supersededResult = database.Projection.Project(oldSlot.OccurrenceIdentity, 0, Utc(2026, 1, 2));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Cancelled, cancelledResult.ResultCode);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.PlanCancelled, cancelledResult.TerminalReasonCode);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Superseded, supersededResult.ResultCode);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.ScheduleRevisionSuperseded, supersededResult.TerminalReasonCode);
    }

    [Fact]
    public void CandidateQueryTruncatesAndOrdersSameTimeByPlanRevisionAndIdentity()
    {
        using var database = new TestDatabase();
        var planA = database.InsertEnabledPlan("order-a");
        var planB = database.InsertEnabledPlan("order-b");
        var scheduleA1 = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var scheduleA2 = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero, TimeSpan.FromMinutes(2));
        database.Schedules.CreateOrGet(planA.Id, 1, scheduleA1, Utc(2026, 1, 1));
        var a1 = database.MaterializeNext(planA.Id, scheduleA1, null);
        database.Schedules.CreateOrGet(planA.Id, 2, scheduleA2, Utc(2026, 1, 2));
        var a2 = database.MaterializeNext(planA.Id, scheduleA2, null, scheduleRevision: 2);
        var scheduleB = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(planB.Id, 1, scheduleB, Utc(2026, 1, 1));
        var b1 = database.MaterializeNext(planB.Id, scheduleB, null);

        var limited = database.Candidates.ListReady(Utc(2026, 1, 2), 2);
        Assert.Equal(2, limited.Count);
        var all = database.Candidates.ListReady(Utc(2026, 1, 2), 100);
        Assert.Equal(new[] { a1.OccurrenceIdentity, a2.OccurrenceIdentity, b1.OccurrenceIdentity }, all.Select(candidate => candidate.OccurrenceIdentity));
        Assert.Equal(all.Select(candidate => candidate.OccurrenceIdentity), database.Candidates.ListReady(Utc(2026, 1, 2), 100).Select(candidate => candidate.OccurrenceIdentity));
        Assert.Empty(database.Candidates.ListReady(Utc(2025, 12, 31), 100));
    }

    [Fact]
    public void CancelledPlanAndSupersededRevisionConvergeToTerminalCancellation()
    {
        using (var cancelled = new TestDatabase())
        {
            var plan = cancelled.InsertEnabledPlan("cancelled");
            var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
            cancelled.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            var slot = cancelled.MaterializeNext(plan.Id, schedule, null);
            cancelled.SetPlanStatus(plan.Id, PlanDefinitionStatus.Cancelled, Utc(2026, 1, 1, 11));
            var result = cancelled.Projection.Project(slot.OccurrenceIdentity, 0, Utc(2026, 1, 1, 12));
            Assert.Equal(PeriodicOccurrenceDueResultCodes.Cancelled, result.ResultCode);
            Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.PlanCancelled, result.TerminalReasonCode);
            Assert.Equal(PlanOccurrenceStatus.Cancelled, result.CurrentStatus);
        }

        using var superseded = new TestDatabase();
        var supersededPlan = superseded.InsertEnabledPlan("superseded");
        var oldSchedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        var newSchedule = DailySchedule(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 1), 1, TimeSpan.Zero);
        superseded.Schedules.CreateOrGet(supersededPlan.Id, 1, oldSchedule, Utc(2026, 1, 1));
        var oldSlot = superseded.MaterializeNext(supersededPlan.Id, oldSchedule, null);
        superseded.Schedules.CreateOrGet(supersededPlan.Id, 2, newSchedule, Utc(2026, 1, 2));
        var result2 = superseded.Projection.Project(oldSlot.OccurrenceIdentity, 0, Utc(2026, 1, 1, 12));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Superseded, result2.ResultCode);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.ScheduleRevisionSuperseded, result2.TerminalReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Cancelled, result2.CurrentStatus);
    }

    [Fact]
    public void ClaimedOccurrenceConvergesWithoutRewritingItsVersion()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("claimed");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        var occurrence = database.Occurrences.Get(slot.OccurrenceIdentity);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, slot.ScheduledStartUtc!.Value).Succeeded);
        database.Occurrences.Update(occurrence, 0);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, slot.ScheduledStartUtc.Value).Succeeded);
        database.Occurrences.Update(occurrence, 1);

        var result = database.Projection.Project(slot.OccurrenceIdentity, 0, Utc(2026, 1, 2));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyClaimed, result.ResultCode);
        Assert.False(result.Changed);
        Assert.Equal(2L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
    }

    [Fact]
    public void StrictUtcAndHardLimitAreRejectedWithoutWrites()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("validation");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);

        Assert.Equal("invalid_observed_at", Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)), 1)).Code);
        Assert.Equal("invalid_limit", Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(Utc(2026, 1, 2), PeriodicOccurrenceDueProjectionLimits.MaxCandidateLimit + 1)).Code);
        Assert.Equal("invalid_observed_at", Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 0, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)))).Code);
        Assert.Equal("invalid_version", Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, -1, Utc(2026, 1, 2))).Code);
        Assert.Equal("invalid_limit", Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(Utc(2026, 1, 2), 0)).Code);
        Assert.Equal("invalid_limit", Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(Utc(2026, 1, 2), -1)).Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT version FROM plan_occurrences WHERE id = $id;", ("$id", slot.OccurrenceIdentity)));
    }

    [Fact]
    public void FutureExpectedVersionsAreConflictsButPastCompletedVersionsConverge()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("future-version");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);

        var futureBefore = Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 1, slot.ScheduledStartUtc!.Value));
        Assert.Equal("concurrency_conflict", futureBefore.Code);
        var due = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyDue, database.Projection.Project(slot.OccurrenceIdentity, 0, Utc(2026, 1, 2)).ResultCode);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.AlreadyDue, database.Projection.Project(slot.OccurrenceIdentity, 1, Utc(2026, 1, 2)).ResultCode);
        var futureAfter = Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 2, Utc(2026, 1, 2)));
        Assert.Equal("concurrency_conflict", futureAfter.Code);
        Assert.Equal(1L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
    }

    [Fact]
    public void ObservedTimeBeforeUpdatedAtIsRejectedWithoutChangingScheduledOccurrence()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("monotonic");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        var slot = database.Materializer.Materialize(candidate, Utc(2026, 1, 1, 13));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value));
        Assert.Equal("non_monotonic_time", exception.Code);
        var occurrence = database.Occurrences.Get(slot.OccurrenceIdentity);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, occurrence.Status);
        Assert.Equal(0L, occurrence.Version);
        Assert.Equal(Utc(2026, 1, 1, 13), occurrence.UpdatedAtUtc);
    }

    [Fact]
    public void DraftPeriodicPlanAndSkippedSlotAreFailClosed()
    {
        using var draft = new TestDatabase();
        var draftPlan = draft.InsertDraftPlan("draft-project");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        draft.Schedules.CreateOrGet(draftPlan.Id, 1, schedule, Utc(2026, 1, 1));
        var draftSlot = draft.MaterializeNext(draftPlan.Id, schedule, null);
        var draftException = Assert.Throws<Phase3PersistenceException>(() => draft.Projection.Project(draftSlot.OccurrenceIdentity, 0, draftSlot.ScheduledStartUtc!.Value));
        Assert.Equal(RecurringPersistenceReasonCodes.PlanNotEnabled, draftException.Code);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, draft.Occurrences.Get(draftSlot.OccurrenceIdentity).Status);

        var fixture = FindDstFixture();
        using var skipped = new TestDatabase();
        var skippedPlan = skipped.InsertEnabledPlan("skipped-project");
        var skippedSchedule = RecurringPlanSchedule.CreateDaily(fixture.Zone.Id, fixture.GapDate, fixture.GapDate, fixture.GapTime, 1, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        skipped.Schedules.CreateOrGet(skippedPlan.Id, 1, skippedSchedule, Utc(2026, 1, 1));
        var skippedSlot = skipped.MaterializeNext(skippedPlan.Id, skippedSchedule, null);
        var skippedException = Assert.Throws<Phase3PersistenceException>(() => skipped.Projection.Project(skippedSlot.OccurrenceIdentity, 0, Utc(2026, 12, 31)));
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, skippedException.Code);
        Assert.Equal(0L, Scalar(skipped.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void PlanAndOccurrenceRowsRemainTheOnlyLifecycleMutationAndAllOtherTablesStayEmpty()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("side-effects");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2, TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var first = database.MaterializeNext(plan.Id, schedule, null);
        var second = database.MaterializeNext(plan.Id, schedule, first.ScheduledStartUtc);
        var planBefore = database.Plans.Get(plan.Id);
        var scheduleBefore = database.Schedules.Get(plan.Id, 1);
        var firstBefore = database.Materializer.Get(first.OccurrenceIdentity);
        var secondBefore = database.Materializer.Get(second.OccurrenceIdentity);

        var before = database.Projection.Project(first.OccurrenceIdentity, 0, first.ScheduledStartUtc!.Value.AddTicks(-1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.BeforeWindow, before.ResultCode);
        var due = database.Projection.Project(first.OccurrenceIdentity, 0, first.ScheduledStartUtc.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, due.ResultCode);
        var missed = database.Projection.Project(second.OccurrenceIdentity, 0, second.LatestStartUtc!.Value.AddTicks(1));
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Missed, missed.ResultCode);

        var planAfter = database.Plans.Get(plan.Id);
        Assert.Equal((planBefore.Status, planBefore.Version, planBefore.UpdatedAtUtc), (planAfter.Status, planAfter.Version, planAfter.UpdatedAtUtc));
        var scheduleAfter = database.Schedules.Get(plan.Id, 1);
        Assert.Equal((scheduleBefore.PlanId, scheduleBefore.ScheduleRevision, scheduleBefore.ScheduleDigest, scheduleBefore.TimeZoneRulesDigest, scheduleBefore.CreatedAtUtc),
            (scheduleAfter.PlanId, scheduleAfter.ScheduleRevision, scheduleAfter.ScheduleDigest, scheduleAfter.TimeZoneRulesDigest, scheduleAfter.CreatedAtUtc));
        Assert.Equal(firstBefore, database.Materializer.Get(first.OccurrenceIdentity));
        Assert.Equal(secondBefore, database.Materializer.Get(second.OccurrenceIdentity));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_advancement_operations;"));
    }

    [Fact]
    public void TwoStoresProjectOverdueOccurrenceOnceAndSecondCallIsAlreadyTerminal()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("missed-race");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        var firstStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var observed = slot.LatestStartUtc!.Value.AddTicks(1);

        var results = Task.WhenAll(
            Task.Run(() => Capture(() => new SqlitePeriodicOccurrenceDueProjectionTransaction(firstStore).Project(slot.OccurrenceIdentity, 0, observed))),
            Task.Run(() => Capture(() => new SqlitePeriodicOccurrenceDueProjectionTransaction(secondStore).Project(slot.OccurrenceIdentity, 0, observed)))).GetAwaiter().GetResult();

        Assert.All(results, result => Assert.True(result.Success, result.Code));
        Assert.Equal(1, results.Count(result => result.Value!.Changed));
        Assert.Equal(1, results.Count(result => result.Value!.ResultCode == PeriodicOccurrenceDueResultCodes.AlreadyTerminal));
        var final = database.Occurrences.Get(slot.OccurrenceIdentity);
        Assert.Equal(PlanOccurrenceStatus.Missed, final.Status);
        Assert.Equal(PeriodicOccurrenceDueTerminalReasonCodes.MissedScheduleWindow, final.TerminalReasonCode);
        Assert.Equal(1L, final.Version);
    }

    [Fact]
    public void TamperedSlotAndOccurrenceWindowsFailClosedForQueryAndProject()
    {
        AssertTamperFails(
            "UPDATE recurring_occurrence_slots SET scheduled_start_utc = scheduled_start_utc + 1 WHERE occurrence_identity = $id;",
            RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
            "slot-scheduled-start");
        AssertTamperFails(
            "UPDATE recurring_occurrence_slots SET latest_start_utc = latest_start_utc + 1 WHERE occurrence_identity = $id;",
            RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
            "slot-latest-start");
        AssertTamperFails(
            "UPDATE recurring_occurrence_slots SET planned_end_utc = planned_end_utc + 1 WHERE occurrence_identity = $id;",
            RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
            "slot-planned-end");
        AssertTamperFails(
            "UPDATE plan_occurrences SET window_start_utc = window_start_utc + 1 WHERE id = $id;",
            RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
            "occurrence-window-start");
        AssertTamperFails(
            "UPDATE plan_occurrences SET window_end_utc = window_end_utc + 1 WHERE id = $id;",
            RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
            "occurrence-window-end");
    }

    [Fact]
    public void TamperedScheduleDigestAndImmutableFactsFailClosedWithStableCodes()
    {
        using (var digest = CreateReadyDatabase("schedule-digest"))
        {
            var otherSchedule = DailySchedule(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 2), 1, TimeSpan.Zero, TimeSpan.FromMinutes(2));
            digest.Database.Schedules.CreateOrGet(digest.Plan.Id, 2, otherSchedule, Utc(2026, 1, 2));
            var otherDigest = digest.Database.Schedules.Get(digest.Plan.Id, 2).ScheduleDigest;
            var constraint = Assert.Throws<SqliteException>(() => Execute(digest.Database.Store, "UPDATE recurring_occurrence_slots SET schedule_digest = $digest WHERE occurrence_identity = $id;", ("$digest", otherDigest), ("$id", digest.Slot.OccurrenceIdentity)));
            Assert.Contains("constraint", constraint.Message, StringComparison.OrdinalIgnoreCase);
            AssertScheduledUnchanged(digest.Database, digest.Slot.OccurrenceIdentity);
        }

        using (var rules = CreateReadyDatabase("schedule-rules"))
        {
            Execute(rules.Database.Store, "UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$digest", RecurringTimeZoneRulesDigest.Prefix + new string('f', 64)), ("$plan_id", rules.Plan.Id));
            AssertDueAccessFails(rules.Database, rules.Slot.OccurrenceIdentity, RecurringPersistenceReasonCodes.TimeZoneRulesChanged);
        }

        using (var immutable = CreateReadyDatabase("schedule-facts"))
        {
            Execute(immutable.Database.Store, "UPDATE recurring_schedule_versions SET local_wall_clock_seconds = local_wall_clock_seconds + 1 WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$plan_id", immutable.Plan.Id));
            AssertDueAccessFails(immutable.Database, immutable.Slot.OccurrenceIdentity, RecurringPersistenceReasonCodes.DigestMismatch);
        }
    }

    [Fact]
    public void CanonicalOccurrenceRelationsFailClosedOrAreRejectedByForeignKeys()
    {
        using (var identity = CreateReadyDatabase("canonical-identity"))
        {
            var forgedIdentity = RecurringOccurrenceIdentity.Prefix + new string('0', 64);
            Execute(identity.Database.Store, "UPDATE recurring_occurrence_slots SET occurrence_identity = $replacement WHERE occurrence_identity = $id;", ("$replacement", forgedIdentity), ("$id", identity.Slot.OccurrenceIdentity));
            AssertDueAccessFails(identity.Database, forgedIdentity, RecurringPersistenceReasonCodes.OccurrenceIdentityConflict);
            Assert.Equal(0L, Scalar(identity.Database.Store, "SELECT version FROM plan_occurrences WHERE id = $id;", ("$id", identity.Slot.OccurrenceIdentity)));
        }

        using (var occurrenceRelation = new TestDatabase())
        {
            var plan = occurrenceRelation.InsertEnabledPlan("relation");
            var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), 2, TimeSpan.Zero);
            occurrenceRelation.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            var first = occurrenceRelation.MaterializeNext(plan.Id, schedule, null);
            var second = occurrenceRelation.MaterializeNext(plan.Id, schedule, first.ScheduledStartUtc);
            Execute(occurrenceRelation.Store, "UPDATE recurring_occurrence_slots SET occurrence_id = $replacement WHERE occurrence_identity = $id;", ("$replacement", second.OccurrenceIdentity), ("$id", first.OccurrenceIdentity));
            AssertDueAccessFails(occurrenceRelation, first.OccurrenceIdentity, RecurringPersistenceReasonCodes.OccurrenceIdentityConflict);
            Assert.Equal(0L, Scalar(occurrenceRelation.Store, "SELECT version FROM plan_occurrences WHERE id = $id;", ("$id", first.OccurrenceIdentity)));
        }
    }

    [Fact]
    public void ProjectionUpdateTriggerRollsBackAllStateAndRemovalAllowsRetry()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("trigger");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        var occurrenceBefore = database.Occurrences.Get(slot.OccurrenceIdentity);
        var planBefore = database.Plans.Get(plan.Id);
        var slotBefore = database.Materializer.Get(slot.OccurrenceIdentity);
        var countsBefore = ReadTableCounts(database.Store);
        Execute(database.Store, $"CREATE TRIGGER task252r_fail_update BEFORE UPDATE OF status_code ON plan_occurrences WHEN OLD.id = '{slot.OccurrenceIdentity}' BEGIN SELECT RAISE(ABORT, 'task252r injected failure'); END;");

        var failure = Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value));
        Assert.Equal(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, failure.Code);
        var occurrenceAfterFailure = database.Occurrences.Get(slot.OccurrenceIdentity);
        Assert.Equal((occurrenceBefore.Status, occurrenceBefore.Version, occurrenceBefore.UpdatedAtUtc, occurrenceBefore.TerminalReasonCode), (occurrenceAfterFailure.Status, occurrenceAfterFailure.Version, occurrenceAfterFailure.UpdatedAtUtc, occurrenceAfterFailure.TerminalReasonCode));
        Assert.Equal((planBefore.Status, planBefore.Version, planBefore.UpdatedAtUtc), (database.Plans.Get(plan.Id).Status, database.Plans.Get(plan.Id).Version, database.Plans.Get(plan.Id).UpdatedAtUtc));
        Assert.Equal(slotBefore, database.Materializer.Get(slot.OccurrenceIdentity));
        Assert.Equal(countsBefore, ReadTableCounts(database.Store));

        Execute(database.Store, "DROP TRIGGER task252r_fail_update;");
        var retry = database.Projection.Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc.Value);
        Assert.Equal(PeriodicOccurrenceDueResultCodes.Due, retry.ResultCode);
        Assert.Equal(1L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
    }

    [Fact]
    public async Task TwoStoresProjectTheSameIdentityOnceAndBothConverge()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("race");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        var firstStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);

        var results = await Task.WhenAll(
            Task.Run(() => Capture(() => new SqlitePeriodicOccurrenceDueProjectionTransaction(firstStore).Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value))),
            Task.Run(() => Capture(() => new SqlitePeriodicOccurrenceDueProjectionTransaction(secondStore).Project(slot.OccurrenceIdentity, 0, slot.ScheduledStartUtc!.Value))));

        Assert.All(results, result => Assert.True(result.Success, result.Code));
        Assert.Equal(1, results.Count(result => result.Value!.Changed));
        Assert.Equal(1L, database.Occurrences.Get(slot.OccurrenceIdentity).Version);
        Assert.Equal(PlanOccurrenceStatus.Due, database.Occurrences.Get(slot.OccurrenceIdentity).Status);
    }

    [Fact]
    public void TamperedImmutableSlotFactsFailClosedBeforeAnyLifecycleWrite()
    {
        using var database = new TestDatabase();
        var plan = database.InsertEnabledPlan("tamper");
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        Execute(database.Store, "UPDATE recurring_occurrence_slots SET latest_start_utc = latest_start_utc + 1 WHERE occurrence_identity = $id;", ("$id", slot.OccurrenceIdentity));

        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(Utc(2026, 1, 2), 1)).Code);
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(slot.OccurrenceIdentity, 0, Utc(2026, 1, 2))).Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT version FROM plan_occurrences WHERE id = $id;", ("$id", slot.OccurrenceIdentity)));
    }

    private static (bool Success, string? Code, PeriodicOccurrenceDueProjectionResult? Value) Capture(Func<PeriodicOccurrenceDueProjectionResult> action)
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

    private static RecurringPlanSchedule DailySchedule(DateOnly start, DateOnly end, int maximum, TimeSpan grace) =>
        RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            start,
            end,
            new TimeOnly(12, 0),
            maximum,
            TimeSpan.FromMinutes(1),
            grace);

    private static RecurringPlanSchedule DailySchedule(DateOnly start, DateOnly end, int maximum, TimeSpan grace, TimeSpan duration) =>
        RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            start,
            end,
            new TimeOnly(12, 0),
            maximum,
            duration,
            grace);

    private static ReadyFixture CreateReadyDatabase(string suffix)
    {
        var database = new TestDatabase();
        var plan = database.InsertEnabledPlan(suffix);
        var schedule = DailySchedule(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var slot = database.MaterializeNext(plan.Id, schedule, null);
        return new ReadyFixture(database, plan, slot);
    }

    private static void AssertTamperFails(string sql, string expectedCode, string suffix)
    {
        using var fixture = CreateReadyDatabase(suffix);
        Execute(fixture.Database.Store, sql, ("$id", fixture.Slot.OccurrenceIdentity));
        AssertDueAccessFails(fixture.Database, fixture.Slot.OccurrenceIdentity, expectedCode);
        AssertScheduledUnchanged(fixture.Database, fixture.Slot.OccurrenceIdentity);
    }

    private static void AssertDueAccessFails(TestDatabase database, string identity, string expectedCode)
    {
        var queryException = Assert.Throws<Phase3PersistenceException>(() => database.Candidates.ListReady(Utc(2026, 1, 2), 10));
        Assert.Equal(expectedCode, queryException.Code);
        var projectException = Assert.Throws<Phase3PersistenceException>(() => database.Projection.Project(identity, 0, Utc(2026, 1, 2)));
        Assert.Equal(expectedCode, projectException.Code);
    }

    private static void AssertScheduledUnchanged(TestDatabase database, string identity)
    {
        var occurrence = database.Occurrences.Get(identity);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, occurrence.Status);
        Assert.Null(occurrence.TerminalReasonCode);
        Assert.Equal(0L, occurrence.Version);
    }

    private sealed class ReadyFixture : IDisposable
    {
        public ReadyFixture(TestDatabase database, PlanDefinition plan, RecurringOccurrenceSlotSnapshot slot)
        {
            Database = database;
            Plan = plan;
            Slot = slot;
        }

        public TestDatabase Database { get; }
        public PlanDefinition Plan { get; }
        public RecurringOccurrenceSlotSnapshot Slot { get; }

        public void Dispose() => Database.Dispose();
    }

    private static (TimeZoneInfo Zone, DateOnly GapDate, TimeOnly GapTime, DateOnly OverlapDate, TimeOnly OverlapTime) FindDstFixture()
    {
        foreach (var id in new[] { "Pacific Standard Time", "Eastern Standard Time", "Central European Standard Time" })
        {
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                continue;
            }
            catch (InvalidTimeZoneException)
            {
                continue;
            }

            DateTime? gap = null;
            DateTime? overlap = null;
            for (var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified); local < new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Unspecified); local = local.AddMinutes(15))
            {
                if (gap is null && zone.IsInvalidTime(local))
                {
                    gap = local;
                }
                if (overlap is null && zone.IsAmbiguousTime(local))
                {
                    overlap = local;
                }
                if (gap is not null && overlap is not null)
                {
                    return (zone, DateOnly.FromDateTime(gap.Value), TimeOnly.FromDateTime(gap.Value), DateOnly.FromDateTime(overlap.Value), TimeOnly.FromDateTime(overlap.Value));
                }
            }
        }

        throw new Xunit.Sdk.XunitException("Task252R requires a detectable DST gap and overlap time zone fixture; none of the supported equivalent Windows zones is available.");
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static long Scalar(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

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

    private static (long Plans, long Schedules, long Slots, long Occurrences, long Cursors, long Operations, long Leases, long Uses, long Scopes, long Runs) ReadTableCounts(SqliteOperationalStore store)
    {
        return (
            Scalar(store, "SELECT COUNT(*) FROM plans;"),
            Scalar(store, "SELECT COUNT(*) FROM recurring_schedule_versions;"),
            Scalar(store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"),
            Scalar(store, "SELECT COUNT(*) FROM plan_occurrences;"),
            Scalar(store, "SELECT COUNT(*) FROM recurring_schedule_cursors;"),
            Scalar(store, "SELECT COUNT(*) FROM recurring_advancement_operations;"),
            Scalar(store, "SELECT COUNT(*) FROM consent_leases;"),
            Scalar(store, "SELECT COUNT(*) FROM lease_uses;"),
            Scalar(store, "SELECT COUNT(*) FROM authorized_capture_scopes;"),
            Scalar(store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AgentRecorderPeriodicDue_" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Store = new SqliteOperationalStore(Path.Combine(root, "state", SqliteOperationalStore.DatabaseFileName));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            Plans = new SqlitePlanDefinitionRepository(Store);
            Occurrences = new SqlitePlanOccurrenceRepository(Store);
            Schedules = new SqliteRecurringScheduleVersionRepository(Store);
            Materializer = new SqliteRecurringOccurrenceMaterializationTransaction(Store);
            Candidates = new SqlitePeriodicOccurrenceDueCandidateQuery(Store);
            Projection = new SqlitePeriodicOccurrenceDueProjectionTransaction(Store);
            Advancement = new SqliteRecurringAdvancementTransaction(Store);
        }

        public SqliteOperationalStore Store { get; }
        public SqlitePlanDefinitionRepository Plans { get; }
        public SqlitePlanOccurrenceRepository Occurrences { get; }
        public SqliteRecurringScheduleVersionRepository Schedules { get; }
        public SqliteRecurringOccurrenceMaterializationTransaction Materializer { get; }
        public SqlitePeriodicOccurrenceDueCandidateQuery Candidates { get; }
        public SqlitePeriodicOccurrenceDueProjectionTransaction Projection { get; }
        public SqliteRecurringAdvancementTransaction Advancement { get; }

        public PlanDefinition InsertEnabledPlan(string id)
        {
            var plan = new PlanDefinition(id, false, Utc(2026, 1, 1));
            Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, Utc(2026, 1, 1, 0, 1)).Succeeded);
            Plans.Insert(plan);
            return plan;
        }

        public PlanDefinition InsertDraftPlan(string id)
        {
            var plan = new PlanDefinition(id, false, Utc(2026, 1, 1));
            Plans.Insert(plan);
            return plan;
        }

        public RecurringOccurrenceSlotSnapshot MaterializeNext(string planId, RecurringPlanSchedule schedule, DateTimeOffset? afterUtc, long scheduleRevision = 1)
        {
            var candidate = new RecurringOccurrenceCalculator().CalculateNext(planId, scheduleRevision, schedule, afterUtc).Candidate!;
            return Materializer.Materialize(candidate, Utc(2026, 1, 1));
        }

        public void SetPlanStatus(string planId, PlanDefinitionStatus status, DateTimeOffset atUtc)
        {
            var plan = Plans.Get(planId);
            var expectedVersion = plan.Version;
            Assert.True(plan.TryTransition(status, atUtc).Succeeded);
            Plans.Update(plan, expectedVersion);
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
