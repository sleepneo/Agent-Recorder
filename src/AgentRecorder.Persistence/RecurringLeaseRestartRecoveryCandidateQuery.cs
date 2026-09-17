using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// A bounded, read-only projection of recurring chains which crossed the
/// durable start gate and still need the restart boundary to reconcile them.
/// The projection contains identity and observed versions only; it never
/// carries proof, specification payload, output paths, or backend data.
/// </summary>
internal sealed class RecurringLeaseRestartRecoveryCandidateQuery : SqliteRepositoryBase
{
    internal RecurringLeaseRestartRecoveryCandidateQuery(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal RecurringLeaseRestartRecoveryCandidatePage ListCandidates(
        string? currentUserSid,
        string? sessionBinding,
        int limit)
    {
        if (limit is < 1 or > PeriodicOccurrenceDueProjectionLimits.MaxCandidateLimit)
        {
            throw new Phase3PersistenceException(
                "recovery_invalid_limit",
                $"The recurring recovery candidate limit must be between 1 and {PeriodicOccurrenceDueProjectionLimits.MaxCandidateLimit}.");
        }

        if (!IsCanonicalIdentity(currentUserSid) || !IsCanonicalIdentity(sessionBinding))
        {
            throw new Phase3PersistenceException(
                "recovery_identity_invalid",
                "The recurring recovery SID and session binding are invalid.");
        }

        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.id,
                   l.lease_id,
                   s.occurrence_identity,
                   o.id,
                   r.id,
                   u.use_id,
                   r.status_code,
                   u.status_code,
                   o.status_code,
                   r.version,
                   u.version,
                   o.version,
                   l.version,
                   p.version
            FROM recurring_occurrence_execution_specs AS x
            JOIN recurring_occurrence_slots AS s
              ON s.occurrence_identity = x.occurrence_identity
             AND s.plan_id = x.plan_id
             AND s.occurrence_id = x.occurrence_id
             AND s.slot_status_code = 'scheduled'
            JOIN plans AS p
              ON p.id = x.plan_id
             AND p.is_one_time = 0
            JOIN plan_occurrences AS o
              ON o.id = x.occurrence_id
             AND o.plan_id = x.plan_id
             AND o.run_id IS NOT NULL
            JOIN recurring_consent_leases AS l
              ON l.lease_id = x.lease_id
             AND l.plan_id = x.plan_id
             AND l.configuration_digest = x.configuration_digest
             AND l.authorization_digest = x.lease_authorization_digest
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
            WHERE x.specification_version = 1
              AND x.approved_current_user_sid = $current_user_sid
              AND x.approved_session_binding = $session_binding
              AND a.current_user_sid = $current_user_sid
              AND a.session_binding = $session_binding
              AND (
                    (r.status_code = 'start_committed'
                     AND u.status_code = 'start_committed'
                     AND o.status_code = 'run_created')
                 OR (r.status_code = 'recording'
                     AND u.status_code = 'consumed'
                     AND o.status_code = 'run_created')
                 OR (r.status_code = 'finalizing'
                     AND u.status_code = 'consumed'
                     AND o.status_code = 'run_created')
                 OR (r.status_code = 'media_ready'
                     AND u.status_code = 'consumed'
                     AND o.status_code = 'run_created')
              )
            ORDER BY s.scheduled_start_utc ASC,
                     p.id COLLATE BINARY ASC,
                     x.occurrence_identity COLLATE BINARY ASC,
                     o.id COLLATE BINARY ASC,
                     r.id COLLATE BINARY ASC,
                     u.use_id COLLATE BINARY ASC
            LIMIT $fetch_limit;
            """;
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        Add(command, "$fetch_limit", checked(limit + 1));

        var rows = new List<RecurringLeaseRestartRecoveryCandidate>(limit + 1);
        var identities = new HashSet<RecurringLeaseRestartRecoveryCandidateIdentity>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var candidate = new RecurringLeaseRestartRecoveryCandidate(
                    ReadRequiredText(reader, 0),
                    ReadRequiredText(reader, 1),
                    ReadRequiredText(reader, 2),
                    ReadRequiredText(reader, 3),
                    ReadRequiredText(reader, 4),
                    ReadRequiredText(reader, 5),
                    ReadRequiredText(reader, 6),
                    ReadRequiredText(reader, 7),
                    ReadRequiredText(reader, 8),
                    ReadInt64(reader, 9),
                    ReadInt64(reader, 10),
                    ReadInt64(reader, 11),
                    ReadInt64(reader, 12),
                    ReadInt64(reader, 13));
                if (!identities.Add(candidate.Identity))
                {
                    throw new Phase3PersistenceException(
                        "recovery_snapshot_invalid",
                        "The recurring recovery candidate query returned a duplicate durable chain.");
                }

                rows.Add(candidate);
            }
        }

        transaction.Commit();
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        return new RecurringLeaseRestartRecoveryCandidatePage(rows, hasMore);
    }

    private static bool IsCanonicalIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}

internal sealed record RecurringLeaseRestartRecoveryCandidate(
    string PlanId,
    string LeaseId,
    string OccurrenceIdentity,
    string OccurrenceId,
    string RunId,
    string UseId,
    string RunStatusCode,
    string UseStatusCode,
    string OccurrenceStatusCode,
    long RunVersion,
    long UseVersion,
    long OccurrenceVersion,
    long LeaseVersion,
    long PlanVersion)
{
    internal RecurringLeaseRestartRecoveryCandidateIdentity Identity =>
        new(PlanId, LeaseId, OccurrenceIdentity, OccurrenceId, RunId, UseId);
}

internal readonly record struct RecurringLeaseRestartRecoveryCandidateIdentity(
    string PlanId,
    string LeaseId,
    string OccurrenceIdentity,
    string OccurrenceId,
    string RunId,
    string UseId);

internal sealed record RecurringLeaseRestartRecoveryCandidatePage(
    IReadOnlyList<RecurringLeaseRestartRecoveryCandidate> Candidates,
    bool HasMore);
