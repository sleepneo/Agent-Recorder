using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringOccurrenceStartCommitStatus
{
    Committed,
    CommittedWithoutProof,
    AlreadyCommitted,
    AlreadyAdvanced,
    Blocked,
    AlreadyBlocked,
    Rejected,
    Conflict,
}

internal enum RecurringOccurrenceStartCommitFailurePoint
{
    AfterRunPreparingUpdate,
    AfterRunStartCommitUpdate,
    AfterUseUpdate,
    AfterLeaseExhaustionUpdate,
    AfterBlockedRunUpdate,
    AfterBlockedUseUpdate,
    AfterBlockedOccurrenceUpdate,
    BeforeFinalReadback,
    BeforeCommit,
}

internal static class RecurringOccurrenceStartCommitReasonCodes
{
    internal const string RequestInvalid = "recurring_start_commit_request_invalid";
    internal const string ClockInvalid = "recurring_start_commit_clock_invalid";
    internal const string ClockMovedBackwards = "recurring_start_commit_clock_moved_backwards";
    internal const string PersistedSnapshotInvalid = "recurring_start_commit_persisted_snapshot_invalid";
    internal const string TransactionFailed = "recurring_start_commit_transaction_failed";
    internal const string Conflict = "recurring_start_commit_conflict";
    internal const string NotReserved = "recurring_start_commit_not_reserved";
    internal const string Committed = "recurring_start_committed";
    internal const string AlreadyCommitted = "recurring_start_already_committed";
    internal const string AlreadyAdvanced = "recurring_start_already_advanced";
    internal const string Blocked = "recurring_start_blocked";
    internal const string AlreadyBlocked = "recurring_start_already_blocked";
    internal const string ProofUnavailableAfterCommit = "recurring_start_commit_proof_unavailable_after_commit";
    internal const string PlanNotEnabled = "recurring_start_commit_plan_not_enabled";
    internal const string LeasePending = "recurring_start_commit_lease_pending";
    internal const string LeaseRejected = "recurring_start_commit_lease_rejected";
    internal const string LeaseRevoked = "recurring_start_commit_lease_revoked";
    internal const string LeaseExpired = "recurring_start_commit_lease_expired";
    internal const string LeaseExhausted = "recurring_start_commit_lease_exhausted";
    internal const string UnattendedDisabled = "unattended_disabled";
    internal const string StopAllBoundary = "stop_all_boundary";
    internal const string ReenableRequiresNewAuthorization = "unattended_reenable_requires_new_authorization";
    internal const string ApprovalMissing = "recurring_start_commit_approval_missing";
    internal const string ApprovalMismatch = "recurring_start_commit_approval_mismatch";
    internal const string BeforeScheduledStart = "recurring_start_commit_before_scheduled_start";
    internal const string BeforeLeaseValidity = "recurring_start_commit_before_lease_validity";
    internal const string AfterLatestStart = "recurring_start_commit_after_latest_start";
    internal const string DurationExceedsPlannedEnd = "recurring_start_commit_duration_exceeds_planned_end";
    internal const string DurationExceedsLease = "recurring_start_commit_duration_exceeds_lease";
    internal const string CurrentTimeInvalid = "recurring_start_commit_current_time_invalid";
    internal const string EnvironmentUnavailable = "execution_environment_unavailable";
}

internal sealed class RecurringOccurrenceStartCommitResult
{
    private RecurringOccurrenceStartCommitResult(
        RecurringOccurrenceStartCommitStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId,
        string? runId,
        string? useId,
        bool changed,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary,
        RecurringStartCommitReceipt? firstCommitReceipt,
        RecurringLeaseUseProof? firstCommitProof,
        RecurringStartCommitReceipt? postCommitRecoveryReceipt)
    {
        Status = status;
        ReasonCode = reasonCode;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        RunId = runId;
        UseId = useId;
        Changed = changed;
        SpecificationSummary = specificationSummary;
        FirstCommitReceipt = firstCommitReceipt;
        FirstCommitProof = firstCommitProof;
        PostCommitRecoveryReceipt = postCommitRecoveryReceipt;
    }

    internal RecurringOccurrenceStartCommitStatus Status { get; }
    internal string StatusCode => Status.ToString();
    internal string ResultCode => StatusCode;
    internal string ReasonCode { get; }
    internal string Reason => ReasonCode;
    internal string OccurrenceIdentity { get; }
    internal string? OccurrenceId { get; }
    internal string? RunId { get; }
    internal string? UseId { get; }
    internal bool Changed { get; }
    internal bool Succeeded => Status is RecurringOccurrenceStartCommitStatus.Committed or
        RecurringOccurrenceStartCommitStatus.AlreadyCommitted or
        RecurringOccurrenceStartCommitStatus.AlreadyAdvanced;
    internal bool CommittedNow => Status is RecurringOccurrenceStartCommitStatus.Committed or
        RecurringOccurrenceStartCommitStatus.CommittedWithoutProof;
    internal bool HasFirstCommitProof => Status == RecurringOccurrenceStartCommitStatus.Committed && FirstCommitProof is not null;
    internal bool CanStartCapture => Status == RecurringOccurrenceStartCommitStatus.Committed && HasFirstCommitProof;
    internal bool CanIssueProof => HasFirstCommitProof;
    internal RecurringOccurrenceExecutionSpecificationSummary? SpecificationSummary { get; }
    internal string? SpecificationDigest => SpecificationSummary?.SpecificationDigest;
    internal RecurringStartCommitReceipt? FirstCommitReceipt { get; }
    internal RecurringLeaseUseProof? FirstCommitProof { get; }
    
    // Kept separate from FirstCommitReceipt for compatibility with the
    // existing proof-publication contract: a proof issuer failure still
    // needs the immutable post-commit identity chain for exact recovery, but
    // it must not look like a successfully published first receipt.
    internal RecurringStartCommitReceipt? PostCommitRecoveryReceipt { get; }

    internal static RecurringOccurrenceStartCommitResult Create(
        RecurringOccurrenceStartCommitStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId = null,
        string? runId = null,
        string? useId = null,
        bool changed = false,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary = null,
        RecurringStartCommitReceipt? firstCommitReceipt = null,
        RecurringLeaseUseProof? firstCommitProof = null,
        RecurringStartCommitReceipt? postCommitRecoveryReceipt = null) =>
        new(status, reasonCode, occurrenceIdentity, occurrenceId, runId, useId, changed, specificationSummary, firstCommitReceipt, firstCommitProof, postCommitRecoveryReceipt);
}

internal enum RecurringFinalRecheckDecisionKind
{
    Eligible,
    Blocked,
    Rejected,
}

internal sealed record RecurringFinalRecheckDecision(
    RecurringFinalRecheckDecisionKind Kind,
    string ReasonCode)
{
    internal static RecurringFinalRecheckDecision Eligible() =>
        new(RecurringFinalRecheckDecisionKind.Eligible, string.Empty);

    internal static RecurringFinalRecheckDecision Blocked(string reasonCode) =>
        new(RecurringFinalRecheckDecisionKind.Blocked, reasonCode);

    internal static RecurringFinalRecheckDecision Rejected(string reasonCode) =>
        new(RecurringFinalRecheckDecisionKind.Rejected, reasonCode);
}

/// <summary>
/// Owns the recurring execution start-commit boundary. The production entry
/// deliberately accepts only the lease and occurrence business identities;
/// time, claims, specification, environment and versions stay inside this
/// service and its immediate SQLite transaction.
/// </summary>
internal sealed class RecurringOccurrenceStartCommitService
{
    private readonly SqliteOperationalStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProviderForTest;
    private readonly Action<RecurringOccurrenceStartCommitFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitHookForTest;
    private readonly Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? _recurringProofIssuerForTest;
    private readonly object _clockGate = new();
    private DateTimeOffset? _lastTrustedNowUtc;

    internal RecurringOccurrenceStartCommitService(
        SqliteOperationalStore store,
        Func<DateTimeOffset>? utcNowForTest = null,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        Action<RecurringOccurrenceStartCommitFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitHookForTest = null,
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? recurringProofIssuerForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _environmentProviderForTest = environmentProviderForTest;
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
        _beforeCommitHookForTest = beforeCommitHookForTest;
        _recurringProofIssuerForTest = recurringProofIssuerForTest;
    }

    internal RecurringOccurrenceStartCommitResult Commit(string leaseId, string occurrenceIdentity)
    {
        if (!IsCanonicalInput(leaseId) || !IsCanonicalInput(occurrenceIdentity))
        {
            return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                RecurringOccurrenceStartCommitReasonCodes.RequestInvalid, occurrenceIdentity ?? "");
        }

        DateTimeOffset trustedNowUtc;
        try
        {
            lock (_clockGate)
            {
                trustedNowUtc = _utcNow();
                if (trustedNowUtc.Offset != TimeSpan.Zero)
                    return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                        RecurringOccurrenceStartCommitReasonCodes.ClockInvalid, occurrenceIdentity);
                if (_lastTrustedNowUtc is { } previous && trustedNowUtc < previous)
                    return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                        RecurringOccurrenceStartCommitReasonCodes.ClockMovedBackwards, occurrenceIdentity);
                _lastTrustedNowUtc = trustedNowUtc;
            }
        }
        catch
        {
            return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                RecurringOccurrenceStartCommitReasonCodes.ClockInvalid, occurrenceIdentity);
        }

        return new SqliteRecurringOccurrenceStartCommitTransaction(
            _store,
            _environmentProviderForTest,
            _failureHookForTest,
            _beforeFinalReadbackHookForTest,
            _beforeCommitHookForTest,
            _recurringProofIssuerForTest).Commit(leaseId, occurrenceIdentity, trustedNowUtc);
    }

    private static bool IsCanonicalInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static RecurringOccurrenceStartCommitResult Result(
        RecurringOccurrenceStartCommitStatus status,
        string reason,
        string identity) =>
        RecurringOccurrenceStartCommitResult.Create(status, reason, identity);
}

internal sealed class SqliteRecurringOccurrenceStartCommitTransaction : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";

    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProviderForTest;
    private readonly Action<RecurringOccurrenceStartCommitFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitHookForTest;
    private readonly Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? _recurringProofIssuerForTest;

    internal SqliteRecurringOccurrenceStartCommitTransaction(
        SqliteOperationalStore store,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        Action<RecurringOccurrenceStartCommitFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitHookForTest = null,
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? recurringProofIssuerForTest = null)
        : base(store)
    {
        _environmentProviderForTest = environmentProviderForTest;
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
        _beforeCommitHookForTest = beforeCommitHookForTest;
        _recurringProofIssuerForTest = recurringProofIssuerForTest;
    }

    internal RecurringOccurrenceStartCommitResult Commit(
        string leaseId,
        string occurrenceIdentity,
        DateTimeOffset trustedNowUtc)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            ValidateInputs(leaseId, occurrenceIdentity, trustedNowUtc);
            transaction = BeginWriteTransaction(connection);

            var snapshot = ReadSnapshot(connection, transaction, leaseId, occurrenceIdentity);
            ValidateSpecification(snapshot);

            var replay = EvaluateReplay(snapshot, occurrenceIdentity);
            if (replay is not null)
            {
                transaction.Commit();
                return replay;
            }

            if (!IsInitialReservationChain(snapshot))
            {
                transaction.Commit();
                return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                    snapshot.HasAnyClaim ? RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid :
                        RecurringOccurrenceStartCommitReasonCodes.NotReserved,
                    occurrenceIdentity,
                    snapshot.Occurrence.Id,
                    snapshot.Occurrence.RunId,
                    snapshot.Use?.Raw.UseId,
                    specification: snapshot.Specification);
            }

            var lifecycleDecision = EvaluateFinalRecheck(snapshot, trustedNowUtc);
            if (lifecycleDecision.Kind == RecurringFinalRecheckDecisionKind.Rejected)
            {
                TryRollback(transaction);
                return Result(
                    RecurringOccurrenceStartCommitStatus.Rejected,
                    lifecycleDecision.ReasonCode,
                    occurrenceIdentity,
                    snapshot.Occurrence.Id,
                    snapshot.Occurrence.RunId,
                    snapshot.Use?.Raw.UseId,
                    specification: snapshot.Specification);
            }

            if (lifecycleDecision.Kind == RecurringFinalRecheckDecisionKind.Blocked)
            {
                var blocked = TerminalizeBlocked(connection, transaction, snapshot, trustedNowUtc, lifecycleDecision.ReasonCode);
                transaction.Commit();
                return blocked;
            }

            var requirements = FixedRegionExecutionEnvironmentRequirements.FromRecurringSpecification(snapshot.Specification!);
            StandingLeaseExecutionEnvironment? environment;
            try
            {
                environment = (_environmentProviderForTest ?? SystemQueryRecurringOccurrenceEnvironmentProvider.Instance)
                    .Capture(new RecurringOccurrenceEnvironmentCaptureRequest(requirements, trustedNowUtc));
            }
            catch
            {
                environment = null;
            }

            if (environment is null ||
                environment.NowUtc.Offset != TimeSpan.Zero ||
                environment.NowUtc.UtcDateTime.Ticks != trustedNowUtc.UtcDateTime.Ticks)
            {
                var blocked = TerminalizeBlocked(connection, transaction, snapshot, trustedNowUtc,
                    RecurringOccurrenceStartCommitReasonCodes.EnvironmentUnavailable);
                transaction.Commit();
                return blocked;
            }

            if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
                    environment, requirements, out var environmentFailure))
            {
                var blocked = TerminalizeBlocked(connection, transaction, snapshot, trustedNowUtc, environmentFailure);
                transaction.Commit();
                return blocked;
            }

            var committed = CommitInitialChain(connection, transaction, snapshot, trustedNowUtc);
            _beforeFinalReadbackHookForTest?.Invoke(connection, transaction);
            _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.BeforeFinalReadback);
            var final = ReadAndValidateFinalState(
                connection, transaction, snapshot, trustedNowUtc, committed, environment);
            _beforeCommitHookForTest?.Invoke(connection, transaction);
            _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.BeforeCommit);
            transaction.Commit();

            // The database commit is the linearization point. Receipt and
            // proof construction happen only after it; an issuer failure
            // therefore cannot be reported as a rollback or reopen the
            // committed occurrence for a later proof retry.
            RecurringStartCommitReceipt? postCommitRecoveryReceipt = null;
            try
            {
                postCommitRecoveryReceipt = new RecurringStartCommitReceipt(
                    final.Plan,
                    final.Occurrence,
                    final.Lease,
                    final.Specification,
                    final.Run,
                    RehydrateLeaseUse(final.Use.Raw),
                    final.Approval,
                    trustedNowUtc,
                    final.Quota,
                    final.LeaseEntries);
                var proof = _recurringProofIssuerForTest is null
                    ? CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(postCommitRecoveryReceipt)
                    : _recurringProofIssuerForTest(postCommitRecoveryReceipt);
                if (proof is null)
                    throw new InvalidOperationException("The recurring proof issuer returned no proof.");

                return Result(
                    RecurringOccurrenceStartCommitStatus.Committed,
                    RecurringOccurrenceStartCommitReasonCodes.Committed,
                    occurrenceIdentity,
                    final.Occurrence.Id,
                    final.Run.Id,
                    final.Use.Raw.UseId,
                    changed: true,
                    specification: final.Specification,
                    firstCommitReceipt: postCommitRecoveryReceipt,
                    firstCommitProof: proof);
            }
            catch
            {
                return Result(
                    RecurringOccurrenceStartCommitStatus.CommittedWithoutProof,
                    RecurringOccurrenceStartCommitReasonCodes.ProofUnavailableAfterCommit,
                    occurrenceIdentity,
                    final.Occurrence.Id,
                    final.Run.Id,
                    final.Use.Raw.UseId,
                    changed: true,
                    specification: final.Specification,
                    postCommitRecoveryReceipt: postCommitRecoveryReceipt);
            }
        }
        catch (Phase3PersistenceException exception)
        {
            TryRollback(transaction);
            return MapFailure(occurrenceIdentity, exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            return Result(RecurringOccurrenceStartCommitStatus.Conflict,
                RecurringOccurrenceStartCommitReasonCodes.Conflict, occurrenceIdentity);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or
            Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            TryRollback(transaction);
            return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, occurrenceIdentity);
        }
        catch
        {
            TryRollback(transaction);
            return Result(RecurringOccurrenceStartCommitStatus.Rejected,
                RecurringOccurrenceStartCommitReasonCodes.TransactionFailed, occurrenceIdentity);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static void ValidateInputs(string leaseId, string occurrenceIdentity, DateTimeOffset trustedNowUtc)
    {
        if (!IsCanonical(leaseId) || !IsCanonical(occurrenceIdentity) || trustedNowUtc.Offset != TimeSpan.Zero)
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.RequestInvalid, "The recurring start-commit inputs are not canonical.");
    }

    private static bool IsCanonical(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static void ValidateSpecification(StartCommitSnapshot snapshot)
    {
        if (snapshot.Specification is null)
            throw SnapshotFailure("The exact recurring execution specification was not found.");

        SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
            snapshot.Specification,
            snapshot.Plan,
            snapshot.Schedule,
            snapshot.Binding,
            snapshot.Profile,
            snapshot.Lease,
            snapshot.Approval,
            snapshot.Candidate,
            snapshot.Occurrence);
    }

    private static RecurringOccurrenceStartCommitResult? EvaluateReplay(
        StartCommitSnapshot snapshot,
        string occurrenceIdentity)
    {
        if (!snapshot.HasAnyClaim)
            return null;

        if (snapshot.Run is null || snapshot.Use is null || snapshot.Occurrence.RunId is null ||
            !string.Equals(snapshot.Occurrence.RunId, snapshot.Run.Id, StringComparison.Ordinal) ||
            snapshot.Occurrence.Status is not (PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed or PlanOccurrenceStatus.Blocked))
        {
            throw SnapshotFailure("The recurring start-commit claim chain is incomplete.");
        }

        ValidateClaimRelations(snapshot);

        if (IsExactBlockedChain(snapshot))
        {
            return Result(
                RecurringOccurrenceStartCommitStatus.AlreadyBlocked,
                RecurringOccurrenceStartCommitReasonCodes.AlreadyBlocked,
                occurrenceIdentity,
                snapshot.Occurrence.Id,
                snapshot.Run.Id,
                snapshot.Use.Raw.UseId,
                specification: snapshot.Specification);
        }

        if (IsExactCommittedChain(snapshot))
        {
            ValidateReplayQuotaAndLease(snapshot);
            return Result(
                RecurringOccurrenceStartCommitStatus.AlreadyCommitted,
                RecurringOccurrenceStartCommitReasonCodes.AlreadyCommitted,
                occurrenceIdentity,
                snapshot.Occurrence.Id,
                snapshot.Run.Id,
                snapshot.Use.Raw.UseId,
                specification: snapshot.Specification);
        }

        if (IsLegalAdvancedChain(snapshot))
        {
            ValidateReplayQuotaAndLease(snapshot);
            return Result(
                RecurringOccurrenceStartCommitStatus.AlreadyAdvanced,
                RecurringOccurrenceStartCommitReasonCodes.AlreadyAdvanced,
                occurrenceIdentity,
                snapshot.Occurrence.Id,
                snapshot.Run.Id,
                snapshot.Use.Raw.UseId,
                specification: snapshot.Specification);
        }

        if (snapshot.Occurrence.Status == PlanOccurrenceStatus.RunCreated &&
            snapshot.Run.Status == RecordingRunStatus.Created &&
            snapshot.Use.Entry.Status == LeaseUseStatus.Reserved)
        {
            return null;
        }

        throw SnapshotFailure("The recurring start-commit chain is not a legal persisted state.");
    }

    private static bool IsInitialReservationChain(StartCommitSnapshot snapshot) =>
        snapshot.Run is not null && snapshot.Use is not null &&
        RecurringReplayIntegrityPolicy.IsInitialReservationChain(
            snapshot.Lease,
            snapshot.Occurrence,
            snapshot.Run,
            RehydrateLeaseUse(snapshot.Use.Raw));

    private static bool IsExactCommittedChain(StartCommitSnapshot snapshot) =>
        snapshot.Run is not null && snapshot.Use is not null &&
        RecurringReplayIntegrityPolicy.IsExactCommittedChain(
            snapshot.Lease,
            snapshot.Occurrence,
            snapshot.Run,
            RehydrateLeaseUse(snapshot.Use.Raw));

    private static bool IsLegalAdvancedChain(StartCommitSnapshot snapshot) =>
        snapshot.Run is not null && snapshot.Use is not null &&
        RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(
            snapshot.Lease,
            snapshot.Occurrence,
            snapshot.Run,
            RehydrateLeaseUse(snapshot.Use.Raw));

    private static bool IsExactBlockedChain(StartCommitSnapshot snapshot) =>
        snapshot.Run is not null && snapshot.Use is not null &&
        RecurringReplayIntegrityPolicy.IsExactPreStartBlockedReleasedChain(
            snapshot.Lease,
            snapshot.Occurrence,
            snapshot.Run,
            RehydrateLeaseUse(snapshot.Use.Raw));

    private static void ValidateClaimRelations(StartCommitSnapshot snapshot)
    {
        if (snapshot.Use is null || snapshot.Run is null ||
            snapshot.Use.Raw.LeaseId != snapshot.Lease.LeaseId ||
            snapshot.Use.Raw.PlanId != snapshot.Lease.PlanId ||
            snapshot.Use.Raw.OccurrenceIdentity != snapshot.OccurrenceIdentity ||
            snapshot.Use.Raw.OccurrenceId != snapshot.Occurrence.Id ||
            snapshot.Use.Raw.RunId != snapshot.Run.Id ||
            snapshot.Run.OccurrenceId != snapshot.Occurrence.Id ||
            snapshot.Occurrence.RunId != snapshot.Run.Id ||
            snapshot.Use.Entry.ReservedDuration > snapshot.Lease.MaxCumulativeDuration)
        {
            throw SnapshotFailure("The recurring run/use/occurrence relations are not exact.");
        }
    }

    private static void ValidateReplayQuotaAndLease(StartCommitSnapshot snapshot)
    {
        var quota = CalculateQuota(snapshot.Lease, snapshot.LeaseEntries);
        if (quota.IsTerminallyExhausted)
        {
            if (snapshot.Lease.Status != ConsentLeaseStatus.Exhausted)
                throw SnapshotFailure("The committed recurring quota requires an exhausted lease.");
        }
        else if (snapshot.Lease.Status != ConsentLeaseStatus.Active)
        {
            throw SnapshotFailure("The committed recurring quota has an invalid lease state.");
        }
    }

    private static RecurringFinalRecheckDecision EvaluateFinalRecheck(
        StartCommitSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot.Plan.IsOneTime || snapshot.Plan.Status != PlanDefinitionStatus.Enabled)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.PlanNotEnabled);

        if (snapshot.Lease.Status != ConsentLeaseStatus.Active)
        {
            var reason = snapshot.Lease.Status switch
            {
                ConsentLeaseStatus.Pending => RecurringOccurrenceStartCommitReasonCodes.LeasePending,
                ConsentLeaseStatus.Rejected => RecurringOccurrenceStartCommitReasonCodes.LeaseRejected,
                ConsentLeaseStatus.Revoked => RecurringOccurrenceStartCommitReasonCodes.LeaseRevoked,
                ConsentLeaseStatus.Expired => RecurringOccurrenceStartCommitReasonCodes.LeaseExpired,
                ConsentLeaseStatus.Exhausted => RecurringOccurrenceStartCommitReasonCodes.LeaseExhausted,
                _ => RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid,
            };
            return reason == RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid
                ? RecurringFinalRecheckDecision.Rejected(reason)
                : RecurringFinalRecheckDecision.Blocked(reason);
        }

        if (snapshot.Approval is null)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.ApprovalMissing);
        if (!IsExactApproval(snapshot.Lease, snapshot.Approval))
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.ApprovalMismatch);

        if (snapshot.Safety.UnattendedMode != UnattendedModeStatus.Enabled)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.UnattendedDisabled);
        if (snapshot.Safety.StopAllApplied && snapshot.Safety.StopAllAppliedAtUtc is { } stopAllAt &&
            snapshot.Approval.ApprovedAtUtc <= stopAllAt)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.StopAllBoundary);
        if (snapshot.Safety.UnattendedEnabledAtUtc is { } enabledAt && snapshot.Approval.ApprovedAtUtc < enabledAt)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.ReenableRequiresNewAuthorization);

        if (nowUtc < snapshot.Plan.UpdatedAtUtc || nowUtc < snapshot.Lease.UpdatedAtUtc ||
            nowUtc < snapshot.Occurrence.UpdatedAtUtc || snapshot.Run is null || snapshot.Use is null ||
            nowUtc < snapshot.Run.UpdatedAtUtc || nowUtc < snapshot.Use.Raw.UpdatedAtUtc ||
            nowUtc < snapshot.Specification!.EvaluatedAtUtc)
            return RecurringFinalRecheckDecision.Rejected(RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid);

        if (nowUtc < snapshot.Specification.ScheduledStartUtc)
            return RecurringFinalRecheckDecision.Rejected(RecurringOccurrenceStartCommitReasonCodes.BeforeScheduledStart);
        if (nowUtc < snapshot.Lease.ValidFromUtc)
            return RecurringFinalRecheckDecision.Rejected(RecurringOccurrenceStartCommitReasonCodes.BeforeLeaseValidity);
        if (nowUtc > snapshot.Specification.LatestStartUtc)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.AfterLatestStart);
        if (nowUtc >= snapshot.Lease.ValidUntilUtc)
            return RecurringFinalRecheckDecision.Blocked(RecurringOccurrenceStartCommitReasonCodes.LeaseExpired);

        var durationBoundaryFailure = EvaluateDurationBoundary(
            nowUtc,
            snapshot.Specification.Duration,
            snapshot.Specification.PlannedEndUtc,
            snapshot.Lease.ValidUntilUtc);
        if (durationBoundaryFailure == RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid)
            return RecurringFinalRecheckDecision.Rejected(durationBoundaryFailure);
        if (durationBoundaryFailure is not null)
            return RecurringFinalRecheckDecision.Blocked(durationBoundaryFailure);

        _ = CalculateQuota(snapshot.Lease, snapshot.LeaseEntries);
        return RecurringFinalRecheckDecision.Eligible();
    }

    // Kept as one pure guard so the two capture-window failure reasons cannot
    // drift from the final recheck ordering. Immutable Task 264 specifications
    // normally make both branches unreachable for a valid slot, but the guard
    // remains fail-closed for future schedule/lease shapes and persisted data.
    internal static string? EvaluateDurationBoundary(
        DateTimeOffset nowUtc,
        TimeSpan duration,
        DateTimeOffset plannedEndUtc,
        DateTimeOffset leaseEndUtc)
    {
        try
        {
            if (nowUtc.Add(duration) > plannedEndUtc)
                return RecurringOccurrenceStartCommitReasonCodes.DurationExceedsPlannedEnd;
            if (nowUtc.Add(duration) > leaseEndUtc)
                return RecurringOccurrenceStartCommitReasonCodes.DurationExceedsLease;
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid;
        }
    }

    private static bool IsExactApproval(RecurringConsentLease lease, RecurringLeaseLocalApprovalEvidence approval) =>
        approval.LeaseId == lease.LeaseId && approval.PlanId == lease.PlanId &&
        approval.ConfigurationDigest == lease.ConfigurationRef.ConfigurationDigest &&
        approval.AuthorizationDigest == lease.AuthorizationDigest &&
        approval.ApprovalKind == RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind &&
        approval.ApprovalVersion == RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion;

    private CommitMutation CommitInitialChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StartCommitSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        var run = snapshot.Run!;
        var preparing = run.TryTransition(RecordingRunStatus.Preparing, nowUtc);
        RequireChanged(preparing, "The recording run could not enter preparing.");
        UpdateRun(connection, transaction, run, expectedVersion: 0, RecordingRunStatus.Created, crossed: false);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterRunPreparingUpdate);

        var committed = run.TryTransition(RecordingRunStatus.StartCommitted, nowUtc);
        RequireChanged(committed, "The recording run could not cross start commit.");
        UpdateRun(connection, transaction, run, expectedVersion: 1, RecordingRunStatus.Preparing, crossed: false);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterRunStartCommitUpdate);

        var use = RehydrateLeaseUse(snapshot.Use!.Raw);
        RequireChanged(use.TryTransition(LeaseUseStatus.StartCommitted, nowUtc), "The recurring use could not cross start commit.");
        UpdateUse(connection, transaction, use, snapshot.Use.Raw.Version, LeaseUseStatus.Reserved,
            snapshot.Lease.PlanId, snapshot.OccurrenceIdentity);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterUseUpdate);

        var updatedEntries = snapshot.LeaseEntries
            .Select(entry => entry.UseId == snapshot.Use.Raw.UseId
                ? RecurringLeaseUseAccountingEntry.CreateFor(
                    snapshot.Lease,
                    entry.UseId,
                    entry.OccurrenceIdentity,
                    entry.RunId,
                    LeaseUseStatus.StartCommitted,
                    entry.ReservedUseCount,
                    entry.ReservedDuration,
                    actualSettledDuration: null)
                : entry)
            .ToArray();
        var quota = CalculateQuota(snapshot.Lease, updatedEntries);
        var lease = snapshot.Lease;
        if (quota.IsTerminallyExhausted)
        {
            var leaseVersion = lease.Version;
            RequireChanged(lease.TryMarkExhausted(quota, nowUtc), "The recurring lease could not be marked exhausted from its exact quota snapshot.");
            UpdateLease(connection, transaction, lease, leaseVersion, ConsentLeaseStatus.Active);
            _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterLeaseExhaustionUpdate);
        }

        return new CommitMutation(run, use, lease, quota);
    }

    private RecurringOccurrenceStartCommitResult TerminalizeBlocked(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StartCommitSnapshot snapshot,
        DateTimeOffset nowUtc,
        string reason)
    {
        if (!RecurringPreStartBlockReasonPolicy.IsKnown(reason))
            throw SnapshotFailure("A blocked terminal reason is not a stable business reason.");

        var run = snapshot.Run!;
        var runVersion = run.Version;
        var originalOccurrenceVersion = snapshot.Occurrence.Version;
        var runTransition = run.TryTransition(RecordingRunStatus.Failed, nowUtc, reason);
        RequireChanged(runTransition, "The recording run could not be terminalized before start commit.");
        UpdateRun(connection, transaction, run, runVersion, RecordingRunStatus.Created, crossed: false);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterBlockedRunUpdate);

        var use = RehydrateLeaseUse(snapshot.Use!.Raw);
        RequireChanged(use.TryReleaseReservation(nowUtc), "The recurring reservation could not be released.");
        UpdateUse(connection, transaction, use, snapshot.Use.Raw.Version, LeaseUseStatus.Reserved,
            snapshot.Lease.PlanId, snapshot.OccurrenceIdentity);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterBlockedUseUpdate);

        var occurrence = snapshot.Occurrence;
        var occurrenceVersion = occurrence.Version;
        RequireChanged(occurrence.TryTransition(PlanOccurrenceStatus.Blocked, nowUtc, reason), "The recurring occurrence could not be blocked.");
        UpdateOccurrenceBlocked(connection, transaction, occurrence, occurrenceVersion);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.AfterBlockedOccurrenceUpdate);

        _beforeFinalReadbackHookForTest?.Invoke(connection, transaction);
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.BeforeFinalReadback);
        var final = ReadAndValidateFinalBlockedState(
            connection,
            transaction,
            snapshot,
            nowUtc,
            reason,
            checked(runVersion + 1),
            checked(originalOccurrenceVersion + 1));
        _failureHookForTest?.Invoke(RecurringOccurrenceStartCommitFailurePoint.BeforeCommit);
        return Result(
            RecurringOccurrenceStartCommitStatus.Blocked,
            reason,
            snapshot.OccurrenceIdentity,
            final.Occurrence.Id,
            final.Run.Id,
            final.Use.Raw.UseId,
            changed: true,
            specification: final.Specification);
    }

    private static StartCommitFinalState ReadAndValidateFinalState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StartCommitSnapshot original,
        DateTimeOffset nowUtc,
        CommitMutation mutation,
        StandingLeaseExecutionEnvironment environment)
    {
        var final = ReadSnapshot(connection, transaction, original.Lease.LeaseId, original.OccurrenceIdentity);
        ValidateSpecification(final);
        if (!string.Equals(final.Specification!.SpecificationDigest, original.Specification!.SpecificationDigest, StringComparison.Ordinal) ||
            !final.Safety.Equals(original.Safety) ||
            final.Plan.Version != original.Plan.Version || final.Plan.Status != original.Plan.Status ||
            final.Lease.LeaseId != mutation.Lease.LeaseId || final.Lease.Status != mutation.Lease.Status ||
            final.Lease.Version != mutation.Lease.Version || final.Lease.UpdatedAtUtc != mutation.Lease.UpdatedAtUtc ||
            final.Occurrence.Status != PlanOccurrenceStatus.RunCreated || final.Occurrence.RunId != mutation.Run.Id ||
            final.Run is null || final.Run.Id != mutation.Run.Id || final.Run.OccurrenceId != original.Occurrence.Id ||
            final.Run.Status != RecordingRunStatus.StartCommitted || !final.Run.HasCrossedStartCommit ||
            final.Run.Version != mutation.Run.Version || final.Run.UpdatedAtUtc != nowUtc ||
            final.Use is null || final.Use.Raw.UseId != mutation.Use.Id || final.Use.Raw.RunId != mutation.Run.Id ||
            final.Use.Entry.Status != LeaseUseStatus.StartCommitted || final.Use.Raw.Version != mutation.Use.Version ||
            final.Use.Raw.UpdatedAtUtc != nowUtc || final.Use.Entry.ReservedDuration != original.Specification.Duration ||
            final.Use.Entry.ActualSettledDuration is not null)
        {
            throw SnapshotFailure("The final recurring start-commit readback was not exact.");
        }

        if (environment.NowUtc.UtcDateTime.Ticks != nowUtc.UtcDateTime.Ticks)
            throw SnapshotFailure("The final environment decision time no longer matches trusted time.");

        ValidateClaimRelations(final);
        var quota = CalculateQuota(final.Lease, final.LeaseEntries);
        if (quota.IsTerminallyExhausted != (final.Lease.Status == ConsentLeaseStatus.Exhausted))
            throw SnapshotFailure("The final recurring quota and lease state are not exact.");
        return new StartCommitFinalState(
            final.Plan,
            final.Lease,
            final.Occurrence,
            final.Run!,
            final.Use!,
            final.Approval,
            final.Specification,
            quota,
            final.LeaseEntries);
    }

    private static StartCommitFinalState ReadAndValidateFinalBlockedState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StartCommitSnapshot original,
        DateTimeOffset nowUtc,
        string reason,
        long expectedRunVersion,
        long expectedOccurrenceVersion)
    {
        var final = ReadSnapshot(connection, transaction, original.Lease.LeaseId, original.OccurrenceIdentity);
        ValidateSpecification(final);
        var finalRun = final.Run ?? throw SnapshotFailure("The final recurring blocked run readback was missing.");
        var finalUse = final.Use ?? throw SnapshotFailure("The final recurring blocked use readback was missing.");
        var originalUse = original.Use ?? throw SnapshotFailure("The original recurring blocked use was missing.");
        if (!string.Equals(final.Specification!.SpecificationDigest, original.Specification!.SpecificationDigest, StringComparison.Ordinal) ||
            !final.Safety.Equals(original.Safety) || final.Lease.Status != original.Lease.Status ||
            final.Lease.Version != original.Lease.Version || final.Lease.UpdatedAtUtc != original.Lease.UpdatedAtUtc ||
            final.Occurrence.Status != PlanOccurrenceStatus.Blocked || final.Occurrence.RunId != original.Occurrence.RunId ||
            final.Occurrence.TerminalReasonCode != reason || final.Occurrence.Version != expectedOccurrenceVersion ||
            finalRun.Status != RecordingRunStatus.Failed || finalRun.HasCrossedStartCommit ||
            finalRun.TerminalReasonCode != reason || finalRun.Version != expectedRunVersion ||
            finalRun.UpdatedAtUtc != nowUtc || finalUse.Entry.Status != LeaseUseStatus.Available ||
            finalUse.Entry.ReservedUseCount != 0 || finalUse.Entry.ReservedDuration != TimeSpan.Zero ||
            finalUse.Entry.ActualSettledDuration is not null || finalUse.Raw.Version != originalUse.Raw.Version + 1 ||
            finalUse.Raw.UpdatedAtUtc != nowUtc)
        {
            throw SnapshotFailure("The final recurring blocked readback was not exact.");
        }

        ValidateClaimRelations(final);
        return new StartCommitFinalState(
            final.Plan,
            final.Lease,
            final.Occurrence,
            final.Run,
            final.Use,
            final.Approval,
            final.Specification,
            CalculateQuota(final.Lease, final.LeaseEntries),
            final.LeaseEntries);
    }

    private static StartCommitSnapshot ReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId,
        string occurrenceIdentity)
    {
        var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, RequiredInput(leaseId))
            ?? throw SnapshotFailure("The exact recurring lease was not found.");
        var plan = ReadPlan(connection, transaction, lease.PlanId);
        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
            ?? throw SnapshotFailure("The exact recurring schedule revision was not found.");
        var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(connection, transaction, lease.PlanId)
            ?? throw SnapshotFailure("The exact recurring profile binding was not found.");
        var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection, transaction, lease.ConfigurationRef.ProfileRef);
        var approval = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(connection, transaction, lease.LeaseId)
            ?? throw SnapshotFailure("The exact recurring local approval evidence was not found.");
        var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
        var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
            connection, transaction, RequiredInput(occurrenceIdentity), schedule);
        if (slot is null || !slot.IsScheduled || slot.OccurrenceId is null || slot.OccurrenceIdentity != occurrenceIdentity)
            throw SnapshotFailure("The recurring occurrence slot is not an exact scheduled slot.");
        var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
        var candidate = CreateCandidate(slot, schedule);
        var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
            connection, transaction, occurrenceIdentity, occurrence.Id);
        var runs = ReadRunsByOccurrence(connection, transaction, occurrence.Id);
        var occurrenceUses = ReadRawUsesByOccurrence(connection, transaction, occurrenceIdentity, occurrence.Id);
        var leaseUses = ReadRawUsesByLease(connection, transaction, lease.LeaseId);
        var entries = RehydrateEntries(connection, transaction, lease, leaseUses);
        RawRecurringUseRow? rawUse = occurrenceUses.Count == 1 ? occurrenceUses[0] : null;
        RecurringUseRow? use = rawUse is null ? null : RehydrateUse(connection, transaction, lease, rawUse);
        RecordingRun? run = runs.Count == 1 ? runs[0] : null;
        var hasAnyClaim = occurrence.RunId is not null || runs.Count != 0 || occurrenceUses.Count != 0;
        if (runs.Count > 1 || occurrenceUses.Count > 1)
            throw SnapshotFailure("The recurring occurrence has duplicate execution claims.");
        if (occurrence.RunId is null && run is not null || occurrence.RunId is not null && (run is null || run.Id != occurrence.RunId))
            throw SnapshotFailure("The recurring occurrence run relation is not exact.");
        if (run is not null && (use is null || use.Raw.RunId != run.Id))
            throw SnapshotFailure("The recurring run/use relation is not exact.");
        if (run is null && use is not null)
            throw SnapshotFailure("A recurring use exists without its recording run.");

        return new StartCommitSnapshot(
            lease,
            plan,
            schedule,
            binding,
            profile,
            approval,
            safety,
            slot,
            occurrence,
            candidate,
            specification,
            run,
            use,
            entries,
            hasAnyClaim,
            occurrenceIdentity);
    }

    private static IReadOnlyList<RecordingRun> ReadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id ORDER BY id COLLATE BINARY;";
        Add(command, "$occurrence_id", RequiredInput(occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
            result.Add(ReadRecordingRunSnapshot(reader));
        return result;
    }

    private static IReadOnlyList<RawRecurringUseRow> ReadRawUsesByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE occurrence_identity = $identity OR occurrence_id = $occurrence_id ORDER BY use_id COLLATE BINARY;";
        Add(command, "$identity", RequiredInput(occurrenceIdentity));
        Add(command, "$occurrence_id", RequiredInput(occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<RawRecurringUseRow>();
        while (reader.Read())
            result.Add(ReadRawUse(reader));
        return result;
    }

    private static IReadOnlyList<RawRecurringUseRow> ReadRawUsesByLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE lease_id = $lease_id ORDER BY created_at_utc, use_id COLLATE BINARY;";
        Add(command, "$lease_id", RequiredInput(leaseId));
        using var reader = command.ExecuteReader();
        var result = new List<RawRecurringUseRow>();
        while (reader.Read())
            result.Add(ReadRawUse(reader));
        return result;
    }

    private static RawRecurringUseRow ReadRawUse(SqliteDataReader reader)
    {
        var reservedTicks = ReadInt64(reader, 8);
        var actualTicks = reader.IsDBNull(9) ? (long?)null : ReadInt64(reader, 9);
        if (reservedTicks < 0 || reservedTicks > TimeSpan.MaxValue.Ticks ||
            (actualTicks is not null && (actualTicks.Value < 0 || actualTicks.Value > TimeSpan.MaxValue.Ticks)))
            throw new PersistedSnapshotException("A recurring use duration is outside the TimeSpan range.");
        var row = new RawRecurringUseRow(
            ReadRequiredText(reader, 0), ReadRequiredText(reader, 1), ReadRequiredText(reader, 2),
            ReadRequiredText(reader, 3), ReadRequiredText(reader, 4), ReadRequiredText(reader, 5),
            ParseStatus(reader, 6, Phase3StateCodes.ParseLeaseUse), ReadInt32(reader, 7),
            TimeSpan.FromTicks(reservedTicks), actualTicks is null ? null : TimeSpan.FromTicks(actualTicks.Value),
            ReadUtcDateTimeOffset(reader, 10), ReadUtcDateTimeOffset(reader, 11), ReadInt64(reader, 12));
        if (row.Version < 0 || row.UpdatedAtUtc < row.CreatedAtUtc)
            throw new PersistedSnapshotException("A recurring use row has non-monotonic metadata.");
        return row;
    }

    private static IReadOnlyList<RecurringLeaseUseAccountingEntry> RehydrateEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        IReadOnlyList<RawRecurringUseRow> rows)
    {
        var entries = rows.Select(row => RehydrateUse(connection, transaction, lease, row).Entry).ToArray();
        _ = CalculateQuota(lease, entries);
        return entries;
    }

    private static RecurringUseRow RehydrateUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        RawRecurringUseRow row)
    {
        if (row.LeaseId != lease.LeaseId || row.PlanId != lease.PlanId)
            throw SnapshotFailure("A recurring use row points to another lease or plan.");
        var entry = RecurringLeaseUseAccountingEntry.Rehydrate(
            row.LeaseId, row.UseId, row.OccurrenceIdentity, row.RunId, row.Status,
            row.ReservedUseCount, row.ReservedDuration, row.ActualSettledDuration);
        entry.ValidateAgainst(lease);
        EnsureExistsWithinTransaction(connection, transaction,
            "SELECT 1 FROM recurring_occurrence_slots WHERE occurrence_identity = $identity AND plan_id = $plan_id AND occurrence_id = $occurrence_id;",
            ("$identity", row.OccurrenceIdentity), ("$plan_id", row.PlanId), ("$occurrence_id", row.OccurrenceId));
        EnsureExistsWithinTransaction(connection, transaction,
            "SELECT 1 FROM recording_runs WHERE id = $run_id AND occurrence_id = $occurrence_id;",
            ("$run_id", row.RunId), ("$occurrence_id", row.OccurrenceId));
        return new RecurringUseRow(row, entry);
    }

    private static RecurringLeaseQuotaSnapshot CalculateQuota(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> entries)
    {
        try
        {
            return RecurringLeaseQuotaCalculator.Calculate(lease, entries);
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid,
                "The recurring accounting projection is not a valid quota snapshot.", exception);
        }
    }

    private static RecurringOccurrenceCandidate CreateCandidate(
        RecurringOccurrenceSlotSnapshot slot,
        RecurringScheduleVersionSnapshot schedule)
    {
        var identity = RecurringOccurrenceIdentity.Create(
            slot.PlanId, slot.ScheduleRevision, schedule.Schedule, slot.LocalDate, slot.LocalWallClockTime);
        return new RecurringOccurrenceCandidate(
            slot.PlanId, slot.ScheduleRevision, schedule.Schedule, slot.LocalDate, slot.LocalWallClockTime,
            identity, slot.ScheduledStartUtc, slot.LatestStartUtc, slot.PlannedEndUtc,
            slot.TerminalReasonCode ?? "", slot.ResolutionCode);
    }

    private static LeaseUse RehydrateLeaseUse(RawRecurringUseRow row) =>
        LeaseUse.Rehydrate(row.UseId, row.LeaseId, row.OccurrenceId, row.RunId, row.CreatedAtUtc,
            row.Status, row.ReservedUseCount, row.ReservedDuration, row.ActualSettledDuration,
            row.UpdatedAtUtc, row.Version);

    private static void UpdateRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordingRun run,
        long expectedVersion,
        RecordingRunStatus expectedStatus,
        bool crossed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recording_runs
            SET status_code = $status_code, has_crossed_start_commit = $crossed,
                terminal_reason_code = $reason, updated_at_utc = $updated_at_utc,
                version = $version
            WHERE id = $id AND occurrence_id = $occurrence_id AND status_code = $expected_status
              AND has_crossed_start_commit = $expected_crossed AND version = $expected_version
              AND media_artifact_id IS $media_artifact_id AND bundle_id IS $bundle_id
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", run.StatusCode);
        Add(command, "$crossed", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$reason", run.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$version", run.Version);
        Add(command, "$id", run.Id);
        Add(command, "$occurrence_id", run.OccurrenceId);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_crossed", crossed ? 1L : 0L);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$media_artifact_id", run.MediaArtifactId);
        Add(command, "$bundle_id", run.BundleId);
        Add(command, "$created_at_utc", UtcTicksInput(run.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.Conflict, "The recording run changed before its recurring start transition.");
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
                updated_at_utc = $updated_at_utc, version = $version
            WHERE use_id = $use_id AND lease_id = $lease_id AND occurrence_id = $occurrence_id
              AND run_id = $run_id AND status_code = $expected_status AND version = $expected_version
              AND plan_id = $plan_id AND occurrence_identity = $occurrence_identity
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", use.StatusCode);
        Add(command, "$reserved_use_count", use.ReservedUseCount);
        Add(command, "$reserved_duration_ticks", use.ReservedDuration.Ticks);
        Add(command, "$actual_settled_duration_ticks", use.ActualSettledDuration is null ? null : use.ActualSettledDuration.Value.Ticks);
        Add(command, "$updated_at_utc", UtcTicksInput(use.UpdatedAtUtc));
        Add(command, "$version", use.Version);
        Add(command, "$use_id", use.Id);
        Add(command, "$lease_id", use.LeaseId);
        Add(command, "$occurrence_id", use.OccurrenceId);
        Add(command, "$run_id", use.RunId);
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(planId));
        Add(command, "$occurrence_identity", RequiredInput(occurrenceIdentity));
        Add(command, "$created_at_utc", UtcTicksInput(use.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.Conflict, "The recurring use changed before its start transition.");
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
            SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $version
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
        Add(command, "$version", lease.Version);
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
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.Conflict, "The recurring lease changed before quota exhaustion could be committed.");
    }

    private static void UpdateOccurrenceBlocked(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_occurrences
            SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $reason,
                updated_at_utc = $updated_at_utc, version = $version
            WHERE id = $id AND plan_id = $plan_id AND status_code = 'run_created'
              AND run_id = $run_id AND terminal_reason_code IS NULL AND version = $expected_version
              AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$reason", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$version", occurrence.Version);
        Add(command, "$id", occurrence.Id);
        Add(command, "$plan_id", occurrence.PlanId);
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException(RecurringOccurrenceStartCommitReasonCodes.Conflict, "The recurring occurrence changed before it could be blocked.");
    }

    private static void RequireChanged(Phase3TransitionResult transition, string message)
    {
        if (!transition.Succeeded || !transition.Changed)
            throw SnapshotFailure(message);
    }

    private static RecurringOccurrenceStartCommitResult MapFailure(
        string occurrenceIdentity,
        Phase3PersistenceException exception)
    {
        var conflict = exception.Code == RecurringOccurrenceStartCommitReasonCodes.Conflict ||
            exception.Code.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
            exception.Code.Contains("unique", StringComparison.OrdinalIgnoreCase);
        var persistedSnapshotInvalid = exception.Code == RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid ||
            exception.Code == RecurringPersistenceReasonCodes.OccurrenceIdentityConflict ||
            exception.Code == RecurringPersistenceReasonCodes.PersistedDataInvalid;
        return Result(conflict ? RecurringOccurrenceStartCommitStatus.Conflict : RecurringOccurrenceStartCommitStatus.Rejected,
            conflict ? RecurringOccurrenceStartCommitReasonCodes.Conflict : persistedSnapshotInvalid
                ? RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid
                : RecurringOccurrenceStartCommitReasonCodes.TransactionFailed,
            occurrenceIdentity);
    }

    private static RecurringOccurrenceStartCommitResult Result(
        RecurringOccurrenceStartCommitStatus status,
        string reason,
        string occurrenceIdentity,
        string? occurrenceId = null,
        string? runId = null,
        string? useId = null,
        bool changed = false,
        RecurringOccurrenceExecutionSpecification? specification = null,
        RecurringStartCommitReceipt? firstCommitReceipt = null,
        RecurringLeaseUseProof? firstCommitProof = null,
        RecurringStartCommitReceipt? postCommitRecoveryReceipt = null) =>
        RecurringOccurrenceStartCommitResult.Create(
            status, reason, occurrenceIdentity, occurrenceId, runId, useId, changed,
            specification is null ? null : RecurringOccurrenceExecutionSpecificationSummary.From(specification),
            firstCommitReceipt,
            firstCommitProof,
            postCommitRecoveryReceipt);

    private static Phase3PersistenceException SnapshotFailure(string message) =>
        new(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, message);

    private sealed record RawRecurringUseRow(
        string UseId,
        string LeaseId,
        string PlanId,
        string OccurrenceIdentity,
        string OccurrenceId,
        string RunId,
        LeaseUseStatus Status,
        int ReservedUseCount,
        TimeSpan ReservedDuration,
        TimeSpan? ActualSettledDuration,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        long Version);

    private sealed record RecurringUseRow(RawRecurringUseRow Raw, RecurringLeaseUseAccountingEntry Entry);

    private sealed record StartCommitSnapshot(
        RecurringConsentLease Lease,
        PlanDefinition Plan,
        RecurringScheduleVersionSnapshot Schedule,
        RecurringPlanProfileBinding Binding,
        RecurringFixedRegionProfileVersion Profile,
        RecurringLeaseLocalApprovalEvidence Approval,
        SqliteStandingLeaseSafetyGlobalState Safety,
        RecurringOccurrenceSlotSnapshot Slot,
        PlanOccurrence Occurrence,
        RecurringOccurrenceCandidate Candidate,
        RecurringOccurrenceExecutionSpecification? Specification,
        RecordingRun? Run,
        RecurringUseRow? Use,
        IReadOnlyList<RecurringLeaseUseAccountingEntry> LeaseEntries,
        bool HasAnyClaim,
        string OccurrenceIdentity)
    ;

    private sealed record CommitMutation(
        RecordingRun Run,
        LeaseUse Use,
        RecurringConsentLease Lease,
        RecurringLeaseQuotaSnapshot Quota);

    private sealed record StartCommitFinalState(
        PlanDefinition Plan,
        RecurringConsentLease Lease,
        PlanOccurrence Occurrence,
        RecordingRun Run,
        RecurringUseRow Use,
        RecurringLeaseLocalApprovalEvidence Approval,
        RecurringOccurrenceExecutionSpecification Specification,
        RecurringLeaseQuotaSnapshot Quota,
        IReadOnlyList<RecurringLeaseUseAccountingEntry> LeaseEntries);
}
