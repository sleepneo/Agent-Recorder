using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringSetupTerminalFailurePoint
{
    AfterChildLeaseUpdate,
    AfterIntentUpdate,
    BeforeCommitAfterFinalRead,
}

internal sealed class SqliteRecurringSetupTerminalTransaction : SqliteRepositoryBase
{
    private readonly Action<RecurringSetupTerminalFailurePoint>? _failureHook;

    internal SqliteRecurringSetupTerminalTransaction(
        SqliteOperationalStore store, Action<RecurringSetupTerminalFailurePoint>? failureHook = null) : base(store)
    {
        _failureHook = failureHook;
    }

    internal RecurringSetupTerminalResult Settle(
        string intentId, string sid, string session, RecurringSetupIntentStatus kind, string reason, DateTimeOffset now)
    {
        try
        {
            if (now.Offset != TimeSpan.Zero || !RecurringSetupTerminalReasonPolicy.Allows(kind, reason))
                return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_request_invalid");
            using var connection = OpenBusinessConnection();
            using var transaction = BeginWriteTransaction(connection);
            var persisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(connection, transaction, intentId);
            if (persisted is null || persisted.IntentKindCode != RecurringSetupIntentCodes.IntentKind ||
                persisted.CurrentUserSid != sid || persisted.SessionBinding != session)
                return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_not_found");

            var readback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(persisted, connection, transaction);
            if (now < persisted.UpdatedAtUtc)
                return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_time_non_monotonic");
            if (readback.Status is RecurringSetupIntentStatus.Rejected or RecurringSetupIntentStatus.Expired)
                return readback.Status == kind && readback.TerminalReasonCode == reason
                    ? new(RecurringSetupTerminalResultStatus.AlreadyTerminal, "already_terminal")
                    : new(RecurringSetupTerminalResultStatus.Conflict, "recurring_setup_terminal_conflict");
            if (readback.Status == RecurringSetupIntentStatus.Activated)
                return new(RecurringSetupTerminalResultStatus.Conflict, "recurring_setup_terminal_activated");
            if (kind == RecurringSetupIntentStatus.Expired && now < persisted.ExpiresAtUtc)
                return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_not_expired");

            if (readback.Status == RecurringSetupIntentStatus.LeaseApprovalPending)
            {
                var chain = SqliteRecurringSetupPreparationTransaction.ValidatePreparedChainWithinTransaction(
                    connection, transaction, persisted, readback.Snapshot);
                var transition = chain.Lease.TryTransition(ConsentLeaseStatus.Rejected, now);
                if (!transition.Succeeded || !transition.Changed)
                    throw new PersistedSnapshotException("The unapproved child Lease could not be rejected.");
                using var child = connection.CreateCommand();
                child.Transaction = transaction;
                child.CommandText = """
                    UPDATE recurring_consent_leases
                    SET status_code = $status, version = $version, updated_at_utc = $now
                    WHERE lease_id = $lease AND plan_id = $plan AND status_code = 'pending'
                      AND version = 0 AND updated_at_utc = $prepared AND authorization_digest = $digest;
                    """;
                Add(child, "$status", chain.Lease.StatusCode);
                Add(child, "$version", chain.Lease.Version);
                Add(child, "$now", UtcTicksInput(chain.Lease.UpdatedAtUtc));
                Add(child, "$lease", chain.Lease.LeaseId);
                Add(child, "$plan", chain.Plan.Id);
                Add(child, "$prepared", UtcTicksInput(chain.Preparation.PreparedAtUtc));
                Add(child, "$digest", chain.Lease.AuthorizationDigest);
                RequireCas(child.ExecuteNonQuery());
                _failureHook?.Invoke(RecurringSetupTerminalFailurePoint.AfterChildLeaseUpdate);
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE setup_intents SET status_code = $status, terminal_reason_code = $reason,
                    updated_at_utc = $now, version = $version
                WHERE intent_id = $id AND intent_kind_code = $kind AND current_user_sid = $sid
                  AND session_binding = $session AND status_code = $oldStatus AND version = $oldVersion
                  AND updated_at_utc = $oldTime AND terminal_reason_code IS NULL;
                """;
            Add(update, "$status", RecurringSetupIntentStatusCodes.ToCode(kind));
            Add(update, "$reason", reason);
            Add(update, "$now", UtcTicksInput(now));
            Add(update, "$version", checked(persisted.Version + 1));
            Add(update, "$id", intentId);
            Add(update, "$kind", RecurringSetupIntentCodes.IntentKind);
            Add(update, "$sid", sid);
            Add(update, "$session", session);
            Add(update, "$oldStatus", persisted.StatusCode);
            Add(update, "$oldVersion", persisted.Version);
            Add(update, "$oldTime", UtcTicksInput(persisted.UpdatedAtUtc));
            RequireCas(update.ExecuteNonQuery());
            _failureHook?.Invoke(RecurringSetupTerminalFailurePoint.AfterIntentUpdate);

            var final = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(connection, transaction, intentId)
                ?? throw new PersistedSnapshotException("The terminal setup intent disappeared.");
            var verified = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(final, connection, transaction);
            if (verified.Status != kind || verified.TerminalReasonCode != reason ||
                final.Version != persisted.Version + 1 || final.UpdatedAtUtc != now)
                throw new PersistedSnapshotException("The final terminal setup intent is not exact.");
            _failureHook?.Invoke(RecurringSetupTerminalFailurePoint.BeforeCommitAfterFinalRead);
            transaction.Commit();
            return new(RecurringSetupTerminalResultStatus.Settled, reason);
        }
        catch (Exception exception) when (IsBusyOrLocked(exception))
        {
            return new(RecurringSetupTerminalResultStatus.Conflict, "recurring_setup_terminal_concurrency_conflict");
        }
        catch (Phase3PersistenceException exception)
        {
            return new(exception.Code == "recurring_setup_terminal_concurrency_conflict"
                ? RecurringSetupTerminalResultStatus.Conflict : RecurringSetupTerminalResultStatus.Rejected, exception.Code);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or ArgumentException or OverflowException)
        {
            return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_snapshot_invalid");
        }
        catch (Exception)
        {
            return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_sqlite_failure");
        }
    }

    internal int ReconcileExpiredPending(DateTimeOffset now, int maximumRows)
    {
        if (maximumRows is < 1 or > 256 || now.Offset != TimeSpan.Zero)
            throw InvalidArgument("The recurring expiry reconciliation bound or time is invalid.");
        var candidates = new List<(string Id, string Sid, string Session)>();
        using (var connection = OpenBusinessConnection())
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT intent_id, current_user_sid, session_binding FROM setup_intents
                WHERE intent_kind_code = $kind
                  AND status_code IN ('region_selection_pending', 'lease_approval_pending') AND expires_at_utc <= $now
                ORDER BY expires_at_utc, intent_id LIMIT $limit;
                """;
            Add(read, "$kind", RecurringSetupIntentCodes.IntentKind);
            Add(read, "$now", UtcTicksInput(now));
            Add(read, "$limit", maximumRows);
            using var reader = read.ExecuteReader();
            while (reader.Read())
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        // Each candidate gets the same immediate transaction and final readback
        // as an individual call. A corrupt row stays untouched and is not counted.
        // The limit bounds attempts, including corrupt rows, not just successes.
        var reconciled = 0;
        foreach (var candidate in candidates)
        {
            var result = Settle(candidate.Id, candidate.Sid, candidate.Session, RecurringSetupIntentStatus.Expired,
                RecurringSetupTerminalReasonPolicy.IntentExpired, now);
            if (result.Changed) reconciled++;
        }
        return reconciled;
    }

    private static void RequireCas(int affected)
    {
        if (affected != 1)
            throw new Phase3PersistenceException("recurring_setup_terminal_concurrency_conflict", "The pending setup chain changed.");
    }

    private static bool IsBusyOrLocked(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 } ||
        (exception.InnerException is { } inner && IsBusyOrLocked(inner));
}
