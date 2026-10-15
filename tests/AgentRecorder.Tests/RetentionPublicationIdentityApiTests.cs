using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RetentionPublicationIdentityApiTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PrivateRelationUsesExistingEnvelopeAndCapabilitiesWithoutMaterials(bool ordinaryOnly)
        => await Run(ordinaryOnly, null);

    public static IEnumerable<object[]> SchemaFaults => SqlitePublicationIdentityReferenceTests.Faults();
    [Theory, MemberData(nameof(SchemaFaults))]
    public async Task EverySchemaFaultReturnsExisting503AndZeroFilesystem(string sql) => await Run(true, sql);

    [Theory]
    [InlineData("missing")][InlineData("duplicate")][InlineData("foreign")][InlineData("flag")][InlineData("throw")]
    public async Task BadQueryRowsCannotLeakItemsAndDoNotProbe(string fault) => await Run(false, fault);

    private static async Task Run(bool ordinaryOnly, string? fault)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        RetentionPublicationIdentityTests.ForeignOrdinary(f, row);
        var query = new UnifiedHistoryQueryService(f.Db.Store, f.Db.Ordinary, ordinaryOnly);
        if (fault?.Contains(';') == true) f.Db.RawExecute(fault);
        else if (fault is not null) query.RetentionIdentityResultsForTests = rows => fault switch
        {
            "missing" => Array.Empty<(object, object)>(), "duplicate" => new[] { rows[0], rows[0] },
            "foreign" => new[] { ((object)"SECRET_FOREIGN", (object)0L) }, "flag" => new[] { (rows[0].CandidateId, (object)2L) },
            _ => throw new IOException("SECRET_PROVIDER")
        };
        var service = new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage, f.Db.Root, f.Files, f.Files, () => At.AddDays(2), TestRuntimeOutputUse.None);
        var prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var audit = new RetentionSharedOutputPathTests.QuietAudit(); using var engine = new RecordingEngine(audit); var tray = new HeadlessTrayContext(audit); engine.SetTray(tray);
        ApiServer? server = null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", f.Db.Root); ApiKeyAuth.InitializeForTesting(f.Db.Root);
            server = new(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
                retentionPreviewGateway: new SqliteRetentionPreviewGateway(service, () => (Sid, Session))); server.Start();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{server.BoundPort}") };
            var url = "/api/v1/retention/preview?completed_before=" + Uri.EscapeDataString(f.Before.ToString("O"));
            using (var denied = await client.GetAsync(url)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var before = SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, include42: true);
            using var response = await client.GetAsync(url); var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(fault is null ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            if (fault is not null) { Assert.Contains("RETENTION_PREVIEW_UNAVAILABLE", text); Assert.DoesNotContain("items", text); }
            else
            {
                using var document = JsonDocument.Parse(text); var data = document.RootElement.GetProperty("data");
                var item = Assert.Single(data.GetProperty("items").EnumerateArray()); Assert.Equal("shared_publication_identity", item.GetProperty("reason_code").GetString());
                Assert.Equal(JsonValueKind.Null, item.GetProperty("observed_file_bytes").ValueKind); Assert.True(data.GetProperty("read_only").GetBoolean());
                Assert.False(data.GetProperty("deletion_supported").GetBoolean()); Assert.False(data.GetProperty("reference_protection_ready").GetBoolean());
                Assert.Equal(1, data.GetProperty("unknown_candidate_file_count").GetInt32());
                Assert.Equal(new[] { "age_candidate", "completed_at_utc", "entry_id", "evaluation", "observed_file_bytes", "reason_code", "recorded_bytes", "recorded_path", "recording_id", "status" }, item.EnumerateObject().Select(p => p.Name).Order());
                using var capabilities = await client.GetAsync("/api/v1/capabilities");
                using var cap = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync()); var preview = cap.RootElement.GetProperty("data").GetProperty("retention_preview");
                Assert.True(preview.GetProperty("read_only").GetBoolean()); Assert.False(preview.GetProperty("deletion_supported").GetBoolean()); Assert.False(preview.GetProperty("reference_protection_ready").GetBoolean());
            }
            foreach (var secret in new[] { "SECRET", "volume_id", "file_id", "reference_count", "reference_source", "nonce", "safe_to_delete" }) Assert.DoesNotContain(secret, text);
            Assert.Equal(before, SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, include42: true)); RetentionRuntimeOutputTests.NoProbes(f);
        }
        finally { server?.Stop(); ApiKeyAuth.ResetForTesting(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", prior); }
    }
}
