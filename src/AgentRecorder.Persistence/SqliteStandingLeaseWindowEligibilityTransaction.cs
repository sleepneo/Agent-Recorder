using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// One immediate SQLite transaction for the pre-dispatch standing window
/// decision. It reads the durable authorization chain and may only expire an
/// otherwise untouched Authorized occurrence. It never creates execution
/// claims or touches a capture backend.
/// </summary>
internal sealed class SqliteStandingLeaseWindowEligibilityTransaction : SqliteRepositoryBase
{
    private const string OccurrenceColumns = "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";
    private const string PlanColumns = "id, is_one_time, status_code, created_at_utc, updated_at_utc, version";
    private const string LeaseColumns = "id, plan_id, occurrence_id, status_code, valid_from_utc, valid_until_utc, max_uses, max_duration_ms, updated_at_utc, version";

    private readonly Action<SqliteConnection, SqliteTransaction>? _beforeCommitForTest;

    internal SqliteStandingLeaseWindowEligibilityTransaction(
        SqliteOperationalStore store,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
        : base(store)
    {
        _beforeCommitForTest = beforeCommitForTest;
    }

    internal StandingLeaseWindowEligibilityResult Evaluate(
        StandingLeaseWindowEligibilityRequest request,
        DateTimeOffset nowUtc,
        string? preparedIntentId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return StandingLeaseWindowEligibilityResult.Rejected(request, "eligibility_time_not_utc");
        }

        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            SqliteStandingLeaseAuthorizationActivationTransaction.PreparedIntentBinding? preparedIntent = null;
            if (preparedIntentId is not null)
            {
                preparedIntent = SqliteStandingLeaseAuthorizationActivationTransaction.ReadPreparedIntent(
                    connection,
                    transaction,
                    preparedIntentId);
                var intentFailure = ValidatePreparedIntent(preparedIntent, preparedIntentId, request);
                if (intentFailure is not null)
                {
                    return StandingLeaseWindowEligibilityResult.Rejected(request, intentFailure);
                }
            }

            var chain = LoadChain(connection, transaction, request);
            if (preparedIntent is not null &&
                (!string.Equals(preparedIntent.CurrentUserSid, chain.Scope.CurrentUserSid, StringComparison.Ordinal) ||
                 !string.Equals(preparedIntent.SessionBinding, chain.Scope.SessionBinding, StringComparison.Ordinal)))
            {
                return StandingLeaseWindowEligibilityResult.Rejected(request, "eligibility_intent_conflict");
            }

            ValidateVersions(chain);
            ValidateTrustedTime(chain, nowUtc);

            var safetyState = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(
                connection,
                transaction);
            var safetyFailure = StandingLeaseSafetyPolicy.EvaluateControlBoundary(
                chain.Lease,
                safetyState.UnattendedMode,
                safetyState.StopAllApplied,
                safetyState.UnattendedEnabledAtUtc);
            if (safetyFailure is not null &&
                (preparedIntentId is not null ||
                 !string.Equals(safetyFailure, StandingLeaseSafetyReasonCodes.LeaseRevoked, StringComparison.Ordinal)))
            {
                transaction.Commit();
                return StandingLeaseWindowEligibilityResult.Rejected(request, safetyFailure);
            }

            var hasClaimEvidence = ValidateClaimEvidence(chain);
            StandingLeaseWindowEligibilityResult result;
            if (hasClaimEvidence)
            {
                result = Phase3TransitionGuards.IsTerminal(chain.Occurrence.Status)
                    ? StandingLeaseWindowEligibilityResult.FromDecision(
                        request,
                        StandingLeaseWindowEligibilityDecision.AlreadyTerminal)
                    : StandingLeaseWindowEligibilityResult.AlreadyClaimed(request);
            }
            else if (Phase3TransitionGuards.IsTerminal(chain.Occurrence.Status))
            {
                result = StandingLeaseWindowEligibilityResult.FromDecision(
                    request,
                    StandingLeaseWindowEligibilityDecision.AlreadyTerminal);
            }
            else
            {
                var decision = StandingLeaseWindowEligibilityPolicy.Evaluate(
                    chain.Plan,
                    chain.Occurrence,
                    chain.Lease,
                    chain.Scope,
                    nowUtc);

                if (decision.Status == StandingLeaseWindowEligibilityStatus.Expired)
                {
                    var expectedOccurrenceVersion = chain.Occurrence.Version;
                    RequireExpiredTransition(chain.Occurrence, nowUtc, decision.Reason);
                    UpdateOccurrence(
                        connection,
                        transaction,
                        chain.Occurrence,
                        expectedOccurrenceVersion);
                    result = StandingLeaseWindowEligibilityResult.Expired(request, decision.Reason);
                    _beforeCommitForTest?.Invoke(connection, transaction);
                }
                else
                {
                    result = StandingLeaseWindowEligibilityResult.FromDecision(
                        request,
                        decision,
                        decision.Status == StandingLeaseWindowEligibilityStatus.Eligible
                            ? new StandingLeaseWindowEligibilityHandoff(
                                request.PlanId,
                                request.OccurrenceId,
                                request.LeaseId,
                                request.ScopeId,
                                request.ScopeDigest,
                                chain.Plan.Version,
                                chain.Occurrence.Version,
                                chain.Lease.Version)
                            : null);
                }
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
            throw SnapshotInvalid(exception);
        }
        catch (Phase3DomainException exception)
        {
            throw SnapshotInvalid(exception);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Phase3PersistenceException(
                "eligibility_snapshot_invalid",
                "The standing eligibility transaction violated a persisted constraint.",
                exception);
        }
        catch (SqliteException exception)
        {
            throw new Phase3PersistenceException(
                "eligibility_sqlite_failure",
                "The standing eligibility transaction failed in SQLite.",
                exception);
        }
        catch (Exception exception)
        {
            throw new Phase3PersistenceException(
                "eligibility_sqlite_failure",
                "The standing eligibility transaction failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    internal StandingLeaseNaturalWakeConcurrencyRevalidationResult RevalidateAfterConcurrency(
        StandingLeaseOneShotExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        var eligibilityRequest = new StandingLeaseWindowEligibilityRequest(
            request.PlanId,
            request.OccurrenceId,
            request.LeaseId,
            request.ScopeId!,
            request.ScopeDigest!);
        var chain = LoadChain(connection, transaction, eligibilityRequest);
        ValidateVersions(chain);

        var hasClaimEvidence = ValidateClaimEvidence(chain);
        if (hasClaimEvidence)
        {
            var run = chain.Runs.Single();
            var use = chain.Uses.Single();
            transaction.Commit();
            return string.Equals(run.Id, request.RunId, StringComparison.Ordinal) &&
                   string.Equals(use.Id, request.LeaseUseId, StringComparison.Ordinal)
                ? StandingLeaseNaturalWakeConcurrencyRevalidationResult.AlreadyCommitted()
                : StandingLeaseNaturalWakeConcurrencyRevalidationResult.AlreadyClaimed();
        }

        var leaseRevoked = chain.Lease.Status == ConsentLeaseStatus.Revoked;
        transaction.Commit();
        return leaseRevoked
            ? StandingLeaseNaturalWakeConcurrencyRevalidationResult.LeaseRevoked()
            : StandingLeaseNaturalWakeConcurrencyRevalidationResult.Unresolved();
    }

    private static string? ValidatePreparedIntent(
        SqliteStandingLeaseAuthorizationActivationTransaction.PreparedIntentBinding? preparedIntent,
        string intentId,
        StandingLeaseWindowEligibilityRequest request)
    {
        if (preparedIntent is null)
        {
            return "eligibility_intent_not_found";
        }

        if (preparedIntent.Status != StandingSetupIntentStatus.Activated)
        {
            return "eligibility_intent_not_activated";
        }

        if (preparedIntent.PlanId is null ||
            preparedIntent.OccurrenceId is null ||
            preparedIntent.LeaseId is null ||
            preparedIntent.ScopeId is null ||
            !string.Equals(preparedIntent.IntentId, intentId, StringComparison.Ordinal) ||
            !string.Equals(preparedIntent.PlanId, request.PlanId, StringComparison.Ordinal) ||
            !string.Equals(preparedIntent.OccurrenceId, request.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(preparedIntent.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(preparedIntent.ScopeId, request.ScopeId, StringComparison.Ordinal))
        {
            return "eligibility_intent_conflict";
        }

        return null;
    }

    private static RecoveryChain LoadChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandingLeaseWindowEligibilityRequest request)
    {
        var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(
            connection,
            transaction,
            request.ScopeId);
        if (scope is null)
        {
            throw SnapshotInvalid(new PersistedSnapshotException("The requested eligibility scope was not found."));
        }

        if (!string.Equals(scope.ScopeId, request.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(scope.PlanId, request.PlanId, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, request.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(scope.ScopeDigest, request.ScopeDigest, StringComparison.Ordinal))
        {
            throw new Phase3PersistenceException(
                "eligibility_scope_identity_mismatch",
                "The eligibility scope does not match the durable request identity.");
        }

        // Do not use the start-gate loader here: its stronger invariant that a
        // lease covers the entire occurrence would prevent this policy from
        // returning the required lease_window_expired decision when a lease
        // has since become too short. The same snapshot parsers are used, with
        // eligibility-specific relation checks below.
        var plan = LoadPlan(connection, transaction, request.PlanId);
        var occurrence = LoadOccurrence(connection, transaction, request.OccurrenceId);
        var lease = LoadLease(connection, transaction, request.LeaseId);
        ValidateAuthorizationRelations(scope, plan, occurrence, lease);

        var runs = LoadRunsByOccurrence(connection, transaction, occurrence.Id);
        var uses = LoadUsesByOccurrence(connection, transaction, occurrence.Id);
        return new RecoveryChain(connection, transaction, plan, occurrence, lease, scope, runs, uses);
    }

    private static void ValidateAuthorizationRelations(
        AuthorizedFixedRegionScope scope,
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease)
    {
        if (!string.Equals(scope.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, lease.Id, StringComparison.Ordinal) ||
            !plan.IsOneTime ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            lease.MaxUses != 1 ||
            scope.ReservedDuration > lease.MaxDuration ||
            scope.CreatedAtUtc < occurrence.CreatedAtUtc ||
            scope.CreatedAtUtc < lease.ValidFromUtc ||
            scope.CreatedAtUtc >= lease.ValidUntilUtc)
        {
            throw SnapshotInvalid(new PersistedSnapshotException(
                "The persisted eligibility authorization relation is inconsistent."));
        }
    }

    private static void ValidateVersions(RecoveryChain chain)
    {
        if (chain.Plan.Version == long.MaxValue ||
            chain.Occurrence.Version == long.MaxValue ||
            chain.Lease.Version == long.MaxValue ||
            chain.Runs.Any(run => run.Version == long.MaxValue) ||
            chain.Uses.Any(use => use.Version == long.MaxValue))
        {
            throw SnapshotInvalid(new PersistedSnapshotException(
                "The persisted eligibility aggregate version cannot be advanced safely."));
        }
    }

    private static void ValidateTrustedTime(RecoveryChain chain, DateTimeOffset nowUtc)
    {
        if (nowUtc < chain.Plan.UpdatedAtUtc ||
            nowUtc < chain.Occurrence.UpdatedAtUtc ||
            nowUtc < chain.Lease.UpdatedAtUtc ||
            chain.Runs.Any(run => nowUtc < run.UpdatedAtUtc) ||
            chain.Uses.Any(use => nowUtc < use.UpdatedAtUtc))
        {
            throw new Phase3PersistenceException(
                "eligibility_time_non_monotonic",
                "The trusted eligibility clock precedes durable aggregate time.");
        }
    }

    private static bool ValidateClaimEvidence(RecoveryChain chain)
    {
        var hasClaimEvidence = chain.Occurrence.RunId is not null ||
            chain.Runs.Count > 0 ||
            chain.Uses.Count > 0;
        if (!hasClaimEvidence)
        {
            return false;
        }

        if (chain.Occurrence.RunId is null ||
            chain.Runs.Count != 1 ||
            chain.Uses.Count != 1)
        {
            throw SnapshotInvalid(new PersistedSnapshotException(
                "The occurrence has incomplete or duplicate execution claim evidence."));
        }

        var run = chain.Runs[0];
        var use = chain.Uses[0];
        if (!string.Equals(chain.Occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(run.OccurrenceId, chain.Occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, chain.Occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(use.LeaseId, chain.Lease.Id, StringComparison.Ordinal) ||
            !run.HasCrossedStartCommit ||
            !run.IsNonRetryable ||
            !use.IsQuotaConsumed ||
            use.ReservedUseCount != 1 ||
            use.ReservedDuration <= TimeSpan.Zero)
        {
            throw SnapshotInvalid(new PersistedSnapshotException(
                "The occurrence claim evidence is not a committed one-time execution chain."));
        }

        return true;
    }

    private static void RequireExpiredTransition(
        PlanOccurrence occurrence,
        DateTimeOffset nowUtc,
        string reason)
    {
        var transition = occurrence.TryTransition(
            PlanOccurrenceStatus.Expired,
            nowUtc,
            reason);
        if (!transition.Succeeded || !transition.Changed)
        {
            throw new Phase3PersistenceException(
                "eligibility_concurrency_conflict",
                "The occurrence could not be expired by the eligibility transition.");
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
        command.CommandText = "UPDATE plan_occurrences SET status_code = $status_code, run_id = $run_id, terminal_reason_code = $terminal_reason_code, updated_at_utc = $updated_at_utc, version = $new_version WHERE id = $id AND version = $expected_version AND plan_id = $plan_id AND run_id IS NULL AND window_start_utc = $window_start_utc AND window_end_utc = $window_end_utc AND created_at_utc = $created_at_utc;";
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
            EnsureImmutableUpdateResult(
                connection,
                transaction,
                "SELECT version FROM plan_occurrences WHERE id = $id;",
                occurrence.Id,
                expectedVersion);
        }

        EnsureRowsAffected(affectedRows);
    }

    private static PlanDefinition LoadPlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {PlanColumns} FROM plans WHERE id = $id;",
            ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SnapshotInvalid(new PersistedSnapshotException("The eligibility plan was not found."));
        }

        return ReadPlanDefinitionSnapshot(reader);
    }

    private static PlanOccurrence LoadOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {OccurrenceColumns} FROM plan_occurrences WHERE id = $id;",
            ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SnapshotInvalid(new PersistedSnapshotException("The eligibility occurrence was not found."));
        }

        return ReadPlanOccurrenceSnapshot(reader);
    }

    private static ConsentLease LoadLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {LeaseColumns} FROM consent_leases WHERE id = $id;",
            ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SnapshotInvalid(new PersistedSnapshotException("The eligibility lease was not found."));
        }

        return ReadConsentLeaseSnapshot(reader);
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {RunColumns} FROM recording_runs WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingRun>();
        while (reader.Read())
        {
            result.Add(ReadRecordingRunSnapshot(reader));
        }

        return result;
    }

    private static IReadOnlyList<LeaseUse> LoadUsesByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = Select(
            connection,
            transaction,
            $"SELECT {UseColumns} FROM lease_uses WHERE occurrence_id = $occurrence_id;",
            ("$occurrence_id", occurrenceId));
        using var reader = command.ExecuteReader();
        var result = new List<LeaseUse>();
        while (reader.Read())
        {
            result.Add(ReadLeaseUseSnapshot(reader));
        }

        return result;
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
        {
            Add(command, name, value);
        }

        return command;
    }

    private static Phase3PersistenceException SnapshotInvalid(Exception exception) =>
        new("eligibility_snapshot_invalid", "The persisted standing eligibility snapshot is invalid.", exception);

    private sealed record RecoveryChain(
        SqliteConnection Connection,
        SqliteTransaction Transaction,
        PlanDefinition Plan,
        PlanOccurrence Occurrence,
        ConsentLease Lease,
        AuthorizedFixedRegionScope Scope,
        IReadOnlyList<RecordingRun> Runs,
        IReadOnlyList<LeaseUse> Uses);

}
