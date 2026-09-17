using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringOccurrenceReservationStatus
{
    Reserved,
    AlreadyReserved,
    AlreadyAdvanced,
    Rejected,
    Conflict,
}

internal enum RecurringOccurrenceReservationFailurePoint
{
    AfterRunInsert,
    AfterRecurringUseInsert,
    AfterOccurrenceCasUpdate,
    BeforeCommitAfterFinalRead,
}

internal static class RecurringOccurrenceReservationReasonCodes
{
    public const string RequestInvalid = "recurring_occurrence_reservation_request_invalid";
    public const string ClockInvalid = "recurring_occurrence_reservation_clock_invalid";
    public const string ClockMovedBackwards = "recurring_occurrence_reservation_clock_moved_backwards";
    public const string PersistedSnapshotInvalid = "recurring_occurrence_reservation_persisted_snapshot_invalid";
    public const string TransactionFailed = "recurring_occurrence_reservation_transaction_failed";
    public const string Conflict = "recurring_occurrence_reservation_conflict";
    public const string PlanNotEnabled = "recurring_occurrence_reservation_plan_not_enabled";
    public const string OccurrenceNotAuthorized = "recurring_occurrence_not_authorized";
    public const string OccurrenceAfterLatestStart = "recurring_occurrence_after_latest_start";
    public const string ApprovalMissing = "recurring_occurrence_reservation_approval_missing";
    public const string ApprovalMismatch = "recurring_occurrence_reservation_approval_mismatch";
    public const string UnattendedDisabled = "unattended_disabled";
    public const string StopAllBoundary = "stop_all_boundary";
    public const string ReenableRequiresNewAuthorization = "unattended_reenable_requires_new_authorization";
    public const string Reserved = "recurring_occurrence_reserved";
    public const string AlreadyReserved = "recurring_occurrence_already_reserved";
    public const string AlreadyAdvanced = "recurring_occurrence_already_advanced";
}

/// <summary>
/// The production reservation request deliberately contains only the two
/// business identifiers supplied by the caller.  Clock, configuration,
/// duration, status, versions, and generated execution IDs stay inside the
/// service and transaction boundary.
/// </summary>
internal sealed class RecurringOccurrenceReservationRequest
{
    internal RecurringOccurrenceReservationRequest(string leaseId, string occurrenceIdentity)
    {
        LeaseId = leaseId;
        OccurrenceIdentity = occurrenceIdentity;
    }

    internal string LeaseId { get; }

    internal string OccurrenceIdentity { get; }
}

internal sealed class RecurringOccurrenceReservationResult
{
    private RecurringOccurrenceReservationResult(
        RecurringOccurrenceReservationStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId,
        string? runId,
        string? useId,
        bool changed,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary)
    {
        Status = status;
        ReasonCode = reasonCode;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        RunId = runId;
        UseId = useId;
        Changed = changed;
        SpecificationSummary = specificationSummary;
    }

    internal RecurringOccurrenceReservationStatus Status { get; }

    internal string StatusCode => Status.ToString();

    internal string ResultCode => StatusCode;

    internal string ReasonCode { get; }

    internal string Reason => ReasonCode;

    internal string OccurrenceIdentity { get; }

    internal string? OccurrenceId { get; }

    internal string? RunId { get; }

    internal string? UseId { get; }

    internal bool Changed { get; }

    internal RecurringOccurrenceExecutionSpecificationSummary? SpecificationSummary { get; }

    internal RecurringOccurrenceExecutionSpecificationSummary? ExecutionSpecificationSummary => SpecificationSummary;

    internal string? SpecificationDigest => SpecificationSummary?.SpecificationDigest;

    internal bool Succeeded => Status is RecurringOccurrenceReservationStatus.Reserved or
        RecurringOccurrenceReservationStatus.AlreadyReserved or
        RecurringOccurrenceReservationStatus.AlreadyAdvanced;

    internal static RecurringOccurrenceReservationResult Create(
        RecurringOccurrenceReservationStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId = null,
        string? runId = null,
        string? useId = null,
        bool changed = false,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary = null) =>
        new(status, reasonCode, occurrenceIdentity, occurrenceId, runId, useId, changed, specificationSummary);
}

internal sealed class RecurringOccurrenceReservationService
{
    private readonly SqliteOperationalStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _runIdFactory;
    private readonly Func<string> _useIdFactory;
    private readonly Action<RecurringOccurrenceReservationFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;
    private readonly object _clockGate = new();
    private DateTimeOffset? _lastTrustedNowUtc;

    internal RecurringOccurrenceReservationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset>? utcNowForTest = null,
        Func<string>? runIdFactoryForTest = null,
        Func<string>? useIdFactoryForTest = null,
        Action<RecurringOccurrenceReservationFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _runIdFactory = runIdFactoryForTest ?? (() => "run-recurring-reservation-" + Guid.NewGuid().ToString("N"));
        _useIdFactory = useIdFactoryForTest ?? (() => "use-recurring-reservation-" + Guid.NewGuid().ToString("N"));
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
    }

    internal RecurringOccurrenceReservationResult Reserve(string leaseId, string occurrenceIdentity)
    {
        var request = new RecurringOccurrenceReservationRequest(leaseId, occurrenceIdentity);
        if (!IsCanonicalInput(request.LeaseId) || !IsCanonicalInput(request.OccurrenceIdentity))
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.RequestInvalid,
                occurrenceIdentity);
        }

        DateTimeOffset trustedNowUtc;
        try
        {
            lock (_clockGate)
            {
                trustedNowUtc = _utcNow();
                if (trustedNowUtc.Offset != TimeSpan.Zero)
                {
                    return RecurringOccurrenceReservationResult.Create(
                        RecurringOccurrenceReservationStatus.Rejected,
                        RecurringOccurrenceReservationReasonCodes.ClockInvalid,
                        occurrenceIdentity);
                }

                if (_lastTrustedNowUtc is { } previous && trustedNowUtc < previous)
                {
                    return RecurringOccurrenceReservationResult.Create(
                        RecurringOccurrenceReservationStatus.Rejected,
                        RecurringOccurrenceReservationReasonCodes.ClockMovedBackwards,
                        occurrenceIdentity);
                }

                _lastTrustedNowUtc = trustedNowUtc;
            }
        }
        catch
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.ClockInvalid,
                occurrenceIdentity);
        }

        string runId;
        string useId;
        try
        {
            runId = RequiredGeneratedId(_runIdFactory());
            useId = RequiredGeneratedId(_useIdFactory());
        }
        catch
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.RequestInvalid,
                occurrenceIdentity);
        }

        return new SqliteRecurringOccurrenceReservationTransaction(_store, _failureHookForTest, _beforeFinalReadbackHookForTest)
            .Reserve(request, trustedNowUtc, runId, useId);
    }

    private static bool IsCanonicalInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static string RequiredGeneratedId(string? value)
    {
        if (!IsCanonicalInput(value))
        {
            throw new InvalidOperationException("The internal reservation identifier factory returned an invalid identifier.");
        }

        return value!;
    }
}

internal sealed class SqliteRecurringOccurrenceReservationTransaction : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";
    private readonly Action<RecurringOccurrenceReservationFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;

    internal SqliteRecurringOccurrenceReservationTransaction(
        SqliteOperationalStore store,
        Action<RecurringOccurrenceReservationFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null)
        : base(store)
    {
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
    }

    internal RecurringOccurrenceReservationResult Reserve(
        RecurringOccurrenceReservationRequest request,
        DateTimeOffset trustedNowUtc,
        string runId,
        string useId)
    {
        ArgumentNullException.ThrowIfNull(request);
        SqliteTransaction? transaction = null;
        using var connection = OpenBusinessConnection();
        try
        {
            ValidateInputs(request, trustedNowUtc, runId, useId);
            transaction = BeginWriteTransaction(connection);

            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, request.LeaseId)
                ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring lease was not found.");
            var plan = ReadPlan(connection, transaction, lease.PlanId);
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                    connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
                ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The exact recurring schedule revision was not found.");
            var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(connection, transaction, lease.PlanId)
                ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The exact recurring profile binding was not found.");
            var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                connection, transaction, lease.ConfigurationRef.ProfileRef);
            var evidence = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(
                connection, transaction, lease.LeaseId);
            var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
            var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
                connection, transaction, request.OccurrenceIdentity, schedule);

            if (!slot.IsScheduled || slot.OccurrenceId is null ||
                !string.Equals(slot.OccurrenceIdentity, request.OccurrenceIdentity, StringComparison.Ordinal))
            {
                return CompleteReadOnly(transaction, Rejected(
                    request,
                    RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                    slot.OccurrenceId));
            }

            var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
            var occurrenceRuns = ReadRunsByOccurrence(connection, transaction, slot.OccurrenceId);
            var occurrenceUseRows = ReadRawUsesByOccurrence(connection, transaction, request.OccurrenceIdentity);
            var requestedLeaseUseRows = ReadRawUsesByLease(connection, transaction, lease.LeaseId);
            var entries = RehydrateAccountingRows(connection, transaction, lease, requestedLeaseUseRows);

            ValidateExactConfiguration(lease, plan, schedule, binding, profile, slot, occurrence);
            var candidate = CalculateAndValidateCandidate(request.OccurrenceIdentity, lease, schedule, slot);
            var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection,
                transaction,
                request.OccurrenceIdentity,
                occurrence.Id);
            var hasAnyClaim = occurrence.RunId is not null || occurrenceRuns.Count != 0 || occurrenceUseRows.Count != 0;
            if (specification is null)
            {
                var missingSpecificationReason = hasAnyClaim || occurrence.Status == PlanOccurrenceStatus.Authorized
                    ? RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid
                    : RecurringOccurrenceReservationReasonCodes.OccurrenceNotAuthorized;
                return CompleteReadOnly(transaction, Rejected(request, missingSpecificationReason, slot.OccurrenceId));
            }

            if (evidence is null)
            {
                return CompleteReadOnly(transaction, Rejected(
                    request,
                    RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                    slot.OccurrenceId));
            }

            SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
                specification,
                plan,
                schedule,
                binding,
                profile,
                lease,
                evidence,
                candidate,
                occurrence);

            var existingClaim = EvaluateExistingClaim(
                connection,
                transaction,
                plan,
                lease,
                schedule,
                binding,
                profile,
                evidence,
                occurrence,
                occurrenceRuns,
                occurrenceUseRows,
                slot,
                entries,
                specification);
            if (existingClaim is not null)
            {
                return CompleteReadOnly(transaction, existingClaim);
            }

            if (occurrence.Status != PlanOccurrenceStatus.Authorized)
            {
                return CompleteReadOnly(transaction, Rejected(
                    request,
                    RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                    slot.OccurrenceId));
            }

            var lifecycleFailure = ValidateLifecycleAndSafety(
                lease, plan, occurrence, evidence, safety, trustedNowUtc);
            if (lifecycleFailure is not null)
            {
                return CompleteReadOnly(transaction, Rejected(request, lifecycleFailure, slot.OccurrenceId));
            }

            if (trustedNowUtc > candidate.LatestStartUtc!.Value)
            {
                return CompleteReadOnly(transaction, Rejected(request, RecurringOccurrenceReservationReasonCodes.OccurrenceAfterLatestStart, slot.OccurrenceId));
            }

            var occurrenceRequest = RecurringLeaseOccurrenceRequest.CreateFor(
                lease,
                request.OccurrenceIdentity,
                slot.ScheduledStartUtc!.Value,
                slot.LatestStartUtc!.Value,
                slot.PlannedEndUtc!.Value,
                specification.Duration);
            var decision = RecurringLeaseQuotaCalculator.EvaluateReservation(
                lease,
                entries.Select(row => row.Entry),
                occurrenceRequest,
                trustedNowUtc);
            if (!decision.IsAllowed)
            {
                return CompleteReadOnly(transaction, Rejected(request, decision.ReasonCode, slot.OccurrenceId));
            }

            if (!occurrence.CanCreateRun)
            {
                return CompleteReadOnly(transaction, Rejected(request, RecurringOccurrenceReservationReasonCodes.OccurrenceNotAuthorized, slot.OccurrenceId));
            }

            if (occurrence.Version == long.MaxValue)
            {
                return CompleteReadOnly(transaction, Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId));
            }

            var occurrenceTransition = occurrence.TryCreateRun(runId, trustedNowUtc);
            if (!occurrenceTransition.Succeeded || !occurrenceTransition.Changed)
            {
                return CompleteReadOnly(transaction, Rejected(request, RecurringOccurrenceReservationReasonCodes.OccurrenceNotAuthorized, slot.OccurrenceId));
            }

            var run = RecordingRun.CreateFor(occurrence, runId, trustedNowUtc);
            var entry = RecurringLeaseUseAccountingEntry.CreateFor(
                lease,
                useId,
                request.OccurrenceIdentity,
                runId,
                LeaseUseStatus.Reserved,
                reservedUseCount: 1,
                specification.Duration,
                actualSettledDuration: null);

            InsertRun(connection, transaction, run);
            _failureHookForTest?.Invoke(RecurringOccurrenceReservationFailurePoint.AfterRunInsert);
            InsertRecurringUse(connection, transaction, lease, slot, entry, trustedNowUtc);
            _failureHookForTest?.Invoke(RecurringOccurrenceReservationFailurePoint.AfterRecurringUseInsert);
            UpdateOccurrence(connection, transaction, occurrence, expectedVersion: occurrence.Version - 1);
            _failureHookForTest?.Invoke(RecurringOccurrenceReservationFailurePoint.AfterOccurrenceCasUpdate);

            _beforeFinalReadbackHookForTest?.Invoke(connection, transaction);
            var finalSpecification = ValidateFinalReadback(
                connection,
                transaction,
                lease,
                plan,
                evidence,
                safety,
                schedule,
                binding,
                profile,
                slot,
                occurrence,
                run,
                entry,
                specification,
                trustedNowUtc);
            _failureHookForTest?.Invoke(RecurringOccurrenceReservationFailurePoint.BeforeCommitAfterFinalRead);
            transaction.Commit();
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Reserved,
                RecurringOccurrenceReservationReasonCodes.Reserved,
                request.OccurrenceIdentity,
                slot.OccurrenceId,
                runId,
                useId,
                changed: true,
                specificationSummary: RecurringOccurrenceExecutionSpecificationSummary.From(finalSpecification));
        }
        catch (Phase3PersistenceException exception)
        {
            TryRollback(transaction);
            return MapFailure(request, exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Conflict,
                RecurringOccurrenceReservationReasonCodes.Conflict,
                request.OccurrenceIdentity);
        }
        catch (PersistedSnapshotException)
        {
            TryRollback(transaction);
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                request.OccurrenceIdentity);
        }
        catch (Phase3DomainException)
        {
            TryRollback(transaction);
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                request.OccurrenceIdentity);
        }
        catch
        {
            TryRollback(transaction);
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Rejected,
                RecurringOccurrenceReservationReasonCodes.TransactionFailed,
                request.OccurrenceIdentity);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static void ValidateInputs(
        RecurringOccurrenceReservationRequest request,
        DateTimeOffset trustedNowUtc,
        string runId,
        string useId)
    {
        if (!IsCanonical(request.LeaseId) || !IsCanonical(request.OccurrenceIdentity) ||
            trustedNowUtc.Offset != TimeSpan.Zero || !IsCanonical(runId) || !IsCanonical(useId))
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.RequestInvalid, "The recurring reservation inputs are not canonical.");
        }
    }

    private static RecurringOccurrenceReservationResult? EvaluateExistingClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        RecurringConsentLease lease,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        RecurringLeaseLocalApprovalEvidence? evidence,
        PlanOccurrence occurrence,
        IReadOnlyList<RecordingRun> occurrenceRuns,
        IReadOnlyList<RawRecurringUseRow> occurrenceUseRows,
        RecurringOccurrenceSlotSnapshot slot,
        IReadOnlyList<RecurringUseRow> requestedLeaseEntries,
        RecurringOccurrenceExecutionSpecification specification)
    {
        var request = new RecurringOccurrenceReservationRequest(lease.LeaseId, slot.OccurrenceIdentity);

        if (!HasExactParentRelations(plan, lease, schedule, binding, profile, occurrence, slot))
        {
            return Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId);
        }

        // A use row for another lease is a claim conflict even if another
        // row is also present. Conflict is intentionally decided before the
        // cardinality check so a competing claim cannot be hidden by a
        // malformed duplicate chain.
        var competingUse = occurrenceUseRows.FirstOrDefault(row =>
            !string.Equals(row.LeaseId, lease.LeaseId, StringComparison.Ordinal));
        if (competingUse is not null)
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.Conflict,
                RecurringOccurrenceReservationReasonCodes.Conflict,
                slot.OccurrenceIdentity,
                slot.OccurrenceId,
                occurrence.RunId,
                competingUse.UseId);
        }

        if (occurrenceRuns.Count > 1 || occurrenceUseRows.Count > 1)
        {
            return Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId);
        }

        var rawUse = occurrenceUseRows.SingleOrDefault();
        var run = occurrenceRuns.SingleOrDefault();
        var hasAnyClaim = occurrence.RunId is not null || run is not null || rawUse is not null;
        if (!hasAnyClaim)
        {
            return null;
        }

        // Every accepted existing chain has all three durable links. A
        // missing occurrence attachment, run row, or use row is never treated
        // as an opportunity to reserve again.
        if (occurrence.Status is not (PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed or PlanOccurrenceStatus.Blocked) ||
            occurrence.RunId is null ||
            run is null ||
            rawUse is null ||
            !string.Equals(run.Id, occurrence.RunId, StringComparison.Ordinal))
        {
            return Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId, occurrence.RunId, rawUse?.UseId);
        }

        var approvalFailure = ValidateExactApprovalEvidence(lease, evidence);
        if (approvalFailure is not null)
        {
            return Rejected(request, approvalFailure, slot.OccurrenceId, occurrence.RunId, rawUse?.UseId);
        }

        var use = RehydrateOneAccountingRow(connection, transaction, lease, rawUse);
        if (!HasExactClaimRelations(lease, occurrence, run, use, slot, requestedLeaseEntries))
        {
            return Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId, run.Id, use.Entry.UseId);
        }

        if (IsExactReservationChain(lease, occurrence, run, use))
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.AlreadyReserved,
                RecurringOccurrenceReservationReasonCodes.AlreadyReserved,
                slot.OccurrenceIdentity,
                slot.OccurrenceId,
                run.Id,
                use.Entry.UseId,
                specificationSummary: RecurringOccurrenceExecutionSpecificationSummary.From(specification));
        }

        if (IsExactBlockedReleasedChain(lease, occurrence, run, use))
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.AlreadyAdvanced,
                RecurringOccurrenceReservationReasonCodes.AlreadyAdvanced,
                slot.OccurrenceIdentity,
                slot.OccurrenceId,
                run.Id,
                use.Entry.UseId,
                specificationSummary: RecurringOccurrenceExecutionSpecificationSummary.From(specification));
        }

        if (IsLegalProgressedChain(lease, occurrence, run, use))
        {
            return RecurringOccurrenceReservationResult.Create(
                RecurringOccurrenceReservationStatus.AlreadyAdvanced,
                RecurringOccurrenceReservationReasonCodes.AlreadyAdvanced,
                slot.OccurrenceIdentity,
                slot.OccurrenceId,
                run.Id,
                use.Entry.UseId,
                specificationSummary: RecurringOccurrenceExecutionSpecificationSummary.From(specification));
        }

        // A released use is deliberately not accepted as a legal replay
        // chain. No recurring lifecycle transaction currently freezes an
        // atomic released tuple across occurrence/run/use, so the safe
        // choice is a stable read-only rejection rather than a second run.
        return Rejected(request, RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, slot.OccurrenceId, run.Id, use.Entry.UseId);
    }

    private static bool HasExactParentRelations(
        PlanDefinition plan,
        RecurringConsentLease lease,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        PlanOccurrence occurrence,
        RecurringOccurrenceSlotSnapshot slot) =>
        !plan.IsOneTime &&
        string.Equals(plan.Id, lease.PlanId, StringComparison.Ordinal) &&
        string.Equals(schedule.PlanId, lease.PlanId, StringComparison.Ordinal) &&
        schedule.ScheduleRevision == lease.ConfigurationRef.ScheduleRevision &&
        string.Equals(schedule.ScheduleDigest, lease.ConfigurationRef.ScheduleDigest, StringComparison.Ordinal) &&
        string.Equals(schedule.TimeZoneRulesDigest, lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal) &&
        string.Equals(binding.PlanId, lease.PlanId, StringComparison.Ordinal) &&
        ProfileReferencesEqual(binding.ProfileRef, lease.ConfigurationRef.ProfileRef) &&
        lease.ConfigurationRef.ProfileRef.Matches(profile) &&
        schedule.Schedule.RecordingDuration == profile.Duration &&
        profile.Duration == lease.PerRunDuration &&
        string.Equals(slot.PlanId, lease.PlanId, StringComparison.Ordinal) &&
        slot.ScheduleRevision == schedule.ScheduleRevision &&
        string.Equals(slot.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) &&
        string.Equals(slot.TimeZoneId, schedule.Schedule.TimeZoneId, StringComparison.Ordinal) &&
        occurrence.Id == slot.OccurrenceId &&
        occurrence.PlanId == lease.PlanId &&
        occurrence.WindowStartUtc == slot.ScheduledStartUtc &&
        occurrence.WindowEndUtc == slot.PlannedEndUtc;

    private static bool HasExactClaimRelations(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        RecurringUseRow use,
        RecurringOccurrenceSlotSnapshot slot,
        IReadOnlyList<RecurringUseRow> requestedLeaseEntries) =>
        occurrence.RunId == run.Id &&
        run.OccurrenceId == occurrence.Id &&
        use.Entry.LeaseId == lease.LeaseId &&
        use.Entry.RunId == run.Id &&
        use.Entry.OccurrenceIdentity == slot.OccurrenceIdentity &&
        use.Entry.OccurrenceId == slot.OccurrenceId &&
        use.PlanId == lease.PlanId &&
        use.OccurrenceId == slot.OccurrenceId &&
        requestedLeaseEntries.Count(row => row.Entry.UseId == use.Entry.UseId) == 1;

    private static bool IsExactReservationChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        RecurringUseRow use)
        => RecurringReplayIntegrityPolicy.IsInitialReservationChain(
            lease,
            occurrence,
            run,
            ToLeaseUse(use));

    private static bool IsLegalProgressedChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        RecurringUseRow use)
        => RecurringReplayIntegrityPolicy.IsLegalAdvancedChain(
            lease,
            occurrence,
            run,
            ToLeaseUse(use));

    private static bool IsExactBlockedReleasedChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        RecurringUseRow use) =>
        RecurringReplayIntegrityPolicy.IsExactPreStartBlockedReleasedChain(
            lease,
            occurrence,
            run,
            ToLeaseUse(use));

    private static LeaseUse ToLeaseUse(RecurringUseRow use) =>
        LeaseUse.Rehydrate(
            use.Entry.UseId,
            use.Entry.LeaseId,
            use.OccurrenceId,
            use.Entry.RunId,
            use.CreatedAtUtc,
            use.Entry.Status,
            use.Entry.ReservedUseCount,
            use.Entry.ReservedDuration,
            use.Entry.ActualSettledDuration,
            use.UpdatedAtUtc,
            use.Version);

    private static string? ValidateExactApprovalEvidence(
        RecurringConsentLease lease,
        RecurringLeaseLocalApprovalEvidence? evidence)
    {
        if (evidence is null)
        {
            return RecurringOccurrenceReservationReasonCodes.ApprovalMissing;
        }

        if (!string.Equals(evidence.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(evidence.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            !string.Equals(evidence.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) ||
            evidence.ApprovalKind != RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind ||
            evidence.ApprovalVersion != RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion)
        {
            return RecurringOccurrenceReservationReasonCodes.ApprovalMismatch;
        }

        return null;
    }

    private static string? ValidateLifecycleAndSafety(
        RecurringConsentLease lease,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        RecurringLeaseLocalApprovalEvidence? evidence,
        SqliteStandingLeaseSafetyGlobalState safety,
        DateTimeOffset trustedNowUtc)
    {
        if (plan.IsOneTime || !string.Equals(plan.Id, lease.PlanId, StringComparison.Ordinal) || plan.Status != PlanDefinitionStatus.Enabled)
        {
            return RecurringOccurrenceReservationReasonCodes.PlanNotEnabled;
        }

        if (occurrence.Status != PlanOccurrenceStatus.Authorized || occurrence.RunId is not null)
        {
            return RecurringOccurrenceReservationReasonCodes.OccurrenceNotAuthorized;
        }

        if (lease.Status != ConsentLeaseStatus.Active)
        {
            return lease.Status switch
            {
                ConsentLeaseStatus.Pending => RecurringLeaseQuotaReasonCodes.Pending,
                ConsentLeaseStatus.Rejected => RecurringLeaseQuotaReasonCodes.Rejected,
                ConsentLeaseStatus.Revoked => RecurringLeaseQuotaReasonCodes.Revoked,
                ConsentLeaseStatus.Expired => RecurringLeaseQuotaReasonCodes.Expired,
                ConsentLeaseStatus.Exhausted => RecurringLeaseQuotaReasonCodes.Exhausted,
                _ => RecurringLeaseQuotaReasonCodes.NotActive,
            };
        }

        if (evidence is null)
        {
            return RecurringOccurrenceReservationReasonCodes.ApprovalMissing;
        }

        if (!string.Equals(evidence.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(evidence.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            !string.Equals(evidence.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) ||
            evidence.ApprovalKind != RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind ||
            evidence.ApprovalVersion != RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion)
        {
            return RecurringOccurrenceReservationReasonCodes.ApprovalMismatch;
        }

        if (safety.UnattendedMode != UnattendedModeStatus.Enabled)
        {
            return RecurringOccurrenceReservationReasonCodes.UnattendedDisabled;
        }

        if (safety.StopAllAppliedAtUtc is { } stopAllAt && evidence.ApprovedAtUtc <= stopAllAt)
        {
            return RecurringOccurrenceReservationReasonCodes.StopAllBoundary;
        }

        if (safety.UnattendedEnabledAtUtc is { } enabledAt && evidence.ApprovedAtUtc < enabledAt)
        {
            return RecurringOccurrenceReservationReasonCodes.ReenableRequiresNewAuthorization;
        }

        if (trustedNowUtc < plan.UpdatedAtUtc || trustedNowUtc < lease.UpdatedAtUtc || trustedNowUtc < occurrence.UpdatedAtUtc)
        {
            return RecurringLeaseQuotaReasonCodes.CurrentTimeInvalid;
        }

        if (trustedNowUtc < lease.ValidFromUtc)
        {
            return RecurringLeaseQuotaReasonCodes.BeforeValidity;
        }

        if (trustedNowUtc >= lease.ValidUntilUtc)
        {
            return RecurringLeaseQuotaReasonCodes.Expired;
        }

        return null;
    }

    private static void ValidateExactConfiguration(
        RecurringConsentLease lease,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        RecurringOccurrenceSlotSnapshot slot,
        PlanOccurrence occurrence)
    {
        if (plan.IsOneTime || !string.Equals(schedule.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            schedule.ScheduleRevision != lease.ConfigurationRef.ScheduleRevision ||
            !string.Equals(schedule.ScheduleDigest, lease.ConfigurationRef.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(schedule.TimeZoneRulesDigest, lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            !string.Equals(binding.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            !ProfileReferencesEqual(binding.ProfileRef, lease.ConfigurationRef.ProfileRef) ||
            !lease.ConfigurationRef.ProfileRef.Matches(profile) ||
            schedule.Schedule.RecordingDuration != profile.Duration ||
            profile.Duration != lease.PerRunDuration ||
            !string.Equals(slot.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            slot.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(slot.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(slot.TimeZoneId, schedule.Schedule.TimeZoneId, StringComparison.Ordinal) ||
            occurrence.Id != slot.OccurrenceId ||
            occurrence.PlanId != lease.PlanId ||
            occurrence.WindowStartUtc != slot.ScheduledStartUtc ||
            occurrence.WindowEndUtc != slot.PlannedEndUtc)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring reservation configuration or occurrence window is not exact.");
        }

        var recomputed = new RecurringPlanConfigurationRef(
            lease.PlanId,
            schedule.ScheduleRevision,
            schedule.ScheduleDigest,
            schedule.TimeZoneRulesDigest,
            binding.ProfileRef);
        if (!string.Equals(recomputed.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring configuration digest no longer matches its exact parents.");
        }
    }

    private static RecurringOccurrenceCandidate CalculateAndValidateCandidate(
        string occurrenceIdentity,
        RecurringConsentLease lease,
        RecurringScheduleVersionSnapshot schedule,
        RecurringOccurrenceSlotSnapshot slot)
    {
        RecurringOccurrenceCandidate candidate;
        try
        {
            candidate = new RecurringOccurrenceCalculator().CalculateCandidateForAuthorization(
                lease.PlanId,
                schedule.ScheduleRevision,
                schedule.Schedule,
                slot.LocalDate);
        }
        catch (Exception exception) when (exception is Phase3DomainException or ArgumentException or OverflowException)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The exact recurring occurrence candidate could not be recalculated.", exception);
        }

        if (!candidate.IsValid || candidate.Identity.Value != occurrenceIdentity ||
            candidate.ScheduledStartUtc != slot.ScheduledStartUtc ||
            candidate.LatestStartUtc != slot.LatestStartUtc ||
            candidate.PlannedEndUtc != slot.PlannedEndUtc ||
            candidate.ScheduleDigest != slot.ScheduleDigest ||
            candidate.TimeZoneId != slot.TimeZoneId)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring occurrence slot does not match the exact schedule calculation.");
        }

        return candidate;
    }

    private static void InsertRun(SqliteConnection connection, SqliteTransaction transaction, RecordingRun run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO recording_runs ({RunColumns}) VALUES ($id, $occurrenceId, $status, $crossed, NULL, NULL, NULL, $created, $updated, $version);";
        Add(command, "$id", RequiredInput(run.Id));
        Add(command, "$occurrenceId", RequiredInput(run.OccurrenceId));
        Add(command, "$status", run.StatusCode);
        Add(command, "$crossed", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$created", UtcTicksInput(run.CreatedAtUtc));
        Add(command, "$updated", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$version", run.Version);
        EnsureRowsAffected(command.ExecuteNonQuery());
    }

    private static void InsertRecurringUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        RecurringOccurrenceSlotSnapshot slot,
        RecurringLeaseUseAccountingEntry entry,
        DateTimeOffset trustedNowUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO recurring_lease_uses ({UseColumns}) VALUES ($useId, $leaseId, $planId, $identity, $occurrenceId, $runId, $status, 1, $reservedDuration, NULL, $created, $updated, 0);";
        Add(command, "$useId", RequiredInput(entry.UseId));
        Add(command, "$leaseId", RequiredInput(lease.LeaseId));
        Add(command, "$planId", RequiredInput(lease.PlanId));
        Add(command, "$identity", RequiredInput(entry.OccurrenceIdentity));
        Add(command, "$occurrenceId", RequiredInput(slot.OccurrenceId!));
        Add(command, "$runId", RequiredInput(entry.RunId));
        Add(command, "$status", entry.StatusCode);
        Add(command, "$reservedDuration", entry.ReservedDuration.Ticks);
        Add(command, "$created", UtcTicksInput(trustedNowUtc));
        Add(command, "$updated", UtcTicksInput(trustedNowUtc));
        EnsureRowsAffected(command.ExecuteNonQuery());
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
            SET status_code = $status,
                run_id = $runId,
                terminal_reason_code = NULL,
                updated_at_utc = $updated,
                version = $newVersion
            WHERE id = $id
              AND plan_id = $planId
              AND status_code = 'authorized'
              AND run_id IS NULL
              AND terminal_reason_code IS NULL
              AND version = $expectedVersion
              AND window_start_utc = $windowStart
              AND window_end_utc = $windowEnd
              AND created_at_utc = $created;
            """;
        Add(command, "$status", Phase3StateCodes.ToCode(PlanOccurrenceStatus.RunCreated));
        Add(command, "$runId", RequiredInput(occurrence.RunId!));
        Add(command, "$updated", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$newVersion", occurrence.Version);
        Add(command, "$id", RequiredInput(occurrence.Id));
        Add(command, "$planId", RequiredInput(occurrence.PlanId));
        Add(command, "$expectedVersion", expectedVersion);
        Add(command, "$windowStart", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$windowEnd", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created", UtcTicksInput(occurrence.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.Conflict, "The recurring occurrence changed before it could be claimed.");
        }
    }

    private static RecurringOccurrenceExecutionSpecification ValidateFinalReadback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        PlanDefinition plan,
        RecurringLeaseLocalApprovalEvidence? evidence,
        SqliteStandingLeaseSafetyGlobalState safety,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        RecurringOccurrenceSlotSnapshot slot,
        PlanOccurrence originalOccurrence,
        RecordingRun originalRun,
        RecurringLeaseUseAccountingEntry originalEntry,
        RecurringOccurrenceExecutionSpecification originalSpecification,
        DateTimeOffset trustedNowUtc)
    {
        var finalPlan = ReadPlan(connection, transaction, lease.PlanId);
        var finalLease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, lease.LeaseId)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring lease readback was missing.");
        var finalEvidence = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(connection, transaction, lease.LeaseId);
        var finalSafety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
        var finalSchedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring schedule readback was missing.");
        var finalBinding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(connection, transaction, lease.PlanId)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring profile binding readback was missing.");
        var finalProfile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection, transaction, lease.ConfigurationRef.ProfileRef);
        var finalSlot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
            connection, transaction, slot.OccurrenceIdentity, finalSchedule);
        var finalOccurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId!);
        var finalCandidate = CalculateAndValidateCandidate(
            slot.OccurrenceIdentity, finalLease, finalSchedule, finalSlot);
        var finalSpecification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection, transaction, slot.OccurrenceIdentity, finalOccurrence.Id)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring execution specification readback was missing.");
        var finalRun = ReadRunById(connection, transaction, originalRun.Id)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring run readback was missing.");
        var finalRows = ReadRawUsesByLease(connection, transaction, lease.LeaseId);
        var finalEntries = RehydrateAccountingRows(connection, transaction, finalLease, finalRows);
        var finalEntry = finalEntries.SingleOrDefault(row => row.Entry.UseId == originalEntry.UseId)
            ?? throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring use readback was missing.");
        _ = RecurringLeaseQuotaCalculator.Calculate(finalLease, finalEntries.Select(row => row.Entry));

        if (!string.Equals(finalSpecification.SpecificationDigest, originalSpecification.SpecificationDigest, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring execution specification changed before commit.");
        }

        if (finalEvidence is null || evidence is null)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The final recurring approval readback was missing.");
        }

        SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
            finalSpecification,
            finalPlan,
            finalSchedule,
            finalBinding,
            finalProfile,
            finalLease,
            finalEvidence,
            finalCandidate,
            finalOccurrence);

        if (finalPlan.IsOneTime || finalPlan.Status != plan.Status || finalPlan.Version != plan.Version || finalPlan.UpdatedAtUtc != plan.UpdatedAtUtc ||
            finalLease.Status != ConsentLeaseStatus.Active || finalLease.Version != lease.Version || finalLease.UpdatedAtUtc != lease.UpdatedAtUtc ||
            !EvidenceMatches(finalEvidence, evidence) ||
            finalSchedule.PlanId != schedule.PlanId || finalSchedule.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(finalSchedule.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(finalSchedule.TimeZoneRulesDigest, schedule.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            finalSchedule.CreatedAtUtc != schedule.CreatedAtUtc ||
            finalBinding.PlanId != binding.PlanId || !ProfileReferencesEqual(finalBinding.ProfileRef, binding.ProfileRef) ||
            finalBinding.BoundAtUtc != binding.BoundAtUtc ||
            finalProfile.ProfileId != profile.ProfileId || finalProfile.ProfileVersion != profile.ProfileVersion ||
            !string.Equals(finalProfile.ProfileDigest, profile.ProfileDigest, StringComparison.Ordinal) ||
            !finalSlot.Equals(slot) || finalOccurrence.Status != PlanOccurrenceStatus.RunCreated ||
            finalOccurrence.RunId != originalRun.Id || finalOccurrence.UpdatedAtUtc != trustedNowUtc ||
            finalRun.Status != RecordingRunStatus.Created || finalRun.Version != 0 || finalRun.HasCrossedStartCommit ||
            finalRun.OccurrenceId != slot.OccurrenceId || finalRun.CreatedAtUtc != trustedNowUtc || finalRun.UpdatedAtUtc != trustedNowUtc ||
            finalEntry.Entry.Status != LeaseUseStatus.Reserved || finalEntry.Entry.RunId != originalRun.Id ||
            finalEntry.Entry.OccurrenceIdentity != slot.OccurrenceIdentity || finalEntry.Entry.ReservedDuration != finalSpecification.Duration ||
            finalEntry.Version != 0 || finalEntry.CreatedAtUtc != trustedNowUtc || finalEntry.UpdatedAtUtc != trustedNowUtc ||
            !ProfileReferencesEqual(binding.ProfileRef, lease.ConfigurationRef.ProfileRef) || profile.Duration != lease.PerRunDuration)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
                "The recurring reservation final readback was not exact.");
        }

        if (!finalSafety.Equals(safety))
        {
            throw new Phase3PersistenceException(
                finalSafety.UnattendedMode == UnattendedModeStatus.Enabled
                    ? RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid
                    : RecurringOccurrenceReservationReasonCodes.UnattendedDisabled,
                "The unattended safety state changed during reservation.");
        }

        return finalSpecification;
    }

    private static bool EvidenceMatches(RecurringLeaseLocalApprovalEvidence left, RecurringLeaseLocalApprovalEvidence right) =>
        left.ApprovalId == right.ApprovalId && left.LeaseId == right.LeaseId && left.PlanId == right.PlanId &&
        left.ConfigurationDigest == right.ConfigurationDigest && left.AuthorizationDigest == right.AuthorizationDigest &&
        left.CurrentUserSid == right.CurrentUserSid && left.SessionBinding == right.SessionBinding &&
        left.ApprovedAtUtc == right.ApprovedAtUtc && left.ApprovalKind == right.ApprovalKind &&
        left.ApprovalVersion == right.ApprovalVersion && left.ApprovalDigest == right.ApprovalDigest;

    private static IReadOnlyList<RecordingRun> ReadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrenceId ORDER BY id COLLATE BINARY;";
        Add(command, "$occurrenceId", RequiredInput(occurrenceId));
        using var reader = command.ExecuteReader();
        var runs = new List<RecordingRun>();
        while (reader.Read())
        {
            runs.Add(ReadRecordingRunSnapshot(reader));
        }

        return runs;
    }

    private static RecordingRun? ReadRunById(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE id = $runId LIMIT 1;";
        Add(command, "$runId", RequiredInput(runId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecordingRunSnapshot(reader) : null;
    }

    private static IReadOnlyList<RawRecurringUseRow> ReadRawUsesByLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        return ReadRawUses(connection, transaction, "lease_id = $leaseId", ("$leaseId", leaseId));
    }

    private static IReadOnlyList<RawRecurringUseRow> ReadRawUsesByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity)
    {
        return ReadRawUses(connection, transaction, "occurrence_identity = $occurrenceIdentity", ("$occurrenceIdentity", occurrenceIdentity));
    }

    private static IReadOnlyList<RawRecurringUseRow> ReadRawUses(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string predicate,
        (string Name, string Value) parameter)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE {predicate} ORDER BY created_at_utc, use_id COLLATE BINARY;";
        Add(command, parameter.Name, RequiredInput(parameter.Value));
        using var reader = command.ExecuteReader();
        var rows = new List<RawRecurringUseRow>();
        while (reader.Read())
        {
            rows.Add(ReadRawUseRow(reader));
        }

        return rows;
    }

    private static RawRecurringUseRow ReadRawUseRow(SqliteDataReader reader)
    {
        var useId = ReadRequiredText(reader, 0);
        var leaseId = ReadRequiredText(reader, 1);
        var planId = ReadRequiredText(reader, 2);
        var identity = ReadRequiredText(reader, 3);
        var occurrenceId = ReadRequiredText(reader, 4);
        var runId = ReadRequiredText(reader, 5);
        var status = ParseStatus(reader, 6, Phase3StateCodes.ParseLeaseUse);
        var count = ReadInt32(reader, 7);
        var reservedDuration = ReadDurationTicks(reader, 8);
        var actualDuration = ReadNullableDurationTicks(reader, 9);
        var createdAt = ReadUtcDateTimeOffset(reader, 10);
        var updatedAt = ReadUtcDateTimeOffset(reader, 11);
        var version = ReadInt64(reader, 12);
        if (version < 0 || updatedAt < createdAt)
        {
            throw new PersistedSnapshotException("A recurring use row has non-monotonic metadata.");
        }

        return new RawRecurringUseRow(useId, leaseId, planId, identity, occurrenceId, runId, status, count, reservedDuration, actualDuration, createdAt, updatedAt, version);
    }

    private static IReadOnlyList<RecurringUseRow> RehydrateAccountingRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        IReadOnlyList<RawRecurringUseRow> rawRows)
    {
        var rows = rawRows.Select(row => RehydrateOneAccountingRow(connection, transaction, lease, row)).ToArray();
        try
        {
            _ = RecurringLeaseQuotaCalculator.Calculate(lease, rows.Select(row => row.Entry));
        }
        catch (Phase3DomainException exception)
        {
            throw new Phase3PersistenceException(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, "The recurring accounting projection is not a valid quota snapshot.", exception);
        }

        return rows;
    }

    private static RecurringUseRow RehydrateOneAccountingRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        RawRecurringUseRow row)
    {
        if (row.LeaseId != lease.LeaseId || row.PlanId != lease.PlanId)
        {
            throw new PersistedSnapshotException("A recurring use row points to another lease or plan.");
        }

        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection,
                transaction,
                lease.PlanId,
                lease.ConfigurationRef.ScheduleRevision)
            ?? throw new PersistedSnapshotException("A recurring use row points to a missing exact schedule.");
        var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
                connection,
                transaction,
                row.OccurrenceIdentity,
                schedule)
            ?? throw new PersistedSnapshotException("A recurring use row points to a missing exact slot.");
        if (!slot.IsScheduled || slot.OccurrenceId != row.OccurrenceId || slot.PlanId != row.PlanId)
        {
            throw new PersistedSnapshotException("A recurring use row points to an inconsistent occurrence slot.");
        }

        var run = ReadRunById(connection, transaction, row.RunId)
            ?? throw new PersistedSnapshotException("A recurring use row points to a missing run.");
        if (run.OccurrenceId != row.OccurrenceId)
        {
            throw new PersistedSnapshotException("A recurring use row points to a run for another occurrence.");
        }

        RecurringLeaseUseAccountingEntry entry;
        try
        {
            entry = RecurringLeaseUseAccountingEntry.Rehydrate(
                row.LeaseId,
                row.UseId,
                row.OccurrenceIdentity,
                row.RunId,
                row.Status,
                row.ReservedUseCount,
                row.ReservedDuration,
                row.ActualSettledDuration);
            entry.ValidateAgainst(lease);
        }
        catch (Phase3DomainException exception)
        {
            throw new PersistedSnapshotException("A recurring use row has invalid state evidence.", exception);
        }

        return new RecurringUseRow(entry, row.Version, row.CreatedAtUtc, row.UpdatedAtUtc, row.OccurrenceId, row.PlanId);
    }

    private static RecurringOccurrenceReservationResult CompleteReadOnly(
        SqliteTransaction transaction,
        RecurringOccurrenceReservationResult result)
    {
        transaction.Commit();
        return result;
    }

    private static RecurringOccurrenceReservationResult Rejected(
        RecurringOccurrenceReservationRequest request,
        string reasonCode,
        string? occurrenceId = null,
        string? runId = null,
        string? useId = null) =>
        RecurringOccurrenceReservationResult.Create(
            RecurringOccurrenceReservationStatus.Rejected,
            reasonCode,
            request.OccurrenceIdentity,
            occurrenceId,
            runId,
            useId);

    private static RecurringOccurrenceReservationResult MapFailure(
        RecurringOccurrenceReservationRequest request,
        Phase3PersistenceException exception)
    {
        var isConflict = exception.Code is
                "concurrency_conflict" or
                "constraint_violation" or
                "already_exists" or
                RecurringOccurrenceReservationReasonCodes.Conflict ||
            exception.Code.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
            exception.Code.Contains("unique", StringComparison.OrdinalIgnoreCase);
        return RecurringOccurrenceReservationResult.Create(
            isConflict ? RecurringOccurrenceReservationStatus.Conflict : RecurringOccurrenceReservationStatus.Rejected,
            isConflict ? RecurringOccurrenceReservationReasonCodes.Conflict : RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid,
            request.OccurrenceIdentity);
    }

    private static bool IsCanonical(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool ProfileReferencesEqual(ProfileRef left, ProfileRef right) =>
        left.ProfileId == right.ProfileId && left.ProfileVersion == right.ProfileVersion && left.ProfileDigest == right.ProfileDigest;

    private static TimeSpan ReadDurationTicks(SqliteDataReader reader, int ordinal)
    {
        var ticks = ReadInt64(reader, ordinal);
        if (ticks < 0 || ticks > TimeSpan.MaxValue.Ticks)
        {
            throw new PersistedSnapshotException("A recurring duration tick value was outside the TimeSpan range.");
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan? ReadNullableDurationTicks(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadDurationTicks(reader, ordinal);

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

    private sealed record RecurringUseRow(
        RecurringLeaseUseAccountingEntry Entry,
        long Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string OccurrenceId,
        string PlanId);
}
