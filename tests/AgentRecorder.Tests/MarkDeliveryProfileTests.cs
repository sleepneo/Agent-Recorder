using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class CodexProfileRecordingRegressionTests
{
    [Fact]
    public async Task RegionOnlyDefaultBundleAcceptsMarkWithPendingDeliveryAfterFakeApproval()
    {
        using var created = await RegionPost(RegionBody());
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var rec = Assert.Single(_engine._recs.Values);
        Assert.True(rec.GenerateBundle);
        Assert.False(_engine.CanAddChapterMarks(rec));
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.True(_engine.CanAddChapterMarks(rec));
        using var response = await _client.PostAsync($"/api/v1/recordings/{rec.Id}/marks",
            new StringContent("{\"label\":\"profile region\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        Assert.Equal(rec.Id, data["recording_id"]!.GetValue<string>());
        Assert.Equal("pending", data["delivery"]!["status"]!.GetValue<string>());
        Assert.Equal("bundle/marks.json", data["delivery"]!["artifact"]!.GetValue<string>());
        Assert.Equal("region_only", rec.ProfileReuseMode);
        Assert.Single(rec.SnapshotMarks());
        Assert.Equal(0, rec.BundleGenerationStarted);
    }
}
