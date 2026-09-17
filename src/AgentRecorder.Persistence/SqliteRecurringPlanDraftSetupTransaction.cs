using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringPlanDraftSetupPersistenceReasonCodes
{
    public const string InvalidArgument = "recurring_plan_draft_setup_invalid_argument";
    public const string ExactProfileNotFound = "recurring_plan_draft_setup_exact_profile_not_found";
    public const string ExactProfileMismatch = "recurring_plan_draft_setup_exact_profile_mismatch";
    public const string DurationMismatch = "recurring_plan_draft_setup_duration_mismatch";
    public const string ExistingOneShotPlan = "recurring_plan_draft_setup_existing_one_shot_plan";
    public const string ContentConflict = "recurring_plan_draft_setup_content_conflict";
    public const string IncompletePersistedData = "recurring_plan_draft_setup_incomplete_persisted_data";
    public const string PersistedDataInvalid = "recurring_plan_draft_setup_persisted_data_invalid";
    public const string ConcurrencyConflict = "recurring_plan_draft_setup_concurrency_conflict";
    public const string AtomicPersistenceFailed = "recurring_plan_draft_setup_atomic_persistence_failed";
}

public enum RecurringPlanDraftSetupFailurePoint
{
    AfterPlanInsert,
    AfterScheduleRevision1Insert,
    AfterBindingInsert,
    BeforeCommitAfterFinalRead,
}

public sealed record RecurringPlanDraftSetupSnapshot(
    PlanDefinition Plan,
    RecurringScheduleVersionSnapshot ScheduleVersion,
    RecurringPlanProfileBinding ProfileBinding,
    RecurringFixedRegionProfileVersion ExactProfile,
    RecurringPlanConfigurationRef ConfigurationRef)
{
    public RecurringScheduleVersionSnapshot Schedule => ScheduleVersion;

    public RecurringPlanProfileBinding Binding => ProfileBinding;

    public RecurringPlanConfigurationRef Configuration => ConfigurationRef;

    public string ConfigurationDigest => ConfigurationRef.ConfigurationDigest;
}

/// <summary>
/// Atomically creates or replays the first recurring draft setup. The Plan,
/// schedule revision 1, and exact profile binding are all committed together;
/// this transaction deliberately does not enable the Plan or create execution
/// state.
/// </summary>
public sealed class SqliteRecurringPlanDraftSetupTransaction : SqliteRepositoryBase
{
    private readonly Action<RecurringPlanDraftSetupFailurePoint>? _failureHook;

    public SqliteRecurringPlanDraftSetupTransaction(SqliteOperationalStore store)
        : this(store, failureHook: null)
    {
    }

    internal SqliteRecurringPlanDraftSetupTransaction(
        SqliteOperationalStore store,
        Action<RecurringPlanDraftSetupFailurePoint>? failureHook)
        : base(store)
    {
        _failureHook = failureHook;
    }

    public RecurringPlanDraftSetupSnapshot CreateOrGet(
        string planId,
        RecurringPlanSchedule schedule,
        ProfileRef profileRef,
        DateTimeOffset createdAtUtc)
    {
        var request = ValidateRequest(planId, schedule, profileRef, createdAtUtc);
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var existingPlan = ReadPlan(connection, transaction, request.PlanId);
            if (existingPlan is not null)
            {
                return ReplayExisting(
                    connection,
                    transaction,
                    existingPlan,
                    request);
            }

            var exactProfile = ReadExactProfile(
                connection,
                transaction,
                request.ProfileRef,
                persistedBinding: false);
            EnsureDurationMatches(request.Schedule, exactProfile);

            var plan = new PlanDefinition(request.PlanId, isOneTime: false, request.CreatedAtUtc);
            SqlitePlanDefinitionRepository.InsertWithinTransaction(connection, transaction, plan);
            InvokeFailure(RecurringPlanDraftSetupFailurePoint.AfterPlanInsert);

            SqliteRecurringScheduleVersionRepository.InsertSchedule(
                connection,
                transaction,
                request.PlanId,
                revision: 1,
                request.Schedule,
                request.TimeZoneRulesDigest,
                request.CreatedAtUtc.UtcDateTime.Ticks);
            InvokeFailure(RecurringPlanDraftSetupFailurePoint.AfterScheduleRevision1Insert);

            var binding = new RecurringPlanProfileBinding(
                request.PlanId,
                request.ProfileRef,
                request.CreatedAtUtc);
            SqliteRecurringPlanProfileBindingRepository.InsertWithinTransaction(
                connection,
                transaction,
                binding);
            InvokeFailure(RecurringPlanDraftSetupFailurePoint.AfterBindingInsert);

            var snapshot = ReadCompleteSetup(connection, transaction, request.PlanId, persistedBinding: true);
            EnsureSameConfiguration(snapshot, request);
            InvokeFailure(RecurringPlanDraftSetupFailurePoint.BeforeCommitAfterFinalRead);
            transaction.Commit();
            return snapshot;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (IsUniqueConstraint(exception))
        {
            TryRollback(transaction);
            return ResolveConcurrentCreate(request, exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.AtomicPersistenceFailed,
                "The recurring recurring-plan draft setup transaction failed.",
                exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.AtomicPersistenceFailed,
                "The recurring recurring-plan draft setup transaction failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private RecurringPlanDraftSetupSnapshot ReplayExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition existingPlan,
        SetupRequest request)
    {
        if (existingPlan.IsOneTime)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.ExistingOneShotPlan,
                "The existing plan is one-shot and cannot be a recurring draft setup.");
        }

        var snapshot = ReadCompleteSetup(connection, transaction, existingPlan.Id, persistedBinding: true);
        EnsureSameConfiguration(snapshot, request);
        transaction.Commit();
        return snapshot;
    }

    private RecurringPlanDraftSetupSnapshot ResolveConcurrentCreate(
        SetupRequest request,
        SqliteException cause)
    {
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var existingPlan = ReadPlan(connection, transaction, request.PlanId);
            if (existingPlan is null)
            {
                throw Failure(
                    RecurringPlanDraftSetupPersistenceReasonCodes.ConcurrencyConflict,
                    "The recurring draft setup conflicted before its winner became readable.",
                    cause);
            }

            if (existingPlan.IsOneTime)
            {
                throw Failure(
                    RecurringPlanDraftSetupPersistenceReasonCodes.ExistingOneShotPlan,
                    "The existing plan is one-shot and cannot be a recurring draft setup.",
                    cause);
            }

            var snapshot = ReadCompleteSetup(connection, transaction, request.PlanId, persistedBinding: true);
            EnsureSameConfiguration(snapshot, request, cause);
            transaction.Commit();
            return snapshot;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.ConcurrencyConflict,
                "The recurring draft setup conflicted with another writer.",
                exception);
        }
    }

    private static SetupRequest ValidateRequest(
        string planId,
        RecurringPlanSchedule schedule,
        ProfileRef profileRef,
        DateTimeOffset createdAtUtc)
    {
        if (schedule is null)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.InvalidArgument,
                "The recurring draft setup schedule is required.");
        }

        string canonicalPlanId;
        long createdTicks;
        try
        {
            canonicalPlanId = RequiredInput(planId);
            createdTicks = UtcTicksInput(createdAtUtc);
            profileRef.Validate();
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.InvalidArgument,
                "The recurring draft setup input is invalid.",
                exception);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.InvalidArgument,
                "The recurring draft setup input is invalid.",
                exception);
        }

        try
        {
            var rulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);
            var configuration = new RecurringPlanConfigurationRef(
                canonicalPlanId,
                scheduleRevision: 1,
                schedule.CanonicalDigest,
                rulesDigest,
                profileRef);
            return new SetupRequest(
                canonicalPlanId,
                schedule,
                profileRef,
                rulesDigest,
                configuration,
                new DateTimeOffset(createdTicks, TimeSpan.Zero));
        }
        catch (Phase3DomainException exception)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.InvalidArgument,
                "The recurring draft setup input is invalid.",
                exception);
        }
    }

    private RecurringPlanDraftSetupSnapshot ReadCompleteSetup(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        bool persistedBinding)
    {
        var plan = ReadPlan(connection, transaction, planId)
            ?? throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData,
                "The recurring draft setup is missing its Plan row.");
        if (plan.IsOneTime)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.ExistingOneShotPlan,
                "The persisted recurring draft setup points to a one-shot Plan.");
        }

        RecurringScheduleVersionSnapshot schedule;
        try
        {
            schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection,
                transaction,
                planId,
                scheduleRevision: 1)
                ?? throw Failure(
                    RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData,
                    "The recurring draft setup is missing schedule revision 1.");
        }
        catch (Phase3PersistenceException exception) when (
            exception.Code != RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring schedule revision 1 is invalid.",
                exception);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring schedule revision 1 is invalid.",
                exception);
        }

        RecurringPlanProfileBinding? binding;
        try
        {
            binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(
                connection,
                transaction,
                planId);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring draft setup profile binding is invalid.",
                exception);
        }
        if (binding is null)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData,
                "The recurring draft setup is missing its exact profile binding.");
        }

        RecurringFixedRegionProfileVersion profile;
        try
        {
            profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                connection,
                transaction,
                binding.ProfileRef);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                persistedBinding
                    ? RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid
                    : RecurringPlanDraftSetupPersistenceReasonCodes.ExactProfileMismatch,
                "The recurring draft setup exact profile is invalid.",
                exception);
        }

        if (!string.Equals(binding.PlanId, planId, StringComparison.Ordinal) ||
            !string.Equals(schedule.PlanId, planId, StringComparison.Ordinal))
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The recurring draft setup relations do not point to the requested Plan.");
        }

        EnsureDurationMatchesPersisted(schedule.Schedule, profile);
        RecurringPlanConfigurationRef configuration;
        try
        {
            configuration = new RecurringPlanConfigurationRef(
                planId,
                schedule.ScheduleRevision,
                schedule.ScheduleDigest,
                schedule.TimeZoneRulesDigest,
                binding.ProfileRef);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The recurring draft setup configuration reference is invalid.",
                exception);
        }

        return new RecurringPlanDraftSetupSnapshot(plan, schedule, binding, profile, configuration);
    }

    private static PlanDefinition? ReadPlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, is_one_time, status_code, created_at_utc, updated_at_utc, version FROM plans WHERE id = $planId LIMIT 1;";
        Add(command, "$planId", planId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        try
        {
            return PlanDefinition.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadBoolean(reader, 1),
                ParseStatus(reader, 2, Phase3StateCodes.ParsePlanDefinition),
                ReadUtcDateTimeOffset(reader, 3),
                ReadUtcDateTimeOffset(reader, 4),
                ReadInt64(reader, 5));
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring draft setup Plan is invalid.",
                exception);
        }
    }

    private static RecurringFixedRegionProfileVersion ReadExactProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileRef profileRef,
        bool persistedBinding)
    {
        try
        {
            return SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(connection, transaction, profileRef);
        }
        catch (Phase3PersistenceException exception)
        {
            var code = exception.Code switch
            {
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound =>
                    RecurringPlanDraftSetupPersistenceReasonCodes.ExactProfileNotFound,
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch =>
                    RecurringPlanDraftSetupPersistenceReasonCodes.ExactProfileMismatch,
                _ when persistedBinding => RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                _ => RecurringPlanDraftSetupPersistenceReasonCodes.ExactProfileMismatch,
            };
            throw Failure(code, "The exact recurring fixed-region profile could not be read.", exception);
        }
    }

    private static void EnsureDurationMatches(RecurringPlanSchedule schedule, RecurringFixedRegionProfileVersion profile)
    {
        if (schedule.RecordingDuration != profile.Duration)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.DurationMismatch,
                "The recurring schedule recording duration must exactly match the profile duration.");
        }
    }

    private static void EnsureDurationMatchesPersisted(RecurringPlanSchedule schedule, RecurringFixedRegionProfileVersion profile)
    {
        if (schedule.RecordingDuration != profile.Duration)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring schedule duration does not exactly match its profile.");
        }
    }

    private static void EnsureSameConfiguration(
        RecurringPlanDraftSetupSnapshot snapshot,
        SetupRequest request,
        Exception? cause = null)
    {
        var same = string.Equals(snapshot.ConfigurationRef.PlanId, request.Configuration.PlanId, StringComparison.Ordinal) &&
                   snapshot.ConfigurationRef.ScheduleRevision == request.Configuration.ScheduleRevision &&
                   string.Equals(snapshot.ConfigurationRef.ScheduleDigest, request.Configuration.ScheduleDigest, StringComparison.Ordinal) &&
                   string.Equals(snapshot.ConfigurationRef.TimeZoneRulesDigest, request.Configuration.TimeZoneRulesDigest, StringComparison.Ordinal) &&
                   SameReference(snapshot.ConfigurationRef.ProfileRef, request.Configuration.ProfileRef) &&
                   string.Equals(snapshot.ConfigurationRef.ConfigurationDigest, request.Configuration.ConfigurationDigest, StringComparison.Ordinal);
        if (!same)
        {
            throw Failure(
                RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict,
                "The recurring draft setup identity conflicts with the persisted immutable setup.",
                cause);
        }
    }

    private void InvokeFailure(RecurringPlanDraftSetupFailurePoint point) => _failureHook?.Invoke(point);

    private static bool SameReference(ProfileRef left, ProfileRef right) =>
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.ProfileVersion == right.ProfileVersion &&
        string.Equals(left.ProfileDigest, right.ProfileDigest, StringComparison.Ordinal);

    private static Phase3PersistenceException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

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

    private sealed record SetupRequest(
        string PlanId,
        RecurringPlanSchedule Schedule,
        ProfileRef ProfileRef,
        string TimeZoneRulesDigest,
        RecurringPlanConfigurationRef Configuration,
        DateTimeOffset CreatedAtUtc);
}
