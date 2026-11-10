using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class Task416StorageReserveTests
{
    private const long MiB = 1024L * 1024;
    private const long Reserve = 2048 * MiB;

    [Theory]
    [InlineData(null, 256, null)]
    [InlineData("", 256, null)]
    [InlineData("256", 256, null)]
    [InlineData("02048", 2048, null)]
    [InlineData("65536", 65536, null)]
    [InlineData(" ", null, "invalid_integer")]
    [InlineData("\t", null, "invalid_integer")]
    [InlineData(" \t ", null, "invalid_integer")]
    [InlineData("invalid", null, "invalid_integer")]
    [InlineData(" 2048", null, "invalid_integer")]
    [InlineData("2048 ", null, "invalid_integer")]
    [InlineData("+2048", null, "invalid_integer")]
    [InlineData("-2048", null, "invalid_integer")]
    [InlineData("2.5", null, "invalid_integer")]
    [InlineData("２５６", null, "invalid_integer")]
    [InlineData("2048\0", null, "invalid_integer")]
    [InlineData("0", null, "out_of_range")]
    [InlineData("255", null, "out_of_range")]
    [InlineData("65537", null, "out_of_range")]
    [InlineData("999999999999999999999", null, "out_of_range")]
    public void StartupParserAcceptsOnlyDecimalMiBInRange(string? input, int? mib, string? reason)
    {
        var setting = RecordingStorageReserve.Parse(input);
        Assert.Equal(mib * MiB, setting.EffectiveBytes);
        Assert.Equal(string.IsNullOrEmpty(input) ? "default" : "environment", setting.Source);
        Assert.Equal(reason, setting.InvalidReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StrictWindowAdmissionCountsReserveOncePerPhysicalVolume(bool sharedVolume)
    {
        var paths = new CaptureWritePaths(@"D:\output", @"D:\data\temp", @"C:\temp\AgentRecorder");
        var provider = new FakeStorageProvider
        {
            Volume = path => sharedVolume || path != paths.WgcTempRoot ? "output-and-av" : "wgc"
        };
        var rec = new Recording { Config = new CaptureConfig { RequireWindowSurface = true, DurationSeconds = 60, WritePaths = paths } };
        using var guard = RecordingStoragePolicy.CreateSafety(rec, provider, RecordingStorageReserve.Parse("2048"));
        const long video = 120 * MiB;
        var budgets = guard.AdmissionBudgets(await provider.QueryAsync(paths.Directories, default));
        Assert.Equal(sharedVolume ? 1 : 2, budgets.Count);
        Assert.Equal(Reserve + (sharedVolume ? 5 : 4) * video + 4096, budgets["output-and-av"]);
        if (!sharedVolume) Assert.Equal(Reserve + video, budgets["wgc"]);
        provider.Free = path => budgets[provider.Volume(path)];
        Assert.Null(await guard.CheckAsync(admission: true));
        provider.Free = path => budgets[provider.Volume(path)] - (path == paths.WgcTempRoot ? 1 : 0);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, await guard.CheckAsync(admission: true));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public async Task PublicationCopyChecksReservePlusActualUnwrittenBytes(bool av, int delta)
    {
        using var temp = new TempDirectory();
        var paths = new CaptureWritePaths(temp.Path, av ? Path.Combine(temp.Path, "av") : null, null);
        var outputFree = Reserve + 8 + delta;
        var provider = new FakeStorageProvider { Free = path => path == paths.OutputDirectory ? outputFree : Reserve };
        using var guard = new WindowStorageSafety(paths, 60, provider, Reserve);
        if (av) guard.EnableFfmpegAvPublication();
        else guard.ReserveFfmpegVideoOnlyPublication();
        var gate = guard.CreateCommitGate(null, finalPublication: true);
        using var copy = await ((IPublicationCopyCapacityGate)gate).BeginCopyAsync(8, default);
        Assert.NotNull(copy);
        if (delta < 0)
        {
            var error = await Assert.ThrowsAsync<StorageSafetyException>(() => copy!.EnsureCapacityAsync(default));
            Assert.Equal(WindowStorageSafety.LowSpaceCode, error.Code);
            Assert.False(gate.TryCommit(() => Assert.Fail("Insufficient copy capacity cannot publish.")));
            return;
        }
        await copy!.EnsureCapacityAsync(default);
        using var partial = File.Create(Path.Combine(temp.Path, "owned-copy.partial"));
        await copy.WriteAsync(partial, new byte[4], default);
        outputFree -= 4;
        Assert.Equal(4, guard.RemainingPublicationCopyBytes);
        await copy.EnsureCapacityAsync(default);
        await copy.WriteAsync(partial, new byte[4], default);
        outputFree -= 4;
        await copy.EnsureCapacityAsync(default);
        Assert.True(gate.TryCommit(() => { }));
    }
}
