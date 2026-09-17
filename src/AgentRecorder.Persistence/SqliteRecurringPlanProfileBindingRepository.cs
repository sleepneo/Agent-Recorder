using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringPlanProfileBindingPersistenceReasonCodes
{
    public const string InvalidArgument = "invalid_argument";
    public const string PlanNotFound = "plan_not_found";
    public const string PlanNotPeriodic = "plan_not_periodic";
    public const string PlanNotDraft = "plan_not_draft";
    public const string ProfileNotFound = "profile_not_found";
    public const string ProfileRefMismatch = "profile_ref_mismatch";
    public const string BoundToOtherProfile = "binding_bound_to_other_profile";
    public const string PersistedDataInvalid = "binding_persisted_data_invalid";
    public const string ConcurrencyConflict = "binding_concurrency_conflict";
    public const string StorageFailure = "binding_storage_failure";
    public const string BindingNotFound = "binding_not_found";
    public const string ListLimitInvalid = "binding_list_limit_invalid";
    public const string ListCursorInvalid = "binding_list_cursor_invalid";
}

/// <summary>
/// Stores one immutable exact profile reference per recurring plan. The
/// initial bind is the only operation subject to the draft gate; retries read
/// the existing immutable row and are idempotent after a plan is enabled,
/// paused, or cancelled.
/// </summary>
public sealed class SqliteRecurringPlanProfileBindingRepository : SqliteRepositoryBase, IRecurringPlanProfileBindingRepository
{
    private const string TableName = "recurring_plan_profile_bindings";
    private const string SelectColumns = "plan_id, profile_id, profile_version, profile_digest, bound_at_utc";

    public SqliteRecurringPlanProfileBindingRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public RecurringPlanProfileBinding Bind(string planId, ProfileRef profileRef, DateTimeOffset boundAtUtc)
    {
        planId = RequiredPlanId(planId);
        var boundTicks = UtcTicksInput(boundAtUtc);
        using var connection = OpenBindingConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var plan = ReadPlan(connection, transaction, planId);
            if (plan is null)
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.PlanNotFound,
                    "The recurring plan was not found.");
            }

            var existing = ReadBinding(connection, transaction, planId);
            if (existing is not null)
            {
                EnsurePersistedPeriodicPlan(plan);
                if (!SameReference(existing.ProfileRef, profileRef))
                {
                    throw Failure(
                        RecurringPlanProfileBindingPersistenceReasonCodes.BoundToOtherProfile,
                        "The recurring plan is already bound to a different exact profile reference.");
                }

                ReadProfileForPersistedBinding(connection, transaction, existing.ProfileRef);
                transaction.Commit();
                return existing;
            }

            ReadProfileForInitialBinding(connection, transaction, profileRef);
            if (plan.IsOneTime)
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.PlanNotPeriodic,
                    "Only recurring plans may bind a fixed-region profile.");
            }

            if (plan.Status != PlanDefinitionStatus.Draft)
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.PlanNotDraft,
                    "A profile may only be initially bound while the recurring plan is draft.");
            }

            var binding = RecurringPlanProfileBinding.Rehydrate(
                planId,
                profileRef,
                new DateTimeOffset(boundTicks, TimeSpan.Zero));
            Insert(connection, transaction, binding);
            transaction.Commit();
            return binding;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (IsUniqueConstraint(exception))
        {
            TryRollback(transaction);
            return ResolveConcurrentBind(planId, profileRef, exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                "The recurring plan profile binding transaction failed.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The recurring plan profile binding could not be reconstructed as canonical data.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public RecurringPlanProfileBinding Get(string planId)
    {
        var binding = TryGet(planId);
        return binding ?? throw Failure(
            RecurringPlanProfileBindingPersistenceReasonCodes.BindingNotFound,
            "The recurring plan profile binding was not found.");
    }

    public RecurringPlanProfileBinding GetByPlanId(string planId) => Get(planId);

    public RecurringPlanProfileBinding? TryGet(string planId)
    {
        planId = RequiredPlanId(planId);
        using var connection = OpenBindingConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var binding = ReadBinding(connection, transaction, planId);
            if (binding is null)
            {
                transaction.Commit();
                return null;
            }

            EnsurePersistedPeriodicPlan(ReadPlan(connection, transaction, planId));

            ReadProfileForPersistedBinding(connection, transaction, binding.ProfileRef);
            transaction.Commit();
            return binding;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                "The recurring plan profile binding could not be read.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring plan profile binding is invalid.",
                exception);
        }
    }

    public RecurringPlanProfileBinding? TryGetByPlanId(string planId) => TryGet(planId);

    public IReadOnlyList<string> ListReferencingPlanIds(
        ProfileRef profileRef,
        string? beforePlanIdExclusive = null,
        int limit = 100)
    {
        if (limit is < 1 or > 100)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.ListLimitInvalid,
                "The binding list limit must be between 1 and 100.");
        }

        if (beforePlanIdExclusive is not null)
        {
            try
            {
                beforePlanIdExclusive = RequiredPlanId(beforePlanIdExclusive);
            }
            catch (Phase3PersistenceException exception)
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.ListCursorInvalid,
                    "The binding list cursor is invalid.",
                    exception);
            }
        }

        using var connection = OpenBindingConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            ReadProfileForInitialBinding(connection, transaction, profileRef);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT plan_id
                FROM {TableName}
                WHERE profile_id = $profileId
                  AND profile_version = $profileVersion
                  AND profile_digest = $profileDigest
                  AND ($beforePlanIdExclusive IS NULL OR plan_id < $beforePlanIdExclusive)
                ORDER BY plan_id DESC
                LIMIT $limit;
                """;
            Add(command, "$profileId", profileRef.ProfileId);
            Add(command, "$profileVersion", profileRef.ProfileVersion);
            Add(command, "$profileDigest", profileRef.ProfileDigest);
            Add(command, "$beforePlanIdExclusive", beforePlanIdExclusive is null ? DBNull.Value : beforePlanIdExclusive);
            Add(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            var planIds = new List<string>();
            while (reader.Read())
            {
                planIds.Add(ReadRequiredText(reader, 0));
            }

            reader.Close();
            foreach (var planId in planIds)
            {
                EnsurePersistedPeriodicPlan(ReadPlan(connection, transaction, planId));
            }

            transaction.Commit();
            return planIds;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                "The recurring plan profile binding list could not be read.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            TryRollback(transaction);
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The recurring plan profile binding list is invalid.",
                exception);
        }
    }

    public IReadOnlyList<string> ListReferencingPlanIdsBefore(
        ProfileRef profileRef,
        string? beforePlanIdExclusive = null,
        int limit = 100) => ListReferencingPlanIds(profileRef, beforePlanIdExclusive, limit);

    internal static RecurringPlanProfileBinding? ReadWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId) => ReadBinding(connection, transaction, planId);

    internal static void InsertWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringPlanProfileBinding binding) => Insert(connection, transaction, binding);

    private RecurringPlanProfileBinding ResolveConcurrentBind(
        string planId,
        ProfileRef profileRef,
        SqliteException cause)
    {
        try
        {
            var existing = TryGet(planId);
            if (existing is null)
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.ConcurrencyConflict,
                    "The binding changed during a concurrent write.",
                    cause);
            }

            if (!SameReference(existing.ProfileRef, profileRef))
            {
                throw Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.BoundToOtherProfile,
                    "The recurring plan is already bound to a different exact profile reference.",
                    cause);
            }

            return existing;
        }
        catch (Phase3PersistenceException exception) when (exception.Code == RecurringPlanProfileBindingPersistenceReasonCodes.BindingNotFound)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.ConcurrencyConflict,
                "The binding changed during a concurrent write.",
                cause);
        }
    }

    private static RecurringPlanProfileBinding? ReadBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE plan_id = $planId LIMIT 1;";
        Add(command, "$planId", planId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        try
        {
            return RecurringPlanProfileBinding.Rehydrate(
                ReadRequiredText(reader, 0),
                new ProfileRef(
                    ReadRequiredText(reader, 1),
                    ReadInt64(reader, 2),
                    ReadRequiredText(reader, 3)),
                ReadUtcDateTimeOffset(reader, 4));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException or Phase3DomainException or PersistedSnapshotException)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring plan profile binding is invalid.",
                exception);
        }
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
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException or Phase3DomainException or PersistedSnapshotException)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted recurring plan is invalid.",
                exception);
        }
    }

    private static PlanDefinition EnsurePersistedPeriodicPlan(PlanDefinition? plan)
    {
        if (plan is null)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The binding points to a missing recurring plan.");
        }

        if (plan.IsOneTime)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The binding points to a plan that is no longer periodic.");
        }

        return plan;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringPlanProfileBinding binding)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {TableName} ({SelectColumns})
            VALUES ($planId, $profileId, $profileVersion, $profileDigest, $boundAtUtc);
            """;
        Add(command, "$planId", binding.PlanId);
        Add(command, "$profileId", binding.ProfileRef.ProfileId);
        Add(command, "$profileVersion", binding.ProfileRef.ProfileVersion);
        Add(command, "$profileDigest", binding.ProfileRef.ProfileDigest);
        Add(command, "$boundAtUtc", binding.BoundAtUtc.UtcDateTime.Ticks);
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                "The recurring plan profile binding insert did not affect exactly one row.");
        }
    }

    private static void ReadProfileForInitialBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileRef profileRef)
    {
        try
        {
            SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(connection, transaction, profileRef);
        }
        catch (Phase3PersistenceException exception)
        {
            throw exception.Code switch
            {
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound => Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.ProfileNotFound,
                    "The exact recurring profile version was not found.",
                    exception),
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch => Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.ProfileRefMismatch,
                    "The exact recurring profile reference does not match the persisted version.",
                    exception),
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid => Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                    "The exact recurring profile version is invalid.",
                    exception),
                _ => Failure(
                    RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                    "The exact recurring profile version could not be read.",
                    exception),
            };
        }
    }

    private static void ReadProfileForPersistedBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileRef profileRef)
    {
        try
        {
            SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(connection, transaction, profileRef);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid,
                "The persisted binding does not point to a valid exact profile version.",
                exception);
        }
    }

    private SqliteConnection OpenBindingConnection()
    {
        try
        {
            return OpenBusinessConnection();
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.StorageFailure,
                "The recurring plan profile binding store could not be opened.",
                exception);
        }
    }

    private static string RequiredPlanId(string planId)
    {
        try
        {
            return RequiredInput(planId);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(
                RecurringPlanProfileBindingPersistenceReasonCodes.InvalidArgument,
                "The recurring plan identifier is invalid.",
                exception);
        }
    }

    private static bool SameReference(ProfileRef left, ProfileRef right) =>
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.ProfileVersion == right.ProfileVersion &&
        string.Equals(left.ProfileDigest, right.ProfileDigest, StringComparison.Ordinal);

    private static Phase3PersistenceException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private static new bool IsUniqueConstraint(SqliteException exception) =>
        exception.SqliteErrorCode == 19 &&
        exception.SqliteExtendedErrorCode is 1555 or 2067 or 2579;

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
}
