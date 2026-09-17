using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringSchedulePersistenceTests
{
    [Fact]
    public void TimeZoneRulesDigestIsCultureIndependentAndChangesWhenRulesChange()
    {
        var zone = TimeZoneInfo.Utc;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var first = RecurringTimeZoneRulesDigest.Compute(zone);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal(first, RecurringTimeZoneRulesDigest.Compute(zone));

            var customZero = TimeZoneInfo.CreateCustomTimeZone("recurring-test-zone", TimeSpan.Zero, "display", "standard");
            var customOneHour = TimeZoneInfo.CreateCustomTimeZone("recurring-test-zone", TimeSpan.FromHours(1), "display", "standard");
            Assert.NotEqual(RecurringTimeZoneRulesDigest.Compute(customZero), RecurringTimeZoneRulesDigest.Compute(customOneHour));

            var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
            var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
            var ruleWithoutBaseDelta = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end, TimeSpan.Zero);
            var ruleWithBaseDelta = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end, TimeSpan.FromMinutes(30));
            var firstRules = TimeZoneInfo.CreateCustomTimeZone("recurring-base-delta-zone", TimeSpan.Zero, "display", "standard", "daylight", new[] { ruleWithoutBaseDelta }, false);
            var secondRules = TimeZoneInfo.CreateCustomTimeZone("recurring-base-delta-zone", TimeSpan.Zero, "display", "standard", "daylight", new[] { ruleWithBaseDelta }, false);
            Assert.NotEqual(RecurringTimeZoneRulesDigest.Compute(firstRules), RecurringTimeZoneRulesDigest.Compute(secondRules));
            Assert.Equal(RecurringTimeZoneRulesDigest.Compute(firstRules), RecurringTimeZoneRulesDigest.Compute(firstRules));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ScheduleVersionRoundTripsLosslessFieldsAndDoesNotUsePlanVersion()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = RecurringPlanSchedule.CreateWeekly(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            new TimeOnly(8, 9, 10),
            new[] { DayOfWeek.Monday, DayOfWeek.Sunday },
            maximumOccurrences: 9,
            recordingDuration: TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 7 + 1234),
            latestStartGrace: TimeSpan.FromTicks(4567));

        var created = Utc(2026, 1, 1);
        var saved = database.Schedules.CreateOrGet(plan.Id, 1, schedule, created);
        Assert.Equal(schedule.CanonicalDigest, saved.ScheduleDigest);
        Assert.Equal(schedule.RecordingDuration, saved.Schedule.RecordingDuration);
        Assert.Equal(schedule.LatestStartGrace, saved.Schedule.LatestStartGrace);
        Assert.Equal(schedule.WeekdayMask, saved.Schedule.WeekdayMask);
        Assert.Equal(schedule.CanonicalDigest, database.Schedules.Get(plan.Id, 1).ScheduleDigest);
        Assert.Equal(0L, Scalar(database.Store, "SELECT version FROM plans WHERE id = 'periodic';"));

        var changedPlan = database.Plans.Get(plan.Id);
        Assert.True(changedPlan.TryMarkUpdated(Utc(2026, 1, 2)).Succeeded);
        database.Plans.Update(changedPlan, 0);
        Assert.Equal(1L, Scalar(database.Store, "SELECT MAX(schedule_revision) FROM recurring_schedule_versions WHERE plan_id = 'periodic';"));

        var newer = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            schedule.LocalStartDate,
            schedule.LocalEndDate,
            schedule.LocalWallClockTime,
            schedule.MaximumOccurrences,
            schedule.RecordingDuration,
            schedule.LatestStartGrace);
        database.Schedules.CreateOrGet(plan.Id, 2, newer, Utc(2026, 1, 2));
        Assert.Equal(2L, Scalar(database.Store, "SELECT MAX(schedule_revision) FROM recurring_schedule_versions WHERE plan_id = 'periodic';"));
    }

    [Fact]
    public void ScheduleVersionRejectsOneShotNonContiguousAndDigestConflicts()
    {
        using var database = new TestDatabase();
        var oneShot = database.InsertPlan("one-shot", isOneTime: true);
        var schedule = DailySchedule();
        Assert.Equal(RecurringPersistenceReasonCodes.OneShotPlanRejected, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.CreateOrGet(oneShot.Id, 1, schedule, Utc(2026, 1, 1))).Code);

        var plan = database.InsertPlan("periodic", isOneTime: false);
        Assert.Equal(RecurringPersistenceReasonCodes.RevisionNotContiguous, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.CreateOrGet(plan.Id, 2, schedule, Utc(2026, 1, 1))).Code);
        var first = database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var retry = database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 2));
        Assert.Equal(first.CreatedAtUtc, retry.CreatedAtUtc);

        Assert.Equal(RecurringPersistenceReasonCodes.RevisionConflict, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.CreateOrGet(plan.Id, 1, DailySchedule(duration: TimeSpan.FromMinutes(2)), Utc(2026, 1, 2))).Code);
        Assert.Equal(RecurringPersistenceReasonCodes.DigestConflict, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.CreateOrGet(plan.Id, 2, schedule, Utc(2026, 1, 2))).Code);
    }

    [Fact]
    public void TamperedTimeZoneRulesDigestFailsClosed()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        Execute(database.Store, "UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$digest", RecurringTimeZoneRulesDigest.Prefix + new string('f', 64)), ("$plan_id", plan.Id));

        Assert.Equal(RecurringPersistenceReasonCodes.TimeZoneRulesChanged, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.Get(plan.Id, 1)).Code);
    }

    [Fact]
    public void TimeZoneDriftAllowsOnlyARevisionWithANewRulesDigest()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        var first = database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var simulatedOldRulesDigest = RecurringTimeZoneRulesDigest.Prefix + new string('f', 64);
        Execute(database.Store, "UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$digest", simulatedOldRulesDigest), ("$plan_id", plan.Id));

        Assert.Equal(RecurringPersistenceReasonCodes.TimeZoneRulesChanged, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.Get(plan.Id, 1)).Code);
        var second = database.Schedules.CreateOrGet(plan.Id, 2, schedule, Utc(2026, 1, 2));
        Assert.Equal(2, second.ScheduleRevision);
        Assert.Equal(second.ScheduleDigest, database.Schedules.GetLatest(plan.Id).ScheduleDigest);
        Assert.Equal(simulatedOldRulesDigest, ScalarText(database.Store, "SELECT time_zone_rules_digest FROM recurring_schedule_versions WHERE plan_id = $plan_id AND schedule_revision = 1;", ("$plan_id", plan.Id)));
        Assert.NotEqual(
            new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!.Identity.Value,
            new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 2, schedule).Candidate!.Identity.Value);
        Assert.Equal(RecurringPersistenceReasonCodes.DigestConflict, Assert.Throws<Phase3PersistenceException>(() => database.Schedules.CreateOrGet(plan.Id, 3, schedule, Utc(2026, 1, 3))).Code);
        Assert.Equal(first.ScheduleDigest, second.ScheduleDigest);
    }

    [Fact]
    public void VersionedDigestAndIdentityChecksRejectNonCanonicalSqlValues()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var rulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);

        using var connection = database.Store.OpenConnection();
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", schedule.CanonicalDigest.ToUpperInvariant()), ("$rules", rulesDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", "recurring-schedule/v1:" + new string('g', 64)), ("$rules", rulesDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", "recurring-schedule/v1:" + new string('f', 63)), ("$rules", rulesDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", "recurring-schedule/v2:" + new string('f', 64)), ("$rules", rulesDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", schedule.CanonicalDigest), ("$rules", "recurring-timezone-rules/v2:" + new string('f', 64))));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", schedule.CanonicalDigest), ("$rules", rulesDigest.ToUpperInvariant())));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", schedule.CanonicalDigest), ("$rules", "recurring-timezone-rules/v1:" + new string('g', 64))));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_schedule_versions (plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ('periodic', 2, $digest, 'daily', 'UTC', $rules, '2026-01-01', '2026-01-03', 43200, 0, 3, 600000000, 50000000, 0);", ("$digest", schedule.CanonicalDigest), ("$rules", "recurring-timezone-rules/v1:" + new string('f', 63))));

        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_occurrence_slots (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ($identity, 'periodic', 1, $digest, '2026-01-01', 43200, 'UTC', 'skipped', NULL, NULL, NULL, '', 'schedule_local_time_invalid', NULL, 0);", ("$identity", candidate.Identity.Value.ToUpperInvariant()), ("$digest", schedule.CanonicalDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_occurrence_slots (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ($identity, 'periodic', 1, $digest, '2026-01-01', 43200, 'UTC', 'skipped', NULL, NULL, NULL, '', 'schedule_local_time_invalid', NULL, 0);", ("$identity", "recurring-occurrence/v1:" + new string('g', 64)), ("$digest", schedule.CanonicalDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_occurrence_slots (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ($identity, 'periodic', 1, $digest, '2026-01-01', 43200, 'UTC', 'skipped', NULL, NULL, NULL, '', 'schedule_local_time_invalid', NULL, 0);", ("$identity", "recurring-occurrence/v1:" + new string('f', 63)), ("$digest", schedule.CanonicalDigest)));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO recurring_occurrence_slots (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ($identity, 'periodic', 1, $digest, '2026-01-01', 43200, 'UTC', 'skipped', NULL, NULL, NULL, '', 'schedule_local_time_invalid', NULL, 0);", ("$identity", "recurring-occurrence/v2:" + new string('f', 64)), ("$digest", schedule.CanonicalDigest)));
    }

    [Fact]
    public void ScheduledMaterializationUsesPlannedEndAndIsExactlyIdempotent()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule(duration: TimeSpan.FromMinutes(3), grace: TimeSpan.FromMinutes(1));
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;

        var first = database.Materializer.Materialize(candidate, Utc(2026, 1, 1, 0, 1));
        var retry = database.Materializer.Materialize(candidate, Utc(2026, 1, 1, 0, 2));

        Assert.Equal(first, retry);
        Assert.Equal(candidate.Identity.Value, first.OccurrenceIdentity);
        Assert.Equal(candidate.PlannedEndUtc, first.PlannedEndUtc);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(candidate.PlannedEndUtc.Value.UtcDateTime.Ticks, Scalar(database.Store, "SELECT window_end_utc FROM plan_occurrences WHERE id = $id;", ("$id", candidate.Identity.Value)));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void ZeroGraceScheduledCandidateIsStillAValidOccurrence()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule(grace: TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;

        var saved = database.Materializer.Materialize(candidate, Utc(2026, 1, 1, 0, 1));
        Assert.Equal(candidate.ScheduledStartUtc, saved.LatestStartUtc);
        Assert.Equal(candidate.PlannedEndUtc, saved.PlannedEndUtc);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void DstGapOnlyCreatesSkippedSlotAndOverlapUsesEarlierUtc()
    {
        var zoneId = GetDstZoneId();
        using (var gapDatabase = new TestDatabase())
        {
            var plan = gapDatabase.InsertPlan("gap", isOneTime: false);
            var schedule = RecurringPlanSchedule.CreateDaily(zoneId, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8), new TimeOnly(2, 30), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero);
            gapDatabase.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
            var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
            var saved = gapDatabase.Materializer.Materialize(candidate, Utc(2026, 1, 1));
            Assert.True(saved.IsSkipped);
            Assert.Equal(RecurringScheduleReasonCodes.WallClockTimeInvalid, saved.TerminalReasonCode);
            Assert.Equal(0L, Scalar(gapDatabase.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        }

        using var overlapDatabase = new TestDatabase();
        var overlapPlan = overlapDatabase.InsertPlan("overlap", isOneTime: false);
        var overlapSchedule = RecurringPlanSchedule.CreateDaily(zoneId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), new TimeOnly(1, 30), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        overlapDatabase.Schedules.CreateOrGet(overlapPlan.Id, 1, overlapSchedule, Utc(2026, 1, 1));
        var overlapCandidate = new RecurringOccurrenceCalculator().CalculateNext(overlapPlan.Id, 1, overlapSchedule).Candidate!;
        var overlapSaved = overlapDatabase.Materializer.Materialize(overlapCandidate, Utc(2026, 1, 1));
        Assert.Equal("schedule_ambiguous_earlier_utc", overlapSaved.ResolutionCode);
        Assert.Equal(overlapCandidate.ScheduledStartUtc, overlapSaved.ScheduledStartUtc);
    }

    [Fact]
    public void CandidateMismatchAndPreexistingOccurrenceLeaveNoHalfWrittenChain()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        var otherSchedule = DailySchedule(duration: TimeSpan.FromMinutes(2));
        var mismatch = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, otherSchedule).Candidate!;
        Assert.Equal(RecurringPersistenceReasonCodes.CandidateMismatch, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Materialize(mismatch, Utc(2026, 1, 1))).Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));

        var preexisting = new PlanOccurrence(candidate.Identity.Value, plan.Id, candidate.ScheduledStartUtc!.Value, candidate.PlannedEndUtc!.Value, Utc(2026, 1, 1));
        database.Occurrences.Insert(preexisting);
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Materialize(candidate, Utc(2026, 1, 1))).Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void InjectedFailureAfterOccurrenceInsertRollsBackTheOccurrenceAndSlotTogether()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        var injected = new SqliteRecurringOccurrenceMaterializationTransaction(database.Store, () => throw new InvalidOperationException("injected"));

        Assert.Equal(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, Assert.Throws<Phase3PersistenceException>(() => injected.Materialize(candidate, Utc(2026, 1, 1))).Code);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    [Fact]
    public void SlotGetRejectsSemanticLocalAndOccurrenceRelationTampering()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        database.Materializer.Materialize(candidate, Utc(2026, 1, 1));
        Assert.Equal(candidate.Identity.Value, database.Materializer.Get(candidate.Identity.Value).OccurrenceIdentity);

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "UPDATE recurring_occurrence_slots SET local_date = '2026-01-02' WHERE occurrence_identity = $identity;", ("$identity", candidate.Identity.Value));
        }
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(candidate.Identity.Value)).Code);

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "UPDATE recurring_occurrence_slots SET local_date = '2026-01-01' WHERE occurrence_identity = $identity;", ("$identity", candidate.Identity.Value));
            Execute(connection, "UPDATE plan_occurrences SET window_end_utc = window_end_utc + 1 WHERE id = $identity;", ("$identity", candidate.Identity.Value));
        }
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(candidate.Identity.Value)).Code);

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "UPDATE plan_occurrences SET window_end_utc = window_end_utc - 1 WHERE id = $identity;", ("$identity", candidate.Identity.Value));
            Execute(connection, "UPDATE recurring_occurrence_slots SET schedule_digest = $digest WHERE occurrence_identity = $identity;", ("$digest", RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), new TimeOnly(12, 0), 4, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5)).CanonicalDigest), ("$identity", candidate.Identity.Value));
        }
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(candidate.Identity.Value)).Code);

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "UPDATE recurring_occurrence_slots SET schedule_digest = $digest, time_zone_id = 'Pacific Standard Time' WHERE occurrence_identity = $identity;", ("$digest", schedule.CanonicalDigest), ("$identity", candidate.Identity.Value));
        }
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(candidate.Identity.Value)).Code);

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "DELETE FROM plan_occurrences WHERE id = $identity;", ("$identity", candidate.Identity.Value));
        }
        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(candidate.Identity.Value)).Code);
    }

    [Fact]
    public void SkippedSlotGetRejectsAForgedPlanOccurrenceRelation()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("gap", isOneTime: false);
        var zoneId = GetDstZoneId();
        var schedule = RecurringPlanSchedule.CreateDaily(zoneId, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8), new TimeOnly(2, 30), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        var slot = database.Materializer.Materialize(candidate, Utc(2026, 1, 1));

        using (var connection = database.Store.OpenConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "INSERT INTO plan_occurrences (id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ($id, $plan_id, 'scheduled', 1, 2, 0, 0, 0);", ("$id", slot.OccurrenceIdentity), ("$plan_id", plan.Id));
        }

        Assert.Equal(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, Assert.Throws<Phase3PersistenceException>(() => database.Materializer.Get(slot.OccurrenceIdentity)).Code);
    }

    [Fact]
    public async Task ConcurrentMaterializationConvergesAndRestartPreservesUtcFacts()
    {
        using var database = new TestDatabase();
        var plan = database.InsertPlan("periodic", isOneTime: false);
        var schedule = DailySchedule();
        database.Schedules.CreateOrGet(plan.Id, 1, schedule, Utc(2026, 1, 1));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(plan.Id, 1, schedule).Candidate!;
        var firstStore = new SqliteOperationalStore(database.Store.DatabasePath);
        var secondStore = new SqliteOperationalStore(database.Store.DatabasePath);

        var results = await Task.WhenAll(
            Task.Run(() => new SqliteRecurringOccurrenceMaterializationTransaction(firstStore).Materialize(candidate, Utc(2026, 1, 1))),
            Task.Run(() => new SqliteRecurringOccurrenceMaterializationTransaction(secondStore).Materialize(candidate, Utc(2026, 1, 1, 0, 1))));

        Assert.Equal(results[0], results[1]);
        var storedStart = Scalar(database.Store, "SELECT scheduled_start_utc FROM recurring_occurrence_slots WHERE occurrence_identity = $id;", ("$id", candidate.Identity.Value));
        var reopened = new SqliteOperationalStore(database.Store.DatabasePath);
        var readBack = new SqliteRecurringOccurrenceMaterializationTransaction(reopened).Get(candidate.Identity.Value);
        Assert.Equal(storedStart, readBack.ScheduledStartUtc!.Value.UtcDateTime.Ticks);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
    }

    private static RecurringPlanSchedule DailySchedule(TimeSpan? duration = null, TimeSpan? grace = null) =>
        RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            new TimeOnly(12, 0),
            3,
            duration ?? TimeSpan.FromMinutes(1),
            grace ?? TimeSpan.FromSeconds(5));

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

    private static string ScalarText(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return (string?)command.ExecuteScalar() ?? string.Empty;
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

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
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
        private readonly string root = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurring_" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Store = new SqliteOperationalStore(Path.Combine(root, "state", SqliteOperationalStore.DatabaseFileName));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            Plans = new SqlitePlanDefinitionRepository(Store);
            Occurrences = new SqlitePlanOccurrenceRepository(Store);
            Schedules = new SqliteRecurringScheduleVersionRepository(Store);
            Materializer = new SqliteRecurringOccurrenceMaterializationTransaction(Store);
        }

        public SqliteOperationalStore Store { get; }
        public SqlitePlanDefinitionRepository Plans { get; }
        public SqlitePlanOccurrenceRepository Occurrences { get; }
        public SqliteRecurringScheduleVersionRepository Schedules { get; }
        public SqliteRecurringOccurrenceMaterializationTransaction Materializer { get; }

        public PlanDefinition InsertPlan(string id, bool isOneTime)
        {
            var plan = new PlanDefinition(id, isOneTime, Utc(2026, 1, 1));
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
