using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OriginalFileHoldFixture;

namespace AgentRecorder.Tests;

public sealed class OriginalFileHoldContractTests(ITestOutputHelper output)
{
    public static TheoryData<string> InvalidPaths => new()
    {
        @"relative.mp4", @"C:video.mp4", @"C:\host\Videos\..\out.mp4", @"C:\host\Videos\child\out.mp4",
        @"C:\host\Videos-old\out.mp4", @"D:\host\Videos\out.mp4", @"\\server\share\out.mp4", @"\\?\C:\host\Videos\out.mp4",
        @"\Device\HarddiskVolume1\out.mp4", @"C:\host\Videos\out.mp4:ads", @"C:\host\Videos\out.mp4 ", @"C:\host\Videos\out.mp4.",
        @"C:\host\Videos\CON.mp4", @"C:\host\Videos\COM1.mp4", @"C:\host\Videos\LPT9.mp4", @"C:\host\Videos\COM¹.mp4",
        @"C:\host\Videos\LPT².mp4", @"C:\host\Videos\COM³.mp4", @"C:\host\Videos\CONIN$.mp4", @"C:\host\Videos\CLOCK$.mp4",
        @"C:\host\Videos\NUL.txt.mp4", @"C:\host\Videos\.mp4", @"C:\host\Videos\out.png", "C:/host/Videos/out.mp4",
        "C:\\host\\Videos\\out\0.mp4", @"C:\host\Videos\" + new string('x', 1024) + ".mp4"
    };
    [Theory, MemberData(nameof(InvalidPaths))]
    public async Task InvalidInputsNeverEnterNativeOrAncestors(string path)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service();
        var result = await service.AcquireAsync(f.Anchor with { Path = path }); Assert.Equal(OriginalFileHoldReason.InvalidInput, result.Reason);
        Assert.Null(result.Owner); Assert.Empty(f.Api.Calls); Assert.Empty(f.Metadata.Calls); Assert.True((await result.Retirement).Retired);
    }
    [Theory]
    [InlineData("C:\\host\\Else")][InlineData("C:\\CON\\Videos")][InlineData("C:\\host\\Videos\\")][InlineData("depth")]
    [InlineData("length")][InlineData("volume")][InlineData("file")]
    public async Task BadFrozenRootOrOriginalAnchorCannotBeReissuedFromOpen(string fault)
    {
        var f = new OriginalFileHoldFixture(); var root = fault == "depth" ? "C:\\" + string.Join("\\", Enumerable.Repeat("a", 32)) + "\\Videos" : fault.StartsWith("C:") ? fault : Root;
        var anchor = fault switch { "length" => f.Anchor with { Length = 0 }, "volume" => f.Anchor with { Identity = new(0, 1, 2) }, "file" => f.Anchor with { Identity = new(1, 0, 0) }, "depth" => f.Anchor with { Path = root + "\\out.mp4" }, _ => f.Anchor };
        Assert.Equal(OriginalFileHoldReason.InvalidInput, (await f.Service(root).AcquireAsync(anchor)).Reason); Assert.Empty(f.Api.Calls); Assert.Empty(f.Metadata.Calls);
    }
    [Theory]
    [InlineData("视频.mp4")][InlineData("COM中.mp4")][InlineData("LPT文.MP4")][InlineData("COM10.mp4")][InlineData("区域一-Ä.mp4")]
    public async Task LegalNamesHoldExactlyOneExistingLeafWithNarrowAdmissionFlags(string name)
    {
        var f = new OriginalFileHoldFixture(); f.Anchor = f.Anchor with { Path = Root + "\\" + name }; var result = await f.Service().AcquireAsync(f.Anchor);
        Assert.Equal(OriginalFileHoldReason.Held, result.Reason); var owner = Assert.IsType<OriginalFileHoldOwner>(result.Owner);
        Assert.Single(f.Api.Opened); Assert.False(f.Api.Opened[0].IsClosed); Assert.False(result.Retirement.IsCompleted);
        Assert.Equal(@"\\?\" + f.Anchor.Path, f.Api.OpenPath); Assert.Equal(0x10080u, f.Api.Access); Assert.Equal(0u, f.Api.Share);
        Assert.Equal(3u, f.Api.Creation); Assert.Equal(0x00200000u, f.Api.Flags); Assert.Equal(3, f.Metadata.Calls.Count);
        var sample = await owner.ObserveAsync(); Assert.Equal(OriginalFileHoldReason.Observed, sample.Reason); Assert.Equal(f.Anchor.Identity, sample.Snapshot!.Identity);
        Assert.Single(f.Api.Opened); Assert.All(f.Api.Observed, h => Assert.Same(f.Api.Opened[0], h));
        await Retire(owner); var count = f.Api.Calls.Count; owner.Dispose(); Assert.Equal(OriginalFileHoldReason.Disposed, (await owner.ObserveAsync()).Reason);
        Assert.Equal(count, f.Api.Calls.Count); Assert.Equal(1, f.Api.CloseCalls);
        output.WriteLine("one OPEN_EXISTING leaf; access=0x10080, share=0, flags=0x200000; same handle survives return and re-observation; confirmed close only.");
    }
    public static IEnumerable<object[]> Faults => FaultData.Select(row => new object[] { row[0], row[1].ToString()! });
    private static TheoryData<string, OriginalFileHoldReason> FaultData => new()
    {
        {"volume",OriginalFileHoldReason.IdentityMismatch},{"low",OriginalFileHoldReason.IdentityMismatch},{"high",OriginalFileHoldReason.IdentityMismatch},
        {"length",OriginalFileHoldReason.LengthMismatch},{"links",OriginalFileHoldReason.StructureInvalid},{"delete",OriginalFileHoldReason.StructureInvalid},
        {"directory",OriginalFileHoldReason.StructureInvalid},{"reparse",OriginalFileHoldReason.StructureInvalid},{"attribute",OriginalFileHoldReason.StructureInvalid},
        {"zero_volume",OriginalFileHoldReason.StructureInvalid},{"zero_id",OriginalFileHoldReason.StructureInvalid},{"negative_allocation",OriginalFileHoldReason.StructureInvalid},
        {"bad_time",OriginalFileHoldReason.StructureInvalid},{"basic",OriginalFileHoldReason.MetadataUnavailable},{"standard",OriginalFileHoldReason.MetadataUnavailable},
        {"identity",OriginalFileHoldReason.MetadataUnavailable},{"path",OriginalFileHoldReason.PathMismatch},{"path_zero",OriginalFileHoldReason.PathMismatch},
        {"path_capacity",OriginalFileHoldReason.PathMismatch},{"path_length",OriginalFileHoldReason.PathMismatch},{"changed",OriginalFileHoldReason.MetadataChanged},
        {"last_access",OriginalFileHoldReason.MetadataChanged},{"changed_id",OriginalFileHoldReason.MetadataChanged},{"changed_path",OriginalFileHoldReason.PathMismatch},
        {"native_throw",OriginalFileHoldReason.WorkerFailed}
    };
    [Theory, MemberData(nameof(Faults))]
    public async Task EveryUntrustedObservationRejectsAndClosesSameLeaf(string fault, string expected)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service();
        f.Api.IdentityValue = _ => new() { VolumeSerialNumber = fault is "volume" ? f.Anchor.Identity.VolumeSerialNumber ^ 1 : fault == "zero_volume" ? 0 : f.Anchor.Identity.VolumeSerialNumber,
            IdLow = fault == "low" ? f.Anchor.Identity.IdLow ^ 1 : fault == "zero_id" ? 0 : f.Anchor.Identity.IdLow,
            IdHigh = fault == "high" ? f.Anchor.Identity.IdHigh ^ 1 : fault == "zero_id" ? 0 : f.Anchor.Identity.IdHigh };
        f.Api.StandardValue = _ => new() { EndOfFile = fault == "length" ? 13 : 12, AllocationSize = fault == "negative_allocation" ? -1 : 4096,
            NumberOfLinks = fault == "links" ? 2u : 1u, DeletePending = fault == "delete" ? (byte)1 : (byte)0, Directory = fault == "directory" ? (byte)1 : (byte)0 };
        f.Api.BasicValue = i => new() { Attributes = fault == "reparse" ? (uint)FileAttributes.ReparsePoint : fault == "attribute" ? 0 : (uint)FileAttributes.Normal,
            LastWriteTime = fault == "bad_time" ? long.MaxValue : fault == "changed" && i == 2 ? 133000000000000001 : 133000000000000000,
            LastAccessTime = fault == "last_access" ? i : 0 };
        f.Api.FailBasic = fault == "basic"; f.Api.FailStandard = fault == "standard"; f.Api.FailIdentity = fault == "identity";
        if (fault == "changed_id") f.Api.IdentityValue = i => new() { VolumeSerialNumber = f.Anchor.Identity.VolumeSerialNumber, IdLow = f.Anchor.Identity.IdLow, IdHigh = i == 2 ? 1 : f.Anchor.Identity.IdHigh };
        if (fault is "path" or "changed_path") f.Api.PathValue = i => @"\\?\" + (fault == "path" || i == 2 ? Root + @"\other.mp4" : f.Anchor.Path);
        f.Api.PathLength = fault switch { "path_zero" => 0, "path_capacity" => 2048, "path_length" => 1, _ => null };
        if (fault == "native_throw") f.Api.Before = n => { if (n == "basic") throw new IOException("private-metadata"); };
        var result = await service.AcquireAsync(f.Anchor); Assert.Equal(expected, result.Reason.ToString()); Assert.Null(result.Owner);
        Assert.Equal(expected, result.PrimaryReason!.Value.ToString()); Assert.True((await result.Retirement).Retired); Assert.Equal(1, f.Api.CloseCalls); Assert.Single(f.Api.Opened);
        Assert.All(f.Api.Observed, h => Assert.Same(f.Api.Opened[0], h));
    }
    [Theory]
    [InlineData("reparse")][InlineData("file")][InlineData("missing")][InlineData("drive")][InlineData("unsupported")]
    public async Task AncestorAndDriveFailuresHaveNoLeafOpen(string fault)
    {
        var f = new OriginalFileHoldFixture(); f.Api.Drive = fault == "drive" ? 2u : 3u; f.Api.Platform = fault != "unsupported";
        f.Metadata.Read = _ => fault switch { "reparse" => FileAttributes.Directory | FileAttributes.ReparsePoint, "file" => FileAttributes.Normal, "missing" => throw new IOException(), _ => FileAttributes.Directory };
        var result = await f.Service().AcquireAsync(f.Anchor); Assert.Null(result.Owner); Assert.Empty(f.Api.Opened); Assert.Equal(0, f.Api.CloseCalls); Assert.True((await result.Retirement).Retired);
    }
    [Theory]
    [InlineData(2,"Missing")][InlineData(5,"Inaccessible")][InlineData(32,"SharingViolation")][InlineData(999,"OpenFailed")]
    public async Task FailedOpenHasNoObjectAndCapacityRecovers(int error, string reason)
    {
        var f = new OriginalFileHoldFixture(); f.Api.InvalidOpen = true; f.Api.OpenError = error; var service = f.Service();
        var failed = await service.AcquireAsync(f.Anchor); Assert.Equal(reason, failed.Reason.ToString()); Assert.True((await failed.Retirement).Retired); Assert.Equal(0, f.Api.CloseCalls);
        f.Api.InvalidOpen = false; var good = await service.AcquireAsync(f.Anchor); Assert.NotNull(good.Owner); await Retire(good.Owner!);
    }
    [Fact]
    public async Task ThirtyTwoAncestorBoundaryIsFiniteAndLegalUnicodeRootStaysInScope()
    {
        var f = new OriginalFileHoldFixture(); var root = "C:\\" + string.Join("\\", Enumerable.Repeat("工作", 30)) + "\\Videos";
        f.Anchor = f.Anchor with { Path = root + "\\视频.mp4" }; var result = await f.Service(root).AcquireAsync(f.Anchor);
        Assert.Equal(OriginalFileHoldReason.Held, result.Reason); Assert.Equal(32, f.Metadata.Calls.Count); await Retire(result.Owner!);
    }
    [Theory]
    [InlineData("standard")][InlineData("identity")][InlineData("path")]
    public async Task PartialReobservationFaultNeverClosesOriginalOrReturnsOldSample(string step)
    {
        var f = new OriginalFileHoldFixture(); var service = f.Service(); var owner = (await service.AcquireAsync(f.Anchor)).Owner!;
        f.Api.Before = n => { if (n == step) throw new IOException("private-observation"); };
        var observation = await owner.ObserveAsync(); Assert.Equal(OriginalFileHoldReason.WorkerFailed, observation.Reason); Assert.Null(observation.Snapshot);
        Assert.False(owner.Retirement.IsCompleted); Assert.Equal(0, f.Api.CloseCalls); Assert.Single(f.Api.Opened);
        f.Api.Before = null; Assert.Equal(OriginalFileHoldReason.Observed, (await owner.ObserveAsync()).Reason); await Retire(owner);
    }
}
