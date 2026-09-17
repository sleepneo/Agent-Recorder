using System.Collections.Generic;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record StandingPlanSetupRecord(
    string IntentId,
    string CurrentUserSid,
    string SessionBinding,
    string StatusCode,
    DateTimeOffset ExpiresAtUtc,
    string? PlanId,
    string? OccurrenceId,
    string? LeaseId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version,
    string? TerminalReasonCode,
    DateTimeOffset? ScheduledStartUtc,
    DateTimeOffset? LatestStartUtc,
    DateTimeOffset? PlannedEndUtc,
    TimeSpan? MaximumDuration,
    DateTimeOffset? LeaseValidUntilUtc,
    string? OutputDirectory,
    string? FrozenFileName);

internal sealed record StandingPlanPreparedRecord(
    string IntentId,
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string ScopeId,
    string ScopeDigest,
    string CurrentUserSid,
    string SessionBinding,
    string StableDisplayFingerprint,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    int DpiX,
    int DpiY,
    int PhysicalWidth,
    int PhysicalHeight,
    AuthorizedDisplayOrientation Orientation,
    string OutputDirectory,
    string FrozenFileName);

internal sealed record StandingPlanSetupExecutionRecord(
    string? OccurrenceStatusCode,
    string? OccurrenceTerminalReasonCode,
    long? OccurrenceVersion,
    string? RunId,
    string? RunStatusCode,
    string? RunTerminalReasonCode,
    DateTimeOffset? RunCreatedAtUtc,
    long? RunVersion,
    DateTimeOffset? RunUpdatedAtUtc,
    string? LeaseStatusCode,
    long? LeaseVersion,
    long? UseVersion,
    string? PlanStatusCode,
    long? PlanVersion);

/// <summary>
/// Read-only projection source for the HTTP status endpoint.  The API never
/// receives the SID/session binding; the local host supplies it here.
/// </summary>
internal sealed class StandingPlanSetupQueryService : SqliteRepositoryBase
{
    internal StandingPlanSetupQueryService(SqliteOperationalStore store) : base(store)
    {
    }

    internal IReadOnlyList<string> ListRecoverable(string? currentUserSid, string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(currentUserSid) || string.IsNullOrWhiteSpace(sessionBinding))
            return Array.Empty<string>();

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id
            FROM setup_intents
            WHERE current_user_sid = $current_user_sid
              AND session_binding = $session_binding
              AND status_code IN ('region_selection_pending', 'lease_approval_pending')
            ORDER BY created_at_utc, intent_id;
            """;
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        var intentIds = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                intentIds.Add(ReadRequiredText(reader, 0));
        }
        transaction.Commit();
        return intentIds;
    }

    internal StandingPlanSetupRecord? Get(string? intentId, string? currentUserSid, string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(currentUserSid) ||
            string.IsNullOrWhiteSpace(sessionBinding))
            return null;

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, current_user_sid, session_binding, status_code,
                   expires_at_utc, plan_id, occurrence_id, lease_id,
                   created_at_utc, updated_at_utc, version, terminal_reason_code,
                   scheduled_start_utc, latest_start_utc, planned_end_utc,
                   maximum_duration_ms, lease_valid_until_utc,
                   output_directory, frozen_file_name
            FROM setup_intents
            WHERE intent_id = $intent_id
              AND current_user_sid = $current_user_sid
              AND session_binding = $session_binding
            LIMIT 1;
            """;
        Add(command, "$intent_id", intentId);
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        StandingPlanSetupRecord? result = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                result = new StandingPlanSetupRecord(
                    ReadRequiredText(reader, 0),
                    ReadRequiredText(reader, 1),
                    ReadRequiredText(reader, 2),
                    ReadRequiredText(reader, 3),
                    ReadUtcDateTimeOffset(reader, 4),
                    ReadNullableText(reader, 5),
                    ReadNullableText(reader, 6),
                    ReadNullableText(reader, 7),
                    ReadUtcDateTimeOffset(reader, 8),
                    ReadUtcDateTimeOffset(reader, 9),
                    ReadInt64(reader, 10),
                    ReadNullableText(reader, 11),
                    ReadNullableUtcDateTimeOffset(reader, 12),
                    ReadNullableUtcDateTimeOffset(reader, 13),
                    ReadNullableUtcDateTimeOffset(reader, 14),
                    ReadNullableDurationMilliseconds(reader, 15),
                    ReadNullableUtcDateTimeOffset(reader, 16),
                    ReadNullableText(reader, 17),
                    ReadNullableText(reader, 18));
            }
        }
        transaction.Commit();
        return result;
    }

    internal StandingPlanPreparedRecord? GetPrepared(string? intentId, string? currentUserSid, string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(currentUserSid) ||
            string.IsNullOrWhiteSpace(sessionBinding))
            return null;

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.intent_id, s.plan_id, s.occurrence_id, s.lease_id,
                   s.scope_id, s.scope_digest, s.current_user_sid,
                   s.session_binding, s.stable_display_fingerprint,
                   s.display_bounds_x, s.display_bounds_y,
                   s.display_bounds_width, s.display_bounds_height,
                   s.region_x, s.region_y, s.region_width, s.region_height,
                   s.dpi_x, s.dpi_y, s.physical_width, s.physical_height,
                   s.orientation_code, s.output_directory, s.frozen_file_name
            FROM setup_intents i
            JOIN authorized_capture_scopes s
              ON s.plan_id = i.plan_id
             AND s.occurrence_id = i.occurrence_id
             AND s.lease_id = i.lease_id
            WHERE i.intent_id = $intent_id
              AND i.current_user_sid = $current_user_sid
              AND i.session_binding = $session_binding
            LIMIT 1;
            """;
        Add(command, "$intent_id", intentId);
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        StandingPlanPreparedRecord? result = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                result = new StandingPlanPreparedRecord(
                    ReadRequiredText(reader, 0),
                    ReadRequiredText(reader, 1),
                    ReadRequiredText(reader, 2),
                    ReadRequiredText(reader, 3),
                    ReadRequiredText(reader, 4),
                    ReadRequiredText(reader, 5),
                    ReadRequiredText(reader, 6),
                    ReadRequiredText(reader, 7),
                    ReadRequiredText(reader, 8),
                    new AuthorizedPhysicalRectangle(ReadInt32(reader, 9), ReadInt32(reader, 10), ReadInt32(reader, 11), ReadInt32(reader, 12)),
                    new AuthorizedPhysicalRectangle(ReadInt32(reader, 13), ReadInt32(reader, 14), ReadInt32(reader, 15), ReadInt32(reader, 16)),
                    ReadInt32(reader, 17),
                    ReadInt32(reader, 18),
                    ReadInt32(reader, 19),
                    ReadInt32(reader, 20),
                    AuthorizedFixedRegionScopeCodes.ParseOrientation(ReadRequiredText(reader, 21)),
                    ReadRequiredText(reader, 22),
                    ReadRequiredText(reader, 23));
            }
        }
        transaction.Commit();
        return result;
    }

    internal StandingPlanSetupExecutionRecord? GetExecution(
        string? intentId,
        string? currentUserSid,
        string? sessionBinding)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(currentUserSid) ||
            string.IsNullOrWhiteSpace(sessionBinding))
            return null;

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.status_code, o.terminal_reason_code, o.version, o.run_id,
                   r.status_code, r.terminal_reason_code, r.created_at_utc, r.version, r.updated_at_utc,
                   l.status_code, l.version,
                   u.version,
                   p.status_code, p.version
            FROM setup_intents i
            LEFT JOIN plans p
              ON p.id = i.plan_id
            LEFT JOIN plan_occurrences o
              ON o.id = i.occurrence_id
             AND o.plan_id = i.plan_id
            LEFT JOIN consent_leases l
              ON l.id = i.lease_id
             AND l.occurrence_id = i.occurrence_id
            LEFT JOIN recording_runs r
              ON r.id = o.run_id
             AND r.occurrence_id = o.id
            LEFT JOIN lease_uses u
              ON u.run_id = r.id
             AND u.occurrence_id = o.id
             AND u.lease_id = l.id
            WHERE i.intent_id = $intent_id
              AND i.current_user_sid = $current_user_sid
              AND i.session_binding = $session_binding
            LIMIT 1;
            """;
        Add(command, "$intent_id", intentId);
        Add(command, "$current_user_sid", currentUserSid);
        Add(command, "$session_binding", sessionBinding);
        StandingPlanSetupExecutionRecord? result = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                result = new StandingPlanSetupExecutionRecord(
                    ReadNullableText(reader, 0),
                    ReadNullableText(reader, 1),
                    ReadNullableInt64(reader, 2),
                    ReadNullableText(reader, 3),
                    ReadNullableText(reader, 4),
                    ReadNullableText(reader, 5),
                    ReadNullableUtcDateTimeOffset(reader, 6),
                    ReadNullableInt64(reader, 7),
                    ReadNullableUtcDateTimeOffset(reader, 8),
                    ReadNullableText(reader, 9),
                    ReadNullableInt64(reader, 10),
                    ReadNullableInt64(reader, 11),
                    ReadNullableText(reader, 12),
                    ReadNullableInt64(reader, 13));
            }
        }
        transaction.Commit();
        return result;
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadInt64(reader, ordinal);
}

/// <summary>
/// Idempotent terminal settlement for a local setup UI that was cancelled,
/// timed out, or rejected.  It also closes a prepared-but-not-approved chain;
/// no active lease can be changed by this adapter.
/// </summary>
internal sealed class StandingPlanSetupTerminalService : SqliteRepositoryBase
{
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeSetupCompareAndSwapForTest;

    internal StandingPlanSetupTerminalService(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeSetupCompareAndSwapForTest = null)
        : base(store)
    {
        _beforeSetupCompareAndSwapForTest = beforeSetupCompareAndSwapForTest;
    }

    internal bool TrySetTerminal(string? intentId, string reasonCode, bool expired = false)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(reasonCode) ||
            reasonCode.Any(char.IsControl) || reasonCode.Contains('/') || reasonCode.Contains('\\'))
            return false;

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        string? status = null;
        string? planId = null;
        string? occurrenceId = null;
        string? leaseId = null;
        long version = 0;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT status_code, plan_id, occurrence_id, lease_id, version
                FROM setup_intents
                WHERE intent_id = $intent_id
                LIMIT 1;
                """;
            Add(read, "$intent_id", intentId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return false;
            }
            status = ReadRequiredText(reader, 0);
            planId = ReadNullableText(reader, 1);
            occurrenceId = ReadNullableText(reader, 2);
            leaseId = ReadNullableText(reader, 3);
            version = ReadInt64(reader, 4);
        }

        if (status is "rejected" or "expired")
        {
            transaction.Commit();
            return true;
        }

        // Activated is outside the setup cancellation boundary.  Safety
        // controls, not a UI race, own an already-active authorization.
        if (status == "activated")
        {
            transaction.Commit();
            return false;
        }

        var terminalStatus = expired ? "expired" : "rejected";
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE setup_intents
                SET status_code = $status_code,
                    terminal_reason_code = $reason_code,
                    updated_at_utc = $updated_at_utc,
                    version = version + 1
                WHERE intent_id = $intent_id
                  AND version = $expected_version
                  AND status_code IN ('region_selection_pending', 'lease_approval_pending');
                """;
            Add(update, "$status_code", terminalStatus);
            Add(update, "$reason_code", reasonCode);
            Add(update, "$updated_at_utc", DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            Add(update, "$intent_id", intentId);
            Add(update, "$expected_version", version);
            _beforeSetupCompareAndSwapForTest?.Invoke(connection, transaction);
            if (update.ExecuteNonQuery() != 1)
            {
                // The compare-and-swap lost. Do not touch any aggregate in
                // this transaction; the opponent's state is authoritative.
                transaction.Rollback();
                return ReadTerminalOutcomeAfterLostCompareAndSwap(intentId);
            }
        }

        if (leaseId is not null)
        {
            using var lease = connection.CreateCommand();
            lease.Transaction = transaction;
            lease.CommandText = """
                UPDATE consent_leases
                SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = version + 1
                WHERE id = $lease_id AND status_code = 'pending';
                """;
            Add(lease, "$status_code", expired ? "expired" : "rejected");
            Add(lease, "$updated_at_utc", DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            Add(lease, "$lease_id", leaseId);
            if (lease.ExecuteNonQuery() != 1)
                throw new Phase3PersistenceException("setup_terminal_child_update_failed", "The pending lease terminal update did not affect exactly one row.");
        }

        if (occurrenceId is not null)
        {
            using var occurrence = connection.CreateCommand();
            occurrence.Transaction = transaction;
            occurrence.CommandText = """
                UPDATE plan_occurrences
                SET status_code = $status_code,
                    terminal_reason_code = $reason_code,
                    updated_at_utc = $updated_at_utc,
                    version = version + 1
                WHERE id = $occurrence_id
                  AND status_code IN ('scheduled', 'due', 'rechecking', 'pending_lease_approval');
                """;
            Add(occurrence, "$status_code", expired ? "expired" : "cancelled");
            Add(occurrence, "$reason_code", reasonCode);
            Add(occurrence, "$updated_at_utc", DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            Add(occurrence, "$occurrence_id", occurrenceId);
            if (occurrence.ExecuteNonQuery() != 1)
                throw new Phase3PersistenceException("setup_terminal_child_update_failed", "The pending occurrence terminal update did not affect exactly one row.");
        }

        if (planId is not null)
        {
            using var plan = connection.CreateCommand();
            plan.Transaction = transaction;
            plan.CommandText = """
                UPDATE plans
                SET status_code = 'cancelled', updated_at_utc = $updated_at_utc, version = version + 1
                WHERE id = $plan_id AND status_code IN ('draft', 'enabled', 'paused');
                """;
            Add(plan, "$updated_at_utc", DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            Add(plan, "$plan_id", planId);
            if (plan.ExecuteNonQuery() != 1)
                throw new Phase3PersistenceException("setup_terminal_child_update_failed", "The pending plan terminal update did not affect exactly one row.");
        }

        transaction.Commit();
        return true;
    }

    private bool ReadTerminalOutcomeAfterLostCompareAndSwap(string intentId)
    {
        using var connection = OpenBusinessConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_code FROM setup_intents WHERE intent_id = $intent_id LIMIT 1;";
        Add(command, "$intent_id", intentId);
        var status = Convert.ToString(command.ExecuteScalar());
        return status is "rejected" or "expired";
    }
}
