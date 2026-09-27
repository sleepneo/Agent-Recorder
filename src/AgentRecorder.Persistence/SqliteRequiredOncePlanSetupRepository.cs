using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record RequiredOncePlanSetupRequestSnapshot(
    string SetupIntentId,
    string IdempotencyKey,
    string CurrentUserSid,
    string SessionBinding,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    TimeSpan Duration,
    string OutputDirectory,
    string FrozenFileName);

internal sealed record RequiredOncePlanRegionSelection(
    DateTimeOffset SelectedAtUtc,
    string StableDisplayFingerprint,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    int DpiX,
    int DpiY,
    int PhysicalWidth,
    int PhysicalHeight,
    AuthorizedDisplayOrientation Orientation,
    string TopologyDigest);

internal sealed record RequiredOncePlanSetupReadback(
    RequiredOncePlanSetupRequestSnapshot Request,
    string StatusCode,
    string? ReasonCode,
    long Version,
    string? PlanId,
    string? OccurrenceId,
    RequiredOncePlanRegionSelection? Selection);

internal enum RequiredOncePlanSetupWriteStatus
{
    Created,
    Existing,
    Conflict,
    Rejected,
    Expired,
    Updated,
}

internal sealed record RequiredOncePlanSetupWriteResult(
    RequiredOncePlanSetupWriteStatus Status,
    RequiredOncePlanSetupReadback? State,
    string? ReasonCode);

/// <summary>
/// Durable setup boundary for required-mode one-time plans. All writes that
/// make an occurrence runnable happen in one immediate SQLite transaction;
/// this repository has no dependency on capture backends or proof issuers.
/// </summary>
internal sealed class SqliteRequiredOncePlanSetupRepository : SqliteRepositoryBase
{
    private const string SelectColumns = "setup_intent_id, idempotency_key, request_digest, current_user_sid, session_binding, status_code, reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, duration_ms, output_directory, frozen_file_name, expires_at_utc, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, topology_digest, selected_at_utc, plan_id, occurrence_id, created_at_utc, updated_at_utc, version";
    private readonly Action<string>? _failureHook;

    internal SqliteRequiredOncePlanSetupRepository(SqliteOperationalStore store, Action<string>? failureHookForTest = null)
        : base(store) => _failureHook = failureHookForTest;

    internal RequiredOncePlanSetupWriteResult CreateOrGet(
        RequiredOncePlanSetupRequestSnapshot request,
        DateTimeOffset nowUtc)
    {
        ValidateRequest(request);
        ValidateUtc(nowUtc);
        var digest = RequestDigest(request);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var existing = ReadByIdentity(connection, transaction, request.CurrentUserSid, request.SessionBinding, request.IdempotencyKey);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return new(RequiredOncePlanSetupWriteStatus.Conflict, existing.State, "idempotency_key_reused");
                }
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Existing, existing.State, null);
            }

            var status = request.LatestStartUtc <= nowUtc ? "expired" : "region_selection_pending";
            var reason = status == "expired" ? "latest_start_window_missed" : null;
            var expiry = request.ExpiresAtUtc;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO required_once_setup_intents
                    (setup_intent_id, idempotency_key, request_digest, current_user_sid, session_binding,
                     status_code, reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc,
                     duration_ms, output_directory, frozen_file_name, expires_at_utc,
                     created_at_utc, updated_at_utc, version)
                    VALUES ($id, $key, $digest, $sid, $session, $status, $reason, $start, $latest,
                            $end, $duration, $output, $filename, $expiry, $now, $now, 0);
                    """;
                Add(command, "$id", request.SetupIntentId);
                Add(command, "$key", request.IdempotencyKey);
                Add(command, "$digest", digest);
                Add(command, "$sid", request.CurrentUserSid);
                Add(command, "$session", request.SessionBinding);
                Add(command, "$status", status);
                Add(command, "$reason", reason);
                Add(command, "$start", UtcTicksInput(request.ScheduledStartUtc));
                Add(command, "$latest", UtcTicksInput(request.LatestStartUtc));
                Add(command, "$end", UtcTicksInput(request.PlannedEndUtc));
                Add(command, "$duration", DurationMillisecondsInput(request.Duration));
                Add(command, "$output", request.OutputDirectory);
                Add(command, "$filename", request.FrozenFileName);
                Add(command, "$expiry", UtcTicksInput(expiry));
                Add(command, "$now", UtcTicksInput(nowUtc));
                command.ExecuteNonQuery();
            }

            var state = new RequiredOncePlanSetupReadback(request, status, reason, 0, null, null, null);
            transaction.Commit();
            return new(status == "expired" ? RequiredOncePlanSetupWriteStatus.Expired : RequiredOncePlanSetupWriteStatus.Created, state, reason);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException)
                throw InvalidSnapshot(exception);
            if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                throw new Phase3PersistenceException("required_once_setup_constraint", "The required plan setup violated a persisted constraint.", exception);
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOncePlanSetupReadback? Get(string intentId, string currentUserSid, string sessionBinding)
    {
        if (!Canonical(intentId) || !Canonical(currentUserSid) || !Canonical(sessionBinding)) return null;
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var row = ReadById(connection, transaction, intentId);
            if (row is null || row.State.Request.CurrentUserSid != currentUserSid || row.State.Request.SessionBinding != sessionBinding)
            {
                transaction.Commit();
                return null;
            }
            ValidateActivationChain(connection, transaction, row.State, allowExecutionProgress: true);
            transaction.Commit();
            return row.State;
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            throw InfrastructureFailure(exception);
        }
    }

    internal IReadOnlyList<string> ListRecoverable(string currentUserSid, string sessionBinding, DateTimeOffset nowUtc)
    {
        if (!Canonical(currentUserSid) || !Canonical(sessionBinding) || nowUtc.Offset != TimeSpan.Zero)
            return Array.Empty<string>();
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            var result = new List<string>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT setup_intent_id FROM required_once_setup_intents WHERE current_user_sid = $sid AND session_binding = $session AND status_code IN ('region_selection_pending', 'creation_approval_pending') ORDER BY created_at_utc, setup_intent_id LIMIT 128;";
            Add(command, "$sid", currentUserSid);
            Add(command, "$session", sessionBinding);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadRequiredText(reader, 0));
            transaction.Commit();
            return result;
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOncePlanSetupWriteResult SaveSelection(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        RequiredOncePlanRegionSelection selection,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateUtc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var row = ReadById(connection, transaction, intentId);
            if (row is null || row.State.Request.CurrentUserSid != currentUserSid || row.State.Request.SessionBinding != sessionBinding)
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Rejected, row?.State, "setup_identity_mismatch");
            }
            var state = row.State;
            if (state.StatusCode == "creation_approval_pending" && SameSelection(state.Selection, selection))
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Existing, state, null);
            }
            if (state.StatusCode != "region_selection_pending")
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Conflict, state, "stale_selection_callback");
            }
            if (nowUtc >= state.Request.ExpiresAtUtc || nowUtc >= state.Request.LatestStartUtc)
                return ExpireWithinTransaction(connection, transaction, state, nowUtc,
                    nowUtc >= state.Request.LatestStartUtc ? "latest_start_window_missed" : "setup_intent_expired");

            ValidateSelection(selection, state.Request);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE required_once_setup_intents SET status_code = 'creation_approval_pending',
                      stable_display_fingerprint = $fingerprint,
                      display_bounds_x = $boundsX, display_bounds_y = $boundsY,
                      display_bounds_width = $boundsWidth, display_bounds_height = $boundsHeight,
                      region_x = $regionX, region_y = $regionY, region_width = $regionWidth, region_height = $regionHeight,
                      dpi_x = $dpiX, dpi_y = $dpiY, physical_width = $physicalWidth, physical_height = $physicalHeight,
                      orientation_code = $orientation, topology_digest = $topology,
                      selected_at_utc = $selectedAt, updated_at_utc = $now, version = version + 1
                    WHERE setup_intent_id = $id AND status_code = 'region_selection_pending' AND version = $version;
                    """;
                AddSelection(command, selection);
                Add(command, "$now", UtcTicksInput(nowUtc));
                Add(command, "$id", intentId);
                Add(command, "$version", state.Version);
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Commit();
                    return new(RequiredOncePlanSetupWriteStatus.Conflict, state, "stale_selection_callback");
                }
            }
            _failureHook?.Invoke("selection_saved_before_commit");
            var updated = ReadById(connection, transaction, intentId)!.State;
            transaction.Commit();
            return new(RequiredOncePlanSetupWriteStatus.Updated, updated, null);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                throw new Phase3PersistenceException("required_once_setup_constraint", "The region selection violated the required plan setup contract.", exception);
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOncePlanSetupWriteResult SetTerminal(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        string statusCode,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        if (statusCode is not ("rejected" or "expired") || !Canonical(reasonCode))
            throw InvalidArgument("A required setup terminal transition needs a stable terminal state and reason.");
        ValidateUtc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var row = ReadById(connection, transaction, intentId);
            if (row is null || row.State.Request.CurrentUserSid != currentUserSid || row.State.Request.SessionBinding != sessionBinding)
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Rejected, null, "setup_identity_mismatch");
            }
            var state = row.State;
            if (state.StatusCode is "scheduled" or "rejected" or "expired")
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Existing, state, state.ReasonCode);
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE required_once_setup_intents SET status_code = $status, reason_code = $reason, updated_at_utc = $now, version = version + 1 WHERE setup_intent_id = $id AND status_code = $expected AND version = $version;";
                Add(command, "$status", statusCode);
                Add(command, "$reason", reasonCode);
                Add(command, "$now", UtcTicksInput(nowUtc));
                Add(command, "$id", intentId);
                Add(command, "$expected", state.StatusCode);
                Add(command, "$version", state.Version);
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Commit();
                    return new(RequiredOncePlanSetupWriteStatus.Conflict, state, "stale_setup_callback");
                }
            }
            var updated = ReadById(connection, transaction, intentId)!.State;
            transaction.Commit();
            return new(statusCode == "expired" ? RequiredOncePlanSetupWriteStatus.Expired : RequiredOncePlanSetupWriteStatus.Updated, updated, reasonCode);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOncePlanSetupWriteResult Activate(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        long expectedVersion,
        string approvalId,
        DateTimeOffset nowUtc)
    {
        if (!Canonical(approvalId)) throw InvalidArgument("The local creation approval identifier is invalid.");
        ValidateUtc(nowUtc);
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var row = ReadById(connection, transaction, intentId);
            if (row is null || row.State.Request.CurrentUserSid != currentUserSid || row.State.Request.SessionBinding != sessionBinding)
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Rejected, row?.State, "setup_identity_mismatch");
            }
            var state = row.State;
            if (state.StatusCode == "scheduled")
            {
                ValidateActivationChain(connection, transaction, state);
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Existing, state, null);
            }
            if (state.StatusCode != "creation_approval_pending" || state.Version != expectedVersion || state.Selection is null)
            {
                transaction.Commit();
                return new(RequiredOncePlanSetupWriteStatus.Conflict, state, "stale_creation_approval_callback");
            }
            if (nowUtc >= state.Request.ExpiresAtUtc || nowUtc >= state.Request.LatestStartUtc)
                return ExpireWithinTransaction(connection, transaction, state, nowUtc,
                    nowUtc >= state.Request.LatestStartUtc ? "latest_start_window_missed" : "setup_intent_expired");

            var planId = "required-once-plan-" + Guid.NewGuid().ToString("N");
            var occurrenceId = "required-once-occurrence-" + Guid.NewGuid().ToString("N");
            var nowTicks = UtcTicksInput(nowUtc);
            using (var plan = connection.CreateCommand())
            {
                plan.Transaction = transaction;
                plan.CommandText = "INSERT INTO plans (id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ($id, 1, 'enabled', $now, $now, 0);";
                Add(plan, "$id", planId);
                Add(plan, "$now", nowTicks);
                plan.ExecuteNonQuery();
            }
            _failureHook?.Invoke("plan_inserted");
            using (var occurrence = connection.CreateCommand())
            {
                occurrence.Transaction = transaction;
                occurrence.CommandText = "INSERT INTO plan_occurrences (id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ($id, $plan, 'scheduled', $start, $end, NULL, NULL, $now, $now, 0);";
                Add(occurrence, "$id", occurrenceId);
                Add(occurrence, "$plan", planId);
                Add(occurrence, "$start", UtcTicksInput(state.Request.ScheduledStartUtc));
                Add(occurrence, "$end", UtcTicksInput(state.Request.PlannedEndUtc));
                Add(occurrence, "$now", nowTicks);
                occurrence.ExecuteNonQuery();
            }
            _failureHook?.Invoke("occurrence_inserted");
            InsertImmutableSpec(connection, transaction, state, planId, occurrenceId, approvalId, nowUtc);
            _failureHook?.Invoke("spec_inserted");
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE required_once_setup_intents SET status_code = 'scheduled', plan_id = $plan, occurrence_id = $occurrence, updated_at_utc = $now, version = version + 1 WHERE setup_intent_id = $id AND status_code = 'creation_approval_pending' AND version = $version AND current_user_sid = $sid AND session_binding = $session;";
                Add(update, "$plan", planId);
                Add(update, "$occurrence", occurrenceId);
                Add(update, "$now", nowTicks);
                Add(update, "$id", intentId);
                Add(update, "$version", expectedVersion);
                Add(update, "$sid", currentUserSid);
                Add(update, "$session", sessionBinding);
                if (update.ExecuteNonQuery() != 1)
                    throw new Phase3PersistenceException("required_once_setup_cas_conflict", "The required setup changed before activation.");
            }
            _failureHook?.Invoke("setup_activated_before_commit");
            var activated = ReadById(connection, transaction, intentId)!.State;
            ValidateActivationChain(connection, transaction, activated);
            transaction.Commit();
            return new(RequiredOncePlanSetupWriteStatus.Updated, activated, null);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                throw new Phase3PersistenceException("required_once_setup_constraint", "The required plan activation violated a persisted constraint.", exception);
            throw InfrastructureFailure(exception);
        }
    }

    private static void InsertImmutableSpec(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequiredOncePlanSetupReadback state,
        string planId,
        string occurrenceId,
        string approvalId,
        DateTimeOffset nowUtc)
    {
        var selection = state.Selection ?? throw InvalidArgument("The required plan setup has no frozen region selection.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO required_once_authorized_specs
            (plan_id, occurrence_id, setup_intent_id, creation_approval_id, approved_current_user_sid,
             approved_session_binding, approved_at_utc, scheduled_start_utc, latest_start_utc, planned_end_utc,
             duration_ms, output_directory, frozen_file_name, stable_display_fingerprint,
             display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height,
             region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height,
             orientation_code, topology_digest, backend_code, audio_mode_code, countdown_seconds,
             wake_policy_code, desktop_requirement_code, output_conflict_policy_code)
            VALUES ($plan, $occurrence, $intent, $approval, $sid, $session, $approvedAt,
             $start, $latest, $end, $duration, $output, $filename, $fingerprint,
             $boundsX, $boundsY, $boundsWidth, $boundsHeight, $regionX, $regionY, $regionWidth, $regionHeight,
             $dpiX, $dpiY, $physicalWidth, $physicalHeight, $orientation, $topology,
             'ffmpeg-region', 'none', 0, 'natural_wake_only', 'interactive_desktop_required', 'fail_if_exists');
            """;
        Add(command, "$plan", planId);
        Add(command, "$occurrence", occurrenceId);
        Add(command, "$intent", state.Request.SetupIntentId);
        Add(command, "$approval", approvalId);
        Add(command, "$sid", state.Request.CurrentUserSid);
        Add(command, "$session", state.Request.SessionBinding);
        Add(command, "$approvedAt", UtcTicksInput(nowUtc));
        Add(command, "$start", UtcTicksInput(state.Request.ScheduledStartUtc));
        Add(command, "$latest", UtcTicksInput(state.Request.LatestStartUtc));
        Add(command, "$end", UtcTicksInput(state.Request.PlannedEndUtc));
        Add(command, "$duration", DurationMillisecondsInput(state.Request.Duration));
        Add(command, "$output", state.Request.OutputDirectory);
        Add(command, "$filename", state.Request.FrozenFileName);
        AddSelection(command, selection);
        command.ExecuteNonQuery();
    }

    private static void AddSelection(SqliteCommand command, RequiredOncePlanRegionSelection selection)
    {
        Add(command, "$fingerprint", selection.StableDisplayFingerprint);
        Add(command, "$boundsX", selection.DisplayBounds.X);
        Add(command, "$boundsY", selection.DisplayBounds.Y);
        Add(command, "$boundsWidth", selection.DisplayBounds.Width);
        Add(command, "$boundsHeight", selection.DisplayBounds.Height);
        Add(command, "$regionX", selection.RegionWithinDisplay.X);
        Add(command, "$regionY", selection.RegionWithinDisplay.Y);
        Add(command, "$regionWidth", selection.RegionWithinDisplay.Width);
        Add(command, "$regionHeight", selection.RegionWithinDisplay.Height);
        Add(command, "$dpiX", selection.DpiX);
        Add(command, "$dpiY", selection.DpiY);
        Add(command, "$physicalWidth", selection.PhysicalWidth);
        Add(command, "$physicalHeight", selection.PhysicalHeight);
        Add(command, "$orientation", OrientationCode(selection.Orientation));
        Add(command, "$topology", selection.TopologyDigest);
        Add(command, "$selectedAt", UtcTicksInput(selection.SelectedAtUtc));
    }

    private sealed record RawReadback(string RequestDigest, RequiredOncePlanSetupReadback State);

    private static RawReadback? ReadById(SqliteConnection connection, SqliteTransaction transaction, string intentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM required_once_setup_intents WHERE setup_intent_id = $id LIMIT 2;";
        Add(command, "$id", intentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = Rehydrate(reader);
        if (reader.Read()) throw new PersistedSnapshotException("A required setup identifier resolved to duplicate rows.");
        return row;
    }

    private static RawReadback? ReadByIdentity(SqliteConnection connection, SqliteTransaction transaction, string sid, string session, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM required_once_setup_intents WHERE current_user_sid = $sid AND session_binding = $session AND idempotency_key = $key LIMIT 2;";
        Add(command, "$sid", sid);
        Add(command, "$session", session);
        Add(command, "$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = Rehydrate(reader);
        if (reader.Read()) throw new PersistedSnapshotException("A required setup identity/key resolved to duplicate rows.");
        return row;
    }

    private static RawReadback Rehydrate(SqliteDataReader reader)
    {
        try
        {
            var intentId = ReadRequiredText(reader, 0);
            var idempotencyKey = ReadRequiredText(reader, 1);
            var digest = ReadRequiredText(reader, 2);
            var sid = ReadRequiredText(reader, 3);
            var session = ReadRequiredText(reader, 4);
            var status = ReadRequiredText(reader, 5);
            var reason = ReadNullableText(reader, 6);
            var start = ReadUtcDateTimeOffset(reader, 7);
            var latest = ReadUtcDateTimeOffset(reader, 8);
            var end = ReadUtcDateTimeOffset(reader, 9);
            var duration = ReadDurationMilliseconds(reader, 10);
            var output = ReadRequiredText(reader, 11);
            var filename = ReadRequiredText(reader, 12);
            var expiry = ReadUtcDateTimeOffset(reader, 13);
            var selection = ReadSelection(reader, 14);
            var planId = ReadNullableText(reader, 30);
            var occurrenceId = ReadNullableText(reader, 31);
            var createdAt = ReadUtcDateTimeOffset(reader, 32);
            var updatedAt = ReadUtcDateTimeOffset(reader, 33);
            var version = ReadInt64(reader, 34);
            if (version < 0 || updatedAt < createdAt || createdAt > DateTimeOffset.UtcNow.AddMinutes(1) ||
                start >= latest || latest > end || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10) ||
                end - start < duration || expiry <= createdAt || status is not ("region_selection_pending" or "creation_approval_pending" or "scheduled" or "rejected" or "expired"))
                throw new PersistedSnapshotException("A required setup row violates its time, version, state, or duration contract.");
            var request = new RequiredOncePlanSetupRequestSnapshot(
                intentId, idempotencyKey, sid, session, createdAt, expiry, start, latest, end, duration, output, filename);
            if (!Canonical(intentId) || !Canonical(idempotencyKey) || !Canonical(sid) || !Canonical(session) ||
                !string.Equals(digest, RequestDigest(request), StringComparison.Ordinal))
                throw new PersistedSnapshotException("The required setup request digest or identity is invalid.");
            ValidateShape(status, reason, selection, planId, occurrenceId);
            return new(digest, new(request, status, reason, version, planId, occurrenceId, selection));
        }
        catch (PersistedSnapshotException) { throw; }
        catch (Exception exception) when (exception is Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new PersistedSnapshotException("The required setup row could not be rehydrated.", exception);
        }
    }

    private static RequiredOncePlanRegionSelection? ReadSelection(SqliteDataReader reader, int start)
    {
        if (reader.IsDBNull(start))
        {
            for (var index = start + 1; index <= start + 15; index++)
                if (!reader.IsDBNull(index)) throw new PersistedSnapshotException("An unselected required setup has partial region evidence.");
            return null;
        }
        for (var index = start + 1; index <= start + 15; index++)
            if (reader.IsDBNull(index)) throw new PersistedSnapshotException("A selected required setup has incomplete region evidence.");
        var bounds = new AuthorizedPhysicalRectangle(ReadInt32(reader, start + 1), ReadInt32(reader, start + 2), ReadInt32(reader, start + 3), ReadInt32(reader, start + 4));
        var region = new AuthorizedPhysicalRectangle(ReadInt32(reader, start + 5), ReadInt32(reader, start + 6), ReadInt32(reader, start + 7), ReadInt32(reader, start + 8));
        var orientation = ReadRequiredText(reader, start + 13) switch
        {
            "landscape" => AuthorizedDisplayOrientation.Landscape,
            "portrait" => AuthorizedDisplayOrientation.Portrait,
            "landscape_flipped" => AuthorizedDisplayOrientation.LandscapeFlipped,
            "portrait_flipped" => AuthorizedDisplayOrientation.PortraitFlipped,
            _ => throw new PersistedSnapshotException("The required setup orientation is unknown."),
        };
        var selection = new RequiredOncePlanRegionSelection(
            ReadUtcDateTimeOffset(reader, start + 15), ReadRequiredText(reader, start), bounds, region,
            ReadInt32(reader, start + 9), ReadInt32(reader, start + 10), ReadInt32(reader, start + 11),
            ReadInt32(reader, start + 12), orientation, ReadRequiredText(reader, start + 14));
        ValidateSelectionShape(selection);
        return selection;
    }

    private static void ValidateActivationChain(SqliteConnection connection, SqliteTransaction transaction, RequiredOncePlanSetupReadback state)
        => ValidateActivationChain(connection, transaction, state, allowExecutionProgress: false);

    private static void ValidateActivationChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequiredOncePlanSetupReadback state,
        bool allowExecutionProgress)
    {
        if (state.StatusCode != "scheduled") return;
        if (state.PlanId is null || state.OccurrenceId is null || state.Selection is null)
            throw new PersistedSnapshotException("A scheduled required setup is missing its plan, occurrence, or selection.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.is_one_time, p.status_code, o.status_code, o.window_start_utc, o.window_end_utc, o.run_id,
                   s.plan_id, s.occurrence_id, s.setup_intent_id, s.approved_current_user_sid,
                   s.approved_session_binding, s.scheduled_start_utc, s.latest_start_utc, s.planned_end_utc,
                   s.duration_ms, s.output_directory, s.frozen_file_name, s.backend_code, s.audio_mode_code,
                   s.countdown_seconds, s.wake_policy_code, s.desktop_requirement_code, s.output_conflict_policy_code,
                   s.creation_approval_id, s.approved_at_utc, s.stable_display_fingerprint,
                   s.display_bounds_x, s.display_bounds_y, s.display_bounds_width, s.display_bounds_height,
                   s.region_x, s.region_y, s.region_width, s.region_height, s.dpi_x, s.dpi_y,
                   s.physical_width, s.physical_height, s.orientation_code, s.topology_digest
            FROM plans AS p
            JOIN plan_occurrences AS o ON o.plan_id = p.id
            JOIN required_once_authorized_specs AS s ON s.plan_id = p.id AND s.occurrence_id = o.id
            WHERE p.id = $plan AND o.id = $occurrence AND s.setup_intent_id = $intent LIMIT 2;
            """;
        Add(command, "$plan", state.PlanId);
        Add(command, "$occurrence", state.OccurrenceId);
        Add(command, "$intent", state.Request.SetupIntentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new PersistedSnapshotException("A scheduled required setup has no complete immutable specification chain.");
        var occurrenceStatus = ReadRequiredText(reader, 2);
        var occurrenceRunId = ReadNullableText(reader, 5);
        var occurrenceShapeValid = allowExecutionProgress
            ? occurrenceStatus switch
            {
                "scheduled" or "pending_confirmation" or "missed" => occurrenceRunId is null,
                "run_created" or "completed" => Canonical(occurrenceRunId),
                "blocked" => occurrenceRunId is null || Canonical(occurrenceRunId),
                "cancelled" or "expired" => occurrenceRunId is null,
                _ => false,
            }
            : occurrenceStatus == "scheduled" && occurrenceRunId is null;
        if (ReadInt64(reader, 0) != 1 || ReadRequiredText(reader, 1) != "enabled" ||
            !occurrenceShapeValid || ReadUtcDateTimeOffset(reader, 3) != state.Request.ScheduledStartUtc ||
            ReadUtcDateTimeOffset(reader, 4) != state.Request.PlannedEndUtc ||
            ReadRequiredText(reader, 6) != state.PlanId || ReadRequiredText(reader, 7) != state.OccurrenceId ||
            ReadRequiredText(reader, 8) != state.Request.SetupIntentId ||
            ReadRequiredText(reader, 9) != state.Request.CurrentUserSid ||
            ReadRequiredText(reader, 10) != state.Request.SessionBinding ||
            ReadUtcDateTimeOffset(reader, 11) != state.Request.ScheduledStartUtc ||
            ReadUtcDateTimeOffset(reader, 12) != state.Request.LatestStartUtc ||
            ReadUtcDateTimeOffset(reader, 13) != state.Request.PlannedEndUtc ||
            ReadDurationMilliseconds(reader, 14) != state.Request.Duration ||
            ReadRequiredText(reader, 15) != state.Request.OutputDirectory ||
            ReadRequiredText(reader, 16) != state.Request.FrozenFileName ||
            ReadRequiredText(reader, 17) != "ffmpeg-region" || ReadRequiredText(reader, 18) != "none" ||
            ReadInt64(reader, 19) != 0 || ReadRequiredText(reader, 20) != "natural_wake_only" ||
            ReadRequiredText(reader, 21) != "interactive_desktop_required" || ReadRequiredText(reader, 22) != "fail_if_exists" ||
            !Canonical(ReadRequiredText(reader, 23)) || ReadUtcDateTimeOffset(reader, 24) >= state.Request.LatestStartUtc ||
            ReadRequiredText(reader, 25) != state.Selection.StableDisplayFingerprint ||
            ReadInt32(reader, 26) != state.Selection.DisplayBounds.X || ReadInt32(reader, 27) != state.Selection.DisplayBounds.Y ||
            ReadInt32(reader, 28) != state.Selection.DisplayBounds.Width || ReadInt32(reader, 29) != state.Selection.DisplayBounds.Height ||
            ReadInt32(reader, 30) != state.Selection.RegionWithinDisplay.X || ReadInt32(reader, 31) != state.Selection.RegionWithinDisplay.Y ||
            ReadInt32(reader, 32) != state.Selection.RegionWithinDisplay.Width || ReadInt32(reader, 33) != state.Selection.RegionWithinDisplay.Height ||
            ReadInt32(reader, 34) != state.Selection.DpiX || ReadInt32(reader, 35) != state.Selection.DpiY ||
            ReadInt32(reader, 36) != state.Selection.PhysicalWidth || ReadInt32(reader, 37) != state.Selection.PhysicalHeight ||
            ReadRequiredText(reader, 38) != OrientationCode(state.Selection.Orientation) ||
            ReadRequiredText(reader, 39) != state.Selection.TopologyDigest)
            throw new PersistedSnapshotException("A scheduled required plan disagrees with its immutable approved specification.");
        if (reader.Read()) throw new PersistedSnapshotException("A required setup resolved to duplicate activation chains.");
    }

    internal static RequiredOncePlanSetupReadback? ReadAndValidateExecutionProgressPlanWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId)
    {
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM required_once_setup_intents WHERE plan_id = $plan AND occurrence_id = $occurrence;";
        Add(count, "$plan", planId);
        Add(count, "$occurrence", occurrenceId);
        if (count.ExecuteScalar() is not long total || total > 1)
            throw new PersistedSnapshotException("Required execution ownership is missing or duplicated.");
        if (total == 0) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM required_once_setup_intents WHERE plan_id = $plan AND occurrence_id = $occurrence LIMIT 2;";
        Add(command, "$plan", planId);
        Add(command, "$occurrence", occurrenceId);
        RequiredOncePlanSetupReadback state;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            state = Rehydrate(reader).State;
            if (reader.Read()) throw new PersistedSnapshotException("Required execution ownership is duplicated.");
        }
        if (state.StatusCode != "scheduled" || state.PlanId != planId || state.OccurrenceId != occurrenceId)
            throw new PersistedSnapshotException("Required execution no longer belongs to an activated one-time setup.");
        ValidateActivationChain(connection, transaction, state, allowExecutionProgress: true);
        using var leaseEvidence = connection.CreateCommand();
        leaseEvidence.Transaction = transaction;
        leaseEvidence.CommandText = """
            SELECT EXISTS (SELECT 1 FROM consent_leases WHERE plan_id = $plan) OR
                   EXISTS (SELECT 1 FROM authorized_capture_scopes WHERE plan_id = $plan) OR
                   EXISTS (SELECT 1 FROM lease_uses WHERE occurrence_id = $occurrence) OR
                   EXISTS (SELECT 1 FROM recurring_consent_leases WHERE plan_id = $plan) OR
                   EXISTS (SELECT 1 FROM recurring_lease_uses WHERE occurrence_id = $occurrence);
            """;
        Add(leaseEvidence, "$plan", planId);
        Add(leaseEvidence, "$occurrence", occurrenceId);
        if (leaseEvidence.ExecuteScalar() is not long contamination || contamination != 0)
            throw new PersistedSnapshotException("Required execution is contaminated by Lease-backed authorization evidence.");
        return state;
    }

    internal static RequiredOncePlanSetupReadback? ReadAndValidateScheduledPlanWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM required_once_setup_intents WHERE plan_id = $plan;";
            Add(count, "$plan", planId);
            if (count.ExecuteScalar() is not long total || total > 1)
                throw new PersistedSnapshotException("A required one-time plan has duplicate setup ownership evidence.");
            if (total == 0) return null;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM required_once_setup_intents WHERE plan_id = $plan LIMIT 2;";
        Add(command, "$plan", planId);
        RequiredOncePlanSetupReadback state;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            state = Rehydrate(reader).State;
            if (reader.Read()) throw new PersistedSnapshotException("A required plan resolved to duplicate setup owners.");
        }
        if (state.StatusCode != "scheduled" || !string.Equals(state.PlanId, planId, StringComparison.Ordinal))
            throw new PersistedSnapshotException("A plan is associated with non-scheduled required setup evidence.");
        ValidateActivationChain(connection, transaction, state);
        using var lease = connection.CreateCommand();
        lease.Transaction = transaction;
        lease.CommandText = """
            SELECT
              EXISTS (SELECT 1 FROM consent_leases WHERE plan_id = $plan) OR
              EXISTS (SELECT 1 FROM authorized_capture_scopes WHERE plan_id = $plan) OR
              EXISTS (SELECT 1 FROM lease_uses WHERE occurrence_id = $occurrence) OR
              EXISTS (SELECT 1 FROM recording_runs WHERE occurrence_id = $occurrence) OR
              EXISTS (SELECT 1 FROM recurring_consent_leases WHERE plan_id = $plan) OR
              EXISTS (SELECT 1 FROM recurring_lease_uses WHERE occurrence_id = $occurrence);
            """;
        Add(lease, "$plan", planId);
        Add(lease, "$occurrence", state.OccurrenceId);
        if (lease.ExecuteScalar() is not long leaseEvidence || leaseEvidence != 0)
            throw new PersistedSnapshotException("A required one-time plan is contaminated by Lease-backed authorization evidence.");
        return state;
    }

    private static void ValidateShape(string status, string? reason, RequiredOncePlanRegionSelection? selection, string? planId, string? occurrenceId)
    {
        var valid = status switch
        {
            "region_selection_pending" => reason is null && selection is null && planId is null && occurrenceId is null,
            "creation_approval_pending" => reason is null && selection is not null && planId is null && occurrenceId is null,
            "scheduled" => reason is null && selection is not null && Canonical(planId) && Canonical(occurrenceId),
            "rejected" or "expired" => Canonical(reason) && planId is null && occurrenceId is null,
            _ => false,
        };
        if (!valid) throw new PersistedSnapshotException("The required setup state does not have its exact state-specific evidence shape.");
    }

    private static bool SameSelection(RequiredOncePlanRegionSelection? left, RequiredOncePlanRegionSelection right) =>
        left is not null && left == right;

    private static RequiredOncePlanSetupWriteResult ExpireWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequiredOncePlanSetupReadback state,
        DateTimeOffset nowUtc,
        string reason)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE required_once_setup_intents SET status_code = 'expired', reason_code = $reason, updated_at_utc = $now, version = version + 1 WHERE setup_intent_id = $id AND status_code = $status AND version = $version;";
        Add(update, "$reason", reason);
        Add(update, "$now", UtcTicksInput(nowUtc));
        Add(update, "$id", state.Request.SetupIntentId);
        Add(update, "$status", state.StatusCode);
        Add(update, "$version", state.Version);
        if (update.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException("required_once_setup_cas_conflict", "The required setup changed before expiry was recorded.");
        var expired = ReadById(connection, transaction, state.Request.SetupIntentId)!.State;
        transaction.Commit();
        return new(RequiredOncePlanSetupWriteStatus.Expired, expired, reason);
    }

    private static void ValidateRequest(RequiredOncePlanSetupRequestSnapshot request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Canonical(request.SetupIntentId) || !Canonical(request.IdempotencyKey) || !Canonical(request.CurrentUserSid) || !Canonical(request.SessionBinding) ||
            !Canonical(request.OutputDirectory) || !Canonical(request.FrozenFileName) || request.OutputDirectory != request.OutputDirectory.Trim() ||
            request.FrozenFileName is "." or ".." || request.FrozenFileName.Contains('/') || request.FrozenFileName.Contains('\\') ||
            request.ScheduledStartUtc.Offset != TimeSpan.Zero || request.LatestStartUtc.Offset != TimeSpan.Zero || request.PlannedEndUtc.Offset != TimeSpan.Zero ||
            request.RequestedAtUtc.Offset != TimeSpan.Zero || request.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            request.ScheduledStartUtc >= request.LatestStartUtc || request.LatestStartUtc > request.PlannedEndUtc ||
            request.Duration <= TimeSpan.Zero || request.Duration > TimeSpan.FromMinutes(10) || request.Duration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            request.PlannedEndUtc - request.ScheduledStartUtc < request.Duration || request.ExpiresAtUtc <= request.RequestedAtUtc ||
            request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl) ||
            request.OutputDirectory.Any(char.IsControl) || request.FrozenFileName.Any(char.IsControl))
            throw InvalidArgument("The required one-time plan setup request is invalid.");
    }

    private static void ValidateSelection(RequiredOncePlanRegionSelection selection, RequiredOncePlanSetupRequestSnapshot request)
    {
        ValidateSelectionShape(selection);
        if (selection.SelectedAtUtc < request.RequestedAtUtc || selection.SelectedAtUtc >= request.ExpiresAtUtc || selection.SelectedAtUtc >= request.LatestStartUtc)
            throw InvalidArgument("The local region selection time falls outside the required setup window.");
    }

    private static void ValidateSelectionShape(RequiredOncePlanRegionSelection selection)
    {
        if (!Canonical(selection.StableDisplayFingerprint) || !Canonical(selection.TopologyDigest) ||
            selection.TopologyDigest.Length != 64 || selection.TopologyDigest.Any(character => !Uri.IsHexDigit(character)) ||
            selection.DisplayBounds.Width <= 0 || selection.DisplayBounds.Height <= 0 ||
            selection.RegionWithinDisplay.X < 0 || selection.RegionWithinDisplay.Y < 0 ||
            selection.RegionWithinDisplay.Width <= 0 || selection.RegionWithinDisplay.Height <= 0 ||
            (long)selection.RegionWithinDisplay.X + selection.RegionWithinDisplay.Width > selection.DisplayBounds.Width ||
            (long)selection.RegionWithinDisplay.Y + selection.RegionWithinDisplay.Height > selection.DisplayBounds.Height ||
            selection.DpiX <= 0 || selection.DpiY <= 0 || selection.PhysicalWidth != selection.DisplayBounds.Width ||
            selection.PhysicalHeight != selection.DisplayBounds.Height || !Enum.IsDefined(selection.Orientation) || selection.SelectedAtUtc.Offset != TimeSpan.Zero)
            throw InvalidArgument("The required fixed-region selection snapshot is invalid.");
    }

    private static string RequestDigest(RequiredOncePlanSetupRequestSnapshot request)
    {
        var canonical = string.Join("\n", new[]
        {
            "required-once-v1", request.ScheduledStartUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.LatestStartUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.PlannedEndUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DurationMillisecondsInput(request.Duration).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.OutputDirectory, request.FrozenFileName,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string OrientationCode(AuthorizedDisplayOrientation orientation) => orientation switch
    {
        AuthorizedDisplayOrientation.Landscape => "landscape",
        AuthorizedDisplayOrientation.Portrait => "portrait",
        AuthorizedDisplayOrientation.LandscapeFlipped => "landscape_flipped",
        AuthorizedDisplayOrientation.PortraitFlipped => "portrait_flipped",
        _ => throw InvalidArgument("The selected display orientation is unsupported."),
    };

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw InvalidArgument("The required setup timestamp must be UTC.");
    }

    private static bool Canonical(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);

    private static void TryRollback(SqliteTransaction? transaction)
    {
        try { transaction?.Rollback(); } catch { }
    }
}
