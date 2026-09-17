using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringLeaseRestartRecoveryFailurePoint
{
    AfterRunUpdate,
    AfterUseUpdate,
    AfterOccurrenceUpdate,
    AfterLeaseExhaustionUpdate,
    BeforeFinalReadback,
    BeforeCommit,
}

/// <summary>
/// Reconciles one recurring post-start chain after a process restart. The
/// transaction has no capture/backend/proof input and never creates a new
/// claim. Every mutable row is re-read under the same immediate transaction.
/// </summary>
internal sealed class SqliteRecurringLeaseRestartRecoveryTransaction : SqliteRepositoryBase
{
    private readonly Action<RecurringLeaseRestartRecoveryFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteRecurringLeaseRestartRecoveryTransaction(
        SqliteOperationalStore store,
        Action<RecurringLeaseRestartRecoveryFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _failureHookForTest = failureHookForTest;
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal RecurringLeaseRestartRecoveryResult Reconcile(
        RecurringLeaseRestartRecoveryCandidate? candidate,
        string? currentUserSid,
        string? sessionBinding,
        DateTimeOffset nowUtc)
    {
        if (!TryValidateCandidate(candidate, out var candidateFailure))
            return RecurringLeaseRestartRecoveryResult.Rejected(candidateFailure, candidate);

        if (nowUtc.Offset != TimeSpan.Zero)
            return RecurringLeaseRestartRecoveryResult.Rejected("recovery_time_not_utc", candidate);

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var snapshot = LoadSnapshot(connection, transaction, candidate!);
            ValidateCandidateReadback(candidate!, snapshot);
            ValidateRecoveryContext(snapshot, currentUserSid, sessionBinding);
            ValidateTrustedRecoveryTime(snapshot, nowUtc);
            ValidateDurationAndQuotaEvidence(snapshot);

            if (!RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(
                    snapshot.Lease,
                    snapshot.Occurrence,
                    snapshot.Run,
                    snapshot.Use))
            {
                throw SnapshotFailure("The recurring restart snapshot is not a legal advanced chain.");
            }

            ValidateLeaseQuotaState(snapshot);

            if (IsTerminalChain(snapshot))
            {
                transaction.Commit();
                return RecurringLeaseRestartRecoveryResult.AlreadyReconciled(candidate!);
            }

            var (runStatus, reason) = RecoveryTarget(snapshot);
            if (!RecurringLifecycleTerminalReasonPolicy.IsKnownPostStart(reason))
                throw SnapshotFailure("The recurring recovery reason is outside the closed lifecycle policy.");
            var runVersion = snapshot.Run.Version;
            var useVersion = snapshot.Use.Version;
            var occurrenceVersion = snapshot.Occurrence.Version;
            var expectedRunStatus = snapshot.Run.Status;
            var expectedUseStatus = snapshot.Use.Status;

            RequireChanged(snapshot.Run.TryTransition(runStatus, nowUtc, reason));
            RequireChanged(snapshot.Use.TryTransition(LeaseUseStatus.StartedUnknown, nowUtc));
            RequireChanged(snapshot.Occurrence.TryTransition(PlanOccurrenceStatus.Blocked, nowUtc, reason));

            UpdateRun(connection, transaction, snapshot.Run, runVersion, expectedRunStatus);
            InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.AfterRunUpdate);
            UpdateUse(connection, transaction, snapshot.Use, useVersion, expectedUseStatus, snapshot.Plan.Id, snapshot.Specification.OccurrenceIdentity);
            InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.AfterUseUpdate);
            UpdateOccurrence(connection, transaction, snapshot.Occurrence, occurrenceVersion);
            InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.AfterOccurrenceUpdate);

            var postTransition = LoadSnapshot(connection, transaction, candidate!);
            EnsureRecoveredChain(postTransition, snapshot, runStatus, reason);
            ReconcileLeaseQuota(connection, transaction, postTransition, nowUtc);

            InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.BeforeFinalReadback);
            var final = LoadSnapshot(connection, transaction, candidate!);
            EnsureRecoveredChain(final, snapshot, runStatus, reason);
            ValidateLeaseQuotaState(final);
            _beforeCommitForTest?.Invoke(connection, transaction);
            InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.BeforeCommit);
            transaction.Commit();
            return RecurringLeaseRestartRecoveryResult.Recovered(candidate!, reason);
        }
        catch (Phase3PersistenceException exception)
        {
            return RecurringLeaseRestartRecoveryResult.Rejected(NormalizeFailure(exception.Code), candidate);
        }
        catch (PersistedSnapshotException)
        {
            return RecurringLeaseRestartRecoveryResult.Rejected("recovery_snapshot_invalid", candidate);
        }
        catch (Phase3DomainException)
        {
            return RecurringLeaseRestartRecoveryResult.Rejected("recovery_snapshot_invalid", candidate);
        }
        catch (SqliteException)
        {
            return RecurringLeaseRestartRecoveryResult.Rejected("recovery_sqlite_failure", candidate);
        }
        catch (Exception)
        {
            return RecurringLeaseRestartRecoveryResult.Rejected("recovery_sqlite_failure", candidate);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static RecurringLeaseExecutionSnapshot LoadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseRestartRecoveryCandidate candidate)
    {
        return SqliteRecurringLeaseExecutionSnapshotLoader.ReadExactSnapshotWithinTransaction(
            connection,
            transaction,
            new RecurringLeaseExecutionSnapshotLookup(
                candidate.LeaseId,
                candidate.PlanId,
                candidate.OccurrenceIdentity,
                candidate.OccurrenceId,
                candidate.RunId,
                candidate.UseId));
    }

    private static void ValidateCandidateReadback(
        RecurringLeaseRestartRecoveryCandidate candidate,
        RecurringLeaseExecutionSnapshot snapshot)
    {
        if (!string.Equals(candidate.PlanId, snapshot.Plan.Id, StringComparison.Ordinal) ||
            !string.Equals(candidate.LeaseId, snapshot.Lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(candidate.OccurrenceIdentity, snapshot.Specification.OccurrenceIdentity, StringComparison.Ordinal) ||
            !string.Equals(candidate.OccurrenceId, snapshot.Occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(candidate.RunId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(candidate.UseId, snapshot.Use.Id, StringComparison.Ordinal) ||
            !string.Equals(candidate.RunStatusCode, snapshot.Run.StatusCode, StringComparison.Ordinal) ||
            !string.Equals(candidate.UseStatusCode, snapshot.Use.StatusCode, StringComparison.Ordinal) ||
            !string.Equals(candidate.OccurrenceStatusCode, snapshot.Occurrence.StatusCode, StringComparison.Ordinal) ||
            candidate.RunVersion != snapshot.Run.Version ||
            candidate.UseVersion != snapshot.Use.Version ||
            candidate.OccurrenceVersion != snapshot.Occurrence.Version ||
            candidate.LeaseVersion != snapshot.Lease.Version ||
            candidate.PlanVersion != snapshot.Plan.Version)
        {
            throw RecoveryConflict("recovery_concurrency_conflict");
        }
    }

    private static void ValidateRecoveryContext(
        RecurringLeaseExecutionSnapshot snapshot,
        string? currentUserSid,
        string? sessionBinding)
    {
        if (!IsCanonicalId(currentUserSid) ||
            !IsCanonicalId(sessionBinding) ||
            !string.Equals(snapshot.Specification.ApprovedCurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Specification.ApprovedSessionBinding, sessionBinding, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Approval.CurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Approval.SessionBinding, sessionBinding, StringComparison.Ordinal))
        {
            throw RecoveryConflict("recovery_identity_mismatch");
        }
    }

    private static bool TryValidateCandidate(
        RecurringLeaseRestartRecoveryCandidate? candidate,
        out string failureReason)
    {
        failureReason = "recovery_candidate_invalid";
        if (candidate is null ||
            !IsCanonicalId(candidate.PlanId) ||
            !IsCanonicalId(candidate.LeaseId) ||
            !IsCanonicalId(candidate.OccurrenceIdentity) ||
            !IsCanonicalId(candidate.OccurrenceId) ||
            !IsCanonicalId(candidate.RunId) ||
            !IsCanonicalId(candidate.UseId) ||
            !IsFiniteVersion(candidate.RunVersion) ||
            !IsFiniteVersion(candidate.UseVersion) ||
            !IsFiniteVersion(candidate.OccurrenceVersion) ||
            !IsFiniteVersion(candidate.LeaseVersion) ||
            !IsFiniteVersion(candidate.PlanVersion) ||
            !Phase3StateCodes.TryParseRecordingRun(candidate.RunStatusCode, out _) ||
            !Phase3StateCodes.TryParseLeaseUse(candidate.UseStatusCode, out _) ||
            !Phase3StateCodes.TryParsePlanOccurrence(candidate.OccurrenceStatusCode, out _))
        {
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static void ValidateTrustedRecoveryTime(
        RecurringLeaseExecutionSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (nowUtc < snapshot.Plan.UpdatedAtUtc ||
            nowUtc < snapshot.Lease.UpdatedAtUtc ||
            nowUtc < snapshot.Occurrence.UpdatedAtUtc ||
            nowUtc < snapshot.Run.UpdatedAtUtc ||
            nowUtc < snapshot.Use.UpdatedAtUtc)
        {
            throw RecoveryConflict("recovery_time_non_monotonic");
        }
    }

    private static void ValidateDurationAndQuotaEvidence(RecurringLeaseExecutionSnapshot snapshot)
    {
        try
        {
            _ = checked(snapshot.Specification.EvaluatedAtUtc.UtcDateTime.Ticks + snapshot.Specification.Duration.Ticks);
            _ = checked(snapshot.Run.CreatedAtUtc.UtcDateTime.Ticks + snapshot.Lease.PerRunDuration.Ticks);
        }
        catch (OverflowException exception)
        {
            throw SnapshotFailure("The recurring restart duration evidence overflowed.", exception);
        }

        if (snapshot.Run.CreatedAtUtc < snapshot.Occurrence.WindowStartUtc ||
            snapshot.Run.CreatedAtUtc >= snapshot.Occurrence.WindowEndUtc ||
            snapshot.Run.CreatedAtUtc < snapshot.Lease.ValidFromUtc ||
            snapshot.Run.CreatedAtUtc >= snapshot.Lease.ValidUntilUtc ||
            snapshot.Specification.Duration <= TimeSpan.Zero ||
            snapshot.Specification.Duration != snapshot.Lease.PerRunDuration ||
            snapshot.Specification.PlannedEndUtc < snapshot.Specification.EvaluatedAtUtc.Add(snapshot.Specification.Duration))
        {
            throw SnapshotFailure("The recurring restart duration or time evidence is invalid.");
        }
    }

    private static void ValidateLeaseQuotaState(RecurringLeaseExecutionSnapshot snapshot)
    {
        switch (snapshot.Lease.Status)
        {
            case ConsentLeaseStatus.Active when snapshot.Quota.IsTerminallyExhausted:
                throw SnapshotFailure("The active recurring lease has terminal quota evidence.");
            case ConsentLeaseStatus.Exhausted when !snapshot.Quota.IsTerminallyExhausted:
                throw SnapshotFailure("The exhausted recurring lease has nonterminal quota evidence.");
            case ConsentLeaseStatus.Pending:
            case ConsentLeaseStatus.Rejected:
                throw RecoveryConflict("recurring_lease_not_executable");
        }
    }

    private static (RecordingRunStatus RunStatus, string Reason) RecoveryTarget(
        RecurringLeaseExecutionSnapshot snapshot) =>
        (snapshot.Run.Status, snapshot.Use.Status, snapshot.Occurrence.Status) switch
        {
            (RecordingRunStatus.StartCommitted, LeaseUseStatus.StartCommitted, PlanOccurrenceStatus.RunCreated) =>
                (RecordingRunStatus.StartedUnknown, "recovery_after_start_commit"),
            (RecordingRunStatus.Recording, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated) =>
                (RecordingRunStatus.SessionInterrupted, "recovery_after_recording_interrupted"),
            (RecordingRunStatus.Finalizing, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated) =>
                (RecordingRunStatus.SessionInterrupted, "recovery_during_finalization"),
            (RecordingRunStatus.MediaReady, LeaseUseStatus.Consumed, PlanOccurrenceStatus.RunCreated) =>
                (RecordingRunStatus.SessionInterrupted, "recovery_before_settlement"),
            _ => throw RecoveryConflict("recovery_state_combination_invalid"),
        };

    private static bool IsTerminalChain(RecurringLeaseExecutionSnapshot snapshot) =>
        (snapshot.Occurrence.Status == PlanOccurrenceStatus.Completed &&
            snapshot.Run.Status == RecordingRunStatus.Settled &&
            snapshot.Use.Status == LeaseUseStatus.Settled) ||
        (snapshot.Occurrence.Status == PlanOccurrenceStatus.Blocked &&
            snapshot.Run.Status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed &&
            snapshot.Use.Status == LeaseUseStatus.StartedUnknown);

    private static void EnsureRecoveredChain(
        RecurringLeaseExecutionSnapshot current,
        RecurringLeaseExecutionSnapshot original,
        RecordingRunStatus expectedRunStatus,
        string reason)
    {
        if (!RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(
                current.Lease,
                current.Occurrence,
                current.Run,
                current.Use) ||
            current.Run.Status != expectedRunStatus ||
            current.Use.Status != LeaseUseStatus.StartedUnknown ||
            current.Occurrence.Status != PlanOccurrenceStatus.Blocked ||
            !string.Equals(current.Run.TerminalReasonCode, reason, StringComparison.Ordinal) ||
            !string.Equals(current.Occurrence.TerminalReasonCode, reason, StringComparison.Ordinal) ||
            current.Run.Id != original.Run.Id ||
            current.Use.Id != original.Use.Id ||
            current.Occurrence.Id != original.Occurrence.Id)
        {
            throw SnapshotFailure("The recurring restart final readback was not exact.");
        }
    }

    private void ReconcileLeaseQuota(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseExecutionSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot.Lease.Status != ConsentLeaseStatus.Active)
            return;

        if (!snapshot.Quota.IsTerminallyExhausted)
            return;

        var expectedVersion = snapshot.Lease.Version;
        RequireChanged(snapshot.Lease.TryMarkExhausted(snapshot.Quota, nowUtc));
        UpdateLease(connection, transaction, snapshot.Lease, expectedVersion, ConsentLeaseStatus.Active);
        InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint.AfterLeaseExhaustionUpdate);
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
            SET status_code = $status_code,
                has_crossed_start_commit = $crossed,
                terminal_reason_code = $reason,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE id = $id
              AND occurrence_id = $occurrence_id
              AND status_code = $expected_status
              AND has_crossed_start_commit = 1
              AND version = $expected_version
              AND media_artifact_id IS NULL
              AND bundle_id IS NULL
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", run.StatusCode);
        Add(command, "$crossed", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$reason", run.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$new_version", run.Version);
        Add(command, "$id", run.Id);
        Add(command, "$occurrence_id", run.OccurrenceId);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$created_at_utc", UtcTicksInput(run.CreatedAtUtc));
        EnsureOneRow(command, "recovery_run_version_conflict");
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
            SET status_code = $status_code,
                reserved_use_count = $reserved_use_count,
                reserved_duration_ticks = $reserved_duration_ticks,
                actual_settled_duration_ticks = $actual_settled_duration_ticks,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE use_id = $use_id
              AND lease_id = $lease_id
              AND plan_id = $plan_id
              AND occurrence_identity = $occurrence_identity
              AND occurrence_id = $occurrence_id
              AND run_id = $run_id
              AND status_code = $expected_status
              AND version = $expected_version
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
        Add(command, "$plan_id", RequiredInput(planId));
        Add(command, "$occurrence_identity", RequiredInput(occurrenceIdentity));
        Add(command, "$occurrence_id", use.OccurrenceId);
        Add(command, "$run_id", use.RunId);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$created_at_utc", UtcTicksInput(use.CreatedAtUtc));
        EnsureOneRow(command, "recovery_use_version_conflict");
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
            SET status_code = $status_code,
                run_id = $run_id,
                terminal_reason_code = $reason,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE id = $id
              AND plan_id = $plan_id
              AND run_id = $run_id
              AND status_code = 'run_created'
              AND terminal_reason_code IS NULL
              AND version = $expected_version
              AND window_start_utc = $window_start_utc
              AND window_end_utc = $window_end_utc
              AND created_at_utc = $created_at_utc;
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
        EnsureOneRow(command, "recovery_occurrence_version_conflict");
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
            SET status_code = $status_code,
                updated_at_utc = $updated_at_utc,
                version = $new_version
            WHERE lease_id = $lease_id
              AND plan_id = $plan_id
              AND status_code = $expected_status
              AND version = $expected_version
              AND schedule_revision = $schedule_revision
              AND schedule_digest = $schedule_digest
              AND time_zone_rules_digest = $time_zone_rules_digest
              AND profile_id = $profile_id
              AND profile_version = $profile_version
              AND profile_digest = $profile_digest
              AND configuration_digest = $configuration_digest
              AND valid_from_utc = $valid_from_utc
              AND valid_until_utc = $valid_until_utc
              AND authorized_plan_latest_end_utc = $authorized_plan_latest_end_utc
              AND per_run_duration_ticks = $per_run_duration_ticks
              AND max_uses = $max_uses
              AND max_cumulative_duration_ticks = $max_cumulative_duration_ticks
              AND authorization_digest = $authorization_digest
              AND created_at_utc = $created_at_utc;
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
        EnsureOneRow(command, "recovery_lease_version_conflict");
    }

    private void InvokeFailureHook(RecurringLeaseRestartRecoveryFailurePoint point) =>
        _failureHookForTest?.Invoke(point);

    private static void EnsureOneRow(SqliteCommand command, string reason)
    {
        if (command.ExecuteNonQuery() != 1)
            throw RecoveryConflict(reason);
    }

    private static void RequireChanged(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
            throw SnapshotFailure("The recurring restart transition was not accepted.");
    }

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsFiniteVersion(long value) => value >= 0 && value < long.MaxValue;

    private static string NormalizeFailure(string code) => code switch
    {
        "recovery_concurrency_conflict" or
        "recovery_time_non_monotonic" or
        "recovery_run_version_conflict" or
        "recovery_use_version_conflict" or
        "recovery_occurrence_version_conflict" or
        "recovery_lease_version_conflict" => "recovery_concurrency_conflict",
        "recovery_sqlite_failure" or "sqlite_failure" or "constraint_violation" => "recovery_sqlite_failure",
        _ => "recovery_snapshot_invalid",
    };

    private static Phase3PersistenceException RecoveryConflict(string reason) =>
        new("recovery_concurrency_conflict", reason);

    private static Phase3PersistenceException SnapshotFailure(string message, Exception? inner = null) =>
        new("recovery_snapshot_invalid", message, inner);
}

internal enum RecurringLeaseRestartRecoveryStatus
{
    Recovered,
    AlreadyReconciled,
    Rejected,
}

internal sealed class RecurringLeaseRestartRecoveryResult
{
    private RecurringLeaseRestartRecoveryResult(
        RecurringLeaseRestartRecoveryStatus status,
        string reason,
        string? planId,
        string? leaseId,
        string? occurrenceIdentity,
        string? occurrenceId,
        string? runId,
        string? useId,
        bool changed)
    {
        Status = status;
        Reason = reason;
        PlanId = planId;
        LeaseId = leaseId;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        RunId = runId;
        UseId = useId;
        Changed = changed;
    }

    internal RecurringLeaseRestartRecoveryStatus Status { get; }
    internal string Reason { get; }
    internal string? PlanId { get; }
    internal string? LeaseId { get; }
    internal string? OccurrenceIdentity { get; }
    internal string? OccurrenceId { get; }
    internal string? RunId { get; }
    internal string? UseId { get; }
    internal bool Changed { get; }
    internal bool Succeeded => Status is RecurringLeaseRestartRecoveryStatus.Recovered or RecurringLeaseRestartRecoveryStatus.AlreadyReconciled;

    internal static RecurringLeaseRestartRecoveryResult Recovered(
        RecurringLeaseRestartRecoveryCandidate candidate,
        string reason) =>
        FromCandidate(RecurringLeaseRestartRecoveryStatus.Recovered, reason, candidate, changed: true);

    internal static RecurringLeaseRestartRecoveryResult AlreadyReconciled(
        RecurringLeaseRestartRecoveryCandidate candidate) =>
        FromCandidate(RecurringLeaseRestartRecoveryStatus.AlreadyReconciled, "recovery_idempotent_noop", candidate, changed: false);

    internal static RecurringLeaseRestartRecoveryResult Rejected(
        string reason,
        RecurringLeaseRestartRecoveryCandidate? candidate = null) =>
        candidate is null
            ? new(RecurringLeaseRestartRecoveryStatus.Rejected, reason, null, null, null, null, null, null, changed: false)
            : FromCandidate(RecurringLeaseRestartRecoveryStatus.Rejected, reason, candidate, changed: false);

    private static RecurringLeaseRestartRecoveryResult FromCandidate(
        RecurringLeaseRestartRecoveryStatus status,
        string reason,
        RecurringLeaseRestartRecoveryCandidate candidate,
        bool changed) =>
        new(status, reason, candidate.PlanId, candidate.LeaseId, candidate.OccurrenceIdentity, candidate.OccurrenceId, candidate.RunId, candidate.UseId, changed);
}
