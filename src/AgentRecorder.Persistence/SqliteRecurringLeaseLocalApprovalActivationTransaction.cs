using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed class SqliteRecurringLeaseLocalApprovalActivationTransaction : SqliteRepositoryBase
{
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;
    private readonly Action<RecurringLeaseLocalApprovalActivationFailurePoint>? _failureHookForTest;

    internal SqliteRecurringLeaseLocalApprovalActivationTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringLeaseLocalApprovalActivationFailurePoint>? failureHookForTest = null)
        : base(store)
    {
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _failureHookForTest = failureHookForTest;
    }

    internal RecurringLeaseLocalApprovalActivationResult Activate(
        RecurringLeaseLocalApprovalActivationRequest request,
        DateTimeOffset trustedNowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            ValidateTrustedNow(trustedNowUtc);
            _beforeWritesForTest?.Invoke(connection, transaction);

            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, request.LeaseId)
                ?? throw Failure("not_found", "The recurring lease was not found.");
            var plan = ReadPlan(connection, transaction, lease.PlanId)
                ?? throw Failure("not_found", "The recurring plan was not found.");
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                    connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
                ?? throw Failure("activation_snapshot_invalid", "The exact recurring schedule revision was not found.");
            var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(connection, transaction, lease.PlanId)
                ?? throw Failure("activation_snapshot_invalid", "The exact recurring profile binding was not found.");
            var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                connection, transaction, lease.ConfigurationRef.ProfileRef);

            ValidateExactConfiguration(lease, plan, schedule, binding, profile);
            ValidateAuthorizationBound(lease, schedule);
            var persistedEvidence = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(
                connection, transaction, request.LeaseId);
            var receipt = ValidateReceiptShapeAndIdentity(request, lease, plan);

            if (persistedEvidence is not null)
            {
                if (plan.Status == PlanDefinitionStatus.Enabled && lease.Status == ConsentLeaseStatus.Active &&
                    persistedEvidence.Matches(receipt))
                {
                    transaction.Commit();
                    return RecurringLeaseLocalApprovalActivationResult.AlreadyActive(request, plan.Id);
                }

                if (!persistedEvidence.Matches(receipt))
                {
                    transaction.Commit();
                    return RecurringLeaseLocalApprovalActivationResult.Conflict(request, "activation_receipt_conflict");
                }

                throw Failure("activation_snapshot_invalid", "Approval evidence and aggregate lifecycle state are partially committed.");
            }

            ValidateInitialReceiptWindow(receipt, lease, plan, trustedNowUtc);
            var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
            ValidateSafetyBoundary(safety, receipt);

            if (plan.Status != PlanDefinitionStatus.Draft || lease.Status != ConsentLeaseStatus.Pending)
            {
                throw Failure("activation_snapshot_invalid", "Recurring approval activation requires draft and pending aggregates.");
            }

            if (plan.Version == long.MaxValue || lease.Version == long.MaxValue)
            {
                throw Failure("activation_snapshot_invalid", "The recurring activation aggregate version is exhausted.");
            }

            var planTransition = plan.TryTransition(PlanDefinitionStatus.Enabled, trustedNowUtc);
            if (!planTransition.Succeeded || !planTransition.Changed)
            {
                throw Failure("activation_snapshot_invalid", planTransition.ReasonCode);
            }

            var leaseTransition = lease.TryTransition(ConsentLeaseStatus.Active, trustedNowUtc);
            if (!leaseTransition.Succeeded || !leaseTransition.Changed)
            {
                throw Failure("activation_snapshot_invalid", leaseTransition.ReasonCode);
            }

            InsertEvidence(connection, transaction, receipt);
            _failureHookForTest?.Invoke(RecurringLeaseLocalApprovalActivationFailurePoint.AfterApprovalEvidenceInsert);
            UpdatePlan(connection, transaction, plan, expectedVersion: plan.Version - 1);
            _failureHookForTest?.Invoke(RecurringLeaseLocalApprovalActivationFailurePoint.AfterPlanUpdate);
            UpdateLease(connection, transaction, lease, expectedVersion: lease.Version - 1);
            _failureHookForTest?.Invoke(RecurringLeaseLocalApprovalActivationFailurePoint.AfterLeaseUpdate);
            // The legacy lease-bound activation path has no setup intent to
            // update. Keep the extended failure seam observable here so the
            // pre-existing rollback matrix remains compatible after the
            // intent-bound path adds its own intent-write boundary.
            _failureHookForTest?.Invoke(RecurringLeaseLocalApprovalActivationFailurePoint.AfterIntentUpdate);

            var finalPlan = ReadPlan(connection, transaction, plan.Id)
                ?? throw Failure("activation_snapshot_invalid", "The final recurring plan readback was missing.");
            var finalLease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, lease.LeaseId)
                ?? throw Failure("activation_snapshot_invalid", "The final recurring lease readback was missing.");
            var finalEvidence = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(connection, transaction, lease.LeaseId)
                ?? throw Failure("activation_snapshot_invalid", "The final approval evidence readback was missing.");
            if (finalPlan.Status != PlanDefinitionStatus.Enabled || finalLease.Status != ConsentLeaseStatus.Active ||
                !finalEvidence.Matches(receipt))
            {
                throw Failure("activation_snapshot_invalid", "The recurring activation final readback did not match the requested transition.");
            }

            _beforeCommitForTest?.Invoke(connection, transaction);
            _failureHookForTest?.Invoke(RecurringLeaseLocalApprovalActivationFailurePoint.BeforeCommitAfterFinalRead);
            transaction.Commit();
            return RecurringLeaseLocalApprovalActivationResult.Activated(request, plan.Id);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_constraint_violation", "The recurring approval activation violated a SQLite constraint.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_failure", "The recurring approval activation transaction failed.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw Failure("activation_sqlite_failure", "The recurring approval activation transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static void ValidateTrustedNow(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw Failure("activation_time_not_utc", "The trusted activation time must use UTC.");
        }
    }

    private static void ValidateExactConfiguration(
        RecurringConsentLease lease,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile)
    {
        if (plan.IsOneTime || !string.Equals(plan.Id, lease.PlanId, StringComparison.Ordinal) ||
            !string.Equals(schedule.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            schedule.ScheduleRevision != lease.ConfigurationRef.ScheduleRevision ||
            !string.Equals(schedule.ScheduleDigest, lease.ConfigurationRef.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(schedule.TimeZoneRulesDigest, lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            schedule.Schedule.RecordingDuration != profile.Duration ||
            !string.Equals(binding.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            !ProfileReferencesEqual(binding.ProfileRef, lease.ConfigurationRef.ProfileRef) ||
            !lease.ConfigurationRef.ProfileRef.Matches(profile))
        {
            throw Failure("activation_snapshot_invalid", "The recurring approval chain is not bound to the exact plan, schedule, profile, and configuration digest.");
        }

        var recomputed = new RecurringPlanConfigurationRef(
            lease.PlanId,
            schedule.ScheduleRevision,
            schedule.ScheduleDigest,
            schedule.TimeZoneRulesDigest,
            binding.ProfileRef);
        if (!string.Equals(recomputed.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal))
        {
            throw Failure("activation_snapshot_invalid", "The recurring configuration digest no longer matches its exact parents.");
        }
    }

    private static void ValidateAuthorizationBound(RecurringConsentLease lease, RecurringScheduleVersionSnapshot schedule)
    {
        DateTimeOffset latestEnd;
        try
        {
            latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
                lease.PlanId, schedule.ScheduleRevision, schedule.Schedule);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure("activation_snapshot_invalid", exception.ReasonCode, exception);
        }

        if (latestEnd != lease.AuthorizedPlanLatestEndUtc)
        {
            throw Failure("activation_snapshot_invalid", "The recurring lease latest planned end is stale or tampered.");
        }
    }

    private static bool ProfileReferencesEqual(ProfileRef left, ProfileRef right) =>
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.ProfileVersion == right.ProfileVersion &&
        string.Equals(left.ProfileDigest, right.ProfileDigest, StringComparison.Ordinal);

    private static RecurringLeaseLocalApprovalReceipt ValidateReceiptShapeAndIdentity(
        RecurringLeaseLocalApprovalActivationRequest request,
        RecurringConsentLease lease,
        PlanDefinition plan)
    {
        var supplied = request.ApprovalReceipt ?? throw Failure("activation_receipt_missing", "The recurring approval receipt is required.");
        RecurringLeaseLocalApprovalReceipt receipt;
        try
        {
            receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                supplied.ApprovalId, supplied.LeaseId, supplied.PlanId,
                supplied.ConfigurationDigest, supplied.AuthorizationDigest,
                supplied.CurrentUserSid, supplied.SessionBinding, supplied.ApprovedAtUtc);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure("activation_receipt_invalid", "The recurring approval receipt is malformed.", exception);
        }

        if (receipt.ApprovalKind != supplied.ApprovalKind || receipt.ApprovalVersion != supplied.ApprovalVersion ||
            !string.Equals(receipt.ApprovalId, supplied.ApprovalId, StringComparison.Ordinal) ||
            !string.Equals(receipt.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(receipt.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(receipt.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal))
        {
            throw Failure("activation_receipt_invalid", "The recurring approval receipt is not bound to the exact lease authorization.");
        }

        return receipt;
    }

    private static void ValidateInitialReceiptWindow(
        RecurringLeaseLocalApprovalReceipt receipt,
        RecurringConsentLease lease,
        PlanDefinition plan,
        DateTimeOffset nowUtc)
    {
        if (receipt.ApprovedAtUtc < lease.CreatedAtUtc || receipt.ApprovedAtUtc > nowUtc ||
            receipt.ApprovedAtUtc >= lease.ValidUntilUtc || nowUtc >= lease.ValidUntilUtc ||
            receipt.ApprovedAtUtc < lease.UpdatedAtUtc || nowUtc < lease.UpdatedAtUtc ||
            receipt.ApprovedAtUtc < plan.UpdatedAtUtc || nowUtc < plan.UpdatedAtUtc)
        {
            throw Failure("activation_receipt_time_invalid", "The recurring approval receipt or trusted activation time is outside the exact lifecycle window.");
        }
    }

    private static void ValidateSafetyBoundary(SqliteStandingLeaseSafetyGlobalState safety, RecurringLeaseLocalApprovalReceipt receipt)
    {
        if (safety.UnattendedMode != UnattendedModeStatus.Enabled)
        {
            throw Failure("unattended_disabled", "Unattended safety must be enabled before recurring activation.");
        }

        if (safety.StopAllAppliedAtUtc is { } stopAllAt && receipt.ApprovedAtUtc <= stopAllAt)
        {
            throw Failure("stop_all_boundary", "The recurring approval predates the durable stop-all boundary.");
        }
    }

    private static PlanDefinition? ReadPlan(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PlanColumns} FROM plans WHERE id = $id LIMIT 1;";
        Add(command, "$id", planId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlanDefinitionSnapshot(reader) : null;
    }

    private static void InsertEvidence(SqliteConnection connection, SqliteTransaction transaction, RecurringLeaseLocalApprovalReceipt receipt)
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

    private static void UpdatePlan(SqliteConnection connection, SqliteTransaction transaction, PlanDefinition plan, long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plans SET status_code = $status, updated_at_utc = $updated, version = $version WHERE id = $id AND is_one_time = 0 AND created_at_utc = $created AND version = $expected;";
        Add(command, "$status", plan.StatusCode);
        Add(command, "$updated", UtcTicksInput(plan.UpdatedAtUtc));
        Add(command, "$version", plan.Version);
        Add(command, "$id", plan.Id);
        Add(command, "$created", UtcTicksInput(plan.CreatedAtUtc));
        Add(command, "$expected", expectedVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure("activation_concurrency_conflict", "The recurring plan changed during activation.");
        }
    }

    private static void UpdateLease(SqliteConnection connection, SqliteTransaction transaction, RecurringConsentLease lease, long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recurring_consent_leases SET status_code = $status, updated_at_utc = $updated, version = $version WHERE lease_id = $lease_id AND plan_id = $plan_id AND configuration_digest = $configuration_digest AND authorization_digest = $authorization_digest AND version = $expected;";
        Add(command, "$status", lease.StatusCode);
        Add(command, "$updated", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$version", lease.Version);
        Add(command, "$lease_id", lease.LeaseId);
        Add(command, "$plan_id", lease.PlanId);
        Add(command, "$configuration_digest", lease.ConfigurationRef.ConfigurationDigest);
        Add(command, "$authorization_digest", lease.AuthorizationDigest);
        Add(command, "$expected", expectedVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure("activation_concurrency_conflict", "The recurring lease changed during activation.");
        }
    }

    private static Phase3PersistenceException Failure(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try { transaction?.Rollback(); } catch { }
    }
}
