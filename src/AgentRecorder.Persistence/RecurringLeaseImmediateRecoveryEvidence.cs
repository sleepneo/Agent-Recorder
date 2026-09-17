using AgentRecorder.Capture;
using AgentRecorder.Core;

namespace AgentRecorder.Persistence;

/// <summary>
/// The immutable projection of one in-process recovery handoff.  The four
/// source types intentionally expose different amounts of evidence: receipts
/// and post-consume handoffs carry the frozen specification, while a proof
/// carries the digest and the proof-level plan/scope bindings.  No mutable
/// row version is copied here; those are read again immediately before the
/// existing restart-recovery CAS.
/// </summary>
internal sealed class RecurringLeaseImmediateRecoveryEvidence
{
    private RecurringLeaseImmediateRecoveryEvidence(
        string? planId,
        string leaseId,
        string occurrenceIdentity,
        string? occurrenceId,
        string runId,
        string useId,
        string evidenceUserSid,
        string evidenceSessionBinding,
        string? evidenceUserSessionBinding,
        RecurringOccurrenceExecutionSpecification? specification,
        string specificationDigest,
        string? profileDigest,
        string? configurationDigest,
        string? leaseAuthorizationDigest,
        string? localApprovalDigest,
        string? capturePlanDigest,
        string? scopeDigest,
        TimeSpan? maxDuration,
        long? maxDurationMilliseconds)
    {
        PlanId = planId;
        LeaseId = leaseId;
        OccurrenceIdentity = occurrenceIdentity;
        OccurrenceId = occurrenceId;
        RunId = runId;
        UseId = useId;
        EvidenceUserSid = evidenceUserSid;
        EvidenceSessionBinding = evidenceSessionBinding;
        EvidenceUserSessionBinding = evidenceUserSessionBinding;
        Specification = specification;
        SpecificationDigest = specificationDigest;
        ProfileDigest = profileDigest;
        ConfigurationDigest = configurationDigest;
        LeaseAuthorizationDigest = leaseAuthorizationDigest;
        LocalApprovalDigest = localApprovalDigest;
        CapturePlanDigest = capturePlanDigest;
        ScopeDigest = scopeDigest;
        MaxDuration = maxDuration;
        MaxDurationMilliseconds = maxDurationMilliseconds;
    }

    internal string? PlanId { get; }
    internal string LeaseId { get; }
    internal string OccurrenceIdentity { get; }
    internal string? OccurrenceId { get; }
    internal string RunId { get; }
    internal string UseId { get; }
    internal string EvidenceUserSid { get; }
    internal string EvidenceSessionBinding { get; }
    internal string? EvidenceUserSessionBinding { get; }
    internal RecurringOccurrenceExecutionSpecification? Specification { get; }
    internal string SpecificationDigest { get; }
    internal string? ProfileDigest { get; }
    internal string? ConfigurationDigest { get; }
    internal string? LeaseAuthorizationDigest { get; }
    internal string? LocalApprovalDigest { get; }
    internal string? CapturePlanDigest { get; }
    internal string? ScopeDigest { get; }
    internal TimeSpan? MaxDuration { get; }
    internal long? MaxDurationMilliseconds { get; }

    internal static RecurringLeaseImmediateRecoveryEvidence FromReceipt(
        RecurringStartCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var specification = receipt.Specification;
        return new(
            receipt.Plan.Id,
            receipt.Lease.LeaseId,
            specification.OccurrenceIdentity,
            receipt.Occurrence.Id,
            receipt.Run.Id,
            receipt.Use.Id,
            receipt.Approval.CurrentUserSid,
            receipt.Approval.SessionBinding,
            receipt.Approval.CurrentUserSid + "|" + receipt.Approval.SessionBinding,
            specification,
            specification.SpecificationDigest,
            specification.ProfileDigest,
            specification.ConfigurationDigest,
            specification.LeaseAuthorizationDigest,
            specification.LocalApprovalDigest,
            null,
            null,
            null,
            null);
    }

    internal static RecurringLeaseImmediateRecoveryEvidence FromProof(
        RecurringLeaseUseProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return new(
            planId: null,
            proof.LeaseId,
            proof.OccurrenceIdentity,
            occurrenceId: null,
            proof.RunId,
            proof.LeaseUseId,
            proof.CurrentUserSid,
            proof.SessionBinding,
            proof.UserSessionBinding,
            specification: null,
            proof.SpecificationDigest,
            profileDigest: null,
            configurationDigest: null,
            leaseAuthorizationDigest: null,
            localApprovalDigest: null,
            proof.CapturePlanDigest,
            proof.ScopeDigest,
            proof.MaxDuration,
            proof.MaxDurationMilliseconds);
    }

    internal static RecurringLeaseImmediateRecoveryEvidence FromAuthorization(
        RecurringLeaseCaptureAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var specification = authorization.Specification;
        return new(
            authorization.PlanId,
            authorization.LeaseId,
            specification.OccurrenceIdentity,
            authorization.OccurrenceId,
            authorization.RunId,
            authorization.UseId,
            specification.ApprovedCurrentUserSid,
            specification.ApprovedSessionBinding,
            specification.ApprovedCurrentUserSid + "|" + specification.ApprovedSessionBinding,
            specification,
            specification.SpecificationDigest,
            authorization.ProfileDigest,
            specification.ConfigurationDigest,
            authorization.LeaseAuthorizationDigest,
            specification.LocalApprovalDigest,
            null,
            null,
            null,
            null);
    }

    internal static RecurringLeaseImmediateRecoveryEvidence FromTicket(
        RecurringLeaseCaptureExecutionTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var specification = ticket.Specification;
        return new(
            ticket.PlanId,
            ticket.LeaseId,
            ticket.OccurrenceIdentity,
            ticket.OccurrenceId,
            ticket.RunId,
            ticket.UseId,
            specification.ApprovedCurrentUserSid,
            specification.ApprovedSessionBinding,
            specification.ApprovedCurrentUserSid + "|" + specification.ApprovedSessionBinding,
            specification,
            specification.SpecificationDigest,
            ticket.ProfileDigest,
            specification.ConfigurationDigest,
            ticket.LeaseAuthorizationDigest,
            specification.LocalApprovalDigest,
            null,
            null,
            null,
            null);
    }

    internal bool Matches(
        RecurringLeaseExecutionSnapshot snapshot,
        out string failureReason)
    {
        failureReason = "recovery_identity_mismatch";
        if (snapshot is null ||
            (PlanId is not null && !string.Equals(PlanId, snapshot.Plan.Id, StringComparison.Ordinal)) ||
            !string.Equals(LeaseId, snapshot.Lease.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(OccurrenceIdentity, snapshot.Specification.OccurrenceIdentity, StringComparison.Ordinal) ||
            (OccurrenceId is not null && !string.Equals(OccurrenceId, snapshot.Occurrence.Id, StringComparison.Ordinal)) ||
            !string.Equals(RunId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(UseId, snapshot.Use.Id, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Specification.SpecificationDigest, SpecificationDigest, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Specification.ApprovedCurrentUserSid, EvidenceUserSid, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Specification.ApprovedSessionBinding, EvidenceSessionBinding, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Approval.CurrentUserSid, EvidenceUserSid, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Approval.SessionBinding, EvidenceSessionBinding, StringComparison.Ordinal) ||
            EvidenceUserSessionBinding is not null &&
            !string.Equals(
                EvidenceUserSessionBinding,
                EvidenceUserSid + "|" + EvidenceSessionBinding,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (Specification is not null && !AreEquivalentSpecification(Specification, snapshot.Specification))
            return false;

        if (ProfileDigest is not null &&
            !string.Equals(ProfileDigest, snapshot.Profile.ProfileDigest, StringComparison.Ordinal))
        {
            return false;
        }

        if (ConfigurationDigest is not null &&
            !string.Equals(ConfigurationDigest, snapshot.Specification.ConfigurationDigest, StringComparison.Ordinal))
        {
            return false;
        }

        if (LeaseAuthorizationDigest is not null &&
            !string.Equals(LeaseAuthorizationDigest, snapshot.Lease.AuthorizationDigest, StringComparison.Ordinal))
        {
            return false;
        }

        if (LocalApprovalDigest is not null &&
            !string.Equals(LocalApprovalDigest, snapshot.Specification.LocalApprovalDigest, StringComparison.Ordinal))
        {
            return false;
        }

        if (CapturePlanDigest is not null &&
            !string.Equals(
                CapturePlanDigest,
                CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUsePlanDigest(
                    snapshot.Plan,
                    snapshot.Occurrence,
                    snapshot.Specification),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (ScopeDigest is not null &&
            !string.Equals(
                ScopeDigest,
                CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUseScopeDigest(
                    snapshot.Specification,
                    snapshot.Lease,
                    snapshot.Approval),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (MaxDuration is not null &&
            (MaxDuration != snapshot.Specification.Duration ||
             MaxDuration != snapshot.Use.ReservedDuration ||
             MaxDuration != snapshot.Lease.PerRunDuration ||
             MaxDurationMilliseconds != MaxDuration.Value.Ticks / TimeSpan.TicksPerMillisecond))
        {
            return false;
        }

        failureReason = string.Empty;
        return true;
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
}
