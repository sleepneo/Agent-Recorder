namespace AgentRecorder.Core.Automation;

public static class RecurringScheduleAuthorizationReasonCodes
{
    public const string NoValidOccurrence = "recurring_schedule_no_valid_occurrence";
    public const string PlanIdInvalid = "recurring_schedule_authorization_plan_id_invalid";
    public const string RevisionInvalid = "recurring_schedule_authorization_revision_invalid";
    public const string DateOverflow = "recurring_schedule_authorization_date_overflow";
    public const string UtcOverflow = "recurring_schedule_authorization_utc_overflow";
}

/// <summary>
/// Computes the exact latest valid planned end in a schedule without
/// materializing the occurrence sequence.  The effective occurrence ordinal is
/// the lesser of the calendar cardinality and MaximumOccurrences; invalid DST
/// local slots consume their ordinal and are skipped when choosing the latest
/// valid end.
/// </summary>
public static class RecurringScheduleAuthorizationBounds
{
    public static DateTimeOffset GetLatestValidPlannedEndUtc(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule)
        => GetLatestValidPlannedEndUtcCore(planId, scheduleRevision, schedule, null);

    // Internal verification seam: the callback observes exact candidate
    // evaluations without exposing an occurrence enumerator to production
    // callers.  The production algorithm remains cardinality/binary-search
    // based and never materializes the occurrence sequence.
    internal static DateTimeOffset GetLatestValidPlannedEndUtcWithEvaluator(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        Action evaluationObserver) =>
        GetLatestValidPlannedEndUtcCore(planId, scheduleRevision, schedule, evaluationObserver);

    // Internal verification seam for the calendar-gap regression tests.  The
    // production path uses the same calculation through the bounded tail.
    internal static long GetMaximumMatchingDateGapTicksForTest(RecurringPlanSchedule schedule) =>
        GetMaximumMatchingDateGapTicks(schedule);

    private static DateTimeOffset GetLatestValidPlannedEndUtcCore(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        Action? evaluationObserver)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string canonicalPlanId;
        try
        {
            canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.PlanIdInvalid, exception.Message);
        }

        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        var totalMatching = CountMatchingOnOrBefore(schedule, schedule.LocalEndDate);
        var effectiveOrdinal = Math.Min(totalMatching, (long)schedule.MaximumOccurrences);
        if (effectiveOrdinal <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence, "The recurring schedule has no matching occurrence.");
        }

        var totalDurationTicks = GetTotalDurationTicks(schedule);
        var offsetBounds = GetUtcOffsetBounds(schedule.TimeZoneInfo);
        var lastPotentialOrdinal = FindLastPotentiallyRepresentableOrdinal(
            schedule, effectiveOrdinal, totalDurationTicks, offsetBounds.MaximumOffsetTicks);
        if (lastPotentialOrdinal <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence, "The recurring schedule has no potentially representable planned end.");
        }

        DateOnly? date = FindNthMatchingDate(schedule, lastPotentialOrdinal);
        var calculator = new RecurringOccurrenceCalculator();
        var firstCandidateLocalTicks = schedule.LocalDateTime(date.Value).Ticks;
        var maximumTailBacktrackTicks = GetMaximumTailBacktrackTicks(schedule, offsetBounds);
        while (date is not null)
        {
            var currentLocalTicks = schedule.LocalDateTime(date.Value).Ticks;
            if (firstCandidateLocalTicks - currentLocalTicks > maximumTailBacktrackTicks)
            {
                throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence, "The recurring schedule has no valid planned end in the bounded UTC/DST tail.");
            }

            evaluationObserver?.Invoke();
            RecurringOccurrenceCandidate candidate;
            try
            {
                candidate = calculator.CalculateCandidateForAuthorization(canonicalPlanId, scheduleRevision, schedule, date.Value);
            }
            catch (Phase3DomainException exception) when (exception.ReasonCode == RecurringScheduleReasonCodes.UtcOverflow)
            {
                date = RecurringScheduleCalendar.FindPreviousMatchingDateBefore(schedule, date.Value);
                continue;
            }

            if (candidate.PlannedEndUtc is { } plannedEndUtc)
            {
                if (plannedEndUtc.Offset != TimeSpan.Zero)
                {
                    throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.UtcOverflow, "The latest planned end was not UTC.");
                }

                return plannedEndUtc;
            }

            date = RecurringScheduleCalendar.FindPreviousMatchingDateBefore(schedule, date.Value);
        }

        throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence, "The recurring schedule has no valid planned end.");
    }

    private static long GetTotalDurationTicks(RecurringPlanSchedule schedule)
    {
        try
        {
            return checked(schedule.LatestStartGrace.Ticks + schedule.RecordingDuration.Ticks);
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.UtcOverflow, "The recurring schedule duration overflowed.");
        }
    }

    private static OffsetBounds GetUtcOffsetBounds(TimeZoneInfo timeZoneInfo)
    {
        try
        {
            var minimum = timeZoneInfo.BaseUtcOffset.Ticks;
            var maximum = minimum;
            foreach (var rule in timeZoneInfo.GetAdjustmentRules())
            {
                var standardOffset = checked(timeZoneInfo.BaseUtcOffset.Ticks + rule.BaseUtcOffsetDelta.Ticks);
                var daylightOffset = checked(standardOffset + rule.DaylightDelta.Ticks);
                minimum = Math.Min(minimum, standardOffset);
                minimum = Math.Min(minimum, daylightOffset);
                maximum = Math.Max(maximum, standardOffset);
                maximum = Math.Max(maximum, daylightOffset);
            }

            return new OffsetBounds(minimum, maximum);
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.UtcOverflow, "The recurring time-zone offset range overflowed.");
        }
    }

    private static long FindLastPotentiallyRepresentableOrdinal(
        RecurringPlanSchedule schedule,
        long effectiveOrdinal,
        long totalDurationTicks,
        long maximumOffsetTicks)
    {
        var low = 1L;
        var high = effectiveOrdinal;
        var result = 0L;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var date = FindNthMatchingDate(schedule, middle);
            if (CanPotentiallyRepresentPlannedEnd(schedule, date, totalDurationTicks, maximumOffsetTicks))
            {
                result = middle;
                low = checked(middle + 1);
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private static bool CanPotentiallyRepresentPlannedEnd(
        RecurringPlanSchedule schedule,
        DateOnly date,
        long totalDurationTicks,
        long maximumOffsetTicks)
    {
        try
        {
            // For any valid local slot, plannedEndUtc is local wall-clock
            // ticks + duration - actual UTC offset.  Using the maximum offset
            // gives a lower bound.  Once that lower bound exceeds UTC MaxValue,
            // every later local date is necessarily overflow; no probe cap is
            // involved.  The remaining exact checks only cover this bounded
            // offset uncertainty and TimeZoneInfo's bounded transition gaps.
            var localTicks = schedule.LocalDateTime(date).Ticks;
            var lowerBoundTicks = checked(localTicks + totalDurationTicks - maximumOffsetTicks);
            return lowerBoundTicks <= DateTimeOffset.MaxValue.UtcDateTime.Ticks;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long GetMaximumTailBacktrackTicks(RecurringPlanSchedule schedule, OffsetBounds offsetBounds)
    {
        try
        {
            // TimeZoneInfo's valid UTC offsets are within the min/max range
            // collected from its adjustment rules.  The same offset range
            // bounds a forward-transition invalid local interval.  Two ranges
            // plus the schedule's maximum gap between matching local dates
            // cover the uncertainty around the UTC limit, a transition that
            // crosses a local-date boundary, and the next reverse candidate.
            // This is a calendar/time-zone bound, not an occurrence-count
            // probe cap.
            var offsetRange = checked(offsetBounds.MaximumOffsetTicks - offsetBounds.MinimumOffsetTicks);
            var maximumMatchingDateGapTicks = GetMaximumMatchingDateGapTicks(schedule);
            return checked(offsetRange * 2 + maximumMatchingDateGapTicks);
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.UtcOverflow, "The recurring time-zone offset tail bound overflowed.");
        }
    }

    private static long GetMaximumMatchingDateGapTicks(RecurringPlanSchedule schedule)
    {
        if (schedule.IsDaily)
        {
            return TimeSpan.TicksPerDay;
        }

        var orderedDayIndices = schedule.WeeklyDays
            .Select(GetMondayBasedDayIndex)
            .OrderBy(index => index)
            .ToArray();
        var maximumGapDays = 0;
        for (var index = 1; index < orderedDayIndices.Length; index++)
        {
            maximumGapDays = Math.Max(maximumGapDays, orderedDayIndices[index] - orderedDayIndices[index - 1]);
        }

        maximumGapDays = Math.Max(
            maximumGapDays,
            orderedDayIndices[0] + 7 - orderedDayIndices[^1]);
        return checked((long)maximumGapDays * TimeSpan.TicksPerDay);
    }

    private static int GetMondayBasedDayIndex(DayOfWeek day) =>
        day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private readonly record struct OffsetBounds(long MinimumOffsetTicks, long MaximumOffsetTicks);

    private static long CountMatchingOnOrBefore(RecurringPlanSchedule schedule, DateOnly date)
    {
        try
        {
            return checked(RecurringScheduleCalendar.CountMatchingOccurrencesBefore(schedule, date) +
                (date >= schedule.LocalStartDate && date <= schedule.LocalEndDate &&
                 (!schedule.IsWeekly || schedule.WeeklyDays.Contains(date.DayOfWeek)) ? 1 : 0));
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.DateOverflow, "The recurring schedule cardinality overflowed.");
        }
    }

    private static DateOnly FindNthMatchingDate(RecurringPlanSchedule schedule, long ordinal)
    {
        var low = schedule.LocalStartDate.DayNumber;
        var high = schedule.LocalEndDate.DayNumber;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var count = CountMatchingOnOrBefore(schedule, DateOnly.FromDayNumber(middle));
            if (count >= ordinal)
            {
                high = middle;
            }
            else
            {
                low = checked(middle + 1);
            }
        }

        var result = DateOnly.FromDayNumber(low);
        if (CountMatchingOnOrBefore(schedule, result) < ordinal)
        {
            throw new Phase3DomainException(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence, "The requested occurrence ordinal was not found.");
        }

        return result;
    }
}
