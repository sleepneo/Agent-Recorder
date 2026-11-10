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
    public async Task RegionOnlyDefaultVideoRejectsMarkAfterFakeApproval()
    {
        using var created = await RegionPost(RegionBody());
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var rec = Assert.Single(_engine._recs.Values);
        Assert.False(rec.GenerateBundle);
        Assert.False(_engine.CanAddChapterMarks(rec));
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.False(_engine.CanAddChapterMarks(rec));
        using var response = await _client.PostAsync($"/api/v1/recordings/{rec.Id}/marks",
            new StringContent("{\"label\":\"profile region\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("UNSUPPORTED_FEATURE",await response.Content.ReadAsStringAsync());
        Assert.Equal("region_only", rec.ProfileReuseMode);
        Assert.Empty(rec.SnapshotMarks());
        Assert.Equal(0, rec.BundleGenerationStarted);
    }
}
