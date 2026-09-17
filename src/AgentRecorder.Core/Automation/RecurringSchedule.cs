using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

/// <summary>
/// The deliberately small recurring-schedule vocabulary supported by phase 2.
/// It is not a cron or RRULE parser.
/// </summary>
public enum RecurringScheduleKind
{
    Daily = 1,
    Weekly = 2,
}

public static class RecurringScheduleReasonCodes
{
    public const string TimeZoneInvalid = "schedule_time_zone_invalid";
    public const string WallClockTimeInvalid = "schedule_local_time_invalid";
    public const string WeekdaysRequired = "schedule_weekdays_required";
    public const string WeekdaysNotAllowed = "schedule_weekdays_not_allowed";
    public const string WeekdayInvalid = "schedule_weekday_invalid";
    public const string DateRangeInvalid = "schedule_date_range_invalid";
    public const string MaximumOccurrencesInvalid = "schedule_maximum_occurrences_invalid";
    public const string RecordingDurationInvalid = "schedule_recording_duration_invalid";
    public const string GraceInvalid = "schedule_grace_invalid";
    public const string RevisionInvalid = "schedule_revision_invalid";
    public const string QueryCursorNotUtc = "schedule_query_cursor_not_utc";
    public const string QueryCursorInvalid = "schedule_query_cursor_invalid";
    public const string DateOverflow = "schedule_date_overflow";
    public const string UtcOverflow = "schedule_utc_overflow";
    public const string OccurrenceDateInvalid = "schedule_occurrence_date_invalid";
    public const string OccurrenceTimeMismatch = "schedule_occurrence_time_mismatch";
    public const string Exhausted = "schedule_exhausted";
    public const string CursorInvalid = "schedule_cursor_invalid";
    public const string CursorRevisionMismatch = "schedule_cursor_revision_mismatch";
}

/// <summary>
/// An immutable, canonicalized daily or weekly schedule. TimeZoneInfo is the
/// only authority used to resolve the supplied Windows/IANA-compatible ID.
/// </summary>
public sealed class RecurringPlanSchedule
{
    public const int CanonicalVersion = 1;

    public RecurringPlanSchedule(
        RecurringScheduleKind kind,
        string timeZoneId,
        DateOnly localStartDate,
        DateOnly localEndDate,
        TimeOnly localWallClockTime,
        int maximumOccurrences,
        TimeSpan recordingDuration,
        TimeSpan latestStartGrace,
        IEnumerable<DayOfWeek>? weeklyDays = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new Phase3DomainException("schedule_kind_invalid", "The recurring schedule kind is not supported.");
        }

        if (localEndDate < localStartDate)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.DateRangeInvalid, "The local schedule date range must be inclusive and ordered.");
        }

        if (localWallClockTime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.WallClockTimeInvalid, "The local wall-clock time must be exact to whole seconds.");
        }

        if (maximumOccurrences <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.MaximumOccurrencesInvalid, "The maximum occurrence count must be positive.");
        }

        if (recordingDuration <= TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RecordingDurationInvalid, "The recording duration must be positive.");
        }

        if (latestStartGrace < TimeSpan.Zero || latestStartGrace > TimeSpan.FromMinutes(5))
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.GraceInvalid, "The latest-start grace must be from zero through 300 seconds.");
        }

        TimeZoneInfo = ResolveTimeZone(timeZoneId);
        Kind = kind;
        LocalStartDate = localStartDate;
        LocalEndDate = localEndDate;
        LocalWallClockTime = localWallClockTime;
        MaximumOccurrences = maximumOccurrences;
        RecordingDuration = recordingDuration;
        LatestStartGrace = latestStartGrace;

        var suppliedWeeklyDays = weeklyDays is null ? null : weeklyDays.ToArray();
        if (kind == RecurringScheduleKind.Daily)
        {
            if (suppliedWeeklyDays is not null)
            {
                throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdaysNotAllowed, "A daily schedule must not carry a weekday collection.");
            }

            WeeklyDays = Array.AsReadOnly(Array.Empty<DayOfWeek>());
        }
        else
        {
            if (suppliedWeeklyDays is null || suppliedWeeklyDays.Length == 0)
            {
                throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdaysRequired, "A weekly schedule must carry at least one weekday.");
            }

            foreach (var day in suppliedWeeklyDays)
            {
                if (!Enum.IsDefined(day))
                {
                    throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdayInvalid, "The weekly weekday value is not recognized.");
                }
            }

            WeeklyDays = new ReadOnlyCollection<DayOfWeek>(
                suppliedWeeklyDays
                    .Distinct()
                    .OrderBy(DaySortOrder)
                    .ToArray());
        }

        CanonicalDigest = RecurringScheduleCanonicalization.ComputeScheduleDigest(this);
    }

    public static RecurringPlanSchedule CreateDaily(
        string timeZoneId,
        DateOnly localStartDate,
        DateOnly localEndDate,
        TimeOnly localWallClockTime,
        int maximumOccurrences,
        TimeSpan recordingDuration,
        TimeSpan latestStartGrace) =>
        new(
            RecurringScheduleKind.Daily,
            timeZoneId,
            localStartDate,
            localEndDate,
            localWallClockTime,
            maximumOccurrences,
            recordingDuration,
            latestStartGrace);

    public static RecurringPlanSchedule CreateWeekly(
        string timeZoneId,
        DateOnly localStartDate,
        DateOnly localEndDate,
        TimeOnly localWallClockTime,
        IEnumerable<DayOfWeek> weeklyDays,
        int maximumOccurrences,
        TimeSpan recordingDuration,
        TimeSpan latestStartGrace) =>
        new(
            RecurringScheduleKind.Weekly,
            timeZoneId,
            localStartDate,
            localEndDate,
            localWallClockTime,
            maximumOccurrences,
            recordingDuration,
            latestStartGrace,
            weeklyDays);

    public RecurringScheduleKind Kind { get; }

    /// <summary>The resolved TimeZoneInfo.Id, never a localized display name.</summary>
    public string TimeZoneId => TimeZoneInfo.Id;

    public TimeZoneInfo TimeZoneInfo { get; }

    public DateOnly LocalStartDate { get; }

    public DateOnly LocalEndDate { get; }

    public TimeOnly LocalWallClockTime { get; }

    public int MaximumOccurrences { get; }

    public TimeSpan RecordingDuration { get; }

    public TimeSpan LatestStartGrace { get; }

    public TimeSpan LatestStartGracePeriod => LatestStartGrace;

    public IReadOnlyList<DayOfWeek> WeeklyDays { get; }

    /// <summary>
    /// Monday is bit 0 and Sunday is bit 6. Daily schedules deliberately use
    /// zero so the persisted representation cannot be mistaken for a weekly
    /// rule.
    /// </summary>
    public int WeekdayMask => Kind == RecurringScheduleKind.Daily
        ? 0
        : WeeklyDays.Aggregate(0, (mask, day) => mask | (1 << DayMaskBit(day)));

    public string CanonicalDigest { get; }

    public string ScheduleDigest => CanonicalDigest;

    public bool IsDaily => Kind == RecurringScheduleKind.Daily;

    public bool IsWeekly => Kind == RecurringScheduleKind.Weekly;

    internal DateTime LocalDateTime(DateOnly localDate) =>
        localDate.ToDateTime(LocalWallClockTime, DateTimeKind.Unspecified);

    private static TimeZoneInfo ResolveTimeZone(string? requestedId)
    {
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.TimeZoneInvalid, "The recurring schedule time-zone ID must not be blank.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(requestedId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.TimeZoneInvalid, "The recurring schedule time-zone ID cannot be resolved.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.TimeZoneInvalid, "The recurring schedule time-zone data is invalid.");
        }
        catch (ArgumentException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.TimeZoneInvalid, "The recurring schedule time-zone ID is invalid.");
        }
    }

    private static int DaySortOrder(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 6,
        _ => throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdayInvalid, "The weekly weekday value is not recognized."),
    };

    private static int DayMaskBit(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 6,
        _ => throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdayInvalid, "The weekly weekday value is not recognized."),
    };
}

public sealed record RecurringOccurrenceIdentity
{
    public const int CanonicalVersion = RecurringPlanSchedule.CanonicalVersion;
    public const string Prefix = "recurring-occurrence/v1:";

    private RecurringOccurrenceIdentity(string value, string digest)
    {
        Value = value;
        Digest = digest;
    }

    public string Value { get; }

    public string Digest { get; }

    internal static RecurringOccurrenceIdentity Create(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateOnly localDate,
        TimeOnly localWallClockTime)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        if (localDate < schedule.LocalStartDate || localDate > schedule.LocalEndDate ||
            (schedule.IsWeekly && !schedule.WeeklyDays.Contains(localDate.DayOfWeek)))
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.OccurrenceDateInvalid, "The occurrence date is not a matching date in the schedule.");
        }

        if (localWallClockTime != schedule.LocalWallClockTime)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.OccurrenceTimeMismatch, "The occurrence wall-clock time does not match the schedule.");
        }

        var bytes = RecurringScheduleCanonicalization.Start("recurring-occurrence/v1");
        RecurringScheduleCanonicalization.AppendString(bytes, canonicalPlanId);
        RecurringScheduleCanonicalization.AppendInt64(bytes, scheduleRevision);
        RecurringScheduleCanonicalization.AppendString(bytes, schedule.CanonicalDigest);
        RecurringScheduleCanonicalization.AppendString(bytes, schedule.TimeZoneId);
        RecurringScheduleCanonicalization.AppendString(bytes, localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        RecurringScheduleCanonicalization.AppendInt64(bytes, localWallClockTime.Ticks);

        var digest = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
        return new RecurringOccurrenceIdentity(Prefix + digest, digest);
    }

    public override string ToString() => Value;
}

public sealed record RecurringOccurrenceCandidate
{
    internal RecurringOccurrenceCandidate(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateOnly localDate,
        TimeOnly localWallClockTime,
        RecurringOccurrenceIdentity identity,
        DateTimeOffset? scheduledStartUtc,
        DateTimeOffset? latestStartUtc,
        DateTimeOffset? plannedEndUtc,
        string reasonCode,
        string resolutionCode)
    {
        PlanId = planId;
        ScheduleRevision = scheduleRevision;
        ScheduleDigest = schedule.CanonicalDigest;
        TimeZoneId = schedule.TimeZoneId;
        LocalDate = localDate;
        LocalWallClockTime = localWallClockTime;
        Identity = identity;
        ScheduledStartUtc = scheduledStartUtc;
        LatestStartUtc = latestStartUtc;
        PlannedEndUtc = plannedEndUtc;
        ReasonCode = reasonCode;
        ResolutionCode = resolutionCode;
    }

    public string PlanId { get; }

    public long ScheduleRevision { get; }

    public string ScheduleDigest { get; }

    public string TimeZoneId { get; }

    public DateOnly LocalDate { get; }

    public TimeOnly LocalWallClockTime { get; }

    public RecurringOccurrenceIdentity Identity { get; }

    public DateTimeOffset? ScheduledStartUtc { get; }

    public DateTimeOffset? LatestStartUtc { get; }

    public DateTimeOffset? PlannedEndUtc { get; }

    public string ReasonCode { get; }

    public string ResolutionCode { get; }

    public bool IsSkipped => ScheduledStartUtc is null;

    public bool IsValid => ScheduledStartUtc is not null;
}

public sealed record RecurringOccurrenceCalculation
{
    internal RecurringOccurrenceCalculation(RecurringOccurrenceCandidate candidate)
    {
        Candidate = candidate;
        ReasonCode = candidate.ReasonCode;
    }

    private RecurringOccurrenceCalculation()
    {
        ReasonCode = RecurringScheduleReasonCodes.Exhausted;
    }

    public static RecurringOccurrenceCalculation Exhausted { get; } = new();

    public RecurringOccurrenceCandidate? Candidate { get; }

    public string ReasonCode { get; }

    public bool IsExhausted => Candidate is null;

    public bool HasCandidate => Candidate is not null;
}

/// <summary>
/// The immutable logical position used to advance one recurring local slot at
/// a time.  The persisted repository owns versioning; this value object only
/// describes the schedule revision, frozen first UTC boundary, local position
/// and exhausted state used by the calculator.
/// </summary>
public sealed record RecurringScheduleCursorPosition
{
    public RecurringScheduleCursorPosition(
        long scheduleRevision,
        DateTimeOffset initialAfterUtc,
        DateOnly? lastLocalDate,
        long lastScheduleOrdinal,
        bool isExhausted,
        long version)
    {
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.CursorInvalid, "The recurring cursor schedule revision must be positive.");
        }

        if (initialAfterUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.QueryCursorNotUtc, "The recurring cursor initial UTC boundary must be UTC.");
        }

        if (lastLocalDate is null && lastScheduleOrdinal != 0 ||
            lastLocalDate is not null && lastScheduleOrdinal <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.CursorInvalid, "The recurring cursor local date and ordinal are inconsistent.");
        }

        if (version < 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.CursorInvalid, "The recurring cursor version must not be negative.");
        }

        ScheduleRevision = scheduleRevision;
        InitialAfterUtc = initialAfterUtc;
        LastLocalDate = lastLocalDate;
        LastScheduleOrdinal = lastScheduleOrdinal;
        IsExhausted = isExhausted;
        Version = version;
    }

    public long ScheduleRevision { get; }

    public DateTimeOffset InitialAfterUtc { get; }

    public DateOnly? LastLocalDate { get; }

    public long LastScheduleOrdinal { get; }

    public bool IsExhausted { get; }

    public long Version { get; }

    public bool IsEmpty => LastLocalDate is null && LastScheduleOrdinal == 0 && !IsExhausted;
}

/// <summary>
/// Computes at most one local schedule slot. It never reads current time and
/// never materializes a collection of future occurrences.
/// </summary>
public sealed class RecurringOccurrenceCalculator
{
    public long GetScheduleOrdinal(RecurringPlanSchedule schedule, DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (localDate < schedule.LocalStartDate || localDate > schedule.LocalEndDate ||
            schedule.IsWeekly && !schedule.WeeklyDays.Contains(localDate.DayOfWeek))
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.OccurrenceDateInvalid, "The occurrence date is not a matching date in the schedule.");
        }

        return checked(RecurringScheduleCalendar.CountMatchingOccurrencesBefore(schedule, localDate) + 1);
    }

    public bool HasLaterMatchingDate(RecurringPlanSchedule schedule, DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return RecurringScheduleCalendar.FindNextMatchingDateAfter(schedule, localDate) is not null;
    }

    /// <summary>
    /// Resolves one already-selected local date using the exact same DST and
    /// overflow rules as the occurrence calculator.  This is intentionally an
    /// internal seam for authorization-bound calculation; callers must not use
    /// it as a second occurrence enumeration API.
    /// </summary>
    internal RecurringOccurrenceCandidate CalculateCandidateForAuthorization(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        if (localDate < schedule.LocalStartDate || localDate > schedule.LocalEndDate ||
            (schedule.IsWeekly && !schedule.WeeklyDays.Contains(localDate.DayOfWeek)))
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.OccurrenceDateInvalid, "The occurrence date is not a matching date in the schedule.");
        }

        var identity = RecurringOccurrenceIdentity.Create(
            canonicalPlanId,
            scheduleRevision,
            schedule,
            localDate,
            schedule.LocalWallClockTime);
        return CreateCandidate(canonicalPlanId, scheduleRevision, schedule, localDate, identity, null, null)
            ?? throw new Phase3DomainException(RecurringScheduleReasonCodes.OccurrenceDateInvalid, "The authorization candidate could not be resolved.");
    }

    public RecurringOccurrenceCalculation CalculateNextFromCursor(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        RecurringScheduleCursorPosition cursor)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(cursor);
        var canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        if (cursor.ScheduleRevision != scheduleRevision)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.CursorRevisionMismatch, "The recurring cursor belongs to another schedule revision.");
        }

        if (cursor.IsExhausted)
        {
            return RecurringOccurrenceCalculation.Exhausted;
        }

        if (cursor.LastLocalDate is null)
        {
            return CalculateNext(canonicalPlanId, scheduleRevision, schedule, cursor.InitialAfterUtc);
        }

        var localDate = RecurringScheduleCalendar.FindNextMatchingDateAfter(schedule, cursor.LastLocalDate.Value);
        if (localDate is null)
        {
            return RecurringOccurrenceCalculation.Exhausted;
        }

        var ordinal = GetScheduleOrdinal(schedule, localDate.Value);
        if (ordinal > schedule.MaximumOccurrences)
        {
            return RecurringOccurrenceCalculation.Exhausted;
        }

        var identity = RecurringOccurrenceIdentity.Create(
            canonicalPlanId,
            scheduleRevision,
            schedule,
            localDate.Value,
            schedule.LocalWallClockTime);
        var candidate = CreateCandidate(
            canonicalPlanId,
            scheduleRevision,
            schedule,
            localDate.Value,
            identity,
            afterUtc: null,
            localCursor: null);

        return candidate is null
            ? RecurringOccurrenceCalculation.Exhausted
            : new RecurringOccurrenceCalculation(candidate);
    }

    public RecurringOccurrenceCalculation CalculateNext(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateTimeOffset? afterUtc = null,
        IEnumerable<string>? processedOccurrenceIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        if (afterUtc is not null && afterUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.QueryCursorNotUtc, "The recurring occurrence query cursor must use UTC.");
        }

        var processed = processedOccurrenceIdentities is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : processedOccurrenceIdentities.ToHashSet(StringComparer.Ordinal);

        DateTime? localCursor = null;
        if (afterUtc is not null)
        {
            try
            {
                localCursor = TimeZoneInfo.ConvertTime(afterUtc.Value, schedule.TimeZoneInfo).DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new Phase3DomainException(RecurringScheduleReasonCodes.QueryCursorInvalid, "The recurring occurrence query cursor cannot be represented in the schedule time zone.");
            }
        }

        var localDate = FindInitialDate(schedule, afterUtc, localCursor);
        if (localDate is null)
        {
            return RecurringOccurrenceCalculation.Exhausted;
        }

        var matchingOccurrenceCount = RecurringScheduleCalendar.CountMatchingOccurrencesBefore(schedule, localDate.Value);
        while (true)
        {
            matchingOccurrenceCount++;
            if (matchingOccurrenceCount > schedule.MaximumOccurrences)
            {
                return RecurringOccurrenceCalculation.Exhausted;
            }

            var identity = RecurringOccurrenceIdentity.Create(
                canonicalPlanId,
                scheduleRevision,
                schedule,
                localDate.Value,
                schedule.LocalWallClockTime);
            if (!processed.Contains(identity.Value))
            {
                var candidate = CreateCandidate(
                    canonicalPlanId,
                    scheduleRevision,
                    schedule,
                    localDate.Value,
                    identity,
                    afterUtc,
                    localCursor);
                if (candidate is not null)
                {
                    return new RecurringOccurrenceCalculation(candidate);
                }
            }

            localDate = RecurringScheduleCalendar.FindNextMatchingDateAfter(schedule, localDate.Value);
            if (localDate is null)
            {
                return RecurringOccurrenceCalculation.Exhausted;
            }
        }
    }

    public RecurringOccurrenceCalculation GetNextCandidate(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateTimeOffset? afterUtc = null,
        IEnumerable<string>? processedOccurrenceIdentities = null) =>
        CalculateNext(planId, scheduleRevision, schedule, afterUtc, processedOccurrenceIdentities);

    private static DateOnly? FindInitialDate(
        RecurringPlanSchedule schedule,
        DateTimeOffset? afterUtc,
        DateTime? localCursor)
    {
        var firstDate = schedule.LocalStartDate;
        if (afterUtc is not null && localCursor is not null)
        {
            var cursorDate = DateOnly.FromDateTime(localCursor.Value);
            if (cursorDate > firstDate)
            {
                firstDate = cursorDate;
            }
        }

        return RecurringScheduleCalendar.FindNextMatchingDateOnOrAfter(schedule, firstDate);
    }

    private static RecurringOccurrenceCandidate? CreateCandidate(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateOnly localDate,
        RecurringOccurrenceIdentity identity,
        DateTimeOffset? afterUtc,
        DateTime? localCursor)
    {
        var localDateTime = schedule.LocalDateTime(localDate);
        if (schedule.TimeZoneInfo.IsInvalidTime(localDateTime))
        {
            if (afterUtc is not null && localCursor is not null && localDateTime <= localCursor.Value)
            {
                return null;
            }

            return new RecurringOccurrenceCandidate(
                planId,
                scheduleRevision,
                schedule,
                localDate,
                schedule.LocalWallClockTime,
                identity,
                scheduledStartUtc: null,
                latestStartUtc: null,
                plannedEndUtc: null,
                RecurringScheduleReasonCodes.WallClockTimeInvalid,
                resolutionCode: "");
        }

        var (scheduledStartUtc, resolutionCode) = ResolveScheduledStartUtc(schedule.TimeZoneInfo, localDateTime);
        if (afterUtc is not null && scheduledStartUtc <= afterUtc.Value)
        {
            return null;
        }

        try
        {
            var latestStartUtc = scheduledStartUtc.Add(schedule.LatestStartGrace);
            var plannedEndUtc = latestStartUtc.Add(schedule.RecordingDuration);
            return new RecurringOccurrenceCandidate(
                planId,
                scheduleRevision,
                schedule,
                localDate,
                schedule.LocalWallClockTime,
                identity,
                scheduledStartUtc,
                latestStartUtc,
                plannedEndUtc,
                reasonCode: "",
                resolutionCode);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.UtcOverflow, "The recurring schedule UTC window overflowed.");
        }
    }

    private static (DateTimeOffset ScheduledStartUtc, string ResolutionCode) ResolveScheduledStartUtc(
        TimeZoneInfo timeZoneInfo,
        DateTime localDateTime)
    {
        try
        {
            if (timeZoneInfo.IsAmbiguousTime(localDateTime))
            {
                var candidates = timeZoneInfo
                    .GetAmbiguousTimeOffsets(localDateTime)
                    .Select(offset => new DateTimeOffset(localDateTime, offset).ToUniversalTime())
                    .OrderBy(value => value)
                    .ToArray();
                return (candidates[0], "schedule_ambiguous_earlier_utc");
            }

            var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZoneInfo);
            return (new DateTimeOffset(utcDateTime), "schedule_exact");
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.UtcOverflow, "The recurring schedule local time cannot be converted to UTC.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.TimeZoneInvalid, "The recurring schedule time-zone data is invalid.");
        }
    }
}

/// <summary>
/// Pure calendar jumps used by the calculator. The methods work on day
/// numbers and bounded weekday offsets instead of walking the original date
/// range one day at a time.
/// </summary>
internal static class RecurringScheduleCalendar
{
    internal static long CountMatchingOccurrencesBefore(
        RecurringPlanSchedule schedule,
        DateOnly localDate)
    {
        if (localDate <= schedule.LocalStartDate)
        {
            return 0;
        }

        try
        {
            var days = checked((long)localDate.DayNumber - schedule.LocalStartDate.DayNumber);
            if (schedule.IsDaily)
            {
                return days;
            }

            var fullWeeks = days / 7;
            var remainderDays = (int)(days % 7);
            var count = checked(fullWeeks * schedule.WeeklyDays.Count);
            var startDayIndex = DayIndex(schedule.LocalStartDate.DayOfWeek);
            for (var offset = 0; offset < remainderDays; offset++)
            {
                var day = DayFromIndex((startDayIndex + offset) % 7);
                if (schedule.WeeklyDays.Contains(day))
                {
                    count++;
                }
            }

            return count;
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.DateOverflow, "The recurring schedule date sequence overflowed.");
        }
    }

    internal static DateOnly? FindNextMatchingDateOnOrAfter(
        RecurringPlanSchedule schedule,
        DateOnly localDate)
    {
        if (localDate < schedule.LocalStartDate)
        {
            localDate = schedule.LocalStartDate;
        }

        if (localDate > schedule.LocalEndDate)
        {
            return null;
        }

        if (schedule.IsDaily)
        {
            return localDate;
        }

        var startDayIndex = DayIndex(localDate.DayOfWeek);
        for (var offset = 0; offset <= 6; offset++)
        {
            var dayNumber = (long)localDate.DayNumber + offset;
            if (dayNumber > schedule.LocalEndDate.DayNumber)
            {
                return null;
            }

            if (schedule.WeeklyDays.Contains(DayFromIndex((startDayIndex + offset) % 7)))
            {
                return DateOnly.FromDayNumber(checked((int)dayNumber));
            }
        }

        return null;
    }

    internal static DateOnly? FindNextMatchingDateAfter(
        RecurringPlanSchedule schedule,
        DateOnly localDate)
    {
        if (localDate >= schedule.LocalEndDate)
        {
            return null;
        }

        if (schedule.IsDaily)
        {
            return DateOnly.FromDayNumber(checked(localDate.DayNumber + 1));
        }

        var startDayIndex = DayIndex(localDate.DayOfWeek);
        for (var offset = 1; offset <= 7; offset++)
        {
            var dayNumber = (long)localDate.DayNumber + offset;
            if (dayNumber > schedule.LocalEndDate.DayNumber)
            {
                return null;
            }

            if (schedule.WeeklyDays.Contains(DayFromIndex((startDayIndex + offset) % 7)))
            {
                return DateOnly.FromDayNumber(checked((int)dayNumber));
            }
        }

        return null;
    }

    internal static DateOnly? FindPreviousMatchingDateBefore(
        RecurringPlanSchedule schedule,
        DateOnly localDate)
    {
        if (localDate <= schedule.LocalStartDate)
        {
            return null;
        }

        if (schedule.IsDaily)
        {
            var dayNumber = checked(localDate.DayNumber - 1);
            return dayNumber < schedule.LocalStartDate.DayNumber
                ? null
                : DateOnly.FromDayNumber(dayNumber);
        }

        var startDayIndex = DayIndex(localDate.DayOfWeek);
        for (var offset = 1; offset <= 7; offset++)
        {
            var dayNumber = (long)localDate.DayNumber - offset;
            if (dayNumber < schedule.LocalStartDate.DayNumber)
            {
                return null;
            }

            if (schedule.WeeklyDays.Contains(DayFromIndex((startDayIndex - offset % 7 + 7) % 7)))
            {
                return DateOnly.FromDayNumber(checked((int)dayNumber));
            }
        }

        return null;
    }

    private static int DayIndex(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0,
        DayOfWeek.Tuesday => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 6,
        _ => throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdayInvalid, "The weekly weekday value is not recognized."),
    };

    private static DayOfWeek DayFromIndex(int index) => index switch
    {
        0 => DayOfWeek.Monday,
        1 => DayOfWeek.Tuesday,
        2 => DayOfWeek.Wednesday,
        3 => DayOfWeek.Thursday,
        4 => DayOfWeek.Friday,
        5 => DayOfWeek.Saturday,
        6 => DayOfWeek.Sunday,
        _ => throw new Phase3DomainException(RecurringScheduleReasonCodes.WeekdayInvalid, "The weekly weekday index is not recognized."),
    };
}

internal static class RecurringScheduleCanonicalization
{
    internal static string ComputeScheduleDigest(RecurringPlanSchedule schedule)
    {
        var bytes = Start("recurring-schedule/v1");
        AppendString(bytes, ScheduleKindCode(schedule.Kind));
        AppendString(bytes, schedule.TimeZoneId);
        AppendString(bytes, schedule.LocalStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AppendString(bytes, schedule.LocalEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AppendInt64(bytes, schedule.LocalWallClockTime.Ticks);
        AppendInt32(bytes, schedule.MaximumOccurrences);
        AppendInt64(bytes, schedule.RecordingDuration.Ticks);
        AppendInt64(bytes, schedule.LatestStartGrace.Ticks);
        AppendInt32(bytes, schedule.WeeklyDays.Count);
        foreach (var day in schedule.WeeklyDays)
        {
            AppendInt32(bytes, (int)day);
        }

        return "recurring-schedule/v1:" + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    internal static List<byte> Start(string schemaName)
    {
        var bytes = new List<byte>(256);
        AppendString(bytes, schemaName);
        AppendInt32(bytes, RecurringPlanSchedule.CanonicalVersion);
        return bytes;
    }

    internal static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendInt32(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    internal static void AppendInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    internal static void AppendInt64(List<byte> bytes, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static string ScheduleKindCode(RecurringScheduleKind kind) => kind switch
    {
        RecurringScheduleKind.Daily => "daily",
        RecurringScheduleKind.Weekly => "weekly",
        _ => throw new Phase3DomainException("schedule_kind_invalid", "The recurring schedule kind is not supported."),
    };
}
