using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringLeaseLifecycleTerminationKind
{
    NaturalExit,
    UserStop,
    BackendStartFailure,
    LifecyclePersistenceFailure,
    SafetyStop,
    SessionInterrupted,
}

internal enum RecurringLeaseLifecycleFailurePoint
{
    AfterRunUpdate,
    AfterUseUpdate,
    AfterOccurrenceUpdate,
    AfterLeaseExhaustionUpdate,
    BeforeFinalReadback,
    BeforeCommit,
}

/// <summary>
/// Durable recurring post-start lifecycle boundary. The caller supplies only
/// immutable execution identity evidence; every mutable aggregate and the
/// persisted execution specification are re-read and validated under one
/// immediate SQLite transaction before any transition is attempted.
/// </summary>
internal sealed class SqliteRecurringLeaseLifecycleTransaction : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private readonly RecurringLeaseExecutionSnapshotLookup _lookup;
    private readonly RecurringOccurrenceExecutionSpecification? _specificationEvidence;
    private readonly string? _evidenceRunId;
    private readonly string? _evidenceUseId;
    private readonly string? _evidenceLeaseAuthorizationDigest;
    private readonly Action<RecurringLeaseLifecycleFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteRecurringLeaseLifecycleTransaction(
        SqliteOperationalStore store,
        RecurringLeaseCaptureExecutionTicket ticket,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : this(
            store,
            RecurringLeaseExecutionSnapshotLookup.FromTicket(
                ticket ?? throw new ArgumentNullException(nameof(ticket))),
            failureHookForTest,
            beforeCommitForTest,
            ticket.Specification,
            ticket.RunId,
            ticket.UseId,
            ticket.LeaseAuthorizationDigest)
    {
    }

    internal SqliteRecurringLeaseLifecycleTransaction(
        SqliteOperationalStore store,
        RecurringStartCommitReceipt receipt,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : this(
            store,
            new RecurringLeaseExecutionSnapshotLookup(
                receipt?.Lease.LeaseId ?? throw new ArgumentNullException(nameof(receipt)),
                receipt.Plan.Id,
                receipt.Specification.OccurrenceIdentity,
                receipt.Occurrence.Id,
                receipt.Run.Id,
                receipt.Use.Id),
            failureHookForTest,
            beforeCommitForTest,
            receipt.Specification,
            receipt.Run.Id,
            receipt.Use.Id,
            receipt.Lease.AuthorizationDigest)
    {
    }

    private SqliteRecurringLeaseLifecycleTransaction(
        SqliteOperationalStore store,
        RecurringLeaseExecutionSnapshotLookup lookup,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest,
        RecurringOccurrenceExecutionSpecification? specificationEvidence,
        string? evidenceRunId,
        string? evidenceUseId,
        string? evidenceLeaseAuthorizationDigest)
        : base(store)
    {
        _lookup = lookup;
        _specificationEvidence = specificationEvidence;
        _evidenceRunId = evidenceRunId;
        _evidenceUseId = evidenceUseId;
        _evidenceLeaseAuthorizationDigest = evidenceLeaseAuthorizationDigest;
        _failureHookForTest = failureHookForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal SqliteRecurringLeaseLifecycleTransaction(
        SqliteOperationalStore store,
        RecurringOccurrenceExecutionSpecification specification,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _specificationEvidence = specification ?? throw new ArgumentNullException(nameof(specification));
        _lookup = default;
        _evidenceRunId = null;
        _evidenceUseId = null;
        _evidenceLeaseAuthorizationDigest = null;
        _failureHookForTest = failureHookForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal RecurringLeaseLifecycleActionResult ObserveFirstFrame(
        FirstFrameObservation? observation,
        DateTimeOffset observedAtUtc)
    {
        if (observation is null ||
            !string.Equals(observation.EvidenceKind, "ffmpeg_progress_frame_and_output_bytes", StringComparison.Ordinal) ||
            observation.FrameNumber < 0 ||
            observation.TotalSizeBytes <= 0 ||
            observedAtUtc.Offset != TimeSpan.Zero)
        {
            return RecurringLeaseLifecycleActionResult.Rejected("first_frame_evidence_invalid");
        }

        return Execute((connection, transaction) =>
        {
            var snapshot = LoadSnapshot(connection, transaction);
            EnsureLegalLifecycleChain(snapshot);
            EnsureExecutableLease(snapshot);

            if (IsTerminalChain(snapshot))
            {
                return RecurringLeaseLifecycleActionResult.Idempotent(
                    "first_frame_after_terminal",
                    terminal: true);
            }

            if (snapshot.Run.Status == RecordingRunStatus.Recording &&
                snapshot.Use.Status == LeaseUseStatus.Consumed &&
                snapshot.Occurrence.Status == PlanOccurrenceStatus.RunCreated)
            {
                return RecurringLeaseLifecycleActionResult.Idempotent("first_frame_already_observed");
            }

            RequireExactCommitted(snapshot, "first_frame_not_expected");
            var runVersion = snapshot.Run.Version;
            var useVersion = snapshot.Use.Version;
            RequireChanged(snapshot.Run.TryTransition(RecordingRunStatus.Recording, observedAtUtc));
            RequireChanged(snapshot.Use.TryTransition(LeaseUseStatus.Consumed, observedAtUtc));
            UpdateRun(connection, transaction, snapshot.Run, runVersion, RecordingRunStatus.StartCommitted);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterRunUpdate);
            UpdateUse(connection, transaction, snapshot.Use, useVersion, LeaseUseStatus.StartCommitted, snapshot.Plan.Id, snapshot.Specification.OccurrenceIdentity);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterUseUpdate);
            ReconcileLeaseQuota(connection, transaction, snapshot, observedAtUtc);
            FinalReadback(connection, transaction, snapshot, "first_frame_observed");
            return RecurringLeaseLifecycleActionResult.Applied("first_frame_observed");
        });
    }

    internal RecurringLeaseLifecycleActionResult ObserveCaptureEnded(
        CaptureEndedObservation? observation)
    {
        if (observation is null)
            return RecurringLeaseLifecycleActionResult.Rejected("capture_ended_observation_invalid");

        return ObserveCaptureEnded(observation.EndedAtUtc);
    }

    internal RecurringLeaseLifecycleActionResult ObserveCaptureEnded(
        DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
            return RecurringLeaseLifecycleActionResult.Rejected("capture_ended_observation_invalid");

        return Execute((connection, transaction) =>
        {
            var snapshot = LoadSnapshot(connection, transaction);
            EnsureLegalLifecycleChain(snapshot);
            EnsureExecutableLease(snapshot);

            if (IsTerminalChain(snapshot))
            {
                return RecurringLeaseLifecycleActionResult.Idempotent(
                    "capture_ended_after_terminal",
                    terminal: true);
            }

            if (IsExactCommitted(snapshot))
                return RecurringLeaseLifecycleActionResult.Idempotent("capture_ended_before_first_frame");

            if (snapshot.Run.Status is RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady)
            {
                return RecurringLeaseLifecycleActionResult.Idempotent("capture_already_finalizing");
            }

            if (snapshot.Run.Status != RecordingRunStatus.Recording ||
                snapshot.Use.Status != LeaseUseStatus.Consumed ||
                snapshot.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
            {
                throw LifecycleConflict("capture_ended_not_expected");
            }

            var runVersion = snapshot.Run.Version;
            RequireChanged(snapshot.Run.TryTransition(RecordingRunStatus.Finalizing, observedAtUtc));
            UpdateRun(connection, transaction, snapshot.Run, runVersion, RecordingRunStatus.Recording);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterRunUpdate);
            FinalReadback(connection, transaction, snapshot, "capture_ended");
            return RecurringLeaseLifecycleActionResult.Applied("capture_ended");
        });
    }

    internal RecurringLeaseLifecycleActionResult CompleteTermination(
        RecurringLeaseLifecycleTerminationKind kind,
        int exitCode,
        OutputMeta? meta,
        DateTimeOffset terminatedAtUtc)
    {
        if (!Enum.IsDefined(kind))
            return RecurringLeaseLifecycleActionResult.Rejected("termination_kind_invalid");
        if (terminatedAtUtc.Offset != TimeSpan.Zero)
            return RecurringLeaseLifecycleActionResult.Rejected("termination_time_invalid");

        return Execute((connection, transaction) =>
        {
            var snapshot = LoadSnapshot(connection, transaction);
            EnsureLegalLifecycleChain(snapshot);
            EnsureExecutableLease(snapshot);

            if (IsTerminalChain(snapshot))
            {
                return RecurringLeaseLifecycleActionResult.Idempotent(
                    "termination_already_settled",
                    terminal: true);
            }

            if (IsExactCommitted(snapshot))
            {
                var reason = BeforeFirstFrameReason(kind);
                TransitionToUnknown(
                    connection,
                    transaction,
                    snapshot,
                    RecordingRunStatus.StartedUnknown,
                    reason,
                    terminatedAtUtc);
                FinalReadback(connection, transaction, snapshot, reason);
                return RecurringLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            EnsurePostStartClaim(snapshot);

            if (kind is RecurringLeaseLifecycleTerminationKind.SafetyStop or
                RecurringLeaseLifecycleTerminationKind.SessionInterrupted)
            {
                var reason = kind == RecurringLeaseLifecycleTerminationKind.SafetyStop
                    ? "safety_stop"
                    : "session_interrupted";
                TransitionToUnknown(
                    connection,
                    transaction,
                    snapshot,
                    RecordingRunStatus.SessionInterrupted,
                    reason,
                    terminatedAtUtc);
                FinalReadback(connection, transaction, snapshot, reason);
                return RecurringLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            if (kind is RecurringLeaseLifecycleTerminationKind.BackendStartFailure or
                RecurringLeaseLifecycleTerminationKind.LifecyclePersistenceFailure)
            {
                if (snapshot.Run.Status == RecordingRunStatus.MediaReady)
                    throw LifecycleConflict("start_failure_after_media_ready");

                var reason = kind == RecurringLeaseLifecycleTerminationKind.BackendStartFailure
                    ? "backend_start_failed_after_first_frame"
                    : "lifecycle_persistence_failure_after_first_frame";
                TransitionToUnknown(
                    connection,
                    transaction,
                    snapshot,
                    RecordingRunStatus.Failed,
                    reason,
                    terminatedAtUtc);
                FinalReadback(connection, transaction, snapshot, reason);
                return RecurringLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            var validOutput = TryReadValidOutput(
                meta,
                snapshot.Specification.FrozenOutputFilePath,
                snapshot.Use.ReservedDuration,
                out var actualDuration,
                out var mediaReason);
            if (exitCode != 0 || !validOutput)
            {
                var reason = kind == RecurringLeaseLifecycleTerminationKind.UserStop
                    ? "user_stop_output_invalid"
                    : exitCode != 0
                        ? "capture_exit_nonzero"
                        : mediaReason;
                var status = kind == RecurringLeaseLifecycleTerminationKind.UserStop
                    ? RecordingRunStatus.SessionInterrupted
                    : RecordingRunStatus.Failed;
                TransitionToUnknown(connection, transaction, snapshot, status, reason, terminatedAtUtc);
                FinalReadback(connection, transaction, snapshot, reason);
                return RecurringLeaseLifecycleActionResult.Applied(reason, terminal: true);
            }

            var runVersion = snapshot.Run.Version;
            var useVersion = snapshot.Use.Version;
            var occurrenceVersion = snapshot.Occurrence.Version;
            var expectedRunStatus = snapshot.Run.Status;
            if (snapshot.Run.Status == RecordingRunStatus.Recording)
            {
                RequireChanged(snapshot.Run.TryTransition(RecordingRunStatus.Finalizing, terminatedAtUtc));
            }

            if (snapshot.Run.Status == RecordingRunStatus.Finalizing)
            {
                RequireChanged(snapshot.Run.TryTransition(RecordingRunStatus.MediaReady, terminatedAtUtc));
            }

            RequireChanged(snapshot.Run.TryTransition(RecordingRunStatus.Settled, terminatedAtUtc));
            RequireChanged(snapshot.Use.TrySettle(actualDuration, terminatedAtUtc));
            RequireChanged(snapshot.Occurrence.TryTransition(PlanOccurrenceStatus.Completed, terminatedAtUtc));
            UpdateRun(connection, transaction, snapshot.Run, runVersion, expectedRunStatus);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterRunUpdate);
            UpdateUse(connection, transaction, snapshot.Use, useVersion, LeaseUseStatus.Consumed, snapshot.Plan.Id, snapshot.Specification.OccurrenceIdentity);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterUseUpdate);
            UpdateOccurrence(connection, transaction, snapshot.Occurrence, occurrenceVersion);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterOccurrenceUpdate);
            ReconcileLeaseQuota(connection, transaction, snapshot, terminatedAtUtc);
            FinalReadback(connection, transaction, snapshot, "media_settled");
            return RecurringLeaseLifecycleActionResult.Applied("media_settled", terminal: true);
        });
    }

    private RecurringLeaseExecutionSnapshot LoadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var lookup = _specificationEvidence is null
            ? _lookup
            : ResolveLookupFromSpecification(connection, transaction, _specificationEvidence);
        var snapshot = SqliteRecurringLeaseExecutionSnapshotLoader.ReadExactSnapshotWithinTransaction(
            connection,
            transaction,
            lookup);
        ValidateEvidenceBinding(snapshot);
        return snapshot;
    }

    private void ValidateEvidenceBinding(RecurringLeaseExecutionSnapshot snapshot)
    {
        if (_specificationEvidence is not null &&
            (!string.Equals(_specificationEvidence.SpecificationDigest, snapshot.Specification.SpecificationDigest, StringComparison.Ordinal) ||
             !string.Equals(_specificationEvidence.PlanId, snapshot.Plan.Id, StringComparison.Ordinal) ||
             !string.Equals(_specificationEvidence.OccurrenceId, snapshot.Occurrence.Id, StringComparison.Ordinal) ||
             !string.Equals(_specificationEvidence.OccurrenceIdentity, snapshot.Specification.OccurrenceIdentity, StringComparison.Ordinal) ||
             !string.Equals(_specificationEvidence.LeaseId, snapshot.Lease.LeaseId, StringComparison.Ordinal)))
        {
            throw SnapshotFailure("The lifecycle immutable specification evidence does not match the persisted specification.");
        }

        if (_evidenceRunId is not null && !string.Equals(_evidenceRunId, snapshot.Run.Id, StringComparison.Ordinal))
            throw SnapshotFailure("The lifecycle run identity evidence does not match the persisted run.");
        if (_evidenceUseId is not null && !string.Equals(_evidenceUseId, snapshot.Use.Id, StringComparison.Ordinal))
            throw SnapshotFailure("The lifecycle use identity evidence does not match the persisted use.");
        if (_evidenceLeaseAuthorizationDigest is not null &&
            !string.Equals(_evidenceLeaseAuthorizationDigest, snapshot.Lease.AuthorizationDigest, StringComparison.Ordinal))
            throw SnapshotFailure("The lifecycle Lease authorization digest evidence does not match the persisted Lease.");
    }

    private static RecurringLeaseExecutionSnapshotLookup ResolveLookupFromSpecification(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringOccurrenceExecutionSpecification specification)
    {
        string? runId = null;
        using (var occurrenceCommand = connection.CreateCommand())
        {
            occurrenceCommand.Transaction = transaction;
            occurrenceCommand.CommandText = "SELECT run_id FROM plan_occurrences WHERE id = $occurrence_id AND plan_id = $plan_id;";
            Add(occurrenceCommand, "$occurrence_id", specification.OccurrenceId);
            Add(occurrenceCommand, "$plan_id", specification.PlanId);
            using var reader = occurrenceCommand.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
                throw SnapshotFailure("The execution specification does not point to a committed occurrence.");
            runId = ReadRequiredText(reader, 0);
        }

        string? useId = null;
        using (var useCommand = connection.CreateCommand())
        {
            useCommand.Transaction = transaction;
            useCommand.CommandText = """
                SELECT use_id
                FROM recurring_lease_uses
                WHERE lease_id = $lease_id AND plan_id = $plan_id
                  AND occurrence_identity = $occurrence_identity
                  AND occurrence_id = $occurrence_id;
                """;
            Add(useCommand, "$lease_id", specification.LeaseId);
            Add(useCommand, "$plan_id", specification.PlanId);
            Add(useCommand, "$occurrence_identity", specification.OccurrenceIdentity);
            Add(useCommand, "$occurrence_id", specification.OccurrenceId);
            using var reader = useCommand.ExecuteReader();
            if (!reader.Read())
                throw SnapshotFailure("The execution specification does not point to a recurring lease use.");
            useId = ReadRequiredText(reader, 0);
            if (reader.Read())
                throw SnapshotFailure("The execution specification points to duplicate recurring lease uses.");
        }

        return new RecurringLeaseExecutionSnapshotLookup(
            specification.LeaseId,
            specification.PlanId,
            specification.OccurrenceIdentity,
            specification.OccurrenceId,
            runId,
            useId);
    }

    private void TransitionToUnknown(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseExecutionSnapshot snapshot,
        RecordingRunStatus runStatus,
        string reason,
        DateTimeOffset atUtc)
    {
        if (!RecurringLifecycleTerminalReasonPolicy.IsKnownPostStart(reason))
            throw SnapshotFailure("The recurring lifecycle reason is not in the closed policy set.");

        var runVersion = snapshot.Run.Version;
        var useVersion = snapshot.Use.Version;
        var occurrenceVersion = snapshot.Occurrence.Version;
        var expectedRunStatus = snapshot.Run.Status;
        var expectedUseStatus = snapshot.Use.Status;
        RequireChanged(snapshot.Run.TryTransition(runStatus, atUtc, reason));
        RequireChanged(snapshot.Use.TryTransition(LeaseUseStatus.StartedUnknown, atUtc));
        RequireChanged(snapshot.Occurrence.TryTransition(PlanOccurrenceStatus.Blocked, atUtc, reason));
        UpdateRun(connection, transaction, snapshot.Run, runVersion, expectedRunStatus);
        InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterRunUpdate);
        UpdateUse(connection, transaction, snapshot.Use, useVersion, expectedUseStatus, snapshot.Plan.Id, snapshot.Specification.OccurrenceIdentity);
        InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterUseUpdate);
        UpdateOccurrence(connection, transaction, snapshot.Occurrence, occurrenceVersion);
        InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterOccurrenceUpdate);
        ReconcileLeaseQuota(connection, transaction, snapshot, atUtc);
    }

    private void ReconcileLeaseQuota(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseExecutionSnapshot original,
        DateTimeOffset atUtc)
    {
        var entries = SqliteRecurringLeaseUseAccountingReader.ReadAllWithinTransaction(
            connection,
            transaction,
            original.Lease);
        RecurringLeaseQuotaSnapshot quota;
        try
        {
            quota = RecurringLeaseQuotaCalculator.Calculate(original.Lease, entries);
        }
        catch (Phase3DomainException exception)
        {
            throw SnapshotFailure("The recurring quota readback is invalid.", exception);
        }

        switch (original.Lease.Status)
        {
            case ConsentLeaseStatus.Active when quota.IsTerminallyExhausted:
            {
                var leaseVersion = original.Lease.Version;
                RequireChanged(original.Lease.TryMarkExhausted(quota, atUtc));
                UpdateLease(connection, transaction, original.Lease, leaseVersion, ConsentLeaseStatus.Active);
                InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.AfterLeaseExhaustionUpdate);
                break;
            }
            case ConsentLeaseStatus.Active:
            case ConsentLeaseStatus.Revoked:
            case ConsentLeaseStatus.Expired:
                break;
            case ConsentLeaseStatus.Exhausted when quota.IsTerminallyExhausted:
                break;
            case ConsentLeaseStatus.Exhausted:
                throw SnapshotFailure("The exhausted recurring lease has nonterminal quota evidence.");
            case ConsentLeaseStatus.Pending:
            case ConsentLeaseStatus.Rejected:
                throw LifecycleConflict("recurring_lease_not_executable");
            default:
                throw SnapshotFailure("The recurring lease status is unknown during quota reconciliation.");
        }
    }

    private void FinalReadback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseExecutionSnapshot original,
        string operation)
    {
        InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.BeforeFinalReadback);
        var final = LoadSnapshot(connection, transaction);
        EnsureLegalLifecycleChain(final);
        if (!string.Equals(final.Specification.SpecificationDigest, original.Specification.SpecificationDigest, StringComparison.Ordinal) ||
            final.Plan.Id != original.Plan.Id ||
            final.Lease.LeaseId != original.Lease.LeaseId ||
            final.Occurrence.Id != original.Occurrence.Id ||
            final.Run.Id != original.Run.Id ||
            final.Use.Id != original.Use.Id)
        {
            throw SnapshotFailure($"The recurring lifecycle {operation} readback was not exact.");
        }

        ValidateFinalLeaseQuota(final);
    }

    private static void ValidateFinalLeaseQuota(RecurringLeaseExecutionSnapshot snapshot)
    {
        if (snapshot.Quota.LeaseId != snapshot.Lease.LeaseId ||
            snapshot.Quota.LeaseVersion != snapshot.Lease.Version ||
            !string.Equals(snapshot.Quota.LeaseAuthorizationDigest, snapshot.Lease.AuthorizationDigest, StringComparison.Ordinal))
        {
            throw SnapshotFailure("The recurring lifecycle quota readback is not bound to the final lease.");
        }

        switch (snapshot.Lease.Status)
        {
            case ConsentLeaseStatus.Active when snapshot.Quota.IsTerminallyExhausted:
                throw SnapshotFailure("The final recurring lifecycle snapshot leaves an active exhausted lease.");
            case ConsentLeaseStatus.Exhausted when !snapshot.Quota.IsTerminallyExhausted:
                throw SnapshotFailure("The final recurring lifecycle snapshot leaves an exhausted lease with nonterminal quota.");
            case ConsentLeaseStatus.Pending:
            case ConsentLeaseStatus.Rejected:
                throw SnapshotFailure("The final recurring lifecycle snapshot has a non-executable lease status.");
        }
    }

    private void EnsureLegalLifecycleChain(RecurringLeaseExecutionSnapshot snapshot)
    {
        if (!RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(
                snapshot.Lease,
                snapshot.Occurrence,
                snapshot.Run,
                snapshot.Use))
        {
            throw SnapshotFailure("The recurring lifecycle chain is not a legal persisted state.");
        }
    }

    private static bool IsExactCommitted(RecurringLeaseExecutionSnapshot snapshot) =>
        RecurringReplayIntegrityPolicy.IsExactCommittedChain(
            snapshot.Lease,
            snapshot.Occurrence,
            snapshot.Run,
            snapshot.Use);

    private static bool IsTerminalChain(RecurringLeaseExecutionSnapshot snapshot) =>
        (snapshot.Occurrence.Status == PlanOccurrenceStatus.Completed &&
            snapshot.Run.Status == RecordingRunStatus.Settled &&
            snapshot.Use.Status == LeaseUseStatus.Settled) ||
        (snapshot.Occurrence.Status == PlanOccurrenceStatus.Blocked &&
            snapshot.Run.Status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed &&
            snapshot.Use.Status == LeaseUseStatus.StartedUnknown);

    private static void EnsureExecutableLease(RecurringLeaseExecutionSnapshot snapshot)
    {
        if (snapshot.Lease.Status is ConsentLeaseStatus.Pending or ConsentLeaseStatus.Rejected)
            throw LifecycleConflict("recurring_lease_not_executable");

        if (snapshot.Lease.Status == ConsentLeaseStatus.Exhausted && !snapshot.Quota.IsTerminallyExhausted)
            throw SnapshotFailure("The exhausted recurring lease has nonterminal quota evidence.");
    }

    private static void EnsurePostStartClaim(RecurringLeaseExecutionSnapshot snapshot)
    {
        if (snapshot.Run.Status is not (RecordingRunStatus.Recording or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady) ||
            snapshot.Use.Status != LeaseUseStatus.Consumed ||
            snapshot.Occurrence.Status != PlanOccurrenceStatus.RunCreated)
        {
            throw LifecycleConflict("termination_not_expected");
        }
    }

    private static void RequireExactCommitted(RecurringLeaseExecutionSnapshot snapshot, string reason)
    {
        if (!IsExactCommitted(snapshot))
            throw LifecycleConflict(reason);
    }

    private static string BeforeFirstFrameReason(RecurringLeaseLifecycleTerminationKind kind) => kind switch
    {
        RecurringLeaseLifecycleTerminationKind.UserStop => "user_stop_before_first_frame",
        RecurringLeaseLifecycleTerminationKind.SafetyStop => "safety_stop_before_first_frame",
        RecurringLeaseLifecycleTerminationKind.SessionInterrupted => "session_interrupted_before_first_frame",
        RecurringLeaseLifecycleTerminationKind.LifecyclePersistenceFailure => "lifecycle_persistence_failure_before_first_frame",
        RecurringLeaseLifecycleTerminationKind.BackendStartFailure => "backend_start_failed_before_first_frame",
        _ => "natural_exit_before_first_frame",
    };

    private static bool TryReadValidOutput(
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

        if (!meta.OutputFileExists || meta.SizeBytes <= 0)
        {
            failureReason = "capture_output_missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(meta.OutputPath))
        {
            failureReason = "capture_output_path_invalid";
            return false;
        }

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

        var ticks = meta.DurationSeconds * TimeSpan.TicksPerSecond;
        if (!double.IsFinite(ticks) || ticks < 0 || ticks > TimeSpan.MaxValue.Ticks)
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        var roundedTicks = Math.Round(ticks, MidpointRounding.ToEven);
        if (!double.IsFinite(roundedTicks) || roundedTicks < 0 || roundedTicks > TimeSpan.MaxValue.Ticks)
        {
            failureReason = "capture_duration_invalid";
            return false;
        }

        try
        {
            actualDuration = TimeSpan.FromTicks(checked((long)roundedTicks));
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

        if (!IsAllowedGracefulStopReason(meta.StopReason) ||
            meta.StopReason is not null && !string.Equals(meta.StopReason, meta.StopReason.Trim(), StringComparison.Ordinal))
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

    private void InvokeFailureHook(RecurringLeaseLifecycleFailurePoint point) =>
        _failureHookForTest?.Invoke(point);

    private RecurringLeaseLifecycleActionResult Execute(
        Func<SqliteConnection, SqliteTransaction, RecurringLeaseLifecycleActionResult> operation)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var result = operation(connection, transaction);
            _beforeCommitForTest?.Invoke(connection, transaction);
            InvokeFailureHook(RecurringLeaseLifecycleFailurePoint.BeforeCommit);
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            throw;
        }
        catch (PersistedSnapshotException exception)
        {
            throw SnapshotFailure("The recurring lifecycle persisted snapshot is invalid.", exception);
        }
        catch (Phase3DomainException exception)
        {
            throw SnapshotFailure("The recurring lifecycle domain transition was rejected.", exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException(
                "constraint_violation",
                "The recurring lifecycle transaction violated a persisted constraint.",
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

    private static void UpdateRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordingRun run,
        long expectedVersion,
        RecordingRunStatus expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recording_runs
            SET status_code = $status_code, has_crossed_start_commit = $crossed,
                terminal_reason_code = $reason, updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE id = $id AND occurrence_id = $occurrence_id AND version = $expected_version
              AND status_code = $expected_status AND has_crossed_start_commit = $expected_crossed
              AND media_artifact_id IS $media_artifact_id AND bundle_id IS $bundle_id
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", run.StatusCode);
        Add(command, "$crossed", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$reason", run.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$new_version", run.Version);
        Add(command, "$id", run.Id);
        Add(command, "$occurrence_id", run.OccurrenceId);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_crossed", 1L);
        Add(command, "$media_artifact_id", run.MediaArtifactId);
        Add(command, "$bundle_id", run.BundleId);
        Add(command, "$created_at_utc", UtcTicksInput(run.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw LifecycleConflict("recurring_run_version_conflict");
    }

    private static void UpdateUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LeaseUse use,
        long expectedVersion,
        LeaseUseStatus expectedStatus,
        string planId,
        string occurrenceIdentity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_lease_uses
            SET status_code = $status_code, reserved_use_count = $reserved_use_count,
                reserved_duration_ticks = $reserved_duration_ticks,
                actual_settled_duration_ticks = $actual_settled_duration_ticks,
                updated_at_utc = $updated_at_utc, version = $new_version
            WHERE use_id = $use_id AND lease_id = $lease_id AND occurrence_id = $occurrence_id
              AND run_id = $run_id AND plan_id = $plan_id AND occurrence_identity = $occurrence_identity
              AND status_code = $expected_status AND version = $expected_version
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", use.StatusCode);
        Add(command, "$reserved_use_count", use.ReservedUseCount);
        Add(command, "$reserved_duration_ticks", use.ReservedDuration.Ticks);
        Add(command, "$actual_settled_duration_ticks", use.ActualSettledDuration?.Ticks);
        Add(command, "$updated_at_utc", UtcTicksInput(use.UpdatedAtUtc));
        Add(command, "$new_version", use.Version);
        Add(command, "$use_id", use.Id);
        Add(command, "$lease_id", use.LeaseId);
        Add(command, "$occurrence_id", use.OccurrenceId);
        Add(command, "$run_id", use.RunId);
        Add(command, "$plan_id", RequiredInput(planId));
        Add(command, "$occurrence_identity", RequiredInput(occurrenceIdentity));
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$created_at_utc", UtcTicksInput(use.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw LifecycleConflict("recurring_use_version_conflict");
    }

    private static void UpdateOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_occurrences
            SET status_code = $status_code, run_id = $run_id,
                terminal_reason_code = $reason, updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE id = $id AND plan_id = $plan_id AND run_id = $run_id
              AND status_code = 'run_created' AND terminal_reason_code IS NULL
              AND version = $expected_version AND window_start_utc = $window_start_utc
              AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$reason", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", occurrence.Id);
        Add(command, "$plan_id", occurrence.PlanId);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw LifecycleConflict("recurring_occurrence_version_conflict");
    }

    private static void UpdateLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        long expectedVersion,
        ConsentLeaseStatus expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_consent_leases
            SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version
            WHERE lease_id = $lease_id AND plan_id = $plan_id AND status_code = $expected_status
              AND version = $expected_version AND schedule_revision = $schedule_revision
              AND schedule_digest = $schedule_digest AND time_zone_rules_digest = $time_zone_rules_digest
              AND profile_id = $profile_id AND profile_version = $profile_version AND profile_digest = $profile_digest
              AND configuration_digest = $configuration_digest AND valid_from_utc = $valid_from_utc
              AND valid_until_utc = $valid_until_utc AND authorized_plan_latest_end_utc = $authorized_plan_latest_end_utc
              AND per_run_duration_ticks = $per_run_duration_ticks AND max_uses = $max_uses
              AND max_cumulative_duration_ticks = $max_cumulative_duration_ticks
              AND authorization_digest = $authorization_digest AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$lease_id", lease.LeaseId);
        Add(command, "$plan_id", lease.PlanId);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$schedule_revision", lease.ConfigurationRef.ScheduleRevision);
        Add(command, "$schedule_digest", lease.ConfigurationRef.ScheduleDigest);
        Add(command, "$time_zone_rules_digest", lease.ConfigurationRef.TimeZoneRulesDigest);
        Add(command, "$profile_id", lease.ConfigurationRef.ProfileRef.ProfileId);
        Add(command, "$profile_version", lease.ConfigurationRef.ProfileRef.ProfileVersion);
        Add(command, "$profile_digest", lease.ConfigurationRef.ProfileRef.ProfileDigest);
        Add(command, "$configuration_digest", lease.ConfigurationRef.ConfigurationDigest);
        Add(command, "$valid_from_utc", UtcTicksInput(lease.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(lease.ValidUntilUtc));
        Add(command, "$authorized_plan_latest_end_utc", UtcTicksInput(lease.AuthorizedPlanLatestEndUtc));
        Add(command, "$per_run_duration_ticks", lease.PerRunDuration.Ticks);
        Add(command, "$max_uses", lease.MaxUses);
        Add(command, "$max_cumulative_duration_ticks", lease.MaxCumulativeDuration.Ticks);
        Add(command, "$authorization_digest", lease.AuthorizationDigest);
        Add(command, "$created_at_utc", UtcTicksInput(lease.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw LifecycleConflict("recurring_lease_version_conflict");
    }

    private static void RequireChanged(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
            throw SnapshotFailure("The recurring lifecycle domain transition was not accepted.");
    }

    private static Phase3PersistenceException LifecycleConflict(string reason) =>
        new("lifecycle_conflict", reason);

    private static Phase3PersistenceException SnapshotFailure(string message, Exception? inner = null) =>
        new(RecurringPersistenceReasonCodes.PersistedDataInvalid, message, inner);
}
