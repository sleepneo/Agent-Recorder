using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record SqliteStandingLeaseSafetyGlobalState(
    UnattendedModeStatus UnattendedMode,
    DateTimeOffset ModeChangedAtUtc,
    DateTimeOffset? UnattendedEnabledAtUtc,
    bool StopAllApplied,
    string? StopAllOperationId,
    string? StopAllReasonCode,
    DateTimeOffset? StopAllRequestedAtUtc,
    DateTimeOffset? StopAllAppliedAtUtc,
    long Version);

internal sealed class SqliteStandingLeaseSafetyControlTransaction : SqliteRepositoryBase
{
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingLeaseSafetyControlTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingLeaseSafetyQueryResult QueryIntent(string intentId)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            if (!TryLoadBoundChain(connection, transaction, intentId, out var chain, out var failure))
            {
                transaction.Commit();
                return StandingLeaseSafetyQueryResult.Rejected(failure!);
            }

            var state = ReadGlobalState(connection, transaction);
            var result = StandingLeaseSafetyQueryResult.Available(BuildState(intentId, chain!, state));
            transaction.Commit();
            return result;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseControlCenterQueryResult QueryControlCenter(string currentUserSid)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var globalState = ReadGlobalState(connection, transaction);
            var items = new List<StandingLeaseControlCenterLeaseSummary>();

            var intentIds = new List<string>();
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT intent_id
                    FROM setup_intents
                    WHERE current_user_sid = $current_user_sid
                    ORDER BY requested_at_utc DESC, intent_id ASC;
                    """;
                Add(command, "$current_user_sid", currentUserSid);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    intentIds.Add(ReadRequiredText(reader, 0));
                }
            }

            foreach (var intentId in intentIds)
            {
                var intent = SqliteStandingLeaseAuthorizationActivationTransaction.ReadPreparedIntent(
                    connection,
                    transaction,
                    intentId);
                if (intent is null || !string.Equals(intent.CurrentUserSid, currentUserSid, StringComparison.Ordinal))
                {
                    throw new PersistedSnapshotException("The control-center setup intent could not be revalidated.");
                }

                StandingLeaseSafetyChain? chain = null;
                var associationCount = new[]
                {
                    intent.PlanId,
                    intent.OccurrenceId,
                    intent.LeaseId,
                    intent.ScopeId,
                }.Count(value => value is not null);
                if (associationCount != 0 && associationCount != 4)
                {
                    throw new PersistedSnapshotException("The control-center setup intent has a partial authorization binding.");
                }

                if (associationCount == 4 &&
                    !TryLoadBoundChain(connection, transaction, intentId, out chain, out var failure))
                {
                    throw new PersistedSnapshotException(
                        "The control-center setup intent chain is invalid: " + failure);
                }

                items.Add(BuildControlCenterItem(intent, chain, globalState));
            }

            transaction.Commit();
            return StandingLeaseControlCenterQueryResult.Available(
                new StandingLeaseControlCenterState(
                    globalState.UnattendedMode,
                    globalState.StopAllApplied,
                    globalState.StopAllOperationId,
                    globalState.StopAllReasonCode,
                    globalState.StopAllRequestedAtUtc,
                    globalState.StopAllAppliedAtUtc,
                    items));
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseSafetyControlResult RevokeLease(
        string intentId,
        string operationId,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var existing = ReadOperation(connection, transaction, operationId);
            if (existing is not null)
            {
                var identityFailure = ValidateOperationIdentity(
                    existing,
                    StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                    intentId,
                    existing.LeaseId,
                    reasonCode);
                if (identityFailure is not null)
                {
                    return CommitReadWithoutCurrentOperation(
                        transaction,
                        StandingLeaseSafetyControlResult.Rejected(operationId, identityFailure));
                }

                transaction.Commit();
                return ExistingOperationResult(existing);
            }

            if (!TryLoadBoundChain(connection, transaction, intentId, out var chain, out var failure))
            {
                InsertOperation(
                    connection,
                    transaction,
                    operationId,
                    StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                    intentId,
                    leaseId: null,
                    nowUtc,
                    reasonCode,
                    "rejected:" + failure!,
                    changed: false,
                    requiresActiveRunStop: false,
                    nowUtc);
                return CommitResult(
                    transaction,
                    StandingLeaseSafetyControlResult.Rejected(operationId, failure!));
            }

            var globalState = ReadGlobalState(connection, transaction);
            var current = chain!;
            var activeRunPresent = HasActiveRun(current.Runs);
            var requiresActiveRunStop = activeRunPresent;
            StandingLeaseSafetyControlResult result;

            if (current.Lease.Status == ConsentLeaseStatus.Revoked)
            {
                result = StandingLeaseSafetyControlResult.AlreadyAppliedResult(
                    operationId,
                    StandingLeaseSafetyReasonCodes.LeaseAlreadyRevoked,
                    requiresActiveRunStop,
                    BuildState(intentId, current, globalState));
            }
            else if (current.Lease.Status is ConsentLeaseStatus.Expired or ConsentLeaseStatus.Rejected ||
                (current.Lease.Status == ConsentLeaseStatus.Exhausted && !activeRunPresent))
            {
                var terminalReason = current.Lease.Status switch
                {
                    ConsentLeaseStatus.Expired => StandingLeaseSafetyReasonCodes.LeaseExpired,
                    ConsentLeaseStatus.Exhausted => StandingLeaseSafetyReasonCodes.LeaseExhausted,
                    ConsentLeaseStatus.Rejected => StandingLeaseSafetyReasonCodes.LeaseRejected,
                    _ => "lease_status_invalid",
                };
                result = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    terminalReason,
                    BuildState(intentId, current, globalState),
                    requiresActiveRunStop);
            }
            else if (current.Lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active or ConsentLeaseStatus.Exhausted))
            {
                result = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    "lease_status_invalid",
                    BuildState(intentId, current, globalState),
                    requiresActiveRunStop);
            }
            else
            {
                if (current.Lease.Version == long.MaxValue ||
                    (!current.Occurrence.HasCreatedRun && current.Occurrence.Version == long.MaxValue))
                {
                    result = StandingLeaseSafetyControlResult.Rejected(
                        operationId,
                        "safety_version_exhausted",
                        BuildState(intentId, current, globalState),
                        requiresActiveRunStop);
                }
                else
                {
                    var leaseVersion = current.Lease.Version;
                    var leaseTransition = current.Lease.TryTransition(ConsentLeaseStatus.Revoked, nowUtc);
                    if (!leaseTransition.Succeeded || !leaseTransition.Changed)
                    {
                        result = StandingLeaseSafetyControlResult.Rejected(
                            operationId,
                            leaseTransition.ReasonCode,
                            BuildState(intentId, current, globalState),
                            requiresActiveRunStop);
                    }
                    else
                    {
                        UpdateLease(connection, transaction, current.Lease, leaseVersion);

                        if (!current.Occurrence.HasCreatedRun && !Phase3TransitionGuards.IsTerminal(current.Occurrence.Status))
                        {
                            var occurrenceVersion = current.Occurrence.Version;
                            var occurrenceStatus = current.Occurrence.Status;
                            var occurrenceTransition = current.Occurrence.TryTransition(
                                PlanOccurrenceStatus.Blocked,
                                nowUtc,
                                StandingLeaseSafetyReasonCodes.LeaseRevoked);
                            if (!occurrenceTransition.Succeeded || !occurrenceTransition.Changed)
                            {
                                throw new Phase3PersistenceException(
                                    "safety_occurrence_block_failed",
                                    "The occurrence could not be blocked when its lease was revoked.");
                            }

                            UpdateOccurrence(connection, transaction, current.Occurrence, occurrenceVersion, occurrenceStatus);
                        }

                        result = StandingLeaseSafetyControlResult.ChangedResult(
                            operationId,
                            StandingLeaseSafetyReasonCodes.LeaseRevoked,
                            requiresActiveRunStop,
                            BuildState(intentId, current, globalState));
                    }
                }
            }

            InsertOperation(
                connection,
                transaction,
                operationId,
                StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                intentId,
                current.Lease.Id,
                nowUtc,
                reasonCode,
                StoredResultCode(result),
                result.Changed,
                result.RequiresActiveRunStop,
                nowUtc);
                _beforeCommitForTest?.Invoke(connection, transaction);
                return CommitResult(transaction, result);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// Revokes one exact recurring consent lease through the same durable
    /// safety boundary and operation ledger as standing leases. Recurring
    /// leases intentionally have no setup-intent key in this ledger identity:
    /// the exact lease id is the target and intent_id remains NULL.
    /// </summary>
    internal StandingLeaseSafetyControlResult RevokeRecurringLease(
        string leaseId,
        string operationId,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var existing = ReadOperation(connection, transaction, operationId);
            if (existing is not null)
            {
                var identityFailure = ValidateOperationIdentity(
                    existing,
                    StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                    intentId: null,
                    leaseId: leaseId,
                    reasonCode: reasonCode);
                if (identityFailure is not null)
                {
                    return CommitReadWithoutCurrentOperation(
                        transaction,
                        StandingLeaseSafetyControlResult.Rejected(operationId, identityFailure));
                }

                transaction.Commit();
                return ExistingOperationResult(existing);
            }

            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(
                connection,
                transaction,
                leaseId);
            if (lease is null)
            {
                var notFound = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    "recurring_lease_not_found");
                InsertOperation(
                    connection,
                    transaction,
                    operationId,
                    StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                    intentId: null,
                    leaseId: leaseId,
                    requestedAtUtc: nowUtc,
                    reasonRequestCode: reasonCode,
                    StoredResultCode(notFound),
                    changed: false,
                    requiresActiveRunStop: false,
                    completedAtUtc: nowUtc);
                _beforeCommitForTest?.Invoke(connection, transaction);
                return CommitResult(transaction, notFound);
            }

            // Read the global row even though a single-lease revoke is a
            // safety contraction. This keeps the unified boundary fail closed
            // when the shared safety state or its ledger prerequisites are
            // damaged.
            _ = ReadGlobalState(connection, transaction);
            var activeRunPresent = HasActiveRecurringRun(connection, transaction, lease.LeaseId);
            StandingLeaseSafetyControlResult result;

            if (lease.Status == ConsentLeaseStatus.Revoked)
            {
                result = StandingLeaseSafetyControlResult.AlreadyAppliedResult(
                    operationId,
                    StandingLeaseSafetyReasonCodes.LeaseAlreadyRevoked,
                    activeRunPresent);
            }
            else if (lease.Status is ConsentLeaseStatus.Expired or ConsentLeaseStatus.Rejected ||
                (lease.Status == ConsentLeaseStatus.Exhausted && !activeRunPresent))
            {
                var terminalReason = lease.Status switch
                {
                    ConsentLeaseStatus.Expired => StandingLeaseSafetyReasonCodes.LeaseExpired,
                    ConsentLeaseStatus.Exhausted => StandingLeaseSafetyReasonCodes.LeaseExhausted,
                    ConsentLeaseStatus.Rejected => StandingLeaseSafetyReasonCodes.LeaseRejected,
                    _ => "lease_status_invalid",
                };
                result = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    terminalReason,
                    requiresActiveRunStop: activeRunPresent);
            }
            else if (lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active or ConsentLeaseStatus.Exhausted))
            {
                result = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    "lease_status_invalid",
                    requiresActiveRunStop: activeRunPresent);
            }
            else if (lease.Version == long.MaxValue)
            {
                result = StandingLeaseSafetyControlResult.Rejected(
                    operationId,
                    "safety_version_exhausted",
                    requiresActiveRunStop: activeRunPresent);
            }
            else
            {
                var expectedVersion = lease.Version;
                var expectedStatus = lease.Status;
                var transition = lease.TryTransition(ConsentLeaseStatus.Revoked, nowUtc);
                if (!transition.Succeeded || !transition.Changed)
                {
                    result = StandingLeaseSafetyControlResult.Rejected(
                        operationId,
                        transition.ReasonCode,
                        requiresActiveRunStop: activeRunPresent);
                }
                else
                {
                    UpdateRecurringLease(connection, transaction, lease, expectedVersion, expectedStatus);
                    result = StandingLeaseSafetyControlResult.ChangedResult(
                        operationId,
                        StandingLeaseSafetyReasonCodes.LeaseRevoked,
                        activeRunPresent);
                }
            }

            InsertOperation(
                connection,
                transaction,
                operationId,
                StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
                intentId: null,
                leaseId: lease.LeaseId,
                requestedAtUtc: nowUtc,
                reasonRequestCode: reasonCode,
                StoredResultCode(result),
                result.Changed,
                result.RequiresActiveRunStop,
                completedAtUtc: nowUtc);
            _beforeCommitForTest?.Invoke(connection, transaction);
            return CommitResult(transaction, result);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseSafetyControlResult StopAllAndRevokeAll(
        string operationId,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var existing = ReadOperation(connection, transaction, operationId);
            if (existing is not null)
            {
                var identityFailure = ValidateOperationIdentity(
                    existing,
                    StandingLeaseSafetyOperationKindCodes.StopAllRevokeAll,
                    intentId: null,
                    leaseId: null,
                    reasonCode);
                if (identityFailure is not null)
                {
                    return CommitReadWithoutCurrentOperation(
                        transaction,
                        StandingLeaseSafetyControlResult.Rejected(operationId, identityFailure));
                }

                transaction.Commit();
                return ExistingOperationResult(existing);
            }

            var globalState = ReadGlobalState(connection, transaction);

            var requiresActiveRunStop = false;
            foreach (var leaseId in ReadStandingLeaseIds(connection, transaction))
            {
                var chain = LoadExecutionChain(connection, transaction, leaseId, requireScope: false);
                requiresActiveRunStop |= HasActiveRun(chain.Runs);

                if (chain.Lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active) &&
                    !(chain.Lease.Status == ConsentLeaseStatus.Exhausted && HasActiveRun(chain.Runs)))
                {
                    continue;
                }

                if (chain.Lease.Version == long.MaxValue ||
                    (!chain.Occurrence.HasCreatedRun && chain.Occurrence.Version == long.MaxValue))
                {
                    throw new Phase3PersistenceException(
                        "safety_version_exhausted",
                        "A revocable lease aggregate cannot be advanced safely.");
                }

                var leaseVersion = chain.Lease.Version;
                var leaseTransition = chain.Lease.TryTransition(ConsentLeaseStatus.Revoked, nowUtc);
                if (!leaseTransition.Succeeded || !leaseTransition.Changed)
                {
                    throw new Phase3PersistenceException(
                        "safety_lease_revoke_failed",
                        "A revocable lease could not be revoked.");
                }

                UpdateLease(connection, transaction, chain.Lease, leaseVersion);

                if (!chain.Occurrence.HasCreatedRun && !Phase3TransitionGuards.IsTerminal(chain.Occurrence.Status))
                {
                    var occurrenceVersion = chain.Occurrence.Version;
                    var occurrenceStatus = chain.Occurrence.Status;
                    var occurrenceTransition = chain.Occurrence.TryTransition(
                        PlanOccurrenceStatus.Blocked,
                        nowUtc,
                        StandingLeaseSafetyReasonCodes.LeaseRevoked);
                    if (!occurrenceTransition.Succeeded || !occurrenceTransition.Changed)
                    {
                        throw new Phase3PersistenceException(
                            "safety_occurrence_block_failed",
                            "An occurrence could not be blocked during stop-all.");
                    }

                    UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion, occurrenceStatus);
                }
            }

            foreach (var leaseId in ReadRecurringLeaseIds(connection, transaction))
            {
                var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(
                        connection,
                        transaction,
                        leaseId)
                    ?? throw new PersistedSnapshotException("The recurring lease disappeared during stop-all.");
                var activeRunPresent = HasActiveRecurringRun(connection, transaction, lease.LeaseId);
                requiresActiveRunStop |= activeRunPresent;

                if (lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active) &&
                    !(lease.Status == ConsentLeaseStatus.Exhausted && activeRunPresent))
                {
                    continue;
                }

                if (lease.Version == long.MaxValue)
                {
                    throw new Phase3PersistenceException(
                        "safety_version_exhausted",
                        "A recurring lease aggregate cannot be advanced safely.");
                }

                var expectedVersion = lease.Version;
                var expectedStatus = lease.Status;
                var transition = lease.TryTransition(ConsentLeaseStatus.Revoked, nowUtc);
                if (!transition.Succeeded || !transition.Changed)
                {
                    throw new Phase3PersistenceException(
                        transition.ReasonCode,
                        "A recurring lease could not be revoked during stop-all.");
                }

                UpdateRecurringLease(connection, transaction, lease, expectedVersion, expectedStatus);
            }

            // Stop All is linearized by this write transaction: every lease
            // visible here is revoked before the history summary is updated.
            // The summary is intentionally not consulted as a future global
            // gate; a later, fully approved authorization gets its own lease
            // boundary and may execute while unattended mode is enabled.
            var updatedState = globalState with
            {
                StopAllApplied = true,
                StopAllOperationId = operationId,
                StopAllReasonCode = reasonCode,
                StopAllRequestedAtUtc = nowUtc,
                StopAllAppliedAtUtc = nowUtc,
                Version = checked(globalState.Version + 1),
            };
            UpdateGlobalState(connection, transaction, globalState, updatedState);

            var changedResult = StandingLeaseSafetyControlResult.ChangedResult(
                operationId,
                StandingLeaseSafetyReasonCodes.StopAllApplied,
                requiresActiveRunStop);
            InsertOperation(
                connection,
                transaction,
                operationId,
                StandingLeaseSafetyOperationKindCodes.StopAllRevokeAll,
                intentId: null,
                leaseId: null,
                nowUtc,
                reasonCode,
                StoredResultCode(changedResult),
                changed: true,
                requiresActiveRunStop,
                nowUtc);
            _beforeCommitForTest?.Invoke(connection, transaction);
            return CommitResult(transaction, changedResult);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseSafetyControlResult SetUnattendedMode(
        string operationId,
        bool enabled,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var operationKind = enabled
                ? StandingLeaseSafetyOperationKindCodes.UnattendedEnable
                : StandingLeaseSafetyOperationKindCodes.UnattendedDisable;
            var existing = ReadOperation(connection, transaction, operationId);
            if (existing is not null)
            {
                var identityFailure = ValidateOperationIdentity(existing, operationKind, null, null, reasonCode);
                if (identityFailure is not null)
                {
                    return CommitReadWithoutCurrentOperation(
                        transaction,
                        StandingLeaseSafetyControlResult.Rejected(operationId, identityFailure));
                }

                var replayRequiresActiveRunStop = !enabled && HasAnyActiveRuns(connection, transaction);
                transaction.Commit();
                return ExistingOperationResult(existing, replayRequiresActiveRunStop);
            }

            var globalState = ReadGlobalState(connection, transaction);
            var targetMode = enabled ? UnattendedModeStatus.Enabled : UnattendedModeStatus.Disabled;
            var requiresActiveRunStop = !enabled && HasAnyActiveRuns(connection, transaction);
            if (globalState.UnattendedMode == targetMode)
            {
                var idempotentResult = StandingLeaseSafetyControlResult.AlreadyAppliedResult(
                    operationId,
                    enabled ? StandingLeaseSafetyReasonCodes.UnattendedAlreadyEnabled : StandingLeaseSafetyReasonCodes.UnattendedDisabled,
                    requiresActiveRunStop);
                InsertOperation(
                    connection,
                    transaction,
                    operationId,
                    operationKind,
                    intentId: null,
                    leaseId: null,
                    nowUtc,
                    reasonCode,
                    StoredResultCode(idempotentResult),
                    changed: false,
                    requiresActiveRunStop,
                    nowUtc);
                transaction.Commit();
                return idempotentResult.WithDurableOperationCommitted();
            }

            var updatedState = globalState with
            {
                UnattendedMode = targetMode,
                ModeChangedAtUtc = nowUtc,
                UnattendedEnabledAtUtc = enabled ? nowUtc : null,
                Version = checked(globalState.Version + 1),
            };
            UpdateGlobalState(connection, transaction, globalState, updatedState);

            var result = StandingLeaseSafetyControlResult.ChangedResult(
                operationId,
                enabled ? StandingLeaseSafetyReasonCodes.UnattendedEnabled : StandingLeaseSafetyReasonCodes.UnattendedDisabled,
                requiresActiveRunStop);
            InsertOperation(
                connection,
                transaction,
                operationId,
                operationKind,
                intentId: null,
                leaseId: null,
                nowUtc,
                reasonCode,
                StoredResultCode(result),
                changed: true,
                requiresActiveRunStop,
                nowUtc);
            _beforeCommitForTest?.Invoke(connection, transaction);
            return CommitResult(transaction, result);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal static SqliteStandingLeaseSafetyGlobalState ReadGlobalState(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state_id, unattended_mode_code, mode_changed_at_utc,
                   unattended_enabled_at_utc, stop_all_applied,
                   stop_all_operation_id, stop_all_reason_code,
                   stop_all_requested_at_utc, stop_all_applied_at_utc, version
            FROM unattended_safety_state
            WHERE state_id = 'global';
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistedSnapshotException("The global unattended safety state is missing.");
        }

        var stateId = ReadRequiredText(reader, 0);
        if (!string.Equals(stateId, "global", StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The global unattended safety state identity is invalid.");
        }

        var mode = UnattendedModeStatusCodes.Parse(ReadRequiredText(reader, 1));
        var modeChangedAtUtc = ReadUtcDateTimeOffset(reader, 2);
        var enabledAtUtc = ReadNullableUtcDateTimeOffset(reader, 3);
        var stopAllApplied = ReadBoolean(reader, 4);
        var operationId = ReadNullableText(reader, 5);
        var reasonCode = ReadNullableText(reader, 6);
        var requestedAtUtc = ReadNullableUtcDateTimeOffset(reader, 7);
        var appliedAtUtc = ReadNullableUtcDateTimeOffset(reader, 8);
        var version = ReadInt64(reader, 9);
        // Schema v6 changes the seeded schema-v5 row from enabled to disabled
        // without rewriting its historical zero enabled-at sentinel. Keep
        // that one immutable bootstrap shape readable, but normalize it to
        // the post-v6 disabled invariant before exposing the trusted model.
        var isHistoricalDisabledBootstrap =
            mode == UnattendedModeStatus.Disabled &&
            modeChangedAtUtc == DateTimeOffset.MinValue &&
            enabledAtUtc == DateTimeOffset.MinValue &&
            !stopAllApplied &&
            operationId is null &&
            reasonCode is null &&
            requestedAtUtc is null &&
            appliedAtUtc is null &&
            version == 0;
        if (isHistoricalDisabledBootstrap)
        {
            enabledAtUtc = null;
        }

        var modeTimestampsValid = mode switch
        {
            UnattendedModeStatus.Enabled =>
                enabledAtUtc is not null && enabledAtUtc.Value == modeChangedAtUtc,
            UnattendedModeStatus.Disabled =>
                enabledAtUtc is null,
            _ => false,
        };
        var stopAllShapeValid = stopAllApplied
            ? operationId is not null && reasonCode is not null && requestedAtUtc is not null && appliedAtUtc is not null
            : operationId is null && reasonCode is null && requestedAtUtc is null && appliedAtUtc is null;
        var stopAllTimelineValid = !stopAllApplied ||
            requestedAtUtc <= appliedAtUtc;
        if (version < 0 || version == long.MaxValue ||
            !modeTimestampsValid ||
            !stopAllShapeValid ||
            !stopAllTimelineValid)
        {
            throw new PersistedSnapshotException("The global unattended safety state is inconsistent.");
        }

        return new SqliteStandingLeaseSafetyGlobalState(
            mode,
            modeChangedAtUtc,
            enabledAtUtc,
            stopAllApplied,
            operationId,
            reasonCode,
            requestedAtUtc,
            appliedAtUtc,
            version);
    }

    internal static void EnsureExecutionAllowed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsentLease lease)
    {
        var globalState = ReadGlobalState(connection, transaction);
        var failure = StandingLeaseSafetyPolicy.EvaluateWake(
            lease,
            globalState.UnattendedMode,
            globalState.StopAllApplied,
            globalState.UnattendedEnabledAtUtc);
        // The existing start-gate precondition owns the stable lease_not_active
        // result for an already revoked lease. Global safety state and the
        // re-enable boundary still fail here before any new execution write.
        if (string.Equals(failure, StandingLeaseSafetyReasonCodes.LeaseRevoked, StringComparison.Ordinal))
        {
            return;
        }

        if (failure is not null)
        {
            throw new Phase3PersistenceException(
                failure,
                "The standing lease is blocked by the persisted safety control boundary.");
        }
    }

    /// <summary>
    /// Revalidates the durable safety and one-shot execution chain at the
    /// final physical backend-start boundary. The caller holds the shared
    /// process interlock, so a successful read and the following Backend.Start
    /// are one process-local linearized operation.
    /// </summary>
    internal string? ValidateStandingStart(
        StandingLeaseCaptureExecutionTicket ticket,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (nowUtc.Offset != TimeSpan.Zero)
            return "standing_engine_time_not_utc";

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        var globalState = ReadGlobalState(connection, transaction);
        if (globalState.UnattendedMode != UnattendedModeStatus.Enabled)
            return StandingLeaseSafetyReasonCodes.UnattendedDisabled;

        var specification = ticket.Specification;
        var scope = ticket.Scope;
        if (!string.Equals(specification.ScopeId, scope.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(specification.ScopeDigest, scope.ScopeDigest, StringComparison.Ordinal))
            return "standing_engine_scope_binding_invalid";

        var persistedScope = SqliteAuthorizedCaptureScopeRepository.TryReadByIdentity(
            connection,
            transaction,
            specification.PlanId,
            specification.OccurrenceId,
            specification.LeaseId);
        if (persistedScope is null ||
            !persistedScope.MatchesExactly(scope) ||
            !specification.MatchesScope(persistedScope))
            return "standing_engine_scope_binding_invalid";

        var chain = LoadExecutionChain(connection, transaction, specification.LeaseId);
        var run = LoadRun(connection, transaction, specification.RunId);
        var use = LoadUse(connection, transaction, specification.LeaseUseId);
        if (run is null || use is null ||
            chain.Runs.Count != 1 || !string.Equals(chain.Runs[0].Id, run.Id, StringComparison.Ordinal) ||
            !string.Equals(chain.Occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, chain.Occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.LeaseId, chain.Lease.Id, StringComparison.Ordinal))
            return "standing_engine_execution_chain_invalid";

        if (globalState.StopAllApplied &&
            globalState.StopAllAppliedAtUtc is { } stopAllAt &&
            run.CreatedAtUtc <= stopAllAt)
            return StandingLeaseSafetyReasonCodes.StopAllActive;

        if (globalState.UnattendedEnabledAtUtc is { } enabledAt && run.CreatedAtUtc < enabledAt)
            return StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization;

        if (chain.Plan.Status != PlanDefinitionStatus.Enabled || !chain.Plan.IsOneTime)
            return "standing_engine_plan_not_enabled";
        if (chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated ||
            !string.Equals(chain.Occurrence.RunId, specification.RunId, StringComparison.Ordinal))
            return "standing_engine_occurrence_not_run_created";
        if (run.Status != RecordingRunStatus.StartCommitted || !run.HasCrossedStartCommit)
            return "standing_engine_run_not_start_committed";
        if (use.Status != LeaseUseStatus.StartCommitted || !use.IsQuotaConsumed)
            return "standing_engine_use_not_start_committed";
        if (chain.Lease.Status != ConsentLeaseStatus.Exhausted)
            return chain.Lease.Status == ConsentLeaseStatus.Revoked
                ? StandingLeaseSafetyReasonCodes.LeaseRevoked
                : "standing_engine_lease_not_exhausted";
        if (chain.Lease.IsExpiredAt(nowUtc) || nowUtc >= chain.Occurrence.WindowEndUtc)
            return StandingLeaseSafetyReasonCodes.LeaseExpired;

        transaction.Commit();
        return null;
    }

    private static StandingLeaseSafetyControlResult ExistingOperationResult(
        StandingLeaseSafetyOperationRecord operation,
        bool? requiresActiveRunStopOverride = null) =>
        operation.ResultCode.StartsWith("rejected:", StringComparison.Ordinal)
            ? StandingLeaseSafetyControlResult.Rejected(
                operation.OperationId,
                operation.ResultCode["rejected:".Length..],
                requiresActiveRunStop: requiresActiveRunStopOverride ?? operation.RequiresActiveRunStop,
                durableOperationCommitted: true)
            : StandingLeaseSafetyControlResult.AlreadyAppliedResult(
                operation.OperationId,
                operation.ResultCode,
                requiresActiveRunStopOverride ?? operation.RequiresActiveRunStop,
                durableOperationCommitted: true,
                durableStateChanged: operation.Changed);

    private static StandingLeaseSafetyControlResult CommitResult(
        SqliteTransaction transaction,
        StandingLeaseSafetyControlResult result)
    {
        transaction.Commit();
        return result.WithDurableOperationCommitted();
    }

    private static StandingLeaseSafetyControlResult CommitReadWithoutCurrentOperation(
        SqliteTransaction transaction,
        StandingLeaseSafetyControlResult result)
    {
        transaction.Commit();
        return result;
    }

    private static string StoredResultCode(StandingLeaseSafetyControlResult result) =>
        result.Status == StandingLeaseSafetyControlResultStatus.Rejected
            ? "rejected:" + result.Reason
            : result.Reason;

    private static string? ValidateOperationIdentity(
        StandingLeaseSafetyOperationRecord operation,
        string expectedKind,
        string? intentId,
        string? leaseId,
        string reasonCode)
    {
        if (!string.Equals(operation.OperationKindCode, expectedKind, StringComparison.Ordinal) ||
            !string.Equals(operation.IntentId, intentId, StringComparison.Ordinal) ||
            !string.Equals(operation.LeaseId, leaseId, StringComparison.Ordinal) ||
            !string.Equals(operation.ReasonRequestCode, reasonCode, StringComparison.Ordinal))
        {
            return "safety_operation_conflict";
        }

        return null;
    }

    private static bool TryLoadBoundChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId,
        out StandingLeaseSafetyChain? chain,
        out string? failure)
    {
        var intent = SqliteStandingLeaseAuthorizationActivationTransaction.ReadPreparedIntent(
            connection,
            transaction,
            intentId);
        if (intent is null)
        {
            chain = null;
            failure = "safety_intent_not_found";
            return false;
        }

        if (intent.PlanId is null || intent.OccurrenceId is null || intent.LeaseId is null || intent.ScopeId is null)
        {
            chain = null;
            failure = "safety_intent_not_bound";
            return false;
        }

        try
        {
            var loaded = LoadExecutionChain(connection, transaction, intent.LeaseId);
            if (!string.Equals(loaded.Plan.Id, intent.PlanId, StringComparison.Ordinal) ||
                !string.Equals(loaded.Occurrence.Id, intent.OccurrenceId, StringComparison.Ordinal) ||
                loaded.Scope is null ||
                !string.Equals(loaded.Scope.ScopeId, intent.ScopeId, StringComparison.Ordinal) ||
                !string.Equals(intent.CurrentUserSid, loaded.Scope.CurrentUserSid, StringComparison.Ordinal) ||
                !string.Equals(intent.SessionBinding, loaded.Scope.SessionBinding, StringComparison.Ordinal))
            {
                chain = null;
                failure = "safety_intent_conflict";
                return false;
            }

            chain = loaded with { Intent = intent };
            failure = null;
            return true;
        }
        catch (PersistedSnapshotException)
        {
            throw;
        }
    }

    private static StandingLeaseSafetyState BuildState(
        string intentId,
        StandingLeaseSafetyChain chain,
        SqliteStandingLeaseSafetyGlobalState globalState)
    {
        var activeRunPresent = HasActiveRun(chain.Runs);
        var blockedReason = StandingLeaseSafetyPolicy.EvaluateWake(
            chain.Lease,
            globalState.UnattendedMode,
            globalState.StopAllApplied,
            globalState.UnattendedEnabledAtUtc);
        return new StandingLeaseSafetyState(
            intentId,
            chain.Plan.Id,
            chain.Occurrence.Id,
            chain.Lease.Id,
            chain.Lease.Status,
            globalState.UnattendedMode,
            globalState.StopAllApplied,
            globalState.StopAllRequestedAtUtc,
            globalState.StopAllAppliedAtUtc,
            globalState.StopAllReasonCode,
            blockedReason,
            activeRunPresent,
            activeRunPresent);
    }

    private static StandingLeaseControlCenterLeaseSummary BuildControlCenterItem(
        SqliteStandingLeaseAuthorizationActivationTransaction.PreparedIntentBinding intent,
        StandingLeaseSafetyChain? chain,
        SqliteStandingLeaseSafetyGlobalState globalState)
    {
        if (chain is null)
        {
            var pendingReason = intent.Status switch
            {
                StandingSetupIntentStatus.RegionSelectionPending => "region_selection_pending",
                StandingSetupIntentStatus.LeaseApprovalPending => "lease_approval_pending",
                StandingSetupIntentStatus.Rejected or StandingSetupIntentStatus.Expired => intent.TerminalReasonCode,
                _ => "safety_intent_not_bound",
            };
            return new StandingLeaseControlCenterLeaseSummary(
                intent.IntentId,
                intent.Status,
                intent.ExpiresAtUtc,
                intent.ScheduledStartUtc,
                intent.LatestStartUtc,
                intent.PlannedEndUtc,
                intent.MaximumDuration,
                intent.LeaseValidUntilUtc,
                intent.PlanId,
                intent.OccurrenceId,
                intent.LeaseId,
                null,
                null,
                pendingReason,
                intent.TerminalReasonCode,
                false,
                false);
        }

        var activeRunPresent = HasActiveRun(chain.Runs);
        var blockedReason = StandingLeaseSafetyPolicy.EvaluateWake(
            chain.Lease,
            globalState.UnattendedMode,
            globalState.StopAllApplied,
            globalState.UnattendedEnabledAtUtc)
            ?? chain.Occurrence.TerminalReasonCode
            ?? intent.TerminalReasonCode;
        return new StandingLeaseControlCenterLeaseSummary(
            intent.IntentId,
            intent.Status,
            intent.ExpiresAtUtc,
            intent.ScheduledStartUtc,
            intent.LatestStartUtc,
            intent.PlannedEndUtc,
            intent.MaximumDuration,
            intent.LeaseValidUntilUtc,
            chain.Plan.Id,
            chain.Occurrence.Id,
            chain.Lease.Id,
            chain.Lease.Status,
            chain.Occurrence.Status,
            blockedReason,
            chain.Occurrence.TerminalReasonCode ?? intent.TerminalReasonCode,
            activeRunPresent,
            activeRunPresent);
    }

    private static bool HasActiveRun(IReadOnlyList<RecordingRun> runs) =>
        runs.Any(run => !Phase3TransitionGuards.IsTerminal(run.Status));

    private static IReadOnlyList<string> ReadStandingLeaseIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.id
            FROM consent_leases l
            ORDER BY l.id;
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(ReadRequiredText(reader, 0));
        }

        return ids;
    }

    private static IReadOnlyList<string> ReadRecurringLeaseIds(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT lease_id FROM recurring_consent_leases ORDER BY lease_id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(ReadRequiredText(reader, 0));
        }

        return ids;
    }

    private static bool HasActiveRecurringRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM recurring_lease_uses u
                INNER JOIN recording_runs r
                    ON r.id = u.run_id AND r.occurrence_id = u.occurrence_id
                WHERE u.lease_id = $lease_id
                  AND r.status_code NOT IN ('settled', 'started_unknown', 'session_interrupted', 'failed'));
            """;
        Add(command, "$lease_id", leaseId);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static StandingLeaseSafetyChain LoadExecutionChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId,
        bool requireScope = true)
    {
        var lease = LoadLease(connection, transaction, leaseId);
        var plan = LoadPlan(connection, transaction, lease.PlanId);
        var occurrence = LoadOccurrence(connection, transaction, lease.OccurrenceId);
        var scope = SqliteAuthorizedCaptureScopeRepository.TryReadByIdentity(
            connection,
            transaction,
            plan.Id,
            occurrence.Id,
            lease.Id);
        if ((requireScope && scope is null) ||
            (scope is not null &&
             (!string.Equals(scope.PlanId, plan.Id, StringComparison.Ordinal) ||
              !string.Equals(scope.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
              !string.Equals(scope.LeaseId, lease.Id, StringComparison.Ordinal))) ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The standing lease safety chain is inconsistent.");
        }

        var runs = LoadRunsByOccurrence(connection, transaction, occurrence.Id);
        return new StandingLeaseSafetyChain(null, plan, occurrence, lease, scope, runs);
    }

    private static PlanDefinition LoadPlan(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {PlanColumns} FROM plans WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistedSnapshotException("The standing lease safety plan was not found.");
        }

        return ReadPlanDefinitionSnapshot(reader);
    }

    private static PlanOccurrence LoadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistedSnapshotException("The standing lease safety occurrence was not found.");
        }

        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static ConsentLease LoadLease(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {LeaseColumns} FROM consent_leases WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistedSnapshotException("The standing lease safety lease was not found.");
        }

        return ReadConsentLeaseSnapshot(reader);
    }

    private static LeaseUse? LoadUse(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLeaseUseSnapshot(reader) : null;
    }

    private static bool HasAnyActiveRuns(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recording_runs WHERE status_code NOT IN ('settled', 'started_unknown', 'session_interrupted', 'failed'));";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId)
    {
        using var command = Select(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;", ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var runs = new List<RecordingRun>();
        while (reader.Read())
        {
            runs.Add(ReadRecordingRunSnapshot(reader));
        }

        return runs;
    }

    private static RecordingRun? LoadRun(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecordingRunSnapshot(reader) : null;
    }

    private static StandingLeaseSafetyOperationRecord? ReadOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, operation_kind_code, intent_id, lease_id,
                   requested_at_utc, reason_code, result_code, changed,
                   requires_active_run_stop, completed_at_utc
            FROM standing_lease_safety_operations
            WHERE operation_id = $operation_id;
            """;
        Add(command, "$operation_id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var persistedId = ReadRequiredText(reader, 0);
        var operationKind = ReadRequiredText(reader, 1);
        var intentId = ReadNullableText(reader, 2);
        var leaseId = ReadNullableText(reader, 3);
        var requestedAtUtc = ReadUtcDateTimeOffset(reader, 4);
        var reasonCode = ReadRequiredText(reader, 5);
        var resultCode = ReadRequiredText(reader, 6);
        var changed = ReadBoolean(reader, 7);
        var requiresActiveRunStop = ReadBoolean(reader, 8);
        var completedAtUtc = ReadUtcDateTimeOffset(reader, 9);

        if (!string.Equals(persistedId, operationId, StringComparison.Ordinal) ||
            !IsCanonicalSafetyValue(persistedId) ||
            !IsRecognizedOperationKind(operationKind) ||
            (intentId is not null && !IsCanonicalSafetyValue(intentId)) ||
            (leaseId is not null && !IsCanonicalSafetyValue(leaseId)) ||
            !IsCanonicalSafetyValue(reasonCode) ||
            !IsCanonicalSafetyResult(resultCode) ||
            completedAtUtc < requestedAtUtc ||
            (resultCode.StartsWith("rejected:", StringComparison.Ordinal) && changed))
        {
            throw new PersistedSnapshotException("The safety operation identity is invalid.");
        }

        if (!IsValidOperationShape(
                operationKind,
                intentId,
                leaseId,
                resultCode,
                changed,
                requiresActiveRunStop))
        {
            throw new PersistedSnapshotException("The safety operation shape is invalid.");
        }

        return new StandingLeaseSafetyOperationRecord(
            persistedId,
            operationKind,
            intentId,
            leaseId,
            requestedAtUtc,
            reasonCode,
            resultCode,
            changed,
            requiresActiveRunStop,
            completedAtUtc);
    }

    private static bool IsRecognizedOperationKind(string value) =>
        value is StandingLeaseSafetyOperationKindCodes.LeaseRevoke
            or StandingLeaseSafetyOperationKindCodes.StopAllRevokeAll
            or StandingLeaseSafetyOperationKindCodes.UnattendedEnable
            or StandingLeaseSafetyOperationKindCodes.UnattendedDisable;

    private static bool IsCanonicalSafetyValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= 128 &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.Contains('\n') &&
        !value.Contains('\r');

    private static bool IsCanonicalSafetyResult(string value) =>
        IsCanonicalSafetyValue(value) &&
        (!value.StartsWith("rejected:", StringComparison.Ordinal) ||
         IsCanonicalSafetyValue(value["rejected:".Length..]));

    private static bool IsValidOperationShape(
        string operationKind,
        string? intentId,
        string? leaseId,
        string resultCode,
        bool changed,
        bool requiresActiveRunStop)
    {
        var isRejected = resultCode.StartsWith("rejected:", StringComparison.Ordinal);
        return operationKind switch
        {
            StandingLeaseSafetyOperationKindCodes.LeaseRevoke =>
                // Standing revoke rows have an intent identity (with or
                // bound lease for a committed decision, or no lease for an
                // unbound/not-found rejection); recurring revoke rows have
                // only the exact lease identity. A row with neither target is
                // never a writer output.
                ((intentId is not null && (leaseId is not null || isRejected)) ||
                 (intentId is null && leaseId is not null)) &&
                !(intentId is not null && leaseId is null && requiresActiveRunStop) &&
                ((isRejected && !changed) ||
                 (!isRejected && resultCode == StandingLeaseSafetyReasonCodes.LeaseRevoked && changed) ||
                 (!isRejected && resultCode == StandingLeaseSafetyReasonCodes.LeaseAlreadyRevoked && !changed)),

            StandingLeaseSafetyOperationKindCodes.StopAllRevokeAll =>
                intentId is null &&
                leaseId is null &&
                !isRejected &&
                resultCode == StandingLeaseSafetyReasonCodes.StopAllApplied &&
                changed,

            StandingLeaseSafetyOperationKindCodes.UnattendedEnable =>
                intentId is null &&
                leaseId is null &&
                !requiresActiveRunStop &&
                ((resultCode == StandingLeaseSafetyReasonCodes.UnattendedEnabled && changed) ||
                 (resultCode == StandingLeaseSafetyReasonCodes.UnattendedAlreadyEnabled && !changed)),

            StandingLeaseSafetyOperationKindCodes.UnattendedDisable =>
                intentId is null &&
                leaseId is null &&
                !isRejected &&
                resultCode == StandingLeaseSafetyReasonCodes.UnattendedDisabled,

            _ => false,
        };
    }

    private static void InsertOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string operationKindCode,
        string? intentId,
        string? leaseId,
        DateTimeOffset requestedAtUtc,
        string reasonRequestCode,
        string resultCode,
        bool changed,
        bool requiresActiveRunStop,
        DateTimeOffset completedAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO standing_lease_safety_operations
                (operation_id, operation_kind_code, intent_id, lease_id,
                 requested_at_utc, reason_code, result_code, changed,
                 requires_active_run_stop, completed_at_utc)
            VALUES
                ($operation_id, $operation_kind_code, $intent_id, $lease_id,
                 $requested_at_utc, $reason_code, $result_code, $changed,
                 $requires_active_run_stop, $completed_at_utc);
            """;
        Add(command, "$operation_id", operationId);
        Add(command, "$operation_kind_code", operationKindCode);
        Add(command, "$intent_id", intentId);
        Add(command, "$lease_id", leaseId);
        Add(command, "$requested_at_utc", UtcTicksInput(requestedAtUtc));
        Add(command, "$reason_code", reasonRequestCode);
        Add(command, "$result_code", resultCode == "" ? "rejected" : resultCode);
        Add(command, "$changed", changed ? 1L : 0L);
        Add(command, "$requires_active_run_stop", requiresActiveRunStop ? 1L : 0L);
        Add(command, "$completed_at_utc", UtcTicksInput(completedAtUtc));
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    private static void UpdateGlobalState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteStandingLeaseSafetyGlobalState expected,
        SqliteStandingLeaseSafetyGlobalState updated)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE unattended_safety_state
            SET unattended_mode_code = $unattended_mode_code,
                mode_changed_at_utc = $mode_changed_at_utc,
                unattended_enabled_at_utc = $unattended_enabled_at_utc,
                stop_all_applied = $stop_all_applied,
                stop_all_operation_id = $stop_all_operation_id,
                stop_all_reason_code = $stop_all_reason_code,
                stop_all_requested_at_utc = $stop_all_requested_at_utc,
                stop_all_applied_at_utc = $stop_all_applied_at_utc,
                version = $new_version
            WHERE state_id = 'global' AND version = $expected_version;
            """;
        Add(command, "$unattended_mode_code", UnattendedModeStatusCodes.ToCode(updated.UnattendedMode));
        Add(command, "$mode_changed_at_utc", UtcTicksInput(updated.ModeChangedAtUtc));
        Add(command, "$unattended_enabled_at_utc", updated.UnattendedEnabledAtUtc is null ? null : UtcTicksInput(updated.UnattendedEnabledAtUtc.Value));
        Add(command, "$stop_all_applied", updated.StopAllApplied ? 1L : 0L);
        Add(command, "$stop_all_operation_id", updated.StopAllOperationId);
        Add(command, "$stop_all_reason_code", updated.StopAllReasonCode);
        Add(command, "$stop_all_requested_at_utc", updated.StopAllRequestedAtUtc is null ? null : UtcTicksInput(updated.StopAllRequestedAtUtc.Value));
        Add(command, "$stop_all_applied_at_utc", updated.StopAllAppliedAtUtc is null ? null : UtcTicksInput(updated.StopAllAppliedAtUtc.Value));
        Add(command, "$new_version", updated.Version);
        Add(command, "$expected_version", expected.Version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException("safety_concurrency_conflict", "The global unattended safety state changed before it could be updated.");
        }
    }

    private static void UpdateLease(SqliteConnection connection, SqliteTransaction transaction, ConsentLease lease, long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE consent_leases SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND occurrence_id = $occurrence_id AND valid_from_utc = $valid_from_utc AND valid_until_utc = $valid_until_utc AND max_uses = $max_uses AND max_duration_ms = $max_duration_ms;";
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$id", lease.Id);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", lease.PlanId);
        Add(command, "$occurrence_id", lease.OccurrenceId);
        Add(command, "$valid_from_utc", UtcTicksInput(lease.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(lease.ValidUntilUtc));
        Add(command, "$max_uses", lease.MaxUses);
        Add(command, "$max_duration_ms", DurationMillisecondsInput(lease.MaxDuration));
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    private static void UpdateRecurringLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        long expectedVersion,
        ConsentLeaseStatus expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_consent_leases
            SET status_code = $status_code,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE lease_id = $lease_id
              AND plan_id = $plan_id
              AND schedule_revision = $schedule_revision
              AND schedule_digest = $schedule_digest
              AND time_zone_rules_digest = $time_zone_rules_digest
              AND profile_id = $profile_id
              AND profile_version = $profile_version
              AND profile_digest = $profile_digest
              AND configuration_digest = $configuration_digest
              AND valid_from_utc = $valid_from_utc
              AND valid_until_utc = $valid_until_utc
              AND authorized_plan_latest_end_utc = $authorized_plan_latest_end_utc
              AND per_run_duration_ticks = $per_run_duration_ticks
              AND max_uses = $max_uses
              AND max_cumulative_duration_ticks = $max_cumulative_duration_ticks
              AND authorization_digest = $authorization_digest
              AND created_at_utc = $created_at_utc
              AND status_code = $expected_status_code
              AND version = $expected_version;
            """;
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$lease_id", lease.LeaseId);
        Add(command, "$plan_id", lease.PlanId);
        Add(command, "$schedule_revision", lease.ConfigurationRef.ScheduleRevision);
        Add(command, "$schedule_digest", lease.ConfigurationRef.ScheduleDigest);
        Add(command, "$time_zone_rules_digest", lease.ConfigurationRef.TimeZoneRulesDigest);
        Add(command, "$profile_id", lease.ConfigurationRef.ProfileRef.ProfileId);
        Add(command, "$profile_version", lease.ConfigurationRef.ProfileRef.ProfileVersion);
        Add(command, "$profile_digest", lease.ConfigurationRef.ProfileRef.ProfileDigest);
        Add(command, "$configuration_digest", lease.ConfigurationRef.ConfigurationDigest);
        Add(command, "$valid_from_utc", UtcTicksInput(lease.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(lease.ValidUntilUtc));
        Add(command, "$authorized_plan_latest_end_utc", UtcTicksInput(lease.AuthorizedPlanLatestEndUtc));
        Add(command, "$per_run_duration_ticks", lease.PerRunDuration.Ticks);
        Add(command, "$max_uses", lease.MaxUses);
        Add(command, "$max_cumulative_duration_ticks", lease.MaxCumulativeDuration.Ticks);
        Add(command, "$authorization_digest", lease.AuthorizationDigest);
        Add(command, "$created_at_utc", UtcTicksInput(lease.CreatedAtUtc));
        Add(command, "$expected_status_code", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    private static void UpdateOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion,
        PlanOccurrenceStatus occurrenceStatusBeforeTransition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc AND run_id IS NULL AND terminal_reason_code IS NULL AND status_code = $expected_status_code;";
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$terminal_reason_code", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", occurrence.Id);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", occurrence.PlanId);
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        Add(command, "$expected_status_code", Phase3StateCodes.ToCode(occurrenceStatusBeforeTransition));
        var affected = command.ExecuteNonQuery();
        if (affected != 1)
        {
            throw new Phase3PersistenceException("safety_occurrence_concurrency_conflict", "The occurrence changed before it could be blocked.");
        }
    }

    private static SqliteCommand Select(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, string Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        return command;
    }

    private sealed record StandingLeaseSafetyChain(
        SqliteStandingLeaseAuthorizationActivationTransaction.PreparedIntentBinding? Intent,
        PlanDefinition Plan,
        PlanOccurrence Occurrence,
        ConsentLease Lease,
        AuthorizedFixedRegionScope? Scope,
        IReadOnlyList<RecordingRun> Runs);

    private sealed record StandingLeaseSafetyOperationRecord(
        string OperationId,
        string OperationKindCode,
        string? IntentId,
        string? LeaseId,
        DateTimeOffset RequestedAtUtc,
        string ReasonRequestCode,
        string ResultCode,
        bool Changed,
        bool RequiresActiveRunStop,
        DateTimeOffset CompletedAtUtc);
}
