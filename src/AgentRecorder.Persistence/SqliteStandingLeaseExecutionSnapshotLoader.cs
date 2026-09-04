using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Owns the atomic standing-proof authorization boundary. The loader acquires
/// a SQLite immediate/write transaction, keeps it open through rehydration,
/// Core validation, and proof consumption, and publishes a context only after
/// the transaction commits.
/// </summary>
internal sealed class SqliteStandingLeaseExecutionSnapshotLoader : SqliteRepositoryBase
{
    private const string RunColumns = "id, occurrence_id, status_code, has_crossed_start_commit, media_artifact_id, bundle_id, terminal_reason_code, created_at_utc, updated_at_utc, version";
    private const string UseColumns = "id, lease_id, occurrence_id, run_id, status_code, reserved_use_count, reserved_duration_ms, actual_settled_duration_ms, created_at_utc, updated_at_utc, version";

    private readonly Action? _snapshotLoadedBeforeCoreGateForTest;
    private readonly Action<SqliteConnection>? _beforeCommitForTest;
    private readonly IStandingLeaseExecutionEnvironmentProvider? _environmentProviderForTest;

    internal SqliteStandingLeaseExecutionSnapshotLoader(SqliteOperationalStore store)
        : this(
            store,
            snapshotLoadedBeforeCoreGateForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest: null)
    {
    }

    // These hooks are internal test seams only. The default production
    // constructor supplies no callbacks and exposes no transaction handle.
    internal SqliteStandingLeaseExecutionSnapshotLoader(
        SqliteOperationalStore store,
        Action? snapshotLoadedBeforeCoreGateForTest,
        Action<SqliteConnection>? beforeCommitForTest,
        IStandingLeaseExecutionEnvironmentProvider? environmentProviderForTest = null)
        : base(store)
    {
        _snapshotLoadedBeforeCoreGateForTest = snapshotLoadedBeforeCoreGateForTest;
        _beforeCommitForTest = beforeCommitForTest;
        _environmentProviderForTest = environmentProviderForTest;
    }

    internal bool TryAuthorizeAndConsumeStandingLeaseUse(
        CaptureAuthorizationProof? proof,
        out StandingLeaseCaptureAuthorization? authorization,
        out string failureReason)
        => TryAuthorizeAndConsumeStandingLeaseUseCore(
            proof,
            scope => _environmentProviderForTest?.Capture(scope) ??
                StandingLeaseExecutionEnvironment.CaptureCurrent(scope),
            out authorization,
            out failureReason);

    // Explicit internal test seam. Production callers use the overload above,
    // which captures the environment inside this Persistence boundary.
    internal bool TryAuthorizeAndConsumeStandingLeaseUseForTest(
        CaptureAuthorizationProof? proof,
        StandingLeaseExecutionEnvironment? environment,
        out StandingLeaseCaptureAuthorization? authorization,
        out string failureReason)
        => TryAuthorizeAndConsumeStandingLeaseUseCore(
            proof,
            _ => environment,
            out authorization,
            out failureReason);

    private bool TryAuthorizeAndConsumeStandingLeaseUseCore(
        CaptureAuthorizationProof? proof,
        Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment?> environmentFactory,
        out StandingLeaseCaptureAuthorization? authorization,
        out string failureReason)
    {
        authorization = null;
        failureReason = "proof_missing";

        // Reject non-standing proofs before opening an authorization
        // transaction. StandingLeaseUse cannot bypass the interactive gate.
        if (proof is null)
        {
            return false;
        }

        if (proof.Kind != CaptureAuthorizationProofKind.StandingLeaseUse ||
            proof is not StandingLeaseUseProof standingProof)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        try
        {
            using var connection = OpenBusinessConnection();
            // deferred:false is SQLite BEGIN IMMEDIATE in the existing
            // repository policy. It prevents another supported writer from
            // changing the durable aggregate while this authorization is
            // loading and validating its snapshot.
            using var transaction = BeginWriteTransaction(connection);

            var scope = SqliteAuthorizedCaptureScopeRepository.TryReadByLeaseId(
                connection,
                transaction,
                standingProof.LeaseId);
            if (scope is null)
            {
                failureReason = "scope_not_found";
                return false;
            }

            var related = SqliteAuthorizedCaptureScopeRepository.LoadAndValidateRelatedForStartGate(
                connection,
                transaction,
                scope);
            var run = LoadRun(connection, transaction, standingProof.RunId);
            var use = LoadUse(connection, transaction, standingProof.LeaseUseId);
            if (run is null || use is null)
            {
                failureReason = "execution_snapshot_not_found";
                return false;
            }

            var occurrenceRuns = LoadRunsByOccurrence(connection, transaction, related.Occurrence.Id);
            var occurrenceUses = LoadUsesByOccurrence(connection, transaction, related.Occurrence.Id);
            if (occurrenceRuns.Count != 1 || occurrenceUses.Count != 1 ||
                !string.Equals(occurrenceRuns[0].Id, run.Id, StringComparison.Ordinal) ||
                !string.Equals(occurrenceUses[0].Id, use.Id, StringComparison.Ordinal))
            {
                throw new PersistedSnapshotException(
                    "The persisted execution snapshot has an unexpected number of claims.");
            }

            var snapshot = new StandingLeaseExecutionSnapshot(
                related.Plan,
                related.Occurrence,
                related.Lease,
                scope,
                run,
                use);

            // Re-use the Task 229 proof invariants while this write
            // transaction still protects the exact database view.
            CaptureAuthorizationProofIssuer.ValidateStandingReceipt(
                new StandingLeaseUseProofIssuanceReceipt(
                    snapshot.Plan,
                    snapshot.Occurrence,
                    snapshot.Lease,
                    snapshot.Scope,
                    snapshot.Run,
                    snapshot.Use,
                    snapshot.Run.CreatedAtUtc));

            StandingLeaseExecutionEnvironment? environment;
            try
            {
                environment = environmentFactory(snapshot.Scope);
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

            // Test-only barrier: the transaction is still held here, before
            // any Core check can consume the process-local proof.
            _snapshotLoadedBeforeCoreGateForTest?.Invoke();

            // Core performs every remaining check and makes proof.TryConsume
            // its final mutable operation while this transaction is open.
            if (!StandingLeaseExecutionGate.TryAuthorizeAndConsumeStandingLeaseUseInTransaction(
                    standingProof,
                    snapshot,
                    environment,
                    out failureReason))
            {
                return false;
            }

            // Test-only failure injection is before Commit and cannot publish
            // a context. If it closes the connection or throws, the proof
            // remains consumed and the transaction is rolled back.
            try
            {
                _beforeCommitForTest?.Invoke(connection);
                transaction.Commit();
            }
            catch
            {
                failureReason = "execution_transaction_commit_failed";
                return false;
            }

            // The context is deliberately constructed and assigned only after
            // SQLite commit succeeds. A commit failure therefore cannot
            // produce a false success context.
            authorization = new StandingLeaseCaptureAuthorization(
                standingProof,
                snapshot,
                environment.NowUtc);
            failureReason = "";
            return true;
        }
        catch (Phase3PersistenceException exception)
        {
            failureReason = exception.Code == "not_found"
                ? "execution_snapshot_not_found"
                : exception.Code;
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

    private static RecordingRun? LoadRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = CreateSelectCommand(
            connection,
            transaction,
            $"SELECT {RunColumns} FROM recording_runs WHERE id = $id;",
            ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecordingRunSnapshot(reader) : null;
    }

    private static LeaseUse? LoadUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = CreateSelectCommand(
            connection,
            transaction,
            $"SELECT {UseColumns} FROM lease_uses WHERE id = $id;",
            ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadLeaseUseSnapshot(reader) : null;
    }

    private static IReadOnlyList<RecordingRun> LoadRunsByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId)
    {
        using var command = CreateSelectCommand(
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
        using var command = CreateSelectCommand(
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
}
