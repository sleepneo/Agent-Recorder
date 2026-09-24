using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record RecurringAdvancementCandidate(
    string SetupIntentId,
    string PlanId,
    string LeaseId,
    string CurrentUserSid,
    string SessionBinding,
    string ConfigurationDigest,
    string ProfileId,
    long ProfileVersion,
    string ProfileDigest,
    long ScheduleRevision,
    long ExpectedCursorVersion,
    DateTimeOffset InitialAfterUtc,
    string? PreviousResultCode,
    string? PreviousOccurrenceIdentity,
    string? PreviousOccurrenceStatusCode);

internal sealed record RecurringAdvancementCandidatePage(
    IReadOnlyList<RecurringAdvancementCandidate> Candidates,
    bool HasMore,
    DateTimeOffset? LastRequestedAtUtc,
    string? LastSetupIntentId);

/// <summary>
/// Bounded discovery of activated recurring setup chains which may advance.
/// The activated setup snapshot is the durable source of the cursor's initial
/// boundary; wall-clock time is used only to test current lease/safety state.
/// </summary>
internal sealed class RecurringAdvancementCandidateQuery : SqliteRepositoryBase
{
    internal const int MaximumPageSize = 64;

    internal RecurringAdvancementCandidateQuery(SqliteOperationalStore store) : base(store) { }

    internal RecurringAdvancementCandidatePage ListPage(
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset? afterRequestedAtUtc,
        string? afterSetupIntentId,
        int pageSize)
    {
        ValidateInputs(observedAtUtc, currentUserSid, sessionBinding, afterRequestedAtUtc, afterSetupIntentId, pageSize);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            if (!IsAdvancementSafetyEnabled(connection, transaction))
            {
                transaction.Commit();
                return new(Array.Empty<RecurringAdvancementCandidate>(), false, null, null);
            }

            var rows = ReadActivatedIntentPage(
                connection, transaction, currentUserSid, sessionBinding,
                afterRequestedAtUtc, afterSetupIntentId, pageSize + 1);
            var hasMore = rows.Count > pageSize;
            if (hasMore)
                rows.RemoveAt(rows.Count - 1);

            var candidates = new List<RecurringAdvancementCandidate>(rows.Count);
            foreach (var row in rows)
            {
                var candidate = ReadEligibleCandidate(
                    connection, transaction, row.IntentId, observedAtUtc, currentUserSid, sessionBinding);
                if (candidate is not null)
                    candidates.Add(candidate);
            }

            transaction.Commit();
            var last = rows.Count == 0 ? null : rows[^1];
            return new(candidates, hasMore, last?.RequestedAtUtc, last?.IntentId);
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
                "The recurring advancement candidate query failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_advancement_candidate_invalid",
                "The recurring advancement candidate query encountered invalid persisted data.", exception);
        }
    }

    internal RecurringAdvancementCandidate? ReadCandidate(
        string setupIntentId,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw Invalid("The recurring advancement time must be UTC.");
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            if (!IsAdvancementSafetyEnabled(connection, transaction))
            {
                transaction.Commit();
                return null;
            }
            var candidate = ReadEligibleCandidate(
                connection, transaction, RequiredInput(setupIntentId), observedAtUtc,
                ValidateIdentity(currentUserSid), ValidateIdentity(sessionBinding));
            transaction.Commit();
            return candidate;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_advancement_candidate_invalid",
                "The recurring advancement candidate could not be re-read.", exception);
        }
    }

    /// <summary>Must be called from the advancement write transaction immediately before writes.</summary>
    internal static bool IsStillEligibleWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringAdvancementCandidate candidate,
        DateTimeOffset advancedAtUtc)
    {
        if (!IsAdvancementSafetyEnabled(connection, transaction))
            return false;

        var persisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(
            connection, transaction, candidate.SetupIntentId);
        if (persisted is null || persisted.Status != RecurringSetupIntentStatus.Activated ||
            !string.Equals(persisted.CurrentUserSid, candidate.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(persisted.SessionBinding, candidate.SessionBinding, StringComparison.Ordinal))
            return false;

        var readback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
            persisted, connection, transaction);
        if (readback.Status != RecurringSetupIntentStatus.Activated ||
            readback.Snapshot.RequestedAtUtc != candidate.InitialAfterUtc)
            return false;

        var chain = SqliteRecurringSetupPreparationTransaction.ValidateActivatedChainWithinTransaction(
            connection, transaction, persisted, readback.Snapshot);
        ValidatePreparationCardinality(
            connection, transaction, chain.Preparation.IntentId,
            chain.Preparation.PlanId, chain.Preparation.LeaseId);
        if (chain.Plan.Status != PlanDefinitionStatus.Enabled ||
            chain.Lease.Status != ConsentLeaseStatus.Active ||
            advancedAtUtc < chain.Lease.ValidFromUtc || advancedAtUtc >= chain.Lease.ValidUntilUtc ||
            !string.Equals(chain.Preparation.PlanId, candidate.PlanId, StringComparison.Ordinal) ||
            !string.Equals(chain.Preparation.LeaseId, candidate.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(chain.Configuration.ConfigurationDigest, candidate.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(chain.Binding.ProfileRef.ProfileId, candidate.ProfileId, StringComparison.Ordinal) ||
            chain.Binding.ProfileRef.ProfileVersion != candidate.ProfileVersion ||
            !string.Equals(chain.Binding.ProfileRef.ProfileDigest, candidate.ProfileDigest, StringComparison.Ordinal))
            return false;

        var latest = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(
            connection, transaction, candidate.PlanId);
        if (latest is null || latest.ScheduleRevision != candidate.ScheduleRevision ||
            chain.Schedule.ScheduleRevision != candidate.ScheduleRevision ||
            chain.Configuration.ScheduleRevision != candidate.ScheduleRevision)
            return false;

        if (HasNonTerminalOccurrence(connection, transaction, candidate.PlanId))
            return false;

        return true;
    }

    private static RecurringAdvancementCandidate? ReadEligibleCandidate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string setupIntentId,
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding)
    {
        var persisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(
            connection, transaction, setupIntentId)
            ?? throw Invalid("An activated recurring setup intent disappeared from its candidate page.");
        if (persisted.Status != RecurringSetupIntentStatus.Activated)
            throw Invalid("The recurring advancement page contains a non-activated setup intent.");
        if (!string.Equals(persisted.CurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
            !string.Equals(persisted.SessionBinding, sessionBinding, StringComparison.Ordinal))
            throw Invalid("The recurring setup identity does not match the current principal and session.");

        var readback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
            persisted, connection, transaction);
        if (readback.Status != RecurringSetupIntentStatus.Activated)
            throw Invalid("The recurring setup intent did not rehydrate as activated.");

        var chain = SqliteRecurringSetupPreparationTransaction.ValidateActivatedChainWithinTransaction(
            connection, transaction, persisted, readback.Snapshot);
        ValidatePreparationCardinality(connection, transaction, chain.Preparation.IntentId,
            chain.Preparation.PlanId, chain.Preparation.LeaseId);

        if (chain.Plan.Status != PlanDefinitionStatus.Enabled ||
            chain.Lease.Status != ConsentLeaseStatus.Active ||
            observedAtUtc < chain.Lease.ValidFromUtc || observedAtUtc >= chain.Lease.ValidUntilUtc)
            return null;

        var latest = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(
            connection, transaction, chain.Plan.Id)
            ?? throw Invalid("The activated recurring setup has no latest schedule revision.");
        if (latest.ScheduleRevision != chain.Configuration.ScheduleRevision ||
            latest.ScheduleRevision != chain.Schedule.ScheduleRevision ||
            !string.Equals(latest.ScheduleDigest, chain.Configuration.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(latest.TimeZoneRulesDigest, chain.Configuration.TimeZoneRulesDigest, StringComparison.Ordinal))
            throw Invalid("The latest recurring schedule revision is not the revision bound to local authorization.");

        var scheduleRevision = latest.ScheduleRevision;
        var cursor = SqliteRecurringAdvancementTransaction.ReadValidatedCursorWithinTransaction(
            connection, transaction, chain.Plan.Id, scheduleRevision, latest);
        long expectedVersion;
        DateTimeOffset initialAfterUtc;
        string? previousResult = null;
        string? previousIdentity = null;
        string? previousOccurrenceStatus = null;
        if (cursor is null)
        {
            EnsureNoAdvancementHistory(connection, transaction, chain.Plan.Id);
            expectedVersion = 0;
            initialAfterUtc = readback.Snapshot.RequestedAtUtc;
        }
        else
        {
            if (cursor.InitialAfterUtc != readback.Snapshot.RequestedAtUtc)
                throw Invalid("The recurring cursor initial boundary differs from the immutable setup request time.");
            expectedVersion = cursor.Version;
            initialAfterUtc = cursor.InitialAfterUtc;
            if (cursor.Version > 0)
            {
                var current = ReadCurrentTransition(connection, transaction, chain.Plan.Id, scheduleRevision, cursor.Version);
                previousResult = current.ResultCode;
                previousIdentity = current.OccurrenceIdentity;
                previousOccurrenceStatus = current.OccurrenceStatusCode;
            }
        }

        if (HasNonTerminalOccurrence(connection, transaction, chain.Plan.Id) || cursor?.IsExhausted == true)
            return null;

        return new RecurringAdvancementCandidate(
            persisted.IntentId,
            chain.Plan.Id,
            chain.Lease.LeaseId,
            currentUserSid,
            sessionBinding,
            chain.Configuration.ConfigurationDigest,
            chain.Binding.ProfileRef.ProfileId,
            chain.Binding.ProfileRef.ProfileVersion,
            chain.Binding.ProfileRef.ProfileDigest,
            scheduleRevision,
            expectedVersion,
            initialAfterUtc,
            previousResult,
            previousIdentity,
            previousOccurrenceStatus);
    }

    private static List<IntentKey> ReadActivatedIntentPage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sid,
        string session,
        DateTimeOffset? afterRequestedAtUtc,
        string? afterSetupIntentId,
        int readLimit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, requested_at_utc
            FROM setup_intents
            WHERE intent_kind_code = $kind AND status_code = 'activated'
              AND current_user_sid = $sid AND session_binding = $session
              AND ($after_time IS NULL OR requested_at_utc < $after_time
                   OR (requested_at_utc = $after_time AND intent_id < $after_id))
            ORDER BY requested_at_utc DESC, intent_id DESC
            LIMIT $limit;
            """;
        Add(command, "$kind", RecurringSetupIntentCodes.IntentKind);
        Add(command, "$sid", sid);
        Add(command, "$session", session);
        Add(command, "$after_time", afterRequestedAtUtc is null ? null : UtcTicksInput(afterRequestedAtUtc.Value));
        Add(command, "$after_id", afterSetupIntentId);
        Add(command, "$limit", readLimit);
        var rows = new List<IntentKey>(readLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new(RequiredPersistedText(reader, 0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero)));
        return rows;
    }

    private static bool IsAdvancementSafetyEnabled(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT unattended_mode_code, stop_all_applied, stop_all_operation_id,
                   stop_all_reason_code, stop_all_requested_at_utc, stop_all_applied_at_utc
            FROM unattended_safety_state WHERE state_id = 'global';
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw Invalid("The global recurring safety state is missing.");
        if (reader.IsDBNull(0) || reader.GetValue(0) is not string mode ||
            reader.GetValue(1) is not long stopAll || stopAll is not (0 or 1))
            throw Invalid("The global recurring safety state is malformed.");
        var operationNull = reader.IsDBNull(2);
        var reasonNull = reader.IsDBNull(3);
        var requestedNull = reader.IsDBNull(4);
        var appliedNull = reader.IsDBNull(5);
        var stopShapeValid = stopAll == 0
            ? operationNull && reasonNull && requestedNull && appliedNull
            : !operationNull && !reasonNull && !requestedNull && !appliedNull;
        if (!stopShapeValid || mode is not ("enabled" or "disabled") || reader.Read())
            throw Invalid("The global recurring safety state has an invalid shape.");
        return mode == "enabled" && stopAll == 0;
    }

    private static bool HasNonTerminalOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status_code FROM plan_occurrences WHERE plan_id = $plan_id;";
        Add(command, "$plan_id", planId);
        var nonTerminalCount = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.GetValue(0) is not string status)
                throw Invalid("A recurring occurrence has no persisted status.");
            if (status is "completed" or "missed" or "blocked" or "cancelled" or "expired")
                continue;
            if (status is not ("scheduled" or "due" or "rechecking" or "pending_lease_approval" or
                "authorized" or "pending_confirmation" or "run_created"))
                throw Invalid("A recurring occurrence has an unknown persisted status.");
            nonTerminalCount++;
            if (nonTerminalCount > 1)
                throw Invalid("More than one non-terminal recurring occurrence exists for a plan.");
        }
        return nonTerminalCount != 0;
    }

    private static void ValidatePreparationCardinality(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId,
        string planId,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM recurring_setup_preparations
            WHERE intent_id = $intent OR plan_id = $plan OR lease_id = $lease;
            """;
        Add(command, "$intent", intentId);
        Add(command, "$plan", planId);
        Add(command, "$lease", leaseId);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw Invalid("The activated recurring preparation relation is missing or ambiguous.");
    }

    private static void EnsureNoAdvancementHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM recurring_advancement_operations WHERE plan_id = $plan) +
              (SELECT COUNT(*) FROM recurring_occurrence_slots WHERE plan_id = $plan) +
              (SELECT COUNT(*) FROM plan_occurrences WHERE plan_id = $plan);
            """;
        Add(command, "$plan", planId);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw Invalid("Recurring advancement history exists without its durable cursor.");
    }

    private static CurrentTransition ReadCurrentTransition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        long revision,
        long resultVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.result_code, a.occurrence_identity, o.status_code
            FROM recurring_advancement_operations AS a
            LEFT JOIN recurring_occurrence_slots AS s
              ON s.occurrence_identity = a.occurrence_identity
            LEFT JOIN plan_occurrences AS o
              ON o.id = s.occurrence_id AND o.plan_id = s.plan_id
            WHERE a.plan_id = $plan AND a.schedule_revision = $revision
              AND a.result_cursor_version = $version;
            """;
        Add(command, "$plan", planId);
        Add(command, "$revision", revision);
        Add(command, "$version", resultVersion);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.GetValue(0) is not string resultCode)
            throw Invalid("The current recurring cursor transition is missing.");
        var identity = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (reader.Read())
            throw Invalid("The current recurring cursor transition is ambiguous.");
        return new(resultCode, identity, status);
    }

    private static void ValidateInputs(
        DateTimeOffset observedAtUtc,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset? afterRequestedAtUtc,
        string? afterSetupIntentId,
        int pageSize)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero ||
            (afterRequestedAtUtc is not null && afterRequestedAtUtc.Value.Offset != TimeSpan.Zero))
            throw Invalid("Recurring advancement query timestamps must be UTC.");
        _ = ValidateIdentity(currentUserSid);
        _ = ValidateIdentity(sessionBinding);
        if ((afterRequestedAtUtc is null) != (afterSetupIntentId is null))
            throw Invalid("The recurring advancement keyset cursor is incomplete.");
        if (afterSetupIntentId is not null)
            _ = RequiredInput(afterSetupIntentId);
        if (pageSize is < 1 or > MaximumPageSize)
            throw Invalid("The recurring advancement candidate page limit is outside its hard bound.");
    }

    private static string ValidateIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            throw Invalid("The recurring advancement principal or session is invalid.");
        return value;
    }

    private static string RequiredPersistedText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not string value || string.IsNullOrWhiteSpace(value))
            throw Invalid("A recurring setup identity field is invalid.");
        return value;
    }

    private static Phase3PersistenceException Invalid(string message) =>
        new("recurring_advancement_candidate_invalid", message);

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); }
        catch (InvalidOperationException) { }
        catch (SqliteException) { }
    }

    private sealed record IntentKey(string IntentId, DateTimeOffset RequestedAtUtc);
    private sealed record CurrentTransition(string ResultCode, string? OccurrenceIdentity, string? OccurrenceStatusCode);
}
