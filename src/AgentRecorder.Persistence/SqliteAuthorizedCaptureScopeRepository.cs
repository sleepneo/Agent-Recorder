using System.Data;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public sealed class SqliteAuthorizedCaptureScopeRepository : SqliteRepositoryBase, IAuthorizedCaptureScopeRepository
{
    private const string SelectColumns = "scope_id, plan_id, occurrence_id, lease_id, authorization_version, created_at_utc, scope_digest, target_type_code, capture_semantics_code, coordinate_space_code, display_identity_status_code, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, backend_code, audio_mode_code, reserved_duration_ms, countdown_seconds, output_directory, frozen_file_name, output_conflict_policy_code, wake_policy_code, desktop_requirement_code, current_user_sid, session_binding, topology_digest";

    public SqliteAuthorizedCaptureScopeRepository(SqliteOperationalStore store)
        : base(store)
    {
    }

    public void Insert(AuthorizedFixedRegionScope snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = RequiredInput(snapshot.ScopeId);
        _ = RequiredInput(snapshot.PlanId);
        _ = RequiredInput(snapshot.OccurrenceId);
        _ = RequiredInput(snapshot.LeaseId);
        var createdTicks = UtcTicksInput(snapshot.CreatedAtUtc);
        var durationMilliseconds = DurationMillisecondsInput(snapshot.ReservedDuration);

        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var related = LoadRelated(connection, transaction, snapshot.PlanId, snapshot.OccurrenceId, snapshot.LeaseId, persisted: false);
            ValidateRelations(snapshot, related.Plan, related.Occurrence, related.Lease, persisted: false);

            var existing = ReadIdentityMatches(connection, transaction, snapshot);
            if (existing.Count > 0)
            {
                EnsureDuplicateMatches(snapshot, existing);
                transaction.Commit();
                return;
            }

            if (related.Lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active))
            {
                throw new Phase3PersistenceException("invalid_argument", "A new authorization scope requires a pending or active lease.");
            }

            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO authorized_capture_scopes ({SelectColumns}) VALUES ($scope_id, $plan_id, $occurrence_id, $lease_id, $authorization_version, $created_at_utc, $scope_digest, $target_type_code, $capture_semantics_code, $coordinate_space_code, $display_identity_status_code, $stable_display_fingerprint, $display_bounds_x, $display_bounds_y, $display_bounds_width, $display_bounds_height, $region_x, $region_y, $region_width, $region_height, $dpi_x, $dpi_y, $physical_width, $physical_height, $orientation_code, $backend_code, $audio_mode_code, $reserved_duration_ms, $countdown_seconds, $output_directory, $frozen_file_name, $output_conflict_policy_code, $wake_policy_code, $desktop_requirement_code, $current_user_sid, $session_binding, $topology_digest);";
                Add(command, "$scope_id", snapshot.ScopeId);
                Add(command, "$plan_id", snapshot.PlanId);
                Add(command, "$occurrence_id", snapshot.OccurrenceId);
                Add(command, "$lease_id", snapshot.LeaseId);
                Add(command, "$authorization_version", snapshot.AuthorizationVersion);
                Add(command, "$created_at_utc", createdTicks);
                Add(command, "$scope_digest", snapshot.ScopeDigest);
                Add(command, "$target_type_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.TargetType));
                Add(command, "$capture_semantics_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.CaptureSemantics));
                Add(command, "$coordinate_space_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.CoordinateSpace));
                Add(command, "$display_identity_status_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.DisplayIdentityStatus));
                Add(command, "$stable_display_fingerprint", snapshot.StableDisplayFingerprint);
                Add(command, "$display_bounds_x", snapshot.DisplayBounds.X);
                Add(command, "$display_bounds_y", snapshot.DisplayBounds.Y);
                Add(command, "$display_bounds_width", snapshot.DisplayBounds.Width);
                Add(command, "$display_bounds_height", snapshot.DisplayBounds.Height);
                Add(command, "$region_x", snapshot.RegionWithinDisplay.X);
                Add(command, "$region_y", snapshot.RegionWithinDisplay.Y);
                Add(command, "$region_width", snapshot.RegionWithinDisplay.Width);
                Add(command, "$region_height", snapshot.RegionWithinDisplay.Height);
                Add(command, "$dpi_x", snapshot.DpiX);
                Add(command, "$dpi_y", snapshot.DpiY);
                Add(command, "$physical_width", snapshot.PhysicalWidth);
                Add(command, "$physical_height", snapshot.PhysicalHeight);
                Add(command, "$orientation_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.Orientation));
                Add(command, "$backend_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.Backend));
                Add(command, "$audio_mode_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.AudioMode));
                Add(command, "$reserved_duration_ms", durationMilliseconds);
                Add(command, "$countdown_seconds", snapshot.CountdownSeconds);
                Add(command, "$output_directory", snapshot.OutputDirectory);
                Add(command, "$frozen_file_name", snapshot.FrozenFileName);
                Add(command, "$output_conflict_policy_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.OutputConflictPolicy));
                Add(command, "$wake_policy_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.WakePolicy));
                Add(command, "$desktop_requirement_code", AuthorizedFixedRegionScopeCodes.ToCode(snapshot.DesktopRequirement));
                Add(command, "$current_user_sid", snapshot.CurrentUserSid);
                Add(command, "$session_binding", snapshot.SessionBinding);
                Add(command, "$topology_digest", snapshot.TopologyDigest);
                EnsureRowsAffected(command.ExecuteNonQuery());
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19 && IsUniqueConstraint(exception))
            {
                var concurrent = ReadIdentityMatches(connection, transaction, snapshot);
                if (concurrent.Count == 0)
                {
                    throw new Phase3PersistenceException("constraint_violation", "The SQLite operation violated an immutable scope constraint.", exception);
                }

                EnsureDuplicateMatches(snapshot, concurrent);
            }

            transaction.Commit();
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
            throw InvalidArgument(exception.Message);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException("constraint_violation", "The SQLite operation violated an immutable scope constraint.", exception);
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

    public AuthorizedFixedRegionScope GetById(string id) =>
        GetByQuery("SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE scope_id = $lookup;", id);

    public AuthorizedFixedRegionScope Get(string id) => GetById(id);

    public AuthorizedFixedRegionScope GetByLeaseId(string leaseId) =>
        GetByQuery("SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE lease_id = $lookup;", leaseId);

    public AuthorizedFixedRegionScope GetByOccurrence(string occurrenceId) =>
        GetByQuery("SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE occurrence_id = $lookup;", occurrenceId);

    // The start gate calls these internal helpers only after it has opened its own
    // write transaction. They intentionally accept the existing connection and
    // transaction so the scope cannot be read from a second business snapshot.
    internal static AuthorizedFixedRegionScope? TryReadById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE scope_id = $scope_id;";
        Add(command, "$scope_id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadScopeSnapshot(reader) : null;
    }

    internal static AuthorizedFixedRegionScope? TryReadByIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE plan_id = $plan_id AND occurrence_id = $occurrence_id AND lease_id = $lease_id;";
        Add(command, "$plan_id", planId);
        Add(command, "$occurrence_id", occurrenceId);
        Add(command, "$lease_id", leaseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var scope = ReadScopeSnapshot(reader);
        if (reader.Read())
        {
            throw new PersistedSnapshotException("More than one authorization scope exists for the same start-gate identity.");
        }

        return scope;
    }

    internal static AuthorizedFixedRegionScope? TryReadByLeaseId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE lease_id = $lease_id;";
        Add(command, "$lease_id", leaseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var scope = ReadScopeSnapshot(reader);
        if (reader.Read())
        {
            throw new PersistedSnapshotException("More than one authorization scope exists for the same lease.");
        }

        return scope;
    }

    internal static (PlanDefinition Plan, PlanOccurrence Occurrence, ConsentLease Lease) LoadAndValidateRelatedForStartGate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizedFixedRegionScope scope)
    {
        var related = LoadRelated(connection, transaction, scope.PlanId, scope.OccurrenceId, scope.LeaseId, persisted: true);
        ValidateRelations(scope, related.Plan, related.Occurrence, related.Lease, persisted: true);
        return related;
    }

    private AuthorizedFixedRegionScope GetByQuery(string sql, string lookup)
    {
        lookup = RequiredInput(lookup);
        using var connection = OpenBusinessConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        try
        {
            AuthorizedFixedRegionScope scope;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                Add(command, "$lookup", lookup);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw NotFound();
                }

                scope = ReadScopeSnapshot(reader);
            }

            var related = LoadRelated(connection, transaction, scope.PlanId, scope.OccurrenceId, scope.LeaseId, persisted: true);
            ValidateRelations(scope, related.Plan, related.Occurrence, related.Lease, persisted: true);
            transaction.Commit();
            return scope;
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

    internal static AuthorizedFixedRegionScope ReadScopeSnapshot(SqliteDataReader reader)
    {
        var authorizationVersion = ReadInt32(reader, 4);
        var duration = ReadDurationMilliseconds(reader, 27);
        try
        {
            return AuthorizedFixedRegionScope.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadRequiredText(reader, 2),
                ReadRequiredText(reader, 3),
                authorizationVersion,
                ReadUtcDateTimeOffset(reader, 5),
                ReadRequiredText(reader, 6),
                ParseStatus(reader, 7, AuthorizedFixedRegionScopeCodes.ParseTargetType),
                ParseStatus(reader, 8, AuthorizedFixedRegionScopeCodes.ParseCaptureSemantics),
                ParseStatus(reader, 9, AuthorizedFixedRegionScopeCodes.ParseCoordinateSpace),
                ParseStatus(reader, 10, AuthorizedFixedRegionScopeCodes.ParseDisplayIdentityStatus),
                ReadRequiredText(reader, 11),
                new AuthorizedPhysicalRectangle(ReadInt32(reader, 12), ReadInt32(reader, 13), ReadInt32(reader, 14), ReadInt32(reader, 15)),
                new AuthorizedPhysicalRectangle(ReadInt32(reader, 16), ReadInt32(reader, 17), ReadInt32(reader, 18), ReadInt32(reader, 19)),
                ReadInt32(reader, 20),
                ReadInt32(reader, 21),
                ReadInt32(reader, 22),
                ReadInt32(reader, 23),
                ParseStatus(reader, 24, AuthorizedFixedRegionScopeCodes.ParseOrientation),
                ParseStatus(reader, 25, AuthorizedFixedRegionScopeCodes.ParseBackend),
                ParseStatus(reader, 26, AuthorizedFixedRegionScopeCodes.ParseAudioMode),
                duration,
                ReadInt32(reader, 28),
                ReadRequiredText(reader, 29),
                ReadRequiredText(reader, 30),
                ParseStatus(reader, 31, AuthorizedFixedRegionScopeCodes.ParseOutputConflictPolicy),
                ParseStatus(reader, 32, AuthorizedFixedRegionScopeCodes.ParseWakePolicy),
                ParseStatus(reader, 33, AuthorizedFixedRegionScopeCodes.ParseDesktopRequirement),
                ReadRequiredText(reader, 34),
                ReadRequiredText(reader, 35),
                ReadRequiredText(reader, 36));
        }
        catch (ArgumentException exception) when (exception is not Phase3DomainException)
        {
            throw new PersistedSnapshotException("The persisted authorization scope snapshot is invalid.", exception);
        }
    }

    private static (PlanDefinition Plan, PlanOccurrence Occurrence, ConsentLease Lease) LoadRelated(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId,
        string leaseId,
        bool persisted)
    {
        try
        {
            var plan = LoadPlan(connection, transaction, planId, persisted);
            var occurrence = LoadOccurrence(connection, transaction, occurrenceId, persisted);
            var lease = LoadLease(connection, transaction, leaseId, persisted);
            return (plan, occurrence, lease);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("A related authorization aggregate contains an invalid snapshot.", exception);
        }
    }

    private static PlanDefinition LoadPlan(SqliteConnection connection, SqliteTransaction transaction, string id, bool persisted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, is_one_time, status_code, created_at_utc, updated_at_utc, version FROM plans WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            ThrowMissingRelated(persisted, "A persisted authorization scope points to a missing plan.");
        }

        return ReadPlanDefinitionSnapshot(reader);
    }

    private static PlanOccurrence LoadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id, bool persisted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version FROM plan_occurrences WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            ThrowMissingRelated(persisted, "A persisted authorization scope points to a missing occurrence.");
        }

        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static ConsentLease LoadLease(SqliteConnection connection, SqliteTransaction transaction, string id, bool persisted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version FROM consent_leases WHERE id = $id;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            ThrowMissingRelated(persisted, "A persisted authorization scope points to a missing lease.");
        }

        return ReadConsentLeaseSnapshot(reader);
    }

    private static void ThrowMissingRelated(bool persisted, string message)
    {
        if (persisted)
        {
            throw new PersistedSnapshotException(message);
        }

        throw new Phase3PersistenceException("invalid_argument", "The authorization scope references a missing related aggregate.");
    }

    private static void ValidateRelations(
        AuthorizedFixedRegionScope scope,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        bool persisted)
    {
        void Fail(string message)
        {
            if (persisted)
            {
                throw new PersistedSnapshotException(message);
            }

            throw new Phase3PersistenceException("invalid_argument", message);
        }

        if (!string.Equals(scope.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, lease.Id, StringComparison.Ordinal) ||
            !plan.IsOneTime ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            lease.MaxUses != 1 ||
            scope.ReservedDuration > lease.MaxDuration ||
            lease.ValidUntilUtc < occurrence.WindowEndUtc ||
            scope.CreatedAtUtc < occurrence.CreatedAtUtc ||
            scope.CreatedAtUtc < lease.ValidFromUtc ||
            scope.CreatedAtUtc >= lease.ValidUntilUtc)
        {
            Fail("The authorization scope relations or lifecycle bounds are inconsistent.");
        }
    }

    private static List<AuthorizedFixedRegionScope> ReadIdentityMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizedFixedRegionScope snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + SelectColumns + " FROM authorized_capture_scopes WHERE scope_id = $scope_id OR lease_id = $lease_id OR (plan_id = $plan_id AND occurrence_id = $occurrence_id);";
        Add(command, "$scope_id", snapshot.ScopeId);
        Add(command, "$lease_id", snapshot.LeaseId);
        Add(command, "$plan_id", snapshot.PlanId);
        Add(command, "$occurrence_id", snapshot.OccurrenceId);
        using var reader = command.ExecuteReader();
        var matches = new List<AuthorizedFixedRegionScope>();
        while (reader.Read())
        {
            matches.Add(ReadScopeSnapshot(reader));
        }

        return matches;
    }

    private static void EnsureDuplicateMatches(
        AuthorizedFixedRegionScope requested,
        IReadOnlyList<AuthorizedFixedRegionScope> persisted)
    {
        if (persisted.Count != 1 || !requested.MatchesExactly(persisted[0]))
        {
            throw ImmutableMismatch();
        }
    }
}
