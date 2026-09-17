using System.Globalization;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RecurringScheduleTests
{
    [Fact]
    public void DailyAsiaShanghaiUsesCanonicalUtcAndWindowFormula()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "Asia/Shanghai",
            new DateOnly(2026, 1, 2),
            new DateOnly(2026, 1, 2),
            new TimeOnly(10, 15, 0),
            maximumOccurrences: 1,
            recordingDuration: TimeSpan.FromMinutes(2),
            latestStartGrace: TimeSpan.FromSeconds(30));

        var result = new RecurringOccurrenceCalculator().CalculateNext("plan-shanghai", 4, schedule);

        var candidate = Assert.IsType<RecurringOccurrenceCandidate>(result.Candidate);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 15, 0, TimeSpan.Zero), candidate.ScheduledStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 15, 30, TimeSpan.Zero), candidate.LatestStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 17, 30, TimeSpan.Zero), candidate.PlannedEndUtc);
        Assert.Equal(TimeSpan.Zero, candidate.ScheduledStartUtc!.Value.Offset);
        Assert.Equal("schedule_exact", candidate.ResolutionCode);
    }

    [Fact]
    public void WeeklyDaysAreSortedMondayThroughSundayAndDuplicatesAreRemoved()
    {
        var schedule = new RecurringPlanSchedule(
            RecurringScheduleKind.Weekly,
            "UTC",
            new DateOnly(2026, 1, 5),
            new DateOnly(2026, 1, 11),
            new TimeOnly(9, 0),
            7,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero,
            new[] { DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Monday, DayOfWeek.Wednesday });

        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Sunday }, schedule.WeeklyDays);
        Assert.Equal("2026-01-05", new RecurringOccurrenceCalculator().CalculateNext("p", 1, schedule).Candidate!.LocalDate.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void DateRangeIsInclusiveAndExhaustsAfterTheLastMatchingDate()
    {
        var schedule = RecurringPlanSchedule.CreateWeekly(
            "UTC",
            new DateOnly(2026, 1, 5),
            new DateOnly(2026, 1, 7),
            new TimeOnly(9, 0),
            new[] { DayOfWeek.Wednesday },
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        var calculator = new RecurringOccurrenceCalculator();
        var candidate = calculator.CalculateNext("p", 1, schedule).Candidate;
        Assert.NotNull(candidate);
        Assert.Equal(new DateOnly(2026, 1, 7), candidate.LocalDate);
        Assert.True(calculator.CalculateNext("p", 1, schedule, candidate.ScheduledStartUtc).IsExhausted);
    }

    [Fact]
    public void MaximumOccurrencesStopsBeforeTheDateRangeEnds()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 10),
            new TimeOnly(9, 0),
            2,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var calculator = new RecurringOccurrenceCalculator();
        var first = calculator.CalculateNext("p", 1, schedule).Candidate;
        Assert.NotNull(first);
        var second = calculator.CalculateNext("p", 1, schedule, first.ScheduledStartUtc).Candidate;
        Assert.NotNull(second);
        Assert.Equal(new DateOnly(2026, 1, 2), second.LocalDate);
        Assert.True(calculator.CalculateNext("p", 1, schedule, second.ScheduledStartUtc).IsExhausted);
    }

    [Fact]
    public void SpringDstGapIsSkippedWithoutShiftingTheWallClockTime()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "America/New_York",
            new DateOnly(2026, 3, 8),
            new DateOnly(2026, 3, 8),
            new TimeOnly(2, 30),
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        var result = new RecurringOccurrenceCalculator().CalculateNext("dst-plan", 1, schedule);
        var candidate = Assert.IsType<RecurringOccurrenceCandidate>(result.Candidate);
        Assert.True(candidate.IsSkipped);
        Assert.Null(candidate.ScheduledStartUtc);
        Assert.Null(candidate.LatestStartUtc);
        Assert.Equal("schedule_local_time_invalid", candidate.ReasonCode);
        Assert.Equal(new DateOnly(2026, 3, 8), candidate.LocalDate);
        Assert.Equal(new TimeOnly(2, 30), candidate.LocalWallClockTime);
    }

    [Fact]
    public void AutumnDstOverlapChoosesTheEarlierUtcExactlyOnce()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "America/New_York",
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 1),
            new TimeOnly(1, 30),
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        var result = new RecurringOccurrenceCalculator().CalculateNext("dst-plan", 1, schedule);
        var candidate = Assert.IsType<RecurringOccurrenceCandidate>(result.Candidate);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), candidate.ScheduledStartUtc);
        Assert.Equal("schedule_ambiguous_earlier_utc", candidate.ResolutionCode);
        Assert.True(new RecurringOccurrenceCalculator().CalculateNext("dst-plan", 1, schedule, candidate.ScheduledStartUtc).IsExhausted);
    }

    [Fact]
    public void ProcessedGapIdentityPreventsReappearanceAfterRestart()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "America/New_York",
            new DateOnly(2026, 3, 8),
            new DateOnly(2026, 3, 8),
            new TimeOnly(2, 30),
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var calculator = new RecurringOccurrenceCalculator();
        var first = calculator.CalculateNext("dst-plan", 1, schedule).Candidate;
        Assert.NotNull(first);

        var restarted = new RecurringOccurrenceCalculator().CalculateNext(
            "dst-plan",
            1,
            schedule,
            processedOccurrenceIdentities: new[] { first.Identity.Value });

        Assert.True(restarted.IsExhausted);
        Assert.Equal(first.Identity, RecurringOccurrenceIdentity.Create(
            "dst-plan", 1, schedule, new DateOnly(2026, 3, 8), new TimeOnly(2, 30)));
    }

    [Fact]
    public void CanonicalDigestAndIdentityAreStableAcrossCultureAndInputOrder()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var digests = new List<(string Digest, string Identity)>();
            foreach (var cultureName in new[] { "zh-CN", "en-US" })
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                var schedule = new RecurringPlanSchedule(
                    RecurringScheduleKind.Weekly,
                    "Asia/Shanghai",
                    new DateOnly(2026, 1, 1),
                    new DateOnly(2026, 1, 31),
                    new TimeOnly(10, 0),
                    4,
                    TimeSpan.FromSeconds(7),
                    TimeSpan.FromSeconds(5),
                    new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Monday });
                var candidate = new RecurringOccurrenceCalculator().CalculateNext("stable-plan", 9, schedule).Candidate!;
                digests.Add((schedule.CanonicalDigest, candidate.Identity.Value));
            }

            Assert.Equal(digests[0], digests[1]);
            Assert.StartsWith(RecurringOccurrenceIdentity.Prefix, digests[0].Identity, StringComparison.Ordinal);
            Assert.DoesNotContain("stable-plan", digests[0].Identity, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ScheduleRevisionAndEveryLegalOccurrenceIdentityInputChangeTheIdentity()
    {
        var baseSchedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            new TimeOnly(9, 0),
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var identity = RecurringOccurrenceIdentity.Create("p", 1, baseSchedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0));
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("other", 1, baseSchedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("p", 2, baseSchedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("p", 1, baseSchedule, new DateOnly(2026, 1, 2), new TimeOnly(9, 0)));
        var changedDateRange = RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 4), new TimeOnly(9, 0), 3, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("p", 1, changedDateRange, new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("p", 1, RecurringPlanSchedule.CreateDaily("Asia/Shanghai", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3), new TimeOnly(9, 0), 3, TimeSpan.FromMinutes(1), TimeSpan.Zero), new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
        var changedWeekday = RecurringPlanSchedule.CreateWeekly("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 4), new TimeOnly(9, 0), new[] { DayOfWeek.Friday }, 3, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.NotEqual(identity, RecurringOccurrenceIdentity.Create("p", 1, changedWeekday, new DateOnly(2026, 1, 2), new TimeOnly(9, 0)));
        Assert.Equal("schedule_occurrence_time_mismatch", Assert.Throws<Phase3DomainException>(() => RecurringOccurrenceIdentity.Create("p", 1, baseSchedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0, 1))).ReasonCode);
    }

    [Fact]
    public void ScheduleRevisionMustBePositiveAndDoesNotReusePlanStateVersion()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2),
            new TimeOnly(9, 0),
            2,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);

        foreach (var invalidRevision in new long[] { 0, -1 })
        {
            var exception = Assert.Throws<Phase3DomainException>(() => RecurringOccurrenceIdentity.Create(
                "p", invalidRevision, schedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
            Assert.Equal("schedule_revision_invalid", exception.ReasonCode);
        }

        var plan = new PlanDefinition("p", false, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var before = RecurringOccurrenceIdentity.Create("p", 1, schedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0));
        Assert.Equal(0, plan.Version);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero)).Succeeded);
        Assert.True(plan.Version > 0);
        var after = RecurringOccurrenceIdentity.Create("p", 1, schedule, new DateOnly(2026, 1, 1), new TimeOnly(9, 0));
        Assert.Equal(before, after);
    }

    [Fact]
    public void IdentityRejectsOutOfRangeAndNonMatchingWeeklySlots()
    {
        var daily = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            new TimeOnly(9, 0),
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var outside = Assert.Throws<Phase3DomainException>(() => RecurringOccurrenceIdentity.Create("p", 1, daily, new DateOnly(2025, 12, 31), new TimeOnly(9, 0)));
        Assert.Equal("schedule_occurrence_date_invalid", outside.ReasonCode);

        var weekly = RecurringPlanSchedule.CreateWeekly(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 7),
            new TimeOnly(9, 0),
            new[] { DayOfWeek.Monday },
            1,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var nonMatching = Assert.Throws<Phase3DomainException>(() => RecurringOccurrenceIdentity.Create("p", 1, weekly, new DateOnly(2026, 1, 1), new TimeOnly(9, 0)));
        Assert.Equal("schedule_occurrence_date_invalid", nonMatching.ReasonCode);
    }

    [Fact]
    public void DailyLongSpanUsesDayNumberJumpAndReturnsTheLateCandidate()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(1970, 1, 1),
            new DateOnly(2099, 12, 31),
            new TimeOnly(9, 0),
            int.MaxValue,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var lateDate = new DateOnly(2099, 12, 31);

        Assert.Equal(
            (long)lateDate.DayNumber - schedule.LocalStartDate.DayNumber,
            RecurringScheduleCalendar.CountMatchingOccurrencesBefore(schedule, lateDate));
        var candidate = new RecurringOccurrenceCalculator().CalculateNext(
            "long-daily",
            1,
            schedule,
            new DateTimeOffset(2099, 12, 30, 10, 0, 0, TimeSpan.Zero)).Candidate;

        Assert.NotNull(candidate);
        Assert.Equal(lateDate, candidate.LocalDate);
    }

    [Fact]
    public void WeeklyLongSpanUsesWeekAndRemainderMathAndHonorsMaximumOccurrences()
    {
        var schedule = RecurringPlanSchedule.CreateWeekly(
            "UTC",
            new DateOnly(1970, 1, 1),
            new DateOnly(2099, 12, 31),
            new TimeOnly(9, 0),
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            2,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var lateDate = new DateOnly(2099, 12, 31);
        var countBeforeLateDate = RecurringScheduleCalendar.CountMatchingOccurrencesBefore(schedule, lateDate);
        var elapsedDays = lateDate.DayNumber - schedule.LocalStartDate.DayNumber;
        var expectedCount = (long)(elapsedDays / 7) * schedule.WeeklyDays.Count;
        for (var offset = 0; offset < elapsedDays % 7; offset++)
        {
            var day = DateOnly.FromDayNumber(schedule.LocalStartDate.DayNumber + (elapsedDays / 7) * 7 + offset).DayOfWeek;
            if (schedule.WeeklyDays.Contains(day))
            {
                expectedCount++;
            }
        }
        Assert.Equal(expectedCount, countBeforeLateDate);
        Assert.True(countBeforeLateDate > 10_000);

        var result = new RecurringOccurrenceCalculator().CalculateNext(
            "long-weekly",
            1,
            schedule,
            new DateTimeOffset(2099, 12, 30, 10, 0, 0, TimeSpan.Zero));
        Assert.True(result.IsExhausted);
    }

    [Fact]
    public void LongSpanDstEndAndProcessedIdentityStillUseBoundedJumps()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "America/New_York",
            new DateOnly(2000, 1, 1),
            new DateOnly(2099, 12, 31),
            new TimeOnly(2, 30),
            int.MaxValue,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var calculator = new RecurringOccurrenceCalculator();
        var gap = calculator.CalculateNext(
            "long-dst",
            1,
            schedule,
            new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero)).Candidate!;
        Assert.True(gap.IsSkipped);

        var afterGap = calculator.CalculateNext(
            "long-dst",
            1,
            schedule,
            new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero),
            new[] { gap.Identity.Value }).Candidate!;
        Assert.Equal(new DateOnly(2026, 3, 9), afterGap.LocalDate);

        var endCandidate = calculator.CalculateNext(
            "long-dst",
            1,
            schedule,
            new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero)).Candidate!;
        Assert.Equal(new DateOnly(2099, 12, 31), endCandidate.LocalDate);
    }

    [Fact]
    public void AfterCursorIsStrictUtcAndOnlyOneCandidateIsReturned()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 3),
            new TimeOnly(9, 0),
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var calculator = new RecurringOccurrenceCalculator();
        var first = calculator.CalculateNext("p", 1, schedule).Candidate!;
        var second = calculator.CalculateNext("p", 1, schedule, first.ScheduledStartUtc).Candidate!;
        Assert.Equal(new DateOnly(2026, 1, 2), second.LocalDate);
        var exception = Assert.Throws<Phase3DomainException>(() => calculator.CalculateNext("p", 1, schedule, second.ScheduledStartUtc!.Value.ToOffset(TimeSpan.FromHours(8))));
        Assert.Equal("schedule_query_cursor_not_utc", exception.ReasonCode);
    }

    [Fact]
    public void InvalidContractsFailClosedWithStableReasonCodes()
    {
        Assert.Equal("schedule_time_zone_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("unknown-zone", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero)).ReasonCode);
        Assert.Equal("schedule_weekdays_required", Assert.Throws<Phase3DomainException>(() => new RecurringPlanSchedule(RecurringScheduleKind.Weekly, "UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero, Array.Empty<DayOfWeek>())).ReasonCode);
        Assert.Equal("schedule_weekdays_not_allowed", Assert.Throws<Phase3DomainException>(() => new RecurringPlanSchedule(RecurringScheduleKind.Daily, "UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero, Array.Empty<DayOfWeek>())).ReasonCode);
        Assert.Equal("schedule_local_time_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0, 0, 1), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero)).ReasonCode);
        Assert.Equal("schedule_maximum_occurrences_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 0, TimeSpan.FromMinutes(1), TimeSpan.Zero)).ReasonCode);
        Assert.Equal("schedule_recording_duration_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.Zero, TimeSpan.Zero)).ReasonCode);
        Assert.Equal("schedule_recording_duration_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromSeconds(-1), TimeSpan.Zero)).ReasonCode);
        Assert.Equal("schedule_grace_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(301))).ReasonCode);
        Assert.Equal("schedule_date_range_invalid", Assert.Throws<Phase3DomainException>(() => RecurringPlanSchedule.CreateDaily("UTC", new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1), new TimeOnly(9, 0), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero)).ReasonCode);
    }

    [Fact]
    public void UtcWindowOverflowFailsClosed()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            DateOnly.MaxValue,
            DateOnly.MaxValue,
            new TimeOnly(23, 59, 59),
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

        var exception = Assert.Throws<Phase3DomainException>(() => new RecurringOccurrenceCalculator().CalculateNext("p", 1, schedule));
        Assert.Equal("schedule_utc_overflow", exception.ReasonCode);
    }
}
