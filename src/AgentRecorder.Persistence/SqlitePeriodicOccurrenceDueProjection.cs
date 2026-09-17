using System.Data;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class PeriodicOccurrenceDueProjectionLimits
{
    public const int MaxCandidateLimit = 100;
}

public abstract class SqlitePeriodicOccurrenceDueRepositoryBase : SqliteRepositoryBase
{
    protected const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    protected const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";

    protected SqlitePeriodicOccurrenceDueRepositoryBase(SqliteOperationalStore store)
        : base(store)
    {
    }

    protected static PlanDefinition ReadPlan(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PlanColumns} FROM plans WHERE id = $id;";
        Add(command, "$id", RequiredInput(planId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PlanNotFound, "The periodic plan was not found.");
        }

        return ReadPlanDefinitionSnapshot(reader);
    }

    protected static PlanOccurrence ReadOccurrence(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;";
        Add(command, "$id", RequiredInput(occurrenceId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The periodic slot occurrence was not found.");
        }

        var occurrence = ReadPlanOccurrenceSnapshot(reader);
        EnsureExistsWithinTransaction(connection, transaction, "SELECT 1 FROM plans WHERE id = $id;", ("$id", occurrence.PlanId));
        if (occurrence.RunId is not null)
        {
            EnsureExistsWithinTransaction(
                connection,
                transaction,
                "SELECT 1 FROM recording_runs WHERE id = $run_id AND occurrence_id = $occurrence_id;",
                ("$run_id", occurrence.RunId),
                ("$occurrence_id", occurrence.Id));
        }

        return occurrence;
    }

    protected static void EnsureExistsWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, string? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        if (command.ExecuteScalar() is null)
        {
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "A periodic occurrence relation points to a missing aggregate.");
        }
    }

    protected static void UpdateOccurrenceWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_occurrences
            SET status_code = $status_code,
                run_id = $run_id,
                terminal_reason_code = $terminal_reason_code,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE id = $id
              AND version = $expected_version
              AND status_code = 'scheduled'
              AND plan_id = $plan_id
              AND window_start_utc = $window_start_utc
              AND window_end_utc = $window_end_utc
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$terminal_reason_code", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", RequiredInput(occurrence.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(occurrence.PlanId));
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));

        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows == 0)
        {
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", occurrence.Id, expectedVersion);
        }

        EnsureRowsAffected(affectedRows);
    }

    protected static PeriodicOccurrenceDueProjectionResult Result(
        string resultCode,
        bool changed,
        PlanOccurrenceStatus previousStatus,
        PlanOccurrence current,
        DateTimeOffset observedAtUtc) =>
        new(
            resultCode,
            changed,
            current.Id,
            previousStatus,
            current.Status,
            current.TerminalReasonCode,
            observedAtUtc,
            current.Version);

    protected static DateTimeOffset ValidateObservedAt(DateTimeOffset observedAtUtc) =>
        observedAtUtc.Offset == TimeSpan.Zero
            ? observedAtUtc
            : throw new Phase3PersistenceException("invalid_observed_at", "observedAtUtc must be explicitly UTC.");

    protected static long ValidateExpectedVersion(long expectedVersion) =>
        expectedVersion >= 0
            ? expectedVersion
            : throw new Phase3PersistenceException("invalid_version", "The expected occurrence version must be non-negative.");

    protected static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
            // Preserve the original stable failure.
        }
    }
}

/// <summary>
/// Read-only bounded projection of persisted recurring slots that have
/// reached their scheduled start.  The query never creates or mutates domain
/// state and does not touch leases, scopes, runs, or capture backends.
/// </summary>
public sealed class SqlitePeriodicOccurrenceDueCandidateQuery : SqlitePeriodicOccurrenceDueRepositoryBase
{
    public SqlitePeriodicOccurrenceDueCandidateQuery(SqliteOperationalStore store)
        : base(store)
    {
    }

    public IReadOnlyList<PeriodicOccurrenceDueCandidate> ListReady(DateTimeOffset observedAtUtc, int limit)
    {
        var observed = ValidateObservedAt(observedAtUtc);
        if (limit <= 0 || limit > PeriodicOccurrenceDueProjectionLimits.MaxCandidateLimit)
        {
            throw new Phase3PersistenceException("invalid_limit", $"The candidate limit must be between 1 and {PeriodicOccurrenceDueProjectionLimits.MaxCandidateLimit}.");
        }

        using var connection = OpenBusinessConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            var candidates = new List<PeriodicOccurrenceDueCandidate>(limit);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.occurrence_identity,
                       s.plan_id,
                       s.schedule_revision,
                       s.schedule_digest,
                       s.scheduled_start_utc,
                       s.latest_start_utc,
                       s.planned_end_utc,
                       o.version,
                       p.status_code,
                       p.is_one_time
                FROM recurring_occurrence_slots AS s
                JOIN plans AS p ON p.id = s.plan_id
                JOIN plan_occurrences AS o
                  ON o.id = s.occurrence_id
                 AND o.plan_id = s.plan_id
                WHERE s.slot_status_code = 'scheduled'
                  AND o.status_code = 'scheduled'
                  AND p.status_code IN ('enabled', 'cancelled')
                  AND p.is_one_time = 0
                  AND s.scheduled_start_utc <= $observed_at_utc
                ORDER BY s.scheduled_start_utc ASC,
                         s.plan_id ASC,
                         s.schedule_revision ASC,
                         s.occurrence_identity ASC
                LIMIT $limit;
                """;
            Add(command, "$observed_at_utc", UtcTicksInput(observed));
            Add(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var identity = ReadRequiredText(reader, 0);
                var planId = ReadRequiredText(reader, 1);
                var revision = ReadInt64(reader, 2);
                var digest = ReadRequiredText(reader, 3);
                var scheduledStart = ReadUtcDateTimeOffset(reader, 4);
                var latestStart = ReadUtcDateTimeOffset(reader, 5);
                var plannedEnd = ReadUtcDateTimeOffset(reader, 6);
                var occurrenceVersion = ReadInt64(reader, 7);
                var planStatus = ReadRequiredText(reader, 8);
                var isOneTime = ReadInt64(reader, 9);

                if (isOneTime != 0 || planStatus is not ("enabled" or "cancelled"))
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The due candidate query returned an invalid plan state.");
                }

                var plan = ReadPlan(connection, transaction, planId);
                if (plan.IsOneTime || plan.Status is not (PlanDefinitionStatus.Enabled or PlanDefinitionStatus.Cancelled))
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The persisted periodic plan state is inconsistent with the due candidate query.");
                }

                var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(connection, transaction, planId, revision)
                    ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The due candidate schedule version was not found.");
                var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(connection, transaction, identity, schedule);
                if (!slot.IsScheduled || slot.OccurrenceId is null ||
                    !string.Equals(slot.OccurrenceIdentity, identity, StringComparison.Ordinal) ||
                    !string.Equals(slot.PlanId, planId, StringComparison.Ordinal) ||
                    slot.ScheduleRevision != revision ||
                    !string.Equals(slot.ScheduleDigest, digest, StringComparison.Ordinal) ||
                    slot.ScheduledStartUtc != scheduledStart ||
                    slot.LatestStartUtc != latestStart ||
                    slot.PlannedEndUtc != plannedEnd)
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The due candidate facts are inconsistent with the validated recurring slot.");
                }

                var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
                if (occurrence.Status != PlanOccurrenceStatus.Scheduled ||
                    occurrence.Version != occurrenceVersion ||
                    !string.Equals(occurrence.Id, identity, StringComparison.Ordinal) ||
                    !string.Equals(occurrence.PlanId, planId, StringComparison.Ordinal))
                {
                    throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The due candidate occurrence relation is not a scheduled immutable match.");
                }

                candidates.Add(new PeriodicOccurrenceDueCandidate(
                    planId,
                    revision,
                    digest,
                    identity,
                    scheduledStart,
                    latestStart,
                    plannedEnd,
                    occurrence.Version));
            }

            transaction.Commit();
            return candidates;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The due candidate query failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PersistedDataInvalid, "The due candidate query encountered invalid persisted data.", exception);
        }
    }
}

/// <summary>
/// Atomically projects one scheduled recurring occurrence to due or missed,
/// or converges to a stable no-op result for a competing lifecycle owner.
/// </summary>
public sealed class SqlitePeriodicOccurrenceDueProjectionTransaction : SqlitePeriodicOccurrenceDueRepositoryBase
{
    public SqlitePeriodicOccurrenceDueProjectionTransaction(SqliteOperationalStore store)
        : base(store)
    {
    }

    public PeriodicOccurrenceDueProjectionResult Project(
        string occurrenceIdentity,
        long expectedOccurrenceVersion,
        DateTimeOffset observedAtUtc) =>
        ProjectCore(
            occurrenceIdentity,
            expectedOccurrenceVersion,
            observedAtUtc,
            candidate: null);

    /// <summary>
    /// Atomically validates every immutable fact carried by one detached
    /// candidate and performs the existing due state machine in the same
    /// immediate transaction. This overload is intentionally internal: the
    /// coordinator must not split candidate validation from due projection.
    /// </summary>
    internal PeriodicOccurrenceDueProjectionResult Project(
        PeriodicOccurrenceDueCandidate candidate,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return ProjectCore(
            candidate.OccurrenceIdentity,
            candidate.OccurrenceVersion,
            observedAtUtc,
            candidate);
    }

    private PeriodicOccurrenceDueProjectionResult ProjectCore(
        string occurrenceIdentity,
        long expectedOccurrenceVersion,
        DateTimeOffset observedAtUtc,
        PeriodicOccurrenceDueCandidate? candidate)
    {
        var identity = RequiredInput(occurrenceIdentity);
        var expectedVersion = ValidateExpectedVersion(expectedOccurrenceVersion);
        var observed = ValidateObservedAt(observedAtUtc);

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadSlotByIdentity(connection, transaction, identity)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The periodic occurrence identity was not found.");
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(connection, transaction, slot.PlanId, slot.ScheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The periodic occurrence schedule version was not found.");
            slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(connection, transaction, identity, schedule);
            if (!slot.IsScheduled || slot.OccurrenceId is null)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "Only a scheduled recurring slot can be projected.");
            }

            var plan = ReadPlan(connection, transaction, slot.PlanId);
            if (plan.IsOneTime)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OneShotPlanRejected, "A one-shot plan cannot be projected as a periodic occurrence.");
            }

            var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
            if (!string.Equals(occurrence.Id, identity, StringComparison.Ordinal) ||
                !string.Equals(occurrence.PlanId, slot.PlanId, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.OccurrenceIdentityConflict, "The periodic occurrence is not bound to its slot identity.");
            }

            if (candidate is not null)
            {
                ValidateCandidateBinding(candidate, slot, schedule, occurrence);
            }

            if (occurrence.Status == PlanOccurrenceStatus.Due)
            {
                EnsureExpectedVersionIsNotFuture(occurrence.Version, expectedVersion);
                transaction.Commit();
                return Result(PeriodicOccurrenceDueResultCodes.AlreadyDue, false, occurrence.Status, occurrence, observed);
            }

            if (Phase3TransitionGuards.IsTerminal(occurrence.Status))
            {
                EnsureExpectedVersionIsNotFuture(occurrence.Version, expectedVersion);
                transaction.Commit();
                return Result(PeriodicOccurrenceDueResultCodes.AlreadyTerminal, false, occurrence.Status, occurrence, observed);
            }

            if (occurrence.Status != PlanOccurrenceStatus.Scheduled)
            {
                EnsureExpectedVersionIsNotFuture(occurrence.Version, expectedVersion);
                transaction.Commit();
                return Result(PeriodicOccurrenceDueResultCodes.AlreadyClaimed, false, occurrence.Status, occurrence, observed);
            }

            if (occurrence.Version != expectedVersion)
            {
                throw new Phase3PersistenceException("concurrency_conflict", "The expected occurrence version is stale before the periodic due decision.");
            }

            if (observed < occurrence.UpdatedAtUtc)
            {
                throw new Phase3PersistenceException("non_monotonic_time", "observedAtUtc must not precede the persisted occurrence updated time.");
            }

            if (plan.Status == PlanDefinitionStatus.Paused)
            {
                transaction.Commit();
                return Result(PeriodicOccurrenceDueResultCodes.PlanPaused, false, occurrence.Status, occurrence, observed);
            }

            if (plan.Status == PlanDefinitionStatus.Draft)
            {
                throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PlanNotEnabled, "A draft periodic plan is not eligible for due projection.");
            }

            var latestSchedule = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(connection, transaction, slot.PlanId)
                ?? throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.ScheduleVersionNotFound, "The periodic plan has no current schedule version.");

            var (nextStatus, resultCode, terminalReason) = plan.Status switch
            {
                PlanDefinitionStatus.Cancelled => (PlanOccurrenceStatus.Cancelled, PeriodicOccurrenceDueResultCodes.Cancelled, PeriodicOccurrenceDueTerminalReasonCodes.PlanCancelled),
                PlanDefinitionStatus.Enabled when slot.ScheduleRevision != latestSchedule.ScheduleRevision => (PlanOccurrenceStatus.Cancelled, PeriodicOccurrenceDueResultCodes.Superseded, PeriodicOccurrenceDueTerminalReasonCodes.ScheduleRevisionSuperseded),
                PlanDefinitionStatus.Enabled when observed < slot.ScheduledStartUtc!.Value => (PlanOccurrenceStatus.Scheduled, PeriodicOccurrenceDueResultCodes.BeforeWindow, null),
                PlanDefinitionStatus.Enabled when observed <= slot.LatestStartUtc!.Value => (PlanOccurrenceStatus.Due, PeriodicOccurrenceDueResultCodes.Due, null),
                PlanDefinitionStatus.Enabled => (PlanOccurrenceStatus.Missed, PeriodicOccurrenceDueResultCodes.Missed, PeriodicOccurrenceDueTerminalReasonCodes.MissedScheduleWindow),
                _ => throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.PlanNotEnabled, "The periodic plan is not enabled for due projection."),
            };

            if (nextStatus == PlanOccurrenceStatus.Scheduled)
            {
                transaction.Commit();
                return Result(resultCode, false, occurrence.Status, occurrence, observed);
            }

            var previousStatus = occurrence.Status;
            var transition = occurrence.TryTransition(nextStatus, observed, terminalReason);
            if (!transition.Succeeded)
            {
                throw new Phase3PersistenceException("periodic_due_transition_rejected", "The periodic due transition was rejected by the domain state machine.");
            }

            UpdateOccurrenceWithinTransaction(connection, transaction, occurrence, expectedVersion);
            transaction.Commit();
            return Result(resultCode, true, previousStatus, occurrence, observed);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (Phase3DomainException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException("periodic_due_transition_rejected", "The periodic due transition was rejected by the domain.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The periodic due projection transaction failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringPersistenceReasonCodes.AtomicPersistenceFailed, "The periodic due projection transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static void EnsureExpectedVersionIsNotFuture(long currentVersion, long expectedVersion)
    {
        if (expectedVersion > currentVersion)
        {
            throw new Phase3PersistenceException("concurrency_conflict", "The expected occurrence version is newer than the persisted occurrence.");
        }
    }

    private static void ValidateCandidateBinding(
        PeriodicOccurrenceDueCandidate candidate,
        RecurringOccurrenceSlotSnapshot slot,
        RecurringScheduleVersionSnapshot schedule,
        PlanOccurrence occurrence)
    {
        if (!string.Equals(candidate.PlanId, slot.PlanId, StringComparison.Ordinal) ||
            candidate.ScheduleRevision != slot.ScheduleRevision ||
            !string.Equals(candidate.ScheduleDigest, slot.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(candidate.OccurrenceIdentity, slot.OccurrenceIdentity, StringComparison.Ordinal) ||
            candidate.ScheduledStartUtc != slot.ScheduledStartUtc ||
            candidate.LatestStartUtc != slot.LatestStartUtc ||
            candidate.PlannedEndUtc != slot.PlannedEndUtc ||
            !string.Equals(schedule.PlanId, slot.PlanId, StringComparison.Ordinal) ||
            schedule.ScheduleRevision != candidate.ScheduleRevision ||
            !string.Equals(schedule.ScheduleDigest, candidate.ScheduleDigest, StringComparison.Ordinal) ||
            occurrence.Version < 0)
        {
            throw new Phase3PersistenceException(
                RecurringPersistenceReasonCodes.OccurrenceIdentityConflict,
                "The due candidate immutable facts do not match the durable recurring occurrence.");
        }
    }
}
