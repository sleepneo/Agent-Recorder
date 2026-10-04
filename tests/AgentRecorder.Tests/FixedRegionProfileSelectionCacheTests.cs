using AgentRecorder.Api;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FixedRegionProfileSelectionCacheTests
{
    [Fact]
    public void CacheBoundsLiveReferencesAndNeverEvictsAnUnexpiredEntry()
    {
        var now = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
        var cache = new FixedRegionProfileSelectionCache(() => now);
        var references = new List<string>();
        for (var index = 0; index < FixedRegionProfileSelectionCache.MaximumEntries; index++)
        {
            Assert.True(cache.TryAdd(Snapshot(now.Add(FixedRegionProfileSelectionCache.Lifetime)), out var reference));
            references.Add(reference);
        }

        Assert.False(cache.TryAdd(Snapshot(now.Add(FixedRegionProfileSelectionCache.Lifetime)), out _));
        Assert.All(references, reference => Assert.True(cache.TryGet(reference, out _)));

        now = now.Add(FixedRegionProfileSelectionCache.Lifetime);
        Assert.All(references, reference => Assert.False(cache.TryGet(reference, out _)));
        Assert.True(cache.TryAdd(Snapshot(now.Add(FixedRegionProfileSelectionCache.Lifetime)), out var replacement));
        Assert.True(cache.TryGet(replacement, out _));
    }

    [Fact]
    public void ConcurrentInsertionsStayWithinTheBoundAndReferencesAreUnique()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new FixedRegionProfileSelectionCache(() => now);
        var accepted = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 128, _ =>
        {
            if (cache.TryAdd(Snapshot(now.AddMinutes(5)), out var reference)) accepted.Add(reference);
        });
        Assert.Equal(FixedRegionProfileSelectionCache.MaximumEntries, accepted.Count);
        Assert.Equal(accepted.Count, accepted.Distinct(StringComparer.Ordinal).Count());
        Assert.All(accepted, reference => Assert.True(cache.TryGet(reference, out _)));
    }

    private static FixedRegionProfileSelectionSnapshot Snapshot(DateTimeOffset expiresAt) => new(
        new AuthorizedPhysicalRectangle(-90, 70, 640, 480), "stable-display",
        new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080), 96, 96, 1920, 1080,
        AuthorizedDisplayOrientation.Landscape, new string('a', 64), expiresAt);
}
