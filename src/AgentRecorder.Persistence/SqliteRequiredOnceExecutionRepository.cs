using System.Security.Cryptography;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record RequiredOnceExecutionCandidate(
    RequiredOnceCaptureExecutionSpecification Specification,
    long OccurrenceVersion);

internal sealed record RequiredOnceStartCommitReceipt(
    string ExecutionId,
    string RunId,
    string ExecutionApprovalId,
    string ProofId,
    string ProofNonce,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset CommittedAtUtc,
    RequiredOnceCaptureExecutionSpecification Specification);

internal sealed record RequiredOnceExecutionWriteResult(bool Succeeded, string ReasonCode);
internal sealed record RequiredOnceExecutionRecoveryBatch(int RecoveredCount, bool HasMore);

/// <summary>
/// Atomic, one-time required execution persistence. This repository never
/// creates Lease/use rows and never returns a reusable authorization.
/// </summary>
internal sealed class SqliteRequiredOnceExecutionRepository : SqliteRepositoryBase
{
    internal const int MaximumCandidates = 32;
    private readonly Action<string>? _failureHook;

    internal SqliteRequiredOnceExecutionRepository(
        SqliteOperationalStore store,
        Action<string>? failureHookForTest = null) : base(store) => _failureHook = failureHookForTest;

    internal IReadOnlyList<RequiredOnceExecutionCandidate> ListDue(
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset nowUtc)
    {
        if (!Canonical(currentUserSid) || !Canonical(sessionBinding) || nowUtc.Offset != TimeSpan.Zero)
            return Array.Empty<RequiredOnceExecutionCandidate>();
        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var raw = new List<(string PlanId, string OccurrenceId, long OccurrenceVersion)>();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT p.id, o.id, o.version
                FROM required_once_setup_intents AS i
                JOIN plans AS p ON p.id = i.plan_id AND p.is_one_time = 1 AND p.status_code = 'enabled'
                JOIN plan_occurrences AS o ON o.plan_id = p.id AND o.id = i.occurrence_id
                WHERE i.current_user_sid = $sid AND i.session_binding = $session
                  AND i.status_code = 'scheduled' AND o.status_code = 'scheduled'
                  AND o.run_id IS NULL AND o.window_start_utc <= $now
                ORDER BY o.window_start_utc, o.id COLLATE BINARY LIMIT $limit;
                """;
            Add(command, "$sid", currentUserSid);
            Add(command, "$session", sessionBinding);
            Add(command, "$now", UtcTicksInput(nowUtc));
            Add(command, "$limit", MaximumCandidates);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    raw.Add((ReadRequiredText(reader, 0), ReadRequiredText(reader, 1), ReadInt64(reader, 2)));
            }

            var candidates = new List<RequiredOnceExecutionCandidate>(raw.Count);
            var corrupt = new List<(string PlanId, string OccurrenceId)>();
            foreach (var item in raw)
            {
                try
                {
                    var state = SqliteRequiredOncePlanSetupRepository.ReadAndValidateScheduledPlanWithinTransaction(
                        connection, transaction, item.PlanId);
                    if (state is null || state.OccurrenceId != item.OccurrenceId ||
                        state.Request.CurrentUserSid != currentUserSid || state.Request.SessionBinding != sessionBinding ||
                        state.Selection is null)
                        throw new PersistedSnapshotException("The required-once wake candidate changed ownership or shape.");
                    var creationApprovalId = ReadCreationApprovalId(connection, transaction, item.PlanId, item.OccurrenceId);
                    var selection = state.Selection;
                    var specification = new RequiredOnceCaptureExecutionSpecification(
                        state.PlanId!, state.OccurrenceId!, state.Request.SetupIntentId, creationApprovalId,
                        state.Request.CurrentUserSid, state.Request.SessionBinding,
                        state.Request.ScheduledStartUtc, state.Request.LatestStartUtc, state.Request.PlannedEndUtc,
                        state.Request.Duration, state.Request.OutputDirectory, state.Request.FrozenFileName,
                        selection.StableDisplayFingerprint, selection.DisplayBounds, selection.RegionWithinDisplay,
                        selection.DpiX, selection.DpiY, selection.PhysicalWidth, selection.PhysicalHeight,
                        selection.Orientation, selection.TopologyDigest);
                    candidates.Add(new(specification, item.OccurrenceVersion));
                }
                catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or
                    ArgumentException or InvalidCastException or FormatException or OverflowException)
                {
                    corrupt.Add((item.PlanId, item.OccurrenceId));
                }
            }
            transaction.Commit();
            foreach (var item in corrupt)
                MarkScheduledTerminal(item.PlanId, item.OccurrenceId, currentUserSid, sessionBinding,
                    PlanOccurrenceStatus.Blocked, "approved_specification_corrupt", nowUtc);
            return candidates;
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOnceExecutionWriteResult ClaimForConfirmation(
        RequiredOnceExecutionCandidate candidate,
        string executionId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Canonical(executionId) || nowUtc.Offset != TimeSpan.Zero)
            return new(false, "execution_claim_input_invalid");
        var specification = candidate.Specification;
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var current = SqliteRequiredOncePlanSetupRepository.ReadAndValidateScheduledPlanWithinTransaction(
                connection, transaction, specification.PlanId);
            if (current is null || current.StatusCode != "scheduled" ||
                current.OccurrenceId != specification.OccurrenceId ||
                current.Request.CurrentUserSid != specification.CurrentUserSid ||
                current.Request.SessionBinding != specification.SessionBinding ||
                current.Selection is null || nowUtc < specification.ScheduledStartUtc)
            {
                transaction.Commit();
                return new(false, "occurrence_not_claimable");
            }
            if (nowUtc >= specification.LatestStartUtc)
            {
                MarkScheduledTerminalWithinTransaction(connection, transaction, specification.PlanId,
                    specification.OccurrenceId, specification.CurrentUserSid, specification.SessionBinding,
                    PlanOccurrenceStatus.Missed, "latest_start_window_missed", nowUtc, candidate.OccurrenceVersion);
                transaction.Commit();
                return new(false, "latest_start_window_missed");
            }
            var persistedApproval = ReadCreationApprovalId(connection, transaction,
                specification.PlanId, specification.OccurrenceId);
            var refreshed = CreateSpecification(current, persistedApproval);
            if (!string.Equals(refreshed.SpecificationDigest, specification.SpecificationDigest, StringComparison.Ordinal))
            {
                MarkScheduledTerminalWithinTransaction(connection, transaction, specification.PlanId,
                    specification.OccurrenceId, specification.CurrentUserSid, specification.SessionBinding,
                    PlanOccurrenceStatus.Blocked, "approved_specification_changed", nowUtc, candidate.OccurrenceVersion);
                transaction.Commit();
                return new(false, "approved_specification_changed");
            }

            var occurrence = ReadOccurrence(connection, transaction, specification.OccurrenceId);
            if (occurrence.Status != PlanOccurrenceStatus.Scheduled || occurrence.Version != candidate.OccurrenceVersion ||
                occurrence.RunId is not null)
            {
                transaction.Commit();
                return new(false, "occurrence_claim_conflict");
            }
            RequireTransition(occurrence.TryTransition(PlanOccurrenceStatus.Due, nowUtc));
            RequireTransition(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, nowUtc));
            RequireTransition(occurrence.TryTransition(PlanOccurrenceStatus.PendingConfirmation, nowUtc));
            UpdateOccurrence(connection, transaction, occurrence, candidate.OccurrenceVersion, "scheduled");
            _failureHook?.Invoke("required_once_occurrence_claimed");
            InsertPendingExecution(connection, transaction, executionId, specification, nowUtc);
            _failureHook?.Invoke("required_once_pending_execution_inserted");
            transaction.Commit();
            return new(true, string.Empty);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                return new(false, "occurrence_claim_conflict");
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOnceStartCommitReceipt? CommitStart(
        string executionId,
        RequiredOnceCaptureExecutionSpecification specification,
        string executionApprovalId,
        string proofId,
        string proofNonce,
        DateTimeOffset approvedAtUtc,
        DateTimeOffset nowUtc,
        string currentUserSid,
        string sessionBinding,
        Func<string?>? environmentRecheck = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!Canonical(executionId) || !Canonical(executionApprovalId) || !Canonical(proofId) ||
            !IsNonce(proofNonce) || approvedAtUtc.Offset != TimeSpan.Zero || nowUtc.Offset != TimeSpan.Zero ||
            approvedAtUtc > nowUtc || nowUtc < specification.ScheduledStartUtc ||
            nowUtc >= specification.LatestStartUtc || currentUserSid != specification.CurrentUserSid ||
            sessionBinding != specification.SessionBinding)
            return null;
        using var connection = OpenBusinessConnection();
        using var transaction = BeginWriteTransaction(connection);
        try
        {
            var setup = SqliteRequiredOncePlanSetupRepository.ReadAndValidateExecutionProgressPlanWithinTransaction(
                connection, transaction, specification.PlanId, specification.OccurrenceId);
            if (setup is null || setup.StatusCode != "scheduled" || setup.Selection is null ||
                setup.Request.CurrentUserSid != currentUserSid || setup.Request.SessionBinding != sessionBinding ||
                !string.Equals(CreateSpecification(setup, specification.CreationApprovalId).SpecificationDigest,
                    specification.SpecificationDigest, StringComparison.Ordinal))
            {
                transaction.Commit();
                return null;
            }

            var occurrence = ReadOccurrence(connection, transaction, specification.OccurrenceId);
            var execution = ReadPendingExecution(connection, transaction, executionId);
            if (execution is null || execution.StatusCode != "pending_confirmation" ||
                occurrence.Status != PlanOccurrenceStatus.PendingConfirmation || occurrence.RunId is not null ||
                execution.PlanId != specification.PlanId || execution.OccurrenceId != specification.OccurrenceId ||
                execution.CurrentUserSid != currentUserSid || execution.SessionBinding != sessionBinding ||
                execution.SpecificationDigest != specification.SpecificationDigest ||
                execution.LatestStartUtc != specification.LatestStartUtc ||
                execution.ScheduledStartUtc != specification.ScheduledStartUtc)
            {
                transaction.Commit();
                return null;
            }

            if (environmentRecheck is not null)
            {
                string? environmentFailure;
                try { environmentFailure = environmentRecheck(); }
                catch { environmentFailure = "execution_environment_revalidation_failed"; }
                if (!string.IsNullOrWhiteSpace(environmentFailure))
                {
                    var failureCode = Canonical(environmentFailure) ? environmentFailure! : "execution_environment_revalidation_failed";
                    var terminalStatus = failureCode == "latest_start_window_missed" ? "expired" : "blocked";
                    SettlePendingExecutionWithinTransaction(connection, transaction, execution, occurrence,
                        terminalStatus, failureCode, nowUtc);
                    transaction.Commit();
                    return null;
                }
            }

            var runId = "required-once-run-" + Guid.NewGuid().ToString("N");
            RequireTransition(occurrence.TryCreateRun(runId, nowUtc));
            var run = RecordingRun.CreateFor(occurrence, runId, nowUtc);
            RequireTransition(run.TryTransition(RecordingRunStatus.Preparing, nowUtc));
            RequireTransition(run.TryTransition(RecordingRunStatus.StartCommitted, nowUtc));
            InsertRun(connection, transaction, run);
            UpdateOccurrence(connection, transaction, occurrence, occurrence.Version - 1, "pending_confirmation");
            _failureHook?.Invoke("required_once_run_committed");

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE required_once_execution_authorizations
                    SET status_code = 'start_committed', run_id = $run, execution_approval_id = $approval,
                        proof_id = $proof, proof_nonce = $nonce, approved_at_utc = $approved,
                        committed_at_utc = $now, updated_at_utc = $now, version = version + 1
                    WHERE execution_id = $id AND status_code = 'pending_confirmation'
                      AND plan_id = $plan AND occurrence_id = $occurrence
                      AND specification_digest = $digest AND current_user_sid = $sid
                      AND session_binding = $session AND latest_start_utc > $now;
                    """;
                Add(update, "$run", runId);
                Add(update, "$approval", executionApprovalId);
                Add(update, "$proof", proofId);
                Add(update, "$nonce", proofNonce);
                Add(update, "$approved", UtcTicksInput(approvedAtUtc));
                Add(update, "$now", UtcTicksInput(nowUtc));
                Add(update, "$id", executionId);
                Add(update, "$plan", specification.PlanId);
                Add(update, "$occurrence", specification.OccurrenceId);
                Add(update, "$digest", specification.SpecificationDigest);
                Add(update, "$sid", currentUserSid);
                Add(update, "$session", sessionBinding);
                if (update.ExecuteNonQuery() != 1)
                {
                    TryRollback(transaction);
                    return null;
                }
            }
            _failureHook?.Invoke("required_once_execution_start_commit_written");
            ValidateCommittedReadback(connection, transaction, executionId, runId, specification, executionApprovalId, proofId, proofNonce, nowUtc);
            transaction.Commit();
            return new(executionId, runId, executionApprovalId, proofId, proofNonce,
                approvedAtUtc, nowUtc, specification);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            if (exception is Phase3PersistenceException) throw;
            if (exception is PersistedSnapshotException) throw InvalidSnapshot(exception);
            if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
                return null;
            throw InfrastructureFailure(exception);
        }
    }

    internal RequiredOnceExecutionWriteResult SettleBeforeStart(
        string executionId,
        string terminalStatus,
        string reasonCode,
        DateTimeOffset nowUtc)
    {
        if (terminalStatus is not ("rejected" or "expired" or "blocked") || !Canonical(reasonCode) || nowUtc.Offset != TimeSpan.Zero)
            return new(false, "execution_terminal_input_invalid");
        return ExecuteLifecycle((connection, transaction) =>
        {
            var execution = ReadExecution(connection, transaction, executionId);
            if (execution is null || execution.StatusCode != "pending_confirmation")
                return new(false, "execution_no_longer_pending");
            var occurrence = ReadOccurrence(connection, transaction, execution.OccurrenceId);
            var next = terminalStatus switch
            {
                "rejected" => PlanOccurrenceStatus.Cancelled,
                "expired" => PlanOccurrenceStatus.Expired,
                _ => PlanOccurrenceStatus.Blocked,
            };
            RequireTransition(occurrence.TryTransition(next, nowUtc, reasonCode));
            UpdateOccurrence(connection, transaction, occurrence, occurrence.Version - 1, "pending_confirmation");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE required_once_execution_authorizations SET status_code = $status, reason_code = $reason, updated_at_utc = $now, version = version + 1 WHERE execution_id = $id AND status_code = 'pending_confirmation' AND version = $version;";
            Add(update, "$status", terminalStatus);
            Add(update, "$reason", reasonCode);
            Add(update, "$now", UtcTicksInput(nowUtc));
            Add(update, "$id", executionId);
            Add(update, "$version", execution.Version);
            if (update.ExecuteNonQuery() != 1) return new(false, "execution_terminal_conflict");
            return new(true, reasonCode);
        });
    }

    internal RequiredOnceExecutionWriteResult ObserveFirstFrame(string executionId, DateTimeOffset atUtc) =>
        ExecuteLifecycle((connection, transaction) =>
        {
            var chain = ReadCommittedChain(connection, transaction, executionId);
            if (chain is null) return new(false, "required_once_execution_missing");
            if (chain.Execution.StatusCode == "recording") return new(true, "idempotent_noop");
            if (chain.Execution.StatusCode != "start_committed" || chain.Run.Status != RecordingRunStatus.StartCommitted)
                return new(false, "required_once_first_frame_state_conflict");
            var oldRunVersion = chain.Run.Version;
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Recording, atUtc));
            UpdateRun(connection, transaction, chain.Run, oldRunVersion, "start_committed");
            UpdateExecutionStatus(connection, transaction, chain.Execution, "recording", null, atUtc);
            return new(true, "first_frame_observed");
        });

    internal RequiredOnceExecutionWriteResult ObserveCaptureEnded(string executionId, DateTimeOffset atUtc) =>
        ExecuteLifecycle((connection, transaction) =>
        {
            var chain = ReadCommittedChain(connection, transaction, executionId);
            if (chain is null) return new(false, "required_once_execution_missing");
            if (chain.Execution.StatusCode is "finalizing" or "settled") return new(true, "idempotent_noop");
            if (chain.Execution.StatusCode == "start_committed" && chain.Run.Status == RecordingRunStatus.StartCommitted)
                return new(true, "capture_ended_before_first_frame");
            if (chain.Execution.StatusCode != "recording" || chain.Run.Status != RecordingRunStatus.Recording)
                return new(false, "required_once_capture_end_state_conflict");
            var oldRunVersion = chain.Run.Version;
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Finalizing, atUtc));
            UpdateRun(connection, transaction, chain.Run, oldRunVersion, "recording");
            UpdateExecutionStatus(connection, transaction, chain.Execution, "finalizing", null, atUtc);
            return new(true, "capture_ended");
        });

    internal RequiredOnceExecutionWriteResult MarkPostCommitFailure(
        string executionId,
        string reasonCode,
        DateTimeOffset atUtc,
        bool sessionInterrupted = false,
        bool startOutcomeKnownFailed = false) => ExecuteLifecycle((connection, transaction) =>
        {
            if (!Canonical(reasonCode) || atUtc.Offset != TimeSpan.Zero)
                return new(false, "required_once_terminal_input_invalid");
            var chain = ReadCommittedChain(connection, transaction, executionId);
            if (chain is null) return new(false, "required_once_execution_missing");
            if (chain.Execution.StatusCode is "settled" or "failed" or "started_unknown" or "session_interrupted")
                return new(true, "idempotent_noop");
            // Before a credible frame, a committed Start has an unknown
            // outcome rather than a confirmed in-session recording. Keep the
            // domain's legal non-retryable edge for that crash window.
            var targetRun = sessionInterrupted && chain.Run.Status != RecordingRunStatus.StartCommitted
                ? RecordingRunStatus.SessionInterrupted
                : startOutcomeKnownFailed
                    ? RecordingRunStatus.Failed
                : chain.Run.Status == RecordingRunStatus.StartCommitted
                    ? RecordingRunStatus.StartedUnknown
                    : RecordingRunStatus.Failed;
            var targetExecution = targetRun == RecordingRunStatus.SessionInterrupted ? "session_interrupted" :
                targetRun == RecordingRunStatus.StartedUnknown ? "started_unknown" : "failed";
            var occurrenceVersion = chain.Occurrence.Version;
            var runVersion = chain.Run.Version;
            var previousRunStatus = chain.Run.StatusCode;
            RequireTransition(chain.Run.TryTransition(targetRun, atUtc, reasonCode));
            if (chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
                return new(false, "required_once_occurrence_terminal_conflict");
            RequireTransition(chain.Occurrence.TryTransition(PlanOccurrenceStatus.Blocked, atUtc, reasonCode));
            UpdateRun(connection, transaction, chain.Run, runVersion, previousRunStatus);
            UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion, "run_created");
            UpdateExecutionStatus(connection, transaction, chain.Execution, targetExecution, reasonCode, atUtc);
            return new(true, reasonCode);
        });

    internal RequiredOnceExecutionWriteResult SettleFinalization(
        string executionId,
        bool succeeded,
        OutputMeta meta,
        int exitCode,
        string? reasonCode,
        DateTimeOffset atUtc) => ExecuteLifecycle((connection, transaction) =>
        {
            ArgumentNullException.ThrowIfNull(meta);
            if (atUtc.Offset != TimeSpan.Zero) return new(false, "required_once_settlement_time_invalid");
            var chain = ReadCommittedChain(connection, transaction, executionId);
            if (chain is null) return new(false, "required_once_execution_missing");
            if (chain.Execution.StatusCode == "settled") return new(true, "idempotent_noop");
            if (chain.Execution.StatusCode is "failed" or "started_unknown" or "session_interrupted")
                return new(true, "idempotent_terminal");

            var expectedPath = chain.Specification.FrozenOutputFilePath;
            var actualPath = meta.OutputPath;
            long actualSize = 0;
            var outputValid = false;
            try
            {
                var fullPath = string.IsNullOrWhiteSpace(actualPath) ? null : Path.GetFullPath(actualPath);
                var file = fullPath is null ? null : new FileInfo(fullPath);
                outputValid = exitCode == 0 && fullPath is not null &&
                    string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase) &&
                    file is { Exists: true, Length: > 512 } && file.Length == meta.SizeBytes &&
                    meta.DurationSeconds > 0 &&
                    meta.DurationSeconds <= chain.Specification.Duration.TotalSeconds * 1.5 &&
                    (meta.AudioSourceKind is null or "none") &&
                    (meta.AudioStatus is null or "not_requested") && !meta.HasAudioStream && meta.AudioCodec is null;
                if (outputValid) actualSize = file!.Length;
            }
            catch { outputValid = false; }
            if (!succeeded || !outputValid)
            {
                var reason = Canonical(reasonCode) ? reasonCode! :
                    exitCode != 0 ? "capture_exit_nonzero" : "required_once_media_invalid";
                var targetRun = chain.Run.Status == RecordingRunStatus.StartCommitted
                    ? RecordingRunStatus.StartedUnknown
                    : RecordingRunStatus.Failed;
                var targetExecution = targetRun == RecordingRunStatus.StartedUnknown ? "started_unknown" : "failed";
                var runVersion = chain.Run.Version;
                var occurrenceVersion = chain.Occurrence.Version;
                var oldRunStatus = chain.Run.StatusCode;
                if (!CanTransitionRun(chain.Run, targetRun, reason, atUtc))
                    return new(false, "required_once_failure_settlement_conflict");
                if (chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
                    return new(false, "required_once_occurrence_terminal_conflict");
                RequireTransition(chain.Occurrence.TryTransition(PlanOccurrenceStatus.Blocked, atUtc, reason));
                UpdateRun(connection, transaction, chain.Run, runVersion, oldRunStatus);
                UpdateOccurrence(connection, transaction, chain.Occurrence, occurrenceVersion, "run_created");
                UpdateExecutionStatus(connection, transaction, chain.Execution, targetExecution, reason, atUtc);
                return new(true, reason);
            }

            if (chain.Occurrence.Status != PlanOccurrenceStatus.RunCreated ||
                chain.Run.Status is not (RecordingRunStatus.Recording or RecordingRunStatus.Finalizing))
                return new(false, "required_once_success_settlement_conflict");
            var executionForSettlement = chain.Execution;
            if (executionForSettlement.StatusCode == "recording")
            {
                // An engine-owned Stop suppresses the backend's synchronous
                // CaptureEnded callback while Stop is in flight. Persist the
                // missing durable edge here so the schema's transition guard
                // observes recording -> finalizing -> settled, never a direct
                // recording -> settled jump.
                UpdateExecutionStatus(connection, transaction, executionForSettlement,
                    "finalizing", null, atUtc);
                executionForSettlement = executionForSettlement with
                {
                    StatusCode = "finalizing",
                    Version = checked(executionForSettlement.Version + 1),
                };
            }
            var oldVersionSuccess = chain.Run.Version;
            var oldStatusSuccess = chain.Run.StatusCode;
            if (chain.Run.Status == RecordingRunStatus.Recording)
                RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Finalizing, atUtc));
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.MediaReady, atUtc));
            RequireTransition(chain.Run.TryTransition(RecordingRunStatus.Settled, atUtc));
            var oldOccurrenceVersion = chain.Occurrence.Version;
            RequireTransition(chain.Occurrence.TryTransition(PlanOccurrenceStatus.Completed, atUtc));
            UpdateRun(connection, transaction, chain.Run, oldVersionSuccess, oldStatusSuccess);
            UpdateOccurrence(connection, transaction, chain.Occurrence, oldOccurrenceVersion, "run_created");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE required_once_execution_authorizations SET status_code = 'settled', reason_code = NULL,
                    output_path = $path, output_size_bytes = $size, actual_duration_ms = $duration,
                    updated_at_utc = $now, version = version + 1
                WHERE execution_id = $id AND status_code IN ('start_committed', 'recording', 'finalizing')
                  AND run_id = $run AND version = $version;
                """;
            Add(update, "$path", expectedPath); Add(update, "$size", actualSize);
            Add(update, "$duration", checked((long)Math.Round(meta.DurationSeconds * 1000d, MidpointRounding.AwayFromZero)));
            Add(update, "$now", UtcTicksInput(atUtc)); Add(update, "$id", executionId);
            Add(update, "$run", chain.Run.Id); Add(update, "$version", executionForSettlement.Version);
            if (update.ExecuteNonQuery() != 1) return new(false, "required_once_settlement_conflict");
            return new(true, "media_settled");
        });

    internal RequiredOnceExecutionWriteResult MarkUnavailable(
        RequiredOnceExecutionCandidate candidate,
        string reasonCode,
        DateTimeOffset nowUtc) => MarkScheduledTerminal(
            candidate.Specification.PlanId, candidate.Specification.OccurrenceId,
            candidate.Specification.CurrentUserSid, candidate.Specification.SessionBinding,
            PlanOccurrenceStatus.Blocked, reasonCode, nowUtc);

    internal RequiredOnceExecutionWriteResult MarkMissed(
        RequiredOnceExecutionCandidate candidate,
        DateTimeOffset nowUtc) => MarkScheduledTerminal(
            candidate.Specification.PlanId, candidate.Specification.OccurrenceId,
            candidate.Specification.CurrentUserSid, candidate.Specification.SessionBinding,
            PlanOccurrenceStatus.Missed, "latest_start_window_missed", nowUtc);

    /// <summary>
    /// Bounded startup reconciliation. Pending local prompts expire without a
    /// Run; any already committed handoff becomes non-retryable interrupted
    /// evidence before the natural-wake scheduler can start.
    /// </summary>
    internal RequiredOnceExecutionRecoveryBatch RecoverOutstanding(
        int limit,
        DateTimeOffset nowUtc)
    {
        if (limit is < 1 or > MaximumCandidates || nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = new List<(string ExecutionId, string Status)>();
        using (var connection = OpenBusinessConnection())
        using (var transaction = BeginReadTransaction(connection))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT execution_id, status_code FROM required_once_execution_authorizations WHERE status_code IN ('pending_confirmation', 'start_committed', 'recording', 'finalizing') ORDER BY created_at_utc, execution_id COLLATE BINARY LIMIT $limit;";
            Add(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((ReadRequiredText(reader, 0), ReadRequiredText(reader, 1)));
            transaction.Commit();
        }

        foreach (var row in rows)
        {
            if (row.Status == "pending_confirmation")
                _ = SettleBeforeStart(row.ExecutionId, "expired", "process_restarted_during_confirmation", nowUtc);
            else
                _ = MarkPostCommitFailure(row.ExecutionId, "process_restarted_after_start_commit", nowUtc,
                    sessionInterrupted: true);
        }

        bool hasMore;
        using (var connection = OpenBusinessConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM required_once_execution_authorizations WHERE status_code IN ('pending_confirmation', 'start_committed', 'recording', 'finalizing'));";
            hasMore = command.ExecuteScalar() is long value && value == 1;
        }
        return new(rows.Count, hasMore);
    }

    private RequiredOnceExecutionWriteResult MarkScheduledTerminal(
        string planId, string occurrenceId, string sid, string session,
        PlanOccurrenceStatus status, string reason, DateTimeOffset nowUtc)
    {
        return ExecuteLifecycle((connection, transaction) =>
        {
            MarkScheduledTerminalWithinTransaction(connection, transaction, planId, occurrenceId,
                sid, session, status, reason, nowUtc, expectedVersion: null);
            return new(true, reason);
        });
    }

    private static void MarkScheduledTerminalWithinTransaction(
        SqliteConnection connection, SqliteTransaction transaction,
        string planId, string occurrenceId, string sid, string session,
        PlanOccurrenceStatus status, string reason, DateTimeOffset nowUtc, long? expectedVersion)
    {
        var occurrence = ReadOccurrence(connection, transaction, occurrenceId);
        if (occurrence.Status != PlanOccurrenceStatus.Scheduled || occurrence.RunId is not null ||
            (expectedVersion.HasValue && occurrence.Version != expectedVersion.Value))
            return;
        using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT COUNT(*) FROM required_once_setup_intents WHERE plan_id = $plan AND occurrence_id = $occurrence AND current_user_sid = $sid AND session_binding = $session AND status_code = 'scheduled';";
            Add(owner, "$plan", planId); Add(owner, "$occurrence", occurrenceId);
            Add(owner, "$sid", sid); Add(owner, "$session", session);
            if (owner.ExecuteScalar() is not long count || count != 1) return;
        }
        RequireTransition(occurrence.TryTransition(status, nowUtc, reason));
        UpdateOccurrence(connection, transaction, occurrence, occurrence.Version - 1, "scheduled");
    }

    private static RequiredOnceCaptureExecutionSpecification CreateSpecification(
        RequiredOncePlanSetupReadback state, string approvalId)
    {
        var selection = state.Selection ?? throw new PersistedSnapshotException("Required-once selection is missing.");
        return new RequiredOnceCaptureExecutionSpecification(
            state.PlanId!, state.OccurrenceId!, state.Request.SetupIntentId, approvalId,
            state.Request.CurrentUserSid, state.Request.SessionBinding,
            state.Request.ScheduledStartUtc, state.Request.LatestStartUtc, state.Request.PlannedEndUtc,
            state.Request.Duration, state.Request.OutputDirectory, state.Request.FrozenFileName,
            selection.StableDisplayFingerprint, selection.DisplayBounds, selection.RegionWithinDisplay,
            selection.DpiX, selection.DpiY, selection.PhysicalWidth, selection.PhysicalHeight,
            selection.Orientation, selection.TopologyDigest);
    }

    private static string ReadCreationApprovalId(
        SqliteConnection connection, SqliteTransaction transaction, string planId, string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT creation_approval_id FROM required_once_authorized_specs WHERE plan_id = $plan AND occurrence_id = $occurrence LIMIT 2;";
        Add(command, "$plan", planId); Add(command, "$occurrence", occurrenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new PersistedSnapshotException("The required-once creation approval evidence is missing.");
        var value = ReadRequiredText(reader, 0);
        if (reader.Read()) throw new PersistedSnapshotException("The required-once creation approval evidence is duplicated.");
        return value;
    }

    private static void InsertPendingExecution(
        SqliteConnection connection, SqliteTransaction transaction, string executionId,
        RequiredOnceCaptureExecutionSpecification specification, DateTimeOffset nowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO required_once_execution_authorizations
            (execution_id, setup_intent_id, plan_id, occurrence_id, status_code,
             current_user_sid, session_binding, specification_digest, creation_approval_id,
             scheduled_start_utc, latest_start_utc, planned_end_utc, duration_ms,
             created_at_utc, updated_at_utc, version)
            VALUES ($id, $setup, $plan, $occurrence, 'pending_confirmation',
             $sid, $session, $digest, $creationApproval, $start, $latest, $end, $duration,
             $now, $now, 0);
            """;
        Add(command, "$id", executionId); Add(command, "$setup", specification.SetupIntentId);
        Add(command, "$plan", specification.PlanId); Add(command, "$occurrence", specification.OccurrenceId);
        Add(command, "$sid", specification.CurrentUserSid); Add(command, "$session", specification.SessionBinding);
        Add(command, "$digest", specification.SpecificationDigest); Add(command, "$creationApproval", specification.CreationApprovalId);
        Add(command, "$start", UtcTicksInput(specification.ScheduledStartUtc));
        Add(command, "$latest", UtcTicksInput(specification.LatestStartUtc));
        Add(command, "$end", UtcTicksInput(specification.PlannedEndUtc));
        Add(command, "$duration", DurationMillisecondsInput(specification.Duration));
        Add(command, "$now", UtcTicksInput(nowUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertRun(SqliteConnection connection, SqliteTransaction transaction, RecordingRun run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO recording_runs (id, occurrence_id, status_code, has_crossed_start_commit, created_at_utc, updated_at_utc, version) VALUES ($id, $occurrence, $status, 1, $created, $updated, $version);";
        Add(command, "$id", run.Id); Add(command, "$occurrence", run.OccurrenceId);
        Add(command, "$status", run.StatusCode); Add(command, "$created", UtcTicksInput(run.CreatedAtUtc));
        Add(command, "$updated", UtcTicksInput(run.UpdatedAtUtc)); Add(command, "$version", run.Version);
        command.ExecuteNonQuery();
    }

    private static void UpdateOccurrence(
        SqliteConnection connection, SqliteTransaction transaction, PlanOccurrence occurrence,
        long oldVersion, string expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status, run_id = $run, terminal_reason_code = $reason, updated_at_utc = $updated, version = $version WHERE id = $id AND status_code = $expected AND version = $oldVersion;";
        Add(command, "$status", occurrence.StatusCode); Add(command, "$run", occurrence.RunId);
        Add(command, "$reason", occurrence.TerminalReasonCode); Add(command, "$updated", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$version", occurrence.Version); Add(command, "$id", occurrence.Id);
        Add(command, "$expected", expectedStatus); Add(command, "$oldVersion", oldVersion);
        if (command.ExecuteNonQuery() != 1) throw ConcurrencyConflict();
    }

    private static PlanOccurrence ReadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version FROM plan_occurrences WHERE id = $id LIMIT 2;";
        Add(command, "$id", occurrenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw NotFound();
        var occurrence = PlanOccurrence.Rehydrate(ReadRequiredText(reader, 0), ReadRequiredText(reader, 1),
            ReadUtcDateTimeOffset(reader, 3), ReadUtcDateTimeOffset(reader, 4), ReadUtcDateTimeOffset(reader, 7),
            Phase3StateCodes.ParsePlanOccurrence(ReadRequiredText(reader, 2)), ReadNullableText(reader, 5),
            ReadNullableText(reader, 6), ReadUtcDateTimeOffset(reader, 8), ReadInt64(reader, 9));
        if (reader.Read()) throw new PersistedSnapshotException("The required occurrence identifier is duplicated.");
        return occurrence;
    }

    private sealed record ExecutionRow(
        string ExecutionId, string PlanId, string OccurrenceId, string? RunId, string StatusCode,
        string CurrentUserSid, string SessionBinding, string SpecificationDigest,
        string? ExecutionApprovalId, string? ProofId, string? ProofNonce,
        DateTimeOffset ScheduledStartUtc, DateTimeOffset LatestStartUtc,
        long Version);

    private static ExecutionRow? ReadPendingExecution(SqliteConnection connection, SqliteTransaction transaction, string executionId) =>
        ReadExecution(connection, transaction, executionId);

    private static ExecutionRow? ReadExecution(SqliteConnection connection, SqliteTransaction transaction, string executionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT execution_id, plan_id, occurrence_id, run_id, status_code, current_user_sid, session_binding, specification_digest, execution_approval_id, proof_id, proof_nonce, scheduled_start_utc, latest_start_utc, version FROM required_once_execution_authorizations WHERE execution_id = $id LIMIT 2;";
        Add(command, "$id", executionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new ExecutionRow(ReadRequiredText(reader, 0), ReadRequiredText(reader, 1), ReadRequiredText(reader, 2),
            ReadNullableText(reader, 3), ReadRequiredText(reader, 4), ReadRequiredText(reader, 5), ReadRequiredText(reader, 6),
            ReadRequiredText(reader, 7), ReadNullableText(reader, 8), ReadNullableText(reader, 9), ReadNullableText(reader, 10),
            ReadUtcDateTimeOffset(reader, 11), ReadUtcDateTimeOffset(reader, 12), ReadInt64(reader, 13));
        if (reader.Read()) throw new PersistedSnapshotException("The required execution identifier is duplicated.");
        return row;
    }

    private sealed record LifecycleChain(
        ExecutionRow Execution,
        RecordingRun Run,
        PlanOccurrence Occurrence,
        RequiredOnceCaptureExecutionSpecification Specification);

    private static LifecycleChain? ReadCommittedChain(
        SqliteConnection connection, SqliteTransaction transaction, string executionId)
    {
        var execution = ReadExecution(connection, transaction, executionId);
        if (execution is null) return null;
        if (execution.RunId is null || execution.ExecutionApprovalId is null ||
            execution.ProofId is null || execution.ProofNonce is null)
            throw new PersistedSnapshotException("A post-commit required execution is missing its run or proof receipt.");
        var setup = SqliteRequiredOncePlanSetupRepository.ReadAndValidateExecutionProgressPlanWithinTransaction(
            connection, transaction, execution.PlanId, execution.OccurrenceId)
            ?? throw new PersistedSnapshotException("The required execution setup owner is missing.");
        var creationApproval = ReadCreationApprovalId(connection, transaction, execution.PlanId, execution.OccurrenceId);
        var specification = CreateSpecification(setup, creationApproval);
        if (execution.SpecificationDigest != specification.SpecificationDigest ||
            execution.CurrentUserSid != specification.CurrentUserSid ||
            execution.SessionBinding != specification.SessionBinding ||
            execution.ScheduledStartUtc != specification.ScheduledStartUtc ||
            execution.LatestStartUtc != specification.LatestStartUtc)
            throw new PersistedSnapshotException("The required execution row no longer matches its immutable specification.");

        var occurrence = ReadOccurrence(connection, transaction, execution.OccurrenceId);
        var run = ReadRun(connection, transaction, execution.RunId);
        if (run.OccurrenceId != occurrence.Id || occurrence.RunId != run.Id || run.Id != execution.RunId)
            throw new PersistedSnapshotException("The required execution, run and occurrence relation is invalid.");
        var valid = (execution.StatusCode, run.Status, occurrence.Status) switch
        {
            ("start_committed", RecordingRunStatus.StartCommitted, PlanOccurrenceStatus.RunCreated) => true,
            ("recording", RecordingRunStatus.Recording, PlanOccurrenceStatus.RunCreated) => true,
            ("finalizing", RecordingRunStatus.Finalizing, PlanOccurrenceStatus.RunCreated) => true,
            ("settled", RecordingRunStatus.Settled, PlanOccurrenceStatus.Completed) => true,
            ("started_unknown", RecordingRunStatus.StartedUnknown, PlanOccurrenceStatus.Blocked) => true,
            ("session_interrupted", RecordingRunStatus.SessionInterrupted, PlanOccurrenceStatus.Blocked) => true,
            ("failed", RecordingRunStatus.Failed, PlanOccurrenceStatus.Blocked) => true,
            _ => false,
        };
        if (!valid || (occurrence.Status == PlanOccurrenceStatus.Blocked &&
            !string.Equals(occurrence.TerminalReasonCode, run.TerminalReasonCode, StringComparison.Ordinal)))
            throw new PersistedSnapshotException("The required execution lifecycle states are unreachable or inconsistent.");
        return new(execution, run, occurrence, specification);
    }

    private static RecordingRun ReadRun(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version FROM recording_runs WHERE id = $id LIMIT 2;";
        Add(command, "$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new PersistedSnapshotException("The required execution run is missing.");
        var run = RecordingRun.Rehydrate(ReadRequiredText(reader, 0), ReadRequiredText(reader, 1),
            ReadUtcDateTimeOffset(reader, 7), Phase3StateCodes.ParseRecordingRun(ReadRequiredText(reader, 2)),
            ReadBoolean(reader, 3), ReadNullableText(reader, 4), ReadNullableText(reader, 5),
            ReadNullableText(reader, 6), ReadUtcDateTimeOffset(reader, 8), ReadInt64(reader, 9));
        if (reader.Read()) throw new PersistedSnapshotException("The required execution run is duplicated.");
        return run;
    }

    private static void UpdateRun(
        SqliteConnection connection, SqliteTransaction transaction,
        RecordingRun run, long oldVersion, string expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE recording_runs SET status_code = $status, has_crossed_start_commit = $committed, terminal_reason_code = $reason, updated_at_utc = $updated, version = $version WHERE id = $id AND occurrence_id = $occurrence AND status_code = $expected AND version = $oldVersion;";
        Add(command, "$status", run.StatusCode); Add(command, "$committed", run.HasCrossedStartCommit ? 1 : 0);
        Add(command, "$reason", run.TerminalReasonCode); Add(command, "$updated", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$version", run.Version); Add(command, "$id", run.Id); Add(command, "$occurrence", run.OccurrenceId);
        Add(command, "$expected", expectedStatus); Add(command, "$oldVersion", oldVersion);
        if (command.ExecuteNonQuery() != 1) throw ConcurrencyConflict();
    }

    private static void UpdateExecutionStatus(
        SqliteConnection connection, SqliteTransaction transaction,
        ExecutionRow execution, string status, string? reason, DateTimeOffset atUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE required_once_execution_authorizations SET status_code = $status, reason_code = $reason, updated_at_utc = $now, version = version + 1 WHERE execution_id = $id AND status_code = $oldStatus AND version = $version;";
        Add(command, "$status", status); Add(command, "$reason", reason); Add(command, "$now", UtcTicksInput(atUtc));
        Add(command, "$id", execution.ExecutionId); Add(command, "$oldStatus", execution.StatusCode); Add(command, "$version", execution.Version);
        if (command.ExecuteNonQuery() != 1) throw ConcurrencyConflict();
    }

    private static void SettlePendingExecutionWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExecutionRow execution,
        PlanOccurrence occurrence,
        string terminalStatus,
        string reasonCode,
        DateTimeOffset atUtc)
    {
        var next = terminalStatus == "expired" ? PlanOccurrenceStatus.Expired : PlanOccurrenceStatus.Blocked;
        RequireTransition(occurrence.TryTransition(next, atUtc, reasonCode));
        UpdateOccurrence(connection, transaction, occurrence, occurrence.Version - 1, "pending_confirmation");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE required_once_execution_authorizations SET status_code = $status, reason_code = $reason, updated_at_utc = $now, version = version + 1 WHERE execution_id = $id AND status_code = 'pending_confirmation' AND version = $version;";
        Add(command, "$status", terminalStatus); Add(command, "$reason", reasonCode);
        Add(command, "$now", UtcTicksInput(atUtc)); Add(command, "$id", execution.ExecutionId);
        Add(command, "$version", execution.Version);
        if (command.ExecuteNonQuery() != 1) throw ConcurrencyConflict();
    }

    private static bool CanTransitionRun(RecordingRun run, RecordingRunStatus target, string reason, DateTimeOffset atUtc)
    {
        if (!Phase3TransitionGuards.CanTransition(run.Status, target, out _)) return false;
        return run.TryTransition(target, atUtc, reason).Succeeded;
    }

    private static void ValidateCommittedReadback(
        SqliteConnection connection, SqliteTransaction transaction, string executionId, string runId,
        RequiredOnceCaptureExecutionSpecification specification, string approvalId,
        string proofId, string nonce, DateTimeOffset nowUtc)
    {
        var row = ReadExecution(connection, transaction, executionId);
        var occurrence = ReadOccurrence(connection, transaction, specification.OccurrenceId);
        using var runCommand = connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = "SELECT status_code, has_crossed_start_commit, occurrence_id FROM recording_runs WHERE id = $id LIMIT 2;";
        Add(runCommand, "$id", runId);
        using var reader = runCommand.ExecuteReader();
        if (row is null || row.StatusCode != "start_committed" || row.RunId != runId ||
            row.ExecutionApprovalId != approvalId || row.ProofId != proofId || row.ProofNonce != nonce ||
            occurrence.Status != PlanOccurrenceStatus.RunCreated || occurrence.RunId != runId ||
            !reader.Read() || ReadRequiredText(reader, 0) != "start_committed" || ReadInt64(reader, 1) != 1 ||
            ReadRequiredText(reader, 2) != occurrence.Id || reader.Read())
            throw new PersistedSnapshotException("The required-once start commit readback does not match its receipt.");
        _ = nowUtc;
    }

    private RequiredOnceExecutionWriteResult ExecuteLifecycle(
        Func<SqliteConnection, SqliteTransaction, RequiredOnceExecutionWriteResult> operation)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var result = operation(connection, transaction);
            if (!result.Succeeded)
            {
                transaction.Commit();
                return result;
            }
            _failureHook?.Invoke("required_once_lifecycle_before_commit");
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException) { TryRollback(transaction); throw; }
        catch (PersistedSnapshotException exception) { TryRollback(transaction); throw InvalidSnapshot(exception); }
        catch (Exception exception) { TryRollback(transaction); throw InfrastructureFailure(exception); }
        finally { transaction?.Dispose(); }
    }

    private static void RequireTransition(Phase3TransitionResult transition)
    {
        if (!transition.Succeeded) throw new PersistedSnapshotException("The required-once lifecycle transition is invalid.");
    }

    private static bool Canonical(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);
    private static bool IsNonce(string? value) => value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void TryRollback(SqliteTransaction? transaction) { try { transaction?.Rollback(); } catch { } }
}
