using System.Data;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class RecurringOccurrenceNaturalWakeCandidateLimits
{
    internal const int MaxCandidateLimit = 100;
}

/// <summary>
/// The minimum durable identity needed to hand one pre-start occurrence to
/// the existing coordinator.  No specification, proof, ticket, output path,
/// capture parameter, or backend crosses this boundary.
/// </summary>
internal sealed record RecurringOccurrenceNaturalWakeCandidate(
    string LeaseId,
    PeriodicOccurrenceDueCandidate Slot,
    string ObservedOccurrenceStatusCode);

internal sealed record RecurringOccurrenceNaturalWakeCandidatePage(
    IReadOnlyList<RecurringOccurrenceNaturalWakeCandidate> Candidates,
    bool HasMore);

/// <summary>
/// Bounded, read-only discovery of recurring occurrences which are still
/// before the start gate.  This query intentionally does not mutate due,
/// authorization, reservation, quota, or lifecycle state.
/// </summary>
internal sealed class RecurringOccurrenceNaturalWakeCandidateQuery : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";

    internal RecurringOccurrenceNaturalWakeCandidateQuery(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal RecurringOccurrenceNaturalWakeCandidatePage ListReady(
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding,
        int limit)
    {
        var observed = ValidateObservedAt(observedAtUtc);
        ValidateCanonicalIdentity(currentUserSid, "currentUserSid");
        ValidateCanonicalIdentity(sessionBinding, "sessionBinding");
        if (limit is < 1 or > RecurringOccurrenceNaturalWakeCandidateLimits.MaxCandidateLimit)
        {
            throw new Phase3PersistenceException(
                "invalid_limit",
                $"The natural-wake candidate limit must be between 1 and {RecurringOccurrenceNaturalWakeCandidateLimits.MaxCandidateLimit}.");
        }

        using var connection = OpenBusinessConnection();
        SqliteRecurringOccurrenceExecutionSpecificationReader.RegisterNaturalWakeSqlFunctions(connection);
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var rows = ReadBaseRows(connection, transaction, observed, currentUserSid, sessionBinding, limit + 1);
            var candidates = new List<RecurringOccurrenceNaturalWakeCandidate>(rows.Count);
            var identities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var candidate = ResolveCandidate(
                    connection,
                    transaction,
                    row,
                    observed,
                    currentUserSid,
                    sessionBinding);
                if (candidate is null)
                    continue;

                if (!identities.Add(candidate.Slot.OccurrenceIdentity))
                {
                    throw InvalidPersistedShape("More than one natural-wake candidate points to the same occurrence.");
                }

                candidates.Add(candidate);
            }

            var hasMore = candidates.Count > limit;
            if (candidates.Count > limit)
                candidates.RemoveRange(limit, candidates.Count - limit);

            transaction.Commit();
            return new RecurringOccurrenceNaturalWakeCandidatePage(candidates, hasMore);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                RecurringPersistenceReasonCodes.AtomicPersistenceFailed,
                "The recurring natural-wake candidate query failed.",
                exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw InvalidPersistedShape("The recurring natural-wake candidate query encountered invalid persisted data.", exception);
        }
    }

    private static IReadOnlyList<BaseRow> ReadBaseRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding,
        int readLimit)
    {
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
                   s.occurrence_id,
                   o.status_code,
                   o.version,
                   o.run_id
            FROM recurring_occurrence_slots AS s
            JOIN plans AS p
              ON p.id = s.plan_id
            JOIN plan_occurrences AS o
              ON o.id = s.occurrence_id
             AND o.plan_id = s.plan_id
            WHERE s.slot_status_code = 'scheduled'
              AND s.scheduled_start_utc IS NOT NULL
              AND s.latest_start_utc IS NOT NULL
              AND s.planned_end_utc IS NOT NULL
              AND s.scheduled_start_utc <= $observed_at_utc
              AND p.is_one_time = 0
              AND (
                    p.status_code = 'enabled'
                    OR (p.status_code = 'cancelled' AND o.status_code IN ('scheduled', 'due'))
                  )
              AND o.status_code IN ('scheduled', 'due', 'authorized', 'run_created')
              AND (
                    (
                        o.status_code IN ('scheduled', 'due')
                        AND EXISTS (
                            SELECT 1
                            FROM recurring_consent_leases AS l
                            JOIN recurring_schedule_versions AS sv
                              ON sv.plan_id = l.plan_id
                             AND sv.schedule_revision = l.schedule_revision
                             AND sv.schedule_digest = l.schedule_digest
                             AND sv.time_zone_rules_digest = l.time_zone_rules_digest
                            JOIN recurring_plan_profile_bindings AS b
                              ON b.plan_id = l.plan_id
                             AND b.profile_id = l.profile_id
                             AND b.profile_version = l.profile_version
                             AND b.profile_digest = l.profile_digest
                            JOIN recurring_lease_local_approvals AS a
                              ON a.lease_id = l.lease_id
                             AND a.plan_id = l.plan_id
                             AND a.configuration_digest = l.configuration_digest
                             AND a.authorization_digest = l.authorization_digest
                            WHERE l.plan_id = s.plan_id
                              AND l.status_code = 'active'
                              AND l.schedule_revision = s.schedule_revision
                              AND l.schedule_digest = s.schedule_digest
                              AND l.valid_from_utc <= s.scheduled_start_utc
                              AND l.valid_until_utc > s.planned_end_utc
                              AND l.valid_until_utc > $observed_at_utc
                              AND a.current_user_sid = $current_user_sid
                              AND a.session_binding = $session_binding
                        )
                    )
                    OR
                    (
                        o.status_code IN ('authorized', 'run_created')
                        AND (
                            NOT EXISTS (
                                SELECT 1
                                FROM recurring_occurrence_execution_specs AS x
                                WHERE x.occurrence_identity = s.occurrence_identity
                                   OR x.occurrence_id = o.id
                            )
                            OR EXISTS (
                                SELECT 1
                                FROM recurring_occurrence_execution_specs AS x
                                WHERE (x.occurrence_identity = s.occurrence_identity
                                    OR x.occurrence_id = o.id)
                                  AND x.plan_id = s.plan_id
                                  AND x.approved_current_user_sid = $current_user_sid
                                  AND x.approved_session_binding = $session_binding
                            )
                        )
                    )
              )
              AND NOT (
                    o.status_code = 'run_created'
                    AND (
                        SELECT COUNT(*)
                        FROM recording_runs AS rr
                        WHERE rr.occurrence_id = o.id
                    ) = 1
                    AND (
                        SELECT COUNT(*)
                        FROM recurring_lease_uses AS uu
                        WHERE uu.occurrence_identity = s.occurrence_identity
                           OR uu.occurrence_id = o.id
                    ) = 1
                    AND (
                        SELECT COUNT(*)
                        FROM recurring_occurrence_execution_specs AS xx
                        WHERE xx.occurrence_identity = s.occurrence_identity
                           OR xx.occurrence_id = o.id
                    ) = 1
                    AND EXISTS (
                        SELECT 1
                        FROM recurring_occurrence_execution_specs AS x
                        JOIN recurring_consent_leases AS l
                          ON l.lease_id = x.lease_id
                         AND l.plan_id = x.plan_id
                         AND l.configuration_digest = x.configuration_digest
                         AND l.authorization_digest = x.lease_authorization_digest
                        JOIN recurring_schedule_versions AS sv
                          ON sv.plan_id = x.plan_id
                         AND sv.schedule_revision = x.schedule_revision
                         AND sv.schedule_digest = x.schedule_digest
                         AND sv.time_zone_rules_digest = x.time_zone_rules_digest
                        JOIN recurring_plan_profile_bindings AS b
                          ON b.plan_id = x.plan_id
                         AND b.profile_id = x.profile_id
                         AND b.profile_version = x.profile_version
                         AND b.profile_digest = x.profile_digest
                        JOIN recurring_fixed_region_profile_versions AS p
                          ON p.profile_id = x.profile_id
                         AND p.profile_version = x.profile_version
                         AND p.profile_digest = x.profile_digest
                        JOIN recurring_lease_local_approvals AS a
                          ON a.approval_id = x.local_approval_id
                         AND a.lease_id = x.lease_id
                         AND a.plan_id = x.plan_id
                         AND a.configuration_digest = x.configuration_digest
                         AND a.authorization_digest = x.lease_authorization_digest
                         AND a.approval_digest = x.local_approval_digest
                         AND a.current_user_sid = x.approved_current_user_sid
                         AND a.session_binding = x.approved_session_binding
                        JOIN recording_runs AS r
                          ON r.id = o.run_id
                         AND r.occurrence_id = o.id
                        JOIN recurring_lease_uses AS u
                          ON u.lease_id = l.lease_id
                         AND u.plan_id = x.plan_id
                         AND u.occurrence_identity = x.occurrence_identity
                         AND u.occurrence_id = o.id
                         AND u.run_id = r.id
                        WHERE x.occurrence_identity = s.occurrence_identity
                          AND x.occurrence_id = o.id
                          AND x.plan_id = s.plan_id
                          AND x.schedule_revision = s.schedule_revision
                          AND x.schedule_digest = s.schedule_digest
                          AND x.time_zone_rules_digest = sv.time_zone_rules_digest
                          AND x.scheduled_start_utc = s.scheduled_start_utc
                          AND x.latest_start_utc = s.latest_start_utc
                          AND x.planned_end_utc = s.planned_end_utc
                          AND x.specification_version = 1
                          AND l.schedule_revision = x.schedule_revision
                          AND l.schedule_digest = x.schedule_digest
                          AND l.time_zone_rules_digest = x.time_zone_rules_digest
                          AND l.profile_id = x.profile_id
                          AND l.profile_version = x.profile_version
                          AND l.profile_digest = x.profile_digest
                          AND x.configuration_digest = l.configuration_digest
                          AND x.lease_authorization_digest = l.authorization_digest
                          AND x.local_approval_id = a.approval_id
                          AND x.local_approval_digest = a.approval_digest
                          AND x.approved_current_user_sid = $current_user_sid
                          AND x.approved_session_binding = $session_binding
                          AND a.current_user_sid = $current_user_sid
                          AND a.session_binding = $session_binding
                          AND x.stable_display_fingerprint = p.stable_display_fingerprint
                          AND x.display_bounds_x = p.display_bounds_x
                          AND x.display_bounds_y = p.display_bounds_y
                          AND x.display_bounds_width = p.display_bounds_width
                          AND x.display_bounds_height = p.display_bounds_height
                          AND x.region_x = p.region_x
                          AND x.region_y = p.region_y
                          AND x.region_width = p.region_width
                          AND x.region_height = p.region_height
                          AND x.virtual_region_x = p.display_bounds_x + p.region_x
                          AND x.virtual_region_y = p.display_bounds_y + p.region_y
                          AND x.virtual_region_width = p.region_width
                          AND x.virtual_region_height = p.region_height
                          AND x.dpi_x = p.dpi_x
                          AND x.dpi_y = p.dpi_y
                          AND x.physical_width = p.physical_width
                          AND x.physical_height = p.physical_height
                          AND x.orientation_code = p.orientation_code
                          AND x.topology_digest = p.topology_digest
                          AND x.backend_code = p.backend_code
                          AND x.audio_mode_code = p.audio_mode_code
                          AND x.duration_ticks = p.duration_ms * 10000
                          AND x.countdown_seconds = p.countdown_seconds
                          AND x.normalized_output_directory = p.output_directory
                          AND x.frozen_output_file_name = recurring_specification_output_file_name(
                                p.filename_prefix,
                                x.occurrence_identity,
                                x.scheduled_start_utc)
                          AND x.frozen_output_file_path = recurring_specification_output_file_path(
                                p.output_directory,
                                p.filename_prefix,
                                x.occurrence_identity,
                                x.scheduled_start_utc)
                          AND x.output_conflict_policy_code = p.output_conflict_policy_code
                          AND x.output_conflict_policy_code = 'fail_if_exists'
                          AND x.evaluated_at_utc >= x.scheduled_start_utc
                          AND x.evaluated_at_utc <= x.latest_start_utc
                          AND x.evaluated_at_utc >= l.valid_from_utc
                          AND x.evaluated_at_utc < l.valid_until_utc
                          AND x.evaluated_at_utc <= x.planned_end_utc - x.duration_ticks
                          AND x.evaluated_at_utc <= l.valid_until_utc - x.duration_ticks
                          AND x.evaluated_at_utc <= o.updated_at_utc
                          -- Reuse the canonical domain rehydrate and its
                          -- full-field digest recomputation before LIMIT.
                          AND recurring_specification_is_canonical(
                                x.occurrence_identity,
                                x.occurrence_id,
                                x.plan_id,
                                x.lease_id,
                                x.schedule_revision,
                                x.schedule_digest,
                                x.time_zone_rules_digest,
                                x.profile_id,
                                x.profile_version,
                                x.profile_digest,
                                x.configuration_digest,
                                x.lease_authorization_digest,
                                x.local_approval_id,
                                x.local_approval_digest,
                                x.scheduled_start_utc,
                                x.latest_start_utc,
                                x.planned_end_utc,
                                x.evaluated_at_utc,
                                x.stable_display_fingerprint,
                                x.display_bounds_x,
                                x.display_bounds_y,
                                x.display_bounds_width,
                                x.display_bounds_height,
                                x.region_x,
                                x.region_y,
                                x.region_width,
                                x.region_height,
                                x.virtual_region_x,
                                x.virtual_region_y,
                                x.virtual_region_width,
                                x.virtual_region_height,
                                x.dpi_x,
                                x.dpi_y,
                                x.physical_width,
                                x.physical_height,
                                x.orientation_code,
                                x.topology_digest,
                                x.backend_code,
                                x.audio_mode_code,
                                x.duration_ticks,
                                x.countdown_seconds,
                                x.normalized_output_directory,
                                x.frozen_output_file_name,
                                x.frozen_output_file_path,
                                x.output_conflict_policy_code,
                                x.approved_current_user_sid,
                                x.approved_session_binding,
                                x.specification_version,
                                x.specification_digest) = 1
                          AND o.terminal_reason_code IS NULL
                          AND o.version IN (4, 5)
                          AND r.has_crossed_start_commit = 1
                          AND r.terminal_reason_code IS NULL
                          AND r.media_artifact_id IS NULL
                          AND r.bundle_id IS NULL
                          -- This is the SQL counterpart of
                          -- HasClaimIdentityAndMonotonicEvidence.  Every
                          -- relation is required before LIMIT may hide the
                          -- source row from the resolver.
                          AND r.created_at_utc = u.created_at_utc
                          AND o.created_at_utc <= o.updated_at_utc
                          AND o.created_at_utc <= r.created_at_utc
                          AND r.created_at_utc <= r.updated_at_utc
                          AND u.created_at_utc <= u.updated_at_utc
                          AND o.updated_at_utc <= r.updated_at_utc
                          AND u.updated_at_utc <= r.updated_at_utc
                          AND l.per_run_duration_ticks > 0
                          AND u.reserved_use_count = 1
                          AND u.reserved_duration_ticks > 0
                          AND u.reserved_duration_ticks = l.per_run_duration_ticks
                          AND u.actual_settled_duration_ticks IS NULL
                          AND (
                                (r.status_code = 'start_committed' AND r.version = 2
                                 AND u.status_code = 'start_committed' AND u.version = 1
                                 AND r.updated_at_utc = u.updated_at_utc)
                             OR (r.status_code = 'recording' AND r.version = 3
                                 AND u.status_code = 'consumed' AND u.version = 2
                                 AND r.updated_at_utc = u.updated_at_utc)
                             OR (r.status_code = 'finalizing' AND r.version = 4
                                 AND u.status_code = 'consumed' AND u.version = 2)
                             OR (r.status_code = 'media_ready' AND r.version = 5
                                 AND u.status_code = 'consumed' AND u.version = 2)
                          )
                    )
              )
            ORDER BY s.scheduled_start_utc ASC,
                     s.plan_id COLLATE BINARY ASC,
                     s.schedule_revision ASC,
                     s.occurrence_identity COLLATE BINARY ASC
            LIMIT $read_limit;
            """;
        Add(command, "$observed_at_utc", UtcTicksInput(observedAtUtc));
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        Add(command, "$read_limit", readLimit);

        var rows = new List<BaseRow>(readLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new BaseRow(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadInt64(reader, 2),
                ReadRequiredText(reader, 3),
                ReadUtcDateTimeOffset(reader, 4),
                ReadUtcDateTimeOffset(reader, 5),
                ReadUtcDateTimeOffset(reader, 6),
                ReadRequiredText(reader, 7),
                ReadRequiredText(reader, 8),
                ReadInt64(reader, 9),
                ReadNullableText(reader, 10)));
        }

        return rows;
    }

    private static RecurringOccurrenceNaturalWakeCandidate? ResolveCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BaseRow row,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding)
    {
        var plan = ReadPlan(connection, transaction, row.PlanId);
        if (plan.IsOneTime || plan.Status is not (PlanDefinitionStatus.Enabled or PlanDefinitionStatus.Cancelled))
            throw InvalidPersistedShape("The natural-wake candidate points to an invalid plan state.");

        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection,
                transaction,
                row.PlanId,
                row.ScheduleRevision)
            ?? throw InvalidPersistedShape("The natural-wake candidate schedule version is missing.");
        var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
            connection,
            transaction,
            row.OccurrenceIdentity,
            schedule);
        if (!slot.IsScheduled ||
            slot.OccurrenceId is null ||
            !string.Equals(slot.OccurrenceId, row.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(slot.PlanId, row.PlanId, StringComparison.Ordinal) ||
            slot.ScheduleRevision != row.ScheduleRevision ||
            !string.Equals(slot.ScheduleDigest, row.ScheduleDigest, StringComparison.Ordinal) ||
            slot.ScheduledStartUtc != row.ScheduledStartUtc ||
            slot.LatestStartUtc != row.LatestStartUtc ||
            slot.PlannedEndUtc != row.PlannedEndUtc)
        {
            throw InvalidPersistedShape("The natural-wake candidate slot facts are not immutable matches.");
        }

        var occurrence = ReadOccurrence(connection, transaction, row.OccurrenceId);
        if (!string.Equals(occurrence.Id, row.OccurrenceIdentity, StringComparison.Ordinal) ||
            !string.Equals(occurrence.PlanId, row.PlanId, StringComparison.Ordinal) ||
            occurrence.Version != row.OccurrenceVersion ||
            !string.Equals(occurrence.StatusCode, row.StatusCode, StringComparison.Ordinal))
        {
            throw InvalidPersistedShape("The natural-wake candidate occurrence facts changed during the read.");
        }

        var candidate = new PeriodicOccurrenceDueCandidate(
            row.PlanId,
            row.ScheduleRevision,
            row.ScheduleDigest,
            row.OccurrenceIdentity,
            row.ScheduledStartUtc,
            row.LatestStartUtc,
            row.PlannedEndUtc,
            row.OccurrenceVersion);

        return occurrence.Status switch
        {
            PlanOccurrenceStatus.Scheduled or PlanOccurrenceStatus.Due =>
                ResolveUnclaimedCandidate(
                    connection,
                    transaction,
                    plan,
                    schedule,
                    slot,
                    occurrence,
                    candidate,
                    observedAtUtc,
                    currentUserSid,
                    sessionBinding),
            PlanOccurrenceStatus.Authorized or PlanOccurrenceStatus.RunCreated =>
                ResolveBoundSpecificationCandidate(
                    connection,
                    transaction,
                    plan,
                    schedule,
                    slot,
                    occurrence,
                    candidate,
                    observedAtUtc,
                    currentUserSid,
                    sessionBinding),
            _ => throw InvalidPersistedShape("The natural-wake candidate status is outside the pre-start matrix."),
        };
    }

    private static RecurringOccurrenceNaturalWakeCandidate? ResolveUnclaimedCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        RecurringOccurrenceSlotSnapshot slot,
        PlanOccurrence occurrence,
        PeriodicOccurrenceDueCandidate candidate,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding)
    {
        if (occurrence.RunId is not null ||
            CountRows(connection, transaction, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity OR occurrence_id = $occurrence_id;", candidate.OccurrenceIdentity, slot.OccurrenceId) != 0 ||
            CountRows(connection, transaction, "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrence_id;", null, slot.OccurrenceId) != 0 ||
            CountRows(connection, transaction, "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity OR occurrence_id = $occurrence_id;", candidate.OccurrenceIdentity, slot.OccurrenceId) != 0)
        {
            throw InvalidPersistedShape("A scheduled or due occurrence has unexpected execution children.");
        }

        var leaseIds = new List<string>(2);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT l.lease_id
                FROM recurring_consent_leases AS l
                JOIN recurring_schedule_versions AS sv
                  ON sv.plan_id = l.plan_id
                 AND sv.schedule_revision = l.schedule_revision
                 AND sv.schedule_digest = l.schedule_digest
                 AND sv.time_zone_rules_digest = l.time_zone_rules_digest
                JOIN recurring_plan_profile_bindings AS b
                  ON b.plan_id = l.plan_id
                 AND b.profile_id = l.profile_id
                 AND b.profile_version = l.profile_version
                 AND b.profile_digest = l.profile_digest
                JOIN recurring_lease_local_approvals AS a
                  ON a.lease_id = l.lease_id
                 AND a.plan_id = l.plan_id
                 AND a.configuration_digest = l.configuration_digest
                 AND a.authorization_digest = l.authorization_digest
                WHERE l.plan_id = $plan_id
                  AND l.status_code = 'active'
                  AND l.schedule_revision = $schedule_revision
                  AND l.schedule_digest = $schedule_digest
                  AND l.valid_from_utc <= $scheduled_start_utc
                  AND l.valid_until_utc > $planned_end_utc
                  AND l.valid_until_utc > $observed_at_utc
                  AND a.current_user_sid = $current_user_sid
                  AND a.session_binding = $session_binding
                ORDER BY l.lease_id COLLATE BINARY
                LIMIT 2;
                """;
            Add(command, "$plan_id", plan.Id);
            Add(command, "$schedule_revision", schedule.ScheduleRevision);
            Add(command, "$schedule_digest", schedule.ScheduleDigest);
            Add(command, "$scheduled_start_utc", UtcTicksInput(candidate.ScheduledStartUtc));
            Add(command, "$planned_end_utc", UtcTicksInput(candidate.PlannedEndUtc));
            Add(command, "$observed_at_utc", UtcTicksInput(observedAtUtc));
            Add(command, "$current_user_sid", currentUserSid);
            Add(command, "$session_binding", sessionBinding);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                leaseIds.Add(ReadRequiredText(reader, 0));
        }

        if (leaseIds.Count == 0)
            return null;
        if (leaseIds.Count != 1)
            throw InvalidPersistedShape("A scheduled or due occurrence has more than one matching recurring lease.");

        var parents = LoadAndValidateParents(
            connection,
            transaction,
            plan,
            schedule,
            candidate,
            leaseIds[0],
            observedAtUtc,
            currentUserSid,
            sessionBinding);
        return new RecurringOccurrenceNaturalWakeCandidate(
            parents.Lease.LeaseId,
            candidate,
            occurrence.StatusCode);
    }

    private static RecurringOccurrenceNaturalWakeCandidate? ResolveBoundSpecificationCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        RecurringOccurrenceSlotSnapshot slot,
        PlanOccurrence occurrence,
        PeriodicOccurrenceDueCandidate candidate,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding)
    {
        try
        {
            var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection,
                transaction,
                candidate.OccurrenceIdentity,
                occurrence.Id);
            if (specification is null)
            {
                throw InvalidPersistedShape(
                    "An authorized or run-created occurrence is missing its exact execution specification.");
            }

            var parents = LoadAndValidateParents(
                connection,
                transaction,
                plan,
                schedule,
                candidate,
                specification.LeaseId,
                observedAtUtc,
                currentUserSid,
                sessionBinding,
                requireCurrentLeaseValidity: false);
            var occurrenceCandidate = CreateOccurrenceCandidate(slot, schedule);
            SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
                specification,
                plan,
                schedule,
                parents.Binding,
                parents.Profile,
                parents.Lease,
                parents.Approval,
                occurrenceCandidate,
                occurrence);

            if (occurrence.Status == PlanOccurrenceStatus.Authorized)
            {
                if (occurrence.RunId is not null || HasAnyClaim(connection, transaction, occurrence.Id, candidate.OccurrenceIdentity))
                    throw InvalidPersistedShape("An authorized occurrence has an unexpected run or use child.");
            }
            else
            {
                if (occurrence.RunId is null)
                    throw InvalidPersistedShape("A run-created occurrence has no attached run.");
                var claimShape = ClassifyReservedClaim(
                    connection,
                    transaction,
                    occurrence,
                    candidate,
                    parents.Lease);
                if (claimShape == ReservedClaimShape.LegalPostStart)
                {
                    throw InvalidPersistedShape(
                        "A legal post-start recurring chain crossed the natural-wake query boundary.");
                }
            }

            return new RecurringOccurrenceNaturalWakeCandidate(
                parents.Lease.LeaseId,
                candidate,
                occurrence.StatusCode);
        }
        catch (Phase3PersistenceException exception)
        {
            if (exception.Code == RecurringPersistenceReasonCodes.PersistedDataInvalid)
                throw;
            throw InvalidPersistedShape(
                "The bound recurring specification or one of its parents is invalid persisted data.",
                exception);
        }
    }

    private static ParentChain LoadAndValidateParents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        PeriodicOccurrenceDueCandidate candidate,
        string leaseId,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding,
        bool requireCurrentLeaseValidity = true)
    {
        var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
            ?? throw InvalidPersistedShape("The natural-wake candidate lease is missing.");
        var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(connection, transaction, plan.Id)
            ?? throw InvalidPersistedShape("The natural-wake candidate profile binding is missing.");
        var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection,
            transaction,
            binding.ProfileRef);
        var approval = ReadExactApproval(connection, transaction, lease.LeaseId)
            ?? throw InvalidPersistedShape("The natural-wake candidate local approval is missing.");

        if (!string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            lease.ConfigurationRef.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(lease.ConfigurationRef.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(lease.ConfigurationRef.TimeZoneRulesDigest, schedule.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            !binding.ProfileRef.Equals(lease.ConfigurationRef.ProfileRef) ||
            !string.Equals(binding.PlanId, plan.Id, StringComparison.Ordinal) ||
            requireCurrentLeaseValidity &&
            (lease.Status != ConsentLeaseStatus.Active ||
             lease.ValidFromUtc > candidate.ScheduledStartUtc ||
             lease.ValidUntilUtc <= candidate.PlannedEndUtc ||
             observedAtUtc >= lease.ValidUntilUtc) ||
            !string.Equals(approval.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(approval.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(approval.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(approval.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) ||
            !string.Equals(approval.CurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
            !string.Equals(approval.SessionBinding, sessionBinding, StringComparison.Ordinal))
        {
            throw InvalidPersistedShape("The natural-wake candidate is not bound to the current recurring lease parents.");
        }

        return new ParentChain(lease, binding, profile, approval);
    }

    private static ReservedClaimShape ClassifyReservedClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        PeriodicOccurrenceDueCandidate candidate,
        RecurringConsentLease lease)
    {
        var runs = ReadRunsByOccurrence(connection, transaction, occurrence.Id);
        if (runs.Count != 1)
            throw InvalidPersistedShape("A run-created occurrence must have exactly one run.");
        var run = runs[0];
        if (!string.Equals(run.Id, occurrence.RunId, StringComparison.Ordinal))
        {
            throw InvalidPersistedShape("The run-created occurrence does not have a reserved pre-start run.");
        }

        var uses = ReadUsesByOccurrence(connection, transaction, candidate.OccurrenceIdentity, occurrence.Id);
        if (uses.Count != 1)
            throw InvalidPersistedShape("A run-created occurrence must have exactly one lease use.");
        var use = uses[0];
        if (!string.Equals(use.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal) ||
            use.ReservedUseCount != 1 ||
            use.ReservedDuration != lease.PerRunDuration ||
            use.ActualSettledDuration is not null)
        {
            throw InvalidPersistedShape("The run-created occurrence does not have one exact reserved lease use.");
        }

        if (RecurringReplayIntegrityPolicy.IsInitialReservationChain(lease, occurrence, run, use))
            return ReservedClaimShape.PreStart;

        if (RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(lease, occurrence, run, use))
            return ReservedClaimShape.LegalPostStart;

        throw InvalidPersistedShape(
            "The run-created occurrence has a malformed run/use status combination.");
    }

    private static bool HasAnyClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId,
        string occurrenceIdentity) =>
        CountRows(connection, transaction, "SELECT COUNT(*) FROM recording_runs WHERE occurrence_id = $occurrence_id;", null, occurrenceId) != 0 ||
        CountRows(connection, transaction, "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_identity = $identity OR occurrence_id = $occurrence_id;", occurrenceIdentity, occurrenceId) != 0;

    private static IReadOnlyList<RecordingRun> ReadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;";
        Add(command, "$occurrence_id", occurrenceId);
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
            result.Add(ReadRecordingRunSnapshot(reader));
        return result;
    }

    private static IReadOnlyList<LeaseUse> ReadUsesByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE occurrence_identity = $identity OR occurrence_id = $occurrence_id;";
        Add(command, "$identity", occurrenceIdentity);
        Add(command, "$occurrence_id", occurrenceId);
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
        {
            var actualTicks = reader.IsDBNull(9) ? (long?)null : ReadInt64(reader, 9);
            result.Add(LeaseUse.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadRequiredText(reader, 4),
                ReadRequiredText(reader, 5),
                ReadUtcDateTimeOffset(reader, 10),
                ParseStatus(reader, 6, Phase3StateCodes.ParseLeaseUse),
                ReadInt32(reader, 7),
                ReadRecurringDuration(ReadInt64(reader, 8)),
                actualTicks is null ? null : ReadRecurringDuration(actualTicks.Value),
                ReadUtcDateTimeOffset(reader, 11),
                ReadInt64(reader, 12)));
        }

        return result;
    }

    private static RecurringLeaseLocalApprovalEvidence? ReadExactApproval(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, lease_id, plan_id, configuration_digest,
                   authorization_digest, current_user_sid, session_binding,
                   approved_at_utc, approval_kind_code, approval_version,
                   approval_digest
            FROM recurring_lease_local_approvals
            WHERE lease_id = $lease_id;
            """;
        Add(command, "$lease_id", leaseId);
        using var reader = command.ExecuteReader();
        RecurringLeaseLocalApprovalEvidence? result = null;
        while (reader.Read())
        {
            if (result is not null)
                throw InvalidPersistedShape("More than one recurring local approval exists for the lease.");

            var approvalVersion = ReadInt64(reader, 9);
            if (approvalVersion is < int.MinValue or > int.MaxValue)
                throw InvalidPersistedShape("The recurring local approval version is invalid.");

            result = RecurringLeaseLocalApprovalEvidence.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadRequiredText(reader, 2),
                ReadRequiredText(reader, 3),
                ReadRequiredText(reader, 4),
                ReadRequiredText(reader, 5),
                ReadRequiredText(reader, 6),
                ReadUtcDateTimeOffset(reader, 7),
                ReadRequiredText(reader, 8),
                (int)approvalVersion,
                ReadRequiredText(reader, 10));
        }

        return result;
    }

    private static RecurringOccurrenceCandidate CreateOccurrenceCandidate(
        RecurringOccurrenceSlotSnapshot slot,
        RecurringScheduleVersionSnapshot schedule)
    {
        var identity = RecurringOccurrenceIdentity.Create(
            slot.PlanId,
            slot.ScheduleRevision,
            schedule.Schedule,
            slot.LocalDate,
            slot.LocalWallClockTime);
        return new RecurringOccurrenceCandidate(
            slot.PlanId,
            slot.ScheduleRevision,
            schedule.Schedule,
            slot.LocalDate,
            slot.LocalWallClockTime,
            identity,
            slot.ScheduledStartUtc,
            slot.LatestStartUtc,
            slot.PlannedEndUtc,
            slot.TerminalReasonCode ?? "",
            slot.ResolutionCode);
    }

    private static long CountRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string? identity,
        string? occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$identity", identity);
        Add(command, "$occurrence_id", occurrenceId);
        var value = command.ExecuteScalar();
        return value is long count
            ? count
            : throw InvalidPersistedShape("A persisted child cardinality was not an Int64 count.");
    }

    private static TimeSpan ReadRecurringDuration(long ticks)
    {
        if (ticks < 0 || ticks > TimeSpan.MaxValue.Ticks)
            throw InvalidPersistedShape("A recurring lease-use duration is outside the TimeSpan range.");
        return TimeSpan.FromTicks(ticks);
    }

    private static void ValidateCanonicalIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new Phase3PersistenceException("invalid_identity", $"The {name} must be a canonical non-empty value.");
        }
    }

    private static Phase3PersistenceException InvalidPersistedShape(string message, Exception? inner = null) =>
        new(RecurringPersistenceReasonCodes.PersistedDataInvalid, message, inner);

    private sealed record BaseRow(
        string OccurrenceIdentity,
        string PlanId,
        long ScheduleRevision,
        string ScheduleDigest,
        DateTimeOffset ScheduledStartUtc,
        DateTimeOffset LatestStartUtc,
        DateTimeOffset PlannedEndUtc,
        string OccurrenceId,
        string StatusCode,
        long OccurrenceVersion,
        string? RunId);

    private sealed record ParentChain(
        RecurringConsentLease Lease,
        RecurringPlanProfileBinding Binding,
        RecurringFixedRegionProfileVersion Profile,
        RecurringLeaseLocalApprovalEvidence Approval);

    private enum ReservedClaimShape
    {
        PreStart,
        LegalPostStart,
    }
}
