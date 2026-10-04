using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record FutureWindowAuthorizationRow(
    string AuthorizationId,
    string IdempotencyKey,
    string RequestDigest,
    string CurrentUserSid,
    string SessionBinding,
    FutureWindowExecutableIdentity ExecutableIdentity,
    string? SystemAudioEndpointId,
    string? SystemAudioEndpointName,
    int MaximumDurationSeconds,
    int ValiditySeconds,
    string OutputDirectory,
    string OutputFileName,
    string StatusCode,
    string? ReasonCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? ApprovalId,
    string? RunId,
    string? WindowId,
    int? ProcessId,
    long? ProcessCreationFileTimeUtc,
    string? RunStatus,
    string? ProofId,
    string? ProofNonce,
    string? OutputPath,
    long? OutputSizeBytes,
    long? ActualDurationMs,
    DateTimeOffset UpdatedAtUtc,
    long Version)
{
    internal FutureWindowAuthorizationScope ToScope() => new(
        AuthorizationId, IdempotencyKey, RequestDigest, CurrentUserSid, SessionBinding,
        ExecutableIdentity, SystemAudioEndpointId, SystemAudioEndpointName,
        MaximumDurationSeconds, ValiditySeconds, OutputDirectory, OutputFileName,
        StatusCode, CreatedAtUtc, ApprovedAtUtc, ExpiresAtUtc, ApprovalId, RunId, Version);
}

internal enum FutureWindowCreateDisposition { Created, Existing, Conflict }

/// <summary>Transactional persistence for independently scoped one-shot grants.</summary>
internal sealed class SqliteFutureWindowAuthorizationRepository
{
    private readonly SqliteOperationalStore _store;

    internal SqliteFutureWindowAuthorizationRepository(SqliteOperationalStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    internal (FutureWindowCreateDisposition Disposition, FutureWindowAuthorizationRow? Row) CreateOrGet(
        FutureWindowAuthorizationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.MaximumDurationSeconds is < 1 or > 1800 ||
            scope.ValiditySeconds is < 1 or > 3600 ||
            scope.MaximumDurationSeconds > scope.ValiditySeconds)
            throw new ArgumentException("The complete approved run must fit within the authorization validity.", nameof(scope));
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var inserted = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO future_window_authorizations (
                    authorization_id, idempotency_key, request_digest, current_user_sid, session_binding,
                    executable_path, executable_file_identity, executable_sha256, signer_subject,
                    signer_certificate_sha256, system_audio_endpoint_id, system_audio_endpoint_name,
                    maximum_duration_seconds, validity_seconds, output_directory, output_file_name,
                    status_code, created_at_utc, updated_at_utc, version)
                VALUES ($id, $key, $digest, $sid, $session, $path, $file, $sha, $signer, $cert,
                    $endpoint, $endpoint_name, $duration, $validity, $directory, $filename,
                    'pending', $created, $created, 0)
                ON CONFLICT(current_user_sid, session_binding, idempotency_key) DO NOTHING;
                """;
            Add(command, "$id", scope.AuthorizationId);
            Add(command, "$key", scope.IdempotencyKey);
            Add(command, "$digest", scope.RequestDigest);
            Add(command, "$sid", scope.CurrentUserSid);
            Add(command, "$session", scope.SessionBinding);
            Add(command, "$path", scope.ExecutableIdentity.CanonicalPath);
            Add(command, "$file", scope.ExecutableIdentity.FileIdentity);
            Add(command, "$sha", scope.ExecutableIdentity.Sha256);
            Add(command, "$signer", scope.ExecutableIdentity.SignerSubject);
            Add(command, "$cert", scope.ExecutableIdentity.SignerCertificateSha256);
            Add(command, "$endpoint", scope.SystemAudioEndpointId);
            Add(command, "$endpoint_name", scope.SystemAudioEndpointName);
            Add(command, "$duration", scope.MaximumDurationSeconds);
            Add(command, "$validity", scope.ValiditySeconds);
            Add(command, "$directory", scope.OutputDirectory);
            Add(command, "$filename", scope.OutputFileName);
            Add(command, "$created", ToDb(scope.CreatedAtUtc));
            inserted = command.ExecuteNonQuery() == 1;
        }

        var row = ReadByIdempotency(connection, transaction, scope.CurrentUserSid,
            scope.SessionBinding, scope.IdempotencyKey);
        transaction.Commit();
        if (row is null)
            return (FutureWindowCreateDisposition.Conflict, null);
        return string.Equals(row.RequestDigest, scope.RequestDigest, StringComparison.Ordinal)
            ? (inserted ? FutureWindowCreateDisposition.Created : FutureWindowCreateDisposition.Existing, row)
            : (FutureWindowCreateDisposition.Conflict, row);
    }

    internal FutureWindowAuthorizationRow? Get(string authorizationId, string userSid, string sessionBinding)
    {
        using var connection = _store.OpenConnection();
        return ReadById(connection, null, authorizationId, userSid, sessionBinding);
    }

    internal IReadOnlyList<FutureWindowAuthorizationRow> List(string userSid, string sessionBinding)
    {
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM future_window_authorizations
            WHERE current_user_sid = $sid AND session_binding = $session
            ORDER BY created_at_utc DESC, authorization_id;
            """;
        Add(command, "$sid", userSid);
        Add(command, "$session", sessionBinding);
        using var reader = command.ExecuteReader();
        var rows = new List<FutureWindowAuthorizationRow>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    internal FutureWindowAuthorizationRow? Approve(
        string authorizationId, string userSid, string sessionBinding,
        string approvalId, DateTimeOffset approvedAtUtc)
    {
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE future_window_authorizations
            SET status_code = 'active', reason_code = NULL, approval_id = $approval,
                approved_at_utc = $approved, expires_at_utc = $expires,
                updated_at_utc = $approved, version = version + 1
            WHERE authorization_id = $id AND current_user_sid = $sid AND session_binding = $session
              AND status_code = 'pending' AND $approved >= created_at_utc
              AND maximum_duration_seconds <= validity_seconds;
            """;
        Add(command, "$approval", approvalId);
        Add(command, "$approved", ToDb(approvedAtUtc));
        Add(command, "$expires", ToDb(approvedAtUtc.AddSeconds(ReadValidity(connection, transaction, authorizationId))));
        Add(command, "$id", authorizationId);
        Add(command, "$sid", userSid);
        Add(command, "$session", sessionBinding);
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        return changed == 1 ? Get(authorizationId, userSid, sessionBinding) : null;
    }

    internal bool SetTerminalWithoutRun(
        string authorizationId, string userSid, string sessionBinding,
        string status, string reason, DateTimeOffset nowUtc)
    {
        if (status is not ("blocked" or "failed" or "expired"))
            throw new ArgumentOutOfRangeException(nameof(status));
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE future_window_authorizations
            SET status_code = $status, reason_code = $reason, updated_at_utc = $now, version = version + 1
            WHERE authorization_id = $id AND current_user_sid = $sid AND session_binding = $session
              AND status_code IN ('pending', 'active');
            """;
        Add(command, "$status", status); Add(command, "$reason", reason); Add(command, "$now", ToDb(nowUtc));
        Add(command, "$id", authorizationId); Add(command, "$sid", userSid); Add(command, "$session", sessionBinding);
        return command.ExecuteNonQuery() == 1;
    }

    internal FutureWindowStartCommitReceipt? TryCommitStart(
        string authorizationId, string userSid, string sessionBinding,
        FutureWindowProcessSnapshot process, DateTimeOffset nowUtc, out string reasonCode)
    {
        reasonCode = "future_window_not_active";
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var before = ReadById(connection, transaction, authorizationId, userSid, sessionBinding);
        if (before is null) { transaction.Rollback(); reasonCode = "future_window_not_found"; return null; }
        if (before.StatusCode != "active") { transaction.Rollback(); reasonCode = "future_window_not_active"; return null; }
        var globalSafety = ReadGlobalSafety(connection, transaction);
        if (!string.Equals(globalSafety.UnattendedMode, "enabled", StringComparison.Ordinal))
        { transaction.Rollback(); reasonCode = "unattended_disabled"; return null; }
        if (globalSafety.StopAllAppliedAtUtc is { } stopAllAt &&
            before.ApprovedAtUtc is { } approvedAt && stopAllAt >= approvedAt)
        { transaction.Rollback(); reasonCode = "future_window_stop_all_applied"; return null; }
        if (before.ExpiresAtUtc is null || nowUtc >= before.ExpiresAtUtc.Value)
        {
            using var expire = connection.CreateCommand();
            expire.Transaction = transaction;
            expire.CommandText = "UPDATE future_window_authorizations SET status_code='expired', reason_code='authorization_expired', updated_at_utc=$now, version=version+1 WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session AND status_code='active';";
            Add(expire, "$now", ToDb(nowUtc)); Add(expire, "$id", authorizationId);
            Add(expire, "$sid", userSid); Add(expire, "$session", sessionBinding); expire.ExecuteNonQuery();
            transaction.Commit(); reasonCode = "future_window_expired"; return null;
        }
        if (before.ApprovedAtUtc is not { } approvedAtForRun || nowUtc < approvedAtForRun)
        { transaction.Rollback(); reasonCode = "future_window_clock_rollback_detected"; return null; }
        if (nowUtc.AddSeconds(before.MaximumDurationSeconds) > before.ExpiresAtUtc.Value)
        {
            using var expire = connection.CreateCommand();
            expire.Transaction = transaction;
            expire.CommandText = "UPDATE future_window_authorizations SET status_code='expired', reason_code='authorization_start_window_expired', updated_at_utc=$now, version=version+1 WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session AND status_code='active';";
            Add(expire, "$now", ToDb(nowUtc)); Add(expire, "$id", authorizationId);
            Add(expire, "$sid", userSid); Add(expire, "$session", sessionBinding); expire.ExecuteNonQuery();
            transaction.Commit(); reasonCode = "future_window_run_would_exceed_expiry"; return null;
        }
        if (!string.Equals(process.ProcessUserSid, userSid, StringComparison.Ordinal) ||
            process.ProcessCreationFileTimeUtc <= before.ApprovedAtUtc?.UtcDateTime.ToFileTimeUtc() ||
            !SameExecutable(process.ExecutableIdentity, before.ExecutableIdentity))
        { transaction.Rollback(); reasonCode = "future_window_process_identity_mismatch"; return null; }

        var runId = "rec_" + Guid.NewGuid().ToString("N")[..12];
        var proofId = "auth_future_" + Guid.NewGuid().ToString("N");
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE future_window_authorizations
            SET status_code='used', run_id=$run, window_id=$window, process_id=$pid,
                process_creation_filetime_utc=$creation, run_status='start_committed',
                proof_id=$proof, proof_nonce=$nonce, updated_at_utc=$now, version=version+1
            WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session
              AND status_code='active' AND expires_at_utc > $now;
            """;
        Add(update, "$run", runId); Add(update, "$window", process.WindowId); Add(update, "$pid", process.ProcessId);
        Add(update, "$creation", process.ProcessCreationFileTimeUtc); Add(update, "$proof", proofId);
        Add(update, "$nonce", nonce); Add(update, "$now", ToDb(nowUtc)); Add(update, "$id", authorizationId);
        Add(update, "$sid", userSid); Add(update, "$session", sessionBinding);
        if (update.ExecuteNonQuery() != 1)
        { transaction.Rollback(); reasonCode = "future_window_start_race_lost"; return null; }
        var after = ReadById(connection, transaction, authorizationId, userSid, sessionBinding);
        transaction.Commit();
        if (after is null) { reasonCode = "future_window_commit_readback_failed"; return null; }
        reasonCode = string.Empty;
        return new FutureWindowStartCommitReceipt(after.ToScope(), process, runId, proofId, nonce, nowUtc);
    }

    internal string? ValidateAtBackendStart(FutureWindowOneShotExecutionTicket ticket, DateTimeOffset nowUtc)
    {
        var expected = ticket.Authorization;
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var row = ReadById(connection, transaction, expected.AuthorizationId, expected.CurrentUserSid, expected.SessionBinding);
        if (row is null) return "future_window_authorization_missing";
        var globalSafety = ReadGlobalSafety(connection, transaction);
        if (!string.Equals(globalSafety.UnattendedMode, "enabled", StringComparison.Ordinal))
            return "unattended_disabled";
        if (globalSafety.StopAllAppliedAtUtc is { } stopAllAt &&
            row.ApprovedAtUtc is { } approvedAt && stopAllAt >= approvedAt)
            return "future_window_stop_all_applied";
        if (row.StatusCode != "used" || row.RunStatus != "start_committed" ||
            !string.Equals(row.RunId, ticket.RunId, StringComparison.Ordinal) ||
            !string.Equals(row.ProofId, ticket.Receipt.ProofId, StringComparison.Ordinal) ||
            !string.Equals(row.ProofNonce, ticket.Receipt.ProofNonce, StringComparison.Ordinal) ||
            !string.Equals(row.WindowId, ticket.Process.WindowId, StringComparison.Ordinal) ||
            row.ProcessId != ticket.Process.ProcessId ||
            row.ProcessCreationFileTimeUtc != ticket.Process.ProcessCreationFileTimeUtc ||
            !SameExecutable(row.ExecutableIdentity, expected.ExecutableIdentity) ||
            !string.Equals(row.RequestDigest, expected.RequestDigest, StringComparison.Ordinal) ||
            row.ApprovedAtUtc is null || row.ExpiresAtUtc is null || nowUtc.Offset != TimeSpan.Zero ||
            nowUtc < ticket.CommittedAtUtc || nowUtc >= row.ExpiresAtUtc.Value ||
            row.CurrentUserSid != expected.CurrentUserSid || row.SessionBinding != expected.SessionBinding)
            return "future_window_authorization_revoked_or_changed";
        if (row.MaximumDurationSeconds is < 1 or > 1800 || row.ValiditySeconds is < 1 or > 3600 ||
            row.MaximumDurationSeconds > row.ValiditySeconds)
            return "future_window_authorization_shape_invalid";
        if (nowUtc.AddSeconds(row.MaximumDurationSeconds) > row.ExpiresAtUtc.Value)
            return "future_window_run_would_exceed_expiry";
        transaction.Commit();
        return null;
    }

    internal FutureWindowAuthorizationRow? Revoke(
        string authorizationId, string userSid, string sessionBinding, DateTimeOffset nowUtc)
    {
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE future_window_authorizations
            SET status_code='revoked', reason_code='locally_revoked', updated_at_utc=$now, version=version+1
            WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session
              AND status_code IN ('pending','active','used');
            """;
        Add(command, "$now", ToDb(nowUtc)); Add(command, "$id", authorizationId);
        Add(command, "$sid", userSid); Add(command, "$session", sessionBinding);
        command.ExecuteNonQuery();
        var row = ReadById(connection, transaction, authorizationId, userSid, sessionBinding);
        transaction.Commit();
        return row;
    }

    internal void RecoverUncertainStarts(DateTimeOffset nowUtc)
    {
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE future_window_authorizations
            SET run_status='started_unknown', reason_code='process_restarted_during_or_after_start',
                updated_at_utc=MAX(updated_at_utc,$now), version=version+1
            WHERE status_code='used' AND run_status='start_committed';
            """;
        Add(command, "$now", ToDb(nowUtc));
        command.ExecuteNonQuery();
    }

    internal void CompleteRun(
        string authorizationId, string runId, bool success, string reasonCode,
        string? outputPath, long? outputSizeBytes, long? actualDurationMs, DateTimeOffset nowUtc)
    {
        var terminalStatus = success ? "completed" : "failed";
        if (success && (string.IsNullOrWhiteSpace(outputPath) || outputSizeBytes is null or <= 0 || actualDurationMs is null or < 0))
            throw new ArgumentException("Successful future-window completion requires verified output evidence.");
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE future_window_authorizations
            SET status_code=$status, reason_code=$reason, run_status=$run_status,
                output_path=$output, output_size_bytes=$size, actual_duration_ms=$duration,
                updated_at_utc=MAX(updated_at_utc,$now), version=version+1
            WHERE authorization_id=$id AND run_id=$run AND status_code='used';
            """;
        Add(command, "$status", terminalStatus); Add(command, "$reason", success ? null : reasonCode);
        Add(command, "$run_status", success ? "completed" : reasonCode == "authorization_revoked" ? "stopped" : "failed");
        Add(command, "$output", success ? outputPath : null); Add(command, "$size", success ? outputSizeBytes : null);
        Add(command, "$duration", success ? actualDurationMs : null); Add(command, "$now", ToDb(nowUtc));
        Add(command, "$id", authorizationId); Add(command, "$run", runId);
        command.ExecuteNonQuery();
    }

    internal void MarkRevokedRunStopped(string authorizationId, string runId, DateTimeOffset nowUtc)
    {
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE future_window_authorizations
            SET run_status='stopped', updated_at_utc=MAX(updated_at_utc,$now), version=version+1
            WHERE authorization_id=$id AND run_id=$run AND status_code='revoked'
              AND run_status IN ('start_committed','started_unknown','recording','finalizing');
            """;
        Add(command, "$now", ToDb(nowUtc)); Add(command, "$id", authorizationId); Add(command, "$run", runId);
        command.ExecuteNonQuery();
    }

    private static bool SameExecutable(FutureWindowExecutableIdentity left, FutureWindowExecutableIdentity right) =>
        left.Version == right.Version &&
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal) &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal) &&
        string.Equals(left.SignerSubject, right.SignerSubject, StringComparison.Ordinal) &&
        string.Equals(left.SignerCertificateSha256, right.SignerCertificateSha256, StringComparison.Ordinal);

    private static FutureWindowAuthorizationRow? ReadByIdempotency(
        SqliteConnection connection, SqliteTransaction transaction, string sid, string session, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT * FROM future_window_authorizations WHERE current_user_sid=$sid AND session_binding=$session AND idempotency_key=$key LIMIT 1;";
        Add(command, "$sid", sid); Add(command, "$session", session); Add(command, "$key", key);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadRow(reader) : null;
    }

    private static FutureWindowAuthorizationRow? ReadById(
        SqliteConnection connection, SqliteTransaction? transaction, string id, string sid, string session)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT * FROM future_window_authorizations WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session LIMIT 1;";
        Add(command, "$id", id); Add(command, "$sid", sid); Add(command, "$session", session);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadRow(reader) : null;
    }

    private static int ReadValidity(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT validity_seconds FROM future_window_authorizations WHERE authorization_id=$id AND status_code='pending';";
        Add(command, "$id", id); return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string UnattendedMode, DateTimeOffset? StopAllAppliedAtUtc) ReadGlobalSafety(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT unattended_mode_code, stop_all_applied_at_utc FROM unattended_safety_state WHERE state_id = 'global';";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new PersistedSnapshotException("The global unattended safety state is missing.");
        var mode = reader.GetString(0);
        DateTimeOffset? stopAllAt = reader.IsDBNull(1)
            ? null
            : new DateTimeOffset(new DateTime(reader.GetInt64(1), DateTimeKind.Utc));
        return (mode, stopAllAt);
    }

    private static FutureWindowAuthorizationRow ReadRow(SqliteDataReader reader)
    {
        string? NullableString(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
        long? NullableLong(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
        int? NullableInt(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
        DateTimeOffset? NullableDate(string name) => NullableLong(name) is { } value ? FromDb(value) : null;
        var identity = new FutureWindowExecutableIdentity(
            1, reader.GetString(reader.GetOrdinal("executable_path")),
            reader.GetString(reader.GetOrdinal("executable_file_identity")),
            reader.GetString(reader.GetOrdinal("executable_sha256")),
            NullableString("signer_subject"), NullableString("signer_certificate_sha256"));
        return new FutureWindowAuthorizationRow(
            reader.GetString(reader.GetOrdinal("authorization_id")), reader.GetString(reader.GetOrdinal("idempotency_key")),
            reader.GetString(reader.GetOrdinal("request_digest")), reader.GetString(reader.GetOrdinal("current_user_sid")),
            reader.GetString(reader.GetOrdinal("session_binding")), identity,
            NullableString("system_audio_endpoint_id"), NullableString("system_audio_endpoint_name"),
            reader.GetInt32(reader.GetOrdinal("maximum_duration_seconds")), reader.GetInt32(reader.GetOrdinal("validity_seconds")),
            reader.GetString(reader.GetOrdinal("output_directory")), reader.GetString(reader.GetOrdinal("output_file_name")),
            reader.GetString(reader.GetOrdinal("status_code")), NullableString("reason_code"),
            FromDb(reader.GetInt64(reader.GetOrdinal("created_at_utc"))), NullableDate("approved_at_utc"), NullableDate("expires_at_utc"),
            NullableString("approval_id"), NullableString("run_id"), NullableString("window_id"), NullableInt("process_id"),
            NullableLong("process_creation_filetime_utc"), NullableString("run_status"), NullableString("proof_id"),
            NullableString("proof_nonce"), NullableString("output_path"), NullableLong("output_size_bytes"),
            NullableLong("actual_duration_ms"), FromDb(reader.GetInt64(reader.GetOrdinal("updated_at_utc"))),
            reader.GetInt64(reader.GetOrdinal("version")));
    }

    private static long ToDb(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
    private static DateTimeOffset FromDb(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
