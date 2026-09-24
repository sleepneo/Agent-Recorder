using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text;

namespace AgentRecorder.Persistence;

internal sealed record RecurringSetupIntentReadback(
    string IntentId,
    RecurringSetupIntentStatus Status,
    long Version,
    string? TerminalReasonCode,
    RecurringSetupIntentSnapshot Snapshot);

/// <summary>
/// The recurring setup-intent transaction owns the one immediate SQLite
/// write boundary. It persists only the strict request and trusted identity;
/// no region/profile/proof/lease activation state is created here.
/// </summary>
internal sealed class SqliteRecurringSetupIntentCreateOrGetTransaction : SqliteRepositoryBase
{
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeWritesForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteRecurringSetupIntentCreateOrGetTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _beforeWritesForTest = beforeWritesForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal RecurringSetupIntentResult CreateOrGet(RecurringSetupIntentSnapshot snapshot, DateTimeOffset nowUtc)
    {
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            ValidateSnapshot(snapshot);
            ValidateUtc(nowUtc, "recurring_setup_intent_clock_invalid");
            connection = OpenBusinessConnection();
            transaction = BeginWriteTransaction(connection);
            var byIdentity = ReadByIdentity(connection, transaction, snapshot);
            var byIntentId = ReadByIntentId(connection, transaction, snapshot.IntentId);

            if (byIdentity is not null && byIdentity.IntentId != byIntentId?.IntentId && byIntentId is not null)
                return RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_identity_conflict");
            if (byIdentity is not null && byIdentity.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
                return RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_cross_kind_conflict");
            if (byIntentId is not null && byIntentId.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
                return RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_cross_kind_conflict");

            var existing = byIdentity ?? byIntentId;
            if (existing is not null)
            {
                var validated = ValidatePersistedAndRehydrate(existing, connection, transaction);
                if (!MatchesImmutableRequest(validated.Snapshot, snapshot))
                    return RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_request_conflict");
                transaction.Commit();
                return ProjectExisting(validated, nowUtc);
            }

            if (snapshot.ExpiresAtUtc <= nowUtc)
                return RecurringSetupIntentResult.Rejected(snapshot, "recurring_setup_intent_expired_at_create");

            _beforeWritesForTest?.Invoke(connection, transaction);
            InsertPending(connection, transaction, snapshot, nowUtc);
            _beforeCommitForTest?.Invoke(connection, transaction);
            transaction.Commit();
            return RecurringSetupIntentResult.Created(snapshot.IntentId);
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
            TryRollback(transaction);
            connection?.Dispose();
            return ResolveConstraintRace(snapshot, nowUtc, exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException("recurring_setup_intent_sqlite_failure", "The recurring setup intent operation failed in SQLite.", exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException("recurring_setup_intent_sqlite_failure", "The recurring setup intent operation failed.", exception);
        }
        finally
        {
            transaction?.Dispose();
            connection?.Dispose();
        }
    }

    internal RecurringSetupIntentReadback? Get(string intentId, string currentUserSid, string sessionBinding, DateTimeOffset nowUtc)
    {
        ValidateCanonicalId(intentId, RecurringSetupIntentSnapshot.MaximumIdLength, false, "intent_id");
        ValidateCanonicalText(currentUserSid, RecurringSetupIntentSnapshot.MaximumBindingLength, false, "current_user_sid");
        ValidateCanonicalText(sessionBinding, RecurringSetupIntentSnapshot.MaximumBindingLength, false, "session_binding");
        ValidateUtc(nowUtc, "recurring_setup_intent_clock_invalid");
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var persisted = ReadByIntentId(connection, transaction, intentId);
        if (persisted is null || persisted.IntentKindCode != RecurringSetupIntentCodes.IntentKind ||
            persisted.CurrentUserSid != currentUserSid || persisted.SessionBinding != sessionBinding)
        {
            transaction.Commit();
            return null;
        }

        var readback = ValidatePersistedAndRehydrate(persisted, connection, transaction);
        transaction.Commit();
        return readback;
    }

    internal int ReconcileExpiredPending(DateTimeOffset nowUtc, int maximumRows)
    {
        return new SqliteRecurringSetupTerminalTransaction(Store).ReconcileExpiredPending(nowUtc, maximumRows);
    }

    private RecurringSetupIntentResult ResolveConstraintRace(RecurringSetupIntentSnapshot snapshot, DateTimeOffset nowUtc, SqliteException cause)
    {
        try
        {
            using var connection = OpenBusinessConnection();
            using var transaction = BeginReadTransaction(connection);
            var existing = ReadByIdentity(connection, transaction, snapshot);
            if (existing is null)
            {
                transaction.Commit();
                throw new Phase3PersistenceException("recurring_setup_intent_constraint_violation", "The recurring setup intent violated a persisted constraint.", cause);
            }
            if (existing.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
            {
                transaction.Commit();
                return RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_cross_kind_conflict");
            }
            var readback = ValidatePersistedAndRehydrate(existing, connection, transaction);
            transaction.Commit();
            return MatchesImmutableRequest(readback.Snapshot, snapshot)
                ? ProjectExisting(readback, nowUtc)
                : RecurringSetupIntentResult.Conflict(snapshot, "recurring_setup_intent_request_conflict");
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException("recurring_setup_intent_constraint_violation", "The recurring setup intent violated a persisted constraint.", exception);
        }
    }

    private static RecurringSetupIntentResult ProjectExisting(RecurringSetupIntentReadback persisted, DateTimeOffset nowUtc)
    {
        if (persisted.Status is RecurringSetupIntentStatus.RegionSelectionPending or RecurringSetupIntentStatus.LeaseApprovalPending && nowUtc >= persisted.Snapshot.ExpiresAtUtc)
            return RecurringSetupIntentResult.Expired(persisted.IntentId);
        return RecurringSetupIntentResult.Existing(persisted.IntentId, persisted.Status);
    }

    private static void InsertPending(SqliteConnection connection, SqliteTransaction transaction, RecurringSetupIntentSnapshot snapshot, DateTimeOffset nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO setup_intents (
                intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code,
                requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc,
                version, terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms,
                lease_valid_until_utc, output_directory, frozen_file_name, recurring_schedule_kind_code, recurring_time_zone_id,
                recurring_time_zone_rules_digest, recurring_schedule_digest, recurring_local_start_date, recurring_local_end_date,
                recurring_local_wall_clock_seconds, recurring_weekday_mask, recurring_maximum_occurrences, recurring_recording_duration_ticks,
                recurring_latest_start_grace_ticks, recurring_max_uses, recurring_max_cumulative_duration_ticks,
                recurring_authorization_valid_until_utc, recurring_target_type_code, recurring_audio_mode_code, recurring_backend_code,
                recurring_countdown_seconds, recurring_output_directory, recurring_filename_prefix, recurring_output_conflict_policy_code,
                recurring_wake_policy_code, recurring_desktop_requirement_code)
            VALUES (
                $intent_id, $kind, $key, $digest, $sid, $session, 'region_selection_pending',
                $requested, $expires, NULL, NULL, NULL, NULL, $created, $updated,
                0, NULL, NULL, NULL, NULL, NULL, $lease, NULL, NULL, $schedule_kind, $time_zone_id,
                $time_zone_rules_digest, $schedule_digest, $local_start_date, $local_end_date, $wall_clock_seconds,
                $weekday_mask, $maximum_occurrences, $recording_duration_ticks, $grace_ticks, $max_uses,
                $max_cumulative_duration_ticks, $authorization_valid_until, $target, $audio, $backend, 0,
                $output_directory, $filename_prefix, $conflict_policy, $wake_policy, $desktop_requirement);
            """;
        Add(command, "$intent_id", RequiredInput(snapshot.IntentId));
        Add(command, "$kind", snapshot.IntentKindCode);
        Add(command, "$key", RequiredInput(snapshot.IdempotencyKey));
        Add(command, "$digest", RequiredInput(snapshot.RequestDigest));
        Add(command, "$sid", RequiredInput(snapshot.CurrentUserSid));
        Add(command, "$session", RequiredInput(snapshot.SessionBinding));
        Add(command, "$requested", UtcTicksInput(nowUtc));
        Add(command, "$expires", UtcTicksInput(snapshot.ExpiresAtUtc));
        Add(command, "$created", UtcTicksInput(nowUtc));
        Add(command, "$updated", UtcTicksInput(nowUtc));
        Add(command, "$lease", UtcTicksInput(snapshot.LeaseValidUntilUtc));
        Add(command, "$schedule_kind", snapshot.Schedule.IsDaily ? "daily" : "weekly");
        Add(command, "$time_zone_id", snapshot.Schedule.TimeZoneId);
        Add(command, "$time_zone_rules_digest", snapshot.TimeZoneRulesDigest);
        Add(command, "$schedule_digest", snapshot.ScheduleDigest);
        Add(command, "$local_start_date", snapshot.Schedule.LocalStartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Add(command, "$local_end_date", snapshot.Schedule.LocalEndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Add(command, "$wall_clock_seconds", (long)snapshot.Schedule.LocalWallClockTime.Hour * 3600 + snapshot.Schedule.LocalWallClockTime.Minute * 60 + snapshot.Schedule.LocalWallClockTime.Second);
        Add(command, "$weekday_mask", snapshot.Schedule.WeekdayMask);
        Add(command, "$maximum_occurrences", snapshot.Schedule.MaximumOccurrences);
        Add(command, "$recording_duration_ticks", snapshot.Schedule.RecordingDuration.Ticks);
        Add(command, "$grace_ticks", snapshot.Schedule.LatestStartGrace.Ticks);
        Add(command, "$max_uses", snapshot.MaxUses);
        Add(command, "$max_cumulative_duration_ticks", snapshot.MaxCumulativeDuration.Ticks);
        Add(command, "$authorization_valid_until", UtcTicksInput(snapshot.LeaseValidUntilUtc));
        Add(command, "$target", snapshot.TargetTypeCode);
        Add(command, "$audio", snapshot.AudioModeCode);
        Add(command, "$backend", snapshot.BackendCode);
        Add(command, "$output_directory", snapshot.OutputDirectory);
        Add(command, "$filename_prefix", snapshot.FilenamePrefix);
        Add(command, "$conflict_policy", snapshot.OutputConflictPolicyCode);
        Add(command, "$wake_policy", snapshot.WakePolicyCode);
        Add(command, "$desktop_requirement", snapshot.DesktopRequirementCode);
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException("recurring_setup_intent_sqlite_failure", "The recurring setup intent insert did not affect exactly one row.");
    }

    private static PersistedIntent? ReadByIdentity(SqliteConnection connection, SqliteTransaction transaction, RecurringSetupIntentSnapshot snapshot) =>
        ReadSingle(connection, transaction, """
            WHERE idempotency_key = $idempotency_key AND current_user_sid = $current_user_sid AND session_binding = $session_binding
            """, new Dictionary<string, object?>
        {
            ["$idempotency_key"] = snapshot.IdempotencyKey,
            ["$current_user_sid"] = snapshot.CurrentUserSid,
            ["$session_binding"] = snapshot.SessionBinding,
        });

    internal static PersistedIntent? ReadByIntentId(SqliteConnection connection, SqliteTransaction transaction, string intentId) =>
        ReadSingle(connection, transaction, "WHERE intent_id = $intent_id", new Dictionary<string, object?> { ["$intent_id"] = intentId });

    private static PersistedIntent? ReadSingle(SqliteConnection connection, SqliteTransaction transaction, string predicate, IReadOnlyDictionary<string, object?> parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding,
                   status_code, requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id,
                   created_at_utc, updated_at_utc, version, terminal_reason_code,
                   scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms,
                   lease_valid_until_utc, output_directory, frozen_file_name,
                   recurring_schedule_kind_code, recurring_time_zone_id, recurring_time_zone_rules_digest, recurring_schedule_digest,
                   recurring_local_start_date, recurring_local_end_date, recurring_local_wall_clock_seconds, recurring_weekday_mask,
                   recurring_maximum_occurrences, recurring_recording_duration_ticks, recurring_latest_start_grace_ticks,
                   recurring_max_uses, recurring_max_cumulative_duration_ticks, recurring_authorization_valid_until_utc,
                   recurring_target_type_code, recurring_audio_mode_code, recurring_backend_code, recurring_countdown_seconds,
                   recurring_output_directory, recurring_filename_prefix, recurring_output_conflict_policy_code,
                   recurring_wake_policy_code, recurring_desktop_requirement_code
            FROM setup_intents
            {predicate}
            LIMIT 1;
            """;
        foreach (var parameter in parameters) Add(command, parameter.Key, parameter.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPersisted(reader) : null;
    }

    private static PersistedIntent ReadPersisted(SqliteDataReader reader) => new(
        ReadRequiredText(reader, 0), ReadRequiredText(reader, 1), ReadRequiredText(reader, 2), ReadRequiredText(reader, 3),
        ReadRequiredText(reader, 4), ReadRequiredText(reader, 5), ReadRequiredText(reader, 6), ReadUtcDateTimeOffset(reader, 7),
        ReadUtcDateTimeOffset(reader, 8), ReadNullableText(reader, 9), ReadNullableText(reader, 10), ReadNullableText(reader, 11),
        ReadNullableText(reader, 12), ReadUtcDateTimeOffset(reader, 13), ReadUtcDateTimeOffset(reader, 14), ReadInt64(reader, 15),
        ReadNullableText(reader, 16), ReadNullableUtcDateTimeOffset(reader, 17), ReadNullableUtcDateTimeOffset(reader, 18),
        ReadNullableUtcDateTimeOffset(reader, 19), ReadNullableInt64(reader, 20), ReadNullableUtcDateTimeOffset(reader, 21),
        ReadNullableText(reader, 22), ReadNullableText(reader, 23), ReadNullableText(reader, 24), ReadNullableText(reader, 25),
        ReadNullableText(reader, 26), ReadNullableText(reader, 27), ReadNullableText(reader, 28), ReadNullableText(reader, 29),
        ReadNullableInt64(reader, 30), ReadNullableInt64(reader, 31), ReadNullableInt64(reader, 32), ReadNullableInt64(reader, 33),
        ReadNullableInt64(reader, 34), ReadNullableInt64(reader, 35), ReadNullableInt64(reader, 36), ReadNullableUtcDateTimeOffset(reader, 37),
        ReadNullableText(reader, 38), ReadNullableText(reader, 39), ReadNullableText(reader, 40), ReadNullableInt64(reader, 41),
        ReadNullableText(reader, 42), ReadNullableText(reader, 43), ReadNullableText(reader, 44), ReadNullableText(reader, 45), ReadNullableText(reader, 46));

    internal static RecurringSetupIntentReadback ValidatePersistedAndRehydrate(
        PersistedIntent persisted,
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (persisted.IntentKindCode != RecurringSetupIntentCodes.IntentKind)
            throw new PersistedSnapshotException("The persisted setup intent kind is not recurring.");
        if (persisted.StatusCode is null)
            throw new PersistedSnapshotException("The persisted recurring status is missing.");
        RecurringSetupIntentStatus status;
        try { status = RecurringSetupIntentStatusCodes.Parse(persisted.StatusCode); }
        catch (Phase3DomainException exception) { throw new PersistedSnapshotException(exception.Message, exception); }
        if (persisted.CreatedAtUtc != persisted.RequestedAtUtc || persisted.ExpiresAtUtc <= persisted.CreatedAtUtc ||
            persisted.UpdatedAtUtc < persisted.CreatedAtUtc || persisted.Version < 0)
            throw new PersistedSnapshotException("The persisted recurring intent time or version window is invalid.");
        if (status == RecurringSetupIntentStatus.RegionSelectionPending)
        {
            if (persisted.Version != 0 || persisted.TerminalReasonCode is not null || persisted.UpdatedAtUtc != persisted.CreatedAtUtc)
                throw new PersistedSnapshotException("The persisted recurring intent is not at its initial setup state.");
            if (SqliteRecurringSetupPreparationTransaction.HasPreparationWithinTransaction(connection, transaction, persisted.IntentId))
                throw new PersistedSnapshotException("The initial recurring intent unexpectedly retains a preparation relation.");
        }
        else if (status is RecurringSetupIntentStatus.Rejected or RecurringSetupIntentStatus.Expired)
        {
            if (persisted.Version is not (1 or 2) ||
                !RecurringSetupTerminalReasonPolicy.Allows(status, persisted.TerminalReasonCode) ||
                (status == RecurringSetupIntentStatus.Expired && persisted.UpdatedAtUtc < persisted.ExpiresAtUtc))
                throw new PersistedSnapshotException("The persisted recurring terminal kind, reason, version or time is invalid.");
            if ((persisted.Version == 2) != SqliteRecurringSetupPreparationTransaction.HasPreparationWithinTransaction(connection, transaction, persisted.IntentId))
                throw new PersistedSnapshotException("The recurring terminal version and preparation relation do not describe the same source state.");
        }
        else if (status == RecurringSetupIntentStatus.LeaseApprovalPending)
        {
            if (persisted.Version != 1 || persisted.TerminalReasonCode is not null)
                throw new PersistedSnapshotException("The persisted recurring intent is not a valid prepared setup state.");
        }
        else if (status == RecurringSetupIntentStatus.Activated)
        {
            if (persisted.Version != 2 || persisted.TerminalReasonCode is not null)
                throw new PersistedSnapshotException("The persisted recurring intent is not a valid activated setup state.");
        }
        else
            throw new PersistedSnapshotException("The persisted recurring intent status is outside the implemented lifecycle.");
        if (persisted.PlanId is not null || persisted.OccurrenceId is not null || persisted.LeaseId is not null || persisted.ScopeId is not null ||
            persisted.ScheduledStartUtc is not null || persisted.LatestStartUtc is not null || persisted.PlannedEndUtc is not null ||
            persisted.MaximumDurationMs is not null || persisted.LegacyOutputDirectory is not null || persisted.FrozenFileName is not null)
            throw new PersistedSnapshotException("The recurring setup intent contains execution associations outside this setup boundary.");
        if (persisted.LeaseValidUntilUtc is null ||
            persisted.AuthorizationValidUntilUtc is null ||
            persisted.LeaseValidUntilUtc != persisted.AuthorizationValidUntilUtc ||
            persisted.TargetTypeCode != RecurringSetupIntentCodes.FixedRegionTarget ||
            persisted.AudioModeCode != RecurringSetupIntentCodes.AudioNone ||
            persisted.BackendCode != RecurringSetupIntentCodes.FfmpegRegionBackend ||
            persisted.CountdownSeconds != 0 ||
            persisted.OutputConflictPolicyCode != RecurringSetupIntentCodes.FailIfExists ||
            persisted.WakePolicyCode != RecurringSetupIntentCodes.NaturalWakeOnly ||
            persisted.DesktopRequirementCode != RecurringSetupIntentCodes.InteractiveDesktopRequired)
            throw new PersistedSnapshotException("The persisted recurring recording and authorization policies are not immutable.");

        var schedule = RehydrateSchedule(persisted);
        if (!string.Equals(persisted.LocalStartDate, schedule.LocalStartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(persisted.LocalEndDate, schedule.LocalEndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            persisted.LocalWallClockSeconds != (long)schedule.LocalWallClockTime.Hour * 3600 + schedule.LocalWallClockTime.Minute * 60 + schedule.LocalWallClockTime.Second ||
            persisted.WeekdayMask != schedule.WeekdayMask ||
            persisted.MaximumOccurrences != schedule.MaximumOccurrences ||
            persisted.RecordingDurationTicks != schedule.RecordingDuration.Ticks ||
            persisted.LatestStartGraceTicks != schedule.LatestStartGrace.Ticks)
            throw new PersistedSnapshotException("The persisted recurring schedule scalar columns are inconsistent.");
        var snapshot = RecurringSetupIntentSnapshot.CreateForPersistence(
            persisted.IntentId, persisted.IdempotencyKey, persisted.CurrentUserSid, persisted.SessionBinding,
            persisted.RequestedAtUtc, persisted.ExpiresAtUtc, schedule,
            RequiredText(persisted.ScheduleDigest, "recurring_schedule_digest"),
            RequiredText(persisted.TimeZoneRulesDigest, "recurring_time_zone_rules_digest"),
            checked((int)RequiredLong(persisted.MaxUses, "recurring_max_uses")),
            TimeSpan.FromTicks(RequiredLong(persisted.MaxCumulativeDurationTicks, "recurring_max_cumulative_duration_ticks")),
            persisted.LeaseValidUntilUtc ?? throw new PersistedSnapshotException("The recurring lease validity is missing."),
            RequiredText(persisted.OutputDirectory, "recurring_output_directory"),
            RequiredText(persisted.FilenamePrefix, "recurring_filename_prefix"),
            persisted.RequestDigest);
        ValidateSnapshot(snapshot);
        if (status == RecurringSetupIntentStatus.LeaseApprovalPending)
        {
            var pending = SqliteRecurringSetupPreparationTransaction.ValidatePreparedChainWithinTransaction(
                connection,
                transaction,
                persisted,
                snapshot);
            if (pending.Preparation.PreparedAtUtc != persisted.UpdatedAtUtc)
                throw new PersistedSnapshotException("The pending intent time does not match its preparation.");
        }
        else if (status == RecurringSetupIntentStatus.Activated)
        {
            var chain = SqliteRecurringSetupPreparationTransaction.ValidateActivatedChainWithinTransaction(
                connection,
                transaction,
                persisted,
                snapshot);
            ValidateActivatedApprovalEvidence(connection, transaction, persisted, chain);
        }
        else if (status is RecurringSetupIntentStatus.Rejected or RecurringSetupIntentStatus.Expired && persisted.Version == 2)
        {
            SqliteRecurringSetupPreparationTransaction.ValidateTerminalChainWithinTransaction(
                connection, transaction, persisted, snapshot);
        }
        return new RecurringSetupIntentReadback(persisted.IntentId, status, persisted.Version, persisted.TerminalReasonCode, snapshot);
    }

    private static void ValidateActivatedApprovalEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedIntent persisted,
        RecurringPreparedChainSnapshot chain)
    {
        if (SqliteRecurringLeaseLocalApprovalEvidenceReader.CountWithinTransaction(
                connection,
                transaction,
                chain.Lease.LeaseId) != 1)
        {
            throw new PersistedSnapshotException("The activated recurring intent must have exactly one local approval evidence row.");
        }

        var evidence = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(
            connection,
            transaction,
            chain.Lease.LeaseId)
            ?? throw new PersistedSnapshotException("The activated recurring intent has no local approval evidence.");

        if (!string.Equals(evidence.LeaseId, chain.Lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(evidence.PlanId, chain.Plan.Id, StringComparison.Ordinal) ||
            !string.Equals(evidence.ConfigurationDigest, chain.Configuration.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.AuthorizationDigest, chain.Lease.AuthorizationDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.CurrentUserSid, persisted.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(evidence.SessionBinding, persisted.SessionBinding, StringComparison.Ordinal) ||
            evidence.ApprovedAtUtc < chain.Preparation.PreparedAtUtc ||
            evidence.ApprovedAtUtc > persisted.UpdatedAtUtc ||
            evidence.ApprovedAtUtc > chain.Plan.UpdatedAtUtc ||
            evidence.ApprovedAtUtc > chain.Lease.UpdatedAtUtc ||
            evidence.ApprovedAtUtc >= persisted.ExpiresAtUtc ||
            evidence.ApprovedAtUtc >= chain.Lease.ValidUntilUtc)
        {
            throw new PersistedSnapshotException("The activated recurring approval evidence is not bound to the exact setup chain and time window.");
        }
    }

    private static RecurringPlanSchedule RehydrateSchedule(PersistedIntent persisted)
    {
        var kind = RequiredText(persisted.ScheduleKindCode, "recurring_schedule_kind_code") switch
        {
            "daily" => RecurringScheduleKind.Daily,
            "weekly" => RecurringScheduleKind.Weekly,
            _ => throw new PersistedSnapshotException("The persisted recurring schedule kind is invalid."),
        };
        var timeZoneId = RequiredText(persisted.TimeZoneId, "recurring_time_zone_id");
        var start = ParseDate(persisted.LocalStartDate, "recurring_local_start_date");
        var end = ParseDate(persisted.LocalEndDate, "recurring_local_end_date");
        var wallClockSeconds = checked((int)RequiredLong(persisted.LocalWallClockSeconds, "recurring_local_wall_clock_seconds"));
        var hour = wallClockSeconds / 3600;
        var minute = wallClockSeconds / 60 % 60;
        var second = wallClockSeconds % 60;
        var mask = checked((int)RequiredLong(persisted.WeekdayMask, "recurring_weekday_mask"));
        var days = kind == RecurringScheduleKind.Weekly ? DaysFromMask(mask) : null;
        try
        {
            return new RecurringPlanSchedule(kind, timeZoneId, start, end, new TimeOnly(hour, minute, second),
                checked((int)RequiredLong(persisted.MaximumOccurrences, "recurring_maximum_occurrences")),
                TimeSpan.FromTicks(RequiredLong(persisted.RecordingDurationTicks, "recurring_recording_duration_ticks")),
                TimeSpan.FromTicks(RequiredLong(persisted.LatestStartGraceTicks, "recurring_latest_start_grace_ticks")), days);
        }
        catch (Exception exception) when (exception is Phase3DomainException or ArgumentException or OverflowException)
        {
            throw new PersistedSnapshotException("The persisted recurring schedule is invalid.", exception);
        }
    }

    private static IReadOnlyList<DayOfWeek> DaysFromMask(int mask)
    {
        if (mask is < 1 or > 127) throw new PersistedSnapshotException("The persisted weekly weekday mask is invalid.");
        var values = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        return values.Where((day, index) => (mask & (1 << index)) != 0).ToArray();
    }

    private static bool MatchesImmutableRequest(RecurringPlanSchedule leftSchedule, RecurringPlanSchedule rightSchedule) => leftSchedule.CanonicalDigest == rightSchedule.CanonicalDigest;

    private static bool MatchesImmutableRequest(RecurringSetupIntentSnapshot persisted, RecurringSetupIntentSnapshot requested) =>
        persisted.IntentKindCode == requested.IntentKindCode && persisted.IdempotencyKey == requested.IdempotencyKey &&
        persisted.RequestDigest == requested.RequestDigest && persisted.CurrentUserSid == requested.CurrentUserSid &&
        persisted.SessionBinding == requested.SessionBinding && persisted.ExpiresAtUtc == requested.ExpiresAtUtc &&
        persisted.LeaseValidUntilUtc == requested.LeaseValidUntilUtc && persisted.ScheduleDigest == requested.ScheduleDigest &&
        persisted.TimeZoneRulesDigest == requested.TimeZoneRulesDigest && persisted.MaxUses == requested.MaxUses &&
        persisted.MaxCumulativeDuration == requested.MaxCumulativeDuration && persisted.OutputDirectory == requested.OutputDirectory &&
        persisted.FilenamePrefix == requested.FilenamePrefix && MatchesImmutableRequest(persisted.Schedule, requested.Schedule);

    private static void ValidateSnapshot(RecurringSetupIntentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCanonicalId(snapshot.IntentId, RecurringSetupIntentSnapshot.MaximumIdLength, false, "intent_id");
        if (snapshot.IntentKindCode != RecurringSetupIntentCodes.IntentKind) throw new Phase3DomainException("recurring_setup_intent_kind_invalid", "The recurring setup intent kind is not supported.");
        ValidateCanonicalId(snapshot.IdempotencyKey, RecurringSetupIntentSnapshot.MaximumIdLength, false, "idempotency_key");
        ValidateCanonicalText(snapshot.CurrentUserSid, RecurringSetupIntentSnapshot.MaximumBindingLength, false, "current_user_sid");
        ValidateCanonicalText(snapshot.SessionBinding, RecurringSetupIntentSnapshot.MaximumBindingLength, false, "session_binding");
        ValidateDigest(snapshot.RequestDigest, "request_digest");
        foreach (var value in new[] { snapshot.RequestedAtUtc, snapshot.ExpiresAtUtc, snapshot.LeaseValidUntilUtc }) ValidateUtc(value, "recurring_setup_intent_time_invalid");
        if (snapshot.ExpiresAtUtc != snapshot.LeaseValidUntilUtc || snapshot.ExpiresAtUtc <= snapshot.RequestedAtUtc) throw new Phase3DomainException("recurring_setup_intent_expiry_invalid", "The recurring setup intent expiry and lease validity must be ordered and equal.");
        var schedule = snapshot.Schedule ?? throw new Phase3DomainException("recurring_setup_intent_schedule_missing", "The recurring setup schedule is required.");
        if (schedule.LocalEndDate.DayNumber - schedule.LocalStartDate.DayNumber > RecurringPlanSetupLimits.MaximumDateSpanDays || schedule.MaximumOccurrences is < 1 or > RecurringPlanSetupLimits.MaximumOccurrences || schedule.RecordingDuration.TotalSeconds is < RecurringPlanSetupLimits.MinimumDurationSeconds or > RecurringPlanSetupLimits.MaximumDurationSeconds || schedule.LatestStartGrace.TotalSeconds is < 0 or > RecurringPlanSetupLimits.MaximumGraceSeconds)
            throw new Phase3DomainException("recurring_setup_intent_schedule_limit_invalid", "The recurring setup schedule exceeds the bounded contract.");
        var requiredTotalTicks = checked((long)schedule.MaximumOccurrences * schedule.RecordingDuration.Ticks);
        if (snapshot.MaxUses != schedule.MaximumOccurrences || snapshot.MaxCumulativeDuration.Ticks < requiredTotalTicks || snapshot.MaxCumulativeDuration.Ticks > TimeSpan.FromSeconds(RecurringPlanSetupLimits.MaximumTotalDurationSeconds).Ticks)
            throw new Phase3DomainException("recurring_setup_intent_quota_invalid", "The recurring authorization quota does not cover the schedule exactly enough.");
        if (snapshot.ScheduleDigest != schedule.CanonicalDigest || snapshot.TimeZoneRulesDigest != RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo))
            throw new Phase3DomainException("recurring_setup_intent_schedule_digest_mismatch", "The recurring schedule digest or time-zone rules digest is not canonical.");
        DateTimeOffset latestEnd;
        try { latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("recurring-setup-intent-boundary", 1, schedule); }
        catch (Phase3DomainException exception) { throw new Phase3DomainException("recurring_setup_intent_schedule_invalid", exception.Message); }
        if (snapshot.LeaseValidUntilUtc <= latestEnd) throw new Phase3DomainException("recurring_setup_intent_lease_validity_boundary_invalid", "The recurring lease validity must be strictly later than the latest scheduled run.");
        if (RecurringPlanSetupLimits.ContainsTraversalPathSegment(snapshot.OutputDirectory)) throw new Phase3DomainException("recurring_setup_intent_output_directory_traversal", "The recurring output directory must not contain traversal segments.");
        var normalizedDirectory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(snapshot.OutputDirectory);
        if (normalizedDirectory != snapshot.OutputDirectory || snapshot.OutputDirectory.Length > RecurringPlanSetupLimits.MaximumOutputDirectoryLength) throw new Phase3DomainException("recurring_setup_intent_output_directory_invalid", "The recurring output directory is not canonical and bounded.");
        ValidateFilenamePrefix(snapshot.FilenamePrefix);
        if (snapshot.RequestDigest != snapshot.ComputeCanonicalDigest()) throw new Phase3DomainException("recurring_setup_intent_digest_mismatch", "The recurring request digest does not match the canonical snapshot.");
    }

    private static void ValidateFilenamePrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > RecurringPlanSetupLimits.MaximumFilenamePrefixLength || value is "." or ".." || value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal) || value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedDeviceName(value))
            throw new Phase3DomainException("recurring_setup_intent_filename_prefix_invalid", "The recurring filename prefix is not safe and canonical.");
        if (value.EnumerateRunes().Any(rune => !Rune.IsLetterOrDigit(rune) && rune.Value is not '-' and not '_' and not ' '))
            throw new Phase3DomainException("recurring_setup_intent_filename_prefix_invalid", "The recurring filename prefix contains an unsupported character.");
    }

    private static bool IsReservedDeviceName(string value)
    {
        var stem = value.TrimEnd(' ').Split('-', StringSplitOptions.None)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    private static string RequiredText(string? value, string field) => value is null || string.IsNullOrWhiteSpace(value) ? throw new PersistedSnapshotException($"The persisted {field} is missing.") : value;
    private static long RequiredLong(long? value, string field) => value ?? throw new PersistedSnapshotException($"The persisted {field} is missing.");
    private static DateOnly ParseDate(string? value, string field) => value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : throw new PersistedSnapshotException($"The persisted {field} is invalid.");
    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadInt64(reader, ordinal);
    private static void ValidateCanonicalId(string value, int maxLength, bool allowSeparators, string field) => ValidateCanonicalText(value, maxLength, allowSeparators, field);
    private static void ValidateCanonicalText(string value, int maxLength, bool allowSeparators, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maxLength || value.Any(char.IsControl) || (!allowSeparators && (value.Contains('/') || value.Contains('\\'))))
            throw new Phase3DomainException("recurring_setup_intent_input_invalid", $"The {field} is not canonical.");
    }
    private static void ValidateDigest(string value, string field)
    {
        if (value.Length != 64 || value.Any(character => character is < '0' or > '9' and < 'a' or > 'f')) throw new Phase3DomainException("recurring_setup_intent_digest_invalid", $"The {field} is not a lowercase SHA-256 digest.");
    }
    private static void ValidateUtc(DateTimeOffset value, string code)
    {
        if (value.Offset != TimeSpan.Zero) throw new Phase3DomainException(code, "The recurring setup intent time must be UTC.");
    }
    private static Phase3PersistenceException SnapshotInvalid(Exception exception) => new("recurring_setup_intent_snapshot_invalid", "The recurring setup intent snapshot is invalid.", exception);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        if (transaction is null) return;
        try { transaction.Rollback(); }
        catch (InvalidOperationException) { }
        catch (SqliteException) { }
    }

    internal sealed record PersistedIntent(
        string IntentId, string IntentKindCode, string IdempotencyKey, string RequestDigest, string CurrentUserSid, string SessionBinding,
        string StatusCode, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc, string? PlanId, string? OccurrenceId,
        string? LeaseId, string? ScopeId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version, string? TerminalReasonCode,
        DateTimeOffset? ScheduledStartUtc, DateTimeOffset? LatestStartUtc, DateTimeOffset? PlannedEndUtc, long? MaximumDurationMs,
        DateTimeOffset? LeaseValidUntilUtc, string? LegacyOutputDirectory, string? FrozenFileName,
        string? ScheduleKindCode, string? TimeZoneId, string? TimeZoneRulesDigest, string? ScheduleDigest,
        string? LocalStartDate, string? LocalEndDate, long? LocalWallClockSeconds, long? WeekdayMask, long? MaximumOccurrences,
        long? RecordingDurationTicks, long? LatestStartGraceTicks, long? MaxUses, long? MaxCumulativeDurationTicks,
        DateTimeOffset? AuthorizationValidUntilUtc, string? TargetTypeCode, string? AudioModeCode, string? BackendCode,
        long? CountdownSeconds, string? OutputDirectory, string? FilenamePrefix, string? OutputConflictPolicyCode,
        string? WakePolicyCode, string? DesktopRequirementCode)
    {
        internal RecurringSetupIntentStatus Status => RecurringSetupIntentStatusCodes.Parse(StatusCode);
    }
}
