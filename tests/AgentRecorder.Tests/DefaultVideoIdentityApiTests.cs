using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultVideoIdentityActualRawAndQuickResponseNamesContainRecordingId(bool quick)
    {
        using var f = RuntimeFixture(); f.StartApi(); using var client = f.Client();
        var request = quick ? new JsonObject { ["target"] = new JsonObject { ["type"] = "primary_display" },
            ["duration_seconds"] = 1, ["countdown_seconds"] = 0,
            ["output"] = new JsonObject { ["directory"] = System.IO.Path.Combine(f.Root, "Videos") } } : f.Request("display");
        if (!quick) request["output"]!.AsObject().Remove("filename");
        using var response = await client.PostAsync(quick ? "/api/v1/recordings/quick?creation_wait_ms=0" : "/api/v1/recordings?creation_wait_ms=0",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = JsonNode.Parse(body)!["data"]!["recording_id"]!.GetValue<string>(); var rec = f.Engine._recs[id];
        SaveSeriesBundleEvidence("default-name-" + quick, new { quick, body, id, path = rec.OutputPath, factories = f.FactoryCalls });
        var confirmationPath = JsonNode.Parse(body)!["data"]!["summary"]!["output"]!.GetValue<string>();
        Assert.Equal(rec.OutputPath, confirmationPath);
        Assert.Contains(id, System.IO.Path.GetFileName(confirmationPath));
        Assert.Matches(@"^recording-\d{4}-\d{2}-\d{2}-\d{6}-" + id + @"\.mp4$", System.IO.Path.GetFileName(rec.OutputPath));
        Assert.Equal(rec.OutputPath, rec.Config.OutputPath); Assert.Null(rec.AuthorizationProof); Assert.Equal(0, f.FactoryCalls);
    }
    [Fact]
    public async Task DefaultVideoIdentityApprovalMovePreservesInitialNameAndBundleStem()
    {
        using var f = RuntimeFixture(); await f.Prepare();
        var request = f.Request("display"); request["output"]!.AsObject().Remove("filename");
        f.Engine.CreateRecording(request, "task380", f.Tray); var rec = Assert.Single(f.Engine._recs.Values);
        var name = System.IO.Path.GetFileName(rec.OutputPath); Assert.Contains(rec.Id, name);
        var directory = System.IO.Path.Combine(f.Root, "moved");
        f.Tray.Confirm!(AgentRecorder.Infrastructure.ConfirmationDecision.Approve(directory));
        await Until(() => rec.State == RecState.recording || rec.IsFinalized);
        Assert.Equal(RecState.recording, rec.State); Assert.Equal(name, System.IO.Path.GetFileName(rec.OutputPath));
        Assert.Equal(System.IO.Path.Combine(directory, name), rec.OutputPath); Assert.Equal(rec.OutputPath, rec.Config.OutputPath);
        Assert.Equal(1, rec.DurationSeconds); Assert.Equal(0, rec.CountdownSeconds); Assert.False(rec.Config.AudioRequested);
        f.Generator.Release.TrySetResult(); await f.Terminal(rec, false);
        await Until(() => rec.BundleSnapshot.Status == "ready");
        Assert.Equal(System.IO.Path.ChangeExtension(rec.OutputPath, ".bundle"), rec.BundleSnapshot.Path);
        Assert.Contains(rec.Id, rec.BundleSnapshot.Path!); Assert.True(System.IO.File.Exists(rec.OutputPath));
    }

}
