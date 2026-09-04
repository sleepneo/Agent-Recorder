namespace AgentRecorder.Core.Automation;

public readonly record struct Phase3TransitionResult(bool Succeeded, bool Changed, string ReasonCode)
{
    public static Phase3TransitionResult Success(string reasonCode = "transitioned") => new(true, true, reasonCode);
    public static Phase3TransitionResult Idempotent => new(true, false, "idempotent_noop");
    public static Phase3TransitionResult Failure(string reasonCode) => new(false, false, reasonCode);
}

/// <summary>
/// One authoritative transition matrix for all Phase 3 aggregates.
/// Aggregate methods delegate to these guards and never mutate another aggregate.
/// </summary>
public static class Phase3TransitionGuards
{
    public static bool CanTransition(PlanDefinitionStatus current, PlanDefinitionStatus next, out string reasonCode)
        => Evaluate(current, next, IsPlanDefinitionEdge, out reasonCode);

    public static bool CanTransition(PlanOccurrenceStatus current, PlanOccurrenceStatus next, out string reasonCode)
        => Evaluate(current, next, IsPlanOccurrenceEdge, out reasonCode);

    public static bool CanTransition(RecordingRunStatus current, RecordingRunStatus next, out string reasonCode)
        => Evaluate(current, next, IsRecordingRunEdge, out reasonCode);

    public static bool CanTransition(ConsentLeaseStatus current, ConsentLeaseStatus next, out string reasonCode)
        => Evaluate(current, next, IsConsentLeaseEdge, out reasonCode);

    public static bool CanTransition(LeaseUseStatus current, LeaseUseStatus next, out string reasonCode)
        => Evaluate(current, next, IsLeaseUseEdge, out reasonCode);

    public static bool IsTerminal(PlanDefinitionStatus status)
    {
        EnsureKnown(status);
        return status is PlanDefinitionStatus.Cancelled;
    }

    public static bool IsTerminal(PlanOccurrenceStatus status)
    {
        EnsureKnown(status);
        return status is PlanOccurrenceStatus.Completed or PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or
            PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired;
    }

    public static bool IsTerminal(RecordingRunStatus status)
    {
        EnsureKnown(status);
        return status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.Settled or RecordingRunStatus.SessionInterrupted or
            RecordingRunStatus.Failed;
    }

    public static bool IsTerminal(ConsentLeaseStatus status)
    {
        EnsureKnown(status);
        return status is ConsentLeaseStatus.Rejected or ConsentLeaseStatus.Revoked or ConsentLeaseStatus.Expired or ConsentLeaseStatus.Exhausted;
    }

    public static bool IsTerminal(LeaseUseStatus status)
    {
        EnsureKnown(status);
        return status is LeaseUseStatus.Settled or LeaseUseStatus.StartedUnknown;
    }

    /// <summary>
    /// The sole retryability decision. Failed before start commit remains retryable;
    /// failed after start commit is conservative and non-retryable. Invalid enum
    /// values throw rather than being interpreted as retryable.
    /// </summary>
    public static bool IsRunNonRetryable(RecordingRunStatus status, bool hasCrossedStartCommit)
    {
        EnsureKnown(status);
        if (!hasCrossedStartCommit && status is (RecordingRunStatus.StartCommitted or RecordingRunStatus.Recording or
            RecordingRunStatus.StartedUnknown or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady or
            RecordingRunStatus.Settled or RecordingRunStatus.SessionInterrupted))
        {
            throw new Phase3DomainException("start_commit_required", "This run state requires a committed start.");
        }

        if (hasCrossedStartCommit && status is (RecordingRunStatus.Created or RecordingRunStatus.Preparing))
        {
            throw new Phase3DomainException("invalid_start_commit_flag", "A pre-commit run state must not claim to have crossed start commit.");
        }

        return (hasCrossedStartCommit && status is (RecordingRunStatus.StartCommitted or RecordingRunStatus.Recording or
            RecordingRunStatus.StartedUnknown or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady or
            RecordingRunStatus.Settled or RecordingRunStatus.SessionInterrupted)) ||
            (status == RecordingRunStatus.Failed && hasCrossedStartCommit);
    }

    public static bool IsUseQuotaConsumed(LeaseUseStatus status)
    {
        EnsureKnown(status);
        return status is LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed or LeaseUseStatus.Settled or LeaseUseStatus.StartedUnknown;
    }

    public static bool IsOccurrenceAbleToCreateRun(PlanOccurrenceStatus status)
    {
        EnsureKnown(status);
        return status is PlanOccurrenceStatus.Authorized or PlanOccurrenceStatus.PendingConfirmation;
    }

    public static bool IsPlanDefinitionEdge(PlanDefinitionStatus current, PlanDefinitionStatus next) => (current, next) switch
    {
        (PlanDefinitionStatus.Draft, PlanDefinitionStatus.Enabled) => true,
        (PlanDefinitionStatus.Enabled, PlanDefinitionStatus.Paused or PlanDefinitionStatus.Cancelled) => true,
        (PlanDefinitionStatus.Paused, PlanDefinitionStatus.Enabled or PlanDefinitionStatus.Cancelled) => true,
        _ => false,
    };

    public static bool IsPlanOccurrenceEdge(PlanOccurrenceStatus current, PlanOccurrenceStatus next) => (current, next) switch
    {
        (PlanOccurrenceStatus.Scheduled, PlanOccurrenceStatus.Due or PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.Due, PlanOccurrenceStatus.Rechecking or PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.Rechecking, PlanOccurrenceStatus.PendingLeaseApproval or PlanOccurrenceStatus.Authorized or PlanOccurrenceStatus.PendingConfirmation or PlanOccurrenceStatus.Missed or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.PendingLeaseApproval, PlanOccurrenceStatus.Authorized or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.Authorized, PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.PendingConfirmation, PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        (PlanOccurrenceStatus.RunCreated, PlanOccurrenceStatus.Completed or PlanOccurrenceStatus.Blocked or PlanOccurrenceStatus.Cancelled or PlanOccurrenceStatus.Expired) => true,
        _ => false,
    };

    public static bool IsRecordingRunEdge(RecordingRunStatus current, RecordingRunStatus next) => (current, next) switch
    {
        (RecordingRunStatus.Created, RecordingRunStatus.Preparing or RecordingRunStatus.Failed) => true,
        (RecordingRunStatus.Preparing, RecordingRunStatus.StartCommitted or RecordingRunStatus.Failed) => true,
        (RecordingRunStatus.StartCommitted, RecordingRunStatus.Recording or RecordingRunStatus.StartedUnknown or RecordingRunStatus.Failed) => true,
        (RecordingRunStatus.Recording, RecordingRunStatus.Finalizing or RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed) => true,
        (RecordingRunStatus.Finalizing, RecordingRunStatus.MediaReady or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed) => true,
        (RecordingRunStatus.MediaReady, RecordingRunStatus.Settled) => true,
        _ => false,
    };

    public static bool IsConsentLeaseEdge(ConsentLeaseStatus current, ConsentLeaseStatus next) => (current, next) switch
    {
        (ConsentLeaseStatus.Pending, ConsentLeaseStatus.Active or ConsentLeaseStatus.Rejected or ConsentLeaseStatus.Revoked or ConsentLeaseStatus.Expired) => true,
        (ConsentLeaseStatus.Active, ConsentLeaseStatus.Revoked or ConsentLeaseStatus.Expired or ConsentLeaseStatus.Exhausted) => true,
        _ => false,
    };

    public static bool IsLeaseUseEdge(LeaseUseStatus current, LeaseUseStatus next) => (current, next) switch
    {
        (LeaseUseStatus.Available, LeaseUseStatus.Reserved) => true,
        (LeaseUseStatus.Reserved, LeaseUseStatus.Available or LeaseUseStatus.StartCommitted) => true,
        (LeaseUseStatus.StartCommitted, LeaseUseStatus.Consumed or LeaseUseStatus.StartedUnknown) => true,
        (LeaseUseStatus.Consumed, LeaseUseStatus.Settled) => true,
        _ => false,
    };

    private static bool Evaluate<TStatus>(TStatus current, TStatus next, Func<TStatus, TStatus, bool> isEdge, out string reasonCode)
        where TStatus : struct, Enum
    {
        if (!IsKnown(current) || !IsKnown(next))
        {
            reasonCode = "unknown_state";
            return false;
        }

        if (EqualityComparer<TStatus>.Default.Equals(current, next))
        {
            reasonCode = "idempotent_noop";
            return true;
        }

        if (isEdge(current, next))
        {
            reasonCode = "transitioned";
            return true;
        }

        reasonCode = "invalid_transition";
        return false;

        bool IsKnown(TStatus state) => state switch
        {
            PlanDefinitionStatus => Enum.IsDefined(state),
            PlanOccurrenceStatus => Enum.IsDefined(state),
            RecordingRunStatus => Enum.IsDefined(state),
            ConsentLeaseStatus => Enum.IsDefined(state),
            LeaseUseStatus => Enum.IsDefined(state),
            _ => false,
        };
    }

    private static void EnsureKnown<TStatus>(TStatus status)
        where TStatus : struct, Enum
    {
        if (!Enum.IsDefined(status))
        {
            throw new Phase3DomainException("unknown_state", $"Unknown {typeof(TStatus).Name} value.");
        }
    }
}
