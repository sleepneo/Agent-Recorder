using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Persists only the internal setup-intent identity and lifecycle metadata.
/// It never creates or updates a Phase 3 aggregate and never opens a UI.
/// </summary>
internal sealed class SqliteStandingSetupIntentCreateOrGetTransaction : SqliteRepositoryBase
{
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingSetupIntentCreateOrGetTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingSetupIntentResult CreateOrGet(
        StandingSetupIntentSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            ValidateSnapshot(snapshot);
            ValidateUtc(nowUtc, "setup_intent_clock_invalid");
            connection = OpenBusinessConnection();
            transaction = BeginWriteTransaction(connection);

            var byIdentity = ReadByIdentity(connection, transaction, snapshot);
            var byIntentId = ReadByIntentId(connection, transaction, snapshot.IntentId);

            if (byIdentity is not null && byIntentId is not null &&
                !string.Equals(byIdentity.IntentId, byIntentId.IntentId, StringComparison.Ordinal))
            {
                return StandingSetupIntentResult.Conflict(snapshot, "setup_intent_identity_conflict");
            }

            if ((byIdentity is not null && byIdentity.IntentKindCode != StandingSetupIntentCodes.IntentKind) ||
                (byIntentId is not null && byIntentId.IntentKindCode != StandingSetupIntentCodes.IntentKind))
            {
                return StandingSetupIntentResult.Conflict(snapshot, "setup_intent_cross_kind_conflict");
            }

            if (byIdentity is not null)
            {
                ValidatePersisted(byIdentity);
                if (!MatchesImmutableRequest(byIdentity, snapshot))
                {
                    return StandingSetupIntentResult.Conflict(snapshot, "setup_intent_request_conflict");
                }

                return ProjectExisting(byIdentity, nowUtc);
            }

            if (byIntentId is not null)
            {
                ValidatePersisted(byIntentId);
                if (!MatchesImmutableRequest(byIntentId, snapshot))
                {
                    return StandingSetupIntentResult.Conflict(snapshot, "setup_intent_request_conflict");
                }

                return ProjectExisting(byIntentId, nowUtc);
            }

            if (snapshot.ExpiresAtUtc <= nowUtc)
            {
                return StandingSetupIntentResult.Rejected(snapshot, "setup_intent_expired_at_create");
            }

            _beforeWritesForTest?.Invoke(connection, transaction);
            InsertPending(connection, transaction, snapshot, nowUtc);
            _beforeCommitForTest?.Invoke(connection, transaction);
            transaction.Commit();
            return StandingSetupIntentResult.Created(snapshot.IntentId);
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
                "setup_intent_constraint_violation",
                "The setup intent operation violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(
                "setup_intent_sqlite_failure",
                "The setup intent operation failed in SQLite.",
                exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(
                "setup_intent_sqlite_failure",
                "The setup intent operation failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
            connection?.Dispose();
        }
    }

    private static StandingSetupIntentResult ProjectExisting(
        PersistedIntent persisted,
        DateTimeOffset nowUtc)
    {
        if (persisted.Status == StandingSetupIntentStatus.RegionSelectionPending &&
            nowUtc >= persisted.ExpiresAtUtc)
        {
            return StandingSetupIntentResult.Expired(persisted.IntentId);
        }

        return StandingSetupIntentResult.Existing(persisted.IntentId, persisted.Status);
    }

    private static void InsertPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandingSetupIntentSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO setup_intents (
                intent_id, intent_kind_code, idempotency_key, request_digest,
                current_user_sid, session_binding, status_code,
                requested_at_utc, expires_at_utc,
                plan_id, occurrence_id, lease_id, scope_id,
                created_at_utc, updated_at_utc, version, terminal_reason_code,
                scheduled_start_utc, latest_start_utc, planned_end_utc,
                maximum_duration_ms, lease_valid_until_utc, output_directory, frozen_file_name)
            VALUES (
                $intent_id, $intent_kind_code, $idempotency_key, $request_digest,
                $current_user_sid, $session_binding, $status_code,
                $requested_at_utc, $expires_at_utc,
                NULL, NULL, NULL, NULL,
                $created_at_utc, $updated_at_utc, 0, NULL,
                $scheduled_start_utc, $latest_start_utc, $planned_end_utc,
                $maximum_duration_ms, $lease_valid_until_utc, $output_directory, $frozen_file_name);
            """;
        Add(command, "$intent_id", RequiredInput(snapshot.IntentId));
        Add(command, "$intent_kind_code", RequiredInput(snapshot.IntentKindCode));
        Add(command, "$idempotency_key", RequiredInput(snapshot.IdempotencyKey));
        Add(command, "$request_digest", RequiredInput(snapshot.RequestDigest));
        Add(command, "$current_user_sid", RequiredInput(snapshot.CurrentUserSid));
        Add(command, "$session_binding", RequiredInput(snapshot.SessionBinding));
        Add(command, "$status_code", StandingSetupIntentCodes.InitialStatus);
        Add(command, "$requested_at_utc", UtcTicksInput(nowUtc));
        Add(command, "$expires_at_utc", UtcTicksInput(snapshot.ExpiresAtUtc));
        Add(command, "$created_at_utc", UtcTicksInput(nowUtc));
        Add(command, "$updated_at_utc", UtcTicksInput(nowUtc));
        Add(command, "$scheduled_start_utc", UtcTicksInput(snapshot.ScheduledStartUtc));
        Add(command, "$latest_start_utc", UtcTicksInput(snapshot.LatestStartUtc));
        Add(command, "$planned_end_utc", UtcTicksInput(snapshot.PlannedEndUtc));
        Add(command, "$maximum_duration_ms", DurationMillisecondsInput(snapshot.MaximumDuration));
        Add(command, "$lease_valid_until_utc", UtcTicksInput(snapshot.LeaseValidUntilUtc));
        Add(command, "$output_directory", RequiredInput(snapshot.OutputDirectory));
        Add(command, "$frozen_file_name", RequiredInput(snapshot.FrozenFileName));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(
                "setup_intent_sqlite_failure",
                "The setup intent insert did not affect exactly one row.");
        }
    }

    private static PersistedIntent? ReadByIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandingSetupIntentSnapshot snapshot)
    {
        using var command = CreateSelect(connection, transaction, """
            SELECT intent_id, intent_kind_code, idempotency_key, request_digest,
                   current_user_sid, session_binding, status_code,
                   requested_at_utc, expires_at_utc,
                   plan_id, occurrence_id, lease_id, scope_id,
                   created_at_utc, updated_at_utc, version, terminal_reason_code,
                   scheduled_start_utc, latest_start_utc, planned_end_utc,
                   maximum_duration_ms, lease_valid_until_utc, output_directory, frozen_file_name
            FROM setup_intents
            WHERE idempotency_key = $idempotency_key
              AND current_user_sid = $current_user_sid
              AND session_binding = $session_binding;
            """);
        Add(command, "$idempotency_key", snapshot.IdempotencyKey);
        Add(command, "$current_user_sid", snapshot.CurrentUserSid);
        Add(command, "$session_binding", snapshot.SessionBinding);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPersisted(reader) : null;
    }

    private static PersistedIntent? ReadByIntentId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string intentId)
    {
        using var command = CreateSelect(connection, transaction, """
            SELECT intent_id, intent_kind_code, idempotency_key, request_digest,
                   current_user_sid, session_binding, status_code,
                   requested_at_utc, expires_at_utc,
                   plan_id, occurrence_id, lease_id, scope_id,
                   created_at_utc, updated_at_utc, version, terminal_reason_code,
                   scheduled_start_utc, latest_start_utc, planned_end_utc,
                   maximum_duration_ms, lease_valid_until_utc, output_directory, frozen_file_name
            FROM setup_intents
            WHERE intent_id = $intent_id;
            """);
        Add(command, "$intent_id", intentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPersisted(reader) : null;
    }

    private static PersistedIntent ReadPersisted(SqliteDataReader reader) => new(
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

    internal static void ValidateSnapshotForPreparation(StandingSetupIntentSnapshot snapshot) => ValidateSnapshot(snapshot);

    private static void ValidateSnapshot(StandingSetupIntentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCanonicalId(snapshot.IntentId, StandingSetupIntentSnapshot.MaximumIdLength, allowSeparators: false, "intent_id");
        if (!string.Equals(snapshot.IntentKindCode, StandingSetupIntentCodes.IntentKind, StringComparison.Ordinal))
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_kind_invalid", "The setup intent kind is not supported."));
        }

        ValidateCanonicalId(snapshot.IdempotencyKey, StandingSetupIntentSnapshot.MaximumIdLength, allowSeparators: false, "idempotency_key");
        ValidateCanonicalText(snapshot.CurrentUserSid, StandingSetupIntentSnapshot.MaximumBindingLength, allowSeparators: false, "current_user_sid");
        ValidateCanonicalText(snapshot.SessionBinding, StandingSetupIntentSnapshot.MaximumBindingLength, allowSeparators: false, "session_binding");
        ValidateDigest(snapshot.RequestDigest, "request_digest");

        foreach (var value in new[]
        {
            snapshot.RequestedAtUtc, snapshot.ExpiresAtUtc, snapshot.ScheduledStartUtc,
            snapshot.LatestStartUtc, snapshot.PlannedEndUtc, snapshot.LeaseValidUntilUtc,
        })
        {
            ValidateUtc(value, "setup_intent_time_invalid");
        }

        if (snapshot.ExpiresAtUtc <= snapshot.RequestedAtUtc)
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_expiry_invalid", "The setup intent expiry must be after its request time."));
        }

        if (snapshot.ScheduledStartUtc >= snapshot.LatestStartUtc ||
            snapshot.LatestStartUtc > snapshot.PlannedEndUtc)
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_window_invalid", "The standing execution window is not ordered."));
        }

        if (snapshot.MaximumDuration <= TimeSpan.Zero ||
            snapshot.MaximumDuration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            snapshot.MaximumDuration > AuthorizedFixedRegionScope.MaximumReservedDuration)
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_duration_invalid", "The standing duration is outside the fixed-region limit."));
        }

        DateTimeOffset latestEndUtc;
        try
        {
            latestEndUtc = snapshot.LatestStartUtc.Add(snapshot.MaximumDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidSnapshot(new Phase3DomainException(
                "setup_intent_window_overflow",
                "The standing execution window exceeds the supported UTC range."));
        }

        if (latestEndUtc > snapshot.PlannedEndUtc)
        {
            throw InvalidSnapshot(new Phase3DomainException(
                "setup_intent_window_invalid",
                "The standing execution window does not contain the full duration after the latest start."));
        }

        if (snapshot.LeaseValidUntilUtc < snapshot.PlannedEndUtc ||
            snapshot.LeaseValidUntilUtc - snapshot.RequestedAtUtc > TimeSpan.FromHours(1))
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_lease_window_invalid", "The lease validity window is outside the one-hour boundary."));
        }

        var normalizedDirectory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(snapshot.OutputDirectory);
        var normalizedFileName = AuthorizedFixedRegionScope.NormalizeFrozenFileNameForAuthorization(snapshot.FrozenFileName);
        if (!string.Equals(normalizedDirectory, snapshot.OutputDirectory, StringComparison.Ordinal) ||
            !string.Equals(normalizedFileName, snapshot.FrozenFileName, StringComparison.Ordinal) ||
            snapshot.OutputDirectory.Length > StandingSetupIntentSnapshot.MaximumPathLength ||
            snapshot.FrozenFileName.Length > StandingSetupIntentSnapshot.MaximumFileNameLength)
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_output_path_invalid", "The output path is not canonical or bounded."));
        }

        if (!string.Equals(snapshot.TargetTypeCode, StandingSetupIntentCodes.FixedRegionTarget, StringComparison.Ordinal) ||
            !string.Equals(snapshot.AudioModeCode, StandingSetupIntentCodes.AudioNone, StringComparison.Ordinal) ||
            !string.Equals(snapshot.BackendCode, StandingSetupIntentCodes.FfmpegRegionBackend, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CaptureSemanticsCode, StandingSetupIntentCodes.DesktopRegionSemantics, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CoordinateSpaceCode, StandingSetupIntentCodes.PhysicalVirtualScreen, StringComparison.Ordinal) ||
            !string.Equals(snapshot.WakePolicyCode, StandingSetupIntentCodes.NaturalWakeOnly, StringComparison.Ordinal) ||
            !string.Equals(snapshot.DesktopRequirementCode, StandingSetupIntentCodes.InteractiveDesktopRequired, StringComparison.Ordinal) ||
            !string.Equals(snapshot.OutputConflictPolicyCode, StandingSetupIntentCodes.FailIfExists, StringComparison.Ordinal))
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_policy_invalid", "The setup intent policy is outside the fixed-region boundary."));
        }

        if (!string.Equals(snapshot.RequestDigest, snapshot.ComputeCanonicalDigest(), StringComparison.Ordinal))
        {
            throw InvalidSnapshot(new Phase3DomainException("setup_intent_digest_mismatch", "The request digest does not match the canonical setup request."));
        }
    }

    private static void ValidatePersisted(PersistedIntent persisted)
    {
        ValidateCanonicalId(persisted.IntentId, StandingSetupIntentSnapshot.MaximumIdLength, allowSeparators: false, "intent_id");
        if (!string.Equals(persisted.IntentKindCode, StandingSetupIntentCodes.IntentKind, StringComparison.Ordinal))
        {
            throw new PersistedSnapshotException("The persisted setup intent kind is invalid.");
        }

        ValidateCanonicalId(persisted.IdempotencyKey, StandingSetupIntentSnapshot.MaximumIdLength, allowSeparators: false, "idempotency_key");
        ValidateCanonicalText(persisted.CurrentUserSid, StandingSetupIntentSnapshot.MaximumBindingLength, allowSeparators: false, "current_user_sid");
        ValidateCanonicalText(persisted.SessionBinding, StandingSetupIntentSnapshot.MaximumBindingLength, allowSeparators: false, "session_binding");
        ValidateDigest(persisted.RequestDigest, "request_digest");
        if (persisted.ExpiresAtUtc <= persisted.RequestedAtUtc || persisted.UpdatedAtUtc < persisted.CreatedAtUtc || persisted.Version < 0)
        {
            throw new PersistedSnapshotException("The persisted setup intent time or version window is invalid.");
        }

        if (persisted.Status is StandingSetupIntentStatus.RegionSelectionPending or StandingSetupIntentStatus.LeaseApprovalPending or StandingSetupIntentStatus.Activated)
        {
            if (persisted.TerminalReasonCode is not null)
            {
                throw new PersistedSnapshotException("A non-terminal setup intent has a terminal reason.");
            }
        }
        else if (string.IsNullOrWhiteSpace(persisted.TerminalReasonCode))
        {
            throw new PersistedSnapshotException("A terminal setup intent has no terminal reason.");
        }

        foreach (var association in new[] { persisted.PlanId, persisted.OccurrenceId, persisted.LeaseId, persisted.ScopeId })
        {
            if (association is not null)
            {
                ValidateCanonicalId(association, StandingSetupIntentSnapshot.MaximumIdLength, allowSeparators: false, "association_id");
            }
        }
    }

    private static bool MatchesImmutableRequest(PersistedIntent persisted, StandingSetupIntentSnapshot snapshot) =>
        string.Equals(persisted.IntentKindCode, snapshot.IntentKindCode, StringComparison.Ordinal) &&
        string.Equals(persisted.IdempotencyKey, snapshot.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(persisted.RequestDigest, snapshot.RequestDigest, StringComparison.Ordinal) &&
        string.Equals(persisted.CurrentUserSid, snapshot.CurrentUserSid, StringComparison.Ordinal) &&
        string.Equals(persisted.SessionBinding, snapshot.SessionBinding, StringComparison.Ordinal) &&
        persisted.ExpiresAtUtc == snapshot.ExpiresAtUtc;

    private static void ValidateCanonicalId(string? value, int maximumLength, bool allowSeparators, string name)
    {
        ValidateCanonicalText(value, maximumLength, allowSeparators, name);
        if (!allowSeparators && (value!.Contains('/') || value.Contains('\\')))
        {
            throw new PersistedSnapshotException($"The {name} contains a path separator.");
        }
    }

    private static void ValidateCanonicalText(string? value, int maximumLength, bool allowSeparators, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > maximumLength ||
            (!allowSeparators && (value.Contains('/') || value.Contains('\\'))))
        {
            throw new PersistedSnapshotException($"The {name} is not canonical.");
        }
    }

    private static void ValidateDigest(string? value, string name)
    {
        ValidateCanonicalText(value, 64, allowSeparators: false, name);
        if (value!.Length != 64 || value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new PersistedSnapshotException($"The {name} is not lowercase SHA-256.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string code)
    {
        if (value.Offset != TimeSpan.Zero || value.UtcDateTime.Ticks < 0)
        {
            throw new Phase3PersistenceException(code, "The setup intent time must be a valid UTC value.");
        }
    }

    private static SqliteCommand CreateSelect(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static Phase3PersistenceException SnapshotInvalid(Exception exception) =>
        new("setup_intent_snapshot_invalid", "The setup intent snapshot is invalid.", exception);

    private sealed record PersistedIntent(
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
