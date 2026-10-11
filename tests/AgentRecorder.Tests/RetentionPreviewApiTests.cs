using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
using static AgentRecorder.Tests.RetentionPreviewQueryTests;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RetentionPreviewApiTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    private Fixture? _fixture;
    private HttpClient Start(bool auth = true, bool configured = true, bool headless = false, IRetentionPreviewGateway? custom = null)
    {
        _fixture ??= new Fixture(ordinaryOnly: headless);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _fixture.Db.Root);
        ApiKeyAuth.InitializeForTesting(_fixture.Db.Root);
        var audit = new AuditLogger(); var tray = new ThrowingTray(); var engine = new RecordingEngine(audit); engine.SetTray(tray);
        engine.BackendFactory = _ => throw new InvalidOperationException("Preview invoked capture.");
        var gateway = configured ? custom ?? new SqliteRetentionPreviewGateway(_fixture.Service, () => (Sid, Session)) : null;
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0, retentionPreviewGateway: gateway);
        _server.Start();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
        if (auth) client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        return client;
    }
    private const string Endpoint = "/api/v1/retention/preview?completed_before=2026-10-05T02:00:00Z";
    private static async Task<JsonDocument> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private static async Task<string?> Code(HttpResponseMessage response) { using var d = await Json(response); return d.RootElement.GetProperty("error").GetProperty("code").GetString(); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BothHostScopesAuthenticateAndAdvertiseNonDeletionContract(bool headless)
    {
        using var client = Start(auth: false, headless: headless); _fixture!.Seed("old"); _fixture.Db.SeedPlan("p"); _fixture.Db.SeedOccurrence("p","o"); _fixture.Db.SeedFuture("f"); var snapshot = _fixture.Db.Snapshot();
        using var denied = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); Assert.Equal(0,_fixture.Reads);
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", "invalid-test-key");
        using var wrong = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode); Assert.Equal(0,_fixture.Reads);
        client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        using var response = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var doc = await Json(response);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean()); Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("request_id").GetString()));
        var data = doc.RootElement.GetProperty("data"); var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("ordinary:old", item.GetProperty("entry_id").GetString()); Assert.True(item.GetProperty("age_candidate").GetBoolean()); Assert.Equal(12,item.GetProperty("recorded_bytes").GetInt64()); Assert.Equal(12,item.GetProperty("observed_file_bytes").GetInt64());
        Assert.True(data.GetProperty("read_only").GetBoolean()); Assert.False(data.GetProperty("deletion_supported").GetBoolean()); Assert.False(data.GetProperty("reference_protection_ready").GetBoolean()); Assert.False(data.GetProperty("physical_identity_protection_ready").GetBoolean()); Assert.False(data.GetProperty("bundle_included").GetBoolean());
        var payload = doc.RootElement.GetRawText(); Assert.DoesNotContain(Sid,payload); Assert.DoesNotContain(Session,payload); Assert.DoesNotContain("proof",payload); Assert.DoesNotContain("cleanup_token",payload); Assert.DoesNotContain("safe_to_delete",payload);
        using var responseCap = await client.GetAsync("/api/v1/capabilities"); using var cap = await Json(responseCap); var c = cap.RootElement.GetProperty("data").GetProperty("retention_preview");
        Assert.True(c.GetProperty("supported").GetBoolean()); Assert.True(c.GetProperty("read_only").GetBoolean()); Assert.False(c.GetProperty("deletion_supported").GetBoolean()); Assert.False(c.GetProperty("reference_protection_ready").GetBoolean());
        Assert.Equal(100,c.GetProperty("page_size_max").GetInt32()); Assert.Equal("host_data_directory_videos_ordinary_mp4",c.GetProperty("scope").GetString()); Assert.Equal("completed_at_utc_exclusive_cutoff",c.GetProperty("age_basis").GetString()); Assert.False(c.GetProperty("directory_enumeration").GetBoolean());
        Assert.Equal(snapshot,_fixture.Db.Snapshot()); Assert.Equal(1,_fixture.Reads); Assert.Equal(0,_fixture.OldProbes);
    }

    [Theory]
    [InlineData("")] [InlineData("completed_before=")] [InlineData("completed_before=2026-10-05")]
    [InlineData("completed_before=2026-10-05T02:00Z")] [InlineData("completed_before=2026-10-05T02:00:00")]
    [InlineData("completed_before=2026-10-05T02:00:00%2B08:00")] [InlineData("completed_before=2026-10-05T02:00:00.12345678Z")]
    [InlineData("completed_before=%202026-10-05T02:00:00Z")] [InlineData("completed_before=2026-02-30T02:00:00Z")]
    [InlineData("completed_before=9999-10-05T02:00:00Z")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&completed_before=2026-10-05T02:00:00Z")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&limit=0")] [InlineData("completed_before=2026-10-05T02:00:00Z&limit=101")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&limit=+1")] [InlineData("completed_before=2026-10-05T02:00:00Z&limit=")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&limit=1.5")] [InlineData("completed_before=2026-10-05T02:00:00Z&limit=1&limit=2")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&source=ordinary")] [InlineData("completed_before=2026-10-05T02:00:00Z&sid=foreign")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&session=foreign")] [InlineData("completed_before=2026-10-05T02:00:00Z&directory=C%3A%5COutside")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&output_path=out.mp4")] [InlineData("completed_before=2026-10-05T02:00:00Z&execute=true")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&delete=true")] [InlineData("completed_before=2026-10-05T02:00:00Z&cursor=")]
    [InlineData("completed_before=2026-10-05T02:00:00Z&cursor=bad")]
    public async Task StrictArgumentsAndCursorReturnStable400(string query)
    {
        using var client = Start(); using var response = await client.GetAsync("/api/v1/retention/preview?"+query);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode); Assert.Equal("INVALID_ARGUMENT",await Code(response)); Assert.Empty(_fixture!.Files.Attributes);
    }

    [Theory]
    [InlineData("2026-10-05T02:00:00.1234567Z")]
    [InlineData("2026-10-05T02:00:00%2B00:00")]
    [InlineData("2026-10-05T02:00:00-00:00")]
    public async Task SevenFractionalDigitsAndExplicitZeroOffsetsAreAccepted(string time)
    {
        using var client = Start(); using var response = await client.GetAsync("/api/v1/retention/preview?completed_before="+time); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var doc = await Json(response); var d = doc.RootElement.GetProperty("data"); Assert.Equal(20,d.GetProperty("page_size").GetInt32()); Assert.Equal(0,d.GetProperty("evaluated_count").GetInt32()); Assert.True(d.GetProperty("observed_bytes_complete").GetBoolean());
    }

    [Fact]
    public async Task ProtectedPageKeepsCursorAndCrossCutoffIsRejectedWithoutProbing()
    {
        using var client = Start(); _fixture!.Seed("a","preparing"); _fixture.Seed("b");
        using var first = await client.GetAsync(Endpoint+"&limit=1"); using var doc = await Json(first); var data = doc.RootElement.GetProperty("data");
        Assert.Equal(0,data.GetProperty("age_candidate_count").GetInt32()); var cursor = data.GetProperty("next_cursor").GetString(); Assert.NotNull(cursor); Assert.Empty(_fixture.Files.Attributes);
        using var mismatch = await client.GetAsync("/api/v1/retention/preview?completed_before=2026-10-05T02:00:01Z&cursor="+Uri.EscapeDataString(cursor!)); Assert.Equal(HttpStatusCode.BadRequest,mismatch.StatusCode);
        using var next = await client.GetAsync(Endpoint+"&limit=100&cursor="+Uri.EscapeDataString(cursor!)); Assert.Equal(HttpStatusCode.OK,next.StatusCode); using var second = await Json(next);
        Assert.Equal("ordinary:b",Assert.Single(second.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("entry_id").GetString());
    }

    [Theory]
    [InlineData("missing")] [InlineData("corrupt")] [InlineData("schema")] [InlineData("gateway")]
    public async Task UnavailabilityIsEntire503WithoutPrivateExceptionsOrFalseEmpty(string fault)
    {
        IRetentionPreviewGateway? custom = fault == "gateway" ? new ThrowingGateway() : null;
        using var client = Start(configured:fault!="missing",custom:custom);
        if (fault == "corrupt") { _fixture!.Seed("a"); _fixture.Seed("b"); _fixture.Db.Corrupt("UPDATE ordinary_recording_history SET output_path=$path WHERE recording_id='b';",("$path","D:\\private\0sql-path.mp4")); }
        if (fault == "schema") _fixture!.Db.Execute("DROP INDEX idx_ordinary_recording_history_created;");
        using var response = await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); Assert.Equal("RETENTION_PREVIEW_UNAVAILABLE",await Code(response));
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("private",text); Assert.DoesNotContain("Sqlite",text); Assert.DoesNotContain(Sid,text); Assert.DoesNotContain("items",text); Assert.Empty(_fixture!.Files.Attributes);
        if(fault=="missing") { using var cap=await client.GetAsync("/api/v1/capabilities"); using var doc=await Json(cap); Assert.False(doc.RootElement.GetProperty("data").GetProperty("retention_preview").GetProperty("supported").GetBoolean()); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrTimedOutSameWriterBarrierReturns503WithoutSampling(bool blocked)
    {
        using var client=Start(); var row=_fixture!.Seed("pending","preparing"); using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        _fixture.Db.Ordinary.BeforePersistForTests = _ => { entered.Set(); if(blocked) Assert.True(release.Wait(TimeSpan.FromSeconds(5))); else throw new IOException("private-writer"); };
        Assert.True(_fixture.Db.Ordinary.TryPublish(row with {Status="failed",CompletedAtUtc=At.AddMinutes(1)}));
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); using var response=await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); Assert.Equal("RETENTION_PREVIEW_UNAVAILABLE",await Code(response)); Assert.Empty(_fixture.Files.Attributes); }
        finally { release.Set(); }
    }

    [Fact]
    public async Task NoExecutionOrCleanupRoutesAreAdded()
    {
        using var client=Start(); var snapshot=_fixture!.Db.Snapshot();
        foreach(var route in new[]{"/api/v1/retention/preview","/api/v1/retention/execute","/api/v1/retention/delete"})
        { using var request=new HttpRequestMessage(HttpMethod.Post,route); using var response=await client.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound,response.StatusCode); }
        using var delete=await client.DeleteAsync("/api/v1/retention/preview"); Assert.Equal(HttpStatusCode.NotFound,delete.StatusCode); Assert.Equal(0,_fixture.Reads); Assert.Equal(snapshot,_fixture.Db.Snapshot());
    }

    [Fact]
    public async Task OversizedCursorAndByteOverflowHaveBounded400AndEntire503Responses()
    {
        using var client=Start(custom:new OverflowGateway());
        using var response=await client.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); Assert.Equal("RETENTION_PREVIEW_UNAVAILABLE",await Code(response));
        _server!.Stop(); _server=null;
        using var normal=Start(); using var bad=await normal.GetAsync(Endpoint+"&cursor="+new string('a',2049)); Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode); Assert.Equal("INVALID_ARGUMENT",await Code(bad)); Assert.Empty(_fixture!.Files.Attributes);
    }
    private sealed class OverflowGateway : IRetentionPreviewGateway
    { public RetentionPreviewPage GetPage(int limit,DateTimeOffset before,string? cursor) => throw new RetentionPreviewUnavailableException(new OverflowException("private-overflow")); }

    public void Dispose() { _server?.Stop(); ApiKeyAuth.ResetForTesting(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior); _fixture?.Dispose(); }
    private sealed class ThrowingGateway : IRetentionPreviewGateway
    { public RetentionPreviewPage GetPage(int limit,DateTimeOffset before,string? cursor) => throw new IOException("private-Sqlite-stack"); }
    private sealed class ThrowingTray : ITrayContext
    {
        public string HostMode=>"headless"; public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new InvalidOperationException("Preview invoked confirmation.");
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("Preview invoked selection.");
        public void SetRecording(RecordingUiPresentation p)=>throw new InvalidOperationException("Preview invoked recording.");
        public void SetIdle(RecordingUiPresentation p)=>throw new InvalidOperationException("Preview invoked recording.");
        public void SetAllIdle()=>throw new InvalidOperationException("Preview invoked recording.");
        public void ShowError(string text)=>throw new InvalidOperationException("Preview invoked UI.");
    }
}
