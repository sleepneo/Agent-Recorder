using AgentRecorder.Infrastructure;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using Microsoft.Data.Sqlite;
using AgentRecorder.Logging;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal intent-bound natural-wake handoff. It resolves only an activated
/// setup intent, then delegates window policy, ID generation, and execution to
/// the existing natural-wake dispatcher and one-shot coordinator.
/// </summary>
internal sealed class StandingLeasePreparedIntentNaturalWakeDispatcher
{
    private readonly SqliteStandingLeasePreparedIntentNaturalWakeHandoffTransaction _handoff;
    private readonly StandingLeaseWindowEligibilityService _eligibility;
    private readonly StandingLeaseNaturalWakeDispatcher _dispatcher;
    private readonly Action? _afterHandoffBeforeEligibilityForTest;
    private readonly Action<string, object> _audit;

    internal StandingLeasePreparedIntentNaturalWakeDispatcher(SqliteOperationalStore store)
        : this(
            store,
            utcNowForTest: null,
            coordinatorForTest: null,
            idGeneratorForTest: null,
            afterHandoffBeforeEligibilityForTest: null,
            auditForTest: null)
    {
    }

    internal StandingLeasePreparedIntentNaturalWakeDispatcher(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Func<
            StandingLeaseOneShotExecutionRequest,
            CancellationToken,
            Task<StandingLeaseOneShotExecutionResult>>? coordinatorForTest = null,
        Func<StandingLeaseGeneratedExecutionIds>? idGeneratorForTest = null,
        Action? afterHandoffBeforeEligibilityForTest = null,
        Action<string, object>? auditForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _handoff = new SqliteStandingLeasePreparedIntentNaturalWakeHandoffTransaction(store);
        _eligibility = new StandingLeaseWindowEligibilityService(store, utcNowForTest);
        _audit = auditForTest ?? new AuditLogger().Log;
        _dispatcher = new StandingLeaseNaturalWakeDispatcher(
            _eligibility,
            coordinatorForTest ?? new StandingLeaseOneShotExecutionCoordinator(store).ExecuteAsync,
            idGeneratorForTest ?? StandingLeaseNaturalWakeDispatcher.GenerateExecutionIds,
            _audit);
        _afterHandoffBeforeEligibilityForTest = afterHandoffBeforeEligibilityForTest;
    }

    internal async Task<StandingLeaseNaturalWakeDispatchResult> DispatchAsync(
        string? intentId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalId(intentId))
        {
            return Rejected("natural_wake_intent_request_invalid");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Rejected("natural_wake_handoff_cancelled");
        }

        (string PlanId, string OccurrenceId, string LeaseId, string ScopeId, string ScopeDigest) binding;
        try
        {
            binding = _handoff.ReadActivated(intentId!);
        }
        catch (Phase3PersistenceException exception)
        {
            var reason = MapHandoffFailure(exception.Code);
            AuditSafetyBlock(intentId!, reason);
            return Rejected(reason);
        }
        catch (PersistedSnapshotException)
        {
            return Rejected("natural_wake_handoff_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return Rejected("natural_wake_handoff_snapshot_invalid");
        }
        catch (Exception)
        {
            return Rejected("natural_wake_handoff_failed");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Rejected("natural_wake_handoff_cancelled");
        }

        try
        {
            _afterHandoffBeforeEligibilityForTest?.Invoke();
        }
        catch
        {
            return Rejected("natural_wake_handoff_failed");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Rejected("natural_wake_handoff_cancelled");
        }

        var request = new StandingLeaseWindowEligibilityRequest(
            binding.PlanId,
            binding.OccurrenceId,
            binding.LeaseId,
            binding.ScopeId,
            binding.ScopeDigest);

        return await _dispatcher
            .DispatchWithEligibilityAsync(
                request,
                cancellationToken,
                candidate => _eligibility.EvaluateForPreparedIntent(intentId!, candidate))
            .ConfigureAwait(false);
    }

    private static StandingLeaseNaturalWakeDispatchResult Rejected(string reason) =>
        StandingLeaseNaturalWakeDispatchResult.Rejected(
            StandingLeaseWindowEligibilityResult.Rejected(reason),
            reason);

    private void AuditSafetyBlock(string intentId, string reason)
    {
        if (reason is not (
            StandingLeaseSafetyReasonCodes.UnattendedDisabled or
            StandingLeaseSafetyReasonCodes.StopAllActive or
            StandingLeaseSafetyReasonCodes.LeaseRevoked or
            StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization))
        {
            return;
        }

        try
        {
            _audit("standing_lease.wake_blocked", new
            {
                intent_id = intentId,
                reason_code = reason,
            });
        }
        catch
        {
            // Audit must not turn a fail-closed safety decision into execution.
        }
    }

    private static string MapHandoffFailure(string code) => code switch
    {
        "natural_wake_intent_not_found" => "natural_wake_intent_not_found",
        "natural_wake_intent_not_activated" => "natural_wake_intent_not_activated",
        "natural_wake_intent_conflict" => "natural_wake_intent_conflict",
        "natural_wake_snapshot_invalid" or "persisted_snapshot_invalid" => "natural_wake_handoff_snapshot_invalid",
        "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
            "natural_wake_handoff_sqlite_failure",
        _ => code,
    };

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('/') &&
        !value.Contains('\\');
}

/// <summary>
/// Internal immediate read transaction for the first handoff snapshot. It
/// never creates claims, expires an occurrence, or invokes eligibility.
/// </summary>
internal sealed class SqliteStandingLeasePreparedIntentNaturalWakeHandoffTransaction : SqliteRepositoryBase
{
    internal SqliteStandingLeasePreparedIntentNaturalWakeHandoffTransaction(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal (string PlanId, string OccurrenceId, string LeaseId, string ScopeId, string ScopeDigest) ReadActivated(
        string intentId)
    {
        using var connection = OpenBusinessConnection();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = BeginWriteTransaction(connection);
            var intent = SqliteStandingLeaseAuthorizationActivationTransaction.ReadPreparedIntent(
                connection,
                transaction,
                intentId);
            if (intent is null)
            {
                throw new Phase3PersistenceException(
                    "natural_wake_intent_not_found",
                    "The requested setup intent was not found.");
            }

            if (intent.Status != StandingSetupIntentStatus.Activated)
            {
                throw new Phase3PersistenceException(
                    "natural_wake_intent_not_activated",
                    "The setup intent is not activated for natural wake.");
            }

            if (intent.PlanId is null || intent.OccurrenceId is null ||
                intent.LeaseId is null || intent.ScopeId is null)
            {
                throw new Phase3PersistenceException(
                    "natural_wake_intent_conflict",
                    "The activated setup intent does not contain a complete authorization chain.");
            }

            var scope = SqliteAuthorizedCaptureScopeRepository.TryReadById(
                connection,
                transaction,
                intent.ScopeId);
            if (scope is null ||
                !string.Equals(scope.ScopeId, intent.ScopeId, StringComparison.Ordinal) ||
                !string.Equals(scope.PlanId, intent.PlanId, StringComparison.Ordinal) ||
                !string.Equals(scope.OccurrenceId, intent.OccurrenceId, StringComparison.Ordinal) ||
                !string.Equals(scope.LeaseId, intent.LeaseId, StringComparison.Ordinal) ||
                !string.Equals(scope.CurrentUserSid, intent.CurrentUserSid, StringComparison.Ordinal) ||
                !string.Equals(scope.SessionBinding, intent.SessionBinding, StringComparison.Ordinal))
            {
                throw new Phase3PersistenceException(
                    "natural_wake_intent_conflict",
                    "The activated setup intent and scope binding are inconsistent.");
            }

            if (!IsCanonicalSha256(scope.ScopeDigest))
            {
                throw new Phase3PersistenceException(
                    "natural_wake_snapshot_invalid",
                    "The persisted authorization scope digest is invalid.");
            }

            var safetyState = SqliteStandingLeaseSafetyControlTransaction.ReadGlobalState(
                connection,
                transaction);
            if (safetyState.UnattendedMode == UnattendedModeStatus.Disabled)
            {
                throw new Phase3PersistenceException(
                    StandingLeaseSafetyReasonCodes.UnattendedDisabled,
                    "Unattended natural wake is disabled.");
            }

            transaction.Commit();
            return (
                intent.PlanId,
                intent.OccurrenceId,
                intent.LeaseId,
                intent.ScopeId,
                scope.ScopeDigest);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static bool IsCanonicalSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
