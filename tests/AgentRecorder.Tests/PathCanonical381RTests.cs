using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static string PathCanonical381RDirectory(string directory, string form) => form switch
    {
        "mixed" => Path.GetDirectoryName(directory)! + "/" + Path.GetFileName(directory),
        "forward" => directory.Replace('\\', '/'),
        "trailing" => directory + "\\/",
        _ => directory
    };

    private static async Task PathCanonical381RHistory(Fixture f, Recording rec)
    {
        var row = f.Writer.Read(rec.Id)!;
        Assert.Equal("completed", row.Status);
        Assert.Equal(rec.OutputPath, row.OutputPath);
        using (var connection = f.Store.OpenConnection())
        using (var transaction = connection.BeginTransaction())
            Assert.Equal(rec.LastMeta!.PublicationReceipt!.Anchor,
                PublicationAnchorStorage.Read(connection, transaction, new[] { row })[rec.Id]);
        Assert.Equal("not_applicable", row.Bundle!.Status);Assert.Null(row.Bundle.IdentityEvidence);Assert.Null(row.MediaHash);Assert.Null(rec.OrdinaryBundleOperation);Assert.Equal(0,f.Generator.Calls);
        using var client = f.Client();
        foreach (var path in new[] { "/api/v1/recording-history/" + rec.Id,
            "/api/v1/recording-history", "/api/v1/history?source=ordinary" })
        {
            var data = await Get(client, path);
            var item = path.Contains("/recording-history/") ? data.GetProperty("recording") :
                data.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("recording_id").GetString() == rec.Id);
            Assert.Equal("completed", item.GetProperty("status").GetString());
            Assert.Equal("not_applicable", item.GetProperty("bundle").GetProperty("status").GetString());
        }
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("mixed")]
    [InlineData("forward")]
    [InlineData("trailing")]
    public async Task PathCanonical381RPublicationHistoryAndBundleUseOnePath(string form)
    {
        using var f = RuntimeFixture(); await f.Prepare(); f.StartApi();
        try
        {
            var request = f.Request("window");
            request["output"]!["directory"] = PathCanonical381RDirectory(Path.Combine(f.Root, "Videos"), form);
            var creation = await NestedWindow381Post(f, request);
            Assert.Equal(HttpStatusCode.OK, creation.Status);
            var data = JsonNode.Parse(creation.Body)!["data"]!;
            var rec = f.Engine._recs[data["recording_id"]!.GetValue<string>()];
            var presented = data["summary"]!["output"]!.GetValue<string>();
            await f.Approve(rec); f.Generator.Release.TrySetResult(); await f.Terminal(rec, false);
            await Until(() => rec.BundleSnapshot.Status == "not_applicable");
            await Until(() => BundleUseCount(f) == 0);
            var snapshot = (OrdinaryRecordingHistorySnapshot)typeof(RecordingEngine)
                .GetMethod("CreateOrdinaryHistorySnapshot", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { rec })!;
            string? validation = null;
            try { SqliteOrdinaryRecordingHistoryStore.ValidateSnapshot(snapshot); }
            catch (Exception e) { validation = e.GetType().Name + ": " + e.Message; }
            using var client = f.Client();
            var statuses = new Dictionary<string, int>();
            foreach (var path in new[] { "/api/v1/recording-history/" + rec.Id,
                "/api/v1/recording-history", "/api/v1/history?source=ordinary" })
            { using var response = await client.GetAsync(path); statuses[path] = (int)response.StatusCode; }
            SaveSeriesBundleEvidence("path381r-" + form, new { presented, rec.OutputPath,
                receiptPath = rec.LastMeta!.PublicationReceipt!.Anchor.Path,
                bundle = rec.BundleSnapshot.Status, validation, statuses,
                unsettled = f.Writer.UnsettledWriteFailureCountForTests,
                attachmentCount = Directory.Exists(Path.ChangeExtension(rec.OutputPath, ".bundle"))
                    ? Directory.GetFiles(Path.ChangeExtension(rec.OutputPath, ".bundle")).Length : 0 });
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
            Assert.Null(validation); Assert.All(statuses.Values, code => Assert.Equal(200, code));
            Assert.Equal(rec.LastMeta.PublicationReceipt.Anchor.Path, presented);
            Assert.Equal(presented, rec.OutputPath); Assert.Equal(rec.OutputPath, rec.Config.OutputPath);
            Assert.Equal(rec.OutputPath, rec.OrdinaryRequestedOutputPath);
            await PathCanonical381RHistory(f, rec);
        }
        finally { await NestedWindow381Drain(f); }
    }
}
