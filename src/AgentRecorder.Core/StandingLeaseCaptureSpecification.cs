using System.IO;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// The only countdown form currently authorized by the standing fixed-region
/// vertical slice. It is a specification value, not a timer or scheduler.
/// </summary>
internal enum StandingLeaseCountdownStrategy
{
    FixedSeconds,
}

/// <summary>
/// Immutable, internal input contract for a future standing fixed-region
/// execution bridge. This type deliberately contains no CaptureConfig and no
/// backend, audio, window, screenshot, or native-handle construction surface.
/// </summary>
internal sealed class StandingLeaseCaptureSpecification
{
    internal const string BackendCode = "ffmpeg-region";
    internal const string AudioCode = "none";
    internal const string CoordinateSpaceCode = "physical_virtual_screen";
    internal const string CountdownStrategyCode = "fixed_seconds";
    internal const string WakePolicyCode = "natural_wake_only";
    internal const string DesktopRequirementCode = "interactive_desktop_required";

    private StandingLeaseCaptureSpecification(
        string runId,
        string leaseId,
        string leaseUseId,
        AuthorizedFixedRegionScope scope,
        AuthorizedPhysicalRectangle virtualScreenRegion,
        Guid specificationBindingId)
    {
        _specificationBindingId = specificationBindingId;
        RunId = runId;
        LeaseId = leaseId;
        LeaseUseId = leaseUseId;
        ScopeDigest = scope.ScopeDigest;
        TargetType = scope.TargetType;
        CaptureSemantics = scope.CaptureSemantics;
        CoordinateSpace = scope.CoordinateSpace;
        VirtualScreenRegion = virtualScreenRegion;
        StableDisplayFingerprint = scope.StableDisplayFingerprint;
        DisplayBounds = scope.DisplayBounds;
        DpiX = scope.DpiX;
        DpiY = scope.DpiY;
        PhysicalWidth = scope.PhysicalWidth;
        PhysicalHeight = scope.PhysicalHeight;
        Orientation = scope.Orientation;
        TopologyDigest = scope.TopologyDigest;
        Backend = scope.Backend;
        BackendCodeValue = BackendCode;
        AudioMode = scope.AudioMode;
        AudioCodeValue = AudioCode;
        MaximumDuration = scope.ReservedDuration;
        CountdownStrategy = StandingLeaseCountdownStrategy.FixedSeconds;
        CountdownStrategyCodeValue = CountdownStrategyCode;
        CountdownSeconds = scope.CountdownSeconds;
        OutputDirectory = scope.OutputDirectory;
        FrozenFileName = scope.FrozenFileName;
        OutputFilePath = scope.OutputFilePath;
        OutputConflictPolicy = scope.OutputConflictPolicy;
        WakePolicy = scope.WakePolicy;
        WakePolicyCodeValue = WakePolicyCode;
        DesktopRequirement = scope.DesktopRequirement;
        DesktopRequirementCodeValue = DesktopRequirementCode;
    }

    private readonly Guid _specificationBindingId;

    internal string RunId { get; }

    internal string LeaseId { get; }

    internal string LeaseUseId { get; }

    internal string ScopeDigest { get; }

    internal AuthorizedScopeTargetType TargetType { get; }

    internal AuthorizedCaptureSemantics CaptureSemantics { get; }

    internal AuthorizedCoordinateSpace CoordinateSpace { get; }

    internal AuthorizedPhysicalRectangle VirtualScreenRegion { get; }

    internal string StableDisplayFingerprint { get; }

    internal AuthorizedPhysicalRectangle DisplayBounds { get; }

    internal int DpiX { get; }

    internal int DpiY { get; }

    internal int PhysicalWidth { get; }

    internal int PhysicalHeight { get; }

    internal AuthorizedDisplayOrientation Orientation { get; }

    internal string TopologyDigest { get; }

    internal AuthorizedCaptureBackend Backend { get; }

    internal string BackendCodeValue { get; }

    internal AuthorizedAudioMode AudioMode { get; }

    internal string AudioCodeValue { get; }

    internal TimeSpan MaximumDuration { get; }

    internal StandingLeaseCountdownStrategy CountdownStrategy { get; }

    internal string CountdownStrategyCodeValue { get; }

    internal int CountdownSeconds { get; }

    internal string OutputDirectory { get; }

    internal string FrozenFileName { get; }

    internal string OutputFilePath { get; }

    internal AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }

    internal AuthorizedWakePolicy WakePolicy { get; }

    internal string WakePolicyCodeValue { get; }

    internal AuthorizedDesktopRequirement DesktopRequirement { get; }

    internal string DesktopRequirementCodeValue { get; }

    internal bool MatchesAuthorization(StandingLeaseCaptureAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var scope = authorization.Scope;
        return _specificationBindingId == authorization.SpecificationBindingId &&
            string.Equals(RunId, authorization.RunId, StringComparison.Ordinal) &&
            string.Equals(LeaseId, authorization.LeaseId, StringComparison.Ordinal) &&
            string.Equals(LeaseUseId, authorization.LeaseUseId, StringComparison.Ordinal) &&
            string.Equals(ScopeDigest, scope.ScopeDigest, StringComparison.Ordinal) &&
            TargetType == scope.TargetType &&
            CaptureSemantics == scope.CaptureSemantics &&
            CoordinateSpace == scope.CoordinateSpace &&
            VirtualScreenRegion == scope.VirtualScreenRegion &&
            string.Equals(StableDisplayFingerprint, scope.StableDisplayFingerprint, StringComparison.Ordinal) &&
            DisplayBounds == scope.DisplayBounds &&
            DpiX == scope.DpiX &&
            DpiY == scope.DpiY &&
            PhysicalWidth == scope.PhysicalWidth &&
            PhysicalHeight == scope.PhysicalHeight &&
            Orientation == scope.Orientation &&
            string.Equals(TopologyDigest, scope.TopologyDigest, StringComparison.Ordinal) &&
            Backend == scope.Backend &&
            AudioMode == scope.AudioMode &&
            MaximumDuration == scope.ReservedDuration &&
            CountdownSeconds == scope.CountdownSeconds &&
            string.Equals(OutputDirectory, scope.OutputDirectory, StringComparison.Ordinal) &&
            string.Equals(FrozenFileName, scope.FrozenFileName, StringComparison.Ordinal) &&
            string.Equals(OutputFilePath, scope.OutputFilePath, StringComparison.Ordinal) &&
            OutputConflictPolicy == scope.OutputConflictPolicy &&
            WakePolicy == scope.WakePolicy &&
            DesktopRequirement == scope.DesktopRequirement;
    }

    /// <summary>
    /// Projects only an already-consumed and already-committed authorization.
    /// No caller-supplied capture configuration is accepted at this boundary.
    /// </summary>
    internal static bool TryCreate(
        StandingLeaseCaptureAuthorization? authorization,
        out StandingLeaseCaptureSpecification? specification,
        out string failureReason)
    {
        specification = null;
        failureReason = "standing_specification_authorization_missing";
        if (authorization is null)
            return false;

        if (!authorization.IsProofConsumed)
        {
            failureReason = "standing_specification_proof_not_consumed";
            return false;
        }

        if (!authorization.HasValidConsumedProofBinding)
        {
            failureReason = "standing_specification_authorization_incomplete";
            return false;
        }

        try
        {
            var scope = authorization.Scope;
            if (scope is null ||
                string.IsNullOrWhiteSpace(authorization.RunId) ||
                string.IsNullOrWhiteSpace(authorization.LeaseId) ||
                string.IsNullOrWhiteSpace(authorization.LeaseUseId) ||
                !string.Equals(authorization.LeaseId, scope.LeaseId, StringComparison.Ordinal) ||
                authorization.ConsumedAtUtc.Offset != TimeSpan.Zero)
            {
                failureReason = "standing_specification_authorization_incomplete";
                return false;
            }

            if (scope.TargetType != AuthorizedScopeTargetType.FixedRegion ||
                scope.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
                scope.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
                scope.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
                string.IsNullOrWhiteSpace(scope.StableDisplayFingerprint) ||
                scope.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
                scope.AudioMode != AuthorizedAudioMode.None ||
                scope.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
                scope.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
                scope.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
            {
                failureReason = "standing_specification_policy_not_supported";
                return false;
            }

            if (string.IsNullOrWhiteSpace(scope.ScopeDigest) ||
                string.IsNullOrWhiteSpace(scope.TopologyDigest) ||
                scope.DpiX <= 0 ||
                scope.DpiY <= 0 ||
                scope.PhysicalWidth <= 0 ||
                scope.PhysicalHeight <= 0 ||
                scope.PhysicalWidth != scope.DisplayBounds.Width ||
                scope.PhysicalHeight != scope.DisplayBounds.Height ||
                scope.ReservedDuration <= TimeSpan.Zero ||
                scope.ReservedDuration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
                scope.CountdownSeconds < 0)
            {
                failureReason = "standing_specification_scope_invalid";
                return false;
            }

            var region = scope.RegionWithinDisplay;
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
            {
                failureReason = "standing_specification_region_invalid";
                return false;
            }

            int regionRight;
            int regionBottom;
            try
            {
                regionRight = checked(region.X + region.Width);
                regionBottom = checked(region.Y + region.Height);
            }
            catch (OverflowException)
            {
                failureReason = "standing_specification_region_overflow";
                return false;
            }

            if (regionRight > scope.DisplayBounds.Width ||
                regionBottom > scope.DisplayBounds.Height)
            {
                failureReason = "standing_specification_region_invalid";
                return false;
            }

            // Read the domain projection once, then validate that exact value.
            AuthorizedPhysicalRectangle virtualScreenRegion;
            try
            {
                virtualScreenRegion = scope.VirtualScreenRegion;
            }
            catch (Phase3DomainException)
            {
                failureReason = "standing_specification_region_overflow";
                return false;
            }

            AuthorizedPhysicalRectangle expectedRegion;
            try
            {
                expectedRegion = new AuthorizedPhysicalRectangle(
                    checked(scope.DisplayBounds.X + region.X),
                    checked(scope.DisplayBounds.Y + region.Y),
                    region.Width,
                    region.Height);
            }
            catch (OverflowException)
            {
                failureReason = "standing_specification_region_overflow";
                return false;
            }

            if (virtualScreenRegion != expectedRegion ||
                virtualScreenRegion.Width <= 0 ||
                virtualScreenRegion.Height <= 0)
            {
                failureReason = "standing_specification_region_invalid";
                return false;
            }

            string normalizedDirectory;
            string finalPath;
            try
            {
                normalizedDirectory = StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory);
                finalPath = Path.GetFullPath(Path.Combine(normalizedDirectory, scope.FrozenFileName));
            }
            catch
            {
                failureReason = "standing_specification_output_path_invalid";
                return false;
            }

            if (!string.Equals(scope.OutputDirectory, normalizedDirectory, StringComparison.Ordinal) ||
                !string.Equals(scope.OutputFilePath, finalPath, StringComparison.Ordinal) ||
                !string.Equals(authorization.OutputFilePath, finalPath, StringComparison.Ordinal))
            {
                failureReason = "standing_specification_output_path_mismatch";
                return false;
            }

            specification = new StandingLeaseCaptureSpecification(
                authorization.RunId,
                authorization.LeaseId,
                authorization.LeaseUseId,
                scope,
                virtualScreenRegion,
                authorization.SpecificationBindingId);
            failureReason = "";
            return true;
        }
        catch (Phase3DomainException)
        {
            specification = null;
            failureReason = "standing_specification_scope_invalid";
            return false;
        }
        catch (Exception)
        {
            specification = null;
            failureReason = "standing_specification_invalid";
            return false;
        }
    }
}
