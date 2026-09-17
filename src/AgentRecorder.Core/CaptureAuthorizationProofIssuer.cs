using System;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Trusted Core-only issuer and binding calculator for capture authorization.
/// Standing lease proofs are issued only from an internal first-commit receipt;
/// there is intentionally no public lease issuer or orchestration path.
/// </summary>
internal static class CaptureAuthorizationProofIssuer
{
    internal const int InteractivePostApprovalStartupDeadlineSeconds = 30;

    private const string CapturePlanDigestSchema = "capture-plan/v1";
    private const string CaptureScopeDigestSchema = "capture-scope/v1";
    private const string StandingLeaseUsePlanDigestSchema = "standing-lease-use-plan/v1";
    private const string RecurringLeaseUsePlanDigestSchema = "recurring-lease-use-plan/v1";
    private const string RecurringLeaseUseScopeDigestSchema = "recurring-lease-use-scope/v1";

    internal static StandingLeaseUseProof IssueStandingLeaseUse(
        StandingLeaseUseProofIssuanceReceipt receipt)
    {
        if (receipt == null) throw new ArgumentNullException(nameof(receipt));

        ValidateStandingReceipt(receipt);

        var issuedAt = receipt.CommittedAtUtc;
        var expiry = receipt.Occurrence.WindowEndUtc <= receipt.Lease.ValidUntilUtc
            ? receipt.Occurrence.WindowEndUtc
            : receipt.Lease.ValidUntilUtc;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        return new StandingLeaseUseProof(
            proofId: "standing_" + Guid.NewGuid().ToString("N"),
            runId: receipt.Run.Id,
            authorizationSourceId: "standing-lease-use:" + receipt.Use.Id,
            capturePlanDigest: ComputeStandingLeaseUsePlanDigest(receipt.Plan),
            scopeDigest: receipt.Scope.ScopeDigest,
            issuedAtUtc: issuedAt,
            expiresAtUtc: expiry,
            currentUserSid: receipt.Scope.CurrentUserSid,
            sessionBinding: receipt.Scope.SessionBinding,
            leaseId: receipt.Lease.Id,
            leaseUseId: receipt.Use.Id,
            oneTimeNonce: nonce,
            maxDuration: receipt.Scope.ReservedDuration);
    }

    internal static string ComputeStandingLeaseUsePlanDigest(PlanDefinition plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        return Digest(builder =>
        {
            Field(builder, "schema", StandingLeaseUsePlanDigestSchema);
            Field(builder, "plan_id", plan.Id);
            Field(builder, "is_one_time", StableBool(plan.IsOneTime));
            Field(builder, "status", plan.StatusCode);
            Field(builder, "created_at_utc_ticks", StableInt64(plan.CreatedAtUtc.UtcDateTime.Ticks));
            Field(builder, "updated_at_utc_ticks", StableInt64(plan.UpdatedAtUtc.UtcDateTime.Ticks));
            Field(builder, "version", StableInt64(plan.Version));
        });
    }

    internal static void ValidateStandingReceipt(StandingLeaseUseProofIssuanceReceipt receipt)
    {
        var scope = receipt.Scope;
        var plan = receipt.Plan;
        var occurrence = receipt.Occurrence;
        var lease = receipt.Lease;
        var run = receipt.Run;
        var use = receipt.Use;

        if (!plan.IsOneTime ||
            !string.Equals(scope.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, lease.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            lease.MaxUses != 1 ||
            scope.ReservedDuration != use.ReservedDuration ||
            !string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.LeaseId, lease.Id, StringComparison.Ordinal) ||
            !string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(use.RunId, run.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The standing proof receipt is not internally bound.");
        }

        if (occurrence.Status != PlanOccurrenceStatus.RunCreated ||
            run.Status != RecordingRunStatus.StartCommitted ||
            !run.HasCrossedStartCommit ||
            !run.IsNonRetryable ||
            use.Status != LeaseUseStatus.StartCommitted ||
            !use.IsQuotaConsumed ||
            use.ReservedUseCount != 1 ||
            lease.Status != ConsentLeaseStatus.Exhausted)
        {
            throw new InvalidOperationException("The standing proof receipt does not prove a committed first use.");
        }

        if (receipt.CommittedAtUtc.Offset != TimeSpan.Zero ||
            receipt.CommittedAtUtc != run.CreatedAtUtc ||
            receipt.CommittedAtUtc != use.CreatedAtUtc ||
            receipt.CommittedAtUtc < scope.CreatedAtUtc ||
            receipt.CommittedAtUtc < occurrence.WindowStartUtc ||
            receipt.CommittedAtUtc >= occurrence.WindowEndUtc ||
            receipt.CommittedAtUtc < lease.ValidFromUtc ||
            receipt.CommittedAtUtc >= lease.ValidUntilUtc)
        {
            throw new InvalidOperationException("The standing proof receipt time is outside its authorization scope.");
        }

        long endTicks;
        try
        {
            endTicks = checked(receipt.CommittedAtUtc.UtcDateTime.Ticks + scope.ReservedDuration.Ticks);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The standing proof receipt duration overflowed.", exception);
        }

        if (scope.ReservedDuration <= TimeSpan.Zero ||
            scope.ReservedDuration > lease.MaxDuration ||
            endTicks > occurrence.WindowEndUtc.UtcDateTime.Ticks ||
            endTicks > lease.ValidUntilUtc.UtcDateTime.Ticks)
        {
            throw new InvalidOperationException("The standing proof receipt duration exceeds its authorization scope.");
        }

        var expiry = occurrence.WindowEndUtc <= lease.ValidUntilUtc
            ? occurrence.WindowEndUtc
            : lease.ValidUntilUtc;
        if (expiry <= receipt.CommittedAtUtc)
        {
            throw new InvalidOperationException("The standing proof receipt has no positive validity window.");
        }
    }

    /// <summary>
    /// Issues the only process-local proof for a recurring first
    /// start-commit. The receipt is the post-commit hand-off; all of its
    /// durable and immutable bindings are revalidated before any random
    /// nonce is requested.
    /// </summary>
    internal static RecurringLeaseUseProof IssueRecurringLeaseUse(
        RecurringStartCommitReceipt receipt)
    {
        if (receipt == null) throw new ArgumentNullException(nameof(receipt));

        ValidateRecurringStartCommitReceipt(receipt);

        var issuedAt = receipt.CommittedAtUtc;
        var expiry = receipt.Specification.PlannedEndUtc <= receipt.Lease.ValidUntilUtc
            ? receipt.Specification.PlannedEndUtc
            : receipt.Lease.ValidUntilUtc;
        if (expiry <= issuedAt)
            throw new InvalidOperationException("The recurring proof has no positive validity window.");

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new RecurringLeaseUseProof(
            proofId: "recurring_" + Guid.NewGuid().ToString("N"),
            runId: receipt.Run.Id,
            leaseId: receipt.Lease.Id,
            leaseUseId: receipt.Use.Id,
            occurrenceIdentity: receipt.Specification.OccurrenceIdentity,
            specificationDigest: receipt.Specification.SpecificationDigest,
            authorizationSourceId: RecurringAuthorizationSourceId(receipt.Lease.Id, receipt.Use.Id),
            capturePlanDigest: ComputeRecurringLeaseUsePlanDigest(
                receipt.Plan, receipt.Occurrence, receipt.Specification),
            scopeDigest: ComputeRecurringLeaseUseScopeDigest(
                receipt.Specification, receipt.Lease, receipt.Approval),
            issuedAtUtc: issuedAt,
            expiresAtUtc: expiry,
            currentUserSid: receipt.Approval.CurrentUserSid,
            sessionBinding: receipt.Approval.SessionBinding,
            oneTimeNonce: nonce,
            maxDuration: receipt.Specification.Duration);
    }

    /// <summary>
    /// Revalidates every identity, digest, lifecycle, quota and time binding
    /// before recurring proof issuance. This method intentionally accepts only
    /// the Core receipt and never a public result or caller-supplied IDs.
    /// </summary>
    internal static void ValidateRecurringStartCommitReceipt(
        RecurringStartCommitReceipt receipt,
        bool allowRevokedLease = false)
    {
        if (receipt == null) throw new ArgumentNullException(nameof(receipt));

        var plan = receipt.Plan;
        var occurrence = receipt.Occurrence;
        var lease = receipt.Lease;
        var specification = receipt.Specification;
        var run = receipt.Run;
        var use = receipt.Use;
        var approval = receipt.Approval;
        var quota = receipt.Quota;

        var recomputedQuota = RecomputeRecurringQuota(receipt);
        Require(
            QuotaMatchesExactly(recomputedQuota, quota),
            "The recurring proof receipt quota is not the exact calculation for all lease accounting entries.");

        var currentUseEntries = receipt.LeaseEntries
            .Where(entry => string.Equals(entry.UseId, use.Id, StringComparison.Ordinal))
            .ToArray();
        Require(
            currentUseEntries.Length == 1,
            "The recurring proof receipt current use is not represented exactly once in lease accounting evidence.");
        var currentUseEntry = currentUseEntries[0];
        Require(
            string.Equals(currentUseEntry.LeaseId, lease.Id, StringComparison.Ordinal) &&
            string.Equals(currentUseEntry.RunId, run.Id, StringComparison.Ordinal) &&
            string.Equals(currentUseEntry.OccurrenceIdentity, specification.OccurrenceIdentity, StringComparison.Ordinal) &&
            currentUseEntry.Status == use.Status &&
            currentUseEntry.ReservedUseCount == use.ReservedUseCount &&
            currentUseEntry.ReservedDuration == use.ReservedDuration &&
            currentUseEntry.ActualSettledDuration == use.ActualSettledDuration,
            "The recurring proof receipt current use evidence does not match the committed use.");

        Require(
            plan.IsPeriodic && plan.Status == PlanDefinitionStatus.Enabled &&
            IsFiniteVersion(plan.Version) &&
            string.Equals(plan.Id, specification.PlanId, StringComparison.Ordinal) &&
            string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal) &&
            string.Equals(occurrence.Id, specification.OccurrenceId, StringComparison.Ordinal) &&
            string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) &&
            string.Equals(lease.ConfigurationRef.PlanId, plan.Id, StringComparison.Ordinal) &&
            string.Equals(lease.Id, specification.LeaseId, StringComparison.Ordinal),
            "The recurring proof receipt is not bound to an enabled periodic plan.");

        Require(
            occurrence.Status == PlanOccurrenceStatus.RunCreated &&
            occurrence.TerminalReasonCode is null &&
            occurrence.RunId is not null &&
            string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) &&
            IsFiniteVersion(occurrence.Version),
            "The recurring proof receipt occurrence is not an exact run-created state.");
        Require(
            occurrence.Version is 4 or 5,
            "The recurring proof receipt occurrence version is not a committed reservation version.");

        Require(
            string.Equals(run.OccurrenceId, occurrence.Id, StringComparison.Ordinal) &&
            string.Equals(use.LeaseId, lease.Id, StringComparison.Ordinal) &&
            string.Equals(use.OccurrenceId, occurrence.Id, StringComparison.Ordinal) &&
            string.Equals(use.RunId, run.Id, StringComparison.Ordinal),
            "The recurring proof receipt run/use relations are not exact.");

        Require(
            run.Status == RecordingRunStatus.StartCommitted &&
            run.HasCrossedStartCommit &&
            run.TerminalReasonCode is null &&
            run.MediaArtifactId is null &&
            run.BundleId is null &&
            run.Version == 2 &&
            use.Status == LeaseUseStatus.StartCommitted &&
            use.ReservedUseCount == 1 &&
            use.ReservedDuration == specification.Duration &&
            use.ReservedDuration == lease.PerRunDuration &&
            use.ActualSettledDuration is null &&
            use.Version == 1 &&
            IsFiniteVersion(run.Version) &&
            IsFiniteVersion(use.Version),
            "The recurring proof receipt does not prove the exact first committed use.");

        Require(
            (lease.Status is ConsentLeaseStatus.Active or ConsentLeaseStatus.Exhausted ||
                allowRevokedLease && lease.Status == ConsentLeaseStatus.Revoked) &&
            IsFiniteVersion(lease.Version) &&
            (allowRevokedLease && lease.Status == ConsentLeaseStatus.Revoked ||
                recomputedQuota.IsTerminallyExhausted == (lease.Status == ConsentLeaseStatus.Exhausted) &&
                (lease.Status == ConsentLeaseStatus.Exhausted
                    ? recomputedQuota.IsTerminallyExhausted &&
                      !recomputedQuota.IsTemporarilyUnavailable &&
                      recomputedQuota.ReasonCode == RecurringLeaseQuotaReasonCodes.QuotaExhausted
                    : !recomputedQuota.IsTerminallyExhausted &&
                      recomputedQuota.IsTemporarilyUnavailable &&
                      recomputedQuota.InFlightUseCount >= 1 &&
                      recomputedQuota.ReasonCode == RecurringLeaseQuotaReasonCodes.RunInFlight)),
            "The recurring proof receipt quota and lease status are not exact.");

        Require(
            string.Equals(
                RecurringPlanConfigurationDigest.Compute(
                    plan.Id,
                    lease.ConfigurationRef.ScheduleRevision,
                    lease.ConfigurationRef.ScheduleDigest,
                    lease.ConfigurationRef.TimeZoneRulesDigest,
                    lease.ConfigurationRef.ProfileRef),
                lease.ConfigurationRef.ConfigurationDigest,
                StringComparison.Ordinal) &&
            string.Equals(specification.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) &&
            specification.ScheduleRevision == lease.ConfigurationRef.ScheduleRevision &&
            string.Equals(specification.ScheduleDigest, lease.ConfigurationRef.ScheduleDigest, StringComparison.Ordinal) &&
            string.Equals(specification.TimeZoneRulesDigest, lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal) &&
            string.Equals(specification.ProfileId, lease.ConfigurationRef.ProfileRef.ProfileId, StringComparison.Ordinal) &&
            specification.ProfileVersion == lease.ConfigurationRef.ProfileRef.ProfileVersion &&
            string.Equals(specification.ProfileDigest, lease.ConfigurationRef.ProfileRef.ProfileDigest, StringComparison.Ordinal) &&
            string.Equals(specification.LeaseAuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) &&
            string.Equals(
                RecurringConsentLeaseAuthorizationDigest.Compute(lease.Authorization),
                lease.AuthorizationDigest,
                StringComparison.Ordinal) &&
            string.Equals(
                RecurringOccurrenceExecutionSpecificationDigest.Compute(specification),
                specification.SpecificationDigest,
                StringComparison.Ordinal),
            "The recurring proof receipt specification or authorization digest is not canonical.");

        Require(
            string.Equals(approval.ApprovalId, specification.LocalApprovalId, StringComparison.Ordinal) &&
            string.Equals(approval.LeaseId, lease.Id, StringComparison.Ordinal) &&
            string.Equals(approval.PlanId, plan.Id, StringComparison.Ordinal) &&
            string.Equals(approval.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) &&
            string.Equals(approval.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) &&
            string.Equals(approval.CurrentUserSid, specification.ApprovedCurrentUserSid, StringComparison.Ordinal) &&
            string.Equals(approval.SessionBinding, specification.ApprovedSessionBinding, StringComparison.Ordinal) &&
            approval.ApprovalKind == RecurringLeaseLocalApprovalReceipt.CurrentApprovalKind &&
            approval.ApprovalVersion == RecurringLeaseLocalApprovalReceipt.CurrentApprovalVersion &&
            string.Equals(approval.ApprovalDigest, specification.LocalApprovalDigest, StringComparison.Ordinal) &&
            string.Equals(
                RecurringLeaseLocalApprovalDigest.Compute(
                    approval.ApprovalId,
                    approval.LeaseId,
                    approval.PlanId,
                    approval.ConfigurationDigest,
                    approval.AuthorizationDigest,
                    approval.CurrentUserSid,
                    approval.SessionBinding,
                    approval.ApprovedAtUtc,
                    approval.ApprovalKind,
                    approval.ApprovalVersion),
                approval.ApprovalDigest,
                StringComparison.Ordinal),
            "The recurring proof receipt approval evidence is not exact.");

        Require(
            receipt.CommittedAtUtc.Offset == TimeSpan.Zero &&
            specification.ScheduledStartUtc.Offset == TimeSpan.Zero &&
            specification.LatestStartUtc.Offset == TimeSpan.Zero &&
            specification.PlannedEndUtc.Offset == TimeSpan.Zero &&
            specification.EvaluatedAtUtc.Offset == TimeSpan.Zero &&
            run.CreatedAtUtc.Offset == TimeSpan.Zero &&
            run.UpdatedAtUtc.Offset == TimeSpan.Zero &&
            use.CreatedAtUtc.Offset == TimeSpan.Zero &&
            use.UpdatedAtUtc.Offset == TimeSpan.Zero &&
            occurrence.CreatedAtUtc.Offset == TimeSpan.Zero &&
            occurrence.UpdatedAtUtc.Offset == TimeSpan.Zero &&
            receipt.CommittedAtUtc == run.UpdatedAtUtc &&
            receipt.CommittedAtUtc == use.UpdatedAtUtc &&
            run.CreatedAtUtc == use.CreatedAtUtc &&
            run.CreatedAtUtc == occurrence.UpdatedAtUtc &&
            occurrence.CreatedAtUtc <= occurrence.UpdatedAtUtc &&
            run.CreatedAtUtc <= run.UpdatedAtUtc &&
            use.CreatedAtUtc <= use.UpdatedAtUtc &&
            approval.ApprovedAtUtc <= receipt.CommittedAtUtc &&
            receipt.CommittedAtUtc >= specification.EvaluatedAtUtc &&
            receipt.CommittedAtUtc >= specification.ScheduledStartUtc &&
            receipt.CommittedAtUtc <= specification.LatestStartUtc &&
            receipt.CommittedAtUtc >= lease.ValidFromUtc &&
            receipt.CommittedAtUtc < lease.ValidUntilUtc,
            "The recurring proof receipt timeline is not an exact UTC commit chain.");

        long endTicks;
        try
        {
            endTicks = checked(receipt.CommittedAtUtc.UtcDateTime.Ticks + specification.Duration.Ticks);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The recurring proof receipt duration overflowed.", exception);
        }

        Require(
            specification.Duration > TimeSpan.Zero &&
            specification.Duration.Ticks % TimeSpan.TicksPerMillisecond == 0 &&
            specification.Duration <= lease.PerRunDuration &&
            endTicks <= specification.PlannedEndUtc.UtcDateTime.Ticks &&
            endTicks <= lease.ValidUntilUtc.UtcDateTime.Ticks,
            "The recurring proof receipt duration exceeds its authorization window.");
    }

    private static RecurringLeaseQuotaSnapshot RecomputeRecurringQuota(
        RecurringStartCommitReceipt receipt)
    {
        try
        {
            return RecurringLeaseQuotaCalculator.Calculate(receipt.Lease, receipt.LeaseEntries);
        }
        catch (Phase3DomainException exception)
        {
            throw new InvalidOperationException(
                "The recurring proof receipt accounting evidence is not a valid exact quota projection.",
                exception);
        }
    }

    private static bool QuotaMatchesExactly(
        RecurringLeaseQuotaSnapshot recomputed,
        RecurringLeaseQuotaSnapshot supplied) =>
        string.Equals(recomputed.LeaseId, supplied.LeaseId, StringComparison.Ordinal) &&
        string.Equals(recomputed.LeaseAuthorizationDigest, supplied.LeaseAuthorizationDigest, StringComparison.Ordinal) &&
        recomputed.LeaseVersion == supplied.LeaseVersion &&
        recomputed.AdmissionUsedUses == supplied.AdmissionUsedUses &&
        recomputed.AdmissionUsedDuration == supplied.AdmissionUsedDuration &&
        recomputed.PermanentlyConsumedUses == supplied.PermanentlyConsumedUses &&
        recomputed.ConservativelyChargedDuration == supplied.ConservativelyChargedDuration &&
        recomputed.RemainingReservableUses == supplied.RemainingReservableUses &&
        recomputed.RemainingReservableDuration == supplied.RemainingReservableDuration &&
        recomputed.InFlightUseCount == supplied.InFlightUseCount &&
        recomputed.IsTemporarilyUnavailable == supplied.IsTemporarilyUnavailable &&
        recomputed.IsTerminallyExhausted == supplied.IsTerminallyExhausted &&
        string.Equals(recomputed.ReasonCode, supplied.ReasonCode, StringComparison.Ordinal);

    internal static string ComputeRecurringLeaseUsePlanDigest(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        RecurringOccurrenceExecutionSpecification specification)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (occurrence == null) throw new ArgumentNullException(nameof(occurrence));
        if (specification == null) throw new ArgumentNullException(nameof(specification));

        return Digest(builder =>
        {
            Field(builder, "schema", RecurringLeaseUsePlanDigestSchema);
            Field(builder, "plan_id", plan.Id);
            Field(builder, "occurrence_id", occurrence.Id);
            Field(builder, "occurrence_identity", specification.OccurrenceIdentity);
            Field(builder, "specification_version", StableInt32(RecurringOccurrenceExecutionSpecification.CanonicalVersion));
            Field(builder, "specification_digest", specification.SpecificationDigest);
        });
    }

    internal static string ComputeRecurringLeaseUseScopeDigest(
        RecurringOccurrenceExecutionSpecification specification,
        RecurringConsentLease lease,
        RecurringLeaseLocalApprovalEvidence approval)
    {
        if (specification == null) throw new ArgumentNullException(nameof(specification));
        if (lease == null) throw new ArgumentNullException(nameof(lease));
        if (approval == null) throw new ArgumentNullException(nameof(approval));

        return Digest(builder =>
        {
            Field(builder, "schema", RecurringLeaseUseScopeDigestSchema);
            Field(builder, "specification_version", StableInt32(RecurringOccurrenceExecutionSpecification.CanonicalVersion));
            Field(builder, "specification_digest", specification.SpecificationDigest);
            Field(builder, "lease_id", lease.Id);
            Field(builder, "lease_authorization_digest", lease.AuthorizationDigest);
            Field(builder, "approval_id", approval.ApprovalId);
            Field(builder, "approval_digest", approval.ApprovalDigest);
            Field(builder, "approved_current_user_sid", approval.CurrentUserSid);
            Field(builder, "approved_session_binding", approval.SessionBinding);
        });
    }

    internal static string RecurringAuthorizationSourceId(string leaseId, string leaseUseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("Lease ID is required.", nameof(leaseId));
        if (string.IsNullOrWhiteSpace(leaseUseId)) throw new ArgumentException("Lease use ID is required.", nameof(leaseUseId));
        return "recurring-lease-use:" + leaseId + ":" + leaseUseId;
    }

    private static bool IsFiniteVersion(long version) => version >= 0 && version != long.MaxValue;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static CaptureAuthorizationProof IssueInteractiveConfirmation(
        Recording recording,
        Confirmation confirmation,
        CapturePlan approvedPlan)
        => IssueInteractiveConfirmation(
            recording,
            confirmation,
            approvedPlan,
            DateTimeOffset.UtcNow);

    internal static CaptureAuthorizationProof IssueInteractiveConfirmation(
        Recording recording,
        Confirmation confirmation,
        CapturePlan approvedPlan,
        DateTimeOffset issuedAtUtc)
    {
        if (recording == null) throw new ArgumentNullException(nameof(recording));
        if (confirmation == null) throw new ArgumentNullException(nameof(confirmation));
        if (approvedPlan == null) throw new ArgumentNullException(nameof(approvedPlan));
        if (!string.Equals(confirmation.Status, "approved", StringComparison.Ordinal))
            throw new InvalidOperationException("Interactive capture proof requires an approved confirmation.");
        if (!string.Equals(confirmation.RecordingId, recording.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Confirmation is not bound to the recording.");

        recording.Config.NormalizeAudioSource();
        var issuedAt = issuedAtUtc.ToUniversalTime();
        var expiresAt = issuedAt.AddSeconds(InteractivePostApprovalStartupDeadlineSeconds);

        return new InteractiveConfirmationProof(
            proofId: "auth_" + Guid.NewGuid().ToString("N"),
            recordingId: recording.Id,
            runId: recording.Id,
            authorizationSourceId: confirmation.Id,
            capturePlanDigest: ComputeCapturePlanDigest(approvedPlan),
            scopeDigest: ComputeCaptureScopeDigest(recording, approvedPlan),
            issuedAtUtc: issuedAt,
            expiresAtUtc: expiresAt,
            userSessionBinding: CaptureAuthorizationSessionBinding.Current,
            maxDurationSeconds: recording.DurationSeconds ?? recording.Config.DurationSeconds,
            maxFrameCount: recording.Config.ScreenshotSeries?.PlannedFrameCount);
    }

    /// <summary>
    /// Synthetic proof used only by the existing direct-engine test seam. It
    /// is never reachable from API/App composition and is marked on Recording
    /// so the production confirmation checks remain strict.
    /// </summary>
    internal static CaptureAuthorizationProof IssueForTests(
        Recording recording,
        CapturePlan approvedPlan)
    {
        if (recording == null) throw new ArgumentNullException(nameof(recording));
        if (approvedPlan == null) throw new ArgumentNullException(nameof(approvedPlan));

        recording.Config.NormalizeAudioSource();
        var issuedAt = DateTimeOffset.UtcNow;
        return new InteractiveConfirmationProof(
            proofId: "auth_test_" + Guid.NewGuid().ToString("N"),
            recordingId: recording.Id,
            runId: recording.Id,
            authorizationSourceId: recording.ConfirmationId ?? "test_confirmation",
            capturePlanDigest: ComputeCapturePlanDigest(approvedPlan),
            scopeDigest: ComputeCaptureScopeDigest(recording, approvedPlan),
            issuedAtUtc: issuedAt,
            expiresAtUtc: issuedAt.AddHours(1),
            userSessionBinding: CaptureAuthorizationSessionBinding.Current,
            maxDurationSeconds: recording.DurationSeconds ?? recording.Config.DurationSeconds,
            maxFrameCount: recording.Config.ScreenshotSeries?.PlannedFrameCount);
    }

    internal static string ComputeCapturePlanDigest(CapturePlan plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        return Digest(builder =>
        {
            Field(builder, "schema", CapturePlanDigestSchema);
            Field(builder, "requested_backend", plan.RequestedBackend);
            Field(builder, "planned_backend", plan.PlannedBackend);
            Field(builder, "selection_reason", plan.Evidence.SelectionReasonCode);
            Field(builder, "availability_source", plan.Evidence.AvailabilitySource);
            Field(builder, "fallback", StableBool(plan.FallbackOccurred));
            Field(builder, "capture_semantics", plan.CaptureSemantics);
            Field(builder, "preview_semantics", plan.PreviewSemantics);
            Field(builder, "coordinate_space", plan.CoordinateSpace);
            Field(builder, "source_kind", plan.SourceKind);
            Field(builder, "target_identity", plan.TargetIdentity);
            Field(builder, "window_handle", StableInt64(plan.WindowHandle.ToInt64()));
            Bounds(builder, "bounds", plan.Bounds);
            Field(builder, "target_display_identity", plan.TargetDisplayIdentity);
            Field(builder, "target_display_identity_status", StableEnum(plan.TargetDisplayIdentityStatus));
            Field(builder, "target_display_id", plan.TargetDisplayId);
            Bounds(builder, "display_bounds", plan.DisplayBounds);
            Field(builder, "audio_source_kind", StableEnum(plan.AudioSourceKind));
            Field(builder, "audio_endpoint_id", plan.AudioEndpointId);
            Field(builder, "audio_endpoint_is_default", StableNullableBool(plan.AudioEndpointIsDefault));
        });
    }

    internal static string ComputeCaptureScopeDigest(Recording recording, CapturePlan plan)
    {
        if (recording == null) throw new ArgumentNullException(nameof(recording));
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        var cfg = recording.Config;
        return Digest(builder =>
        {
            Field(builder, "schema", CaptureScopeDigestSchema);
            Field(builder, "plan_digest", ComputeCapturePlanDigest(plan));
            Field(builder, "recording_source_type", recording.SourceType);
            Field(builder, "recording_output_path", recording.OutputPath);
            Field(builder, "config_output_path", cfg.OutputPath);
            Field(builder, "output_conflict_policy", cfg.OutputConflictPolicy);
            Field(builder, "mode", cfg.Mode);
            Field(builder, "source_kind", cfg.SourceKind);
            Field(builder, "display_id", cfg.DisplayId);
            Field(builder, "display_stable_identity", cfg.DisplayStableIdentity);
            Field(builder, "display_identity_status", StableEnum(cfg.DisplayIdentityStatus));
            Bounds(builder, "display_bounds", cfg.DisplayBounds);
            Field(builder, "window_handle", StableInt64(cfg.WindowHandle.ToInt64()));
            Bounds(builder, "capture_bounds", cfg.Bounds);
            Field(builder, "microphone", StableBool(cfg.Microphone));
            Field(builder, "mic_device", cfg.MicDevice);
            Field(builder, "audio_source_kind", StableEnum(cfg.AudioSourceKind));
            Field(builder, "system_loopback_endpoint", cfg.SystemLoopbackEndpoint);
            Field(builder, "system_loopback_endpoint_is_default", StableNullableBool(cfg.SystemLoopbackEndpointIsDefault));
            Field(builder, "fps", StableInt32(cfg.Fps));
            Field(builder, "quality", cfg.Quality);
            Field(builder, "duration_seconds", StableNullableInt32(cfg.DurationSeconds));
            Field(builder, "countdown_seconds", StableInt32(cfg.CountdownSeconds));
            Field(builder, "region_normalized_width", StableNullableInt32(cfg.RegionNormalizedBounds?.w));
            Field(builder, "region_normalized_height", StableNullableInt32(cfg.RegionNormalizedBounds?.h));

            var series = cfg.ScreenshotSeries;
            Field(builder, "series_interval_ms", StableNullableInt32(series?.IntervalMs));
            Field(builder, "series_max_count", StableNullableInt32(series?.MaxCount));
            Field(builder, "series_max_duration_seconds", StableNullableInt32(series?.MaxDurationSeconds));
            Field(builder, "series_planned_frame_count", StableNullableInt32(series?.PlannedFrameCount));
        });
    }

    private static string Digest(Action<StringBuilder> write)
    {
        var builder = new StringBuilder();
        write(builder);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void Field(StringBuilder builder, string name, string? value)
    {
        builder.Append(name).Append('=');
        if (value == null)
        {
            builder.Append("<null>");
        }
        else
        {
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        builder.Append('\n');
    }

    private static void Bounds(StringBuilder builder, string name, CapturePlanBounds? bounds)
    {
        if (bounds == null)
        {
            Field(builder, name, null);
            return;
        }

        Field(builder, name, string.Create(
            CultureInfo.InvariantCulture,
            $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}"));
    }

    private static void Bounds(StringBuilder builder, string name, (int x, int y, int w, int h)? bounds)
    {
        if (bounds == null)
        {
            Field(builder, name, null);
            return;
        }

        var value = bounds.Value;
        Field(builder, name, string.Create(
            CultureInfo.InvariantCulture,
            $"{value.x},{value.y},{value.w},{value.h}"));
    }

    private static string StableBool(bool value) => value ? "true" : "false";

    private static string? StableNullableBool(bool? value) =>
        value.HasValue ? StableBool(value.Value) : null;

    private static string StableInt32(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string StableInt64(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? StableNullableInt32(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string StableEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
