using System.IO;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Immutable, metadata-only requirements for one fixed-region execution
/// environment. The requirements are shared by one-shot authorization scopes
/// and recurring profiles; they contain no proof or capture state.
/// </summary>
internal sealed class FixedRegionExecutionEnvironmentRequirements
{
    internal FixedRegionExecutionEnvironmentRequirements(
        string currentUserSid,
        string sessionBinding,
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
        string normalizedOutputDirectory,
        string frozenOutputFilePath,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        TimeSpan reservedDuration)
    {
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
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
        NormalizedOutputDirectory = normalizedOutputDirectory;
        FrozenOutputFilePath = frozenOutputFilePath;
        OutputConflictPolicy = outputConflictPolicy;
        ReservedDuration = reservedDuration;
    }

    internal string CurrentUserSid { get; }

    internal string SessionBinding { get; }

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

    internal string NormalizedOutputDirectory { get; }

    internal string FrozenOutputFilePath { get; }

    internal AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }

    internal TimeSpan ReservedDuration { get; }

    internal static FixedRegionExecutionEnvironmentRequirements FromScope(
        AuthorizedFixedRegionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(
            scope.CurrentUserSid,
            scope.SessionBinding,
            scope.StableDisplayFingerprint,
            scope.DisplayBounds,
            scope.RegionWithinDisplay,
            scope.VirtualScreenRegion,
            scope.DpiX,
            scope.DpiY,
            scope.PhysicalWidth,
            scope.PhysicalHeight,
            scope.Orientation,
            scope.TopologyDigest,
            StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory),
            Path.GetFullPath(scope.OutputFilePath),
            scope.OutputConflictPolicy,
            scope.ReservedDuration);
    }

    internal static FixedRegionExecutionEnvironmentRequirements FromRecurringSpecification(
        RecurringOccurrenceExecutionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return new(
            specification.ApprovedCurrentUserSid,
            specification.ApprovedSessionBinding,
            specification.StableDisplayFingerprint,
            specification.DisplayBounds,
            specification.RegionWithinDisplay,
            specification.VirtualScreenRegion,
            specification.DpiX,
            specification.DpiY,
            specification.PhysicalWidth,
            specification.PhysicalHeight,
            specification.Orientation,
            specification.TopologyDigest,
            specification.NormalizedOutputDirectory,
            specification.FrozenOutputFilePath,
            specification.OutputConflictPolicy,
            specification.Duration);
    }
}
