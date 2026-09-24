using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Atomically activates one recurring setup intent that already owns a fully
/// persisted preparation chain. This transaction never creates occurrences,
/// runs, lease uses, proof, tickets, scheduler state, or capture state.
/// </summary>
internal sealed class SqliteRecurringLeasePreparedIntentApprovalActivationTransaction : SqliteRepositoryBase
{
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeIntentWriteForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;
    private readonly Action<RecurringLeaseLocalApprovalActivationFailurePoint>? _failureHookForTest;

    internal SqliteRecurringLeasePreparedIntentApprovalActivationTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringLeaseLocalApprovalActivationFailurePoint>? failureHookForTest = null)
        : base(store)
    {
        _beforeWritesForTest = beforeWritesForTest;
        _beforeIntentWriteForTest = beforeIntentWriteForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _failureHookForTest = failureHookForTest;
    }

    internal RecurringLeasePreparedIntentApprovalActivationResult ActivatePreparedIntent(
        string intentId,
        RecurringLeaseLocalApprovalReceipt approvalReceipt,
        DateTimeOffset trustedNowUtc)
    {
        ArgumentNullException.ThrowIfNull(approvalReceipt);
        ValidateTrustedNow(trustedNowUtc);

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            _beforeWritesForTest?.Invoke(connection, transaction);

            var persisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(
                connection,
                transaction,
                intentId);
            if (persisted is null || persisted.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
            {
                transaction.Commit();
                return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(
                    intentId,
                    "activation_intent_not_found");
            }

            var status = ParseStatus(persisted.StatusCode);
            if (status == RecurringSetupIntentStatus.Activated)
            {
                var activatedReadback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
                    persisted,
                    connection,
                    transaction);
                var activatedChain = SqliteRecurringSetupPreparationTransaction.ValidateActivatedChainWithinTransaction(
                    connection,
                    transaction,
                    persisted,
                    activatedReadback.Snapshot);
                var activatedReceipt = ValidateReceiptShapeAndIdentity(
                    intentId,
                    approvalReceipt,
                    persisted,
                    activatedChain);
                var evidence = ReadExactlyOneEvidence(connection, transaction, activatedChain.Lease.LeaseId);
                if (evidence.Matches(activatedReceipt))
                {
                    transaction.Commit();
                    return RecurringLeasePreparedIntentApprovalActivationResult.AlreadyActive(
                        intentId,
                        activatedChain.Plan.Id,
                        activatedChain.Lease.LeaseId);
                }

                transaction.Commit();
                return RecurringLeasePreparedIntentApprovalActivationResult.Conflict(
                    intentId,
                    "activation_receipt_conflict",
                    activatedChain.Plan.Id,
                    activatedChain.Lease.LeaseId);
            }

            if (status != RecurringSetupIntentStatus.LeaseApprovalPending)
            {
                transaction.Commit();
                return RecurringLeasePreparedIntentApprovalActivationResult.Rejected(
                    intentId,
                    "activation_intent_state_invalid");
            }

            var readback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
                persisted,
                connection,
                transaction);
            var chain = SqliteRecurringSetupPreparationTransaction.ValidatePreparedChainWithinTransaction(
                connection,
                transaction,
                persisted,
                readback.Snapshot);

            if (SqliteRecurringLeaseLocalApprovalEvidenceReader.CountWithinTransaction(
                    connection,
                    transaction,
                    chain.Lease.LeaseId) != 0)
            {
                throw Failure("activation_intent_conflict", "A pending recurring intent already has approval evidence.");
            }

            var pendingReceipt = ValidateReceiptShapeAndIdentity(intentId, approvalReceipt, persisted, chain);
            ValidateInitialReceiptWindow(pendingReceipt, persisted, chain, trustedNowUtc);
            var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
            ValidateSafetyBoundary(safety, pendingReceipt);

            if (chain.Plan.Status != PlanDefinitionStatus.Draft || chain.Lease.Status != ConsentLeaseStatus.Pending)
                throw Failure("activation_snapshot_invalid", "The prepared recurring chain is not pending activation.");
            if (chain.Plan.Version == long.MaxValue || chain.Lease.Version == long.MaxValue || persisted.Version != 1)
                throw Failure("activation_version_exhausted", "The recurring activation version is exhausted.");

            var planTransition = chain.Plan.TryTransition(PlanDefinitionStatus.Enabled, trustedNowUtc);
            if (!planTransition.Succeeded || !planTransition.Changed)
                throw Failure("activation_snapshot_invalid", planTransition.ReasonCode);
            var leaseTransition = chain.Lease.TryTransition(ConsentLeaseStatus.Active, trustedNowUtc);
            if (!leaseTransition.Succeeded || !leaseTransition.Changed)
                throw Failure("activation_snapshot_invalid", leaseTransition.ReasonCode);

            InsertEvidence(connection, transaction, pendingReceipt);
            InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint.AfterApprovalEvidenceInsert);
            UpdatePlan(connection, transaction, chain.Plan, expectedVersion: chain.Plan.Version - 1);
            InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint.AfterPlanUpdate);
            UpdateLease(connection, transaction, chain.Lease, expectedVersion: chain.Lease.Version - 1);
            InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint.AfterLeaseUpdate);

            _beforeIntentWriteForTest?.Invoke(connection, transaction);
            UpdateSetupIntent(connection, transaction, persisted, trustedNowUtc);
            InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint.AfterIntentUpdate);

            var finalPersisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(
                    connection,
                    transaction,
                    intentId)
                ?? throw Failure("activation_snapshot_invalid", "The activated setup intent disappeared before final readback.");
            if (finalPersisted.StatusCode != RecurringSetupIntentCodes.ActivatedStatus ||
                finalPersisted.Version != 2 ||
                finalPersisted.TerminalReasonCode is not null ||
                finalPersisted.UpdatedAtUtc != trustedNowUtc)
            {
                throw Failure("activation_snapshot_invalid", "The final recurring setup intent readback did not match activation.");
            }

            var finalReadback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
                finalPersisted,
                connection,
                transaction);
            var finalChain = SqliteRecurringSetupPreparationTransaction.ValidateActivatedChainWithinTransaction(
                connection,
                transaction,
                finalPersisted,
                finalReadback.Snapshot);
            var finalEvidence = ReadExactlyOneEvidence(connection, transaction, finalChain.Lease.LeaseId);
            if (finalChain.Plan.Status != PlanDefinitionStatus.Enabled ||
                finalChain.Lease.Status != ConsentLeaseStatus.Active ||
                !finalEvidence.Matches(pendingReceipt))
            {
                throw Failure("activation_snapshot_invalid", "The final recurring activation chain is not exact.");
            }

            _beforeCommitForTest?.Invoke(connection, transaction);
            InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint.BeforeCommitAfterFinalRead);
            transaction.Commit();
            return RecurringLeasePreparedIntentApprovalActivationResult.Activated(
                intentId,
                finalChain.Plan.Id,
                finalChain.Lease.LeaseId);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            TryRollback(transaction);
            throw Failure("activation_snapshot_invalid", "The recurring activation snapshot is invalid.", exception);
        }
        catch (Phase3DomainException exception)
        {
            TryRollback(transaction);
            throw Failure("activation_snapshot_invalid", "The recurring activation domain state is invalid.", exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            TryRollback(transaction);
            throw Failure("activation_concurrency_conflict", "The recurring activation transaction was busy or locked.", exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_constraint_violation", "The recurring activation violated a SQLite constraint.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_failure", "The recurring activation transaction failed in SQLite.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_failure", "The recurring activation transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static RecurringSetupIntentStatus ParseStatus(string statusCode)
    {
        try
        {
            return RecurringSetupIntentStatusCodes.Parse(statusCode);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("The recurring setup intent status is invalid.", exception);
        }
    }

    private static RecurringLeaseLocalApprovalReceipt ValidateReceiptShapeAndIdentity(
        string intentId,
        RecurringLeaseLocalApprovalReceipt supplied,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringPreparedChainSnapshot chain)
    {
        if (supplied.ApprovedAtUtc.Offset != TimeSpan.Zero)
            throw Failure("activation_receipt_time_not_utc", "The approval receipt time must be UTC.");

        RecurringLeaseLocalApprovalReceipt receipt;
        try
        {
            receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                supplied.ApprovalId,
                supplied.LeaseId,
                supplied.PlanId,
                supplied.ConfigurationDigest,
                supplied.AuthorizationDigest,
                supplied.CurrentUserSid,
                supplied.SessionBinding,
                supplied.ApprovedAtUtc);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure("activation_receipt_invalid", "The recurring approval receipt is malformed.", exception);
        }

        if (receipt.ApprovalKind != supplied.ApprovalKind ||
            receipt.ApprovalVersion != supplied.ApprovalVersion ||
            !string.Equals(receipt.ApprovalId, supplied.ApprovalId, StringComparison.Ordinal) ||
            !string.Equals(receipt.LeaseId, chain.Lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlanId, chain.Plan.Id, StringComparison.Ordinal) ||
            !string.Equals(receipt.ConfigurationDigest, chain.Configuration.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.AuthorizationDigest, chain.Lease.AuthorizationDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.CurrentUserSid, persisted.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(receipt.SessionBinding, persisted.SessionBinding, StringComparison.Ordinal) ||
            !string.Equals(intentId, persisted.IntentId, StringComparison.Ordinal))
        {
            throw Failure("activation_receipt_identity_mismatch", "The recurring approval receipt is not bound to the exact setup intent chain.");
        }

        return receipt;
    }

    private static void ValidateInitialReceiptWindow(
        RecurringLeaseLocalApprovalReceipt receipt,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringPreparedChainSnapshot chain,
        DateTimeOffset trustedNowUtc)
    {
        if (receipt.ApprovedAtUtc < chain.Preparation.PreparedAtUtc ||
            receipt.ApprovedAtUtc > trustedNowUtc)
        {
            throw Failure(
                receipt.ApprovedAtUtc > trustedNowUtc ? "activation_receipt_time_in_future" : "activation_receipt_time_invalid",
                "The approval receipt is outside the preparation-to-now boundary.");
        }

        if (trustedNowUtc < persisted.UpdatedAtUtc || trustedNowUtc < chain.Plan.UpdatedAtUtc || trustedNowUtc < chain.Lease.UpdatedAtUtc)
            throw Failure("activation_time_non_monotonic", "The trusted activation time precedes the prepared lifecycle timestamps.");
        if (trustedNowUtc >= persisted.ExpiresAtUtc)
            throw Failure("activation_intent_expired", "The recurring setup intent has expired.");
        if (trustedNowUtc >= chain.Lease.ValidUntilUtc)
            throw Failure("activation_lease_expired", "The recurring Lease has expired.");
        if (receipt.ApprovedAtUtc >= persisted.ExpiresAtUtc || receipt.ApprovedAtUtc >= chain.Lease.ValidUntilUtc)
            throw Failure("activation_receipt_time_invalid", "The approval receipt is outside the authorization validity window.");
    }

    private static void ValidateSafetyBoundary(
        SqliteStandingLeaseSafetyGlobalState safety,
        RecurringLeaseLocalApprovalReceipt receipt)
    {
        if (safety.UnattendedMode != UnattendedModeStatus.Enabled)
            throw Failure("unattended_disabled", "Unattended safety must be enabled before recurring activation.");
        if (safety.StopAllAppliedAtUtc is { } stopAllAt && receipt.ApprovedAtUtc <= stopAllAt)
            throw Failure("stop_all_boundary", "The approval receipt predates the durable stop-all boundary.");
    }

    private static RecurringLeaseLocalApprovalEvidence ReadExactlyOneEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        if (SqliteRecurringLeaseLocalApprovalEvidenceReader.CountWithinTransaction(connection, transaction, leaseId) != 1)
            throw Failure("activation_snapshot_invalid", "The recurring Lease does not have exactly one approval evidence row.");
        return SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(connection, transaction, leaseId)
            ?? throw Failure("activation_snapshot_invalid", "The recurring Lease approval evidence is missing.");
    }

    private static void InsertEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseLocalApprovalReceipt receipt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurring_lease_local_approvals
                (approval_id, lease_id, plan_id, configuration_digest, authorization_digest,
                 current_user_sid, session_binding, approved_at_utc, approval_kind_code,
                 approval_version, approval_digest)
            VALUES ($approval_id, $lease_id, $plan_id, $configuration_digest, $authorization_digest,
                    $current_user_sid, $session_binding, $approved_at_utc, $approval_kind_code,
                    $approval_version, $approval_digest);
            """;
        Add(command, "$approval_id", receipt.ApprovalId);
        Add(command, "$lease_id", receipt.LeaseId);
        Add(command, "$plan_id", receipt.PlanId);
        Add(command, "$configuration_digest", receipt.ConfigurationDigest);
        Add(command, "$authorization_digest", receipt.AuthorizationDigest);
        Add(command, "$current_user_sid", receipt.CurrentUserSid);
        Add(command, "$session_binding", receipt.SessionBinding);
        Add(command, "$approved_at_utc", UtcTicksInput(receipt.ApprovedAtUtc));
        Add(command, "$approval_kind_code", receipt.ApprovalKind);
        Add(command, "$approval_version", receipt.ApprovalVersion);
        Add(command, "$approval_digest", receipt.ApprovalDigest);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    private static void UpdatePlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plans
            SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version
            WHERE id = $id AND is_one_time = 0 AND created_at_utc = $created_at_utc
              AND status_code = 'draft' AND version = $expected_version;
            """;
        Add(command, "$status_code", plan.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(plan.UpdatedAtUtc));
        Add(command, "$new_version", plan.Version);
        Add(command, "$id", plan.Id);
        Add(command, "$created_at_utc", UtcTicksInput(plan.CreatedAtUtc));
        Add(command, "$expected_version", expectedVersion);
        if (command.ExecuteNonQuery() != 1)
            throw Failure("activation_concurrency_conflict", "The recurring Plan changed during activation.");
    }

    private static void UpdateLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_consent_leases
            SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version
            WHERE lease_id = $lease_id AND plan_id = $plan_id AND status_code = 'pending'
              AND version = $expected_version AND schedule_revision = $schedule_revision
              AND schedule_digest = $schedule_digest AND time_zone_rules_digest = $time_zone_rules_digest
              AND profile_id = $profile_id AND profile_version = $profile_version AND profile_digest = $profile_digest
              AND configuration_digest = $configuration_digest AND valid_from_utc = $valid_from_utc
              AND valid_until_utc = $valid_until_utc AND authorized_plan_latest_end_utc = $authorized_plan_latest_end_utc
              AND per_run_duration_ticks = $per_run_duration_ticks AND max_uses = $max_uses
              AND max_cumulative_duration_ticks = $max_cumulative_duration_ticks
              AND authorization_digest = $authorization_digest AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$lease_id", lease.LeaseId);
        Add(command, "$plan_id", lease.PlanId);
        Add(command, "$expected_version", expectedVersion);
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
        if (command.ExecuteNonQuery() != 1)
            throw Failure("activation_concurrency_conflict", "The recurring Lease changed during activation.");
    }

    private static void UpdateSetupIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent intent,
        DateTimeOffset trustedNowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE setup_intents
            SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = 2
            WHERE intent_id = $intent_id
              AND intent_kind_code = $intent_kind_code
              AND status_code = $expected_status_code
              AND version = 1
              AND terminal_reason_code IS NULL;
            """;
        Add(command, "$status_code", RecurringSetupIntentCodes.ActivatedStatus);
        Add(command, "$updated_at_utc", UtcTicksInput(trustedNowUtc));
        Add(command, "$intent_id", intent.IntentId);
        Add(command, "$intent_kind_code", RecurringSetupIntentCodes.IntentKind);
        Add(command, "$expected_status_code", RecurringSetupIntentCodes.LeaseApprovalPendingStatus);
        if (command.ExecuteNonQuery() != 1)
        {
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT status_code, version FROM setup_intents WHERE intent_id = $intent_id;";
            Add(read, "$intent_id", intent.IntentId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                throw Failure("activation_snapshot_invalid", "The recurring setup intent disappeared during activation.");

            var status = ReadRequiredText(reader, 0);
            var version = ReadInt64(reader, 1);
            if (version != intent.Version)
                throw Failure("activation_concurrency_conflict", "The recurring setup intent changed during activation.");
            throw Failure(
                status == RecurringSetupIntentCodes.LeaseApprovalPendingStatus
                    ? "activation_immutable_mismatch"
                    : "activation_intent_conflict",
                "The recurring setup intent lifecycle or immutable state no longer matches activation.");
        }
    }

    private static void ValidateTrustedNow(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero || value.UtcDateTime.Ticks < 0)
            throw Failure("activation_time_not_utc", "The trusted activation time must be UTC and non-negative.");
    }

    private void InvokeFailure(RecurringLeaseLocalApprovalActivationFailurePoint point) => _failureHookForTest?.Invoke(point);

    private static Phase3PersistenceException Failure(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try { transaction?.Rollback(); } catch { }
    }
}
