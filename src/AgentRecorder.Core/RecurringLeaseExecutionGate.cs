using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Recurring proof gate. Persistence must call the consuming overload only
/// while its immediate transaction is open; this class never publishes an
/// authorization context and never opens a database connection.
/// </summary>
internal static class RecurringLeaseExecutionGate
{
    internal static bool TryValidateDurableSnapshot(
        RecurringLeaseExecutionSnapshot? snapshot,
        out string failureReason,
        bool includeCurrentSafety = true)
    {
        failureReason = "execution_snapshot_missing";
        if (snapshot is null)
            return false;

        try
        {
            if (!snapshot.Plan.IsPeriodic || snapshot.Plan.Status != PlanDefinitionStatus.Enabled ||
                snapshot.Plan.Id != snapshot.Lease.PlanId ||
                snapshot.Lease.ConfigurationRef.PlanId != snapshot.Plan.Id ||
                !snapshot.Lease.ConfigurationRef.ProfileRef.Matches(snapshot.Profile) ||
                !string.Equals(snapshot.Specification.ConfigurationDigest, snapshot.Lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Specification.ScheduleDigest, snapshot.Lease.ConfigurationRef.ScheduleDigest, StringComparison.Ordinal) ||
                snapshot.Specification.ScheduleRevision != snapshot.Lease.ConfigurationRef.ScheduleRevision ||
                !string.Equals(snapshot.Specification.TimeZoneRulesDigest, snapshot.Lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal))
            {
                failureReason = "execution_snapshot_invalid";
                return false;
            }

            if (!string.Equals(snapshot.Specification.OccurrenceId, snapshot.Occurrence.Id, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Specification.OccurrenceIdentity, snapshot.Specification.OccurrenceIdentity.Trim(), StringComparison.Ordinal))
            {
                failureReason = "execution_snapshot_invalid";
                return false;
            }

            if (snapshot.Specification.OccurrenceId != snapshot.Occurrence.Id ||
                snapshot.Specification.PlanId != snapshot.Plan.Id ||
                snapshot.Specification.LeaseId != snapshot.Lease.Id ||
                snapshot.Specification.ConfigurationDigest != snapshot.Lease.ConfigurationRef.ConfigurationDigest ||
                snapshot.Specification.LeaseAuthorizationDigest != snapshot.Lease.AuthorizationDigest ||
                snapshot.Specification.ProfileId != snapshot.Profile.ProfileId ||
                snapshot.Specification.ProfileVersion != snapshot.Profile.ProfileVersion ||
                snapshot.Specification.ProfileDigest != snapshot.Profile.ProfileDigest ||
                snapshot.Specification.LocalApprovalId != snapshot.Approval.ApprovalId ||
                snapshot.Specification.LocalApprovalDigest != snapshot.Approval.ApprovalDigest ||
                snapshot.Specification.ApprovedCurrentUserSid != snapshot.Approval.CurrentUserSid ||
                snapshot.Specification.ApprovedSessionBinding != snapshot.Approval.SessionBinding ||
                !string.Equals(
                    RecurringOccurrenceExecutionSpecificationDigest.Compute(snapshot.Specification),
                    snapshot.Specification.SpecificationDigest,
                    StringComparison.Ordinal))
            {
                failureReason = "execution_snapshot_invalid";
                return false;
            }

            if (snapshot.Profile.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
                snapshot.Profile.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
                snapshot.Profile.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
                snapshot.Profile.AudioMode != AuthorizedAudioMode.None ||
                snapshot.Profile.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
                snapshot.Profile.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
                snapshot.Profile.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
                snapshot.Specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
                snapshot.Specification.AudioMode != AuthorizedAudioMode.None ||
                snapshot.Specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists)
            {
                failureReason = "execution_scope_policy_mismatch";
                return false;
            }

            if (snapshot.Approval.LeaseId != snapshot.Lease.Id ||
                snapshot.Approval.PlanId != snapshot.Plan.Id ||
                snapshot.Approval.ConfigurationDigest != snapshot.Lease.ConfigurationRef.ConfigurationDigest ||
                snapshot.Approval.AuthorizationDigest != snapshot.Lease.AuthorizationDigest ||
                snapshot.Approval.ApprovalKind != RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind ||
                snapshot.Approval.ApprovalVersion != RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion)
            {
                failureReason = "execution_snapshot_invalid";
                return false;
            }

            if (snapshot.Occurrence.Status != PlanOccurrenceStatus.RunCreated ||
                snapshot.Occurrence.TerminalReasonCode is not null ||
                snapshot.Occurrence.RunId != snapshot.Run.Id ||
                snapshot.Run.OccurrenceId != snapshot.Occurrence.Id ||
                snapshot.Run.Status != RecordingRunStatus.StartCommitted ||
                !snapshot.Run.HasCrossedStartCommit ||
                !snapshot.Run.IsNonRetryable ||
                snapshot.Run.TerminalReasonCode is not null ||
                snapshot.Run.MediaArtifactId is not null ||
                snapshot.Run.BundleId is not null ||
                snapshot.Use.LeaseId != snapshot.Lease.Id ||
                snapshot.Use.OccurrenceId != snapshot.Occurrence.Id ||
                snapshot.Use.RunId != snapshot.Run.Id ||
                snapshot.Use.Status != LeaseUseStatus.StartCommitted ||
                !snapshot.Use.IsQuotaConsumed ||
                snapshot.Use.ReservedUseCount != 1 ||
                snapshot.Use.ReservedDuration != snapshot.Specification.Duration ||
                snapshot.Use.ActualSettledDuration is not null ||
                snapshot.Run.CreatedAtUtc != snapshot.Use.CreatedAtUtc ||
                snapshot.Run.CreatedAtUtc != snapshot.Occurrence.UpdatedAtUtc ||
                snapshot.Run.UpdatedAtUtc != snapshot.Use.UpdatedAtUtc ||
                snapshot.Run.CreatedAtUtc > snapshot.Run.UpdatedAtUtc ||
                snapshot.Use.CreatedAtUtc > snapshot.Use.UpdatedAtUtc ||
                !IsFiniteVersion(snapshot.Occurrence.Version) ||
                !IsFiniteVersion(snapshot.Run.Version) ||
                !IsFiniteVersion(snapshot.Use.Version))
            {
                failureReason = "execution_state_not_start_committed";
                return false;
            }

            if (includeCurrentSafety && snapshot.Lease.Status == ConsentLeaseStatus.Revoked)
            {
                failureReason = "lease_revoked";
                return false;
            }

            if (snapshot.Lease.Status is not (ConsentLeaseStatus.Active or ConsentLeaseStatus.Exhausted or ConsentLeaseStatus.Revoked) ||
                !IsFiniteVersion(snapshot.Lease.Version))
            {
                failureReason = "lease_not_executable";
                return false;
            }

            var receipt = new RecurringStartCommitReceipt(
                snapshot.Plan,
                snapshot.Occurrence,
                snapshot.Lease,
                snapshot.Specification,
                snapshot.Run,
                snapshot.Use,
                snapshot.Approval,
                snapshot.Run.UpdatedAtUtc,
                snapshot.Quota,
                snapshot.LeaseEntries);
            CaptureAuthorizationProofIssuer.ValidateRecurringStartCommitReceipt(
                receipt,
                allowRevokedLease: !includeCurrentSafety);

            if (includeCurrentSafety)
            {
                if (!IsCurrentSafetyEvidenceStructurallyValid(snapshot.Safety) ||
                    snapshot.Safety.UnattendedMode != UnattendedModeStatus.Enabled)
                {
                    failureReason = "unattended_disabled";
                    return false;
                }

                if (snapshot.Safety.StopAllApplied &&
                    snapshot.Safety.StopAllAppliedAtUtc is { } stopAllAt &&
                    snapshot.Approval.ApprovedAtUtc <= stopAllAt)
                {
                    failureReason = "stop_all_active";
                    return false;
                }

                if (snapshot.Safety.UnattendedEnabledAtUtc is { } enabledAt &&
                    snapshot.Approval.ApprovedAtUtc < enabledAt)
                {
                    failureReason = "unattended_reenable_requires_new_authorization";
                    return false;
                }
            }

            failureReason = "";
            return true;
        }
        catch (Phase3DomainException)
        {
            failureReason = "persisted_snapshot_invalid";
            return false;
        }
        catch (InvalidOperationException)
        {
            failureReason = "persisted_snapshot_invalid";
            return false;
        }
        catch (OverflowException)
        {
            failureReason = "persisted_snapshot_invalid";
            return false;
        }
    }

    internal static bool TryValidateTicketSnapshotBinding(
        RecurringLeaseCaptureExecutionTicket? ticket,
        RecurringLeaseExecutionSnapshot? snapshot,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "execution_snapshot_ticket_binding_invalid";
        if (ticket is null || snapshot is null || nowUtc.Offset != TimeSpan.Zero)
            return false;

        try
        {
            var revokedSafetyChange = snapshot.Lease.Status == ConsentLeaseStatus.Revoked;
            if (revokedSafetyChange)
            {
                if (ticket.LeaseStatus is not (ConsentLeaseStatus.Active or ConsentLeaseStatus.Exhausted) ||
                    ticket.LeaseVersion < 0 ||
                    ticket.LeaseVersion == long.MaxValue)
                {
                    return false;
                }

                var expectedRevokedVersion = checked(ticket.LeaseVersion + 1);
                if (snapshot.Lease.Version != expectedRevokedVersion ||
                    snapshot.Lease.UpdatedAtUtc < ticket.LeaseUpdatedAtUtc ||
                    snapshot.Lease.UpdatedAtUtc < ticket.LeaseCreatedAtUtc ||
                    snapshot.Lease.UpdatedAtUtc > nowUtc)
                {
                    return false;
                }
            }

            if (ticket.PlanId != snapshot.Plan.Id ||
                ticket.PlanIsOneTime != snapshot.Plan.IsOneTime ||
                ticket.PlanIsPeriodic != snapshot.Plan.IsPeriodic ||
                ticket.PlanStatus != snapshot.Plan.Status ||
                ticket.PlanCreatedAtUtc != snapshot.Plan.CreatedAtUtc ||
                ticket.PlanUpdatedAtUtc != snapshot.Plan.UpdatedAtUtc ||
                ticket.PlanVersion != snapshot.Plan.Version ||
                ticket.OccurrenceId != snapshot.Occurrence.Id ||
                ticket.OccurrencePlanId != snapshot.Occurrence.PlanId ||
                ticket.OccurrenceStatus != snapshot.Occurrence.Status ||
                ticket.OccurrenceRunId != snapshot.Occurrence.RunId ||
                ticket.OccurrenceTerminalReasonCode != snapshot.Occurrence.TerminalReasonCode ||
                ticket.OccurrenceWindowStartUtc != snapshot.Occurrence.WindowStartUtc ||
                ticket.OccurrenceWindowEndUtc != snapshot.Occurrence.WindowEndUtc ||
                ticket.OccurrenceCreatedAtUtc != snapshot.Occurrence.CreatedAtUtc ||
                ticket.OccurrenceUpdatedAtUtc != snapshot.Occurrence.UpdatedAtUtc ||
                ticket.OccurrenceVersion != snapshot.Occurrence.Version ||
                ticket.LeaseId != snapshot.Lease.Id ||
                ticket.LeasePlanId != snapshot.Lease.PlanId ||
                ticket.LeaseOccurrenceId != snapshot.Occurrence.Id ||
                !revokedSafetyChange && ticket.LeaseStatus != snapshot.Lease.Status ||
                ticket.LeaseValidFromUtc != snapshot.Lease.ValidFromUtc ||
                ticket.LeaseValidUntilUtc != snapshot.Lease.ValidUntilUtc ||
                ticket.LeaseAuthorizedPlanLatestEndUtc != snapshot.Lease.AuthorizedPlanLatestEndUtc ||
                ticket.LeasePerRunDuration != snapshot.Lease.PerRunDuration ||
                ticket.LeaseMaxUses != snapshot.Lease.MaxUses ||
                ticket.LeaseMaxCumulativeDuration != snapshot.Lease.MaxCumulativeDuration ||
                ticket.LeaseAuthorizationDigest != snapshot.Lease.AuthorizationDigest ||
                ticket.LeaseCreatedAtUtc != snapshot.Lease.CreatedAtUtc ||
                !revokedSafetyChange && ticket.LeaseUpdatedAtUtc != snapshot.Lease.UpdatedAtUtc ||
                !revokedSafetyChange && ticket.LeaseVersion != snapshot.Lease.Version ||
                ticket.UseId != snapshot.Use.Id ||
                ticket.UseLeaseId != snapshot.Use.LeaseId ||
                ticket.UseOccurrenceId != snapshot.Use.OccurrenceId ||
                ticket.UseRunId != snapshot.Use.RunId ||
                ticket.UseStatus != snapshot.Use.Status ||
                ticket.UseReservedUseCount != snapshot.Use.ReservedUseCount ||
                ticket.UseReservedDuration != snapshot.Use.ReservedDuration ||
                ticket.UseActualSettledDuration != snapshot.Use.ActualSettledDuration ||
                ticket.UseCreatedAtUtc != snapshot.Use.CreatedAtUtc ||
                ticket.UseUpdatedAtUtc != snapshot.Use.UpdatedAtUtc ||
                ticket.UseVersion != snapshot.Use.Version ||
                ticket.UseIsQuotaConsumed != snapshot.Use.IsQuotaConsumed ||
                ticket.RunId != snapshot.Run.Id ||
                ticket.RunOccurrenceId != snapshot.Run.OccurrenceId ||
                ticket.RunStatus != snapshot.Run.Status ||
                ticket.RunHasCrossedStartCommit != snapshot.Run.HasCrossedStartCommit ||
                ticket.RunMediaArtifactId != snapshot.Run.MediaArtifactId ||
                ticket.RunBundleId != snapshot.Run.BundleId ||
                ticket.RunTerminalReasonCode != snapshot.Run.TerminalReasonCode ||
                ticket.RunCreatedAtUtc != snapshot.Run.CreatedAtUtc ||
                ticket.RunUpdatedAtUtc != snapshot.Run.UpdatedAtUtc ||
                ticket.RunVersion != snapshot.Run.Version ||
                ticket.RunIsNonRetryable != snapshot.Run.IsNonRetryable ||
                !AreEquivalentSpecification(ticket.Specification, snapshot.Specification) ||
                ticket.ProfileId != snapshot.Profile.ProfileId ||
                ticket.ProfileBindingBoundAtUtc != snapshot.ProfileBindingBoundAtUtc ||
                ticket.ProfileVersion != snapshot.Profile.ProfileVersion ||
                ticket.ProfileDigest != snapshot.Profile.ProfileDigest ||
                ticket.TargetType != AuthorizedScopeTargetType.FixedRegion ||
                ticket.TargetType != snapshot.Profile.TargetType ||
                ticket.CaptureSemantics != snapshot.Profile.CaptureSemantics ||
                ticket.CoordinateSpace != snapshot.Profile.CoordinateSpace ||
                ticket.DisplayIdentityStatus != snapshot.Profile.DisplayIdentityStatus ||
                ticket.RebindPolicy != snapshot.Profile.RebindPolicy ||
                ticket.VirtualScreenRegion != snapshot.Profile.VirtualScreenRegion ||
                ticket.DisplayBounds != snapshot.Profile.DisplayBounds ||
                ticket.RegionWithinDisplay != snapshot.Profile.RegionWithinDisplay ||
                ticket.StableDisplayFingerprint != snapshot.Profile.StableDisplayFingerprint ||
                ticket.DpiX != snapshot.Profile.DpiX ||
                ticket.DpiY != snapshot.Profile.DpiY ||
                ticket.PhysicalWidth != snapshot.Profile.PhysicalWidth ||
                ticket.PhysicalHeight != snapshot.Profile.PhysicalHeight ||
                ticket.Orientation != snapshot.Profile.Orientation ||
                ticket.TopologyDigest != snapshot.Profile.TopologyDigest ||
                ticket.Backend != snapshot.Profile.Backend ||
                ticket.AudioMode != snapshot.Profile.AudioMode ||
                ticket.Duration != snapshot.Profile.Duration ||
                ticket.CountdownSeconds != snapshot.Profile.CountdownSeconds ||
                ticket.NormalizedOutputDirectory != snapshot.Profile.OutputDirectory ||
                ticket.OutputConflictPolicy != snapshot.Profile.OutputConflictPolicy ||
                ticket.WakePolicy != snapshot.Profile.WakePolicy ||
                ticket.DesktopRequirement != snapshot.Profile.DesktopRequirement)
            {
                return false;
            }

            failureReason = "";
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TryValidateCurrentExecutionWindow(
        RecurringLeaseExecutionSnapshot? snapshot,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "execution_window_invalid";
        if (snapshot is null || nowUtc.Offset != TimeSpan.Zero)
            return false;

        try
        {
            if (nowUtc < snapshot.Specification.ScheduledStartUtc ||
                nowUtc > snapshot.Specification.LatestStartUtc ||
                nowUtc < snapshot.Occurrence.WindowStartUtc ||
                nowUtc >= snapshot.Occurrence.WindowEndUtc ||
                nowUtc < snapshot.Lease.ValidFromUtc ||
                nowUtc >= snapshot.Lease.ValidUntilUtc)
            {
                return false;
            }

            var endTicks = checked(nowUtc.UtcDateTime.Ticks + snapshot.Specification.Duration.Ticks);
            if (endTicks > snapshot.Specification.PlannedEndUtc.UtcDateTime.Ticks ||
                endTicks > snapshot.Lease.ValidUntilUtc.UtcDateTime.Ticks)
            {
                return false;
            }

            failureReason = "";
            return true;
        }
        catch (OverflowException)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static RecurringLeaseCurrentSafetyDecision EvaluateCurrentSafety(
        RecurringLeaseExecutionSnapshot? snapshot)
    {
        if (snapshot is null)
            return new(RecurringLeaseCurrentSafetyStatus.StateInvalid);

        try
        {
            if (!IsCurrentSafetyEvidenceStructurallyValid(snapshot.Safety))
            {
                return new(RecurringLeaseCurrentSafetyStatus.StateInvalid);
            }

            if (snapshot.Safety.UnattendedMode != UnattendedModeStatus.Enabled)
                return new(RecurringLeaseCurrentSafetyStatus.UnattendedDisabled);

            if (snapshot.Safety.StopAllApplied &&
                snapshot.Safety.StopAllAppliedAtUtc is { } stopAllAt &&
                snapshot.Approval.ApprovedAtUtc <= stopAllAt)
            {
                return new(RecurringLeaseCurrentSafetyStatus.StopAllActive);
            }

            if (snapshot.Lease.Status == ConsentLeaseStatus.Revoked)
                return new(RecurringLeaseCurrentSafetyStatus.LeaseRevoked);

            if (snapshot.Safety.UnattendedEnabledAtUtc is { } enabledAt &&
                snapshot.Approval.ApprovedAtUtc < enabledAt)
            {
                return new(RecurringLeaseCurrentSafetyStatus.ReenableRequiresNewAuthorization);
            }

            return new(RecurringLeaseCurrentSafetyStatus.Allowed);
        }
        catch (Exception)
        {
            return new(RecurringLeaseCurrentSafetyStatus.StateInvalid);
        }
    }

    private static bool AreEquivalentSpecification(
        RecurringOccurrenceExecutionSpecification left,
        RecurringOccurrenceExecutionSpecification right) =>
        left.PlanId == right.PlanId &&
        left.OccurrenceId == right.OccurrenceId &&
        left.OccurrenceIdentity == right.OccurrenceIdentity &&
        left.LeaseId == right.LeaseId &&
        left.ScheduleRevision == right.ScheduleRevision &&
        left.ScheduleDigest == right.ScheduleDigest &&
        left.TimeZoneRulesDigest == right.TimeZoneRulesDigest &&
        left.ProfileId == right.ProfileId &&
        left.ProfileVersion == right.ProfileVersion &&
        left.ProfileDigest == right.ProfileDigest &&
        left.ConfigurationDigest == right.ConfigurationDigest &&
        left.LeaseAuthorizationDigest == right.LeaseAuthorizationDigest &&
        left.LocalApprovalId == right.LocalApprovalId &&
        left.LocalApprovalDigest == right.LocalApprovalDigest &&
        left.ScheduledStartUtc == right.ScheduledStartUtc &&
        left.LatestStartUtc == right.LatestStartUtc &&
        left.PlannedEndUtc == right.PlannedEndUtc &&
        left.EvaluatedAtUtc == right.EvaluatedAtUtc &&
        left.StableDisplayFingerprint == right.StableDisplayFingerprint &&
        left.DisplayBounds == right.DisplayBounds &&
        left.RegionWithinDisplay == right.RegionWithinDisplay &&
        left.VirtualScreenRegion == right.VirtualScreenRegion &&
        left.DpiX == right.DpiX &&
        left.DpiY == right.DpiY &&
        left.PhysicalWidth == right.PhysicalWidth &&
        left.PhysicalHeight == right.PhysicalHeight &&
        left.Orientation == right.Orientation &&
        left.TopologyDigest == right.TopologyDigest &&
        left.Backend == right.Backend &&
        left.AudioMode == right.AudioMode &&
        left.Duration == right.Duration &&
        left.CountdownSeconds == right.CountdownSeconds &&
        left.NormalizedOutputDirectory == right.NormalizedOutputDirectory &&
        left.FrozenOutputFileName == right.FrozenOutputFileName &&
        left.FrozenOutputFilePath == right.FrozenOutputFilePath &&
        left.OutputConflictPolicy == right.OutputConflictPolicy &&
        left.ApprovedCurrentUserSid == right.ApprovedCurrentUserSid &&
        left.ApprovedSessionBinding == right.ApprovedSessionBinding &&
        left.SpecificationDigest == right.SpecificationDigest;

    internal static bool TryAuthorizeAndConsumeRecurringLeaseUseInTransaction(
        CaptureAuthorizationProof? proof,
        RecurringLeaseExecutionSnapshot? snapshot,
        StandingLeaseExecutionEnvironment? environment,
        DateTimeOffset trustedNowUtc,
        out string failureReason)
    {
        failureReason = "proof_missing";
        if (proof is null)
            return false;

        if (proof.Kind != CaptureAuthorizationProofKind.RecurringLeaseUse ||
            proof is not RecurringLeaseUseProof recurringProof)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        if (snapshot is null)
        {
            failureReason = "execution_snapshot_missing";
            return false;
        }

        if (!TryValidateDurableSnapshot(snapshot, out failureReason))
            return false;

        if (!TryValidateRecurringProofTimeWindow(recurringProof, snapshot, trustedNowUtc, out failureReason))
            return false;

        if (environment is null)
        {
            failureReason = "execution_environment_unavailable";
            return false;
        }

        if (environment.NowUtc.Offset != TimeSpan.Zero ||
            environment.NowUtc.UtcDateTime.Ticks != trustedNowUtc.UtcDateTime.Ticks)
        {
            failureReason = "execution_environment_time_mismatch";
            return false;
        }

        if (!TryValidateRecurringProofBinding(
                recurringProof,
                snapshot,
                requireConsumed: false,
                out failureReason))
        {
            return false;
        }

        if (snapshot.Safety.UnattendedMode != UnattendedModeStatus.Enabled)
        {
            failureReason = "unattended_disabled";
            return false;
        }

        if (snapshot.Safety.StopAllApplied &&
            snapshot.Safety.StopAllAppliedAtUtc is { } stopAllAt &&
            snapshot.Approval.ApprovedAtUtc <= stopAllAt)
        {
            failureReason = "stop_all_active";
            return false;
        }

        if (snapshot.Lease.Status == ConsentLeaseStatus.Revoked)
        {
            failureReason = "lease_revoked";
            return false;
        }

        if (snapshot.Safety.UnattendedEnabledAtUtc is { } enabledAt &&
            snapshot.Approval.ApprovedAtUtc < enabledAt)
        {
            failureReason = "unattended_reenable_requires_new_authorization";
            return false;
        }

        if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
                environment,
                FixedRegionExecutionEnvironmentRequirements.FromRecurringSpecification(snapshot.Specification),
                out failureReason))
        {
            return false;
        }

        // This is intentionally the only mutable operation in the gate.
        if (!recurringProof.TryConsume(trustedNowUtc, out failureReason))
            return false;

        failureReason = "";
        return true;
    }

    /// <summary>
    /// Performs the non-mutating proof/time checks that must happen before a
    /// live environment query. This prevents an obviously expired or
    /// out-of-window proof from causing an unnecessary provider call while
    /// keeping the final gate as the only consumer.
    /// </summary>
    internal static bool TryValidateRecurringProofTimeWindow(
        RecurringLeaseUseProof recurringProof,
        RecurringLeaseExecutionSnapshot snapshot,
        DateTimeOffset trustedNowUtc,
        out string failureReason)
    {
        if (trustedNowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "execution_time_not_utc";
            return false;
        }

        if (!recurringProof.CheckAvailableAt(trustedNowUtc, out failureReason))
            return false;

        if (recurringProof.IssuedAtUtc.Offset != TimeSpan.Zero ||
            recurringProof.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "execution_time_not_utc";
            return false;
        }

        if (trustedNowUtc < recurringProof.IssuedAtUtc)
        {
            failureReason = "proof_not_yet_valid";
            return false;
        }

        var expectedProofExpiry = snapshot.Specification.PlannedEndUtc <= snapshot.Lease.ValidUntilUtc
            ? snapshot.Specification.PlannedEndUtc
            : snapshot.Lease.ValidUntilUtc;
        if (recurringProof.IssuedAtUtc != snapshot.Run.UpdatedAtUtc ||
            recurringProof.ExpiresAtUtc != expectedProofExpiry)
        {
            failureReason = "proof_time_binding_mismatch";
            return false;
        }

        if (trustedNowUtc >= recurringProof.ExpiresAtUtc)
        {
            failureReason = "proof_expired";
            return false;
        }

        if (trustedNowUtc < snapshot.Specification.ScheduledStartUtc ||
            trustedNowUtc > snapshot.Specification.LatestStartUtc ||
            trustedNowUtc < snapshot.Occurrence.WindowStartUtc ||
            trustedNowUtc >= snapshot.Occurrence.WindowEndUtc ||
            trustedNowUtc < snapshot.Lease.ValidFromUtc ||
            trustedNowUtc >= snapshot.Lease.ValidUntilUtc)
        {
            failureReason = "execution_window_invalid";
            return false;
        }

        long endTicks;
        try
        {
            endTicks = checked(trustedNowUtc.UtcDateTime.Ticks + snapshot.Specification.Duration.Ticks);
        }
        catch (OverflowException)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }

        if (endTicks > snapshot.Specification.PlannedEndUtc.UtcDateTime.Ticks ||
            endTicks > snapshot.Lease.ValidUntilUtc.UtcDateTime.Ticks ||
            endTicks > recurringProof.ExpiresAtUtc.UtcDateTime.Ticks)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }

        failureReason = "";
        return true;
    }

    /// <summary>
    /// Recomputes every non-time proof binding from the exact durable
    /// snapshot. The recurring authorization and the consuming gate share
    /// this check so the post-commit handoff cannot silently weaken the
    /// proof contract.
    /// </summary>
    internal static bool TryValidateRecurringProofBinding(
        RecurringLeaseUseProof recurringProof,
        RecurringLeaseExecutionSnapshot snapshot,
        bool requireConsumed,
        out string failureReason)
    {
        failureReason = "proof_binding_mismatch";
        if (recurringProof is null || snapshot is null)
            return false;

        if (requireConsumed && !recurringProof.IsConsumed)
        {
            failureReason = "proof_not_consumed";
            return false;
        }

        if (!string.Equals(recurringProof.RecordingId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.RunId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.LeaseId, snapshot.Lease.Id, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.LeaseUseId, snapshot.Use.Id, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.OccurrenceIdentity, snapshot.Specification.OccurrenceIdentity, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.SpecificationDigest, snapshot.Specification.SpecificationDigest, StringComparison.Ordinal))
        {
            failureReason = "proof_identity_mismatch";
            return false;
        }

        if (!string.Equals(
                recurringProof.AuthorizationSourceId,
                CaptureAuthorizationProofIssuer.RecurringAuthorizationSourceId(snapshot.Lease.Id, snapshot.Use.Id),
                StringComparison.Ordinal))
        {
            failureReason = "proof_source_mismatch";
            return false;
        }

        if (!string.Equals(
                recurringProof.CapturePlanDigest,
                CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUsePlanDigest(
                    snapshot.Plan, snapshot.Occurrence, snapshot.Specification),
                StringComparison.Ordinal))
        {
            failureReason = "proof_plan_digest_mismatch";
            return false;
        }

        if (!string.Equals(
                recurringProof.ScopeDigest,
                CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUseScopeDigest(
                    snapshot.Specification, snapshot.Lease, snapshot.Approval),
                StringComparison.Ordinal))
        {
            failureReason = "proof_scope_digest_mismatch";
            return false;
        }

        if (!string.Equals(recurringProof.CurrentUserSid, snapshot.Approval.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.SessionBinding, snapshot.Approval.SessionBinding, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.UserSessionBinding, recurringProof.CurrentUserSid + "|" + recurringProof.SessionBinding, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.CurrentUserSid, snapshot.Specification.ApprovedCurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(recurringProof.SessionBinding, snapshot.Specification.ApprovedSessionBinding, StringComparison.Ordinal) ||
            !string.Equals(CaptureAuthorizationSessionBinding.Current, snapshot.Approval.SessionBinding, StringComparison.Ordinal) ||
            recurringProof.MaxDuration != snapshot.Specification.Duration ||
            recurringProof.MaxDuration != snapshot.Use.ReservedDuration ||
            recurringProof.MaxDuration != snapshot.Lease.PerRunDuration ||
            recurringProof.MaxDurationMilliseconds != recurringProof.MaxDuration.Ticks / TimeSpan.TicksPerMillisecond ||
            recurringProof.MaxDurationSeconds is not null ||
            recurringProof.MaxFrameCount is not null)
        {
            failureReason = "proof_binding_mismatch";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static bool IsFiniteVersion(long version) => version >= 0 && version != long.MaxValue;

    private static bool IsCurrentSafetyEvidenceStructurallyValid(
        RecurringLeaseExecutionSafetyEvidence safety) =>
        Enum.IsDefined(safety.UnattendedMode) &&
        IsFiniteVersion(safety.Version) &&
        (safety.UnattendedMode == UnattendedModeStatus.Enabled
            ? safety.UnattendedEnabledAtUtc is not null
            : safety.UnattendedMode == UnattendedModeStatus.Disabled && safety.UnattendedEnabledAtUtc is null) &&
        (safety.StopAllApplied
            ? safety.StopAllAppliedAtUtc is not null
            : safety.StopAllAppliedAtUtc is null);
}
