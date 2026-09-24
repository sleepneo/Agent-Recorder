using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringSetupPreparationFailurePoint
{
    AfterProfileInsert,
    AfterPlanInsert,
    AfterScheduleRevision1Insert,
    AfterBindingInsert,
    AfterLeaseInsert,
    AfterPreparationInsert,
    AfterIntentUpdate,
    BeforeCommitAfterFinalRead,
}

internal sealed record RecurringSetupPreparationSnapshot(
    string IntentId,
    string SelectionDigest,
    DateTimeOffset SelectedAtUtc,
    string PlanId,
    string ProfileId,
    long ProfileVersion,
    string ProfileDigest,
    string LeaseId,
    string ConfigurationDigest,
    DateTimeOffset PreparedAtUtc,
    long PreparationVersion);

internal sealed record RecurringPreparedChainSnapshot(
    RecurringSetupPreparationSnapshot Preparation,
    PlanDefinition Plan,
    RecurringScheduleVersionSnapshot Schedule,
    RecurringPlanProfileBinding Binding,
    RecurringFixedRegionProfileVersion Profile,
    RecurringPlanConfigurationRef Configuration,
    RecurringConsentLease Lease);

/// <summary>
/// The sole write boundary for recurring setup preparation. Profile, draft
/// Plan, schedule, binding, pending Lease, preparation relation, and intent
/// CAS are all one SQLite immediate transaction.
/// </summary>
internal sealed class SqliteRecurringSetupPreparationTransaction : SqliteRepositoryBase
{
    private const string PreparationColumns = "intent_id, selection_digest, selected_at_utc, plan_id, profile_id, profile_version, profile_digest, lease_id, configuration_digest, prepared_at_utc, preparation_version";

    private readonly IRecurringSetupPreparationIdProvider _idProvider;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;
    private readonly Action<RecurringSetupPreparationFailurePoint>? _failureHook;

    internal SqliteRecurringSetupPreparationTransaction(
        SqliteOperationalStore store,
        IRecurringSetupPreparationIdProvider idProvider,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringSetupPreparationFailurePoint>? failureHook = null)
        : base(store)
    {
        _idProvider = idProvider ?? throw new ArgumentNullException(nameof(idProvider));
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _failureHook = failureHook;
    }

    internal RecurringSetupPreparationResult Prepare(
        RecurringFixedRegionSelectionSnapshot selection,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return RecurringSetupPreparationResult.Rejected(selection.IntentId, "recurring_setup_preparation_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var persisted = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(connection, transaction, selection.IntentId);
            if (persisted is null || persisted.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
            {
                transaction.Commit();
                return RecurringSetupPreparationResult.Rejected(selection.IntentId, "recurring_setup_preparation_intent_not_found");
            }

            var readback = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(
                persisted,
                connection,
                transaction);
            var request = readback.Snapshot;
            ValidateSelection(selection, persisted, request, nowUtc);

            if (readback.Status == RecurringSetupIntentStatus.LeaseApprovalPending)
            {
                var existing = ReadPreparation(connection, transaction, persisted.IntentId)
                    ?? throw new Phase3PersistenceException(
                        "recurring_setup_preparation_chain_conflict",
                        "The prepared recurring intent is missing its preparation relation.");
                if (!string.Equals(existing.SelectionDigest, selection.SelectionDigest, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return RecurringSetupPreparationResult.Conflict(
                        persisted.IntentId,
                        "recurring_setup_preparation_selection_conflict");
                }

                transaction.Commit();
                return RecurringSetupPreparationResult.Existing(existing);
            }

            if (readback.Status != RecurringSetupIntentStatus.RegionSelectionPending)
            {
                transaction.Commit();
                return RecurringSetupPreparationResult.Conflict(
                    persisted.IntentId,
                    "recurring_setup_preparation_intent_state_conflict");
            }

            if (nowUtc >= request.ExpiresAtUtc)
            {
                transaction.Commit();
                return RecurringSetupPreparationResult.Expired(persisted.IntentId);
            }

            if (ReadPreparation(connection, transaction, persisted.IntentId) is not null)
            {
                transaction.Commit();
                return RecurringSetupPreparationResult.Conflict(
                    persisted.IntentId,
                    "recurring_setup_preparation_partial_binding");
            }

            var ids = AllocateIds();
            var profile = CreateProfile(ids.ProfileId, selection, request, nowUtc);
            var plan = new PlanDefinition(ids.PlanId, isOneTime: false, nowUtc);
            var rulesDigest = RecurringTimeZoneRulesDigest.Compute(request.Schedule.TimeZoneInfo);
            var configuration = new RecurringPlanConfigurationRef(
                plan.Id,
                scheduleRevision: 1,
                request.Schedule.CanonicalDigest,
                rulesDigest,
                profile.Reference);
            var latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
                plan.Id,
                scheduleRevision: 1,
                request.Schedule);
            var lease = RecurringConsentLease.CreatePending(
                ids.LeaseId,
                configuration,
                request.RequestedAtUtc,
                request.LeaseValidUntilUtc,
                latestEnd,
                request.Schedule.RecordingDuration,
                request.MaxUses,
                request.MaxCumulativeDuration,
                nowUtc);
            var preparation = new RecurringSetupPreparationSnapshot(
                persisted.IntentId,
                selection.SelectionDigest,
                selection.SelectedAtUtc,
                plan.Id,
                profile.ProfileId,
                profile.ProfileVersion,
                profile.ProfileDigest,
                lease.LeaseId,
                configuration.ConfigurationDigest,
                nowUtc,
                1);

            _beforeWritesForTest?.Invoke(connection, transaction);
            SqliteRecurringFixedRegionProfileRepository.InsertWithinTransaction(connection, transaction, profile);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterProfileInsert);
            SqlitePlanDefinitionRepository.InsertWithinTransaction(connection, transaction, plan);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterPlanInsert);
            SqliteRecurringScheduleVersionRepository.InsertSchedule(
                connection,
                transaction,
                plan.Id,
                revision: 1,
                request.Schedule,
                rulesDigest,
                nowUtc.UtcDateTime.Ticks);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterScheduleRevision1Insert);
            SqliteRecurringPlanProfileBindingRepository.InsertWithinTransaction(
                connection,
                transaction,
                new RecurringPlanProfileBinding(plan.Id, profile.Reference, nowUtc));
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterBindingInsert);
            SqliteRecurringConsentLeaseRepository.InsertWithinTransaction(connection, transaction, lease);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterLeaseInsert);
            InsertPreparation(connection, transaction, preparation);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterPreparationInsert);
            UpdateSetupIntent(connection, transaction, persisted, nowUtc);
            InvokeFailure(RecurringSetupPreparationFailurePoint.AfterIntentUpdate);

            ValidatePreparedChainWithinTransaction(connection, transaction, persisted, request);
            InvokeFailure(RecurringSetupPreparationFailurePoint.BeforeCommitAfterFinalRead);
            _beforeCommitForTest?.Invoke(connection, transaction);
            transaction.Commit();
            return RecurringSetupPreparationResult.Prepared(preparation);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_snapshot_invalid",
                "The recurring setup preparation snapshot is invalid.",
                exception);
        }
        catch (Phase3DomainException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_request_invalid",
                "The recurring setup preparation request is invalid.",
                exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_constraint_violation",
                "The recurring setup preparation transaction violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_sqlite_failure",
                "The recurring setup preparation transaction failed in SQLite.",
                exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_sqlite_failure",
                "The recurring setup preparation transaction failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal static RecurringSetupPreparationSnapshot? ReadPreparationWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId) =>
        ReadPreparation(connection, transaction, intentId);

    internal static RecurringPreparedChainSnapshot ValidatePreparedChainWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringSetupIntentSnapshot request)
    {
        return ValidateChainWithinTransaction(connection, transaction, persisted, request, PreparationLifecycle.Pending);
    }

    internal static RecurringPreparedChainSnapshot ValidateActivatedChainWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringSetupIntentSnapshot request)
    {
        return ValidateChainWithinTransaction(connection, transaction, persisted, request, PreparationLifecycle.Activated);
    }

    internal static RecurringPreparedChainSnapshot ValidateTerminalChainWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringSetupIntentSnapshot request) =>
        ValidateChainWithinTransaction(connection, transaction, persisted, request, PreparationLifecycle.Terminal);

    private enum PreparationLifecycle { Pending, Activated, Terminal }

    private static RecurringPreparedChainSnapshot ValidateChainWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringSetupIntentSnapshot request,
        PreparationLifecycle lifecycle)
    {
        var requireDraft = lifecycle != PreparationLifecycle.Activated;
        var preparation = ReadPreparation(connection, transaction, persisted.IntentId)
            ?? throw new PersistedSnapshotException("The prepared recurring intent has no preparation relation.");
        if (!string.Equals(preparation.IntentId, persisted.IntentId, StringComparison.Ordinal) ||
            preparation.PreparationVersion != 1 ||
            preparation.SelectedAtUtc.Offset != TimeSpan.Zero ||
            preparation.SelectedAtUtc < persisted.CreatedAtUtc ||
            preparation.SelectedAtUtc < request.RequestedAtUtc ||
            preparation.SelectedAtUtc > preparation.PreparedAtUtc ||
            preparation.PreparedAtUtc < request.RequestedAtUtc ||
            preparation.PreparedAtUtc >= request.ExpiresAtUtc ||
            (lifecycle == PreparationLifecycle.Terminal && persisted.UpdatedAtUtc < preparation.PreparedAtUtc))
        {
            throw new PersistedSnapshotException("The recurring preparation relation has invalid identity, version, or timing.");
        }

        var plan = ReadPlan(connection, transaction, preparation.PlanId)
            ?? throw new PersistedSnapshotException("The recurring preparation Plan is missing.");
        if (plan.IsOneTime ||
            !string.Equals(plan.Id, preparation.PlanId, StringComparison.Ordinal) ||
            plan.CreatedAtUtc != preparation.PreparedAtUtc ||
            plan.UpdatedAtUtc < preparation.PreparedAtUtc ||
            (requireDraft
                ? plan.Status != PlanDefinitionStatus.Draft || plan.Version != 0 || plan.UpdatedAtUtc != preparation.PreparedAtUtc
                : !RecurringLifecycleReachability.IsExactPath(
                    PlanDefinitionStatus.Enabled,
                    1,
                    plan.Status,
                    plan.Version,
                    Phase3TransitionGuards.IsPlanDefinitionEdge)))
        {
            throw new PersistedSnapshotException(requireDraft
                ? "The recurring preparation Plan is not an untouched draft."
                : "The activated recurring Plan is not a legal post-activation state.");
        }

        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
            connection,
            transaction,
            preparation.PlanId,
            scheduleRevision: 1)
            ?? throw new PersistedSnapshotException("The recurring preparation schedule revision 1 is missing.");
        if (schedule.PlanId != preparation.PlanId ||
            schedule.ScheduleRevision != 1 ||
            schedule.ScheduleDigest != request.ScheduleDigest ||
            schedule.TimeZoneRulesDigest != request.TimeZoneRulesDigest ||
            schedule.Schedule.CanonicalDigest != request.Schedule.CanonicalDigest ||
            schedule.CreatedAtUtc != preparation.PreparedAtUtc)
        {
            throw new PersistedSnapshotException("The recurring preparation schedule does not match the frozen request.");
        }

        var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(
            connection,
            transaction,
            preparation.PlanId)
            ?? throw new PersistedSnapshotException("The recurring preparation profile binding is missing.");
        if (binding.PlanId != preparation.PlanId ||
            binding.ProfileRef.ProfileId != preparation.ProfileId ||
            binding.ProfileRef.ProfileVersion != preparation.ProfileVersion ||
            !string.Equals(binding.ProfileRef.ProfileDigest, preparation.ProfileDigest, StringComparison.Ordinal) ||
            binding.BoundAtUtc != preparation.PreparedAtUtc)
        {
            throw new PersistedSnapshotException("The recurring preparation profile binding is not exact.");
        }

        var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection,
            transaction,
            binding.ProfileRef);
        if (profile.ProfileVersion != RecurringFixedRegionProfileVersion.FirstProfileVersion ||
            !binding.ProfileRef.Matches(profile) ||
            profile.Duration != request.Schedule.RecordingDuration ||
            profile.OutputDirectory != request.OutputDirectory ||
            profile.FilenamePrefix != request.FilenamePrefix ||
            profile.CountdownSeconds != 0 ||
            profile.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            profile.RebindPolicy != RecurringFixedRegionRebindPolicy.ExactMatchOnly ||
            profile.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            profile.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            profile.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            profile.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            profile.AudioMode != AuthorizedAudioMode.None ||
            profile.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            profile.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            profile.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
            profile.CreatedAtUtc != preparation.PreparedAtUtc)
        {
            throw new PersistedSnapshotException("The recurring preparation Profile is not the frozen fixed-region MVP profile.");
        }

        var selectionDigest = RecurringFixedRegionSelectionEvidenceDigest.Compute(
            persisted.IntentId,
            persisted.CurrentUserSid,
            persisted.SessionBinding,
            preparation.SelectedAtUtc,
            profile);
        if (!string.Equals(selectionDigest, preparation.SelectionDigest, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The recurring selection evidence digest does not match the durable intent time and Profile evidence.");
        }

        RecurringPlanConfigurationRef configuration;
        try
        {
            configuration = new RecurringPlanConfigurationRef(
                preparation.PlanId,
                1,
                schedule.ScheduleDigest,
                schedule.TimeZoneRulesDigest,
                binding.ProfileRef);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("The recurring preparation configuration is invalid.", exception);
        }

        if (!string.Equals(configuration.ConfigurationDigest, preparation.ConfigurationDigest, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The recurring preparation configuration digest is not canonical.");
        }

        var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, preparation.LeaseId)
            ?? throw new PersistedSnapshotException("The recurring preparation Lease is missing.");
        var latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
            preparation.PlanId,
            1,
            request.Schedule);
        if (lease.LeaseId != preparation.LeaseId ||
            lease.PlanId != preparation.PlanId ||
            (lifecycle == PreparationLifecycle.Pending
                ? lease.Status != ConsentLeaseStatus.Pending || lease.Version != 0 || lease.UpdatedAtUtc != preparation.PreparedAtUtc
                : lifecycle == PreparationLifecycle.Terminal
                ? lease.Status != ConsentLeaseStatus.Rejected || lease.Version != 1 || lease.UpdatedAtUtc != persisted.UpdatedAtUtc
                : !RecurringLifecycleReachability.IsExactPath(
                    ConsentLeaseStatus.Active,
                    1,
                    lease.Status,
                    lease.Version,
                    Phase3TransitionGuards.IsConsentLeaseEdge)) ||
            lease.CreatedAtUtc != preparation.PreparedAtUtc ||
            lease.UpdatedAtUtc < preparation.PreparedAtUtc ||
            lease.ValidFromUtc != request.RequestedAtUtc ||
            lease.ValidUntilUtc != request.LeaseValidUntilUtc ||
            lease.AuthorizedPlanLatestEndUtc != latestEnd ||
            lease.PerRunDuration != request.Schedule.RecordingDuration ||
            lease.MaxUses != request.MaxUses ||
            lease.MaxCumulativeDuration != request.MaxCumulativeDuration ||
            !string.Equals(lease.ConfigurationRef.ConfigurationDigest, configuration.ConfigurationDigest, StringComparison.Ordinal) ||
            lease.ConfigurationRef.ProfileRef.ProfileId != binding.ProfileRef.ProfileId ||
            lease.ConfigurationRef.ProfileRef.ProfileVersion != binding.ProfileRef.ProfileVersion ||
            !string.Equals(lease.ConfigurationRef.ProfileRef.ProfileDigest, binding.ProfileRef.ProfileDigest, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException(requireDraft
                ? "The recurring preparation Lease does not match the frozen chain."
                : "The activated recurring Lease does not match the frozen chain or legal post-activation state.");
        }

        if (lifecycle == PreparationLifecycle.Activated)
        {
            if (lease.Status == ConsentLeaseStatus.Active && lease.UpdatedAtUtc >= lease.ValidUntilUtc)
                throw new PersistedSnapshotException("An active recurring Lease is past its validity boundary.");
            if (lease.Status == ConsentLeaseStatus.Expired && lease.UpdatedAtUtc < lease.ValidUntilUtc)
                throw new PersistedSnapshotException("An expired recurring Lease precedes its validity boundary.");

            ValidateActivatedQuotaWithinTransaction(connection, transaction, lease);
        }
        else
        {
            ValidateNoApprovalOrExecution(connection, transaction, preparation);
        }

        return new RecurringPreparedChainSnapshot(
            preparation,
            plan,
            schedule,
            binding,
            profile,
            configuration,
            lease);
    }

    private static void ValidateNoApprovalOrExecution(
        SqliteConnection connection, SqliteTransaction transaction, RecurringSetupPreparationSnapshot preparation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM recurring_lease_local_approvals WHERE lease_id = $lease OR plan_id = $plan)
                OR EXISTS(SELECT 1 FROM plan_occurrences WHERE plan_id = $plan)
                OR EXISTS(SELECT 1 FROM recurring_occurrence_slots WHERE plan_id = $plan)
                OR EXISTS(SELECT 1 FROM recurring_lease_uses WHERE lease_id = $lease OR plan_id = $plan)
                OR EXISTS(SELECT 1 FROM recurring_occurrence_execution_specs WHERE lease_id = $lease OR plan_id = $plan);
            """;
        Add(command, "$lease", preparation.LeaseId);
        Add(command, "$plan", preparation.PlanId);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw new PersistedSnapshotException("An unapproved recurring setup chain has approval or execution associations.");
    }

    private static void ValidateActivatedQuotaWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease)
    {
        IReadOnlyList<RecurringLeaseUseAccountingEntry> entries;
        RecurringLeaseQuotaSnapshot quota;
        try
        {
            entries = SqliteRecurringLeaseUseAccountingReader.ReadAllWithinTransaction(
                connection,
                transaction,
                lease,
                requireProductionVersionShape: true);
            quota = RecurringLeaseQuotaCalculator.Calculate(lease, entries);
        }
        catch (Phase3PersistenceException exception)
        {
            throw new PersistedSnapshotException(
                "The activated recurring Lease accounting projection is invalid.",
                exception);
        }
        catch (PersistedSnapshotException exception)
        {
            throw new PersistedSnapshotException(
                "The activated recurring Lease accounting projection is invalid.",
                exception);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException(
                "The activated recurring Lease quota calculation is invalid.",
                exception);
        }

        if (!string.Equals(quota.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            quota.LeaseVersion != lease.Version ||
            !string.Equals(quota.LeaseAuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The activated recurring Lease quota is not bound to the exact Lease snapshot.");
        }

        var terminalQuotaRequired = (lease.Status, lease.Version) switch
        {
            (ConsentLeaseStatus.Active, 1) => false,
            (ConsentLeaseStatus.Revoked, 2) => false,
            (ConsentLeaseStatus.Expired, 2) => false,
            (ConsentLeaseStatus.Exhausted, 2) => true,
            (ConsentLeaseStatus.Revoked, 3) => true,
            _ => throw new PersistedSnapshotException(
                "The activated recurring Lease status/version has no legal quota matrix entry."),
        };

        if (quota.IsTerminallyExhausted != terminalQuotaRequired)
        {
            throw new PersistedSnapshotException(
                terminalQuotaRequired
                    ? "The activated recurring Lease requires terminal quota evidence."
                    : "The activated recurring Lease must not have terminal quota evidence.");
        }
    }

    private static RecurringFixedRegionProfileVersion CreateProfile(
        string profileId,
        RecurringFixedRegionSelectionSnapshot selection,
        RecurringSetupIntentSnapshot request,
        DateTimeOffset nowUtc)
    {
        var specification = new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion,
            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            selection.StableDisplayFingerprint,
            selection.DisplayBounds,
            selection.RegionWithinDisplay,
            selection.DpiX,
            selection.DpiY,
            selection.PhysicalWidth,
            selection.PhysicalHeight,
            selection.Orientation,
            selection.TopologyDigest,
            AuthorizedCaptureBackend.FfmpegRegion,
            AuthorizedAudioMode.None,
            request.Schedule.RecordingDuration,
            countdownSeconds: 0,
            request.OutputDirectory,
            request.FilenamePrefix,
            AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);
        return RecurringFixedRegionProfileVersion.CreateVersion1(profileId, nowUtc, specification);
    }

    private ChainIds AllocateIds()
    {
        var ids = new ChainIds(
            RequiredCanonicalId(_idProvider.CreateProfileId(), "profile_id"),
            RequiredCanonicalId(_idProvider.CreatePlanId(), "plan_id"),
            RequiredCanonicalId(_idProvider.CreateLeaseId(), "lease_id"));
        if (new[] { ids.ProfileId, ids.PlanId, ids.LeaseId }.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_identity_conflict",
                "The recurring preparation identities must be pairwise distinct.");
        }

        return ids;
    }

    private static void ValidateSelection(
        RecurringFixedRegionSelectionSnapshot selection,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        RecurringSetupIntentSnapshot request,
        DateTimeOffset nowUtc)
    {
        if (selection.IntentId != persisted.IntentId ||
            selection.CurrentUserSid != persisted.CurrentUserSid ||
            selection.SessionBinding != persisted.SessionBinding)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_identity_conflict",
                "The trusted selection is bound to another recurring setup intent, SID, or session.");
        }

        if (selection.SelectedAtUtc.Offset != TimeSpan.Zero ||
            selection.SelectedAtUtc < persisted.CreatedAtUtc ||
            selection.SelectedAtUtc < request.RequestedAtUtc ||
            selection.SelectedAtUtc > nowUtc ||
            selection.SelectedAtUtc >= persisted.ExpiresAtUtc)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_selection_conflict",
                "The trusted selection timestamp is outside the durable setup window.");
        }

        if (selection.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            selection.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            selection.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            selection.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            selection.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            selection.AudioMode != AuthorizedAudioMode.None ||
            selection.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            selection.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            selection.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
            !Enum.IsDefined(selection.Orientation))
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_selection_conflict",
                "The trusted selection is outside the fixed-region recurring MVP policy.");
        }

        if (string.IsNullOrWhiteSpace(selection.StableDisplayFingerprint) ||
            selection.StableDisplayFingerprint != selection.StableDisplayFingerprint.Trim() ||
            selection.StableDisplayFingerprint.Length > 256 ||
            selection.StableDisplayFingerprint.Any(char.IsControl) ||
            selection.StableDisplayFingerprint.Contains('/') ||
            selection.StableDisplayFingerprint.Contains('\\') ||
            !IsLowerHexDigest(selection.TopologyDigest) ||
            selection.DisplayBounds.Width <= 0 ||
            selection.DisplayBounds.Height <= 0 ||
            selection.RegionWithinDisplay.X < 0 ||
            selection.RegionWithinDisplay.Y < 0 ||
            selection.RegionWithinDisplay.Width <= 0 ||
            selection.RegionWithinDisplay.Height <= 0 ||
            selection.DpiX <= 0 ||
            selection.DpiY <= 0 ||
            selection.PhysicalWidth != selection.DisplayBounds.Width ||
            selection.PhysicalHeight != selection.DisplayBounds.Height)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_selection_conflict",
                "The trusted selection geometry or display identity is not canonical.");
        }

        try
        {
            if (checked(selection.RegionWithinDisplay.X + selection.RegionWithinDisplay.Width) > selection.DisplayBounds.Width ||
                checked(selection.RegionWithinDisplay.Y + selection.RegionWithinDisplay.Height) > selection.DisplayBounds.Height ||
                checked(selection.DisplayBounds.X + selection.RegionWithinDisplay.X + selection.RegionWithinDisplay.Width) > int.MaxValue ||
                checked(selection.DisplayBounds.Y + selection.RegionWithinDisplay.Y + selection.RegionWithinDisplay.Height) > int.MaxValue)
            {
                throw new Phase3PersistenceException(
                    "recurring_setup_preparation_selection_conflict",
                    "The trusted selection region is outside the display or overflows the physical coordinate space.");
            }
        }
        catch (OverflowException exception)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_selection_conflict",
                "The trusted selection geometry overflows the physical coordinate space.",
                exception);
        }

        if (request.Schedule.RecordingDuration > RecurringFixedRegionProfileVersion.MaximumDuration)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_selection_conflict",
                "The recurring schedule duration is outside the fixed-region profile bound.");
        }
    }

    private static bool IsLowerHexDigest(string? value) =>
        value is not null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool HasPreparationWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM recurring_setup_preparations WHERE intent_id = $intent_id);";
        Add(command, "$intent_id", intentId);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static string RequiredCanonicalId(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() ||
            value.Length > RecurringSetupIntentSnapshot.MaximumIdLength ||
            value.Any(char.IsControl) ||
            value.Contains('/') ||
            value.Contains('\\'))
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_identity_conflict",
                $"The allocated recurring {field} is not canonical.");
        }

        return value;
    }

    private static RecurringSetupPreparationSnapshot? ReadPreparation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PreparationColumns} FROM recurring_setup_preparations WHERE intent_id = $intent_id LIMIT 1;";
        Add(command, "$intent_id", intentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        try
        {
            var preparation = new RecurringSetupPreparationSnapshot(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadUtcDateTimeOffset(reader, 2),
                ReadRequiredText(reader, 3),
                ReadRequiredText(reader, 4),
                ReadInt64(reader, 5),
                ReadRequiredText(reader, 6),
                ReadRequiredText(reader, 7),
                ReadRequiredText(reader, 8),
                ReadUtcDateTimeOffset(reader, 9),
                ReadInt64(reader, 10));
            if (preparation.PreparationVersion != 1 ||
                !IsLowerHexDigest(preparation.SelectionDigest[RecurringFixedRegionSelectionSnapshot.DigestPrefix.Length..]) ||
                !preparation.SelectionDigest.StartsWith(RecurringFixedRegionSelectionSnapshot.DigestPrefix, StringComparison.Ordinal))
            {
                throw new PersistedSnapshotException("The persisted recurring preparation relation is not canonical.");
            }

            return preparation;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new PersistedSnapshotException("The persisted recurring preparation relation is invalid.", exception);
        }
    }

    private static void InsertPreparation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringSetupPreparationSnapshot preparation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO recurring_setup_preparations ({PreparationColumns})
            VALUES ($intent_id, $selection_digest, $selected_at_utc, $plan_id, $profile_id, $profile_version,
                    $profile_digest, $lease_id, $configuration_digest, $prepared_at_utc, $preparation_version);
            """;
        Add(command, "$intent_id", preparation.IntentId);
        Add(command, "$selection_digest", preparation.SelectionDigest);
        Add(command, "$selected_at_utc", UtcTicksInput(preparation.SelectedAtUtc));
        Add(command, "$plan_id", preparation.PlanId);
        Add(command, "$profile_id", preparation.ProfileId);
        Add(command, "$profile_version", preparation.ProfileVersion);
        Add(command, "$profile_digest", preparation.ProfileDigest);
        Add(command, "$lease_id", preparation.LeaseId);
        Add(command, "$configuration_digest", preparation.ConfigurationDigest);
        Add(command, "$prepared_at_utc", UtcTicksInput(preparation.PreparedAtUtc));
        Add(command, "$preparation_version", preparation.PreparationVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_sqlite_failure",
                "The recurring preparation relation insert did not affect exactly one row.");
        }
    }

    private static void UpdateSetupIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteRecurringSetupIntentCreateOrGetTransaction.PersistedIntent persisted,
        DateTimeOffset nowUtc)
    {
        if (persisted.Version == long.MaxValue)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_concurrency_conflict",
                "The recurring setup intent version cannot be incremented.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE setup_intents
            SET status_code = $status_code,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE intent_id = $intent_id
              AND intent_kind_code = $kind
              AND status_code = $expected_status
              AND version = $expected_version
              AND terminal_reason_code IS NULL
              AND plan_id IS NULL AND occurrence_id IS NULL AND lease_id IS NULL AND scope_id IS NULL;
            """;
        Add(command, "$status_code", RecurringSetupIntentCodes.LeaseApprovalPendingStatus);
        Add(command, "$updated_at_utc", UtcTicksInput(nowUtc));
        Add(command, "$new_version", checked(persisted.Version + 1));
        Add(command, "$intent_id", persisted.IntentId);
        Add(command, "$kind", RecurringSetupIntentCodes.IntentKind);
        Add(command, "$expected_status", RecurringSetupIntentCodes.InitialStatus);
        Add(command, "$expected_version", persisted.Version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(
                "recurring_setup_preparation_concurrency_conflict",
                "The recurring setup intent changed before preparation could commit.");
        }
    }

    private static PlanDefinition? ReadPlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, is_one_time, status_code, created_at_utc, updated_at_utc, version FROM plans WHERE id = $plan_id LIMIT 1;";
        Add(command, "$plan_id", planId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlanDefinitionSnapshot(reader) : null;
    }

    private static void InvokeFailure(
        Action<RecurringSetupPreparationFailurePoint>? failureHook,
        RecurringSetupPreparationFailurePoint point) => failureHook?.Invoke(point);

    private void InvokeFailure(RecurringSetupPreparationFailurePoint point) => InvokeFailure(_failureHook, point);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
        }
        catch (SqliteException)
        {
        }
    }

    private sealed record ChainIds(string ProfileId, string PlanId, string LeaseId);
}
