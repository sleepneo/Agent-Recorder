using System.Net;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

// These tests need eligibility, not fake delivery success. Any unexpected final
// generation fails explicitly instead of manufacturing a ready Bundle.
internal sealed class UnusedMarkBundleGenerator : IRecordingBundleGenerator
{
    public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed, "unused_test_generator"));
}

public sealed partial class ChapterMarksTests
{
    [Fact]
    public async Task MediaOnlyApiAndDomainRejectWithoutChangingRecordingAuditOrFiles()
    {
        var rec = Register(bundle: false);
        var files = Directory.GetFileSystemEntries(_dataDir).Order().ToArray();
        var audit = _audit.Events.ToArray();
        var result = await PostMarkAsync(rec.Id, "{\"label\":\"位置\"}");
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        using var json = JsonDocument.Parse(result.Body);
        Assert.Equal("MARK_DELIVERY_UNAVAILABLE", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("bundle_disabled", json.RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        foreach (var source in new[] { "agent", "hotkey" })
        {
            var ex = Assert.Throws<ApiException>(() => _engine.AddMark(rec.Id, "x", source));
            Assert.Equal("MARK_DELIVERY_UNAVAILABLE", ex.Code);
        }
        Assert.Empty(rec.SnapshotMarks());
        Assert.Equal(audit, _audit.Events.ToArray());
        Assert.Equal(files, Directory.GetFileSystemEntries(_dataDir).Order().ToArray());
        Assert.Equal(RecState.recording, rec.State);
        Assert.False(rec.GenerateBundle);
        Assert.Equal(0, rec.BundleGenerationStarted);
        Assert.False(_engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording).ChapterMarksEligible);
    }

    [Theory]
    [InlineData("ffmpeg", false, true)]
    [InlineData("ffmpeg-region-av-split", false, true)]
    [InlineData("wgc-continuous", true, true)]
    [InlineData("wgc-window-av-split", true, true)]
    [InlineData("wgc-continuous", false, false)]
    [InlineData("wgc-window-av-split", false, false)]
    [InlineData("wgc", true, false)]
    [InlineData("wgc-experimental", true, false)]
    public async Task MarkAndFinalBundleUseSameBackendScopeWithoutQuerySideEffects(string backend, bool ordinary, bool eligible)
    {
        var rec = Register(backend: backend, ordinary: ordinary);
        var before = rec.BundleSnapshot;
        for (int i = 0; i < 3; i++)
            Assert.Equal(eligible, _engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording).ChapterMarksEligible);
        Assert.Equal(0, rec.BundleGenerationStarted);
        Assert.Same(before, rec.BundleSnapshot);
        var result = await PostMarkAsync(rec.Id, "{\"label\":\"backend scope\"}");
        Assert.Equal(eligible ? HttpStatusCode.OK : HttpStatusCode.Conflict, result.Status);
        if (!eligible)
        {
            Assert.Contains("backend_not_supported", result.Body);
            Assert.Empty(rec.SnapshotMarks());
            Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
        }
        var prepare = typeof(RecordingEngine).GetMethod("TryPrepareBundleGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var args = new object?[] { rec, new OutputMeta { Container = "mp4" }, null };
        Assert.Equal(eligible, (bool)prepare.Invoke(_engine, args)!);
        Assert.Equal(eligible ? 1 : 0, rec.BundleGenerationStarted);
    }

    [Fact]
    public async Task MissingGeneratorRejectsBothSourcesAndProductionSnapshot()
    {
        using var engine = new RecordingEngine(_audit);
        var rec = Register();
        engine._recs[rec.Id] = rec;
        foreach (var source in new[] { "agent", "hotkey" })
        {
            var ex = Assert.Throws<ApiException>(() => engine.AddMark(rec.Id, "x", source));
            Assert.Equal("MARK_DELIVERY_UNAVAILABLE", ex.Code);
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(ex.Details));
            Assert.Equal("bundle_generator_unavailable", details.RootElement.GetProperty("reason").GetString());
        }
        Assert.False(engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording).ChapterMarksEligible);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
        Assert.Equal(RecState.recording, rec.State);
        var server = new AgentRecorder.Api.ApiServer(engine, _audit, new NoOpTray(), null, null, null, null, null, null, null, null, null, 0);
        server.Start();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            using var response = await client.PostAsync($"http://127.0.0.1:{server.BoundPort}/api/v1/recordings/{rec.Id}/marks", JsonContent("{\"label\":\"x\"}"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("bundle_generator_unavailable", await response.Content.ReadAsStringAsync());
            Assert.Empty(rec.SnapshotMarks());
            Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(RecState.pending_confirmation)]
    [InlineData(RecState.countdown)]
    [InlineData(RecState.stopping)]
    [InlineData(RecState.completed)]
    public async Task MediaOnlyDoesNotReplaceExistingLifecycleErrors(RecState state)
    {
        var rec = Register(state, bundle: false);
        var response = await PostMarkAsync(rec.Id, "{\"label\":\"x\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Contains("RECORDING_NOT_ACTIVE", response.Body);
        Assert.Empty(rec.SnapshotMarks());
    }
    [Fact]
    public async Task MediaOnlyBeforeFirstFrameKeepsExistingTimelineRejection()
    {
        var rec = Register(bundle: false);
        rec.MarkTimelineAnchorTicks = null;
        var response = await PostMarkAsync(rec.Id, "{\"label\":\"x\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Contains("RECORDING_NOT_ACTIVE", response.Body);
        Assert.Empty(rec.SnapshotMarks());
    }

}
