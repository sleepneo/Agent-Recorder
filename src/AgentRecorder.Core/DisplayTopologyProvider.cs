using System;
using System.Collections.Generic;
using System.Linq;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Privacy-safe snapshot of one display's public ordinal, internal stable
/// fingerprint, resolution status, and physical bounds. No pixels, raw device
/// paths, or native handles are part of the revalidation contract.
/// </summary>
public readonly record struct DisplayTopologySnapshot(
    string PublicId,
    string? StableIdentity,
    DisplayIdentityResolutionStatus IdentityStatus,
    CapturePlanBounds Bounds);

/// <summary>
/// Supplies the current connected-display topology at the confirmation barrier.
/// Implementations must enumerate metadata only.
/// </summary>
public interface IDisplayTopologyProvider
{
    IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays();
}

/// <summary>
/// Production topology provider. The injected SystemQuery seam remains below
/// this boundary so tests can inject this provider without changing global
/// display enumeration state.
/// </summary>
public sealed class SystemQueryDisplayTopologyProvider : IDisplayTopologyProvider
{
    public static readonly SystemQueryDisplayTopologyProvider Instance = new();

    private SystemQueryDisplayTopologyProvider() { }

    public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays()
        => SystemQuery.EnumDisplayTopology()
            .Select(display => new DisplayTopologySnapshot(
                display.id,
                display.identity_status == DisplayIdentityResolutionStatus.Resolved
                    ? display.stable_identity
                    : null,
                display.identity_status,
                new CapturePlanBounds(
                    display.bounds.x,
                    display.bounds.y,
                    display.bounds.width,
                    display.bounds.height)))
            .ToArray();

    internal IReadOnlyList<StandingLeaseDisplayMetadata> GetCurrentExecutionMetadata()
        => SystemQuery.EnumDisplayTopologyMetadata()
            .Select(display => new StandingLeaseDisplayMetadata(
                display.Id,
                display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved
                    ? display.StableIdentity
                    : null,
                display.IdentityStatus,
                TryCreateBounds(display.Bounds),
                display.DpiX,
                display.DpiY,
                display.PhysicalWidth,
                display.PhysicalHeight,
                ResolveOrientation(display.Bounds, display.Rotation)))
            .ToArray();

    private static AuthorizedPhysicalRectangle? TryCreateBounds(SystemQuery.Bounds bounds)
    {
        try
        {
            return new AuthorizedPhysicalRectangle(bounds.x, bounds.y, bounds.width, bounds.height);
        }
        catch
        {
            return null;
        }
    }

    private static AuthorizedDisplayOrientation? ResolveOrientation(
        SystemQuery.Bounds bounds,
        uint? rotation)
    {
        // DISPLAYCONFIG_ROTATION identity/90/180/270 values. A missing or
        // unknown rotation is intentionally not guessed from an ordinal.
        if (rotation is null || bounds.width <= 0 || bounds.height <= 0)
            return null;

        bool portrait = bounds.height > bounds.width;
        return rotation.Value switch
        {
            1 when portrait => AuthorizedDisplayOrientation.Portrait,
            1 when bounds.width > bounds.height => AuthorizedDisplayOrientation.Landscape,
            2 => AuthorizedDisplayOrientation.Portrait,
            3 when portrait => AuthorizedDisplayOrientation.PortraitFlipped,
            3 when bounds.width > bounds.height => AuthorizedDisplayOrientation.LandscapeFlipped,
            4 => AuthorizedDisplayOrientation.PortraitFlipped,
            _ => null,
        };
    }
}
