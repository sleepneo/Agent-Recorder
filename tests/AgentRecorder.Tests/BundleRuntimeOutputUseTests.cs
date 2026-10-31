using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static Fixture RuntimeFixture() => new(testRoot: Path.Combine(Path.GetTempPath(), "bundle-runtime"));
    private static void RegisterOtherOutput(Fixture f, string name = "other")
    {
        var path = Path.Combine(f.Root, name + ".mp4");
        Claim(f.Engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = path }, path);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BundleRuntimeUseSurvivesNewRegistrationUntilRealGeneratorAndHostExit(bool fail)
    {
        using var f = RuntimeFixture(); await f.Prepare();
        if (fail) f.Generator.Fault = _ => Task.FromResult(RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed));
        var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(rec.IsFinalized); Assert.True(f.Wgc.OutputRetirement.IsCompletedSuccessfully);
            Assert.Equal("generating", rec.BundleSnapshot.Status); Assert.True(Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse);
            RegisterOtherOutput(f);
            Assert.True(Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse);
        }
        finally { f.Generator.Release.TrySetResult(); }
        await f.Generator.Work!.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => f.Writer.Read(rec.Id)!.Bundle!.Status == (fail ? "failed" : "ready"));
        await Until(() => !Read(f.Engine, rec.OutputPath)!.Paths.Single().InUse);
        Assert.Equal(RecState.completed, rec.State); Assert.True(File.Exists(rec.OutputPath)); Assert.Equal(1, f.Generator.Calls);
    }
}
