using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringPersistenceReasonCodes
{
    public const string ScheduleVersionNotFound = "schedule_version_not_found";
    public const string SchedulePlanNotFound = "schedule_plan_not_found";
    public const string OneShotPlanRejected = "one_shot_plan_rejected";
    public const string RevisionInvalid = "schedule_revision_invalid";
    public const string RevisionNotContiguous = "schedule_revision_not_contiguous";
    public const string RevisionConflict = "schedule_revision_conflict";
    public const string DigestConflict = "schedule_digest_conflict";
    public const string DigestMismatch = "schedule_digest_mismatch";
    public const string PersistedDataInvalid = "schedule_persisted_data_invalid";
    public const string TimeZoneRulesChanged = "schedule_time_zone_rules_changed";
    public const string CandidateMismatch = "schedule_candidate_mismatch";
    public const string OccurrenceIdentityConflict = "occurrence_identity_conflict";
    public const string OccurrenceSlotConflict = "occurrence_slot_conflict";
    public const string AtomicPersistenceFailed = "recurring_atomic_persistence_failed";
    public const string CursorNotFound = "recurring_cursor_not_found";
    public const string CursorInvalid = "recurring_cursor_invalid";
    public const string CursorStale = "recurring_cursor_stale";
    public const string CursorRequestConflict = "recurring_cursor_request_conflict";
    public const string AdvancementIdempotencyConflict = "recurring_advancement_idempotency_conflict";
    public const string PlanNotFound = "recurring_plan_not_found";
    public const string PlanNotEnabled = "recurring_plan_not_enabled";
    public const string ScheduleRevisionSuperseded = "recurring_schedule_revision_superseded";
    public const string OperationRelationInvalid = "recurring_operation_relation_invalid";
    public const string AdvancedAtRegression = "recurring_advanced_at_regression";
}

public sealed record RecurringScheduleCursorSnapshot(
    string PlanId,
    long ScheduleRevision,
    string ScheduleDigest,
    string TimeZoneRulesDigest,
    DateTimeOffset InitialAfterUtc,
    DateOnly? LastLocalDate,
    long LastScheduleOrdinal,
    bool IsExhausted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version)
{
    public bool IsEmpty => LastLocalDate is null && LastScheduleOrdinal == 0 && !IsExhausted;

    public RecurringScheduleCursorPosition ToPosition() => new(
        ScheduleRevision,
        InitialAfterUtc,
        LastLocalDate,
        LastScheduleOrdinal,
        IsExhausted,
        Version);
}

public sealed record RecurringAdvancementOperationSnapshot(
    string OperationId,
    string PlanId,
    long ScheduleRevision,
    string RequestDigest,
    long ExpectedCursorVersion,
    string ResultCode,
    string? OccurrenceIdentity,
    long ResultCursorVersion,
    DateTimeOffset CreatedAtUtc);

public sealed record RecurringScheduleVersionSnapshot(
    string PlanId,
    long ScheduleRevision,
    RecurringPlanSchedule Schedule,
    string TimeZoneRulesDigest,
    DateTimeOffset CreatedAtUtc)
{
    public string ScheduleDigest => Schedule.CanonicalDigest;
}

public sealed record RecurringOccurrenceSlotSnapshot(
    string OccurrenceIdentity,
    string PlanId,
    long ScheduleRevision,
    string ScheduleDigest,
    DateOnly LocalDate,
    TimeOnly LocalWallClockTime,
    string TimeZoneId,
    string SlotStatusCode,
    DateTimeOffset? ScheduledStartUtc,
    DateTimeOffset? LatestStartUtc,
    DateTimeOffset? PlannedEndUtc,
    string ResolutionCode,
    string? TerminalReasonCode,
    string? OccurrenceId,
    DateTimeOffset CreatedAtUtc)
{
    public bool IsScheduled => string.Equals(SlotStatusCode, "scheduled", StringComparison.Ordinal);

    public bool IsSkipped => string.Equals(SlotStatusCode, "skipped", StringComparison.Ordinal);
}

/// <summary>
/// Owns only immutable recurring schedule-version persistence.  A revision is
/// independent from PlanDefinition.Version and is never updated in place.
/// </summary>
public sealed class SqliteRecurringScheduleVersionRepository : SqliteRepositoryBase
{
    public SqliteRecurringScheduleVersionRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public RecurringScheduleVersionSnapshot CreateOrGet(
        string planId,
        long scheduleRevision,
        RecurringPlanSchedule schedule,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var canonicalPlanId = RequiredInput(planId);
        if (scheduleRevision <= 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.RevisionInvalid, "The recurring schedule revision must be positive.");
        }

        var createdTicks = UtcTicksInput(createdAtUtc);
        var rulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            EnsurePeriodicPlan(connection, transaction, canonicalPlanId);

            var existing = ReadScheduleByRevision(connection, transaction, canonicalPlanId, scheduleRevision);
            if (existing is not null)
            {
                if (SchedulesEquivalent(existing.Schedule, schedule) &&
                    string.Equals(existing.TimeZoneRulesDigest, rulesDigest, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return existing;
                }

                throw new Phase3PersistenceException(
                    RecurringPersistenceReasonCodes.RevisionConflict,
                    "The recurring schedule revision is already bound to different immutable content.");
            }

            if (ScheduleDigestExists(connection, transaction, canonicalPlanId, schedule.CanonicalDigest, rulesDigest))
            {
                throw new Phase3PersistenceException(
                    RecurringPersistenceReasonCodes.DigestConflict,
                    "The recurring schedule digest already belongs to another revision.");
            }

            var latestRevision = ReadLatestRevision(connection, transaction, canonicalPlanId);
            if (scheduleRevision != checked(latestRevision + 1))
            {
                throw new Phase3PersistenceException(
                    RecurringPersistenceReasonCodes.RevisionNotContiguous,
                    "The recurring schedule revision is not the next contiguous revision.");
            }

            InsertSchedule(connection, transaction, canonicalPlanId, scheduleRevision, schedule, rulesDigest, createdTicks);
            var result = new RecurringScheduleVersionSnapshot(
                canonicalPlanId,
                scheduleRevision,
                schedule,
                rulesDigest,
                ToUtc(createdTicks));
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                RecurringPersistenceReasonCodes.RevisionConflict,
                "The recurring schedule version conflicted with another immutable row.",
                exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring schedule version transaction failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring schedule version transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public RecurringScheduleVersionSnapshot Get(string planId, long scheduleRevision)
    {
        var canonicalPlanId = RequiredInput(planId);
        if (scheduleRevision <= 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.RevisionInvalid, "The recurring schedule revision must be positive.");
        }

        using var connection = OpenBusinessConnection();
        try
        {
            var snapshot = ReadScheduleByRevision(connection, transaction: null, canonicalPlanId, scheduleRevision);
            return snapshot ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found.");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring schedule version could not be read.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring schedule version is invalid.", exception);
        }
    }

    public RecurringScheduleVersionSnapshot GetLatest(string planId)
    {
        var canonicalPlanId = RequiredInput(planId);
        using var connection = OpenBusinessConnection();
        try
        {
            var snapshot = ReadLatestSchedule(connection, transaction: null, canonicalPlanId);
            return snapshot ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found.");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The latest recurring schedule version could not be read.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring schedule version is invalid.", exception);
        }
    }

    internal static RecurringScheduleVersionSnapshot? ReadScheduleByRevision(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        long scheduleRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id, schedule_revision, schedule_digest, schedule_kind_code,
                   time_zone_id, time_zone_rules_digest, local_start_date, local_end_date,
                   local_wall_clock_seconds, weekday_mask, maximum_occurrences,
                   recording_duration_ticks, latest_start_grace_ticks, created_at_utc
            FROM recurring_schedule_versions
            WHERE plan_id = $plan_id AND schedule_revision = $schedule_revision;
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", scheduleRevision);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadScheduleSnapshot(reader) : null;
    }

    internal static RecurringScheduleVersionSnapshot? ReadLatestSchedule(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id, schedule_revision, schedule_digest, schedule_kind_code,
                   time_zone_id, time_zone_rules_digest, local_start_date, local_end_date,
                   local_wall_clock_seconds, weekday_mask, maximum_occurrences,
                   recording_duration_ticks, latest_start_grace_ticks, created_at_utc
            FROM recurring_schedule_versions
            WHERE plan_id = $plan_id
            ORDER BY schedule_revision DESC
            LIMIT 1;
            """;
        Add(command, "$plan_id", planId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadScheduleSnapshot(reader) : null;
    }

    internal static RecurringScheduleVersionSnapshot ReadScheduleSnapshot(SqliteDataReader reader)
    {
        var planId = ReadRequiredText(reader, 0);
        var revision = ReadPositiveInt64(reader, 1, RecurringPersistenceReasonCodes.RevisionInvalid);
        var storedDigest = ReadRequiredText(reader, 2);
        var kindCode = ReadRequiredText(reader, 3);
        var timeZoneId = ReadRequiredText(reader, 4);
        var storedRulesDigest = ReadRequiredText(reader, 5);
        var localStartDate = ReadIsoDate(reader, 6);
        var localEndDate = ReadIsoDate(reader, 7);
        var wallClockSeconds = ReadInt64(reader, 8);
        var weekdayMask = ReadInt64(reader, 9);
        var maximumOccurrences = ReadInt64(reader, 10);
        var recordingDurationTicks = ReadInt64(reader, 11);
        var graceTicks = ReadInt64(reader, 12);
        var createdTicks = ReadNonNegativeInt64(reader, 13);

        if (!IsVersionedDigest(storedDigest, RecurringPlanSchedule.CanonicalVersion, "recurring-schedule/v1:"))
        {
            throw InvalidPersistedData("The persisted recurring schedule digest is not canonical.");
        }

        if (!IsVersionedDigest(storedRulesDigest, RecurringTimeZoneRulesDigest.CanonicalVersion, RecurringTimeZoneRulesDigest.Prefix))
        {
            throw InvalidPersistedData("The persisted time-zone rules digest is not canonical.");
        }

        if (localEndDate < localStartDate || wallClockSeconds is < 0 or > 86399 ||
            maximumOccurrences is <= 0 or > int.MaxValue ||
            recordingDurationTicks <= 0 || recordingDurationTicks > TimeSpan.MaxValue.Ticks ||
            graceTicks < 0 || graceTicks > TimeSpan.FromMinutes(5).Ticks ||
            weekdayMask is < 0 or > 127)
        {
            throw InvalidPersistedData("The persisted recurring schedule scalar is outside its domain range.");
        }

        RecurringScheduleKind kind;
        IEnumerable<DayOfWeek>? weeklyDays;
        if (string.Equals(kindCode, "daily", StringComparison.Ordinal))
        {
            if (weekdayMask != 0)
            {
                throw InvalidPersistedData("A daily schedule must have a zero weekday mask.");
            }

            kind = RecurringScheduleKind.Daily;
            weeklyDays = null;
        }
        else if (string.Equals(kindCode, "weekly", StringComparison.Ordinal) && weekdayMask > 0)
        {
            kind = RecurringScheduleKind.Weekly;
            weeklyDays = DaysFromMask((int)weekdayMask);
        }
        else
        {
            throw InvalidPersistedData("The persisted recurring schedule kind or weekday mask is invalid.");
        }

        try
        {
            var schedule = new RecurringPlanSchedule(
                kind,
                timeZoneId,
                localStartDate,
                localEndDate,
                TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(wallClockSeconds)),
                (int)maximumOccurrences,
                TimeSpan.FromTicks(recordingDurationTicks),
                TimeSpan.FromTicks(graceTicks),
                weeklyDays);

            if (!string.Equals(schedule.CanonicalDigest, storedDigest, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.DigestMismatch, "The persisted schedule digest does not match its reconstructed content.");
            }

            var currentRulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);
            if (!string.Equals(currentRulesDigest, storedRulesDigest, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.TimeZoneRulesChanged, "The current time-zone rules do not match the persisted schedule version.");
            }

            return new RecurringScheduleVersionSnapshot(planId, revision, schedule, storedRulesDigest, ToUtc(createdTicks));
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring schedule cannot be reconstructed.", exception);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring schedule time zone cannot be resolved.", exception);
        }
    }

    private static void EnsurePeriodicPlan(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_one_time FROM plans WHERE id = $plan_id;";
        Add(command, "$plan_id", planId);
        var value = command.ExecuteScalar();
        if (value is null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.SchedulePlanNotFound, "The target plan does not exist.");
        }

        if (value is not long isOneTime)
        {
            throw InvalidPersistedData("The plan one-time flag was not an INTEGER.");
        }

        if (isOneTime == 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OneShotPlanRejected, "A one-shot plan cannot own a recurring schedule version.");
        }

        if (isOneTime != 0)
        {
            throw InvalidPersistedData("The plan one-time flag is invalid.");
        }
    }

    private static long ReadLatestRevision(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(schedule_revision), 0) FROM recurring_schedule_versions WHERE plan_id = $plan_id;";
        Add(command, "$plan_id", planId);
        return command.ExecuteScalar() is long value && value >= 0
            ? value
            : throw InvalidPersistedData("The latest schedule revision was not an INTEGER.");
    }

    private static bool ScheduleDigestExists(SqliteConnection connection, SqliteTransaction transaction, string planId, string digest, string rulesDigest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM recurring_schedule_versions WHERE plan_id = $plan_id AND schedule_digest = $schedule_digest AND time_zone_rules_digest = $time_zone_rules_digest LIMIT 1;";
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_digest", digest);
        Add(command, "$time_zone_rules_digest", rulesDigest);
        return command.ExecuteScalar() is not null;
    }

    internal static void InsertSchedule(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        long revision,
        RecurringPlanSchedule schedule,
        string rulesDigest,
        long createdTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurring_schedule_versions
                (plan_id, schedule_revision, schedule_digest, schedule_kind_code,
                 time_zone_id, time_zone_rules_digest, local_start_date, local_end_date,
                 local_wall_clock_seconds, weekday_mask, maximum_occurrences,
                 recording_duration_ticks, latest_start_grace_ticks, created_at_utc)
            VALUES
                ($plan_id, $schedule_revision, $schedule_digest, $schedule_kind_code,
                 $time_zone_id, $time_zone_rules_digest, $local_start_date, $local_end_date,
                 $local_wall_clock_seconds, $weekday_mask, $maximum_occurrences,
                 $recording_duration_ticks, $latest_start_grace_ticks, $created_at_utc);
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", revision);
        Add(command, "$schedule_digest", schedule.CanonicalDigest);
        Add(command, "$schedule_kind_code", schedule.IsDaily ? "daily" : "weekly");
        Add(command, "$time_zone_id", schedule.TimeZoneId);
        Add(command, "$time_zone_rules_digest", rulesDigest);
        Add(command, "$local_start_date", schedule.LocalStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$local_end_date", schedule.LocalEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$local_wall_clock_seconds", schedule.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond);
        Add(command, "$weekday_mask", schedule.WeekdayMask);
        Add(command, "$maximum_occurrences", schedule.MaximumOccurrences);
        Add(command, "$recording_duration_ticks", schedule.RecordingDuration.Ticks);
        Add(command, "$latest_start_grace_ticks", schedule.LatestStartGrace.Ticks);
        Add(command, "$created_at_utc", createdTicks);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The schedule version insert did not affect exactly one row.");
        }
    }

    private static bool SchedulesEquivalent(RecurringPlanSchedule left, RecurringPlanSchedule right) =>
        left.Kind == right.Kind &&
        string.Equals(left.TimeZoneId, right.TimeZoneId, StringComparison.Ordinal) &&
        left.LocalStartDate == right.LocalStartDate &&
        left.LocalEndDate == right.LocalEndDate &&
        left.LocalWallClockTime == right.LocalWallClockTime &&
        left.MaximumOccurrences == right.MaximumOccurrences &&
        left.RecordingDuration == right.RecordingDuration &&
        left.LatestStartGrace == right.LatestStartGrace &&
        left.WeekdayMask == right.WeekdayMask &&
        string.Equals(left.CanonicalDigest, right.CanonicalDigest, StringComparison.Ordinal);

    private static IReadOnlyList<DayOfWeek> DaysFromMask(int mask)
    {
        var days = new List<DayOfWeek>(7);
        var orderedDays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        for (var index = 0; index < orderedDays.Length; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                days.Add(orderedDays[index]);
            }
        }

        return days;
    }

    private static DateOnly ReadIsoDate(SqliteDataReader reader, int ordinal)
    {
        var value = ReadRequiredText(reader, ordinal);
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !string.Equals(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw InvalidPersistedData("A persisted local date is not canonical ISO-8601.");
        }

        return date;
    }

    private static new string ReadRequiredText(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string value || string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw InvalidPersistedData("A required recurring schedule TEXT field is invalid.");
        }

        return value;
    }

    private static long ReadPositiveInt64(SqliteDataReader reader, int ordinal, string code)
    {
        var value = ReadInt64(reader, ordinal);
        if (value <= 0)
        {
            throw new Phase3PersistenceException(code, "A persisted recurring schedule revision is not positive.");
        }

        return value;
    }

    private static long ReadNonNegativeInt64(SqliteDataReader reader, int ordinal)
    {
        var value = ReadInt64(reader, ordinal);
        return value >= 0 && value <= DateTime.MaxValue.Ticks
            ? value
            : throw InvalidPersistedData("A persisted UTC tick value is outside the UTC DateTime range.");
    }

    private static new long ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value
            ? value
            : throw InvalidPersistedData("A persisted INTEGER field is not Int64.");

    private static bool IsVersionedDigest(string value, int version, string prefix)
    {
        var expectedPrefix = prefix;
        return value.StartsWith(expectedPrefix, StringComparison.Ordinal) &&
            value.Length == expectedPrefix.Length + 64 &&
            value[(expectedPrefix.Length)..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            version == 1;
    }

    private static DateTimeOffset ToUtc(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

    private static Phase3PersistenceException InvalidPersistedData(string message) =>
        new(RecurringPersistenceReasonCodes.PersistedDataInvalid, message);

    private static new void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
            // Preserve the original stable error.
        }
    }
}

/// <summary>
/// Materializes exactly one calculator candidate.  It deliberately contains
/// no cursor advancement, lease, scope, run, or scheduler behavior.
/// </summary>
public sealed class SqliteRecurringOccurrenceMaterializationTransaction : SqliteRepositoryBase
{
    private readonly Action? _afterOccurrenceInsert;

    public SqliteRecurringOccurrenceMaterializationTransaction(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal SqliteRecurringOccurrenceMaterializationTransaction(SqliteOperationalStore store, Action afterOccurrenceInsert)
        : base(store)
    {
        _afterOccurrenceInsert = afterOccurrenceInsert ?? throw new ArgumentNullException(nameof(afterOccurrenceInsert));
    }

    public RecurringOccurrenceSlotSnapshot Materialize(
        RecurringOccurrenceCandidate candidate,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var createdTicks = UtcTicksInput(createdAtUtc);
        var candidatePlanId = RequiredInput(candidate.PlanId);
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var result = MaterializeWithinTransaction(connection, transaction, candidate, createdTicks);
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceSlotConflict, "The recurring occurrence materialization conflicted with another immutable row.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring occurrence materialization transaction failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring occurrence materialization transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal RecurringOccurrenceSlotSnapshot MaterializeWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringOccurrenceCandidate candidate,
        long createdTicks)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(candidate);

        var candidatePlanId = RequiredInput(candidate.PlanId);
        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection,
                transaction,
                candidatePlanId,
                candidate.ScheduleRevision)
            ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found.");

        EnsurePeriodicPlan(connection, transaction, candidatePlanId);
        ValidateCandidate(candidate, schedule);

        var existing = ReadSlotByIdentity(connection, transaction, candidate.Identity.Value);
        if (existing is not null)
        {
            ValidateExistingSlot(existing, candidate, schedule);
            if (existing.IsScheduled)
            {
                EnsureOccurrenceWindow(connection, transaction, existing.OccurrenceId!, candidate);
            }

            return existing;
        }

        if (ReadSlotByLocalSlot(connection, transaction, candidate) is not null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceSlotConflict, "The local recurring schedule slot is already bound to another identity.");
        }

        if (candidate.IsValid)
        {
            EnsureNoExistingOccurrence(connection, transaction, candidate.Identity.Value);
            var occurrence = new PlanOccurrence(
                candidate.Identity.Value,
                candidatePlanId,
                candidate.ScheduledStartUtc!.Value,
                candidate.PlannedEndUtc!.Value,
                ToUtc(createdTicks));
            SqlitePlanOccurrenceRepository.InsertWithinTransaction(connection, transaction, occurrence);
            _afterOccurrenceInsert?.Invoke();
        }

        InsertSlot(connection, transaction, candidate, createdTicks);
        return ToSnapshot(candidate, ToUtc(createdTicks));
    }

    public RecurringOccurrenceSlotSnapshot Get(string occurrenceIdentity)
    {
        var identity = RequiredInput(occurrenceIdentity);
        using var connection = OpenBusinessConnection();
        try
        {
            var slot = ReadSlotByIdentity(connection, transaction: null, identity)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The recurring occurrence identity was not found.");
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection,
                transaction: null,
                slot.PlanId,
                slot.ScheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version for the slot was not found.");

            if (!string.Equals(slot.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
                !string.Equals(slot.TimeZoneId, schedule.Schedule.TimeZoneId, StringComparison.Ordinal) ||
                (slot.IsScheduled && !string.Equals(slot.OccurrenceId, slot.OccurrenceIdentity, StringComparison.Ordinal)))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The persisted occurrence slot is not bound to its schedule version.");
            }

            var expectedIdentity = RecurringOccurrenceIdentity.Create(
                slot.PlanId,
                slot.ScheduleRevision,
                schedule.Schedule,
                slot.LocalDate,
                slot.LocalWallClockTime);
            if (!string.Equals(expectedIdentity.Value, slot.OccurrenceIdentity, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The persisted occurrence identity is inconsistent with its schedule slot.");
            }

            var candidate = new RecurringOccurrenceCandidate(
                slot.PlanId,
                slot.ScheduleRevision,
                schedule.Schedule,
                slot.LocalDate,
                slot.LocalWallClockTime,
                expectedIdentity,
                slot.ScheduledStartUtc,
                slot.LatestStartUtc,
                slot.PlannedEndUtc,
                slot.TerminalReasonCode ?? string.Empty,
                slot.ResolutionCode);
            try
            {
                ValidateCandidate(candidate, schedule);
            }
            catch (Phase3PersistenceException exception) when (exception.Code == RecurringPersistenceReasonCodes.CandidateMismatch)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The persisted occurrence slot is inconsistent with its schedule version.", exception);
            }

            if (slot.IsScheduled)
            {
                EnsureOccurrenceWindow(connection, transaction: null, slot.OccurrenceId!, candidate);
            }
            else
            {
                EnsureNoExistingOccurrence(connection, transaction: null, slot.OccurrenceIdentity);
            }

            return slot;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring occurrence slot could not be read.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring occurrence slot is invalid.", exception);
        }
    }

    internal static RecurringOccurrenceSlotSnapshot ReadAndValidatePersistedSlotReference(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string occurrenceIdentity,
        RecurringScheduleVersionSnapshot schedule)
    {
        var slot = ReadSlotByIdentity(connection, transaction, occurrenceIdentity)
            ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The referenced recurring occurrence slot was not found.");
        if (slot.IsScheduled && !string.Equals(slot.OccurrenceId, slot.OccurrenceIdentity, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The scheduled recurring slot occurrence relation is not canonical.");
        }
        if (!string.Equals(slot.PlanId, schedule.PlanId, StringComparison.Ordinal) ||
            slot.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(slot.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(slot.TimeZoneId, schedule.Schedule.TimeZoneId, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The referenced recurring occurrence slot is not bound to the requested schedule.");
        }

        RecurringOccurrenceIdentity expectedIdentity;
        try
        {
            expectedIdentity = RecurringOccurrenceIdentity.Create(
                slot.PlanId,
                slot.ScheduleRevision,
                schedule.Schedule,
                slot.LocalDate,
                slot.LocalWallClockTime);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The referenced recurring occurrence slot local facts are invalid.", exception);
        }

        if (!string.Equals(expectedIdentity.Value, slot.OccurrenceIdentity, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The referenced recurring occurrence identity is not canonical.");
        }

        var candidate = new RecurringOccurrenceCandidate(
            slot.PlanId,
            slot.ScheduleRevision,
            schedule.Schedule,
            slot.LocalDate,
            slot.LocalWallClockTime,
            expectedIdentity,
            slot.ScheduledStartUtc,
            slot.LatestStartUtc,
            slot.PlannedEndUtc,
            slot.TerminalReasonCode ?? string.Empty,
            slot.ResolutionCode);
        try
        {
            ValidateCandidate(candidate, schedule);
            if (slot.IsScheduled)
            {
                EnsureOccurrenceWindow(connection, transaction, slot.OccurrenceId!, candidate);
            }
            else
            {
                EnsureNoExistingOccurrence(connection, transaction, slot.OccurrenceIdentity);
            }
            return slot;
        }
        catch (Phase3PersistenceException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The referenced recurring occurrence slot is inconsistent with its immutable schedule facts.", exception);
        }
    }

    internal static void ValidatePersistedSlotReference(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string occurrenceIdentity,
        RecurringScheduleVersionSnapshot schedule) =>
        _ = ReadAndValidatePersistedSlotReference(connection, transaction, occurrenceIdentity, schedule);

    private static void EnsurePeriodicPlan(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_one_time FROM plans WHERE id = $plan_id;";
        Add(command, "$plan_id", planId);
        var value = command.ExecuteScalar();
        if (value is null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.SchedulePlanNotFound, "The target plan does not exist.");
        }

        if (value is not long flag || flag is not (0 or 1))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The plan one-time flag is invalid.");
        }

        if (flag == 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OneShotPlanRejected, "A one-shot plan cannot materialize a recurring occurrence.");
        }
    }

    private static void ValidateCandidate(RecurringOccurrenceCandidate candidate, RecurringScheduleVersionSnapshot persisted)
    {
        if (!string.Equals(candidate.PlanId, persisted.PlanId, StringComparison.Ordinal) ||
            candidate.ScheduleRevision != persisted.ScheduleRevision ||
            !string.Equals(candidate.ScheduleDigest, persisted.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(candidate.TimeZoneId, persisted.Schedule.TimeZoneId, StringComparison.Ordinal) ||
            candidate.LocalDate < persisted.Schedule.LocalStartDate ||
            candidate.LocalDate > persisted.Schedule.LocalEndDate ||
            candidate.LocalWallClockTime != persisted.Schedule.LocalWallClockTime)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "The occurrence candidate does not match the persisted schedule version.");
        }

        RecurringOccurrenceIdentity expectedIdentity;
        try
        {
            expectedIdentity = RecurringOccurrenceIdentity.Create(
                persisted.PlanId,
                persisted.ScheduleRevision,
                persisted.Schedule,
                candidate.LocalDate,
                candidate.LocalWallClockTime);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "The occurrence candidate local slot is not valid for the persisted schedule.", exception);
        }

        if (!string.Equals(candidate.Identity.Value, expectedIdentity.Value, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "The occurrence candidate identity is not canonical for its schedule slot.");
        }

        var localDateTime = persisted.Schedule.LocalDateTime(candidate.LocalDate);
        if (persisted.Schedule.TimeZoneInfo.IsInvalidTime(localDateTime))
        {
            if (!candidate.IsSkipped ||
                !string.Equals(candidate.ReasonCode, RecurringScheduleReasonCodes.WallClockTimeInvalid, StringComparison.Ordinal) ||
                candidate.ResolutionCode.Length != 0 ||
                candidate.LatestStartUtc is not null ||
                candidate.PlannedEndUtc is not null)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "A DST gap candidate must be a skipped slot with no UTC window.");
            }

            return;
        }

        if (!candidate.IsValid || candidate.ScheduledStartUtc is null || candidate.LatestStartUtc is null || candidate.PlannedEndUtc is null ||
            candidate.ReasonCode.Length != 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "A resolvable local slot must carry a complete scheduled UTC window.");
        }

        var expected = ResolveExpectedUtc(persisted.Schedule, localDateTime);
        var expectedLatest = expected.StartUtc.Add(persisted.Schedule.LatestStartGrace);
        var expectedEnd = expectedLatest.Add(persisted.Schedule.RecordingDuration);
        if (candidate.ScheduledStartUtc.Value.Offset != TimeSpan.Zero ||
            candidate.LatestStartUtc.Value.Offset != TimeSpan.Zero ||
            candidate.PlannedEndUtc.Value.Offset != TimeSpan.Zero ||
            candidate.ScheduledStartUtc.Value != expected.StartUtc ||
            candidate.LatestStartUtc.Value != expectedLatest ||
            candidate.PlannedEndUtc.Value != expectedEnd ||
            !string.Equals(candidate.ResolutionCode, expected.ResolutionCode, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "The occurrence candidate UTC resolution is not canonical for the persisted time-zone rules.");
        }
    }

    private static (DateTimeOffset StartUtc, string ResolutionCode) ResolveExpectedUtc(RecurringPlanSchedule schedule, DateTime localDateTime)
    {
        try
        {
            if (schedule.TimeZoneInfo.IsAmbiguousTime(localDateTime))
            {
                var start = schedule.TimeZoneInfo.GetAmbiguousTimeOffsets(localDateTime)
                    .Select(offset => new DateTimeOffset(localDateTime, offset).ToUniversalTime())
                    .Min();
                return (start, "schedule_ambiguous_earlier_utc");
            }

            return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, schedule.TimeZoneInfo)), "schedule_exact");
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidTimeZoneException)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CandidateMismatch, "The candidate local time could not be resolved using the persisted time-zone rules.", exception);
        }
    }

    private static void EnsureNoExistingOccurrence(SqliteConnection connection, SqliteTransaction? transaction, string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM plan_occurrences WHERE id = $id LIMIT 1;";
        Add(command, "$id", occurrenceId);
        if (command.ExecuteScalar() is not null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The occurrence identity is already used by a record outside the recurring slot.");
        }
    }

    private static void EnsureOccurrenceWindow(SqliteConnection connection, SqliteTransaction? transaction, string occurrenceId, RecurringOccurrenceCandidate candidate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT plan_id, window_start_utc, window_end_utc FROM plan_occurrences WHERE id = $id;";
        Add(command, "$id", occurrenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetValue(0) is not string planId || reader.GetValue(1) is not long windowStart || reader.GetValue(2) is not long windowEnd ||
            !string.Equals(planId, candidate.PlanId, StringComparison.Ordinal) ||
            windowStart != candidate.ScheduledStartUtc!.Value.UtcDateTime.Ticks ||
            windowEnd != candidate.PlannedEndUtc!.Value.UtcDateTime.Ticks)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The recurring slot occurrence window is inconsistent with its immutable candidate.");
        }
    }

    internal static RecurringOccurrenceSlotSnapshot? ReadSlotByIdentity(SqliteConnection connection, SqliteTransaction? transaction, string identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SlotSelect + " WHERE occurrence_identity = $occurrence_identity;";
        Add(command, "$occurrence_identity", identity);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSlotSnapshot(reader) : null;
    }

    private static RecurringOccurrenceSlotSnapshot? ReadSlotByLocalSlot(SqliteConnection connection, SqliteTransaction transaction, RecurringOccurrenceCandidate candidate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SlotSelect + " WHERE plan_id = $plan_id AND schedule_revision = $schedule_revision AND local_date = $local_date AND local_wall_clock_seconds = $local_wall_clock_seconds;";
        Add(command, "$plan_id", candidate.PlanId);
        Add(command, "$schedule_revision", candidate.ScheduleRevision);
        Add(command, "$local_date", candidate.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$local_wall_clock_seconds", candidate.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSlotSnapshot(reader) : null;
    }

    private static RecurringOccurrenceSlotSnapshot ReadSlotSnapshot(SqliteDataReader reader)
    {
        var identity = ReadRequiredText(reader, 0);
        var planId = ReadRequiredText(reader, 1);
        var revision = ReadPositiveInt64(reader, 2);
        var digest = ReadRequiredText(reader, 3);
        var date = ReadIsoDate(reader, 4);
        var wallClockSeconds = ReadInt64(reader, 5);
        var timeZoneId = ReadRequiredText(reader, 6);
        var status = ReadRequiredText(reader, 7);
        var scheduled = ReadNullableUtc(reader, 8);
        var latest = ReadNullableUtc(reader, 9);
        var planned = ReadNullableUtc(reader, 10);
        var resolution = ReadRequiredTextAllowEmpty(reader, 11);
        var terminalReason = ReadNullableText(reader, 12);
        var occurrenceId = ReadNullableText(reader, 13);
        var created = ReadUtc(reader, 14);

        if (!string.Equals(identity, identity.Trim(), StringComparison.Ordinal) ||
            !IsOccurrenceIdentity(identity) ||
            wallClockSeconds is < 0 or > 86399 || status is not ("scheduled" or "skipped") ||
            (status == "scheduled" && (scheduled is null || latest is null || planned is null || latest < scheduled || planned <= latest ||
                resolution is not ("schedule_exact" or "schedule_ambiguous_earlier_utc") || terminalReason is not null || occurrenceId is null)) ||
            (status == "skipped" && (scheduled is not null || latest is not null || planned is not null || resolution.Length != 0 ||
                terminalReason != RecurringScheduleReasonCodes.WallClockTimeInvalid || occurrenceId is not null)))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring occurrence slot violates its immutable shape.");
        }

        return new RecurringOccurrenceSlotSnapshot(identity, planId, revision, digest, date, TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(wallClockSeconds)), timeZoneId, status, scheduled, latest, planned, resolution, terminalReason, occurrenceId, created);
    }

    private static void ValidateExistingSlot(RecurringOccurrenceSlotSnapshot existing, RecurringOccurrenceCandidate candidate, RecurringScheduleVersionSnapshot schedule)
    {
        if (!string.Equals(existing.PlanId, candidate.PlanId, StringComparison.Ordinal) ||
            existing.ScheduleRevision != candidate.ScheduleRevision ||
            !string.Equals(existing.ScheduleDigest, candidate.ScheduleDigest, StringComparison.Ordinal) ||
            existing.LocalDate != candidate.LocalDate ||
            existing.LocalWallClockTime != candidate.LocalWallClockTime ||
            !string.Equals(existing.TimeZoneId, candidate.TimeZoneId, StringComparison.Ordinal) ||
            existing.IsScheduled != candidate.IsValid ||
            existing.ScheduledStartUtc != candidate.ScheduledStartUtc ||
            existing.LatestStartUtc != candidate.LatestStartUtc ||
            existing.PlannedEndUtc != candidate.PlannedEndUtc ||
            !string.Equals(existing.ResolutionCode, candidate.ResolutionCode, StringComparison.Ordinal) ||
            !string.Equals(existing.TerminalReasonCode, candidate.IsSkipped ? candidate.ReasonCode : null, StringComparison.Ordinal) ||
            !string.Equals(existing.OccurrenceId, candidate.IsValid ? candidate.Identity.Value : null, StringComparison.Ordinal) ||
            !string.Equals(existing.TimeZoneId, schedule.Schedule.TimeZoneId, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The existing occurrence identity is bound to different immutable slot facts.");
        }
    }

    private static void InsertSlot(SqliteConnection connection, SqliteTransaction transaction, RecurringOccurrenceCandidate candidate, long createdTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurring_occurrence_slots
                (occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date,
                 local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc,
                 latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code,
                 occurrence_id, created_at_utc)
            VALUES
                ($occurrence_identity, $plan_id, $schedule_revision, $schedule_digest, $local_date,
                 $local_wall_clock_seconds, $time_zone_id, $slot_status_code, $scheduled_start_utc,
                 $latest_start_utc, $planned_end_utc, $resolution_code, $terminal_reason_code,
                 $occurrence_id, $created_at_utc);
            """;
        Add(command, "$occurrence_identity", candidate.Identity.Value);
        Add(command, "$plan_id", candidate.PlanId);
        Add(command, "$schedule_revision", candidate.ScheduleRevision);
        Add(command, "$schedule_digest", candidate.ScheduleDigest);
        Add(command, "$local_date", candidate.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$local_wall_clock_seconds", candidate.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond);
        Add(command, "$time_zone_id", candidate.TimeZoneId);
        Add(command, "$slot_status_code", candidate.IsValid ? "scheduled" : "skipped");
        Add(command, "$scheduled_start_utc", candidate.ScheduledStartUtc is null ? null : candidate.ScheduledStartUtc.Value.UtcDateTime.Ticks);
        Add(command, "$latest_start_utc", candidate.LatestStartUtc is null ? null : candidate.LatestStartUtc.Value.UtcDateTime.Ticks);
        Add(command, "$planned_end_utc", candidate.PlannedEndUtc is null ? null : candidate.PlannedEndUtc.Value.UtcDateTime.Ticks);
        Add(command, "$resolution_code", candidate.ResolutionCode);
        Add(command, "$terminal_reason_code", candidate.IsSkipped ? candidate.ReasonCode : null);
        Add(command, "$occurrence_id", candidate.IsValid ? candidate.Identity.Value : null);
        Add(command, "$created_at_utc", createdTicks);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring occurrence slot insert did not affect exactly one row.");
        }
    }

    private static RecurringOccurrenceSlotSnapshot ToSnapshot(RecurringOccurrenceCandidate candidate, DateTimeOffset createdAtUtc) =>
        new(
            candidate.Identity.Value,
            candidate.PlanId,
            candidate.ScheduleRevision,
            candidate.ScheduleDigest,
            candidate.LocalDate,
            candidate.LocalWallClockTime,
            candidate.TimeZoneId,
            candidate.IsValid ? "scheduled" : "skipped",
            candidate.ScheduledStartUtc,
            candidate.LatestStartUtc,
            candidate.PlannedEndUtc,
            candidate.ResolutionCode,
            candidate.IsSkipped ? candidate.ReasonCode : null,
            candidate.IsValid ? candidate.Identity.Value : null,
            createdAtUtc);

    private const string SlotSelect = "SELECT occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc FROM recurring_occurrence_slots";

    private static DateOnly ReadIsoDate(SqliteDataReader reader, int ordinal)
    {
        var value = ReadRequiredText(reader, ordinal);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            string.Equals(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
            ? date
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A persisted local date is not canonical ISO-8601.");
    }

    private static DateTimeOffset? ReadNullableUtc(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    private static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal)
    {
        var value = ReadInt64(reader, ordinal);
        return value >= 0 && value <= DateTime.MaxValue.Ticks
            ? new DateTimeOffset(new DateTime(value, DateTimeKind.Utc))
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A persisted UTC tick value is invalid.");
    }

    private static new string? ReadNullableText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = ReadRequiredTextAllowEmpty(reader, ordinal);
        return value.Length == 0 ? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A nullable persisted TEXT value is blank.") : value;
    }

    private static new string ReadRequiredText(SqliteDataReader reader, int ordinal)
    {
        var value = ReadRequiredTextAllowEmpty(reader, ordinal);
        return value.Length == 0 ? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A required persisted TEXT value is blank.") : value;
    }

    private static string ReadRequiredTextAllowEmpty(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is string value && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? value
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A persisted TEXT value is invalid.");

    private static bool IsOccurrenceIdentity(string value) =>
        value.StartsWith(RecurringOccurrenceIdentity.Prefix, StringComparison.Ordinal) &&
        value.Length == RecurringOccurrenceIdentity.Prefix.Length + 64 &&
        value[RecurringOccurrenceIdentity.Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static long ReadPositiveInt64(SqliteDataReader reader, int ordinal) =>
        ReadInt64(reader, ordinal) > 0
            ? ReadInt64(reader, ordinal)
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A persisted schedule revision is not positive.");

    private static new long ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value
            ? value
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "A persisted INTEGER value is not Int64.");

    private static DateTimeOffset ToUtc(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

    private static new void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
        }
    }
}

internal enum RecurringAdvancementFailurePoint
{
    AfterOccurrenceMaterialization,
    AfterCursorUpdate,
    BeforeOperationInsert,
}

/// <summary>
/// Advances exactly one persisted recurring local slot.  Cursor state,
/// occurrence materialization and the idempotency result share one SQLite
/// write transaction; this class contains no scheduler or due-time policy.
/// </summary>
public sealed class SqliteRecurringAdvancementTransaction : SqliteRepositoryBase
{
    private readonly Action<RecurringAdvancementFailurePoint>? _failureHook;

    public SqliteRecurringAdvancementTransaction(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal SqliteRecurringAdvancementTransaction(
        SqliteOperationalStore store,
        Action<RecurringAdvancementFailurePoint> failureHook)
        : base(store)
    {
        _failureHook = failureHook ?? throw new ArgumentNullException(nameof(failureHook));
    }

    public RecurringAdvancementOperationSnapshot AdvanceOne(
        string planId,
        long scheduleRevision,
        string operationId,
        long expectedCursorVersion,
        DateTimeOffset initialAfterUtc,
        DateTimeOffset advancedAtUtc)
    {
        return AdvanceOneCore(
            planId, scheduleRevision, operationId, expectedCursorVersion,
            initialAfterUtc, advancedAtUtc, runtimeCandidate: null, out _)!;
    }

    internal RecurringAdvancementOperationSnapshot? AdvanceOneForRuntime(
        RecurringAdvancementCandidate candidate,
        string operationId,
        DateTimeOffset advancedAtUtc,
        out bool wasReplay)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return AdvanceOneCore(
            candidate.PlanId,
            candidate.ScheduleRevision,
            operationId,
            candidate.ExpectedCursorVersion,
            candidate.InitialAfterUtc,
            advancedAtUtc,
            candidate,
            out wasReplay);
    }

    private RecurringAdvancementOperationSnapshot? AdvanceOneCore(
        string planId,
        long scheduleRevision,
        string operationId,
        long expectedCursorVersion,
        DateTimeOffset initialAfterUtc,
        DateTimeOffset advancedAtUtc,
        RecurringAdvancementCandidate? runtimeCandidate,
        out bool wasReplay)
    {
        wasReplay = false;
        var canonicalPlanId = RequiredInput(planId);
        var canonicalOperationId = RequiredInput(operationId);
        if (scheduleRevision <= 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.RevisionInvalid, "The recurring schedule revision must be positive.");
        }

        if (expectedCursorVersion < 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorStale, "The expected recurring cursor version must not be negative.");
        }

        var requestDigest = RecurringAdvancementRequestDigest.Compute(
            canonicalPlanId,
            scheduleRevision,
            expectedCursorVersion,
            initialAfterUtc);
        var initialAfterTicks = UtcTicksInput(initialAfterUtc);
        var advancedAtTicks = UtcTicksInput(advancedAtUtc);

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var existingOperation = ReadOperation(connection, transaction, canonicalOperationId);
            if (existingOperation is not null)
            {
                if (!string.Equals(existingOperation.PlanId, canonicalPlanId, StringComparison.Ordinal) ||
                    existingOperation.ScheduleRevision != scheduleRevision ||
                    existingOperation.ExpectedCursorVersion != expectedCursorVersion ||
                    !string.Equals(existingOperation.RequestDigest, requestDigest, StringComparison.Ordinal))
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AdvancementIdempotencyConflict, "The operation id is already bound to a different recurring advancement request.");
                }

                var replaySchedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                        connection,
                        transaction,
                        canonicalPlanId,
                        scheduleRevision)
                    ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found for the operation.");
                var replayCursor = ReadCursor(connection, transaction, canonicalPlanId, scheduleRevision)
                    ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorNotFound, "The recurring cursor for the operation was not found.");
                ValidateCursor(connection, transaction, replayCursor, replaySchedule);
                ValidateOperation(connection, transaction, existingOperation, replayCursor, replaySchedule);
                transaction.Commit();
                wasReplay = true;
                return existingOperation;
            }

            if (runtimeCandidate is not null &&
                !RecurringAdvancementCandidateQuery.IsStillEligibleWithinTransaction(
                    connection, transaction, runtimeCandidate, advancedAtUtc))
            {
                transaction.Commit();
                return null;
            }

            EnsureEnabledPeriodicPlan(connection, transaction, canonicalPlanId);
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                    connection,
                    transaction,
                    canonicalPlanId,
                    scheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found.");
            var latest = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(connection, transaction, canonicalPlanId)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The latest recurring schedule version was not found.");
            if (latest.ScheduleRevision != scheduleRevision)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleRevisionSuperseded, "The requested recurring schedule revision has been superseded by a newer revision.");
            }

            var cursor = ReadCursor(connection, transaction, canonicalPlanId, scheduleRevision);
            if (cursor is null)
            {
                if (expectedCursorVersion != 0)
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorNotFound, "The recurring cursor does not exist for the expected non-zero version.");
                }

                InsertCursor(connection, transaction, canonicalPlanId, schedule, initialAfterTicks, advancedAtTicks);
                cursor = ReadCursor(connection, transaction, canonicalPlanId, scheduleRevision)
                    ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor could not be read after creation.");
            }

            ValidateCursor(connection, transaction, cursor, schedule);
            if (cursor.Version != expectedCursorVersion)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorStale, "The expected recurring cursor version is stale.");
            }

            if (cursor.InitialAfterUtc.UtcDateTime.Ticks != initialAfterTicks)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorRequestConflict, "The recurring cursor initial UTC boundary is immutable.");
            }

            if (advancedAtTicks < cursor.UpdatedAtUtc.UtcDateTime.Ticks)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AdvancedAtRegression, "The recurring advancement timestamp cannot move backwards from the cursor timestamp.");
            }

            if (cursor.IsExhausted)
            {
                var exhaustedOperation = InsertOperation(
                    connection,
                    transaction,
                    canonicalOperationId,
                    canonicalPlanId,
                    scheduleRevision,
                    requestDigest,
                    expectedCursorVersion,
                    "exhausted",
                    occurrenceIdentity: null,
                    cursor.Version,
                    advancedAtTicks);
                transaction.Commit();
                return exhaustedOperation;
            }

            var calculator = new RecurringOccurrenceCalculator();
            var calculation = calculator.CalculateNextFromCursor(
                canonicalPlanId,
                scheduleRevision,
                schedule.Schedule,
                cursor.ToPosition());

            RecurringOccurrenceSlotSnapshot? slot = null;
            string resultCode;
            DateOnly? nextLocalDate = cursor.LastLocalDate;
            var nextOrdinal = cursor.LastScheduleOrdinal;
            var nextExhausted = false;
            if (calculation.Candidate is not null)
            {
                var materializer = new SqliteRecurringOccurrenceMaterializationTransaction(
                    Store,
                    () => InvokeFailure(RecurringAdvancementFailurePoint.AfterOccurrenceMaterialization));
                slot = materializer.MaterializeWithinTransaction(connection, transaction, calculation.Candidate, advancedAtTicks);
                resultCode = calculation.Candidate.IsSkipped ? "skipped" : "scheduled";
                nextLocalDate = calculation.Candidate.LocalDate;
                nextOrdinal = calculator.GetScheduleOrdinal(schedule.Schedule, nextLocalDate.Value);
                nextExhausted = nextOrdinal >= schedule.Schedule.MaximumOccurrences ||
                    !calculator.HasLaterMatchingDate(schedule.Schedule, nextLocalDate.Value);
            }
            else
            {
                resultCode = "exhausted";
                nextExhausted = true;
            }

            var resultCursorVersion = checked(cursor.Version + 1);
            UpdateCursor(
                connection,
                transaction,
                cursor,
                schedule,
                nextLocalDate,
                nextOrdinal,
                nextExhausted,
                advancedAtTicks,
                resultCursorVersion);
            InvokeFailure(RecurringAdvancementFailurePoint.AfterCursorUpdate);

            InvokeFailure(RecurringAdvancementFailurePoint.BeforeOperationInsert);
            var operation = InsertOperation(
                connection,
                transaction,
                canonicalOperationId,
                canonicalPlanId,
                scheduleRevision,
                requestDigest,
                expectedCursorVersion,
                resultCode,
                slot?.OccurrenceIdentity,
                resultCursorVersion,
                advancedAtTicks);
            transaction.Commit();
            return operation;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring advancement transaction failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring advancement transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public RecurringScheduleCursorSnapshot GetCursor(string planId, long scheduleRevision)
    {
        var canonicalPlanId = RequiredInput(planId);
        if (scheduleRevision <= 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.RevisionInvalid, "The recurring schedule revision must be positive.");
        }

        using var connection = OpenBusinessConnection();
        try
        {
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(connection, null, canonicalPlanId, scheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version was not found.");
            var cursor = ReadCursor(connection, null, canonicalPlanId, scheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorNotFound, "The recurring cursor was not found.");
            ValidateCursor(connection, null, cursor, schedule);
            return cursor;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring cursor could not be read.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor is invalid.", exception);
        }
    }

    public RecurringAdvancementOperationSnapshot GetOperation(string operationId)
    {
        var canonicalOperationId = RequiredInput(operationId);
        using var connection = OpenBusinessConnection();
        try
        {
            var operation = ReadOperation(connection, null, canonicalOperationId)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring advancement operation was not found.");
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(connection, null, operation.PlanId, operation.ScheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The recurring schedule version for the operation was not found.");
            var cursor = ReadCursor(connection, null, operation.PlanId, operation.ScheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorNotFound, "The recurring cursor for the operation was not found.");
            ValidateCursor(connection, null, cursor, schedule);
            ValidateOperation(connection, null, operation, cursor, schedule);
            return operation;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring advancement operation could not be read.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring advancement operation is invalid.", exception);
        }
    }

    private static void EnsureEnabledPeriodicPlan(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_one_time, status_code FROM plans WHERE id = $plan_id;";
        Add(command, "$plan_id", planId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PlanNotFound, "The recurring plan was not found.");
        }

        if (reader.GetValue(0) is not long isOneTime || reader.GetValue(1) is not string statusCode)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The recurring plan persisted state is invalid.");
        }

        if (isOneTime == 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OneShotPlanRejected, "A one-shot plan cannot advance a recurring schedule.");
        }

        if (isOneTime != 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The recurring plan one-time flag is invalid.");
        }

        if (!string.Equals(statusCode, "enabled", StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PlanNotEnabled, "The recurring plan must be enabled before advancement.");
        }
    }

    internal static RecurringScheduleCursorSnapshot? ReadValidatedCursorWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        long scheduleRevision,
        RecurringScheduleVersionSnapshot schedule)
    {
        var cursor = ReadCursor(connection, transaction, planId, scheduleRevision);
        if (cursor is not null)
            ValidateCursor(connection, transaction, cursor, schedule);
        return cursor;
    }

    private static RecurringScheduleCursorSnapshot? ReadCursor(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        long scheduleRevision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id, schedule_revision, schedule_digest, time_zone_rules_digest,
                   initial_after_utc, last_local_date, last_schedule_ordinal, is_exhausted,
                   created_at_utc, updated_at_utc, version
            FROM recurring_schedule_cursors
            WHERE plan_id = $plan_id AND schedule_revision = $schedule_revision;
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", scheduleRevision);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCursorSnapshot(reader) : null;
    }

    private static RecurringScheduleCursorSnapshot ReadCursorSnapshot(SqliteDataReader reader)
    {
        var planId = ReadText(reader, 0);
        var revision = ReadPositive(reader, 1);
        var scheduleDigest = ReadText(reader, 2);
        var rulesDigest = ReadText(reader, 3);
        var initialAfter = ReadUtc(reader, 4);
        DateOnly? lastDate = null;
        if (!reader.IsDBNull(5))
        {
            var dateText = ReadText(reader, 5);
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                !string.Equals(parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dateText, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor local date is not canonical.");
            }

            lastDate = parsed;
        }

        var ordinal = ReadNonNegative(reader, 6);
        var exhausted = ReadBoolean(reader, 7);
        var created = ReadUtc(reader, 8);
        var updated = ReadUtc(reader, 9);
        var version = ReadNonNegative(reader, 10);
        try
        {
            _ = new RecurringScheduleCursorPosition(revision, initialAfter, lastDate, ordinal, exhausted, version);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor value object is invalid.", exception);
        }

        if (updated < created)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor timestamps are not monotonic.");
        }

        return new RecurringScheduleCursorSnapshot(planId, revision, scheduleDigest, rulesDigest, initialAfter, lastDate, ordinal, exhausted, created, updated, version);
    }

    private static RecurringAdvancementOperationSnapshot? ReadOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, plan_id, schedule_revision, request_digest,
                   expected_cursor_version, result_code, occurrence_identity,
                   result_cursor_version, created_at_utc
            FROM recurring_advancement_operations
            WHERE operation_id = $operation_id;
            """;
        Add(command, "$operation_id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperationSnapshot(reader) : null;
    }

    private static RecurringAdvancementOperationSnapshot ReadOperationSnapshot(SqliteDataReader reader) =>
        new(
            ReadText(reader, 0),
            ReadText(reader, 1),
            ReadPositive(reader, 2),
            ReadDigest(reader, 3, RecurringAdvancementRequestDigest.Prefix, 89),
            ReadNonNegative(reader, 4),
            ReadResultCode(reader, 5),
            ReadNullableText(reader, 6),
            ReadNonNegative(reader, 7),
            ReadUtc(reader, 8));

    private static RecurringAdvancementOperationSnapshot InsertOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string planId,
        long scheduleRevision,
        string requestDigest,
        long expectedCursorVersion,
        string resultCode,
        string? occurrenceIdentity,
        long resultCursorVersion,
        long createdAtTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurring_advancement_operations
                (operation_id, plan_id, schedule_revision, request_digest,
                 expected_cursor_version, result_code, occurrence_identity,
                 result_cursor_version, created_at_utc)
            VALUES
                ($operation_id, $plan_id, $schedule_revision, $request_digest,
                 $expected_cursor_version, $result_code, $occurrence_identity,
                 $result_cursor_version, $created_at_utc);
            """;
        Add(command, "$operation_id", operationId);
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", scheduleRevision);
        Add(command, "$request_digest", requestDigest);
        Add(command, "$expected_cursor_version", expectedCursorVersion);
        Add(command, "$result_code", resultCode);
        Add(command, "$occurrence_identity", occurrenceIdentity);
        Add(command, "$result_cursor_version", resultCursorVersion);
        Add(command, "$created_at_utc", createdAtTicks);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring advancement operation insert did not affect exactly one row.");
        }

        return new RecurringAdvancementOperationSnapshot(
            operationId,
            planId,
            scheduleRevision,
            requestDigest,
            expectedCursorVersion,
            resultCode,
            occurrenceIdentity,
            resultCursorVersion,
            ToUtc(createdAtTicks));
    }

    private static void InsertCursor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        RecurringScheduleVersionSnapshot schedule,
        long initialAfterTicks,
        long createdAtTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurring_schedule_cursors
                (plan_id, schedule_revision, schedule_digest, time_zone_rules_digest,
                 initial_after_utc, last_local_date, last_schedule_ordinal, is_exhausted,
                 created_at_utc, updated_at_utc, version)
            VALUES
                ($plan_id, $schedule_revision, $schedule_digest, $time_zone_rules_digest,
                 $initial_after_utc, NULL, 0, 0, $created_at_utc, $created_at_utc, 0);
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", schedule.ScheduleRevision);
        Add(command, "$schedule_digest", schedule.ScheduleDigest);
        Add(command, "$time_zone_rules_digest", schedule.TimeZoneRulesDigest);
        Add(command, "$initial_after_utc", initialAfterTicks);
        Add(command, "$created_at_utc", createdAtTicks);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The recurring cursor insert did not affect exactly one row.");
        }
    }

    private static void UpdateCursor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringScheduleCursorSnapshot current,
        RecurringScheduleVersionSnapshot schedule,
        DateOnly? lastLocalDate,
        long lastOrdinal,
        bool exhausted,
        long updatedAtTicks,
        long resultVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_schedule_cursors
            SET last_local_date = $last_local_date,
                last_schedule_ordinal = $last_schedule_ordinal,
                is_exhausted = $is_exhausted,
                updated_at_utc = $updated_at_utc,
                version = $version
            WHERE plan_id = $plan_id
              AND schedule_revision = $schedule_revision
              AND schedule_digest = $schedule_digest
              AND time_zone_rules_digest = $time_zone_rules_digest
              AND version = $expected_version;
            """;
        Add(command, "$last_local_date", lastLocalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$last_schedule_ordinal", lastOrdinal);
        Add(command, "$is_exhausted", exhausted ? 1 : 0);
        Add(command, "$updated_at_utc", updatedAtTicks);
        Add(command, "$version", resultVersion);
        Add(command, "$plan_id", current.PlanId);
        Add(command, "$schedule_revision", current.ScheduleRevision);
        Add(command, "$schedule_digest", schedule.ScheduleDigest);
        Add(command, "$time_zone_rules_digest", schedule.TimeZoneRulesDigest);
        Add(command, "$expected_version", current.Version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorStale, "The recurring cursor changed before it could be advanced.");
        }
    }

    private static void ValidateCursor(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecurringScheduleCursorSnapshot cursor,
        RecurringScheduleVersionSnapshot schedule)
    {
        if (!string.Equals(cursor.PlanId, schedule.PlanId, StringComparison.Ordinal) ||
            cursor.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(cursor.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(cursor.TimeZoneRulesDigest, schedule.TimeZoneRulesDigest, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor is not bound to the immutable schedule version.");
        }

        var calculator = new RecurringOccurrenceCalculator();
        if (cursor.LastLocalDate is not null)
        {
            long ordinal;
            try
            {
                ordinal = calculator.GetScheduleOrdinal(schedule.Schedule, cursor.LastLocalDate.Value);
            }
            catch (Exception exception) when (exception is Phase3DomainException or ArgumentException)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor local date is not a matching schedule date.", exception);
            }

            if (ordinal != cursor.LastScheduleOrdinal || ordinal > schedule.Schedule.MaximumOccurrences)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor ordinal is inconsistent with its schedule.");
            }

            var shouldBeExhausted = ordinal >= schedule.Schedule.MaximumOccurrences ||
                !calculator.HasLaterMatchingDate(schedule.Schedule, cursor.LastLocalDate.Value);
            if (cursor.IsExhausted != shouldBeExhausted)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor exhaustion flag is inconsistent with its schedule position.");
            }
        }

        if (cursor.Version == 0)
        {
            if (!cursor.IsEmpty)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A zero-version recurring cursor must be empty and not exhausted.");
            }

            if (CountOperations(connection, transaction, cursor) != 0)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A zero-version recurring cursor cannot have operation history.");
            }

            return;
        }

        if (cursor.LastLocalDate is null && !cursor.IsExhausted)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A non-empty recurring cursor without a local date must be exhausted.");
        }

        var transitionHistory = ReadTransitionHistory(connection, transaction, cursor);
        if (transitionHistory.Count != cursor.Version ||
            transitionHistory.DistinctResultVersions != cursor.Version ||
            transitionHistory.MinimumResultVersion != 1 ||
            transitionHistory.MaximumResultVersion != cursor.Version)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor does not have a contiguous one-operation-per-version transition chain.");
        }

        var currentTransition = ReadTransitionOperation(connection, transaction, cursor.PlanId, cursor.ScheduleRevision, cursor.Version - 1, cursor.Version);
        if (currentTransition is null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's current transition operation is missing or ambiguous.");
        }

        ValidateCurrentTransitionOperation(connection, transaction, currentTransition, cursor, schedule);
    }

    private static void ValidateOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecurringAdvancementOperationSnapshot operation,
        RecurringScheduleCursorSnapshot cursor,
        RecurringScheduleVersionSnapshot schedule)
    {
        if (!string.Equals(operation.PlanId, cursor.PlanId, StringComparison.Ordinal) ||
            operation.ScheduleRevision != cursor.ScheduleRevision ||
            operation.ExpectedCursorVersion < 0 ||
            operation.ResultCursorVersion < 0 ||
            operation.ResultCursorVersion > cursor.Version)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation is not bound to a valid cursor version.");
        }

        var expectedDigest = RecurringAdvancementRequestDigest.Compute(
            operation.PlanId,
            operation.ScheduleRevision,
            operation.ExpectedCursorVersion,
            cursor.InitialAfterUtc);
        if (!string.Equals(operation.RequestDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation request digest does not match its immutable request fields.");
        }

        if (operation.ResultCode is "scheduled" or "skipped")
        {
            if (operation.OccurrenceIdentity is null ||
                operation.ExpectedCursorVersion == long.MaxValue ||
                operation.ResultCursorVersion != operation.ExpectedCursorVersion + 1)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation cursor version relation is invalid.");
            }

            if (!string.Equals(operation.OccurrenceIdentity, operation.OccurrenceIdentity.Trim(), StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation occurrence relation is invalid.");
            }

            try
            {
                SqliteRecurringOccurrenceMaterializationTransaction.ValidatePersistedSlotReference(
                    connection,
                    transaction,
                    operation.OccurrenceIdentity,
                    schedule);
            }
            catch (Phase3PersistenceException exception)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation occurrence relation is invalid.", exception);
            }

            using var statusCommand = connection.CreateCommand();
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = "SELECT slot_status_code FROM recurring_occurrence_slots WHERE occurrence_identity = $identity;";
            Add(statusCommand, "$identity", operation.OccurrenceIdentity);
            if (!string.Equals(statusCommand.ExecuteScalar() as string, operation.ResultCode, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation occurrence status relation is invalid.");
            }
        }
        else if (operation.ResultCode == "exhausted")
        {
            if (operation.OccurrenceIdentity is not null || !cursor.IsExhausted ||
                (operation.ResultCursorVersion != operation.ExpectedCursorVersion &&
                 (operation.ExpectedCursorVersion == long.MaxValue || operation.ResultCursorVersion != operation.ExpectedCursorVersion + 1)))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring exhausted operation relation is invalid.");
            }
        }
        else
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "The recurring operation result code is invalid.");
        }
    }

    private static void ValidateCurrentTransitionOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecurringAdvancementOperationSnapshot operation,
        RecurringScheduleCursorSnapshot cursor,
        RecurringScheduleVersionSnapshot schedule)
    {
        ValidateOperation(connection, transaction, operation, cursor, schedule);
        if (operation.ExpectedCursorVersion != cursor.Version - 1 ||
            operation.ResultCursorVersion != cursor.Version)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's current transition operation has the wrong version relation.");
        }

        if (operation.ResultCode is "scheduled" or "skipped")
        {
            if (cursor.LastLocalDate is null || operation.OccurrenceIdentity is null)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's current occurrence transition does not project a local position.");
            }

            var localDate = ReadOperationSlotDate(connection, transaction, operation.OccurrenceIdentity);
            var calculator = new RecurringOccurrenceCalculator();
            long ordinal;
            try
            {
                ordinal = calculator.GetScheduleOrdinal(schedule.Schedule, localDate);
            }
            catch (Exception exception) when (exception is Phase3DomainException or ArgumentException)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's current occurrence transition has an invalid local date.", exception);
            }

            if (localDate != cursor.LastLocalDate.Value || ordinal != cursor.LastScheduleOrdinal)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor does not project the current occurrence transition.");
            }

            return;
        }

        if (operation.ResultCode != "exhausted")
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's current transition result code is invalid.");
        }

        if (cursor.LastLocalDate is null)
        {
            if (cursor.Version != 1 || cursor.LastScheduleOrdinal != 0)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor's direct exhausted transition is not its first transition.");
            }

            return;
        }

        if (cursor.Version <= 1)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring exhausted transition must preserve a prior local position.");
        }

        var previous = ReadTransitionOperation(
            connection,
            transaction,
            cursor.PlanId,
            cursor.ScheduleRevision,
            cursor.Version - 2,
            cursor.Version - 1);
        if (previous is null || previous.ResultCode is not ("scheduled" or "skipped") || previous.OccurrenceIdentity is null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring exhausted transition does not preserve a prior occurrence transition.");
        }

        var previousDate = ReadOperationSlotDate(connection, transaction, previous.OccurrenceIdentity);
        long previousOrdinal;
        try
        {
            previousOrdinal = new RecurringOccurrenceCalculator().GetScheduleOrdinal(schedule.Schedule, previousDate);
        }
        catch (Exception exception) when (exception is Phase3DomainException or ArgumentException)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring exhausted transition's prior local position is invalid.", exception);
        }
        if (previousDate != cursor.LastLocalDate.Value || previousOrdinal != cursor.LastScheduleOrdinal)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring exhausted transition changed the persisted local position.");
        }
    }

    private static (long Count, long DistinctResultVersions, long MinimumResultVersion, long MaximumResultVersion) ReadTransitionHistory(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecurringScheduleCursorSnapshot cursor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT result_cursor_version),
                   COALESCE(MIN(result_cursor_version), 0), COALESCE(MAX(result_cursor_version), 0)
            FROM recurring_advancement_operations
            WHERE plan_id = $plan_id
              AND schedule_revision = $schedule_revision
              AND result_cursor_version = expected_cursor_version + 1
              AND result_cursor_version <= $version;
            """;
        Add(command, "$plan_id", cursor.PlanId);
        Add(command, "$schedule_revision", cursor.ScheduleRevision);
        Add(command, "$version", cursor.Version);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetValue(0) is not long count || reader.GetValue(1) is not long distinct ||
            reader.GetValue(2) is not long minimum || reader.GetValue(3) is not long maximum)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring operation history aggregate is invalid.");
        }
        reader.Dispose();

        using var futureCommand = connection.CreateCommand();
        futureCommand.Transaction = transaction;
        futureCommand.CommandText = "SELECT COUNT(*) FROM recurring_advancement_operations WHERE plan_id = $plan_id AND schedule_revision = $schedule_revision AND result_cursor_version > $version;";
        Add(futureCommand, "$plan_id", cursor.PlanId);
        Add(futureCommand, "$schedule_revision", cursor.ScheduleRevision);
        Add(futureCommand, "$version", cursor.Version);
        if (futureCommand.ExecuteScalar() is not long futureCount || futureCount != 0)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring operation history contains a future cursor version.");
        }

        return (count, distinct, minimum, maximum);
    }

    private static long CountOperations(SqliteConnection connection, SqliteTransaction? transaction, RecurringScheduleCursorSnapshot cursor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM recurring_advancement_operations WHERE plan_id = $plan_id AND schedule_revision = $schedule_revision;";
        Add(command, "$plan_id", cursor.PlanId);
        Add(command, "$schedule_revision", cursor.ScheduleRevision);
        return command.ExecuteScalar() is long count
            ? count
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring operation history count is invalid.");
    }

    private static RecurringAdvancementOperationSnapshot? ReadTransitionOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        long scheduleRevision,
        long expectedCursorVersion,
        long resultCursorVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, plan_id, schedule_revision, request_digest,
                   expected_cursor_version, result_code, occurrence_identity,
                   result_cursor_version, created_at_utc
            FROM recurring_advancement_operations
            WHERE plan_id = $plan_id
              AND schedule_revision = $schedule_revision
              AND expected_cursor_version = $expected_cursor_version
              AND result_cursor_version = $result_cursor_version;
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$schedule_revision", scheduleRevision);
        Add(command, "$expected_cursor_version", expectedCursorVersion);
        Add(command, "$result_cursor_version", resultCursorVersion);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var result = ReadOperationSnapshot(reader);
        if (reader.Read())
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring cursor transition has duplicate operations.");
        }

        return result;
    }

    private static DateOnly ReadOperationSlotDate(SqliteConnection connection, SqliteTransaction? transaction, string occurrenceIdentity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT local_date FROM recurring_occurrence_slots WHERE occurrence_identity = $occurrence_identity;";
        Add(command, "$occurrence_identity", occurrenceIdentity);
        var value = command.ExecuteScalar() as string;
        if (value is null || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !string.Equals(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "The recurring operation slot local date is invalid.");
        }

        return date;
    }

    private void InvokeFailure(RecurringAdvancementFailurePoint point) => _failureHook?.Invoke(point);

    private static string ReadText(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is string value && string.Equals(value, value.Trim(), StringComparison.Ordinal) && value.Length > 0
            ? value
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring advancement TEXT value is invalid.");

    private static new string? ReadNullableText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = ReadText(reader, ordinal);
        return value;
    }

    private static string ReadDigest(SqliteDataReader reader, int ordinal, string prefix, int length)
    {
        var value = ReadText(reader, ordinal);
        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == length &&
            value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "A recurring advancement digest is not canonical.");
    }

    private static string ReadResultCode(SqliteDataReader reader, int ordinal)
    {
        var value = ReadText(reader, ordinal);
        return value is "scheduled" or "skipped" or "exhausted"
            ? value
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OperationRelationInvalid, "A recurring advancement result code is invalid.");
    }

    private static long ReadPositive(SqliteDataReader reader, int ordinal)
    {
        var value = ReadNonNegative(reader, ordinal);
        return value > 0 ? value : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring revision is not positive.");
    }

    private static long ReadNonNegative(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal) is long integer
            ? integer
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring INTEGER value is invalid.");
        return value >= 0 ? value : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring INTEGER value is negative.");
    }

    private static new bool ReadBoolean(SqliteDataReader reader, int ordinal) => ReadNonNegative(reader, ordinal) switch
    {
        0 => false,
        1 => true,
        _ => throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring boolean value is invalid."),
    };

    private static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal)
    {
        var ticks = ReadNonNegative(reader, ordinal);
        return ticks <= DateTime.MaxValue.Ticks
            ? ToUtc(ticks)
            : throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.CursorInvalid, "A recurring UTC tick value is invalid.");
    }

    private static DateTimeOffset ToUtc(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

    private static new void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
        }
    }
}
