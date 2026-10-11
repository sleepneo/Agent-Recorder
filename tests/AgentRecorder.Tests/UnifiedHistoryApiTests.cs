using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class UnifiedHistoryApiTests : IDisposable
{
    private readonly UnifiedHistoryTestDatabase _database = new();
    private readonly string? _prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    private HttpClient Start(IUnifiedHistoryGateway? gateway, bool auth = true)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _database.Root);
        ApiKeyAuth.InitializeForTesting(_database.Root);
        var audit=new AuditLogger(); var tray=new ThrowingTray(); var engine=new RecordingEngine(audit); engine.SetTray(tray);
        engine.BackendFactory = _ => throw new InvalidOperationException("History invoked a capture backend.");
        _server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,unifiedHistoryGateway:gateway); _server.Start();
        var client=new HttpClient(new HttpClientHandler{UseProxy=false}) {BaseAddress=new Uri($"http://127.0.0.1:{_server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
        if(auth) client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey); return client;
    }
    private IUnifiedHistoryGateway Gateway(bool ordinaryOnly=false) => new SqliteUnifiedHistoryGateway(
        new AgentRecorder.Persistence.UnifiedHistoryQueryService(_database.Store,_database.Ordinary,ordinaryOnly),()=> (Sid,Session));

    [Fact]
    public async Task LoopbackRequiresAuthReturnsEnvelopeAndTruthfulCapabilitiesWithoutUiCallsOrWrites()
    {
        _database.SeedOrdinary("r"); _database.SeedFuture("f"); _database.SeedPlan("p"); _database.SeedOccurrence("p","o");
        var snapshot=_database.Snapshot(); using var client=Start(Gateway(),auth:false);
        using var anonymous=await client.GetAsync("/api/v1/history"); Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        using var response=await client.GetAsync("/api/v1/history?limit=3&created_from=2026-10-05T01:00:00Z"); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync(); using var document=JsonDocument.Parse(text); var root=document.RootElement; var data=root.GetProperty("data");
        Assert.True(root.GetProperty("ok").GetBoolean()); Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("request_id").GetString())); Assert.Equal(3,data.GetProperty("items").GetArrayLength());
        Assert.Equal("all",data.GetProperty("filters").GetProperty("source").GetString());
        Assert.DoesNotContain("proof",text,StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(Sid,text); Assert.DoesNotContain(Session,text);
        Assert.DoesNotContain("digest",text,StringComparison.OrdinalIgnoreCase);
        using var cap=await client.GetAsync("/api/v1/capabilities"); using var caps=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());
        var history=caps.RootElement.GetProperty("data").GetProperty("unified_history"); Assert.True(history.GetProperty("supported").GetBoolean());
        Assert.Equal(3,history.GetProperty("supported_sources").GetArrayLength()); Assert.True(history.GetProperty("read_only").GetBoolean());
        Assert.Equal(snapshot,_database.Snapshot());
    }

    [Theory]
    [InlineData("limit=0")][InlineData("limit=101")][InlineData("limit=+1")][InlineData("limit=1&limit=2")]
    [InlineData("source=nope")][InlineData("status=completed")][InlineData("cursor=")][InlineData("cursor=bad")]
    [InlineData("created_from=2026-10-05T01:00:00")][InlineData("created_from=2026-10-05")]
    [InlineData("created_from=2026-10-05T01:00:00%2B08:00")]
    [InlineData("created_from=2026-10-06T01:00:00Z&created_before=2026-10-05T01:00:00Z")]
    [InlineData("created_from=2026-10-05T01:00:00Z&created_before=2026-10-05T01:00:00Z")]
    public async Task ParametersAndCursorsHaveStable400Errors(string query)
    { using var client=Start(Gateway()); using var response=await client.GetAsync("/api/v1/history?"+query); Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode); Assert.Equal("INVALID_ARGUMENT",await Code(response)); }

    [Fact]
    public async Task HeadlessAllUsesOrdinaryAndExplicitUnsupportedSourcesExplain400()
    {
        _database.SeedOrdinary("r"); _database.SeedFuture("f"); using var client=Start(Gateway(true));
        using var response=await client.GetAsync("/api/v1/history"); using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ordinary",doc.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("source").GetString());
        using var bad=await client.GetAsync("/api/v1/history?source=plan"); Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode); Assert.Equal("INVALID_ARGUMENT",await Code(bad));
        using var cap=await client.GetAsync("/api/v1/capabilities"); using var caps=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());
        Assert.Equal("ordinary",Assert.Single(caps.RootElement.GetProperty("data").GetProperty("unified_history").GetProperty("supported_sources").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task NoGatewayReturns503AndSupportedFalseRatherThanEmptyHistory()
    {
        using var client=Start(null); using var response=await client.GetAsync("/api/v1/history"); Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); Assert.Equal("HISTORY_UNAVAILABLE",await Code(response));
        using var cap=await client.GetAsync("/api/v1/capabilities"); using var doc=JsonDocument.Parse(await cap.Content.ReadAsStringAsync()); Assert.False(doc.RootElement.GetProperty("data").GetProperty("unified_history").GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task CorruptStorage503DoesNotLeakInternalExceptionOrPath()
    {
        _database.SeedOrdinary("r",completed:true); _database.Execute("UPDATE ordinary_recording_history SET output_path=$path;",("$path","D:\\private\0secret.mp4"));
        using var client=Start(Gateway()); using var response=await client.GetAsync("/api/v1/history"); Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); Assert.Equal("HISTORY_UNAVAILABLE",await Code(response));
        var text=await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("secret",text); Assert.DoesNotContain("Sqlite",text); Assert.DoesNotContain(Sid,text);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OrdinaryStatusHttpWorksForAppAndHeadlessScopesWithTruthfulCapabilities(bool ordinaryOnly)
    {
        foreach(var status in OrdinaryRecordingHistoryStatusCodes.Supported) _database.Ordinary.Register(_database.Recording(status) with { Status=status });
        var before=_database.Snapshot();using var client=Start(Gateway(ordinaryOnly),auth:false);
        using(var anonymous=await client.GetAsync("/api/v1/history?source=ordinary&status=failed")) Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        foreach(var status in OrdinaryRecordingHistoryStatusCodes.Supported)
        {
            using var response=await client.GetAsync("/api/v1/history?source=ordinary&status="+status+"&limit=1&created_from=2026-10-05T01:00:00Z&created_before=2026-10-05T01:00:01Z");
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var data=doc.RootElement.GetProperty("data");
            Assert.Equal(status,data.GetProperty("filters").GetProperty("status").GetString());Assert.Equal(status,Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        }
        using var cap=await client.GetAsync("/api/v1/capabilities");using var caps=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());var history=caps.RootElement.GetProperty("data").GetProperty("unified_history");
        Assert.Equal("ordinary",Assert.Single(history.GetProperty("status_supported_sources").EnumerateArray()).GetString());
        Assert.Equal(OrdinaryRecordingHistoryStatusCodes.Supported,history.GetProperty("supported_statuses").EnumerateArray().Select(v=>v.GetString()));
        Assert.Contains(history.GetProperty("filters").EnumerateArray(),v=>v.GetString()=="status");
        Assert.Equal(before,_database.Snapshot());
    }

    [Theory]
    [InlineData("status=failed")] [InlineData("source=all&status=failed")] [InlineData("source=plan&status=failed")] [InlineData("source=future_window&status=failed")]
    [InlineData("source=ordinary&status=")] [InlineData("source=ordinary&status=FAILED")] [InlineData("source=ordinary&status=nope")]
    [InlineData("source=ordinary&status=failed%2Ccompleted")] [InlineData("source=ordinary&status=%20failed")]
    [InlineData("source=ordinary&status=failed&status=failed")] [InlineData("source=ordinary&status=failed&sta%74us=completed")]
    [InlineData("source=ordinary&status=failed&%53TATUS=completed")] [InlineData("source=ordinary&source=ordinary&status=failed")]
    public async Task StatusTransportRejectsAmbiguousAndNonOrdinaryValues(string query)
    {using var client=Start(Gateway());using var response=await client.GetAsync("/api/v1/history?"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal("INVALID_ARGUMENT",await Code(response));}

    [Fact]
    public async Task NoHistoryCannotAdvertiseStatusValuesAndStatusReadKeepsStorage503()
    {
        using var client=Start(null);using var response=await client.GetAsync("/api/v1/history?source=ordinary&status=failed");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        using var cap=await client.GetAsync("/api/v1/capabilities");using var doc=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());var h=doc.RootElement.GetProperty("data").GetProperty("unified_history");
        Assert.Empty(h.GetProperty("status_supported_sources").EnumerateArray());Assert.Empty(h.GetProperty("supported_statuses").EnumerateArray());
    }

    [Fact]
    public async Task HttpCursorRejectsChangedStatusAndMalformedIndexRemains503()
    {
        foreach(var id in new[]{"a","b"}) _database.Ordinary.Register(_database.Recording(id) with{Status="failed"});
        using var client=Start(Gateway());using var first=await client.GetAsync("/api/v1/history?source=ordinary&status=failed&limit=1");
        using var doc=JsonDocument.Parse(await first.Content.ReadAsStringAsync());var cursor=Uri.EscapeDataString(doc.RootElement.GetProperty("data").GetProperty("next_cursor").GetString()!);
        using var changed=await client.GetAsync("/api/v1/history?source=ordinary&status=completed&cursor="+cursor);Assert.Equal(HttpStatusCode.BadRequest,changed.StatusCode);Assert.Equal("INVALID_ARGUMENT",await Code(changed));
        using var next=await client.GetAsync("/api/v1/history?source=ordinary&status=failed&cursor="+cursor);Assert.Equal(HttpStatusCode.OK,next.StatusCode);
        _database.Execute("DROP INDEX idx_ordinary_recording_history_status_created;");
        using var unavailable=await client.GetAsync("/api/v1/history?source=ordinary&status=failed");Assert.Equal(HttpStatusCode.ServiceUnavailable,unavailable.StatusCode);Assert.Equal("HISTORY_UNAVAILABLE",await Code(unavailable));
    }

    private static async Task<string?> Code(HttpResponseMessage response) { using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return d.RootElement.GetProperty("error").GetProperty("code").GetString(); }
    public void Dispose() { _server?.Stop(); ApiKeyAuth.ResetForTesting(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior); _database.Dispose(); }
    private sealed class ThrowingTray : ITrayContext
    {
        public string HostMode=>"headless"; public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation,Action<ConfirmationDecision> callback)=>throw new InvalidOperationException("History invoked confirmation.");
        public void RequestRegionSelection(int timeoutSeconds,Action<string,int,int,int,int,string,string> callback)=>throw new InvalidOperationException("History invoked selection.");
        public void SetRecording(RecordingUiPresentation presentation)=>throw new InvalidOperationException("History invoked capture UI.");
        public void SetIdle(RecordingUiPresentation presentation)=>throw new InvalidOperationException("History invoked capture UI.");
        public void SetAllIdle()=>throw new InvalidOperationException("History invoked capture UI.");
        public void ShowError(string text)=>throw new InvalidOperationException("History invoked UI.");
    }
}
