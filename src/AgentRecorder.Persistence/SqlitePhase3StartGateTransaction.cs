using AgentRecorder.Core.Automation;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Coordinates the single-use Phase 3 start gate inside one SQLite write transaction.
/// This type deliberately has no capture, proof, UI, scheduler, or callback dependency.
/// </summary>
public sealed class SqlitePhase3StartGateTransaction : SqliteRepositoryBase, IPhase3StartGateTransaction
{
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";

    public SqlitePhase3StartGateTransaction(SqliteOperationalStore store)
        : base(store)
    {
    }

    public Phase3StartGateCommitResult Commit(Phase3StartGateRequest request)
    {
        var input = ValidateRequest(request);
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;

        try
        {
            transaction = BeginWriteTransaction(connection);

            // The scope and its related aggregates are read from this exact write
            // transaction snapshot. The public scope repository is deliberately not
            // used here because it would open a second business connection.
            var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(connection, transaction, input.ScopeId);
            PlanDefinition plan;
            PlanOccurrence occurrence;
            ConsentLease lease;
            if (scope is null)
            {
                // Keep the existing aggregate loading/error behavior long enough to
                // distinguish a missing scope from a legacy/incomplete evidence chain.
                plan = LoadPlan(connection, transaction, input.PlanId);
                occurrence = LoadOccurrence(connection, transaction, input.OccurrenceId);
                lease = LoadLease(connection, transaction, input.LeaseId);

                // If the caller supplied a different ScopeId for the same durable
                // start-gate identity, surface that as a request conflict rather
                // than treating an existing authorization as absent.
                var identityScope = SqliteAuthorizedCaptureScopeRepository.TryReadByIdentity(
                    connection,
                    transaction,
                    input.PlanId,
                    input.OccurrenceId,
                    input.LeaseId);
                if (identityScope is not null)
                {
                    var related = SqliteAuthorizedCaptureScopeRepository.LoadAndValidateRelatedForStartGate(
                        connection,
                        transaction,
                        identityScope);
                    plan = related.Plan;
                    occurrence = related.Occurrence;
                    lease = related.Lease;
                    scope = identityScope;
                }
            }
            else
            {
                var related = SqliteAuthorizedCaptureScopeRepository.LoadAndValidateRelatedForStartGate(
                    connection,
                    transaction,
                    scope);
                plan = related.Plan;
                occurrence = related.Occurrence;
                lease = related.Lease;
            }

            // These are all strict Core rehydrations on this same transaction-bound connection.
            var requestedRun = LoadRunById(connection, transaction, input.RunId);
            var requestedUse = LoadUseById(connection, transaction, input.LeaseUseId);
            var occurrenceRuns = LoadRunsByOccurrence(connection, transaction, input.OccurrenceId);
            var occurrenceUses = LoadUsesByOccurrence(connection, transaction, input.OccurrenceId);

            if (scope is null)
            {
                if (HasAnyStartGateEvidence(occurrence, requestedRun, requestedUse, occurrenceRuns, occurrenceUses))
                {
                    throw PersistedEvidenceInvalid("The persisted start-gate evidence has no authorization scope.");
                }

                throw ScopeNotFound();
            }

            var existingResult = CheckExistingEvidence(
                input,
                plan,
                occurrence,
                lease,
                scope,
                requestedRun,
                requestedUse,
                occurrenceRuns,
                occurrenceUses);
            if (existingResult is not null)
            {
                return existingResult;
            }

            SqliteStandingLeaseSafetyControlTransaction.EnsureExecutionAllowed(
                connection,
                transaction,
                lease);

            ValidateScopeBinding(input, scope);
            ValidateStartGatePreconditions(input, plan, occurrence, lease);

            // Domain transitions happen before any SQL write. The final snapshots are then
            // written with fixed SQL below; SQL never invents a status transition.
            RequireTransition(occurrence.TryCreateRun(input.RunId, input.CommitAtUtc));
            var run = RecordingRun.CreateFor(occurrence, input.RunId, input.CommitAtUtc);
            RequireTransition(run.TryTransition(RecordingRunStatus.Preparing, input.CommitAtUtc));
            RequireTransition(run.TryTransition(RecordingRunStatus.StartCommitted, input.CommitAtUtc));

            var use = LeaseUse.CreateFor(lease, occurrence, run, input.LeaseUseId, input.CommitAtUtc);
            RequireTransition(use.TryReserve(1, input.ReservedDuration, input.CommitAtUtc));
            RequireTransition(use.TryTransition(LeaseUseStatus.StartCommitted, input.CommitAtUtc));
            RequireTransition(lease.TryTransition(ConsentLeaseStatus.Exhausted, input.CommitAtUtc));

            // Keep the LeaseUse insert last so a test-only ABORT trigger demonstrates that
            // every earlier write is rolled back as part of this same transaction.
            InsertRun(connection, transaction, run);
            UpdateOccurrence(connection, transaction, occurrence, input.ExpectedOccurrenceVersion);
            UpdateLease(connection, transaction, lease, input.ExpectedLeaseVersion);
            InsertUse(connection, transaction, use);

            transaction.Commit();
            var firstCommitProof = CaptureAuthorizationProofIssuer.IssueStandingLeaseUse(
                new StandingLeaseUseProofIssuanceReceipt(
                    plan,
                    occurrence,
                    lease,
                    scope,
                    run,
                    use,
                    run.CreatedAtUtc));
            return new Phase3StartGateCommitResult(
                Phase3StartGateCommitStatus.Committed,
                run.Id,
                use.Id)
            {
                FirstCommitProof = firstCommitProof,
            };
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
                "The Phase 3 start-gate transaction violated a persisted constraint.",
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

    private static ValidatedRequest ValidateRequest(Phase3StartGateRequest request)
    {
        if (request is null)
        {
            throw InvalidArgument();
        }

        var planId = RequiredInput(request.PlanId);
        var occurrenceId = RequiredInput(request.OccurrenceId);
        var leaseId = RequiredInput(request.LeaseId);
        var scopeId = RequiredInput(request.ScopeId);
        var scopeDigest = Sha256DigestInput(request.ScopeDigest);
        var runId = RequiredInput(request.RunId);
        var leaseUseId = RequiredInput(request.LeaseUseId);
        if (request.ExpectedPlanVersion < 0 || request.ExpectedOccurrenceVersion < 0 || request.ExpectedLeaseVersion < 0 ||
            request.ExpectedPlanVersion == long.MaxValue || request.ExpectedOccurrenceVersion == long.MaxValue || request.ExpectedLeaseVersion == long.MaxValue)
        {
            throw InvalidArgument();
        }

        var commitTicks = UtcTicksInput(request.CommitAtUtc);
        if (request.ReservedDuration < TimeSpan.Zero)
        {
            _ = DurationMillisecondsInput(request.ReservedDuration);
        }

        var durationMilliseconds = DurationMillisecondsInput(request.ReservedDuration);
        if (durationMilliseconds <= 0)
        {
            throw new Phase3PersistenceException(
                "duration_out_of_scope",
                "The reserved duration must be positive.");
        }

        long endTicks;
        try
        {
            endTicks = checked(commitTicks + request.ReservedDuration.Ticks);
        }
        catch (OverflowException exception)
        {
            throw new Phase3PersistenceException(
                "duration_out_of_scope",
                "The reserved duration exceeds the UTC time range.",
                exception);
        }

        if (endTicks > DateTime.MaxValue.Ticks)
        {
            throw new Phase3PersistenceException(
                "duration_out_of_scope",
                "The reserved duration exceeds the UTC time range.");
        }

        return new ValidatedRequest(
            planId,
            occurrenceId,
            leaseId,
            scopeId,
            scopeDigest,
            runId,
            leaseUseId,
            request.ExpectedPlanVersion,
            request.ExpectedOccurrenceVersion,
            request.ExpectedLeaseVersion,
            request.ReservedDuration,
            durationMilliseconds,
            request.CommitAtUtc,
            commitTicks,
            endTicks);
    }

    private static void ValidateScopeBinding(
        ValidatedRequest input,
        AuthorizedFixedRegionScope scope)
    {
        if (!string.Equals(scope.ScopeId, input.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(scope.PlanId, input.PlanId, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, input.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, input.LeaseId, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (!string.Equals(scope.ScopeDigest, input.ScopeDigest, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (input.CommitAtUtc < scope.CreatedAtUtc)
        {
            throw StartGateConflict();
        }

        if (scope.ReservedDuration != input.ReservedDuration)
        {
            throw new Phase3PersistenceException(
                "duration_out_of_scope",
                "The requested duration does not match the authorized scope.");
        }
    }

    private static bool HasAnyStartGateEvidence(
        PlanOccurrence occurrence,
        RecordingRun? requestedRun,
        LeaseUse? requestedUse,
        IReadOnlyList<RecordingRun> occurrenceRuns,
        IReadOnlyList<LeaseUse> occurrenceUses) =>
        occurrence.RunId is not null ||
        requestedRun is not null ||
        requestedUse is not null ||
        occurrenceRuns.Count > 0 ||
        occurrenceUses.Count > 0;

    private static string Sha256DigestInput(string value)
    {
        var digest = RequiredInput(value);
        if (digest.Length != 64 || digest.Any(character =>
                character < '0' || (character > '9' && character < 'a') || character > 'f'))
        {
            throw InvalidArgument();
        }

        return digest;
    }

    private static Phase3StartGateCommitResult? CheckExistingEvidence(
        ValidatedRequest input,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        AuthorizedFixedRegionScope scope,
        RecordingRun? requestedRun,
        LeaseUse? requestedUse,
        IReadOnlyList<RecordingRun> occurrenceRuns,
        IReadOnlyList<LeaseUse> occurrenceUses)
    {
        if (occurrenceRuns.Count > 1 || occurrenceUses.Count > 1)
        {
            throw new PersistedSnapshotException("More than one start-gate claim exists for an occurrence.");
        }

        var occurrenceRun = occurrenceRuns.SingleOrDefault();
        var occurrenceUse = occurrenceUses.SingleOrDefault();

        if (requestedRun is not null && !string.Equals(requestedRun.OccurrenceId, input.OccurrenceId, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (requestedUse is not null &&
            (!string.Equals(requestedUse.OccurrenceId, input.OccurrenceId, StringComparison.Ordinal) ||
             !string.Equals(requestedUse.LeaseId, input.LeaseId, StringComparison.Ordinal) ||
             !string.Equals(requestedUse.RunId, input.RunId, StringComparison.Ordinal)))
        {
            throw StartGateConflict();
        }

        if (occurrenceRun is not null && !string.Equals(occurrenceRun.Id, input.RunId, StringComparison.Ordinal))
        {
            throw OccurrenceAlreadyClaimed();
        }

        if (occurrenceUse is not null && !string.Equals(occurrenceUse.Id, input.LeaseUseId, StringComparison.Ordinal))
        {
            throw OccurrenceAlreadyClaimed();
        }

        var anyEvidence = requestedRun is not null || requestedUse is not null || occurrenceRun is not null || occurrenceUse is not null;
        if (!anyEvidence)
        {
            if (occurrence.RunId is not null)
            {
                throw OccurrenceAlreadyClaimed();
            }

            return null;
        }

        if (requestedRun is null || requestedUse is null || occurrenceRun is null || occurrenceUse is null)
        {
            throw StartGateConflict();
        }

        ValidateCompleteEvidence(input, plan, occurrence, lease, scope, requestedRun, requestedUse, occurrenceRun, occurrenceUse);

        return new Phase3StartGateCommitResult(
            Phase3StartGateCommitStatus.AlreadyCommitted,
            input.RunId,
            input.LeaseUseId);
    }

    private static void ValidateCompleteEvidence(
        ValidatedRequest input,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        AuthorizedFixedRegionScope scope,
        RecordingRun run,
        LeaseUse use,
        RecordingRun occurrenceRun,
        LeaseUse occurrenceUse)
    {
        // First prove that the rows themselves could have been produced by a valid
        // start-gate transaction. These checks deliberately use persisted evidence,
        // not the request, so a request mismatch cannot mask a corrupted snapshot.
        if (!string.Equals(occurrence.Id, run.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(plan.Id, occurrence.PlanId, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
        {
            throw PersistedEvidenceInvalid("The persisted start-gate identity is inconsistent.");
        }

        if (!plan.IsOneTime ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
        {
            throw PersistedEvidenceInvalid("The persisted start-gate aggregate scope is inconsistent.");
        }

        if (!string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrenceRun.Id, run.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrenceRun.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrenceUse.Id, use.Id, StringComparison.Ordinal))
        {
            throw PersistedEvidenceInvalid("The persisted start-gate run claim is inconsistent.");
        }

        if (!string.Equals(use.LeaseId, lease.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal))
        {
            throw PersistedEvidenceInvalid("The persisted start-gate lease-use relation is inconsistent.");
        }

        var occurrenceHasRunState = occurrence.Status is PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed or
            PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired;
        var leaseHasTerminalScope = lease.Status is ConsentLeaseStatus.Exhausted or ConsentLeaseStatus.Revoked or ConsentLeaseStatus.Expired;
        if (!occurrenceHasRunState ||
            !run.HasCrossedStartCommit ||
            !run.IsNonRetryable ||
            !use.IsQuotaConsumed ||
            use.ReservedUseCount != 1 ||
            !leaseHasTerminalScope ||
            lease.Status == ConsentLeaseStatus.Active)
        {
            throw PersistedEvidenceInvalid("The persisted start-gate states do not prove a committed start.");
        }

        if (run.CreatedAtUtc != use.CreatedAtUtc)
        {
            throw PersistedEvidenceInvalid("The persisted start-gate commit times disagree.");
        }

        ValidatePersistedEvidenceScope(occurrence, lease, run.CreatedAtUtc, use.ReservedDuration);

        // Only after the persisted chain is internally valid may request fields be
        // compared. A different identity, commit time, or duration is a caller
        // conflict, not permission to reinterpret or shorten durable evidence.
        if (!string.Equals(plan.Id, input.PlanId, StringComparison.Ordinal) ||
            !string.Equals(occurrence.Id, input.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(lease.Id, input.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(run.Id, input.RunId, StringComparison.Ordinal) ||
            !string.Equals(use.Id, input.LeaseUseId, StringComparison.Ordinal) ||
            run.CreatedAtUtc != input.CommitAtUtc ||
            use.ReservedDuration != input.ReservedDuration)
        {
            throw StartGateConflict();
        }

        // The durable evidence is now proven. Only at this point may request
        // scope identity, digest, and exact duration be compared for idempotence.
        ValidateScopeBinding(input, scope);
    }

    private static void ValidatePersistedEvidenceScope(
        PlanOccurrence occurrence,
        ConsentLease lease,
        DateTimeOffset persistedCommitAtUtc,
        TimeSpan persistedDuration)
    {
        long persistedEndTicks;
        try
        {
            _ = DurationMillisecondsInput(persistedDuration);
            if (persistedDuration <= TimeSpan.Zero)
            {
                throw PersistedEvidenceInvalid("The persisted reservation duration is not positive.");
            }

            persistedEndTicks = checked(persistedCommitAtUtc.UtcDateTime.Ticks + persistedDuration.Ticks);
        }
        catch (OverflowException exception)
        {
            throw PersistedEvidenceInvalid("The persisted start-gate end time overflowed.", exception);
        }
        catch (Phase3PersistenceException exception) when (exception.Code == "duration_not_representable")
        {
            throw PersistedEvidenceInvalid("The persisted reservation duration is not millisecond-representable.", exception);
        }

        if (persistedCommitAtUtc < occurrence.WindowStartUtc || persistedCommitAtUtc >= occurrence.WindowEndUtc ||
            persistedCommitAtUtc < lease.ValidFromUtc || persistedCommitAtUtc >= lease.ValidUntilUtc ||
            lease.MaxUses != 1 ||
            persistedDuration > lease.MaxDuration ||
            persistedEndTicks > occurrence.WindowEndUtc.UtcDateTime.Ticks ||
            persistedEndTicks > lease.ValidUntilUtc.UtcDateTime.Ticks)
        {
            throw PersistedEvidenceInvalid("The persisted start-gate evidence exceeds its authorization scope.");
        }
    }

    private static void ValidateStartGatePreconditions(
        ValidatedRequest input,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease)
    {
        if (!string.Equals(plan.Id, input.PlanId, StringComparison.Ordinal) ||
            !string.Equals(occurrence.Id, input.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(lease.Id, input.LeaseId, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (plan.Version != input.ExpectedPlanVersion)
        {
            throw ConcurrencyConflict();
        }

        if (!plan.IsOneTime || plan.Status != PlanDefinitionStatus.Enabled)
        {
            throw new Phase3PersistenceException("plan_not_enabled", "The plan is not enabled for this start gate.");
        }

        if (occurrence.Version != input.ExpectedOccurrenceVersion)
        {
            throw ConcurrencyConflict();
        }

        if (!string.Equals(occurrence.PlanId, input.PlanId, StringComparison.Ordinal) ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (occurrence.RunId is not null)
        {
            throw OccurrenceAlreadyClaimed();
        }

        if (occurrence.Status != PlanOccurrenceStatus.Authorized)
        {
            throw new Phase3PersistenceException("occurrence_not_authorized", "The occurrence is not authorized for this start gate.");
        }

        if (lease.Version != input.ExpectedLeaseVersion)
        {
            throw ConcurrencyConflict();
        }

        if (!string.Equals(lease.PlanId, input.PlanId, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, input.OccurrenceId, StringComparison.Ordinal))
        {
            throw StartGateConflict();
        }

        if (lease.Status != ConsentLeaseStatus.Active)
        {
            throw new Phase3PersistenceException("lease_not_active", "The lease is not active for this start gate.");
        }

        if (lease.MaxUses != 1)
        {
            throw new Phase3PersistenceException("quota_exceeded", "The lease is not a supported single-use lease.");
        }

        if (input.CommitAtUtc < lease.ValidFromUtc || input.CommitAtUtc >= lease.ValidUntilUtc)
        {
            throw new Phase3PersistenceException("lease_not_valid_at_commit", "The commit time is outside the lease validity window.");
        }

        if (input.CommitAtUtc < occurrence.WindowStartUtc || input.CommitAtUtc >= occurrence.WindowEndUtc)
        {
            throw new Phase3PersistenceException("occurrence_not_in_window", "The commit time is outside the occurrence window.");
        }

        if (input.ReservedDuration > lease.MaxDuration ||
            input.EndTicks > occurrence.WindowEndUtc.UtcDateTime.Ticks ||
            input.EndTicks > lease.ValidUntilUtc.UtcDateTime.Ticks)
        {
            throw new Phase3PersistenceException("duration_out_of_scope", "The reserved duration exceeds the authorized scope.");
        }
    }

    private static void RequireTransition(Phase3TransitionResult result)
    {
        if (!result.Succeeded || !result.Changed)
        {
            throw new Phase3PersistenceException(
                "start_gate_conflict",
                "The start-gate domain transition was not accepted.",
                new Phase3DomainException(result.ReasonCode, "The start-gate domain transition was not accepted."));
        }
    }

    private static PlanDefinition LoadPlan(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {PlanColumns} FROM plans WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw NotFound();
        }

        return ReadPlanDefinitionSnapshot(reader);
    }

    private static PlanOccurrence LoadOccurrence(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw NotFound();
        }

        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static ConsentLease LoadLease(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {LeaseColumns} FROM consent_leases WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw NotFound();
        }

        return ReadConsentLeaseSnapshot(reader);
    }

    private static RecordingRun? LoadRunById(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecordingRunSnapshot(reader) : null;
    }

    private static LeaseUse? LoadUseById(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLeaseUseSnapshot(reader) : null;
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId)
    {
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;", ("$occurrence_id", occurrenceId));
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
        using var command = CreateSelectCommand(connection, transaction, $"SELECT {UseColumns} FROM lease_uses WHERE occurrence_id = $occurrence_id;", ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
        {
            result.Add(ReadLeaseUseSnapshot(reader));
        }

        return result;
    }

    private static SqliteCommand CreateSelectCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, string Value)[] parameters)
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

    private static void InsertRun(SqliteConnection connection, SqliteTransaction transaction, RecordingRun run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO recording_runs (id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version) VALUES ($id, $occurrence_id, $status_code, $has_crossed_start_commit, $media_artifact_id, $bundle_id, $terminal_reason_code, $created_at_utc, $updated_at_utc, $version);";
        Add(command, "$id", RequiredInput(run.Id));
        Add(command, "$occurrence_id", RequiredInput(run.OccurrenceId));
        Add(command, "$status_code", run.StatusCode);
        Add(command, "$has_crossed_start_commit", run.HasCrossedStartCommit ? 1L : 0L);
        Add(command, "$media_artifact_id", NullableInput(run.MediaArtifactId));
        Add(command, "$bundle_id", NullableInput(run.BundleId));
        Add(command, "$terminal_reason_code", NullableInput(run.TerminalReasonCode));
        Add(command, "$created_at_utc", UtcTicksInput(run.CreatedAtUtc));
        Add(command, "$updated_at_utc", UtcTicksInput(run.UpdatedAtUtc));
        Add(command, "$version", run.Version);
        EnsureOneWrite(command.ExecuteNonQuery());
    }

    private static void InsertUse(SqliteConnection connection, SqliteTransaction transaction, LeaseUse use)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO lease_uses (id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version) VALUES ($id, $lease_id, $occurrence_id, $run_id, $status_code, $reserved_use_count, $reserved_duration_ms, $actual_settled_duration_ms, $created_at_utc, $updated_at_utc, $version);";
        Add(command, "$id", RequiredInput(use.Id));
        Add(command, "$lease_id", RequiredInput(use.LeaseId));
        Add(command, "$occurrence_id", RequiredInput(use.OccurrenceId));
        Add(command, "$run_id", RequiredInput(use.RunId));
        Add(command, "$status_code", use.StatusCode);
        Add(command, "$reserved_use_count", use.ReservedUseCount);
        Add(command, "$reserved_duration_ms", DurationMillisecondsInput(use.ReservedDuration));
        Add(command, "$actual_settled_duration_ms", use.ActualSettledDuration is null ? null : DurationMillisecondsInput(use.ActualSettledDuration.Value));
        Add(command, "$created_at_utc", UtcTicksInput(use.CreatedAtUtc));
        Add(command, "$updated_at_utc", UtcTicksInput(use.UpdatedAtUtc));
        Add(command, "$version", use.Version);
        EnsureOneWrite(command.ExecuteNonQuery());
    }

    private static void UpdateOccurrence(SqliteConnection connection, SqliteTransaction transaction, PlanOccurrence occurrence, long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc;";
        Add(command, "$status_code", occurrence.StatusCode);
        Add(command, "$run_id", NullableInput(occurrence.RunId));
        Add(command, "$terminal_reason_code", NullableInput(occurrence.TerminalReasonCode));
        Add(command, "$updated_at_utc", UtcTicksInput(occurrence.UpdatedAtUtc));
        Add(command, "$new_version", occurrence.Version);
        Add(command, "$id", RequiredInput(occurrence.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(occurrence.PlanId));
        Add(command, "$window_start_utc", UtcTicksInput(occurrence.WindowStartUtc));
        Add(command, "$window_end_utc", UtcTicksInput(occurrence.WindowEndUtc));
        Add(command, "$created_at_utc", UtcTicksInput(occurrence.CreatedAtUtc));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows != 1)
        {
            EnsureGateUpdateSucceeded(connection, transaction, "SELECT version FROM plan_occurrences WHERE id = $id;", occurrence.Id, expectedVersion);
        }
    }

    private static void UpdateLease(SqliteConnection connection, SqliteTransaction transaction, ConsentLease lease, long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE consent_leases SET status_code = $status_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND occurrence_id = $occurrence_id AND valid_from_utc = $valid_from_utc AND valid_until_utc = $valid_until_utc AND max_uses = $max_uses AND max_duration_ms = $max_duration_ms;";
        Add(command, "$status_code", lease.StatusCode);
        Add(command, "$updated_at_utc", UtcTicksInput(lease.UpdatedAtUtc));
        Add(command, "$new_version", lease.Version);
        Add(command, "$id", RequiredInput(lease.Id));
        Add(command, "$expected_version", expectedVersion);
        Add(command, "$plan_id", RequiredInput(lease.PlanId));
        Add(command, "$occurrence_id", RequiredInput(lease.OccurrenceId));
        Add(command, "$valid_from_utc", UtcTicksInput(lease.ValidFromUtc));
        Add(command, "$valid_until_utc", UtcTicksInput(lease.ValidUntilUtc));
        Add(command, "$max_uses", lease.MaxUses);
        Add(command, "$max_duration_ms", DurationMillisecondsInput(lease.MaxDuration));
        var affectedRows = command.ExecuteNonQuery();
        if (affectedRows != 1)
        {
            EnsureGateUpdateSucceeded(connection, transaction, "SELECT version FROM consent_leases WHERE id = $id;", lease.Id, expectedVersion);
        }
    }

    private static void EnsureGateUpdateSucceeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string selectVersionSql,
        string id,
        long expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = selectVersionSql;
        Add(command, "$id", id);
        var current = command.ExecuteScalar();
        if (current is null)
        {
            throw NotFound();
        }

        if (current is not long currentVersion)
        {
            throw new PersistedSnapshotException("The persisted version was not an Int64 value.");
        }

        if (currentVersion != expectedVersion)
        {
            throw ConcurrencyConflict();
        }

        throw StartGateConflict();
    }

    private static void EnsureOneWrite(int affectedRows)
    {
        if (affectedRows != 1)
        {
            throw InfrastructureFailure(new InvalidOperationException("The start-gate write did not affect exactly one row."));
        }
    }

    private static Phase3PersistenceException StartGateConflict() =>
        new("start_gate_conflict", "The requested start-gate evidence conflicts with the current database state.");

    private static Phase3PersistenceException ScopeNotFound() =>
        new("scope_not_found", "The requested authorization scope was not found.");

    private static PersistedSnapshotException PersistedEvidenceInvalid(string reason, Exception? innerException = null) =>
        new(reason, innerException);

    private static Phase3PersistenceException OccurrenceAlreadyClaimed() =>
        new("occurrence_already_claimed", "The occurrence has already been claimed.");

    private sealed record ValidatedRequest(
        string PlanId,
        string OccurrenceId,
        string LeaseId,
        string ScopeId,
        string ScopeDigest,
        string RunId,
        string LeaseUseId,
        long ExpectedPlanVersion,
        long ExpectedOccurrenceVersion,
        long ExpectedLeaseVersion,
        TimeSpan ReservedDuration,
        long ReservedDurationMilliseconds,
        DateTimeOffset CommitAtUtc,
        long CommitTicks,
        long EndTicks);
}
