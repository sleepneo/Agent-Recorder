using System.Net;
using System.Text;
using System.Security.Cryptography;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static async Task BundleSourceDenied(Fixture f, Recording rec)
    {
        if (f.Api is null) f.StartApi(); using var client = f.Client();
        var request = f.Request("display"); request["output"]!["conflict_policy"] = "rename";
        using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
        Assert.Single(f.Engine._recs); Assert.Equal(1, f.FactoryCalls); Assert.Equal(RecState.completed, rec.State);
        Assert.True(File.Exists(rec.OutputPath));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SilentBundleSourceStaysExclusiveThroughQueueAndGenerator(bool queued)
    {
        using var f = RuntimeFixture(); await f.Prepare(); Func<Task>? run = null;
        if (queued) f.Engine.BundleGenerationDispatcherForTests = work => { run = work; return Task.CompletedTask; };
        var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        var hash = SHA256.HashData(File.ReadAllBytes(rec.OutputPath));
        var host = queued ? null : HostTask(f);
        try { await BundleSourceDenied(f, rec); }
        finally
        {
            f.Generator.Release.TrySetResult();
            if (queued) await run!(); else await host!;
        }
        await Until(() => BundleUseCount(f) == 0); await f.Wgc!.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", rec.BundleSnapshot.Status); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(rec.OutputPath)));
        using var client = f.Client(); var next = f.Request("display"); next["output"]!["conflict_policy"] = "rename";
        using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
            new StringContent(next.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(1, f.FactoryCalls);
        var pending = f.Engine._recs.Values.Single(r => r.Id != rec.Id);
        Assert.Equal(RecState.pending_confirmation, pending.State); Assert.NotEqual(rec.OutputPath, pending.OutputPath);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(rec.OutputPath)));
    }
}
