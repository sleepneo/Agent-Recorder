using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringConsentLeasePersistenceReasonCodes
{
    public const string InvalidArgument = "recurring_lease_persistence_invalid_argument";
    public const string AlreadyExists = "recurring_lease_persistence_already_exists";
    public const string NotFound = "recurring_lease_persistence_not_found";
    public const string ListLimitInvalid = "recurring_lease_persistence_list_limit_invalid";
    public const string ListCursorInvalid = "recurring_lease_persistence_list_cursor_invalid";
    public const string PersistedDataInvalid = "recurring_lease_persistence_persisted_data_invalid";
    public const string StorageFailure = "recurring_lease_persistence_storage_failure";
}

public static class RecurringLeaseUseAccountingPersistenceReasonCodes
{
    public const string InvalidArgument = "recurring_lease_use_accounting_invalid_argument";
    public const string LeaseNotFound = "recurring_lease_use_accounting_lease_not_found";
    public const string PersistedDataInvalid = "recurring_lease_use_accounting_persisted_data_invalid";
    public const string StorageFailure = "recurring_lease_use_accounting_storage_failure";
}

public sealed class SqliteRecurringConsentLeaseRepository : SqliteRepositoryBase, IRecurringConsentLeaseRepository
{
    private const string TableName = "recurring_consent_leases";
    private const string SelectColumns = "lease_id, plan_id, schedule_revision, schedule_digest, time_zone_rules_digest, " +
        "profile_id, profile_version, profile_digest, configuration_digest, status_code, valid_from_utc, valid_until_utc, " +
        "authorized_plan_latest_end_utc, per_run_duration_ticks, max_uses, max_cumulative_duration_ticks, authorization_digest, " +
        "created_at_utc, updated_at_utc, version";

    public SqliteRecurringConsentLeaseRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void InsertPending(RecurringConsentLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Status != ConsentLeaseStatus.Pending || lease.Version != 0 || lease.UpdatedAtUtc != lease.CreatedAtUtc)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "Only a fresh pending recurring lease may be inserted.");
        }

        var leaseId = RequiredLeaseId(lease.LeaseId);
        var values = BuildLeaseValues(lease);
        using var connection = OpenLeaseConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {TableName} ({SelectColumns})
                VALUES ($leaseId, $planId, $scheduleRevision, $scheduleDigest, $timeZoneRulesDigest,
                        $profileId, $profileVersion, $profileDigest, $configurationDigest, $statusCode,
                        $validFromUtc, $validUntilUtc, $authorizedPlanLatestEndUtc, $perRunDurationTicks,
                        $maxUses, $maxCumulativeDurationTicks, $authorizationDigest, $createdAtUtc, $updatedAtUtc, $version);
                """;
            Add(command, "$leaseId", leaseId);
            Add(command, "$planId", values.PlanId);
            Add(command, "$scheduleRevision", values.ScheduleRevision);
            Add(command, "$scheduleDigest", values.ScheduleDigest);
            Add(command, "$timeZoneRulesDigest", values.TimeZoneRulesDigest);
            Add(command, "$profileId", values.ProfileId);
            Add(command, "$profileVersion", values.ProfileVersion);
            Add(command, "$profileDigest", values.ProfileDigest);
            Add(command, "$configurationDigest", values.ConfigurationDigest);
            Add(command, "$statusCode", values.StatusCode);
            Add(command, "$validFromUtc", values.ValidFromUtc);
            Add(command, "$validUntilUtc", values.ValidUntilUtc);
            Add(command, "$authorizedPlanLatestEndUtc", values.AuthorizedPlanLatestEndUtc);
            Add(command, "$perRunDurationTicks", values.PerRunDurationTicks);
            Add(command, "$maxUses", values.MaxUses);
            Add(command, "$maxCumulativeDurationTicks", values.MaxCumulativeDurationTicks);
            Add(command, "$authorizationDigest", values.AuthorizationDigest);
            Add(command, "$createdAtUtc", values.CreatedAtUtc);
            Add(command, "$updatedAtUtc", values.UpdatedAtUtc);
            Add(command, "$version", values.Version);
            if (command.ExecuteNonQuery() != 1)
            {
                throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease insert did not affect exactly one row.");
            }

            transaction.Commit();
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (IsSqliteConstraint(exception) && IsUniqueConstraint(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.AlreadyExists, "The recurring lease already exists.", exception);
        }
        catch (SqliteException exception) when (IsSqliteConstraint(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease violates a persisted relation or constraint.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease could not be persisted.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public RecurringConsentLease Get(string leaseId)
    {
        var lease = TryGet(leaseId);
        return lease ?? throw Failure(RecurringConsentLeasePersistenceReasonCodes.NotFound, "The recurring lease was not found.");
    }

    public RecurringConsentLease? TryGet(string leaseId)
    {
        leaseId = RequiredLeaseId(leaseId);
        using var connection = OpenLeaseConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var lease = ReadWithinTransaction(connection, transaction, leaseId);
            transaction.Commit();
            return lease;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease could not be read.", exception);
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring lease is invalid.", exception);
        }
    }

    public IReadOnlyList<RecurringConsentLease> ListByPlan(
        string planId,
        string? afterLeaseIdExclusive = null,
        int limit = 100)
    {
        planId = RequiredPlanId(planId);
        if (limit is < 1 or > 1000)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.ListLimitInvalid, "The recurring lease list limit must be between 1 and 1000.");
        }

        if (afterLeaseIdExclusive is not null)
        {
            try
            {
                afterLeaseIdExclusive = RequiredLeaseId(afterLeaseIdExclusive);
            }
            catch (Phase3PersistenceException exception)
            {
                throw Failure(RecurringConsentLeasePersistenceReasonCodes.ListCursorInvalid, "The recurring lease list cursor is invalid.", exception);
            }
        }

        using var connection = OpenLeaseConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT lease_id
                FROM {TableName}
                WHERE plan_id = $planId
                  AND ($afterLeaseIdExclusive IS NULL OR lease_id COLLATE BINARY > $afterLeaseIdExclusive COLLATE BINARY)
                ORDER BY lease_id COLLATE BINARY
                LIMIT $limit;
                """;
            Add(command, "$planId", planId);
            Add(command, "$afterLeaseIdExclusive", afterLeaseIdExclusive);
            Add(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            var leaseIds = new List<string>();
            while (reader.Read())
            {
                leaseIds.Add(ReadRequiredText(reader, 0));
            }

            reader.Close();
            var leases = new List<RecurringConsentLease>(leaseIds.Count);
            foreach (var leaseId in leaseIds)
            {
                var lease = ReadWithinTransaction(connection, transaction, leaseId)
                    ?? throw new PersistedSnapshotException("The recurring lease disappeared during a stable read.");
                if (!string.Equals(lease.PlanId, planId, StringComparison.Ordinal))
                {
                    throw new PersistedSnapshotException("The recurring lease list returned a row for another plan.");
                }

                leases.Add(lease);
            }

            transaction.Commit();
            return leases;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease list could not be read.", exception);
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.PersistedDataInvalid, "The recurring lease list contains invalid persisted data.", exception);
        }
    }

    internal static RecurringConsentLease? ReadWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE lease_id = $leaseId LIMIT 1;";
        Add(command, "$leaseId", leaseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var persistedLease = ReadSnapshot(reader);
        reader.Close();
        EnsureExactParents(connection, transaction, persistedLease);
        return persistedLease;
    }

    // The recurring setup preparation transaction owns the surrounding
    // immediate transaction. Do not route this through InsertPending, which
    // would create a second commit boundary.
    internal static void InsertWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Status != ConsentLeaseStatus.Pending || lease.Version != 0 || lease.UpdatedAtUtc != lease.CreatedAtUtc)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "Only a fresh pending recurring lease may be inserted.");
        }

        var values = BuildLeaseValues(lease);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {TableName} ({SelectColumns})
            VALUES ($leaseId, $planId, $scheduleRevision, $scheduleDigest, $timeZoneRulesDigest,
                    $profileId, $profileVersion, $profileDigest, $configurationDigest, $statusCode,
                    $validFromUtc, $validUntilUtc, $authorizedPlanLatestEndUtc, $perRunDurationTicks,
                    $maxUses, $maxCumulativeDurationTicks, $authorizationDigest, $createdAtUtc, $updatedAtUtc, $version);
            """;
        Add(command, "$leaseId", RequiredLeaseId(lease.LeaseId));
        Add(command, "$planId", values.PlanId);
        Add(command, "$scheduleRevision", values.ScheduleRevision);
        Add(command, "$scheduleDigest", values.ScheduleDigest);
        Add(command, "$timeZoneRulesDigest", values.TimeZoneRulesDigest);
        Add(command, "$profileId", values.ProfileId);
        Add(command, "$profileVersion", values.ProfileVersion);
        Add(command, "$profileDigest", values.ProfileDigest);
        Add(command, "$configurationDigest", values.ConfigurationDigest);
        Add(command, "$statusCode", values.StatusCode);
        Add(command, "$validFromUtc", values.ValidFromUtc);
        Add(command, "$validUntilUtc", values.ValidUntilUtc);
        Add(command, "$authorizedPlanLatestEndUtc", values.AuthorizedPlanLatestEndUtc);
        Add(command, "$perRunDurationTicks", values.PerRunDurationTicks);
        Add(command, "$maxUses", values.MaxUses);
        Add(command, "$maxCumulativeDurationTicks", values.MaxCumulativeDurationTicks);
        Add(command, "$authorizationDigest", values.AuthorizationDigest);
        Add(command, "$createdAtUtc", values.CreatedAtUtc);
        Add(command, "$updatedAtUtc", values.UpdatedAtUtc);
        Add(command, "$version", values.Version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease insert did not affect exactly one row.");
        }
    }

    // Internal aggregate CAS used by production lifecycle adapters that
    // perform a domain-approved terminal transition outside the setup
    // activation boundary. The recurring setup activation transaction keeps
    // its own exact write set and does not route through this method.
    internal void Update(RecurringConsentLease lease, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || lease.Version != expectedVersion + 1)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease update version does not describe one domain transition.");
        }

        using var connection = OpenLeaseConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {TableName}
                SET status_code = $statusCode, updated_at_utc = $updatedAtUtc, version = $newVersion
                WHERE lease_id = $leaseId AND plan_id = $planId
                  AND status_code = $expectedStatus AND version = $expectedVersion
                  AND schedule_revision = $scheduleRevision AND schedule_digest = $scheduleDigest
                  AND time_zone_rules_digest = $timeZoneRulesDigest
                  AND profile_id = $profileId AND profile_version = $profileVersion AND profile_digest = $profileDigest
                  AND configuration_digest = $configurationDigest AND valid_from_utc = $validFromUtc
                  AND valid_until_utc = $validUntilUtc AND authorized_plan_latest_end_utc = $authorizedPlanLatestEndUtc
                  AND per_run_duration_ticks = $perRunDurationTicks AND max_uses = $maxUses
                  AND max_cumulative_duration_ticks = $maxCumulativeDurationTicks
                  AND authorization_digest = $authorizationDigest AND created_at_utc = $createdAtUtc;
                """;
            Add(command, "$statusCode", lease.StatusCode);
            Add(command, "$updatedAtUtc", UtcTicksInput(lease.UpdatedAtUtc));
            Add(command, "$newVersion", lease.Version);
            Add(command, "$leaseId", RequiredLeaseId(lease.LeaseId));
            Add(command, "$planId", RequiredPlanId(lease.PlanId));
            var expectedStatus = lease.Status switch
            {
                ConsentLeaseStatus.Expired or ConsentLeaseStatus.Exhausted when expectedVersion == 1 => ConsentLeaseStatus.Active,
                ConsentLeaseStatus.Revoked when expectedVersion == 1 => ConsentLeaseStatus.Active,
                ConsentLeaseStatus.Revoked when expectedVersion == 2 => ConsentLeaseStatus.Exhausted,
                _ => throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease update target is outside the production transition shape."),
            };
            Add(command, "$expectedStatus", Phase3StateCodes.ToCode(expectedStatus));
            Add(command, "$expectedVersion", expectedVersion);
            Add(command, "$scheduleRevision", lease.ConfigurationRef.ScheduleRevision);
            Add(command, "$scheduleDigest", lease.ConfigurationRef.ScheduleDigest);
            Add(command, "$timeZoneRulesDigest", lease.ConfigurationRef.TimeZoneRulesDigest);
            Add(command, "$profileId", lease.ConfigurationRef.ProfileRef.ProfileId);
            Add(command, "$profileVersion", lease.ConfigurationRef.ProfileRef.ProfileVersion);
            Add(command, "$profileDigest", lease.ConfigurationRef.ProfileRef.ProfileDigest);
            Add(command, "$configurationDigest", lease.ConfigurationRef.ConfigurationDigest);
            Add(command, "$validFromUtc", UtcTicksInput(lease.ValidFromUtc));
            Add(command, "$validUntilUtc", UtcTicksInput(lease.ValidUntilUtc));
            Add(command, "$authorizedPlanLatestEndUtc", UtcTicksInput(lease.AuthorizedPlanLatestEndUtc));
            Add(command, "$perRunDurationTicks", lease.PerRunDuration.Ticks);
            Add(command, "$maxUses", lease.MaxUses);
            Add(command, "$maxCumulativeDurationTicks", lease.MaxCumulativeDuration.Ticks);
            Add(command, "$authorizationDigest", lease.AuthorizationDigest);
            Add(command, "$createdAtUtc", UtcTicksInput(lease.CreatedAtUtc));
            if (command.ExecuteNonQuery() != 1)
                throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease update did not affect exactly one row.");

            transaction.Commit();
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception) when (IsSqliteConstraint(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease update violated a persisted constraint.", exception);
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease could not be updated.", exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static RecurringConsentLease ReadSnapshot(SqliteDataReader reader)
    {
        var leaseId = ReadRequiredText(reader, 0);
        var planId = ReadRequiredText(reader, 1);
        var scheduleRevision = ReadPositiveInt64(reader, 2);
        var scheduleDigest = ReadRequiredText(reader, 3);
        var timeZoneRulesDigest = ReadRequiredText(reader, 4);
        var profileRef = new ProfileRef(
            ReadRequiredText(reader, 5),
            ReadPositiveInt64(reader, 6),
            ReadRequiredText(reader, 7));
        var configuration = RecurringPlanConfigurationRef.Rehydrate(
            planId,
            scheduleRevision,
            scheduleDigest,
            timeZoneRulesDigest,
            profileRef,
            ReadRequiredText(reader, 8));
        var status = ParseStatus(reader, 9, Phase3StateCodes.ParseConsentLease);
        var validFrom = ReadUtcDateTimeOffset(reader, 10);
        var validUntil = ReadUtcDateTimeOffset(reader, 11);
        var latestEnd = ReadUtcDateTimeOffset(reader, 12);
        var perRunDuration = ReadDurationTicks(reader, 13, positive: true);
        var maxUses = ReadInt64(reader, 14);
        var maxCumulativeDuration = ReadDurationTicks(reader, 15, positive: true);
        var authorizationDigest = ReadRequiredText(reader, 16);
        var createdAt = ReadUtcDateTimeOffset(reader, 17);
        var updatedAt = ReadUtcDateTimeOffset(reader, 18);
        var version = ReadInt64(reader, 19);

        if (version < 0)
        {
            throw new PersistedSnapshotException("The persisted recurring lease version is negative.");
        }

        try
        {
            return RecurringConsentLease.Rehydrate(
                leaseId,
                configuration,
                validFrom,
                validUntil,
                latestEnd,
                perRunDuration,
                maxUses,
                maxCumulativeDuration,
                authorizationDigest,
                status,
                createdAt,
                updatedAt,
                version);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("The persisted recurring lease is not a canonical domain snapshot.", exception);
        }
    }

    private static void EnsureExactParents(SqliteConnection connection, SqliteTransaction transaction, RecurringConsentLease lease)
    {
        using var scheduleCommand = connection.CreateCommand();
        scheduleCommand.Transaction = transaction;
        scheduleCommand.CommandText = """
            SELECT 1 FROM recurring_schedule_versions
            WHERE plan_id = $planId AND schedule_revision = $scheduleRevision
              AND schedule_digest = $scheduleDigest AND time_zone_rules_digest = $timeZoneRulesDigest
            LIMIT 1;
            """;
        Add(scheduleCommand, "$planId", lease.PlanId);
        Add(scheduleCommand, "$scheduleRevision", lease.ConfigurationRef.ScheduleRevision);
        Add(scheduleCommand, "$scheduleDigest", lease.ConfigurationRef.ScheduleDigest);
        Add(scheduleCommand, "$timeZoneRulesDigest", lease.ConfigurationRef.TimeZoneRulesDigest);
        if (scheduleCommand.ExecuteScalar() is null)
        {
            throw new PersistedSnapshotException("The recurring lease points to a missing exact schedule parent.");
        }

        using var profileCommand = connection.CreateCommand();
        profileCommand.Transaction = transaction;
        profileCommand.CommandText = """
            SELECT 1 FROM recurring_plan_profile_bindings
            WHERE plan_id = $planId AND profile_id = $profileId
              AND profile_version = $profileVersion AND profile_digest = $profileDigest
            LIMIT 1;
            """;
        Add(profileCommand, "$planId", lease.PlanId);
        Add(profileCommand, "$profileId", lease.ConfigurationRef.ProfileRef.ProfileId);
        Add(profileCommand, "$profileVersion", lease.ConfigurationRef.ProfileRef.ProfileVersion);
        Add(profileCommand, "$profileDigest", lease.ConfigurationRef.ProfileRef.ProfileDigest);
        if (profileCommand.ExecuteScalar() is null)
        {
            throw new PersistedSnapshotException("The recurring lease points to a missing exact profile binding.");
        }
    }

    private static LeaseValues BuildLeaseValues(RecurringConsentLease lease) => new(
        lease.PlanId,
        lease.ConfigurationRef.ScheduleRevision,
        lease.ConfigurationRef.ScheduleDigest,
        lease.ConfigurationRef.TimeZoneRulesDigest,
        lease.ConfigurationRef.ProfileRef.ProfileId,
        lease.ConfigurationRef.ProfileRef.ProfileVersion,
        lease.ConfigurationRef.ProfileRef.ProfileDigest,
        lease.ConfigurationRef.ConfigurationDigest,
        lease.StatusCode,
        UtcTicksInput(lease.ValidFromUtc),
        UtcTicksInput(lease.ValidUntilUtc),
        UtcTicksInput(lease.AuthorizedPlanLatestEndUtc),
        PositiveDurationTicks(lease.PerRunDuration),
        lease.MaxUses,
        PositiveDurationTicks(lease.MaxCumulativeDuration),
        lease.AuthorizationDigest,
        UtcTicksInput(lease.CreatedAtUtc),
        UtcTicksInput(lease.UpdatedAtUtc),
        lease.Version);

    private static long PositiveDurationTicks(TimeSpan duration) =>
        duration > TimeSpan.Zero ? duration.Ticks : throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease duration must be positive.");

    private static TimeSpan ReadDurationTicks(SqliteDataReader reader, int ordinal, bool positive)
    {
        var ticks = ReadInt64(reader, ordinal);
        if ((positive && ticks <= 0) || (!positive && ticks < 0) || ticks > TimeSpan.MaxValue.Ticks)
        {
            throw new PersistedSnapshotException("A recurring lease duration tick value is outside the TimeSpan range.");
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static long ReadPositiveInt64(SqliteDataReader reader, int ordinal)
    {
        var value = ReadInt64(reader, ordinal);
        return value > 0 ? value : throw new PersistedSnapshotException("A positive recurring lease integer was invalid.");
    }

    private static string RequiredLeaseId(string value)
    {
        try
        {
            return RequiredInput(value);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring lease identifier is invalid.", exception);
        }
    }

    private static string RequiredPlanId(string value)
    {
        try
        {
            return RequiredInput(value);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.InvalidArgument, "The recurring plan identifier is invalid.", exception);
        }
    }

    private SqliteConnection OpenLeaseConnection()
    {
        try
        {
            return OpenBusinessConnection();
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringConsentLeasePersistenceReasonCodes.StorageFailure, "The recurring lease store could not be opened.", exception);
        }
    }

    private static bool IsSnapshotException(Exception exception) =>
        exception is PersistedSnapshotException or Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException;

    private static bool IsSqliteConstraint(SqliteException exception) => exception.SqliteErrorCode == 19;

    private static Phase3PersistenceException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
        }
    }

    private sealed record LeaseValues(
        string PlanId,
        long ScheduleRevision,
        string ScheduleDigest,
        string TimeZoneRulesDigest,
        string ProfileId,
        long ProfileVersion,
        string ProfileDigest,
        string ConfigurationDigest,
        string StatusCode,
        long ValidFromUtc,
        long ValidUntilUtc,
        long AuthorizedPlanLatestEndUtc,
        long PerRunDurationTicks,
        long MaxUses,
        long MaxCumulativeDurationTicks,
        string AuthorizationDigest,
        long CreatedAtUtc,
        long UpdatedAtUtc,
        long Version);
}

public sealed class SqliteRecurringLeaseUseAccountingReader : SqliteRepositoryBase, IRecurringLeaseUseAccountingReader
{
    private const string TableName = "recurring_lease_uses";
    private const string SelectColumns = "use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, " +
        "reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";

    public SqliteRecurringLeaseUseAccountingReader(SqliteOperationalStore store)
        : base(store)
    {
    }

    public IReadOnlyList<RecurringLeaseUseAccountingEntry> ListByLease(string leaseId)
    {
        leaseId = RequiredLeaseId(leaseId);
        using var connection = OpenAccountingConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
                ?? throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.LeaseNotFound, "The recurring lease was not found.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT {SelectColumns}
                FROM {TableName}
                WHERE lease_id = $leaseId
                ORDER BY created_at_utc, use_id COLLATE BINARY;
                """;
            Add(command, "$leaseId", leaseId);
            using var reader = command.ExecuteReader();
            var entries = new List<RecurringLeaseUseAccountingEntry>();
            var occurrenceIdentities = new HashSet<string>(StringComparer.Ordinal);
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var entry = ReadAndValidateEntry(
                    connection,
                    transaction,
                    reader,
                    lease,
                    occurrenceIdentities,
                    runIds,
                    requireProductionVersionShape: false);
                entries.Add(entry);
            }

            reader.Close();
            transaction.Commit();
            return entries;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.StorageFailure, "The recurring accounting projection could not be read.", exception);
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring accounting projection is invalid.", exception);
        }
    }

    /// <summary>
    /// Reads the complete recurring accounting projection on a caller-owned
    /// connection and transaction. This is intentionally internal: production
    /// callers must not receive a load-then-consume two-step boundary.
    /// </summary>
    internal static IReadOnlyList<RecurringLeaseUseAccountingEntry> ReadAllWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        bool requireProductionVersionShape = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(lease);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM {TableName}
            WHERE lease_id = $leaseId
            ORDER BY created_at_utc, use_id COLLATE BINARY;
            """;
        Add(command, "$leaseId", lease.LeaseId);
        using var reader = command.ExecuteReader();
        var entries = new List<RecurringLeaseUseAccountingEntry>();
        var occurrenceIdentities = new HashSet<string>(StringComparer.Ordinal);
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            entries.Add(ReadAndValidateEntry(
                connection,
                transaction,
                reader,
                lease,
                occurrenceIdentities,
                runIds,
                requireProductionVersionShape));
        }

        return entries;
    }

    public RecurringLeaseUseAccountingEntry? TryGetByOccurrence(string occurrenceIdentity)
    {
        occurrenceIdentity = RequiredOccurrenceIdentity(occurrenceIdentity);
        using var connection = OpenAccountingConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE occurrence_identity = $occurrenceIdentity LIMIT 1;";
            Add(command, "$occurrenceIdentity", occurrenceIdentity);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            var leaseId = ReadRequiredText(reader, 1);
            reader.Close();
            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
                ?? throw new PersistedSnapshotException("The recurring accounting row points to a missing lease.");

            command.Dispose();
            using var entryCommand = connection.CreateCommand();
            entryCommand.Transaction = transaction;
            entryCommand.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE occurrence_identity = $occurrenceIdentity LIMIT 1;";
            Add(entryCommand, "$occurrenceIdentity", occurrenceIdentity);
            using var entryReader = entryCommand.ExecuteReader();
            if (!entryReader.Read())
            {
                throw new PersistedSnapshotException("The recurring accounting row disappeared during a stable read.");
            }

            var entry = ReadAndValidateEntry(
                connection,
                transaction,
                entryReader,
                lease,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                requireProductionVersionShape: false);
            entryReader.Close();
            transaction.Commit();
            return entry;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.StorageFailure, "The recurring accounting occurrence lookup failed.", exception);
        }
        catch (Exception exception) when (IsSnapshotException(exception))
        {
            TryRollback(transaction);
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring accounting row is invalid.", exception);
        }
    }

    private static RecurringLeaseUseAccountingEntry ReadAndValidateEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteDataReader reader,
        RecurringConsentLease lease,
        HashSet<string> occurrenceIdentities,
        HashSet<string> runIds,
        bool requireProductionVersionShape)
    {
        var useId = ReadRequiredText(reader, 0);
        var leaseId = ReadRequiredText(reader, 1);
        var planId = ReadRequiredText(reader, 2);
        var occurrenceIdentity = ReadRequiredText(reader, 3);
        var occurrenceId = ReadRequiredText(reader, 4);
        var runId = ReadRequiredText(reader, 5);
        var status = ParseStatus(reader, 6, Phase3StateCodes.ParseLeaseUse);
        var reservedCount = ReadInt32(reader, 7);
        var reservedDuration = ReadDurationTicks(reader, 8, positive: false);
        var actualDuration = ReadNullableDurationTicks(reader, 9);
        var createdAt = ReadUtcDateTimeOffset(reader, 10);
        var updatedAt = ReadUtcDateTimeOffset(reader, 11);
        var version = ReadInt64(reader, 12);

        if (!string.Equals(leaseId, lease.LeaseId, StringComparison.Ordinal) || !string.Equals(planId, lease.PlanId, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The recurring accounting row points to another lease or plan.");
        }

        if (!occurrenceIdentities.Add(occurrenceIdentity) || !runIds.Add(runId))
        {
            throw new PersistedSnapshotException("Recurring accounting evidence contains a duplicate occurrence or run.");
        }

        EnsureOccurrenceAndRunRelations(connection, transaction, occurrenceIdentity, planId, occurrenceId, runId);
        if (version < 0 || updatedAt < createdAt ||
            (requireProductionVersionShape && !IsLegalPersistedUseVersion(status, version)))
        {
            throw new PersistedSnapshotException("The recurring accounting version, state, or time is not a legal production shape.");
        }

        RecurringLeaseUseAccountingEntry entry;
        try
        {
            entry = RecurringLeaseUseAccountingEntry.Rehydrate(
                leaseId, useId, occurrenceIdentity, runId, status, reservedCount, reservedDuration, actualDuration);
            entry.ValidateAgainst(lease);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("The recurring accounting state evidence is invalid.", exception);
        }

        return entry;
    }

    private static bool IsLegalPersistedUseVersion(LeaseUseStatus status, long version) =>
        status switch
        {
            // Reservation inserts the durable row already in Reserved/v0;
            // the row is never persisted as the in-memory Available/v0 state.
            LeaseUseStatus.Reserved => version == 0,
            LeaseUseStatus.StartCommitted => version == 1,
            LeaseUseStatus.Consumed => version == 2,
            LeaseUseStatus.Settled => version == 3,
            // StartCommitted -> StartedUnknown and Consumed ->
            // StartedUnknown are the two production edges.
            LeaseUseStatus.StartedUnknown => version is 2 or 3,
            // A pre-start release is the only durable Available projection.
            LeaseUseStatus.Available => version == 1,
            _ => false,
        };

    private static void EnsureOccurrenceAndRunRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity,
        string planId,
        string occurrenceId,
        string runId)
    {
        using var occurrenceCommand = connection.CreateCommand();
        occurrenceCommand.Transaction = transaction;
        occurrenceCommand.CommandText = """
            SELECT 1 FROM recurring_occurrence_slots
            WHERE occurrence_identity = $occurrenceIdentity AND plan_id = $planId AND occurrence_id = $occurrenceId
            LIMIT 1;
            """;
        Add(occurrenceCommand, "$occurrenceIdentity", occurrenceIdentity);
        Add(occurrenceCommand, "$planId", planId);
        Add(occurrenceCommand, "$occurrenceId", occurrenceId);
        if (occurrenceCommand.ExecuteScalar() is null)
        {
            throw new PersistedSnapshotException("The recurring accounting row points to a missing exact occurrence slot.");
        }

        using var runCommand = connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = "SELECT 1 FROM recording_runs WHERE id = $runId AND occurrence_id = $occurrenceId LIMIT 1;";
        Add(runCommand, "$runId", runId);
        Add(runCommand, "$occurrenceId", occurrenceId);
        if (runCommand.ExecuteScalar() is null)
        {
            throw new PersistedSnapshotException("The recurring accounting row points to a missing exact recording run.");
        }
    }

    private static TimeSpan ReadDurationTicks(SqliteDataReader reader, int ordinal, bool positive)
    {
        var ticks = ReadInt64(reader, ordinal);
        if ((positive && ticks <= 0) || (!positive && ticks < 0) || ticks > TimeSpan.MaxValue.Ticks)
        {
            throw new PersistedSnapshotException("A recurring accounting duration tick value is outside the TimeSpan range.");
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan? ReadNullableDurationTicks(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadDurationTicks(reader, ordinal, positive: false);
    }

    private static string RequiredLeaseId(string value)
    {
        try
        {
            return RequiredInput(value);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.InvalidArgument, "The recurring lease identifier is invalid.", exception);
        }
    }

    private static string RequiredOccurrenceIdentity(string value)
    {
        try
        {
            return RequiredInput(value);
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.InvalidArgument, "The recurring occurrence identity is invalid.", exception);
        }
    }

    private SqliteConnection OpenAccountingConnection()
    {
        try
        {
            return OpenBusinessConnection();
        }
        catch (Phase3PersistenceException exception)
        {
            throw Failure(RecurringLeaseUseAccountingPersistenceReasonCodes.StorageFailure, "The recurring accounting store could not be opened.", exception);
        }
    }

    private static bool IsSnapshotException(Exception exception) =>
        exception is PersistedSnapshotException or Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException;

    private static Phase3PersistenceException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
        }
    }
}
