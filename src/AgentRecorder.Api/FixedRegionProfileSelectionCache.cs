using System.Security.Cryptography;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Api;

internal sealed record FixedRegionProfileSelectionSnapshot(
    AuthorizedPhysicalRectangle VirtualBounds,
    string StableDisplayFingerprint,
    AuthorizedPhysicalRectangle DisplayBounds,
    int DpiX,
    int DpiY,
    int PhysicalWidth,
    int PhysicalHeight,
    AuthorizedDisplayOrientation Orientation,
    string TopologyDigest,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Process-local, short-lived provenance for successful profile-purpose UI selections.</summary>
internal sealed class FixedRegionProfileSelectionCache
{
    internal const int MaximumEntries = 32;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, FixedRegionProfileSelectionSnapshot> _items = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;

    internal FixedRegionProfileSelectionCache(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    internal bool TryAdd(FixedRegionProfileSelectionSnapshot snapshot, out string reference)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            PruneExpired(_utcNow());
            if (_items.Count >= MaximumEntries)
            {
                reference = string.Empty;
                return false;
            }
            do
            {
                reference = "sel_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            } while (_items.ContainsKey(reference));
            _items.Add(reference, snapshot);
            return true;
        }
    }

    internal bool TryGet(string reference, out FixedRegionProfileSelectionSnapshot snapshot)
    {
        lock (_gate)
        {
            var now = _utcNow();
            PruneExpired(now);
            if (_items.TryGetValue(reference, out snapshot!) && snapshot.ExpiresAtUtc > now)
                return true;
            snapshot = null!;
            return false;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var key in _items.Where(item => item.Value.ExpiresAtUtc <= now).Select(item => item.Key).ToArray())
            _items.Remove(key);
    }
}
