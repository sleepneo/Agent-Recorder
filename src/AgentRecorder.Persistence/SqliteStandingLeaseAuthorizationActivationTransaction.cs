using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Atomically activates one already-persisted standing authorization chain.
/// The transaction has no execution, proof, scheduler, wake, or backend
/// dependency. It only moves the three existing durable aggregates after a
/// trusted local approval receipt and the persisted fixed-region scope agree.
/// </summary>
internal sealed class SqliteStandingLeaseAuthorizationActivationTransaction : SqliteRepositoryBase
{
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeIntentWriteForTest;

    internal SqliteStandingLeaseAuthorizationActivationTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeIntentWriteForTest = null)
        : base(store)
    {
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _beforeIntentWriteForTest = beforeIntentWriteForTest;
    }

    internal StandingLeaseAuthorizationActivationResult Activate(
        StandingLeaseAuthorizationActivationRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            return ActivateInTransaction(connection, transaction, request, nowUtc, preparedIntent: null);
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw SnapshotInvalid(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw SnapshotInvalid(exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException(
                "activation_constraint_violation",
                "The standing authorization activation violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(
                "activation_sqlite_failure",
                "The standing authorization activation failed in SQLite.",
                exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(
                "activation_sqlite_failure",
                "The standing authorization activation failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseAuthorizationActivationResult ActivatePreparedIntent(
        string intentId,
        StandingLeaseLocalApprovalReceipt approvalReceipt,
        DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        StandingLeaseAuthorizationActivationRequest? request = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var preparedIntent = ReadPreparedIntent(connection, transaction, intentId);
            if (preparedIntent is null)
            {
                return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_intent_not_found");
            }

            if (preparedIntent.Status is not (StandingSetupIntentStatus.LeaseApprovalPending or StandingSetupIntentStatus.Activated))
            {
                return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_intent_state_invalid");
            }

            if (preparedIntent.PlanId is null || preparedIntent.OccurrenceId is null ||
                preparedIntent.LeaseId is null || preparedIntent.ScopeId is null)
            {
                return StandingLeaseAuthorizationActivationResult.Conflict(null, "activation_intent_conflict");
            }

            if (preparedIntent.Status == StandingSetupIntentStatus.LeaseApprovalPending && nowUtc >= preparedIntent.ExpiresAtUtc)
            {
                return StandingLeaseAuthorizationActivationResult.Rejected(null, "activation_intent_expired");
            }

            var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(
                connection,
                transaction,
                preparedIntent.ScopeId);
            if (scope is null)
            {
                return StandingLeaseAuthorizationActivationResult.Conflict(null, "activation_intent_conflict");
            }

            if (!string.Equals(preparedIntent.CurrentUserSid, scope.CurrentUserSid, StringComparison.Ordinal) ||
                !string.Equals(preparedIntent.SessionBinding, scope.SessionBinding, StringComparison.Ordinal))
            {
                return StandingLeaseAuthorizationActivationResult.Conflict(null, "activation_intent_conflict");
            }

            request = new StandingLeaseAuthorizationActivationRequest(
                preparedIntent.PlanId,
                preparedIntent.OccurrenceId,
                preparedIntent.LeaseId,
                preparedIntent.ScopeId,
                scope.ScopeDigest,
                approvalReceipt);
            return ActivateInTransaction(connection, transaction, request, nowUtc, preparedIntent);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapPreparedFailure(request, exception.Code);
        }
        catch (PersistedSnapshotException)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_snapshot_invalid");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_constraint_violation");
        }
        catch (SqliteException)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_failure");
        }
        catch (Exception)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_failure");
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private StandingLeaseAuthorizationActivationResult ActivateInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandingLeaseAuthorizationActivationRequest request,
        DateTimeOffset nowUtc,
        PreparedIntentBinding? preparedIntent)
    {
        var receipt = request.ApprovalReceipt;
        if (receipt is null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_receipt_missing");
        }

        var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(
            connection,
            transaction,
            request.ScopeId);
        if (scope is null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_scope_not_found");
        }

        if (preparedIntent is not null && HasPreparedChainIdentityConflict(connection, transaction, preparedIntent, scope))
        {
            return StandingLeaseAuthorizationActivationResult.Conflict(request, "activation_intent_conflict");
        }

        if (!MatchesRequestIdentity(request, scope))
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_scope_identity_mismatch");
        }

        var related = SqliteAuthorizedCaptureScopeRepository.LoadAndValidateRelatedForStartGate(
            connection,
            transaction,
            scope);
        var runs = LoadRunsByOccurrence(connection, transaction, related.Occurrence.Id);
        var uses = LoadUsesByChain(connection, transaction, related.Occurrence.Id, related.Lease.Id);

        if (related.Occurrence.RunId is not null || runs.Count != 0 || uses.Count != 0)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_execution_evidence_present");
        }

        var receiptFailure = ValidateReceipt(receipt, scope, request);
        if (receiptFailure is not null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, receiptFailure);
        }

        var isAlreadyActive = related.Plan.Status == PlanDefinitionStatus.Enabled &&
            related.Occurrence.Status == PlanOccurrenceStatus.Authorized &&
            related.Lease.Status == ConsentLeaseStatus.Active;

        if (preparedIntent is not null &&
            ((preparedIntent.Status == StandingSetupIntentStatus.Activated && !isAlreadyActive) ||
             (preparedIntent.Status == StandingSetupIntentStatus.LeaseApprovalPending && isAlreadyActive)))
        {
            return StandingLeaseAuthorizationActivationResult.Conflict(request, "activation_intent_conflict");
        }

        var timeFailure = ValidateApprovalTime(
            receipt,
            scope,
            related.Plan,
            related.Occurrence,
            related.Lease,
            nowUtc,
            requireLeaseLive: !isAlreadyActive);
        if (timeFailure is not null)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, timeFailure);
        }

        if (isAlreadyActive)
        {
            return StandingLeaseAuthorizationActivationResult.AlreadyActive(request);
        }

        var isPendingActivation = related.Plan.Status == PlanDefinitionStatus.Draft &&
            related.Occurrence.Status == PlanOccurrenceStatus.PendingLeaseApproval &&
            related.Lease.Status == ConsentLeaseStatus.Pending;
        if (!isPendingActivation)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_state_inconsistent");
        }

        var safetyState = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(
            connection,
            transaction);
        if (safetyState.UnattendedMode == UnattendedModeStatus.Disabled)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(
                request,
                StandingLeaseSafetyReasonCodes.UnattendedDisabled);
        }

        if (related.Plan.Version == long.MaxValue ||
            related.Occurrence.Version == long.MaxValue ||
            related.Lease.Version == long.MaxValue)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_version_exhausted");
        }

        if (!related.Plan.IsOneTime)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_plan_not_one_time");
        }

        var planTransition = related.Plan.TryTransition(PlanDefinitionStatus.Enabled, receipt.ApprovedAtUtc);
        if (!planTransition.Succeeded || !planTransition.Changed)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, planTransition.ReasonCode);
        }

        var occurrenceTransition = related.Occurrence.TryTransition(PlanOccurrenceStatus.Authorized, receipt.ApprovedAtUtc);
        if (!occurrenceTransition.Succeeded || !occurrenceTransition.Changed)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, occurrenceTransition.ReasonCode);
        }

        var leaseTransition = related.Lease.TryTransition(ConsentLeaseStatus.Active, receipt.ApprovedAtUtc);
        if (!leaseTransition.Succeeded || !leaseTransition.Changed)
        {
            return StandingLeaseAuthorizationActivationResult.Rejected(request, leaseTransition.ReasonCode);
        }

        _beforeWritesForTest?.Invoke(connection, transaction);
        UpdatePlan(connection, transaction, related.Plan, expectedVersion: related.Plan.Version - 1);
        UpdateOccurrence(connection, transaction, related.Occurrence, expectedVersion: related.Occurrence.Version - 1);
        UpdateLease(connection, transaction, related.Lease, expectedVersion: related.Lease.Version - 1);

        if (preparedIntent is not null)
        {
            _beforeIntentWriteForTest?.Invoke(connection, transaction);
            UpdatePreparedIntent(connection, transaction, preparedIntent, receipt.ApprovedAtUtc);
        }

        _beforeCommitForTest?.Invoke(connection, transaction);
        transaction.Commit();
        return StandingLeaseAuthorizationActivationResult.Activated(request);
    }

    internal static PreparedIntentBinding? ReadPreparedIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, intent_kind_code, status_code, expires_at_utc,
                   plan_id, occurrence_id, lease_id, scope_id,
                   current_user_sid, session_binding, version, terminal_reason_code,
                   request_digest, idempotency_key, requested_at_utc,
                   scheduled_start_utc, latest_start_utc, planned_end_utc,
                   maximum_duration_ms, lease_valid_until_utc,
                   output_directory, frozen_file_name
            FROM setup_intents
            WHERE intent_id = $intent_id;
            """;
        Add(command, "$intent_id", intentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var persistedIntentId = ReadRequiredText(reader, 0);
        if (!IsCanonicalId(persistedIntentId) || !string.Equals(persistedIntentId, intentId, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The setup intent identity is not canonical or does not match the lookup.");
        }

        if (!string.Equals(ReadRequiredText(reader, 1), StandingSetupIntentCodes.IntentKind, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The setup intent kind is not supported.");
        }

        var status = StandingSetupIntentStatusCodes.Parse(ReadRequiredText(reader, 2));
        var expiresAtUtc = ReadUtcDateTimeOffset(reader, 3);
        var planId = ReadNullableText(reader, 4);
        var occurrenceId = ReadNullableText(reader, 5);
        var leaseId = ReadNullableText(reader, 6);
        var scopeId = ReadNullableText(reader, 7);
        var currentUserSid = ReadRequiredText(reader, 8);
        var sessionBinding = ReadRequiredText(reader, 9);
        var version = ReadInt64(reader, 10);
        var terminalReasonCode = ReadNullableText(reader, 11);
        var requestDigest = ReadRequiredText(reader, 12);
        var idempotencyKey = ReadRequiredText(reader, 13);
        var requestedAtUtc = ReadUtcDateTimeOffset(reader, 14);
        var scheduledStartUtc = ReadUtcDateTimeOffset(reader, 15);
        var latestStartUtc = ReadUtcDateTimeOffset(reader, 16);
        var plannedEndUtc = ReadUtcDateTimeOffset(reader, 17);
        var maximumDuration = ReadDurationMilliseconds(reader, 18);
        var leaseValidUntilUtc = ReadUtcDateTimeOffset(reader, 19);
        var outputDirectory = ReadRequiredText(reader, 20);
        var frozenFileName = ReadRequiredText(reader, 21);

        foreach (var value in new[]
        {
            requestedAtUtc, expiresAtUtc, scheduledStartUtc, latestStartUtc,
            plannedEndUtc, leaseValidUntilUtc,
        })
        {
            ValidateUtcValue(value, "The setup intent contains an invalid UTC value.");
        }

        if (!IsCanonicalText(currentUserSid) || !IsCanonicalText(sessionBinding) ||
            !IsCanonicalDigest(requestDigest) || version < 0)
        {
            throw new PersistedSnapshotException("The setup intent immutable binding is invalid.");
        }

        try
        {
            var snapshot = StandingSetupIntentSnapshot.CreateForPersistence(
                persistedIntentId,
                idempotencyKey,
                currentUserSid,
                sessionBinding,
                requestedAtUtc,
                expiresAtUtc,
                scheduledStartUtc,
                latestStartUtc,
                plannedEndUtc,
                maximumDuration,
                leaseValidUntilUtc,
                outputDirectory,
                frozenFileName,
                requestDigest);
            SqliteStandingSetupIntentCreateOrGetTransaction.ValidateSnapshotForPreparation(snapshot);
        }
        catch (Phase3PersistenceException exception)
        {
            throw new PersistedSnapshotException("The persisted setup intent request is invalid.", exception);
        }

        foreach (var association in new[] { planId, occurrenceId, leaseId, scopeId })
        {
            if (association is not null && !IsCanonicalId(association))
            {
                throw new PersistedSnapshotException("The setup intent association identity is invalid.");
            }
        }

        if (status is StandingSetupIntentStatus.RegionSelectionPending or StandingSetupIntentStatus.LeaseApprovalPending or StandingSetupIntentStatus.Activated)
        {
            if (terminalReasonCode is not null)
            {
                throw new PersistedSnapshotException("A non-terminal setup intent contains a terminal reason.");
            }
        }
        else if (status is StandingSetupIntentStatus.Rejected or StandingSetupIntentStatus.Expired)
        {
            if (string.IsNullOrWhiteSpace(terminalReasonCode))
            {
                throw new PersistedSnapshotException("A terminal setup intent has no terminal reason.");
            }
        }

        return new PreparedIntentBinding(
            persistedIntentId,
            status,
            expiresAtUtc,
            requestedAtUtc,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            maximumDuration,
            leaseValidUntilUtc,
            terminalReasonCode,
            planId,
            occurrenceId,
            leaseId,
            scopeId,
            currentUserSid,
            sessionBinding,
            version);
    }

    private static void UpdatePreparedIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedIntentBinding intent,
        DateTimeOffset approvedAtUtc)
    {
        if (intent.Version == long.MaxValue)
        {
            throw new Phase3PersistenceException("activation_intent_version_exhausted", "The setup intent version cannot be incremented.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE setup_intents
            SET status_code = $status_code,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE intent_id = $intent_id
              AND status_code = $expected_status_code
              AND version = $expected_version
              AND plan_id = $plan_id
              AND occurrence_id = $occurrence_id
              AND lease_id = $lease_id
              AND scope_id = $scope_id
              AND terminal_reason_code IS NULL;
            """;
        Add(command, "$status_code", StandingSetupIntentCodes.ActivatedStatus);
        Add(command, "$updated_at_utc", UtcTicksInput(approvedAtUtc));
        Add(command, "$new_version", intent.Version + 1);
        Add(command, "$intent_id", intent.IntentId);
        Add(command, "$expected_status_code", StandingSetupIntentCodes.LeaseApprovalPendingStatus);
        Add(command, "$expected_version", intent.Version);
        Add(command, "$plan_id", intent.PlanId);
        Add(command, "$occurrence_id", intent.OccurrenceId);
        Add(command, "$lease_id", intent.LeaseId);
        Add(command, "$scope_id", intent.ScopeId);
        if (command.ExecuteNonQuery() != 1)
        {
            EnsureIntentUpdateSucceeded(connection, transaction, intent);
        }
    }

    private static void EnsureIntentUpdateSucceeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedIntentBinding intent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status_code, version FROM setup_intents WHERE intent_id = $intent_id;";
        Add(command, "$intent_id", intent.IntentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SnapshotInvalid(new PersistedSnapshotException("The setup intent disappeared during activation."));
        }

        var status = ReadRequiredText(reader, 0);
        var version = ReadInt64(reader, 1);
        if (version != intent.Version)
        {
            throw new Phase3PersistenceException("activation_concurrency_conflict", "The setup intent changed before activation could commit.");
        }

        throw new Phase3PersistenceException(
            string.Equals(status, StandingSetupIntentCodes.LeaseApprovalPendingStatus, StringComparison.Ordinal)
                ? "activation_immutable_mismatch"
                : "activation_intent_conflict",
            "The setup intent immutable binding or expected state no longer matches.");
    }

    private static StandingLeaseAuthorizationActivationResult MapPreparedFailure(
        StandingLeaseAuthorizationActivationRequest? request,
        string code) => code switch
        {
            "activation_concurrency_conflict" or "activation_immutable_mismatch" or "activation_intent_conflict" =>
                StandingLeaseAuthorizationActivationResult.Conflict(request, code),
            "activation_constraint_violation" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_constraint_violation"),
            "activation_sqlite_failure" or "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_sqlite_failure"),
            "setup_intent_snapshot_invalid" or "persisted_snapshot_invalid" or "activation_snapshot_invalid" =>
                StandingLeaseAuthorizationActivationResult.Rejected(request, "activation_snapshot_invalid"),
            _ => StandingLeaseAuthorizationActivationResult.Rejected(request, code),
        };

    private static void ValidateUtcValue(DateTimeOffset value, string message)
    {
        if (value.Offset != TimeSpan.Zero || value.UtcDateTime.Ticks < 0)
        {
            throw new PersistedSnapshotException(message);
        }
    }

    internal sealed record PreparedIntentBinding(
        string IntentId,
        StandingSetupIntentStatus Status,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ScheduledStartUtc,
        DateTimeOffset LatestStartUtc,
        DateTimeOffset PlannedEndUtc,
        TimeSpan MaximumDuration,
        DateTimeOffset LeaseValidUntilUtc,
        string? TerminalReasonCode,
        string? PlanId,
        string? OccurrenceId,
        string? LeaseId,
        string? ScopeId,
        string CurrentUserSid,
        string SessionBinding,
        long Version);

    private static string? ValidateReceipt(
        StandingLeaseLocalApprovalReceipt receipt,
        AuthorizedFixedRegionScope scope,
        StandingLeaseAuthorizationActivationRequest request)
    {
        if (!IsCanonicalId(receipt.ApprovalId) ||
            !IsCanonicalId(receipt.PlanId) ||
            !IsCanonicalId(receipt.OccurrenceId) ||
            !IsCanonicalId(receipt.LeaseId) ||
            !IsCanonicalId(receipt.ScopeId) ||
            !IsCanonicalDigest(receipt.ScopeDigest) ||
            !IsCanonicalText(receipt.CurrentUserSid) ||
            !IsCanonicalText(receipt.SessionBinding) ||
            receipt.ApprovalKind is null ||
            receipt.ApprovalVersion <= 0)
        {
            return "activation_receipt_invalid";
        }

        if (receipt.ApprovalKind != StandingLeaseLocalApprovalReceipt.CurrentApprovalKind ||
            receipt.ApprovalVersion != StandingLeaseLocalApprovalReceipt.CurrentApprovalVersion)
        {
            return "activation_receipt_kind_unsupported";
        }

        if (!MatchesRequestIdentity(request, receipt) ||
            !string.Equals(receipt.PlanId, scope.PlanId, StringComparison.Ordinal) ||
            !string.Equals(receipt.OccurrenceId, scope.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(receipt.LeaseId, scope.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ScopeId, scope.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(receipt.ScopeDigest, scope.ScopeDigest, StringComparison.Ordinal) ||
            !string.Equals(receipt.CurrentUserSid, scope.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(receipt.SessionBinding, scope.SessionBinding, StringComparison.Ordinal))
        {
            return "activation_receipt_identity_mismatch";
        }

        return null;
    }

    private static string? ValidateApprovalTime(
        StandingLeaseLocalApprovalReceipt receipt,
        AuthorizedFixedRegionScope scope,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        DateTimeOffset nowUtc,
        bool requireLeaseLive)
    {
        if (receipt.ApprovedAtUtc.Offset != TimeSpan.Zero)
        {
            return "activation_receipt_time_not_utc";
        }

        if (receipt.ApprovedAtUtc < scope.CreatedAtUtc ||
            receipt.ApprovedAtUtc < plan.CreatedAtUtc ||
            receipt.ApprovedAtUtc < occurrence.CreatedAtUtc ||
            receipt.ApprovedAtUtc < lease.ValidFromUtc)
        {
            return "activation_receipt_time_invalid";
        }

        if (receipt.ApprovedAtUtc > nowUtc)
        {
            return "activation_receipt_time_in_future";
        }

        if (nowUtc < plan.UpdatedAtUtc ||
            nowUtc < occurrence.UpdatedAtUtc ||
            nowUtc < lease.UpdatedAtUtc)
        {
            return "activation_time_non_monotonic";
        }

        if (receipt.ApprovedAtUtc < plan.UpdatedAtUtc ||
            receipt.ApprovedAtUtc < occurrence.UpdatedAtUtc ||
            receipt.ApprovedAtUtc < lease.UpdatedAtUtc)
        {
            return "activation_time_non_monotonic";
        }

        if (receipt.ApprovedAtUtc < lease.ValidFromUtc ||
            receipt.ApprovedAtUtc >= lease.ValidUntilUtc)
        {
            return "activation_lease_not_valid_at_approval";
        }

        if (requireLeaseLive && nowUtc >= lease.ValidUntilUtc)
        {
            return "activation_lease_expired";
        }

        return null;
    }

    private static bool MatchesRequestIdentity(
        StandingLeaseAuthorizationActivationRequest request,
        AuthorizedFixedRegionScope scope) =>
        string.Equals(request.PlanId, scope.PlanId, StringComparison.Ordinal) &&
        string.Equals(request.OccurrenceId, scope.OccurrenceId, StringComparison.Ordinal) &&
        string.Equals(request.LeaseId, scope.LeaseId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeId, scope.ScopeId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeDigest, scope.ScopeDigest, StringComparison.Ordinal);

    private static bool MatchesRequestIdentity(
        StandingLeaseAuthorizationActivationRequest request,
        StandingLeaseLocalApprovalReceipt receipt) =>
        string.Equals(request.PlanId, receipt.PlanId, StringComparison.Ordinal) &&
        string.Equals(request.OccurrenceId, receipt.OccurrenceId, StringComparison.Ordinal) &&
        string.Equals(request.LeaseId, receipt.LeaseId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeId, receipt.ScopeId, StringComparison.Ordinal) &&
        string.Equals(request.ScopeDigest, receipt.ScopeDigest, StringComparison.Ordinal);

    private static bool HasPreparedChainIdentityConflict(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedIntentBinding preparedIntent,
        AuthorizedFixedRegionScope scope)
    {
        if (preparedIntent.PlanId is null || preparedIntent.OccurrenceId is null ||
            preparedIntent.LeaseId is null || preparedIntent.ScopeId is null ||
            !string.Equals(scope.ScopeId, preparedIntent.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(scope.PlanId, preparedIntent.PlanId, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, preparedIntent.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, preparedIntent.LeaseId, StringComparison.Ordinal))
        {
            return true;
        }

        var planExists = ReadExistingId(connection, transaction, "plans", preparedIntent.PlanId);
        var occurrencePlanId = ReadSingleText(connection, transaction,
            "SELECT plan_id FROM plan_occurrences WHERE id = $id;", preparedIntent.OccurrenceId);
        var leaseRelations = ReadTwoText(connection, transaction,
            "SELECT plan_id, occurrence_id FROM consent_leases WHERE id = $id;", preparedIntent.LeaseId);

        return !planExists || occurrencePlanId is null || leaseRelations is null ||
            !string.Equals(occurrencePlanId, preparedIntent.PlanId, StringComparison.Ordinal) ||
            !string.Equals(leaseRelations.Value.PlanId, preparedIntent.PlanId, StringComparison.Ordinal) ||
            !string.Equals(leaseRelations.Value.OccurrenceId, preparedIntent.OccurrenceId, StringComparison.Ordinal);
    }

    private static bool ReadExistingId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id FROM {table} WHERE id = $id;";
        Add(command, "$id", id);
        return command.ExecuteScalar() is string persistedId &&
            string.Equals(persistedId, id, StringComparison.Ordinal);
    }

    private static string? ReadSingleText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$id", id);
        var value = command.ExecuteScalar();
        return value is string text ? text : null;
    }

    private static (string PlanId, string OccurrenceId)? ReadTwoText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return reader.GetValue(0) is string planId && reader.GetValue(1) is string occurrenceId
            ? (planId, occurrenceId)
            : null;
    }

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalText(string? value) =>
        IsCanonicalId(value) &&
        !value!.Contains('\\') &&
        !value.Contains('/');

    private static bool IsCanonicalDigest(string? value) =>
        IsCanonicalId(value) &&
        value!.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
        {
            result.Add(ReadRecordingRunSnapshot(reader));
        }

        return result;
    }

    private static IReadOnlyList<LeaseUse> LoadUsesByChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId,
        string leaseId)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {UseColumns} FROM lease_uses WHERE occurrence_id = $occurrence_id OR lease_id = $lease_id;",
            ("$occurrence_id", occurrenceId),
            ("$lease_id", leaseId));
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
        {
            result.Add(ReadLeaseUseSnapshot(reader));
        }

        return result;
    }

    private static void UpdatePlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plans SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND is_one_time = $is_one_time AND created_at_utc = $created_at_utc AND status_code = $expected_status_code;";
        Add(command, "$status_code", plan.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(plan.UpdatedAtUtc));
        Add(command, "$new_version", plan.Version);
        Add(command, "$id", RequiredInput(plan.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$is_one_time", plan.IsOneTime ? 1L : 0L);
        Add(command, "$created_at_utc", UtcTicksInput(plan.CreatedAtUtc));
        Add(command, "$expected_status_code", Phase3StateCodes.ToCode(PlanDefinitionStatus.Draft));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows != 1)
        {
            EnsureUpdateSucceeded(connection, transaction, "SELECT version FROM plans WHERE id = $id;", plan.Id, expectedVersion);
        }
    }

    private static void UpdateOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc AND run_id IS NULL AND terminal_reason_code IS NULL AND status_code = $expected_status_code;";
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", NullableInput(occurrence.RunId));
        Add(command, "$terminal_reason_code", NullableInput(occurrence.TerminalReasonCode));
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", RequiredInput(occurrence.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(occurrence.PlanId));
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        Add(command, "$expected_status_code", Phase3StateCodes.ToCode(PlanOccurrenceStatus.PendingLeaseApproval));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows != 1)
        {
            EnsureUpdateSucceeded(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", occurrence.Id, expectedVersion);
        }
    }

    private static void UpdateLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsentLease lease,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE consent_leases SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND occurrence_id = $occurrence_id AND valid_from_utc = $valid_from_utc AND valid_until_utc = $valid_until_utc AND max_uses = $max_uses AND max_duration_ms = $max_duration_ms AND status_code = $expected_status_code;";
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$id", RequiredInput(lease.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(lease.PlanId));
        Add(command, "$occurrence_id", RequiredInput(lease.OccurrenceId));
        Add(command, "$valid_from_utc", UtcTicksInput(lease.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(lease.ValidUntilUtc));
        Add(command, "$max_uses", lease.MaxUses);
        Add(command, "$max_duration_ms", DurationMillisecondsInput(lease.MaxDuration));
        Add(command, "$expected_status_code", Phase3StateCodes.ToCode(ConsentLeaseStatus.Pending));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows != 1)
        {
            EnsureUpdateSucceeded(connection, transaction, "SELECT version FROM consent_leases WHERE id = $id;", lease.Id, expectedVersion);
        }
    }

    private static void EnsureUpdateSucceeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string selectVersionSql,
        string id,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = selectVersionSql;
        Add(command, "$id", id);
        var current = command.ExecuteScalar();
        if (current is null)
        {
            throw SnapshotInvalid(new PersistedSnapshotException("An activation aggregate disappeared during its transaction."));
        }

        if (current is not long currentVersion)
        {
            throw SnapshotInvalid(new PersistedSnapshotException("An activation aggregate version was not an Int64 value."));
        }

        if (currentVersion != expectedVersion)
        {
            throw new Phase3PersistenceException(
                "activation_concurrency_conflict",
                "An activation aggregate changed before its update could commit.");
        }

        throw new Phase3PersistenceException(
            "activation_immutable_mismatch",
            "An activation aggregate immutable field or expected state no longer matches.");
    }

    private static SqliteCommand Select(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, string Value)[] parameters)
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

    private static Phase3PersistenceException SnapshotInvalid(Exception exception) =>
        new("activation_snapshot_invalid", "The standing authorization activation snapshot is invalid.", exception);
}
