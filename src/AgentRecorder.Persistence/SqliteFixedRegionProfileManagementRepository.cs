using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class FixedRegionProfileManagementReasonCodes
{
    public const string NotFound = "profile_not_found";
    public const string Deleted = "profile_deleted";
    public const string InUse = "profile_in_use";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string PreconditionFailed = "profile_precondition_failed";
    public const string StorageFailure = "profile_management_storage_failed";
}

public sealed record FixedRegionProfilePlanReference(string PlanId, long ProfileVersion, string ProfileDigest);

public sealed class FixedRegionProfileInUseException : InvalidOperationException
{
    public FixedRegionProfileInUseException(long referenceCount, IReadOnlyList<FixedRegionProfilePlanReference> references)
        : base("The fixed-region profile is referenced by one or more plans.")
    {
        ReferenceCount = referenceCount;
        References = references;
    }

    public long ReferenceCount { get; }
    public IReadOnlyList<FixedRegionProfilePlanReference> References { get; }
}

/// <summary>
/// Transactional directory operations layered beside the immutable profile
/// version repository. No capture/authorization services are reachable here.
/// </summary>
public sealed class SqliteFixedRegionProfileManagementRepository : SqliteRepositoryBase
{
    private const string DirectoryTable = "fixed_region_profile_directory";
    private const string IdempotencyTable = "fixed_region_profile_create_idempotency";

    public SqliteFixedRegionProfileManagementRepository(SqliteOperationalStore store) : base(store) { }

    public FixedRegionProfileCreateResult CreateFromExisting(
        string idempotencyKey,
        string requestHash,
        ProfileRef sourceRef,
        string name,
        FixedRegionProfileChanges changes,
        DateTimeOffset nowUtc)
    {
        idempotencyKey = ValidateIdempotencyKey(idempotencyKey);
        requestHash = ValidateRequestHash(requestHash);
        name = NormalizeName(name);
        ArgumentNullException.ThrowIfNull(changes);
        nowUtc = Utc(nowUtc);

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var replay = ReadIdempotency(connection, transaction, idempotencyKey);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.Method, "POST", StringComparison.Ordinal) ||
                    !string.Equals(replay.Value.Path, "/api/v1/profiles", StringComparison.Ordinal) ||
                    !string.Equals(replay.Value.Hash, requestHash, StringComparison.Ordinal))
                    throw Failure(FixedRegionProfileManagementReasonCodes.IdempotencyKeyReused,
                        "The Idempotency-Key is already bound to a different normalized request.");

                var replayVersion = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                    connection, transaction, replay.Value.ProfileRef);
                var replayRecord = new FixedRegionProfileDirectoryRecord(
                    replay.Value.Name, replay.Value.CreatedAtUtc, replay.Value.CreatedAtUtc, null, replayVersion);
                var replayEtag = FixedRegionProfileETag.Compute(replayRecord);
                if (!FixedTimeEquals(replay.Value.ETag, replayEtag))
                    throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure,
                        "The persisted profile idempotency response no longer matches its immutable result.");
                transaction.Commit();
                return new FixedRegionProfileCreateResult(replayRecord, Replayed: true, replayEtag);
            }

            var source = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(connection, transaction, sourceRef);
            var sourceDirectory = ReadDirectory(connection, transaction, source.ProfileId);
            if (sourceDirectory is null)
                throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The source profile was not found.");
            if (sourceDirectory.Value.DeletedAtUtc.HasValue)
                throw Failure(FixedRegionProfileManagementReasonCodes.Deleted, "A deleted profile cannot be copied.");

            var profileId = "prof_" + Guid.NewGuid().ToString("N");
            var version = RecurringFixedRegionProfileVersion.CreateVersion1(
                profileId, nowUtc, ApplyChanges(source, changes));
            SqliteRecurringFixedRegionProfileRepository.InsertWithinTransaction(connection, transaction, version);
            // The v21 profile-version trigger creates a stable fallback row in
            // the same transaction. Replace only its mutable display metadata.
            UpdateDirectory(connection, transaction, profileId, name, nowUtc.UtcDateTime.Ticks, deletedAtUtc: null);
            var record = new FixedRegionProfileDirectoryRecord(name, nowUtc, nowUtc, null, version);
            var etag = FixedRegionProfileETag.Compute(record);
            InsertIdempotency(connection, transaction, idempotencyKey, requestHash, record, etag, nowUtc.UtcDateTime.Ticks);
            transaction.Commit();
            return new FixedRegionProfileCreateResult(record, Replayed: false, etag);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure,
                "The fixed-region profile transaction failed.", exception);
        }
        catch (ArgumentException exception)
        {
            TryRollback(transaction);
            throw Failure("profile_invalid", "The profile values are invalid.", exception);
        }
    }

    public FixedRegionProfileCreateResult? ReadCreateReplay(string idempotencyKey, string requestHash)
    {
        idempotencyKey = ValidateIdempotencyKey(idempotencyKey);
        requestHash = ValidateRequestHash(requestHash);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var replay = ReadIdempotency(connection, transaction, idempotencyKey);
        if (replay is null)
        {
            transaction.Commit();
            return null;
        }
        if (!string.Equals(replay.Value.Method, "POST", StringComparison.Ordinal) ||
            !string.Equals(replay.Value.Path, "/api/v1/profiles", StringComparison.Ordinal) ||
            !string.Equals(replay.Value.Hash, requestHash, StringComparison.Ordinal))
            throw Failure(FixedRegionProfileManagementReasonCodes.IdempotencyKeyReused,
                "The Idempotency-Key is already bound to a different normalized request.");

        var version = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection, transaction, replay.Value.ProfileRef);
        var record = new FixedRegionProfileDirectoryRecord(replay.Value.Name, replay.Value.CreatedAtUtc,
            replay.Value.CreatedAtUtc, null, version);
        var etag = FixedRegionProfileETag.Compute(record);
        if (!FixedTimeEquals(replay.Value.ETag, etag))
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure,
                "The persisted profile idempotency response no longer matches its immutable result.");
        transaction.Commit();
        return new FixedRegionProfileCreateResult(record, Replayed: true, etag);
    }

    public FixedRegionProfileCreateResult CreateFromSelection(
        string idempotencyKey,
        string requestHash,
        string name,
        RecurringFixedRegionProfileSpecification specification,
        DateTimeOffset nowUtc)
    {
        idempotencyKey = ValidateIdempotencyKey(idempotencyKey);
        requestHash = ValidateRequestHash(requestHash);
        name = NormalizeName(name);
        ArgumentNullException.ThrowIfNull(specification);
        nowUtc = Utc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var replay = ReadIdempotency(connection, transaction, idempotencyKey);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.Method, "POST", StringComparison.Ordinal) ||
                    !string.Equals(replay.Value.Path, "/api/v1/profiles", StringComparison.Ordinal) ||
                    !string.Equals(replay.Value.Hash, requestHash, StringComparison.Ordinal))
                    throw Failure(FixedRegionProfileManagementReasonCodes.IdempotencyKeyReused,
                        "The Idempotency-Key is already bound to a different normalized request.");
                var replayVersion = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                    connection, transaction, replay.Value.ProfileRef);
                var replayRecord = new FixedRegionProfileDirectoryRecord(replay.Value.Name,
                    replay.Value.CreatedAtUtc, replay.Value.CreatedAtUtc, null, replayVersion);
                var replayEtag = FixedRegionProfileETag.Compute(replayRecord);
                if (!FixedTimeEquals(replay.Value.ETag, replayEtag))
                    throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure,
                        "The persisted profile idempotency response no longer matches its immutable result.");
                transaction.Commit();
                return new FixedRegionProfileCreateResult(replayRecord, Replayed: true, replayEtag);
            }

            var profileId = "prof_" + Guid.NewGuid().ToString("N");
            var version = RecurringFixedRegionProfileVersion.CreateVersion1(profileId, nowUtc, specification);
            SqliteRecurringFixedRegionProfileRepository.InsertWithinTransaction(connection, transaction, version);
            UpdateDirectory(connection, transaction, profileId, name, nowUtc.UtcDateTime.Ticks, deletedAtUtc: null);
            var record = new FixedRegionProfileDirectoryRecord(name, nowUtc, nowUtc, null, version);
            var etag = FixedRegionProfileETag.Compute(record);
            InsertIdempotency(connection, transaction, idempotencyKey, requestHash, record, etag, nowUtc.UtcDateTime.Ticks);
            transaction.Commit();
            return new FixedRegionProfileCreateResult(record, Replayed: false, etag);
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure,
                "The fixed-region profile transaction failed.", exception);
        }
        catch (ArgumentException exception)
        {
            TryRollback(transaction);
            throw Failure("profile_invalid", "The profile values are invalid.", exception);
        }
    }

    public FixedRegionProfileDirectoryPage List(int limit, string? afterProfileId, bool includeDeleted)
    {
        if (limit is < 1 or > 100) throw Failure("profile_list_limit_invalid", "The profile list limit is invalid.");
        if (afterProfileId is not null) _ = ValidateProfileId(afterProfileId);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var metadata = new List<(string Id, string Name, long Created, long Updated, long? Deleted)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT profile_id, name, created_at_utc, updated_at_utc, deleted_at_utc
                FROM {DirectoryTable}
                WHERE ($includeDeleted = 1 OR deleted_at_utc IS NULL)
                  AND ($afterId IS NULL OR profile_id COLLATE BINARY > $afterId)
                ORDER BY profile_id COLLATE BINARY
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
            command.Parameters.AddWithValue("$afterId", (object?)afterProfileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                metadata.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        var hasMore = metadata.Count > limit;
        if (hasMore) metadata.RemoveAt(metadata.Count - 1);
        var items = new List<FixedRegionProfileDirectoryRecord>(metadata.Count);
        foreach (var item in metadata)
        {
            var current = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, item.Id)
                ?? throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "A profile directory entry has no immutable version.");
            items.Add(ToRecord(item.Name, item.Created, item.Updated, item.Deleted, current));
        }
        transaction.Commit();
        return new FixedRegionProfileDirectoryPage(items,
            hasMore && items.Count > 0 ? items[^1].ProfileId : null);
    }

    public FixedRegionProfileDirectoryRecord? Get(string profileId)
    {
        profileId = ValidateProfileId(profileId);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var metadata = ReadDirectory(connection, transaction, profileId);
        if (metadata is null)
        {
            transaction.Commit();
            return null;
        }
        var current = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId)
            ?? throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "A profile directory entry has no immutable version.");
        var result = ToRecord(metadata.Value.Name, metadata.Value.CreatedAtUtc, metadata.Value.UpdatedAtUtc,
            metadata.Value.DeletedAtUtc, current);
        transaction.Commit();
        return result;
    }

    public FixedRegionProfileVersionPageSnapshot? ReadVersionPageSnapshot(
        string profileId, int limit, long? beforeVersionExclusive)
    {
        profileId = ValidateProfileId(profileId);
        if (limit is < 1 or > 100 || beforeVersionExclusive is <= 0)
            throw Failure("profile_cursor_invalid", "The profile version cursor or limit is invalid.");
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var metadata = ReadDirectory(connection, transaction, profileId);
        if (metadata is null)
        {
            transaction.Commit();
            throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile was not found.");
        }
        var current = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId)
            ?? throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "A profile directory entry has no immutable version.");
        var versions = SqliteRecurringFixedRegionProfileRepository.ListVersionsWithinTransaction(
            connection, transaction, profileId, beforeVersionExclusive, limit + 1);
        var hasMore = versions.Count > limit;
        if (hasMore) versions = versions.Take(limit).ToArray();
        var page = new FixedRegionProfileVersionPage(versions,
            hasMore && versions.Count > 0 ? versions[^1].ProfileVersion.ToString(CultureInfo.InvariantCulture) : null);
        var directory = ToRecord(metadata.Value.Name, metadata.Value.CreatedAtUtc, metadata.Value.UpdatedAtUtc,
            metadata.Value.DeletedAtUtc, current);
        transaction.Commit();
        return new FixedRegionProfileVersionPageSnapshot(directory, page);
    }

    public FixedRegionProfileExactVersionSnapshot? ReadExactVersionSnapshot(string profileId, long version)
    {
        profileId = ValidateProfileId(profileId);
        if (version <= 0) throw Failure("profile_cursor_invalid", "The exact profile version is invalid.");
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var metadata = ReadDirectory(connection, transaction, profileId);
        if (metadata is null)
        {
            transaction.Commit();
            return null;
        }
        var current = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId)
            ?? throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "A profile directory entry has no immutable version.");
        var selected = version <= current.ProfileVersion
            ? ReadExactVersion(connection, transaction, profileId, version)
            : null;
        var directory = ToRecord(metadata.Value.Name, metadata.Value.CreatedAtUtc, metadata.Value.UpdatedAtUtc,
            metadata.Value.DeletedAtUtc, current);
        transaction.Commit();
        return new FixedRegionProfileExactVersionSnapshot(directory, selected);
    }

    public FixedRegionProfileDirectoryRecord Patch(
        string profileId,
        string ifMatch,
        string? name,
        FixedRegionProfileChanges changes,
        DateTimeOffset nowUtc)
    {
        profileId = ValidateProfileId(profileId);
        ArgumentNullException.ThrowIfNull(changes);
        if (name is not null) name = NormalizeName(name);
        nowUtc = Utc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var metadata = ReadDirectory(connection, transaction, profileId)
                ?? throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile was not found.");
            if (metadata.DeletedAtUtc.HasValue)
                throw Failure(FixedRegionProfileManagementReasonCodes.Deleted, "The profile has been deleted.");
            var previous = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId)
                ?? throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile has no immutable version.");
            var current = ToRecord(metadata.Name, metadata.CreatedAtUtc, metadata.UpdatedAtUtc, null, previous);
            if (!FixedTimeEquals(ifMatch, FixedRegionProfileETag.Compute(current)))
                throw Failure(FixedRegionProfileManagementReasonCodes.PreconditionFailed, "The profile changed after the supplied If-Match token was read.");

            var candidate = RecurringFixedRegionProfileVersion.CreateNextVersion(previous, ApplyChanges(previous, changes),
                nowUtc < previous.CreatedAtUtc ? previous.CreatedAtUtc : nowUtc);
            SqliteRecurringFixedRegionProfileRepository.InsertWithinTransaction(connection, transaction, candidate);
            var nextName = name ?? metadata.Name;
            var updatedTicks = Math.Max(nowUtc.UtcDateTime.Ticks, metadata.UpdatedAtUtc.UtcDateTime.Ticks);
            UpdateDirectory(connection, transaction, profileId, nextName, updatedTicks, deletedAtUtc: null);
            var result = ToRecord(nextName, metadata.CreatedAtUtc.UtcDateTime.Ticks,
                updatedTicks, null, candidate);
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "The profile update failed.", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            TryRollback(transaction);
            throw Failure("profile_invalid", "The profile values are invalid.", exception);
        }
    }

    public void Delete(string profileId, string ifMatch, DateTimeOffset nowUtc)
    {
        profileId = ValidateProfileId(profileId);
        nowUtc = Utc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var metadata = ReadDirectory(connection, transaction, profileId)
                ?? throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile was not found.");
            if (metadata.DeletedAtUtc.HasValue)
                throw Failure(FixedRegionProfileManagementReasonCodes.PreconditionFailed, "The profile is already deleted.");
            var latest = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId)
                ?? throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile has no immutable version.");
            var record = ToRecord(metadata.Name, metadata.CreatedAtUtc, metadata.UpdatedAtUtc, null, latest);
            if (!FixedTimeEquals(ifMatch, FixedRegionProfileETag.Compute(record)))
                throw Failure(FixedRegionProfileManagementReasonCodes.PreconditionFailed, "The profile changed after the supplied If-Match token was read.");

            var count = CountReferences(connection, transaction, profileId);
            if (count > 0)
                throw new FixedRegionProfileInUseException(count, ReadReferenceSummary(connection, transaction, profileId, 10));

            var deletedTicks = Math.Max(nowUtc.UtcDateTime.Ticks, metadata.CreatedAtUtc.UtcDateTime.Ticks);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {DirectoryTable} SET deleted_at_utc = $deletedAt, updated_at_utc = $updatedAt WHERE profile_id = $profileId AND deleted_at_utc IS NULL;";
            command.Parameters.AddWithValue("$deletedAt", deletedTicks);
            command.Parameters.AddWithValue("$updatedAt", deletedTicks);
            command.Parameters.AddWithValue("$profileId", profileId);
            if (command.ExecuteNonQuery() != 1)
                throw Failure(FixedRegionProfileManagementReasonCodes.PreconditionFailed, "The profile delete precondition changed.");
            transaction.Commit();
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (FixedRegionProfileInUseException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "The profile delete failed.", exception);
        }
    }

    private static RecurringFixedRegionProfileSpecification ApplyChanges(
        RecurringFixedRegionProfileVersion source,
        FixedRegionProfileChanges changes) => new(
            source.TargetType, source.RebindPolicy, source.CaptureSemantics, source.CoordinateSpace,
            source.DisplayIdentityStatus, source.StableDisplayFingerprint, source.DisplayBounds,
            source.RegionWithinDisplay, source.DpiX, source.DpiY, source.PhysicalWidth, source.PhysicalHeight,
            source.Orientation, source.TopologyDigest, source.Backend, source.AudioMode,
            changes.DurationSeconds.HasValue ? TimeSpan.FromSeconds(changes.DurationSeconds.Value) : source.Duration,
            changes.CountdownSeconds ?? source.CountdownSeconds,
            changes.OutputDirectory ?? source.OutputDirectory,
            changes.FilenamePrefix ?? source.FilenamePrefix,
            source.OutputConflictPolicy, source.WakePolicy, source.DesktopRequirement);

    private static FixedRegionProfileDirectoryRecord ToRecord(
        string name, long createdTicks, long updatedTicks, long? deletedTicks,
        RecurringFixedRegionProfileVersion current) => new(
            name,
            new DateTimeOffset(createdTicks, TimeSpan.Zero),
            new DateTimeOffset(updatedTicks, TimeSpan.Zero),
            deletedTicks.HasValue ? new DateTimeOffset(deletedTicks.Value, TimeSpan.Zero) : null,
            current);

    private static FixedRegionProfileDirectoryRecord ToRecord(
        string name, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc,
        DateTimeOffset? deletedAtUtc, RecurringFixedRegionProfileVersion current) =>
        new(name, createdAtUtc, updatedAtUtc, deletedAtUtc, current);

    private static (string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? DeletedAtUtc)? ReadDirectory(
        SqliteConnection connection, SqliteTransaction transaction, string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name, created_at_utc, updated_at_utc, deleted_at_utc FROM {DirectoryTable} WHERE profile_id = $profileId LIMIT 1;";
        command.Parameters.AddWithValue("$profileId", profileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
            reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));
    }

    private static RecurringFixedRegionProfileVersion? ReadExactVersion(
        SqliteConnection connection, SqliteTransaction transaction, string profileId, long version)
    {
        try
        {
            var latest = SqliteRecurringFixedRegionProfileRepository.ReadLatestWithinTransaction(connection, transaction, profileId);
            if (latest is null || latest.ProfileVersion < version) return null;
            return SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                connection, transaction, new ProfileRef(profileId, version, ReadDigest(connection, transaction, profileId, version)));
        }
        catch (Phase3PersistenceException exception) when (exception.Code == RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound)
        {
            return null;
        }
    }

    private static string ReadDigest(SqliteConnection connection, SqliteTransaction transaction, string profileId, long version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT profile_digest FROM recurring_fixed_region_profile_versions WHERE profile_id = $id AND profile_version = $version LIMIT 1;";
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() as string ?? throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile version was not found.");
    }

    private static (string Method, string Path, string Hash, ProfileRef ProfileRef, string Name, string ETag, DateTimeOffset CreatedAtUtc)? ReadIdempotency(
        SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT method, request_path, request_hash, profile_id, result_version, result_digest, result_name, result_etag, created_at_utc FROM {IdempotencyTable} WHERE idempotency_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        try
        {
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
                new ProfileRef(reader.GetString(3), reader.GetInt64(4), reader.GetString(5)),
                reader.GetString(6), reader.GetString(7), new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is ArgumentException or Phase3DomainException or InvalidCastException or FormatException or OverflowException)
        {
            throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "The persisted profile idempotency record is invalid.", exception);
        }
    }

    private static void InsertIdempotency(SqliteConnection connection, SqliteTransaction transaction,
        string key, string hash, FixedRegionProfileDirectoryRecord profile, string etag, long createdTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {IdempotencyTable}(idempotency_key,method,request_path,request_hash,profile_id,result_version,result_digest,result_name,result_etag,created_at_utc) VALUES($key,'POST','/api/v1/profiles',$hash,$id,1,$digest,$name,$etag,$created);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$id", profile.ProfileId);
        command.Parameters.AddWithValue("$digest", profile.CurrentVersion.ProfileDigest);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$etag", etag);
        command.Parameters.AddWithValue("$created", createdTicks);
        if (command.ExecuteNonQuery() != 1) throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "The idempotency result was not persisted.");
    }

    private static void UpdateDirectory(SqliteConnection connection, SqliteTransaction transaction,
        string profileId, string name, long updatedTicks, long? deletedAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {DirectoryTable} SET name = $name, updated_at_utc = max(updated_at_utc, $updated), deleted_at_utc = $deleted WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updated", updatedTicks);
        command.Parameters.AddWithValue("$deleted", (object?)deletedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", profileId);
        if (command.ExecuteNonQuery() != 1) throw Failure(FixedRegionProfileManagementReasonCodes.StorageFailure, "The profile directory row is missing.");
    }

    private static long CountReferences(SqliteConnection connection, SqliteTransaction transaction, string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM recurring_plan_profile_bindings WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<FixedRegionProfilePlanReference> ReadReferenceSummary(
        SqliteConnection connection, SqliteTransaction transaction, string profileId, int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT plan_id,profile_version,profile_digest FROM recurring_plan_profile_bindings WHERE profile_id = $id ORDER BY plan_id COLLATE BINARY LIMIT $limit;";
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<FixedRegionProfilePlanReference>();
        while (reader.Read()) result.Add(new FixedRegionProfilePlanReference(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        return result;
    }

    private static string ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Failure("profile_invalid", "The idempotency key is invalid.");
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl) || normalized.Contains('/') || normalized.Contains('\\'))
            throw Failure("profile_invalid", "The idempotency key is invalid.");
        return normalized;
    }

    private static string ValidateRequestHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw Failure("profile_invalid", "The normalized request hash is invalid.");
        return value.ToLowerInvariant();
    }

    private static string NormalizeName(string value)
    {
        if (value is null || value.Any(char.IsControl)) throw Failure("profile_invalid", "The profile name is invalid.");
        var normalized = value.Trim();
        var runeCount = normalized.EnumerateRunes().Count();
        if (runeCount is < 1 or > 80) throw Failure("profile_invalid", "The profile name must contain 1-80 readable characters.");
        return normalized;
    }

    private static string ValidateProfileId(string value)
    {
        try { return new ProfileRef(value, 1, RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)).ProfileId; }
        catch (ArgumentException exception) { throw Failure(FixedRegionProfileManagementReasonCodes.NotFound, "The profile was not found.", exception); }
    }

    private static DateTimeOffset Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw Failure("profile_invalid", "Profile timestamps must be UTC.");
        return value;
    }

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static Phase3PersistenceException Failure(string code, string message, Exception? inner = null) => new(code, message, inner);

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); }
        catch (InvalidOperationException) { }
        catch (SqliteException) { }
    }
}
