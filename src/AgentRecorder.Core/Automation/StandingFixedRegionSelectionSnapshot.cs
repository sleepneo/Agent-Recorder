namespace AgentRecorder.Core.Automation;

/// <summary>
/// Internal trusted-local selection result. It contains only the physical
/// display selection needed to prepare a pending authorization scope; it is not
/// an approval receipt, proof, capture configuration, or screen payload.
/// </summary>
internal sealed class StandingFixedRegionSelectionSnapshot
{
    private StandingFixedRegionSelectionSnapshot(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset selectedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedScopeTargetType targetType,
        AuthorizedCaptureSemantics captureSemantics,
        AuthorizedCoordinateSpace coordinateSpace,
        AuthorizedDisplayIdentityStatus displayIdentityStatus,
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement,
        string? planId,
        string? occurrenceId,
        string? leaseId,
        string? scopeId)
    {
        IntentId = intentId;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        SelectedAtUtc = selectedAtUtc;
        StableDisplayFingerprint = stableDisplayFingerprint;
        DisplayBounds = displayBounds;
        RegionWithinDisplay = regionWithinDisplay;
        DpiX = dpiX;
        DpiY = dpiY;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        Orientation = orientation;
        TopologyDigest = topologyDigest;
        TargetType = targetType;
        CaptureSemantics = captureSemantics;
        CoordinateSpace = coordinateSpace;
        DisplayIdentityStatus = displayIdentityStatus;
        Backend = backend;
        AudioMode = audioMode;
        OutputConflictPolicy = outputConflictPolicy;
        WakePolicy = wakePolicy;
        DesktopRequirement = desktopRequirement;
        PlanId = planId;
        OccurrenceId = occurrenceId;
        LeaseId = leaseId;
        ScopeId = scopeId;
    }

    internal string IntentId { get; }
    internal string CurrentUserSid { get; }
    internal string SessionBinding { get; }
    internal DateTimeOffset SelectedAtUtc { get; }
    internal string StableDisplayFingerprint { get; }
    internal AuthorizedPhysicalRectangle DisplayBounds { get; }
    internal AuthorizedPhysicalRectangle RegionWithinDisplay { get; }
    internal int DpiX { get; }
    internal int DpiY { get; }
    internal int PhysicalWidth { get; }
    internal int PhysicalHeight { get; }
    internal AuthorizedDisplayOrientation Orientation { get; }
    internal string TopologyDigest { get; }
    internal AuthorizedScopeTargetType TargetType { get; }
    internal AuthorizedCaptureSemantics CaptureSemantics { get; }
    internal AuthorizedCoordinateSpace CoordinateSpace { get; }
    internal AuthorizedDisplayIdentityStatus DisplayIdentityStatus { get; }
    internal AuthorizedCaptureBackend Backend { get; }
    internal AuthorizedAudioMode AudioMode { get; }
    internal AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }
    internal AuthorizedWakePolicy WakePolicy { get; }
    internal AuthorizedDesktopRequirement DesktopRequirement { get; }
    internal string? PlanId { get; }
    internal string? OccurrenceId { get; }
    internal string? LeaseId { get; }
    internal string? ScopeId { get; }

    internal static StandingFixedRegionSelectionSnapshot CreateForTrustedLocalSelectionAdapter(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset selectedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        string? planId = null,
        string? occurrenceId = null,
        string? leaseId = null,
        string? scopeId = null) =>
        CreateRaw(
            intentId,
            currentUserSid,
            sessionBinding,
            selectedAtUtc,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            topologyDigest,
            AuthorizedScopeTargetType.FixedRegion,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            AuthorizedCaptureBackend.FfmpegRegion,
            AuthorizedAudioMode.None,
            AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired,
            planId,
            occurrenceId,
            leaseId,
            scopeId);

    /// <summary>
    /// Test-only seam. It allows malformed policies and identity values to be
    /// passed to the preparation service so it can fail closed.
    /// </summary>
    internal static StandingFixedRegionSelectionSnapshot CreateForTest(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset selectedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedScopeTargetType targetType = AuthorizedScopeTargetType.FixedRegion,
        AuthorizedCaptureSemantics captureSemantics = AuthorizedCaptureSemantics.DesktopRegion,
        AuthorizedCoordinateSpace coordinateSpace = AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        AuthorizedDisplayIdentityStatus displayIdentityStatus = AuthorizedDisplayIdentityStatus.Resolved,
        AuthorizedCaptureBackend backend = AuthorizedCaptureBackend.FfmpegRegion,
        AuthorizedAudioMode audioMode = AuthorizedAudioMode.None,
        AuthorizedOutputConflictPolicy outputConflictPolicy = AuthorizedOutputConflictPolicy.FailIfExists,
        AuthorizedWakePolicy wakePolicy = AuthorizedWakePolicy.NaturalWakeOnly,
        AuthorizedDesktopRequirement desktopRequirement = AuthorizedDesktopRequirement.InteractiveDesktopRequired,
        string? planId = null,
        string? occurrenceId = null,
        string? leaseId = null,
        string? scopeId = null) =>
        CreateRaw(
            intentId,
            currentUserSid,
            sessionBinding,
            selectedAtUtc,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            topologyDigest,
            targetType,
            captureSemantics,
            coordinateSpace,
            displayIdentityStatus,
            backend,
            audioMode,
            outputConflictPolicy,
            wakePolicy,
            desktopRequirement,
            planId,
            occurrenceId,
            leaseId,
            scopeId);

    private static StandingFixedRegionSelectionSnapshot CreateRaw(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset selectedAtUtc,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedScopeTargetType targetType,
        AuthorizedCaptureSemantics captureSemantics,
        AuthorizedCoordinateSpace coordinateSpace,
        AuthorizedDisplayIdentityStatus displayIdentityStatus,
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement,
        string? planId,
        string? occurrenceId,
        string? leaseId,
        string? scopeId) =>
        new(
            intentId,
            currentUserSid,
            sessionBinding,
            selectedAtUtc,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            topologyDigest,
            targetType,
            captureSemantics,
            coordinateSpace,
            displayIdentityStatus,
            backend,
            audioMode,
            outputConflictPolicy,
            wakePolicy,
            desktopRequirement,
            planId,
            occurrenceId,
            leaseId,
            scopeId);
}
