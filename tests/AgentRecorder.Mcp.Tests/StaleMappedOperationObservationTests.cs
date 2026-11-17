using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

public sealed class StaleMappedOperationObservationTests(Fixture fixture) : IClassFixture<Fixture>
{
    [Theory]
    [InlineData("accepted", false)]
    [InlineData("submitting", false)]
    [InlineData("accepted", true)]
    [InlineData("submitting", true)]
    public async Task LateMappingUsesVerifiedSameHostAndNeverCreatesOrStops(string previous, bool changedHost)
    {
        await using var api = await FakeApi.Start(fixture); api.Set("approved"); api.Set("state", "recording");
        var binding = new BindingStore(fixture.NewPath("binding.json")); var tools = new RecorderTools(binding);
        var saved = await tools.CallAsync("recorder_binding_set", new Dictionary<string, JsonElement>
        { ["package_root"] = JsonSerializer.SerializeToElement(fixture.Package), ["data_dir"] = JsonSerializer.SerializeToElement(api.Data) }, CancellationToken.None);
        Assert.True(saved.Ok);
        using var host = await BoundHost.OpenAsync(saved.Binding!, null, CancellationToken.None);
        var store = new OperationStore(binding); Directory.CreateDirectory(store.Root);
        using var owner = Process.GetCurrentProcess(); var id = Guid.NewGuid().ToString("D");
        var original = new OperationEntry { SchemaVersion = 1, OperationId = id, DurationSeconds = 10, Binding = saved.Binding!,
            OwnerPid = owner.Id, OwnerStartedTicks = owner.StartTime.ToUniversalTime().Ticks, Status = previous, ReasonCode = "submission_intent",
            Host = previous == "submitting" ? host.Stamp : null };
        store.Write(original); var lease = store.WorkerLease(id); var reads = 0; var routes = api.Routes.Length;
        await using var manager = new OperationManager(binding, tools)
        {
            AfterOperationRead = () =>
            {
                if (++reads != 1) return;
                store.Write(original with { Status = "recording", RecordingId = "rec_123456abcdef",
                    Host = changedHost ? host.Stamp with { InstanceId = Guid.NewGuid().ToString("D") } : host.Stamp });
                lease.Dispose();
            }
        };
        var query = new Dictionary<string, JsonElement> { ["operation_id"] = JsonSerializer.SerializeToElement(id) };
        var result = await manager.CallAsync("recorder_operation_get", query, CancellationToken.None);
        if (changedHost)
        {
            Assert.Equal("operation_host_changed", result.ReasonCode); Assert.False(result.Ok);
            Assert.Equal(routes, api.Routes.Length);
        }
        else
        {
            Assert.Equal("recording", result.Status); Assert.Equal("recording_state_observed", result.ReasonCode);
            Assert.Equal("rec_123456abcdef", result.RecordingId);
            Assert.Contains(api.Routes.Skip(routes), route => route.Contains("/recordings/rec_123456abcdef"));
            Assert.Equal("recording", (await manager.CallAsync("recorder_record_region", query, CancellationToken.None)).Status);
            await using var restarted = new OperationManager(binding, tools);
            Assert.Equal("recording", (await restarted.CallAsync("recorder_operation_get", query, CancellationToken.None)).Status);
        }
        Assert.Equal(0, api.Posts); Assert.Equal(0, UpgradeCompatibilityScenarios.StopPosts(api));
        Assert.Single(store.Entries()); Assert.Empty(Directory.EnumerateFiles(api.Data, "*.mp4", SearchOption.AllDirectories));
    }
}
