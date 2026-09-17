using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// One immediate transaction which turns a pending setup intent plus a trusted
/// fixed-region selection into a pending authorization chain. This class has
/// no activation, proof, execution, scheduler, environment, or backend calls.
/// </summary>
internal sealed class SqliteStandingLeasePreparationTransaction : SqliteRepositoryBase
{
    private const string SetupIntentColumns = "intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc, version, terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms, lease_valid_until_utc, output_directory, frozen_file_name";

    private readonly IStandingLeasePreparationIdProvider _idProvider;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingLeasePreparationTransaction(
        SqliteOperationalStore store,
        IStandingLeasePreparationIdProvider idProvider,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _idProvider = idProvider ?? throw new ArgumentNullException(nameof(idProvider));
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingLeasePreparationResult Prepare(
        StandingFixedRegionSelectionSnapshot selection,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeasePreparationResult.Rejected(selection.IntentId, "preparation_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var persisted = ReadSetupIntent(connection, transaction, selection.IntentId);
            if (persisted is null)
            {
                return StandingLeasePreparationResult.Rejected(selection.IntentId, "preparation_intent_not_found");
            }

            var request = RehydrateRequest(persisted);
            ValidateSelection(selection, persisted, request, nowUtc);

            if (persisted.Status == StandingSetupIntentStatus.RegionSelectionPending)
            {
                if (nowUtc >= persisted.ExpiresAtUtc)
                {
                    return StandingLeasePreparationResult.Expired(persisted.IntentId);
                }

                if (AssociationCount(persisted) != 0)
                {
                    return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_partial_binding");
                }

                var ids = AllocateIds(selection);
                var plan = new PlanDefinition(ids.PlanId, isOneTime: true, nowUtc);
                var occurrence = PlanOccurrence.Rehydrate(
                    ids.OccurrenceId,
                    plan.Id,
                    request.ScheduledStartUtc,
                    request.PlannedEndUtc,
                    nowUtc,
                    PlanOccurrenceStatus.PendingLeaseApproval,
                    runId: null,
                    terminalReasonCode: null,
                    updatedAtUtc: nowUtc,
                    version: 0);
                var lease = ConsentLease.CreateFor(
                    plan,
                    occurrence,
                    nowUtc,
                    request.LeaseValidUntilUtc,
                    maxUses: 1,
                    request.MaximumDuration,
                    ids.LeaseId);
                var scope = CreateScope(selection, request, plan, occurrence, lease, ids.ScopeId, nowUtc);

                _beforeWritesForTest?.Invoke(connection, transaction);
                SqlitePlanDefinitionRepository.InsertWithinTransaction(connection, transaction, plan);
                SqlitePlanOccurrenceRepository.InsertWithinTransaction(connection, transaction, occurrence);
                SqliteConsentLeaseRepository.InsertWithinTransaction(connection, transaction, lease);
                SqliteAuthorizedCaptureScopeRepository.InsertWithinTransaction(connection, transaction, scope);
                UpdateSetupIntent(connection, transaction, persisted, ids, nowUtc);
                _beforeCommitForTest?.Invoke(connection, transaction);
                transaction.Commit();
                return StandingLeasePreparationResult.Prepared(
                    persisted.IntentId,
                    plan.Id,
                    occurrence.Id,
                    lease.Id,
                    scope.ScopeId,
                    scope.ScopeDigest);
            }

            if (persisted.Status == StandingSetupIntentStatus.LeaseApprovalPending)
            {
                var existing = ValidateExistingChain(connection, transaction, persisted, request, selection);
                transaction.Commit();
                return existing;
            }

            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_intent_state_conflict");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw new Phase3PersistenceException("preparation_snapshot_invalid", "The persisted preparation snapshot is invalid.", exception);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException("preparation_request_invalid", "The preparation request is invalid.", exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException("preparation_constraint_violation", "The preparation transaction violated a persisted constraint.", exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException("preparation_sqlite_failure", "The preparation transaction failed in SQLite.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException("preparation_sqlite_failure", "The preparation transaction failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static PersistedSetupIntent? ReadSetupIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SetupIntentColumns} FROM setup_intents WHERE intent_id = $intent_id;";
        Add(command, "$intent_id", intentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPersistedSetupIntent(reader) : null;
    }

    private static PersistedSetupIntent ReadPersistedSetupIntent(SqliteDataReader reader) => new(
        ReadRequiredText(reader, 0),
        ReadRequiredText(reader, 1),
        ReadRequiredText(reader, 2),
        ReadRequiredText(reader, 3),
        ReadRequiredText(reader, 4),
        ReadRequiredText(reader, 5),
        StandingSetupIntentStatusCodes.Parse(ReadRequiredText(reader, 6)),
        ReadUtcDateTimeOffset(reader, 7),
        ReadUtcDateTimeOffset(reader, 8),
        ReadNullableText(reader, 9),
        ReadNullableText(reader, 10),
        ReadNullableText(reader, 11),
        ReadNullableText(reader, 12),
        ReadUtcDateTimeOffset(reader, 13),
        ReadUtcDateTimeOffset(reader, 14),
        ReadInt64(reader, 15),
        ReadNullableText(reader, 16),
        ReadNullableUtcDateTimeOffset(reader, 17),
        ReadNullableUtcDateTimeOffset(reader, 18),
        ReadNullableUtcDateTimeOffset(reader, 19),
        ReadNullableDurationMilliseconds(reader, 20),
        ReadNullableUtcDateTimeOffset(reader, 21),
        ReadNullableText(reader, 22),
        ReadNullableText(reader, 23));

    private static StandingSetupIntentSnapshot RehydrateRequest(PersistedSetupIntent persisted)
    {
        ValidatePersistedIntent(persisted);
        if (persisted.ScheduledStartUtc is null ||
            persisted.LatestStartUtc is null ||
            persisted.PlannedEndUtc is null ||
            persisted.MaximumDuration is null ||
            persisted.LeaseValidUntilUtc is null ||
            persisted.OutputDirectory is null ||
            persisted.FrozenFileName is null)
        {
            throw new Phase3PersistenceException(
                "preparation_request_metadata_missing",
                "The setup intent does not contain the durable request metadata required for preparation.");
        }

        var request = StandingSetupIntentSnapshot.CreateForPersistence(
            persisted.IntentId,
            persisted.IdempotencyKey,
            persisted.CurrentUserSid,
            persisted.SessionBinding,
            persisted.RequestedAtUtc,
            persisted.ExpiresAtUtc,
            persisted.ScheduledStartUtc.Value,
            persisted.LatestStartUtc.Value,
            persisted.PlannedEndUtc.Value,
            persisted.MaximumDuration.Value,
            persisted.LeaseValidUntilUtc.Value,
            persisted.OutputDirectory,
            persisted.FrozenFileName,
            persisted.RequestDigest);
        SqliteStandingSetupIntentCreateOrGetTransaction.ValidateSnapshotForPreparation(request);
        return request;
    }

    private static void ValidatePersistedIntent(PersistedSetupIntent persisted)
    {
        ValidateCanonicalId(persisted.IntentId, StandingSetupIntentSnapshot.MaximumIdLength, "intent_id");
        if (!string.Equals(persisted.IntentKindCode, StandingSetupIntentCodes.IntentKind, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException("preparation_snapshot_invalid", "The persisted setup intent kind is not supported.");
        }

        ValidateCanonicalId(persisted.IdempotencyKey, StandingSetupIntentSnapshot.MaximumIdLength, "idempotency_key");
        ValidateCanonicalText(persisted.CurrentUserSid, StandingSetupIntentSnapshot.MaximumBindingLength, "current_user_sid");
        ValidateCanonicalText(persisted.SessionBinding, StandingSetupIntentSnapshot.MaximumBindingLength, "session_binding");
        ValidateDigest(persisted.RequestDigest, "request_digest");
        ValidateUtc(persisted.RequestedAtUtc, "preparation_request_time_invalid");
        ValidateUtc(persisted.ExpiresAtUtc, "preparation_expiry_invalid");
        ValidateUtc(persisted.CreatedAtUtc, "preparation_created_time_invalid");
        ValidateUtc(persisted.UpdatedAtUtc, "preparation_updated_time_invalid");
        if (persisted.ExpiresAtUtc <= persisted.RequestedAtUtc ||
            persisted.UpdatedAtUtc < persisted.CreatedAtUtc ||
            persisted.Version < 0)
        {
            throw new Phase3PersistenceException("preparation_snapshot_invalid", "The persisted setup intent time or version is invalid.");
        }

        if (persisted.Status is StandingSetupIntentStatus.RegionSelectionPending or StandingSetupIntentStatus.LeaseApprovalPending or StandingSetupIntentStatus.Activated)
        {
            if (persisted.TerminalReasonCode is not null)
            {
                throw new Phase3PersistenceException("preparation_snapshot_invalid", "A non-terminal setup intent has a terminal reason.");
            }
        }
        else if (persisted.Status is StandingSetupIntentStatus.Rejected or StandingSetupIntentStatus.Expired)
        {
            if (string.IsNullOrWhiteSpace(persisted.TerminalReasonCode))
            {
                throw new Phase3PersistenceException("preparation_snapshot_invalid", "A terminal setup intent has no terminal reason.");
            }
        }

        foreach (var association in new[] { persisted.PlanId, persisted.OccurrenceId, persisted.LeaseId, persisted.ScopeId })
        {
            ValidateOptionalId(association, "association_id");
        }
    }

    private static void ValidateSelection(
        StandingFixedRegionSelectionSnapshot selection,
        PersistedSetupIntent persisted,
        StandingSetupIntentSnapshot request,
        DateTimeOffset nowUtc)
    {
        ValidateCanonicalId(selection.IntentId, StandingSetupIntentSnapshot.MaximumIdLength, "selection_intent_id");
        ValidateCanonicalText(selection.CurrentUserSid, StandingSetupIntentSnapshot.MaximumBindingLength, "selection_current_user_sid");
        ValidateCanonicalText(selection.SessionBinding, StandingSetupIntentSnapshot.MaximumBindingLength, "selection_session_binding");
        ValidateCanonicalText(selection.StableDisplayFingerprint, 256, "stable_display_fingerprint");
        ValidateDigest(selection.TopologyDigest, "topology_digest");
        ValidateUtc(selection.SelectedAtUtc, "selection_time_invalid");

        if (!string.Equals(selection.IntentId, persisted.IntentId, StringComparison.Ordinal) ||
            !string.Equals(selection.CurrentUserSid, persisted.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(selection.SessionBinding, persisted.SessionBinding, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException("preparation_selection_conflict", "The fixed-region selection is bound to another intent, SID, or session.");
        }

        if (selection.SelectedAtUtc < persisted.CreatedAtUtc || selection.SelectedAtUtc > nowUtc)
        {
            throw new Phase3PersistenceException("preparation_selection_conflict", "The fixed-region selection time is outside the trusted intent interval.");
        }

        if (selection.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            selection.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            selection.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            selection.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            selection.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            selection.AudioMode != AuthorizedAudioMode.None ||
            selection.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            selection.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            selection.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
        {
            throw new Phase3PersistenceException("preparation_request_invalid", "The fixed-region selection policy is outside the first-MVP boundary.");
        }

        _ = request;
        ValidateOptionalId(selection.PlanId, "selection_plan_id");
        ValidateOptionalId(selection.OccurrenceId, "selection_occurrence_id");
        ValidateOptionalId(selection.LeaseId, "selection_lease_id");
        ValidateOptionalId(selection.ScopeId, "selection_scope_id");
        var candidateIds = new[] { selection.PlanId, selection.OccurrenceId, selection.LeaseId, selection.ScopeId }
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (candidateIds.Distinct(StringComparer.Ordinal).Count() != candidateIds.Length)
        {
            throw new Phase3PersistenceException("preparation_request_invalid", "Preparation identities must be distinct.");
        }
    }

    private ChainIds AllocateIds(StandingFixedRegionSelectionSnapshot selection)
    {
        var ids = new ChainIds(
            selection.PlanId ?? _idProvider.CreatePlanId(),
            selection.OccurrenceId ?? _idProvider.CreateOccurrenceId(),
            selection.LeaseId ?? _idProvider.CreateLeaseId(),
            selection.ScopeId ?? _idProvider.CreateScopeId());
        ValidateCanonicalId(ids.PlanId, StandingSetupIntentSnapshot.MaximumIdLength, "plan_id");
        ValidateCanonicalId(ids.OccurrenceId, StandingSetupIntentSnapshot.MaximumIdLength, "occurrence_id");
        ValidateCanonicalId(ids.LeaseId, StandingSetupIntentSnapshot.MaximumIdLength, "lease_id");
        ValidateCanonicalId(ids.ScopeId, StandingSetupIntentSnapshot.MaximumIdLength, "scope_id");
        if (new[] { ids.PlanId, ids.OccurrenceId, ids.LeaseId, ids.ScopeId }.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new Phase3PersistenceException("preparation_request_invalid", "Preparation identities must be distinct.");
        }

        return ids;
    }

    private static AuthorizedFixedRegionScope CreateScope(
        StandingFixedRegionSelectionSnapshot selection,
        StandingSetupIntentSnapshot request,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        string scopeId,
        DateTimeOffset createdAtUtc) =>
        AuthorizedFixedRegionScope.CreateFor(
            plan,
            occurrence,
            lease,
            scopeId,
            createdAtUtc,
            selection.TargetType,
            selection.CaptureSemantics,
            selection.CoordinateSpace,
            selection.DisplayIdentityStatus,
            selection.StableDisplayFingerprint,
            selection.DisplayBounds,
            selection.RegionWithinDisplay,
            selection.DpiX,
            selection.DpiY,
            selection.PhysicalWidth,
            selection.PhysicalHeight,
            selection.Orientation,
            selection.Backend,
            selection.AudioMode,
            request.MaximumDuration,
            countdownSeconds: 0,
            request.OutputDirectory,
            request.FrozenFileName,
            selection.OutputConflictPolicy,
            selection.WakePolicy,
            selection.DesktopRequirement,
            request.CurrentUserSid,
            request.SessionBinding,
            selection.TopologyDigest);

    private static StandingLeasePreparationResult ValidateExistingChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedSetupIntent persisted,
        StandingSetupIntentSnapshot request,
        StandingFixedRegionSelectionSnapshot selection)
    {
        if (persisted.PlanId is null || persisted.OccurrenceId is null || persisted.LeaseId is null || persisted.ScopeId is null)
        {
            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_partial_binding");
        }

        if (selection.PlanId is not null && !string.Equals(selection.PlanId, persisted.PlanId, StringComparison.Ordinal) ||
            selection.OccurrenceId is not null && !string.Equals(selection.OccurrenceId, persisted.OccurrenceId, StringComparison.Ordinal) ||
            selection.LeaseId is not null && !string.Equals(selection.LeaseId, persisted.LeaseId, StringComparison.Ordinal) ||
            selection.ScopeId is not null && !string.Equals(selection.ScopeId, persisted.ScopeId, StringComparison.Ordinal))
        {
            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_identity_conflict");
        }

        var plan = TryReadPlan(connection, transaction, persisted.PlanId);
        var occurrence = TryReadOccurrence(connection, transaction, persisted.OccurrenceId);
        var lease = TryReadLease(connection, transaction, persisted.LeaseId);
        var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(connection, transaction, persisted.ScopeId);
        if (plan is null || occurrence is null || lease is null || scope is null)
        {
            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_chain_conflict");
        }

        if (!plan.IsOneTime || plan.Status != PlanDefinitionStatus.Draft || plan.Version != 0 ||
            occurrence.PlanId != plan.Id || occurrence.Status != PlanOccurrenceStatus.PendingLeaseApproval ||
            occurrence.RunId is not null || occurrence.TerminalReasonCode is not null || occurrence.Version != 0 ||
            occurrence.WindowStartUtc != request.ScheduledStartUtc || occurrence.WindowEndUtc != request.PlannedEndUtc ||
            lease.PlanId != plan.Id || lease.OccurrenceId != occurrence.Id || lease.Status != ConsentLeaseStatus.Pending ||
            lease.MaxUses != 1 || lease.MaxDuration != request.MaximumDuration || lease.ValidUntilUtc != request.LeaseValidUntilUtc ||
            lease.ValidUntilUtc < occurrence.WindowEndUtc ||
            scope.PlanId != plan.Id || scope.OccurrenceId != occurrence.Id || scope.LeaseId != lease.Id ||
            scope.CurrentUserSid != request.CurrentUserSid || scope.SessionBinding != request.SessionBinding ||
            scope.ReservedDuration > lease.MaxDuration ||
            scope.CreatedAtUtc < occurrence.CreatedAtUtc ||
            scope.CreatedAtUtc < lease.ValidFromUtc ||
            scope.CreatedAtUtc >= lease.ValidUntilUtc)
        {
            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_chain_conflict");
        }

        var expected = CreateScope(selection, request, plan, occurrence, lease, scope.ScopeId, scope.CreatedAtUtc);
        if (!scope.MatchesExactly(expected))
        {
            return StandingLeasePreparationResult.Conflict(persisted.IntentId, "preparation_selection_conflict");
        }

        return StandingLeasePreparationResult.Existing(
            persisted.IntentId,
            plan.Id,
            occurrence.Id,
            lease.Id,
            scope.ScopeId,
            scope.ScopeDigest);
    }

    private static void UpdateSetupIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedSetupIntent persisted,
        ChainIds ids,
        DateTimeOffset nowUtc)
    {
        if (persisted.Version == long.MaxValue)
        {
            throw new Phase3PersistenceException("preparation_version_exhausted", "The setup intent version cannot be incremented.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE setup_intents
            SET status_code = $status_code,
                plan_id = $plan_id,
                occurrence_id = $occurrence_id,
                lease_id = $lease_id,
                scope_id = $scope_id,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE intent_id = $intent_id
              AND status_code = $expected_status_code
              AND version = $expected_version
              AND plan_id IS NULL AND occurrence_id IS NULL AND lease_id IS NULL AND scope_id IS NULL;
            """;
        Add(command, "$status_code", StandingSetupIntentCodes.LeaseApprovalPendingStatus);
        Add(command, "$plan_id", ids.PlanId);
        Add(command, "$occurrence_id", ids.OccurrenceId);
        Add(command, "$lease_id", ids.LeaseId);
        Add(command, "$scope_id", ids.ScopeId);
        Add(command, "$updated_at_utc", UtcTicksInput(nowUtc));
        Add(command, "$new_version", persisted.Version + 1);
        Add(command, "$intent_id", persisted.IntentId);
        Add(command, "$expected_status_code", StandingSetupIntentCodes.InitialStatus);
        Add(command, "$expected_version", persisted.Version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException("preparation_concurrency_conflict", "The setup intent changed before preparation could commit.");
        }
    }

    private static PlanDefinition? TryReadPlan(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, is_one_time, status_code, created_at_utc, updated_at_utc, version FROM plans WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlanDefinitionSnapshot(reader) : null;
    }

    private static PlanOccurrence? TryReadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version FROM plan_occurrences WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlanOccurrenceSnapshot(reader) : null;
    }

    private static ConsentLease? TryReadLease(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version FROM consent_leases WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConsentLeaseSnapshot(reader) : null;
    }

    private static int AssociationCount(PersistedSetupIntent persisted) =>
        new[] { persisted.PlanId, persisted.OccurrenceId, persisted.LeaseId, persisted.ScopeId }.Count(value => value is not null);

    private static void ValidateOptionalId(string? value, string name)
    {
        if (value is not null)
        {
            ValidateCanonicalId(value, StandingSetupIntentSnapshot.MaximumIdLength, name);
        }
    }

    private static void ValidateCanonicalId(string? value, int maximumLength, string name)
    {
        ValidateCanonicalText(value, maximumLength, name);
        if (value!.Contains('/') || value.Contains('\\'))
        {
            throw new Phase3PersistenceException("preparation_request_invalid", $"The {name} contains a path separator.");
        }
    }

    private static void ValidateCanonicalText(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > maximumLength)
        {
            throw new Phase3PersistenceException("preparation_request_invalid", $"The {name} is not canonical.");
        }
    }

    private static void ValidateDigest(string? value, string name)
    {
        ValidateCanonicalText(value, 64, name);
        if (value!.Length != 64 || value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3PersistenceException("preparation_request_invalid", $"The {name} is not lowercase SHA-256.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string code)
    {
        if (value.Offset != TimeSpan.Zero || value.UtcDateTime.Ticks < 0)
        {
            throw new Phase3PersistenceException(code, "The trusted preparation time must be UTC.");
        }
    }

    private sealed record ChainIds(string PlanId, string OccurrenceId, string LeaseId, string ScopeId);

    private sealed record PersistedSetupIntent(
        string IntentId,
        string IntentKindCode,
        string IdempotencyKey,
        string RequestDigest,
        string CurrentUserSid,
        string SessionBinding,
        StandingSetupIntentStatus Status,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        string? PlanId,
        string? OccurrenceId,
        string? LeaseId,
        string? ScopeId,
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
}
