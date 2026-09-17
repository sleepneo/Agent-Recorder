using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Short read-only queries used by the in-process standing runtime. Callers
/// receive only intent/recovery identity; all capture details are reloaded by
/// the existing intent-bound handoff and start gate.
/// </summary>
internal sealed class StandingLeaseNaturalWakeCandidateQuery : SqliteRepositoryBase
{
    internal StandingLeaseNaturalWakeCandidateQuery(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal IReadOnlyList<StandingLeaseNaturalWakeCandidate> ListCandidates(
        string? currentUserSid,
        string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(currentUserSid) || string.IsNullOrWhiteSpace(sessionBinding))
            return Array.Empty<StandingLeaseNaturalWakeCandidate>();

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.intent_id, p.id, o.id, l.id,
                   o.window_start_utc, o.window_end_utc,
                   i.latest_start_utc, i.planned_end_utc,
                   o.version, l.version, p.version
            FROM setup_intents i
            JOIN plans p
              ON p.id = i.plan_id
            JOIN plan_occurrences o
              ON o.id = i.occurrence_id
             AND o.plan_id = i.plan_id
            JOIN consent_leases l
              ON l.id = i.lease_id
             AND l.occurrence_id = i.occurrence_id
            JOIN authorized_capture_scopes s
              ON s.scope_id = i.scope_id
             AND s.plan_id = i.plan_id
             AND s.occurrence_id = i.occurrence_id
             AND s.lease_id = i.lease_id
            WHERE i.status_code = 'activated'
              AND i.current_user_sid = $current_user_sid
              AND i.session_binding = $session_binding
              AND s.current_user_sid = $current_user_sid
              AND s.session_binding = $session_binding
              AND p.status_code = 'enabled'
              AND o.status_code = 'authorized'
              AND l.status_code = 'active'
              AND o.run_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM recording_runs r WHERE r.occurrence_id = o.id)
              AND NOT EXISTS (SELECT 1 FROM lease_uses u WHERE u.occurrence_id = o.id)
            ORDER BY o.window_start_utc, i.intent_id;
            """;
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);

        var candidates = new List<StandingLeaseNaturalWakeCandidate>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(new StandingLeaseNaturalWakeCandidate(
                    ReadRequiredText(reader, 0),
                    ReadRequiredText(reader, 1),
                    ReadRequiredText(reader, 2),
                    ReadRequiredText(reader, 3),
                    ReadUtcDateTimeOffset(reader, 4),
                    ReadUtcDateTimeOffset(reader, 5),
                    ReadNullableUtcDateTimeOffset(reader, 6) ?? ReadUtcDateTimeOffset(reader, 5),
                    ReadNullableUtcDateTimeOffset(reader, 7) ?? ReadUtcDateTimeOffset(reader, 5),
                    ReadInt64(reader, 8),
                    ReadInt64(reader, 9),
                    ReadInt64(reader, 10)));
            }
        }
        transaction.Commit();
        return candidates;
    }

    internal IReadOnlyList<StandingLeaseRecoveryCandidate> ListRecoveryCandidates(
        string? currentUserSid,
        string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(currentUserSid) || string.IsNullOrWhiteSpace(sessionBinding))
            return Array.Empty<StandingLeaseRecoveryCandidate>();

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.intent_id, p.id, o.id, l.id, r.id, u.id,
                   s.scope_id, s.scope_digest,
                   r.status_code, u.status_code, o.status_code,
                   r.version, u.version, o.version, l.version, p.version
            FROM setup_intents i
            JOIN plans p
              ON p.id = i.plan_id
            JOIN plan_occurrences o
              ON o.id = i.occurrence_id
             AND o.plan_id = i.plan_id
            JOIN consent_leases l
              ON l.id = i.lease_id
             AND l.occurrence_id = i.occurrence_id
            JOIN authorized_capture_scopes s
              ON s.scope_id = i.scope_id
             AND s.plan_id = i.plan_id
             AND s.occurrence_id = i.occurrence_id
             AND s.lease_id = i.lease_id
            JOIN recording_runs r
              ON r.occurrence_id = o.id
             AND r.id = o.run_id
            JOIN lease_uses u
              ON u.occurrence_id = o.id
             AND u.run_id = r.id
             AND u.lease_id = l.id
            WHERE i.status_code = 'activated'
              AND i.current_user_sid = $current_user_sid
              AND i.session_binding = $session_binding
              AND s.current_user_sid = $current_user_sid
              AND s.session_binding = $session_binding
              AND (
                    r.status_code IN ('start_committed', 'started_unknown', 'recording', 'finalizing', 'media_ready')
                 OR u.status_code IN ('start_committed', 'started_unknown', 'consumed')
              )
            ORDER BY r.created_at_utc, r.id;
            """;
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);

        var candidates = new List<StandingLeaseRecoveryCandidate>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(new StandingLeaseRecoveryCandidate(
                    ReadRequiredText(reader, 0),
                    ReadRequiredText(reader, 1),
                    ReadRequiredText(reader, 2),
                    ReadRequiredText(reader, 3),
                    ReadRequiredText(reader, 4),
                    ReadRequiredText(reader, 5),
                    ReadRequiredText(reader, 6),
                    ReadRequiredText(reader, 7),
                    ReadRequiredText(reader, 8),
                    ReadRequiredText(reader, 9),
                    ReadRequiredText(reader, 10),
                    ReadInt64(reader, 11),
                    ReadInt64(reader, 12),
                    ReadInt64(reader, 13),
                    ReadInt64(reader, 14),
                    ReadInt64(reader, 15)));
            }
        }
        transaction.Commit();
        return candidates;
    }
}

internal sealed record StandingLeaseNaturalWakeCandidate(
    string IntentId,
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    long OccurrenceVersion,
    long LeaseVersion,
    long PlanVersion);

internal sealed record StandingLeaseRecoveryCandidate(
    string IntentId,
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string RunId,
    string LeaseUseId,
    string ScopeId,
    string ScopeDigest,
    string RunStatusCode,
    string LeaseUseStatusCode,
    string OccurrenceStatusCode,
    long RunVersion,
    long LeaseUseVersion,
    long OccurrenceVersion,
    long LeaseVersion,
    long PlanVersion);
