using System;
using System.Collections.Generic;
using System.Linq;
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
}
