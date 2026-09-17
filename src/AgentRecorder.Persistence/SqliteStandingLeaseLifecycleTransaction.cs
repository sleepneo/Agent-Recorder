using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum StandingLeaseLifecycleTerminationKind
{
    NaturalExit,
    UserStop,
    BackendStartFailure,
    LifecyclePersistenceFailure,
    SafetyStop,
    SessionInterrupted,
}

/// <summary>
/// The post-start lifecycle write boundary. Every operation reloads all
/// aggregates from one immediate SQLite transaction, applies only Core domain
/// transitions, and writes the changed snapshots with their loaded versions.
/// </summary>
internal sealed class SqliteStandingLeaseLifecycleTransaction : SqliteRepositoryBase
{
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    private readonly string _runId;
    private readonly string _leaseId;
    private readonly string _leaseUseId;
    private readonly string _authorizedOutputPath;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingLeaseLifecycleTransaction(
        SqliteOperationalStore store,
        StandingLeaseCaptureSpecification specification,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        ArgumentNullException.ThrowIfNull(specification);
        _runId = specification.RunId;
        _leaseId = specification.LeaseId;
        _leaseUseId = specification.LeaseUseId;
        _authorizedOutputPath = specification.OutputFilePath;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingLeaseLifecycleActionResult ObserveFirstFrame(
        FirstFrameObservation? observation,
        DateTimeOffset observedAtUtc)
    {
        if (observation is null ||
            string.IsNullOrWhiteSpace(observation.EvidenceKind) ||
            !string.Equals(observation.EvidenceKind, observation.EvidenceKind.Trim(), StringComparison.Ordinal) ||
            observation.FrameNumber < 0 ||
            observation.TotalSizeBytes <= 0)
        {
            return StandingLeaseLifecycleActionResult.Rejected("first_frame_evidence_invalid");
        }

        return Execute((connection, transaction) =>
        {
            var chain = LoadChain(connection, transaction);
            if (IsTerminalChain(chain))
            {
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "first_frame_after_terminal",
                    terminal: true);
            }

            if (chain.Run.Status == RecordingRunStatus.Recording &&
                chain.Use.Status == LeaseUseStatus.Consumed &&
                chain.Occurrence.Status == PlanOccurrenceStatus.RunCreated)
            {
                return StandingLeaseLifecycleActionResult.Idempotent("first_frame_already_observed");
            }

            if (chain.Run.Status != RecordingRunStatus.StartCommitted ||
                chain.Use.Status != LeaseUseStatus.StartCommitted ||
                chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
            {
                throw LifecycleConflict("first_frame_not_expected");
            }

            var runVersion = chain.Run.Version;
            var useVersion = chain.Use.Version;
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Recording, observedAtUtc));
            RequireTransition(chain.Use.TryTransition(LeaseUseStatus.Consumed, observedAtUtc));
            UpdateRun(connection, transaction, chain.Run, runVersion);
            UpdateUse(connection, transaction, chain.Use, useVersion);
            return StandingLeaseLifecycleActionResult.Applied("first_frame_observed");
        });
    }

    internal StandingLeaseLifecycleActionResult ObserveCaptureEnded(
        DateTimeOffset observedAtUtc)
    {
        return Execute((connection, transaction) =>
        {
            var chain = LoadChain(connection, transaction);
            if (IsTerminalChain(chain))
            {
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "capture_ended_after_terminal",
                    terminal: true);
            }

            if (chain.Run.Status == RecordingRunStatus.StartCommitted &&
                chain.Use.Status == LeaseUseStatus.StartCommitted)
            {
                return StandingLeaseLifecycleActionResult.Idempotent("capture_ended_before_first_frame");
            }

            if (chain.Run.Status == RecordingRunStatus.Finalizing &&
                chain.Use.Status == LeaseUseStatus.Consumed)
            {
                return StandingLeaseLifecycleActionResult.Idempotent("capture_already_finalizing");
            }

            if (chain.Run.Status != RecordingRunStatus.Recording ||
                chain.Use.Status != LeaseUseStatus.Consumed ||
                chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
            {
                throw LifecycleConflict("capture_ended_not_expected");
            }

            var runVersion = chain.Run.Version;
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Finalizing, observedAtUtc));
            UpdateRun(connection, transaction, chain.Run, runVersion);
            return StandingLeaseLifecycleActionResult.Applied("capture_ended");
        });
    }

    internal StandingLeaseLifecycleActionResult CompleteTermination(
        StandingLeaseLifecycleTerminationKind kind,
        int exitCode,
        OutputMeta? meta,
        DateTimeOffset terminatedAtUtc)
    {
        return Execute((connection, transaction) =>
        {
            var chain = LoadChain(connection, transaction);
            if (IsTerminalChain(chain))
            {
                return StandingLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    terminal: true);
            }

            if (chain.Run.Status == RecordingRunStatus.StartCommitted &&
                chain.Use.Status == LeaseUseStatus.StartCommitted)
            {
                var runVersion = chain.Run.Version;
                var useVersion = chain.Use.Version;
                var occurrenceVersion = chain.Occurrence.Version;
                var reason = kind switch
                {
                    StandingLeaseLifecycleTerminationKind.UserStop => "user_stop_before_first_frame",
                    StandingLeaseLifecycleTerminationKind.SafetyStop => "safety_stop_before_first_frame",
                    StandingLeaseLifecycleTerminationKind.SessionInterrupted => "session_interrupted_before_first_frame",
                    StandingLeaseLifecycleTerminationKind.LifecyclePersistenceFailure => "lifecycle_persistence_failure_before_first_frame",
                    StandingLeaseLifecycleTerminationKind.BackendStartFailure => "backend_start_failed_before_first_frame",
                    _ => "natural_exit_before_first_frame",
                };
                RequireTransition(chain.Run.TryTransition(
                    RecordingRunStatus.StartedUnknown,
                    terminatedAtUtc,
                    reason));
                RequireTransition(chain.Use.TryTransition(
                    LeaseUseStatus.StartedUnknown,
                    terminatedAtUtc));
                RequireTransition(chain.Occurrence.TryTransition(
                    PlanOccurrenceStatus.Blocked,
                    terminatedAtUtc,
                    reason));
                UpdateRun(connection, transaction, chain.Run, runVersion);
                UpdateUse(connection, transaction, chain.Use, useVersion);
                UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion);
                return StandingLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            if (chain.Run.Status is not (RecordingRunStatus.Recording or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady) ||
                chain.Use.Status != LeaseUseStatus.Consumed ||
                chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
            {
                throw LifecycleConflict("termination_not_expected");
            }

            if (kind is StandingLeaseLifecycleTerminationKind.SafetyStop or
                StandingLeaseLifecycleTerminationKind.SessionInterrupted)
            {
                var runVersion = chain.Run.Version;
                var useVersion = chain.Use.Version;
                var occurrenceVersion = chain.Occurrence.Version;
                var reason = kind == StandingLeaseLifecycleTerminationKind.SafetyStop
                    ? "safety_stop"
                    : "session_interrupted";
                RequireTransition(chain.Run.TryTransition(
                    RecordingRunStatus.SessionInterrupted,
                    terminatedAtUtc,
                    reason));
                RequireTransition(chain.Use.TryTransition(
                    LeaseUseStatus.StartedUnknown,
                    terminatedAtUtc));
                RequireTransition(chain.Occurrence.TryTransition(
                    PlanOccurrenceStatus.Blocked,
                    terminatedAtUtc,
                    reason));
                UpdateRun(connection, transaction, chain.Run, runVersion);
                UpdateUse(connection, transaction, chain.Use, useVersion);
                UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion);
                return StandingLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            // A backend-start or lifecycle-persistence failure is a business
            // failure even when Stop returns a clean exit code and apparently
            // valid media. This branch must precede media validation so a
            // first frame followed by a throwing Start can never settle as
            // completed.
            if (kind is StandingLeaseLifecycleTerminationKind.BackendStartFailure or
                StandingLeaseLifecycleTerminationKind.LifecyclePersistenceFailure)
            {
                var runVersion = chain.Run.Version;
                var useVersion = chain.Use.Version;
                var occurrenceVersion = chain.Occurrence.Version;
                var reason = kind == StandingLeaseLifecycleTerminationKind.BackendStartFailure
                    ? "backend_start_failed_after_first_frame"
                    : "lifecycle_persistence_failure_after_first_frame";
                if (chain.Run.Status is RecordingRunStatus.Recording or RecordingRunStatus.Finalizing)
                {
                    RequireTransition(chain.Run.TryTransition(
                        RecordingRunStatus.Failed,
                        terminatedAtUtc,
                        reason));
                }
                else
                {
                    throw LifecycleConflict("start_failure_after_media_ready");
                }

                RequireTransition(chain.Use.TryTransition(
                    LeaseUseStatus.StartedUnknown,
                    terminatedAtUtc));
                RequireTransition(chain.Occurrence.TryTransition(
                    PlanOccurrenceStatus.Blocked,
                    terminatedAtUtc,
                    reason));
                UpdateRun(connection, transaction, chain.Run, runVersion);
                UpdateUse(connection, transaction, chain.Use, useVersion);
                UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion);
                return StandingLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            var hasSuccessfulExit = exitCode == 0;
            var mediaFailure = "capture_output_invalid";
            if (!hasSuccessfulExit ||
                !TryReadValidDuration(meta, _authorizedOutputPath, chain.Use.ReservedDuration, out var actualDuration, out mediaFailure))
            {
                var runVersion = chain.Run.Version;
                var useVersion = chain.Use.Version;
                var occurrenceVersion = chain.Occurrence.Version;
                var runStatus = kind == StandingLeaseLifecycleTerminationKind.UserStop
                    ? RecordingRunStatus.SessionInterrupted
                    : RecordingRunStatus.Failed;
                var reason = kind == StandingLeaseLifecycleTerminationKind.LifecyclePersistenceFailure
                    ? "lifecycle_persistence_failure"
                    : kind == StandingLeaseLifecycleTerminationKind.BackendStartFailure
                    ? "backend_start_failed_after_first_frame"
                    : !hasSuccessfulExit
                    ? "capture_exit_nonzero"
                    : kind == StandingLeaseLifecycleTerminationKind.UserStop
                    ? "user_stop_output_invalid"
                    : mediaFailure;

                if (chain.Run.Status is RecordingRunStatus.Recording or RecordingRunStatus.Finalizing)
                {
                    RequireTransition(chain.Run.TryTransition(runStatus, terminatedAtUtc, reason));
                }
                else
                {
                    throw LifecycleConflict("media_ready_output_invalid");
                }

                RequireTransition(chain.Use.TryTransition(
                    LeaseUseStatus.StartedUnknown,
                    terminatedAtUtc));
                RequireTransition(chain.Occurrence.TryTransition(
                    PlanOccurrenceStatus.Blocked,
                    terminatedAtUtc,
                    reason));
                UpdateRun(connection, transaction, chain.Run, runVersion);
                UpdateUse(connection, transaction, chain.Use, useVersion);
                UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion);
                return StandingLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            var normalRunVersion = chain.Run.Version;
            var normalUseVersion = chain.Use.Version;
            var normalOccurrenceVersion = chain.Occurrence.Version;
            if (chain.Run.Status == RecordingRunStatus.Recording)
            {
                RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Finalizing, terminatedAtUtc));
            }

            if (chain.Run.Status == RecordingRunStatus.Finalizing)
            {
                RequireTransition(chain.Run.TryTransition(RecordingRunStatus.MediaReady, terminatedAtUtc));
            }

            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Settled, terminatedAtUtc));
            RequireTransition(chain.Use.TrySettle(actualDuration, terminatedAtUtc));
            RequireTransition(chain.Occurrence.TryTransition(
                PlanOccurrenceStatus.Completed,
                terminatedAtUtc));
            UpdateRun(connection, transaction, chain.Run, normalRunVersion);
            UpdateUse(connection, transaction, chain.Use, normalUseVersion);
            UpdateOccurrence(connection, transaction, chain.Occurrence, normalOccurrenceVersion);
            return StandingLeaseLifecycleActionResult.Applied("media_settled", terminal: true);
        });
    }

    private StandingLeaseLifecycleActionResult Execute(
        Func<SqliteConnection, SqliteTransaction, StandingLeaseLifecycleActionResult> operation)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var result = operation(connection, transaction);
            _beforeCommitForTest?.Invoke(connection, transaction);
            transaction.Commit();
            return result;
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
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException(
                "constraint_violation",
                "The standing lifecycle transaction violated a persisted constraint.",
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

    private LifecycleChain LoadChain(SqliteConnection connection, SqliteTransaction transaction)
    {
        var run = LoadRun(connection, transaction, _runId);
        var use = LoadUse(connection, transaction, _leaseUseId);
        var occurrence = LoadOccurrence(connection, transaction, run.OccurrenceId);
        var lease = LoadLease(connection, transaction, use.LeaseId);

        if (!string.Equals(use.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(use.LeaseId, _leaseId, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.Id, _leaseId, StringComparison.Ordinal))
        {
            throw LifecycleConflict("lifecycle_relation_mismatch");
        }

        return new LifecycleChain(occurrence, run, use);
    }

    private static bool IsTerminalChain(LifecycleChain chain)
    {
        if (chain.Run.Status == RecordingRunStatus.Settled &&
            chain.Use.Status == LeaseUseStatus.Settled &&
            chain.Occurrence.Status == PlanOccurrenceStatus.Completed)
        {
            return true;
        }

        return (chain.Run.Status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed) &&
            chain.Use.Status == LeaseUseStatus.StartedUnknown &&
            chain.Occurrence.Status == PlanOccurrenceStatus.Blocked;
    }

    private static bool TryReadValidDuration(
        OutputMeta? meta,
        string authorizedOutputPath,
        TimeSpan authorizedDuration,
        out TimeSpan actualDuration,
        out string failureReason)
    {
        actualDuration = default;
        failureReason = "capture_output_invalid";
        if (meta is null)
        {
            failureReason = "capture_output_missing";
            return false;
        }

        // FFmpeg Probe always supplies OutputPath/OutputFileExists, while a
        // backend that only reports in-memory terminal metadata may omit the
        // path. Require the strong file-exists bit when a path is supplied,
        // and always require positive output bytes.
        if (meta.SizeBytes <= 0 ||
            (!string.IsNullOrWhiteSpace(meta.OutputPath) && !meta.OutputFileExists))
        {
            failureReason = "capture_output_missing";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(meta.OutputPath))
        {
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(meta.OutputPath);
            }
            catch
            {
                failureReason = "capture_output_path_invalid";
                return false;
            }

            if (!string.Equals(normalizedPath, authorizedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "capture_output_path_mismatch";
                return false;
            }
        }

        if (!double.IsFinite(meta.DurationSeconds))
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        if (meta.DurationSeconds < 0)
        {
            failureReason = "capture_duration_negative";
            return false;
        }

        if (meta.DurationSeconds > authorizedDuration.TotalSeconds)
        {
            failureReason = "capture_duration_exceeds_authorization";
            return false;
        }

        var milliseconds = meta.DurationSeconds * 1000d;
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > long.MaxValue)
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        var roundedMilliseconds = Math.Round(milliseconds, MidpointRounding.ToEven);
        if (roundedMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        try
        {
            actualDuration = TimeSpan.FromTicks(checked((long)roundedMilliseconds * TimeSpan.TicksPerMillisecond));
        }
        catch (OverflowException)
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        if (actualDuration < TimeSpan.Zero || actualDuration > authorizedDuration)
        {
            failureReason = "capture_duration_exceeds_authorization";
            return false;
        }

        if (!IsAllowedGracefulStopReason(meta.StopReason))
        {
            failureReason = "capture_terminal_reason_invalid";
            return false;
        }

        if (meta.StopReason is not null &&
            !string.Equals(meta.StopReason, meta.StopReason.Trim(), StringComparison.Ordinal))
        {
            failureReason = "capture_terminal_reason_invalid";
            return false;
        }

        return true;
    }

    private static bool IsAllowedGracefulStopReason(string? reason) =>
        string.IsNullOrEmpty(reason) ||
        reason.Equals("natural", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("stopped", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("user_requested", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("duration_reached", StringComparison.OrdinalIgnoreCase);

    private static void RequireTransition(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
        {
            throw new Phase3PersistenceException(
                "lifecycle_transition_rejected",
                "The standing lifecycle domain transition was not accepted.",
                new Phase3DomainException(result.ReasonCode, "The standing lifecycle transition was not accepted."));
        }
    }

    private static PlanOccurrence LoadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw NotFound();
        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static RecordingRun LoadRun(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw NotFound();
        return ReadRecordingRunSnapshot(reader);
    }

    private static LeaseUse LoadUse(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw NotFound();
        return ReadLeaseUseSnapshot(reader);
    }

    private static ConsentLease LoadLease(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Select(connection, transaction, $"SELECT {LeaseColumns} FROM consent_leases WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw NotFound();
        return ReadConsentLeaseSnapshot(reader);
    }

    private static SqliteCommand Select(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            Add(command, name, value);
        return command;
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
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", occurrence.Id, expectedVersion);
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
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM recording_runs WHERE id = $id;", run.Id, expectedVersion);
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
            EnsureImmutableUpdateResult(connection, transaction, "SELECT version FROM lease_uses WHERE id = $id;", use.Id, expectedVersion);
        EnsureRowsAffected(affectedRows);
    }

    private static Phase3PersistenceException LifecycleConflict(string reason) =>
        new("lifecycle_conflict", reason);

    private sealed record LifecycleChain(
        PlanOccurrence Occurrence,
        RecordingRun Run,
        LeaseUse Use);
}
