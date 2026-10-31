using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Core.Automation;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class CodexProfileRecordingRegressionTests
{
    [Theory]
    [InlineData(11, 20, 96)]
    [InlineData(10, 21, 120)]
    [InlineData(11, 21, 144)]
    public async Task RegionOnlySavedOddOriginsKeepExactPixelsThroughApproval(int x, int y, int dpi)
    {
        _display.Metadata = _display.Metadata with { DpiX = dpi, DpiY = dpi };
        _display.Others = new[] { new StandingLeaseDisplayMetadata("display_2", "display-stable-v1-" + new string('c', 64),
            DisplayIdentityResolutionStatus.Resolved, new(1820, 0, 1280, 720), 96, 96, 1280, 720, AuthorizedDisplayOrientation.Landscape) };
        SystemQuery.SetDisplayTopologyProvider(() =>
        {
            Interlocked.Increment(ref _systemDisplayCalls);
            return new[] { _display.Metadata }.Concat(_display.Others).Select((m, i) =>
                new SystemQuery.DisplayTopologyInfo(m.PublicId, "Display " + i, i == 0,
                    new(m.PhysicalBounds!.Value.X, m.PhysicalBounds.Value.Y, m.PhysicalBounds.Value.Width, m.PhysicalBounds.Value.Height),
                    m.DpiX!.Value / 96.0, m.StableDisplayFingerprint, m.IdentityStatus)).ToList();
        });
        var expected = (-100 + x, 50 + y, 640, 480);
        _tray.Selection = ("selected", expected.Item1, expected.Item2, 640, 480, "display_1", "virtual_screen");
        using var selection = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        var selectionText = await selection.Content.ReadAsStringAsync();
        Assert.True(selection.IsSuccessStatusCode, selectionText);
        var selectionRef = JsonNode.Parse(selectionText)!["data"]!["selection_ref"]!.GetValue<string>();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "Odd physical origin", source_selection_ref = selectionRef,
                changes = new { duration_seconds = 60, countdown_seconds = 5, output_directory = Path.Combine(_temp.Path, "saved-output") }
            }), Encoding.UTF8, "application/json")
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var saved = await _client.SendAsync(create);
        var savedText = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var reference = JsonNode.Parse(savedText)!["data"]!["profile"]!["profile_ref"]!;
        var exact = _gateway.ReadExactVersionSnapshot(reference["id"]!.GetValue<string>(), reference["version"]!.GetValue<long>())!.Version!;
        Assert.Equal(reference["digest"]!.GetValue<string>(), exact.ProfileDigest);
        Assert.Equal(new AuthorizedPhysicalRectangle(x, y, 640, 480), exact.RegionWithinDisplay);
        Assert.Equal(dpi, exact.DpiX);
        Assert.Equal(dpi, exact.DpiY);
        // Negative control: the unchanged raw entry accepts precisely the same version.
        var old = await CreateRecording(exact);
        Assert.Equal(0, _backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Reject());
        Assert.Equal(RecState.rejected, old.State);
        var selectionCalls = _tray.SelectionCalls;
        var body = new JsonObject
        {
            ["target"] = new JsonObject { ["type"] = "profile_region", ["profile_ref"] = reference.DeepClone() },
            ["duration_seconds"] = 20, ["output"] = new JsonObject { ["directory"] = _temp.Path }
        };
        using var response = await RegionPost(body.ToJsonString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.Equal("requires_user_confirmation", JsonNode.Parse(text)!["data"]!["status"]!.GetValue<string>());
        var rec = Assert.Single(_engine._recs.Values.Where(r => !r.IsFinalized));
        Assert.Equal(exact.Reference, rec.FixedRegionProfileReference);
        Assert.Equal(expected, rec.Config.Bounds);
        Assert.Equal(0, _backend.StartCalls);
        var selected = Path.Combine(_temp.Path, "local-output");
        _tray.Decision!(ConfirmationDecision.Approve(selected));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, _backend.StartCalls);
        Assert.Equal(expected, _backend.Config!.Bounds);
        Assert.Equal(20, _backend.Config.DurationSeconds);
        Assert.Equal(3, _backend.Config.CountdownSeconds);
        Assert.Equal(selected, Path.GetDirectoryName(_backend.Config.OutputPath));
        Assert.Equal(CaptureAuthorizationProofState.Consumed, _backend.Proof!.State);
        Assert.Equal(rec.Id, _backend.Proof.RecordingId);
        Assert.Equal(CaptureAuthorizationProofKind.InteractiveConfirmation, _backend.Proof.Kind);
        Assert.Equal(selectionCalls, _tray.SelectionCalls);
        Assert.Equal(exact.ProfileDigest, _gateway.ReadExactVersionSnapshot(exact.ProfileId, exact.ProfileVersion)!.Version!.ProfileDigest);
    }

    public static IEnumerable<object[]> RegionOnlyInvalidOutputs()
    {
        foreach (var audio in new[] { "none", "system", "microphone" })
            foreach (var kind in new[] { "relative", "nul-relative", "nul-absolute", "control", "wildcard-directory",
                "filename-separator", "filename-control", "filename-reserved", "template-parent", "template-invalid" })
                yield return new object[] { audio, kind };
    }

    [Theory]
    [MemberData(nameof(RegionOnlyInvalidOutputs))]
    public async Task RegionOnlyInvalidOutputHasNoProviderUiOrOutputCreation(string audio, string kind)
    {
        var body = JsonNode.Parse(RegionBody())!;
        body.AsObject().Remove("audio");
        if (audio != "none")
            body["audio"] = new JsonObject
            {
                [audio == "system" ? "system_audio" : "microphone"] = new JsonObject
                {
                    ["enabled"] = true, ["device_id"] = audio == "system" ? "render-fixed" : "mic-fixed"
                }
            };
        var output = new JsonObject { ["directory"] = Path.Combine(_temp.Path, "must-not-create") };
        switch (kind)
        {
            case "relative": output["directory"] = "relative-directory"; break;
            case "nul-relative": output["directory"] = "bad\0directory"; break;
            case "nul-absolute": output["directory"] = Path.Combine(_temp.Path, "bad\0directory"); break;
            case "control": output["directory"] = Path.Combine(_temp.Path, "bad\u0001directory"); break;
            case "wildcard-directory": output["directory"] = Path.Combine(_temp.Path, "bad*directory"); break;
            case "filename-separator": output["filename"] = "nested/file.mp4"; break;
            case "filename-control": output["filename"] = "bad\0.mp4"; break;
            case "filename-reserved": output["filename"] = "CON.mp4"; break;
            case "template-parent": output["filename_template"] = "../{id}"; break;
            case "template-invalid": output["filename_template"] = "bad|{datetime}"; break;
        }
        body["output"] = output;
        var counters = (_regionAudio.Calls, _regionMic.Calls, _systemDisplayCalls,
            _display.MetadataCalls, _display.TopologyCalls, _tray.ConfirmationCalls, _tray.SelectionCalls);
        var dirs = Directory.GetDirectories(_temp.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var files = Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var created = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var watcher = new FileSystemWatcher(_temp.Path) { IncludeSubdirectories = true };
        watcher.Created += (_, e) => created.Enqueue(e.FullPath);
        watcher.EnableRaisingEvents = true;
        using var response = await RegionPost(body.ToJsonString());
        await Task.Delay(100); // Allow the OS filesystem event queue to drain.
        watcher.EnableRaisingEvents = false;
        Assert.Empty(created);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OUTPUT_PATH_INVALID", Error(response));
        Assert.Equal(counters, (_regionAudio.Calls, _regionMic.Calls, _systemDisplayCalls,
            _display.MetadataCalls, _display.TopologyCalls, _tray.ConfirmationCalls, _tray.SelectionCalls));
        Assert.Equal(dirs, Directory.GetDirectories(_temp.Path, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(files, Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Empty(_engine._recs);
        Assert.Null(_tray.Decision);
        Assert.Equal(0, _backend.StartCalls);
    }

    [Theory]
    [InlineData("default", "none")]
    [InlineData("absolute", "system")]
    [InlineData("absolute", "microphone")]
    public async Task RegionOnlyValidOutputStillAllowsLocalOverride(string directory, string audio)
    {
        RegionHelper();
        var body = JsonNode.Parse(RegionBody())!;
        body["output"] = new JsonObject
        {
            ["directory"] = directory == "default" ? "default" : Path.Combine(_temp.Path, "new-request-output"),
            ["filename_template"] = "本次-{date}-{time}-{datetime}-{source}-{id}", ["conflict_policy"] = "rename"
        };
        body.AsObject().Remove("audio");
        if (audio != "none")
            body["audio"] = new JsonObject { [audio == "system" ? "system_audio" : "microphone"] =
                new JsonObject { ["enabled"] = true, ["device_id"] = audio == "system" ? "render-fixed" : "mic-fixed" } };
        using var response = await RegionPost(body.ToJsonString());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var rec = Assert.Single(_engine._recs.Values);
        Assert.Equal(0, _backend.StartCalls);
        if (directory == "absolute") Assert.Equal(body["output"]!["directory"]!.GetValue<string>(), Path.GetDirectoryName(rec.OutputPath));
        var selected = Path.Combine(_temp.Path, "local-override");
        _tray.Decision!(ConfirmationDecision.Approve(selected));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(selected, Path.GetDirectoryName(_backend.Config!.OutputPath));
        Assert.Equal(CaptureAuthorizationProofState.Consumed, _backend.Proof!.State);
        Assert.Equal(1, _backend.StartCalls);
    }
}
