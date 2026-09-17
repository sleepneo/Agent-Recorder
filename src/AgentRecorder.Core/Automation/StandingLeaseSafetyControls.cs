namespace AgentRecorder.Core.Automation;

internal enum UnattendedModeStatus
{
    Enabled,
    Disabled,
}

internal static class UnattendedModeStatusCodes
{
    internal const string Enabled = "enabled";
    internal const string Disabled = "disabled";

    internal static string ToCode(UnattendedModeStatus status) => status switch
    {
        UnattendedModeStatus.Enabled => Enabled,
        UnattendedModeStatus.Disabled => Disabled,
        _ => throw new Phase3DomainException("unknown_state", "Unknown unattended mode state."),
    };

    internal static UnattendedModeStatus Parse(string code) => code switch
    {
        Enabled => UnattendedModeStatus.Enabled,
        Disabled => UnattendedModeStatus.Disabled,
        _ => throw new Phase3DomainException("unknown_state", "The unattended mode state is not supported."),
    };
}

internal enum StandingLeaseSafetyControlResultStatus
{
    Changed,
    AlreadyApplied,
    Rejected,
}

internal enum StandingLeaseSafetyQueryStatus
{
    Available,
    Rejected,
}

internal static class StandingLeaseSafetyReasonCodes
{
    internal const string UnattendedDisabled = "unattended_disabled";
    internal const string StopAllActive = "stop_all_active";
    internal const string LeaseRevoked = "lease_revoked";
    internal const string LeaseAlreadyRevoked = "lease_already_revoked";
    internal const string StopAllApplied = "stop_all_applied";
    internal const string UnattendedEnabled = "unattended_enabled";
    internal const string UnattendedAlreadyEnabled = "unattended_already_enabled";
    internal const string ReenableRequiresNewAuthorization = "unattended_reenable_requires_new_authorization";
    internal const string LeasePending = "lease_pending";
    internal const string LeaseExpired = "lease_expired";
    internal const string LeaseExhausted = "lease_exhausted";
    internal const string LeaseRejected = "lease_rejected";
    internal const string ActiveRunStopFailed = "active_run_stop_failed";
}

internal static class StandingLeaseSafetyOperationKindCodes
{
    internal const string LeaseRevoke = "lease_revoke";
    internal const string StopAllRevokeAll = "stop_all_revoke_all";
    internal const string UnattendedEnable = "unattended_enable";
    internal const string UnattendedDisable = "unattended_disable";
}

internal sealed record StandingLeaseSafetyState(
    string IntentId,
    string? PlanId,
    string? OccurrenceId,
    string? LeaseId,
    ConsentLeaseStatus? LeaseStatus,
    UnattendedModeStatus UnattendedMode,
    bool StopAllApplied,
    DateTimeOffset? StopAllRequestedAtUtc,
    DateTimeOffset? StopAllAppliedAtUtc,
    string? StopAllReasonCode,
    string? BlockedReason,
    bool ActiveRunPresent,
    bool RequiresActiveRunStop);

internal sealed class StandingLeaseSafetyQueryResult
{
    private StandingLeaseSafetyQueryResult(
        StandingLeaseSafetyQueryStatus status,
        string reason,
        StandingLeaseSafetyState? state)
    {
        Status = status;
        Reason = reason;
        State = state;
    }

    internal StandingLeaseSafetyQueryStatus Status { get; }
    internal string Reason { get; }
    internal StandingLeaseSafetyState? State { get; }

    internal static StandingLeaseSafetyQueryResult Available(StandingLeaseSafetyState state) =>
        new(StandingLeaseSafetyQueryStatus.Available, "available", state);

    internal static StandingLeaseSafetyQueryResult Rejected(string reason) =>
        new(StandingLeaseSafetyQueryStatus.Rejected, reason, null);
}

internal sealed class StandingLeaseSafetyControlResult
{
    private StandingLeaseSafetyControlResult(
        StandingLeaseSafetyControlResultStatus status,
        string reason,
        string? operationId,
        bool changed,
        bool requiresActiveRunStop,
        StandingLeaseSafetyState? state,
        bool durableOperationCommitted,
        bool durableStateChanged,
        bool physicalStopFailed,
        bool physicalStopRetryRecommended)
    {
        Status = status;
        Reason = reason;
        OperationId = operationId;
        Changed = changed;
        RequiresActiveRunStop = requiresActiveRunStop;
        State = state;
        DurableOperationCommitted = durableOperationCommitted;
        DurableStateChanged = durableStateChanged;
        PhysicalStopFailed = physicalStopFailed;
        PhysicalStopRetryRecommended = physicalStopRetryRecommended;
    }

    internal StandingLeaseSafetyControlResultStatus Status { get; }
    internal string Reason { get; }
    internal string? OperationId { get; }
    // True only when this call produced a new safety-state transition.
    internal bool Changed { get; }
    internal bool RequiresActiveRunStop { get; }
    internal StandingLeaseSafetyState? State { get; }
    // True only when this request's exact operation identity has a durable
    // ledger decision. An unrelated operation occupying the requested id is
    // not a commit for the current request.
    internal bool DurableOperationCommitted { get; }
    // The first application outcome recorded by the durable operation. This
    // remains true on an exact replay of a changed operation.
    internal bool DurableStateChanged { get; }
    // The result of the external physical stopper call made by this request.
    internal bool PhysicalStopFailed { get; }
    // True when the physical stopper should be attempted again.
    internal bool PhysicalStopRetryRecommended { get; }

    internal static StandingLeaseSafetyControlResult ChangedResult(
        string operationId,
        string reason,
        bool requiresActiveRunStop = false,
        StandingLeaseSafetyState? state = null,
        bool durableOperationCommitted = false) =>
        new(
            StandingLeaseSafetyControlResultStatus.Changed,
            reason,
            operationId,
            true,
            requiresActiveRunStop,
            state,
            durableOperationCommitted,
            durableStateChanged: true,
            physicalStopFailed: false,
            physicalStopRetryRecommended: false);

    internal static StandingLeaseSafetyControlResult AlreadyAppliedResult(
        string operationId,
        string reason,
        bool requiresActiveRunStop = false,
        StandingLeaseSafetyState? state = null,
        bool durableOperationCommitted = false,
        bool durableStateChanged = false) =>
        new(
            StandingLeaseSafetyControlResultStatus.AlreadyApplied,
            reason,
            operationId,
            false,
            requiresActiveRunStop,
            state,
            durableOperationCommitted,
            durableStateChanged,
            physicalStopFailed: false,
            physicalStopRetryRecommended: false);

    internal static StandingLeaseSafetyControlResult Rejected(
        string? operationId,
        string reason,
        StandingLeaseSafetyState? state = null,
        bool requiresActiveRunStop = false,
        bool durableOperationCommitted = false,
        bool durableStateChanged = false,
        bool physicalStopFailed = false,
        bool physicalStopRetryRecommended = false) =>
        new(
            StandingLeaseSafetyControlResultStatus.Rejected,
            reason,
            operationId,
            false,
            requiresActiveRunStop,
            state,
            durableOperationCommitted,
            durableStateChanged,
            physicalStopFailed,
            physicalStopRetryRecommended);

    internal StandingLeaseSafetyControlResult WithDurableOperationCommitted() =>
        new(
            Status,
            Reason,
            OperationId,
            Changed,
            RequiresActiveRunStop,
            State,
            durableOperationCommitted: true,
            durableStateChanged: DurableStateChanged,
            physicalStopFailed: PhysicalStopFailed,
            physicalStopRetryRecommended: PhysicalStopRetryRecommended);

    internal StandingLeaseSafetyControlResult WithPhysicalStopFailure() =>
        new(
            StandingLeaseSafetyControlResultStatus.Rejected,
            StandingLeaseSafetyReasonCodes.ActiveRunStopFailed,
            OperationId,
            changed: false,
            requiresActiveRunStop: RequiresActiveRunStop,
            state: State,
            durableOperationCommitted: DurableOperationCommitted,
            durableStateChanged: DurableStateChanged,
            physicalStopFailed: true,
            physicalStopRetryRecommended: true);
}

internal static class StandingLeaseSafetyPolicy
{
    internal static string? EvaluateControlBoundary(
        ConsentLease lease,
        UnattendedModeStatus unattendedMode,
        bool stopAllApplied,
        DateTimeOffset? unattendedEnabledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (unattendedMode == UnattendedModeStatus.Disabled)
        {
            return StandingLeaseSafetyReasonCodes.UnattendedDisabled;
        }

        // Stop All is a transaction-scoped revocation of the authorizations
        // that existed at its linearization point. The persisted flag is a
        // historical summary and must not become a permanent future gate.

        if (lease.Status == ConsentLeaseStatus.Revoked)
        {
            return StandingLeaseSafetyReasonCodes.LeaseRevoked;
        }

        if (lease.Status == ConsentLeaseStatus.Active &&
            unattendedEnabledAtUtc is not null &&
            lease.UpdatedAtUtc < unattendedEnabledAtUtc.Value)
        {
            return StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization;
        }

        return null;
    }

    internal static string? EvaluateWake(
        ConsentLease lease,
        UnattendedModeStatus unattendedMode,
        bool stopAllApplied,
        DateTimeOffset? unattendedEnabledAtUtc)
    {
        var controlFailure = EvaluateControlBoundary(
            lease,
            unattendedMode,
            stopAllApplied,
            unattendedEnabledAtUtc);
        if (controlFailure is not null)
        {
            return controlFailure;
        }

        if (lease.Status != ConsentLeaseStatus.Active)
        {
            return lease.Status switch
            {
                ConsentLeaseStatus.Pending => StandingLeaseSafetyReasonCodes.LeasePending,
                ConsentLeaseStatus.Expired => StandingLeaseSafetyReasonCodes.LeaseExpired,
                ConsentLeaseStatus.Exhausted => StandingLeaseSafetyReasonCodes.LeaseExhausted,
                ConsentLeaseStatus.Rejected => StandingLeaseSafetyReasonCodes.LeaseRejected,
                _ => "lease_status_invalid",
            };
        }

        return null;
    }
}
