using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class CodexProfileRecordingRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedVideoQuota420AppliesToQuickDisplayAndProfileRegion(bool reuse)
    {
        using var writer = new SqliteOrdinaryRecordingHistoryStore(new(Path.Combine(_temp.Path, "state.db")));
        _engine.ConfigureOrdinaryRecordingHistory(writer);
        int reads = 0;
        _engine.ConfigureManagedVideoQuota(new(ManagedVideoQuotaSetting.Parse("1"), _temp.Path, () =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new ManagedVideoQuotaUsage(0));
        }));
        var capacity = RecordingPreflightChecker.StorageCapacityProvider;
        RecordingPreflightChecker.StorageCapacityProvider = new FakeStorageProvider();
        try
        {
            var request = reuse ? JsonNode.Parse(RegionBody())! : JsonNode.Parse("""
                {"target":{"type":"primary_display"},"duration_seconds":1,"countdown_seconds":0,"output":{}}
                """)!;
            request["output"]!["directory"] = Path.Combine(_temp.Path, "Videos");
            using var response = await RegionPost(request.ToJsonString());
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("MANAGED_VIDEO_QUOTA_EXCEEDED", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(1, reads);
            Assert.Empty(_engine._recs);
            Assert.Null(_tray.Decision);
            Assert.Equal(0, _backend.StartCalls);
        }
        finally { RecordingPreflightChecker.StorageCapacityProvider = capacity; }
    }
}
