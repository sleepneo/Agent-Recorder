using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Immutable, process-local evidence for the execution handoff. It is made
/// before the authorization CAS and contains no lifecycle aggregate or
/// collection reference. The ticket copies these values after the CAS.
/// </summary>
internal sealed class RecurringLeaseCaptureExecutionHandoffEvidence
{
    internal RecurringLeaseCaptureExecutionHandoffEvidence(
        RecurringLeaseUseProof consumedProof,
        RecurringLeaseExecutionSnapshot snapshot,
        DateTimeOffset validatedAtUtc)
    {
        ConsumedProof = consumedProof ?? throw new ArgumentNullException(nameof(consumedProof));
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        Specification = snapshot.Specification;
        if (validatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The recurring authorization time must be UTC.", nameof(validatedAtUtc));

        ValidatedAtUtc = validatedAtUtc;

        var plan = snapshot.Plan;
        PlanId = plan.Id;
        PlanIsOneTime = plan.IsOneTime;
        PlanIsPeriodic = plan.IsPeriodic;
        PlanStatus = plan.Status;
        PlanCreatedAtUtc = plan.CreatedAtUtc;
        PlanUpdatedAtUtc = plan.UpdatedAtUtc;
        PlanVersion = plan.Version;

        var occurrence = snapshot.Occurrence;
        OccurrenceId = occurrence.Id;
        OccurrencePlanId = occurrence.PlanId;
        OccurrenceStatus = occurrence.Status;
        OccurrenceRunId = occurrence.RunId;
        OccurrenceTerminalReasonCode = occurrence.TerminalReasonCode;
        OccurrenceWindowStartUtc = occurrence.WindowStartUtc;
        OccurrenceWindowEndUtc = occurrence.WindowEndUtc;
        OccurrenceCreatedAtUtc = occurrence.CreatedAtUtc;
        OccurrenceUpdatedAtUtc = occurrence.UpdatedAtUtc;
        OccurrenceVersion = occurrence.Version;

        var lease = snapshot.Lease;
        LeaseId = lease.LeaseId;
        LeasePlanId = lease.PlanId;
        LeaseOccurrenceId = snapshot.Specification.OccurrenceId;
        LeaseStatus = lease.Status;
        LeaseValidFromUtc = lease.ValidFromUtc;
        LeaseValidUntilUtc = lease.ValidUntilUtc;
        LeaseAuthorizedPlanLatestEndUtc = lease.AuthorizedPlanLatestEndUtc;
        LeasePerRunDuration = lease.PerRunDuration;
        LeaseMaxUses = lease.MaxUses;
        LeaseMaxCumulativeDuration = lease.MaxCumulativeDuration;
        LeaseAuthorizationDigest = lease.AuthorizationDigest;
        LeaseCreatedAtUtc = lease.CreatedAtUtc;
        LeaseUpdatedAtUtc = lease.UpdatedAtUtc;
        LeaseVersion = lease.Version;

        var use = snapshot.Use;
        UseId = use.Id;
        UseLeaseId = use.LeaseId;
        UseOccurrenceId = use.OccurrenceId;
        UseRunId = use.RunId;
        UseStatus = use.Status;
        UseReservedUseCount = use.ReservedUseCount;
        UseReservedDuration = use.ReservedDuration;
        UseActualSettledDuration = use.ActualSettledDuration;
        UseCreatedAtUtc = use.CreatedAtUtc;
        UseUpdatedAtUtc = use.UpdatedAtUtc;
        UseVersion = use.Version;
        UseIsQuotaConsumed = use.IsQuotaConsumed;

        var run = snapshot.Run;
        RunId = run.Id;
        RunOccurrenceId = run.OccurrenceId;
        RunStatus = run.Status;
        RunHasCrossedStartCommit = run.HasCrossedStartCommit;
        RunMediaArtifactId = run.MediaArtifactId;
        RunBundleId = run.BundleId;
        RunTerminalReasonCode = run.TerminalReasonCode;
        RunCreatedAtUtc = run.CreatedAtUtc;
        RunUpdatedAtUtc = run.UpdatedAtUtc;
        RunVersion = run.Version;
        RunIsNonRetryable = run.IsNonRetryable;

        var profile = snapshot.Profile;
        ProfileBindingBoundAtUtc = snapshot.ProfileBindingBoundAtUtc;
        ProfileId = profile.ProfileId;
        ProfileVersion = profile.ProfileVersion;
        ProfileDigest = profile.ProfileDigest;
        TargetType = profile.TargetType;
        RebindPolicy = profile.RebindPolicy;
        CaptureSemantics = profile.CaptureSemantics;
        CoordinateSpace = profile.CoordinateSpace;
        DisplayIdentityStatus = profile.DisplayIdentityStatus;
        StableDisplayFingerprint = profile.StableDisplayFingerprint;
        DisplayBounds = profile.DisplayBounds;
        RegionWithinDisplay = profile.RegionWithinDisplay;
        VirtualScreenRegion = profile.VirtualScreenRegion;
        DpiX = profile.DpiX;
        DpiY = profile.DpiY;
        PhysicalWidth = profile.PhysicalWidth;
        PhysicalHeight = profile.PhysicalHeight;
        Orientation = profile.Orientation;
        TopologyDigest = profile.TopologyDigest;
        Backend = profile.Backend;
        AudioMode = profile.AudioMode;
        Duration = profile.Duration;
        CountdownSeconds = profile.CountdownSeconds;
        NormalizedOutputDirectory = profile.OutputDirectory;
        OutputConflictPolicy = profile.OutputConflictPolicy;
        WakePolicy = profile.WakePolicy;
        DesktopRequirement = profile.DesktopRequirement;
    }

    internal RecurringLeaseUseProof ConsumedProof { get; }
    internal RecurringOccurrenceExecutionSpecification Specification { get; }
    internal DateTimeOffset ValidatedAtUtc { get; }

    internal string PlanId { get; }
    internal bool PlanIsOneTime { get; }
    internal bool PlanIsPeriodic { get; }
    internal PlanDefinitionStatus PlanStatus { get; }
    internal DateTimeOffset PlanCreatedAtUtc { get; }
    internal DateTimeOffset PlanUpdatedAtUtc { get; }
    internal long PlanVersion { get; }

    internal string OccurrenceId { get; }
    internal string OccurrencePlanId { get; }
    internal PlanOccurrenceStatus OccurrenceStatus { get; }
    internal string? OccurrenceRunId { get; }
    internal string? OccurrenceTerminalReasonCode { get; }
    internal DateTimeOffset OccurrenceWindowStartUtc { get; }
    internal DateTimeOffset OccurrenceWindowEndUtc { get; }
    internal DateTimeOffset OccurrenceCreatedAtUtc { get; }
    internal DateTimeOffset OccurrenceUpdatedAtUtc { get; }
    internal long OccurrenceVersion { get; }

    internal string LeaseId { get; }
    internal string LeasePlanId { get; }
    internal string LeaseOccurrenceId { get; }
    internal ConsentLeaseStatus LeaseStatus { get; }
    internal DateTimeOffset LeaseValidFromUtc { get; }
    internal DateTimeOffset LeaseValidUntilUtc { get; }
    internal DateTimeOffset LeaseAuthorizedPlanLatestEndUtc { get; }
    internal TimeSpan LeasePerRunDuration { get; }
    internal long LeaseMaxUses { get; }
    internal TimeSpan LeaseMaxCumulativeDuration { get; }
    internal string LeaseAuthorizationDigest { get; }
    internal DateTimeOffset LeaseCreatedAtUtc { get; }
    internal DateTimeOffset LeaseUpdatedAtUtc { get; }
    internal long LeaseVersion { get; }

    internal string UseId { get; }
    internal string UseLeaseId { get; }
    internal string UseOccurrenceId { get; }
    internal string UseRunId { get; }
    internal LeaseUseStatus UseStatus { get; }
    internal int UseReservedUseCount { get; }
    internal TimeSpan UseReservedDuration { get; }
    internal TimeSpan? UseActualSettledDuration { get; }
    internal DateTimeOffset UseCreatedAtUtc { get; }
    internal DateTimeOffset UseUpdatedAtUtc { get; }
    internal long UseVersion { get; }
    internal bool UseIsQuotaConsumed { get; }

    internal string RunId { get; }
    internal string RunOccurrenceId { get; }
    internal RecordingRunStatus RunStatus { get; }
    internal bool RunHasCrossedStartCommit { get; }
    internal string? RunMediaArtifactId { get; }
    internal string? RunBundleId { get; }
    internal string? RunTerminalReasonCode { get; }
    internal DateTimeOffset RunCreatedAtUtc { get; }
    internal DateTimeOffset RunUpdatedAtUtc { get; }
    internal long RunVersion { get; }
    internal bool RunIsNonRetryable { get; }

    internal string ProfileId { get; }
    internal DateTimeOffset? ProfileBindingBoundAtUtc { get; }
    internal long ProfileVersion { get; }
    internal string ProfileDigest { get; }
    internal AuthorizedScopeTargetType TargetType { get; }
    internal RecurringFixedRegionRebindPolicy RebindPolicy { get; }
    internal AuthorizedCaptureSemantics CaptureSemantics { get; }
    internal AuthorizedCoordinateSpace CoordinateSpace { get; }
    internal AuthorizedDisplayIdentityStatus DisplayIdentityStatus { get; }
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
    internal AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }
    internal AuthorizedWakePolicy WakePolicy { get; }
    internal AuthorizedDesktopRequirement DesktopRequirement { get; }
}

/// <summary>
/// Immutable internal result of the recurring proof consumption boundary.
/// The original snapshot remains private for validation; the handoff surface
/// contains only frozen scalar/value evidence.
/// </summary>
internal sealed class RecurringLeaseCaptureAuthorization
{
    private readonly RecurringLeaseUseProof _consumedProof;
    private readonly RecurringLeaseExecutionSnapshot _snapshot;
    private readonly RecurringLeaseCaptureExecutionHandoffEvidence _handoffEvidence;
    private int _handoffState;

    internal RecurringLeaseCaptureAuthorization(
        RecurringLeaseUseProof consumedProof,
        RecurringLeaseExecutionSnapshot snapshot,
        DateTimeOffset validatedAtUtc)
    {
        _consumedProof = consumedProof ?? throw new ArgumentNullException(nameof(consumedProof));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (!consumedProof.IsConsumed)
            throw new InvalidOperationException("A consumed recurring proof is required.");
        if (validatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The recurring authorization time must be UTC.", nameof(validatedAtUtc));

        _handoffEvidence = new RecurringLeaseCaptureExecutionHandoffEvidence(
            consumedProof,
            snapshot,
            validatedAtUtc);
    }

    internal RecurringOccurrenceExecutionSpecification Specification => _handoffEvidence.Specification;
    internal DateTimeOffset ValidatedAtUtc => _handoffEvidence.ValidatedAtUtc;

    internal string PlanId => _handoffEvidence.PlanId;
    internal bool PlanIsOneTime => _handoffEvidence.PlanIsOneTime;
    internal bool PlanIsPeriodic => _handoffEvidence.PlanIsPeriodic;
    internal PlanDefinitionStatus PlanStatus => _handoffEvidence.PlanStatus;
    internal long PlanVersion => _handoffEvidence.PlanVersion;
    internal DateTimeOffset PlanCreatedAtUtc => _handoffEvidence.PlanCreatedAtUtc;
    internal DateTimeOffset PlanUpdatedAtUtc => _handoffEvidence.PlanUpdatedAtUtc;

    internal string OccurrenceId => _handoffEvidence.OccurrenceId;
    internal string OccurrencePlanId => _handoffEvidence.OccurrencePlanId;
    internal PlanOccurrenceStatus OccurrenceStatus => _handoffEvidence.OccurrenceStatus;
    internal string? OccurrenceRunId => _handoffEvidence.OccurrenceRunId;
    internal string? OccurrenceTerminalReasonCode => _handoffEvidence.OccurrenceTerminalReasonCode;
    internal DateTimeOffset OccurrenceWindowStartUtc => _handoffEvidence.OccurrenceWindowStartUtc;
    internal DateTimeOffset OccurrenceWindowEndUtc => _handoffEvidence.OccurrenceWindowEndUtc;
    internal DateTimeOffset OccurrenceCreatedAtUtc => _handoffEvidence.OccurrenceCreatedAtUtc;
    internal DateTimeOffset OccurrenceUpdatedAtUtc => _handoffEvidence.OccurrenceUpdatedAtUtc;
    internal long OccurrenceVersion => _handoffEvidence.OccurrenceVersion;

    internal string LeaseId => _handoffEvidence.LeaseId;
    internal string LeasePlanId => _handoffEvidence.LeasePlanId;
    internal string LeaseOccurrenceId => _handoffEvidence.LeaseOccurrenceId;
    internal ConsentLeaseStatus LeaseStatus => _handoffEvidence.LeaseStatus;
    internal DateTimeOffset LeaseValidFromUtc => _handoffEvidence.LeaseValidFromUtc;
    internal DateTimeOffset LeaseValidUntilUtc => _handoffEvidence.LeaseValidUntilUtc;
    internal DateTimeOffset LeaseAuthorizedPlanLatestEndUtc => _handoffEvidence.LeaseAuthorizedPlanLatestEndUtc;
    internal TimeSpan LeasePerRunDuration => _handoffEvidence.LeasePerRunDuration;
    internal long LeaseMaxUses => _handoffEvidence.LeaseMaxUses;
    internal TimeSpan LeaseMaxCumulativeDuration => _handoffEvidence.LeaseMaxCumulativeDuration;
    internal string LeaseAuthorizationDigest => _handoffEvidence.LeaseAuthorizationDigest;
    internal DateTimeOffset LeaseCreatedAtUtc => _handoffEvidence.LeaseCreatedAtUtc;
    internal DateTimeOffset LeaseUpdatedAtUtc => _handoffEvidence.LeaseUpdatedAtUtc;
    internal long LeaseVersion => _handoffEvidence.LeaseVersion;

    internal string UseId => _handoffEvidence.UseId;
    internal string UseLeaseId => _handoffEvidence.UseLeaseId;
    internal string UseOccurrenceId => _handoffEvidence.UseOccurrenceId;
    internal string UseRunId => _handoffEvidence.UseRunId;
    internal LeaseUseStatus UseStatus => _handoffEvidence.UseStatus;
    internal int UseReservedUseCount => _handoffEvidence.UseReservedUseCount;
    internal TimeSpan UseReservedDuration => _handoffEvidence.UseReservedDuration;
    internal TimeSpan? UseActualSettledDuration => _handoffEvidence.UseActualSettledDuration;
    internal DateTimeOffset UseCreatedAtUtc => _handoffEvidence.UseCreatedAtUtc;
    internal DateTimeOffset UseUpdatedAtUtc => _handoffEvidence.UseUpdatedAtUtc;
    internal long UseVersion => _handoffEvidence.UseVersion;
    internal bool UseIsQuotaConsumed => _handoffEvidence.UseIsQuotaConsumed;

    internal string RunId => _handoffEvidence.RunId;
    internal string RunOccurrenceId => _handoffEvidence.RunOccurrenceId;
    internal RecordingRunStatus RunStatus => _handoffEvidence.RunStatus;
    internal bool RunHasCrossedStartCommit => _handoffEvidence.RunHasCrossedStartCommit;
    internal string? RunMediaArtifactId => _handoffEvidence.RunMediaArtifactId;
    internal string? RunBundleId => _handoffEvidence.RunBundleId;
    internal string? RunTerminalReasonCode => _handoffEvidence.RunTerminalReasonCode;
    internal DateTimeOffset RunCreatedAtUtc => _handoffEvidence.RunCreatedAtUtc;
    internal DateTimeOffset RunUpdatedAtUtc => _handoffEvidence.RunUpdatedAtUtc;
    internal long RunVersion => _handoffEvidence.RunVersion;
    internal bool RunIsNonRetryable => _handoffEvidence.RunIsNonRetryable;

    internal string ProfileId => _handoffEvidence.ProfileId;
    internal DateTimeOffset? ProfileBindingBoundAtUtc => _handoffEvidence.ProfileBindingBoundAtUtc;
    internal long ProfileVersion => _handoffEvidence.ProfileVersion;
    internal string ProfileDigest => _handoffEvidence.ProfileDigest;
    internal AuthorizedScopeTargetType TargetType => _handoffEvidence.TargetType;
    internal RecurringFixedRegionRebindPolicy RebindPolicy => _handoffEvidence.RebindPolicy;
    internal AuthorizedCaptureSemantics CaptureSemantics => _handoffEvidence.CaptureSemantics;
    internal AuthorizedCoordinateSpace CoordinateSpace => _handoffEvidence.CoordinateSpace;
    internal AuthorizedDisplayIdentityStatus DisplayIdentityStatus => _handoffEvidence.DisplayIdentityStatus;
    internal AuthorizedPhysicalRectangle VirtualScreenRegion => _handoffEvidence.VirtualScreenRegion;
    internal AuthorizedPhysicalRectangle DisplayBounds => _handoffEvidence.DisplayBounds;
    internal AuthorizedPhysicalRectangle RegionWithinDisplay => _handoffEvidence.RegionWithinDisplay;
    internal string StableDisplayFingerprint => _handoffEvidence.StableDisplayFingerprint;
    internal int DpiX => _handoffEvidence.DpiX;
    internal int DpiY => _handoffEvidence.DpiY;
    internal int PhysicalWidth => _handoffEvidence.PhysicalWidth;
    internal int PhysicalHeight => _handoffEvidence.PhysicalHeight;
    internal AuthorizedDisplayOrientation Orientation => _handoffEvidence.Orientation;
    internal string TopologyDigest => _handoffEvidence.TopologyDigest;
    internal AuthorizedCaptureBackend Backend => _handoffEvidence.Backend;
    internal AuthorizedAudioMode AudioMode => _handoffEvidence.AudioMode;
    internal TimeSpan Duration => _handoffEvidence.Duration;
    internal int CountdownSeconds => _handoffEvidence.CountdownSeconds;
    internal string NormalizedOutputDirectory => _handoffEvidence.NormalizedOutputDirectory;
    internal string OutputFilePath => Specification.FrozenOutputFilePath;
    internal AuthorizedOutputConflictPolicy OutputConflictPolicy => _handoffEvidence.OutputConflictPolicy;
    internal AuthorizedWakePolicy WakePolicy => _handoffEvidence.WakePolicy;
    internal AuthorizedDesktopRequirement DesktopRequirement => _handoffEvidence.DesktopRequirement;

    internal bool IsProofConsumed => _consumedProof.IsConsumed;

    internal bool HasValidConsumedProofBinding => TryValidateForExecutionTicket(out _);

    /// <summary>
    /// Validates the private post-commit snapshot without opening a database,
    /// consulting the current environment, or changing proof state. The
    /// frozen handoff evidence is compared to the private source first so a
    /// mutation between authorization construction and handoff is rejected.
    /// </summary>
    internal bool TryValidateForExecutionTicket(out string failureReason)
    {
        failureReason = "recurring_execution_authorization_invalid";

        try
        {
            if (!SnapshotStillMatchesFrozenEvidence() ||
                ValidatedAtUtc.Offset != TimeSpan.Zero ||
                !_consumedProof.IsConsumed)
            {
                return false;
            }

            if (!RecurringLeaseExecutionGate.TryValidateDurableSnapshot(_snapshot, out _))
                return false;

            if (!RecurringLeaseExecutionGate.TryValidateRecurringProofBinding(
                    _consumedProof,
                    _snapshot,
                    requireConsumed: true,
                    out _))
            {
                return false;
            }

            var expectedExpiry = Specification.PlannedEndUtc <= LeaseValidUntilUtc
                ? Specification.PlannedEndUtc
                : LeaseValidUntilUtc;
            if (_consumedProof.IssuedAtUtc.Offset != TimeSpan.Zero ||
                _consumedProof.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                _consumedProof.IssuedAtUtc != RunUpdatedAtUtc ||
                _consumedProof.IssuedAtUtc != UseUpdatedAtUtc ||
                _consumedProof.ExpiresAtUtc != expectedExpiry ||
                ValidatedAtUtc < _consumedProof.IssuedAtUtc ||
                ValidatedAtUtc >= _consumedProof.ExpiresAtUtc)
            {
                return false;
            }

            if (!HasSupportedProfileAndSpecificationBinding())
                return false;

            failureReason = "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prepares the already-frozen evidence before the authorization CAS. The
    /// ticket constructor receives only this value object after the CAS.
    /// </summary>
    internal bool TryPrepareExecutionTicket(
        out RecurringLeaseCaptureExecutionHandoffEvidence? evidence,
        out string failureReason)
    {
        evidence = null;
        if (!TryValidateForExecutionTicket(out failureReason))
            return false;

        evidence = _handoffEvidence;
        failureReason = "";
        return true;
    }

    /// <summary>
    /// Atomically burns the authorization handoff. A failed ticket
    /// construction must never reopen this state.
    /// </summary>
    internal bool TryClaimHandoff(out string failureReason)
    {
        if (Interlocked.CompareExchange(ref _handoffState, 1, 0) != 0)
        {
            failureReason = "recurring_execution_authorization_already_handed_off";
            return false;
        }

        failureReason = "";
        return true;
    }

    private bool SnapshotStillMatchesFrozenEvidence()
    {
        var plan = _snapshot.Plan;
        var occurrence = _snapshot.Occurrence;
        var lease = _snapshot.Lease;
        var use = _snapshot.Use;
        var run = _snapshot.Run;
        var profile = _snapshot.Profile;

        return ReferenceEquals(_snapshot.Specification, _handoffEvidence.Specification) &&
            plan.Id == PlanId && plan.IsOneTime == PlanIsOneTime && plan.IsPeriodic == PlanIsPeriodic &&
            plan.Status == PlanStatus && plan.CreatedAtUtc == PlanCreatedAtUtc &&
            plan.UpdatedAtUtc == PlanUpdatedAtUtc && plan.Version == PlanVersion &&
            occurrence.Id == OccurrenceId && occurrence.PlanId == OccurrencePlanId &&
            occurrence.Status == OccurrenceStatus && occurrence.RunId == OccurrenceRunId &&
            occurrence.TerminalReasonCode == OccurrenceTerminalReasonCode &&
            occurrence.WindowStartUtc == OccurrenceWindowStartUtc && occurrence.WindowEndUtc == OccurrenceWindowEndUtc &&
            occurrence.CreatedAtUtc == OccurrenceCreatedAtUtc && occurrence.UpdatedAtUtc == OccurrenceUpdatedAtUtc &&
            occurrence.Version == OccurrenceVersion &&
            lease.LeaseId == LeaseId && lease.PlanId == LeasePlanId &&
            _snapshot.Specification.OccurrenceId == LeaseOccurrenceId &&
            lease.Status == LeaseStatus && lease.ValidFromUtc == LeaseValidFromUtc &&
            lease.ValidUntilUtc == LeaseValidUntilUtc &&
            lease.AuthorizedPlanLatestEndUtc == LeaseAuthorizedPlanLatestEndUtc &&
            lease.PerRunDuration == LeasePerRunDuration && lease.MaxUses == LeaseMaxUses &&
            lease.MaxCumulativeDuration == LeaseMaxCumulativeDuration &&
            lease.AuthorizationDigest == LeaseAuthorizationDigest &&
            lease.CreatedAtUtc == LeaseCreatedAtUtc && lease.UpdatedAtUtc == LeaseUpdatedAtUtc &&
            lease.Version == LeaseVersion &&
            use.Id == UseId && use.LeaseId == UseLeaseId && use.OccurrenceId == UseOccurrenceId &&
            use.RunId == UseRunId && use.Status == UseStatus &&
            use.ReservedUseCount == UseReservedUseCount && use.ReservedDuration == UseReservedDuration &&
            use.ActualSettledDuration == UseActualSettledDuration && use.CreatedAtUtc == UseCreatedAtUtc &&
            use.UpdatedAtUtc == UseUpdatedAtUtc && use.Version == UseVersion &&
            use.IsQuotaConsumed == UseIsQuotaConsumed &&
            run.Id == RunId && run.OccurrenceId == RunOccurrenceId && run.Status == RunStatus &&
            run.HasCrossedStartCommit == RunHasCrossedStartCommit &&
            run.MediaArtifactId == RunMediaArtifactId && run.BundleId == RunBundleId &&
            run.TerminalReasonCode == RunTerminalReasonCode && run.CreatedAtUtc == RunCreatedAtUtc &&
            run.UpdatedAtUtc == RunUpdatedAtUtc && run.Version == RunVersion &&
            run.IsNonRetryable == RunIsNonRetryable &&
            _snapshot.ProfileBindingBoundAtUtc == ProfileBindingBoundAtUtc &&
            profile.ProfileId == ProfileId && profile.ProfileVersion == ProfileVersion &&
            profile.ProfileDigest == ProfileDigest && profile.TargetType == TargetType &&
            profile.RebindPolicy == RebindPolicy && profile.CaptureSemantics == CaptureSemantics &&
            profile.CoordinateSpace == CoordinateSpace && profile.DisplayIdentityStatus == DisplayIdentityStatus &&
            profile.StableDisplayFingerprint == StableDisplayFingerprint &&
            profile.DisplayBounds == DisplayBounds && profile.RegionWithinDisplay == RegionWithinDisplay &&
            profile.VirtualScreenRegion == VirtualScreenRegion && profile.DpiX == DpiX && profile.DpiY == DpiY &&
            profile.PhysicalWidth == PhysicalWidth && profile.PhysicalHeight == PhysicalHeight &&
            profile.Orientation == Orientation && profile.TopologyDigest == TopologyDigest &&
            profile.Backend == Backend && profile.AudioMode == AudioMode && profile.Duration == Duration &&
            profile.CountdownSeconds == CountdownSeconds && profile.OutputDirectory == NormalizedOutputDirectory &&
            profile.OutputConflictPolicy == OutputConflictPolicy && profile.WakePolicy == WakePolicy &&
            profile.DesktopRequirement == DesktopRequirement;
    }

    private bool HasSupportedProfileAndSpecificationBinding()
    {
        var profile = _snapshot.Profile;
        var specification = _snapshot.Specification;

        if (!Enum.IsDefined(profile.TargetType) ||
            !Enum.IsDefined(profile.RebindPolicy) ||
            !Enum.IsDefined(profile.CaptureSemantics) ||
            !Enum.IsDefined(profile.CoordinateSpace) ||
            !Enum.IsDefined(profile.DisplayIdentityStatus) ||
            !Enum.IsDefined(profile.Backend) ||
            !Enum.IsDefined(profile.AudioMode) ||
            !Enum.IsDefined(profile.OutputConflictPolicy) ||
            !Enum.IsDefined(profile.WakePolicy) ||
            !Enum.IsDefined(profile.DesktopRequirement) ||
            profile.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            profile.RebindPolicy != RecurringFixedRegionRebindPolicy.ExactMatchOnly ||
            profile.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            profile.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            profile.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            profile.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            profile.AudioMode != AuthorizedAudioMode.None ||
            profile.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            profile.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            profile.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
        {
            return false;
        }

        if (specification.StableDisplayFingerprint != profile.StableDisplayFingerprint ||
            specification.DisplayBounds != profile.DisplayBounds ||
            specification.RegionWithinDisplay != profile.RegionWithinDisplay ||
            specification.VirtualScreenRegion != profile.VirtualScreenRegion ||
            specification.DpiX != profile.DpiX ||
            specification.DpiY != profile.DpiY ||
            specification.PhysicalWidth != profile.PhysicalWidth ||
            specification.PhysicalHeight != profile.PhysicalHeight ||
            specification.Orientation != profile.Orientation ||
            specification.TopologyDigest != profile.TopologyDigest ||
            specification.Backend != profile.Backend ||
            specification.AudioMode != profile.AudioMode ||
            specification.Duration != profile.Duration ||
            specification.CountdownSeconds != profile.CountdownSeconds ||
            specification.OutputConflictPolicy != profile.OutputConflictPolicy ||
            !string.Equals(specification.NormalizedOutputDirectory, profile.OutputDirectory, StringComparison.Ordinal) ||
            !string.Equals(
                specification.FrozenOutputFileName,
                profile.RenderOutputFileName(specification.OccurrenceIdentity, specification.ScheduledStartUtc),
                StringComparison.Ordinal) ||
            !string.Equals(
                specification.FrozenOutputFilePath,
                profile.ResolveOutputPath(specification.OccurrenceIdentity, specification.ScheduledStartUtc),
                StringComparison.Ordinal) ||
            !string.Equals(
                specification.FrozenOutputFilePath,
                Path.GetFullPath(Path.Combine(
                    specification.NormalizedOutputDirectory,
                    specification.FrozenOutputFileName)),
                StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
