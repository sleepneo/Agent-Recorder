using System.Data;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringFixedRegionProfilePersistenceReasonCodes
{
    public const string ProfileNotFound = "profile_not_found";
    public const string ProfileRefMismatch = "profile_ref_mismatch";
    public const string ProfileVersionConflict = "profile_version_conflict";
    public const string ProfileVersionStale = "profile_version_stale";
    public const string ProfilePersistedDataInvalid = "profile_persisted_data_invalid";
    public const string ProfileAtomicPersistenceFailed = "profile_atomic_persistence_failed";
    public const string ProfileListLimitInvalid = "profile_list_limit_invalid";
}

/// <summary>
/// Persists immutable recurring fixed-region profile versions. The repository
/// deliberately has no update or delete API; SQLite triggers enforce the same
/// boundary for direct database writers.
/// </summary>
public sealed class SqliteRecurringFixedRegionProfileRepository : SqliteRepositoryBase
{
    private const string TableName = "recurring_fixed_region_profile_versions";

    public SqliteRecurringFixedRegionProfileRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public RecurringFixedRegionProfileVersion CreateVersion1(RecurringFixedRegionProfileVersion profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProfileVersion != RecurringFixedRegionProfileVersion.FirstProfileVersion)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                "Only profile version 1 can be created through CreateVersion1.");
        }

        using var connection = OpenProfileConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var existing = ReadSingle(connection, transaction, profile.ProfileId, profile.ProfileVersion);
            if (existing is not null)
            {
                if (Equivalent(existing, profile))
                {
                    transaction.Commit();
                    return existing;
                }

                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                    "The profile version is already persisted with different immutable content.");
            }

            Insert(connection, transaction, profile);
            transaction.Commit();
            return profile;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsProfileUniqueConstraint(exception))
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                "The profile version conflicts with an immutable persisted row.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The profile version transaction failed.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid,
                "The profile version could not be persisted as canonical data.",
                exception);
        }
    }

    public RecurringFixedRegionProfileVersion CreateVersion1(
        string profileId,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification) =>
        CreateVersion1(ConstructVersion1(profileId, createdAtUtc, specification));

    public RecurringFixedRegionProfileVersion CreateNext(
        ProfileRef expectedCurrentRef,
        RecurringFixedRegionProfileSpecification specification,
        DateTimeOffset createdAtUtc)
    {
        ValidateReference(expectedCurrentRef);
        ArgumentNullException.ThrowIfNull(specification);

        using var connection = OpenProfileConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var expected = ReadSingle(connection, transaction, expectedCurrentRef.ProfileId, expectedCurrentRef.ProfileVersion);
            if (expected is null)
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound,
                    "The expected current profile version was not found.");
            }

            if (!expectedCurrentRef.Matches(expected))
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                    "The expected current profile reference does not match the persisted version.");
            }

            RecurringFixedRegionProfileVersion candidate;
            try
            {
                candidate = RecurringFixedRegionProfileVersion.CreateNextVersion(expected, specification, createdAtUtc);
            }
            catch (Phase3DomainException exception)
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                    "The next profile version is not a valid contiguous immutable version.",
                    exception);
            }

            var latest = ReadLatest(connection, transaction, expected.ProfileId);
            if (latest is null)
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid,
                    "The profile history has no latest version after reading its expected version.");
            }

            if (latest.ProfileVersion > candidate.ProfileVersion)
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionStale,
                    "The expected profile version is older than the persisted latest version.");
            }

            if (latest.ProfileVersion == candidate.ProfileVersion)
            {
                var replay = ReadSingle(connection, transaction, candidate.ProfileId, candidate.ProfileVersion);
                if (replay is not null && Equivalent(replay, candidate))
                {
                    transaction.Commit();
                    return replay;
                }

                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                    "The next profile version already exists with different immutable content.");
            }

            if (latest.ProfileVersion != expected.ProfileVersion)
            {
                throw Failure(
                    RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionStale,
                    "The expected profile version is no longer the latest contiguous version.");
            }

            Insert(connection, transaction, candidate);
            transaction.Commit();
            return candidate;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsProfileUniqueConstraint(exception))
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict,
                "The next profile version conflicts with an immutable persisted row.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The next profile version transaction failed.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid,
                "The profile version could not be reconstructed as canonical data.",
                exception);
        }
    }

    public RecurringFixedRegionProfileVersion Get(string profileId, long profileVersion)
    {
        ValidateProfileId(profileId);
        if (profileVersion <= 0)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The profile reference version must be positive.");
        }

        using var connection = OpenProfileConnection();
        try
        {
            var profile = ReadSingle(connection, transaction: null, profileId, profileVersion);
            return profile ?? throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound,
                "The requested profile version was not found.");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The profile version could not be read.",
                exception);
        }
    }

    public RecurringFixedRegionProfileVersion Get(ProfileRef profileRef)
    {
        ValidateReference(profileRef);
        var profile = Get(profileRef.ProfileId, profileRef.ProfileVersion);
        if (!profileRef.Matches(profile))
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The requested profile reference does not match the immutable persisted version.");
        }

        return profile;
    }

    internal static RecurringFixedRegionProfileVersion ReadExactWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileRef profileRef)
    {
        ValidateReference(profileRef);
        var profile = ReadSingle(connection, transaction, profileRef.ProfileId, profileRef.ProfileVersion);
        if (profile is null)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound,
                "The requested profile version was not found.");
        }

        if (!profileRef.Matches(profile))
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The requested profile reference does not match the immutable persisted version.");
        }

        return profile;
    }

    public RecurringFixedRegionProfileVersion GetLatest(string profileId)
    {
        ValidateProfileId(profileId);
        using var connection = OpenProfileConnection();
        try
        {
            var profile = ReadLatest(connection, transaction: null, profileId);
            return profile ?? throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound,
                "The requested profile has no persisted version.");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The latest profile version could not be read.",
                exception);
        }
    }

    public IReadOnlyList<RecurringFixedRegionProfileVersion> ListVersions(
        string profileId,
        long? beforeVersionExclusive = null,
        int limit = 100)
    {
        ValidateProfileId(profileId);
        if (limit is < 1 or > 100)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileListLimitInvalid,
                "The profile version list limit must be between 1 and 100.");
        }

        if (beforeVersionExclusive is <= 0)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The profile version list cursor must be positive when supplied.");
        }

        using var connection = OpenProfileConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {Columns}
                FROM {TableName}
                WHERE profile_id = $profileId
                  AND ($beforeVersionExclusive IS NULL OR profile_version < $beforeVersionExclusive)
                ORDER BY profile_version DESC
                LIMIT $limit;
                """;
            AddProfileParameter(command, "$profileId", profileId);
            AddProfileParameter(command, "$beforeVersionExclusive", beforeVersionExclusive.HasValue ? beforeVersionExclusive.Value : DBNull.Value);
            AddProfileParameter(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            var profiles = new List<RecurringFixedRegionProfileVersion>();
            while (reader.Read())
            {
                profiles.Add(ReadProfile(reader));
            }

            return profiles;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The profile version list could not be read.",
                exception);
        }
    }

    private static readonly string Columns = string.Join(", ", new[]
    {
        "profile_id", "profile_version", "profile_digest", "created_at_utc",
        "target_type_code", "rebind_policy_code", "capture_semantics_code", "coordinate_space_code", "display_identity_status_code",
        "stable_display_fingerprint", "display_bounds_x", "display_bounds_y", "display_bounds_width", "display_bounds_height",
        "region_x", "region_y", "region_width", "region_height", "dpi_x", "dpi_y", "physical_width", "physical_height",
        "orientation_code", "topology_digest", "backend_code", "audio_mode_code", "duration_ms", "countdown_seconds",
        "output_directory", "filename_prefix", "filename_template", "output_conflict_policy_code", "wake_policy_code", "desktop_requirement_code",
    });

    private static RecurringFixedRegionProfileVersion? ReadSingle(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        long profileVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM {TableName} WHERE profile_id = $profileId AND profile_version = $profileVersion LIMIT 1;";
        AddProfileParameter(command, "$profileId", profileId);
        AddProfileParameter(command, "$profileVersion", profileVersion);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    private static RecurringFixedRegionProfileVersion? ReadLatest(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM {TableName} WHERE profile_id = $profileId ORDER BY profile_version DESC LIMIT 1;";
        AddProfileParameter(command, "$profileId", profileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    private static RecurringFixedRegionProfileVersion ReadProfile(SqliteDataReader reader)
    {
        try
        {
            var profileId = reader.GetString(0);
            var profileVersion = reader.GetInt64(1);
            var profileDigest = reader.GetString(2);
            var createdAtUtc = new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero);
            var specification = new RecurringFixedRegionProfileSpecification(
                ParseTargetType(reader.GetString(4)),
                ParseRebindPolicy(reader.GetString(5)),
                ParseCaptureSemantics(reader.GetString(6)),
                ParseCoordinateSpace(reader.GetString(7)),
                ParseDisplayIdentityStatus(reader.GetString(8)),
                reader.GetString(9),
                new AuthorizedPhysicalRectangle(ReadProfileInt32(reader, 10), ReadProfileInt32(reader, 11), ReadProfileInt32(reader, 12), ReadProfileInt32(reader, 13)),
                new AuthorizedPhysicalRectangle(ReadProfileInt32(reader, 14), ReadProfileInt32(reader, 15), ReadProfileInt32(reader, 16), ReadProfileInt32(reader, 17)),
                ReadProfileInt32(reader, 18),
                ReadProfileInt32(reader, 19),
                ReadProfileInt32(reader, 20),
                ReadProfileInt32(reader, 21),
                ParseOrientation(reader.GetString(22)),
                reader.GetString(23),
                ParseBackend(reader.GetString(24)),
                ParseAudioMode(reader.GetString(25)),
                TimeSpan.FromMilliseconds(reader.GetInt64(26)),
                ReadProfileInt32(reader, 27),
                reader.GetString(28),
                reader.GetString(29),
                ParseOutputConflictPolicy(reader.GetString(31)),
                ParseWakePolicy(reader.GetString(32)),
                ParseDesktopRequirement(reader.GetString(33)));

            return RecurringFixedRegionProfileVersion.Rehydrate(
                profileId,
                profileVersion,
                createdAtUtc,
                specification,
                profileDigest,
                reader.GetString(30));
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException or InvalidOperationException or Phase3DomainException)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid,
                "The persisted recurring fixed-region profile is invalid.",
                exception);
        }
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringFixedRegionProfileVersion profile)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {TableName} (
                {Columns})
            VALUES (
                $profileId, $profileVersion, $profileDigest, $createdAtUtc,
                $targetType, $rebindPolicy, $captureSemantics, $coordinateSpace, $displayIdentityStatus,
                $stableDisplayFingerprint, $displayBoundsX, $displayBoundsY, $displayBoundsWidth, $displayBoundsHeight,
                $regionX, $regionY, $regionWidth, $regionHeight, $dpiX, $dpiY, $physicalWidth, $physicalHeight,
                $orientation, $topologyDigest, $backend, $audioMode, $durationMs, $countdownSeconds,
                $outputDirectory, $filenamePrefix, $filenameTemplate, $outputConflictPolicy, $wakePolicy, $desktopRequirement);
            """;
        AddProfileParameter(command, "$profileId", profile.ProfileId);
        AddProfileParameter(command, "$profileVersion", profile.ProfileVersion);
        AddProfileParameter(command, "$profileDigest", profile.ProfileDigest);
        AddProfileParameter(command, "$createdAtUtc", profile.CreatedAtUtc.UtcDateTime.Ticks);
        AddProfileParameter(command, "$targetType", RecurringFixedRegionProfileCode.ToCode(profile.TargetType));
        AddProfileParameter(command, "$rebindPolicy", RecurringFixedRegionProfileCode.ToCode(profile.RebindPolicy));
        AddProfileParameter(command, "$captureSemantics", RecurringFixedRegionProfileCode.ToCode(profile.CaptureSemantics));
        AddProfileParameter(command, "$coordinateSpace", RecurringFixedRegionProfileCode.ToCode(profile.CoordinateSpace));
        AddProfileParameter(command, "$displayIdentityStatus", RecurringFixedRegionProfileCode.ToCode(profile.DisplayIdentityStatus));
        AddProfileParameter(command, "$stableDisplayFingerprint", profile.StableDisplayFingerprint);
        AddProfileParameter(command, "$displayBoundsX", profile.DisplayBounds.X);
        AddProfileParameter(command, "$displayBoundsY", profile.DisplayBounds.Y);
        AddProfileParameter(command, "$displayBoundsWidth", profile.DisplayBounds.Width);
        AddProfileParameter(command, "$displayBoundsHeight", profile.DisplayBounds.Height);
        AddProfileParameter(command, "$regionX", profile.RegionWithinDisplay.X);
        AddProfileParameter(command, "$regionY", profile.RegionWithinDisplay.Y);
        AddProfileParameter(command, "$regionWidth", profile.RegionWithinDisplay.Width);
        AddProfileParameter(command, "$regionHeight", profile.RegionWithinDisplay.Height);
        AddProfileParameter(command, "$dpiX", profile.DpiX);
        AddProfileParameter(command, "$dpiY", profile.DpiY);
        AddProfileParameter(command, "$physicalWidth", profile.PhysicalWidth);
        AddProfileParameter(command, "$physicalHeight", profile.PhysicalHeight);
        AddProfileParameter(command, "$orientation", RecurringFixedRegionProfileCode.ToCode(profile.Orientation));
        AddProfileParameter(command, "$topologyDigest", profile.TopologyDigest);
        AddProfileParameter(command, "$backend", RecurringFixedRegionProfileCode.ToCode(profile.Backend));
        AddProfileParameter(command, "$audioMode", RecurringFixedRegionProfileCode.ToCode(profile.AudioMode));
        AddProfileParameter(command, "$durationMs", checked((long)profile.Duration.TotalMilliseconds));
        AddProfileParameter(command, "$countdownSeconds", profile.CountdownSeconds);
        AddProfileParameter(command, "$outputDirectory", profile.OutputDirectory);
        AddProfileParameter(command, "$filenamePrefix", profile.FilenamePrefix);
        AddProfileParameter(command, "$filenameTemplate", profile.FilenameTemplate);
        AddProfileParameter(command, "$outputConflictPolicy", RecurringFixedRegionProfileCode.ToCode(profile.OutputConflictPolicy));
        AddProfileParameter(command, "$wakePolicy", RecurringFixedRegionProfileCode.ToCode(profile.WakePolicy));
        AddProfileParameter(command, "$desktopRequirement", RecurringFixedRegionProfileCode.ToCode(profile.DesktopRequirement));
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The profile insert did not affect exactly one row.");
        }
    }

    private SqliteConnection OpenProfileConnection()
    {
        try
        {
            return Store.OpenConnection();
        }
        catch (Exception exception) when (exception is SqliteOperationalStoreException or SqliteException or IOException or UnauthorizedAccessException)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileAtomicPersistenceFailed,
                "The recurring fixed-region profile store could not be opened.",
                exception);
        }
    }

    private static RecurringFixedRegionProfileVersion ConstructVersion1(
        string profileId,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification)
    {
        try
        {
            return RecurringFixedRegionProfileVersion.CreateVersion1(profileId, createdAtUtc, specification);
        }
        catch (Phase3DomainException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfilePersistedDataInvalid,
                "The profile specification is not canonical.",
                exception);
        }
    }

    private static bool Equivalent(RecurringFixedRegionProfileVersion left, RecurringFixedRegionProfileVersion right) =>
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.ProfileVersion == right.ProfileVersion &&
        string.Equals(left.ProfileDigest, right.ProfileDigest, StringComparison.Ordinal);

    private static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            !string.Equals(profileId, profileId.Trim(), StringComparison.Ordinal) ||
            profileId.Length > 128 ||
            profileId.Any(char.IsControl) ||
            profileId.Contains('/') || profileId.Contains('\\') || profileId.Contains(':'))
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The profile reference identifier is invalid.");
        }
    }

    private static void ValidateReference(ProfileRef profileRef)
    {
        try
        {
            profileRef.Validate();
        }
        catch (Phase3DomainException exception)
        {
            throw Failure(
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch,
                "The profile reference is invalid.",
                exception);
        }
    }

    private static int ReadProfileInt32(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetInt64(ordinal);
        return checked((int)value);
    }

    private static AuthorizedScopeTargetType ParseTargetType(string value) => value switch
    {
        "fixed_region" => AuthorizedScopeTargetType.FixedRegion,
        _ => throw InvalidCode(value),
    };

    private static RecurringFixedRegionRebindPolicy ParseRebindPolicy(string value) => value switch
    {
        "exact_match_only" => RecurringFixedRegionRebindPolicy.ExactMatchOnly,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedCaptureSemantics ParseCaptureSemantics(string value) => value switch
    {
        "desktop_region" => AuthorizedCaptureSemantics.DesktopRegion,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedCoordinateSpace ParseCoordinateSpace(string value) => value switch
    {
        "physical_virtual_screen" => AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedDisplayIdentityStatus ParseDisplayIdentityStatus(string value) => value switch
    {
        "resolved" => AuthorizedDisplayIdentityStatus.Resolved,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedDisplayOrientation ParseOrientation(string value) => value switch
    {
        "landscape" => AuthorizedDisplayOrientation.Landscape,
        "portrait" => AuthorizedDisplayOrientation.Portrait,
        "landscape_flipped" => AuthorizedDisplayOrientation.LandscapeFlipped,
        "portrait_flipped" => AuthorizedDisplayOrientation.PortraitFlipped,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedCaptureBackend ParseBackend(string value) => value switch
    {
        "ffmpeg-region" => AuthorizedCaptureBackend.FfmpegRegion,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedAudioMode ParseAudioMode(string value) => value switch
    {
        "none" => AuthorizedAudioMode.None,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedOutputConflictPolicy ParseOutputConflictPolicy(string value) => value switch
    {
        "fail_if_exists" => AuthorizedOutputConflictPolicy.FailIfExists,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedWakePolicy ParseWakePolicy(string value) => value switch
    {
        "natural_wake_only" => AuthorizedWakePolicy.NaturalWakeOnly,
        _ => throw InvalidCode(value),
    };

    private static AuthorizedDesktopRequirement ParseDesktopRequirement(string value) => value switch
    {
        "interactive_desktop_required" => AuthorizedDesktopRequirement.InteractiveDesktopRequired,
        _ => throw InvalidCode(value),
    };

    private static InvalidOperationException InvalidCode(string value) =>
        new($"Unexpected recurring fixed-region profile code {value}.");

    private static bool IsProfileUniqueConstraint(SqliteException exception) =>
        exception.SqliteErrorCode == 19 &&
        exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private static void AddProfileParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static Phase3PersistenceException Failure(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);
}
