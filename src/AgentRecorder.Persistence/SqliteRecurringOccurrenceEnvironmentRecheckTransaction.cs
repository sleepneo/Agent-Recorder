using System.IO;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringOccurrenceEnvironmentRecheckResultStatus
{
    Authorized,
    AlreadyAuthorized,
    Missed,
    Blocked,
    BeforeWindow,
    AlreadyTerminal,
    AlreadyAdvanced,
    Rejected,
    Conflict,
}

internal enum RecurringOccurrenceEnvironmentRecheckFailurePoint
{
    AfterFirstRecheckingCas,
    AfterSpecificationInsert,
    AfterFinalOccurrenceCas,
    AfterFinalReadback,
    BeforeCommit,
}

internal static class RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes
{
    internal const string RequestInvalid = "recurring_recheck_request_invalid";
    internal const string ClockInvalid = "recurring_recheck_clock_invalid";
    internal const string ClockMovedBackwards = "recurring_recheck_clock_moved_backwards";
    internal const string PersistedSnapshotInvalid = "recurring_recheck_persisted_snapshot_invalid";
    internal const string TransactionFailed = "recurring_recheck_transaction_failed";
    internal const string Conflict = "recurring_recheck_conflict";
    internal const string EnvironmentUnavailable = "execution_environment_unavailable";
    internal const string Authorized = "recurring_recheck_authorized";
    internal const string AlreadyAuthorized = "recurring_recheck_already_authorized";
    internal const string Missed = "recurring_recheck_missed";
    internal const string Blocked = "recurring_recheck_blocked";
    internal const string BeforeWindow = "recurring_recheck_before_window";
    internal const string AlreadyTerminal = "recurring_recheck_already_terminal";
    internal const string AlreadyAdvanced = "recurring_recheck_already_advanced";
}

internal sealed class RecurringOccurrenceEnvironmentRecheckResult
{
    private RecurringOccurrenceEnvironmentRecheckResult(
        RecurringOccurrenceEnvironmentRecheckResultStatus status,
        string reasonCode,
        bool changed,
        string occurrenceIdentity,
        string? occurrenceId,
        PlanOccurrenceStatus? finalOccurrenceStatus,
        long? finalOccurrenceVersion,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary)
    {
        Status = status;
        ReasonCode = reasonCode;
        Changed = changed;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        FinalOccurrenceStatus = finalOccurrenceStatus;
        FinalOccurrenceVersion = finalOccurrenceVersion;
        SpecificationSummary = specificationSummary;
    }

    internal RecurringOccurrenceEnvironmentRecheckResultStatus Status { get; }
    internal string StatusCode => Status.ToString();
    internal string ResultCode => StatusCode;
    internal string ReasonCode { get; }
    internal string Reason => ReasonCode;
    internal bool Changed { get; }
    internal bool Succeeded => Status is RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized or
        RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAuthorized;
    internal string OccurrenceIdentity { get; }
    internal string? OccurrenceId { get; }
    internal PlanOccurrenceStatus? FinalOccurrenceStatus { get; }
    internal long? FinalOccurrenceVersion { get; }
    internal RecurringOccurrenceExecutionSpecificationSummary? SpecificationSummary { get; }
    internal RecurringOccurrenceExecutionSpecificationSummary? ExecutionSpecificationSummary => SpecificationSummary;
    internal string? SpecificationDigest => SpecificationSummary?.SpecificationDigest;

    internal static RecurringOccurrenceEnvironmentRecheckResult Create(
        RecurringOccurrenceEnvironmentRecheckResultStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId = null,
        PlanOccurrenceStatus? finalOccurrenceStatus = null,
        long? finalOccurrenceVersion = null,
        bool changed = false,
        RecurringOccurrenceExecutionSpecificationSummary? specificationSummary = null) =>
        new(status, reasonCode, changed, occurrenceIdentity, occurrenceId, finalOccurrenceStatus, finalOccurrenceVersion, specificationSummary);
}

/// <summary>
/// Samples trusted time once and owns the public business boundary. The
/// production method accepts only lease_id and occurrence_identity; all other
/// facts are read from the immediate SQLite transaction or trusted providers.
/// </summary>
internal sealed class RecurringOccurrenceEnvironmentRecheckService
{
    private readonly SqliteOperationalStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProviderForTest;
    private readonly Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _afterFirstRecheckingCasHookForTest;
    private readonly object _clockGate = new();
    private DateTimeOffset? _lastTrustedNowUtc;

    internal RecurringOccurrenceEnvironmentRecheckService(
        SqliteOperationalStore store,
        Func<DateTimeOffset>? utcNowForTest = null,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? afterFirstRecheckingCasHookForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _environmentProviderForTest = environmentProviderForTest;
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
        _afterFirstRecheckingCasHookForTest = afterFirstRecheckingCasHookForTest;
    }

    internal RecurringOccurrenceEnvironmentRecheckResult Recheck(
        string leaseId,
        string occurrenceIdentity)
    {
        if (!IsCanonicalInput(leaseId) || !IsCanonicalInput(occurrenceIdentity))
        {
            return RecurringOccurrenceEnvironmentRecheckResult.Create(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.RequestInvalid,
                occurrenceIdentity ?? "");
        }

        DateTimeOffset trustedNowUtc;
        try
        {
            lock (_clockGate)
            {
                trustedNowUtc = _utcNow();
                if (trustedNowUtc.Offset != TimeSpan.Zero)
                {
                    return RecurringOccurrenceEnvironmentRecheckResult.Create(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.ClockInvalid,
                        occurrenceIdentity);
                }

                if (_lastTrustedNowUtc is { } previous && trustedNowUtc < previous)
                {
                    return RecurringOccurrenceEnvironmentRecheckResult.Create(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.ClockMovedBackwards,
                        occurrenceIdentity);
                }

                _lastTrustedNowUtc = trustedNowUtc;
            }
        }
        catch
        {
            return RecurringOccurrenceEnvironmentRecheckResult.Create(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.ClockInvalid,
                occurrenceIdentity);
        }

        return new SqliteRecurringOccurrenceEnvironmentRecheckTransaction(
            _store,
            _environmentProviderForTest,
            _failureHookForTest,
            _beforeFinalReadbackHookForTest,
            _afterFirstRecheckingCasHookForTest)
            .Recheck(leaseId, occurrenceIdentity, trustedNowUtc);
    }

    private static bool IsCanonicalInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}

internal sealed class SqliteRecurringOccurrenceEnvironmentRecheckTransaction : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";

    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProviderForTest;
    private readonly Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? _failureHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeFinalReadbackHookForTest;
    private readonly Action<SqliteConnection, SqliteTransaction>? _afterFirstRecheckingCasHookForTest;

    internal SqliteRecurringOccurrenceEnvironmentRecheckTransaction(
        SqliteOperationalStore store,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeFinalReadbackHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? afterFirstRecheckingCasHookForTest = null)
        : base(store)
    {
        _environmentProviderForTest = environmentProviderForTest;
        _failureHookForTest = failureHookForTest;
        _beforeFinalReadbackHookForTest = beforeFinalReadbackHookForTest;
        _afterFirstRecheckingCasHookForTest = afterFirstRecheckingCasHookForTest;
    }

    internal RecurringOccurrenceEnvironmentRecheckResult Recheck(
        string leaseId,
        string occurrenceIdentity,
        DateTimeOffset trustedNowUtc)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        PlanOccurrenceStatus? originalOccurrenceStatus = null;
        long? originalOccurrenceVersion = null;
        string? loadedOccurrenceId = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var occurrenceMetadata = ReadOccurrenceMetadata(connection, transaction, occurrenceIdentity);
            if (occurrenceMetadata is { } metadata)
            {
                loadedOccurrenceId = metadata.Id;
                originalOccurrenceStatus = metadata.Status;
                originalOccurrenceVersion = metadata.Version;
            }

            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
                ?? throw SnapshotFailure("The exact recurring lease was not found.");
            var plan = ReadPlan(connection, transaction, lease.PlanId);
            var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                    connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
                ?? throw SnapshotFailure("The exact recurring schedule version was not found.");
            var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(
                    connection, transaction, lease.PlanId)
                ?? throw SnapshotFailure("The exact recurring profile binding was not found.");
            var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
                connection, transaction, lease.ConfigurationRef.ProfileRef);
            var approval = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(
                    connection, transaction, lease.LeaseId)
                ?? throw SnapshotFailure("The exact recurring local approval evidence was not found.");
            var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
            var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
                connection, transaction, occurrenceIdentity, schedule);
            if (!slot.IsScheduled || slot.OccurrenceId is null ||
                !string.Equals(slot.OccurrenceIdentity, occurrenceIdentity, StringComparison.Ordinal))
            {
                throw SnapshotFailure("The recurring occurrence slot is not a scheduled exact slot.");
            }

            var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
            loadedOccurrenceId = occurrence.Id;
            originalOccurrenceStatus = occurrence.Status;
            originalOccurrenceVersion = occurrence.Version;
            var candidate = CreateCandidate(slot, schedule);
            var existingSpecification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(connection, transaction, occurrenceIdentity, occurrence.Id);
            var claims = ReadExecutionClaims(connection, transaction, lease, occurrence, occurrenceIdentity);

            if (existingSpecification is not null)
            {
                if (!string.Equals(existingSpecification.LeaseId, lease.LeaseId, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return Result(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Conflict,
                        occurrenceIdentity,
                        occurrence.Id,
                        occurrence.Status,
                        occurrence.Version);
                }

                SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
                    existingSpecification,
                    plan,
                    schedule,
                    binding,
                    profile,
                    lease,
                    approval,
                    candidate,
                    occurrence);

                if (occurrence.Status == PlanOccurrenceStatus.Authorized)
                {
                    var safetyBoundary = ApplyAuthorizedSafetyBoundary(
                        connection,
                        transaction,
                        plan,
                        schedule,
                        occurrence,
                        lease,
                        candidate,
                        binding,
                        profile,
                        approval,
                        safety,
                        trustedNowUtc,
                        occurrenceIdentity);
                    if (safetyBoundary is not null)
                    {
                        transaction.Commit();
                        return safetyBoundary;
                    }

                    transaction.Commit();
                    return Result(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAuthorized,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyAuthorized,
                        occurrenceIdentity,
                        occurrence.Id,
                        occurrence.Status,
                        occurrence.Version,
                        existingSpecification);
                }

                if (Phase3TransitionGuards.IsTerminal(occurrence.Status))
                {
                    transaction.Commit();
                    return Result(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyTerminal,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyTerminal,
                        occurrenceIdentity,
                        occurrence.Id,
                        occurrence.Status,
                        occurrence.Version);
                }

                if (claims.HasClaim && occurrence.Status == PlanOccurrenceStatus.RunCreated)
                {
                    transaction.Commit();
                    return Result(
                        RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAdvanced,
                        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyAdvanced,
                        occurrenceIdentity,
                        occurrence.Id,
                        occurrence.Status,
                        occurrence.Version,
                        existingSpecification);
                }

                throw SnapshotFailure("A non-terminal non-authorized occurrence already has an execution specification.");
            }

            if (Phase3TransitionGuards.IsTerminal(occurrence.Status))
            {
                transaction.Commit();
                return Result(
                    RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyTerminal,
                    RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyTerminal,
                    occurrenceIdentity,
                    occurrence.Id,
                    occurrence.Status,
                    occurrence.Version);
            }

            if (claims.HasClaim)
            {
                transaction.Commit();
                return Result(
                    RecurringOccurrenceEnvironmentRecheckResultStatus.AlreadyAdvanced,
                    RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.AlreadyAdvanced,
                    occurrenceIdentity,
                    occurrence.Id,
                    occurrence.Status,
                    occurrence.Version);
            }

            if (occurrence.Status is not (PlanOccurrenceStatus.Due or PlanOccurrenceStatus.Rechecking))
            {
                transaction.Commit();
                return Result(
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                    occurrence.Status == PlanOccurrenceStatus.Authorized
                        ? RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid
                        : RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceNotRechecking,
                    occurrenceIdentity,
                    occurrence.Id,
                    originalOccurrenceStatus,
                    originalOccurrenceVersion);
            }

            var originalStatus = occurrence.Status;
            var originalVersion = occurrence.Version;
            if (originalStatus == PlanOccurrenceStatus.Due)
            {
                RequireTransition(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, trustedNowUtc));
            }

            var request = new RecurringOccurrenceEnvironmentRecheckRequest(
                plan,
                schedule.Schedule,
                occurrence,
                lease,
                candidate,
                binding,
                profile,
                approval,
                new StandingLeaseExecutionEnvironment(trustedNowUtc, "", "", isInteractiveDesktop: false));

            var preEnvironmentDecision = RecurringOccurrenceEnvironmentRecheckPolicy.EvaluateBeforeEnvironment(request);
            if (preEnvironmentDecision is not null)
            {
                if (preEnvironmentDecision.Status is RecurringOccurrenceEnvironmentRecheckStatus.Missed or RecurringOccurrenceEnvironmentRecheckStatus.Blocked)
                {
                    PersistInitialRecheckingCasIfNeeded(connection, transaction, occurrence, originalStatus, originalVersion);
                    var completed = ApplyPreEnvironmentDecision(
                        connection,
                        transaction,
                        occurrence,
                        occurrenceIdentity,
                        preEnvironmentDecision,
                        trustedNowUtc);
                    transaction.Commit();
                    return completed!;
                }

                transaction.Rollback();
                return Result(
                    preEnvironmentDecision.Status == RecurringOccurrenceEnvironmentRecheckStatus.BeforeWindow
                        ? RecurringOccurrenceEnvironmentRecheckResultStatus.BeforeWindow
                        : RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                    preEnvironmentDecision.ReasonCode,
                    occurrenceIdentity,
                    occurrence.Id,
                    originalStatus,
                    originalVersion);
            }

            PersistInitialRecheckingCasIfNeeded(connection, transaction, occurrence, originalStatus, originalVersion);

            var safetyFailure = EvaluateRecurringSafety(lease, approval, safety);
            if (safetyFailure is not null)
            {
                var blocked = ApplyTerminalDecision(
                    connection,
                    transaction,
                    occurrence,
                    occurrenceIdentity,
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                    safetyFailure,
                    trustedNowUtc);
                transaction.Commit();
                return blocked;
            }

            var requirements = CreateRequirements(profile, candidate, approval);
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
                var blocked = ApplyTerminalDecision(
                    connection,
                    transaction,
                    occurrence,
                    occurrenceIdentity,
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                    RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.EnvironmentUnavailable,
                    trustedNowUtc);
                transaction.Commit();
                return blocked;
            }

            var fullDecision = RecurringOccurrenceEnvironmentRecheckPolicy.Evaluate(request with { Environment = environment });
            if (fullDecision.Status == RecurringOccurrenceEnvironmentRecheckStatus.BeforeWindow)
            {
                transaction.Rollback();
                return Result(
                    RecurringOccurrenceEnvironmentRecheckResultStatus.BeforeWindow,
                    RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.BeforeWindow,
                    occurrenceIdentity,
                    occurrence.Id,
                    originalStatus,
                    originalVersion);
            }

            if (fullDecision.Status == RecurringOccurrenceEnvironmentRecheckStatus.Missed)
            {
                var missed = ApplyTerminalDecision(
                    connection,
                    transaction,
                    occurrence,
                    occurrenceIdentity,
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Missed,
                    fullDecision.ReasonCode,
                    trustedNowUtc);
                transaction.Commit();
                return missed;
            }

            if (fullDecision.Status == RecurringOccurrenceEnvironmentRecheckStatus.Blocked)
            {
                var blocked = ApplyTerminalDecision(
                    connection,
                    transaction,
                    occurrence,
                    occurrenceIdentity,
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                    fullDecision.ReasonCode,
                    trustedNowUtc);
                transaction.Commit();
                return blocked;
            }

            if (fullDecision.Status != RecurringOccurrenceEnvironmentRecheckStatus.Eligible ||
                fullDecision.ExecutionSpecification is null)
            {
                transaction.Rollback();
                return Result(
                    RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                    fullDecision.ReasonCode,
                    occurrenceIdentity,
                    occurrence.Id,
                    originalStatus,
                    originalVersion);
            }

            var specification = fullDecision.ExecutionSpecification;
            InsertSpecification(connection, transaction, specification);
            _failureHookForTest?.Invoke(RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterSpecificationInsert);

            var expectedFinalVersion = occurrence.Version;
            RequireTransition(occurrence.TryTransition(PlanOccurrenceStatus.Authorized, trustedNowUtc));
            UpdateOccurrenceForRecheck(connection, transaction, occurrence, expectedFinalVersion, PlanOccurrenceStatus.Rechecking);
            _failureHookForTest?.Invoke(RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterFinalOccurrenceCas);

            _beforeFinalReadbackHookForTest?.Invoke(connection, transaction);
            var finalOccurrence = ReadOccurrence(connection, transaction, occurrence.Id);
            EnsureOccurrenceReadback(occurrence, finalOccurrence);
            var readback = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(connection, transaction, occurrenceIdentity, finalOccurrence.Id)
                ?? throw SnapshotFailure("The execution specification disappeared before commit.");
            SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
                readback,
                plan,
                schedule,
                binding,
                profile,
                lease,
                approval,
                candidate,
                finalOccurrence);
            if (finalOccurrence.Status != PlanOccurrenceStatus.Authorized)
                throw SnapshotFailure("The recurring occurrence did not read back as authorized.");
            _failureHookForTest?.Invoke(RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterFinalReadback);
            _failureHookForTest?.Invoke(RecurringOccurrenceEnvironmentRecheckFailurePoint.BeforeCommit);
            transaction.Commit();

            return Result(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Authorized,
                occurrenceIdentity,
                finalOccurrence.Id,
                finalOccurrence.Status,
                finalOccurrence.Version,
                readback,
                changed: true);
        }
        catch (Phase3PersistenceException exception)
        {
            TryRollback(transaction);
            return Result(
                exception.Code == "concurrency_conflict"
                    ? RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict
                    : RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                MapPersistenceFailureReason(exception.Code),
                occurrenceIdentity,
                loadedOccurrenceId,
                originalOccurrenceStatus,
                originalOccurrenceVersion);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            TryRollback(transaction);
            return Result(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Conflict,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Conflict,
                occurrenceIdentity,
                loadedOccurrenceId,
                originalOccurrenceStatus,
                originalOccurrenceVersion);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            TryRollback(transaction);
            return Result(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid,
                occurrenceIdentity,
                loadedOccurrenceId,
                originalOccurrenceStatus,
                originalOccurrenceVersion);
        }
        catch
        {
            TryRollback(transaction);
            return Result(
                RecurringOccurrenceEnvironmentRecheckResultStatus.Rejected,
                RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.TransactionFailed,
                occurrenceIdentity,
                loadedOccurrenceId,
                originalOccurrenceStatus,
                originalOccurrenceVersion);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private void PersistInitialRecheckingCasIfNeeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        PlanOccurrenceStatus originalStatus,
        long originalVersion)
    {
        if (originalStatus != PlanOccurrenceStatus.Due)
            return;

        UpdateOccurrenceForRecheck(
            connection,
            transaction,
            occurrence,
            originalVersion,
            PlanOccurrenceStatus.Due);
        _failureHookForTest?.Invoke(RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterFirstRecheckingCas);
        _afterFirstRecheckingCasHookForTest?.Invoke(connection, transaction);
    }

    private static void EnsureOccurrenceReadback(PlanOccurrence expected, PlanOccurrence actual)
    {
        if (!string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(actual.PlanId, expected.PlanId, StringComparison.Ordinal) ||
            actual.Status != expected.Status ||
            actual.WindowStartUtc != expected.WindowStartUtc ||
            actual.WindowEndUtc != expected.WindowEndUtc ||
            !string.Equals(actual.RunId, expected.RunId, StringComparison.Ordinal) ||
            !string.Equals(actual.TerminalReasonCode, expected.TerminalReasonCode, StringComparison.Ordinal) ||
            actual.CreatedAtUtc != expected.CreatedAtUtc ||
            actual.UpdatedAtUtc != expected.UpdatedAtUtc ||
            actual.Version != expected.Version)
        {
            throw SnapshotFailure("The final recurring occurrence readback did not match the committed transition.");
        }
    }

    private static RecurringOccurrenceEnvironmentRecheckResult? ApplyPreEnvironmentDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        string occurrenceIdentity,
        RecurringOccurrenceEnvironmentRecheckDecision decision,
        DateTimeOffset trustedNowUtc)
    {
        return decision.Status switch
        {
            RecurringOccurrenceEnvironmentRecheckStatus.BeforeWindow => null,
            RecurringOccurrenceEnvironmentRecheckStatus.Missed => ApplyTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Missed,
                decision.ReasonCode,
                trustedNowUtc),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked => ApplyTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                decision.ReasonCode,
                trustedNowUtc),
            _ => null,
        };
    }

    private static RecurringOccurrenceEnvironmentRecheckResult? ApplyAuthorizedSafetyBoundary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        PlanOccurrence occurrence,
        RecurringConsentLease lease,
        RecurringOccurrenceCandidate candidate,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        RecurringLeaseLocalApprovalEvidence approval,
        SqliteStandingLeaseSafetyGlobalState safety,
        DateTimeOffset trustedNowUtc,
        string occurrenceIdentity)
    {
        var safetyFailure = EvaluateRecurringSafety(lease, approval, safety);
        if (safetyFailure is not null)
        {
            return ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                safetyFailure,
                trustedNowUtc);
        }

        if (lease.Status != ConsentLeaseStatus.Active)
        {
            return ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseNotActive,
                trustedNowUtc);
        }

        if (trustedNowUtc < lease.ValidFromUtc || trustedNowUtc >= lease.ValidUntilUtc)
        {
            return ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Blocked,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseOutsideValidity,
                trustedNowUtc);
        }

        if (trustedNowUtc < candidate.ScheduledStartUtc!.Value)
            return null;

        if (trustedNowUtc > candidate.LatestStartUtc!.Value)
        {
            return ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Missed,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLatestStart,
                trustedNowUtc);
        }

        DateTimeOffset captureEndUtc;
        try
        {
            captureEndUtc = trustedNowUtc.Add(profile.Duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (captureEndUtc > candidate.PlannedEndUtc)
        {
            return ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Missed,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedPlannedEnd,
                trustedNowUtc);
        }

        return captureEndUtc > lease.ValidUntilUtc
            ? ApplyAuthorizedTerminalDecision(
                connection,
                transaction,
                occurrence,
                occurrenceIdentity,
                RecurringOccurrenceEnvironmentRecheckResultStatus.Missed,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLeaseValidity,
                trustedNowUtc)
            : null;
    }

    private static RecurringOccurrenceEnvironmentRecheckResult ApplyAuthorizedTerminalDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        string occurrenceIdentity,
        RecurringOccurrenceEnvironmentRecheckResultStatus status,
        string reasonCode,
        DateTimeOffset trustedNowUtc)
    {
        var expectedVersion = occurrence.Version;
        var targetStatus = status == RecurringOccurrenceEnvironmentRecheckResultStatus.Missed
            ? PlanOccurrenceStatus.Missed
            : PlanOccurrenceStatus.Blocked;
        RequireTransition(occurrence.TryTransition(targetStatus, trustedNowUtc, reasonCode));
        UpdateOccurrenceForRecheck(connection, transaction, occurrence, expectedVersion, PlanOccurrenceStatus.Authorized);
        return Result(status, reasonCode, occurrenceIdentity, occurrence.Id, occurrence.Status, occurrence.Version, changed: true);
    }

    private static RecurringOccurrenceEnvironmentRecheckResult ApplyTerminalDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        string occurrenceIdentity,
        RecurringOccurrenceEnvironmentRecheckResultStatus status,
        string reasonCode,
        DateTimeOffset trustedNowUtc)
    {
        var expectedVersion = occurrence.Version;
        var targetStatus = status == RecurringOccurrenceEnvironmentRecheckResultStatus.Missed
            ? PlanOccurrenceStatus.Missed
            : PlanOccurrenceStatus.Blocked;
        RequireTransition(occurrence.TryTransition(targetStatus, trustedNowUtc, reasonCode));
        UpdateOccurrenceForRecheck(connection, transaction, occurrence, expectedVersion, PlanOccurrenceStatus.Rechecking);
        return Result(status, reasonCode, occurrenceIdentity, occurrence.Id, occurrence.Status, occurrence.Version, changed: true);
    }

    private static void UpdateOccurrenceForRecheck(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanOccurrence occurrence,
        long expectedVersion,
        PlanOccurrenceStatus expectedStatus)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_occurrences
            SET status_code = $status_code,
                run_id = $run_id,
                terminal_reason_code = $terminal_reason_code,
                updated_at_utc = $updated_at_utc,
                version = $version
            WHERE id = $id
              AND plan_id = $plan_id
              AND status_code = $expected_status
              AND version = $expected_version
              AND window_start_utc = $window_start_utc
              AND window_end_utc = $window_end_utc
              AND created_at_utc = $created_at_utc;
            """;
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", occurrence.RunId);
        Add(command, "$terminal_reason_code", occurrence.TerminalReasonCode);
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$version", occurrence.Version);
        Add(command, "$id", RequiredInput(occurrence.Id));
        Add(command, "$plan_id", RequiredInput(occurrence.PlanId));
        Add(command, "$expected_status", Phase3StateCodes.ToCode(expectedStatus));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException("concurrency_conflict", "The recurring occurrence changed during environment recheck.");
    }

    private static RecurringOccurrenceCandidate CreateCandidate(
        RecurringOccurrenceSlotSnapshot slot,
        RecurringScheduleVersionSnapshot schedule)
    {
        var identity = RecurringOccurrenceIdentity.Create(
            slot.PlanId,
            slot.ScheduleRevision,
            schedule.Schedule,
            slot.LocalDate,
            slot.LocalWallClockTime);
        return new RecurringOccurrenceCandidate(
            slot.PlanId,
            slot.ScheduleRevision,
            schedule.Schedule,
            slot.LocalDate,
            slot.LocalWallClockTime,
            identity,
            slot.ScheduledStartUtc,
            slot.LatestStartUtc,
            slot.PlannedEndUtc,
            slot.TerminalReasonCode ?? "",
            slot.ResolutionCode);
    }

    private static FixedRegionExecutionEnvironmentRequirements CreateRequirements(
        RecurringFixedRegionProfileVersion profile,
        RecurringOccurrenceCandidate candidate,
        RecurringLeaseLocalApprovalEvidence approval) =>
        new(
            approval.CurrentUserSid,
            approval.SessionBinding,
            profile.StableDisplayFingerprint,
            profile.DisplayBounds,
            profile.RegionWithinDisplay,
            profile.VirtualScreenRegion,
            profile.DpiX,
            profile.DpiY,
            profile.PhysicalWidth,
            profile.PhysicalHeight,
            profile.Orientation,
            profile.TopologyDigest,
            StandingLeaseOutputPath.NormalizeDirectory(profile.OutputDirectory),
            profile.ResolveOutputPath(candidate.Identity.Value, candidate.ScheduledStartUtc!.Value),
            profile.OutputConflictPolicy,
            profile.Duration);

    private static void InsertSpecification(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringOccurrenceExecutionSpecification specification)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
            command.CommandText = $"""
            INSERT INTO recurring_occurrence_execution_specs ({SqliteRecurringOccurrenceExecutionSpecificationReader.SpecColumns})
            VALUES ($occurrence_identity, $occurrence_id, $plan_id, $lease_id, $schedule_revision, $schedule_digest, $time_zone_rules_digest, $profile_id, $profile_version, $profile_digest, $configuration_digest, $lease_authorization_digest, $local_approval_id, $local_approval_digest, $scheduled_start_utc, $latest_start_utc, $planned_end_utc, $evaluated_at_utc, $stable_display_fingerprint, $display_bounds_x, $display_bounds_y, $display_bounds_width, $display_bounds_height, $region_x, $region_y, $region_width, $region_height, $virtual_region_x, $virtual_region_y, $virtual_region_width, $virtual_region_height, $dpi_x, $dpi_y, $physical_width, $physical_height, $orientation_code, $topology_digest, $backend_code, $audio_mode_code, $duration_ticks, $countdown_seconds, $normalized_output_directory, $frozen_output_file_name, $frozen_output_file_path, $output_conflict_policy_code, $approved_current_user_sid, $approved_session_binding, $specification_version, $specification_digest);
            """;
        Add(command, "$occurrence_identity", specification.OccurrenceIdentity);
        Add(command, "$occurrence_id", specification.OccurrenceId);
        Add(command, "$plan_id", specification.PlanId);
        Add(command, "$lease_id", specification.LeaseId);
        Add(command, "$schedule_revision", specification.ScheduleRevision);
        Add(command, "$schedule_digest", specification.ScheduleDigest);
        Add(command, "$time_zone_rules_digest", specification.TimeZoneRulesDigest);
        Add(command, "$profile_id", specification.ProfileId);
        Add(command, "$profile_version", specification.ProfileVersion);
        Add(command, "$profile_digest", specification.ProfileDigest);
        Add(command, "$configuration_digest", specification.ConfigurationDigest);
        Add(command, "$lease_authorization_digest", specification.LeaseAuthorizationDigest);
        Add(command, "$local_approval_id", specification.LocalApprovalId);
        Add(command, "$local_approval_digest", specification.LocalApprovalDigest);
        Add(command, "$scheduled_start_utc", UtcTicksInput(specification.ScheduledStartUtc));
        Add(command, "$latest_start_utc", UtcTicksInput(specification.LatestStartUtc));
        Add(command, "$planned_end_utc", UtcTicksInput(specification.PlannedEndUtc));
        Add(command, "$evaluated_at_utc", UtcTicksInput(specification.EvaluatedAtUtc));
        Add(command, "$stable_display_fingerprint", specification.StableDisplayFingerprint);
        AddRectangle(command, "display_bounds", specification.DisplayBounds);
        AddRectangle(command, "region", specification.RegionWithinDisplay);
        AddRectangle(command, "virtual_region", specification.VirtualScreenRegion);
        Add(command, "$dpi_x", specification.DpiX);
        Add(command, "$dpi_y", specification.DpiY);
        Add(command, "$physical_width", specification.PhysicalWidth);
        Add(command, "$physical_height", specification.PhysicalHeight);
        Add(command, "$orientation_code", RecurringFixedRegionProfileCode.ToCode(specification.Orientation));
        Add(command, "$topology_digest", specification.TopologyDigest);
        Add(command, "$backend_code", RecurringFixedRegionProfileCode.ToCode(specification.Backend));
        Add(command, "$audio_mode_code", RecurringFixedRegionProfileCode.ToCode(specification.AudioMode));
        Add(command, "$duration_ticks", specification.Duration.Ticks);
        Add(command, "$countdown_seconds", specification.CountdownSeconds);
        Add(command, "$normalized_output_directory", specification.NormalizedOutputDirectory);
        Add(command, "$frozen_output_file_name", specification.FrozenOutputFileName);
        Add(command, "$frozen_output_file_path", specification.FrozenOutputFilePath);
        Add(command, "$output_conflict_policy_code", RecurringFixedRegionProfileCode.ToCode(specification.OutputConflictPolicy));
        Add(command, "$approved_current_user_sid", specification.ApprovedCurrentUserSid);
        Add(command, "$approved_session_binding", specification.ApprovedSessionBinding);
        Add(command, "$specification_version", RecurringOccurrenceExecutionSpecification.CanonicalVersion);
        Add(command, "$specification_digest", specification.SpecificationDigest);
        if (command.ExecuteNonQuery() != 1)
            throw new Phase3PersistenceException(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.TransactionFailed, "The execution specification insert did not affect exactly one row.");
    }

    private static void AddRectangle(SqliteCommand command, string prefix, AuthorizedPhysicalRectangle rectangle)
    {
        Add(command, "$" + prefix + "_x", rectangle.X);
        Add(command, "$" + prefix + "_y", rectangle.Y);
        Add(command, "$" + prefix + "_width", rectangle.Width);
        Add(command, "$" + prefix + "_height", rectangle.Height);
    }

    

    private sealed record ExecutionClaims(bool HasClaim);

    private static ExecutionClaims ReadExecutionClaims(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        string occurrenceIdentity)
    {
        var runs = new List<RecordingRun>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id OR ($run_id IS NOT NULL AND id = $run_id);";
            Add(command, "$occurrence_id", occurrence.Id);
            Add(command, "$run_id", occurrence.RunId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var run = ReadRecordingRunSnapshot(reader);
                if (!string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
                    throw SnapshotFailure("A recording run points to a different occurrence.");
                runs.Add(run);
            }
        }

        if (runs.Count > 1)
            throw SnapshotFailure("The recurring occurrence has multiple recording runs.");

        var uses = new List<RecurringLeaseUseAccountingEntry>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE occurrence_identity = $occurrence_identity OR occurrence_id = $occurrence_id;";
            Add(command, "$occurrence_identity", occurrenceIdentity);
            Add(command, "$occurrence_id", occurrence.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var useId = ReadRequiredText(reader, 0);
                var rowLeaseId = ReadRequiredText(reader, 1);
                var planId = ReadRequiredText(reader, 2);
                var rowIdentity = ReadRequiredText(reader, 3);
                var rowOccurrenceId = ReadRequiredText(reader, 4);
                var runId = ReadRequiredText(reader, 5);
                var status = ParseStatus(reader, 6, Phase3StateCodes.ParseLeaseUse);
                var reservedCount = ReadInt32(reader, 7);
                var reservedTicks = ReadInt64(reader, 8);
                var actualTicks = reader.IsDBNull(9) ? (long?)null : ReadInt64(reader, 9);
                var createdAt = ReadUtcDateTimeOffset(reader, 10);
                var updatedAt = ReadUtcDateTimeOffset(reader, 11);
                var version = ReadInt64(reader, 12);
                if (!string.Equals(rowIdentity, occurrenceIdentity, StringComparison.Ordinal) ||
                    !string.Equals(rowOccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
                    !string.Equals(rowLeaseId, lease.LeaseId, StringComparison.Ordinal) ||
                    !string.Equals(planId, lease.PlanId, StringComparison.Ordinal) ||
                    createdAt.Offset != TimeSpan.Zero || updatedAt < createdAt || version < 0)
                {
                    throw SnapshotFailure("The recurring use claim is not a canonical exact claim.");
                }

                if (runs.Count == 1 && !string.Equals(runId, runs[0].Id, StringComparison.Ordinal))
                    throw SnapshotFailure("The recurring use claim does not reference the exact recording run.");

                var entry = RecurringLeaseUseAccountingEntry.Rehydrate(
                    rowLeaseId,
                    useId,
                    rowIdentity,
                    runId,
                    status,
                    reservedCount,
                    TimeSpan.FromTicks(reservedTicks),
                    actualTicks is null ? null : TimeSpan.FromTicks(actualTicks.Value));
                entry.ValidateAgainst(lease);
                uses.Add(entry);
            }
        }

        if (runs.Count > 1 || uses.Count > 1)
            throw SnapshotFailure("The recurring occurrence has multiple execution claims.");
        if (occurrence.RunId is null)
        {
            if (runs.Count != 0)
                throw SnapshotFailure("A recording run exists without the occurrence run relation.");
        }
        else if (runs.Count != 1 || !string.Equals(runs[0].Id, occurrence.RunId, StringComparison.Ordinal))
        {
            throw SnapshotFailure("The occurrence run relation is not canonical.");
        }

        if (runs.Count == 1)
        {
            if (uses.Count != 1)
                throw SnapshotFailure("A recording run does not have exactly one recurring use claim.");
        }
        else if (uses.Count != 0)
        {
            throw SnapshotFailure("A recurring use exists without its recording run.");
        }

        return new ExecutionClaims(runs.Count == 1 && uses.Count == 1);
    }

    private static OccurrenceMetadata? ReadOccurrenceMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.id, o.status_code, o.version
            FROM recurring_occurrence_slots AS s
            JOIN plan_occurrences AS o ON o.id = s.occurrence_id
            WHERE s.occurrence_identity = $occurrence_identity;
            """;
        Add(command, "$occurrence_identity", RequiredInput(occurrenceIdentity));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new OccurrenceMetadata(
            ReadRequiredText(reader, 0),
            ParseStatus(reader, 1, Phase3StateCodes.ParsePlanOccurrence),
            ReadInt64(reader, 2));
    }

    private sealed record OccurrenceMetadata(string Id, PlanOccurrenceStatus Status, long Version);

    private static RecurringOccurrenceEnvironmentRecheckResult Result(
        RecurringOccurrenceEnvironmentRecheckResultStatus status,
        string reasonCode,
        string occurrenceIdentity,
        string? occurrenceId = null,
        PlanOccurrenceStatus? finalOccurrenceStatus = null,
        long? finalOccurrenceVersion = null,
        RecurringOccurrenceExecutionSpecification? specification = null,
        bool changed = false) =>
        RecurringOccurrenceEnvironmentRecheckResult.Create(
            status,
            reasonCode,
            occurrenceIdentity,
            occurrenceId,
            finalOccurrenceStatus,
            finalOccurrenceVersion,
            changed,
            specification is null ? null : RecurringOccurrenceExecutionSpecificationSummary.From(specification));

    private static string MapPersistenceFailureReason(string code) => code switch
    {
        "concurrency_conflict" => RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.Conflict,
        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.TransactionFailed => RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.TransactionFailed,
        RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid => RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid,
        _ => RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid,
    };

    private static string? EvaluateRecurringSafety(
        RecurringConsentLease lease,
        RecurringLeaseLocalApprovalEvidence approval,
        SqliteStandingLeaseSafetyGlobalState safety)
    {
        if (safety.UnattendedMode != UnattendedModeStatus.Enabled)
            return StandingLeaseSafetyReasonCodes.UnattendedDisabled;
        if (safety.StopAllApplied &&
            safety.StopAllAppliedAtUtc is { } stopAllAt &&
            approval.ApprovedAtUtc <= stopAllAt)
        {
            return StandingLeaseSafetyReasonCodes.StopAllActive;
        }
        if (lease.Status == ConsentLeaseStatus.Revoked)
            return StandingLeaseSafetyReasonCodes.LeaseRevoked;
        if (lease.Status == ConsentLeaseStatus.Active &&
            safety.UnattendedEnabledAtUtc is { } enabledAt &&
            lease.UpdatedAtUtc < enabledAt)
        {
            return StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization;
        }

        return null;
    }

    private static Phase3PersistenceException SnapshotFailure(string message) =>
        new(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, message);

    private static void RequireTransition(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
            throw new Phase3PersistenceException(RecurringOccurrenceEnvironmentRecheckPersistenceReasonCodes.PersistedSnapshotInvalid, "The recurring occurrence transition was rejected.");
    }

    private static new void TryRollback(SqliteTransaction? transaction)
    {
        try { transaction?.Rollback(); } catch { }
    }

}
