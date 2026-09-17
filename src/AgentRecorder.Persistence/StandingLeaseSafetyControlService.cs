using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Persistence;

internal interface IStandingLeaseActiveRunStopper
{
    void StopAll(string reason);
}

internal sealed class RecordingEngineStandingLeaseActiveRunStopper : IStandingLeaseActiveRunStopper
{
    private readonly RecordingEngine _engine;

    internal RecordingEngineStandingLeaseActiveRunStopper(RecordingEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public void StopAll(string reason) => _engine.StopAllSync(reason);
}

/// <summary>
/// The single internal boundary for durable unattended safety controls. It
/// owns input validation, trusted time, persistence result normalization and
/// audit emission; UI and wake callers do not write safety tables directly.
/// </summary>
internal sealed class StandingLeaseSafetyControlService
{
    private readonly SqliteStandingLeaseSafetyControlTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private readonly Action<string, object> _audit;
    private readonly IStandingLeaseActiveRunStopper? _activeRunStopper;
    private readonly StandingLeaseStartSafetyInterlock? _startSafetyInterlock;
    private DateTimeOffset? _lastUtcNow;

    internal StandingLeaseSafetyControlService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, auditForTest: null, activeRunStopper: null, beforeCommitForTest: null, startSafetyInterlock: null)
    {
    }

    internal StandingLeaseSafetyControlService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<string, object>? auditForTest = null,
        IStandingLeaseActiveRunStopper? activeRunStopper = null,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? beforeCommitForTest = null,
        StandingLeaseStartSafetyInterlock? startSafetyInterlock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _audit = auditForTest ?? new AuditLogger().Log;
        _activeRunStopper = activeRunStopper;
        _startSafetyInterlock = startSafetyInterlock;
        _transaction = new SqliteStandingLeaseSafetyControlTransaction(store, beforeCommitForTest);
    }

    internal string? ValidateStandingStart(
        StandingLeaseCaptureExecutionTicket ticket,
        DateTimeOffset nowUtc)
    {
        try
        {
            return _transaction.ValidateStandingStart(ticket, nowUtc);
        }
        catch (PersistedSnapshotException)
        {
            return "safety_snapshot_invalid";
        }
        catch (Phase3DomainException)
        {
            return "safety_snapshot_invalid";
        }
        catch (Phase3PersistenceException exception)
        {
            return NormalizeFailure(exception.Code);
        }
        catch
        {
            return "safety_sqlite_failure";
        }
    }

    internal StandingLeaseSafetyQueryResult QueryIntent(string? intentId)
    {
        if (!IsCanonicalId(intentId))
        {
            return StandingLeaseSafetyQueryResult.Rejected("safety_intent_request_invalid");
        }

        try
        {
            return _transaction.QueryIntent(intentId!);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseSafetyQueryResult.Rejected(NormalizeFailure(exception.Code));
        }
        catch (PersistedSnapshotException)
        {
            return StandingLeaseSafetyQueryResult.Rejected("safety_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return StandingLeaseSafetyQueryResult.Rejected("safety_snapshot_invalid");
        }
        catch (Exception)
        {
            return StandingLeaseSafetyQueryResult.Rejected("safety_sqlite_failure");
        }
    }

    internal bool IsUnattendedEnabled(string currentUserSid)
    {
        if (!IsCanonicalUserSid(currentUserSid))
            return false;

        try
        {
            var result = _transaction.QueryControlCenter(currentUserSid);
            return result.Status == StandingLeaseControlCenterQueryStatus.Available &&
                result.State?.UnattendedMode == UnattendedModeStatus.Enabled;
        }
        catch
        {
            return false;
        }
    }

    internal StandingLeaseControlCenterQueryResult QueryControlCenter(string? currentUserSid)
    {
        if (!IsCanonicalUserSid(currentUserSid))
        {
            return StandingLeaseControlCenterQueryResult.Rejected("safety_user_identity_invalid");
        }

        try
        {
            return _transaction.QueryControlCenter(currentUserSid!);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseControlCenterQueryResult.Rejected(NormalizeFailure(exception.Code));
        }
        catch (PersistedSnapshotException)
        {
            return StandingLeaseControlCenterQueryResult.Rejected("safety_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return StandingLeaseControlCenterQueryResult.Rejected("safety_snapshot_invalid");
        }
        catch (Exception)
        {
            return StandingLeaseControlCenterQueryResult.Rejected("safety_sqlite_failure");
        }
    }

    internal StandingLeaseSafetyControlResult RevokeLease(
        string? intentId,
        string? operationId,
        string reasonCode = "user_requested")
    {
        if (!IsCanonicalId(intentId))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_intent_request_invalid");
        }

        if (!IsCanonicalId(operationId))
        {
            return StandingLeaseSafetyControlResult.Rejected(null, "safety_operation_request_invalid");
        }

        if (!IsCanonicalReason(reasonCode))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_reason_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return CompleteControlAudit(
                "lease_revoke",
                intentId,
                null,
                operationId,
                reasonCode,
                StandingLeaseSafetyControlResult.Rejected(operationId, clockFailure));
        }

        var result = Execute(
            operationId!,
            () => _transaction.RevokeLease(intentId!, operationId!, reasonCode, nowUtc));
        return CompleteControlAudit(
            "lease_revoke",
            intentId,
            result.State?.LeaseId,
            operationId,
            reasonCode,
            result);
    }

    internal StandingLeaseSafetyControlResult RevokeRecurringLease(
        string? leaseId,
        string? operationId,
        string reasonCode = "user_requested")
    {
        if (!IsCanonicalId(leaseId))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_lease_request_invalid");
        }

        if (!IsCanonicalId(operationId))
        {
            return StandingLeaseSafetyControlResult.Rejected(null, "safety_operation_request_invalid");
        }

        if (!IsCanonicalReason(reasonCode))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_reason_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return CompleteControlAudit(
                "lease_revoke",
                null,
                leaseId,
                operationId,
                reasonCode,
                StandingLeaseSafetyControlResult.Rejected(operationId, clockFailure));
        }

        var result = Execute(
            operationId!,
            () => _transaction.RevokeRecurringLease(leaseId!, operationId!, reasonCode, nowUtc));
        return CompleteControlAudit(
            "lease_revoke",
            null,
            leaseId,
            operationId,
            reasonCode,
            result);
    }

    internal StandingLeaseSafetyControlResult StopAllAndRevokeAll(
        string? operationId,
        string reasonCode = "user_requested")
    {
        if (!IsCanonicalId(operationId))
        {
            return StandingLeaseSafetyControlResult.Rejected(null, "safety_operation_request_invalid");
        }

        if (!IsCanonicalReason(reasonCode))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_reason_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return CompleteControlAudit(
                "stop_all_revoke_all",
                null,
                null,
                operationId,
                reasonCode,
                StandingLeaseSafetyControlResult.Rejected(operationId, clockFailure));
        }

        var result = Execute(
            operationId!,
            () => _transaction.StopAllAndRevokeAll(operationId!, reasonCode, nowUtc),
            forcePhysicalStop: true);
        return CompleteControlAudit(
            "stop_all_revoke_all",
            null,
            null,
            operationId,
            reasonCode,
            result);
    }

    internal StandingLeaseSafetyControlResult SetUnattendedEnabled(
        bool enabled,
        string? operationId,
        string reasonCode = "user_requested")
    {
        if (!IsCanonicalId(operationId))
        {
            return StandingLeaseSafetyControlResult.Rejected(null, "safety_operation_request_invalid");
        }

        if (!IsCanonicalReason(reasonCode))
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_reason_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return CompleteControlAudit(
                enabled ? "unattended_enable" : "unattended_disable",
                null,
                null,
                operationId,
                reasonCode,
                StandingLeaseSafetyControlResult.Rejected(operationId, clockFailure));
        }

        var result = Execute(
            operationId!,
            () => _transaction.SetUnattendedMode(operationId!, enabled, reasonCode, nowUtc));
        return CompleteControlAudit(
            enabled ? "unattended_enable" : "unattended_disable",
            null,
            null,
            operationId,
            reasonCode,
            result);
    }

    internal StandingLeaseSafetyControlResult DisableUnattended(
        string? operationId,
        string reasonCode = "user_requested") =>
        SetUnattendedEnabled(false, operationId, reasonCode);

    internal StandingLeaseSafetyControlResult EnableUnattended(
        string? operationId,
        string reasonCode = "user_requested") =>
        SetUnattendedEnabled(true, operationId, reasonCode);

    private StandingLeaseSafetyControlResult Execute(
        string operationId,
        Func<StandingLeaseSafetyControlResult> operation,
        bool forcePhysicalStop = false)
    {
        try
        {
            var result = _startSafetyInterlock is null
                ? operation()
                : _startSafetyInterlock.Execute("standing_safety_control", operation);
            var shouldDispatchPhysicalStop =
                forcePhysicalStop &&
                result.Status is StandingLeaseSafetyControlResultStatus.Changed or StandingLeaseSafetyControlResultStatus.AlreadyApplied;
            if ((shouldDispatchPhysicalStop || result.RequiresActiveRunStop) && _activeRunStopper is not null)
            {
                try
                {
                    _activeRunStopper.StopAll("standing_lease_safety_control");
                }
                catch
                {
                    SafeAudit("standing_lease.active_run_stop_failed", new
                    {
                        operation_id = operationId,
                        reason_code = "active_run_stop_failed",
                    });
                    if (shouldDispatchPhysicalStop || result.RequiresActiveRunStop)
                    {
                        // The durable safety transition has already committed.
                        // Keep the legacy Rejected status/reason for existing
                        // callers, but expose the committed state and retry
                        // obligation as orthogonal facts.
                        return result.WithPhysicalStopFailure();
                    }
                }
            }

            return result;
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, NormalizeFailure(exception.Code));
        }
        catch (PersistedSnapshotException)
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_snapshot_invalid");
        }
        catch (Exception)
        {
            return StandingLeaseSafetyControlResult.Rejected(operationId, "safety_sqlite_failure");
        }
    }

    private StandingLeaseSafetyControlResult CompleteControlAudit(
        string operationKind,
        string? intentId,
        string? leaseId,
        string? operationId,
        string requestReason,
        StandingLeaseSafetyControlResult result)
    {
        SafeAudit("standing_lease.safety_control", new
        {
            operation_kind = operationKind,
            operation_id = operationId,
            intent_id = intentId,
            lease_id = leaseId,
            request_reason = requestReason,
            result_status = result.Status.ToString(),
            result_reason = result.Reason,
            changed = result.Changed,
            durable_operation_committed = result.DurableOperationCommitted,
            durable_state_changed = result.DurableStateChanged,
            physical_stop_failed = result.PhysicalStopFailed,
            physical_stop_retry_recommended = result.PhysicalStopRetryRecommended,
            requires_active_run_stop = result.RequiresActiveRunStop,
        });
        return result;
    }

    private void SafeAudit(string eventName, object payload)
    {
        try
        {
            _audit(eventName, payload);
        }
        catch
        {
            // Audit output must not turn a committed safety decision into an
            // ambiguous control result.
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "safety_clock_unavailable";
        DateTimeOffset? sampled;
        try
        {
            sampled = _utcNow();
        }
        catch
        {
            return false;
        }

        if (sampled is null)
        {
            return false;
        }

        nowUtc = sampled.Value;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "safety_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "safety_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static string NormalizeFailure(string code) => code switch
    {
        "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" => "safety_sqlite_failure",
        "persisted_snapshot_invalid" or "safety_snapshot_invalid" => "safety_snapshot_invalid",
        "concurrency_conflict" or "safety_concurrency_conflict" => "safety_concurrency_conflict",
        _ => code,
    };

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= 128 &&
        !value.Contains('/') &&
        !value.Contains('\\');

    private static bool IsCanonicalReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= 128 &&
        !value.Contains('/') &&
        !value.Contains('\\');

    private static bool IsCanonicalUserSid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length <= 256 &&
        !value.Contains('\n') &&
        !value.Contains('\r');
}
