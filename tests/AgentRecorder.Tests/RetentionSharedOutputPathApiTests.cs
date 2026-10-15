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
public sealed class RetentionSharedOutputPathApiTests : IDisposable
{
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    private RetentionSharedOutputPathTests.Fixture? _f;
    private HttpClient Start(bool headless=false,string? fault=null)
    {
        _f=new(headless);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_f.Db.Root);ApiKeyAuth.InitializeForTesting(_f.Db.Root);
        var audit=new RetentionSharedOutputPathTests.QuietAudit();var tray=new HeadlessTrayContext(audit);var engine=new RecordingEngine(audit);engine.SetTray(tray);engine.BackendFactory=_=>throw new IOException("SECRET_CAPTURE_FORBIDDEN");
        var service=new RetentionPreviewQueryService(_f.Query,_f.Db.Root, TestRuntimeOutputUse.None);
        if(fault is "missing_evidence" or "invalid_evidence")service=new((sid,session,limit,before,cursor)=>
        {
            var p=_f.Query.GetOrdinaryEvidencePage(sid,session,limit,before,cursor);
            return p with{Items=p.Items.Select(e=>e with{SharedOutputReference=fault=="missing_evidence"?null:(SharedOutputPathReferenceState)999}).ToArray()};
        },_f.Db.Root,_f.Files,_f.Files,()=>At.AddDays(5),TestRuntimeOutputUse.None);
        _server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:fault=="missing_provider"?null:new SqliteRetentionPreviewGateway(service,()=> (Sid,Session)));_server.Start();
        return new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{_server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private static string Endpoint=>"/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(At.AddHours(1).ToString("O"))+"&limit=1";
    private static void Auth(HttpClient c)=>c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(d.RootElement.GetProperty("ok").GetBoolean());Assert.False(string.IsNullOrWhiteSpace(d.RootElement.GetProperty("request_id").GetString()));return d.RootElement.GetProperty("data").Clone();}
    private static async Task Error(HttpResponseMessage response,HttpStatusCode status,string code)
    {Assert.Equal(status,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var d=JsonDocument.Parse(text);Assert.False(d.RootElement.GetProperty("ok").GetBoolean());Assert.Equal(code,d.RootElement.GetProperty("error").GetProperty("code").GetString());Assert.False(string.IsNullOrWhiteSpace(d.RootElement.GetProperty("request_id").GetString()));foreach(var s in new[]{"SECRET","Sqlite",Sid,Session,"items","foreign-sid"})Assert.DoesNotContain(s,text);}
    [Theory]
    [InlineData(false,"ordinary")][InlineData(true,"ordinary")][InlineData(false,"future")][InlineData(true,"future")]
    [InlineData(false,"run")][InlineData(true,"run")][InlineData(false,"required")][InlineData(true,"required")]
    public async Task FormalGatewaysProtectFourSourcesWithoutDisclosingReferenceIdentity(bool headless,string source)
    {
        using var c=Start(headless);var f=_f!;var path=source=="run"?f.Run():f.PathFor();f.Seed(path:path);
        if(source=="ordinary")f.Seed("SECRET_OTHER",path,"failed",f.Before.AddDays(1));if(source=="future")f.Future(path);if(source=="required")await f.Required(path);
        var calls=0;f.Query.RetentionPathCommandForTests=(_,_,_,_)=>calls++;
        using(var denied=await c.GetAsync(Endpoint)){await Error(denied,HttpStatusCode.Unauthorized,"UNAUTHORIZED");Assert.Equal(0,calls);}
        Auth(c);var before=f.Snapshot();using var response=await c.GetAsync(Endpoint);var data=await Data(response);var item=Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("shared_output_path",item.GetProperty("reason_code").GetString());Assert.Equal("protected",item.GetProperty("evaluation").GetString());Assert.True(item.GetProperty("age_candidate").GetBoolean());Assert.Equal(JsonValueKind.Null,item.GetProperty("observed_file_bytes").ValueKind);
        Assert.Equal(1,data.GetProperty("unknown_candidate_file_count").GetInt32());Assert.Equal(0,data.GetProperty("observed_candidate_bytes").GetInt64());Assert.False(data.GetProperty("observed_bytes_complete").GetBoolean());
        foreach(var key in new[]{"deletion_supported","reference_protection_ready","physical_identity_protection_ready","bundle_included"})Assert.False(data.GetProperty(key).GetBoolean());
        var fields=new[]{"entry_id","recording_id","status","completed_at_utc","evaluation","reason_code","age_candidate","recorded_path","recorded_bytes","observed_file_bytes"};Assert.Equal(fields.Order(),item.EnumerateObject().Select(p=>p.Name).Order());
        var text=await response.Content.ReadAsStringAsync();foreach(var secret in new[]{"SECRET_","foreign-sid","foreign-session",Sid,Session,"proof","\"scope\"","reference_source","reference_count","safe_to_delete"})Assert.DoesNotContain(secret,text);
        Assert.Equal(1,calls);Assert.Equal(before,f.Snapshot());Assert.Equal(0,f.OldProbes);
        using var cap=await c.GetAsync("/api/v1/capabilities");var h=(await Data(cap)).GetProperty("retention_preview");Assert.True(h.GetProperty("read_only").GetBoolean());Assert.False(h.GetProperty("reference_protection_ready").GetBoolean());Assert.False(h.GetProperty("deletion_supported").GetBoolean());
    }
    [Theory]
    [InlineData("missing_provider")][InlineData("missing_evidence")][InlineData("invalid_evidence")][InlineData("query")][InlineData("index")][InlineData("receipt")][InlineData("collation")]
    public async Task UnavailableEvidenceUsesSafe503WithNoWritesOrPrematureProbes(string fault)
    {
        using var c=Start(fault:fault);var f=_f!;f.Seed();f.Seed("other",status:"failed",at:f.Before.AddDays(1));
        if(fault=="index")f.Db.Execute("DROP INDEX idx_future_window_authorizations_shared_output_path;");if(fault=="receipt")f.Db.Execute("UPDATE schema_migrations SET name='SECRET_BAD_RECEIPT' WHERE version=28;");
        if(fault=="query")f.Query.RetentionPathCommandForTests=(_,_,_,_)=>throw new IOException("SECRET_SQL");if(fault=="collation")f.Query.RetentionPathCommandForTests=(con,_,_,_)=>con.CreateCollation(SqliteOutputPathCollation.Name,null);
        Auth(c);var before=f.Snapshot();using var response=await c.GetAsync(Endpoint);await Error(response,HttpStatusCode.ServiceUnavailable,"RETENTION_PREVIEW_UNAVAILABLE");f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData("source_type=window")][InlineData("plan_id=p")][InlineData("profile_id=p")][InlineData("status=completed")][InlineData("path=C%3A%5Csecret.mp4")]
    public async Task ExistingPreviewQueryAllowlistRemainsUnchanged(string query)
    {using var c=Start();Auth(c);var calls=0;_f!.Query.RetentionPathCommandForTests=(_,_,_,_)=>calls++;using var response=await c.GetAsync(Endpoint+"&"+query);await Error(response,HttpStatusCode.BadRequest,"INVALID_ARGUMENT");Assert.Equal(0,calls);_f.NoProbes();}
    public void Dispose(){_server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);_f?.Dispose();}
}
