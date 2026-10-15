using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.OriginalFileScopeFixture;

namespace AgentRecorder.Tests;

public sealed class OriginalFileScopeContractTests
{
    public static TheoryData<string> InvalidPaths => OriginalFileHoldContractTests.InvalidPaths;
    [Theory, MemberData(nameof(InvalidPaths))]
    public async Task LexicalRejectionUsesNoIo(string path)
    {
        var f = new OriginalFileScopeFixture(); var result = await f.Service().AcquireAsync(f.Anchor with { Path = path });
        Assert.Equal(OriginalFileHoldReason.InvalidInput, result.Reason); Assert.Null(result.Owner);
        Assert.Empty(f.Api.Calls); Assert.True((await result.Retirement).Retired); Assert.Empty((await result.Retirement).Resources);
    }
    [Theory]
    [InlineData("C:\\CON\\Videos")][InlineData("C:\\LPT¹\\Videos")][InlineData("C:\\Videos\\")]
    [InlineData("C:\\")][InlineData("\\\\?\\C:\\Videos")][InlineData("C:\\host\\Else")]
    public async Task BadFrozenRootsDoNotRelaxDriveRootLeafRules(string root)
    {
        var f = new OriginalFileScopeFixture { Root = root }; f.Anchor = f.Anchor with { Path = root + @"\视频.mp4" };
        Assert.Equal(OriginalFileHoldReason.InvalidInput, (await f.Service().AcquireAsync(f.Anchor)).Reason); Assert.Empty(f.Api.Calls);
    }
    [Theory]
    [InlineData(0)][InlineData(30)][InlineData(31)]
    public async Task AncestorCountIncludesDriveRootAndUnicodeBoundary(int components)
    {
        var f = new OriginalFileScopeFixture { Root = @"C:\" + string.Join("\\", Enumerable.Repeat("工作", components).Append("Videos")) };
        f.Anchor = f.Anchor with { Path = f.Root + @"\中文-COM中.mp4" };
        var held = await f.Service().AcquireAsync(f.Anchor);
        if (components == 31) { Assert.Equal(OriginalFileHoldReason.InvalidInput, held.Reason); Assert.Empty(f.Api.Calls); return; }
        Assert.Equal(OriginalFileHoldReason.Held, held.Reason); Assert.Equal(components + 3, f.Api.Opened.Count);
        await Retire(held.Owner!); AssertClosedInReverse(f.Api);
    }
    [Fact]
    public async Task OneOrderedOpenPerObjectExactParametersSameHandlesAndCombinedOwner()
    {
        var f = new OriginalFileScopeFixture(); var held = await f.Service().AcquireAsync(f.Anchor); Assert.NotNull(held.Owner);
        Assert.Equal(new[] { @"C:\", @"C:\host", f.Root, f.Anchor.Path }, f.Api.Opened.Select(n => n.Path));
        var args = f.Api.Parameters.ToArray(); Assert.Equal(4, args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            Assert.Equal(@"\\?\" + f.Api.Opened.ElementAt(i).Path, args[i].Path); Assert.Equal(3u, args[i].Creation);
            Assert.Equal(i == 3 ? 0x10080u : 0x81u, args[i].Access); Assert.Equal(i == 3 ? 0u : 3u, args[i].Share);
            Assert.Equal(i == 3 ? 0x200000u : 0x2200000u, args[i].Flags);
        }
        Assert.All(f.Api.Opened, n => Assert.False(n.Handle.IsClosed)); Assert.False(held.Retirement.IsCompleted);
        var result = await held.Owner!.ObserveAsync(); Assert.Equal(OriginalFileHoldReason.Observed, result.Reason);
        Assert.Equal(f.Anchor.Identity, result.Snapshot!.Identity); Assert.Equal(4, f.Api.Opened.Count);
        foreach (var c in f.Api.Calls.Where(c => c.Handle is not null)) Assert.Same(f.Api.Opened.ElementAt(c.Index).Handle, c.Handle);
        // Full chain is verified after the leaf Open, before its metadata, and
        // again after leaf verification while every handle remains live.
        var calls = f.Api.Calls.ToArray(); var leafOpen = Array.FindIndex(calls, c => c.Stage == "open" && c.Index == 3);
        Assert.Equal(new[] { 0, 1, 2 }, calls.Skip(leafOpen + 1).Where(c => c.Stage == "basic").Take(3).Select(c => c.Index));
        await Retire(held.Owner); AssertClosedInReverse(f.Api);
        Assert.Equal(new[] { 3, 2, 1, 0 }, (await held.Retirement).Resources.Select(x => x.Leaf ? 3 : x.DirectoryIndex));
        var count = f.Api.Calls.Count; held.Owner.Dispose(); Assert.Equal(OriginalFileHoldReason.Disposed, (await held.Owner.ObserveAsync()).Reason);
        Assert.Equal(count, f.Api.Calls.Count);
    }
    public static IEnumerable<object[]> FailureStages => from layer in Enumerable.Range(0, 4)
        from stage in new[] { "open", "basic", "standard", "identity", "path" }
        from throwing in new[] { false, true } select new object[] { layer, stage, throwing };
    [Theory, MemberData(nameof(FailureStages))]
    public async Task EveryLayerNativeFailureCleansPartialObjectsWithoutFollowingReplacement(int layer, string stage, bool throwing)
    {
        var f = new OriginalFileScopeFixture(); f.Api.Fail = c => c.Index == layer && c.Stage == stage;
        if (throwing) f.Api.Before = c => { if (c.Index == layer && c.Stage == stage) throw new IOException("owned-fault"); };
        var service = f.Service(); var failed = await service.AcquireAsync(f.Anchor); Assert.Null(failed.Owner);
        var retired = await failed.Retirement; Assert.Equal(throwing && stage == "open" ? false : true, retired.Retired);
        AssertClosedInReverse(f.Api); Assert.True(f.Api.Opened.Count <= layer + 1);
        if (throwing && stage == "open") { Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); Assert.Contains(retired.Resources, x => x.Outcome == OriginalFileCloseOutcome.Unknown); }
        else { f.Api.Fail = null; f.Api.Before = null; var next = await service.AcquireAsync(f.Anchor); await Retire(next.Owner!); }
    }
    public static IEnumerable<object[]> UnsafeDirectories => from layer in Enumerable.Range(0, 3)
        from fault in new[] { "file", "reparse", "attributes", "delete", "directory_flag", "bad_directory_flag", "zero_volume", "zero_id", "cross_volume", "path", "device_path", "ads", "negative_length", "negative_allocation" }
        select new object[] { layer, fault };
    private static void Mutate(OriginalFileScopeFixture f, int layer, string fault, bool later = false)
    {
        bool At(Node n, int round) => n.Index == layer && (!later || round > 1);
        f.Api.BasicValue = (n, i, v) => { if (At(n, i)) v.Attributes = fault switch { "file" => (uint)FileAttributes.Normal, "reparse" => (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint), "attributes" => 0, _ => v.Attributes }; return v; };
        f.Api.StandardValue = (n, i, v) => { if (At(n, i)) { if (fault == "delete") v.DeletePending = 1; if (fault == "directory_flag") v.Directory = 0;
            if (fault == "bad_directory_flag") v.Directory = 2; if (fault == "negative_length") v.EndOfFile = -1; if (fault == "negative_allocation") v.AllocationSize = -1; } return v; };
        f.Api.IdentityValue = (n, i, v) => { if (At(n, i)) { if (fault == "zero_volume") v.VolumeSerialNumber = 0; if (fault == "cross_volume") v.VolumeSerialNumber++;
            if (fault == "zero_id") { v.IdLow = 0; v.IdHigh = 0; } if (fault == "replacement") v.IdHigh++; } return v; };
        f.Api.PathValue = (n, i, v) => At(n, i) ? fault switch { "path" => @"\\?\C:\escaped", "device_path" => @"\Device\volume", "ads" => v + ":other", _ => v } : v;
    }
    [Theory, MemberData(nameof(UnsafeDirectories))]
    public async Task DirectoryFactsAreFromHandlesAndRejectUnknownStructureVolumeOrPath(int layer, string fault)
    {
        var f = new OriginalFileScopeFixture(); Mutate(f, layer, fault); var failed = await f.Service().AcquireAsync(f.Anchor);
        Assert.Null(failed.Owner); Assert.True((await failed.Retirement).Retired); Assert.Equal(layer + 1, f.Api.Opened.Count); AssertClosedInReverse(f.Api);
    }
    public static IEnumerable<object[]> RecheckFailures => from layer in Enumerable.Range(0, 3)
        from stage in new[] { "basic", "standard", "identity", "path" } from throwing in new[] { false, true }
        select new object[] { layer, stage, throwing };
    [Theory, MemberData(nameof(RecheckFailures))]
    public async Task EveryChainRecheckNativeFailureClosesLeafFirstAndNeverFollowsReplacement(int layer, string stage, bool throwing)
    {
        var f = new OriginalFileScopeFixture();
        f.Api.Fail = c => c.Index == layer && c.Stage == stage && c.Number == 2;
        if (throwing) f.Api.Before = c => { if (c.Index == layer && c.Stage == stage && c.Number == 2) throw new IOException(); };
        var failed = await f.Service().AcquireAsync(f.Anchor); Assert.Null(failed.Owner); Assert.Equal(4, f.Api.Opened.Count);
        Assert.True((await failed.Retirement).Retired); AssertClosedInReverse(f.Api);
    }
    [Theory]
    [InlineData("platform")][InlineData("drive")][InlineData("anchor_size")][InlineData("anchor_volume")][InlineData("anchor_id")]
    public async Task UnsupportedOrInvalidAnchorsAreNotResampledIntoUsableScope(string fault)
    {
        var f = new OriginalFileScopeFixture(); f.Api.Platform = fault != "platform"; f.Api.Drive = fault == "drive" ? 2u : 3u;
        f.Anchor = fault switch { "anchor_size" => f.Anchor with { Length = 0 }, "anchor_volume" => f.Anchor with { Identity = new(0, 1, 1) },
            "anchor_id" => f.Anchor with { Identity = new(1, 0, 0) }, _ => f.Anchor };
        var result = await f.Service().AcquireAsync(f.Anchor); Assert.Null(result.Owner); Assert.Empty(f.Api.Opened); Assert.True((await result.Retirement).Retired);
        if (fault.StartsWith("anchor_", StringComparison.Ordinal)) Assert.Empty(f.Api.Calls);
    }
    public static IEnumerable<object[]> ChangedDirectories => from layer in Enumerable.Range(0, 3)
        from fault in new[] { "replacement", "reparse", "file", "delete", "path", "cross_volume" } select new object[] { layer, fault };
    [Theory, MemberData(nameof(ChangedDirectories))]
    public async Task FullChainRecheckRejectsSameNameReplacementWithoutReopen(int layer, string fault)
    {
        var f = new OriginalFileScopeFixture(); Mutate(f, layer, fault, later: true); var failed = await f.Service().AcquireAsync(f.Anchor);
        Assert.Null(failed.Owner); Assert.Equal(4, f.Api.Opened.Count); Assert.True((await failed.Retirement).Retired); AssertClosedInReverse(f.Api);
    }
    [Theory]
    [InlineData("low")][InlineData("high")][InlineData("length")][InlineData("zero_length")]
    public async Task OriginalLeafAnchorCannotBeReissuedByScopeSampling(string fault)
    {
        var f = new OriginalFileScopeFixture(); f.Api.IdentityValue = (n, _, v) => { if (n.Leaf) { if (fault == "low") v.IdLow++; if (fault == "high") v.IdHigh++; } return v; };
        f.Api.StandardValue = (n, _, v) => { if (n.Leaf) { if (fault == "length") v.EndOfFile++; if (fault == "zero_length") v.EndOfFile = 0; } return v; };
        var failed = await f.Service().AcquireAsync(f.Anchor); Assert.Null(failed.Owner); Assert.True((await failed.Retirement).Retired); AssertClosedInReverse(f.Api);
    }
    [Theory, MemberData(nameof(ChangedDirectories))]
    public async Task FailedScopeReobservationKeepsAllOriginalResourcesAndReturnsNoOldSnapshot(int layer, string fault)
    {
        var f = new OriginalFileScopeFixture(); var service = f.Service(); var held = await service.AcquireAsync(f.Anchor);
        Mutate(f, layer, fault); var sampled = await held.Owner!.ObserveAsync(); Assert.NotEqual(OriginalFileHoldReason.Observed, sampled.Reason); Assert.Null(sampled.Snapshot);
        Assert.Equal(4, f.Api.Opened.Count); Assert.DoesNotContain(f.Api.Calls, c => c.Stage == "close"); Assert.False(held.Retirement.IsCompleted);
        Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(f.Anchor)).Reason); await Retire(held.Owner);
        Assert.Equal(sampled.Reason, (await held.Retirement).PrimaryReason); AssertClosedInReverse(f.Api);
    }
}
