using AgentRecorder.Core.Automation;

namespace AgentRecorder.Persistence;

/// <summary>
/// The closed set of lifecycle/recovery reasons that may describe a run which
/// crossed start-commit and later became an abnormal terminal chain.  Keeping
/// this list in one persistence policy prevents reservation and start-commit
/// replay validators from drifting apart or accepting arbitrary text.
/// </summary>
internal static class RecurringLifecycleTerminalReasonPolicy
{
    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "natural_exit_before_first_frame",
        "user_stop_before_first_frame",
        "safety_stop_before_first_frame",
        "session_interrupted_before_first_frame",
        "lifecycle_persistence_failure_before_first_frame",
        "backend_start_failed_before_first_frame",
        "recovery_after_start_commit",
        "recovery_after_recording_interrupted",
        "recovery_during_finalization",
        "recovery_before_settlement",
        "safety_stop",
        "session_interrupted",
        "backend_start_failed_after_first_frame",
        "lifecycle_persistence_failure_after_first_frame",
        "lifecycle_persistence_failure",
        "capture_exit_nonzero",
        "capture_output_invalid",
        "capture_output_missing",
        "capture_output_path_invalid",
        "capture_output_path_mismatch",
        "capture_duration_invalid",
        "capture_duration_negative",
        "capture_duration_exceeds_authorization",
        "capture_terminal_reason_invalid",
        "user_stop_output_invalid",
    };

    internal static bool IsKnownPostStart(string? reason) =>
        reason is not null && KnownReasons.Contains(reason);
}

/// <summary>
/// Shared integrity predicates for the durable recurring run/use/occurrence
/// chain. The version shapes below are derived from the current Task 264
/// authorization paths and Task 265/lifecycle transitions; they are not
/// open-ended compatibility ranges.
/// </summary>
internal static class RecurringReplayIntegrityPolicy
{
    internal static bool IsReservationOccurrenceVersion(long version) =>
        version is 4 or 5;

    internal static bool IsPostStartTerminalOccurrenceVersion(long version) =>
        version is 5 or 6;

    internal static bool HasClaimIdentityAndMonotonicEvidence(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        LeaseUse use,
        TimeSpan expectedDuration)
    {
        return IsFiniteVersion(occurrence.Version) &&
            IsFiniteVersion(run.Version) &&
            IsFiniteVersion(use.Version) &&
            occurrence.RunId == run.Id &&
            run.OccurrenceId == occurrence.Id &&
            use.LeaseId == lease.LeaseId &&
            use.OccurrenceId == occurrence.Id &&
            use.RunId == run.Id &&
            run.CreatedAtUtc == use.CreatedAtUtc &&
            occurrence.CreatedAtUtc <= occurrence.UpdatedAtUtc &&
            occurrence.CreatedAtUtc <= run.CreatedAtUtc &&
            run.CreatedAtUtc <= run.UpdatedAtUtc &&
            use.CreatedAtUtc <= use.UpdatedAtUtc &&
            occurrence.UpdatedAtUtc <= run.UpdatedAtUtc &&
            use.UpdatedAtUtc <= run.UpdatedAtUtc &&
            expectedDuration == lease.PerRunDuration &&
            expectedDuration > TimeSpan.Zero &&
            use.ReservedUseCount == 1 &&
            use.ReservedDuration == expectedDuration &&
            use.ReservedDuration > TimeSpan.Zero;
    }

    internal static bool IsInitialReservationChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        LeaseUse use)
    {
        return HasClaimIdentityAndMonotonicEvidence(lease, occurrence, run, use, lease.PerRunDuration) &&
            IsReservationOccurrenceVersion(occurrence.Version) &&
            occurrence.Status == PlanOccurrenceStatus.RunCreated &&
            occurrence.TerminalReasonCode is null &&
            occurrence.UpdatedAtUtc == run.CreatedAtUtc &&
            run.Status == RecordingRunStatus.Created &&
            !run.HasCrossedStartCommit &&
            run.TerminalReasonCode is null &&
            run.MediaArtifactId is null &&
            run.BundleId is null &&
            run.Version == 0 &&
            run.CreatedAtUtc == run.UpdatedAtUtc &&
            use.Status == LeaseUseStatus.Reserved &&
            use.Version == 0 &&
            use.ActualSettledDuration is null &&
            use.CreatedAtUtc == use.UpdatedAtUtc;
    }

    internal static bool IsExactCommittedChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        LeaseUse use)
    {
        return HasClaimIdentityAndMonotonicEvidence(lease, occurrence, run, use, lease.PerRunDuration) &&
            IsReservationOccurrenceVersion(occurrence.Version) &&
            occurrence.Status == PlanOccurrenceStatus.RunCreated &&
            occurrence.TerminalReasonCode is null &&
            occurrence.UpdatedAtUtc == run.CreatedAtUtc &&
            run.Status == RecordingRunStatus.StartCommitted &&
            run.HasCrossedStartCommit &&
            run.TerminalReasonCode is null &&
            run.MediaArtifactId is null &&
            run.BundleId is null &&
            run.Version == 2 &&
            run.UpdatedAtUtc == use.UpdatedAtUtc &&
            run.UpdatedAtUtc >= run.CreatedAtUtc &&
            use.Status == LeaseUseStatus.StartCommitted &&
            use.Version == 1 &&
            use.ActualSettledDuration is null;
    }

    internal static bool IsExactPreStartBlockedReleasedChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        LeaseUse use)
    {
        return IsFiniteVersion(occurrence.Version) &&
            IsFiniteVersion(run.Version) &&
            IsFiniteVersion(use.Version) &&
            IsPostStartTerminalOccurrenceVersion(occurrence.Version) &&
            occurrence.Status == PlanOccurrenceStatus.Blocked &&
            occurrence.RunId == run.Id &&
            occurrence.TerminalReasonCode is not null &&
            RecurringPreStartBlockReasonPolicy.IsKnown(occurrence.TerminalReasonCode) &&
            run.OccurrenceId == occurrence.Id &&
            run.Status == RecordingRunStatus.Failed &&
            !run.HasCrossedStartCommit &&
            run.TerminalReasonCode == occurrence.TerminalReasonCode &&
            run.MediaArtifactId is null &&
            run.BundleId is null &&
            run.Version == 1 &&
            run.CreatedAtUtc == use.CreatedAtUtc &&
            run.CreatedAtUtc <= run.UpdatedAtUtc &&
            use.LeaseId == lease.LeaseId &&
            use.OccurrenceId == occurrence.Id &&
            use.RunId == run.Id &&
            use.Status == LeaseUseStatus.Available &&
            use.Version == 1 &&
            use.ReservedUseCount == 0 &&
            use.ReservedDuration == TimeSpan.Zero &&
            use.ActualSettledDuration is null &&
            occurrence.CreatedAtUtc <= occurrence.UpdatedAtUtc &&
            occurrence.CreatedAtUtc <= run.CreatedAtUtc &&
            run.UpdatedAtUtc == use.UpdatedAtUtc &&
            occurrence.UpdatedAtUtc == run.UpdatedAtUtc;
    }

    internal static bool IsLegalAdvancedChain(
        RecurringConsentLease lease,
        PlanOccurrence occurrence,
        RecordingRun run,
        LeaseUse use)
    {
        if (!HasClaimIdentityAndMonotonicEvidence(lease, occurrence, run, use, lease.PerRunDuration))
            return false;

        return (occurrence.Status, run.Status, use.Status) switch
        {
            (PlanOccurrenceStatus.RunCreated, RecordingRunStatus.StartCommitted, LeaseUseStatus.StartCommitted) =>
                IsReservationOccurrenceVersion(occurrence.Version) &&
                occurrence.TerminalReasonCode is null &&
                run.HasCrossedStartCommit && run.TerminalReasonCode is null &&
                run.MediaArtifactId is null && run.BundleId is null &&
                run.Version == 2 && use.Version == 1 &&
                use.ActualSettledDuration is null &&
                run.UpdatedAtUtc == use.UpdatedAtUtc,

            (PlanOccurrenceStatus.RunCreated, RecordingRunStatus.Recording, LeaseUseStatus.Consumed) =>
                IsReservationOccurrenceVersion(occurrence.Version) &&
                occurrence.TerminalReasonCode is null &&
                run.HasCrossedStartCommit && run.TerminalReasonCode is null &&
                run.MediaArtifactId is null && run.BundleId is null &&
                run.Version == 3 && use.Version == 2 &&
                use.ActualSettledDuration is null &&
                run.UpdatedAtUtc == use.UpdatedAtUtc,

            (PlanOccurrenceStatus.RunCreated, RecordingRunStatus.Finalizing, LeaseUseStatus.Consumed) =>
                IsReservationOccurrenceVersion(occurrence.Version) &&
                occurrence.TerminalReasonCode is null &&
                run.HasCrossedStartCommit && run.TerminalReasonCode is null &&
                run.MediaArtifactId is null && run.BundleId is null &&
                run.Version == 4 && use.Version == 2 &&
                use.ActualSettledDuration is null,

            (PlanOccurrenceStatus.RunCreated, RecordingRunStatus.MediaReady, LeaseUseStatus.Consumed) =>
                IsReservationOccurrenceVersion(occurrence.Version) &&
                occurrence.TerminalReasonCode is null &&
                run.HasCrossedStartCommit && run.TerminalReasonCode is null &&
                run.MediaArtifactId is null && run.BundleId is null &&
                run.Version == 5 && use.Version == 2 &&
                use.ActualSettledDuration is null,

            (PlanOccurrenceStatus.Completed, RecordingRunStatus.Settled, LeaseUseStatus.Settled) =>
                IsPostStartTerminalOccurrenceVersion(occurrence.Version) &&
                run.HasCrossedStartCommit && run.TerminalReasonCode is null &&
                occurrence.TerminalReasonCode is null &&
                run.MediaArtifactId is null && run.BundleId is null &&
                run.Version == 6 && use.Version == 3 &&
                use.ActualSettledDuration is not null &&
                use.ActualSettledDuration.Value >= TimeSpan.Zero &&
                use.ActualSettledDuration.Value <= use.ReservedDuration &&
                occurrence.UpdatedAtUtc == run.UpdatedAtUtc &&
                run.UpdatedAtUtc == use.UpdatedAtUtc,

            (PlanOccurrenceStatus.Blocked, RecordingRunStatus.StartedUnknown, LeaseUseStatus.StartedUnknown) or
            (PlanOccurrenceStatus.Blocked, RecordingRunStatus.SessionInterrupted, LeaseUseStatus.StartedUnknown) or
            (PlanOccurrenceStatus.Blocked, RecordingRunStatus.Failed, LeaseUseStatus.StartedUnknown) =>
                IsPostStartTerminalOccurrenceVersion(occurrence.Version) &&
                run.HasCrossedStartCommit &&
                run.TerminalReasonCode is not null &&
                RecurringLifecycleTerminalReasonPolicy.IsKnownPostStart(run.TerminalReasonCode) &&
                run.TerminalReasonCode == occurrence.TerminalReasonCode &&
                run.MediaArtifactId is null && run.BundleId is null &&
                use.ActualSettledDuration is null &&
                IsLegalAbnormalRunVersionShape(run.Status, run.Version, use.Version) &&
                occurrence.UpdatedAtUtc == run.UpdatedAtUtc &&
                run.UpdatedAtUtc == use.UpdatedAtUtc,

            _ => false,
        };
    }

    private static bool IsLegalAbnormalRunVersionShape(
        RecordingRunStatus status,
        long runVersion,
        long useVersion) =>
        status switch
        {
            RecordingRunStatus.StartedUnknown =>
                (runVersion, useVersion) is (3, 2) or (4, 3),
            RecordingRunStatus.SessionInterrupted =>
                (runVersion, useVersion) is (4, 3) or (5, 3) or (6, 3),
            RecordingRunStatus.Failed =>
                (runVersion, useVersion) is (4, 3) or (5, 3),
            _ => false,
        };

    private static bool IsFiniteVersion(long version) =>
        version >= 0 && version < long.MaxValue;
}
