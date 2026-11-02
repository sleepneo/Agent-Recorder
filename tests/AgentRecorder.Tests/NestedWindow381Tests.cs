using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    private static JsonObject NestedWindow381Request(Fixture f, string role, string window = "window_42", Recording? parent = null)
    {
        var q = f.Request("window"); q["source"]!["window_id"] = window;
        q["output"]!.AsObject().Remove("filename");
        q["nested"] = new JsonObject { ["role"] = role, ["session_id"] = "window381", ["parent_recording_id"] = parent?.Id };
        return q;
    }
    private static async Task<(HttpStatusCode Status, string Body)> NestedWindow381Post(Fixture f, JsonObject q, bool quick = false)
    {
        using var client = f.Client(); using var response = await client.PostAsync(quick ? "/api/v1/recordings/quick" : "/api/v1/recordings?creation_wait_ms=0",
            new StringContent(q.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task NestedWindow381LegalRawOuterIsAcceptedBeforeAnyWorker()
    {
        using var f = RuntimeFixture(); f.StartApi();
        var response = await NestedWindow381Post(f, NestedWindow381Request(f, "outer"));
        SaveSeriesBundleEvidence("window381-red", new { status = (int)response.Status, response.Body, factories = f.FactoryCalls, recordings = f.Engine._recs.Count });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(RecState.pending_confirmation, Assert.Single(f.Engine._recs.Values).State);
        Assert.Equal(0, f.FactoryCalls);
    }
}
