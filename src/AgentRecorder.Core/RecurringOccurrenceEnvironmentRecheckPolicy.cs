using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Stable reason codes for the pure recurring occurrence environment
/// recheck. The order of checks in <see cref="RecurringOccurrenceEnvironmentRecheckPolicy"/>
/// is the reason precedence contract.
/// </summary>
internal static class RecurringOccurrenceEnvironmentRecheckReasonCodes
{
    internal const string InputMissing = "recurring_recheck_input_missing";
    internal const string NonUtcTime = "recurring_recheck_non_utc_time";
    internal const string PlanNotPeriodic = "recurring_recheck_plan_not_periodic";
    internal const string ParentMismatch = "recurring_recheck_parent_mismatch";
    internal const string CandidateInvalid = "recurring_recheck_candidate_invalid";
    internal const string OccurrenceRelationInvalid = "recurring_recheck_occurrence_relation_invalid";
    internal const string ApprovalInvalid = "recurring_recheck_approval_invalid";
    internal const string ProfilePolicyNotSupported = "recurring_recheck_profile_policy_not_supported";
    internal const string TimeRelationInvalid = "recurring_recheck_time_relation_invalid";
    internal const string TimeOverflow = "recurring_recheck_time_overflow";
    internal const string PlanNotEnabled = "recurring_recheck_plan_not_enabled";
    internal const string OccurrenceNotRechecking = "recurring_recheck_occurrence_not_rechecking";
    internal const string LeaseNotActive = "recurring_recheck_lease_not_active";
    internal const string LeaseOutsideValidity = "recurring_recheck_lease_outside_validity";
    internal const string BeforeWindow = "recurring_recheck_before_window";
    internal const string MissedLatestStart = "recurring_recheck_missed_latest_start";
    internal const string MissedPlannedEnd = "recurring_recheck_missed_planned_end";
    internal const string MissedLeaseValidity = "recurring_recheck_missed_lease_validity";
    internal const string AlreadyTerminal = "recurring_recheck_already_terminal";
}

internal enum RecurringOccurrenceEnvironmentRecheckStatus
{
    BeforeWindow,
    Eligible,
    Missed,
    Blocked,
    Rejected,
    AlreadyTerminal,
}

internal sealed record RecurringOccurrenceEnvironmentRecheckDecision
{
    private RecurringOccurrenceEnvironmentRecheckDecision(
        RecurringOccurrenceEnvironmentRecheckStatus status,
        string reasonCode,
        RecurringOccurrenceExecutionSpecification? executionSpecification)
    {
        Status = status;
        ReasonCode = reasonCode;
        ExecutionSpecification = executionSpecification;
    }

    internal RecurringOccurrenceEnvironmentRecheckStatus Status { get; }

    internal string ReasonCode { get; }

    internal RecurringOccurrenceExecutionSpecification? ExecutionSpecification { get; }

    internal static RecurringOccurrenceEnvironmentRecheckDecision BeforeWindow() =>
        new(
            RecurringOccurrenceEnvironmentRecheckStatus.BeforeWindow,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.BeforeWindow,
            null);

    internal static RecurringOccurrenceEnvironmentRecheckDecision Eligible(
        RecurringOccurrenceExecutionSpecification specification) =>
        new(RecurringOccurrenceEnvironmentRecheckStatus.Eligible, "", specification);

    internal static RecurringOccurrenceEnvironmentRecheckDecision Missed(string reasonCode) =>
        new(RecurringOccurrenceEnvironmentRecheckStatus.Missed, reasonCode, null);

    internal static RecurringOccurrenceEnvironmentRecheckDecision Blocked(string reasonCode) =>
        new(RecurringOccurrenceEnvironmentRecheckStatus.Blocked, reasonCode, null);

    internal static RecurringOccurrenceEnvironmentRecheckDecision Rejected(string reasonCode) =>
        new(RecurringOccurrenceEnvironmentRecheckStatus.Rejected, reasonCode, null);

    internal static RecurringOccurrenceEnvironmentRecheckDecision AlreadyTerminal() =>
        new(
            RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal,
            null);
}

/// <summary>
/// Internal immutable input bundle for the environment recheck. Every member
/// is a previously loaded domain snapshot; the policy does not rehydrate or
/// query any external source.
/// </summary>
internal sealed record RecurringOccurrenceEnvironmentRecheckRequest(
    PlanDefinition Plan,
    RecurringPlanSchedule Schedule,
    PlanOccurrence Occurrence,
    RecurringConsentLease Lease,
    RecurringOccurrenceCandidate Candidate,
    RecurringPlanProfileBinding ProfileBinding,
    RecurringFixedRegionProfileVersion Profile,
    RecurringLeaseLocalApprovalEvidence ApprovalEvidence,
    StandingLeaseExecutionEnvironment Environment);

/// <summary>
/// Pure, deterministic policy for the first recurring fixed-region execution
/// recheck. It only validates immutable snapshots and constructs an
/// execution specification; it does not write state, create identities, issue
/// proof, inspect the machine, or start a capture backend.
/// </summary>
internal static class RecurringOccurrenceEnvironmentRecheckPolicy
{
    /// <summary>
    /// Runs the complete snapshot, lifecycle, and time-window portion of the
    /// policy without touching the live environment. A null result means the
    /// caller may now invoke the trusted environment provider. This staged
    /// boundary is what keeps malformed, missed, and blocked occurrences from
    /// probing the desktop or output filesystem.
    /// </summary>
    internal static RecurringOccurrenceEnvironmentRecheckDecision? EvaluateBeforeEnvironment(
        RecurringOccurrenceEnvironmentRecheckRequest? request)
    {
        if (request is null ||
            request.Plan is null ||
            request.Schedule is null ||
            request.Occurrence is null ||
            request.Lease is null ||
            request.Candidate is null ||
            request.ProfileBinding is null ||
            request.Profile is null ||
            request.ApprovalEvidence is null ||
            request.Environment is null)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.InputMissing);
        }

        if (!AllTimesAreUtc(request))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.NonUtcTime);
        if (!request.Plan.IsPeriodic)
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotPeriodic);
        if (!HasExactParentChain(request))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);
        if (!HasValidCandidateAndOccurrence(request))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid);
        if (!HasValidOccurrenceSnapshotShape(request.Occurrence))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceRelationInvalid);
        if (!HasValidApprovalEvidence(request))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.ApprovalInvalid);
        if (!HasSupportedProfilePolicy(request.Profile))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.ProfilePolicyNotSupported);
        if (!HasValidTimeRelations(request))
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
        if (Phase3TransitionGuards.IsTerminal(request.Occurrence.Status))
            return RecurringOccurrenceEnvironmentRecheckDecision.AlreadyTerminal();
        if (request.Plan.Status != PlanDefinitionStatus.Enabled)
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotEnabled);
        if (request.Occurrence.Status != PlanOccurrenceStatus.Rechecking ||
            request.Occurrence.RunId is not null ||
            request.Occurrence.TerminalReasonCode is not null)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceNotRechecking);
        }
        if (request.Lease.Status != ConsentLeaseStatus.Active)
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseNotActive);

        var nowUtc = request.Environment.NowUtc;
        if (nowUtc < request.Lease.ValidFromUtc || nowUtc >= request.Lease.ValidUntilUtc)
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseOutsideValidity);

        var scheduledStartUtc = request.Candidate.ScheduledStartUtc!.Value;
        var latestStartUtc = request.Candidate.LatestStartUtc!.Value;
        var plannedEndUtc = request.Candidate.PlannedEndUtc!.Value;
        if (nowUtc < scheduledStartUtc)
            return RecurringOccurrenceEnvironmentRecheckDecision.BeforeWindow();
        if (nowUtc > latestStartUtc)
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLatestStart);

        DateTimeOffset captureEndUtc;
        try
        {
            captureEndUtc = nowUtc.Add(request.Profile.Duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeOverflow);
        }

        if (captureEndUtc > plannedEndUtc)
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedPlannedEnd);
        if (captureEndUtc > request.Lease.ValidUntilUtc)
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLeaseValidity);

        return null;
    }

    internal static RecurringOccurrenceEnvironmentRecheckDecision Evaluate(
        RecurringOccurrenceEnvironmentRecheckRequest? request)
    {
        if (request is null ||
            request.Plan is null ||
            request.Schedule is null ||
            request.Occurrence is null ||
            request.Lease is null ||
            request.Candidate is null ||
            request.ProfileBinding is null ||
            request.Profile is null ||
            request.ApprovalEvidence is null ||
            request.Environment is null)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.InputMissing);
        }

        if (!AllTimesAreUtc(request))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.NonUtcTime);
        }

        if (!request.Plan.IsPeriodic)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotPeriodic);
        }

        if (!HasExactParentChain(request))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);
        }

        if (!HasValidCandidateAndOccurrence(request))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid);
        }

        if (!HasValidOccurrenceSnapshotShape(request.Occurrence))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceRelationInvalid);
        }

        if (!HasValidApprovalEvidence(request))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.ApprovalInvalid);
        }

        if (!HasSupportedProfilePolicy(request.Profile))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.ProfilePolicyNotSupported);
        }

        if (!HasValidTimeRelations(request))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
        }

        if (Phase3TransitionGuards.IsTerminal(request.Occurrence.Status))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.AlreadyTerminal();
        }

        if (request.Plan.Status != PlanDefinitionStatus.Enabled)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotEnabled);
        }

        if (request.Occurrence.Status != PlanOccurrenceStatus.Rechecking ||
            request.Occurrence.RunId is not null ||
            request.Occurrence.TerminalReasonCode is not null)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceNotRechecking);
        }

        if (request.Lease.Status != ConsentLeaseStatus.Active)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseNotActive);
        }

        var nowUtc = request.Environment.NowUtc;
        if (nowUtc < request.Lease.ValidFromUtc || nowUtc >= request.Lease.ValidUntilUtc)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseOutsideValidity);
        }

        var scheduledStartUtc = request.Candidate.ScheduledStartUtc!.Value;
        var latestStartUtc = request.Candidate.LatestStartUtc!.Value;
        var plannedEndUtc = request.Candidate.PlannedEndUtc!.Value;

        // This check intentionally precedes all display and output validation.
        if (nowUtc < scheduledStartUtc)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.BeforeWindow();
        }

        if (nowUtc > latestStartUtc)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLatestStart);
        }

        DateTimeOffset captureEndUtc;
        try
        {
            captureEndUtc = nowUtc.Add(request.Profile.Duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeOverflow);
        }

        if (captureEndUtc > plannedEndUtc)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedPlannedEnd);
        }

        if (captureEndUtc > request.Lease.ValidUntilUtc)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Missed(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLeaseValidity);
        }

        if (!TryCreateEnvironmentRequirements(request, out var requirements))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);
        }

        if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
                request.Environment,
                requirements,
                out var environmentFailure))
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Blocked(environmentFailure);
        }

        try
        {
            var specification = RecurringOccurrenceExecutionSpecification.Create(
                request,
                requirements,
                scheduledStartUtc,
                latestStartUtc,
                plannedEndUtc,
                nowUtc);
            return RecurringOccurrenceEnvironmentRecheckDecision.Eligible(specification);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeOverflow);
        }
        catch (Phase3DomainException)
        {
            return RecurringOccurrenceEnvironmentRecheckDecision.Rejected(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid);
        }
    }

    private static bool AllTimesAreUtc(RecurringOccurrenceEnvironmentRecheckRequest request)
    {
        var times = new DateTimeOffset?[]
        {
            request.Environment.NowUtc,
            request.Plan.CreatedAtUtc,
            request.Plan.UpdatedAtUtc,
            request.Occurrence.WindowStartUtc,
            request.Occurrence.WindowEndUtc,
            request.Occurrence.CreatedAtUtc,
            request.Occurrence.UpdatedAtUtc,
            request.Lease.ValidFromUtc,
            request.Lease.ValidUntilUtc,
            request.Lease.AuthorizedPlanLatestEndUtc,
            request.Lease.CreatedAtUtc,
            request.Lease.UpdatedAtUtc,
            request.Candidate.ScheduledStartUtc,
            request.Candidate.LatestStartUtc,
            request.Candidate.PlannedEndUtc,
            request.ProfileBinding.BoundAtUtc,
            request.Profile.CreatedAtUtc,
            request.ApprovalEvidence.ApprovedAtUtc,
        };

        return times.All(value => value is null || value.Value.Offset == TimeSpan.Zero);
    }

    private static bool HasExactParentChain(RecurringOccurrenceEnvironmentRecheckRequest request)
    {
        try
        {
            var configuration = request.Lease.ConfigurationRef;
            var expectedTimeZoneRulesDigest = RecurringTimeZoneRulesDigest.Compute(request.Schedule.TimeZoneInfo);
            var expectedConfigurationDigest = RecurringPlanConfigurationDigest.Compute(
                request.Plan.Id,
                configuration.ScheduleRevision,
                request.Schedule.CanonicalDigest,
                expectedTimeZoneRulesDigest,
                request.Profile.Reference);
            var expectedAuthorizationDigest = RecurringConsentLeaseAuthorizationDigest.Compute(request.Lease.Authorization);
            var expectedProfileDigest = RecurringFixedRegionProfileDigest.Compute(request.Profile);

            return string.Equals(request.Occurrence.PlanId, request.Plan.Id, StringComparison.Ordinal) &&
                string.Equals(configuration.PlanId, request.Plan.Id, StringComparison.Ordinal) &&
                request.ProfileBinding.PlanId == request.Plan.Id &&
                request.ProfileBinding.ProfileRef.Matches(request.Profile) &&
                configuration.ScheduleRevision == request.Candidate.ScheduleRevision &&
                configuration.ScheduleRevision > 0 &&
                string.Equals(configuration.ScheduleDigest, request.Schedule.CanonicalDigest, StringComparison.Ordinal) &&
                string.Equals(configuration.ScheduleDigest, request.Candidate.ScheduleDigest, StringComparison.Ordinal) &&
                string.Equals(configuration.TimeZoneRulesDigest, expectedTimeZoneRulesDigest, StringComparison.Ordinal) &&
                configuration.ProfileRef.Matches(request.Profile) &&
                string.Equals(configuration.ConfigurationDigest, expectedConfigurationDigest, StringComparison.Ordinal) &&
                string.Equals(request.Lease.AuthorizationDigest, expectedAuthorizationDigest, StringComparison.Ordinal) &&
                string.Equals(request.Profile.ProfileDigest, expectedProfileDigest, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidCandidateAndOccurrence(RecurringOccurrenceEnvironmentRecheckRequest request)
    {
        var candidate = request.Candidate;
        if (!candidate.IsValid ||
            candidate.ScheduledStartUtc is null ||
            candidate.LatestStartUtc is null ||
            candidate.PlannedEndUtc is null ||
            string.IsNullOrWhiteSpace(candidate.ResolutionCode) ||
            !string.IsNullOrEmpty(candidate.ReasonCode) ||
            candidate.TimeZoneId != request.Schedule.TimeZoneId ||
            candidate.LocalWallClockTime != request.Schedule.LocalWallClockTime ||
            candidate.PlanId != request.Plan.Id ||
            candidate.ScheduleRevision != request.Lease.ConfigurationRef.ScheduleRevision ||
            candidate.Identity is null)
        {
            return false;
        }

        RecurringOccurrenceIdentity expectedIdentity;
        RecurringOccurrenceCandidate expectedCandidate;
        try
        {
            expectedIdentity = RecurringOccurrenceIdentity.Create(
                request.Plan.Id,
                candidate.ScheduleRevision,
                request.Schedule,
                candidate.LocalDate,
                request.Schedule.LocalWallClockTime);
            expectedCandidate = new RecurringOccurrenceCalculator()
                .CalculateCandidateForAuthorization(
                    request.Plan.Id,
                    candidate.ScheduleRevision,
                    request.Schedule,
                    candidate.LocalDate);
        }
        catch (Phase3DomainException)
        {
            return false;
        }

        if (!string.Equals(candidate.Identity.Value, expectedIdentity.Value, StringComparison.Ordinal) ||
            !string.Equals(candidate.Identity.Digest, expectedIdentity.Digest, StringComparison.Ordinal) ||
            candidate.ScheduledStartUtc != expectedCandidate.ScheduledStartUtc ||
            candidate.LatestStartUtc != expectedCandidate.LatestStartUtc ||
            candidate.PlannedEndUtc != expectedCandidate.PlannedEndUtc ||
            candidate.ResolutionCode != expectedCandidate.ResolutionCode)
        {
            return false;
        }

        DateTimeOffset expectedLatest;
        DateTimeOffset expectedPlannedEnd;
        try
        {
            expectedLatest = candidate.ScheduledStartUtc.Value.Add(request.Schedule.LatestStartGrace);
            expectedPlannedEnd = expectedLatest.Add(request.Schedule.RecordingDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return candidate.ScheduledStartUtc.Value <= candidate.LatestStartUtc.Value &&
            candidate.LatestStartUtc.Value <= candidate.PlannedEndUtc.Value &&
            candidate.LatestStartUtc.Value == expectedLatest &&
            candidate.PlannedEndUtc.Value == expectedPlannedEnd &&
            request.Occurrence.Id == candidate.Identity.Value &&
            request.Occurrence.WindowStartUtc == candidate.ScheduledStartUtc.Value &&
            request.Occurrence.WindowEndUtc == candidate.PlannedEndUtc.Value;
    }

    private static bool HasValidOccurrenceSnapshotShape(PlanOccurrence occurrence)
    {
        try
        {
            if (!Enum.IsDefined(occurrence.Status))
                return false;

            return occurrence.Status switch
            {
                PlanOccurrenceStatus.Scheduled or
                PlanOccurrenceStatus.Due or
                PlanOccurrenceStatus.Rechecking or
                PlanOccurrenceStatus.PendingLeaseApproval or
                PlanOccurrenceStatus.Authorized or
                PlanOccurrenceStatus.PendingConfirmation =>
                    occurrence.RunId is null && occurrence.TerminalReasonCode is null,
                PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed =>
                    occurrence.RunId is not null && occurrence.TerminalReasonCode is null,
                PlanOccurrenceStatus.Missed =>
                    occurrence.RunId is null && !string.IsNullOrWhiteSpace(occurrence.TerminalReasonCode),
                PlanOccurrenceStatus.Blocked or
                PlanOccurrenceStatus.Cancelled or
                PlanOccurrenceStatus.Expired =>
                    !string.IsNullOrWhiteSpace(occurrence.TerminalReasonCode),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidApprovalEvidence(RecurringOccurrenceEnvironmentRecheckRequest request)
    {
        try
        {
            var evidence = request.ApprovalEvidence;
            var expectedDigest = RecurringLeaseLocalApprovalDigest.Compute(
                evidence.ApprovalId,
                evidence.LeaseId,
                evidence.PlanId,
                evidence.ConfigurationDigest,
                evidence.AuthorizationDigest,
                evidence.CurrentUserSid,
                evidence.SessionBinding,
                evidence.ApprovedAtUtc,
                evidence.ApprovalKind,
                evidence.ApprovalVersion);

            return !string.IsNullOrWhiteSpace(evidence.ApprovalId) &&
                evidence.LeaseId == request.Lease.LeaseId &&
                evidence.PlanId == request.Plan.Id &&
                evidence.ConfigurationDigest == request.Lease.ConfigurationRef.ConfigurationDigest &&
                evidence.AuthorizationDigest == request.Lease.AuthorizationDigest &&
                evidence.ApprovalKind == RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind &&
                evidence.ApprovalVersion == RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion &&
                evidence.ApprovedAtUtc.Offset == TimeSpan.Zero &&
                string.Equals(evidence.ApprovalDigest, expectedDigest, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSupportedProfilePolicy(RecurringFixedRegionProfileVersion profile) =>
        Enum.IsDefined(profile.TargetType) &&
        Enum.IsDefined(profile.RebindPolicy) &&
        Enum.IsDefined(profile.CaptureSemantics) &&
        Enum.IsDefined(profile.CoordinateSpace) &&
        Enum.IsDefined(profile.DisplayIdentityStatus) &&
        Enum.IsDefined(profile.Backend) &&
        Enum.IsDefined(profile.AudioMode) &&
        Enum.IsDefined(profile.OutputConflictPolicy) &&
        Enum.IsDefined(profile.WakePolicy) &&
        Enum.IsDefined(profile.DesktopRequirement) &&
        profile.TargetType == AuthorizedScopeTargetType.FixedRegion &&
        profile.RebindPolicy == RecurringFixedRegionRebindPolicy.ExactMatchOnly &&
        profile.CaptureSemantics == AuthorizedCaptureSemantics.DesktopRegion &&
        profile.CoordinateSpace == AuthorizedCoordinateSpace.PhysicalVirtualScreen &&
        profile.DisplayIdentityStatus == AuthorizedDisplayIdentityStatus.Resolved &&
        profile.Backend == AuthorizedCaptureBackend.FfmpegRegion &&
        profile.AudioMode == AuthorizedAudioMode.None &&
        profile.OutputConflictPolicy == AuthorizedOutputConflictPolicy.FailIfExists &&
        profile.WakePolicy == AuthorizedWakePolicy.NaturalWakeOnly &&
        profile.DesktopRequirement == AuthorizedDesktopRequirement.InteractiveDesktopRequired &&
        profile.Duration > TimeSpan.Zero &&
        profile.Duration <= RecurringFixedRegionProfileVersion.MaximumDuration;

    private static bool HasValidTimeRelations(RecurringOccurrenceEnvironmentRecheckRequest request)
    {
        var now = request.Environment.NowUtc;
        var candidate = request.Candidate;
        var scheduled = candidate.ScheduledStartUtc!.Value;
        var latest = candidate.LatestStartUtc!.Value;
        var plannedEnd = candidate.PlannedEndUtc!.Value;
        if (now < request.Plan.CreatedAtUtc ||
            now < request.Plan.UpdatedAtUtc ||
            now < request.Occurrence.CreatedAtUtc ||
            now < request.Occurrence.UpdatedAtUtc ||
            now < request.Lease.CreatedAtUtc ||
            now < request.Lease.UpdatedAtUtc ||
            now < request.Profile.CreatedAtUtc ||
            now < request.ProfileBinding.BoundAtUtc ||
            now < request.ApprovalEvidence.ApprovedAtUtc ||
            request.ApprovalEvidence.ApprovedAtUtc < request.Plan.CreatedAtUtc ||
            request.ApprovalEvidence.ApprovedAtUtc < request.Lease.CreatedAtUtc ||
            request.ApprovalEvidence.ApprovedAtUtc < request.Profile.CreatedAtUtc ||
            request.ApprovalEvidence.ApprovedAtUtc < request.ProfileBinding.BoundAtUtc ||
            request.ApprovalEvidence.ApprovedAtUtc >= request.Lease.ValidUntilUtc ||
            request.Lease.AuthorizedPlanLatestEndUtc < plannedEnd ||
            request.Lease.AuthorizedPlanLatestEndUtc <= request.Lease.ValidFromUtc ||
            scheduled > latest ||
            latest > plannedEnd)
        {
            return false;
        }

        try
        {
            return latest == scheduled.Add(request.Schedule.LatestStartGrace) &&
                plannedEnd == latest.Add(request.Profile.Duration) &&
                request.Profile.Duration == request.Schedule.RecordingDuration &&
                request.Profile.Duration == request.Lease.PerRunDuration;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryCreateEnvironmentRequirements(
        RecurringOccurrenceEnvironmentRecheckRequest request,
        out FixedRegionExecutionEnvironmentRequirements requirements)
    {
        requirements = null!;
        try
        {
            var scheduled = request.Candidate.ScheduledStartUtc!.Value;
            var frozenFilePath = request.Profile.ResolveOutputPath(
                request.Candidate.Identity.Value,
                scheduled);
            requirements = new FixedRegionExecutionEnvironmentRequirements(
                request.ApprovalEvidence.CurrentUserSid,
                request.ApprovalEvidence.SessionBinding,
                request.Profile.StableDisplayFingerprint,
                request.Profile.DisplayBounds,
                request.Profile.RegionWithinDisplay,
                request.Profile.VirtualScreenRegion,
                request.Profile.DpiX,
                request.Profile.DpiY,
                request.Profile.PhysicalWidth,
                request.Profile.PhysicalHeight,
                request.Profile.Orientation,
                request.Profile.TopologyDigest,
                StandingLeaseOutputPath.NormalizeDirectory(request.Profile.OutputDirectory),
                frozenFilePath,
                request.Profile.OutputConflictPolicy,
                request.Profile.Duration);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Immutable, post-recheck execution data. It is deliberately not a proof,
/// has no nonce or consumption operation, and cannot start a capture backend.
/// </summary>
internal sealed class RecurringOccurrenceExecutionSpecification
{
    private RecurringOccurrenceExecutionSpecification(
        string planId,
        string occurrenceId,
        string occurrenceIdentity,
        string leaseId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        string profileId,
        long profileVersion,
        string profileDigest,
        string configurationDigest,
        string leaseAuthorizationDigest,
        string localApprovalId,
        string localApprovalDigest,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        DateTimeOffset evaluatedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        AuthorizedPhysicalRectangle virtualScreenRegion,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan duration,
        int countdownSeconds,
        string normalizedOutputDirectory,
        string frozenOutputFileName,
        string frozenOutputFilePath,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        string approvedCurrentUserSid,
        string approvedSessionBinding)
    {
        PlanId = planId;
        OccurrenceId = occurrenceId;
        OccurrenceIdentity = occurrenceIdentity;
        LeaseId = leaseId;
        ScheduleRevision = scheduleRevision;
        ScheduleDigest = scheduleDigest;
        TimeZoneRulesDigest = timeZoneRulesDigest;
        ProfileId = profileId;
        ProfileVersion = profileVersion;
        ProfileDigest = profileDigest;
        ConfigurationDigest = configurationDigest;
        LeaseAuthorizationDigest = leaseAuthorizationDigest;
        LocalApprovalId = localApprovalId;
        LocalApprovalDigest = localApprovalDigest;
        ScheduledStartUtc = scheduledStartUtc;
        LatestStartUtc = latestStartUtc;
        PlannedEndUtc = plannedEndUtc;
        EvaluatedAtUtc = evaluatedAtUtc;
        StableDisplayFingerprint = stableDisplayFingerprint;
        DisplayBounds = displayBounds;
        RegionWithinDisplay = regionWithinDisplay;
        VirtualScreenRegion = virtualScreenRegion;
        DpiX = dpiX;
        DpiY = dpiY;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        Orientation = orientation;
        TopologyDigest = topologyDigest;
        Backend = backend;
        AudioMode = audioMode;
        Duration = duration;
        CountdownSeconds = countdownSeconds;
        NormalizedOutputDirectory = normalizedOutputDirectory;
        FrozenOutputFileName = frozenOutputFileName;
        FrozenOutputFilePath = frozenOutputFilePath;
        OutputConflictPolicy = outputConflictPolicy;
        ApprovedCurrentUserSid = approvedCurrentUserSid;
        ApprovedSessionBinding = approvedSessionBinding;
        SpecificationDigest = RecurringOccurrenceExecutionSpecificationDigest.Compute(this);
    }

    internal const int CanonicalVersion = 1;

    internal const string DigestPrefix = "recurring-occurrence-execution-spec/v1:";

    internal string PlanId { get; }
    internal string OccurrenceId { get; }
    internal string OccurrenceIdentity { get; }
    internal string LeaseId { get; }
    internal long ScheduleRevision { get; }
    internal string ScheduleDigest { get; }
    internal string TimeZoneRulesDigest { get; }
    internal string ProfileId { get; }
    internal long ProfileVersion { get; }
    internal string ProfileDigest { get; }
    internal string ConfigurationDigest { get; }
    internal string LeaseAuthorizationDigest { get; }
    internal string LocalApprovalId { get; }
    internal string LocalApprovalDigest { get; }
    internal DateTimeOffset ScheduledStartUtc { get; }
    internal DateTimeOffset LatestStartUtc { get; }
    internal DateTimeOffset PlannedEndUtc { get; }
    internal DateTimeOffset EvaluatedAtUtc { get; }
    internal string StableDisplayFingerprint { get; }
    internal AuthorizedPhysicalRectangle DisplayBounds { get; }
    internal AuthorizedPhysicalRectangle RegionWithinDisplay { get; }
    internal AuthorizedPhysicalRectangle VirtualScreenRegion { get; }
    internal int DpiX { get; }
    internal int DpiY { get; }
    internal int PhysicalWidth { get; }
    internal int PhysicalHeight { get; }
    internal AuthorizedDisplayOrientation Orientation { get; }
    internal string TopologyDigest { get; }
    internal AuthorizedCaptureBackend Backend { get; }
    internal AuthorizedAudioMode AudioMode { get; }
    internal TimeSpan Duration { get; }
    internal int CountdownSeconds { get; }
    internal string NormalizedOutputDirectory { get; }
    internal string FrozenOutputFileName { get; }
    internal string FrozenOutputFilePath { get; }
    internal AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }
    internal string ApprovedCurrentUserSid { get; }
    internal string ApprovedSessionBinding { get; }
    internal string SpecificationDigest { get; }

    internal static RecurringOccurrenceExecutionSpecification Create(
        RecurringOccurrenceEnvironmentRecheckRequest request,
        FixedRegionExecutionEnvironmentRequirements requirements,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        DateTimeOffset evaluatedAtUtc)
    {
        var fileName = request.Profile.RenderOutputFileName(
            request.Candidate.Identity.Value,
            scheduledStartUtc);
        return new(
            request.Plan.Id,
            request.Occurrence.Id,
            request.Candidate.Identity.Value,
            request.Lease.LeaseId,
            request.Candidate.ScheduleRevision,
            request.Candidate.ScheduleDigest,
            request.Lease.ConfigurationRef.TimeZoneRulesDigest,
            request.Profile.ProfileId,
            request.Profile.ProfileVersion,
            request.Profile.ProfileDigest,
            request.Lease.ConfigurationRef.ConfigurationDigest,
            request.Lease.AuthorizationDigest,
            request.ApprovalEvidence.ApprovalId,
            request.ApprovalEvidence.ApprovalDigest,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            evaluatedAtUtc,
            request.Profile.StableDisplayFingerprint,
            requirements.DisplayBounds,
            requirements.RegionWithinDisplay,
            requirements.VirtualScreenRegion,
            requirements.DpiX,
            requirements.DpiY,
            requirements.PhysicalWidth,
            requirements.PhysicalHeight,
            requirements.Orientation,
            requirements.TopologyDigest,
            request.Profile.Backend,
            request.Profile.AudioMode,
            request.Profile.Duration,
            request.Profile.CountdownSeconds,
            requirements.NormalizedOutputDirectory,
            fileName,
            requirements.FrozenOutputFilePath,
            request.Profile.OutputConflictPolicy,
            request.ApprovalEvidence.CurrentUserSid,
            request.ApprovalEvidence.SessionBinding);
    }

    /// <summary>
    /// Rehydrates one immutable persisted specification only after every
    /// scalar and canonical-code invariant has been checked.  The supplied
    /// digest is compared to a freshly recomputed digest before the object is
    /// returned, so a corrupt row never produces a usable partial object.
    /// </summary>
    internal static RecurringOccurrenceExecutionSpecification Rehydrate(
        string planId,
        string occurrenceId,
        string occurrenceIdentity,
        string leaseId,
        long scheduleRevision,
        string scheduleDigest,
        string timeZoneRulesDigest,
        string profileId,
        long profileVersion,
        string profileDigest,
        string configurationDigest,
        string leaseAuthorizationDigest,
        string localApprovalId,
        string localApprovalDigest,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        DateTimeOffset evaluatedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        AuthorizedPhysicalRectangle virtualScreenRegion,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan duration,
        int countdownSeconds,
        string normalizedOutputDirectory,
        string frozenOutputFileName,
        string frozenOutputFilePath,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        string approvedCurrentUserSid,
        string approvedSessionBinding,
        int specificationVersion,
        string specificationDigest)
    {
        ValidateRehydratedText(planId, nameof(planId));
        ValidateRehydratedText(occurrenceId, nameof(occurrenceId));
        ValidateRehydratedText(occurrenceIdentity, nameof(occurrenceIdentity));
        ValidateRehydratedText(leaseId, nameof(leaseId));
        ValidateRehydratedText(profileId, nameof(profileId));
        ValidateRehydratedText(stableDisplayFingerprint, nameof(stableDisplayFingerprint));
        ValidateRehydratedText(normalizedOutputDirectory, nameof(normalizedOutputDirectory));
        ValidateRehydratedText(frozenOutputFileName, nameof(frozenOutputFileName));
        ValidateRehydratedText(frozenOutputFilePath, nameof(frozenOutputFilePath));
        ValidateRehydratedText(approvedCurrentUserSid, nameof(approvedCurrentUserSid));
        ValidateRehydratedText(approvedSessionBinding, nameof(approvedSessionBinding));
        ValidateRehydratedDigest(scheduleDigest, "recurring-schedule/v1:", nameof(scheduleDigest));
        ValidateRehydratedDigest(timeZoneRulesDigest, "recurring-timezone-rules/v1:", nameof(timeZoneRulesDigest));
        ValidateRehydratedDigest(profileDigest, "recurring-fixed-region-profile/v1:", nameof(profileDigest));
        ValidateRehydratedDigest(configurationDigest, "recurring-plan-configuration/v1:", nameof(configurationDigest));
        ValidateRehydratedDigest(leaseAuthorizationDigest, "recurring-consent-lease-authorization/v1:", nameof(leaseAuthorizationDigest));
        ValidateRehydratedText(localApprovalId, nameof(localApprovalId));
        ValidateRehydratedDigest(localApprovalDigest, "recurring-lease-local-approval/v1:", nameof(localApprovalDigest));
        ValidateRehydratedDigest(specificationDigest, DigestPrefix, nameof(specificationDigest));

        if (specificationVersion != CanonicalVersion || scheduleRevision <= 0 || profileVersion <= 0 ||
            !AreUtc(scheduledStartUtc, latestStartUtc, plannedEndUtc, evaluatedAtUtc) ||
            scheduledStartUtc.UtcDateTime.Ticks < 0 ||
            latestStartUtc < scheduledStartUtc ||
            plannedEndUtc <= latestStartUtc ||
            evaluatedAtUtc < scheduledStartUtc ||
            dpiX <= 0 || dpiY <= 0 || physicalWidth <= 0 || physicalHeight <= 0 ||
            duration <= TimeSpan.Zero || duration > RecurringFixedRegionProfileVersion.MaximumDuration ||
            countdownSeconds is < 0 or > 10 ||
            !Enum.IsDefined(orientation) ||
            backend != AuthorizedCaptureBackend.FfmpegRegion ||
            audioMode != AuthorizedAudioMode.None ||
            outputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            !string.Equals(frozenOutputFilePath, normalizedOutputDirectory + frozenOutputFileName, StringComparison.Ordinal) ||
            frozenOutputFileName.Contains('/') || frozenOutputFileName.Contains('\\') ||
            regionWithinDisplay.X < 0 || regionWithinDisplay.Y < 0 ||
            checked(regionWithinDisplay.X + regionWithinDisplay.Width) > displayBounds.Width ||
            checked(regionWithinDisplay.Y + regionWithinDisplay.Height) > displayBounds.Height ||
            virtualScreenRegion != new AuthorizedPhysicalRectangle(
                checked(displayBounds.X + regionWithinDisplay.X),
                checked(displayBounds.Y + regionWithinDisplay.Y),
                regionWithinDisplay.Width,
                regionWithinDisplay.Height))
        {
            throw new Phase3DomainException(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid,
                "The persisted recurring execution specification is outside its canonical domain.");
        }

        var specification = new RecurringOccurrenceExecutionSpecification(
            planId,
            occurrenceId,
            occurrenceIdentity,
            leaseId,
            scheduleRevision,
            scheduleDigest,
            timeZoneRulesDigest,
            profileId,
            profileVersion,
            profileDigest,
            configurationDigest,
            leaseAuthorizationDigest,
            localApprovalId,
            localApprovalDigest,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            evaluatedAtUtc,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            virtualScreenRegion,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            topologyDigest,
            backend,
            audioMode,
            duration,
            countdownSeconds,
            normalizedOutputDirectory,
            frozenOutputFileName,
            frozenOutputFilePath,
            outputConflictPolicy,
            approvedCurrentUserSid,
            approvedSessionBinding);

        if (!RecurringFixedRegionProfileValidation.FixedTimeEquals(
                specification.SpecificationDigest,
                specificationDigest))
        {
            throw new Phase3DomainException(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid,
                "The persisted recurring execution specification digest does not match its contents.");
        }

        return specification;
    }

    private static bool AreUtc(params DateTimeOffset[] values) => values.All(value => value.Offset == TimeSpan.Zero);

    private static void ValidateRehydratedText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new Phase3DomainException(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid,
                $"The persisted specification field {fieldName} is not canonical.");
        }
    }

    private static void ValidateRehydratedDigest(string value, string prefix, string fieldName)
    {
        ValidateRehydratedText(value, fieldName);
        if (value.Length != prefix.Length + 64 ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value[(prefix.Length)..].Any(character =>
                !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
        {
            throw new Phase3DomainException(
                RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid,
                $"The persisted specification digest field {fieldName} is not canonical.");
        }
    }

}

/// <summary>
/// Canonical, field-labelled, length-delimited SHA-256 digest of an execution
/// specification. All integers are big-endian and all strings are UTF-8;
/// culture, JSON, object hashes, and enumeration order do not participate.
/// </summary>
internal static class RecurringOccurrenceExecutionSpecificationDigest
{
    internal static string Compute(RecurringOccurrenceExecutionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var bytes = new List<byte>(2048);
        AppendString(bytes, "recurring-occurrence-execution-spec/v1");
        AppendInt64(bytes, RecurringOccurrenceExecutionSpecification.CanonicalVersion);
        AppendString(bytes, "plan_id"); AppendString(bytes, specification.PlanId);
        AppendString(bytes, "occurrence_id"); AppendString(bytes, specification.OccurrenceId);
        AppendString(bytes, "occurrence_identity"); AppendString(bytes, specification.OccurrenceIdentity);
        AppendString(bytes, "lease_id"); AppendString(bytes, specification.LeaseId);
        AppendString(bytes, "schedule_revision"); AppendInt64(bytes, specification.ScheduleRevision);
        AppendString(bytes, "schedule_digest"); AppendString(bytes, specification.ScheduleDigest);
        AppendString(bytes, "time_zone_rules_digest"); AppendString(bytes, specification.TimeZoneRulesDigest);
        AppendString(bytes, "profile_id"); AppendString(bytes, specification.ProfileId);
        AppendString(bytes, "profile_version"); AppendInt64(bytes, specification.ProfileVersion);
        AppendString(bytes, "profile_digest"); AppendString(bytes, specification.ProfileDigest);
        AppendString(bytes, "configuration_digest"); AppendString(bytes, specification.ConfigurationDigest);
        AppendString(bytes, "lease_authorization_digest"); AppendString(bytes, specification.LeaseAuthorizationDigest);
        AppendString(bytes, "local_approval_id"); AppendString(bytes, specification.LocalApprovalId);
        AppendString(bytes, "local_approval_digest"); AppendString(bytes, specification.LocalApprovalDigest);
        AppendString(bytes, "scheduled_start_utc_ticks"); AppendInt64(bytes, specification.ScheduledStartUtc.UtcDateTime.Ticks);
        AppendString(bytes, "latest_start_utc_ticks"); AppendInt64(bytes, specification.LatestStartUtc.UtcDateTime.Ticks);
        AppendString(bytes, "planned_end_utc_ticks"); AppendInt64(bytes, specification.PlannedEndUtc.UtcDateTime.Ticks);
        AppendString(bytes, "evaluated_at_utc_ticks"); AppendInt64(bytes, specification.EvaluatedAtUtc.UtcDateTime.Ticks);
        AppendString(bytes, "stable_display_fingerprint"); AppendString(bytes, specification.StableDisplayFingerprint);
        AppendString(bytes, "display_bounds"); AppendRectangle(bytes, specification.DisplayBounds);
        AppendString(bytes, "region_within_display"); AppendRectangle(bytes, specification.RegionWithinDisplay);
        AppendString(bytes, "virtual_screen_region"); AppendRectangle(bytes, specification.VirtualScreenRegion);
        AppendString(bytes, "dpi_x"); AppendInt32(bytes, specification.DpiX);
        AppendString(bytes, "dpi_y"); AppendInt32(bytes, specification.DpiY);
        AppendString(bytes, "physical_width"); AppendInt32(bytes, specification.PhysicalWidth);
        AppendString(bytes, "physical_height"); AppendInt32(bytes, specification.PhysicalHeight);
        AppendString(bytes, "orientation"); AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(specification.Orientation));
        AppendString(bytes, "topology_digest"); AppendString(bytes, specification.TopologyDigest);
        AppendString(bytes, "backend"); AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(specification.Backend));
        AppendString(bytes, "audio_mode"); AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(specification.AudioMode));
        AppendString(bytes, "duration_ticks"); AppendInt64(bytes, specification.Duration.Ticks);
        AppendString(bytes, "countdown_seconds"); AppendInt32(bytes, specification.CountdownSeconds);
        AppendString(bytes, "normalized_output_directory"); AppendString(bytes, specification.NormalizedOutputDirectory);
        AppendString(bytes, "frozen_output_file_name"); AppendString(bytes, specification.FrozenOutputFileName);
        AppendString(bytes, "frozen_output_file_path"); AppendString(bytes, specification.FrozenOutputFilePath);
        AppendString(bytes, "output_conflict_policy"); AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(specification.OutputConflictPolicy));
        AppendString(bytes, "approved_current_user_sid"); AppendString(bytes, specification.ApprovedCurrentUserSid);
        AppendString(bytes, "approved_session_binding"); AppendString(bytes, specification.ApprovedSessionBinding);

        return RecurringOccurrenceExecutionSpecification.DigestPrefix +
            Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void AppendRectangle(List<byte> bytes, AuthorizedPhysicalRectangle rectangle)
    {
        AppendInt32(bytes, rectangle.X);
        AppendInt32(bytes, rectangle.Y);
        AppendInt32(bytes, rectangle.Width);
        AppendInt32(bytes, rectangle.Height);
    }

    private static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendInt64(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void AppendInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendInt64(List<byte> bytes, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}
