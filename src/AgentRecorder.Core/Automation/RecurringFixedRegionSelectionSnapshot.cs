using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

/// <summary>
/// Immutable evidence produced by the trusted local recurring-region adapter.
/// The type is intentionally internal: it is not an API request, a profile
/// reference, or an authorization/approval object.
/// </summary>
internal sealed class RecurringFixedRegionSelectionSnapshot
{
    internal const string DigestPrefix = "recurring-fixed-region-selection/v1:";

    private RecurringFixedRegionSelectionSnapshot(
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
        AuthorizedDesktopRequirement desktopRequirement)
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
        SelectionDigest = RecurringFixedRegionSelectionEvidenceDigest.Compute(this);
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
    internal string SelectionDigest { get; }

    internal static RecurringFixedRegionSelectionSnapshot CreateForTrustedLocalSelectionAdapter(
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
        string topologyDigest) =>
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
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    /// <summary>
    /// Test-only malformed-input seam. Production construction remains the
    /// fixed-policy trusted-local factory above.
    /// </summary>
    internal static RecurringFixedRegionSelectionSnapshot CreateForTest(
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
        AuthorizedDesktopRequirement desktopRequirement = AuthorizedDesktopRequirement.InteractiveDesktopRequired) =>
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
            desktopRequirement);

    private static RecurringFixedRegionSelectionSnapshot CreateRaw(
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
        AuthorizedDesktopRequirement desktopRequirement) =>
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
            desktopRequirement);

}

/// <summary>
/// Canonical recurring selection-evidence digest. The trusted local snapshot
/// and persisted prepared-chain readback must use the same byte contract.
/// </summary>
internal static class RecurringFixedRegionSelectionEvidenceDigest
{
    internal static string Compute(RecurringFixedRegionSelectionSnapshot selection) =>
        Compute(
            selection.IntentId,
            selection.CurrentUserSid,
            selection.SessionBinding,
            selection.SelectedAtUtc,
            selection.StableDisplayFingerprint,
            selection.DisplayBounds,
            selection.RegionWithinDisplay,
            selection.DpiX,
            selection.DpiY,
            selection.PhysicalWidth,
            selection.PhysicalHeight,
            selection.Orientation,
            selection.TopologyDigest,
            selection.TargetType,
            selection.CaptureSemantics,
            selection.CoordinateSpace,
            selection.DisplayIdentityStatus,
            selection.Backend,
            selection.AudioMode,
            selection.OutputConflictPolicy,
            selection.WakePolicy,
            selection.DesktopRequirement);

    internal static string Compute(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset selectedAtUtc,
        RecurringFixedRegionProfileVersion profile) =>
        Compute(
            intentId,
            currentUserSid,
            sessionBinding,
            selectedAtUtc,
            profile.StableDisplayFingerprint,
            profile.DisplayBounds,
            profile.RegionWithinDisplay,
            profile.DpiX,
            profile.DpiY,
            profile.PhysicalWidth,
            profile.PhysicalHeight,
            profile.Orientation,
            profile.TopologyDigest,
            profile.TargetType,
            profile.CaptureSemantics,
            profile.CoordinateSpace,
            profile.DisplayIdentityStatus,
            profile.Backend,
            profile.AudioMode,
            profile.OutputConflictPolicy,
            profile.WakePolicy,
            profile.DesktopRequirement);

    private static string Compute(
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
        AuthorizedDesktopRequirement desktopRequirement)
    {
        var bytes = new List<byte>(512);
        AppendString(bytes, "recurring-fixed-region-selection/v1");
        AppendString(bytes, intentId);
        AppendString(bytes, currentUserSid);
        AppendString(bytes, sessionBinding);
        // The caller must pass UTC for trusted evidence. Ticks are deliberately
        // not normalized here, so an invalid non-UTC test seam cannot silently
        // become canonical evidence before the preparation boundary rejects it.
        AppendInt64(bytes, selectedAtUtc.Ticks);
        AppendString(bytes, stableDisplayFingerprint);
        AppendRectangle(bytes, displayBounds);
        AppendRectangle(bytes, regionWithinDisplay);
        AppendInt64(bytes, dpiX);
        AppendInt64(bytes, dpiY);
        AppendInt64(bytes, physicalWidth);
        AppendInt64(bytes, physicalHeight);
        AppendString(bytes, orientation.ToString());
        AppendString(bytes, topologyDigest);
        AppendString(bytes, targetType.ToString());
        AppendString(bytes, captureSemantics.ToString());
        AppendString(bytes, coordinateSpace.ToString());
        AppendString(bytes, displayIdentityStatus.ToString());
        AppendString(bytes, backend.ToString());
        AppendString(bytes, audioMode.ToString());
        AppendString(bytes, outputConflictPolicy.ToString());
        AppendString(bytes, wakePolicy.ToString());
        AppendString(bytes, desktopRequirement.ToString());
        return RecurringFixedRegionSelectionSnapshot.DigestPrefix + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void AppendRectangle(List<byte> destination, AuthorizedPhysicalRectangle value)
    {
        AppendInt64(destination, value.X);
        AppendInt64(destination, value.Y);
        AppendInt64(destination, value.Width);
        AppendInt64(destination, value.Height);
    }

    private static void AppendString(List<byte> destination, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
        AppendInt64(destination, encoded.Length);
        destination.AddRange(encoded);
    }

    private static void AppendInt64(List<byte> destination, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        destination.AddRange(buffer.ToArray());
    }
}
