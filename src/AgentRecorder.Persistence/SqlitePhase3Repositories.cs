using System.Data;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed class PersistedSnapshotException : Exception
{
    public PersistedSnapshotException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public abstract class SqliteRepositoryBase
{
    private protected SqliteRepositoryBase(SqliteOperationalStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    private protected SqliteOperationalStore Store { get; }

    private protected SqliteConnection OpenBusinessConnection()
    {
        try
        {
            return Store.OpenConnection();
        }
        catch (Exception exception) when (exception is SqliteOperationalStoreException or SqliteException or IOException or UnauthorizedAccessException)
        {
            throw InfrastructureFailure(exception);
        }
    }

    private protected static SqliteTransaction BeginWriteTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

    private protected static SqliteTransaction BeginReadTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

    private protected static void ExecuteWrite(
        SqliteConnection connection,
        Action<SqliteTransaction> operation,
        bool duplicateIsAlreadyExists)
    {
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            operation(transaction);
            transaction.Commit();
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsConstraint(exception))
        {
            throw new Phase3PersistenceException(
                duplicateIsAlreadyExists && IsUniqueConstraint(exception)
                    ? "already_exists"
                    : "constraint_violation",
                "The SQLite operation violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private protected static Phase3PersistenceException InfrastructureFailure(Exception exception) =>
        new("sqlite_failure", "The SQLite persistence operation failed.", exception);

    private protected static Phase3PersistenceException NotFound() =>
        new("not_found", "The requested persisted aggregate was not found.");

    private protected static Phase3PersistenceException InvalidArgument(string reason = "The aggregate snapshot is not valid for persistence.") =>
        new("invalid_argument", reason);

    private protected static Phase3PersistenceException InvalidVersion() =>
        new("invalid_version", "The new aggregate version must be exactly one greater than expected.");

    private protected static Phase3PersistenceException ConcurrencyConflict() =>
        new("concurrency_conflict", "The aggregate was changed by another writer.");

    private protected static Phase3PersistenceException ImmutableMismatch() =>
        new("immutable_mismatch", "The persisted aggregate identity or immutable scope no longer matches.");

    private protected static Phase3PersistenceException InvalidSnapshot(Exception exception) =>
        new("persisted_snapshot_invalid", "The persisted aggregate snapshot is invalid.", exception);

    private protected static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private protected static string RequiredInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw InvalidArgument();
        }

        return value;
    }

    private protected static string? NullableInput(string? value) => value is null ? null : RequiredInput(value);

    private protected static long UtcTicksInput(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw InvalidArgument();
        }

        var ticks = value.UtcDateTime.Ticks;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw InvalidArgument();
        }

        return ticks;
    }

    private protected static long DurationMillisecondsInput(TimeSpan value)
    {
        if (value < TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new Phase3PersistenceException(
                "duration_not_representable",
                "The duration must be non-negative and exactly representable in milliseconds.");
        }

        return value.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private protected static void ValidateExpectedVersion(long actualVersion, long expectedVersion)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || actualVersion != expectedVersion + 1)
        {
            throw InvalidVersion();
        }
    }

    private protected static long ReadInt64(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is not long integer)
        {
            throw new PersistedSnapshotException("An INTEGER field did not contain an Int64 value.");
        }

        return integer;
    }

    private protected static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        var value = ReadInt64(reader, ordinal);
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new PersistedSnapshotException("An INTEGER field was outside the Int32 range.");
        }

        return (int)value;
    }

    private protected static bool ReadBoolean(SqliteDataReader reader, int ordinal)
    {
        var value = ReadInt64(reader, ordinal);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new PersistedSnapshotException("A boolean field was not encoded as 0 or 1."),
        };
    }

    private protected static string ReadRequiredText(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is not string text || string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("A required TEXT field was null, blank, or non-canonical.");
        }

        return text;
    }

    private protected static string? ReadNullableText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        if (value is not string text || string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("A nullable TEXT field was not null, blank, or non-canonical.");
        }

        return text;
    }

    private protected static DateTimeOffset ReadUtcDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        var ticks = ReadInt64(reader, ordinal);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new PersistedSnapshotException("A UTC tick value was outside the DateTime range.");
        }

        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private protected static DateTimeOffset? ReadNullableUtcDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadUtcDateTimeOffset(reader, ordinal);
    }

    private protected static TimeSpan ReadDurationMilliseconds(SqliteDataReader reader, int ordinal)
    {
        var milliseconds = ReadInt64(reader, ordinal);
        var maxMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds < 0 || milliseconds > maxMilliseconds)
        {
            throw new PersistedSnapshotException("A duration millisecond value was outside the TimeSpan range.");
        }

        return TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));
    }

    private protected static TimeSpan? ReadNullableDurationMilliseconds(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadDurationMilliseconds(reader, ordinal);
    }

    private protected static T ParseStatus<T>(SqliteDataReader reader, int ordinal, Func<string, T> parse)
    {
        var code = ReadRequiredText(reader, ordinal);
        try
        {
            return parse(code);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("A persisted status code was not recognized.", exception);
        }
    }

    private protected static PlanDefinition ReadPlanDefinitionSnapshot(SqliteDataReader reader) =>
        PlanDefinition.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadBoolean(reader, 1),
            ParseStatus(reader, 2, Phase3StateCodes.ParsePlanDefinition),
            ReadUtcDateTimeOffset(reader, 3),
            ReadUtcDateTimeOffset(reader, 4),
            ReadInt64(reader, 5));

    private protected static PlanOccurrence ReadPlanOccurrenceSnapshot(SqliteDataReader reader) =>
        PlanOccurrence.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 1),
            ReadUtcDateTimeOffset(reader, 3),
            ReadUtcDateTimeOffset(reader, 4),
            ReadUtcDateTimeOffset(reader, 7),
            ParseStatus(reader, 2, Phase3StateCodes.ParsePlanOccurrence),
            ReadNullableText(reader, 5),
            ReadNullableText(reader, 6),
            ReadUtcDateTimeOffset(reader, 8),
            ReadInt64(reader, 9));

    private protected static RecordingRun ReadRecordingRunSnapshot(SqliteDataReader reader) =>
        RecordingRun.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 1),
            ReadUtcDateTimeOffset(reader, 7),
            ParseStatus(reader, 2, Phase3StateCodes.ParseRecordingRun),
            ReadBoolean(reader, 3),
            ReadNullableText(reader, 4),
            ReadNullableText(reader, 5),
            ReadNullableText(reader, 6),
            ReadUtcDateTimeOffset(reader, 8),
            ReadInt64(reader, 9));

    private protected static ConsentLease ReadConsentLeaseSnapshot(SqliteDataReader reader) =>
        ConsentLease.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 1),
            ReadRequiredText(reader, 2),
            ReadUtcDateTimeOffset(reader, 4),
            ReadUtcDateTimeOffset(reader, 5),
            ReadInt32(reader, 6),
            ReadDurationMilliseconds(reader, 7),
            ParseStatus(reader, 3, Phase3StateCodes.ParseConsentLease),
            ReadUtcDateTimeOffset(reader, 8),
            ReadInt64(reader, 9));

    private protected static LeaseUse ReadLeaseUseSnapshot(SqliteDataReader reader) =>
        LeaseUse.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 1),
            ReadRequiredText(reader, 2),
            ReadRequiredText(reader, 3),
            ReadUtcDateTimeOffset(reader, 8),
            ParseStatus(reader, 4, Phase3StateCodes.ParseLeaseUse),
            ReadInt32(reader, 5),
            ReadDurationMilliseconds(reader, 6),
            ReadNullableDurationMilliseconds(reader, 7),
            ReadUtcDateTimeOffset(reader, 9),
            ReadInt64(reader, 10));

    private protected static void EnsureExists(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }
        if (command.ExecuteScalar() is null)
        {
            throw new PersistedSnapshotException("A persisted relation points to a missing aggregate.");
        }
    }

    private protected static void EnsureRowsAffected(int affectedRows)
    {
        if (affectedRows != 1)
        {
            throw InfrastructureFailure(new InvalidOperationException("The aggregate update did not affect exactly one row."));
        }
    }

    private protected static void EnsureImmutableUpdateResult(
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
        var value = command.ExecuteScalar();
        if (value is null)
        {
            throw NotFound();
        }

        if (value is not long currentVersion)
        {
            throw new PersistedSnapshotException("The persisted version was not an Int64 value.");
        }

        throw currentVersion == expectedVersion ? ImmutableMismatch() : ConcurrencyConflict();
    }

    private static bool IsConstraint(SqliteException exception) => exception.SqliteErrorCode == 19;

    private protected static bool IsUniqueConstraint(SqliteException exception) => exception.SqliteExtendedErrorCode is 1555 or 2067 or 2579;
}

public sealed class SqlitePlanDefinitionRepository : SqliteRepositoryBase, IPlanDefinitionRepository
{
    private const string SelectColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";

    public SqlitePlanDefinitionRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(PlanDefinition snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = RequiredInput(snapshot.Id);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO plans ({SelectColumns}) VALUES ($id, $is_one_time, $status_code, $created_at_utc, $updated_at_utc, $version);";
            Add(command, "$id", id);
            Add(command, "$is_one_time", snapshot.IsOneTime ? 1L : 0L);
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$created_at_utc", createdTicks);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$version", snapshot.Version);
            EnsureRowsAffected(command.ExecuteNonQuery());
        }, duplicateIsAlreadyExists: true);
    }

    internal static void InsertWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO plans ({SelectColumns}) VALUES ($id, $is_one_time, $status_code, $created_at_utc, $updated_at_utc, $version);";
        Add(command, "$id", RequiredInput(snapshot.Id));
        Add(command, "$is_one_time", snapshot.IsOneTime ? 1L : 0L);
        Add(command, "$status_code", snapshot.StatusCode);
        Add(command, "$created_at_utc", UtcTicksInput(snapshot.CreatedAtUtc));
        Add(command, "$updated_at_utc", UtcTicksInput(snapshot.UpdatedAtUtc));
        Add(command, "$version", snapshot.Version);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    public PlanDefinition Get(string id)
    {
        id = RequiredInput(id);
        using var connection = OpenBusinessConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM plans WHERE id = $id;";
            Add(command, "$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw NotFound();
            }

            return ReadPlanDefinitionSnapshot(reader);
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
    }

    public void Update(PlanDefinition snapshot, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedVersion(snapshot.Version, expectedVersion);
        var id = RequiredInput(snapshot.Id);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE plans SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND is_one_time = $is_one_time AND created_at_utc = $created_at_utc;";
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$new_version", snapshot.Version);
            Add(command, "$id", id);
            Add(command, "$expected_version", expectedVersion);
            Add(command, "$is_one_time", snapshot.IsOneTime ? 1L : 0L);
            Add(command, "$created_at_utc", createdTicks);
            var affectedRows = command.ExecuteNonQuery();
            if (affectedRows == 0)
            {
                EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM plans WHERE id = $id;", id, expectedVersion);
            }

            EnsureRowsAffected(affectedRows);
        }, duplicateIsAlreadyExists: false);
    }
}

public sealed class SqlitePlanOccurrenceRepository : SqliteRepositoryBase, IPlanOccurrenceRepository
{
    private const string SelectColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";

    public SqlitePlanOccurrenceRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(PlanOccurrence snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = RequiredInput(snapshot.Id);
        var planId = RequiredInput(snapshot.PlanId);
        var runId = NullableInput(snapshot.RunId);
        var reason = NullableInput(snapshot.TerminalReasonCode);
        var windowStartTicks = UtcTicksInput(snapshot.WindowStartUtc);
        var windowEndTicks = UtcTicksInput(snapshot.WindowEndUtc);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO plan_occurrences ({SelectColumns}) VALUES ($id, $plan_id, $status_code, $window_start_utc, $window_end_utc, $run_id, $terminal_reason_code, $created_at_utc, $updated_at_utc, $version);";
            Add(command, "$id", id);
            Add(command, "$plan_id", planId);
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$window_start_utc", windowStartTicks);
            Add(command, "$window_end_utc", windowEndTicks);
            Add(command, "$run_id", runId);
            Add(command, "$terminal_reason_code", reason);
            Add(command, "$created_at_utc", createdTicks);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$version", snapshot.Version);
            EnsureRowsAffected(command.ExecuteNonQuery());
        }, duplicateIsAlreadyExists: true);
    }

    internal static void InsertWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO plan_occurrences ({SelectColumns}) VALUES ($id, $plan_id, $status_code, $window_start_utc, $window_end_utc, $run_id, $terminal_reason_code, $created_at_utc, $updated_at_utc, $version);";
        Add(command, "$id", RequiredInput(snapshot.Id));
        Add(command, "$plan_id", RequiredInput(snapshot.PlanId));
        Add(command, "$status_code", snapshot.StatusCode);
        Add(command, "$window_start_utc", UtcTicksInput(snapshot.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(snapshot.WindowEndUtc));
        Add(command, "$run_id", NullableInput(snapshot.RunId));
        Add(command, "$terminal_reason_code", NullableInput(snapshot.TerminalReasonCode));
        Add(command, "$created_at_utc", UtcTicksInput(snapshot.CreatedAtUtc));
        Add(command, "$updated_at_utc", UtcTicksInput(snapshot.UpdatedAtUtc));
        Add(command, "$version", snapshot.Version);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    public PlanOccurrence Get(string id)
    {
        id = RequiredInput(id);
        using var connection = OpenBusinessConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM plan_occurrences WHERE id = $id;";
            Add(command, "$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw NotFound();
            }

            var occurrence = ReadPlanOccurrenceSnapshot(reader);

            EnsureExists(connection, "SELECT 1 FROM plans WHERE id = $id;", ("$id", occurrence.PlanId));
            if (occurrence.RunId is not null)
            {
                EnsureExists(connection, "SELECT 1 FROM recording_runs WHERE id = $run_id AND occurrence_id = $occurrence_id;", ("$run_id", occurrence.RunId), ("$occurrence_id", occurrence.Id));
            }

            return occurrence;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
    }

    public void Update(PlanOccurrence snapshot, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedVersion(snapshot.Version, expectedVersion);
        var id = RequiredInput(snapshot.Id);
        var planId = RequiredInput(snapshot.PlanId);
        var runId = NullableInput(snapshot.RunId);
        var reason = NullableInput(snapshot.TerminalReasonCode);
        var windowStartTicks = UtcTicksInput(snapshot.WindowStartUtc);
        var windowEndTicks = UtcTicksInput(snapshot.WindowEndUtc);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc;";
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$run_id", runId);
            Add(command, "$terminal_reason_code", reason);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$new_version", snapshot.Version);
            Add(command, "$id", id);
            Add(command, "$expected_version", expectedVersion);
            Add(command, "$plan_id", planId);
            Add(command, "$window_start_utc", windowStartTicks);
            Add(command, "$window_end_utc", windowEndTicks);
            Add(command, "$created_at_utc", createdTicks);
            var affectedRows = command.ExecuteNonQuery();
            if (affectedRows == 0)
            {
                EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", id, expectedVersion);
            }

            EnsureRowsAffected(affectedRows);
        }, duplicateIsAlreadyExists: false);
    }
}

public sealed class SqliteRecordingRunRepository : SqliteRepositoryBase, IRecordingRunRepository
{
    private const string SelectColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";

    public SqliteRecordingRunRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(RecordingRun snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = RequiredInput(snapshot.Id);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var artifactId = NullableInput(snapshot.MediaArtifactId);
        var bundleId = NullableInput(snapshot.BundleId);
        var reason = NullableInput(snapshot.TerminalReasonCode);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO recording_runs ({SelectColumns}) VALUES ($id, $occurrence_id, $status_code, $has_crossed_start_commit, $media_artifact_id, $bundle_id, $terminal_reason_code, $created_at_utc, $updated_at_utc, $version);";
            Add(command, "$id", id);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$has_crossed_start_commit", snapshot.HasCrossedStartCommit ? 1L : 0L);
            Add(command, "$media_artifact_id", artifactId);
            Add(command, "$bundle_id", bundleId);
            Add(command, "$terminal_reason_code", reason);
            Add(command, "$created_at_utc", createdTicks);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$version", snapshot.Version);
            EnsureRowsAffected(command.ExecuteNonQuery());
        }, duplicateIsAlreadyExists: true);
    }

    public RecordingRun Get(string id)
    {
        id = RequiredInput(id);
        using var connection = OpenBusinessConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM recording_runs WHERE id = $id;";
            Add(command, "$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw NotFound();
            }

            var run = ReadRecordingRunSnapshot(reader);

            EnsureExists(connection, "SELECT 1 FROM plan_occurrences WHERE id = $id;", ("$id", run.OccurrenceId));
            return run;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
    }

    public void Update(RecordingRun snapshot, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedVersion(snapshot.Version, expectedVersion);
        var id = RequiredInput(snapshot.Id);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var artifactId = NullableInput(snapshot.MediaArtifactId);
        var bundleId = NullableInput(snapshot.BundleId);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE recording_runs SET status_code = $status_code, has_crossed_start_commit = $has_crossed_start_commit, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND occurrence_id = $occurrence_id AND media_artifact_id IS $media_artifact_id AND bundle_id IS $bundle_id AND created_at_utc = $created_at_utc;";
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$has_crossed_start_commit", snapshot.HasCrossedStartCommit ? 1L : 0L);
            Add(command, "$terminal_reason_code", NullableInput(snapshot.TerminalReasonCode));
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$new_version", snapshot.Version);
            Add(command, "$id", id);
            Add(command, "$expected_version", expectedVersion);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$media_artifact_id", artifactId);
            Add(command, "$bundle_id", bundleId);
            Add(command, "$created_at_utc", createdTicks);
            var affectedRows = command.ExecuteNonQuery();
            if (affectedRows == 0)
            {
                EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM recording_runs WHERE id = $id;", id, expectedVersion);
            }

            EnsureRowsAffected(affectedRows);
        }, duplicateIsAlreadyExists: false);
    }
}

public sealed class SqliteConsentLeaseRepository : SqliteRepositoryBase, IConsentLeaseRepository
{
    private const string SelectColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    public SqliteConsentLeaseRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(ConsentLease snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = RequiredInput(snapshot.Id);
        var planId = RequiredInput(snapshot.PlanId);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var validFromTicks = UtcTicksInput(snapshot.ValidFromUtc);
        var validUntilTicks = UtcTicksInput(snapshot.ValidUntilUtc);
        var maxDurationMilliseconds = DurationMillisecondsInput(snapshot.MaxDuration);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO consent_leases ({SelectColumns}) VALUES ($id, $plan_id, $occurrence_id, $status_code, $valid_from_utc, $valid_until_utc, $max_uses, $max_duration_ms, $updated_at_utc, $version);";
            Add(command, "$id", id);
            Add(command, "$plan_id", planId);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$valid_from_utc", validFromTicks);
            Add(command, "$valid_until_utc", validUntilTicks);
            Add(command, "$max_uses", snapshot.MaxUses);
            Add(command, "$max_duration_ms", maxDurationMilliseconds);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$version", snapshot.Version);
            EnsureRowsAffected(command.ExecuteNonQuery());
        }, duplicateIsAlreadyExists: true);
    }

    internal static void InsertWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsentLease snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO consent_leases ({SelectColumns}) VALUES ($id, $plan_id, $occurrence_id, $status_code, $valid_from_utc, $valid_until_utc, $max_uses, $max_duration_ms, $updated_at_utc, $version);";
        Add(command, "$id", RequiredInput(snapshot.Id));
        Add(command, "$plan_id", RequiredInput(snapshot.PlanId));
        Add(command, "$occurrence_id", RequiredInput(snapshot.OccurrenceId));
        Add(command, "$status_code", snapshot.StatusCode);
        Add(command, "$valid_from_utc", UtcTicksInput(snapshot.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(snapshot.ValidUntilUtc));
        Add(command, "$max_uses", snapshot.MaxUses);
        Add(command, "$max_duration_ms", DurationMillisecondsInput(snapshot.MaxDuration));
        Add(command, "$updated_at_utc", UtcTicksInput(snapshot.UpdatedAtUtc));
        Add(command, "$version", snapshot.Version);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    public ConsentLease Get(string id)
    {
        id = RequiredInput(id);
        using var connection = OpenBusinessConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM consent_leases WHERE id = $id;";
            Add(command, "$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw NotFound();
            }

            var lease = ReadConsentLeaseSnapshot(reader);

            EnsureExists(connection, "SELECT 1 FROM plans WHERE id = $id;", ("$id", lease.PlanId));
            EnsureExists(connection, "SELECT 1 FROM plan_occurrences WHERE id = $occurrence_id AND plan_id = $plan_id;", ("$occurrence_id", lease.OccurrenceId), ("$plan_id", lease.PlanId));
            return lease;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
    }

    public void Update(ConsentLease snapshot, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedVersion(snapshot.Version, expectedVersion);
        var id = RequiredInput(snapshot.Id);
        var planId = RequiredInput(snapshot.PlanId);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var validFromTicks = UtcTicksInput(snapshot.ValidFromUtc);
        var validUntilTicks = UtcTicksInput(snapshot.ValidUntilUtc);
        var maxDurationMilliseconds = DurationMillisecondsInput(snapshot.MaxDuration);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE consent_leases SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND occurrence_id = $occurrence_id AND valid_from_utc = $valid_from_utc AND valid_until_utc = $valid_until_utc AND max_uses = $max_uses AND max_duration_ms = $max_duration_ms;";
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$new_version", snapshot.Version);
            Add(command, "$id", id);
            Add(command, "$expected_version", expectedVersion);
            Add(command, "$plan_id", planId);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$valid_from_utc", validFromTicks);
            Add(command, "$valid_until_utc", validUntilTicks);
            Add(command, "$max_uses", snapshot.MaxUses);
            Add(command, "$max_duration_ms", maxDurationMilliseconds);
            var affectedRows = command.ExecuteNonQuery();
            if (affectedRows == 0)
            {
                EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM consent_leases WHERE id = $id;", id, expectedVersion);
            }

            EnsureRowsAffected(affectedRows);
        }, duplicateIsAlreadyExists: false);
    }
}

public sealed class SqliteLeaseUseRepository : SqliteRepositoryBase, ILeaseUseRepository
{
    private const string SelectColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";

    public SqliteLeaseUseRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(LeaseUse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = RequiredInput(snapshot.Id);
        var leaseId = RequiredInput(snapshot.LeaseId);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var runId = RequiredInput(snapshot.RunId);
        var reservedDurationMilliseconds = DurationMillisecondsInput(snapshot.ReservedDuration);
        long? actualDurationMilliseconds = snapshot.ActualSettledDuration is null
            ? null
            : DurationMillisecondsInput(snapshot.ActualSettledDuration.Value);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO lease_uses ({SelectColumns}) VALUES ($id, $lease_id, $occurrence_id, $run_id, $status_code, $reserved_use_count, $reserved_duration_ms, $actual_settled_duration_ms, $created_at_utc, $updated_at_utc, $version);";
            Add(command, "$id", id);
            Add(command, "$lease_id", leaseId);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$run_id", runId);
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$reserved_use_count", snapshot.ReservedUseCount);
            Add(command, "$reserved_duration_ms", reservedDurationMilliseconds);
            Add(command, "$actual_settled_duration_ms", actualDurationMilliseconds);
            Add(command, "$created_at_utc", createdTicks);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$version", snapshot.Version);
            EnsureRowsAffected(command.ExecuteNonQuery());
        }, duplicateIsAlreadyExists: true);
    }

    public LeaseUse Get(string id)
    {
        id = RequiredInput(id);
        using var connection = OpenBusinessConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM lease_uses WHERE id = $id;";
            Add(command, "$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw NotFound();
            }

            var use = ReadLeaseUseSnapshot(reader);

            EnsureExists(connection, "SELECT 1 FROM consent_leases WHERE id = $lease_id AND occurrence_id = $occurrence_id;", ("$lease_id", use.LeaseId), ("$occurrence_id", use.OccurrenceId));
            EnsureExists(connection, "SELECT 1 FROM recording_runs WHERE id = $run_id AND occurrence_id = $occurrence_id;", ("$run_id", use.RunId), ("$occurrence_id", use.OccurrenceId));
            return use;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw InvalidSnapshot(exception);
        }
        catch (SqliteException exception)
        {
            throw InfrastructureFailure(exception);
        }
        catch (Exception exception)
        {
            throw InfrastructureFailure(exception);
        }
    }

    public void Update(LeaseUse snapshot, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedVersion(snapshot.Version, expectedVersion);
        var id = RequiredInput(snapshot.Id);
        var leaseId = RequiredInput(snapshot.LeaseId);
        var occurrenceId = RequiredInput(snapshot.OccurrenceId);
        var runId = RequiredInput(snapshot.RunId);
        var reservedDurationMilliseconds = DurationMillisecondsInput(snapshot.ReservedDuration);
        long? actualDurationMilliseconds = snapshot.ActualSettledDuration is null
            ? null
            : DurationMillisecondsInput(snapshot.ActualSettledDuration.Value);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var updatedTicks = UtcTicksInput(snapshot.UpdatedAtUtc);

        using var connection = OpenBusinessConnection();
        ExecuteWrite(connection, transaction =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE lease_uses SET status_code = $status_code, reserved_use_count = $reserved_use_count, reserved_duration_ms = $reserved_duration_ms, actual_settled_duration_ms = $actual_settled_duration_ms, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND lease_id = $lease_id AND occurrence_id = $occurrence_id AND run_id = $run_id AND created_at_utc = $created_at_utc;";
            Add(command, "$status_code", snapshot.StatusCode);
            Add(command, "$reserved_use_count", snapshot.ReservedUseCount);
            Add(command, "$reserved_duration_ms", reservedDurationMilliseconds);
            Add(command, "$actual_settled_duration_ms", actualDurationMilliseconds);
            Add(command, "$updated_at_utc", updatedTicks);
            Add(command, "$new_version", snapshot.Version);
            Add(command, "$id", id);
            Add(command, "$expected_version", expectedVersion);
            Add(command, "$lease_id", leaseId);
            Add(command, "$occurrence_id", occurrenceId);
            Add(command, "$run_id", runId);
            Add(command, "$created_at_utc", createdTicks);
            var affectedRows = command.ExecuteNonQuery();
            if (affectedRows == 0)
            {
                EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM lease_uses WHERE id = $id;", id, expectedVersion);
            }

            EnsureRowsAffected(affectedRows);
        }, duplicateIsAlreadyExists: false);
    }
}
