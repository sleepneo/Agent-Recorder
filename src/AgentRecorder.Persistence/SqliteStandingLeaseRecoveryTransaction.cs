using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// One immediate SQLite transaction for reconciling the durable tail of a
/// standing execution after process restart. This type deliberately has no
/// reference to ICaptureBackend or any execution/proof object.
/// </summary>
internal sealed class SqliteStandingLeaseRecoveryTransaction : SqliteRepositoryBase
{
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    private static readonly HashSet<string> KnownAbnormalReasons = new(StringComparer.Ordinal)
    {
        "natural_exit_before_first_frame",
        "user_stop_before_first_frame",
        "recovery_after_start_commit",
        "recovery_after_recording_interrupted",
        "recovery_during_finalization",
        "recovery_before_settlement",
        "backend_start_failed_after_first_frame",
        "capture_exit_nonzero",
        "capture_output_invalid",
        "capture_output_missing",
        "capture_output_path_invalid",
        "capture_output_path_mismatch",
        "capture_duration_invalid",
        "capture_duration_negative",
        "capture_duration_exceeds_authorization",
        "capture_terminal_reason_invalid",
        "user_stop_output_invalid",
        "lifecycle_persistence_failure_before_first_frame",
        "lifecycle_persistence_failure",
    };

    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingLeaseRecoveryTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingLeaseRecoveryResult Reconcile(
        StandingLeaseRecoveryRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseRecoveryResult.Rejected("recovery_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var chain = LoadChain(connection, transaction, request);
            ValidateTrustedRecoveryTime(chain, nowUtc);

            var result = ReconcileChain(chain, request, nowUtc);
            if (result.Changed)
            {
                _beforeCommitForTest?.Invoke(connection, transaction);
            }

            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw RecoverySnapshotInvalid(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw RecoverySnapshotInvalid(exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException(
                "recovery_snapshot_invalid",
                "The standing recovery transaction violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(
                "recovery_sqlite_failure",
                "The standing recovery transaction failed in SQLite.",
                exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(
                "recovery_sqlite_failure",
                "The standing recovery transaction failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static StandingLeaseRecoveryResult ReconcileChain(
        RecoveryChain chain,
        StandingLeaseRecoveryRequest request,
        DateTimeOffset nowUtc)
    {
        ValidatePersistedEvidence(chain);

        if (IsSettledTerminal(chain))
        {
            ValidateSettledTerminal(chain);
            return StandingLeaseRecoveryResult.AlreadyReconciled(request);
        }

        if (IsUnknownTerminal(chain))
        {
            ValidateUnknownTerminal(chain);
            return StandingLeaseRecoveryResult.AlreadyReconciled(request);
        }

        RecordingRunStatus targetRunStatus;
        string reason;
        switch (chain.Run.Status, chain.Use.Status, chain.Occurrence.Status)
        {
            case (RecordingRunStatus.StartCommitted, LeaseUseStatus.StartCommitted, PlanOccurrenceStatus.RunCreated):
                targetRunStatus = RecordingRunStatus.StartedUnknown;
                reason = "recovery_after_start_commit";
                break;
            case (RecordingRunStatus.Recording, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated):
                targetRunStatus = RecordingRunStatus.SessionInterrupted;
                reason = "recovery_after_recording_interrupted";
                break;
            case (RecordingRunStatus.Finalizing, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated):
                targetRunStatus = RecordingRunStatus.SessionInterrupted;
                reason = "recovery_during_finalization";
                break;
            case (RecordingRunStatus.MediaReady, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated):
                targetRunStatus = RecordingRunStatus.SessionInterrupted;
                reason = "recovery_before_settlement";
                break;
            default:
                throw RecoveryConflict("recovery_state_combination_invalid");
        }

        var runVersion = chain.Run.Version;
        var useVersion = chain.Use.Version;
        var occurrenceVersion = chain.Occurrence.Version;

        RequireTransition(chain.Run.TryTransition(targetRunStatus, nowUtc, reason));
        RequireTransition(chain.Use.TryTransition(LeaseUseStatus.StartedUnknown, nowUtc));
        RequireTransition(chain.Occurrence.TryTransition(
            PlanOccurrenceStatus.Blocked,
            nowUtc,
            reason));

        UpdateRun(chain.Connection, chain.Transaction, chain.Run, runVersion);
        UpdateUse(chain.Connection, chain.Transaction, chain.Use, useVersion);
        UpdateOccurrence(chain.Connection, chain.Transaction, chain.Occurrence, occurrenceVersion);
        return StandingLeaseRecoveryResult.Recovered(request, reason);
    }

    private static RecoveryChain LoadChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandingLeaseRecoveryRequest request)
    {
        var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(connection, transaction, request.ScopeId)
            ?? throw RecoverySnapshotInvalid(new PersistedSnapshotException("The requested recovery scope was not found."));

        if (!string.Equals(scope.ScopeId, request.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(scope.PlanId, request.PlanId, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, request.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(scope.ScopeDigest, request.ScopeDigest, StringComparison.Ordinal))
        {
            throw RecoveryConflict("recovery_scope_identity_mismatch");
        }

        var related = SqliteAuthorizedCaptureScopeRepository.LoadAndValidateRelatedForStartGate(
            connection,
            transaction,
            scope);
        var occurrence = LoadOccurrence(connection, transaction, request.OccurrenceId);
        var run = LoadRun(connection, transaction, request.RunId);
        var use = LoadUse(connection, transaction, request.LeaseUseId);
        var occurrenceRuns = LoadRunsByOccurrence(connection, transaction, request.OccurrenceId);
        var occurrenceUses = LoadUsesByOccurrence(connection, transaction, request.OccurrenceId);

        if (occurrenceRuns.Count != 1 || occurrenceUses.Count != 1 ||
            !string.Equals(occurrenceRuns[0].Id, run.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrenceUses[0].Id, use.Id, StringComparison.Ordinal))
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The recovery occurrence does not have exactly one durable run and lease use."));
        }

        if (!string.Equals(related.Plan.Id, request.PlanId, StringComparison.Ordinal) ||
            !string.Equals(related.Occurrence.Id, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(related.Lease.Id, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(occurrence.PlanId, related.Plan.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.LeaseId, related.Lease.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal))
        {
            throw RecoveryConflict("recovery_relation_mismatch");
        }

        return new RecoveryChain(connection, transaction, related.Plan, occurrence, related.Lease, scope, run, use);
    }

    private static void ValidatePersistedEvidence(RecoveryChain chain)
    {
        if (chain.Plan.Version == long.MaxValue ||
            chain.Occurrence.Version == long.MaxValue ||
            chain.Lease.Version == long.MaxValue ||
            chain.Run.Version == long.MaxValue ||
            chain.Use.Version == long.MaxValue ||
            chain.Lease.Status != ConsentLeaseStatus.Exhausted ||
            !chain.Run.HasCrossedStartCommit ||
            !chain.Run.IsNonRetryable ||
            chain.Use.ReservedUseCount != 1 ||
            chain.Use.ReservedDuration != chain.Scope.ReservedDuration ||
            chain.Use.ReservedDuration <= TimeSpan.Zero ||
            chain.Run.CreatedAtUtc != chain.Use.CreatedAtUtc ||
            chain.Run.CreatedAtUtc < chain.Scope.CreatedAtUtc ||
            chain.Run.CreatedAtUtc < chain.Occurrence.WindowStartUtc ||
            chain.Run.CreatedAtUtc >= chain.Occurrence.WindowEndUtc ||
            chain.Run.CreatedAtUtc < chain.Lease.ValidFromUtc ||
            chain.Run.CreatedAtUtc >= chain.Lease.ValidUntilUtc ||
            chain.Scope.ReservedDuration > chain.Lease.MaxDuration)
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The persisted recovery evidence does not prove a committed standing start."));
        }

        try
        {
            _ = DurationMillisecondsInput(chain.Use.ReservedDuration);
            _ = checked(chain.Run.CreatedAtUtc.UtcDateTime.Ticks + chain.Use.ReservedDuration.Ticks);
        }
        catch (Exception exception) when (exception is Phase3PersistenceException or OverflowException)
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The persisted recovery duration is invalid.", exception));
        }

        var endTicks = checked(chain.Run.CreatedAtUtc.UtcDateTime.Ticks + chain.Use.ReservedDuration.Ticks);
        if (endTicks > chain.Occurrence.WindowEndUtc.UtcDateTime.Ticks ||
            endTicks > chain.Lease.ValidUntilUtc.UtcDateTime.Ticks)
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The persisted recovery duration exceeds its authorization window."));
        }
    }

    private static void ValidateTrustedRecoveryTime(RecoveryChain chain, DateTimeOffset nowUtc)
    {
        if (nowUtc < chain.Plan.UpdatedAtUtc ||
            nowUtc < chain.Occurrence.UpdatedAtUtc ||
            nowUtc < chain.Lease.UpdatedAtUtc ||
            nowUtc < chain.Run.UpdatedAtUtc ||
            nowUtc < chain.Use.UpdatedAtUtc)
        {
            throw new Phase3PersistenceException(
                "recovery_time_non_monotonic",
                "The trusted recovery clock precedes durable aggregate time.");
        }
    }

    private static bool IsSettledTerminal(RecoveryChain chain) =>
        chain.Run.Status == RecordingRunStatus.Settled &&
        chain.Use.Status == LeaseUseStatus.Settled &&
        chain.Occurrence.Status == PlanOccurrenceStatus.Completed;

    private static bool IsUnknownTerminal(RecoveryChain chain) =>
        (chain.Run.Status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed) &&
        chain.Use.Status == LeaseUseStatus.StartedUnknown &&
        chain.Occurrence.Status == PlanOccurrenceStatus.Blocked;

    private static void ValidateSettledTerminal(RecoveryChain chain)
    {
        if (chain.Run.TerminalReasonCode is not null ||
            chain.Occurrence.TerminalReasonCode is not null ||
            chain.Use.ActualSettledDuration is null ||
            chain.Use.ActualSettledDuration.Value > chain.Use.ReservedDuration)
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The settled recovery terminal contains an invalid reason or duration."));
        }
    }

    private static void ValidateUnknownTerminal(RecoveryChain chain)
    {
        var runReason = chain.Run.TerminalReasonCode;
        if (!IsKnownAbnormalReason(runReason) ||
            !string.Equals(runReason, chain.Occurrence.TerminalReasonCode, StringComparison.Ordinal) ||
            chain.Use.ActualSettledDuration is not null)
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The recovery terminal reason is missing, unknown, or inconsistent."));
        }
    }

    private static bool IsKnownAbnormalReason(string? reason) =>
        reason is not null &&
        string.Equals(reason, reason.Trim(), StringComparison.Ordinal) &&
        KnownAbnormalReasons.Contains(reason);

    private static void RequireTransition(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
        {
            throw RecoveryConflict(result.ReasonCode);
        }
    }

    private static void UpdateOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND run_id = $run_id AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc;";
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$terminal_reason_code", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", occurrence.Id);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", occurrence.PlanId);
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows == 0)
        {
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", occurrence.Id, expectedVersion);
        }

        EnsureRowsAffected(affectedRows);
    }

    private static void UpdateRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordingRun run,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recording_runs SET status_code = $status_code, has_crossed_start_commit = $has_crossed_start_commit, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND occurrence_id = $occurrence_id AND media_artifact_id IS $media_artifact_id AND bundle_id IS $bundle_id AND created_at_utc = $created_at_utc;";
        Add(command, "$status_code", run.StatusCode);
        Add(command, "$has_crossed_start_commit", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$terminal_reason_code", run.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$new_version", run.Version);
        Add(command, "$id", run.Id);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$occurrence_id", run.OccurrenceId);
        Add(command, "$media_artifact_id", run.MediaArtifactId);
        Add(command, "$bundle_id", run.BundleId);
        Add(command, "$created_at_utc", UtcTicksInput(run.CreatedAtUtc));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows == 0)
        {
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM recording_runs WHERE id = $id;", run.Id, expectedVersion);
        }

        EnsureRowsAffected(affectedRows);
    }

    private static void UpdateUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LeaseUse use,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE lease_uses SET status_code = $status_code, reserved_use_count = $reserved_use_count, reserved_duration_ms = $reserved_duration_ms, actual_settled_duration_ms = $actual_settled_duration_ms, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND lease_id = $lease_id AND occurrence_id = $occurrence_id AND run_id = $run_id AND created_at_utc = $created_at_utc;";
        Add(command, "$status_code", use.StatusCode);
        Add(command, "$reserved_use_count", use.ReservedUseCount);
        Add(command, "$reserved_duration_ms", DurationMillisecondsInput(use.ReservedDuration));
        Add(command, "$actual_settled_duration_ms", use.ActualSettledDuration is null ? null : DurationMillisecondsInput(use.ActualSettledDuration.Value));
        Add(command, "$updated_at_utc", UtcTicksInput(use.UpdatedAtUtc));
        Add(command, "$new_version", use.Version);
        Add(command, "$id", use.Id);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$lease_id", use.LeaseId);
        Add(command, "$occurrence_id", use.OccurrenceId);
        Add(command, "$run_id", use.RunId);
        Add(command, "$created_at_utc", UtcTicksInput(use.CreatedAtUtc));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows == 0)
        {
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM lease_uses WHERE id = $id;", use.Id, expectedVersion);
        }

        EnsureRowsAffected(affectedRows);
    }

    private static PlanOccurrence LoadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The recovery occurrence was not found."));
        }

        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static RecordingRun LoadRun(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The recovery run was not found."));
        }

        return ReadRecordingRunSnapshot(reader);
    }

    private static LeaseUse LoadUse(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw RecoverySnapshotInvalid(new PersistedSnapshotException("The recovery lease use was not found."));
        }

        return ReadLeaseUseSnapshot(reader);
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId)
    {
        using var command = Select(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;", ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
        {
            result.Add(ReadRecordingRunSnapshot(reader));
        }

        return result;
    }

    private static IReadOnlyList<LeaseUse> LoadUsesByOccurrence(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId)
    {
        using var command = Select(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE occurrence_id = $occurrence_id;", ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
        {
            result.Add(ReadLeaseUseSnapshot(reader));
        }

        return result;
    }

    private static SqliteCommand Select(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, string Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        return command;
    }

    private static Phase3PersistenceException RecoverySnapshotInvalid(Exception exception) =>
        new("recovery_snapshot_invalid", "The persisted standing recovery snapshot is invalid.", exception);

    private static Phase3PersistenceException RecoveryConflict(string reason) =>
        new("recovery_conflict", reason);

    private sealed record RecoveryChain(
        SqliteConnection Connection,
        SqliteTransaction Transaction,
        PlanDefinition Plan,
        PlanOccurrence Occurrence,
        ConsentLease Lease,
        AuthorizedFixedRegionScope Scope,
        RecordingRun Run,
        LeaseUse Use);
}
