using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local, one-time handoff from a committed recurring authorization
/// to a future execution bridge. The ticket owns only copied scalar/value
/// evidence, the authoritative immutable specification, and the consumed
/// proof. It never retains an authorization, snapshot, or lifecycle object.
/// </summary>
internal sealed class RecurringLeaseCaptureExecutionTicket
{
    private readonly RecurringLeaseUseProof _consumedProof;
    private readonly RecurringOccurrenceExecutionSpecification _specification;
    private readonly DateTimeOffset _validatedAtUtc;

    private readonly string _planId;
    private readonly bool _planIsOneTime;
    private readonly bool _planIsPeriodic;
    private readonly PlanDefinitionStatus _planStatus;
    private readonly DateTimeOffset _planCreatedAtUtc;
    private readonly DateTimeOffset _planUpdatedAtUtc;
    private readonly long _planVersion;

    private readonly string _occurrenceId;
    private readonly string _occurrencePlanId;
    private readonly PlanOccurrenceStatus _occurrenceStatus;
    private readonly string? _occurrenceRunId;
    private readonly string? _occurrenceTerminalReasonCode;
    private readonly DateTimeOffset _occurrenceWindowStartUtc;
    private readonly DateTimeOffset _occurrenceWindowEndUtc;
    private readonly DateTimeOffset _occurrenceCreatedAtUtc;
    private readonly DateTimeOffset _occurrenceUpdatedAtUtc;
    private readonly long _occurrenceVersion;

    private readonly string _leaseId;
    private readonly string _leasePlanId;
    private readonly string _leaseOccurrenceId;
    private readonly ConsentLeaseStatus _leaseStatus;
    private readonly DateTimeOffset _leaseValidFromUtc;
    private readonly DateTimeOffset _leaseValidUntilUtc;
    private readonly DateTimeOffset _leaseAuthorizedPlanLatestEndUtc;
    private readonly TimeSpan _leasePerRunDuration;
    private readonly long _leaseMaxUses;
    private readonly TimeSpan _leaseMaxCumulativeDuration;
    private readonly string _leaseAuthorizationDigest;
    private readonly DateTimeOffset _leaseCreatedAtUtc;
    private readonly DateTimeOffset _leaseUpdatedAtUtc;
    private readonly long _leaseVersion;

    private readonly string _useId;
    private readonly string _useLeaseId;
    private readonly string _useOccurrenceId;
    private readonly string _useRunId;
    private readonly LeaseUseStatus _useStatus;
    private readonly int _useReservedUseCount;
    private readonly TimeSpan _useReservedDuration;
    private readonly TimeSpan? _useActualSettledDuration;
    private readonly DateTimeOffset _useCreatedAtUtc;
    private readonly DateTimeOffset _useUpdatedAtUtc;
    private readonly long _useVersion;
    private readonly bool _useIsQuotaConsumed;

    private readonly string _runId;
    private readonly string _runOccurrenceId;
    private readonly RecordingRunStatus _runStatus;
    private readonly bool _runHasCrossedStartCommit;
    private readonly string? _runMediaArtifactId;
    private readonly string? _runBundleId;
    private readonly string? _runTerminalReasonCode;
    private readonly DateTimeOffset _runCreatedAtUtc;
    private readonly DateTimeOffset _runUpdatedAtUtc;
    private readonly long _runVersion;
    private readonly bool _runIsNonRetryable;

    private readonly string _profileId;
    private readonly DateTimeOffset? _profileBindingBoundAtUtc;
    private readonly long _profileVersion;
    private readonly string _profileDigest;
    private readonly AuthorizedScopeTargetType _targetType;
    private readonly RecurringFixedRegionRebindPolicy _rebindPolicy;
    private readonly AuthorizedCaptureSemantics _captureSemantics;
    private readonly AuthorizedCoordinateSpace _coordinateSpace;
    private readonly AuthorizedDisplayIdentityStatus _displayIdentityStatus;
    private readonly string _stableDisplayFingerprint;
    private readonly AuthorizedPhysicalRectangle _displayBounds;
    private readonly AuthorizedPhysicalRectangle _regionWithinDisplay;
    private readonly AuthorizedPhysicalRectangle _virtualScreenRegion;
    private readonly int _dpiX;
    private readonly int _dpiY;
    private readonly int _physicalWidth;
    private readonly int _physicalHeight;
    private readonly AuthorizedDisplayOrientation _orientation;
    private readonly string _topologyDigest;
    private readonly AuthorizedCaptureBackend _backend;
    private readonly AuthorizedAudioMode _audioMode;
    private readonly TimeSpan _duration;
    private readonly int _countdownSeconds;
    private readonly string _normalizedOutputDirectory;
    private readonly AuthorizedOutputConflictPolicy _outputConflictPolicy;
    private readonly AuthorizedWakePolicy _wakePolicy;
    private readonly AuthorizedDesktopRequirement _desktopRequirement;
    private int _claimState;

    private RecurringLeaseCaptureExecutionTicket(RecurringLeaseCaptureExecutionHandoffEvidence evidence)
    {
        _consumedProof = evidence.ConsumedProof;
        _specification = evidence.Specification;
        _validatedAtUtc = evidence.ValidatedAtUtc;

        _planId = evidence.PlanId;
        _planIsOneTime = evidence.PlanIsOneTime;
        _planIsPeriodic = evidence.PlanIsPeriodic;
        _planStatus = evidence.PlanStatus;
        _planCreatedAtUtc = evidence.PlanCreatedAtUtc;
        _planUpdatedAtUtc = evidence.PlanUpdatedAtUtc;
        _planVersion = evidence.PlanVersion;

        _occurrenceId = evidence.OccurrenceId;
        _occurrencePlanId = evidence.OccurrencePlanId;
        _occurrenceStatus = evidence.OccurrenceStatus;
        _occurrenceRunId = evidence.OccurrenceRunId;
        _occurrenceTerminalReasonCode = evidence.OccurrenceTerminalReasonCode;
        _occurrenceWindowStartUtc = evidence.OccurrenceWindowStartUtc;
        _occurrenceWindowEndUtc = evidence.OccurrenceWindowEndUtc;
        _occurrenceCreatedAtUtc = evidence.OccurrenceCreatedAtUtc;
        _occurrenceUpdatedAtUtc = evidence.OccurrenceUpdatedAtUtc;
        _occurrenceVersion = evidence.OccurrenceVersion;

        _leaseId = evidence.LeaseId;
        _leasePlanId = evidence.LeasePlanId;
        _leaseOccurrenceId = evidence.LeaseOccurrenceId;
        _leaseStatus = evidence.LeaseStatus;
        _leaseValidFromUtc = evidence.LeaseValidFromUtc;
        _leaseValidUntilUtc = evidence.LeaseValidUntilUtc;
        _leaseAuthorizedPlanLatestEndUtc = evidence.LeaseAuthorizedPlanLatestEndUtc;
        _leasePerRunDuration = evidence.LeasePerRunDuration;
        _leaseMaxUses = evidence.LeaseMaxUses;
        _leaseMaxCumulativeDuration = evidence.LeaseMaxCumulativeDuration;
        _leaseAuthorizationDigest = evidence.LeaseAuthorizationDigest;
        _leaseCreatedAtUtc = evidence.LeaseCreatedAtUtc;
        _leaseUpdatedAtUtc = evidence.LeaseUpdatedAtUtc;
        _leaseVersion = evidence.LeaseVersion;

        _useId = evidence.UseId;
        _useLeaseId = evidence.UseLeaseId;
        _useOccurrenceId = evidence.UseOccurrenceId;
        _useRunId = evidence.UseRunId;
        _useStatus = evidence.UseStatus;
        _useReservedUseCount = evidence.UseReservedUseCount;
        _useReservedDuration = evidence.UseReservedDuration;
        _useActualSettledDuration = evidence.UseActualSettledDuration;
        _useCreatedAtUtc = evidence.UseCreatedAtUtc;
        _useUpdatedAtUtc = evidence.UseUpdatedAtUtc;
        _useVersion = evidence.UseVersion;
        _useIsQuotaConsumed = evidence.UseIsQuotaConsumed;

        _runId = evidence.RunId;
        _runOccurrenceId = evidence.RunOccurrenceId;
        _runStatus = evidence.RunStatus;
        _runHasCrossedStartCommit = evidence.RunHasCrossedStartCommit;
        _runMediaArtifactId = evidence.RunMediaArtifactId;
        _runBundleId = evidence.RunBundleId;
        _runTerminalReasonCode = evidence.RunTerminalReasonCode;
        _runCreatedAtUtc = evidence.RunCreatedAtUtc;
        _runUpdatedAtUtc = evidence.RunUpdatedAtUtc;
        _runVersion = evidence.RunVersion;
        _runIsNonRetryable = evidence.RunIsNonRetryable;

        _profileId = evidence.ProfileId;
        _profileBindingBoundAtUtc = evidence.ProfileBindingBoundAtUtc;
        _profileVersion = evidence.ProfileVersion;
        _profileDigest = evidence.ProfileDigest;
        _targetType = evidence.TargetType;
        _rebindPolicy = evidence.RebindPolicy;
        _captureSemantics = evidence.CaptureSemantics;
        _coordinateSpace = evidence.CoordinateSpace;
        _displayIdentityStatus = evidence.DisplayIdentityStatus;
        _stableDisplayFingerprint = evidence.StableDisplayFingerprint;
        _displayBounds = evidence.DisplayBounds;
        _regionWithinDisplay = evidence.RegionWithinDisplay;
        _virtualScreenRegion = evidence.VirtualScreenRegion;
        _dpiX = evidence.DpiX;
        _dpiY = evidence.DpiY;
        _physicalWidth = evidence.PhysicalWidth;
        _physicalHeight = evidence.PhysicalHeight;
        _orientation = evidence.Orientation;
        _topologyDigest = evidence.TopologyDigest;
        _backend = evidence.Backend;
        _audioMode = evidence.AudioMode;
        _duration = evidence.Duration;
        _countdownSeconds = evidence.CountdownSeconds;
        _normalizedOutputDirectory = evidence.NormalizedOutputDirectory;
        _outputConflictPolicy = evidence.OutputConflictPolicy;
        _wakePolicy = evidence.WakePolicy;
        _desktopRequirement = evidence.DesktopRequirement;
    }

    internal RecurringOccurrenceExecutionSpecification Specification => _specification;
    internal DateTimeOffset ValidatedAtUtc => _validatedAtUtc;

    internal string PlanId => _planId;
    internal bool PlanIsOneTime => _planIsOneTime;
    internal bool PlanIsPeriodic => _planIsPeriodic;
    internal PlanDefinitionStatus PlanStatus => _planStatus;
    internal DateTimeOffset PlanCreatedAtUtc => _planCreatedAtUtc;
    internal DateTimeOffset PlanUpdatedAtUtc => _planUpdatedAtUtc;
    internal long PlanVersion => _planVersion;

    internal string OccurrenceId => _occurrenceId;
    internal string OccurrencePlanId => _occurrencePlanId;
    internal string OccurrenceIdentity => _specification.OccurrenceIdentity;
    internal PlanOccurrenceStatus OccurrenceStatus => _occurrenceStatus;
    internal string? OccurrenceRunId => _occurrenceRunId;
    internal string? OccurrenceTerminalReasonCode => _occurrenceTerminalReasonCode;
    internal DateTimeOffset OccurrenceWindowStartUtc => _occurrenceWindowStartUtc;
    internal DateTimeOffset OccurrenceWindowEndUtc => _occurrenceWindowEndUtc;
    internal DateTimeOffset OccurrenceCreatedAtUtc => _occurrenceCreatedAtUtc;
    internal DateTimeOffset OccurrenceUpdatedAtUtc => _occurrenceUpdatedAtUtc;
    internal long OccurrenceVersion => _occurrenceVersion;

    internal string LeaseId => _leaseId;
    internal string LeasePlanId => _leasePlanId;
    internal string LeaseOccurrenceId => _leaseOccurrenceId;
    internal ConsentLeaseStatus LeaseStatus => _leaseStatus;
    internal DateTimeOffset LeaseValidFromUtc => _leaseValidFromUtc;
    internal DateTimeOffset LeaseValidUntilUtc => _leaseValidUntilUtc;
    internal DateTimeOffset LeaseAuthorizedPlanLatestEndUtc => _leaseAuthorizedPlanLatestEndUtc;
    internal TimeSpan LeasePerRunDuration => _leasePerRunDuration;
    internal long LeaseMaxUses => _leaseMaxUses;
    internal TimeSpan LeaseMaxCumulativeDuration => _leaseMaxCumulativeDuration;
    internal string LeaseAuthorizationDigest => _leaseAuthorizationDigest;
    internal DateTimeOffset LeaseCreatedAtUtc => _leaseCreatedAtUtc;
    internal DateTimeOffset LeaseUpdatedAtUtc => _leaseUpdatedAtUtc;
    internal long LeaseVersion => _leaseVersion;

    internal string UseId => _useId;
    internal string UseLeaseId => _useLeaseId;
    internal string UseOccurrenceId => _useOccurrenceId;
    internal string UseRunId => _useRunId;
    internal LeaseUseStatus UseStatus => _useStatus;
    internal int UseReservedUseCount => _useReservedUseCount;
    internal TimeSpan UseReservedDuration => _useReservedDuration;
    internal TimeSpan? UseActualSettledDuration => _useActualSettledDuration;
    internal DateTimeOffset UseCreatedAtUtc => _useCreatedAtUtc;
    internal DateTimeOffset UseUpdatedAtUtc => _useUpdatedAtUtc;
    internal long UseVersion => _useVersion;
    internal bool UseIsQuotaConsumed => _useIsQuotaConsumed;

    internal string RunId => _runId;
    internal string RunOccurrenceId => _runOccurrenceId;
    internal RecordingRunStatus RunStatus => _runStatus;
    internal bool RunHasCrossedStartCommit => _runHasCrossedStartCommit;
    internal string? RunMediaArtifactId => _runMediaArtifactId;
    internal string? RunBundleId => _runBundleId;
    internal string? RunTerminalReasonCode => _runTerminalReasonCode;
    internal DateTimeOffset RunCreatedAtUtc => _runCreatedAtUtc;
    internal DateTimeOffset RunUpdatedAtUtc => _runUpdatedAtUtc;
    internal long RunVersion => _runVersion;
    internal bool RunIsNonRetryable => _runIsNonRetryable;

    internal string ProfileId => _profileId;
    internal DateTimeOffset? ProfileBindingBoundAtUtc => _profileBindingBoundAtUtc;
    internal long ProfileVersion => _profileVersion;
    internal string ProfileDigest => _profileDigest;
    internal AuthorizedScopeTargetType TargetType => _targetType;
    internal AuthorizedCaptureSemantics CaptureSemantics => _captureSemantics;
    internal AuthorizedCoordinateSpace CoordinateSpace => _coordinateSpace;
    internal AuthorizedDisplayIdentityStatus DisplayIdentityStatus => _displayIdentityStatus;
    internal RecurringFixedRegionRebindPolicy RebindPolicy => _rebindPolicy;
    internal AuthorizedPhysicalRectangle VirtualScreenRegion => _virtualScreenRegion;
    internal AuthorizedPhysicalRectangle DisplayBounds => _displayBounds;
    internal AuthorizedPhysicalRectangle RegionWithinDisplay => _regionWithinDisplay;
    internal string StableDisplayFingerprint => _stableDisplayFingerprint;
    internal int DpiX => _dpiX;
    internal int DpiY => _dpiY;
    internal int PhysicalWidth => _physicalWidth;
    internal int PhysicalHeight => _physicalHeight;
    internal AuthorizedDisplayOrientation Orientation => _orientation;
    internal string TopologyDigest => _topologyDigest;
    internal AuthorizedCaptureBackend Backend => _backend;
    internal AuthorizedAudioMode AudioMode => _audioMode;
    internal TimeSpan Duration => _duration;
    internal int CountdownSeconds => _countdownSeconds;
    internal string NormalizedOutputDirectory => _normalizedOutputDirectory;
    internal string FrozenOutputFileName => _specification.FrozenOutputFileName;
    internal string OutputFilePath => _specification.FrozenOutputFilePath;
    internal AuthorizedOutputConflictPolicy OutputConflictPolicy => _outputConflictPolicy;
    internal AuthorizedWakePolicy WakePolicy => _wakePolicy;
    internal AuthorizedDesktopRequirement DesktopRequirement => _desktopRequirement;

    internal bool IsProofConsumed => _consumedProof.IsConsumed;
    internal bool IsClaimed => Volatile.Read(ref _claimState) == 1;

    /// <summary>
    /// The proof is available to a later bridge only after this ticket has
    /// crossed its own execution claim. Before that point no proof object is
    /// returned from the ticket.
    /// </summary>
    internal CaptureAuthorizationProof GetConsumedProofForBackendStart()
    {
        if (Volatile.Read(ref _claimState) != 1)
            throw new InvalidOperationException("The recurring execution ticket must be claimed before proof handoff.");

        return _consumedProof;
    }

    /// <summary>
    /// Signs out at most one ticket from one post-commit authorization. All
    /// predictable validation runs before the authorization CAS; any
    /// unexpected construction failure after the CAS permanently burns it.
    /// </summary>
    internal static bool TryCreate(
        RecurringLeaseCaptureAuthorization? authorization,
        out RecurringLeaseCaptureExecutionTicket? ticket,
        out string failureReason)
    {
        ticket = null;
        failureReason = "recurring_execution_authorization_missing";
        if (authorization is null)
            return false;

        if (!authorization.TryPrepareExecutionTicket(out var evidence, out failureReason))
            return false;

        if (!authorization.TryClaimHandoff(out failureReason))
            return false;

        try
        {
            ticket = new RecurringLeaseCaptureExecutionTicket(evidence!);
            failureReason = "";
            return true;
        }
        catch
        {
            // Do not reset the authorization handoff after the CAS. A
            // construction failure is an at-most-once terminal outcome.
            ticket = null;
            failureReason = "recurring_execution_ticket_unavailable";
            return false;
        }
    }

    /// <summary>
    /// Claims this ticket exactly once for a later execution bridge. Claiming
    /// does not start a backend and cannot be reset after any bridge failure.
    /// </summary>
    internal bool TryClaim(out string failureReason)
    {
        if (Interlocked.CompareExchange(ref _claimState, 1, 0) != 0)
        {
            failureReason = "recurring_execution_already_claimed";
            return false;
        }

        failureReason = "";
        return true;
    }
}
