using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal readonly record struct RecurringLeaseExecutionSnapshotLookup(
    string LeaseId,
    string? PlanId,
    string OccurrenceIdentity,
    string? OccurrenceId,
    string RunId,
    string LeaseUseId)
{
    internal static RecurringLeaseExecutionSnapshotLookup FromProof(RecurringLeaseUseProof proof) =>
        new(
            proof.LeaseId,
            PlanId: null,
            proof.OccurrenceIdentity,
            OccurrenceId: null,
            proof.RunId,
            proof.LeaseUseId);

    internal static RecurringLeaseExecutionSnapshotLookup FromTicket(
        RecurringLeaseCaptureExecutionTicket ticket) =>
        new(
            ticket.LeaseId,
            ticket.PlanId,
            ticket.OccurrenceIdentity,
            ticket.OccurrenceId,
            ticket.RunId,
            ticket.UseId);
}

/// <summary>
/// The sole production entry for consuming a recurring lease-use proof. The
/// SQLite immediate transaction remains open from exact snapshot loading
/// through environment validation and the final process-local proof consume.
/// </summary>
internal sealed class SqliteRecurringLeaseExecutionSnapshotLoader : SqlitePeriodicOccurrenceDueRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "use_id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ticks, actual_settled_duration_ticks, created_at_utc, updated_at_utc, version";

    private readonly Action? _beforeBeginWriteTransactionForTest;
    private readonly Action? _snapshotLoadedBeforeEnvironmentForTest;
    private readonly Action<SqliteConnection>? _beforeCommitForTest;
    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProviderForTest;
    private readonly Func<DateTimeOffset>? _trustedUtcClockForTest;
    private readonly object _clockLock = new();
    private long? _lastTrustedUtcTicks;

    internal SqliteRecurringLeaseExecutionSnapshotLoader(SqliteOperationalStore store)
        : this(store, null, null, null, null, null)
    {
    }

    // All optional seams are internal and test-only. Production construction
    // supplies the system clock, system-query environment provider and no
    // callbacks or commit hooks.
    internal SqliteRecurringLeaseExecutionSnapshotLoader(
        SqliteOperationalStore store,
        Action? snapshotLoadedBeforeEnvironmentForTest,
        Action<SqliteConnection>? beforeCommitForTest,
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest = null,
        Func<DateTimeOffset>? trustedUtcClockForTest = null,
        Action? beforeBeginWriteTransactionForTest = null)
        : base(store)
    {
        _beforeBeginWriteTransactionForTest = beforeBeginWriteTransactionForTest;
        _snapshotLoadedBeforeEnvironmentForTest = snapshotLoadedBeforeEnvironmentForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _environmentProviderForTest = environmentProviderForTest;
        _trustedUtcClockForTest = trustedUtcClockForTest;
    }

    internal bool TryAuthorizeAndConsumeRecurringLeaseUse(
        CaptureAuthorizationProof? proof,
        out RecurringLeaseCaptureAuthorization? authorization,
        out string failureReason)
    {
        authorization = null;
        failureReason = "proof_missing";

        // Type and kind rejection happen before a clock sample or database
        // connection. Identity fields are used only below to locate rows.
        if (proof is null)
            return false;

        if (proof.Kind != CaptureAuthorizationProofKind.RecurringLeaseUse ||
            proof is not RecurringLeaseUseProof recurringProof)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        DateTimeOffset trustedNowUtc;

        try
        {
            using var connection = OpenBusinessConnection();
            _beforeBeginWriteTransactionForTest?.Invoke();
            using var transaction = BeginWriteTransaction(connection);

            var snapshot = ReadExactSnapshotWithinTransaction(
                connection,
                transaction,
                RecurringLeaseExecutionSnapshotLookup.FromProof(recurringProof));

            // Validate the complete persisted trust root before sampling the
            // live environment. This ensures corrupt durable state never
            // causes a provider call or reaches the mutable proof operation.
            if (!RecurringLeaseExecutionGate.TryValidateDurableSnapshot(snapshot, out failureReason))
                return false;

            // The clock sample is deliberately taken only after the durable
            // snapshot has been validated and while the immediate write lock
            // is still held. It is the sole timestamp passed to the provider
            // and the Core gate.
            if (!TrySampleTrustedUtc(out trustedNowUtc, out failureReason))
                return false;

            // Reject an obviously unavailable proof/window before querying
            // the live environment. The full Core gate repeats these checks
            // after the provider as defense in depth.
            if (!RecurringLeaseExecutionGate.TryValidateRecurringProofTimeWindow(
                    recurringProof,
                    snapshot,
                    trustedNowUtc,
                    out failureReason))
            {
                return false;
            }

            // Deterministic writer-barrier seam. The immediate transaction is
            // still held and the proof is still Available here.
            _snapshotLoadedBeforeEnvironmentForTest?.Invoke();

            StandingLeaseExecutionEnvironment? environment;
            try
            {
                var requirements = FixedRegionExecutionEnvironmentRequirements.FromRecurringSpecification(snapshot.Specification);
                environment = (_environmentProviderForTest ?? SystemQueryRecurringOccurrenceEnvironmentProvider.Instance)
                    .Capture(new RecurringOccurrenceEnvironmentCaptureRequest(requirements, trustedNowUtc));
            }
            catch
            {
                failureReason = "execution_environment_unavailable";
                return false;
            }

            if (environment is null)
            {
                failureReason = "execution_environment_unavailable";
                return false;
            }

            if (!RecurringLeaseExecutionGate.TryAuthorizeAndConsumeRecurringLeaseUseInTransaction(
                    recurringProof,
                    snapshot,
                    environment,
                    trustedNowUtc,
                    out failureReason))
            {
                return false;
            }

            try
            {
                _beforeCommitForTest?.Invoke(connection);
                transaction.Commit();
            }
            catch
            {
                // TryConsume already happened. A commit failure is therefore
                // deliberately reported as consumed-without-authorization;
                // the proof is never copied or reissued.
                failureReason = "execution_transaction_commit_failed";
                return false;
            }

            // Publication is strictly post-commit. Any unexpected construction
            // failure is conservative: no authorization is returned and the
            // already-consumed proof cannot be retried.
            try
            {
                authorization = new RecurringLeaseCaptureAuthorization(
                    recurringProof,
                    snapshot,
                    trustedNowUtc);
            }
            catch
            {
                failureReason = "execution_authorization_publication_failed";
                authorization = null;
                return false;
            }

            failureReason = "";
            return true;
        }
        catch (Phase3PersistenceException exception)
        {
            failureReason = MapPersistenceFailure(exception.Code);
            return false;
        }
        catch (PersistedSnapshotException)
        {
            failureReason = "persisted_snapshot_invalid";
            return false;
        }
        catch (Phase3DomainException)
        {
            failureReason = "persisted_snapshot_invalid";
            return false;
        }
        catch (SqliteException)
        {
            failureReason = "sqlite_failure";
            return false;
        }
        catch (Exception)
        {
            failureReason = "sqlite_failure";
            return false;
        }
    }

    private bool TrySampleTrustedUtc(out DateTimeOffset trustedNowUtc, out string failureReason)
    {
        trustedNowUtc = default;
        failureReason = "clock_unavailable";
        try
        {
            trustedNowUtc = _trustedUtcClockForTest?.Invoke() ?? DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }

        if (trustedNowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "execution_time_not_utc";
            return false;
        }

        lock (_clockLock)
        {
            var ticks = trustedNowUtc.UtcDateTime.Ticks;
            if (_lastTrustedUtcTicks is { } previous && ticks < previous)
            {
                failureReason = "clock_moved_backwards";
                return false;
            }

            _lastTrustedUtcTicks = ticks;
        }

        failureReason = "";
        return true;
    }

    /// <summary>
    /// Shared exact recurring snapshot reader. Callers must supply an already
    /// opened connection and transaction; this method never opens a second
    /// connection or commits the caller's transaction.
    /// </summary>
    internal static RecurringLeaseExecutionSnapshot ReadExactSnapshotWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringLeaseExecutionSnapshotLookup lookup)
    {
        var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(
                connection, transaction, lookup.LeaseId)
            ?? throw SnapshotFailure("The recurring lease was not found.");
        if (lookup.PlanId is not null &&
            !string.Equals(lease.PlanId, lookup.PlanId, StringComparison.Ordinal))
        {
            throw SnapshotFailure("The recurring lease is not bound to the exact plan.");
        }
        var plan = ReadPlan(connection, transaction, lease.PlanId);
        var schedule = SqliteRecurringScheduleVersionRepository.ReadScheduleByRevision(
                connection, transaction, lease.PlanId, lease.ConfigurationRef.ScheduleRevision)
            ?? throw SnapshotFailure("The exact recurring schedule was not found.");
        var binding = SqliteRecurringPlanProfileBindingRepository.ReadWithinTransaction(
                connection, transaction, lease.PlanId)
            ?? throw SnapshotFailure("The exact recurring profile binding was not found.");
        var profile = SqliteRecurringFixedRegionProfileRepository.ReadExactWithinTransaction(
            connection, transaction, lease.ConfigurationRef.ProfileRef);
        if (!string.Equals(binding.PlanId, lease.PlanId, StringComparison.Ordinal) ||
            !binding.ProfileRef.Matches(profile))
        {
            throw SnapshotFailure("The recurring profile binding is not exact.");
        }

        var approval = ReadExactApproval(connection, transaction, lease.LeaseId)
            ?? throw SnapshotFailure("The exact recurring local approval was not found.");
        var safety = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(connection, transaction);
        var slot = SqliteRecurringOccurrenceMaterializationTransaction.ReadAndValidatePersistedSlotReference(
            connection, transaction, lookup.OccurrenceIdentity, schedule);
        if (!slot.IsScheduled || slot.OccurrenceId is null ||
            !string.Equals(slot.OccurrenceIdentity, lookup.OccurrenceIdentity, StringComparison.Ordinal) ||
            lookup.OccurrenceId is not null &&
            !string.Equals(slot.OccurrenceId, lookup.OccurrenceId, StringComparison.Ordinal))
        {
            throw SnapshotFailure("The recurring occurrence slot is not an exact scheduled slot.");
        }

        var occurrence = ReadOccurrence(connection, transaction, slot.OccurrenceId);
        var candidate = CreateCandidate(slot, schedule);
        var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection, transaction, lookup.OccurrenceIdentity, occurrence.Id)
            ?? throw SnapshotFailure("The recurring execution specification was not found.");
        SqliteRecurringOccurrenceExecutionSpecificationReader.ValidateAgainstParents(
            specification,
            plan,
            schedule,
            binding,
            profile,
            lease,
            approval,
            candidate,
            occurrence);

        var runs = LoadRunsByOccurrence(connection, transaction, occurrence.Id);
        var uses = LoadUsesByOccurrence(connection, transaction, occurrence.Id);
        if (runs.Count != 1 || uses.Count != 1)
            throw SnapshotFailure("The recurring occurrence claim cardinality is invalid.");

        var run = runs[0];
        var use = uses[0];
        if (!string.Equals(run.Id, lookup.RunId, StringComparison.Ordinal) ||
            !string.Equals(use.Id, lookup.LeaseUseId, StringComparison.Ordinal))
        {
            throw SnapshotFailure("The recurring proof does not point to the unique occurrence claim.");
        }

        var entries = SqliteRecurringLeaseUseAccountingReader.ReadAllWithinTransaction(
            connection, transaction, lease);
        RecurringLeaseQuotaSnapshot quota;
        try
        {
            quota = RecurringLeaseQuotaCalculator.Calculate(lease, entries);
        }
        catch (Phase3DomainException exception)
        {
            throw SnapshotFailure("The recurring quota evidence is invalid.", exception);
        }

        return new RecurringLeaseExecutionSnapshot(
            plan,
            lease,
            profile,
            approval,
            new RecurringLeaseExecutionSafetyEvidence(
                safety.UnattendedMode,
                safety.StopAllApplied,
                safety.StopAllAppliedAtUtc,
                safety.UnattendedEnabledAtUtc,
                safety.Version),
            occurrence,
            specification,
            run,
            use,
            entries,
            quota,
            binding.BoundAtUtc);
    }

    private static RecurringLeaseLocalApprovalEvidence? ReadExactApproval(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, lease_id, plan_id, configuration_digest,
                   authorization_digest, current_user_sid, session_binding,
                   approved_at_utc, approval_kind_code, approval_version,
                   approval_digest
            FROM recurring_lease_local_approvals
            WHERE lease_id = $lease_id;
            """;
        Add(command, "$lease_id", leaseId);
        using var reader = command.ExecuteReader();
        RecurringLeaseLocalApprovalEvidence? result = null;
        while (reader.Read())
        {
            if (result is not null)
                throw SnapshotFailure("More than one recurring local approval exists for the lease.");

            var approvalVersion = ReadInt64(reader, 9);
            if (approvalVersion is < int.MinValue or > int.MaxValue)
                throw SnapshotFailure("The recurring local approval version is invalid.");

            result = RecurringLeaseLocalApprovalEvidence.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadRequiredText(reader, 2),
                ReadRequiredText(reader, 3),
                ReadRequiredText(reader, 4),
                ReadRequiredText(reader, 5),
                ReadRequiredText(reader, 6),
                ReadUtcDateTimeOffset(reader, 7),
                ReadRequiredText(reader, 8),
                (int)approvalVersion,
                ReadRequiredText(reader, 10));
        }

        return result;
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;";
        Add(command, "$occurrence_id", occurrenceId);
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
            result.Add(ReadRecordingRunSnapshot(reader));
        return result;
    }

    private static IReadOnlyList<LeaseUse> LoadUsesByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {UseColumns} FROM recurring_lease_uses WHERE occurrence_id = $occurrence_id;";
        Add(command, "$occurrence_id", occurrenceId);
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
            result.Add(ReadRecurringLeaseUse(reader));
        return result;
    }

    private static LeaseUse ReadRecurringLeaseUse(SqliteDataReader reader)
    {
        var reservedTicks = ReadInt64(reader, 6);
        long? actualTicks = reader.IsDBNull(7) ? null : ReadInt64(reader, 7);
        var reservedDuration = ReadRecurringDuration(reservedTicks, positive: false);
        TimeSpan? actualDuration = actualTicks is null ? null : ReadRecurringDuration(actualTicks.Value, positive: false);
        return LeaseUse.Rehydrate(
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 1),
            ReadRequiredText(reader, 2),
            ReadRequiredText(reader, 3),
            ReadUtcDateTimeOffset(reader, 8),
            ParseStatus(reader, 4, Phase3StateCodes.ParseLeaseUse),
            ReadInt32(reader, 5),
            reservedDuration,
            actualDuration,
            ReadUtcDateTimeOffset(reader, 9),
            ReadInt64(reader, 10));
    }

    private static TimeSpan ReadRecurringDuration(long ticks, bool positive)
    {
        if ((positive && ticks <= 0) || (!positive && ticks < 0) || ticks > TimeSpan.MaxValue.Ticks)
            throw new PersistedSnapshotException("A recurring use duration is outside the TimeSpan range.");
        return TimeSpan.FromTicks(ticks);
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

    private static string MapPersistenceFailure(string code) => code switch
    {
        "not_found" => "execution_snapshot_not_found",
        RecurringPersistenceReasonCodes.PlanNotFound => "execution_snapshot_not_found",
        RecurringPersistenceReasonCodes.OccurrenceIdentityConflict => "persisted_snapshot_invalid",
        RecurringPersistenceReasonCodes.PersistedDataInvalid => "persisted_snapshot_invalid",
        RecurringLeaseUseAccountingPersistenceReasonCodes.PersistedDataInvalid => "persisted_snapshot_invalid",
        _ => "sqlite_failure",
    };

    private static Phase3PersistenceException SnapshotFailure(string message, Exception? innerException = null) =>
        new("persisted_snapshot_invalid", message, innerException);
}
